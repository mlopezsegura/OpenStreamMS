using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenStreamMS.Services.Drivers;

/// <summary>
/// Estado actual de un driver en la máquina.
/// </summary>
public record DriverStatus(
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
/// Gestiona la instalación de un driver firmado de nefarius (ViGEmBus, HidHide):
/// usa el instalador incluido con OpenStreamMS (<c>drivers\&lt;prefijo&gt;*.exe</c>, lo
/// descarga build-installer.bat) o, si no está, descarga la última release de GitHub,
/// y lo ejecuta en modo silencioso.
/// </summary>
public abstract class NefariusDriverManager
{
    private const string UserAgent   = "OpenStreamMS";
    private const int    MaxLogLines = 500;

    private static readonly HttpClient _http = BuildHttpClient();

    private readonly object _sync = new();
    private readonly List<string> _log = new();
    private volatile bool   _busy;
    private          string? _lastAction;
    private          bool    _rebootRequired;

    /// <summary>Nombre para logs y mensajes ("ViGEmBus").</summary>
    public abstract string Name { get; }
    /// <summary>Repositorio de GitHub con las releases ("nefarius/ViGEmBus").</summary>
    protected abstract string GithubRepo { get; }
    /// <summary>Nombre del servicio del driver.</summary>
    protected abstract string ServiceName { get; }
    /// <summary>Texto que contiene su DisplayName en Agregar o quitar programas.</summary>
    protected abstract string DisplayNameMatch { get; }
    /// <summary>Argumentos de instalación silenciosa del .exe de la release.</summary>
    protected abstract string InstallArgs { get; }
    /// <summary>Prefijo del instalador incluido en <c>drivers\</c> y del asset de la release.</summary>
    protected abstract string InstallerPrefix { get; }

    // ── API pública ──────────────────────────────────────────────────────────

    public bool IsBusy => _busy;

    public DriverStatus GetStatus()
    {
        var (imagePath, state) = ReadService();
        var (displayVersion, _) = ReadUninstallEntry();

        // "Installed" = el servicio existe y el ImagePath apunta a un archivo real.
        // Algunos sistemas cargan el driver bajo demanda, así que "Stopped" también
        // cuenta como instalado mientras el .sys exista.
        var driverFileOk = imagePath is not null &&
                           File.Exists(ResolveDriverPath(imagePath));

        return new DriverStatus(
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
            string installer;
            bool   downloaded = false;
            if (BundledInstaller() is { } bundled)
            {
                installer = bundled;
                AppendLog($"Usando el instalador incluido: {installer}");
            }
            else
            {
                AppendLog($"Descargando última release de {GithubRepo}…");
                var (assetUrl, assetName) = await FetchLatestInstallerUrlAsync(ct);
                AppendLog($"Asset: {assetName}");

                installer = Path.Combine(Path.GetTempPath(), $"OpenStreamMS_{assetName}");
                await DownloadAsync(assetUrl, installer, ct);
                downloaded = true;
                AppendLog($"Descargado en: {installer}");
            }

            AppendLog($"Ejecutando instalador silencioso ({InstallArgs})…");
            var exit = await RunProcessAsync(installer, InstallArgs, Path.GetDirectoryName(installer)!, ct);
            if (downloaded) try { File.Delete(installer); } catch { }

            InterpretExitCode(exit, installing: true);
        });
    }

    /// <summary>Instala el driver si no lo está. Para el registro del servicio (<c>--install</c>).</summary>
    public void EnsureInstalled()
    {
        if (GetStatus().Installed) return;
        InstallAsync().GetAwaiter().GetResult();
    }

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("uninstall", async () =>
        {
            var (_, uninstallCmd) = ReadUninstallEntry();
            if (string.IsNullOrWhiteSpace(uninstallCmd))
            {
                AppendLog($"✗ No se encontró el comando de desinstalación de {Name} en el registro.");
                return;
            }

            AppendLog($"Usando comando de desinstalación registrado: {uninstallCmd}");
            var (exe, args) = SplitCommandLine(uninstallCmd);
            args = AppendSilentFlags(exe, args);
            var exit = await RunProcessAsync(exe, args, Path.GetDirectoryName(exe) is { Length: > 0 } d ? d : ".", ct);
            InterpretExitCode(exit, installing: false);
        });
    }

    // ── Implementación interna ───────────────────────────────────────────────

    private string? BundledInstaller()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "drivers");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, $"{InstallerPrefix}*.exe").OrderDescending().FirstOrDefault()
            : null;
    }

    private void InterpretExitCode(int exit, bool installing)
    {
        AppendLog($"Instalador salió con código {exit}.");
        switch (exit)
        {
            case 0:
                _rebootRequired = false;
                AppendLog(installing
                    ? $"✓ {Name} instalado correctamente."
                    : $"✓ {Name} desinstalado correctamente.");
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
            if (_busy) throw new InvalidOperationException($"Ya hay una operación de {Name} en curso.");
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
            Logger.Error($"[{Name}] Error en {action}: {ex}");
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
            if (name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return (asset.GetProperty("browser_download_url").GetString()!, name);
        }
        throw new InvalidOperationException($"No se encontró el instalador .exe en la última release de {GithubRepo}.");
    }

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
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
        Logger.Log($"[{Name}] {line}");
    }

    // ── Helpers del registro ─────────────────────────────────────────────────

    private (string? ImagePath, string? State) ReadService()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            if (key is null) return (null, null);

            var imagePath = key.GetValue("ImagePath") as string;

            // Estado: lo leemos vía ServiceController para no depender del registro volátil
            string? state = null;
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(ServiceName);
                state = sc.Status.ToString();
            }
            catch { /* el servicio puede existir sólo como driver on-demand y ServiceController fallar */ }

            return (imagePath, state);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// Busca la entrada de desinstalación del driver bajo HKLM\...\Uninstall\* y
    /// devuelve (DisplayVersion, UninstallString).
    /// </summary>
    private (string? Version, string? UninstallString) ReadUninstallEntry()
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
                        display.Contains(DisplayNameMatch, StringComparison.OrdinalIgnoreCase))
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
        // ImagePath puede venir como "\SystemRoot\System32\drivers\X.sys"
        // o "\??\C:\Windows\System32\drivers\X.sys". Normalizamos.
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

    /// <summary>
    /// Fuerza el modo silencioso en el comando registrado (que suele ser interactivo):
    /// <c>msiexec /X{...}</c> usa /qn; un bundle (WiX Burn), /quiet /uninstall.
    /// </summary>
    private static string AppendSilentFlags(string exe, string args)
    {
        bool msiexec = Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
        var needs = msiexec ? new[] { "/qn", "/norestart" } : new[] { "/quiet", "/uninstall", "/norestart" };
        var final = args ?? "";
        foreach (var flag in needs)
            if (!final.Contains(flag, StringComparison.OrdinalIgnoreCase))
                final = (final + " " + flag).Trim();
        return final;
    }
}

