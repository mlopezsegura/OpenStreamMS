using Microsoft.Extensions.Hosting;
using OpenStreamMS.Core.Api;
using OpenStreamMS.Services.Localization;
using OpenStreamMS.Services.Seats;

namespace OpenStreamMS.Services.Mosaic;

/// <summary>
/// Mosaico: una sesión que se está retransmitiendo (la anfitriona) a la que se unen en caliente
/// hasta otras tres. El cliente de Moonlight/WebRTC ve las cuatro en una cuadrícula de 2×2.
/// <list type="bullet">
/// <item>Vídeo: el Sunshine de la anfitriona lee los fotogramas RemoteIdd de todas y los compone
/// (<c>osms-mosaic.txt</c> junto a su <c>sunshine.conf</c>, ver <see cref="MosaicLayout"/>).
/// Cada sesión se reconecta al tamaño de su parte; el lienzo es la resolución de la anfitriona.</item>
/// <item>Sunshine de las invitadas: en pausa mientras están en el mosaico (como un puesto exclusivo).</item>
/// <item>Mandos: el mando N que crea el Sunshine de la anfitriona va a la sesión N por orden de
/// llegada (<see cref="PadTarget"/>, lo aplica <see cref="Gamepad.GamepadSessionIsolation"/>).</item>
/// <item>Teclado, ratón y audio: solo los de la anfitriona.</item>
/// </list>
/// El mosaico se deshace al sacar a la anfitriona, al pararse, o cuando lleva
/// <see cref="ClientGrace"/> sin ningún cliente conectado.
/// </summary>
public sealed class MosaicService(StreamSessionService sessions, SeatManager seats) : BackgroundService
{
    private static readonly TimeSpan ClientGrace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    private sealed class Group
    {
        public required Guid Host { get; init; }
        /// <summary>Por orden de llegada; [0] es la anfitriona.</summary>
        public readonly List<Guid> Members = [];
        /// <summary>Resolución RDP que tiene ahora cada miembro, para reconectar solo lo que cambia.</summary>
        public readonly Dictionary<Guid, (int W, int H)> Applied = [];
        public DateTime? NoClientSince;
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Group> _groups = [];
    /// <summary>Un cambio a la vez: cada uno reconecta sesiones RDP (~1-2 s cada una).</summary>
    private readonly SemaphoreSlim _ops = new(1, 1);
    private int _generation;

    /// <summary>Sube con cada cambio de miembros o de sesión de Windows de un miembro.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Miembros del mosaico de <paramref name="host"/> por orden de llegada; vacío si no tiene.</summary>
    public IReadOnlyList<Guid> Members(Guid host)
    {
        lock (_sync) return _groups.TryGetValue(host, out var g) ? g.Members.ToList() : [];
    }

    /// <summary>
    /// Sesión a la que va el mando número <paramref name="index"/> creado por el Sunshine de
    /// <paramref name="creator"/>: la sesión en esa posición del mosaico, o la anfitriona si no hay
    /// tantas. Null si <paramref name="creator"/> no es anfitriona de un mosaico.
    /// </summary>
    public Guid? PadTarget(Guid creator, int index)
    {
        lock (_sync)
        {
            if (!_groups.TryGetValue(creator, out var g)) return null;
            return index >= 0 && index < g.Members.Count ? g.Members[index] : creator;
        }
    }

    /// <summary>Une <paramref name="guestId"/> al mosaico de <paramref name="hostId"/> (lo crea si no existe).</summary>
    /// <exception cref="KeyNotFoundException">Alguna de las dos sesiones no existe.</exception>
    /// <exception cref="InvalidOperationException">No se puede unir; el mensaje dice por qué.</exception>
    public async Task JoinAsync(Guid hostId, Guid guestId, CancellationToken ct = default)
    {
        await _ops.WaitAsync(ct);
        try
        {
            var host  = sessions.Get(hostId)  ?? throw new KeyNotFoundException();
            var guest = sessions.Get(guestId) ?? throw new KeyNotFoundException();
            Validate(host, guest);

            if (guest.State is not (SessionState.Running or SessionState.Starting))
            {
                Logger.Log($"[Mosaico] Arrancando '{guest.Name}' para unirla al mosaico de '{host.Name}'…", guestId);
                sessions.Start(guestId);
            }
            await WaitRunningAsync(guest, ct);
            Validate(host, guest);   // pudo cambiar algo mientras arrancaba

            Group g;
            lock (_sync)
            {
                if (!_groups.TryGetValue(hostId, out g!))
                {
                    g = new Group { Host = hostId };
                    g.Members.Add(hostId);
                    g.Applied[hostId] = (host.RdpWidth, host.RdpHeight);
                    _groups[hostId] = g;
                }
                g.Members.Add(guestId);
                g.Applied[guestId] = (guest.RdpWidth, guest.RdpHeight);
                Renumber(g);
                g.NoClientSince = null;
                _generation++;
            }
            sessions.PauseSunshine(guestId, $"mosaico de '{host.Name}'");
            Logger.Log($"[Mosaico] '{guest.Name}' se une al mosaico de '{host.Name}' ({g.Members.Count}/{MosaicLayout.MaxMembers}).", hostId);

            try
            {
                Apply(g);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Mosaico] No se pudo colocar '{guest.Name}' en el mosaico de '{host.Name}': {ex.Message}", hostId);
                Remove(g, guestId);
                throw new InvalidOperationException(RequestCulture.T("error.mosaicApply", ex.Message));
            }
        }
        finally
        {
            _ops.Release();
        }
    }

