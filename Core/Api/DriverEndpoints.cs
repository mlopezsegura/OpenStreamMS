using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Services.Drivers;

namespace OpenStreamMS.Core.Api;

public record DriverLogResponse(string[] Lines, bool Busy, string? LastAction);

public static class DriverEndpoints
{
    public static IEndpointRouteBuilder MapDriverApis(this IEndpointRouteBuilder app)
    {
        app.MapDriverApi<ViGEmBusManager>("vigembus",
            "Detecta si el driver ViGEmBus (mando virtual) está instalado, su versión y estado del servicio.");
        app.MapDriverApi<HidHideManager>("hidhide",
            "Detecta si el driver HidHide (aislamiento de mandos por sesión) está instalado, su versión y estado del servicio.");
        return app;
    }

    private static void MapDriverApi<TManager>(this IEndpointRouteBuilder app, string route, string statusDescription)
        where TManager : NefariusDriverManager
    {
        string name = typeof(TManager).Name.Replace("Manager", "");
        var group = app.MapGroup($"/api/{route}").WithTags(name);

        group.MapGet("/status", (TManager mgr) =>
                TypedResults.Ok(mgr.GetStatus()))
             .WithName($"Get{name}Status")
             .WithSummary($"Estado de {name}")
             .WithDescription(statusDescription);

        group.MapGet("/log", (TManager mgr) =>
                TypedResults.Ok(new DriverLogResponse(mgr.GetLog(), mgr.IsBusy, mgr.GetStatus().LastAction)))
             .WithName($"Get{name}Log")
             .WithSummary("Log de la última operación")
             .WithDescription("Últimas líneas del log generado durante install/uninstall.");

        group.MapPost("/install", (TManager mgr) => Run(mgr, route, mgr.InstallAsync))
             .WithName($"Install{name}")
             .WithSummary($"Instalar {name}")
             .WithDescription("""
                Ejecuta en modo silencioso el instalador incluido con OpenStreamMS o, si no está,
                la última release de GitHub. El instalador puede devolver código 3010 indicando
                que hace falta reiniciar Windows para activar el driver por completo.
                """);

        group.MapPost("/uninstall", (TManager mgr) => Run(mgr, route, mgr.UninstallAsync))
             .WithName($"Uninstall{name}")
             .WithSummary($"Desinstalar {name}")
             .WithDescription("Lanza el comando de desinstalación registrado en modo silencioso.");
    }

    private static Results<Accepted, BadRequest<string>> Run(
        NefariusDriverManager mgr, string route, Func<CancellationToken, Task> op)
    {
        if (mgr.IsBusy)
            return TypedResults.BadRequest($"Ya hay una operación de {mgr.Name} en curso.");

        _ = Task.Run(() => op(CancellationToken.None));
        return TypedResults.Accepted($"/api/{route}/status");
    }
}
