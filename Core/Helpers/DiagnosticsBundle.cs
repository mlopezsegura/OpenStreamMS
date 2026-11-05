using Microsoft.Win32;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Services;
using OpenStreamMS.Services.Drivers;
using OpenStreamMS.Services.Session;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenStreamMS.Core.Helpers;

/// <summary>
/// Paquete de diagnóstico (.zip) para depurar un equipo ajeno: datos del equipo y de Windows
/// (versión y compilación), estado de OpenStreamMS (RDP Wrapper, RemoteIdd, drivers, sesiones,
/// puestos), sus logs y los de cada Sunshine (también el de la ejecución anterior, que Sunshine
/// rota a <c>sunshine.log.1</c> al arrancar) y los errores del registro de eventos de Windows.
/// <para>
/// Se quitan datos personales antes de escribir: nombre del equipo, cuentas de usuario
/// (salvo las osms- que crea OpenStreamMS), correos y credenciales del panel de Sunshine. No
/// entran las credenciales ni el estado de emparejamiento de Sunshine (<c>credentials\</c>,
/// <c>sunshine_state.json</c>). Aun así, quien lo envía debe revisarlo: el README lo dice.
/// </para>
/// Cada apartado va en su try: si uno falla, se apunta en <c>errors.txt</c> y se sigue.
/// </summary>
internal static partial class DiagnosticsBundle
{
    /// <summary>Lo que solo tiene el servicio en marcha. Desde la línea de comandos va vacío.</summary>
    public sealed record Sources(
        Func<IEnumerable<SessionResponse>>? Sessions = null,
        Func<object>? Seats = null,
        RdpWrapperManager? RdpWrapper = null,
        IReadOnlyList<NefariusDriverManager>? Drivers = null,
        string? LogLevel = null,
        IReadOnlyList<string>? ExtraUsers = null);

    private const long MaxFileBytes      = 8 * 1024 * 1024;   // de cada log, el final
    private const int  MaxEventsChannel  = 1500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Escribe el .zip en <paramref name="output"/> (lo deja abierto).</summary>
    public static void Write(Stream output, Sources src, int days)
    {
        var errors  = new StringBuilder();
        var summary = new StringBuilder();
        var redact  = BuildRedactor(src);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        void Section(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { errors.AppendLine($"[{name}] {ex.GetType().Name}: {ex.Message}"); }
        }

        AddText(zip, "00-README.txt", Readme(days), redact);

        Section("system",   () => AddText(zip, "system.txt", SystemInfo(summary), redact));
        Section("rdp",      () => AddText(zip, "rdp-registry.txt", RdpRegistry(), redact));
        Section("services", () => AddText(zip, "services.txt", ServicesInfo(), redact));
        Section("wts",      () => AddText(zip, "windows-sessions.txt", WindowsSessions(), redact));
        Section("procs",    () => AddText(zip, "processes.txt", Processes(), redact));
        Section("ports",    () => AddText(zip, "listening-ports.txt", ListeningPorts(), redact));
        Section("pnp",      () => AddText(zip, "pnputil-problem-devices.txt", Run("pnputil.exe", "/enum-devices /problem"), redact));
        Section("drivers",  () => AddText(zip, "pnputil-drivers.txt", Run("pnputil.exe", "/enum-drivers"), redact));
        Section("openstreamms", () => AddText(zip, "openstreamms.json", OpenStreamState(src, summary), redact));

        Section("logs", () => AddLogs(zip, src, redact, errors));
        Section("events", () => AddEvents(zip, days, redact, errors));

        AddText(zip, "00-summary.txt", summary.ToString(), redact);
        if (errors.Length > 0) AddText(zip, "errors.txt", errors.ToString(), redact);
    }

    // ── Equipo y Windows ─────────────────────────────────────────────────────

