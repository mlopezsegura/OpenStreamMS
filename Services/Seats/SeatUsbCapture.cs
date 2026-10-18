using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Gamepad;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Separación total de los ratones y teclados de un puesto, solo con drivers de Microsoft:
/// mientras el puesto está encendido, el dispositivo USB (o su interfaz, si es compuesto) pasa
/// del driver HID al genérico WinUSB (<c>winusb.inf</c>, incluido en Windows). Windows deja de
/// verlo como ratón/teclado, así que no mueve el cursor de la consola ni teclea en ella, y el
/// servicio lee sus informes USB directamente (<see cref="UsbHidReader"/>). Al apagar el puesto
/// vuelve a su driver (<c>input.inf</c>).
///
/// Cada cambio se anota en disco antes de hacerse: si el servicio se cae o se apaga el PC con
/// un puesto encendido, <see cref="RestoreAll"/> lo deshace en el siguiente arranque.
/// </summary>
internal static class UsbCapture
{
    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS", "usb-capture.json");
    private static readonly object _journalLock = new();

    /// <summary>GUID de interfaz propio que se le da a WinUSB para poder abrir el dispositivo.</summary>
    public static readonly Guid InterfaceGuid = new("6b1e3c4a-3f2b-4d77-9c41-0a1b2c3d4e5f");

    private const string MouseClass    = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
    private const string KeyboardClass = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
    private const string UsbDeviceClass = "{88BAE032-5A81-49f0-BC3D-A4FF138216D6}";

