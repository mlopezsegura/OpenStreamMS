using Microsoft.Extensions.Hosting;
using OpenStreamMS.Core.Api;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>
/// Aísla los mandos virtuales de ViGEmBus por sesión de Windows sin driver propio.
/// <para>
/// Los mandos virtuales son dispositivos de toda la máquina: el Steam de cada sesión
/// (host incluido) los ve y se los queda, así que el de la sesión de stream no recibe
/// la entrada en Big Picture/juegos. Aquí se fija la ACL de cada mando (y de sus hijos
/// HID) para que solo lo abran SYSTEM y el usuario de la sesión cuyo Sunshine lo creó.
/// </para>
/// <para>
/// Qué Sunshine creó cada mando se deduce del log: Sunshine escribe
/// "Gamepad N will be ..." justo antes de enchufarlo en ViGEmBus.
/// </para>
/// </summary>
public sealed partial class GamepadAccessGuard : BackgroundService
{
    private const string ViGEmBusHardwareId = @"Nefarius\ViGEmBus\Gen1";
    private static readonly TimeSpan Tick          = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BusRescan     = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MatchBefore   = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MatchAfter    = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AssignTimeout = TimeSpan.FromSeconds(10);
    private const int MaxRestartsPerPad = 3;

    [GeneratedRegex(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\]: \w+: Gamepad \d+ will be", RegexOptions.Multiline)]
    private static partial Regex GamepadCreatedRegex();

    private readonly StreamSessionService _sessions;

    private sealed class Pad
    {
        public required string InstanceId { get; init; }
        public DateTime FirstSeen { get; init; }
        public Guid? Session;
        public string? Sddl;
        public bool GaveUp;
        public int Restarts;
    }

    private sealed class LogCursor
    {
        public long Offset;
        public byte[] Head = [];
        public readonly List<DateTime> Pending = [];   // creaciones aún sin mando asignado
    }

    private readonly Dictionary<string, Pad>        _pads    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, LogCursor>    _cursors = [];
    private readonly Dictionary<string, string>     _readback = new(StringComparer.Ordinal); // SDDL pedido → SDDL que devuelve Windows
    private List<string> _buses = [];
    private DateTime _lastBusScan = DateTime.MinValue;
    private bool _accessWarned;

    public GamepadAccessGuard(StreamSessionService sessions) => _sessions = sessions;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                try { Scan(); }
                catch (Exception ex) { Logger.Warning($"[Gamepad] Error vigilando mandos: {ex.Message}"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    private void Scan()
    {
        var running = _sessions.GetRunningSunshineSessions();
        if (running.Count == 0 && _pads.Count == 0) return;

        if (DateTime.UtcNow - _lastBusScan > BusRescan || _buses.Count == 0)
        {
            _buses = FindViGEmBuses();
            _lastBusScan = DateTime.UtcNow;
        }

        foreach (var s in running) ReadGamepadEvents(s.Id, s.SunshineLog);
        foreach (var id in _cursors.Keys.Where(id => running.All(s => s.Id != id)).ToList())
            _cursors.Remove(id);

        // Mandos presentes = hijos directos de cada bus ViGEm
        var present = _buses.SelectMany(b => Cm.Children(b)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _pads.Keys.Where(k => !present.Contains(k)).ToList())
            _pads.Remove(gone);

        var now = DateTime.Now;
        foreach (var id in present)
        {
            if (!_pads.TryGetValue(id, out var pad))
                _pads[id] = pad = new Pad { InstanceId = id, FirstSeen = now };

            if (pad.Session is null && !pad.GaveUp)
                TryAssign(pad, running, now);

            if (pad.Session is { } sid && pad.Sddl is not null)
                Enforce(pad, running.FirstOrDefault(s => s.Id == sid).Name ?? sid.ToString());
        }
    }

    // ── Asignación mando → sesión ────────────────────────────────────────────

    private void TryAssign(Pad pad,
        IReadOnlyList<(Guid Id, string Name, uint RdpSessionId, string SunshineLog)> running, DateTime now)
    {
        Guid? owner = null;
        DateTime best = DateTime.MaxValue;
        foreach (var (sessionId, cursor) in _cursors)
        {
            foreach (var t in cursor.Pending)
            {
                if (t < pad.FirstSeen - MatchBefore || t > pad.FirstSeen + MatchAfter) continue;
                if (t < best) { best = t; owner = sessionId; }
            }
        }

        if (owner is { } o)
            _cursors[o].Pending.Remove(best);
        else if (now - pad.FirstSeen < AssignTimeout)
            return;                                   // aún puede llegar la línea del log
        else if (running.Count == 1)
            owner = running[0].Id;                    // sin línea en el log, pero solo hay una sesión
        else
        {
            pad.GaveUp = true;
            Logger.Warning($"[Gamepad] No se pudo saber qué sesión creó el mando {pad.InstanceId}; se deja sin restringir.");
            return;
        }

        var session = running.FirstOrDefault(s => s.Id == owner);
        var user = session.Id == Guid.Empty ? null : SessionUserSid(session.RdpSessionId);
        if (user is null)
        {
            pad.GaveUp = true;
            Logger.Warning($"[Gamepad] Sin usuario para la sesión de Windows {session.RdpSessionId}; mando {pad.InstanceId} sin restringir.");
            return;
        }

        pad.Session = owner;
        pad.Sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{user.Value})";
        Logger.Log($"[Gamepad] Mando {pad.InstanceId} → sesión '{session.Name}' (usuario {user.Translate(typeof(NTAccount))})", owner);
    }

    private void ReadGamepadEvents(Guid sessionId, string logPath)
    {
        if (!_cursors.TryGetValue(sessionId, out var cur))
            _cursors[sessionId] = cur = new LogCursor();
        try
        {
            if (!File.Exists(logPath)) return;
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = ReadHead(fs, 256);
            if (cur.Offset > fs.Length || (cur.Offset > 0 && !head.AsSpan(0, Math.Min(head.Length, cur.Head.Length)).SequenceEqual(cur.Head)))
                cur.Offset = 0;                       // log rotado al relanzar Sunshine
            if (fs.Length == cur.Offset) return;

            fs.Seek(cur.Offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8, false, 4096, leaveOpen: true);
            string chunk = sr.ReadToEnd();
            cur.Offset = fs.Length;
            cur.Head = head;

            var cutoff = DateTime.Now - MatchBefore - AssignTimeout;
            foreach (Match m in GamepadCreatedRegex().Matches(chunk))
            {
                if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) && t >= cutoff)
                    cur.Pending.Add(t);
            }
            cur.Pending.RemoveAll(t => t < cutoff);
        }
        catch (IOException) { /* Sunshine escribiendo; se reintenta en el siguiente tick */ }
    }

