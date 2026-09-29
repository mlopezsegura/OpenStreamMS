using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using OpenStreamMS.Services;

namespace OpenStreamMS.Services.Session;

/// <summary>
/// Estado actual de TermWrap (rewrite moderno de rdpwrap, mantenido por <see href="https://github.com/llccd/TermWrap"/>).
/// </summary>
public record TermWrapStatus(
    /// <summary>true si TermWrap.dll está copiado y TermService.ServiceDll apunta a él.</summary>
    bool    Installed,
    /// <summary>Directorio canónico de instalación (<c>%ProgramFiles%\RDP Wrapper</c>, compartido con rdpwrap).</summary>
    string  InstallDir,
    /// <summary>Versión del release de TermWrap copiado (leída de <c>termwrap_version.txt</c>). Null si no se detecta.</summary>
    string? BundleVersion,
    /// <summary>Versión de <c>termsrv.dll</c> del sistema (file version).</summary>
    string? TermsrvVersion,
    /// <summary>Valor actual del registro <c>ServiceDll</c> de TermService.</summary>
    string? ServiceDllPath,
    /// <summary>true si hay una operación en curso (install/uninstall).</summary>
    bool    Busy,
    /// <summary>Última acción ejecutada (texto corto para mostrar).</summary>
    string? LastAction);

/// <summary>
/// Gestor de TermWrap. Reemplazo recomendado de RDPWrap (asmtron) porque integra
/// <c>RDPWrapOffsetFinder</c> y descubre los offsets de termsrv.dll automáticamente,
/// sobreviviendo a Windows Updates sin necesidad de actualizar <c>rdpwrap.ini</c>.
/// <para>
/// Ruta de instalación compartida con rdpwrap (<c>%ProgramFiles%\RDP Wrapper</c>) — solo
/// uno de los dos puede tener registrado <c>ServiceDll</c> a la vez. <see cref="RdpWrapperManager"/>
/// se mantiene como fallback por si una build de Windows rompe TermWrap.
/// </para>
/// </summary>
public sealed class TermWrapManager
{
    private const string GithubRepo  = "llccd/TermWrap";
    private const string UserAgent   = "OpenStreamMS";
    private const int    MaxLogLines = 500;

    private static readonly HttpClient _http = BuildHttpClient();

    private readonly string _installDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrapper");

    private readonly object _sync = new();
    private readonly List<string> _log = new();
    private volatile bool   _busy;
    private          string? _lastAction;

    public bool IsBusy => _busy;

    public TermWrapStatus GetStatus()
    {
        var dll          = Path.Combine(_installDir, "TermWrap.dll");
        var serviceDll   = ReadServiceDll();
        var dllExists    = File.Exists(dll);
        var pointsToOurs = serviceDll is not null &&
                           PathsEqual(serviceDll, dll);

        return new TermWrapStatus(
            Installed:      dllExists && pointsToOurs,
            InstallDir:     _installDir,
            BundleVersion:  ReadBundleVersion(),
            TermsrvVersion: ReadTermsrvVersion(),
            ServiceDllPath: serviceDll,
            Busy:           _busy,
            LastAction:     _lastAction);
    }

    public string[] GetLog()
    {
        lock (_sync) return _log.ToArray();
    }

    /// <summary>
    /// Descarga TermWrap-X.Y.zip de la última release en GitHub, copia las DLLs
    /// (arquitectura del proceso) a <c>%ProgramFiles%\RDP Wrapper</c>, registra
    /// <c>TermWrap.dll</c> como <c>ServiceDll</c> de TermService y reinicia el servicio.
    /// </summary>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("install", async () =>
        {
            AppendLog($"Descargando última release de {GithubRepo}…");
            var (assetUrl, assetName, version) = await FetchLatestZipUrlAsync(ct);
            AppendLog($"Asset: {assetName} (v{version})");

            var tempZip = Path.Combine(Path.GetTempPath(), $"OpenStreamMS_{assetName}");
            await DownloadAsync(assetUrl, tempZip, ct);
            AppendLog($"Descargado en: {tempZip}");

            Directory.CreateDirectory(_installDir);

            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64   => "x64",
                Architecture.Arm64 => "x64", // TermWrap solo distribuye x64/x86; en ARM64 Windows ejecuta x64 emulado.
                Architecture.X86   => "x86",
                _                  => "x64",
            };
            AppendLog($"Arquitectura objetivo: {arch}");

            using (var archive = ZipFile.OpenRead(tempZip))
            {
                int copied = 0;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directorios

                    var prefix = arch + "/";
                    if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dest = Path.Combine(_installDir, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                    AppendLog($"  → {entry.Name}");
                    copied++;
                }
                if (copied == 0)
                    throw new InvalidOperationException($"El zip no contiene DLLs para arquitectura {arch}.");
            }

            File.WriteAllText(Path.Combine(_installDir, "termwrap_version.txt"), version);
            try { File.Delete(tempZip); } catch { }

            AppendLog("Apuntando TermService.ServiceDll a TermWrap.dll…");
            SetTermServiceDll(Path.Combine(_installDir, "TermWrap.dll"));

            AppendLog("Reiniciando TermService (las sesiones RDP activas se cerrarán)…");
            RestartTermService();

