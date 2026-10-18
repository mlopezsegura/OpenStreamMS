using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenStreamMS.Core.Helpers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Presentador que corre en la sesión de consola (dentro de TrayApp o como proceso anfitrión).
/// Presenta las texturas compartidas en el monitor del puesto a pantalla completa en flip model,
/// reproduce el audio en la salida física del puesto y enruta los eventos Raw Input de los
/// dispositivos USB asignados al puesto.
/// </summary>
public static class SeatHost
{
    private static readonly ConcurrentDictionary<Guid, ActiveSeatHost> _activeSeats = new();

    /// <summary>
    /// Abre un puesto: conecta al servicio por su tubería, recibe las texturas compartidas,
    /// crea el presentador en el monitor del puesto, abre el audio y enruta los periféricos.
    /// </summary>
    public static void OpenSeat(
        Guid seatId,
        string pipeName,
        SeatMonitor? monitor,
        string? audioOutId,
        IReadOnlyList<SeatDevice> devices)
    {
        CloseSeat(seatId);
        var host = new ActiveSeatHost(seatId, pipeName, monitor, audioOutId, devices);
        _activeSeats[seatId] = host;
        host.Start();
    }

    /// <summary>Cierra el presentador de un puesto y libera sus recursos.</summary>
    public static void CloseSeat(Guid seatId)
    {
        if (_activeSeats.TryRemove(seatId, out var host))
        {
            host.Stop();
        }
    }

    /// <summary>Ejecución como proceso independiente: <c>OpenStreamMS.exe --seat-host &lt;pipeName&gt; [seatId]</c>.</summary>
    public static int RunStandalone(string pipeName, Guid seatId)
    {
        Logger.Log($"[SeatHost] Modo independiente iniciado para tubería '{pipeName}' (seatId={seatId})");
        var host = new ActiveSeatHost(seatId, pipeName, null, null, []);
        _activeSeats[seatId] = host;
        host.Start();
        host.WaitForExit();
        return 0;
    }
}

/// <summary>Instancia activa de un presentador de puesto en la consola.</summary>
internal sealed class ActiveSeatHost
{
    private readonly Guid _seatId;
    private readonly string _pipeName;
    private SeatMonitor? _monitor;
    private string? _audioOutId;
    private IReadOnlyList<SeatDevice> _devices;

    private volatile bool _stopping;
    private Thread? _thread;
    private readonly ManualResetEventSlim _exitEvent = new(false);

    // D3D11 & DXGI
    private ID3D11Device? _dev;
    private ID3D11DeviceContext? _ctx;
    private IDXGISwapChain? _sc;
    private IDXGIKeyedMutex[]? _keyedMutexes;
    private ID3D11Texture2D[]? _sharedTextures;
    private CursorRenderer? _cursorRenderer;

    // Audio
    private WasapiOut? _wasapiOut;
    private SeatJitterBuffer? _audioBuffer;

    // Ventana del presentador (la entrada la recibe SeatInputRouter)
    private IntPtr _hwnd;

    // Cursor state
    private int _curX, _curY;
    private bool _curVisible;

    public ActiveSeatHost(
        Guid seatId,
        string pipeName,
        SeatMonitor? monitor,
        string? audioOutId,
        IReadOnlyList<SeatDevice> devices)
    {
        _seatId = seatId;
        _pipeName = pipeName;
        _monitor = monitor;
        _audioOutId = audioOutId;
        _devices = devices;
    }

