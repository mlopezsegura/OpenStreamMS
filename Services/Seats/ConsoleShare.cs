using System.Runtime.InteropServices;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Pantalla dividida en el monitor principal: la consola se queda con la mitad que no usa ningún
/// puesto. Corre en la consola, en el proceso de los presentadores.
/// <list type="bullet">
/// <item>Cada mitad de puesto se registra como barra de aplicación (AppBar): las ventanas maximizadas
/// de la consola y el área de trabajo dejan de meterse debajo.</item>
/// <item>El cursor de la consola se encierra en su mitad (ClipCursor). Windows quita el recorte al
/// cambiar de ventana activa, así que los presentadores lo reaplican en su bucle.</item>
/// </list>
/// Con las dos mitades en puestos, la consola queda detrás solo como anfitriona y no se recorta nada.
/// </summary>
internal static class ConsoleShare
{
    private sealed record Part(SeatScreenArea Area, IntPtr Hwnd, Native.RECT Monitor);

    private static readonly object _lock = new();
    private static readonly Dictionary<Guid, Part> _parts = [];
    private static Native.RECT? _consoleRect;
    /// <summary>
    /// Mitad de la consola con pantalla virtual propia (ver SeatManager, "mitad de la consola"):
    /// el cursor se queda en esa pantalla, que el presentador pinta en la mitad del monitor físico.
    /// Manda sobre <see cref="_consoleRect"/>, que solo es el respaldo con barra de aplicación.
    /// </summary>
    private static Native.RECT? _virtualRect;

    private static Native.RECT? Clip => _virtualRect ?? _consoleRect;

    public static void SetVirtual((int X, int Y, int W, int H)? rect)
    {
        _virtualRect = rect is { } r ? Rect(r) : null;
        Logger.Log($"[ConsoleShare] Pantalla virtual de la consola: {Describe(_virtualRect)}.");
        if (!SeatInputRouter.CursorFrozen) ReleaseClip();
    }

    private const uint WM_APP_APPBAR = 0x8010;   // DefWindowProc ignora los avisos del shell

    /// <summary>Registra o recoloca la mitad del puesto <paramref name="seatId"/> en el monitor principal.</summary>
    public static void Set(Guid seatId, IntPtr hwnd, SeatScreenArea area,
                           (int X, int Y, int W, int H) seat, (int X, int Y, int W, int H) monitor)
    {
        lock (_lock)
        {
            var abd = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = hwnd,
                uCallbackMessage = WM_APP_APPBAR,
                uEdge = area switch
                {
                    SeatScreenArea.Left => ABE_LEFT, SeatScreenArea.Top => ABE_TOP,
                    SeatScreenArea.Right => ABE_RIGHT, _ => ABE_BOTTOM,
                },
                rc = Rect(seat),
            };
            if (!_parts.ContainsKey(seatId) && SHAppBarMessage(ABM_NEW, ref abd) == 0)
                Logger.Warning("[ConsoleShare] El shell no aceptó la barra de aplicación del puesto.");
            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            abd.rc = Rect(seat);   // la mitad entera, aunque el shell proponga dejar sitio a la barra de tareas
            SHAppBarMessage(ABM_SETPOS, ref abd);

            _parts[seatId] = new Part(area, hwnd, Rect(monitor));
            Recompute();
        }
        Logger.Log($"[ConsoleShare] Monitor principal dividido: puesto en {area}, consola en {Describe(_consoleRect)}.");
    }

    public static void Remove(Guid seatId)
    {
        lock (_lock)
        {
            if (!_parts.Remove(seatId, out var part)) return;
            var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = part.Hwnd };
            SHAppBarMessage(ABM_REMOVE, ref abd);
            Recompute();
        }
        Logger.Log($"[ConsoleShare] Puesto fuera del monitor principal; consola en {Describe(_consoleRect)}.");
    }

    /// <summary>Reaplica el recorte del cursor de la consola si Windows lo ha quitado.</summary>
    public static void Enforce()
    {
        if (Clip is not { } r || SeatInputRouter.CursorFrozen) return;
        if (Native.GetClipCursor(out var cur) && cur.Equals(r)) return;
        Native.ClipCursor(ref r);
    }

    /// <summary>El escudo de Raw Input suelta el cursor: vuelve a la mitad de la consola, o libre.</summary>
    public static void ReleaseClip()
    {
        if (Clip is { } r) Native.ClipCursor(ref r);
        else Native.ClipCursor(IntPtr.Zero);
    }

    /// <summary>La consola tiene la mitad opuesta solo si hay un único puesto en el principal.</summary>
    private static void Recompute()
    {
        Native.RECT? next = null;
        if (_parts.Count == 1)
        {
            var p = _parts.Values.First();
            var other = new SeatMonitor
            {
                Area = p.Area switch
                {
                    SeatScreenArea.Left => SeatScreenArea.Right, SeatScreenArea.Right => SeatScreenArea.Left,
                    SeatScreenArea.Top => SeatScreenArea.Bottom, _ => SeatScreenArea.Top,
                },
            };
            var m = p.Monitor;
            next = Rect(other.Region(m.left, m.top, m.right - m.left, m.bottom - m.top));
        }
        _consoleRect = next;
        if (!SeatInputRouter.CursorFrozen) ReleaseClip();
    }

    private static Native.RECT Rect((int X, int Y, int W, int H) r) =>
        new() { left = r.X, top = r.Y, right = r.X + r.W, bottom = r.Y + r.H };

    private static string Describe(Native.RECT? r) =>
        r is { } x ? $"({x.left},{x.top})-({x.right},{x.bottom})" : "ninguna mitad (solo anfitriona)";

    // ── Barra de aplicación ─────────────────────────────────────────────────────

    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
}
