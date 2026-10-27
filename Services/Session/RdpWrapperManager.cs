using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenStreamMS.Services;

namespace OpenStreamMS.Services.Session;

/// <summary>
/// Estado actual del RDP Wrapper en la máquina.
/// </summary>
public record RdpWrapperStatus(
    /// <summary>true si ServiceDll apunta a un wrapper conocido, el archivo existe y TermService lo tiene cargado.</summary>
    bool    Installed,
    /// <summary>Wrapper al que apunta ServiceDll: <c>"asmtron"</c>, <c>"TermWrap"</c> o null si es el termsrv.dll de Windows u otro.</summary>
    string? Wrapper,
    /// <summary>true si la DLL del wrapper está cargada en el proceso de TermService; null si no se pudo comprobar.</summary>
    bool?   Loaded,
    /// <summary>true si RDP-Tcp pasa por el proveedor de RemoteIdd (captura remoteidd disponible).</summary>
    bool    RemoteIdd,
    /// <summary>Directorio canónico de instalación (<c>%ProgramFiles%\RDP Wrapper</c>).</summary>
    string  InstallDir,
    /// <summary>Versión del bundle asmtron instalado (leída de <c>autoupdate__info.txt</c>). Null si no se detecta.</summary>
    string? BundleVersion,
    /// <summary>Versión de <c>termsrv.dll</c> del sistema (file version).</summary>
    string? TermsrvVersion,
    /// <summary>Valor actual del registro <c>ServiceDll</c> de TermService.</summary>
    string? ServiceDllPath,
    /// <summary>true si hay una operación en curso (install/update/uninstall).</summary>
    bool    Busy,
    /// <summary>Última acción ejecutada (texto corto para mostrar).</summary>
    string? LastAction);

/// <summary>
/// Gestiona la instalación de <see href="https://github.com/asmtron/rdpwrap"/>
/// desde la UI: descarga la última release, la extrae a <c>%ProgramFiles%\RDP Wrapper</c>
/// y delega en <c>autoupdate.bat</c> para aplicar/actualizar el parche en TermService.
/// </summary>
public sealed class RdpWrapperManager
{
    private const string GithubRepo   = "asmtron/rdpwrap";
    private const string UserAgent    = "OpenStreamMS";
    private const int    MaxLogLines  = 500;

    private static readonly HttpClient _http = BuildHttpClient();

    private readonly string _installDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrapper");

    private readonly object _sync = new();
    private readonly List<string> _log = new();
    private volatile bool   _busy;
    private          string? _lastAction;

    // ── API pública ──────────────────────────────────────────────────────────

    public bool IsBusy => _busy;

    public RdpWrapperStatus GetStatus()
    {
        var serviceDll = ReadServiceDll();
        // TermWrap (llccd) sustituye a asmtron cuando RemoteIdd está instalado: parchea además
        // IsAllowNonRDPStack, que el proveedor RDS necesita. Ambos se registran en ServiceDll.
        var wrapper = Path.GetFileName(serviceDll ?? "").ToLowerInvariant() switch
        {
            "rdpwrap.dll"  => "asmtron",
            "termwrap.dll" => "TermWrap",
            _              => null,
        };
        var filesOk = wrapper switch
        {
            "asmtron"  => File.Exists(serviceDll) && File.Exists(Path.Combine(Path.GetDirectoryName(serviceDll)!, "rdpwrap.ini")),
            "TermWrap" => File.Exists(serviceDll),
            _          => false,
        };
        var loaded = wrapper is null ? null : IsLoadedInTermService(serviceDll!);

        return new RdpWrapperStatus(
            Installed:     filesOk && loaded == true,
            Wrapper:       wrapper,
            Loaded:        loaded,
            RemoteIdd:     IsRemoteIddActive(),
            InstallDir:    _installDir,
            BundleVersion: wrapper == "TermWrap" ? ReadFileVersion(serviceDll!) : ReadBundleVersion(),
            TermsrvVersion:ReadTermsrvVersion(),
            ServiceDllPath:serviceDll,
            Busy:          _busy,
            LastAction:    _lastAction);
    }

    public string[] GetLog()
    {
        lock (_sync) return _log.ToArray();
    }