    public void Start()
    {
        _thread = new Thread(RunPresenter)
        {
            IsBackground = true,
            Name = $"SeatHost_{_seatId:N}"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        if (_hwnd != IntPtr.Zero)
        {
            Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        _exitEvent.Set();
    }

    public void WaitForExit() => _exitEvent.Wait();

    private void RunPresenter()
    {
        Logger.Log($"[SeatHost] Conectando a la tubería del servicio: {_pipeName}");
        SeatChannel channel;
        try
        {
            channel = SeatPipes.Connect(_pipeName, timeoutMs: 15000);
        }
        catch (Exception ex)
        {
            Logger.Error($"[SeatHost] No se pudo conectar a la tubería '{_pipeName}': {ex.Message}");
            _exitEvent.Set();
            return;
        }

        using (channel)
        {
            try
            {
                // 1. Enviar PID al servicio
                channel.SendJson(new JsonObject
                {
                    ["op"] = "ready",
                    ["pid"] = Environment.ProcessId
                });

                // 2. Esperar mensaje "init" con los handles duplicados y configuración
                var initMsg = channel.Receive();
                if (initMsg is null || initMsg.Value.Type != SeatMsg.Json)
                {
                    Logger.Error("[SeatHost] No se recibió el mensaje de inicialización.");
                    return;
                }

                var initJson = SeatChannel.ParseJson(initMsg.Value.Data);
                int width = initJson["width"]?.GetValue<int>() ?? 1920;
                int height = initJson["height"]?.GetValue<int>() ?? 1080;
                uint luidLow = initJson["luidLow"]?.GetValue<uint>() ?? 0;
                int luidHigh = initJson["luidHigh"]?.GetValue<int>() ?? 0;

                // Si viene monitor o devices en el initJson (modo standalone)
                if (_monitor is null && initJson["monitor"] is JsonObject monObj)
                {
                    _monitor = JsonSerializer.Deserialize<SeatMonitor>(monObj);
                }
                if (string.IsNullOrEmpty(_audioOutId) && initJson["audioOutId"] is JsonValue aVal)
                {
                    _audioOutId = aVal.GetValue<string>();
                }
                if (_devices.Count == 0 && initJson["devices"] is JsonArray devArr)
                {
                    _devices = JsonSerializer.Deserialize<List<SeatDevice>>(devArr) ?? [];
                }

                var handles = initJson["handles"]?.AsArray().Select(h => h!.GetValue<long>()).ToArray() ?? [];
                if (handles.Length == 0)
                {
                    Logger.Error("[SeatHost] El mensaje de inicialización no contiene handles de textura.");
                    return;
                }

                // 3. Ubicar el monitor en la topología de la consola
                var (monX, monY, monW, monH) = LocateMonitor(width, height);

                Logger.Log($"[SeatHost] Presentando en ventana ({monX},{monY} {monW}x{monH})");

                // 4. Crear ventana a pantalla completa: WS_EX_NOACTIVATE | WS_EX_TRANSPARENT
                _hwnd = CreatePresenterWindow(monX, monY, monW, monH);


                // 6. Crear dispositivo Direct3D 11 en el mismo adaptador
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
                IDXGIAdapter1? adapter = null;
                for (uint i = 0; factory.EnumAdapters1(i, out var ad).Success; i++)
                {
                    var l = ad.Description1.Luid;
                    if (l.LowPart == luidLow && l.HighPart == luidHigh)
                    {
                        adapter = ad;
                        break;
                    }
                }
                if (adapter is null)
                {
                    factory.EnumAdapters1(0, out adapter);
                }

                D3D11.D3D11CreateDevice(
                    adapter!, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _dev, out _ctx).CheckError();

                // 7. Crear SwapChain para _hwnd con FlipDiscard. El back buffer tiene el tamaño de
                //    la sesión (CopyResource exige tamaños idénticos y si no, no copia nada: pantalla
                //    negra); Scaling.Stretch lo ajusta a la ventana si el monitor tiene otro modo.
                var scDesc = new SwapChainDescription1
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    Format = Format.B8G8R8A8_UNorm,
                    BufferCount = 2,
                    BufferUsage = Usage.RenderTargetOutput,
                    SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.FlipDiscard,
                    Scaling = Scaling.Stretch,
                    Flags = SwapChainFlags.FrameLatencyWaitableObject
                };

                _sc = factory.CreateSwapChainForHwnd(_dev!, _hwnd, scDesc);
                using var sc2 = _sc.QueryInterface<IDXGISwapChain2>();
                sc2.MaximumFrameLatency = 1;
                IntPtr waitable = sc2.FrameLatencyWaitableObject;

                // 8. Abrir las texturas compartidas recibidas
                using var dev1 = _dev!.QueryInterface<ID3D11Device1>();
                _sharedTextures = new ID3D11Texture2D[handles.Length];
                _keyedMutexes = new IDXGIKeyedMutex[handles.Length];

                for (int i = 0; i < handles.Length; i++)
                {
                    _sharedTextures[i] = dev1.OpenSharedResource1<ID3D11Texture2D>(new IntPtr(handles[i]));
                    _keyedMutexes[i] = _sharedTextures[i].QueryInterface<IDXGIKeyedMutex>();
                }

                // Copia local del último fotograma: permite repintar el cursor sin fotograma nuevo
                // y devolver la textura compartida al agente en cuanto se ha copiado.
                _lastFrame = _dev!.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                });

                // 9. Inicializar renderizador de cursor (coordenadas de la sesión)
                _cursorRenderer = new CursorRenderer(_dev!, _ctx!, width, height);

                // 10. Inicializar reproductor de audio si hay salida asignada
                InitAudio();

                // 11. Hilo receptor de mensajes del canal (frames, audio, cursor)
                var receiveThread = new Thread(() => ReceiveLoop(channel))
                {
                    IsBackground = true,
                    Name = $"SeatHost_Receive_{_seatId:N}"
                };
                receiveThread.Start();

                // 12. Teclado y ratón del puesto: Raw Input compartido por todos los puestos del proceso.
                SeatInputRouter.Register(_seatId, _devices, channel);
                _inputChannel = channel;

                // 13. Bucle principal de presentación y bombeo de mensajes de Windows
                Logger.Log($"[SeatHost] Presentador activo ({width}x{height} → {monW}x{monH}) y enrutando periféricos.");
                IntPtr[] wakeHandles = [_wake.SafeWaitHandle.DangerousGetHandle()];
                int lastSeq = 0, lastCursor = 0;
                bool haveFrame = false;
                int copied = 0, busy = 0, presented = 0;
                long nextStats = Environment.TickCount64 + 10_000, nextLocate = Environment.TickCount64 + 2_000;
                var placed = (monX, monY, monW, monH);
                while (!_stopping)
                {
                    // Windows recoloca las pantallas por su cuenta unos segundos después de añadir
                    // el monitor (o al reconectarse): la ventana lo sigue para no quedarse en otro.
                    if (Environment.TickCount64 >= nextLocate)
                    {
                        nextLocate = Environment.TickCount64 + 2_000;
                        var now = LocateMonitor(width, height);
                        if (now != placed)
                        {
                            placed = now;
                            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, now.X, now.Y, now.W, now.H, Native.SWP_NOACTIVATE);
                            Logger.Log($"[SeatHost] El monitor se ha movido: ventana recolocada en ({now.X},{now.Y} {now.W}x{now.H}).");
                        }
                    }
                    if (Environment.TickCount64 >= nextStats)
                    {
                        nextStats += 10_000;
                        Logger.Log($"[SeatHost] 10 s: avisos={_frameSeq} copiados={copied} ocupados={busy} presentados={presented} entrada={SeatInputRouter.Routed} audio_cortes={_audioBuffer?.Underruns} audio_descartes={_audioBuffer?.Drops}");
                    }
                    // Despierta con un fotograma/cursor nuevo o con un mensaje de la ventana.
                    Native.MsgWaitForMultipleObjects(1, wakeHandles, false, 100, Native.QS_ALLINPUT);
                    PumpMessages();

                    bool dirty = false;
                    int seq = _frameSeq;
                    int frameIdx = _currentFrameIdx;
                    if (seq != lastSeq && frameIdx >= 0 && frameIdx < _keyedMutexes.Length)
                    {
                        var km = _keyedMutexes[frameIdx];
                        if (SeatAgent.TryAcquire(km, 1, 20))
                        {
                            try { _ctx!.CopyResource(_lastFrame, _sharedTextures[frameIdx]); }
                            finally { km.ReleaseSync(0); }
                            haveFrame = dirty = true;
                            copied++;
                        }
                        else busy++;
                        lastSeq = seq;   // si el agente ya la reescribió, llegará otro aviso
                    }
                    int cursor = _cursorSeq;
                    if (cursor != lastCursor) { lastCursor = cursor; dirty |= haveFrame; }

                    if (dirty)
                    {
                        Native.WaitForSingleObject(waitable, 100);
                        using var bb = _sc.GetBuffer<ID3D11Texture2D>(0);
                        _ctx!.CopyResource(bb, _lastFrame);
                        _cursorRenderer?.DrawCursor(bb, _curX, _curY, _curVisible);
                        _sc.Present(1, PresentFlags.None);
                        presented++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SeatHost] Error en el presentador: {ex.Message}");
            }
            finally
            {
                Cleanup();
            }
        }
    }

    private volatile int _currentFrameIdx = -1;
    private volatile int _frameSeq, _cursorSeq;
    private readonly AutoResetEvent _wake = new(false);
    private ID3D11Texture2D? _lastFrame;

    private void ReceiveLoop(SeatChannel channel)
    {
        while (!_stopping)
        {
            var msg = channel.Receive();
            if (msg is null) break;

            var (type, data) = msg.Value;
            switch (type)
            {
                case SeatMsg.Frame:
                    if (data.Length >= 4)
                    {
                        _currentFrameIdx = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
                        _frameSeq++;
                        _wake.Set();
                    }
                    break;

                case SeatMsg.CursorPos:
                    if (data.Length >= 12)
                    {
                        _curX = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
                        _curY = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
                        _curVisible = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4)) != 0;
                        _cursorSeq++;
                        _wake.Set();
                    }
                    break;

                case SeatMsg.CursorShape:
                    if (data.Length >= 24)
                    {
                        uint shapeType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
                        uint width = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
                        uint height = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
                        uint pitch = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4));
                        int hotX = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));
                        int hotY = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20, 4));
                        var shapeBytes = data.AsSpan(24);

                        _cursorRenderer?.UpdateShape(shapeType, (int)width, (int)height, (int)pitch, hotX, hotY, shapeBytes);
                        _cursorSeq++;
                        _wake.Set();
                    }
                    break;

                case SeatMsg.Audio:
                    if (_audioBuffer is not null && data.Length > 0)
                    {
                        try { _audioBuffer.Add(data); }
                        catch { }
                    }
                    break;

                case SeatMsg.Json:
                    try
                    {
                        var json = SeatChannel.ParseJson(data);
                        if (json["op"]?.GetValue<string>() == "stop")
                        {
                            _stopping = true;
                        }
                        else if (json["op"]?.GetValue<string>() == "audio_format")
                        {
                            int sampleRate = json["sampleRate"]?.GetValue<int>() ?? 48000;
                            int channels = json["channels"]?.GetValue<int>() ?? 2;
                            int bits = json["bits"]?.GetValue<int>() ?? 32;
                            SetupAudioFormat(sampleRate, channels, bits);
                        }
                    }
                    catch { }
                    break;
            }
        }
        _stopping = true;
    }

    private void InitAudio()
    {
        if (string.IsNullOrEmpty(_audioOutId)) return;
        try
        {
            using var mm = new MMDeviceEnumerator();
            var devs = mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var dev = devs.FirstOrDefault(d => d.ID.Equals(_audioOutId, StringComparison.OrdinalIgnoreCase))
                      ?? devs.FirstOrDefault(d => d.FriendlyName.Contains(_audioOutId, StringComparison.OrdinalIgnoreCase));

            if (dev is null)
            {
                Logger.Warning($"[SeatHost] Dispositivo de audio no encontrado: {_audioOutId}");
                return;
            }

            // Formato por defecto de la tarjeta hasta recibir audio_format
            var mix = dev.AudioClient.MixFormat;
            SetupAudioDevice(dev, mix);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SeatHost] No se pudo inicializar audio: {ex.Message}");
        }
    }

    private MMDevice? _currentAudioDevice;

    private void SetupAudioDevice(MMDevice dev, WaveFormat wf)
    {
        try
        {
            _wasapiOut?.Stop();
            _wasapiOut?.Dispose();

            _currentAudioDevice = dev;
            _audioBuffer = new SeatJitterBuffer(wf);
            _wasapiOut = new WasapiOut(dev, AudioClientShareMode.Shared, true, 10);
            _wasapiOut.Init(_audioBuffer);
            _wasapiOut.Play();
            Logger.Log($"[SeatHost] Salida de audio lista en '{dev.FriendlyName}' ({wf})");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SeatHost] Error al iniciar WasapiOut: {ex.Message}");
        }
    }

    private void SetupAudioFormat(int sampleRate, int channels, int bits)
    {
        if (_currentAudioDevice is null) return;
        try
        {
            var wf = bits > 0
                ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
                : new WaveFormat(sampleRate, -bits, channels);
            SetupAudioDevice(_currentAudioDevice, wf);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SeatHost] Error configurando formato de audio: {ex.Message}");
        }
    }

    // Estático: Windows guarda el puntero a este delegado en la clase de ventana, que sigue
    // registrada aunque el puesto se cierre y se reabra. Si el GC lo recogiera, el siguiente
    // DispatchMessage tumbaría el proceso entero (FailFast "callback on a garbage collected delegate").
    private static readonly Native.WndProc s_wndProc = Native.DefWindowProc;

    /// <summary>Posición y tamaño actuales del monitor del puesto en el escritorio de la consola.</summary>
    private (int X, int Y, int W, int H) LocateMonitor(int width, int height)
    {
        if (_monitor is null) return (0, 0, width, height);
        var live = DisplayTopology.Enumerate().FirstOrDefault(d => d.DevicePath == _monitor.DevicePath && d.Active);
        if (live is not null && live.Width > 0 && live.Height > 0)
            return (live.PosX, live.PosY, live.Width, live.Height);
        return _monitor.Width > 0 && _monitor.Height > 0 ? (0, 0, _monitor.Width, _monitor.Height) : (0, 0, width, height);
    }

    private IntPtr CreatePresenterWindow(int x, int y, int w, int h)
    {
        string clsName = $"SeatHostWnd_{_seatId:N}";
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = s_wndProc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = clsName
        };
        Native.RegisterClassEx(ref wc);

        uint exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
        uint style = Native.WS_POPUP | Native.WS_VISIBLE;

        IntPtr hwnd = Native.CreateWindowEx(
            exStyle, clsName, $"OpenStreamMS Seat Presenter {_seatId}",
            style, x, y, w, h, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx falló");

        return hwnd;
    }

    private static void PumpMessages()
    {
        while (Native.PeekMessage(out var m, IntPtr.Zero, 0, 0, 1))
        {
            Native.TranslateMessage(ref m);
            Native.DispatchMessage(ref m);
        }
    }

    private SeatChannel? _inputChannel;

    private void Cleanup()
    {
        if (_inputChannel is not null) SeatInputRouter.Unregister(_seatId, _inputChannel);

        try { _wasapiOut?.Stop(); } catch { }
        try { _wasapiOut?.Dispose(); } catch { }
        _cursorRenderer?.Dispose();
        try { _lastFrame?.Dispose(); } catch { }

        if (_keyedMutexes is not null)
            foreach (var km in _keyedMutexes)
                try { km.Dispose(); } catch { }

        if (_sharedTextures is not null)
            foreach (var tex in _sharedTextures)
                try { tex.Dispose(); } catch { }

        try { _sc?.Dispose(); } catch { }
        try { _ctx?.Dispose(); } catch { }
        try { _dev?.Dispose(); } catch { }

        if (_hwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _exitEvent.Set();
    }
}

