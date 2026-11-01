using System.Net.NetworkInformation;
using System.Net.Sockets;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;

namespace OpenStreamMS.Core.Api;

/// <summary>Ajustes globales que se cambian desde el panel.</summary>
/// <param name="LogLevel">Nivel mínimo de log: Info, Warning, Error u Off.</param>
public record SettingsResponse(string LogLevel);

public record SetLogLevelRequest(string LogLevel);

/// <param name="HostName">Nombre del PC en la red.</param>
/// <param name="Addresses">IPv4 de la red local, primero las de adaptadores con puerta de enlace.</param>
public record NetworkInfoResponse(string HostName, List<string> Addresses);

/// <summary>Ajustes globales del servicio y datos del equipo para conectar clientes.</summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("/", (ServiceConfig cfg) => TypedResults.Ok(new SettingsResponse(cfg.LogThreshold.ToString())))
             .WithSummary("Ajustes globales.");

        group.MapPut("/logging", (SetLogLevelRequest req, ServiceConfig cfg) =>
             {
                 if (Logger.ParseThreshold(req.LogLevel) is not { } level)
                     return Results.BadRequest("Nivel de log no válido: usa Info, Warning, Error u Off.");

                 // Se avisa antes de cambiarlo: con "Off" o "Error" ya no quedaría constancia.
                 Logger.Warning($"[Settings] Nivel de log: {cfg.LogThreshold} → {level}.");
                 cfg.LogLevel = level.ToString();
                 cfg.Save();
                 Logger.SetThreshold(level);
                 return Results.Ok(new SettingsResponse(level.ToString()));
             })
             .WithSummary("Cambia el nivel de log global (servicio, bandeja, puestos y, al relanzarse, Sunshine).");

        app.MapGet("/api/system/network", () => TypedResults.Ok(GetNetworkInfo()))
           .WithTags("Settings")
           .WithSummary("Nombre e IPs de este PC en la red local, para conectar Moonlight o una TV.");

        return app;
    }

    private static NetworkInfoResponse GetNetworkInfo()
    {
        var found = new List<(string Ip, bool Gateway)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var props = nic.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;   // APIPA: sin DHCP, nadie llega por ahí
                    found.Add((ip, gateway));
                }
            }
        }
        catch { /* sin IPs: el panel solo enseña el nombre */ }

        var addresses = found.OrderByDescending(f => f.Gateway).Select(f => f.Ip).Distinct().ToList();
        return new NetworkInfoResponse(Environment.MachineName, addresses);
    }
}