    public async Task InstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("install", async () =>
        {
            AppendLog("Descargando última release de asmtron/rdpwrap…");
            var (assetUrl, assetName) = await FetchLatestPortableZipUrlAsync(ct);
            AppendLog($"Asset: {assetName}");

            var tempZip = Path.Combine(Path.GetTempPath(), $"OpenStreamMS_{assetName}");
            await DownloadAsync(assetUrl, tempZip, ct);
            AppendLog($"Descargado en: {tempZip}");

            Directory.CreateDirectory(_installDir);
            AppendLog($"Extrayendo en: {_installDir}");
            ZipFile.ExtractToDirectory(tempZip, _installDir, overwriteFiles: true);
            try { File.Delete(tempZip); } catch { }

            AppendLog("Ejecutando autoupdate.bat (parchea termsrv.dll y reinicia TermService)…");
            var exitCode = await RunBatchAsync(Path.Combine(_installDir, "autoupdate.bat"), ct);
            AppendLog($"autoupdate.bat salió con código {exitCode}.");

            var status = GetStatus();
            AppendLog(status.Installed
                ? "✓ RDP Wrapper instalado y activo."
                : "✗ RDP Wrapper NO quedó activo (revisa el log).");
        });
    }

    public async Task UpdateAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("update", async () =>
        {
            var autoupdate = Path.Combine(_installDir, "autoupdate.bat");
            if (!File.Exists(autoupdate))
            {
                AppendLog($"No se encontró {autoupdate}. Ejecuta 'Instalar' primero.");
                return;
            }

            AppendLog("Ejecutando autoupdate.bat (refresca rdpwrap.ini si es necesario)…");
            var exitCode = await RunBatchAsync(autoupdate, ct);
            AppendLog($"autoupdate.bat salió con código {exitCode}.");
        });
    }

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("uninstall", async () =>
        {
            var rdpwinst = Path.Combine(_installDir, "RDPWInst.exe");
            if (!File.Exists(rdpwinst))
            {
                AppendLog($"No se encontró {rdpwinst}. Nada que desinstalar.");
                return;
            }

            AppendLog("Ejecutando RDPWInst.exe -u (restaura termsrv.dll original)…");
            var exitCode = await RunProcessAsync(rdpwinst, "-u", _installDir, ct);
            AppendLog($"RDPWInst.exe salió con código {exitCode}.");

            var status = GetStatus();
            AppendLog(status.Installed
                ? "✗ Todavía aparece instalado; revisa el log."
                : "✓ Desinstalación completada.");
        });
    }

    // ── Respaldo: TermWrap → asmtron ─────────────────────────────────────────

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
    /// Al arrancar, antes de levantar puestos: si RemoteIdd está puesto pero TermWrap no funciona
    /// (no cargado, la tarea de asmtron lo cambió, o un Windows Update rompió sus parches y RDP-Tcp
    /// no escucha), se vuelve a asmtron y a RDP-Tcp normal. Los puestos con captura remoteidd
    /// arrancan entonces con la captura de siempre. Para volver a RemoteIdd: install.ps1.
    /// </summary>
    public void EnsureMultiRdp(CancellationToken ct)
    {
        if (!IsRemoteIddActive()) return;
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
        Logger.Error($"[RdpWrap] RemoteIdd sin TermWrap funcional ({problem}). Se vuelve a RDP Wrapper de asmtron.");
        LeaveRemoteIdd();
        SetServiceDllToAsmtron();
        RestartTermService();
        Logger.Log($"[RdpWrap] Respaldo aplicado. Estado: {GetStatus()}");
    }

    /// <summary>Null si TermWrap está cargado y RDP-Tcp escucha; si no, el motivo.</summary>
    private string? TermWrapProblem()
    {
        var st = GetStatus();
        if (st.Wrapper != "TermWrap") return $"ServiceDll = {st.ServiceDllPath}";
        if (st.Loaded != true)        return "TermWrap.dll no está cargada en TermService";
        if (!IsRdpTcpListening())     return "RDP-Tcp no escucha";
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
    /// Quita el proveedor de RemoteIdd de RDP-Tcp y reactiva la tarea de autoupdate de asmtron
    /// que install.ps1 desactivó. Con asmtron, termsrv rechaza un proveedor que no es el de
    /// Microsoft y RDP-Tcp dejaría de escuchar: hay que hacerlo antes de cualquier acción de asmtron.
    /// El driver y la DLL del proveedor se quedan instalados; uninstall.ps1 los quita.
    /// </summary>
    private void LeaveRemoteIdd()
    {
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RdpTcpKey, writable: true))
        {
            if (key is null) return;
            var inner = key.GetValue("OsmsInnerProtocol_Object") as string;
            if (string.IsNullOrWhiteSpace(inner)) inner = RdpCoreTsClsid;
            key.SetValue("LoadableProtocol_Object", inner, Microsoft.Win32.RegistryValueKind.String);
            AppendLog($"RDP-Tcp devuelto a {inner} (RemoteIdd fuera).");
        }
        foreach (var task in ReadDisabledTasks())
        {
            var exit = RunProcessAsync("schtasks.exe", $"/Change /TN \"{task}\" /ENABLE", Environment.SystemDirectory,
                                       CancellationToken.None).GetAwaiter().GetResult();
            AppendLog($"Tarea {task} reactivada (schtasks salió con {exit}).");
        }
    }

    /// <summary>Tareas que install.ps1 de RemoteIdd desactivó ("TaskPath|TaskName" en su fichero de estado).</summary>
    private static IEnumerable<string> ReadDisabledTasks()
    {
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                 "OpenStreamMS", "remoteidd-install.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(state));
            if (!doc.RootElement.TryGetProperty("DisabledTasks", out var arr)) return [];
            // ConvertTo-Json guarda un solo elemento como cadena, no como lista.
            List<JsonElement> list = arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().ToList() : new() { arr };
            return list.Where(e => e.ValueKind == JsonValueKind.String)
                       .Select(e => e.GetString()!.Replace("|", ""))
                       .Where(t => t.Length > 0).ToList();
        }
        catch { return []; }
    }

    private void SetServiceDllToAsmtron()
    {
        var dll = Path.Combine(_installDir, "rdpwrap.dll");
        if (!File.Exists(dll) || !File.Exists(Path.Combine(_installDir, "rdpwrap.ini")))
        {
            AppendLog($"No está {dll} con su rdpwrap.ini: ServiceDll se deja como está. Instala RDP Wrapper desde la UI.");
            return;
        }
        using var key = Microsoft.Win32.Registry.LocalMachine
            .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermService\Parameters", writable: true);
        key?.SetValue("ServiceDll", dll, Microsoft.Win32.RegistryValueKind.ExpandString);
        AppendLog($"TermService: ServiceDll -> {dll}.");
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
        AppendLog("TermService reiniciado.");
    }

    // ── Implementación interna ───────────────────────────────────────────────

    private async Task RunExclusiveAsync(string action, Func<Task> work)
    {
        lock (_sync)
        {
            if (_busy) throw new InvalidOperationException("Ya hay una operación de RDP Wrapper en curso.");
            _busy       = true;
            _lastAction = action;
            _log.Clear();
        }
        AppendLog($"--- Inicio: {action} ({DateTime.Now:HH:mm:ss}) ---");
        try
        {
            // Las tres acciones dejan ServiceDll en rdpwrap.dll o termsrv.dll y reinician TermService:
            // con el proveedor de RemoteIdd aún en RDP-Tcp, el RDP dejaría de escuchar.
            if (IsRemoteIddActive()) LeaveRemoteIdd();
            await work();
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            Logger.Error($"[RdpWrap] Error en {action}: {ex}");
        }
        finally
        {
            AppendLog($"--- Fin: {action} ({DateTime.Now:HH:mm:ss}) ---");
            _busy = false;
        }
    }

    private static HttpClient BuildHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    private async Task<(string Url, string Name)> FetchLatestPortableZipUrlAsync(CancellationToken ct)
    {
        var apiUrl = $"https://api.github.com/repos/{GithubRepo}/releases/latest";
        using var resp = await _http.GetAsync(apiUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

        var assets = doc.RootElement.GetProperty("assets");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            // Queremos el .zip portable, NO el -Installer.exe ni -Installer.zip
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Installer", StringComparison.OrdinalIgnoreCase))
            {
                var url = asset.GetProperty("browser_download_url").GetString()!;
                return (url, name);
            }
        }
        throw new InvalidOperationException("No se encontró el ZIP portable en la última release de asmtron/rdpwrap.");
    }

    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
    }

    private Task<int> RunBatchAsync(string batFile, CancellationToken ct) =>
        RunProcessAsync("cmd.exe", $"/c \"\"{batFile}\"\"", Path.GetDirectoryName(batFile)!, ct);

    private async Task<int> RunProcessAsync(string fileName, string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) AppendLog($"[stderr] {e.Data}"); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await p.WaitForExitAsync(ct);
        return p.ExitCode;
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
    /// no basta: hasta el siguiente reinicio del servicio sigue cargada la anterior, y la tarea de
    /// autoupdate de asmtron puede haberla cambiado por detrás. Null si TermService está parado o
    /// no se puede leer el proceso.
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

    private string? ReadBundleVersion()
    {
        try
        {
            var info = Path.Combine(_installDir, "helper", "autoupdate__info.txt");
            if (!File.Exists(info)) return null;
            foreach (var line in File.ReadLines(info))
            {
                // "Automatic RDP Wrapper installer and updater v.1.4       asmtron (2025-10-23)"
                var idx = line.IndexOf("v.", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && line.Contains("asmtron", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = line[(idx + 2)..].TrimStart();
                    var end  = rest.IndexOfAny(new[] { ' ', '\t' });
                    return end > 0 ? rest[..end] : rest;
                }
            }
        }
        catch { }
        return null;
    }
}
