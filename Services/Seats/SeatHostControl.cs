using OpenStreamMS.Core.Helpers;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenStreamMS.Services.Seats;

/// <summary>
/// Canal de control entre el servicio (sesión 0) y el proceso anfitrión en la consola (TrayApp).
/// Permite al gestor de puestos solicitar la apertura o cierre de un puesto en la consola física.
/// </summary>
public static class SeatHostControl
{
    private const string PipeName = "OpenStreamMS.SeatHostControl";

    // ── Lado del Servicio (sesión 0) ──

    private static NamedPipeServerStream? _serverPipe;
    private static SeatChannel? _serverChannel;
    private static readonly object _serverLock = new();
    private static volatile bool _serverRunning;

    public static void StartServer(CancellationToken stoppingToken)
    {
        _serverRunning = true;
        _ = Task.Run(() => ServerLoop(stoppingToken), stoppingToken);
    }

    private static async Task ServerLoop(CancellationToken ct)
    {
        while (_serverRunning && !ct.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));

                var server = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 65536, 65536, security);

                await server.WaitForConnectionAsync(ct);

                // El icono de bandeja arranca en todas las sesiones (también en las de los
                // puestos y las de streaming): solo el de la consola puede ser el presentador.
                int clientPid = LocalCaller.PipeClientPid(server);
                if (!LocalCaller.IsInConsoleSession(clientPid))
                {
                    server.Dispose();
                    Logger.Warning($"[Seats] Canal de control de anfitrión: rechazado el proceso {clientPid}, no está en la consola.");
                    await Task.Delay(500, ct);
                    continue;
                }

                lock (_serverLock)
                {
                    _serverPipe?.Dispose();
                    _serverPipe = server;
                    _serverChannel = new SeatChannel(server);
                }
                Logger.Log("[Seats] TrayApp conectado al canal de control de anfitrión.");

                // Leer mensajes hasta que se desconecte
                while (_serverRunning && !ct.IsCancellationRequested)
                {
                    var msg = _serverChannel.Receive();
                    if (msg is null) break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (_serverRunning)
                {
                    Logger.Warning($"[Seats] Canal de control de anfitrión: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
            finally
            {
                lock (_serverLock)
                {
                    _serverChannel?.Dispose();
                    _serverChannel = null;
                    _serverPipe?.Dispose();
                    _serverPipe = null;
                }
            }
        }
    }

    public static bool IsHostConnected
    {
        get
        {
            lock (_serverLock)
            {
                return _serverChannel is not null && _serverPipe is { IsConnected: true };
            }
        }
    }

    /// <summary>
    /// Pide al proceso anfitrión que abra el puesto. Si TrayApp no está conectado,
    /// lanza <c>OpenStreamMS.exe --seat-host &lt;pipeName&gt; &lt;seatId&gt;</c> en la sesión de consola.
    /// </summary>
    public static void RequestOpenSeat(
        Guid seatId,
        string hostPipeName,
        SeatMonitor? monitor,
        string? audioOutId,
        IReadOnlyList<SeatDevice> devices)
    {
        var msg = new JsonObject
        {
            ["op"] = "open_seat",
            ["seatId"] = seatId.ToString(),
            ["pipeName"] = hostPipeName,
            ["audioOutId"] = audioOutId ?? "",
            ["monitor"] = monitor is not null ? JsonSerializer.SerializeToNode(monitor) : null,
            ["devices"] = JsonSerializer.SerializeToNode(devices)
        };

        lock (_serverLock)
        {
            if (_serverChannel is not null && _serverPipe is { IsConnected: true })
            {
                try
                {
                    _serverChannel.SendJson(msg);
                    Logger.Log($"[Seats] Orden 'open_seat' enviada a TrayApp para puesto {seatId}.");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Seats] No se pudo enviar 'open_seat' a TrayApp: {ex.Message}");
                }
            }
        }

        // Si TrayApp no está conectado, lanzar proceso independiente en la consola
        if (ConsoleSession.TryGetUser(out uint consoleId, out _, out _))
        {
            Logger.Log($"[Seats] TrayApp no conectado; lanzando presentador independiente en sesión de consola {consoleId}...");
            new ProcessInSession().LaunchInSession(
                consoleId,
                Environment.ProcessPath!,
                $"--seat-host {hostPipeName} {seatId}");
        }
        else
        {
            Logger.Warning("[Seats] No hay ningún usuario con sesión iniciada en la consola física.");
        }
    }

    public static void RequestCloseSeat(Guid seatId)
    {
        lock (_serverLock)
        {
            if (_serverChannel is not null && _serverPipe is { IsConnected: true })
            {
                try
                {
                    _serverChannel.SendJson(new JsonObject
                    {
                        ["op"] = "close_seat",
                        ["seatId"] = seatId.ToString()
                    });
                }
                catch { }
            }
        }
        SeatHost.CloseSeat(seatId);
    }

    // ── Lado del Cliente (TrayApp en consola) ──

    public static IDisposable StartClient()
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(() => ClientLoop(cts.Token), cts.Token);
        return new ActionDisposable(() => cts.Cancel());
    }

    private static async Task ClientLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Fuera de la consola el servicio rechazaría la conexión: ni se intenta, para no
                // quitarle el turno (la tubería admite un solo cliente) al icono de la consola.
                if (!LocalCaller.IsInConsoleSession(Environment.ProcessId))
                {
                    await Task.Delay(5000, ct);
                    continue;
                }

                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(5000, ct);
                SeatPipes.RequireServiceServer(client);
                using var channel = new SeatChannel(client);
                Logger.Log("[TrayApp] Conectado al canal de control de anfitrión de puestos.");

                while (!ct.IsCancellationRequested)
                {
                    var msg = channel.Receive();
                    if (msg is null) break;

                    if (msg.Value.Type == SeatMsg.Json)
                    {
                        try
                        {
                            var json = SeatChannel.ParseJson(msg.Value.Data);
                            string? op = json["op"]?.GetValue<string>();
                            if (op == "open_seat")
                            {
                                Guid sid = Guid.Parse(json["seatId"]!.GetValue<string>());
                                string pName = json["pipeName"]!.GetValue<string>();
                                string? aId = json["audioOutId"]?.GetValue<string>();
                                SeatMonitor? mon = json["monitor"] is JsonObject mo
                                    ? JsonSerializer.Deserialize<SeatMonitor>(mo)
                                    : null;
                                var devs = json["devices"] is JsonArray da
                                    ? JsonSerializer.Deserialize<List<SeatDevice>>(da) ?? []
                                    : [];

                                SeatHost.OpenSeat(sid, pName, mon, aId, devs);
                            }
                            else if (op == "close_seat")
                            {
                                Guid sid = Guid.Parse(json["seatId"]!.GetValue<string>());
                                SeatHost.CloseSeat(sid);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"[TrayApp] Error procesando orden de puesto: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Servicio no iniciado o reconectando
                await Task.Delay(3000, ct);
            }
        }
    }

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