/// <summary>Dibuja el cursor de la sesión en el swapchain back buffer con mezcla alfa.</summary>
internal sealed class CursorRenderer : IDisposable
{
    private readonly ID3D11Device _dev;
    private readonly ID3D11DeviceContext _ctx;
    private readonly int _targetWidth, _targetHeight;

    private ID3D11Texture2D? _cursorTex;
    private ID3D11ShaderResourceView? _cursorSrv;
    private ID3D11BlendState? _blendState;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11SamplerState? _sampler;

    private int _width, _height, _hotX, _hotY;
    private byte[]? _bgraPixels;

    public CursorRenderer(ID3D11Device dev, ID3D11DeviceContext ctx, int targetW, int targetH)
    {
        _dev = dev;
        _ctx = ctx;
        _targetWidth = targetW;
        _targetHeight = targetH;
        InitD3D();
    }

    private void InitD3D()
    {
        try
        {
            // Blend state: SrcAlpha, InvSrcAlpha
            var blendDesc = new BlendDescription
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false
            };
            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.SourceAlpha,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.Zero,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All
            };
            _blendState = _dev.CreateBlendState(blendDesc);

            // Shaders
            const string hlsl = @"
struct VS_IN { float2 pos : POSITION; float2 uv : TEXCOORD; };
struct PS_IN { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };
PS_IN vs_main(VS_IN input) {
    PS_IN output;
    output.pos = float4(input.pos, 0.0, 1.0);
    output.uv = input.uv;
    return output;
}
Texture2D tex : register(t0);
SamplerState samp : register(s0);
float4 ps_main(PS_IN input) : SV_TARGET {
    return tex.Sample(samp, input.uv);
}";

