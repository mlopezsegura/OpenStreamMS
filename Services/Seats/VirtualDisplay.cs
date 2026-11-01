using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Pantalla virtual de SudoVDA (driver IddCx de Apollo, protocolo 0.2.x) en la consola. Las
/// pantallas IddCx de consola siempre aparecen en la sesión de consola, se creen desde donde se
/// creen (probado el 2026-10-26 desde una sesión RDP): el servicio puede crearla desde la sesión 0.
/// <para>
/// El driver la quita solo si deja de recibir pings (watchdog): si el servicio se cae, la consola
/// vuelve sola a su monitor físico.
/// </para>
/// </summary>
public sealed class VirtualDisplay : IDisposable
{
    private static readonly Guid Interface = new("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");

    private readonly SafeFileHandle _dev;
    private readonly Guid _monitor = Guid.NewGuid();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _ping;

    /// <summary>Adaptador (LUID, parte baja) y target de la pantalla: identifican su ruta CCD.</summary>
    public uint AdapterLow { get; }
    public uint TargetId { get; }

    private VirtualDisplay(SafeFileHandle dev, int width, int height, int hz)
    {
        _dev = dev;
        var (timeout, _) = Watchdog();
        var o = Io<AddParams, AddOut>(0x800, new AddParams
        {
            W = (uint)width, H = (uint)height, Hz = (uint)hz, Monitor = _monitor,
            Name = Fixed("OSMS-Console"), Serial = Fixed("OSMS0001"),
        });
        AdapterLow = (uint)(o.Luid & 0xFFFFFFFF);
        TargetId = o.Target;

        // Ping a un tercio del timeout del watchdog (mínimo 1 s).
        int interval = (int)Math.Max(1000, timeout * 1000 / 3);
        _ping = new Thread(() =>
        {
            while (!_cts.Token.WaitHandle.WaitOne(interval))
                try { Io<Empty, Empty>(0x888, default); } catch { /* el siguiente lo reintenta */ }
        }) { IsBackground = true, Name = "VirtualDisplay_Ping" };
        _ping.Start();
        Logger.Log($"[VirtualDisplay] Pantalla virtual {width}x{height}@{hz} creada (target {TargetId}, watchdog {timeout} s).");
    }

    /// <summary>Crea la pantalla; null si SudoVDA no está instalado o la rechaza.</summary>
    public static VirtualDisplay? TryCreate(int width, int height, int hz)
    {
        SafeFileHandle? dev = null;
        try
        {
            dev = Open();
            if (dev is null)
            {
                Logger.Warning("[VirtualDisplay] SudoVDA no está instalado: la consola no puede tener su propia mitad.");
                return null;
            }
            return new VirtualDisplay(dev, width, height, hz);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[VirtualDisplay] No se pudo crear la pantalla virtual: {ex.Message}");
            dev?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Io<RemoveParams, Empty>(0x801, new RemoveParams { Monitor = _monitor }); }
        catch (Exception ex) { Logger.Warning($"[VirtualDisplay] No se pudo quitar la pantalla virtual: {ex.Message}"); }
        _dev.Dispose();
        Logger.Log("[VirtualDisplay] Pantalla virtual quitada.");
    }

    private (uint Timeout, uint Countdown) Watchdog()
    {
        var d = Io<Empty, Dog>(0x803, default);
        return (d.Timeout, d.Countdown);
    }

    private static byte[] Fixed(string s)
    {
        var b = new byte[14];
        Encoding.ASCII.GetBytes(s, 0, Math.Min(13, s.Length), b, 0);
        return b;
    }

    // ── IOCTL ──────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct AddParams
    {
        public uint W, H, Hz;
        public Guid Monitor;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)] public byte[] Name;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)] public byte[] Serial;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AddOut { public long Luid; public uint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RemoveParams { public Guid Monitor; }
    [StructLayout(LayoutKind.Sequential)] private struct Dog { public uint Timeout, Countdown; }
    [StructLayout(LayoutKind.Sequential)] private struct Empty { }

    // FILE_DEVICE_UNKNOWN, METHOD_BUFFERED, FILE_ANY_ACCESS
    private static uint Ctl(uint fn) => (0x22u << 16) | (fn << 2);

    private TOut Io<TIn, TOut>(uint fn, TIn input) where TIn : struct where TOut : struct
    {
        // Empty mide 1 byte en .NET; al driver se le manda 0, como en la herramienta de pruebas.
        int inSize = typeof(TIn) == typeof(Empty) ? 0 : Marshal.SizeOf<TIn>();
        int outSize = typeof(TOut) == typeof(Empty) ? 0 : Marshal.SizeOf<TOut>();
        IntPtr pin = Marshal.AllocHGlobal(Math.Max(1, inSize)), pout = Marshal.AllocHGlobal(Math.Max(1, outSize));
        try
        {
            if (inSize > 0) Marshal.StructureToPtr(input, pin, false);
            if (!DeviceIoControl(_dev, Ctl(fn), pin, (uint)inSize, pout, (uint)outSize, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SudoVDA IOCTL 0x{fn:X}");
            return outSize > 0 ? Marshal.PtrToStructure<TOut>(pout) : default;
        }
        finally
        {
            if (inSize > 0) Marshal.DestroyStructure<TIn>(pin);
            Marshal.FreeHGlobal(pin);
            Marshal.FreeHGlobal(pout);
        }
    }

    private static SafeFileHandle? Open()
    {
        var g = Interface;
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);   // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == -1) return null;
        try
        {
            var d = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buf, 8);   // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W en x64
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, size, out _, IntPtr.Zero)) continue;
                    var h = CreateFile(Marshal.PtrToStringUni(buf + 4)!, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                    if (!h.IsInvalid) return h;
                    h.Dispose();
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid Class; public uint Flags; public IntPtr Reserved; }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint ret, IntPtr ov);
    [DllImport("setupapi", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, uint flags);
    [DllImport("setupapi", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint req, IntPtr info);
    [DllImport("setupapi")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
