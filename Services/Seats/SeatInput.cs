using OpenStreamMS.Core.Helpers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Entrada de los puestos en la consola: un único receptor de Raw Input por proceso (Windows solo
/// admite un destino por proceso y tipo de dispositivo; si cada presentador registrara el suyo,
/// el último le quitaría la entrada a los demás) que reparte cada evento al puesto dueño del puerto USB.
///
/// Raw Input no puede impedir que esos eventos lleguen también a la consola, así que actúa de
/// escudo, con lo mínimo en cada caso para que usar los dos ratones a la vez no se atasque:
/// <list type="bullet">
/// <item>Movimiento de un ratón de puesto: el cursor de la consola se congela donde está (ClipCursor de 1×1).</item>
/// <item>Clic o tecla de un puesto: además, una ventana propia casi invisible se pone bajo el cursor
/// y toma el foco, de modo que esos clics y teclas caen ahí y no hacen nada.</item>
/// <item>Cualquier entrada de la consola: se descongela el cursor y, si el escudo tenía el foco,
/// vuelve a la ventana que lo tenía. El cursor no salta: sigue donde se quedó.</item>
/// </list>
/// </summary>
internal static class SeatInputRouter
{
    private sealed record Route(Guid SeatId, IReadOnlyList<SeatDevice> Devices, SeatChannel Channel);

    private static readonly object _lock = new();
    private static readonly Dictionary<Guid, Route> _routes = [];
    private static volatile Dictionary<string, Route> _byPort = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<IntPtr, string> _portOf = new();

    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static uint _threadId;

    // Estado del escudo (solo se toca desde el hilo del enrutador)
    private static bool _frozen, _shielded;
    private static IntPtr _savedForeground;
    private static Native.RECT _freezeRect;

    private const int ShieldSize = 8;

    /// <summary>Eventos enviados a los puestos desde que arrancó el proceso (diagnóstico).</summary>
    public static int Routed;
    private const uint WM_APP_STOP = 0x8001;

    public static void Register(Guid seatId, IReadOnlyList<SeatDevice> devices, SeatChannel channel)
    {
        lock (_lock)
        {
            _routes[seatId] = new Route(seatId, devices, channel);
            Rebuild();
            if (_thread is null)
            {
                using var ready = new ManualResetEventSlim();
                _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "SeatInputRouter" };
                _thread.Start();
                ready.Wait(5000);
            }
        }
    }

    /// <summary>Quita la ruta solo si sigue siendo la de ese canal (un presentador nuevo del mismo puesto ya la sustituyó).</summary>
    public static void Unregister(Guid seatId, SeatChannel channel)
    {
        lock (_lock)
        {
            if (!_routes.TryGetValue(seatId, out var r) || r.Channel != channel) return;
            _routes.Remove(seatId);
            Rebuild();
            if (_routes.Count == 0 && _thread is not null)
            {
                Native.PostThreadMessage(_threadId, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
                _thread = null;
            }
        }
    }

    private static void Rebuild()
    {
        var map = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _routes.Values)
            foreach (var d in r.Devices)
                map[d.UsbPort] = r;
        _byPort = map;
    }

    // ── Hilo del enrutador ─────────────────────────────────────────────────────

    // Estático: la clase de ventana guarda el puntero (ver SeatHost.s_wndProc).
    private static readonly Native.WndProc s_wndProc = WndProc;

    private static void Run(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        try
        {
            _hwnd = CreateShieldWindow();
            Native.RAWINPUTDEVICE[] devs =
            [
                new() { usUsagePage = 1, usUsage = 2 /*ratón*/,   dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd },
                new() { usUsagePage = 1, usUsage = 6 /*teclado*/, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd },
            ];
            if (!Native.RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
                Logger.Warning($"[SeatInput] RegisterRawInputDevices falló: {Marshal.GetLastWin32Error()}");
            Logger.Log("[SeatInput] Enrutador de entrada y escudo de consola activos.");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SeatInput] No se pudo crear el enrutador de entrada: {ex.Message}");
            ready.Set();
            return;
        }
        ready.Set();

        while (Native.GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.message == WM_APP_STOP) break;
            Native.TranslateMessage(ref m);
            Native.DispatchMessage(ref m);
        }

        Release();
        Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _portOf.Clear();
        Logger.Log("[SeatInput] Enrutador de entrada detenido.");
    }

    /// <summary>
    /// Ventana de 8×8, encima de todo y con opacidad mínima (invisible pero recibe clics). Solo se
    /// muestra, bajo el cursor congelado, mientras un puesto hace clic o teclea.
    /// </summary>
    private static IntPtr CreateShieldWindow()
    {
        const string cls = "OpenStreamMS.SeatShield";
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = s_wndProc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = cls,
        };
        Native.RegisterClassEx(ref wc);

        IntPtr hwnd = Native.CreateWindowEx(
            Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED,
            cls, "OpenStreamMS", Native.WS_POPUP,   // oculta hasta que un puesto hace clic o teclea
            0, 0, ShieldSize, ShieldSize, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx (escudo) falló");
        Native.SetLayeredWindowAttributes(hwnd, 0, 1, Native.LWA_ALPHA);
        return hwnd;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_INPUT:
                try { OnRawInput(lParam); } catch (Exception ex) { Logger.Warning($"[SeatInput] {ex.Message}"); }
                break;   // DefWindowProc libera el bloque de Raw Input
            case Native.WM_SETCURSOR:
                if (_shielded) return 1;   // no cambiar la forma del cursor de la consola
                break;
            case Native.WM_CLOSE:
                return IntPtr.Zero;   // la cierra Unregister, no Alt+F4 desde el teclado de un puesto
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static unsafe void OnRawInput(IntPtr hRaw)
    {
        byte* buf = stackalloc byte[256];
        uint size = 256;
        uint hdrSize = (uint)sizeof(Native.RAWINPUTHEADER);
        if (Native.GetRawInputData(hRaw, Native.RID_INPUT, (IntPtr)buf, ref size, hdrSize) == uint.MaxValue) return;

        var hdr = *(Native.RAWINPUTHEADER*)buf;
        if (hdr.hDevice == IntPtr.Zero) return;   // entrada inyectada (SendInput, RDP): ni puesto ni consola

        bool isMouse = hdr.dwType == Native.RIM_TYPEMOUSE;
        if (!isMouse && hdr.dwType != Native.RIM_TYPEKEYBOARD) return;

        if (!_byPort.TryGetValue(PortOf(hdr.hDevice), out var route))
        {
            // Periférico de la consola: devolverle el cursor.
            Release();
            return;
        }

        SeatInputEvent ev;
        if (isMouse)
        {
            var m = *(Native.RAWMOUSE*)(buf + hdrSize);
            bool absolute = (m.usFlags & 1) != 0;   // tabletas/escritorio remoto: solo botones y rueda
            ev = new SeatInputEvent
            {
                Kind = SeatInputEvent.Mouse,
                Flags = m.usButtonFlags,
                Data = (short)m.usButtonData,
                Dx = absolute ? 0 : m.lLastX,
                Dy = absolute ? 0 : m.lLastY,
            };
            if (ev.Flags == 0 && ev.Dx == 0 && ev.Dy == 0) return;
        }
        else
        {
            var k = *(Native.RAWKEYBOARD*)(buf + hdrSize);
            ev = new SeatInputEvent { Kind = SeatInputEvent.Keyboard, Code = k.MakeCode, Flags = k.Flags };
        }

        Freeze(shield: !isMouse || ev.Flags != 0);
        Interlocked.Increment(ref Routed);
        try { route.Channel.Send(SeatMsg.Input, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref ev, 1))); }
        catch { /* el puesto se está cerrando */ }
    }

    private static string PortOf(IntPtr hDevice)
    {
        if (_portOf.TryGetValue(hDevice, out var cached)) return cached;
        string port = "";
        uint n = 0;
        Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, null, ref n);
        if (n > 0)
        {
            var sb = new StringBuilder((int)n + 1);
            if (Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, sb, ref n) > 0)
                port = DeviceTopology.UsbPortOf(DeviceTopology.InstanceFromInterfacePath(sb.ToString())) ?? "";
        }
        _portOf[hDevice] = port;
        return port;
    }

    // ── Escudo de consola ──────────────────────────────────────────────────────

    private static void Freeze(bool shield)
    {
        if (!_frozen)
        {
            Native.GetCursorPos(out var p);
            _freezeRect = new Native.RECT { left = p.x, top = p.y, right = p.x + 1, bottom = p.y + 1 };
            _frozen = true;
        }
        // Se reaplica en cada evento: Windows quita el ClipCursor al cambiar de ventana activa.
        Native.ClipCursor(ref _freezeRect);

        if (shield && !_shielded)
        {
            _shielded = true;
            _savedForeground = Native.GetForegroundWindow();
            int half = ShieldSize / 2;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _freezeRect.left - half, _freezeRect.top - half,
                                ShieldSize, ShieldSize, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            // Puede fallar si otro proceso tiene el foco; el primer clic sobre el escudo lo activa igualmente.
            Native.SetForegroundWindow(_hwnd);
        }
    }

    private static void Release()
    {
        if (_frozen)
        {
            _frozen = false;
            Native.ClipCursor(IntPtr.Zero);
        }
        if (_shielded)
        {
            _shielded = false;
            Native.ShowWindow(_hwnd, Native.SW_HIDE);   // que no se coma los clics de la consola
            if (_savedForeground != IntPtr.Zero && Native.GetForegroundWindow() == _hwnd)
                Native.SetForegroundWindow(_savedForeground);
        }
    }
}
