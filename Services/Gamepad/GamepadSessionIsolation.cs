using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenStreamMS.Core.Api;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace OpenStreamMS.Services.Gamepad;

/// <summary>
/// Aísla los mandos por sesión de Windows con <see cref="HidHide"/>:
/// <list type="bullet">
/// <item>cada mando virtual de ViGEmBus solo es visible en la sesión cuyo Sunshine lo creó;</item>
/// <item>los mandos físicos solo son visibles en la sesión de consola (el PC del host).</item>
/// </list>
/// <para>
/// Los mandos son dispositivos de toda la máquina: el Steam de cada sesión (host
/// incluido) los ve y se los queda. Aquí cada nodo de mando (clases HID, XUSB y Xbox
/// One, las que filtra HidHide) se añade a su lista negra con una sesión
/// (<c>HID\...!N</c>): HidHide deniega abrirlo a los procesos de cualquier otra sesión.
/// </para>
/// <para>
/// HidHide decide al abrir el dispositivo, así que la entrada tiene que estar antes de
/// que otro Steam lo abra: se escucha la enumeración de dispositivos
/// (CM_Register_Notification) y se oculta cada nodo en cuanto aparece. Si llega tarde
/// (ya arrancado), se reinicia ese nodo HID para cerrar los handles ya abiertos.
/// </para>
/// <para>
/// Qué Sunshine creó cada mando se deduce del log: Sunshine escribe
/// "Gamepad N will be ..." justo antes de enchufarlo en ViGEmBus.
/// </para>
/// </summary>
public sealed partial class GamepadSessionIsolation : BackgroundService
{
    private const string ViGEmBusHardwareId = @"Nefarius\ViGEmBus\Gen1";
    private static readonly TimeSpan Fallback      = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BusRescan     = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MatchBefore   = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MatchAfter    = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AssignTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HidHideRetry  = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PhysicalRescan = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\]: \w+: Gamepad \d+ will be", RegexOptions.Multiline)]
    private static partial Regex GamepadCreatedRegex();

    private readonly StreamSessionService _sessions;

    private sealed class Pad
    {
        public required string InstanceId { get; init; }
        public DateTime FirstSeen { get; init; }
        public Guid? Session;
        public string SessionName = "";
        public uint WindowsSession;
        public bool GaveUp;
        public readonly HashSet<string> Hidden    = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Restarted = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LogCursor
    {
        public long Offset;
        public byte[] Head = [];
        public readonly List<DateTime> Pending = [];   // creaciones aún sin mando asignado
    }

    private readonly Dictionary<string, Pad>     _pads    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, LogCursor> _cursors = [];
    private readonly Channel<(string Id, bool Enumerated)> _arrivals = Channel.CreateUnbounded<(string, bool)>(new() { SingleReader = true });
    private List<string> _buses = [];
    private DateTime _lastBusScan = DateTime.MinValue;
    private SafeFileHandle? _hidHide;
    private DateTime _lastHidHideTry = DateTime.MinValue;
    private bool _hidHideWarned;

    // Mandos físicos ocultados → sesión de consola con la que se ocultaron
    private readonly Dictionary<string, uint> _physical = new(StringComparer.OrdinalIgnoreCase);
    private uint _console = NoSession;
    private DateTime _lastPhysicalScan = DateTime.MinValue;
    private const uint NoSession = 0xFFFFFFFF;

    public GamepadSessionIsolation(StreamSessionService sessions) => _sessions = sessions;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DisableControllerToVKMapping();

        using var notification = Cm.WatchEnumeration((id, enumerated) => _arrivals.Writer.TryWrite((id, enumerated)));
        if (notification is null)
            Logger.Warning("[Gamepad] No se pudo escuchar la llegada de dispositivos; solo se revisarán cada segundo.");

        // Todo el trabajo en este bucle (un solo hilo): llegadas al instante y una
        // revisión periódica para lo que se escape (mandos previos, asignaciones pendientes).
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(Fallback);
                try
                {
                    var dev = await _arrivals.Reader.ReadAsync(wait.Token);
                    do { Handle(dev.Id, dev.Enumerated); } while (_arrivals.Reader.TryRead(out dev));
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    Scan();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warning($"[Gamepad] Error vigilando mandos: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { _hidHide?.Dispose(); }
    }

    /// <summary>
    /// Abre HidHide si aún no lo está (se reintenta cada <see cref="HidHideRetry"/>: puede
    /// instalarse con el servicio ya en marcha), lo activa y limpia entradas antiguas.
    /// </summary>
    private bool EnsureHidHide()
    {
        if (_hidHide is not null) return true;
        if (DateTime.UtcNow - _lastHidHideTry < HidHideRetry) return false;
        _lastHidHideTry = DateTime.UtcNow;

        var h = HidHide.Open();
        if (h is null)
        {
            if (!_hidHideWarned)
            {
                _hidHideWarned = true;
                Logger.Warning("[Gamepad] HidHide no está instalado: los mandos serán visibles en todas las sesiones.");
            }
            return false;
        }
        try
        {
            if (!HidHide.GetActive(h))
            {
                HidHide.SetActive(h, true);
                Logger.Log("[Gamepad] HidHide activado.");
            }
        }
        catch (Win32Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo configurar HidHide: {ex.Message}");
            h.Dispose();
            return false;
        }
        _hidHide = h;
        Logger.Log("[Gamepad] HidHide disponible: mandos aislados por sesión.");
        RemoveStaleJails();
        return true;
    }

    /// <summary>
    /// ControllerToVKMapping (mando → teclas de navegación de Windows) lee los mandos
    /// de todas las sesiones y rompe el aislamiento. Se desactiva.
    /// </summary>
    private static void DisableControllerToVKMapping()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\ControllerToVKMapping");
            if (key.GetValue("Enabled") is int v && v == 0) return;
            key.SetValue("Enabled", 0, RegistryValueKind.DWord);
            Logger.Log("[Gamepad] ControllerToVKMapping desactivado (ignora el aislamiento de mandos por sesión).");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo desactivar ControllerToVKMapping: {ex.Message}");
        }
    }

    private List<string> Buses()
    {
        if (DateTime.UtcNow - _lastBusScan > BusRescan || _buses.Count == 0)
        {
            _buses = FindViGEmBuses();
            _lastBusScan = DateTime.UtcNow;
        }
        return _buses;
    }

    /// <summary>
    /// Un nodo enumerado o arrancado: si es un mando ViGEm o cuelga de uno, se oculta a
    /// las demás sesiones; si es un mando físico, a todas menos la de consola. Una
    /// enumeración es un dispositivo nuevo aunque repita instance id (Sunshine reenchufa
    /// mandos): se olvida lo que se sabía de él.
    /// </summary>
    private void Handle(string instanceId, bool enumerated)
    {
        if (!EnsureHidHide()) return;
        if (enumerated)
        {
            _pads.Remove(instanceId);
            foreach (var p in _pads.Values) p.Hidden.Remove(instanceId);
            _physical.Remove(instanceId);
        }

        if (ViGEmPadOf(instanceId) is { } padId)
        {
            var pad = Track(padId, _sessions.GetRunningSunshineSessions());
            if (pad.Session is not null) Hide(pad);
        }
        else if (Cm.IsGameController(instanceId))
        {
            HidePhysical([instanceId]);
        }
    }

    /// <summary>El mando ViGEm (hijo directo de un bus) del que cuelga el nodo, o null.</summary>
    private string? ViGEmPadOf(string instanceId)
    {
        string child = instanceId;
        for (string? parent = Cm.Parent(child); parent is not null; child = parent, parent = Cm.Parent(parent))
            if (Buses().Contains(parent, StringComparer.OrdinalIgnoreCase)) return child;
        return null;
    }

    private void Scan()
    {
        if (!EnsureHidHide()) return;
        ScanPhysical();

        var running = _sessions.GetRunningSunshineSessions();
        if (running.Count == 0 && _pads.Count == 0) return;

        // Mandos presentes = hijos directos de cada bus ViGEm
        var present = Buses().SelectMany(b => Cm.Children(b)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _pads.Values.Where(p => !present.Contains(p.InstanceId)).ToList())
        {
            _pads.Remove(gone.InstanceId);
            Unhide(gone);
        }
        foreach (var id in _cursors.Keys.Where(id => running.All(s => s.Id != id)).ToList())
            _cursors.Remove(id);

        foreach (var id in present)
        {
            var pad = Track(id, running);
            if (pad.Session is not null) Hide(pad);
        }
    }

    private Pad Track(string padId,
        IReadOnlyList<(Guid Id, string Name, uint RdpSessionId, string SunshineLog)> running)
    {
        var now = DateTime.Now;
        if (!_pads.TryGetValue(padId, out var pad))
            _pads[padId] = pad = new Pad { InstanceId = padId, FirstSeen = now };

        if (pad.Session is null && !pad.GaveUp)
        {
            foreach (var s in running) ReadGamepadEvents(s.Id, s.SunshineLog);
            TryAssign(pad, running, now);
        }
        return pad;
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
        else if (running.Count == 1)
            owner = running[0].Id;                    // sin línea en el log, pero solo hay una sesión
        else if (now - pad.FirstSeen < AssignTimeout)
            return;                                   // aún puede llegar la línea del log
        else
        {
            pad.GaveUp = true;
            Logger.Warning($"[Gamepad] No se pudo saber qué sesión creó el mando {pad.InstanceId}; queda visible en todas.");
            return;
        }

        var session = running.FirstOrDefault(s => s.Id == owner);
        if (session.Id == Guid.Empty) return;         // la sesión del log ya no está en marcha
        pad.Session = owner;
        pad.SessionName = session.Name;
        pad.WindowsSession = session.RdpSessionId;
        Logger.Log($"[Gamepad] Mando {pad.InstanceId} → sesión '{session.Name}' (sesión de Windows {session.RdpSessionId})", owner);
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
        catch (IOException) { /* Sunshine escribiendo; se reintenta en la siguiente pasada */ }
    }

    private static byte[] ReadHead(FileStream fs, int count)
    {
        var buf = new byte[count];
        fs.Seek(0, SeekOrigin.Begin);
        int n = fs.ReadAtLeast(buf, count, throwOnEndOfStream: false);
        return n == count ? buf : buf[..n];
    }

    // ── HidHide ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Oculta a las demás sesiones los nodos del mando que filtra HidHide (el propio mando
    /// si es XUSB y sus hijos HID). Un nodo que ya estaba arrancado pudo abrirse desde otra
    /// sesión (HidHide no revoca handles), así que se reinicia una vez para cerrarlos.
    /// </summary>
    private void Hide(Pad pad)
    {
        var nodes = Cm.Descendants(pad.InstanceId).Prepend(pad.InstanceId)
                      .Where(n => Cm.IsHidHideClass(n) && !pad.Hidden.Contains(n))
                      .ToList();
        if (nodes.Count == 0) return;

        var started = nodes.Where(Cm.IsOpenableAndStarted).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!UpdateJails(add: nodes.Select(n => HidHide.JailEntry(n, pad.WindowsSession)), remove: nodes)) return;

        foreach (var node in nodes)
        {
            pad.Hidden.Add(node);
            if (started.Contains(node) && pad.Restarted.Add(node))
            {
                try
                {
                    DevSetup.Restart(node);
                    Logger.Log($"[Gamepad] {node} visible solo en la sesión '{pad.SessionName}' ({pad.WindowsSession}); reiniciado para cerrar handles de otras sesiones.", pad.Session);
                }
                catch (Win32Exception ex)
                {
                    Logger.Warning($"[Gamepad] {node} ocultado tarde y no se pudo reiniciar: {ex.Message}. " +
                                   "Un proceso de otra sesión que ya lo tuviera abierto lo sigue viendo.");
                }
            }
            else
            {
                Logger.Log($"[Gamepad] {node} visible solo en la sesión '{pad.SessionName}' ({pad.WindowsSession}).", pad.Session);
            }
        }
    }

    /// <summary>
    /// Revisa los mandos físicos presentes: al cambiar la sesión de consola (otro usuario
    /// en el PC, cambio rápido de usuario) se vuelven a ocultar con la nueva sesión.
    /// </summary>
    private void ScanPhysical()
    {
        uint console = WTSGetActiveConsoleSessionId();
        if (console == NoSession) return;   // transición de sesión: se reintenta luego
        if (console == _console && DateTime.UtcNow - _lastPhysicalScan < PhysicalRescan) return;
        _lastPhysicalScan = DateTime.UtcNow;

        if (console != _console)
        {
            if (_console != NoSession)
                Logger.Log($"[Gamepad] La sesión de consola pasa de {_console} a {console}: se reasignan los mandos físicos.");
            _console = console;
        }
        foreach (var gone in _physical.Keys.Where(id => !Cm.Exists(id)).ToList())
            _physical.Remove(gone);

        HidePhysical(Cm.GameControllers().Where(id => ViGEmPadOf(id) is null).ToList());
    }

    /// <summary>
    /// Deja los mandos físicos visibles solo en la sesión de consola. Si alguno estaba
    /// arrancado con sesiones de stream en marcha, se reinicia una vez para cerrar los
    /// handles que esas sesiones pudieran tener.
    /// </summary>
    private void HidePhysical(IReadOnlyList<string> ids)
    {
        if (_console == NoSession) _console = WTSGetActiveConsoleSessionId();
        if (_console == NoSession) return;

        var nodes = ids.Where(id => !_physical.TryGetValue(id, out var s) || s != _console).ToList();
        if (nodes.Count == 0) return;

        bool streaming = _sessions.GetRunningSunshineSessions().Count > 0;
        var started = nodes.Where(Cm.IsOpenableAndStarted).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!UpdateJails(add: nodes.Select(n => HidHide.JailEntry(n, _console)), remove: nodes)) return;

        foreach (var node in nodes)
        {
            bool first = !_physical.ContainsKey(node);
            _physical[node] = _console;
            Logger.Log($"[Gamepad] Mando físico {node} visible solo en la sesión de consola ({_console}).");
            if (!(first && streaming && started.Contains(node))) continue;
            try { DevSetup.Restart(node); }
            catch (Win32Exception ex)
            {
                Logger.Warning($"[Gamepad] No se pudo reiniciar {node}: {ex.Message}. " +
                               "Una sesión de stream que ya lo tuviera abierto lo sigue viendo.");
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    /// <summary>
    /// Quita de HidHide todas las entradas "jail" (las de sesión que pone este servicio) y
    /// deja las del usuario. Para la desinstalación, con el servicio ya parado.
    /// </summary>
    public static void ClearJails()
    {
        using var h = HidHide.Open();
        if (h is null) return;
        try
        {
            var list = HidHide.GetBlacklist(h);
            var kept = list.Where(e => HidHide.JailedInstance(e) is null).ToList();
            if (kept.Count != list.Count) HidHide.SetBlacklist(h, kept);
        }
        catch (Win32Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudieron quitar las entradas de sesión de HidHide: {ex.Message}");
        }
    }

    /// <summary>Quita de la lista negra los nodos de un mando que ya no existe.</summary>
    private void Unhide(Pad pad)
    {
        if (pad.Hidden.Count > 0 && UpdateJails(add: [], remove: pad.Hidden))
            Logger.Log($"[Gamepad] Mando {pad.InstanceId} retirado; sus nodos HID salen de la lista negra de HidHide.", pad.Session);
    }

    /// <summary>
    /// Al arrancar: quita las entradas "jail" de dispositivos que ya no existen (de una
    /// ejecución anterior que no pudo limpiar). Las entradas sin sesión son del usuario.
    /// </summary>
    private void RemoveStaleJails()
    {
        var stale = HidHide.GetBlacklist(_hidHide!)
                           .Select(HidHide.JailedInstance)
                           .OfType<string>()
                           .Where(id => !Cm.Exists(id))
                           .ToList();
        if (stale.Count > 0 && UpdateJails(add: [], remove: stale))
            Logger.Log($"[Gamepad] Quitadas {stale.Count} entradas antiguas de la lista negra de HidHide.");
    }

    /// <summary>
    /// Lectura-modificación-escritura de la lista negra: quita las entradas jail de
    /// <paramref name="remove"/> y añade <paramref name="add"/>. El resto (las del usuario) se respeta.
    /// </summary>
    private bool UpdateJails(IEnumerable<string> add, IEnumerable<string> remove)
    {
        try
        {
            var drop = remove.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = HidHide.GetBlacklist(_hidHide!)
                              .Where(e => HidHide.JailedInstance(e) is not { } id || !drop.Contains(id))
                              .Concat(add)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();
            HidHide.SetBlacklist(_hidHide!, list);
            return true;
        }
        catch (Win32Exception ex)
        {
            Logger.Warning($"[Gamepad] No se pudo actualizar la lista negra de HidHide: {ex.Message}");
            return false;
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
        private const uint DN_STARTED = 0x8;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out int parent, int devInst, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Child(out int child, int devInst, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Sibling(out int sibling, int devInst, int flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, int devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(int devInst, char[] buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_SizeW(out int len, string? filter, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(int devInst, int property, out int type, byte[]? buffer, ref int len, int flags);

        // ── Notificación de enumeración ──────────────────────────────────────
        private const int CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES = 0x2;
        private const int CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE = 2;
        private const int CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED = 4;
        private const int CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED = 5;
        private const int FilterSize = 416;   // sizeof(CM_NOTIFY_FILTER) en x64

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int NotifyCallback(IntPtr hNotify, IntPtr context, int action, IntPtr eventData, int eventDataSize);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Register_Notification(IntPtr filter, IntPtr context, NotifyCallback callback, out IntPtr handle);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Unregister_Notification(IntPtr handle);

        private sealed class Registration(IntPtr handle, NotifyCallback callback) : IDisposable
        {
            private NotifyCallback? _keepAlive = callback;   // el delegado no puede recolectarse mientras esté registrado
            public void Dispose()
            {
                if (_keepAlive is null) return;
                CM_Unregister_Notification(handle);
                _keepAlive = null;
            }
        }

        /// <summary>
        /// Avisa con el instance id de cada dispositivo que se enumera (true) o arranca. El aviso
        /// llega en un hilo del sistema: <paramref name="onDevice"/> debe ser rápido.
        /// </summary>
        public static IDisposable? WatchEnumeration(Action<string, bool> onDevice)
        {
            NotifyCallback callback = (_, _, action, data, _) =>
            {
                if ((action == CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED || action == CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED)
                    && data != IntPtr.Zero && Marshal.PtrToStringUni(data + 8) is { Length: > 0 } id)
                    onDevice(id, action == CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED);
                return 0;
            };

            IntPtr filter = Marshal.AllocHGlobal(FilterSize);
            try
            {
                for (int i = 0; i < FilterSize; i++) Marshal.WriteByte(filter, i, 0);
                Marshal.WriteInt32(filter, 0, FilterSize);
                Marshal.WriteInt32(filter, 4, CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES);
                Marshal.WriteInt32(filter, 8, CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE);
                return CM_Register_Notification(filter, IntPtr.Zero, callback, out var handle) == CR_SUCCESS
                    ? new Registration(handle, callback)
                    : null;
            }
            finally { Marshal.FreeHGlobal(filter); }
        }

        public static IEnumerable<string> DeviceIds(string filter,
            int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT)
        {
            if (CM_Get_Device_ID_List_SizeW(out int len, filter, flags) != CR_SUCCESS || len <= 1) return [];
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW(filter, buf, len, flags) != CR_SUCCESS) return [];
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        public static IEnumerable<string> HardwareIds(string deviceId) => RegistryStrings(deviceId, CM_DRP_HARDWAREID);

        private static string[] RegistryStrings(string deviceId, int property)
        {
            if (CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return [];
            int len = 0;
            CM_Get_DevNode_Registry_PropertyW(inst, property, out _, null, ref len, 0);
            if (len == 0) return [];
            var buf = new byte[len];
            if (CM_Get_DevNode_Registry_PropertyW(inst, property, out _, buf, ref len, 0) != CR_SUCCESS) return [];
            return Encoding.Unicode.GetString(buf, 0, len).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        public static string? Parent(string deviceId) =>
            CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS
            && CM_Get_Parent(out int parent, inst, 0) == CR_SUCCESS
                ? Id(parent)
                : null;

        /// <summary>
        /// Arrancado y sin hijos: es el nodo que abren las aplicaciones (la colección HID, o
        /// el XUSB si no tiene hijo HID). Reiniciarlo cierra sus handles sin tocar el resto.
        /// </summary>
        public static bool IsOpenableAndStarted(string deviceId) => IsStarted(deviceId) && Children(deviceId).Count == 0;

        public static bool IsStarted(string deviceId) =>
            CM_Locate_DevNodeW(out int inst, deviceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS
            && CM_Get_DevNode_Status(out uint status, out _, inst, 0) == CR_SUCCESS
            && (status & DN_STARTED) != 0;

        private const int CM_DRP_COMPATIBLEIDS = 0x3;
        private const int CM_DRP_CLASSGUID = 0x9;
        private const int CM_GETIDLIST_FILTER_CLASS = 0x200;

        // Clases que filtra HidHide (su instalador lo registra como UpperFilter de ellas)
        private const string HidClass           = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";
        private const string XnaCompositeClass  = "{d61ca365-5af4-4486-998b-9db4734c6ca3}";   // Xbox 360 / XUSB
        private const string XboxCompositeClass = "{05f5cfe2-4733-4950-a6bb-07aad01a3a84}";   // Xbox One / GIP

        private static string? ClassGuid(string deviceId) =>
            RegistryStrings(deviceId, CM_DRP_CLASSGUID).FirstOrDefault();

        public static bool IsHidHideClass(string deviceId) =>
            ClassGuid(deviceId) is { } c &&
            (c.Equals(HidClass, StringComparison.OrdinalIgnoreCase) ||
             c.Equals(XnaCompositeClass, StringComparison.OrdinalIgnoreCase) ||
             c.Equals(XboxCompositeClass, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Mando o joystick: un nodo XUSB/Xbox One, o una colección HID de juego
        /// (id compatible HID_DEVICE_SYSTEM_GAME). Teclados y ratones no lo son.
        /// </summary>
        public static bool IsGameController(string deviceId) => ClassGuid(deviceId) switch
        {
            { } c when c.Equals(XnaCompositeClass, StringComparison.OrdinalIgnoreCase)
                    || c.Equals(XboxCompositeClass, StringComparison.OrdinalIgnoreCase) => true,
            { } c when c.Equals(HidClass, StringComparison.OrdinalIgnoreCase) =>
                RegistryStrings(deviceId, CM_DRP_COMPATIBLEIDS)
                    .Any(id => id.Equals("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };

        /// <summary>Mandos presentes (de cualquier origen) en las clases que filtra HidHide.</summary>
        public static IEnumerable<string> GameControllers() =>
            new[] { HidClass, XnaCompositeClass, XboxCompositeClass }
                .SelectMany(c => DeviceIds(c, CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT))
                .Where(IsGameController);

        public static bool Exists(string deviceId) =>
            CM_Locate_DevNodeW(out _, deviceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;

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

    private static class DevSetup
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public int HeaderSize; public int InstallFunction;   // SP_CLASSINSTALL_HEADER
            public int StateChange; public int Scope; public int HwProfile;
        }

        private const int DIF_PROPERTYCHANGE = 0x12;
        private const int DICS_PROPCHANGE = 3;
        private const int DICS_FLAG_GLOBAL = 1;

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS p, int size);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        /// <summary>Reinicia el dispositivo (DIF_PROPERTYCHANGE), como "deshabilitar y habilitar".</summary>
        public static void Restart(string instanceId)
        {
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            try
            {
                var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref d))
                    throw new Win32Exception();
                var p = new SP_PROPCHANGE_PARAMS
                {
                    HeaderSize = 8, InstallFunction = DIF_PROPERTYCHANGE,
                    StateChange = DICS_PROPCHANGE, Scope = DICS_FLAG_GLOBAL, HwProfile = 0
                };
                if (!SetupDiSetClassInstallParamsW(set, ref d, ref p, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()) ||
                    !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref d))
                    throw new Win32Exception();
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}
