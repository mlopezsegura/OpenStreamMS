using OpenStreamMS.Services;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenStreamMS.Services.Sandboxie;

/// <summary>
/// Estado actual de Sandboxie-Plus en la máquina.
/// </summary>
public record SandboxieStatus(
    /// <summary>true si SbieSvc está registrado y los binarios están localizables.</summary>
    bool    Installed,
    /// <summary>Versión instalada (de la entrada Uninstall). Null si no se detecta.</summary>
    string? InstalledVersion,
    /// <summary>Estado del servicio: "Running", "Stopped", null si no está registrado.</summary>
    string? ServiceState,
    /// <summary>Ruta absoluta a Start.exe detectado, o null.</summary>
    string? StartExePath,
    /// <summary>Carpeta de instalación detectada (InstallLocation del registro).</summary>
    string? InstallDir,
    /// <summary>Nombre del box configurado para Steam (p.ej. "OpenStream").</summary>
    string  BoxName,
    /// <summary>true si la última operación terminó con código que indica reboot pendiente.</summary>
    bool    RebootRequired,
    /// <summary>true si hay una operación en curso.</summary>
    bool    Busy,
    /// <summary>Última acción ejecutada.</summary>
    string? LastAction);

/// <summary>
/// Gestiona la instalación de <see href="https://github.com/sandboxie-plus/Sandboxie"/>
/// (Sandboxie-Plus, fork mantenido por DavidXanatos) desde la UI: descarga
/// la última release, ejecuta el instalador Inno Setup en modo silencioso y
/// detecta la ruta de <c>Start.exe</c> a partir del registro de Windows.
/// Patrón equivalente al de <c>ViGEmBusManager</c>: nada se embebe, todo se
/// descarga bajo demanda.
/// </summary>
public sealed class SandboxieManager
{
    private const string GithubRepo  = "sandboxie-plus/Sandboxie";
    private const string ServiceName = "SbieSvc";
    private const string UserAgent   = "OpenStreamMS";
    private const int    MaxLogLines = 500;

    private static readonly HttpClient _http = BuildHttpClient();

    private readonly object _sync = new();
    private readonly List<string> _log = new();
    private readonly string _boxName;
    private volatile bool _busy;
    private          string? _lastAction;
    private          bool    _rebootRequired;

    public SandboxieManager(string boxName = "OpenStream")
    {
        _boxName = boxName;
    }

    public bool   IsBusy  => _busy;
    public string BoxName => _boxName;

    public SandboxieStatus GetStatus()
    {
        var (installDir, displayVersion) = ReadUninstallEntry();
        var startExe = ResolveStartExe(installDir);
        var serviceState = ReadServiceState();

        var installed = serviceState is not null
                        && startExe is not null
                        && File.Exists(startExe);

        return new SandboxieStatus(
            Installed:        installed,
            InstalledVersion: displayVersion,
            ServiceState:     serviceState,
            StartExePath:     startExe,
            InstallDir:       installDir,
            BoxName:          _boxName,
            RebootRequired:   _rebootRequired,
            Busy:             _busy,
            LastAction:       _lastAction);
    }

    public string[] GetLog()
    {
        lock (_sync) return _log.ToArray();
    }

    /// <summary>
    /// Devuelve la ruta a <c>Start.exe</c> si Sandboxie está instalado, o null.
    /// Útil para que <see cref="Sunshine.SunshineConfigurator"/> sepa qué línea
    /// de comando inyectar en <c>apps.json</c>.
    /// </summary>
    public string? GetStartExePath() => GetStatus().StartExePath;

