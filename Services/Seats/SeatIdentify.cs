using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenStreamMS.Core.Helpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Identificar el hardware al configurar un puesto: el monitor muestra el logo, la salida de
/// audio suena y el dispositivo que se pulsa se ilumina en el panel. Todo ocurre en la sesión
/// de consola (la sesión 0 del servicio no tiene pantallas ni Raw Input): el servicio lanza
/// <c>OpenStreamMS.exe --identify &lt;cmd&gt;</c> allí, como hace <see cref="ConsoleDisplay"/>.
/// </summary>
public static class SeatIdentify
{
    private static readonly string Dir = ConsoleDisplay.SharedDir("identify");
    private static readonly string ActivityFile  = Path.Combine(Dir, "activity.json");
    private static readonly string KeepAliveFile = Path.Combine(Dir, "keepalive");

    /// <summary>Lo que tarda en apagarse el resaltado de un dispositivo tras su última pulsación.</summary>
    private const int ActiveMs = 700;
    /// <summary>Sin consultas del panel durante este tiempo, el escucha se cierra solo.</summary>
    private const int KeepAliveMs = 8000;

    private static readonly object _listenerLock = new();
    private static int _listenerPid;

    // ── Lado del servicio ────────────────────────────────────────────────────

    /// <summary>Muestra el logo en el monitor unos segundos (lo enciende antes si está fuera del escritorio).</summary>
    public static void TestMonitor(string devicePath) => Launch($"monitor \"{devicePath}\"", wait: true);

    /// <summary>Hace sonar un aviso por esa salida de audio.</summary>
    public static void TestAudio(string endpointId) => Launch($"audio \"{endpointId}\"", wait: true);

