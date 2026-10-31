using System.Runtime.InteropServices;
using System.Text;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Ventanas de la consola entre monitores. Con la mitad de la consola en una pantalla virtual, el
/// monitor físico sigue siendo una pantalla del escritorio de la consola, tapada por los
/// presentadores: nada debe quedarse en ella.
/// <list type="bullet">
/// <item>Al hacer principal la virtual (<c>--display primary</c>) se anotan antes las ventanas del
/// físico y después se llevan a la virtual: tras el cambio, Windows tarda en recolocarlas y sus
/// coordenadas aún no dicen en qué monitor están.</item>
/// <item>Mientras dura, el presentador de la consola barre cada segundo (<see cref="Sweep"/>) por si
/// alguna ventana nueva o que recuerda su sitio se abre en el físico.</item>
/// </list>
/// Al quitar la virtual, Windows devuelve solo sus ventanas al monitor que queda. Las ventanas de
/// programas elevados no se pueden mover sin elevación y se quedan donde estaban.
/// </summary>
internal static class ConsoleWindows
{
    public readonly record struct Area(int X, int Y, int W, int H)
    {
        public static Area Of(DisplayInfo d) => new(d.PosX, d.PosY, d.Width, d.Height);
        public bool ContainsCenterOf(RECT r)
        {
            int cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
            return cx >= X && cx < X + W && cy >= Y && cy < Y + H;
        }
    }

    /// <summary>
    /// Ventanas de aplicación cuyo centro está en ese monitor. Las minimizadas están aparcadas en
    /// (-32000,-32000): cuenta su posición normal, que es donde volverán al restaurarlas.
    /// </summary>
    public static List<(IntPtr Hwnd, RECT Rect)> On(Area monitor)
    {
        var found = new List<(IntPtr, RECT)>();
        EnumWindows((hwnd, _) =>
        {
            if (Movable(hwnd) && Where(hwnd) is { } r && monitor.ContainsCenterOf(r)) found.Add((hwnd, r));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static RECT? Where(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            return GetWindowPlacement(hwnd, ref wp) ? wp.rcNormalPosition : null;
        }
        return GetWindowRect(hwnd, out var r) ? r : null;
    }

    /// <summary>Programa de la ventana, para el log.</summary>
    public static string Describe(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return $"{p.ProcessName} ({pid})"; }
        catch { return $"pid {pid}"; }
    }

    /// <summary>Las lleva a <paramref name="to"/> en la misma posición relativa que tenían en <paramref name="from"/>.</summary>
    /// <returns>Cuántas se movieron y las que no se pudieron mover.</returns>
    public static (int Moved, List<IntPtr> Failed) Move(IEnumerable<(IntPtr Hwnd, RECT Rect)> windows, Area from, Area to)
    {
        int moved = 0;
        var failed = new List<IntPtr>();
        foreach (var (hwnd, r) in windows)
            if (MoveTo(hwnd, r, from, to)) moved++; else failed.Add(hwnd);
        return (moved, failed);
    }

    /// <summary>Barrido del presentador de la consola: lo que esté en el físico pasa a la virtual.</summary>
    public static (int Moved, List<IntPtr> Failed) Sweep(Area physical, Area virtualDisplay)
    {
        var stray = On(physical);
        return stray.Count == 0 ? (0, []) : Move(stray, physical, virtualDisplay);
    }

    private static readonly string[] ShellClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW"];

    /// <summary>Ventanas de aplicación visibles: ni de herramientas (presentadores, escudo), ni del shell, ni ocultas por DWM.</summary>
    private static bool Movable(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        return !ShellClasses.Contains(cls.ToString());
    }

    /// <summary>Misma posición relativa en el monitor de destino, sin salirse de él.</summary>
    private static bool MoveTo(IntPtr hwnd, RECT r, Area from, Area to)
    {
        int w = Math.Min(r.right - r.left, to.W), h = Math.Min(r.bottom - r.top, to.H);
        int x = to.X + Math.Clamp((int)((long)(r.left - from.X) * to.W / Math.Max(1, from.W)), 0, to.W - w);
        int y = to.Y + Math.Clamp((int)((long)(r.top - from.Y) * to.H / Math.Max(1, from.H)), 0, to.H - h);

        // Maximizadas y minimizadas: se mueve su posición normal y Windows las maximiza/minimiza en
        // el monitor de esa posición.
        if (IsZoomed(hwnd) || IsIconic(hwnd))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) return false;
            int nw = Math.Min(wp.rcNormalPosition.right - wp.rcNormalPosition.left, to.W);
            int nh = Math.Min(wp.rcNormalPosition.bottom - wp.rcNormalPosition.top, to.H);
            wp.rcNormalPosition = new RECT { left = to.X, top = to.Y, right = to.X + nw, bottom = to.Y + nh };
            wp.showCmd = IsIconic(hwnd) ? SW_SHOWMINNOACTIVE : SW_MAXIMIZE;
            return SetWindowPlacement(hwnd, ref wp);
        }
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14, SW_SHOWMINNOACTIVE = 7, SW_MAXIMIZE = 3;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
