using OpenStreamMS.Services.Gamepad;
using OpenStreamMS.Services.Seats;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenStreamMS.Core.Helpers;

/// <summary>Teclado, ratón o mando físico conectado por USB.</summary>
public record InputDeviceInfo(string InstanceId, SeatDeviceKind Kind, string UsbPort, string Name);

/// <summary>
/// Ubica los dispositivos de entrada en su puerto USB físico. Los puestos asignan los
/// dispositivos por puerto (no por aparato): el puerto es la ruta de ubicación del nodo
/// USB del dispositivo, p. ej. <c>PCIROOT(0)#PCI(0801)#PCI(0003)#USBROOT(0)#USB(3)</c>.
/// </summary>
public static class DeviceTopology
{
    private const string MouseClass    = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
    private const string KeyboardClass = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const int CM_GETIDLIST_FILTER_CLASS   = 0x200;

    /// <summary>Teclados, ratones y mandos físicos conectados por USB, uno por puerto y tipo.</summary>
    public static List<InputDeviceInfo> EnumerateInputDevices()
    {
        var result = new List<InputDeviceInfo>();
        void Add(IEnumerable<string> ids, SeatDeviceKind kind)
        {
            foreach (var id in ids)
            {
                if (UsbDeviceOf(id) is not { } usb) continue;          // PS/2, RDP o virtual
                var port = LocationPath(usb) ?? usb;
                if (result.Any(d => d.Kind == kind && d.UsbPort == port)) continue;
                result.Add(new InputDeviceInfo(id, kind, port, DisplayName(usb)));
            }
        }
        Add(GamepadSessionIsolation.Cm.DeviceIds(MouseClass, CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT), SeatDeviceKind.Mouse);
        // Solo teclados de verdad: muchos aparatos (controladores RGB, teclas multimedia de
        // ratones…) se anuncian con una colección de teclado. Un teclado real expone una
        // interfaz USB "boot keyboard" (Class_03&SubClass_01&Prot_01).
        Add(GamepadSessionIsolation.Cm.DeviceIds(KeyboardClass, CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT)
                .Where(IsBootKeyboard), SeatDeviceKind.Keyboard);
        Add(GamepadSessionIsolation.Cm.GameControllers(), SeatDeviceKind.Gamepad);
        return result;
    }

    /// <summary>Puerto USB de cualquier nodo (colección HID, XUSB…), o null si no cuelga de un USB.</summary>
    public static string? UsbPortOf(string instanceId) =>
        UsbDeviceOf(instanceId) is { } usb ? LocationPath(usb) ?? usb : null;

    /// <summary>Instance id a partir de la ruta de interfaz (<c>\\?\HID#VID_..#..#{guid}</c>).</summary>
    public static string InstanceFromInterfacePath(string path)
    {
        var s = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        int g = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (g >= 0) s = s[..g];
        return s.Replace('#', '\\');
    }

    /// <summary>
    /// Primer antecesor que es un dispositivo USB (no una interfaz <c>&amp;MI_xx</c> de un
    /// compuesto): así un teclado con varias colecciones HID es un único puerto.
    /// </summary>
    private static string? UsbDeviceOf(string instanceId)
    {
        for (string? id = instanceId; id is not null; id = GamepadSessionIsolation.Cm.Parent(id))
        {
            if (!id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
            if (id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;
            return id;
        }
        return null;
    }

    // ── Propiedades de dispositivo ──────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(int devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref int size, int flags);

    private static readonly Guid DeviceProps = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static DEVPROPKEY LocationPaths  => new() { fmtid = DeviceProps, pid = 37 };
    private static DEVPROPKEY FriendlyName   => new() { fmtid = DeviceProps, pid = 14 };
    private static DEVPROPKEY DeviceDesc     => new() { fmtid = DeviceProps, pid = 2 };
    private static DEVPROPKEY BusReportedDesc => new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

    private static DEVPROPKEY CompatibleIds => new() { fmtid = DeviceProps, pid = 4 };

    /// <summary>La colección de teclado cuelga de una interfaz USB de teclado "boot".</summary>
    private static bool IsBootKeyboard(string keyboardInstance) =>
        GamepadSessionIsolation.Cm.Parent(keyboardInstance) is { } parent &&
        Strings(parent, CompatibleIds).Any(c => c.Contains("Class_03&SubClass_01&Prot_01", StringComparison.OrdinalIgnoreCase));

    private static string? LocationPath(string instanceId) => Strings(instanceId, LocationPaths).FirstOrDefault();

    private static string DisplayName(string usbInstance) =>
        Strings(usbInstance, BusReportedDesc).FirstOrDefault()
        ?? Strings(usbInstance, FriendlyName).FirstOrDefault()
        ?? Strings(usbInstance, DeviceDesc).FirstOrDefault()
        ?? usbInstance;

    private static string[] Strings(string instanceId, DEVPROPKEY key)
    {
        if (CM_Locate_DevNodeW(out int inst, instanceId, 0) != 0) return [];
        int size = 0;
        CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
        if (size <= 2) return [];
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) != 0) return [];
        return Encoding.Unicode.GetString(buf, 0, size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