    /// <summary>
    /// Saca <paramref name="memberId"/> del mosaico de <paramref name="hostId"/>. Sacar a la
    /// anfitriona deshace el mosaico.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No hay ese mosaico o la sesión no está en él.</exception>
    public async Task LeaveAsync(Guid hostId, Guid memberId, CancellationToken ct = default)
    {
        await _ops.WaitAsync(ct);
        try
        {
            Group g;
            lock (_sync)
            {
                if (!_groups.TryGetValue(hostId, out g!) || !g.Members.Contains(memberId))
                    throw new KeyNotFoundException();
            }
            if (memberId == hostId) Dissolve(g, "la anfitriona sale");
            else Remove(g, memberId);
        }
        finally
        {
            _ops.Release();
        }
    }

    private void Validate(StreamSession host, StreamSession guest)
    {
        if (host.Id == guest.Id)
            throw new InvalidOperationException(RequestCulture.T("error.mosaicSelf"));
        if (host.State != SessionState.Running || host.RdpSessionId == 0 || host.Local)
            throw new InvalidOperationException(RequestCulture.T("error.mosaicHostStopped"));
        if (guest.Local)
            throw new InvalidOperationException(RequestCulture.T("error.mosaicCapture", guest.Name));
        foreach (var s in new[] { host, guest })
        {
            if (!StreamSessionService.CapturesWithRemoteIdd(s))
                throw new InvalidOperationException(RequestCulture.T("error.mosaicCapture", s.Name));
            if (seats.GetAll().Any(seat => seat.SessionId == s.Id && seat.State is SeatState.Running or SeatState.Starting))
                throw new InvalidOperationException(RequestCulture.T("error.mosaicSeat", s.Name));
        }
        if (host.ClientsMoonlight + host.ClientsWebRtc is 0)
            throw new InvalidOperationException(RequestCulture.T("error.mosaicNoClient", host.Name));
        if (guest.ClientsMoonlight + guest.ClientsWebRtc is > 0)
            throw new InvalidOperationException(RequestCulture.T("error.mosaicGuestStreaming", guest.Name));

        lock (_sync)
        {
            if (host.MosaicHost is { } h && h != host.Id)
                throw new InvalidOperationException(RequestCulture.T("error.mosaicBusy", host.Name));
            if (guest.MosaicHost is not null || _groups.ContainsKey(guest.Id))
                throw new InvalidOperationException(RequestCulture.T("error.mosaicBusy", guest.Name));
            if (_groups.TryGetValue(host.Id, out var g) && g.Members.Count >= MosaicLayout.MaxMembers)
                throw new InvalidOperationException(RequestCulture.T("error.mosaicFull"));
        }
    }

    private static async Task WaitRunningAsync(StreamSession session, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (session.State != SessionState.Running || session.RdpSessionId == 0)
        {
            if (session.State is SessionState.Error or SessionState.Stopped || DateTime.UtcNow > deadline)
                throw new InvalidOperationException(RequestCulture.T("error.mosaicGuestStart", session.Name));
            await Task.Delay(500, ct);
        }
    }

