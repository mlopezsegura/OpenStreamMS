using Microsoft.Extensions.Hosting;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Core.Helpers;
using System.Collections.Concurrent;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Gestiona los puestos locales: inventario de dispositivos libres, alta/edición/borrado,
/// y el ciclo de encendido/apagado de cada puesto (monitor, sesión asignada, audio y entrada).
/// Es el punto central de las fases F0–F4. La captura/inyección dentro de la sesión la realiza
/// el agente de sesión (ver <c>SEAT-AGENT-TODO.md</c>): este servicio lo orquesta pero no lo implementa.
/// </summary>
public sealed class SeatManager(StreamSessionService sessions) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Seat> _seats =
        new(SeatStore.Load().ToDictionary(s => s.Id));
    private readonly object _saveLock = new();

    // ── Ciclo de vida ──────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Log($"[Seats] Gestor de puestos iniciado ({_seats.Count} puesto(s)).");
        foreach (var seat in _seats.Values.Where(s => s.AutoStart && s.SessionId is not null))
        {
            try { Start(seat.Id); }
            catch (Exception ex) { Logger.Warning($"[Seats] Autoarranque de '{seat.Name}' falló: {ex.Message}"); }
        }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
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
        var used = _seats.Values.Where(s => s.State == SeatState.Running && s.Monitor is not null)
                                .Select(s => s.Monitor!.DevicePath).ToHashSet();
        // Un monitor libre: no está en el escritorio, o está pero no lo gestiona un puesto activo.
        return DisplayTopology.Enumerate().Where(d => !used.Contains(d.DevicePath)).ToList();
    }

    public IReadOnlyList<(string Id, string Name)> AvailableAudioOutputs() => SeatAudio.Outputs();

    public IReadOnlyList<InputDeviceInfo> AvailableInputDevices()
    {
        var used = _seats.Values.SelectMany(s => s.Devices).Select(d => d.UsbPort).ToHashSet();
        return DeviceTopology.EnumerateInputDevices().Where(d => !used.Contains(d.UsbPort)).ToList();
    }

    // ── CRUD ───────────────────────────────────────────────────────────────────

    public IEnumerable<Seat> GetAll() => _seats.Values.OrderBy(s => s.CreatedAt);
    public Seat? Get(Guid id) => _seats.GetValueOrDefault(id);

    public Seat Create(Seat seat)
    {
        if (string.IsNullOrWhiteSpace(seat.Name)) throw new ArgumentException("El puesto necesita un nombre.");
        seat.Id = seat.Id == Guid.Empty ? Guid.NewGuid() : seat.Id;
        _seats[seat.Id] = seat;
        Save();
        Logger.Log($"[Seats] Puesto creado: '{seat.Name}'.");
        return seat;
    }

    public Seat Update(Guid id, Seat changes)
    {
        var seat = Require(id);
        if (seat.State == SeatState.Running)
            throw new InvalidOperationException("Detén el puesto antes de editarlo.");
        seat.Name             = changes.Name;
        seat.Monitor          = changes.Monitor;
        seat.AudioOutId       = changes.AudioOutId;
        seat.AudioOutName     = changes.AudioOutName;
        seat.Devices          = changes.Devices;
        seat.SessionId        = changes.SessionId;
        seat.AutoStart        = changes.AutoStart;
        seat.ExclusiveSession = changes.ExclusiveSession;
        Save();
        return seat;
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

            // 3. Ajustar la pantalla RDP a la resolución/frecuencia del monitor del puesto.
            if (seat.Monitor is { Width: > 0, Height: > 0 } mon)
            {
                Status(seat, "Ajustando la resolución de la sesión…");
                sessions.ReconnectRdp(session.Id, mon.Width, mon.Height, mon.RefreshHz > 0 ? mon.RefreshHz : session.RdpFrameRate);
            }

            // 4. Encender el monitor del puesto (añadirlo al escritorio).
            if (seat.Monitor is not null)
            {
                Status(seat, "Encendiendo el monitor…");
                if (!DisplayTopology.Activate(seat.Monitor.DevicePath))
                    Logger.Warning($"[Seats] '{seat.Name}': no se pudo activar el monitor; se presentará igualmente si ya estaba activo.");
            }

            // 5. Mandos del puesto → la sesión (lo aplica GamepadSessionIsolation con los
            //    puertos USB de seat.Devices; aquí solo se registra la intención).
            Logger.Log($"[Seats] '{seat.Name}': {seat.Devices.Count} dispositivo(s) asignados a la sesión {session.RdpSessionId}.");

            // ───────────────────────────────────────────────────────────────────
            // TODO (F1–F3): AGENTE DE SESIÓN Y PRESENTADOR — ver docs/SEAT-AGENT-TODO.md
            //
            //   a) Lanzar el agente en la sesión del puesto, p. ej.:
            //        var pipe = SeatPipes.NewName("agent");
            //        using var server = SeatPipes.CreateServer(pipe, userSid);
            //        new ProcessInSession().LaunchInSession(session.RdpSessionId,
            //            Environment.ProcessPath!, $"--seat-agent {pipe}");
            //        var channel = new SeatChannel(server);   // tras WaitForConnection
            //
            //   b) El agente captura (DDA → anillo de texturas NT compartidas), el audio
            //      (loopback 10 ms) y el cursor, e inyecta la entrada (SetThreadDesktop +
            //      SendInput). El servicio duplica los handles de textura al proceso
            //      anfitrión (SeatHost) y reenvía audio/cursor/entrada por el canal.
            //
            //   c) SeatHost (en la consola, dentro de la app de bandeja) presenta la
            //      textura a pantalla completa en el monitor del puesto, reproduce el
            //      audio en seat.AudioOutId y manda la entrada de los dispositivos del
            //      puesto (identificados por puerto USB vía Raw Input) por el canal.
            //
            // Esta pieza NO está implementada aquí (ver nota en el README del proyecto).
            // Todo lo anterior y posterior sí funciona: el monitor se enciende, la sesión
            // se ajusta y el audio/entrada quedan listos para cuando el agente exista.
            // ───────────────────────────────────────────────────────────────────

            seat.State = SeatState.Running;
            seat.StatusDetail = "Activo (falta el agente de sesión para ver imagen y usar la entrada).";
            Interlocked.Increment(ref _generation);   // que GamepadSessionIsolation reasigne los mandos
            Logger.Log($"[Seats] Puesto '{seat.Name}' activo.");
        }
        catch (Exception ex)
        {
            seat.State = SeatState.Error;
            seat.ErrorMessage = ex.Message;
            Logger.Error($"[Seats] '{seat.Name}' no pudo arrancar: {ex.Message}");
            try { Stop(seat.Id); } catch { /* best effort */ }
        }
    }

    public void Stop(Guid id)
    {
        var seat = Require(id);
        // TODO (F1–F3): parar el agente de sesión y el presentador (op "stop" por el canal).

        if (seat.Monitor is not null)
            DisplayTopology.Deactivate(seat.Monitor.DevicePath);

        if (seat.SessionId is { } sid && seat.ExclusiveSession)
            sessions.ResumeSunshine(sid);

        seat.State = SeatState.Stopped;
        seat.StatusDetail = null;
        Interlocked.Increment(ref _generation);   // los mandos del puesto vuelven a la consola
        Logger.Log($"[Seats] Puesto '{seat.Name}' detenido.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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
