using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenStreamMS.Services.VigEmBus;

/// <summary>
/// Estado actual del driver ViGEmBus en la máquina.
/// </summary>
public record ViGEmBusStatus(
    /// <summary>true si el servicio está registrado y el driver es localizable.</summary>
    bool    Installed,
    /// <summary>Versión instalada (de la entrada de desinstalación de Windows). Null si no se detecta.</summary>
    string? InstalledVersion,
    /// <summary>Estado del servicio: "Running", "Stopped", null si no está registrado.</summary>
    string? ServiceState,
    /// <summary>ImagePath del servicio (ruta al driver <c>.sys</c>).</summary>
    string? DriverImagePath,
    /// <summary>true si la última operación terminó con código 3010 (hace falta reiniciar).</summary>
    bool    RebootRequired,
    /// <summary>true si hay una operación en curso.</summary>
    bool    Busy,
    /// <summary>Última acción ejecutada.</summary>
    string? LastAction);

/// <summary>
/// Gestiona la instalación de <see href="https://github.com/nefarius/ViGEmBus"/>
/// (driver de mando virtual, dependencia de Sunshine/Moonlight) desde la UI:
/// descarga la última release firmada por nefarius y ejecuta el bundle WiX
/// en modo silencioso.
/// </summary>
public sealed class ViGEmBusManager
{
    private const string GithubRepo  = "nefarius/ViGEmBus";
    private const string UserAgent   = "OpenStreamMS";
    private const int    MaxLogLines = 500;

    private static readonly HttpClient _http = BuildHttpClient();

    private readonly object _sync = new();
    private readonly List<string> _log = new();
    private volatile bool   _busy;
    private          string? _lastAction;
    private          bool    _rebootRequired;

    // ── API pública ──────────────────────────────────────────────────────────

    public bool IsBusy => _busy;

    public ViGEmBusStatus GetStatus()
    {
        var (imagePath, state) = ReadService();
        var (displayVersion, _) = ReadUninstallEntry();

        // "Installed" = el servicio existe y el ImagePath apunta a un archivo real.
        // Algunos sistemas cargan el driver bajo demanda, así que "Stopped" también
        // cuenta como instalado mientras el .sys exista.
        var driverFileOk = imagePath is not null &&
                           File.Exists(ResolveDriverPath(imagePath));

        return new ViGEmBusStatus(
            Installed:        imagePath is not null && driverFileOk,
            InstalledVersion: displayVersion,
            ServiceState:     state,
            DriverImagePath:  imagePath,
            RebootRequired:   _rebootRequired,
            Busy:             _busy,
            LastAction:       _lastAction);
    }

    public string[] GetLog()
    {
        lock (_sync) return _log.ToArray();
    }