/// <summary>
/// <see href="https://github.com/nefarius/ViGEmBus"/>: driver de mando virtual,
/// dependencia de Sunshine/Moonlight.
/// </summary>
public sealed class ViGEmBusManager : NefariusDriverManager
{
    public override string Name => "ViGEmBus";
    protected override string GithubRepo       => "nefarius/ViGEmBus";
    protected override string ServiceName      => "ViGEmBus";
    protected override string DisplayNameMatch => "ViGEm";       // "ViGEm Bus Driver"
    protected override string InstallArgs      => "/quiet /install /norestart";   // bundle WiX Burn
    protected override string InstallerPrefix  => "ViGEmBus_";
}

/// <summary>
/// <see href="https://github.com/nefarius/HidHide"/>: filtro que oculta mandos a otras
/// sesiones de Windows (lo usa <see cref="Gamepad.GamepadSessionIsolation"/>).
/// </summary>
public sealed class HidHideManager : NefariusDriverManager
{
    public override string Name => "HidHide";
    protected override string GithubRepo       => "nefarius/HidHide";
    protected override string ServiceName      => "HidHide";
    protected override string DisplayNameMatch => "HidHide";
    protected override string InstallArgs      => "/exenoui /qn /norestart";      // bootstrapper Advanced Installer
    protected override string InstallerPrefix  => "HidHide_";
}
