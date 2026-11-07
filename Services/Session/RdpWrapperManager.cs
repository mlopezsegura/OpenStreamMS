using System.Diagnostics;
using OpenStreamMS.Services;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Services.Session;

/// <summary>
/// Estado del multi-RDP (TermWrap) en la máquina.
/// </summary>
public record RdpWrapperStatus(
    /// <summary>true si ServiceDll apunta a un wrapper conocido, el archivo existe y TermService lo tiene cargado.</summary>
    bool    Installed,
    /// <summary>
    /// Wrapper al que apunta ServiceDll: <c>"TermWrap"</c>, <c>"asmtron"</c> (instalación antigua:
    /// el arranque de OpenStreamMS lo cambia a TermWrap) o null si es el termsrv.dll de Windows u otro.
    /// </summary>
    string? Wrapper,
    /// <summary>true si la DLL del wrapper está cargada en el proceso de TermService; null si no se pudo comprobar.</summary>
    bool?   Loaded,
    /// <summary>
    /// Instalado pero aún sin cargar: TermService está parado (en Windows cliente arranca con la
    /// primera conexión RDP) o sigue con la DLL anterior hasta reiniciar Windows. Es lo normal
    /// justo después de instalar; no es un fallo.
    /// </summary>
    bool    Pending,
    /// <summary>true si RDP-Tcp pasa por el proveedor de RemoteIdd (captura remoteidd disponible).</summary>
    bool    RemoteIdd,
    /// <summary>Directorio canónico de instalación (<c>%ProgramFiles%\RDP Wrapper</c>).</summary>
    string  InstallDir,
    /// <summary>Versión de TermWrap.dll. Null si ServiceDll no apunta a TermWrap.</summary>
    string? BundleVersion,
    /// <summary>Versión de <c>termsrv.dll</c> del sistema (file version).</summary>
    string? TermsrvVersion,
    /// <summary>Valor actual del registro <c>ServiceDll</c> de TermService.</summary>
    string? ServiceDllPath,
    /// <summary>true si hay una operación en curso (instalar/reparar).</summary>
    bool    Busy,
    /// <summary>Última acción ejecutada (texto corto para mostrar).</summary>
    string? LastAction);

/// <summary>
/// Multi-RDP con TermWrap (llccd) como única opción: varias sesiones RDP en Windows cliente,
/// Home incluido, y el parche IsAllowNonRDPStack que necesita el proveedor de RemoteIdd. Ya no
/// se usa el RDP Wrapper de asmtron: si una instalación antigua lo tiene puesto, el arranque lo
/// cambia a TermWrap y desactiva su tarea de autoupdate. Ver <see cref="TermWrapInstaller"/>.
/// </summary>
public sealed class RdpWrapperManager
{
    private const int MaxLogLines = 500;

    private readonly string _installDir = TermWrapInstaller.InstallDir;

    private readonly object _sync = new();
    private readonly List<string> _log = new();
    private volatile bool   _busy;
    private          string? _lastAction;

    // ── API pública ──────────────────────────────────────────────────────────

    public bool IsBusy => _busy;

    public RdpWrapperStatus GetStatus()
    {
        var serviceDll = ReadServiceDll();
        var wrapper = Path.GetFileName(serviceDll ?? "").ToLowerInvariant() switch
        {
            "termwrap.dll" => "TermWrap",
            "rdpwrap.dll"  => "asmtron",
            _              => null,
        };
        var filesOk = wrapper is not null && File.Exists(serviceDll);
        var loaded  = wrapper is null ? null : IsLoadedInTermService(serviceDll!);

        return new RdpWrapperStatus(
            Installed:     filesOk && loaded == true,
            Wrapper:       wrapper,
            Loaded:        loaded,
            Pending:       filesOk && loaded != true,
            RemoteIdd:     IsRemoteIddActive(),
            InstallDir:    _installDir,
            BundleVersion: wrapper == "TermWrap" ? ReadFileVersion(serviceDll!) : null,
            TermsrvVersion:ReadTermsrvVersion(),
            ServiceDllPath:serviceDll,
            Busy:          _busy,
            LastAction:    _lastAction);
    }

    public string[] GetLog()
    {
        lock (_sync) return _log.ToArray();
    }

    /// <summary>Instala o repara TermWrap: ficheros con su hash, ServiceDll y TermService.</summary>
    public Task InstallAsync(CancellationToken ct = default) =>
        RunExclusiveAsync("install", async () =>
        {
            await TermWrapInstaller.EnsureFilesAsync(AppendLog, ct);
            TermWrapInstaller.DisableAsmtronAutoupdate(AppendLog);
            TermWrapInstaller.PointServiceDll(AppendLog);
            if (GetStatus().Loaded != true) ApplyTermServiceChange();
            AppendLog(GetStatus().Installed
                ? "✓ Multi-RDP (TermWrap) instalado y activo."
                : "✗ TermWrap aún no está cargado: se cargará al reiniciar el equipo.");
        });

    // ── Arranque: TermWrap y RemoteIdd ───────────────────────────────────────

