using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenStreamMS.Core.Helpers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Agente de sesión: corre dentro de la sesión de Windows asignada al puesto
/// (lanzado como <c>OpenStreamMS.exe --seat-agent &lt;pipeName&gt;</c>).
/// Captura la pantalla vía DDA a un anillo de 3 texturas compartidas NT,
/// el audio vía WASAPI loopback, el cursor, e inyecta la entrada con SendInput.
/// </summary>
public static class SeatAgent
{
    private static volatile bool _stopping;
    private static int _inputsReceived;

    public static int Run(string pipeName)
    {
        Logger.Log($"[SeatAgent] Iniciando agente en sesión {Process.GetCurrentProcess().SessionId} (pid={Environment.ProcessId})");
        SeatChannel channel;
        try
        {
            channel = SeatPipes.Connect(pipeName, timeoutMs: 15000);
        }
        catch (Exception ex)
        {
            Logger.Error($"[SeatAgent] No se pudo conectar a la tubería '{pipeName}': {ex.Message}");
            return 1;
        }

        using (channel)
        {
            // 1. Enumerar adaptadores y salidas para encontrar la pantalla de la sesión
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            IDXGIAdapter1? adapter = null;
            IDXGIOutput? output = null;

            for (int attempt = 0; attempt < 20 && output is null; attempt++)
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var ad).Success; i++)
                {
                    for (uint j = 0; ad.EnumOutputs(j, out var o).Success; j++)
                    {
                        var d = o.Description;
                        if (d.AttachedToDesktop)
                        {
                            adapter = ad;
                            output = o;
                            break;
                        }
                    }
                    if (output is not null) break;
                }
                if (output is null) Thread.Sleep(500);
            }

            if (adapter is null || output is null)
            {
                Logger.Error("[SeatAgent] No se encontró ninguna salida vinculada al escritorio de la sesión.");
                return 1;
            }

            var od = output.Description;
            int ow = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
            int oh = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;
            var luid = adapter.Description1.Luid;
            Logger.Log($"[SeatAgent] Salida detectada: {od.DeviceName} ({ow}x{oh}) en {adapter.Description1.Description}");

            // 2. Crear dispositivo D3D11 y DDA
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? dev, out ID3D11DeviceContext? ctx).CheckError();

            using var o1 = output.QueryInterface<IDXGIOutput1>();
            IDXGIOutputDuplication dupl;
            try
            {
                dupl = o1.DuplicateOutput(dev!);
            }
            catch (Exception ex)
            {
                Logger.Error($"[SeatAgent] DuplicateOutput falló: {ex.Message}");
                return 1;
            }

            // 3. Crear el anillo de 3 texturas D3D11 compartidas
            const int RingSize = 3;
            var sharedTextures = new ID3D11Texture2D[RingSize];
            var keyedMutexes = new IDXGIKeyedMutex[RingSize];
            var handles = new long[RingSize];

            var texDesc = new Texture2DDescription
            {
                Width = (uint)ow,
                Height = (uint)oh,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                MiscFlags = ResourceOptionFlags.SharedNTHandle | ResourceOptionFlags.SharedKeyedMutex
            };

            for (int i = 0; i < RingSize; i++)
            {
                sharedTextures[i] = dev!.CreateTexture2D(texDesc);
                keyedMutexes[i] = sharedTextures[i].QueryInterface<IDXGIKeyedMutex>();
                using var r1 = sharedTextures[i].QueryInterface<IDXGIResource1>();
                IntPtr h = r1.CreateSharedHandle(null, unchecked((Vortice.DXGI.SharedResourceFlags)(int)0x80000001 /*GENERIC_ALL*/), null);
                handles[i] = h.ToInt64();
            }

            Logger.Log($"[SeatAgent] Anillo de {RingSize} texturas compartidas creado ({ow}x{oh}).");

            // 4. Anunciar las texturas al servicio
            var initObj = new JsonObject
            {
                ["op"] = "textures",
                ["pid"] = Environment.ProcessId,
                ["width"] = ow,
                ["height"] = oh,
                ["luidLow"] = luid.LowPart,
                ["luidHigh"] = luid.HighPart,
                ["handles"] = new JsonArray(handles.Select(h => (JsonNode)h).ToArray())
            };
            channel.SendJson(initObj);

            // 5. Iniciar audio loopback
            using var audioCapture = StartAudio(channel);

            // 6. Iniciar hilo de captura de pantalla y cursor
            var captureThread = new Thread(() => CaptureLoop(channel, dev!, ctx!, o1, dupl, sharedTextures, keyedMutexes))
            {
                IsBackground = true,
                Name = "SeatAgent_Capture"
            };
            captureThread.Start();

            // 7. Bucle de inyección de entrada (hilo principal)
            var injector = new Injector();
            injector.EnsureInputDesktop();

            while (!_stopping)
            {
                var msg = channel.Receive();
                if (msg is null) break; // Tubería cerrada

                var (type, data) = msg.Value;
                switch (type)
                {
                    case SeatMsg.Input:
                        if (data.Length >= SeatInputEvent.Size)
                        {
                            var ev = MemoryMarshal.Read<SeatInputEvent>(data);
                            if (Interlocked.Increment(ref _inputsReceived) <= 3)
                                Logger.Log($"[SeatAgent] Entrada recibida: tipo={ev.Kind} flags={ev.Flags:X} dx={ev.Dx} dy={ev.Dy} código={ev.Code:X}");
                            injector.Inject(ev);
                        }
                        else Logger.Warning($"[SeatAgent] Mensaje de entrada de {data.Length} bytes (se esperaban {SeatInputEvent.Size}).");
                        break;

                    case SeatMsg.Json:
                        try
                        {
                            var json = SeatChannel.ParseJson(data);
                            if (json["op"]?.GetValue<string>() == "stop")
                            {
                                _stopping = true;
                            }
                        }
                        catch { }
                        break;
                }
            }

            _stopping = true;
            try { dupl.Dispose(); } catch { }
            for (int i = 0; i < RingSize; i++)
            {
                try { keyedMutexes[i].Dispose(); } catch { }
                try { sharedTextures[i].Dispose(); } catch { }
            }
            try { ctx?.Dispose(); } catch { }
            try { dev?.Dispose(); } catch { }

            Logger.Log("[SeatAgent] Agente finalizado limpiamente.");
            return 0;
        }
    }

    private static void CaptureLoop(
        SeatChannel channel,
        ID3D11Device dev,
        ID3D11DeviceContext ctx,
        IDXGIOutput1 output,
        IDXGIOutputDuplication dupl,
        ID3D11Texture2D[] sharedTextures,
        IDXGIKeyedMutex[] keyedMutexes)
    {
        int ringIdx = 0;
        byte[]? shapeBuffer = null;
        int lastPointerX = -1, lastPointerY = -1;
        bool lastPointerVisible = false;

        Span<byte> framePayload = stackalloc byte[12];
        Span<byte> posPayload = stackalloc byte[12];
        int sent = 0, ringBusy = 0;
        long nextStats = Environment.TickCount64 + 10_000;

        while (!_stopping)
        {
            if (Environment.TickCount64 >= nextStats)
            {
                nextStats += 10_000;
                Logger.Log($"[SeatAgent] 10 s: enviados={sent} anillo_ocupado={ringBusy} entrada={_inputsReceived} " +
                           $"inyección_fallida={Injector.Failures} último_error={Injector.LastError} {Injector.Diagnostics()}");
            }
            var res = dupl.AcquireNextFrame(100, out var info, out var dres);
            if (res == Vortice.DXGI.ResultCode.WaitTimeout) continue;
            if (res.Failure)
            {
                if (res == Vortice.DXGI.ResultCode.AccessLost)
                {
                    // Cambio de escritorio (pantalla de bloqueo, UAC, Ctrl+Alt+Supr) o de modo: la
                    // captura se rehace en el escritorio activo. El escritorio seguro solo se puede
                    // capturar como SYSTEM (puestos que piden contraseña); si no, se espera a que vuelva.
                    try { dupl.Dispose(); } catch { }
                    if (Recapture(dev, output) is not { } again) break;   // apagando
                    dupl = again;
                }
                continue;
            }

            try
            {
                // ── Cursor Position & Visibility ──
                // DDA solo rellena PointerPosition cuando LastMouseUpdateTime != 0; en los demás
                // fotogramas viene a cero y el cursor saltaría a la esquina.
                bool curVisible = info.PointerPosition.Visible;
                int curX = info.PointerPosition.Position.X;
                int curY = info.PointerPosition.Position.Y;
                if (info.LastMouseUpdateTime != 0 &&
                    (curX != lastPointerX || curY != lastPointerY || curVisible != lastPointerVisible))
                {
                    lastPointerX = curX;
                    lastPointerY = curY;
                    lastPointerVisible = curVisible;

                    BinaryPrimitives.WriteInt32LittleEndian(posPayload[0..], curX);
                    BinaryPrimitives.WriteInt32LittleEndian(posPayload[4..], curY);
                    BinaryPrimitives.WriteInt32LittleEndian(posPayload[8..], curVisible ? 1 : 0);
                    channel.Send(SeatMsg.CursorPos, posPayload);
                }

                // ── Cursor Shape ──
                if (info.PointerShapeBufferSize > 0)
                {
                    if (shapeBuffer is null || shapeBuffer.Length < info.PointerShapeBufferSize)
                        shapeBuffer = new byte[info.PointerShapeBufferSize];

                    unsafe
                    {
                        fixed (byte* pBuf = shapeBuffer)
                        {
                            var shapeRes = dupl.GetFramePointerShape(
                                info.PointerShapeBufferSize,
                                (IntPtr)pBuf,
                                out uint reqSize,
                                out var shapeInfo);

                            if (shapeRes.Success)
                            {
                                int shapeLen = (int)reqSize;
                                byte[] shapePayload = new byte[24 + shapeLen];
                                BinaryPrimitives.WriteUInt32LittleEndian(shapePayload.AsSpan(0), shapeInfo.Type);
                                BinaryPrimitives.WriteUInt32LittleEndian(shapePayload.AsSpan(4), shapeInfo.Width);
                                BinaryPrimitives.WriteUInt32LittleEndian(shapePayload.AsSpan(8), shapeInfo.Height);
                                BinaryPrimitives.WriteUInt32LittleEndian(shapePayload.AsSpan(12), shapeInfo.Pitch);
                                BinaryPrimitives.WriteInt32LittleEndian(shapePayload.AsSpan(16), shapeInfo.HotSpot.X);
                                BinaryPrimitives.WriteInt32LittleEndian(shapePayload.AsSpan(20), shapeInfo.HotSpot.Y);
                                shapeBuffer.AsSpan(0, shapeLen).CopyTo(shapePayload.AsSpan(24));

                                channel.Send(SeatMsg.CursorShape, shapePayload);
                            }
                        }
                    }
                }

                // ── Desktop Video Frame ──
                if (info.LastPresentTime != 0 && dres is not null)
                {
                    using var frameTex = dres.QueryInterface<ID3D11Texture2D>();
                    var km = keyedMutexes[ringIdx];
                    // Clave 0 = el presentador ya la consumió. Clave 1 = fotograma viejo que se saltó
                    // porque llegó otro más nuevo: también se puede reescribir. Sin esta segunda vía
                    // el anillo se atasca en esa textura y la imagen se congela.
                    if (TryAcquire(km, 0, 10) || TryAcquire(km, 1, 0))
                    {
                        try
                        {
                            ctx.CopyResource(sharedTextures[ringIdx], frameTex);
                        }
                        finally
                        {
                            km.ReleaseSync(1);
                        }
                        ctx.Flush();

                        long qpc = Stopwatch.GetTimestamp();
                        BinaryPrimitives.WriteInt32LittleEndian(framePayload[0..], ringIdx);
                        BinaryPrimitives.WriteInt64LittleEndian(framePayload[4..], qpc);
                        channel.Send(SeatMsg.Frame, framePayload);

                        ringIdx = (ringIdx + 1) % sharedTextures.Length;
                        sent++;
                    }
                    else ringBusy++;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[SeatAgent] Error en AcquireNextFrame: {ex.Message}");
            }
            finally
            {
                dres?.Dispose();
                dupl.ReleaseFrame();
            }
        }
    }

    /// <summary>Rehace la duplicación en el escritorio de entrada actual; null si el agente se está apagando.</summary>
    private static IDXGIOutputDuplication? Recapture(ID3D11Device dev, IDXGIOutput1 output)
    {
        string? lastError = null;
        while (!_stopping)
        {
            string desktop = Injector.AttachThreadToInputDesktop();
            try
            {
                var dupl = output.DuplicateOutput(dev);
                Logger.Log($"[SeatAgent] Captura rehecha en el escritorio '{desktop}'.");
                return dupl;
            }
            catch (Exception ex)
            {
                string err = $"{desktop}: {ex.Message}";
                if (err != lastError) Logger.Warning($"[SeatAgent] Sin captura por ahora ({err}); reintentando.");
                lastError = err;
                Thread.Sleep(250);
            }
        }
        return null;
    }

    private static IDisposable? StartAudio(SeatChannel channel)
    {
        try
        {
            using var mm = new MMDeviceEnumerator();
            var dev = mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (dev is null)
            {
                Logger.Warning("[SeatAgent] No se encontró dispositivo de audio para capturar en la sesión.");
                return null;
            }

            var cap = new LowLatencyLoopback(dev, 10);
            var f = cap.WaveFormat;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);

            channel.SendJson(new JsonObject
            {
                ["op"] = "audio_format",
                ["sampleRate"] = f.SampleRate,
                ["channels"] = f.Channels,
                ["bits"] = isFloat ? f.BitsPerSample : -f.BitsPerSample
            });

            cap.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0 && !_stopping)
                {
                    channel.Send(SeatMsg.Audio, e.Buffer.AsSpan(0, e.BytesRecorded));
                }
            };

            cap.StartRecording();
            Logger.Log($"[SeatAgent] Captura de audio loopback iniciada: {f}");
            return cap;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SeatAgent] No se pudo inicializar la captura de audio: {ex.Message}");
            return null;
        }
    }

    /// <summary>AcquireSync crudo con timeout sobre IDXGIKeyedMutex.</summary>
    public static unsafe bool TryAcquire(IDXGIKeyedMutex km, ulong key, uint ms)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)(*(void***)km.NativePointer)[8];
        return fn(km.NativePointer, key, ms) == 0;
    }
}