    private static string SystemInfo(StringBuilder summary)
    {
        var sb = new StringBuilder();
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string Nt(string v) => nt?.GetValue(v)?.ToString() ?? "?";

        // ProductName sigue diciendo "Windows 10" en Windows 11: se corrige por la compilación.
        var product = Nt("ProductName");
        if (int.TryParse(Nt("CurrentBuild"), out var build) && build >= 22000)
            product = product.Replace("Windows 10", "Windows 11");
        var windows = $"{product} ({Nt("EditionID")}) {Nt("DisplayVersion")}, compilación {Nt("CurrentBuild")}.{Nt("UBR")}";
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";

        summary.AppendLine($"Generado:      {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        summary.AppendLine($"OpenStreamMS:  {version}");
        summary.AppendLine($"Windows:       {windows}");
        summary.AppendLine($"Arquitectura:  {RuntimeInformation.OSArchitecture}");

        sb.AppendLine($"OpenStreamMS:        {version}");
        sb.AppendLine($"Windows:             {windows}");
        sb.AppendLine($"EditionID:           {Nt("EditionID")}");
        sb.AppendLine($"DisplayVersion:      {Nt("DisplayVersion")}");
        sb.AppendLine($"Build:               {Nt("CurrentBuild")}.{Nt("UBR")}");
        sb.AppendLine($"BuildLabEx:          {Nt("BuildLabEx")}");
        sb.AppendLine($"InstallationType:    {Nt("InstallationType")}");
        sb.AppendLine($"Kernel:              {Environment.OSVersion.VersionString}");
        sb.AppendLine($"Arquitectura:        {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Idioma de Windows:   {CultureInfo.InstalledUICulture.Name}");
        sb.AppendLine($"Encendido desde:     {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm} (d.hh:mm)");
        sb.AppendLine($".NET:                {RuntimeInformation.FrameworkDescription}");

        using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            sb.AppendLine($"CPU:                 {cpu?.GetValue("ProcessorNameString")?.ToString()?.Trim()} ({Environment.ProcessorCount} hilos)");
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
            sb.AppendLine($"RAM:                 {mem.ullTotalPhys / (1024 * 1024)} MB ({mem.ullAvailPhys / (1024 * 1024)} MB libres)");

        sb.AppendLine();
        sb.AppendLine("Adaptadores de pantalla (driver; RDP y los virtuales salen uno por sesión o pantalla):");
        var adapters = new List<string>();
        using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
        {
            foreach (var name in cls?.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)) ?? [])
            {
                try
                {
                    using var k = cls!.OpenSubKey(name);
                    if (k is null) continue;
                    adapters.Add($"{k.GetValue("DriverDesc")} | versión {k.GetValue("DriverVersion")} | fecha {k.GetValue("DriverDate")} | {k.GetValue("MatchingDeviceId")}");
                }
                catch { /* subclave sin permiso */ }
            }
        }
        foreach (var g in adapters.GroupBy(a => a))
        {
            sb.AppendLine($"  - {g.Key}{(g.Count() > 1 ? $"  (×{g.Count()})" : "")}");
            var parts = g.Key.Split(" | ");
            summary.AppendLine($"GPU:           {parts[0]} ({parts[1].Replace("versión ", "")}){(g.Count() > 1 ? $" ×{g.Count()}" : "")}");
        }
        return sb.ToString();
    }

    /// <summary>Valores de registro que deciden si RDP, la multisesión y RemoteIdd funcionan.</summary>
    private static string RdpRegistry()
    {
        (string Key, string[] Values)[] items =
        [
            (@"SYSTEM\CurrentControlSet\Control\Terminal Server", ["fDenyTSConnections", "fSingleSessionPerUser", "AllowRemoteRPC"]),
            (@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations", ["DWMFRAMEINTERVAL"]),
            (@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp",
                ["PortNumber", "fEnableWinStation", "MaxInstanceCount", "LoadableProtocol_Object", "OsmsInnerProtocol_Object",
                 "OsmsRefreshHz", "AudioEnumeratorDll", "SecurityLayer", "UserAuthentication"]),
            (@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services",
                ["fDenyTSConnections", "fSingleSessionPerUser", "MaxInstanceCount", "fDisableAudioCapture",
                 "fEnableVirtualizedGraphics", "AVC444ModePreferred", "bEnumerateHWBeforeSW", "fDisableCam", "fDisablePNPRedir"]),
            (@"SYSTEM\CurrentControlSet\Services\TermService\Parameters", ["ServiceDll"]),
            (@"SYSTEM\CurrentControlSet\Services\UmRdpService\Parameters", ["ServiceDll"]),
            (@"SYSTEM\CurrentControlSet\Control", ["SystemStartOptions"]),
            (@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", ["UEFISecureBootEnabled"]),
            (@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", ["Enabled"]),
            (@"SYSTEM\CurrentControlSet\Control\CI\Policy", ["VerifiedAndReputablePolicyState"]),
            (@"SOFTWARE\OpenStreamMS", ["LogLevel"]),
        ];
        var sb = new StringBuilder("HKLM\n");
        foreach (var (path, values) in items)
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            sb.AppendLine($"[{path}]{(key is null ? "  (no existe)" : "")}");
            if (key is null) continue;
            foreach (var v in values)
            {
                var val = key.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (val is null) continue;
                sb.AppendLine($"  {v} = {(val is byte[] b ? Convert.ToHexString(b) : val is string[] m ? string.Join(" | ", m) : val)}");
            }
        }
        return sb.ToString();
    }

    private static string ServicesInfo()
    {
        string[] names =
        [
            "OpenStreamMS", "TermService", "UmRdpService", "SessionEnv", "Audiosrv", "AudioEndpointBuilder",
            "ViGEmBus", "HidHide", "GameInputSvc", "XboxGipSvc", "XblAuthManager", "XboxNetApiSvc", "BthAvctpSvc",
        ];
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            try
            {
                using var sc = new ServiceController(name);
                sb.AppendLine($"{name,-22} {sc.Status,-14} inicio: {sc.StartType}");
            }
            catch (InvalidOperationException) { sb.AppendLine($"{name,-22} (no instalado)"); }
        }
        return sb.ToString();
    }

    private static string WindowsSessions()
    {
        var sb = new StringBuilder("Id  Estado         Estación            Usuario\n");
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buf, out var count)) return $"WTSEnumerateSessions falló: {Marshal.GetLastWin32Error()}";
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFOW>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFOW>(buf + i * size);
                var station = Marshal.PtrToStringUni(info.pWinStationName) ?? "";
                var user = "";
                if (WTSQuerySessionInformationW(IntPtr.Zero, info.SessionId, 5 /* WTSUserName */, out var ub, out _))
                {
                    user = Marshal.PtrToStringUni(ub) ?? "";
                    WTSFreeMemory(ub);
                }
                string[] states = ["Active", "Connected", "ConnectQuery", "Shadow", "Disconnected", "Idle", "Listen", "Reset", "Down", "Init"];
                var state = info.State >= 0 && info.State < states.Length ? states[info.State] : info.State.ToString();
                sb.AppendLine($"{info.SessionId,-3} {state,-14} {station,-19} {user}");
            }
        }
        finally { WTSFreeMemory(buf); }
        return sb.ToString();
    }

    /// <summary>Procesos que importan para depurar, con la sesión de Windows en la que corren.</summary>
    private static string Processes()
    {
        string[] wanted =
        [
            "OpenStreamMS", "sunshine", "wfreerdp", "steam", "osms-steam", "steamwebhelper", "explorer",
            "WUDFHost", "dwm", "GameInputSvc", "gamingservices", "XboxPcApp", "GameBar", "ViGEmBus", "HidHide",
            "SystemSettings", "rdpclip",
        ];
        var sb = new StringBuilder("Sesión  Nombre (instancias)\n");
        foreach (var g in Process.GetProcesses()
                     .Where(p => wanted.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                     .GroupBy(p => (p.SessionId, p.ProcessName))
                     .OrderBy(g => g.Key.SessionId).ThenBy(g => g.Key.ProcessName))
            sb.AppendLine($"{g.Key.SessionId,-7} {g.Key.ProcessName} ({g.Count()})");
        return sb.ToString();
    }

    private static string ListeningPorts()
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        var sb = new StringBuilder("TCP en escucha:\n");
        foreach (var ep in props.GetActiveTcpListeners().OrderBy(e => e.Port).ThenBy(e => e.Address.ToString()))
            sb.AppendLine($"  {ep.Address}:{ep.Port}");
        sb.AppendLine("UDP en escucha:");
        foreach (var ep in props.GetActiveUdpListeners().OrderBy(e => e.Port).ThenBy(e => e.Address.ToString()))
            sb.AppendLine($"  {ep.Address}:{ep.Port}");
        return sb.ToString();
    }

    // ── OpenStreamMS ─────────────────────────────────────────────────────────

    private static string OpenStreamState(Sources src, StringBuilder summary)
    {
        var root = new JsonObject();
        var level = src.LogLevel ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenStreamMS")?.GetValue("LogLevel")?.ToString() ?? "?";
        root["logLevel"] = level;
        summary.AppendLine($"Nivel de log:  {level}{(level is "Error" or "Off" ? "  (poco detalle: pon Info, reproduce el fallo y genera otro paquete)" : "")}");

        var rdp = src.RdpWrapper ?? new RdpWrapperManager();
        var rdpStatus = rdp.GetStatus();
        root["rdpWrapper"] = JsonSerializer.SerializeToNode(rdpStatus, Json);
        root["remoteIddActive"] = RdpWrapperManager.IsRemoteIddActive();
        root["remoteIddPendingRestart"] = RdpWrapperManager.IsRemoteIddPendingRestart();
        root["remoteIddUsable"] = RdpWrapperManager.IsRemoteIddUsable();
        summary.AppendLine($"Multi-RDP:     {rdpStatus.Wrapper ?? "ninguno"} (cargado: {rdpStatus.Loaded?.ToString() ?? "?"}, termsrv {rdpStatus.TermsrvVersion})");
        summary.AppendLine($"RemoteIdd:     {(RdpWrapperManager.IsRemoteIddUsable() ? "en uso" : RdpWrapperManager.IsRemoteIddActive() ? "instalado, pendiente de reinicio" : "no")}");

        var drivers = new JsonObject();
        foreach (var d in src.Drivers ?? [new ViGEmBusManager(), new HidHideManager()])
        {
            var st = d.GetStatus();
            drivers[d.Name] = JsonSerializer.SerializeToNode(st, Json);
            summary.AppendLine($"{d.Name + ":",-15}{(st.Installed ? $"{st.InstalledVersion} ({st.ServiceState})" : "no instalado")}");
        }
        root["drivers"] = drivers;

        if (src.Sessions is not null)
        {
            var list = src.Sessions().ToList();
            var arr = JsonSerializer.SerializeToNode(list, Json)!.AsArray();
            foreach (var s in arr.OfType<JsonObject>())
                foreach (var prop in new[] { "username", "domain", "sunshineAuthUser", "sunshineAuthPass" })
                    if (s[prop] is JsonValue v && v.ToString().Length > 0) s[prop] = "<redacted>";
            root["sessions"] = arr;
            summary.AppendLine($"Sesiones:      {list.Count}");
            foreach (var s in list)
                summary.AppendLine($"  - {s.Id.ToString()[..8]} {s.State}: {(s.Local ? "local" : s.Isolated ? "usuario aislado" : "cuenta propia")}" +
                                   $"{(s.UseStreamProfile ? ", perfil de stream" : "")}, captura {s.Capture ?? "auto"}, encoder {s.Encoder ?? "auto"}");
        }
        else root["sessions"] = "(no disponible: generado desde la línea de comandos)";

        if (src.Seats is not null) root["seats"] = JsonSerializer.SerializeToNode(src.Seats(), Json);
        return root.ToJsonString(Json);
    }

    private static void AddLogs(ZipArchive zip, Sources src, Redactor redact, StringBuilder errors)
    {
        var baseDir = AppContext.BaseDirectory;

        // Servicio, bandeja, puestos: openstream.log y uno por sesión (openstream-<id>.log), con su .old.
        foreach (var f in Directory.EnumerateFiles(baseDir, "openstream*.log*"))
            TryAddFile(zip, $"logs/{Path.GetFileName(f)}", f, redact, errors);

        // Cada Sunshine: el log actual y el de la ejecución anterior (Sunshine lo rota al arrancar).
        var sessionsDir = Path.Combine(baseDir, "sessions");
        if (Directory.Exists(sessionsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(sessionsDir))
            {
                var config = Path.Combine(dir, "Sunshine", "config");
                if (!Directory.Exists(config)) continue;
                var id = Path.GetFileName(dir);
                foreach (var f in Directory.EnumerateFiles(config, "sunshine.log*"))
                    TryAddFile(zip, $"sunshine/{id}/{Path.GetFileName(f)}", f, redact, errors);
                var conf = Path.Combine(config, "sunshine.conf");
                if (File.Exists(conf))
                    AddText(zip, $"sunshine/{id}/sunshine.conf", MaskConf(ReadTail(conf)), redact);
            }
        }

        // RemoteIdd y su instalación (%ProgramData%\OpenStreamMS). No se entra en state\: guarda
        // los emparejamientos de Sunshine.
        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenStreamMS");
        if (Directory.Exists(programData))
            foreach (var f in Directory.EnumerateFiles(programData).Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                                                                           f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                TryAddFile(zip, $"programdata/{Path.GetFileName(f)}", f, redact, errors);

        // Registros en memoria de las instalaciones (RDP Wrapper, ViGEmBus, HidHide).
        if (src.RdpWrapper is { } rdp) AddText(zip, "logs/rdpwrapper-operations.log", string.Join('\n', rdp.GetLog()), redact);
        foreach (var d in src.Drivers ?? [])
            AddText(zip, $"logs/{d.Name.ToLowerInvariant()}-operations.log", string.Join('\n', d.GetLog()), redact);
    }

    /// <summary>En sunshine.conf, fuera cualquier valor que parezca un secreto.</summary>
    private static string MaskConf(string conf) =>
        SecretConfLine().Replace(conf, m => $"{m.Groups[1].Value}= <redacted>");

    // ── Eventos de Windows ───────────────────────────────────────────────────

    private static void AddEvents(ZipArchive zip, int days, Redactor redact, StringBuilder errors)
    {
        int[] errorsOnly = [1, 2];          // crítico, error
        int[] withWarnings = [1, 2, 3];     // + advertencia
        (string Channel, int[]? Levels, string File)[] channels =
        [
            ("System", withWarnings, "system.txt"),
            ("Application", errorsOnly, "application.txt"),
            // Escritorio remoto: todos los niveles (los 20523/258 de carga del proveedor son informativos).
            ("Microsoft-Windows-TerminalServices-LocalSessionManager/Operational", null, "ts-localsessionmanager.txt"),
            ("Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational", null, "ts-remoteconnectionmanager.txt"),
            ("Microsoft-Windows-RemoteDesktopServices-RdpCoreTS/Operational", null, "rdpcorets.txt"),
            // Firma y carga de drivers (RemoteIdd, ViGEmBus, HidHide).
            ("Microsoft-Windows-CodeIntegrity/Operational", withWarnings, "codeintegrity.txt"),
            ("Microsoft-Windows-DriverFrameworks-UserMode/Operational", withWarnings, "driverframeworks-usermode.txt"),
            ("Microsoft-Windows-Kernel-PnP/Configuration", withWarnings, "kernel-pnp.txt"),
        ];
        foreach (var (channel, levels, file) in channels)
        {
            try { AddText(zip, $"events/{file}", ReadEvents(channel, days, levels), redact); }
            catch (Exception ex) { errors.AppendLine($"[events:{channel}] {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private static string ReadEvents(string channel, int days, int[]? levels)
    {
        long ms = days * 86_400_000L;
        var level = levels is null ? "" : "(" + string.Join(" or ", levels.Select(l => $"Level={l}")) + ") and ";
        var query = new EventLogQuery(channel, PathType.LogName,
            $"*[System[{level}TimeCreated[timediff(@SystemTime) <= {ms}]]]") { ReverseDirection = true };

        var sb = new StringBuilder($"# {channel} — últimos {days} día(s), del más reciente al más antiguo\n");
        int n = 0;
        try
        {
            using var reader = new EventLogReader(query);
            for (EventRecord? rec; n < MaxEventsChannel && (rec = reader.ReadEvent()) is not null; n++)
            {
                using (rec)
                {
                    string msg;
                    try { msg = rec.FormatDescription() ?? ""; } catch { msg = ""; }
                    if (msg.Length == 0)
                        msg = string.Join(" | ", rec.Properties.Select(p => p.Value?.ToString()));
                    var lvl = rec.Level switch { 1 => "CRIT", 2 => "ERROR", 3 => "WARN", 4 => "INFO", 5 => "VERB", _ => "INFO" };
                    sb.AppendLine($"{rec.TimeCreated:yyyy-MM-dd HH:mm:ss.fff} [{lvl}] {rec.ProviderName} #{rec.Id}: {msg.ReplaceLineEndings(" ⏎ ")}");
                }
            }
        }
        catch (EventLogNotFoundException) { return sb.AppendLine("(el canal no existe en este equipo)").ToString(); }
        if (n == MaxEventsChannel) sb.AppendLine($"(… cortado en {MaxEventsChannel} eventos)");
        if (n == 0) sb.AppendLine("(ninguno)");
        return sb.ToString();
    }

    // ── Utilidades ───────────────────────────────────────────────────────────

    private static string Readme(int days) => $"""
        OpenStreamMS diagnostic bundle / paquete de diagnóstico
        =======================================================

        EN: This archive helps the OpenStreamMS developers find what went wrong. It contains:
          - 00-summary.txt: Windows version and build, OpenStreamMS version, drivers, sessions.
          - system.txt, services.txt, rdp-registry.txt, windows-sessions.txt, processes.txt,
            listening-ports.txt, pnputil-*.txt: PC hardware, Windows and driver information.
          - openstreamms.json: state of OpenStreamMS (RDP Wrapper, RemoteIdd, drivers, sessions, seats).
          - logs/, sunshine/, programdata/: OpenStreamMS logs and each Sunshine log, including
            the previous run (sunshine.log.1).
          - events/: Windows Event Log errors and warnings from the last {days} day(s).
        The PC name, user account names, e-mail addresses and the Sunshine web panel credentials
        are replaced with <pc>, <user1>/<user2>/…, <email> and <redacted>. Sunshine pairing data and
        credentials are NOT included. Please look through the files before sending them, and
        remove anything you do not want to share.
        Tip: set the log level to "Info" in Settings, reproduce the problem, then create the bundle.

        ES: Este archivo ayuda a los desarrolladores de OpenStreamMS a encontrar el fallo. Contiene
        la versión y compilación de Windows, datos del equipo y de los drivers, el estado de
        OpenStreamMS, sus logs y los de cada Sunshine (también de la ejecución anterior) y los
        errores y avisos del registro de eventos de Windows de los últimos {days} día(s).
        El nombre del equipo, las cuentas de usuario, los correos y las credenciales del panel de
        Sunshine se sustituyen por <pc>, <user1>/<user2>/…, <email> y <redacted>. No se incluyen los
        emparejamientos ni las credenciales de Sunshine. Revisa los archivos antes de enviarlos.
        """;

    private static void TryAddFile(ZipArchive zip, string name, string path, Redactor redact, StringBuilder errors)
    {
        try { AddText(zip, name, ReadTail(path), redact); }
        catch (Exception ex) { errors.AppendLine($"[{name}] {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Lee un fichero aunque otro proceso lo tenga abierto; si es enorme, solo el final.</summary>
    private static string ReadTail(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var prefix = "";
        if (fs.Length > MaxFileBytes)
        {
            fs.Seek(-MaxFileBytes, SeekOrigin.End);
            prefix = $"[… solo los últimos {MaxFileBytes / (1024 * 1024)} MB de {fs.Length / (1024 * 1024)} MB …]\n";
        }
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return prefix + reader.ReadToEnd();
    }

    private static void AddText(ZipArchive zip, string name, string text, Redactor redact)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(redact.Apply(text));
    }

    private static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"no se pudo lanzar {exe}");
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return $"({exe} {args}: no terminó en 30 s)"; }
        return $"> {exe} {args}  (código {p.ExitCode})\n{stdout.Result}{stderr.Result}";
    }

    // ── Datos personales ─────────────────────────────────────────────────────

    private static Redactor BuildRedactor(Sources src)
    {
        var r = new Redactor();
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] builtIn = ["Public", "Default", "Default User", "All Users", "defaultuser0", "Administrator", "Administrador",
                            "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE", "Guest", "Invitado", "WDAGUtilityAccount"];

        // Carpetas de C:\Users (también las de cuentas ya borradas) y perfiles registrados.
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            var usersDir = Environment.ExpandEnvironmentVariables(list?.GetValue("ProfilesDirectory") as string ?? @"%SystemDrive%\Users");
            foreach (var d in Directory.EnumerateDirectories(usersDir)) users.Add(Path.GetFileName(d));
            foreach (var sid in list?.GetSubKeyNames() ?? [])
            {
                using var k = list!.OpenSubKey(sid);
                if (k?.GetValue("ProfileImagePath") is string p) users.Add(Path.GetFileName(p));
            }
        }
        catch { }
        foreach (var u in src.ExtraUsers ?? []) if (!string.IsNullOrWhiteSpace(u)) users.Add(u);

        // Secretos de las sesiones: credenciales del panel de Sunshine.
        if (src.Sessions is not null)
        {
            try
            {
                foreach (var s in src.Sessions())
                {
                    r.AddTerm(s.SunshineAuthPass, "<redacted>", minLength: 4);
                    r.AddTerm(s.SunshineAuthUser, "<redacted>", minLength: 4);
                    if (!string.IsNullOrWhiteSpace(s.Username)) users.Add(s.Username);
                }
            }
            catch { }
        }

        // Cada cuenta con su número (<user1>, <user2>…): así se ve si la consola y una sesión
        // usan la misma cuenta sin saber cuál es. Los perfiles con dominio o duplicados acaban en
        // "nombre.PC" o "nombre.000": cuentan como la misma cuenta.
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in users.Where(u => !builtIn.Contains(u, StringComparer.OrdinalIgnoreCase) &&
                                           !u.StartsWith("osms-", StringComparison.OrdinalIgnoreCase))
                               .OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
        {
            var dot = u.IndexOf('.');
            var bare = dot > 2 ? u[..dot] : u;
            if (!tags.TryGetValue(bare, out var tag)) tags[bare] = tag = $"<user{tags.Count + 1}>";
            tags[u] = tag;
        }
        foreach (var (u, tag) in tags.OrderByDescending(t => t.Key.Length)) r.AddTerm(u, tag);
        r.AddTerm(Environment.MachineName, "<pc>");
        return r;
    }

    private sealed class Redactor
    {
        private readonly List<(Regex Re, string With)> _rules = [];

        public void AddTerm(string? term, string with, int minLength = 3)
        {
            if (string.IsNullOrWhiteSpace(term) || term.Length < minLength) return;
            var re = new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(term) + @"(?![\p{L}\p{N}_])",
                               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _rules.Add((re, with));
        }

        public string Apply(string text)
        {
            foreach (var (re, with) in _rules) text = re.Replace(text, with);
            return EmailRegex().Replace(text, "<email>");
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?im)^(\s*[\w.]*(?:pass|secret|token|pin|salt)[\w.]*\s*)=.*$")]
    private static partial Regex SecretConfLine();

    // ── Win32 ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFOW
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