    /// <summary>Proveedor de protocolo de RemoteIdd (Native/RemoteIdd/Provider/install.ps1).</summary>
    public const string RemoteIddProviderClsid = "{8B0F3C51-2D7A-4F6E-A1C9-5E4D2B7F0A36}";
    private const string RdpTcpKey     = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string RdpCoreTsClsid = "{5828227c-20cf-4408-b73f-73ab70b8849f}";

    /// <summary>true si RDP-Tcp pasa por el proveedor de RemoteIdd.</summary>
    public static bool IsRemoteIddActive()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RdpTcpKey);
            return string.Equals(key?.GetValue("LoadableProtocol_Object") as string, RemoteIddProviderClsid,
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Al arrancar, antes de levantar puestos: pone TermWrap como multi-RDP si ServiceDll apunta a
    /// otra cosa, y si RemoteIdd está puesto pero TermWrap no funciona (no cargado o un Windows
    /// Update rompió sus parches y RDP-Tcp no escucha) quita RemoteIdd de RDP-Tcp. Los puestos con
    /// captura remoteidd arrancan entonces con la captura de siempre. Para volver a RemoteIdd: install.ps1.
    /// </summary>
    public void EnsureMultiRdp(CancellationToken ct)
    {
        try { EnsureMultiRdpCore(ct); }
        finally { MultiRdpCheckedSource.TrySetResult(); }
    }

    private static readonly TaskCompletionSource MultiRdpCheckedSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Se completa cuando <see cref="EnsureMultiRdp"/> ha terminado. Los puestos esperan a esto
    /// antes de autoarrancar: si no, arrancan con captura remoteidd y un instante después se quita
    /// RemoteIdd, y Sunshine se queda sin pantalla.
    /// </summary>
    public static Task MultiRdpChecked => MultiRdpCheckedSource.Task;

    /// <summary>
    /// RemoteIdd instalado después de que arrancara TermService: install.ps1 no reinicia TermService
    /// (se quedaba colgado al pararse), así que TermWrap y el proveedor entran al reiniciar el
    /// equipo. Mientras tanto no está roto, está pendiente: ni se usa ni se deshace.
    /// </summary>
    public static bool IsRemoteIddPendingRestart()
    {
        try
        {
            var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                     "OpenStreamMS", "remoteidd-install.json");
            if (!IsRemoteIddActive() || !File.Exists(state)) return false;
            var pid = GetServicePid("TermService");
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            return File.GetLastWriteTime(state) > p.StartTime;
        }
        catch { return false; }
    }

    /// <summary>RemoteIdd puesto en RDP-Tcp y ya cargado (no pendiente de reiniciar el equipo).</summary>
    public static bool IsRemoteIddUsable() => IsRemoteIddActive() && !IsRemoteIddPendingRestart();

    private void EnsureMultiRdpCore(CancellationToken ct)
    {
        EnsureTermWrap(ct);
        if (!IsRemoteIddActive()) return;
        if (IsRemoteIddPendingRestart())
        {
            Logger.Error("[RdpWrap] RemoteIdd instalado y pendiente de reiniciar el equipo: hasta entonces los " +
                         "puestos usan la captura de siempre.");
            return;
        }
        // En el arranque del PC TermService puede tardar en abrir el listener.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string? problem;
        while ((problem = TermWrapProblem()) is not null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            Thread.Sleep(2000);
        if (problem is null)
        {
            Logger.Log("[RdpWrap] TermWrap activo y RDP-Tcp escuchando: RemoteIdd disponible.");
            return;
        }
        Logger.Error($"[RdpWrap] RemoteIdd sin TermWrap funcional ({problem}). Se quita RemoteIdd de RDP-Tcp; el multi-RDP sigue siendo TermWrap.");
        LeaveRemoteIdd();
        ApplyTermServiceChange();
        Logger.Log($"[RdpWrap] RemoteIdd fuera de RDP-Tcp. Estado: {GetStatus()}");
    }

    /// <summary>
    /// Si ServiceDll apunta a otra cosa (termsrv.dll de Windows, rdpwrap.dll de asmtron de una
    /// instalación antigua, o un Windows Update que lo cambió) se vuelve a poner TermWrap,
    /// descargándolo si faltan sus ficheros.
    /// </summary>
    private void EnsureTermWrap(CancellationToken ct)
    {
        try
        {
            var st = GetStatus();
            if (st.Wrapper == "TermWrap" && TermWrapInstaller.FilesOk())
            {
                if (st.Loaded != true)
                    Logger.Warning("[RdpWrap] TermWrap puesto pero no cargado en TermService (se cargará al reiniciarlo).");
                return;
            }
            Logger.Log($"[RdpWrap] Multi-RDP: ServiceDll = {st.ServiceDllPath}; se pone TermWrap.");
            TermWrapInstaller.EnsureFilesAsync(AppendLog, ct).GetAwaiter().GetResult();
            TermWrapInstaller.DisableAsmtronAutoupdate(AppendLog);
            if (TermWrapInstaller.PointServiceDll(AppendLog) || GetStatus().Loaded != true)
                ApplyTermServiceChange();
            Logger.Log($"[RdpWrap] Multi-RDP con TermWrap. Estado: {GetStatus()}");
        }
        catch (Exception ex)
        {
            Logger.Error(BackendText.Get("rdpwrap.failedToSetTermWrap", ex.Message));
        }
    }

    /// <summary>Null si TermWrap está cargado y RDP-Tcp escucha; si no, el motivo.</summary>
    private string? TermWrapProblem()
    {
        var st = GetStatus();
        if (st.Wrapper != "TermWrap") return BackendText.Get("rdpwrap.serviceDllMismatch", st.ServiceDllPath);
        if (st.Loaded != true)        return BackendText.Get("rdpwrap.termwrapNotLoaded");
        if (!IsRdpTcpListening())     return BackendText.Get("rdpwrap.rdpTcpNotListening");
        return null;
    }

    private static bool IsRdpTcpListening()
    {
        int port = 3389;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RdpTcpKey);
            if (key?.GetValue("PortNumber") is int p && p > 0) port = p;
        }
        catch { }
        return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(e => e.Port == port);
    }

    /// <summary>
    /// Quita el proveedor de RemoteIdd de RDP-Tcp. El driver y la DLL del proveedor se quedan
    /// instalados; uninstall.ps1 los quita. Las tareas de autoupdate de asmtron que install.ps1
    /// desactivó siguen desactivadas: reactivarlas volvería a poner rdpwrap.dll al arrancar.
    /// </summary>
    private void LeaveRemoteIdd()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: true);
        if (key is null) return;
        var inner = key.GetValue("OsmsInnerProtocol_Object") as string;
        if (string.IsNullOrWhiteSpace(inner)) inner = RdpCoreTsClsid;
        key.SetValue("LoadableProtocol_Object", inner, Microsoft.Win32.RegistryValueKind.String);
        AppendLog($"RDP-Tcp devuelto a {inner} (RemoteIdd fuera).");
    }

    /// <summary>
    /// Reinicia TermService para que cargue el cambio, salvo con sesiones remotas o de puesto
    /// abiertas: pararlo así puede dejarlo a medias (StopPending) y colgar el apagado. Entonces el
    /// cambio queda en el registro y se aplica en el próximo arranque del equipo.
    /// </summary>
    private void ApplyTermServiceChange()
    {
        var live = OpenStreamMS.Core.Helpers.RdpSessionCreator.DescribeNonConsoleUserSessions();
        if (live.Count > 0)
        {
            AppendLog(BackendText.Get("rdpwrap.openSessionsPreventRestart", string.Join(", ", live)));
            return;
        }
        RestartTermService();
    }

    private void RestartTermService()
    {
        using var sc = new System.ServiceProcess.ServiceController("TermService");
        if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
        {
            sc.Stop(); // ServiceController para antes los dependientes (UmRdpService).
            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }
        sc.Start();
        sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        AppendLog(BackendText.Get("rdpwrap.termServiceRestarted"));
    }

    // ── Implementación interna ───────────────────────────────────────────────

    private async Task RunExclusiveAsync(string action, Func<Task> work)
    {
        lock (_sync)
        {
            if (_busy) throw new InvalidOperationException(BackendText.Get("rdpwrap.multiRdpBusy"));
            _busy       = true;
            _lastAction = action;
            _log.Clear();
        }
        AppendLog(BackendText.Get("rdpwrap.log.start", action, DateTime.Now.ToString("HH:mm:ss")));
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            Logger.Error($"[RdpWrap] Error en {action}: {ex}");
        }
        finally
        {
            AppendLog(BackendText.Get("rdpwrap.log.end", action, DateTime.Now.ToString("HH:mm:ss")));
            _busy = false;
        }
    }

    private void AppendLog(string line)
    {
        lock (_sync)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        Logger.Log($"[RdpWrap] {line}");
    }

    // ── Detección de estado ──────────────────────────────────────────────────

    private static string? ReadServiceDll()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermService\Parameters");
            return key?.GetValue("ServiceDll") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Mira si la DLL está en los módulos del svchost de TermService. Que ServiceDll apunte a ella
    /// no basta: hasta el siguiente reinicio del servicio sigue cargada la anterior. Null si
    /// TermService está parado o no se puede leer el proceso.
    /// </summary>
    private static bool? IsLoadedInTermService(string dllPath)
    {
        try
        {
            var pid = GetServicePid("TermService");
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            foreach (ProcessModule m in p.Modules)
                if (string.Equals(m.FileName, dllPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch { return null; }
    }

    private static uint GetServicePid(string name)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return 0;
        try
        {
            var svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return 0;
            try
            {
                var buf = new byte[36]; // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, buf, buf.Length, out _)) return 0;
                const uint SERVICE_RUNNING = 4;
                return BitConverter.ToUInt32(buf, 4) == SERVICE_RUNNING ? BitConverter.ToUInt32(buf, 28) : 0;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private const uint SC_MANAGER_CONNECT   = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr svc, int infoLevel, byte[] buffer, int size, out int needed);
    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr h);

    private static string? ReadFileVersion(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null; }
        catch { return null; }
    }

    private static string? ReadTermsrvVersion()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "termsrv.dll");
            if (!File.Exists(path)) return null;
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch { return null; }
    }
}
