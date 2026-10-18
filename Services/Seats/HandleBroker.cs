using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Duplica handles entre procesos (sesión del puesto -> servicio -> consola).
/// Permite que el presentador (consola) abra las texturas compartidas NT creadas
/// por el agente (sesión de streaming), aunque pertenezcan a usuarios distintos.
/// </summary>
internal static class HandleBroker
{
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_SAME_ACCESS = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Duplica un handle desde el proceso <paramref name="fromPid"/> al proceso <paramref name="toPid"/>.
    /// </summary>
    public static IntPtr DuplicateHandleTo(int fromPid, IntPtr handle, int toPid)
    {
        IntPtr srcProc = OpenProcess(PROCESS_DUP_HANDLE, false, (uint)fromPid);
        if (srcProc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess(fromPid={fromPid}) falló");
        try
        {
            IntPtr dstProc = OpenProcess(PROCESS_DUP_HANDLE, false, (uint)toPid);
            if (dstProc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess(toPid={toPid}) falló");
            try
            {
                if (!DuplicateHandle(srcProc, handle, dstProc, out IntPtr dup, 0, false, DUPLICATE_SAME_ACCESS))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"DuplicateHandle falló de pid {fromPid} a {toPid}");
                return dup;
            }
            finally { CloseHandle(dstProc); }
        }
        finally { CloseHandle(srcProc); }
    }
}