            var status = GetStatus();
            AppendLog(status.Installed
                ? "✓ TermWrap instalado y activo."
                : "✗ TermWrap NO quedó activo (revisa el log).");
        });
    }

    /// <summary>
    /// Restaura el <c>ServiceDll</c> original (<c>%SystemRoot%\System32\termsrv.dll</c>),
    /// reinicia TermService y borra los archivos copiados. No toca rdpwrap si estuviera
    /// instalado en el mismo directorio (TermWrap.dll, UmWrap.dll, EndpWrap.dll, Zydis.dll
    /// y termwrap_version.txt son los únicos que se eliminan).
    /// </summary>
    public async Task UninstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("uninstall", async () =>
        {
            AppendLog("Restaurando ServiceDll por defecto (%SystemRoot%\\System32\\termsrv.dll)…");
            SetTermServiceDll(@"%SystemRoot%\System32\termsrv.dll");

            AppendLog("Reiniciando TermService…");
            RestartTermService();

            string[] artefacts =
            {
                "TermWrap.dll", "UmWrap.dll", "EndpWrap.dll", "Zydis.dll",
                "termwrap_version.txt",
            };

            foreach (var name in artefacts)
            {
                var path = Path.Combine(_installDir, name);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        AppendLog($"  borrado: {name}");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  no se pudo borrar {name}: {ex.Message}");
                }
            }

            await Task.CompletedTask;

            var status = GetStatus();
            AppendLog(status.Installed
                ? "✗ Todavía aparece instalado; revisa el log."
                : "✓ Desinstalación completada.");
        });
    }

    private async Task RunExclusiveAsync(string action, Func<Task> work)
    {
        lock (_sync)
        {
            if (_busy) throw new InvalidOperationException("Ya hay una operación de TermWrap en curso.");
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
            Logger.Error($"[TermWrap] Error en {action}: {ex}");
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

    private async Task<(string Url, string Name, string Version)> FetchLatestZipUrlAsync(CancellationToken ct)
    {
        var apiUrl = $"https://api.github.com/repos/{GithubRepo}/releases/latest";
        using var resp = await _http.GetAsync(apiUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

        var version = doc.RootElement.TryGetProperty("tag_name", out var tag)
            ? tag.GetString()?.TrimStart('v') ?? "?"
            : "?";

        var assets = doc.RootElement.GetProperty("assets");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                name.StartsWith("TermWrap", StringComparison.OrdinalIgnoreCase))
            {
                var url = asset.GetProperty("browser_download_url").GetString()!;
                return (url, name, version);
            }
        }
        throw new InvalidOperationException("No se encontró el ZIP de TermWrap en la última release.");
    }

    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
    }

    private static void SetTermServiceDll(string path)
    {
        // ServiceDll DEBE ser REG_EXPAND_SZ para que el SCM expanda %ProgramFiles%/%SystemRoot%
        // antes de cargar la DLL. Si quedara como REG_SZ, TermService fallaría al iniciar.
        using var key = Microsoft.Win32.Registry.LocalMachine
            .CreateSubKey(@"SYSTEM\CurrentControlSet\Services\TermService\Parameters", writable: true)
            ?? throw new InvalidOperationException("no se pudo abrir HKLM\\...\\TermService\\Parameters");

        // Convertir ruta absoluta a la forma con variable de entorno si vive bajo ProgramFiles,
        // así el registro sigue siendo portable entre arquitecturas (Program Files vs Program Files (x86)).
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var stored = path.StartsWith(pf, StringComparison.OrdinalIgnoreCase)
            ? "%ProgramFiles%" + path[pf.Length..]
            : path;

        key.SetValue("ServiceDll", stored, Microsoft.Win32.RegistryValueKind.ExpandString);
    }

    private void RestartTermService()
    {
        using var sc = new ServiceController("TermService");

        // TermService tiene dependientes (UmRdpService, SessionEnv...). Stop con timeout
        // generoso para que el SCM cierre dependientes primero. Si falla, lo logueamos
        // pero seguimos intentando arrancar — el siguiente reboot terminará el cambio.
        try
        {
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                AppendLog("  TermService detenido.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"  No se pudo detener TermService limpiamente: {ex.Message}");
        }

        try
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            AppendLog("  TermService arrancado.");
        }
        catch (Exception ex)
        {
            AppendLog($"  ERROR arrancando TermService: {ex.Message}. Puede ser necesario reiniciar Windows.");
        }
    }

    private void AppendLog(string line)
    {
        lock (_sync)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        Logger.Log($"[TermWrap] {line}");
    }

    private static string? ReadServiceDll()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermService\Parameters");
            if (key is null) return null;

            // Leer en bruto y expandir manualmente: si es REG_EXPAND_SZ, el helper de
            // .NET ya expande, pero forzamos el caso por si la entrada quedó como REG_SZ.
            var raw = key.GetValue("ServiceDll", null,
                Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return raw is null ? null : Environment.ExpandEnvironmentVariables(raw);
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
            var info = Path.Combine(_installDir, "termwrap_version.txt");
            return File.Exists(info) ? File.ReadAllText(info).Trim() : null;
        }
        catch { return null; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd('\\'),
            Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