/// <summary>WASAPI loopback con búfer reducido a 10 ms (el default de NAudio es 100 ms).</summary>
internal sealed class LowLatencyLoopback(MMDevice dev, int bufferMs) : WasapiCapture(dev, false, bufferMs)
{
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}

/// <summary>Inyecta eventos de ratón y teclado dentro de la sesión con SendInput.</summary>
internal sealed class Injector
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const ushort RI_KEY_BREAK = 0x01;
    private const ushort RI_KEY_E0 = 0x02;
    private const ushort RI_KEY_E1 = 0x04;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, char[] pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private long _nextDesktopCheck;

    /// <summary>
    /// SendInput solo llega al escritorio de entrada (Default, o Winlogon durante el inicio de
    /// sesión, el bloqueo o una reconexión). Se vuelve a comprobar cada medio segundo y cada vez
    /// que SendInput falla: si el agente se quedara con el de la reconexión, la entrada se
    /// perdería entera aunque la sesión ya muestre el escritorio normal.
    /// </summary>
    public void EnsureInputDesktop(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now < _nextDesktopCheck) return;
        _nextDesktopCheck = now + 500;
        try
        {
            IntPtr hDesk = OpenInputDesktop(0, false, 0x10000000 /*GENERIC_ALL*/);
            if (hDesk == IntPtr.Zero) return;
            string input = DesktopName(hDesk), current = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
            if (!string.Equals(input, current, StringComparison.OrdinalIgnoreCase))
            {
                if (SetThreadDesktop(hDesk))
                    Logger.Log($"[SeatAgent] Inyectando en el escritorio '{input}' (antes '{current}').");
                else
                    Logger.Warning($"[SeatAgent] No se pudo pasar al escritorio '{input}': {Marshal.GetLastWin32Error()}");
            }
            CloseDesktop(hDesk);   // si es el del hilo, el sistema lo mantiene abierto
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Pasa el hilo que llama al escritorio de entrada actual (Default, o Winlogon si la sesión
    /// está bloqueada) y devuelve su nombre, o "inaccesible" si no hay permiso (no SYSTEM).
    /// </summary>
    public static string AttachThreadToInputDesktop()
    {
        IntPtr hDesk = OpenInputDesktop(0, false, 0x10000000 /*GENERIC_ALL*/);
        if (hDesk == IntPtr.Zero) return $"inaccesible ({Marshal.GetLastWin32Error()})";
        string name = DesktopName(hDesk);
        if (!SetThreadDesktop(hDesk)) name += $" (SetThreadDesktop {Marshal.GetLastWin32Error()})";
        CloseDesktop(hDesk);   // si es el del hilo, el sistema lo mantiene abierto
        return name;
    }

    private static string DesktopName(IntPtr hDesk)
    {
        var buf = new char[256];
        return hDesk != IntPtr.Zero && GetUserObjectInformation(hDesk, 2 /*UOI_NAME*/, buf, buf.Length * 2, out _)
            ? new string(buf).Split('\0')[0]
            : "";
    }

    public static int Failures, LastError;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, char[] buf, int max);

    /// <summary>Cursor, ventana activa y escritorio de entrada de la sesión (diagnóstico).</summary>
    public static string Diagnostics()
    {
        string cursor = GetCursorPos(out var p) ? $"{p.X},{p.Y}" : $"error {Marshal.GetLastWin32Error()}";
        IntPtr fg = GetForegroundWindow();
        string fore = "ninguna";
        if (fg != IntPtr.Zero)
        {
            GetWindowThreadProcessId(fg, out uint pid);
            var cls = new char[128];
            int n = GetClassName(fg, cls, cls.Length);
            string proc = "?";
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            fore = $"{proc}/{new string(cls, 0, n)}";
        }
        IntPtr hDesk = OpenInputDesktop(0, false, 0x0001 /*DESKTOP_READOBJECTS*/);
        string input = hDesk != IntPtr.Zero ? DesktopName(hDesk) : $"inaccesible ({Marshal.GetLastWin32Error()})";
        if (hDesk != IntPtr.Zero) CloseDesktop(hDesk);
        return $"cursor={cursor} activa={fore} escritorio_entrada={input}";
    }

    private void Send(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 0)
        {
            // Escritorio de entrada cambiado (o bloqueado): reintentar una vez en el actual.
            EnsureInputDesktop(force: true);
            if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 0)
            {
                LastError = Marshal.GetLastWin32Error();
                Interlocked.Increment(ref Failures);
            }
        }
    }

    public void Inject(SeatInputEvent ev)
    {
        EnsureInputDesktop();

        if (ev.Kind == SeatInputEvent.Mouse)
        {
            uint flags = (ev.Dx != 0 || ev.Dy != 0) ? MOUSEEVENTF_MOVE : 0;
            uint mouseData = 0;

            if ((ev.Flags & 0x0001) != 0) flags |= MOUSEEVENTF_LEFTDOWN;
            if ((ev.Flags & 0x0002) != 0) flags |= MOUSEEVENTF_LEFTUP;
            if ((ev.Flags & 0x0004) != 0) flags |= MOUSEEVENTF_RIGHTDOWN;
            if ((ev.Flags & 0x0008) != 0) flags |= MOUSEEVENTF_RIGHTUP;
            if ((ev.Flags & 0x0010) != 0) flags |= MOUSEEVENTF_MIDDLEDOWN;
            if ((ev.Flags & 0x0020) != 0) flags |= MOUSEEVENTF_MIDDLEUP;
            if ((ev.Flags & 0x0040) != 0) { flags |= MOUSEEVENTF_XDOWN; mouseData = 1; }
            if ((ev.Flags & 0x0080) != 0) { flags |= MOUSEEVENTF_XUP; mouseData = 1; }
            if ((ev.Flags & 0x0100) != 0) { flags |= MOUSEEVENTF_XDOWN; mouseData = 2; }
            if ((ev.Flags & 0x0200) != 0) { flags |= MOUSEEVENTF_XUP; mouseData = 2; }
            if ((ev.Flags & 0x0400) != 0) { flags |= MOUSEEVENTF_WHEEL; mouseData = (uint)ev.Data; }
            if ((ev.Flags & 0x0800) != 0) { flags |= MOUSEEVENTF_HWHEEL; mouseData = (uint)ev.Data; }

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = ev.Dx,
                    dy = ev.Dy,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            };
            Send(input);
        }
        else if (ev.Kind == SeatInputEvent.Keyboard && (ev.Flags & RI_KEY_E1) != 0)
        {
            // Pausa (E1 1D 45): no se puede mandar como scancode; va por su tecla virtual.
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0x13 /*VK_PAUSE*/, dwFlags = (ev.Flags & RI_KEY_BREAK) != 0 ? KEYEVENTF_KEYUP : 0 }
            };
            Send(input);
        }
        else if (ev.Kind == SeatInputEvent.Keyboard)
        {
            uint flags = KEYEVENTF_SCANCODE;
            if ((ev.Flags & RI_KEY_BREAK) != 0) flags |= KEYEVENTF_KEYUP;
            if ((ev.Flags & RI_KEY_E0) != 0) flags |= KEYEVENTF_EXTENDEDKEY;

            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT
                {
                    wScan = ev.Code,
                    wVk = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            };
            Send(input);
        }
    }
}
