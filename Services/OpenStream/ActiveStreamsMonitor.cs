using OpenStreamMS.Core.Api;

namespace OpenStreamMS.Services.OpenStream;

/// <summary>
/// Pregunta cada 5 s a cada Sunshine activo cuántos clientes están transmitiendo
/// (<c>GET /api/streams</c>, del fork sunshine-webrtc) y lo deja en la sesión para el panel.
/// Un Sunshine sin ese endpoint (404) se marca como no compatible y no se le vuelve a preguntar
/// hasta que se relance.
/// </summary>
public sealed class ActiveStreamsMonitor(StreamSessionService sessions, SunshineProxy proxy) : BackgroundService
{
    private readonly Dictionary<Guid, int> _unsupportedPid = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var s in sessions.GetAll())
            {
                if (s.State != SessionState.Running || s.SunshinePid == 0)
                {
                    s.ClientsMoonlight = s.ClientsWebRtc = null;
                    continue;
                }
                if (_unsupportedPid.TryGetValue(s.Id, out var pid) && pid == s.SunshinePid) continue;

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(3));
                    var (status, body) = await proxy.SendJsonAsync(s, HttpMethod.Get, "/api/streams", ct: cts.Token);
                    if (status == 404)
                    {
                        _unsupportedPid[s.Id] = s.SunshinePid;
                        s.ClientsMoonlight = s.ClientsWebRtc = null;
                    }
                    else if (status is >= 200 and < 300 && body is { } b &&
                             b.TryGetProperty("moonlight", out var m) && b.TryGetProperty("webrtc", out var w))
                    {
                        s.ClientsMoonlight = m.GetInt32();
                        s.ClientsWebRtc = w.GetInt32();
                    }
                    else
                    {
                        s.ClientsMoonlight = s.ClientsWebRtc = null;
                    }
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Sunshine arrancando o reiniciándose: sin dato hasta la siguiente pasada.
                    s.ClientsMoonlight = s.ClientsWebRtc = null;
                }
            }
        }
    }
}