    public async Task InstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("install", async () =>
        {
            AppendLog("Buscando última release de sandboxie-plus/Sandboxie…");
            var (assetUrl, assetName) = await FetchLatestInstallerUrlAsync(ct);
            AppendLog($"Asset: {assetName}");

            var tempExe = Path.Combine(Path.GetTempPath(), $"OpenStreamMS_{assetName}");
            await DownloadAsync(assetUrl, tempExe, ct);
            AppendLog($"Descargado en: {tempExe}");

            // Inno Setup: /VERYSILENT no muestra UI; /SUPPRESSMSGBOXES quita los Yes/No
            // (SP no tiene EULA de aceptación obligatoria); /NORESTART evita el reboot
            // automático (devuelve 3010 si hace falta). Timeout duro de 10 min: si el
            // instalador no es Inno (p.ej. legacy NSIS), las flags no aplican y la UI
            // se queda esperando interacción para siempre.
            AppendLog("Ejecutando instalador silencioso (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART)…");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

            int exit;
            try
            {
                exit = await RunProcessAsync(tempExe,
                    "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    Path.GetDirectoryName(tempExe)!, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                AppendLog("✗ Timeout: el instalador no terminó en 10 minutos (probablemente espera UI). Mata el proceso manualmente y revisa que el asset es Sandboxie-Plus (Inno), no Classic (NSIS).");
                try { File.Delete(tempExe); } catch { }
                return;
            }

            try { File.Delete(tempExe); } catch { }

            InterpretExitCode(exit, installing: true);

            // Tras instalar, configurar el box OpenStream y forzar reload.
            try
            {
                if (await MergeOpenStreamBoxViaSbieIniAsync(ct))
                    await ReloadSandboxieConfigAsync(ct);
            }
            catch (Exception ex)
            {
                AppendLog($"Aviso: no se pudo mergear box [{_boxName}]: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Llama <c>Start.exe /reload_conf</c> para que SbieSvc relea el .ini sin
    /// reiniciar el servicio. Idempotente; falla silenciosamente si Start.exe
    /// no está disponible aún.
    /// </summary>
    public async Task ReloadSandboxieConfigAsync(CancellationToken ct = default)
    {
        var start = GetStartExePath();
        if (start is null)
        {
            AppendLog("reload_conf: Start.exe no localizable, se omite.");
            return;
        }
        AppendLog($"Recargando configuración: {start} /reload_conf");
        var exit = await RunProcessAsync(start, "/reload_conf",
            Path.GetDirectoryName(start)!, ct);
        AppendLog($"reload_conf exit code: {exit}");
    }

    /// <summary>
    /// Re-mergea el box <see cref="_boxName"/> en <c>Sandboxie.ini</c> y fuerza
    /// reload sin reinstalar nada. Útil si el merge anterior se hizo con encoding
    /// equivocado o el .ini fue editado manualmente.
    /// </summary>
    public async Task SyncBoxAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("sync-box", async () =>
        {
            if (!await MergeOpenStreamBoxViaSbieIniAsync(ct))
            {
                AppendLog("No se pudo configurar el box. ¿Está Sandboxie instalado?");
                return;
            }
            await ReloadSandboxieConfigAsync(ct);
        });
    }

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        await RunExclusiveAsync("uninstall", async () =>
        {
            var (_, _, uninstallCmd) = ReadFullUninstallEntry();

            if (string.IsNullOrWhiteSpace(uninstallCmd))
            {
                AppendLog("No se encontró comando de desinstalación. ¿Sandboxie está instalado?");
                return;
            }

            AppendLog($"Usando comando registrado: {uninstallCmd}");
            var (exe, args) = SplitCommandLine(uninstallCmd);

            // Inno Setup unins000.exe acepta /VERYSILENT igual que el instalador
            args = AppendSilentFlags(args);
            var exit = await RunProcessAsync(exe, args,
                Path.GetDirectoryName(exe) ?? ".", ct);
            InterpretExitCode(exit, installing: false);
        });
    }

    /// <summary>
    /// Línea de comando que Sunshine debe ejecutar para lanzar Steam dentro
    /// del sandbox. Sólo válida si <see cref="GetStartExePath"/> devuelve no-null.
    /// </summary>
    public string? BuildSandboxedSteamCommand(string steamUri)
    {
        if (string.IsNullOrWhiteSpace(steamUri))
            throw new ArgumentException("steamUri vacío", nameof(steamUri));

        var startExe = GetStartExePath();
        if (startExe is null) return null;

        return $"\"{startExe}\" /box:{_boxName} {steamUri}";
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
                    ? "✓ Sandboxie-Plus instalado correctamente."
                    : "✓ Sandboxie-Plus desinstalado correctamente.");
                break;
            case 3010:
                _rebootRequired = true;
                AppendLog("⚠ Se requiere reiniciar Windows para completar la operación.");
                break;
            case 1602:
                AppendLog("✗ Cancelado por el usuario.");
                break;
            case 1603:
                AppendLog("✗ Fallo fatal durante la instalación (revisa el log de Inno Setup).");
                break;
            default:
                AppendLog("✗ Código de salida desconocido.");
                break;
        }

        var st = GetStatus();
        AppendLog($"Estado actual: Installed={st.Installed}, Version={st.InstalledVersion ?? "—"}, " +
                  $"ServiceState={st.ServiceState ?? "—"}, StartExe={st.StartExePath ?? "—"}");
    }

    /// <summary>
    /// Crea/actualiza la sección [<see cref="_boxName"/>] usando <c>SbieIni.exe</c>,
    /// la herramienta oficial. Maneja encoding (UTF-16 LE), locking y reload del
    /// servicio en una sola operación atómica. Devuelve true si el box quedó
    /// configurado, false si falló.
    /// </summary>
    private async Task<bool> MergeOpenStreamBoxViaSbieIniAsync(CancellationToken ct)
    {
        var startExe = GetStartExePath();
        if (startExe is null)
        {
            AppendLog("Start.exe no localizable; no se puede invocar SbieIni.");
            return false;
        }
        var sbieIni = Path.Combine(Path.GetDirectoryName(startExe)!, "SbieIni.exe");
        if (!File.Exists(sbieIni))
        {
            AppendLog($"SbieIni.exe no encontrado en {sbieIni}. Fallback a edición de .ini.");
            return MergeOpenStreamBoxFile() is not null;
        }

        var workDir = Path.GetDirectoryName(sbieIni)!;
        AppendLog($"Configurando box [{_boxName}] vía SbieIni.exe");

        // Limpiar sección previa para evitar duplicados (set sobreescribe el primer
        // valor de la clave; append añade entradas multi-valor).
        await RunProcessAsync(sbieIni, $"delete {_boxName}", workDir, ct);

        var commands = new (string verb, string key, string value)[]
        {
            ("set",    "Enabled",                "y"),
            ("set",    "ConfigLevel",            "10"),
            ("set",    "AutoRecover",            "y"),
            ("set",    "BorderColor",            "#02C8FB,ttl"),
            ("append", "Template",               "OpenBluetooth"),
            ("append", "Template",               "AutoRecoverIgnore"),
            ("append", "Template",               "LingerProgram"),
            ("append", "Template",               "qWave"),
            ("append", "RecoverFolder",          "%Personal%"),
            ("append", "RecoverFolder",          "%Desktop%"),
            ("append", "RecoverFolder",          "%Downloads%"),
            ("append", "OpenFilePath",           "*\\Steam\\steamapps\\common\\*"),
            ("append", "OpenFilePath",           "%Desktop%"),
            ("append", "OpenFilePath",           "%Personal%"),
            ("append", "NormalFilePath",         "%SystemDrive%\\Program Files (x86)\\Steam\\"),
            ("set",    "BlockNetworkFiles",      "n"),
            ("set",    "BlockNetParam",          "n"),
            ("set",    "UseFileDeleteV2",        "y"),
            ("set",    "UseRegDeleteV2",         "y"),
            ("set",    "PreferExternalManifest", "y"),
        };

        foreach (var (verb, key, value) in commands)
        {
            var args = $"{verb} {_boxName} {key} \"{value}\"";
            var exit = await RunProcessAsync(sbieIni, args, workDir, ct);
            if (exit != 0)
                AppendLog($"⚠ SbieIni.exe {verb} {key} salió con código {exit}");
        }

        AppendLog($"Box [{_boxName}] configurado vía SbieIni.exe.");
        return true;
    }

    /// <summary>
    /// Fallback: edita Sandboxie.ini directamente. Sólo se usa si SbieIni.exe
    /// no está disponible (instalaciones muy viejas).
    /// </summary>
    private string? MergeOpenStreamBoxFile()
    {
        var iniPath = ResolveActiveIniPath();
        if (iniPath is null)
        {
            AppendLog("No se localizó Sandboxie.ini activo; merge cancelado.");
            return null;
        }
        AppendLog($"Mergeando box [{_boxName}] en: {iniPath}");

        var section = $"""
            [{_boxName}]
            Enabled=y
            ConfigLevel=10
            AutoRecover=y
            BorderColor=#02C8FB,ttl
            Template=OpenBluetooth
            Template=AutoRecoverIgnore
            Template=LingerProgram
            Template=qWave
            OpenFilePath=*\Steam\steamapps\common\*
            NormalFilePath=%SystemDrive%\Program Files (x86)\Steam\
            BlockNetworkFiles=n
            BlockNetParam=n
            UseFileDeleteV2=y
            UseRegDeleteV2=y
            PreferExternalManifest=y
            """;

        // Detectar encoding del existente para no corromperlo. Sandboxie escribe
        // UTF-16 LE con BOM por defecto en Plus moderno.
        Encoding writeEnc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        string existing = string.Empty;
        if (File.Exists(iniPath))
        {
            var bytes = File.ReadAllBytes(iniPath);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                writeEnc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                writeEnc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            else
                writeEnc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            existing = File.ReadAllText(iniPath);
        }

        existing = ReplaceOrAppendSection(existing, _boxName, section);
        File.WriteAllText(iniPath, existing, writeEnc);
        AppendLog($"Sandboxie.ini actualizado (sección [{_boxName}], encoding={writeEnc.WebName}).");
        return iniPath;
    }

    /// <summary>
    /// Localiza el <c>Sandboxie.ini</c> que el servicio realmente está usando.
    /// Orden de probe:
    /// <list type="number">
    ///   <item>Junto al ejecutable (instalación normal de Sandboxie-Plus).</item>
    ///   <item><c>%WINDIR%\Sandboxie.ini</c> (legacy / Classic).</item>
    /// </list>
    /// Si ninguno existe pero hay <c>InstallDir</c> conocido, devuelve el path
    /// junto al exe para que se cree allí (Plus crea el .ini en su carpeta de
    /// instalación tras la primera ejecución).
    /// </summary>
    private string? ResolveActiveIniPath()
    {
        var startExe = GetStartExePath();
        var installDir = startExe is not null ? Path.GetDirectoryName(startExe) : null;

        var candidates = new List<string>();
        if (installDir is not null)
            candidates.Add(Path.Combine(installDir, "Sandboxie.ini"));

        var winDir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        candidates.Add(Path.Combine(winDir, "Sandboxie.ini"));

        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is not null) return existing;

        // Ninguno existe: preferir junto al exe (donde Plus lo crea por defecto).
        return candidates.FirstOrDefault();
    }