    /// <summary>
    /// Nodos USB a pasar a WinUSB para el dispositivo de ese puerto: el propio dispositivo, o
    /// las interfaces (<c>&amp;MI_xx</c>) de un compuesto que tienen un ratón o teclado debajo.
    /// Así una interfaz de un teclado gaming que no es de entrada (RGB, audio…) no se toca.
    /// </summary>
    public static List<string> FindTargets(string usbPort)
    {
        var device = GamepadSessionIsolation.Cm.DeviceIds("USB")
            .FirstOrDefault(id => !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(DeviceTopology.UsbPortOf(id), usbPort, StringComparison.OrdinalIgnoreCase));
        if (device is null) return [];

        var children = GamepadSessionIsolation.Cm.Children(device);
        var interfaces = children.Where(c => c.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = interfaces.Count > 0 ? interfaces : [device];
        return candidates.Where(HasInputCollection).ToList();
    }

    private static bool HasInputCollection(string node) =>
        GamepadSessionIsolation.Cm.Children(node).Any(c =>
            GamepadSessionIsolation.Cm.ClassGuidOf(c) is { } g &&
            (g.Equals(MouseClass, StringComparison.OrdinalIgnoreCase) || g.Equals(KeyboardClass, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Pasa el nodo a WinUSB. Devuelve false si no se pudo (el puesto sigue con Raw Input).</summary>
    public static bool Capture(string instanceId)
    {
        Journal(add: instanceId);
        try
        {
            // WinUSB lee DeviceInterfaceGUIDs al arrancar: sin él no expone interfaz que abrir.
            using (var k = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters"))
                k.SetValue("DeviceInterfaceGUIDs", new[] { InterfaceGuid.ToString("B") }, RegistryValueKind.MultiString);

            bool ok = Setup.Install(instanceId, winUsb: true);
            if (!ok) Journal(remove: instanceId);
            return ok;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SeatUsb] No se pudo pasar {instanceId} a WinUSB: {ex.Message}");
            Journal(remove: instanceId);
            return false;
        }
    }

    /// <summary>Devuelve el nodo a su driver HID de Windows.</summary>
    public static bool Release(string instanceId)
    {
        bool ok = false;
        try { ok = Setup.Install(instanceId, winUsb: false); }
        catch (Exception ex) { Logger.Warning($"[SeatUsb] No se pudo restaurar el driver de {instanceId}: {ex.Message}"); }
        // Si falla queda en el registro y se reintenta en el próximo arranque del servicio.
        if (ok) Journal(remove: instanceId);
        return ok;
    }

    /// <summary>Restaura lo que quedara capturado de una ejecución anterior (caída, apagado brusco).</summary>
    public static void RestoreAll()
    {
        foreach (var id in ReadJournal())
        {
            if (!GamepadSessionIsolation.Cm.Exists(id)) continue;   // desconectado: se reintenta en el próximo arranque
            Logger.Log($"[SeatUsb] Restaurando el driver de {id} (quedó capturado).");
            Release(id);
        }
    }

    private static List<string> ReadJournal()
    {
        lock (_journalLock)
        {
            try { return File.Exists(JournalPath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(JournalPath)) ?? [] : []; }
            catch { return []; }
        }
    }

    private static void Journal(string? add = null, string? remove = null)
    {
        lock (_journalLock)
        {
            var list = ReadJournal();
            if (add is not null && !list.Contains(add, StringComparer.OrdinalIgnoreCase)) list.Add(add);
            if (remove is not null) list.RemoveAll(i => i.Equals(remove, StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            File.WriteAllText(JournalPath, JsonSerializer.Serialize(list));
        }
    }

    // ── SetupAPI ─────────────────────────────────────────────────────────────

    private static unsafe class Setup
    {
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(IntPtr g, string? e, IntPtr h, int f);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr s, int i, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiOpenDeviceInfoW(IntPtr s, string id, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr g, IntPtr hwnd);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiBuildDriverInfoList(IntPtr s, ref SP_DEVINFO_DATA d, int t);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiEnumDriverInfoW(IntPtr s, ref SP_DEVINFO_DATA d, int t, int i, byte* drv);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiGetDriverInfoDetailW(IntPtr s, ref SP_DEVINFO_DATA d, byte* drv, byte* det, int sz, out int req);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiGetDeviceInstallParamsW(IntPtr s, ref SP_DEVINFO_DATA d, byte* p);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiSetDeviceInstallParamsW(IntPtr s, ref SP_DEVINFO_DATA d, byte* p);
        [DllImport("setupapi", SetLastError = true)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s, ref SP_DEVINFO_DATA d, int prop, byte* buf, int size);
        [DllImport("setupapi")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("newdev", SetLastError = true)] static extern bool DiInstallDevice(IntPtr hwnd, IntPtr s, ref SP_DEVINFO_DATA d, byte* drv, int flags, out bool reboot);

        const int SPDIT_CLASSDRIVER = 1, SPDIT_COMPATDRIVER = 2, SPDRP_CLASSGUID = 8;
        // Tamaños x64 de SP_DRVINFO_DATA_V2_W, SP_DRVINFO_DETAIL_DATA_W y SP_DEVINSTALL_PARAMS_W
        const int DRVINFO_SIZE = 1568, DETAIL_SIZE = 1584, DETAIL_INF_OFFSET = 544, DIP_SIZE = 584, DIP_PATH_OFFSET = 60;
        const int DI_ENUMSINGLEINF = 0x10000, DI_FLAGSEX_ALLOWEXCLUDEDDRVS = 0x800;

        /// <summary>
        /// winUsb: fuerza el modelo de <c>winusb.inf</c> (cambiando antes la clase a USBDevice, como
        /// hace el Administrador de dispositivos al elegir "Dispositivo WinUsb"). Si no, instala
        /// el driver compatible de <c>input.inf</c>, que vuelve a poner la clase HID.
        /// </summary>
        public static bool Install(string instanceId, bool winUsb)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            try
            {
                var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref dev))
                    throw new InvalidOperationException($"dispositivo no encontrado ({Marshal.GetLastWin32Error()})");

                int listType = SPDIT_COMPATDRIVER;
                if (winUsb)
                {
                    string cls = UsbDeviceClass + "\0";
                    fixed (char* pc = cls)
                        if (!SetupDiSetDeviceRegistryPropertyW(set, ref dev, SPDRP_CLASSGUID, (byte*)pc, cls.Length * 2))
                            throw new InvalidOperationException($"no se pudo cambiar la clase ({Marshal.GetLastWin32Error()})");
                    dev.ClassGuid = new Guid(UsbDeviceClass);

                    byte* dip = stackalloc byte[DIP_SIZE];
                    new Span<byte>(dip, DIP_SIZE).Clear();
                    *(int*)dip = DIP_SIZE;
                    SetupDiGetDeviceInstallParamsW(set, ref dev, dip);
                    *(int*)(dip + 4) |= DI_ENUMSINGLEINF;
                    *(int*)(dip + 8) |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS;
                    string inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "winusb.inf") + "\0";
                    fixed (char* c = inf) Buffer.MemoryCopy(c, dip + DIP_PATH_OFFSET, 520, inf.Length * 2);
                    if (!SetupDiSetDeviceInstallParamsW(set, ref dev, dip))
                        throw new InvalidOperationException($"SetupDiSetDeviceInstallParams ({Marshal.GetLastWin32Error()})");
                    listType = SPDIT_CLASSDRIVER;
                }

                if (!SetupDiBuildDriverInfoList(set, ref dev, listType))
                    throw new InvalidOperationException($"SetupDiBuildDriverInfoList ({Marshal.GetLastWin32Error()})");

                byte* drv = stackalloc byte[DRVINFO_SIZE];
                byte* det = stackalloc byte[8192];
                string wanted = winUsb ? "winusb.inf" : "input.inf";
                for (int i = 0; ; i++)
                {
                    new Span<byte>(drv, DRVINFO_SIZE).Clear();
                    *(int*)drv = DRVINFO_SIZE;
                    if (!SetupDiEnumDriverInfoW(set, ref dev, listType, i, drv))
                        throw new InvalidOperationException($"no hay driver {wanted} para este dispositivo");
                    *(int*)det = DETAIL_SIZE;
                    SetupDiGetDriverInfoDetailW(set, ref dev, drv, det, 8192, out _);
                    string infName = Path.GetFileName(new string((char*)(det + DETAIL_INF_OFFSET)));
                    if (!infName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!DiInstallDevice(IntPtr.Zero, set, ref dev, drv, 0, out _))
                        throw new InvalidOperationException($"DiInstallDevice ({Marshal.GetLastWin32Error():X})");
                    Logger.Log($"[SeatUsb] {instanceId} → {new string((char*)(drv + 16))} ({wanted}).");
                    return true;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}

/// <summary>
/// Lee un ratón/teclado puesto en WinUSB y traduce sus informes HID a <see cref="SeatInputEvent"/>.
/// Si se desconecta, reintenta abrirlo cada segundo (vuelve con el mismo driver WinUSB).
/// </summary>
internal sealed unsafe class UsbHidReader : IDisposable
{
    private readonly string _instanceId;
    private readonly Action<SeatInputEvent> _emit;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private SafeFileHandle? _file;
    private IntPtr _winusb;
    private readonly object _handleLock = new();

    public UsbHidReader(string instanceId, Action<SeatInputEvent> emit)
    {
        _instanceId = instanceId;
        _emit = emit;
        _thread = new Thread(Run) { IsBackground = true, Name = $"SeatUsb {instanceId}" };
        _thread.Start();
    }

    private void Run()
    {
        string? lastError = null;
        while (!_stopping)
        {
            try
            {
                if (Open(out byte pipe, out int packet, out var layout) is { } error)
                {
                    if (error != lastError) Logger.Warning($"[SeatUsb] Esperando a {_instanceId}: {error}");
                    lastError = error;
                    Close();
                    Thread.Sleep(1000);
                    continue;
                }
                lastError = null;
                Logger.Log($"[SeatUsb] Leyendo {_instanceId} ({layout.Summary}).");
                var state = new HidTranslator(layout, _emit);
                var managed = new byte[packet];
                int traced = 0;
                fixed (byte* buf = managed)
                    while (!_stopping && WinUsb_ReadPipe(_winusb, pipe, buf, packet, out int read, IntPtr.Zero))
                    {
                        if (read <= 0) continue;
                        // Diagnóstico: los primeros informes en crudo, para ver el formato real del aparato.
                        if (traced < 8) { traced++; Logger.Log($"[SeatUsb] informe: {Convert.ToHexString(managed, 0, read)}"); }
                        state.OnReport(new ReadOnlySpan<byte>(buf, read));
                    }
                state.ReleaseAll();
                if (!_stopping) Logger.Warning($"[SeatUsb] {_instanceId} desconectado ({Marshal.GetLastWin32Error()}).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[SeatUsb] {_instanceId}: {ex.Message}");
                Thread.Sleep(1000);
            }
            finally { Close(); }
        }
    }

    /// <summary>Abre el dispositivo; devuelve null si está listo para leer o el motivo por el que no.</summary>
    private string? Open(out byte pipe, out int packet, out HidReportLayout layout)
    {
        pipe = 0; packet = 8; layout = HidReportLayout.Empty;
        var g = UsbCapture.InterfaceGuid;
        int cr = CM_Get_Device_Interface_List_SizeW(out int len, ref g, _instanceId, 0);
        if (cr != 0 || len <= 1) return $"sin interfaz WinUSB (CM {cr})";
        var list = new char[len];
        if ((cr = CM_Get_Device_Interface_ListW(ref g, _instanceId, list, len, 0)) != 0) return $"sin interfaz WinUSB (CM {cr})";
        string path = new string(list).TrimEnd('\0').Split('\0')[0];
        if (path.Length == 0) return "sin interfaz WinUSB";

        var file = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
        if (file.IsInvalid) return $"CreateFile {Marshal.GetLastWin32Error()}";
        if (!WinUsb_Initialize(file, out var winusb)) { int e = Marshal.GetLastWin32Error(); file.Dispose(); return $"WinUsb_Initialize {e}"; }
        lock (_handleLock) { _file = file; _winusb = winusb; }

        byte* ifd = stackalloc byte[9];
        if (!WinUsb_QueryInterfaceSettings(winusb, 0, ifd)) return $"WinUsb_QueryInterfaceSettings {Marshal.GetLastWin32Error()}";
        byte iface = ifd[2], protocol = ifd[7];
        byte* pi = stackalloc byte[12];
        for (byte e = 0; e < ifd[4]; e++)
        {
            if (!WinUsb_QueryPipe(winusb, 0, e, pi)) continue;
            if (*(int*)pi == 3 /*interrupt*/ && (pi[4] & 0x80) != 0) { pipe = pi[4]; packet = *(ushort*)(pi + 6); break; }
        }
        if (pipe == 0) return $"sin endpoint de interrupción ({ifd[4]} endpoints)";

        // Descriptor de informe HID: GET_DESCRIPTOR(0x22) dirigido a la interfaz, pidiendo justo la
        // longitud que anuncia su descriptor HID (algunos ratones no responden si se pide de más).
        int want = ReportDescriptorLength(winusb, iface);
        int err = 0;
        byte* rd = stackalloc byte[1024];
        for (int attempt = 0; attempt < 3 && layout.Fields.Count == 0 && layout.Arrays.Count == 0; attempt++)
        {
            int n = attempt == 2 ? 1024 : (want is > 0 and <= 1024 ? want : 1024);
            ulong setup = 0x81UL | (6UL << 8) | (0x2200UL << 16) | ((ulong)iface << 32) | ((ulong)n << 48);
            if (WinUsb_ControlTransfer(winusb, setup, rd, n, out int rdl, IntPtr.Zero) && rdl > 0)
                layout = HidReportLayout.Parse(new ReadOnlySpan<byte>(rd, rdl));
            else err = Marshal.GetLastWin32Error();
        }

        if (layout.Fields.Count == 0 && layout.Arrays.Count == 0)
        {
            // Sin descriptor utilizable: protocolo "boot" (formato fijo) si es un ratón o teclado boot.
            if (HidReportLayout.Boot(protocol) is not { } boot)
                return $"descriptor HID no disponible (err {err}, longitud {want}) y no es un dispositivo boot (protocolo {protocol})";
            ulong setProtocol = 0x21UL | (0x0BUL << 8) | (0UL << 16) | ((ulong)iface << 32);   // SET_PROTOCOL(boot)
            bool protoOk = WinUsb_ControlTransfer(winusb, setProtocol, null, 0, out _, IntPtr.Zero);
            layout = boot;
            Logger.Warning($"[SeatUsb] {_instanceId}: sin descriptor HID (err {err}, longitud {want}); protocolo boot {(protoOk ? "activado" : $"rechazado ({Marshal.GetLastWin32Error()})")}.");
        }
        return null;
    }

    /// <summary>wDescriptorLength del descriptor de informe, sacado del descriptor HID (0x21) de la interfaz.</summary>
    private static int ReportDescriptorLength(IntPtr winusb, byte iface)
    {
        byte* cfg = stackalloc byte[1024];
        if (!WinUsb_GetDescriptor(winusb, 2 /*configuración*/, 0, 0, cfg, 1024, out int len)) return 0;
        bool inIface = false;
        for (int i = 0; i + 1 < len && cfg[i] > 0; i += cfg[i])
        {
            byte type = cfg[i + 1];
            if (type == 4) inIface = i + 2 < len && cfg[i + 2] == iface;
            else if (type == 0x21 && inIface && i + 8 < len)
                for (int k = 0; k < cfg[i + 5] && i + 8 + k * 3 < len; k++)
                    if (cfg[i + 6 + k * 3] == 0x22) return cfg[i + 7 + k * 3] | (cfg[i + 8 + k * 3] << 8);
        }
        return 0;
    }

    private void Close()
    {
        lock (_handleLock)
        {
            if (_winusb != IntPtr.Zero) { WinUsb_Free(_winusb); _winusb = IntPtr.Zero; }
            _file?.Dispose();
            _file = null;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        lock (_handleLock)
        {
            // Desbloquea el ReadPipe en curso.
            if (_winusb != IntPtr.Zero)
                for (byte p = 0x81; p < 0x90; p++) WinUsb_AbortPipe(_winusb, p);
            _file?.Dispose();
        }
        _thread.Join(2000);
    }

    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid g, string inst, int f);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_ListW(ref Guid g, string inst, char[] buf, int len, int f);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle h, out IntPtr w);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Free(IntPtr w);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_QueryInterfaceSettings(IntPtr w, byte alt, byte* desc);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_QueryPipe(IntPtr w, byte alt, byte idx, byte* info);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr w, byte pipe, byte* buf, int len, out int read, IntPtr ov);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_AbortPipe(IntPtr w, byte pipe);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_GetDescriptor(IntPtr w, byte type, byte index, ushort lang, byte* buf, int len, out int read);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_ControlTransfer(IntPtr w, ulong setup, byte* buf, int len, out int read, IntPtr ov);
}
