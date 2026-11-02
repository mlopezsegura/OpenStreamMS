using Microsoft.Extensions.Hosting;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Core.Helpers;
using System.Collections.Concurrent;
using System.Diagnostics;

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Gestiona los puestos locales: inventario de dispositivos libres, alta/edición/borrado,
/// y el ciclo de encendido/apagado de cada puesto (monitor, sesión asignada, audio y entrada).
/// Es el punto central de las fases F0–F4. Orquesta el agente de sesión y el presentador en la consola.
/// </summary>
public sealed class SeatManager(StreamSessionService sessions) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Seat> _seats =
        new(SeatStore.Load().ToDictionary(s => s.Id));
    private readonly ConcurrentDictionary<Guid, ActiveSeatSession> _activeSeats = new();
    private readonly object _saveLock = new();

    // ── Ciclo de vida ──────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SeatHostControl.StartServer(stoppingToken);
        try { UsbCapture.RestoreAll(); }
        catch (Exception ex) { Logger.Warning($"[Seats] No se pudieron restaurar los drivers USB: {ex.Message}"); }
        ConsoleLockGuard.Release();   // si el servicio se cayó con un puesto encendido
        // Sin puestos exclusivos nadie necesita quedarse sin apagar: el grupo Usuarios recupera el derecho.
        if (!_seats.Values.Any(s => s.ExclusiveSession)) ShutdownRight.Restore();
        Logger.Log($"[Seats] Gestor de puestos iniciado ({_seats.Count} puesto(s)).");
        // Antes de autoarrancar, que termine la comprobación de TermWrap/RemoteIdd: decide la
        // captura de los puestos y puede quitar RemoteIdd de RDP-Tcp.
        try { await OpenStreamMS.Services.Session.RdpWrapperManager.MultiRdpChecked.WaitAsync(TimeSpan.FromSeconds(120), stoppingToken); }
        catch (TimeoutException) { Logger.Warning("[Seats] La comprobación de multi-RDP no terminó en 120 s; se autoarrancan los puestos igualmente."); }
        catch (OperationCanceledException) { return; }
        foreach (var seat in _seats.Values.Where(s => s.AutoStart && s.SessionId is not null))
        {
            if (UsesConsoleUser(seat.SessionId))
            {
                Logger.Warning($"[Seats] '{seat.Name}' no se autoarranca: usa el mismo usuario que la consola.");
                continue;
            }
            try { Start(seat.Id); }
            catch (Exception ex) { Logger.Warning($"[Seats] Autoarranque de '{seat.Name}' falló: {ex.Message}"); }
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                WatchSeatSessions();
            }
        }
        catch (OperationCanceledException) { }

        foreach (var seat in _seats.Values.Where(s => s.State == SeatState.Running))
            try { Stop(seat.Id); } catch { /* cierre */ }
    }

    // ── Consulta para el aislamiento de mandos ──────────────────────────────────

    /// <summary>
    /// Cambia cada vez que un puesto se enciende o se apaga. GamepadSessionIsolation lo
    /// vigila para reasignar los mandos sin esperar a su rescan periódico.
    /// </summary>
    private volatile int _generation;
    public int Generation => _generation;

    /// <summary>
    /// Sesión de Windows del puesto activo que tiene asignado ese puerto USB, o false si
    /// ningún puesto encendido lo reclama (entonces el dispositivo va a la consola).
    /// </summary>
    public bool TryGetSeatWindowsSession(string usbPort, out uint windowsSession)
    {
        windowsSession = 0;
        foreach (var seat in _seats.Values)
        {
            if (seat.State != SeatState.Running) continue;
            if (!seat.Devices.Any(d => d.UsbPort.Equals(usbPort, StringComparison.OrdinalIgnoreCase))) continue;
            if (seat.SessionId is not { } sid || sessions.Get(sid) is not { } session) continue;
            if (session.State != SessionState.Running || session.RdpSessionId == 0) continue;
            windowsSession = session.RdpSessionId;
            return true;
        }
        return false;
    }

    // ── Inventario ─────────────────────────────────────────────────────────────

    public IReadOnlyList<DisplayInfo> AvailableMonitors()
    {
        // Con pantalla dividida, un monitor con una mitad en uso sigue libre para la otra.
        var used = _seats.Values.Where(s => s.State == SeatState.Running && s.Monitor is { Area: SeatScreenArea.Full })
                                .Select(s => s.Monitor!.DevicePath).ToHashSet();
        // Un monitor libre: no está en el escritorio, o está pero no lo gestiona un puesto activo.
        // El principal (activo en 0,0, ver IsPrimary) también se ofrece, pero solo para pantalla dividida.
        return ConsoleDisplay.List().Where(d => !used.Contains(d.DevicePath) && !IsConsoleHalfDisplay(d)).ToList();
    }

    public IReadOnlyList<(string Id, string Name)> AvailableAudioOutputs() => SeatAudio.Outputs();

    /// <summary>Micrófonos que no usa otro puesto (un micrófono solo puede ir a una sesión).</summary>
    /// <param name="forSeat">Puesto que se está editando: el suyo cuenta como libre.</param>
    public IReadOnlyList<(string Id, string Name)> AvailableAudioInputs(Guid? forSeat = null)
    {
        var used = _seats.Values.Where(s => s.Id != forSeat && s.AudioInId is not null)
                                .Select(s => s.AudioInId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return SeatAudio.Inputs().Where(a => !used.Contains(a.Id)).ToList();
    }

    /// <param name="forSeat">Puesto que se está editando: sus propios dispositivos cuentan como libres.</param>
    public IReadOnlyList<InputDeviceInfo> AvailableInputDevices(Guid? forSeat = null)
    {
        var used = _seats.Values.Where(s => s.Id != forSeat)
                                .SelectMany(s => s.Devices).Select(d => d.UsbPort)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DeviceTopology.EnumerateInputDevices().Where(d => !used.Contains(d.UsbPort)).ToList();
    }

    /// <summary>El monitor de un puesto encendido muestra su sesión: no se puede probar.</summary>
    public void TestMonitor(string devicePath)
    {
        if (_seats.Values.FirstOrDefault(s => s.State is SeatState.Running or SeatState.Starting
                                              && s.Monitor?.DevicePath == devicePath) is { } busy)
            throw new InvalidOperationException($"El monitor lo está usando el puesto '{busy.Name}'.");
        SeatIdentify.TestMonitor(devicePath);
    }

    // ── CRUD ───────────────────────────────────────────────────────────────────

    public IEnumerable<Seat> GetAll() => _seats.Values.OrderBy(s => s.CreatedAt);
    public Seat? Get(Guid id) => _seats.GetValueOrDefault(id);

    public Seat Create(Seat seat)
    {
        if (string.IsNullOrWhiteSpace(seat.Name)) throw new ArgumentException("El puesto necesita un nombre.");
        seat.Id = seat.Id == Guid.Empty ? Guid.NewGuid() : seat.Id;
        Validate(seat, seat.Id);
        _seats[seat.Id] = seat;
        Save();
        Logger.Log($"[Seats] Puesto creado: '{seat.Name}'.");
        return seat;
    }

    public Seat Update(Guid id, Seat changes)
    {
        var seat = Require(id);
        if (string.IsNullOrWhiteSpace(changes.Name)) throw new ArgumentException("El puesto necesita un nombre.");
        if (seat.State is SeatState.Running or SeatState.Starting)
        {
            // Encendido solo se cambia lo que no toca el hardware ni la sesión: nombre y autoarranque.
            if (!SameHardware(seat, changes))
                throw new InvalidOperationException("Detén el puesto antes de cambiar su pantalla, audio, dispositivos o sesión.");
            if (changes.AutoStart && UsesConsoleUser(seat.SessionId)) throw new ArgumentException(ConsoleUserAutoStartError);
            seat.Name      = changes.Name;
            seat.AutoStart = changes.AutoStart;
            Save();
            return seat;
        }
        Validate(changes, id);
        seat.Name             = changes.Name;
        seat.Monitor          = changes.Monitor;
        seat.AudioOutId       = changes.AudioOutId;
        seat.AudioOutName     = changes.AudioOutName;
        seat.AudioInId        = changes.AudioInId;
        seat.AudioInName      = changes.AudioInName;
        seat.Devices          = changes.Devices;
        seat.SessionId        = changes.SessionId;
        seat.AutoStart        = changes.AutoStart;
        seat.ExclusiveSession = changes.ExclusiveSession;
        Save();
        return seat;
    }

    /// <summary>Misma pantalla, audio, dispositivos, sesión y modo exclusivo: lo que exige apagar el puesto para cambiarlo.</summary>
    private static bool SameHardware(Seat a, Seat b)
    {
        static string Monitor(SeatMonitor? m) =>
            m is null ? "" : $"{m.DevicePath}|{m.Area}|{m.Width}|{m.Height}|{m.RefreshHz}";
        static string Devices(Seat s) =>
            string.Join(",", s.Devices.Select(d => $"{d.Kind}:{d.UsbPort}").OrderBy(x => x, StringComparer.Ordinal));
        return Monitor(a.Monitor) == Monitor(b.Monitor) &&
               (a.AudioOutId ?? "") == (b.AudioOutId ?? "") &&
               (a.AudioInId ?? "") == (b.AudioInId ?? "") &&
               Devices(a) == Devices(b) &&
               a.SessionId == b.SessionId &&
               a.ExclusiveSession == b.ExclusiveSession;
    }

    /// <summary>El autoarranque solo afecta al próximo arranque del servicio: se puede cambiar con el puesto encendido.</summary>
    public Seat SetAutoStart(Guid id, bool autoStart)
    {
        var seat = Require(id);
        if (autoStart && UsesConsoleUser(seat.SessionId)) throw new ArgumentException(ConsoleUserAutoStartError);
        seat.AutoStart = autoStart;
        Save();
        return seat;
    }

    /// <summary>
    /// Un monitor (o una mitad, con pantalla dividida), un puerto USB o una sesión solo pueden
    /// pertenecer a un puesto: dos puestos pintando en el mismo sitio o metiendo entrada en la
    /// misma sesión se pisarían.
    /// </summary>
    private void Validate(Seat candidate, Guid selfId)
    {
        if (candidate.AutoStart && UsesConsoleUser(candidate.SessionId))
            throw new ArgumentException(ConsoleUserAutoStartError);
        if (candidate.Monitor is { Area: SeatScreenArea.Full } full &&
            ConsoleDisplay.List().Any(d => d.DevicePath == full.DevicePath && IsConsoleMonitor(d)))
            throw new ArgumentException("El monitor principal es de la consola: un puesto solo puede ocupar una mitad.");
        foreach (var other in _seats.Values.Where(s => s.Id != selfId))
        {
            if (candidate.Monitor is { } m && other.Monitor is { } om && m.Overlaps(om))
                throw new ArgumentException(om.Area == SeatScreenArea.Full
                    ? $"El monitor ya está asignado al puesto '{other.Name}'."
                    : $"Esa parte del monitor se solapa con la del puesto '{other.Name}'.");
            if (candidate.SessionId is { } sid && other.SessionId == sid)
                throw new ArgumentException($"La sesión ya está asignada al puesto '{other.Name}'.");
            if (candidate.AudioInId is { } mic && string.Equals(other.AudioInId, mic, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"El micrófono ya está asignado al puesto '{other.Name}'.");
            var shared = candidate.Devices.FirstOrDefault(d =>
                other.Devices.Any(o => o.UsbPort.Equals(d.UsbPort, StringComparison.OrdinalIgnoreCase)));
            if (shared is not null)
                throw new ArgumentException($"El dispositivo '{shared.Label}' ya está asignado al puesto '{other.Name}'.");
        }
    }

    public void Delete(Guid id)
    {
        var seat = Require(id);
        if (seat.State == SeatState.Running) Stop(id);
        _seats.TryRemove(id, out _);
        Save();
        Logger.Log($"[Seats] Puesto borrado: '{seat.Name}'.");
    }

    // ── Encendido / apagado ──────────────────────────────────────────────────

    public void Start(Guid id)
    {
        var seat = Require(id);
        if (seat.State == SeatState.Running) return;
        if (seat.SessionId is not { } sessionId) throw new InvalidOperationException("El puesto no tiene sesión asignada.");
        var session = sessions.Get(sessionId) ?? throw new InvalidOperationException("La sesión asignada no existe.");

        seat.State = SeatState.Starting;
        seat.ErrorMessage = null;
        _ = Task.Run(() => DoStart(seat, session));
    }

    private void DoStart(Seat seat, StreamSession session)
    {
        try
        {
            // 1. Arrancar la sesión si hace falta.
            if (session.State is not (SessionState.Running or SessionState.Starting))
            {
                Status(seat, "Arrancando la sesión…");
                sessions.Start(session.Id);
            }
            WaitSessionRunning(session);

            // 2. Modo exclusivo: la sesión deja de servirse por Moonlight/WebRTC.
            if (seat.ExclusiveSession)
                sessions.PauseSunshine(session.Id, $"puesto '{seat.Name}'");

            // 3. Encender el monitor del puesto (añadirlo al escritorio). Si es el principal y la
            //    consola tenía una parte, se la quita: al terminar se rehace con lo que quede libre
            //    (si no queda nada, la consola queda detrás solo como anfitriona).
            if (seat.Monitor is not null)
            {
                if (_consoleHalf?.MonitorPath == seat.Monitor.DevicePath) StopConsoleHalf();
                Status(seat, "Encendiendo el monitor…");
                if (!ConsoleDisplay.Activate(seat.Monitor.DevicePath))
                    Logger.Warning($"[Seats] '{seat.Name}': no se pudo activar el monitor; se presentará igualmente si ya estaba activo.");
            }

            // 4. Ajustar la pantalla RDP al monitor: la resolución fijada en el puesto o, si
            //    no hay, la que Windows le haya dado al encenderlo.
            //    Tras un reajuste (_resized puesto) la sesión puede estar en otro modo aunque el
            //    nuevo coincida con el suyo: se reconecta igualmente.
            //    El micrófono del puesto también va en la conexión RDP: si lo hay, se reconecta
            //    siempre (y al apagar se reconecta sin él).
            string? mic = MicrophoneFor(seat);
            var target = seat.Monitor is { } mon && ResolveMode(mon, session) is { } m
                ? m : (Width: session.RdpWidth, Height: session.RdpHeight, Hz: session.RdpFrameRate);
            bool own = target.Width == session.RdpWidth && target.Height == session.RdpHeight && target.Hz == session.RdpFrameRate;
            if (!own || mic is not null || _resized.ContainsKey(seat.Id))
            {
                Status(seat, mic is null
                    ? $"Ajustando la sesión a {target.Width}x{target.Height} @ {target.Hz} Hz…"
                    : $"Conectando la sesión a {target.Width}x{target.Height} @ {target.Hz} Hz con el micrófono…");
                sessions.ReconnectRdp(session.Id, target.Width, target.Height, target.Hz, mic);
            }
            if (own && mic is null) _resized.TryRemove(seat.Id, out _);
            else _resized[seat.Id] = 0;

            // 5. Mandos del puesto → la sesión (lo aplica GamepadSessionIsolation con los
            //    puertos USB de seat.Devices; aquí solo se registra la intención).
            Logger.Log($"[Seats] '{seat.Name}': {seat.Devices.Count} dispositivo(s) asignados a la sesión {session.RdpSessionId}.");

            // 6. Lanzar agente en la sesión y presentador en la consola.
            //    Si el puesto pide contraseña, el agente corre como SYSTEM en la sesión: es la única
            //    forma de capturar la pantalla de bloqueo (escritorio seguro) e inyectarle el teclado.
            bool requiresLogin = RequiresLogin(seat, session);
            var activeSession = OpenPresentation(seat, session.RdpSessionId, asSystem: requiresLogin, agentArgs: "",
                // Antes de que el monitor muestre nada: el puesto arranca en la pantalla de bloqueo.
                beforePresenter: requiresLogin ? () => LockSession(seat, session) : null);
            _activeSeats[seat.Id] = activeSession;
            StartBridge(activeSession, seat);

            // 7. Ratones y teclados del puesto: fuera de la consola (WinUSB) y directos a la sesión.
            CaptureInputDevices(seat, activeSession);

            // La consola no puede bloquearse mientras haya puestos: su pantalla de bloqueo
            // taparía también los monitores de los puestos.
            ConsoleLockGuard.Engage();

            seat.State = SeatState.Running;
            seat.StatusDetail = "Activo: imagen, audio y periféricos conectados.";
            Interlocked.Increment(ref _generation);   // que GamepadSessionIsolation reasigne los mandos
            Logger.Log($"[Seats] Puesto '{seat.Name}' activo.");

            if (seat.Monitor is not null) TryStartConsoleArea(seat.Monitor);
        }
        catch (Exception ex)
        {
            seat.State = SeatState.Error;
            seat.ErrorMessage = ex.Message;
            Logger.Error($"[Seats] '{seat.Name}' no pudo arrancar: {ex.Message}");
            try { Stop(seat.Id); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Lanza el agente en la sesión de Windows <paramref name="windowsSession"/> y el presentador en
    /// la consola, le pasa al presentador las texturas del agente y devuelve el conjunto conectado
    /// (sin puente: lo arranca quien lo llama).
    /// </summary>
    /// <param name="agentArgs">Argumentos extra de --seat-agent (p. ej. --output para la consola).</param>
    /// <param name="beforePresenter">Se ejecuta con el agente ya capturando y antes de mostrar nada.</param>
    /// <param name="extraInit">Campos añadidos al mensaje init del presentador.</param>
    private ActiveSeatSession OpenPresentation(Seat seat, uint windowsSession, bool asSystem, string agentArgs,
                                               Action? beforePresenter = null, JsonObject? extraInit = null)
    {
        Status(seat, "Iniciando agente de captura e imagen...");
        var agentPipe = SeatPipes.NewName("agent");
        var agentServer = SeatPipes.CreateServer(agentPipe);
        int agentPid = new ProcessInSession().LaunchInSession(
            windowsSession,
            Environment.ProcessPath!,
            $"--seat-agent {agentPipe} {agentArgs}".TrimEnd(),
            asSystem: asSystem);

        using var agentTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        SeatPipes.AcceptFrom(agentServer, pid => pid == agentPid, "agente", agentTimeoutCts.Token);
        var agentChannel = new SeatChannel(agentServer);

        var texturesMsg = agentChannel.Receive();
        if (texturesMsg is null || texturesMsg.Value.Type != SeatMsg.Json)
            throw new InvalidOperationException("El agente no envió la configuración de texturas.");

        var texJson = SeatChannel.ParseJson(texturesMsg.Value.Data);
        int width = texJson["width"]?.GetValue<int>() ?? 1920;
        int height = texJson["height"]?.GetValue<int>() ?? 1080;
        uint luidLow = texJson["luidLow"]?.GetValue<uint>() ?? 0;
        int luidHigh = texJson["luidHigh"]?.GetValue<int>() ?? 0;
        var agentHandles = texJson["handles"]?.AsArray().Select(h => h!.GetValue<long>()).ToArray()
            ?? throw new InvalidOperationException("El agente no proporcionó handles de texturas.");

        beforePresenter?.Invoke();

        // Preparar tubería para SeatHost
        var hostPipe = SeatPipes.NewName("host");
        var hostServer = SeatPipes.CreateServer(hostPipe);

        Status(seat, "Iniciando presentador en la consola...");
        SeatHostControl.RequestOpenSeat(seat.Id, hostPipe, seat.Monitor, seat.AudioOutId, seat.Devices);

        // El presentador (icono de bandeja o --seat-host) corre en la consola. Las texturas,
        // que muestran el escritorio de la sesión, se duplican al proceso que está realmente
        // al otro lado de la tubería, no al PID que diga el mensaje.
        using var hostTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        int hostPid = SeatPipes.AcceptFrom(hostServer, LocalCaller.IsInConsoleSession, "presentador", hostTimeoutCts.Token);
        var hostChannel = new SeatChannel(hostServer);

        var hostReadyMsg = hostChannel.Receive();
        if (hostReadyMsg is null || hostReadyMsg.Value.Type != SeatMsg.Json)
            throw new InvalidOperationException("El presentador no envió confirmación.");

        // Duplicar handles de textura: agentPid -> hostPid
        var dupHandles = new long[agentHandles.Length];
        for (int i = 0; i < agentHandles.Length; i++)
        {
            IntPtr dup = HandleBroker.DuplicateHandleTo(agentPid, new IntPtr(agentHandles[i]), hostPid);
            dupHandles[i] = dup.ToInt64();
        }

        // Enviar handles duplicados y configuración inicial a SeatHost
        var init = new JsonObject
        {
            ["op"] = "init",
            ["seatId"] = seat.Id.ToString(),
            ["width"] = width,
            ["height"] = height,
            ["luidLow"] = luidLow,
            ["luidHigh"] = luidHigh,
            ["handles"] = new JsonArray(dupHandles.Select(h => (JsonNode)h).ToArray()),
            // El presentador independiente (--seat-host, sin TrayApp) solo sabe esto por aquí.
            ["monitor"] = seat.Monitor is not null ? JsonSerializer.SerializeToNode(seat.Monitor) : null,
            ["audioOutId"] = seat.AudioOutId,
            ["devices"] = JsonSerializer.SerializeToNode(seat.Devices),
        };
        foreach (var (k, v) in extraInit ?? []) init[k] = v?.DeepClone();
        hostChannel.SendJson(init);

        return new ActiveSeatSession
        {
            AgentServer = agentServer,
            AgentChannel = agentChannel,
            HostServer = hostServer,
            HostChannel = hostChannel,
            Cts = new CancellationTokenSource(),
            AgentPid = agentPid,
            HostPid = hostPid
        };
    }

    /// <param name="disconnectedFromSeat">
    /// El usuario pulsó Desconectar dentro del puesto. El puesto se apaga, pero la sesión se
    /// reconecta siempre en segundo plano a su resolución: desconectada, Windows le retira la
    /// pantalla y Sunshine no arranca ("Unable to find display", Moonlight da 503).
    /// </param>
    /// <param name="restarting">
    /// Se apaga para volver a encenderlo enseguida (<see cref="Readjust"/>): el monitor sigue en
    /// el escritorio, la sesión no vuelve a su resolución y Sunshine sigue en pausa.
    /// </param>
    /// <summary>
    /// Id de endpoint del micrófono del puesto para FreeRDP (<c>/microphone:sys:winmm,dev:</c>, con
    /// el parche de FreeRDP/patches); null si no tiene o no está conectado (entonces el puesto
    /// arranca sin micrófono y se avisa en el log).
    /// </summary>
    private static string? MicrophoneFor(Seat seat)
    {
        if (seat.AudioInId is not { } id) return null;
        if (SeatAudio.Inputs().Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return id;
        Logger.Warning($"[Seats] '{seat.Name}': el micrófono '{seat.AudioInName}' no está conectado; el puesto arranca sin micrófono.");
        return null;
    }

    public void Stop(Guid id, bool disconnectedFromSeat = false, bool restarting = false)
    {
        var seat = Require(id);

        // Antes que nada: el monitor físico vuelve a ser el principal de la consola.
        if (seat.Monitor is { } sm && _consoleHalf?.MonitorPath == sm.DevicePath) StopConsoleHalf();

        if (_activeSeats.TryRemove(id, out var active))
        {
            active.Dispose();
            foreach (var node in active.Captured)
                UsbCapture.Release(node);
        }
        SeatHostControl.RequestCloseSeat(id);

        // Con pantalla dividida, el monitor sigue en el escritorio mientras la otra mitad esté en uso.
        if (seat.Monitor is { } mon && !restarting && !SharesMonitorWithActiveSeat(seat))
            ConsoleDisplay.Deactivate(mon.DevicePath);

        if (seat.SessionId is { } sid && !restarting)
        {
            // Devolver la sesión a su resolución propia si el puesto la cambió.
            if ((_resized.TryRemove(seat.Id, out _) | disconnectedFromSeat) && sessions.Get(sid) is { State: SessionState.Running } s)
            {
                try { sessions.ReconnectRdp(sid, s.RdpWidth, s.RdpHeight, s.RdpFrameRate); }
                catch (Exception ex) { Logger.Warning($"[Seats] '{seat.Name}': no se pudo restaurar la resolución de la sesión: {ex.Message}"); }
            }
            if (seat.ExclusiveSession) sessions.ResumeSunshine(sid);
        }

        seat.State = SeatState.Stopped;
        seat.StatusDetail = disconnectedFromSeat ? "Desconectado desde el puesto. Enciéndelo de nuevo desde OpenStreamMS." : null;
        if (!restarting && !_seats.Values.Any(s => s.State is SeatState.Running or SeatState.Starting))
            ConsoleLockGuard.Release();
        Interlocked.Increment(ref _generation);   // los mandos del puesto vuelven a la consola
        Logger.Log($"[Seats] Puesto '{seat.Name}' detenido.");

        // Si quedan puestos en el principal, la consola vuelve con la parte que ahora quede libre.
        if (!restarting && seat.Monitor is { } m &&
            _seats.Values.FirstOrDefault(s => s.Id != id && s.State == SeatState.Running && s.Monitor?.DevicePath == m.DevicePath) is { } other)
            _ = Task.Run(() => TryStartConsoleArea(other.Monitor!));
    }

    /// <summary>
    /// Pasa los ratones y teclados del puesto a WinUSB y los lee desde aquí: la consola deja de
    /// recibirlos por completo. Si alguno no se puede capturar, sigue por Raw Input (el
    /// presentador de la consola lo reenvía, con el escudo).
    /// </summary>
    private void CaptureInputDevices(Seat seat, ActiveSeatSession active)
    {
        var nodes = seat.Devices.Where(d => d.Kind is SeatDeviceKind.Mouse or SeatDeviceKind.Keyboard)
                                .SelectMany(d => UsbCapture.FindTargets(d.UsbPort))
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (nodes.Count == 0) return;

        Status(seat, "Separando el ratón y el teclado del puesto…");
        foreach (var node in nodes)
        {
            if (!UsbCapture.Capture(node))
            {
                Logger.Warning($"[Seats] '{seat.Name}': {node} sigue compartido con la consola (Raw Input).");
                continue;
            }
            active.Captured.Add(node);
            int sent = 0;
            bool failLogged = false;
            active.Readers.Add(new UsbHidReader(node, ev =>
            {
                try
                {
                    ev.Qpc = Stopwatch.GetTimestamp();   // el informe acaba de llegar del aparato
                    active.AgentChannel.Send(SeatMsg.Input, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref ev, 1)));
                    if (++sent is 1 or 100 or 1000)
                        Logger.Log($"[SeatUsb] {sent} evento(s) enviados al agente de '{seat.Name}' (último: tipo={ev.Kind} flags={ev.Flags:X} dx={ev.Dx} dy={ev.Dy}).");
                }
                catch (Exception ex) when (!active.Cts.IsCancellationRequested)
                {
                    if (!failLogged) { failLogged = true; Logger.Warning($"[SeatUsb] No se pudo enviar la entrada al agente de '{seat.Name}': {ex.Message}"); }
                }
                catch { /* el puesto se está apagando */ }
            }));
        }
        Logger.Log($"[Seats] '{seat.Name}': {active.Captured.Count} de {nodes.Count} interfaz(es) de entrada separadas de la consola.");
    }

    // ── Parte de la consola ─────────────────────────────────────────────────────
    //
    // Los puestos del monitor principal dejan el resto a la consola. Para que la consola tenga un
    // escritorio de verdad de ese tamaño (barra de tareas, Inicio, notificaciones, pantalla
    // completa), se le crea una pantalla virtual (SudoVDA) de esa parte y se hace principal; un
    // agente la captura y el presentador la pinta en su parte del monitor físico, igual que un
    // puesto. El cursor de la consola se queda en la pantalla virtual (ConsoleShare).
    // La consola se queda la parte libre más grande (SeatAreas.Cover); si lo libre tiene forma de
    // L, lo que sobra se tapa de negro para que no asome el escritorio físico de detrás.
    // Cada vez que un puesto del principal se enciende o se apaga, se rehace.
    // Sin SudoVDA, con mitades queda el respaldo de la barra de aplicación (ConsoleShare.Set).

    private sealed class ConsoleHalf
    {
        public required string MonitorPath { get; init; }
        public required VirtualDisplay Display { get; init; }
        public required Seat Presentation { get; init; }
        public ActiveSeatSession? Active { get; set; }
    }

    private readonly object _consoleHalfLock = new();
    private ConsoleHalf? _consoleHalf;

    /// <summary>La pantalla virtual de la consola: no es un monitor que se pueda asignar.</summary>
    private bool IsConsoleHalfDisplay(DisplayInfo d) =>
        _consoleHalf is { } h && d.AdapterLow == h.Display.AdapterLow && d.TargetId == h.Display.TargetId;

    /// <summary>
    /// Monitor principal de la consola: el que está en (0,0) o, con la parte de la consola activa,
    /// el físico que la pantalla virtual ha desplazado.
    /// </summary>
    public bool IsConsoleMonitor(DisplayInfo d) =>
        _consoleHalf?.MonitorPath == d.DevicePath || (IsPrimary(d) && !IsConsoleHalfDisplay(d));

    /// <summary>
    /// Si el monitor es el principal y le quedan celdas libres, la consola pasa a la parte libre
    /// más grande con su propia pantalla virtual. Si algo falla, los puestos siguen igual.
    /// </summary>
    private void TryStartConsoleArea(SeatMonitor mon)
    {
        lock (_consoleHalfLock)
        {
            if (_consoleHalf is not null) return;
            var on = _seats.Values.Where(s => s.State is SeatState.Running or SeatState.Starting
                                              && s.Monitor?.DevicePath == mon.DevicePath).ToList();
            int free = SeatAreas.AllCells;
            foreach (var s in on) free &= ~SeatAreas.Cells(s.Monitor!.Area);
            var parts = SeatAreas.Cover(free);
            if (on.Count == 0 || parts.Count == 0) return;
            var live = ConsoleDisplay.List().FirstOrDefault(d => d.DevicePath == mon.DevicePath);
            if (live is null || !IsPrimary(live) || live.Width <= 0 || live.Height <= 0) return;
            if (!ConsoleSession.TryGetUser(out var consoleId, out _, out _)) return;

            var area = parts[0];
            var presentation = new Seat
            {
                Id = Guid.NewGuid(), Name = "Consola", State = SeatState.Running,
                Monitor = new SeatMonitor { DevicePath = mon.DevicePath, FriendlyName = mon.FriendlyName, Area = area },
            };
            var (_, _, w, h) = presentation.Monitor.Region(0, 0, live.Width, live.Height);
            var display = VirtualDisplay.TryCreate(w, h, live.RefreshHz > 0 ? live.RefreshHz : 60);
            if (display is null) return;   // respaldo: barra de aplicación

            var half = new ConsoleHalf { MonitorPath = mon.DevicePath, Display = display, Presentation = presentation };
            _consoleHalf = half;
            try
            {
                // La pantalla tarda un poco en aparecer en la consola.
                string id = $"target:{display.AdapterLow}:{display.TargetId}";
                DisplayInfo? vdd = null;
                for (int i = 0; i < 25 && vdd is not { Active: true, Gdi: not null }; i++)
                {
                    Thread.Sleep(200);
                    vdd = ConsoleDisplay.List().FirstOrDefault(IsConsoleHalfDisplay);
                }
                if (vdd is not { Active: true, Gdi: { } gdi })
                    throw new InvalidOperationException("la pantalla virtual no apareció en la consola");
                if (!ConsoleDisplay.MakePrimary(id, gatherFrom: mon.DevicePath))
                    throw new InvalidOperationException("no se pudo hacer principal la pantalla virtual");

                // Agente como SYSTEM: también captura el escritorio seguro (UAC) de la consola.
                // "blank": partes libres que no son de la consola (forma de L), tapadas de negro.
                half.Active = OpenPresentation(presentation, consoleId, asSystem: true, agentArgs: $"--output {gdi} --no-audio",
                    extraInit: new JsonObject
                    {
                        ["consoleClip"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = vdd.Width, ["h"] = vdd.Height },
                        ["blank"] = new JsonArray(parts.Skip(1).Select(p => (JsonNode)p.ToString()).ToArray()),
                    });
                StartBridge(half.Active, presentation);
                Logger.Log($"[Seats] La consola ocupa la parte {area} de '{mon.FriendlyName}' con su propia pantalla de {vdd.Width}x{vdd.Height}" +
                           (parts.Count > 1 ? $"; tapado: {string.Join(", ", parts.Skip(1))}." : "."));
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Seats] No se pudo dar a la consola su parte ({ex.Message}); queda el respaldo.");
                StopConsoleHalf();
            }
        }
    }

    /// <summary>Devuelve el monitor físico a la consola y quita la pantalla virtual.</summary>
    private void StopConsoleHalf()
    {
        lock (_consoleHalfLock)
        {
            if (_consoleHalf is not { } half) return;
            half.Presentation.State = SeatState.Stopped;
            half.Active?.Dispose();
            SeatHostControl.RequestCloseSeat(half.Presentation.Id);
            // Primero el físico vuelve a ser el principal; después se quita la virtual.
            if (!ConsoleDisplay.MakePrimary(half.MonitorPath))
                Logger.Warning("[Seats] No se pudo devolver el monitor principal a la consola; se recupera al quitar la pantalla virtual.");
            half.Display.Dispose();
            _consoleHalf = null;
            Logger.Log("[Seats] La consola vuelve a tener su monitor entero.");
        }
    }

    // ── Usuario del puesto: contraseña y autoarranque ──────────────────────────

    private const string ConsoleUserAutoStartError =
        "Este puesto usa el mismo usuario que la consola: no puede arrancar solo. Enciéndelo desde OpenStreamMS o asígnale otra sesión.";

    /// <summary>Usuario de la consola: el que tiene sesión ahora o, si aún no, el del inicio automático.</summary>
    private static string? ConsoleUserName()
    {
        if (ConsoleSession.TryGetUser(out _, out _, out var user)) return user;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
        return key?.GetValue("DefaultUserName") as string is { Length: > 0 } u ? u : null;
    }

    /// <summary>La sesión del puesto es del mismo usuario de Windows que la consola (no aislada).</summary>
    private bool UsesConsoleUser(Guid? sessionId)
    {
        if (sessionId is not { } sid || sessions.Get(sid) is not { } s || s.Isolated) return false;
        var console = ConsoleUserName();
        string user = s.Username.Contains('\\') ? s.Username[(s.Username.LastIndexOf('\\') + 1)..] : s.Username;
        return console is not null && string.Equals(user, console, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pide la contraseña al encender: puesto exclusivo con un usuario normal de Windows que no
    /// es el de la consola. Con usuario aislado/efímero no hay contraseña que pedir; si la sesión
    /// no es exclusiva se comparte con Moonlight y no se bloquea; y el usuario de la consola no
    /// se puede bloquear (ConsoleLockGuard se lo impide mientras haya puestos).
    /// </summary>
    private bool RequiresLogin(Seat seat, StreamSession session) =>
        seat.ExclusiveSession && !session.Isolated && !UsesConsoleUser(seat.SessionId);

    /// <summary>Bloquea la sesión del puesto desde dentro (LockWorkStation con el usuario de la sesión).</summary>
    private void LockSession(Seat seat, StreamSession session)
    {
        try
        {
            Status(seat, "Bloqueando la sesión: el puesto pedirá la contraseña…");
            new ProcessInSession().LaunchInSession(session.RdpSessionId,
                Path.Combine(Environment.SystemDirectory, "rundll32.exe"), "user32.dll,LockWorkStation",
                waitForExit: true, waitTimeoutMs: 5000);
            Logger.Log($"[Seats] '{seat.Name}': sesión bloqueada; pedirá la contraseña de '{session.Username}'.");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seats] '{seat.Name}': no se pudo bloquear la sesión: {ex.Message}");
        }
    }

    // ── Desconexión desde el puesto ─────────────────────────────────────────────

    private const int WTSConnectState = 8, WTSActive = 0;
    private const int DisconnectGraceMs = 3000;   // el propio ReconnectRdp desconecta ~1,5 s
    private readonly ConcurrentDictionary<Guid, long> _disconnectedSince = new();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>Sesión con un cliente conectado (false si está desconectada o ya no existe).</summary>
    private static bool IsSessionConnected(uint windowsSession)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, windowsSession, WTSConnectState, out var buf, out _)) return false;
        try { return Marshal.ReadInt32(buf) == WTSActive; }
        finally { WTSFreeMemory(buf); }
    }

    /// <summary>
    /// Pulsar Desconectar (o cerrar sesión) dentro de un puesto lo apaga entero: monitor fuera,
    /// ratón y teclado devueltos a la consola, y queda apagado hasta que se encienda desde aquí.
    /// </summary>
    private void WatchSeatSessions()
    {
        foreach (var seat in _seats.Values)
        {
            if (seat.State != SeatState.Running || seat.SessionId is not { } sid ||
                sessions.Get(sid) is not { RdpSessionId: > 0 } session || IsSessionConnected(session.RdpSessionId))
            {
                _disconnectedSince.TryRemove(seat.Id, out _);
                continue;
            }
            long now = Environment.TickCount64;
            if (now - _disconnectedSince.GetOrAdd(seat.Id, now) < DisconnectGraceMs) continue;

            _disconnectedSince.TryRemove(seat.Id, out _);
            Logger.Log($"[Seats] '{seat.Name}': su sesión se desconectó desde el puesto; se apaga hasta que se encienda desde OpenStreamMS.");
            try { Stop(seat.Id, disconnectedFromSeat: true); }
            catch (Exception ex) { Logger.Warning($"[Seats] '{seat.Name}': no se pudo apagar tras la desconexión: {ex.Message}"); }
        }
    }

    /// <summary>
    /// El presentador avisa de que el monitor del puesto cambió de tamaño (PIP, otro modo):
    /// se rehace el puesto sin apagar el monitor, y al encenderse la sesión toma su modo nuevo.
    /// </summary>
    private void Readjust(Guid id)
    {
        if (!_readjusting.TryAdd(id, 0)) return;
        try
        {
            var seat = Require(id);
            if (seat.State != SeatState.Running) return;
            Logger.Log($"[Seats] '{seat.Name}': el monitor cambió de modo; se reajusta la sesión.");
            Stop(id, restarting: true);
            Start(id);
        }
        catch (Exception ex) { Logger.Warning($"[Seats] No se pudo reajustar el puesto: {ex.Message}"); }
        finally { _readjusting.TryRemove(id, out _); }
    }

    private readonly ConcurrentDictionary<Guid, byte> _readjusting = new();

    private void StartBridge(ActiveSeatSession active, Seat seat)
    {
        // Agente -> Anfitrión (vídeo, audio, cursor, control)
        Task.Run(() =>
        {
            try
            {
                while (!active.Cts.IsCancellationRequested)
                {
                    var msg = active.AgentChannel.Receive();
                    if (msg is null) break;
                    active.HostChannel.Send(msg.Value.Type, msg.Value.Data);
                }
            }
            catch { }
            finally
            {
                if (seat.State == SeatState.Running && !active.Cts.IsCancellationRequested)
                {
                    Logger.Warning($"[Seats] '{seat.Name}': el agente de sesión se desconectó.");
                    OnPresentationLost(seat, "se cerró el agente de captura de la sesión");
                }
            }
        }, active.Cts.Token);

        // Anfitrión -> Agente (entrada, control)
        Task.Run(() =>
        {
            try
            {
                while (!active.Cts.IsCancellationRequested)
                {
                    var msg = active.HostChannel.Receive();
                    if (msg is null) break;
                    if (msg.Value.Type == SeatMsg.Json &&
                        SeatChannel.ParseJson(msg.Value.Data)["op"]?.GetValue<string>() == "monitor_mode")
                    {
                        // Para el servicio, no para el agente. Fuera de este hilo: Stop cierra el canal.
                        _ = Task.Run(() => Readjust(seat.Id));
                        continue;
                    }
                    active.AgentChannel.Send(msg.Value.Type, msg.Value.Data);
                }
            }
            catch { }
            finally
            {
                if (seat.State == SeatState.Running && !active.Cts.IsCancellationRequested)
                {
                    Logger.Warning($"[Seats] '{seat.Name}': el presentador de la consola se desconectó.");
                    OnPresentationLost(seat, "se cerró el presentador de la consola (icono de bandeja)");
                }
            }
        }, active.Cts.Token);
    }

    /// <summary>
    /// El agente o el presentador se cortaron sin que nadie lo pidiera (Salir en el icono de
    /// bandeja, proceso cerrado o caído): el puesto no puede seguir a medias, se apaga del todo
    /// (teclado y ratón devueltos, resolución restaurada y, si la consola tenía su mitad, el
    /// monitor físico vuelve a ser el principal). Se enciende de nuevo desde OpenStreamMS.
    /// Los cortes pedidos (Stop, Readjust) cancelan antes el puente y no llegan aquí.
    /// </summary>
    private void OnPresentationLost(Seat seat, string why)
    {
        // Al cerrarse el icono de bandeja se cortan a la vez el agente y el presentador: un solo apagado.
        if (!_lost.TryAdd(seat.Id, 0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (_consoleHalf?.Presentation == seat)
                {
                    Logger.Warning($"[Seats] La mitad de la consola se quedó sin imagen ({why}); la consola recupera su monitor.");
                    StopConsoleHalf();
                    return;
                }
                if (!_seats.ContainsKey(seat.Id) || seat.State != SeatState.Running) return;
                Logger.Warning($"[Seats] '{seat.Name}' se apaga: {why}.");
                Stop(seat.Id);
                seat.StatusDetail = $"Apagado: {why}. Enciéndelo de nuevo desde OpenStreamMS.";
            }
            catch (Exception ex) { Logger.Warning($"[Seats] '{seat.Name}': no se pudo apagar tras el corte: {ex.Message}"); }
            finally { _lost.TryRemove(seat.Id, out _); }
        });
    }

    private readonly ConcurrentDictionary<Guid, byte> _lost = new();

    private sealed class ActiveSeatSession : IDisposable
    {
        public required NamedPipeServerStream AgentServer { get; init; }
        public required SeatChannel AgentChannel { get; init; }
        public required NamedPipeServerStream HostServer { get; init; }
        public required SeatChannel HostChannel { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public int AgentPid { get; init; }
        public int HostPid { get; init; }
        /// <summary>Nodos USB pasados a WinUSB (hay que devolverlos a su driver al apagar).</summary>
        public List<string> Captured { get; } = [];
        public List<UsbHidReader> Readers { get; } = [];

        public void Dispose()
        {
            // Primero los lectores: sueltan en la sesión lo que hubiera pulsado mientras el canal sigue abierto.
            foreach (var r in Readers) try { r.Dispose(); } catch { }
            try { Cts.Cancel(); } catch { }
            try { AgentChannel.SendJson(new JsonObject { ["op"] = "stop" }); } catch { }
            try { HostChannel.SendJson(new JsonObject { ["op"] = "stop" }); } catch { }
            try { AgentChannel.Dispose(); } catch { }
            try { AgentServer.Dispose(); } catch { }
            try { HostChannel.Dispose(); } catch { }
            try { HostServer.Dispose(); } catch { }
            try { Cts.Dispose(); } catch { }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Puestos que reconectaron su sesión a otra resolución (hay que restaurarla al apagar).</summary>
    private readonly ConcurrentDictionary<Guid, byte> _resized = new();

    /// <summary>
    /// Modo a usar para la sesión: el fijado en el puesto, o el que tiene el monitor ya
    /// encendido (su mitad, con pantalla dividida). La frecuencia, si no está fijada, es la
    /// configurada en la sesión.
    /// </summary>
    private static (int Width, int Height, int Hz)? ResolveMode(SeatMonitor mon, StreamSession session)
    {
        int hz = mon.RefreshHz > 0 ? mon.RefreshHz : session.RdpFrameRate;
        if (mon.Width > 0 && mon.Height > 0) return (mon.Width, mon.Height, hz);
        var live = ConsoleDisplay.List().FirstOrDefault(d => d.DevicePath == mon.DevicePath && d.Active);
        if (live is not { Width: > 0, Height: > 0 }) return null;
        var (_, _, w, h) = mon.Region(0, 0, live.Width, live.Height);
        return (w, h, hz);
    }

    /// <summary>El monitor principal de la consola: activo en (0,0).</summary>
    public static bool IsPrimary(DisplayInfo d) => d.Active && d.PosX == 0 && d.PosY == 0;

    /// <summary>Otro puesto encendido (o encendiéndose) usa la otra mitad del mismo monitor.</summary>
    private bool SharesMonitorWithActiveSeat(Seat seat) =>
        _seats.Values.Any(s => s.Id != seat.Id && s.State is SeatState.Running or SeatState.Starting
                               && s.Monitor?.DevicePath == seat.Monitor?.DevicePath);

    private static void WaitSessionRunning(StreamSession session, int timeoutMs = 45000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (session.State == SessionState.Running && session.RdpSessionId > 0) return;
            if (session.State == SessionState.Error) throw new Exception(session.ErrorMessage ?? "La sesión falló al arrancar.");
            Thread.Sleep(250);
        }
        throw new TimeoutException("La sesión no llegó a estar activa a tiempo.");
    }

    private void Status(Seat seat, string detail)
    {
        seat.StatusDetail = detail;
        Logger.Log($"[Seats] '{seat.Name}': {detail}");
    }

    private Seat Require(Guid id) => _seats.GetValueOrDefault(id)
        ?? throw new KeyNotFoundException($"No existe el puesto {id}.");

    private void Save() { lock (_saveLock) SeatStore.Save(_seats.Values); }
}
