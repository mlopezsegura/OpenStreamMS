using OpenStreamMS.Core.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenStreamMS.Services.Sunshine
{
    public enum SunshineRunStatus
    {
        Running,
        Started,
        UnexpectedExit,
        LaunchFailed
    }

    public sealed record SunshineRunResult(
        SunshineRunStatus Status,
        int Pid = 0,
        string? Message = null);

    public class SunshineManager
    {
        private readonly string _sunshineExe;
        private readonly bool   _vddEnabled;
        private readonly SunshineConfigurator.SunshineConfig _cfg;
        private readonly SunshineConfigurator.SunshineCredentials? _creds;
        private readonly Dictionary<string, string>? _envOverrides;
        private readonly string? _statePersistPath;
        private readonly string? _stateAliasPath;
        private FileSystemWatcher? _stateWatcher;
        private System.Threading.Timer? _statePollTimer;
        private DateTime _lastMirrorUtc = DateTime.MinValue;
        private readonly object _mirrorLock = new();
        private int  _sunshinePid = 0;
        private bool _configured  = false;
        private DateTime _lastStartLocal = DateTime.MinValue;

        public SunshineManager(string sunshineExePath,
                               bool vddEnabled,
                               SunshineConfigurator.SunshineConfig cfg,
                               SunshineConfigurator.SunshineCredentials? credentials = null,
                               Dictionary<string, string>? envOverrides = null,
                               string? statePersistPath = null,
                               string? stateAliasPath = null)
        {
            _sunshineExe           = sunshineExePath;
            _vddEnabled            = vddEnabled;
            _cfg                   = cfg;
            _creds                 = credentials;
            _envOverrides          = envOverrides;
            _statePersistPath      = statePersistPath;
            _stateAliasPath        = stateAliasPath;
        }

        private string ActiveStatePath =>
            Path.Combine(Path.GetDirectoryName(_sunshineExe)!, "config", "sunshine_state.json");

        /// <summary>
        /// Lanza sunshine.exe en la sesión RDP indicada si no está ya corriendo.
        /// Si VDD está habilitado, parchea apps.json antes del primer arranque.
        /// </summary>
        public SunshineRunResult EnsureRunning(uint sessionId)
        {
            // Camino caliente: el monitor pasa por aqui cada 5 s por sesion. Sin log:
            // antes escribia una linea por tick (~17k/dia) en disco y en el log de sesion.
            if (_sunshinePid > 0 && IsProcessAlive(_sunshinePid))
                return new SunshineRunResult(SunshineRunStatus.Running, _sunshinePid);

            if (_sunshinePid > 0)
            {
                var deadPid = _sunshinePid;
                _sunshinePid = 0;

                var crash = SunshineCrashDetector.TryFindRecentFault(_sunshineExe, deadPid, _lastStartLocal);
                var message = crash ??
                    $"Sunshine terminó inesperadamente (PID={deadPid}); no se encontró un evento Application Error reciente.";

                Logger.Log($"[Sunshine] {message}");
                return new SunshineRunResult(SunshineRunStatus.UnexpectedExit, 0, message);
            }

            if (!File.Exists(_sunshineExe))
            {
                var message = $"No se encontró sunshine.exe en: {_sunshineExe}";
                Logger.Error($"[Sunshine] {message}");
                return new SunshineRunResult(SunshineRunStatus.LaunchFailed, 0, message);
            }

            if (!_configured)
            {
                // Restaurar estado (incluye named_devices = dispositivos emparejados) ANTES
                // de tocar credenciales, así --creds opera sobre el JSON con los pareos
                // intactos y solo reescribe username/salt/password.
                RestoreStateBackup();

                try
                {
                    SunshineConfigurator.WriteConfig(_sunshineExe, _cfg);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Sunshine] No se pudo actualizar sunshine.conf: {ex.Message}");
                }

                if (_creds is not null)
                {
                    try
                    {
                        ProvisionCredentialsViaCli(_sunshineExe, _creds);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[Sunshine] No se pudo provisionar credenciales: {ex.Message}");
                    }
                }

                if (_vddEnabled)
                {
                    try
                    {
                        SunshineConfigurator.EnableVirtualDisplay(_sunshineExe);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[Sunshine] No se pudo configurar VDD en apps.json: {ex.Message}");
                    }
                }

                try
                {
                    SunshineConfigurator.ConfigureAppEnvironment(_sunshineExe, _envOverrides);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Sunshine] No se pudo configurar entorno aislado en apps.json: {ex.Message}");
                }

                try
                {
                    SunshineConfigurator.ConfigureSteamEntry(_sunshineExe);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Sunshine] No se pudo configurar la entrada de Steam en apps.json: {ex.Message}");
                }

                try
                {
                    SunshineConfigurator.ConfigurePowerEntries(_sunshineExe);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Sunshine] No se pudieron configurar las entradas Reboot/Power Off en apps.json: {ex.Message}");
                }

                _configured = true;
            }

            // Pasar el .conf como argv[1] fuerza a Sunshine a no usar %APPDATA% como fuente
            // de configuración/estado. Dentro del conf, file_state/file_apps/pkey/cert son
            // rutas absolutas a la carpeta de la sesión, así la instancia queda totalmente
            // aislada y persistente entre reinicios del servicio.
            var configPath = Path.Combine(Path.GetDirectoryName(_sunshineExe)!, "config", "sunshine.conf");
            var argQuoted  = $"\"{configPath}\"";

            // Marca el final del log ANTES de lanzar: la verificacion del encoder solo
            // mira las lineas del arranque nuevo (Sunshine sondea encoders nada mas
            // arrancar y escribe el veredicto al instante).
            var logMark = CaptureLogMark();

            var result = StartSunshineProcess(sessionId, argQuoted);

            if (result.Status == SunshineRunStatus.Started && result.Pid > 0)
            {
                // Si el sondeo cayo a encode por software (libx264) — porque la sesion
                // estaba bloqueada en ese instante — relanza UNA vez con la sesion ya
                // desbloqueada para forzar la GPU (amdvce/h264_amf). Evita el 4K diente
                // de sierra 60→8 por CPU.
                result = VerifyHardwareEncoderOrRelaunch(sessionId, argQuoted, logMark, result);
                if (result.Pid > 0)
                    StartStateMirror();
            }

            return result;
        }

        private string SunshineLogPath =>
            Path.Combine(Path.GetDirectoryName(_sunshineExe)!, "config", "sunshine.log");

        /// <summary>
        /// Posicion del final del log + sus primeros bytes. Sunshine 2026.9+ rota el log
        /// al arrancar (sunshine.log → .1): si la cabecera cambia, el fichero es nuevo y
        /// hay que leerlo desde 0 (solo con la longitud se saltaria el inicio del log
        /// nuevo si ya supera el tamaño del anterior, que es donde sale el encoder).
        /// </summary>
        private readonly record struct LogMark(long Offset, byte[] Head);

        private const int LogHeadBytes = 256;

        // Sunshine 2026.9+ tarda ~16 s desde el arranque hasta sondear encoders (display
        // manager + enumeracion de pantallas); con 12 s el veredicto salia siempre Unknown.
        private const int EncoderVerdictTimeoutMs = 30_000;

        private LogMark CaptureLogMark()
        {
            try
            {
                if (!File.Exists(SunshineLogPath)) return new LogMark(0, []);
                using var fs = new FileStream(SunshineLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new LogMark(fs.Length, ReadHead(fs, LogHeadBytes));
            }
            catch { return new LogMark(0, []); }
        }

        private static byte[] ReadHead(FileStream fs, int count)
        {
            var buf = new byte[count];
            fs.Seek(0, SeekOrigin.Begin);
            int n = fs.ReadAtLeast(buf, count, throwOnEndOfStream: false);
            return n == count ? buf : buf[..n];
        }

        /// <summary>
        /// Lanza sunshine.exe en la sesión RDP (SYSTEM → ProcessInSession; debug → directo).
        /// No arranca el state mirror: lo hace el llamante una vez fijado el PID definitivo
        /// para no duplicarlo si hay que relanzar.
        /// </summary>
        private SunshineRunResult StartSunshineProcess(uint sessionId, string argQuoted)
        {
            Logger.Log($"[Sunshine] Lanzando en sesión {sessionId}: {_sunshineExe} {argQuoted}");
            try
            {
                _lastStartLocal = DateTime.Now;

                if (WindowsIdentity.GetCurrent().IsSystem)
                {
                    // Producción (SYSTEM): lanzar en la sesión RDP usando ProcessInSession.
                    // Prioridad Normal: Sunshine ya eleva sus hilos de captura/encode por su
                    // cuenta; AboveNormal en todo el proceso coincidio con microsaltos.
                    _sunshinePid = new ProcessInSession().LaunchInSession(
                        sessionId, _sunshineExe, argQuoted, _envOverrides);
                }
                else
                {
                    // Debug (no SYSTEM): WTSQueryUserToken no está disponible, lanzar directo
                    var psi = new ProcessStartInfo(_sunshineExe, argQuoted)
                    {
                        WorkingDirectory = Path.GetDirectoryName(_sunshineExe),
                        UseShellExecute = _envOverrides is not { Count: > 0 }
                    };

                    if (_envOverrides is { Count: > 0 })
                    {
                        foreach (var (key, value) in _envOverrides)
                            psi.Environment[key] = value;
                    }

                    var p = Process.Start(psi);
                    _sunshinePid = p?.Id ?? 0;
                }

                if (_sunshinePid > 0)
                    Logger.Info($"[Sunshine] Iniciado con PID {_sunshinePid}");
                else
                    Logger.Error("[Sunshine] No se pudo lanzar");

                return _sunshinePid > 0
                    ? new SunshineRunResult(SunshineRunStatus.Started, _sunshinePid)
                    : new SunshineRunResult(SunshineRunStatus.LaunchFailed, 0, "No se pudo lanzar sunshine.exe.");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Sunshine] Error al lanzar: {ex.Message}");
                _sunshinePid = 0;
                return new SunshineRunResult(SunshineRunStatus.LaunchFailed, 0, ex.Message);
            }
        }

        private enum EncoderVerdict { Unknown, Hardware, Software }

        /// <summary>
        /// Comprueba el veredicto del sondeo de encoders en el log. Si fue SOFTWARE
        /// (libx264 / "Couldn't find any working encoder"), mata y relanza Sunshine UNA
        /// vez (la sesión ya está desbloqueada → debería encontrar h264_amf). Si fue
        /// hardware o no se pudo determinar, deja el proceso como está.
        /// </summary>
        private SunshineRunResult VerifyHardwareEncoderOrRelaunch(
            uint sessionId, string argQuoted, LogMark logMark, SunshineRunResult current)
        {
            var verdict = WaitForEncoderVerdict(logMark, timeoutMs: EncoderVerdictTimeoutMs);

            if (verdict == EncoderVerdict.Hardware)
            {
                Logger.Log("[Sunshine] Encoder hardware confirmado (amdvce/h264_amf).");
                return current;
            }
            if (verdict == EncoderVerdict.Unknown)
            {
                Logger.Warning("[Sunshine] No se pudo confirmar el encoder en el log; se deja como está.");
                return current;
            }

            Logger.Warning("[Sunshine] El sondeo cayó a encode SOFTWARE (libx264 → 4K diente de sierra). " +
                           "Relanzando una vez con la sesión ya desbloqueada para forzar GPU...");
            try { if (_sunshinePid > 0) KillProcessTree(_sunshinePid); } catch { }
            _sunshinePid = 0;

            var retryMark = CaptureLogMark();
            var retry = StartSunshineProcess(sessionId, argQuoted);
            if (retry.Status == SunshineRunStatus.Started && retry.Pid > 0)
            {
                var v2 = WaitForEncoderVerdict(retryMark, timeoutMs: EncoderVerdictTimeoutMs);
                if (v2 == EncoderVerdict.Hardware)
                    Logger.Log("[Sunshine] Tras relanzar: encoder hardware confirmado.");
                else
                    Logger.Warning($"[Sunshine] Tras relanzar el encoder sigue sin confirmarse como hardware (verdict={v2}).");
            }
            return retry;
        }

        /// <summary>
        /// Lee las líneas nuevas del sunshine.log (desde <paramref name="from"/>)
        /// hasta detectar si el sondeo eligió hardware (Creating encoder [*_amf]) o
        /// software (libx264 / "Couldn't find any working encoder"), o agotar el timeout.
        /// </summary>
        private EncoderVerdict WaitForEncoderVerdict(LogMark from, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                string chunk = ReadLogFrom(SunshineLogPath, from);
                if (chunk.Length > 0)
                {
                    if (chunk.Contains("Creating encoder [h264_amf]") ||
                        chunk.Contains("Creating encoder [hevc_amf]") ||
                        chunk.Contains("Creating encoder [av1_amf]"))
                        return EncoderVerdict.Hardware;

                    if (chunk.Contains("Couldn't find any working encoder") ||
                        chunk.Contains("[libx264 @"))
                        return EncoderVerdict.Software;
                }
                Thread.Sleep(500);
            }
            return EncoderVerdict.Unknown;
        }

        private static string ReadLogFrom(string path, LogMark from)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long offset = from.Offset;
                if (offset > fs.Length ||
                    (offset > 0 && !ReadHead(fs, from.Head.Length).AsSpan().SequenceEqual(from.Head)))
                    offset = 0;   // log truncado/rotado: fichero nuevo
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private static void KillProcessTree(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch { /* ya muerto o sin acceso */ }
        }

        public void Stop()
        {
            if (_sunshinePid > 0 && IsProcessAlive(_sunshinePid))
                RequestGracefulStateFlushAndExit(_sunshinePid);

            // Volcado final del estado al backup antes de matar el proceso, por si Sunshine
            // tenía cambios sin flushear que el watcher aún no había mirrored.
            MirrorStateToBackup(force: true);
            StopStateMirror();

            if (_sunshinePid <= 0) return;
            try
            {
                using var process = Process.GetProcessById(_sunshinePid);
                if (!process.HasExited)
                {
                    process.Kill();
                    Logger.Log($"[Sunshine] Proceso {_sunshinePid} detenido");
                }
            }
            catch { /* ya terminó */ }
            _sunshinePid = 0;
            _lastStartLocal = DateTime.MinValue;
        }

        /// <summary>
        /// Fuerza un mirror del <c>sunshine_state.json</c> activo al backup persistente
        /// sin tocar el proceso.
        /// </summary>
        public void FlushStateMirror()
        {
            MirrorStateToBackup(force: true);
        }

        private void RequestGracefulStateFlushAndExit(int pid)
        {
            if (_creds is null || _cfg.StreamPort <= 0)
                return;

            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    AllowAutoRedirect = false,
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"https://127.0.0.1:{_cfg.StreamPort + 1}/api/restart");

                var basic = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{_creds.Username}:{_creds.PlainPassword}"));
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                Logger.Log($"[Sunshine] /api/restart solicitado antes de parar (HTTP {(int)resp.StatusCode}) para flushear state.");

                if (WaitForProcessExit(pid, TimeSpan.FromSeconds(3)))
                    Logger.Log($"[Sunshine] Proceso {pid} salió limpiamente tras flush de state.");
                else
                    Logger.Warning($"[Sunshine] El proceso {pid} no salió tras /api/restart; se usará kill como fallback.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Sunshine] No se pudo solicitar flush limpio antes de parar: {ex.Message}");
            }
        }

        private static bool WaitForProcessExit(int pid, TimeSpan timeout)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.WaitForExit(timeout);
            }
            catch
            {
                return true;
            }
        }

        static bool IsProcessAlive(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }
        }

        /// <summary>
        /// Provisiona usuario/contraseña del panel ejecutando <c>sunshine.exe "&lt;conf&gt;" --creds user pass</c>.
        /// Sunshine escribe el archivo (en SU formato y en SU path resuelto) y termina; así garantizamos
        /// que el state file que Sunshine leerá al arrancar de verdad contiene exactamente estas credenciales,
        /// sin depender de que nuestra escritura JSON coincida con su parser.
        /// <para>
        /// Sunshine &gt;=2026.x al ejecutar <c>--creds</c> reescribe TODO el state file con solo
        /// username/salt/password, eliminando <c>root.named_devices</c> (pareos Moonlight) y
        /// <c>root.uniqueid</c>. Capturamos el subarbol <c>root</c> antes y lo restauramos despues,
        /// para que los dispositivos emparejados sobrevivan a cada arranque de la sesion.
        /// </para>
        /// </summary>
        private static void ProvisionCredentialsViaCli(
            string sunshineExe, SunshineConfigurator.SunshineCredentials creds)
        {
            var configPath = Path.Combine(Path.GetDirectoryName(sunshineExe)!, "config", "sunshine.conf");
            var stateFile  = Path.Combine(Path.GetDirectoryName(sunshineExe)!, "config", "sunshine_state.json");

            JsonNode? preservedRoot = CaptureStateRoot(stateFile);

            var psi = new ProcessStartInfo
            {
                FileName               = sunshineExe,
                WorkingDirectory       = Path.GetDirectoryName(sunshineExe),
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("--creds");
            psi.ArgumentList.Add(creds.Username);
            psi.ArgumentList.Add(creds.PlainPassword);

            using var p = Process.Start(psi)
                ?? throw new Exception("No se pudo lanzar sunshine.exe --creds");

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();

            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("sunshine.exe --creds no terminó en 15 s");
            }

            if (!string.IsNullOrWhiteSpace(stdout))
                Logger.Log($"[Sunshine] --creds stdout: {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr))
                Logger.Log($"[Sunshine] --creds stderr: {stderr.Trim()}");

            if (p.ExitCode != 0)
                throw new Exception($"sunshine.exe --creds devolvió exit code {p.ExitCode}");

            RestoreStateRoot(stateFile, preservedRoot);

            Logger.Log($"[Sunshine] Credenciales provisionadas vía --creds (user={creds.Username})");
        }

        /// <summary>
        /// Lee <c>sunshine_state.json</c> y devuelve un clon del subarbol <c>root</c>
        /// (uniqueid + named_devices). Null si no existe o no se puede parsear.
        /// </summary>
        private static JsonNode? CaptureStateRoot(string stateFile)
        {
            try
            {
                if (!File.Exists(stateFile)) return null;
                if (JsonNode.Parse(File.ReadAllText(stateFile)) is not JsonObject root) return null;
                return root["root"]?.DeepClone();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reinyecta el subarbol <c>root</c> capturado antes de <c>--creds</c> en el
        /// state file que Sunshine acaba de reescribir. Si el snapshot es null no toca
        /// nada (primer arranque sin pareos previos).
        /// </summary>
        private static void RestoreStateRoot(string stateFile, JsonNode? preservedRoot)
        {
            if (preservedRoot is null) return;

            try
            {
                if (!File.Exists(stateFile))
                {
                    Logger.Warning("[Sunshine] sunshine_state.json no existe tras --creds; no se restaura root.");
                    return;
                }

                if (JsonNode.Parse(File.ReadAllText(stateFile)) is not JsonObject state)
                {
                    Logger.Warning("[Sunshine] sunshine_state.json no es JSON valido tras --creds; no se restaura root.");
                    return;
                }

                // Restauramos siempre que el snapshot capturado antes de --creds tenga
                // dispositivos: --creds puede escribir root con named_devices ausente, ""
                // (Boost.PropertyTree array vacío) o array vacío [], y en cualquiera de
                // esos casos hay que reinyectar el snapshot para no perder pareos.
                int preservedCount =
                    (preservedRoot is JsonObject prevObj && prevObj["named_devices"] is JsonArray prevArr)
                        ? prevArr.Count
                        : 0;

                int newCount =
                    (state["root"] is JsonObject newRoot && newRoot["named_devices"] is JsonArray newArr)
                        ? newArr.Count
                        : 0;

                // Si --creds dejó al menos los mismos devices que teníamos, respetar.
                if (preservedCount == 0 || newCount >= preservedCount)
                {
                    Logger.Log($"[Sunshine] sunshine_state.json: root tras --creds OK (preserved={preservedCount}, new={newCount}); sin restaurar.");
                    return;
                }

                state["root"] = preservedRoot.DeepClone();
                File.WriteAllText(
                    stateFile,
                    state.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                Logger.Log($"[Sunshine] sunshine_state.json: root restaurado tras --creds (named_devices={preservedCount}, eran={newCount}).");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Sunshine] No se pudo restaurar root tras --creds: {ex.Message}");
            }
        }

        /// <summary>
        /// Si existe un <c>sunshine_state.json</c> en la ubicación persistente fuera del
        /// instance dir, lo copia a la carpeta config de Sunshine antes de arrancar. Así
        /// los pareos Moonlight (<c>named_devices</c>) y demás estado sobreviven a:
        /// reinicios del servicio, recreación de la sesión RDP y borrados accidentales del
        /// instance dir.
        /// </summary>
        private void RestoreStateBackup()
        {
            try
            {
                var backup = GetBestRestoreCandidate();
                if (backup is null)
                {
                    Logger.Log("[Sunshine] No hay backup util de sunshine_state.json para restaurar.");
                    return;
                }

                var activePath = ActiveStatePath;
                Directory.CreateDirectory(Path.GetDirectoryName(activePath)!);
                var active = ReadStateSnapshot(activePath);

                // Compare useful state instead of file size: credentials and paired
                // devices are what must survive restarts and accidental recreation.
                if (!ShouldRestoreState(active, backup))
                {
                    Logger.Log($"[Sunshine] sunshine_state.json activo conserva estado suficiente (devices={active.NamedDeviceCount}, creds={active.HasCredentials}); no se restaura.");
                    return;
                }

                File.Copy(backup.Path, activePath, overwrite: true);
                Logger.Log($"[Sunshine] sunshine_state.json restaurado desde backup (devices={backup.NamedDeviceCount}, creds={backup.HasCredentials}): {backup.Path} -> {activePath}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Sunshine] No se pudo restaurar sunshine_state.json: {ex.Message}");
            }
        }

        private StateSnapshot? GetBestRestoreCandidate()
        {
            var explicitCandidates = GetStatePathCandidates().ToList();

            var candidates = explicitCandidates
                .Select(ReadStateSnapshot)
                .Where(IsUsableStateBackup)
                .ToList();

            if (candidates.Count == 0)
            {
                // Compatibility path for sessions created before the stable alias
                // existed: recover only when there is a single unambiguous old backup.
                var legacy = GetLegacySingleStateBackup(explicitCandidates);
                if (legacy is not null && IsUsableStateBackup(legacy))
                    candidates.Add(legacy);
            }

            return candidates
                .OrderByDescending(c => c.HasNamedDevicesKey)
                .ThenByDescending(c => c.LastWriteUtc)
                .ThenByDescending(c => IsPrimaryStatePath(c.Path))
                .ThenByDescending(c => c.NamedDeviceCount)
                .ThenByDescending(c => c.HasCredentials)
                .FirstOrDefault();
        }

        private IEnumerable<string> GetStatePathCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in new[] { _statePersistPath, _stateAliasPath })
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (seen.Add(path))
                    yield return path;
            }
        }

        private StateSnapshot? GetLegacySingleStateBackup(IReadOnlyCollection<string> explicitCandidates)
        {
            var root = GetLegacyStateRoot();
            if (root is null || !Directory.Exists(root)) return null;

            var explicitSet = new HashSet<string>(explicitCandidates, StringComparer.OrdinalIgnoreCase);
            var legacy = Directory.EnumerateFiles(root, "sunshine_state.json", SearchOption.AllDirectories)
                .Where(path => !explicitSet.Contains(path))
                .Select(ReadStateSnapshot)
                .Where(IsUsableStateBackup)
                .ToList();

            return legacy.Count == 1 ? legacy[0] : null;
        }

        private string? GetLegacyStateRoot()
        {
            if (string.IsNullOrWhiteSpace(_statePersistPath)) return null;
            var sessionDir = Path.GetDirectoryName(_statePersistPath);
            return string.IsNullOrWhiteSpace(sessionDir) ? null : Path.GetDirectoryName(sessionDir);
        }

        private bool IsPrimaryStatePath(string path) =>
            !string.IsNullOrWhiteSpace(_statePersistPath) &&
            string.Equals(path, _statePersistPath, StringComparison.OrdinalIgnoreCase);

        private static bool ShouldRestoreState(StateSnapshot active, StateSnapshot backup)
        {
            if (!active.Exists || !active.ValidJson) return true;
            // named_devices presente (aunque sea "" o []) es estado deliberado de
            // Sunshine. Lo respetamos salvo que el backup sea más reciente: eso indica
            // que el activo es una copia vieja/vacía del instance dir.
            if (backup.HasNamedDevicesKey)
            {
                if (!active.HasNamedDevicesKey) return true;
                if (backup.LastWriteUtc > active.LastWriteUtc.AddMilliseconds(100) &&
                    !SameNamedDeviceState(active, backup))
                    return true;
            }

            if (backup.HasCredentials && !active.HasCredentials && !active.HasNamedDevicesKey)
                return true;

            return false;
        }

        private static bool SameNamedDeviceState(StateSnapshot a, StateSnapshot b) =>
            a.HasNamedDevicesKey == b.HasNamedDevicesKey &&
            a.HasNamedDevicesArray == b.HasNamedDevicesArray &&
            a.NamedDeviceCount == b.NamedDeviceCount;

        private static bool IsUsableStateBackup(StateSnapshot state) =>
            state.Exists && state.ValidJson && (state.HasCredentials || state.HasNamedDevicesKey);

        private static StateSnapshot ReadStateSnapshot(string path)
        {
            if (!File.Exists(path))
                return new StateSnapshot(path, false, false, false, false, false, 0, 0, DateTime.MinValue);

            var info = new FileInfo(path);
            var validJson = false;
            var hasCredentials = false;
            var hasNamedDevicesKey = false;
            var hasNamedDevicesArray = false;
            var namedDeviceCount = 0;

            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
                {
                    validJson = true;
                    hasCredentials =
                        HasJsonString(root, "username") &&
                        HasJsonString(root, "salt") &&
                        HasJsonString(root, "password");

                    if (root["root"] is JsonObject rootObj && rootObj.ContainsKey("named_devices"))
                    {
                        // Sunshine usa Boost.PropertyTree: cuando se borra el último device,
                        // named_devices se serializa como "" (string vacío), no como []. Hay
                        // que distinguir "key ausente" (estado corrupto/inicial) de "key presente
                        // con 0 devices" (intención del usuario tras borrar todos los pareos).
                        hasNamedDevicesKey = true;
                        if (rootObj["named_devices"] is JsonArray devices)
                        {
                            hasNamedDevicesArray = true;
                            namedDeviceCount = devices.Count;
                        }
                    }
                }
            }
            catch
            {
                validJson = false;
            }

            return new StateSnapshot(
                path,
                true,
                validJson,
                hasCredentials,
                hasNamedDevicesKey,
                hasNamedDevicesArray,
                namedDeviceCount,
                info.Length,
                info.LastWriteTimeUtc);
        }

        private static bool HasJsonString(JsonObject obj, string key) =>
            obj[key] is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            !string.IsNullOrWhiteSpace(text);

        private sealed record StateSnapshot(
            string Path,
            bool Exists,
            bool ValidJson,
            bool HasCredentials,
            bool HasNamedDevicesKey,
            bool HasNamedDevicesArray,
            int NamedDeviceCount,
            long Length,
            DateTime LastWriteUtc);

        /// <summary>
        /// Arranca un FileSystemWatcher sobre el <c>sunshine_state.json</c> activo: cada vez
        /// que Sunshine lo reescribe (p.ej. un pareo nuevo desde Moonlight) lo replicamos a
        /// la ubicación persistente. Hace un mirror inicial inmediato por si Sunshine ya
        /// había escrito antes de armar el watcher (durante <c>--creds</c>).
        /// </summary>
        private void StartStateMirror()
        {
            var targets = GetStatePathCandidates().ToList();
            if (targets.Count == 0) return;

            try
            {
                var activePath = ActiveStatePath;
                var dir = Path.GetDirectoryName(activePath)!;
                Directory.CreateDirectory(dir);
                foreach (var target in targets)
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                StopStateMirror();

                _stateWatcher = new FileSystemWatcher(dir, "sunshine_state.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _stateWatcher.Changed += OnStateChanged;
                _stateWatcher.Created += OnStateChanged;
                _stateWatcher.Renamed += OnStateChanged;

                MirrorStateToBackup(force: true);

                // Safety net: polling cada 5 s. FileSystemWatcher en Windows
                // puede perder eventos si el buffer se desborda durante un burst
                // (p.ej. múltiples escrituras de Sunshine al emparejar). El poll
                // detecta cambios por mtime y compensa.
                _statePollTimer = new System.Threading.Timer(
                    _ => MirrorStateToBackupIfStale(),
                    null,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5));

                Logger.Log($"[Sunshine] Mirror de sunshine_state.json activo: {string.Join(", ", targets)}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Sunshine] No se pudo iniciar mirror de sunshine_state.json: {ex.Message}");
            }
        }

        private void OnStateChanged(object sender, FileSystemEventArgs e) =>
            MirrorStateToBackup();

        private void StopStateMirror()
        {
            if (_statePollTimer is not null)
            {
                try { _statePollTimer.Dispose(); } catch { }
                _statePollTimer = null;
            }
            if (_stateWatcher is null) return;
            try
            {
                _stateWatcher.EnableRaisingEvents = false;
                _stateWatcher.Changed -= OnStateChanged;
                _stateWatcher.Created -= OnStateChanged;
                _stateWatcher.Renamed -= OnStateChanged;
                _stateWatcher.Dispose();
            }
            catch { }
            _stateWatcher = null;
        }

        /// <summary>
        /// Mirror sólo si el archivo activo es más reciente que el backup.
        /// Llamado por el timer de polling como safety net contra eventos
        /// perdidos del FileSystemWatcher.
        /// </summary>
        private void MirrorStateToBackupIfStale()
        {
            var targets = GetStatePathCandidates().ToList();
            if (targets.Count == 0) return;
            try
            {
                var activePath = ActiveStatePath;
                if (!File.Exists(activePath)) return;

                var activeUtc = File.GetLastWriteTimeUtc(activePath);
                if (targets.Any(target =>
                    !File.Exists(target) ||
                    activeUtc > File.GetLastWriteTimeUtc(target)))
                {
                    MirrorStateToBackup(force: true);
                }
            }
            catch { /* poll silencioso */ }
        }

        private void MirrorStateToBackup(bool force = false)
        {
            var targets = GetStatePathCandidates().ToList();
            if (targets.Count == 0) return;

            lock (_mirrorLock)
            {
                try
                {
                    var activePath = ActiveStatePath;
                    var activeState = ReadStateSnapshot(activePath);
                    if (!activeState.Exists) return;

                    if (!activeState.ValidJson)
                    {
                        Logger.Warning("[Sunshine] Mirror omitido: sunshine_state.json activo no es JSON valido.");
                        return;
                    }

                    var targetStates = targets.Select(ReadStateSnapshot).ToList();
                    // Sólo preservamos el backup cuando el activo NO tiene la clave
                    // named_devices (corrupto/sin inicializar). Si el activo trae la clave
                    // — aunque venga vacía como "" (Sunshine serializa así un array sin
                    // elementos vía Boost.PropertyTree) o como [] — refleja la voluntad
                    // del usuario y debe sobrescribir el backup.
                    if (!activeState.HasNamedDevicesKey && targetStates.Any(t => t.HasNamedDevicesKey))
                    {
                        Logger.Warning("[Sunshine] Mirror omitido: activo sin named_devices y backup con named_devices; backup preservado.");
                        return;
                    }

                    var now = DateTime.UtcNow;
                    if (!force && (now - _lastMirrorUtc).TotalMilliseconds < 250) return;

                    System.Threading.Thread.Sleep(50);

                    foreach (var target in targets)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                        Exception? lastErr = null;
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                File.Copy(activePath, target, overwrite: true);
                                lastErr = null;
                                break;
                            }
                            catch (IOException ex)
                            {
                                lastErr = ex;
                                System.Threading.Thread.Sleep(100);
                            }
                        }

                        if (lastErr is not null)
                            Logger.Warning($"[Sunshine] Mirror de state.json bloqueado tras 3 intentos ({target}): {lastErr.Message}");
                    }

                    _lastMirrorUtc = now;
                }
                catch (IOException) { /* archivo en uso, proximo evento lo cogera */ }
                catch (Exception ex)
                {
                    Logger.Warning($"[Sunshine] Error copiando sunshine_state.json a backup: {ex.Message}");
                }
            }
        }
    }
}
