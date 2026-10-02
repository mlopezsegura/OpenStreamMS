using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;
using OpenStreamMS.Services.OpenStream;
using OpenStreamMS.Services.Session;
using OpenStreamMS.Services.Sunshine;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Gestiona el ciclo de vida de todas las sesiones de streaming.
/// Hilo-seguro; el estado de cada sesión se actualiza desde tareas en segundo plano.
/// Las sesiones se persisten en sessions.json para sobrevivir reinicios del servicio.
/// </summary>
public class StreamSessionService
{
    private readonly ServiceConfig    _defaultConfig;
    private readonly ConcurrentDictionary<Guid, StreamSession>   _sessions = new();
    private readonly ConcurrentDictionary<Guid, SunshineManager> _sunshine = new();

    private static readonly string SessionsFile =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sessions.json");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true };

    public StreamSessionService(ServiceConfig defaultConfig)
    {
        _defaultConfig = defaultConfig;
        LoadSessions();
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    public StreamSession Create(string name, string username, string domain, string password,
                                string? sunshineExePath, bool rdpBackground, bool vddEnabled,
                                int rdpWidth, int rdpHeight, int rdpFrameRate, int rdpColorDepth,
                                bool enabled,
                                bool useStreamProfile = false, int sunshineStreamPort = 47989,
                                string? sunshineName = null, string? capture = null,
                                string? encoder = null, string? outputName = null,
                                string? originWebUiAllowed = null,
                                string? sunshineAuthUser = null, string? sunshineAuthPass = null,
                                bool rotateSunshineCredentials = false,
                                bool isolated = false, bool isolatedEphemeral = false)
    {
        var session = new StreamSession
        {
            Name               = name,
            Username           = username,
            Domain             = domain,
            Password           = password,
            SunshineExePath    = ResolveExePath(sunshineExePath),
            RdpBackground      = rdpBackground,
            VddEnabled         = vddEnabled,
            RdpWidth           = rdpWidth  > 0 ? rdpWidth  : 1920,
            RdpHeight          = rdpHeight > 0 ? rdpHeight : 1080,
            RdpFrameRate       = NormalizeRdpFrameRate(rdpFrameRate),
            RdpColorDepth      = NormalizeRdpColorDepth(rdpColorDepth),
            Enabled            = enabled,
            UseStreamProfile   = useStreamProfile,
            SunshineStreamPort = sunshineStreamPort > 0 ? sunshineStreamPort : 47989,
            SunshineName       = NullIfBlank(sunshineName),
            Capture            = NullIfBlank(capture),
            Encoder            = NullIfBlank(encoder),
            OutputName         = NullIfBlank(outputName),
            OriginWebUiAllowed = NullIfBlank(originWebUiAllowed),
            SunshineAuthUser   = NullIfBlank(sunshineAuthUser) ?? "",
            SunshineAuthPass   = NullIfBlank(sunshineAuthPass) ?? "",
            RotateSunshineCredentials = rotateSunshineCredentials,
            Isolated           = isolated,
            IsolatedEphemeral  = isolatedEphemeral,
        };
        _sessions[session.Id] = session;
        Logger.Log($"[SessionService] Sesión creada: '{name}' ({session.Id}) enabled={enabled}");
        SaveSessions();
        return session;
    }

    public IEnumerable<StreamSession> GetAll() => _sessions.Values;

    public StreamSession? Get(Guid id) => _sessions.GetValueOrDefault(id);

    public bool Delete(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return false;
        if (session.State is SessionState.Starting or SessionState.Running or SessionState.Stopping)
            throw new InvalidOperationException("Detén la sesión antes de eliminarla.");

        _sessions.TryRemove(id, out _);
        _sunshine.TryRemove(id, out _);
        RemoveFirewallRule(id);

        // Aislamiento nativo: eliminar usuario dedicado + perfil + registro
        if (session.Isolated)
            try { IsolatedUserManager.RemoveUser(id); }
            catch (Exception ex) { Logger.Warning($"[SessionService] No se pudo eliminar el usuario aislado: {ex.Message}"); }

        var instanceDir = GetSunshineInstanceDir(id);
        if (Directory.Exists(instanceDir))
            try { Directory.Delete(instanceDir, recursive: true); }
            catch (Exception ex) { Logger.Warning($"[SessionService] No se pudo eliminar instancia Sunshine: {ex.Message}"); }

        Logger.Log($"[SessionService] Sesión eliminada: {id}");
        SaveSessions();
        return true;
    }

    public StreamSession SetEnabled(Guid id, bool enabled)
    {
        var session = RequireSession(id);
        session.Enabled = enabled;
        Logger.Log($"[{session.Name}] Sesión {(enabled ? "habilitada" : "deshabilitada")} para auto-arranque.");
        SaveSessions();
        return session;
    }

    public StreamSession UpdateConfig(Guid id, string name, string? sunshineExePath,
                                      bool rdpBackground, bool vddEnabled,
                                      int rdpWidth, int rdpHeight,
                                      int rdpFrameRate, int rdpColorDepth,
                                      int sunshineStreamPort, bool useStreamProfile,
                                      string? sunshineName, string? capture,
                                      string? encoder, string? outputName,
                                      string? originWebUiAllowed,
                                      string? sunshineAuthUser, string? sunshineAuthPass,
                                      bool rotateSunshineCredentials,
                                      bool isolated = false, bool isolatedEphemeral = false)
    {
        var session = RequireSession(id);

        if (session.State is SessionState.Starting or SessionState.Running or SessionState.Stopping)
            throw new InvalidOperationException("Detén la sesión antes de modificar su configuración.");

        var oldExe  = session.SunshineExePath;
        var oldPort = session.SunshineStreamPort;
        session.Name               = name;
        session.SunshineExePath    = ResolveExePath(sunshineExePath);
        session.RdpBackground      = rdpBackground;
        session.VddEnabled         = vddEnabled;
        session.RdpWidth           = rdpWidth  > 0 ? rdpWidth  : 1920;
        session.RdpHeight          = rdpHeight > 0 ? rdpHeight : 1080;
        session.RdpFrameRate       = NormalizeRdpFrameRate(rdpFrameRate);
        session.RdpColorDepth      = NormalizeRdpColorDepth(rdpColorDepth);
        session.SunshineStreamPort = sunshineStreamPort > 0 ? sunshineStreamPort : 47989;
        session.UseStreamProfile   = useStreamProfile;
        session.SunshineName       = NullIfBlank(sunshineName);
        session.Capture            = NullIfBlank(capture);
        session.Encoder            = NullIfBlank(encoder);
        session.OutputName         = NullIfBlank(outputName);
        session.OriginWebUiAllowed = NullIfBlank(originWebUiAllowed);
        session.RotateSunshineCredentials = rotateSunshineCredentials;

        // Si se desactiva el aislamiento, limpiar el usuario dedicado que quedaba
        if (session.Isolated && !isolated)
            try { IsolatedUserManager.RemoveUser(session.Id); }
            catch (Exception ex) { Logger.Warning($"[{session.Name}] No se pudo limpiar el usuario aislado: {ex.Message}"); }
        session.Isolated          = isolated;
        session.IsolatedEphemeral = isolatedEphemeral;

        // Credenciales del panel: vacío = mantener las actuales (no las pisamos con "" sin querer).
        // Para forzar regeneración hay un endpoint dedicado (POST .../sunshine-credentials/reset).
        var newUser = NullIfBlank(sunshineAuthUser);
        var newPass = NullIfBlank(sunshineAuthPass);
        if (newUser is not null) session.SunshineAuthUser = newUser;
        if (newPass is not null) session.SunshineAuthPass = newPass;

        // Recrear el SunshineManager con la nueva configuración
        _sunshine.TryRemove(id, out _);

        // Si cambió la plantilla de Sunshine, borrar la instancia para forzar re-copia
        if (!string.Equals(oldExe, session.SunshineExePath, StringComparison.OrdinalIgnoreCase))
        {
            var instanceDir = GetSunshineInstanceDir(id);
            if (Directory.Exists(instanceDir))
                try { Directory.Delete(instanceDir, recursive: true); } catch { }
        }

        // Si cambió el puerto base, actualizar la regla de firewall (rango Moonlight completo)
        if (oldPort != session.SunshineStreamPort && Directory.Exists(GetSunshineInstanceDir(id)))
            AddFirewallRule(id, session.SunshineStreamPort);

        Logger.Log($"[{session.Name}] Configuración actualizada.");
        SaveSessions();
        return session;
    }

    // ── CONTROL ──────────────────────────────────────────────────────────────

    public void Start(Guid id)
    {
        var session = RequireSession(id);

        if (session.State is SessionState.Starting or SessionState.Running)
            throw new InvalidOperationException($"La sesión ya está en estado '{session.State}'.");

        session.State        = SessionState.Starting;
        session.ErrorMessage = null;

        // Operación bloqueante (hasta 30 s) → ejecutar en hilo del pool
        _ = Task.Run(() => DoStart(session));
    }

    public void Stop(Guid id)
    {
        var session = RequireSession(id);

        if (session.State is not (SessionState.Starting or SessionState.Running))
            throw new InvalidOperationException($"La sesión no está activa (estado: '{session.State}').");

        session.State = SessionState.Stopping;
        Logger.Log($"[{session.Name}] Deteniendo...", session.Id);
        ForceStopInternal(session);
    }

    /// <summary>
    /// Reinicia una sesión: mata todos sus procesos (FreeRDP, Sunshine, logoff) y la vuelve a iniciar.
    /// No comprueba precondiciones de estado — funciona aunque la sesión esté colgada en Starting/Stopping/Error.
    /// </summary>
    public void Restart(Guid id)
    {
        var session = RequireSession(id);
        Logger.Log($"[{session.Name}] Reinicio solicitado (estado actual: {session.State}).", session.Id);
        session.State        = SessionState.Stopping;
        session.ErrorMessage = null;

        _ = Task.Run(() => DoRestart(session));
    }

    private void DoRestart(StreamSession session)
    {
        Logger.SetSessionContext(session.Id);
        try
        {
            ForceStopInternal(session);

            // Pausa breve para que Windows libere el sessionId y los locks del usuario
            Thread.Sleep(1500);

            session.State        = SessionState.Starting;
            session.ErrorMessage = null;
            DoStart(session);
        }
        finally
        {
            Logger.SetSessionContext(null);
        }
    }

    /// <summary>
    /// Lógica de force-kill compartida por Stop y Restart. Mata FreeRDP, Sunshine y hace logoff
    /// de la sesión Windows. Siempre deja la sesión en estado Stopped al terminar.
    /// </summary>
    private void ForceStopInternal(StreamSession session)
    {
        try
        {
            // Sunshine corre dentro de la sesión RDP. Si matamos FreeRDP primero,
            // Windows puede tirar abajo Sunshine antes de que escriba named_devices.
            // Pararlo primero permite /api/restart -> save_state -> mirror.
            if (_sunshine.TryGetValue(session.Id, out var mgr))
            {
                var killTask = Task.Run(mgr.Stop);
                if (!killTask.Wait(TimeSpan.FromSeconds(10)))
                    Logger.Warning($"[{session.Name}] Timeout al matar Sunshine.", session.Id);
            }
            // Forzar recreación del SunshineManager en el próximo Start
            _sunshine.TryRemove(session.Id, out _);

            if (session.FreeRdpPid > 0)
            {
                try
                {
                    System.Diagnostics.Process.GetProcessById(session.FreeRdpPid).Kill();
                    Logger.Log($"[{session.Name}] FreeRDP (PID {session.FreeRdpPid}) detenido.", session.Id);
                }
                catch { }
                session.FreeRdpPid = 0;
            }

            // Cerrar la sesión Windows del usuario (terminar todos sus procesos)
            if (session.RdpSessionId > 0)
            {
                StreamProfileSetup.LogoffSession(session.RdpSessionId);
                session.RdpSessionId = 0;
            }

            // Modo efímero: borrar el perfil del usuario aislado en segundo plano
            // (profsvc tarda unos segundos en descargar el hive tras el logoff)
            if (session.Isolated && session.IsolatedEphemeral)
                _ = IsolatedUserManager.DeleteProfileWithRetryAsync(session.Id, session.Name);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[{session.Name}] Aviso al detener: {ex.Message}", session.Id);
        }
        finally
        {
            // Garantizado: el estado SIEMPRE llega a Stopped
            session.State     = SessionState.Stopped;
            session.StoppedAt = DateTime.UtcNow;
            session.SunshinePid = 0;
            Logger.Log($"[{session.Name}] Detenida.", session.Id);
        }
    }

    /// <summary>
    /// Sunshine persiste los clientes emparejados en <c>sunshine_state.json</c> al finalizar
    /// el handshake real con Moonlight. <c>/api/pin</c> sólo entrega el PIN a esa negociación,
    /// así que reiniciar Sunshine aquí corta el pairing antes de que <c>save_state()</c> se
    /// ejecute. En su lugar hacemos mirrors diferidos del state activo al backup persistente;
    /// el FileSystemWatcher normalmente lo hará antes, y estos flushes cubren eventos perdidos.
    /// </summary>
    public void ScheduleSunshineStateMirror(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        if (session.State != SessionState.Running) return;
        if (!_sunshine.TryGetValue(id, out var mgr)) return;

        _ = Task.Run(async () =>
        {
            Logger.SetSessionContext(id);
            try
            {
                foreach (var delay in new[]
                {
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(8),
                    TimeSpan.FromSeconds(20)
                })
                {
                    await Task.Delay(delay);
                    if (session.State != SessionState.Running) return;
                    mgr.FlushStateMirror();
                    Logger.Log($"[{session.Name}] Mirror diferido de sunshine_state.json ejecutado tras mutación de pareos.", id);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[{session.Name}] Error en mirror diferido tras mutación de pareos: {ex.Message}", id);
            }
            finally
            {
                Logger.SetSessionContext(null);
            }
        });
    }

    // ── RECUPERACIÓN ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reinicia la sesion completa cuando Sunshine se cierra inesperadamente.
    /// </summary>
    private void RestartAfterSunshineFault(StreamSession session, string? reason)
    {
        if (session.State != SessionState.Running)
            return;

        var message = string.IsNullOrWhiteSpace(reason)
            ? "Sunshine terminó inesperadamente; reiniciando sesión completa."
            : $"{reason} Reiniciando sesión completa.";

        Logger.Log($"[{session.Name}] {message}", session.Id);
        session.State = SessionState.Stopping;
        session.ErrorMessage = message;
        session.SunshinePid = 0;

        _ = Task.Run(() => DoRestart(session));
    }

    // ── AUTO-ARRANQUE (llamado al iniciar el servicio) ────────────────────────

    /// <summary>
    /// Lanza en segundo plano todas las sesiones con <c>Enabled = true</c>.
    /// Llamado por <see cref="OpenStreamService"/> al arrancar.
    /// </summary>
    public void AutoStartEnabled()
    {
        var toStart = _sessions.Values.Where(s => s.Enabled).ToList();
        if (toStart.Count == 0) return;

        Logger.Log($"[SessionService] Auto-arrancando {toStart.Count} sesión(es) habilitada(s)...");
        foreach (var session in toStart)
        {
            try { Start(session.Id); }
            catch (Exception ex)
            {
                Logger.Log($"[{session.Name}] No se pudo auto-arrancar: {ex.Message}");
            }
        }
    }

    // ── MONITORIZACIÓN (llamado por el BackgroundService cada 5 s) ───────────

    public void MonitorAll()
    {
        foreach (var session in _sessions.Values.Where(s => s.State == SessionState.Running))
        {
            Logger.SetSessionContext(session.Id);
            try
            {
                var mgr = GetOrCreateSunshineManager(session);
                var result = mgr.EnsureRunning(session.RdpSessionId);
                session.SunshinePid = result.Pid;

                if (result.Status == SunshineRunStatus.UnexpectedExit)
                {
                    RestartAfterSunshineFault(session, result.Message);
                    continue;
                }

                if (result.Status == SunshineRunStatus.LaunchFailed)
                {
                    session.State = SessionState.Error;
                    session.ErrorMessage = result.Message ?? "No se pudo lanzar Sunshine.";
                    Logger.Error($"[{session.Name}] Sunshine no pudo arrancar: {session.ErrorMessage}", session.Id);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[{session.Name}] Error en monitorización: {ex.Message}", session.Id);
                session.State        = SessionState.Error;
                session.ErrorMessage = ex.Message;
            }
            finally
            {
                Logger.SetSessionContext(null);
            }
        }
    }

    // ── LOGS ─────────────────────────────────────────────────────────────────

    public string[] GetLogs(Guid id, int lines)
    {
        RequireSession(id);
        return Logger.ReadSessionLogs(id, lines);
    }

    // ── PERSISTENCIA ─────────────────────────────────────────────────────────

    private void SaveSessions()
    {
        try
        {
            var data = _sessions.Values.Select(s => new SessionData(
                s.Id, s.Name, s.Username, s.Domain, s.Password,
                s.SunshineExePath, s.RdpBackground, s.VddEnabled,
                s.RdpWidth, s.RdpHeight, s.Enabled,
                s.CreatedAt, s.UseStreamProfile,
                SunshineWebPort:    0, // legacy: no se usa, el valor autoritativo es SunshineStreamPort
                s.SunshineName, s.Capture, s.Encoder, s.OutputName, s.OriginWebUiAllowed,
                s.SunshineAuthUser, s.SunshineAuthPass, s.RotateSunshineCredentials,
                SunshineStreamPort: s.SunshineStreamPort,
                RdpFrameRate: s.RdpFrameRate,
                RdpColorDepth: s.RdpColorDepth,
                Isolated: s.Isolated,
                IsolatedEphemeral: s.IsolatedEphemeral));
            File.WriteAllText(SessionsFile, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SessionService] No se pudieron guardar las sesiones: {ex.Message}");
        }
    }

    private void LoadSessions()
    {
        if (!File.Exists(SessionsFile)) return;
        try
        {
            var data = JsonSerializer.Deserialize<SessionData[]>(File.ReadAllText(SessionsFile));
            if (data is null) return;

            foreach (var d in data)
            {
                // Migración: si la sesión es antigua y trae SunshineWebPort pero no
                // SunshineStreamPort, derivamos stream = web - 1.
                int streamPort = d.SunshineStreamPort > 0
                    ? d.SunshineStreamPort
                    : (d.SunshineWebPort > 0 ? d.SunshineWebPort - 1 : 47989);

                var session = new StreamSession
                {
                    Id                 = d.Id,
                    Name               = d.Name,
                    Username           = d.Username,
                    Domain             = d.Domain,
                    Password           = d.Password,
                    SunshineExePath    = d.SunshineExePath,
                    RdpBackground      = d.RdpBackground,
                    VddEnabled         = d.VddEnabled,
                    RdpWidth           = d.RdpWidth  > 0 ? d.RdpWidth  : 1920,
                    RdpHeight          = d.RdpHeight > 0 ? d.RdpHeight : 1080,
                    RdpFrameRate       = NormalizeRdpFrameRate(d.RdpFrameRate),
                    RdpColorDepth      = NormalizeRdpColorDepth(d.RdpColorDepth),
                    Enabled            = d.Enabled,
                    CreatedAt          = d.CreatedAt,
                    UseStreamProfile   = d.UseStreamProfile,
                    SunshineStreamPort = streamPort,
                    SunshineName       = d.SunshineName,
                    Capture            = d.Capture,
                    Encoder            = d.Encoder,
                    OutputName         = d.OutputName,
                    OriginWebUiAllowed = d.OriginWebUiAllowed,
                    SunshineAuthUser   = d.SunshineAuthUser ?? "",
                    SunshineAuthPass   = d.SunshineAuthPass ?? "",
                    RotateSunshineCredentials = d.RotateSunshineCredentials,
                    Isolated           = d.Isolated,
                    IsolatedEphemeral  = d.IsolatedEphemeral,
                    State              = SessionState.Stopped,
                };
                _sessions[session.Id] = session;
            }
            Logger.Log($"[SessionService] {data.Length} sesión(es) cargadas desde disco.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SessionService] No se pudieron cargar las sesiones: {ex.Message}");
        }
    }

    // ── PRIVATE ──────────────────────────────────────────────────────────────

    private void DoStart(StreamSession session)
    {
        Logger.SetSessionContext(session.Id);
        try
        {
            StreamUserConfig user;
            if (session.Isolated)
            {
                // Aislamiento nativo: usuario local dedicado con contraseña rotada.
                // Si el modo efímero dejó un perfil sin borrar (p.ej. servicio
                // reiniciado a mitad), intentarlo ahora antes de re-logar.
                if (session.IsolatedEphemeral)
                    IsolatedUserManager.TryDeleteProfile(session.Id, out _);

                var creds = IsolatedUserManager.EnsureUser(session.Id, session.Name);
                user = new StreamUserConfig
                {
                    Username = creds.Username,
                    Domain   = ".",
                    Password = creds.Password,
                };
                Logger.Log($"[{session.Name}] Aislamiento nativo activo: usuario dedicado '{creds.Username}' " +
                           $"(perfil {(session.IsolatedEphemeral ? "efímero" : "persistente")}).", session.Id);
            }
            else
            {
                user = new StreamUserConfig
                {
                    Username = session.Username,
                    Domain   = session.Domain,
                    Password = session.Password,
                };
            }

            Logger.Log($"[{session.Name}] Iniciando sesión RDP para usuario '{user.Username}'...", session.Id);

            var rdpId = RdpSessionCreator.GetOrCreateSession(user,
                                                              session.RdpWidth, session.RdpHeight,
                                                              session.RdpFrameRate, session.RdpColorDepth,
                                                              out int freeRdpPid,
                                                              preferMstsc: !string.Equals(
                                                                  _defaultConfig.RdpClient, "freerdp",
                                                                  StringComparison.OrdinalIgnoreCase));

            // Stop() pudo haber sido llamado mientras esperábamos la sesión RDP
            if (session.State != SessionState.Starting)
            {
                Logger.Log($"[{session.Name}] Inicio abortado (sesión detenida durante la creación RDP).", session.Id);
                if (freeRdpPid > 0) try { System.Diagnostics.Process.GetProcessById(freeRdpPid).Kill(); } catch { }
                return;
            }

            session.RdpSessionId = rdpId;
            session.FreeRdpPid   = freeRdpPid;

            // ── Audio: aislamiento via mute per-process de wfreerdp ───────────
            // FreeRDP /audio-mode:redirect + /sound: RDP server intercepta el audio
            // del juego dentro de la sesión RDP y lo redirige por canal a wfreerdp.exe,
            // que lo reproduce en el host (típicamente en el endpoint físico).
            // Sunshine, dentro de la sesión RDP, hace WASAPI loopback en "Remote Audio"
            // (endpoint virtual que crea Windows automáticamente cuando hay redirect),
            // capturando el SOURCE del audio antes del transporte RDP. Moonlight oye.
            // Para que el host NO oiga: mute persistente de la audio session de
            // wfreerdp.exe via ISimpleAudioVolume.SetMute. Mute es per-process por
            // endpoint, no toca volumen global ni el de otros procesos. Sunshine no
            // se ve afectado porque captura el source, no el output de wfreerdp.
            if (freeRdpPid > 0)
            {
                Logger.Log($"[{session.Name}] Silenciando wfreerdp.exe (PID={freeRdpPid}) en host para evitar fuga de audio...", session.Id);
                AudioSessionMuter.StartPersistentMute((uint)freeRdpPid);
            }
            else
            {
                // Sesión RDP reutilizada (no se lanzó FreeRDP). Sin PID de wfreerdp no
                // hay nada que silenciar en el host; el audio debería seguir confinado
                // al wfreerdp.exe original (si sigue vivo y muteado por su thread).
                Logger.Warning($"[{session.Name}] Sin PID de FreeRDP, no se puede aplicar mute en host esta vez.", session.Id);
            }

            Dictionary<string, string>? profileOverrides = null;
            if (session.UseStreamProfile && session.Isolated)
            {
                // El usuario dedicado ya tiene perfil/HKCU propios: la redirección
                // de env vars es redundante y solo añade rutas raras. Omitida.
                Logger.Log($"[{session.Name}] UseStreamProfile omitido: el aislamiento nativo ya proporciona perfil propio.", session.Id);
            }
            else if (session.UseStreamProfile)
            {
                Logger.Log($"[{session.Name}] Configurando perfil de stream independiente...", session.Id);
                profileOverrides = StreamProfileSetup.BuildOverrides(rdpId);
                StreamProfileSetup.RestartExplorer(rdpId, profileOverrides);
            }

            RdpSessionCreator.ApplyDisplayFrequency(rdpId, session.RdpFrameRate);

            // Anti-fallback a encode por software. Sunshine fija su encoder UNA vez al
            // arrancar (sondeo). Si la sesion esta en el lock screen en ese instante,
            // el sondeo de amdvce falla ("Failed to Open Input Desktop [0x5]") y cae a
            // libx264 (CPU) de por vida → 4K = diente de sierra 60→8. Desactivamos el
            // auto-bloqueo y esperamos a que la sesion este desbloqueada ANTES de
            // lanzar Sunshine para que encuentre la GPU (h264_amf).
            RdpSessionCreator.PreventSessionLock();
            RdpSessionCreator.WaitForSessionUnlocked(rdpId);

            EnsureSunshineInstance(session, user.Domain, user.Username);
            var mgr = GetOrCreateSunshineManager(session, profileOverrides);
            var sunshineResult = mgr.EnsureRunning(rdpId);
            session.SunshinePid = sunshineResult.Pid;

            if (sunshineResult.Status == SunshineRunStatus.LaunchFailed)
                throw new Exception(sunshineResult.Message ?? "No se pudo lanzar Sunshine.");

            if (session.State != SessionState.Starting)
            {
                Logger.Log($"[{session.Name}] Inicio abortado (sesión detenida durante el lanzamiento de Sunshine).", session.Id);
                return;
            }

            // NOTA: no desconectar el cliente FreeRDP aunque haya VDD. En una sesion
            // RDP desconectada Windows retira el stack de display completo y Sunshine
            // falla con ERROR_ACCESS_DENIED en QueryDisplayConfig ("failed to query
            // display paths and modes"), asi que el VDD per-app nunca llega a crearse.
            // El throttle de frame-acks se mitiga en el cliente (ver /frame-ack en
            // RdpSessionCreator).

            session.State     = SessionState.Running;
            session.StartedAt = DateTime.UtcNow;
            Logger.Log($"[{session.Name}] Sesión activa (RDP={rdpId}).", session.Id);
        }
        catch (Exception ex)
        {
            if (session.State == SessionState.Starting)
            {
                session.State        = SessionState.Error;
                session.ErrorMessage = ex.Message;
            }
            Logger.Error($"[{session.Name}] Error al iniciar: {ex.Message}", session.Id);
        }
        finally
        {
            Logger.SetSessionContext(null);
        }
    }

    private SunshineManager GetOrCreateSunshineManager(StreamSession session,
        Dictionary<string, string>? profileOverrides = null) =>
        _sunshine.GetOrAdd(session.Id, _ =>
        {
            EnsureSunshineCredentials(session, session.RotateSunshineCredentials);
            profileOverrides ??= BuildProfileOverridesForProcessLaunch(session);
            return new SunshineManager(
                GetSunshineInstanceExe(session.Id),
                session.VddEnabled,
                BuildSunshineConfig(session),
                new SunshineConfigurator.SunshineCredentials(session.SunshineAuthUser, session.SunshineAuthPass),
                profileOverrides,
                GetSunshineStatePersistPath(session.Id),
                GetSunshineStateAliasPath(session));
        });

    /// <summary>
    /// Ruta persistente del <c>sunshine_state.json</c> fuera del instance dir. Vive en
    /// <c>%ProgramData%\OpenStreamMS\state\&lt;sessionId&gt;\sunshine_state.json</c>: SYSTEM tiene
    /// permisos, sobrevive a borrados de la carpeta sessions y a reinstalaciones del
    /// servicio. SunshineManager lo usa como backup/restore para conservar named_devices.
    /// </summary>
    private static string GetSunshineStatePersistPath(Guid sessionId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenStreamMS", "state", sessionId.ToString(), "sunshine_state.json");

    /// <summary>
    /// Ruta alternativa independiente del Guid. Si una sesion se elimina y se crea
    /// otra vez con la misma identidad logica, este backup permite recuperar el
    /// sunshine_state.json anterior.
    /// </summary>
    private static string GetSunshineStateAliasPath(StreamSession session) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenStreamMS", "state-index", BuildSunshineStateKey(session), "sunshine_state.json");

    private static string BuildSunshineStateKey(StreamSession session)
    {
        static string Norm(string? value) => (value ?? "").Trim().ToLowerInvariant();

        var identity = string.Join("|",
            Norm(session.Domain),
            Norm(session.Username),
            Norm(session.Name),
            session.SunshineStreamPort.ToString());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, string>? BuildProfileOverridesForProcessLaunch(StreamSession session)
    {
        // Con aislamiento nativo el usuario dedicado ya tiene perfil propio
        if (!session.UseStreamProfile || session.Isolated || session.RdpSessionId == 0)
            return null;

        return StreamProfileSetup.BuildOverrides(session.RdpSessionId);
    }

    /// <summary>
    /// Si la sesión todavía no tiene credenciales para el panel web de Sunshine,
    /// las genera y las persiste. Idempotente: nunca regenera si ya existen.
    /// </summary>
    private void EnsureSunshineCredentials(StreamSession session, bool rotate)
    {
        if (!rotate &&
            !string.IsNullOrEmpty(session.SunshineAuthUser) &&
            !string.IsNullOrEmpty(session.SunshineAuthPass)) return;

        var creds = SunshineConfigurator.GenerateCredentials();
        session.SunshineAuthUser = creds.Username;
        session.SunshineAuthPass = creds.PlainPassword;
        Logger.Log(rotate
            ? $"[{session.Name}] Credenciales de Sunshine rotadas (user={creds.Username})."
            : $"[{session.Name}] Credenciales de Sunshine generadas (user={creds.Username}).",
            session.Id);
        SaveSessions();
    }

    /// <summary>
    /// Borra las credenciales internas de Sunshine para esta sesión y **mata el proceso
    /// de Sunshine** para forzar su relanzamiento con credenciales nuevas. El monitor
    /// periódico (<see cref="MonitorAll"/>, cada 5 s) lo relanzará automáticamente si
    /// la sesión sigue en estado <c>Running</c>: creará un nuevo <see cref="SunshineManager"/>,
    /// generará credenciales frescas vía <see cref="EnsureSunshineCredentials"/>, escribirá
    /// <c>sunshine_state.json</c> y arrancará Sunshine con todo sincronizado.
    /// </summary>
    public void ResetSunshineCredentials(Guid id)
    {
        var session = RequireSession(id);
        session.SunshineAuthUser = "";
        session.SunshineAuthPass = "";
        session.SunshinePid = 0;

        // Matar la Sunshine en curso: si la dejamos viva con las creds viejas, el
        // siguiente intento vuelve a dar 401 y entramos en un bucle. El monitor la
        // relanzará (con state.json recién escrito) en cuestión de segundos.
        if (_sunshine.TryRemove(id, out var mgr))
        {
            try { mgr.Stop(); }
            catch (Exception ex) { Logger.Warning($"[{session.Name}] Aviso al parar Sunshine durante reset de creds: {ex.Message}", id); }
        }

        SaveSessions();
        Logger.Log($"[{session.Name}] Credenciales Sunshine reseteadas; Sunshine detenido, se relanzará automáticamente.", id);
    }

    private static SunshineConfigurator.SunshineConfig BuildSunshineConfig(StreamSession s) =>
        new(
            StreamPort:               s.SunshineStreamPort,
            Name:                     string.IsNullOrWhiteSpace(s.SunshineName) ? s.Name : s.SunshineName,
            Capture:                  s.Capture,
            Encoder:                  s.Encoder,
            OutputName:               s.OutputName,
            OriginWebUiAllowed:       s.OriginWebUiAllowed,
            // Sin audio_sink → Sunshine usa el default de la sesión RDP donde corre.
            // Con FreeRDP /audio-mode:redirect + /sound, ese default es "Remote Audio"
            // (endpoint que crea Windows para el redirect). Loopback ahí captura el
            // source del juego.
            AudioSink: null);

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static int NormalizeRdpFrameRate(int frameRate) =>
        Math.Clamp(frameRate > 0 ? frameRate : 60, 1, 240);

    private static int NormalizeRdpColorDepth(int colorDepth) => colorDepth switch
    {
        8 or 15 or 16 or 24 or 32 => colorDepth,
        _ => 32
    };

    private static string GetSunshineInstanceDir(Guid sessionId) =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sessions", sessionId.ToString(), "Sunshine");

    private static string GetSunshineInstanceExe(Guid sessionId) =>
        Path.Combine(GetSunshineInstanceDir(sessionId), "sunshine.exe");

    private static void EnsureSunshineInstance(StreamSession session,
                                               string effectiveDomain, string effectiveUsername)
    {
        var instanceDir = GetSunshineInstanceDir(session.Id);
        var instanceExe = GetSunshineInstanceExe(session.Id);

        if (!File.Exists(instanceExe))
        {
            var templateDir = Path.GetDirectoryName(session.SunshineExePath)!;
            if (!Directory.Exists(templateDir))
                throw new DirectoryNotFoundException($"Directorio de Sunshine no encontrado: {templateDir}");

            Logger.Log($"[{session.Name}] Copiando Sunshine a instancia propia: {instanceDir}", session.Id);
            CopyDirectory(templateDir, instanceDir);
        }
        else
        {
            Logger.Log($"[{session.Name}] Instancia de Sunshine ya existe en: {instanceDir}", session.Id);
            SyncSunshineInstanceBinaries(session, instanceDir, instanceExe);
        }

        // El instance dir hereda los ACL restrictivos de Program Files (Usuarios=RX).
        // Sunshine corre como el usuario de sesión (no elevado) y necesita escribir
        // sunshine_state.json, sunshine.log, etc. El servicio corre como SYSTEM y puede
        // conceder el permiso aquí antes de lanzar el proceso.
        GrantSunshineInstanceWriteAccess(instanceDir, effectiveDomain, effectiveUsername, session.Id);

        // Siempre garantizar la regla de firewall, independientemente de si la instancia
        // ya existía (p.ej. sesiones creadas antes de que se añadiera este código).
        AddFirewallRule(session.Id, session.SunshineStreamPort);
    }

    /// <summary>
    /// Si el Sunshine de plantilla (el integrado o el configurado) tiene otra version que
    /// la instancia de la sesion, recopia los binarios y assets. <c>config/</c> (conf,
    /// state con los pareos Moonlight, credenciales, apps.json, logs) NO se toca.
    /// assets/web se limpia antes: sus ficheros llevan hash en el nombre y si no quedarian
    /// restos de la version anterior.
    /// </summary>
    private static void SyncSunshineInstanceBinaries(StreamSession session, string instanceDir, string instanceExe)
    {
        var templateDir = Path.GetDirectoryName(session.SunshineExePath)!;
        var templateExe = Path.Combine(templateDir, "sunshine.exe");
        if (!File.Exists(templateExe) ||
            string.Equals(Path.GetFullPath(templateDir).TrimEnd('\\'),
                          Path.GetFullPath(instanceDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return;

        var templateVer = System.Diagnostics.FileVersionInfo.GetVersionInfo(templateExe).FileVersion ?? "";
        var instanceVer = System.Diagnostics.FileVersionInfo.GetVersionInfo(instanceExe).FileVersion ?? "";
        if (templateVer == instanceVer && new FileInfo(templateExe).Length == new FileInfo(instanceExe).Length)
            return;

        Logger.Log($"[{session.Name}] Actualizando Sunshine de la instancia: {instanceVer} → {templateVer} " +
                   "(se conserva config/: pareos, credenciales y ajustes).", session.Id);
        try
        {
            var webDir = Path.Combine(instanceDir, "assets", "web");
            if (Directory.Exists(webDir))
                Directory.Delete(webDir, recursive: true);

            // sunshine.exe el ultimo: es la marca de version, asi una copia a medias se
            // reintenta en el proximo arranque en vez de quedar "igualada".
            var files = Directory.GetFiles(templateDir, "*", SearchOption.AllDirectories)
                                 .OrderBy(f => string.Equals(f, templateExe, StringComparison.OrdinalIgnoreCase));
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(templateDir, file);
                if (rel.StartsWith("config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                var dst = Path.Combine(instanceDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            // Tipicamente un sunshine.exe huerfano bloqueando ficheros. Se sigue con lo
            // que haya: la comparacion de version reintentara en el proximo arranque.
            Logger.Warning($"[{session.Name}] No se pudo actualizar Sunshine de la instancia: {ex.Message}", session.Id);
        }
    }

    private static void GrantSunshineInstanceWriteAccess(
        string dir, string domain, string username, Guid sessionId)
    {
        try
        {
            var account = string.IsNullOrWhiteSpace(domain) || domain == "."
                ? username
                : $"{domain}\\{username}";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "icacls.exe",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add("/grant");
            psi.ArgumentList.Add($"{account}:(OI)(CI)(M)");
            psi.ArgumentList.Add("/T");

            using var p = System.Diagnostics.Process.Start(psi)
                ?? throw new Exception("No se pudo lanzar icacls.exe");
            p.WaitForExit(10_000);

            if (p.ExitCode == 0)
                Logger.Log($"[SessionService] Permisos escritura concedidos a '{account}' en: {dir}", sessionId);
            else
                Logger.Warning($"[SessionService] icacls /grant falló (exit={p.ExitCode}) para '{account}'", sessionId);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[SessionService] No se pudieron conceder permisos a '{username}': {ex.Message}", sessionId);
        }
    }

    /// <summary>
    /// Abre en el firewall de Windows el rango de puertos Moonlight que usa Sunshine
    /// (TCP+UDP <c>streamPort..streamPort+21</c>). El panel HTTPS queda fuera del rango
    /// y por tanto ya no se expone — OpenStreamMS hace de reverse proxy.
    /// </summary>
    private static void AddFirewallRule(Guid sessionId, int streamPort)
    {
        var name = FirewallRuleName(sessionId);
        // Sunshine usa offsets del base port: video +9 (UDP), audio +10 (UDP),
        // control +11 (UDP), mic +13 (UDP), RTSP +21 (TCP). Abrimos el rango entero
        // para TCP y UDP; el panel (+1) queda dentro pero solo escucha en localhost
        // una vez que el stream se gestiona por proxy.
        var tcpName = name + " (TCP)";
        var udpName = name + " (UDP)";
        var range   = $"{streamPort}-{streamPort + 21}";
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{tcpName}\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{udpName}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{tcpName}\" dir=in action=allow protocol=TCP localport={range} profile=any");
            RunNetsh($"advfirewall firewall add rule name=\"{udpName}\" dir=in action=allow protocol=UDP localport={range} profile=any");
            Logger.Log($"[Sunshine] Firewall: abierto rango {range} TCP+UDP ({sessionId})");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Sunshine] No se pudo añadir regla de firewall: {ex.Message}");
        }
    }

    private static void RemoveFirewallRule(Guid sessionId)
    {
        var name = FirewallRuleName(sessionId);
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{name} (TCP)\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{name} (UDP)\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{name}\""); // legacy pre-rango
        }
        catch { }
    }

    private static string FirewallRuleName(Guid sessionId) => $"OpenStreamMS - Sunshine {sessionId}";

    private static void RunNetsh(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
        p?.WaitForExit(5_000);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: true);
    }

    private StreamSession RequireSession(Guid id) =>
        _sessions.TryGetValue(id, out var s) ? s
        : throw new KeyNotFoundException($"Sesión {id} no encontrada.");

    private string ResolveExePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return _defaultConfig.SunshineExePath;
        return Path.IsPathRooted(path) ? path
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
    }

    // ── DTO de persistencia (solo uso interno) ────────────────────────────────

    private record SessionData(
        Guid     Id,
        string   Name,
        string   Username,
        string   Domain,
        string   Password,
        string   SunshineExePath,
        bool     RdpBackground,
        bool     VddEnabled,
        int      RdpWidth,
        int      RdpHeight,
        bool     Enabled,
        DateTime CreatedAt,
        bool     UseStreamProfile   = false,
        /// <summary>Campo legacy: antes se guardaba el puerto del panel web. Si &gt; 0 se usa para derivar StreamPort (WebPort-1) al cargar.</summary>
        int      SunshineWebPort    = 0,
        string?  SunshineName       = null,
        string?  Capture            = null,
        string?  Encoder            = null,
        string?  OutputName         = null,
        string?  OriginWebUiAllowed = null,
        string?  SunshineAuthUser   = null,
        string?  SunshineAuthPass   = null,
        bool     RotateSunshineCredentials = false,
        /// <summary>Puerto base Moonlight (autoritativo). Si falta, se deriva de SunshineWebPort-1.</summary>
        int      SunshineStreamPort = 0,
        int      RdpFrameRate = 60,
        int      RdpColorDepth = 32,
        /// <summary>Aislamiento nativo: usuario local dedicado gestionado por el servicio.</summary>
        bool     Isolated = false,
        /// <summary>Solo con Isolated: borrar el perfil del usuario dedicado al detener.</summary>
        bool     IsolatedEphemeral = false);
}