    public async Task InstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("install", async () =>
        {
            AppendLog("Descargando última release de nefarius/ViGEmBus…");
            var (assetUrl, assetName) = await FetchLatestInstallerUrlAsync(ct);
            AppendLog($"Asset: {assetName}");

            var tempExe = Path.Combine(Path.GetTempPath(), $"OpenStreamMS_{assetName}");
            await DownloadAsync(assetUrl, tempExe, ct);
            AppendLog($"Descargado en: {tempExe}");

            AppendLog("Ejecutando instalador silencioso (/quiet /install /norestart)…");
            var exit = await RunProcessAsync(tempExe, "/quiet /install /norestart",
                                             Path.GetDirectoryName(tempExe)!, ct);
            try { File.Delete(tempExe); } catch { }

            InterpretExitCode(exit, installing: true);
        });
    }

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("uninstall", async () =>
        {
            // El bundle WiX Burn guarda su BundleCachePath en la entrada de Uninstall.
            // Si existe, lanzamos esa copia cacheada directamente para desinstalar.
            var (_, uninstallCmd) = ReadUninstallEntry();

            if (!string.IsNullOrWhiteSpace(uninstallCmd))
            {
                AppendLog($"Usando comando de desinstalación registrado: {uninstallCmd}");
                var (exe, args) = SplitCommandLine(uninstallCmd);
                args = AppendSilentFlags(args);
                var exit = await RunProcessAsync(exe, args, Path.GetDirectoryName(exe) ?? ".", ct);
                InterpretExitCode(exit, installing: false);
                return;
            }

            // Fallback: re-descargar el bundle y lanzarlo con /uninstall.
            AppendLog("No se encontró comando de desinstalación en el registro. Descargando bundle para desinstalar…");
            var (assetUrl, assetName) = await FetchLatestInstallerUrlAsync(ct);
            var tempExe = Path.Combine(Path.GetTempPath(), $"OpenStreamMS_{assetName}");
            await DownloadAsync(assetUrl, tempExe, ct);

            AppendLog("Ejecutando bundle en modo /quiet /uninstall /norestart…");
            var exit2 = await RunProcessAsync(tempExe, "/quiet /uninstall /norestart",
                                              Path.GetDirectoryName(tempExe)!, ct);
            try { File.Delete(tempExe); } catch { }

            InterpretExitCode(exit2, installing: false);
        });
    }

    // ── Implementación interna ───────────────────────────────────────────────

    private void InterpretExitCode(int exit, bool installing)
    {
        AppendLog($"Instalador salió con código {exit}.");
        switch (exit)
        {
            case 0:
                _rebootRequired = false;
                AppendLog(installing
                    ? "✓ ViGEmBus instalado correctamente."
                    : "✓ ViGEmBus desinstalado correctamente.");
                break;
            case 3010:
                _rebootRequired = true;
                AppendLog("⚠ Se requiere reiniciar Windows para completar la operación.");
                break;
            case 1602:
                AppendLog("✗ Cancelado por el usuario.");
                break;
            case 1603:
                AppendLog("✗ Fallo fatal durante la instalación (revisa el log de Windows Installer).");
                break;
            default:
                AppendLog("✗ Código de salida desconocido.");
                break;
        }

        // Ver cómo queda tras la operación
        var st = GetStatus();
        AppendLog($"Estado actual: Installed={st.Installed}, Version={st.InstalledVersion ?? "—"}, ServiceState={st.ServiceState ?? "—"}");
    }

    private async Task RunExclusiveAsync(string action, Func<Task> work)
    {
        lock (_sync)
        {
            if (_busy) throw new InvalidOperationException("Ya hay una operación de ViGEmBus en curso.");
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
            Logger.Error($"[ViGEmBus] Error en {action}: {ex}");
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

    private async Task<(string Url, string Name)> FetchLatestInstallerUrlAsync(CancellationToken ct)
    {
        var apiUrl = $"https://api.github.com/repos/{GithubRepo}/releases/latest";
        using var resp = await _http.GetAsync(apiUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return (asset.GetProperty("browser_download_url").GetString()!, name);
        }
        throw new InvalidOperationException("No se encontró el instalador .exe en la última release de nefarius/ViGEmBus.");
    }

    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
    }

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
        Logger.Log($"[ViGEmBus] {line}");
    }

    // ── Helpers del registro ─────────────────────────────────────────────────

    private static (string? ImagePath, string? State) ReadService()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus");
            if (key is null) return (null, null);

            var imagePath = key.GetValue("ImagePath") as string;

            // Estado: lo leemos vía ServiceController para no depender del registro volátil
            string? state = null;
            try
            {
                using var sc = new System.ServiceProcess.ServiceController("ViGEmBus");
                state = sc.Status.ToString();
            }
            catch { /* el servicio puede existir sólo como driver on-demand y ServiceController fallar */ }

            return (imagePath, state);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// Busca la entrada de desinstalación de ViGEmBus bajo HKLM\...\Uninstall\* y
    /// devuelve (DisplayVersion, UninstallString).
    /// </summary>
    private static (string? Version, string? UninstallString) ReadUninstallEntry()
    {
        foreach (var root in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        })
        {
            try
            {
                using var parent = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
                if (parent is null) continue;
                foreach (var subName in parent.GetSubKeyNames())
                {
                    using var sub = parent.OpenSubKey(subName);
                    var display = sub?.GetValue("DisplayName") as string;
                    if (display is not null &&
                        display.Contains("ViGEmBus", StringComparison.OrdinalIgnoreCase))
                    {
                        return (
                            sub?.GetValue("DisplayVersion") as string,
                            sub?.GetValue("QuietUninstallString") as string
                                ?? sub?.GetValue("UninstallString") as string);
                    }
                }
            }
            catch { /* ignorar */ }
        }
        return (null, null);
    }

    private static string ResolveDriverPath(string imagePath)
    {
        // ImagePath puede venir como "\SystemRoot\System32\drivers\ViGEmBus.sys"
        // o "\??\C:\Windows\System32\drivers\ViGEmBus.sys". Normalizamos.
        var p = imagePath.Trim();
        if (p.StartsWith(@"\??\",        StringComparison.Ordinal))       p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", p[12..]);
        return p;
    }

    private static (string Exe, string Args) SplitCommandLine(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].TrimStart());
        }
        var sp = cmd.IndexOf(' ');
        return sp > 0 ? (cmd[..sp], cmd[(sp + 1)..]) : (cmd, "");
    }

    private static string AppendSilentFlags(string args)
    {
        // Forzamos modo silencioso al string registrado (que suele ser interactivo)
        var needs = new[] { "/quiet", "/uninstall", "/norestart" };
        var final = args ?? "";
        foreach (var flag in needs)
            if (!final.Contains(flag, StringComparison.OrdinalIgnoreCase))
                final = (final + " " + flag).Trim();
        return final;
    }
}