    private static string ReplaceOrAppendSection(string ini, string section, string fullSectionText)
    {
        var header = $"[{section}]";
        var normalized = ini.Replace("\r\n", "\n");
        var lines = normalized.Split('\n').ToList();

        int start = -1, end = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.Equals(trimmed, header, StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var t = lines[j].TrimStart();
                    if (t.StartsWith('[') && t.TrimEnd().EndsWith(']'))
                    {
                        end = j;
                        break;
                    }
                }
                break;
            }
        }

        var rendered = fullSectionText.Trim() + "\r\n";

        if (start < 0)
        {
            var separator = ini.EndsWith("\n") || ini.Length == 0 ? "" : "\r\n";
            return ini + separator + "\r\n" + rendered;
        }

        var before = string.Join("\r\n", lines.Take(start));
        var after  = string.Join("\r\n", lines.Skip(end));
        var sep1   = before.Length > 0 ? "\r\n" : "";
        var sep2   = after.Length > 0 ? "\r\n" : "";
        return before + sep1 + rendered + sep2 + after;
    }

    private async Task RunExclusiveAsync(string action, Func<Task> work)
    {
        lock (_sync)
        {
            if (_busy) throw new InvalidOperationException("Ya hay una operación de Sandboxie en curso.");
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
            Logger.Error($"[Sandboxie] Error en {action}: {ex}");
        }
        finally
        {
            AppendLog($"--- Fin: {action} ({DateTime.Now:HH:mm:ss}) ---");
            _busy = false;
        }
    }

    private static HttpClient BuildHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>
    /// Busca en la última release el instalador Inno Setup de <b>Sandboxie-Plus</b>
    /// para x64. Patrón habitual: <c>Sandboxie-Plus-x64-Install-vX.Y.Z.exe</c>.
    /// Se descartan explícitamente las builds <i>Classic</i> (legacy NSIS, no
    /// soporta <c>/VERYSILENT</c> y bloquea la instalación) y las ARM64.
    /// </summary>
    private async Task<(string Url, string Name)> FetchLatestInstallerUrlAsync(CancellationToken ct)
    {
        var apiUrl = $"https://api.github.com/repos/{GithubRepo}/releases/latest";
        using var resp = await _http.GetAsync(apiUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

        var candidates = new List<(string Name, string Url)>();
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url  = asset.GetProperty("browser_download_url").GetString() ?? "";

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("Classic",   StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("ARM64",     StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("ARM",       StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Plus",     StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("Plus",     StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("x64",      StringComparison.OrdinalIgnoreCase)) continue;

            candidates.Add((name, url));
        }

        // Prioridad: el que diga "Install" (instalador completo) sobre el
        // resto. Si hay varios "Install", coge el de menor longitud para
        // evitar variantes como "Sandboxie-Plus-x64-Install-vXXX-debug.exe".
        var preferred = candidates
            .Where(c => c.Name.Contains("Install", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name.Length)
            .FirstOrDefault();

        if (preferred.Url is not null)
            return (preferred.Url, preferred.Name);

        if (candidates.Count > 0)
            return (candidates[0].Url, candidates[0].Name);

        throw new InvalidOperationException(
            $"No se encontró instalador Sandboxie-Plus x64 en la última release de {GithubRepo}.");
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
        Logger.Log($"[Sandboxie] {line}");
    }

    // ── Helpers de detección (registro / servicio) ───────────────────────────

    private static string? ReadServiceState()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceName);
            return sc.Status.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Devuelve (InstallLocation, DisplayVersion) buscando en HKLM\...\Uninstall\*
    /// la entrada cuyo DisplayName contiene "Sandboxie".
    /// </summary>
    private static (string? InstallDir, string? Version) ReadUninstallEntry()
    {
        var (installDir, version, _) = ReadFullUninstallEntry();
        return (installDir, version);
    }

    private static (string? InstallDir, string? Version, string? UninstallString) ReadFullUninstallEntry()
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
                        display.Contains("Sandboxie", StringComparison.OrdinalIgnoreCase))
                    {
                        return (
                            sub?.GetValue("InstallLocation")  as string,
                            sub?.GetValue("DisplayVersion")   as string,
                            sub?.GetValue("QuietUninstallString") as string
                                ?? sub?.GetValue("UninstallString") as string);
                    }
                }
            }
            catch { /* ignorar */ }
        }
        return (null, null, null);
    }

    /// <summary>
    /// Resuelve la ruta absoluta a <c>Start.exe</c> dentro del directorio de
    /// instalación. Si <paramref name="installDir"/> es null intenta rutas
    /// estándar de Sandboxie-Plus.
    /// </summary>
    private static string? ResolveStartExe(string? installDir)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(installDir))
            candidates.Add(Path.Combine(installDir, "Start.exe"));

        var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        candidates.Add(Path.Combine(pf,    "Sandboxie-Plus", "Start.exe"));
        candidates.Add(Path.Combine(pfx86, "Sandboxie-Plus", "Start.exe"));
        candidates.Add(Path.Combine(pf,    "Sandboxie",      "Start.exe"));
        candidates.Add(Path.Combine(pfx86, "Sandboxie",      "Start.exe"));

        return candidates.FirstOrDefault(File.Exists);
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
        var needs = new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" };
        var final = args ?? "";
        foreach (var flag in needs)
            if (!final.Contains(flag, StringComparison.OrdinalIgnoreCase))
                final = (final + " " + flag).Trim();
        return final;
    }
}
