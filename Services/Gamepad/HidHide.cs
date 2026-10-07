using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>
/// Cliente mínimo del driver <see href="https://github.com/nefarius/HidHide">HidHide</see>
/// por su dispositivo de control <c>\\.\HidHide</c> (ver DEVELOPER.md del proyecto).
/// <para>
/// Una entrada de la lista negra con la forma <c>HID\...\...!N</c> ("jail" de sesión,
/// HidHide 1.4.181+) oculta el dispositivo a todos los procesos salvo a los de la sesión
/// de Windows N. Se comprueba al abrir el dispositivo: un handle ya abierto no se revoca.
/// </para>
/// </summary>
internal static class HidHide
{
    private const uint DeviceType    = 32769;
    private const uint FileReadData  = 1;
    private static uint Ioctl(uint function) => (DeviceType << 16) | (FileReadData << 14) | (function << 2);

    private static readonly uint GetBlacklistCode = Ioctl(2050);
    private static readonly uint SetBlacklistCode = Ioctl(2051);
    private static readonly uint GetActiveCode    = Ioctl(2052);
    private static readonly uint SetActiveCode    = Ioctl(2053);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ_WRITE = 0x3;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? inBuf, int inSize, byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>Abre el driver, o null si HidHide no está instalado.</summary>
    public static SafeFileHandle? Open()
    {
        var h = CreateFileW(@"\\.\HidHide", GENERIC_READ, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    public static bool GetActive(SafeFileHandle device)
    {
        var buf = new byte[1];
        if (!DeviceIoControl(device, GetActiveCode, null, 0, buf, 1, out _, IntPtr.Zero)) throw new Win32Exception();
        return buf[0] != 0;
    }

    public static void SetActive(SafeFileHandle device, bool active)
    {
        if (!DeviceIoControl(device, SetActiveCode, [(byte)(active ? 1 : 0)], 1, null, 0, out _, IntPtr.Zero)) throw new Win32Exception();
    }

    public static List<string> GetBlacklist(SafeFileHandle device)
    {
        if (!DeviceIoControl(device, GetBlacklistCode, null, 0, null, 0, out int needed, IntPtr.Zero)) throw new Win32Exception();
        var buf = new byte[needed];
        if (needed > 0 && !DeviceIoControl(device, GetBlacklistCode, null, 0, buf, buf.Length, out needed, IntPtr.Zero)) throw new Win32Exception();
        return Encoding.Unicode.GetString(buf, 0, needed).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static void SetBlacklist(SafeFileHandle device, IEnumerable<string> entries)
    {
        // MULTI_SZ: cada cadena con su NUL y un NUL final
        var sb = new StringBuilder();
        foreach (var e in entries) sb.Append(e).Append('\0');
        sb.Append('\0');
        if (sb.Length == 1) sb.Append('\0');
        var buf = Encoding.Unicode.GetBytes(sb.ToString());
        if (!DeviceIoControl(device, SetBlacklistCode, buf, buf.Length, null, 0, out _, IntPtr.Zero)) throw new Win32Exception();
    }

    /// <summary>Entrada de lista negra que deja el dispositivo visible solo en <paramref name="sessionId"/>.</summary>
    public static string JailEntry(string instanceId, uint sessionId) => $"{instanceId}!{sessionId}";

    /// <summary>Instance id de una entrada "jail" (o null si no lo es).</summary>
    public static string? JailedInstance(string entry) =>
        entry.LastIndexOf('!') is int i and > 0 && uint.TryParse(entry.AsSpan(i + 1), out _) ? entry[..i] : null;
}