    /// <summary>
    /// Puertos USB de los dispositivos que se acaban de usar. Cada consulta mantiene vivo el
    /// escucha de Raw Input de la consola, o lo arranca si no lo está.
    /// </summary>
    public static List<string> ActiveInputPorts()
    {
        try { File.WriteAllText(KeepAliveFile, ""); } catch { /* el escucha ya se cerrará solo */ }
        EnsureListener();
        try
        {
            if (!File.Exists(ActivityFile)) return [];
            var last = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(ActivityFile)) ?? [];
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return last.Where(kv => now - kv.Value < ActiveMs).Select(kv => kv.Key).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];   // el escucha está reescribiendo el archivo: vale la próxima consulta
        }
    }

    private static void EnsureListener()
    {
        lock (_listenerLock)
        {
            if (_listenerPid != 0)
            {
                try { using var p = Process.GetProcessById(_listenerPid); if (!p.HasExited) return; }
                catch (ArgumentException) { /* ya terminó */ }
            }
            _listenerPid = 0;
            try { File.Delete(ActivityFile); } catch { }
            _listenerPid = Launch("input", wait: false);
        }
    }

    private static int Launch(string command, bool wait)
    {
        if (!ConsoleSession.TryGetUser(out var consoleId, out _, out _))
            throw new InvalidOperationException("No hay nadie con sesión iniciada en la consola.");
        if (!wait)
            return new ProcessInSession().LaunchInSession(consoleId, Environment.ProcessPath!, $"--identify {command}");
        // La prueba dura unos segundos: que no bloquee la petición del panel.
        _ = Task.Run(() =>
        {
            try
            {
                new ProcessInSession().LaunchInSession(consoleId, Environment.ProcessPath!, $"--identify {command}",
                                                       waitForExit: true, waitTimeoutMs: 20000);
            }
            catch (Exception ex) { Logger.Warning($"[Seats] --identify {command} falló: {ex.Message}"); }
        });
        return 0;
    }

    // ── Lado de la consola: OpenStreamMS.exe --identify monitor|audio|input [arg] ──

    public static int RunCommand(string[] args)
    {
        try
        {
            return args.ElementAtOrDefault(1) switch
            {
                "monitor" when args.Length > 2 => ShowMonitor(args[2]),
                "audio"   when args.Length > 2 => PlayChime(args[2]),
                "input"                        => ListenInput(),
                _                              => 2,
            };
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Identify] {string.Join(' ', args.Skip(1))}: {ex.Message}");
            return 1;
        }
    }

    // ── Monitor: el logo de OpenStreamMS a pantalla completa ─────────────────

    private const int MonitorSeconds = 6;
    private static readonly Color Accent = Color.FromArgb(0x7c, 0x3a, 0xed);

    private static int ShowMonitor(string devicePath)
    {
        SetProcessDpiAwarenessContext(-4);   // PER_MONITOR_AWARE_V2: coordenadas en píxeles reales
        var display = DisplayTopology.Enumerate().FirstOrDefault(d => d.DevicePath == devicePath);
        if (display is null) { Logger.Warning("[Identify] El monitor no está conectado."); return 1; }

        bool activated = false;
        if (!display.Active)
        {
            if (!DisplayTopology.Activate(devicePath)) { Logger.Warning($"[Identify] No se pudo encender '{display.FriendlyName}'."); return 1; }
            activated = true;
            // Windows tarda un momento en colocarlo en el escritorio.
            for (int i = 0; i < 20 && display is not { Active: true, Width: > 0 }; i++)
            {
                Thread.Sleep(150);
                display = DisplayTopology.Enumerate().FirstOrDefault(d => d.DevicePath == devicePath);
            }
        }
        try
        {
            if (display is not { Active: true, Width: > 0 }) return 1;
            Application.EnableVisualStyles();
            using var form = new LogoForm(display.FriendlyName)
            {
                Bounds = new Rectangle(display.PosX, display.PosY, display.Width, display.Height),
            };
            var close = new System.Windows.Forms.Timer { Interval = MonitorSeconds * 1000 };
            close.Tick += (_, _) => form.Close();
            form.Shown += (_, _) => close.Start();
            Application.Run(form);
            close.Dispose();
            return 0;
        }
        finally
        {
            if (activated) DisplayTopology.Deactivate(devicePath);   // se deja como estaba
        }
    }

    private sealed class LogoForm : Form
    {
        private readonly string _monitorName;

        public LogoForm(string monitorName)
        {
            _monitorName = monitorName;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(0x0f, 0x0f, 0x14);
            DoubleBuffered = true;
            Cursor.Hide();
            KeyDown += (_, _) => Close();
            MouseDown += (_, _) => Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // El mismo logo que el panel: cuadrado redondeado del color de acento con un "play".
            float size = Math.Min(ClientSize.Width, ClientSize.Height) / 4f;
            float x = (ClientSize.Width - size) / 2f, y = ClientSize.Height / 2f - size * 0.85f;
            using (var path = RoundedRect(new RectangleF(x, y, size, size), size * 7 / 32f))
            using (var accent = new SolidBrush(Accent))
                g.FillPath(accent, path);
            float u = size / 32f;
            g.FillPolygon(Brushes.White, [new PointF(x + 12 * u, y + 9 * u), new PointF(x + 23 * u, y + 16 * u), new PointF(x + 12 * u, y + 23 * u)]);

            using var title = new Font("Segoe UI Semibold", size / 4.5f, GraphicsUnit.Pixel);
            using var sub   = new Font("Segoe UI", size / 9f, GraphicsUnit.Pixel);
            using var muted = new SolidBrush(Color.FromArgb(0xa1, 0xa1, 0xaa));
            using var center = new StringFormat { Alignment = StringAlignment.Center };
            float ty = y + size + size * 0.18f;
            g.DrawString("OpenStreamMS", title, Brushes.White, ClientSize.Width / 2f, ty, center);
            g.DrawString(_monitorName, sub, muted, ClientSize.Width / 2f, ty + title.Height * 1.25f, center);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Cursor.Show();
            base.OnFormClosed(e);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    // ── Audio: un arpegio corto (do-mi-sol-do), dos veces ────────────────────

    private static int PlayChime(string endpointId)
    {
        using var mm = new MMDeviceEnumerator();
        using var device = mm.GetDevice(endpointId);
        int rate = device.AudioClient.MixFormat.SampleRate;   // el de la tarjeta: sin remuestreo
        var samples = Chime(rate);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        output.Init(new SampleArrayProvider(samples, rate));
        output.Play();
        // Lo que dura el aviso más el búfer de la tarjeta; no se espera a PlaybackStopped,
        // que algunas tarjetas no llegan a avisar.
        Thread.Sleep(samples.Length / 2 * 1000 / rate + 400);
        output.Stop();
        return 0;
    }

    private static float[] Chime(int rate)
    {
        double[] notes = [523.25, 659.25, 783.99, 1046.50];   // do5, mi5, sol5, do6
        const double step = 0.14, ring = 0.9, gap = 0.45;
        double once = step * (notes.Length - 1) + ring;
        int total = (int)((once * 2 + gap) * rate);
        var mono = new float[total];
        for (int rep = 0; rep < 2; rep++)
        {
            double start = rep * (once + gap);
            for (int n = 0; n < notes.Length; n++)
            {
                int first = (int)((start + n * step) * rate), len = (int)(ring * rate);
                for (int i = 0; i < len && first + i < total; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Min(1, t / 0.005) * Math.Exp(-t * 5);   // ataque corto, cola de campana
                    double w = 2 * Math.PI * notes[n] * t;
                    mono[first + i] += (float)(env * 0.22 * (Math.Sin(w) + 0.25 * Math.Sin(2 * w)));
                }
            }
        }
        var stereo = new float[total * 2];
        for (int i = 0; i < total; i++) stereo[2 * i] = stereo[2 * i + 1] = mono[i];
        return stereo;
    }

    private sealed class SampleArrayProvider(float[] samples, int rate) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, samples.Length - _pos);
            Array.Copy(samples, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    // ── Entrada: qué dispositivo se acaba de pulsar ──────────────────────────

    private static int ListenInput()
    {
        var last = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        bool dirty = false;
        using var window = new RawInputWindow(port =>
        {
            last[port] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            dirty = true;
        });

        var flush = new System.Windows.Forms.Timer { Interval = 100 };
        flush.Tick += (_, _) =>
        {
            if (KeepAliveExpired()) { Application.ExitThread(); return; }
            if (!dirty) return;
            dirty = false;
            try
            {
                var tmp = ActivityFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(last));
                File.Move(tmp, ActivityFile, overwrite: true);
            }
            catch (IOException) { dirty = true; }   // el servicio lo está leyendo: en la siguiente
        };
        flush.Start();
        Application.Run();
        flush.Dispose();
        return 0;
    }

    private static bool KeepAliveExpired()
    {
        try { return (DateTime.UtcNow - File.GetLastWriteTimeUtc(KeepAliveFile)).TotalMilliseconds > KeepAliveMs; }
        catch { return true; }
    }

    /// <summary>
    /// Ventana oculta que recibe Raw Input de teclados, ratones y mandos (también sin foco) y
    /// avisa con el puerto USB del dispositivo. Teclados: al pulsar una tecla. Ratones: botón,
    /// rueda o movimiento. Mandos HID: cuando cambia el estado de algún botón (muchos mandos
    /// mandan informes sin parar aunque nadie los toque).
    /// </summary>
    private sealed class RawInputWindow : NativeWindow, IDisposable
    {
        private readonly Action<string> _onActivity;
        private readonly Dictionary<IntPtr, string?> _ports = [];
        private readonly Dictionary<IntPtr, IntPtr> _preparsed = [];
        private readonly Dictionary<IntPtr, string> _buttons = [];

        public RawInputWindow(Action<string> onActivity)
        {
            _onActivity = onActivity;
            CreateHandle(new CreateParams { Caption = "OpenStreamMS.Identify" });
            var devices = new[] { (ushort)2, (ushort)6, (ushort)4, (ushort)5, (ushort)8 }   // ratón, teclado, joystick, mando, multieje
                .Select(usage => new RAWINPUTDEVICE { usUsagePage = 1, usUsage = usage, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle })
                .ToArray();
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                throw new System.ComponentModel.Win32Exception();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) OnInput(m.LParam);
            base.WndProc(ref m);
        }

        private void OnInput(IntPtr hRawInput)
        {
            uint size = 0, header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, header);
            if (size == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, header) != size) return;
                var h = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (h.hDevice == IntPtr.Zero) return;   // entrada inyectada (SendInput)
                IntPtr data = buf + (int)header;
                bool active = h.dwType switch
                {
                    RIM_TYPEKEYBOARD => (Marshal.ReadInt16(data, 2) & RI_KEY_BREAK) == 0,                // tecla pulsada
                    RIM_TYPEMOUSE    => Marshal.ReadInt16(data, 4) != 0                                  // botones/rueda
                                        || Marshal.ReadInt32(data, 12) != 0 || Marshal.ReadInt32(data, 16) != 0,   // movimiento
                    RIM_TYPEHID      => ButtonsChanged(h.hDevice, data),
                    _                => false,
                };
                if (active && PortOf(h.hDevice) is { } port) _onActivity(port);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private string? PortOf(IntPtr hDevice)
        {
            if (_ports.TryGetValue(hDevice, out var cached)) return cached;
            uint chars = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
            string? port = null;
            if (chars > 0)
            {
                IntPtr name = Marshal.AllocHGlobal((int)chars * 2);
                try
                {
                    if ((int)GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, name, ref chars) > 0 &&
                        Marshal.PtrToStringUni(name) is { Length: > 0 } path)
                        port = Core.Helpers.DeviceTopology.UsbPortOf(Core.Helpers.DeviceTopology.InstanceFromInterfacePath(path));
                }
                finally { Marshal.FreeHGlobal(name); }
            }
            return _ports[hDevice] = port;
        }

        /// <summary>Botones pulsados en cada informe HID (página de botones); avisa solo si cambian.</summary>
        private bool ButtonsChanged(IntPtr hDevice, IntPtr data)
        {
            if (Preparsed(hDevice) is not { } pp) return false;
            int reportSize = Marshal.ReadInt32(data, 0), count = Marshal.ReadInt32(data, 4);
            IntPtr report = data + 8;
            var pressed = new List<ushort>();
            var usages = new ushort[128];
            for (int r = 0; r < count; r++, report += reportSize)
            {
                uint n = (uint)usages.Length;
                if (HidP_GetUsages(HidP_Input, 9, 0, usages, ref n, pp, report, (uint)reportSize) == HIDP_STATUS_SUCCESS)
                    pressed.AddRange(usages.Take((int)n));
            }
            string state = string.Join(',', pressed.Order());
            bool changed = _buttons.TryGetValue(hDevice, out var prev) ? prev != state : pressed.Count > 0;
            _buttons[hDevice] = state;
            return changed && pressed.Count > 0;   // al pulsar, no al soltar
        }

        private IntPtr? Preparsed(IntPtr hDevice)
        {
            if (_preparsed.TryGetValue(hDevice, out var cached)) return cached == IntPtr.Zero ? null : cached;
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            IntPtr pp = IntPtr.Zero;
            if (size > 0)
            {
                pp = Marshal.AllocHGlobal((int)size);
                if ((int)GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, pp, ref size) <= 0)
                {
                    Marshal.FreeHGlobal(pp);
                    pp = IntPtr.Zero;
                }
            }
            _preparsed[hDevice] = pp;
            return pp == IntPtr.Zero ? null : pp;
        }

        public void Dispose()
        {
            foreach (var pp in _preparsed.Values.Where(p => p != IntPtr.Zero)) Marshal.FreeHGlobal(pp);
            _preparsed.Clear();
            DestroyHandle();
        }

        private const int WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007, RIDI_PREPARSEDDATA = 0x20000005;
        private const uint RIDEV_INPUTSINK = 0x100;
        private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
        private const short RI_KEY_BREAK = 1;
        private const int HidP_Input = 0;
        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint command, IntPtr data, ref uint size);
        [DllImport("hid.dll")]
        private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
            [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData, IntPtr report, uint reportLength);
    }
}