    /// <summary>
    /// Deja cada miembro en su parte: escribe el fichero de Sunshine, reconecta las sesiones cuyo
    /// tamaño cambia y vuelve a escribirlo (una reconexión puede cambiar la sesión de Windows).
    /// </summary>
    private void Apply(Group g)
    {
        var host = sessions.Get(g.Host) ?? throw new KeyNotFoundException();
        int w = host.RdpWidth, h = host.RdpHeight, hz = host.RdpFrameRate;
        var members = g.Members.Select(id => sessions.Get(id) ?? throw new KeyNotFoundException()).ToList();
        var areas = MosaicLayout.Areas(members.Count);

        WriteFile(g, members, w, h);
        for (int i = 0; i < members.Count; i++)
        {
            var target = MosaicLayout.PartSize(areas[i], w, h);
            if (g.Applied.GetValueOrDefault(members[i].Id) == target) continue;
            sessions.ReconnectRdp(members[i].Id, target.Width, target.Height, hz);
            g.Applied[members[i].Id] = target;
            Logger.Log($"[Mosaico] '{members[i].Name}' en la parte {areas[i]} ({target.Width}x{target.Height}).", g.Host);
        }
        WriteFile(g, members, w, h);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Saca un invitado: vuelve a su resolución, se reanuda su Sunshine y se recoloca el resto.</summary>
    private void Remove(Group g, Guid id, bool reapply = true)
    {
        lock (_sync)
        {
            g.Members.Remove(id);
            Renumber(g);
            _generation++;
        }
        var s = sessions.Get(id);
        if (s is not null)
        {
            Clear(s);
            RestoreOwnSize(g, s);
            Logger.Log($"[Mosaico] '{s.Name}' sale del mosaico.", g.Host);
        }
        g.Applied.Remove(id);
        sessions.ResumeSunshine(id);

        if (!reapply) return;
        if (g.Members.Count <= 1)
        {
            End(g);
            return;
        }
        try { Apply(g); }
        catch (Exception ex) { Logger.Error($"[Mosaico] No se pudo recolocar el mosaico: {ex.Message}", g.Host); }
    }

    private void Dissolve(Group g, string reason)
    {
        Logger.Log($"[Mosaico] Se deshace el mosaico: {reason}.", g.Host);
        foreach (var id in g.Members.Skip(1).ToList()) Remove(g, id, reapply: false);
        End(g);
    }

    /// <summary>Solo queda la anfitriona: sin fichero, a su resolución y fuera de la lista.</summary>
    private void End(Group g)
    {
        DeleteFile(g.Host);
        if (sessions.Get(g.Host) is { } host)
        {
            Clear(host);
            RestoreOwnSize(g, host);
        }
        lock (_sync)
        {
            _groups.Remove(g.Host);
            _generation++;
        }
    }

    /// <summary>Anfitriona, posición y recuento de cada miembro, para el panel. Con <see cref="_sync"/> tomado.</summary>
    private void Renumber(Group g)
    {
        for (int i = 0; i < g.Members.Count; i++)
        {
            if (sessions.Get(g.Members[i]) is not { } s) continue;
            s.MosaicHost  = g.Host;
            s.MosaicIndex = i;
            s.MosaicCount = i == 0 ? g.Members.Count : null;
        }
    }

    private static void Clear(StreamSession s)
    {
        s.MosaicHost  = null;
        s.MosaicIndex = null;
        s.MosaicCount = null;
    }

    private void RestoreOwnSize(Group g, StreamSession s)
    {
        if (s.State != SessionState.Running || s.RdpSessionId == 0) return;
        if (g.Applied.GetValueOrDefault(s.Id) == (s.RdpWidth, s.RdpHeight)) return;
        try
        {
            sessions.ReconnectRdp(s.Id, s.RdpWidth, s.RdpHeight, s.RdpFrameRate);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Mosaico] '{s.Name}' no pudo volver a su resolución: {ex.Message}", g.Host);
        }
    }

    private static void WriteFile(Group g, List<StreamSession> members, int w, int h)
    {
        var path = Path.Combine(StreamSessionService.SunshineConfigDir(g.Host), MosaicLayout.FileName);
        var text = MosaicLayout.Render(w, h, members.Select(m => m.RdpSessionId).ToList());
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static void DeleteFile(Guid host)
    {
        try { File.Delete(Path.Combine(StreamSessionService.SunshineConfigDir(host), MosaicLayout.FileName)); }
        catch (Exception ex) { Logger.Warning($"[Mosaico] No se pudo borrar {MosaicLayout.FileName}: {ex.Message}", host); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Un fichero de una ejecución anterior dejaría a Sunshine en mosaico sin nadie que lo lleve.
        foreach (var s in sessions.GetAll()) DeleteFile(s.Id);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _ops.WaitAsync(stoppingToken);
            try { Watch(); }
            catch (Exception ex) { Logger.Warning($"[Mosaico] Error vigilando los mosaicos: {ex.Message}"); }
            finally { _ops.Release(); }
        }
    }

    /// <summary>Saca a las sesiones paradas y deshace los mosaicos sin anfitriona o sin cliente.</summary>
    private void Watch()
    {
        List<Group> groups;
        lock (_sync) groups = _groups.Values.ToList();
        foreach (var g in groups)
        {
            var host = sessions.Get(g.Host);
            if (host is null || host.State != SessionState.Running)
            {
                Dissolve(g, "la anfitriona se ha parado");
                continue;
            }
            foreach (var id in g.Members.Skip(1).ToList())
                if (sessions.Get(id) is not { State: SessionState.Running })
                    Remove(g, id);
            lock (_sync)
                if (!_groups.ContainsKey(g.Host)) continue;

            // Sin dato (Sunshine reiniciándose o sin el endpoint) no se cuenta como desconectado.
            if (host.ClientsMoonlight + host.ClientsWebRtc is 0)
            {
                g.NoClientSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - g.NoClientSince > ClientGrace)
                    Dissolve(g, "el cliente se ha desconectado");
            }
            else
            {
                g.NoClientSince = null;
            }
        }
    }
}