    private static byte[] ReadHead(FileStream fs, int count)
    {
        var buf = new byte[count];
        fs.Seek(0, SeekOrigin.Begin);
        int n = fs.ReadAtLeast(buf, count, throwOnEndOfStream: false);
        return n == count ? buf : buf[..n];
    }

    private static SecurityIdentifier? SessionUserSid(uint sessionId)
    {
        string user = Wts.Query(sessionId, Wts.WTSUserName);
        if (user.Length == 0) return null;
        string domain = Wts.Query(sessionId, Wts.WTSDomainName);
        try
        {
            var account = new NTAccount(domain.Length > 0 ? domain : ".", user);
            return (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException) { return null; }
    }

    // ── Aplicar la ACL ───────────────────────────────────────────────────────

    /// <summary>
    /// Pone la ACL en el mando y en todos sus descendientes (HID/XUSB). Los cambios de
    /// seguridad solo se aplican al arrancar el dispositivo, así que reinicia el nodo
    /// más alto que haya cambiado. Se repite en cada tick: los hijos que aparezcan
    /// tarde también quedan cubiertos.
    /// </summary>
    private void Enforce(Pad pad, string sessionName)
    {
        var nodes = new List<string> { pad.InstanceId };
        nodes.AddRange(Cm.Descendants(pad.InstanceId));

        var changed = new List<string>();
        foreach (var node in nodes)
        {
            try
            {
                string? current = DevSec.Get(node);
                if (current is not null && _readback.TryGetValue(pad.Sddl!, out var expected) && current == expected)
                    continue;
                if (current == pad.Sddl) continue;

                DevSec.Set(node, pad.Sddl!);
                if (DevSec.Get(node) is { } rb) _readback[pad.Sddl!] = rb;
                changed.Add(node);
            }
            catch (Win32Exception ex)
            {
                if (!_accessWarned)
                {
                    _accessWarned = true;
                    Logger.Warning($"[Gamepad] No se pudo fijar la seguridad de {node}: {ex.Message} (¿el servicio no corre como SYSTEM?)");
                }
                return;
            }
        }
        if (changed.Count == 0) return;

        if (pad.Restarts >= MaxRestartsPerPad)
        {
            Logger.Warning($"[Gamepad] El mando {pad.InstanceId} sigue cambiando tras {MaxRestartsPerPad} reinicios; no se reinicia más.");
            return;
        }
        pad.Restarts++;

        // Reiniciar el padre recrea los hijos con la ACL ya guardada; si solo cambió
        // un hijo nuevo, basta con reiniciar ese.
        var toRestart = changed.Contains(pad.InstanceId, StringComparer.OrdinalIgnoreCase)
            ? [pad.InstanceId]
            : changed.Where(c => !changed.Any(p => p != c && Cm.Descendants(p).Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();
        foreach (var node in toRestart)
        {
            try
            {
                DevSec.Restart(node);
                Logger.Log($"[Gamepad] Mando restringido a la sesión '{sessionName}': {node}", pad.Session);
            }
            catch (Win32Exception ex)
            {
                Logger.Warning($"[Gamepad] No se pudo reiniciar {node} para aplicar la ACL: {ex.Message}");
            }
        }
    }

    private static List<string> FindViGEmBuses() =>
        Cm.DeviceIds(@"ROOT\SYSTEM")
          .Where(id => Cm.HardwareIds(id).Any(h => h.Equals(ViGEmBusHardwareId, StringComparison.OrdinalIgnoreCase)))
          .ToList();

    // ── Interop ──────────────────────────────────────────────────────────────

    private static class Cm
    {
        private const int CR_SUCCESS = 0;
        private const int CM_LOCATE_DEVNODE_NORMAL = 0;
        private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
        private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
        private const int CM_DRP_HARDWAREID = 0x2;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Child(out int child, int devInst, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Sibling(out int sibling, int devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(int devInst, char[] buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out int len, string? filter, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(int devInst, int property, out int type, byte[]? buffer, ref int len, int flags);

        public static IEnumerable<string> DeviceIds(string enumerator)
        {
            const int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_SizeW(out int len, enumerator, flags) != CR_SUCCESS || len <= 1) return [];
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW(enumerator, buf, len, flags) != CR_SUCCESS) return [];
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        public static IEnumerable<string> HardwareIds(string deviceId)
        {
            if (CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return [];
            int len = 0;
            CM_Get_DevNode_Registry_PropertyW(inst, CM_DRP_HARDWAREID, out _, null, ref len, 0);
            if (len == 0) return [];
            var buf = new byte[len];
            if (CM_Get_DevNode_Registry_PropertyW(inst, CM_DRP_HARDWAREID, out _, buf, ref len, 0) != CR_SUCCESS) return [];
            return Encoding.Unicode.GetString(buf, 0, len).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        public static List<string> Children(string deviceId)
        {
            var result = new List<string>();
            if (CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return result;
            if (CM_Get_Child(out int child, inst, 0) != CR_SUCCESS) return result;
            do
            {
                if (Id(child) is { } id) result.Add(id);
            }
            while (CM_Get_Sibling(out child, child, 0) == CR_SUCCESS);
            return result;
        }

        public static List<string> Descendants(string deviceId)
        {
            var result = new List<string>();
            foreach (var c in Children(deviceId))
            {
                result.Add(c);
                result.AddRange(Descendants(c));
            }
            return result;
        }

        private static string? Id(int devInst)
        {
            var buf = new char[512];
            return CM_Get_Device_IDW(devInst, buf, buf.Length, 0) == CR_SUCCESS
                ? new string(buf).TrimEnd('\0')
                : null;
        }
    }

    private static class DevSec
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public int HeaderSize; public int InstallFunction;   // SP_CLASSINSTALL_HEADER
            public int StateChange; public int Scope; public int HwProfile;
        }

        private const int SPDRP_SECURITY_SDS = 0x18;
        private const int DIF_PROPERTYCHANGE = 0x12;
        private const int DICS_PROPCHANGE = 3;
        private const int DICS_FLAG_GLOBAL = 1;

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int prop, byte[]? buf, int size);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int prop, out int type, byte[]? buf, int size, out int required);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS p, int size);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        private static T With<T>(string instanceId, Func<IntPtr, SP_DEVINFO_DATA, T> action)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            try
            {
                var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref d))
                    throw new Win32Exception();
                return action(set, d);
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        public static string? Get(string instanceId) => With(instanceId, (set, d) =>
        {
            SetupDiGetDeviceRegistryPropertyW(set, ref d, SPDRP_SECURITY_SDS, out _, null, 0, out int required);
            if (required == 0) return null;               // sin ACL propia: la de la clase
            var buf = new byte[required];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref d, SPDRP_SECURITY_SDS, out _, buf, buf.Length, out _))
                return null;
            return Encoding.Unicode.GetString(buf).TrimEnd('\0');
        });

        public static void Set(string instanceId, string sddl) => With(instanceId, (set, d) =>
        {
            // Valida el SDDL antes de dárselo a SetupAPI
            _ = new RawSecurityDescriptor(sddl);
            var buf = Encoding.Unicode.GetBytes(sddl + "\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref d, SPDRP_SECURITY_SDS, buf, buf.Length))
                throw new Win32Exception();
            return 0;
        });

        public static void Restart(string instanceId) => With(instanceId, (set, d) =>
        {
            var p = new SP_PROPCHANGE_PARAMS
            {
                HeaderSize = 8, InstallFunction = DIF_PROPERTYCHANGE,
                StateChange = DICS_PROPCHANGE, Scope = DICS_FLAG_GLOBAL, HwProfile = 0
            };
            if (!SetupDiSetClassInstallParamsW(set, ref d, ref p, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()) ||
                !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref d))
                throw new Win32Exception();
            return 0;
        });
    }

    private static class Wts
    {
        public const int WTSUserName = 5;
        public const int WTSDomainName = 7;

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
        private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        public static string Query(uint sessionId, int infoClass)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buf, out _)) return "";
            try { return Marshal.PtrToStringUni(buf) ?? ""; }
            finally { WTSFreeMemory(buf); }
        }
    }
}
