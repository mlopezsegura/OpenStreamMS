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
    /// <summary>true si la DLL está copiada, registrada en el servicio y el archivo existe.</summary>
    bool    Installed,
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
        var dll           = Path.Combine(_installDir, "rdpwrap.dll");
        var ini           = Path.Combine(_installDir, "rdpwrap.ini");
        var serviceDll    = ReadServiceDll();
        var dllInstalled  = File.Exists(dll) && File.Exists(ini);
        var pointsToOurs  = serviceDll is not null &&
                            serviceDll.Equals(dll, StringComparison.OrdinalIgnoreCase);

        return new RdpWrapperStatus(
            Installed:     dllInstalled && pointsToOurs,
            InstallDir:    _installDir,
            BundleVersion: ReadBundleVersion(),
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