            var vsBlob = Compiler.Compile(hlsl, "vs_main", "cursor.hlsl", "vs_4_0");
            var psBlob = Compiler.Compile(hlsl, "ps_main", "cursor.hlsl", "ps_4_0");
            _vs = _dev.CreateVertexShader(vsBlob.Span);
            _ps = _dev.CreatePixelShader(psBlob.Span);

            InputElementDescription[] inputElements =
            [
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0)
            ];
            _inputLayout = _dev.CreateInputLayout(inputElements, vsBlob.Span);

            // Dynamic vertex buffer for quad (6 vertices: 2 triangles)
            _vertexBuffer = _dev.CreateBuffer(new BufferDescription
            {
                ByteWidth = 6 * 16,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });

            _sampler = _dev.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            });
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SeatHost] No se pudieron inicializar shaders de cursor: {ex.Message}");
        }
    }

    private readonly object _shapeLock = new();
    private bool _shapeDirty;

    /// <summary>
    /// Se llama desde el hilo receptor: solo convierte los píxeles. La textura se crea y se sube
    /// en <see cref="DrawCursor"/>, en el hilo del presentador, porque el contexto inmediato de
    /// D3D11 no admite llamadas desde dos hilos a la vez.
    /// </summary>
    public void UpdateShape(uint type, int w, int h, int pitch, int hotX, int hotY, ReadOnlySpan<byte> shapeBytes)
    {
        if (w <= 0 || h <= 0) return;
        lock (_shapeLock)
        {
            ConvertShape(type, w, h, pitch, hotX, hotY, shapeBytes);
            _shapeDirty = true;
        }
    }

    private void ConvertShape(uint type, int w, int h, int pitch, int hotX, int hotY, ReadOnlySpan<byte> shapeBytes)
    {
        _width = w;
        _height = (type == 1 /*Monochrome*/) ? h / 2 : h;
        _hotX = hotX;
        _hotY = hotY;

        int pixelCount = _width * _height;
        if (_bgraPixels is null || _bgraPixels.Length < pixelCount * 4)
            _bgraPixels = new byte[pixelCount * 4];

        if (type == 2 /*Color*/)
        {
            int rowBytes = _width * 4;
            for (int y = 0; y < _height; y++)
            {
                var srcRow = shapeBytes.Slice(y * pitch, rowBytes);
                srcRow.CopyTo(_bgraPixels.AsSpan(y * rowBytes, rowBytes));
            }
        }
        else if (type == 1 /*Monochrome*/)
        {
            int andMaskOffset = 0;
            int xorMaskOffset = _height * pitch;

            for (int y = 0; y < _height; y++)
            {
                int rowOffset = y * _width * 4;
                for (int x = 0; x < _width; x++)
                {
                    int byteIdx = (x / 8) + (y * pitch);
                    int bitIdx = 7 - (x % 8);
                    bool andBit = (shapeBytes[andMaskOffset + byteIdx] & (1 << bitIdx)) != 0;
                    bool xorBit = (shapeBytes[xorMaskOffset + byteIdx] & (1 << bitIdx)) != 0;

                    int px = rowOffset + x * 4;
                    if (!andBit && !xorBit) // Negro
                    {
                        _bgraPixels[px + 0] = 0;
                        _bgraPixels[px + 1] = 0;
                        _bgraPixels[px + 2] = 0;
                        _bgraPixels[px + 3] = 255;
                    }
                    else if (!andBit && xorBit) // Blanco
                    {
                        _bgraPixels[px + 0] = 255;
                        _bgraPixels[px + 1] = 255;
                        _bgraPixels[px + 2] = 255;
                        _bgraPixels[px + 3] = 255;
                    }
                    else // Transparente
                    {
                        _bgraPixels[px + 0] = 0;
                        _bgraPixels[px + 1] = 0;
                        _bgraPixels[px + 2] = 0;
                        _bgraPixels[px + 3] = 0;
                    }
                }
            }
        }

    }

    private void UploadShape()
    {
        // Recrear textura de cursor
        _cursorSrv?.Dispose();
        _cursorTex?.Dispose();

        _cursorTex = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });

        _ctx.UpdateSubresource(_bgraPixels.AsSpan(0, _width * _height * 4), _cursorTex, 0, (uint)(_width * 4));
        _cursorSrv = _dev.CreateShaderResourceView(_cursorTex);
    }

    public unsafe void DrawCursor(ID3D11Texture2D backBuffer, int curX, int curY, bool visible)
    {
        lock (_shapeLock)
        {
            if (_shapeDirty && _bgraPixels is not null && _width > 0 && _height > 0) UploadShape();
            _shapeDirty = false;
        }
        if (!visible || _cursorSrv is null || _vs is null || _ps is null || _width <= 0 || _height <= 0)
            return;

        int left = curX - _hotX;
        int top = curY - _hotY;
        int right = left + _width;
        int bottom = top + _height;

        // Convertir coordenadas de píxeles a NDC [-1, 1]
        float x0 = (left / (float)_targetWidth) * 2.0f - 1.0f;
        float y0 = 1.0f - (top / (float)_targetHeight) * 2.0f;
        float x1 = (right / (float)_targetWidth) * 2.0f - 1.0f;
        float y1 = 1.0f - (bottom / (float)_targetHeight) * 2.0f;

        Span<float> quad =
        [
            x0, y0, 0f, 0f,
            x1, y0, 1f, 0f,
            x0, y1, 0f, 1f,
            x0, y1, 0f, 1f,
            x1, y0, 1f, 0f,
            x1, y1, 1f, 1f
        ];

        var mapped = _ctx.Map(_vertexBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            fixed (float* pQuad = quad)
            {
                Buffer.MemoryCopy(pQuad, (void*)mapped.DataPointer, 6 * 16, 6 * 16);
            }
        }
        finally
        {
            _ctx.Unmap(_vertexBuffer!, 0);
        }

        using var rtv = _dev.CreateRenderTargetView(backBuffer);
        _ctx.OMSetRenderTargets(rtv);
        _ctx.RSSetViewport(0, 0, _targetWidth, _targetHeight);

        _ctx.OMSetBlendState(_blendState);
        _ctx.IASetInputLayout(_inputLayout);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.IASetVertexBuffer(0, _vertexBuffer!, 16, 0);

        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetShaderResource(0, _cursorSrv);
        _ctx.PSSetSampler(0, _sampler);

        _ctx.Draw(6, 0);
        // Soltar el back buffer como destino: el siguiente fotograma lo sobrescribe con CopyResource.
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    public void Dispose()
    {
        _cursorSrv?.Dispose();
        _cursorTex?.Dispose();
        _blendState?.Dispose();
        _vertexBuffer?.Dispose();
        _inputLayout?.Dispose();
        _vs?.Dispose();
        _ps?.Dispose();
        _sampler?.Dispose();
    }
}

internal static class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const uint WM_INPUT = 0x00FF;
    public const uint WM_CLOSE = 0x0010;

    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_DEVICENAME = 0x20000007;

    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEKEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWMOUSE
    {
        public ushort usFlags;
        public ushort pad;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, StringBuilder? pData, ref uint pcbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    public const uint QS_ALLINPUT = 0x04FF;

    // ── Escudo de consola (SeatInputRouter) ──
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x2;
    public const int SW_HIDE = 0, SW_SHOWNA = 8;

    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public const uint WM_SETCURSOR = 0x0020;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")] public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[] pHandles, bool fWaitAll, uint dwMilliseconds, uint dwWakeMask);
}
