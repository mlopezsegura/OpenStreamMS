using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Services.VigEmBus;

namespace OpenStreamMS.Core.Api;

public record ViGEmBusLogResponse(string[] Lines, bool Busy, string? LastAction);

public static class ViGEmBusEndpoints
{
    public static IEndpointRouteBuilder MapViGEmBusApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vigembus").WithTags("ViGEmBus");

        group.MapGet("/status", (ViGEmBusManager mgr) =>
                TypedResults.Ok(mgr.GetStatus()))
             .WithName("GetViGEmBusStatus")
             .WithSummary("Estado de ViGEmBus")
             .WithDescription("Detecta si el driver ViGEmBus está instalado, su versión y estado del servicio.");

        group.MapGet("/log", (ViGEmBusManager mgr) =>
                TypedResults.Ok(new ViGEmBusLogResponse(mgr.GetLog(), mgr.IsBusy, mgr.GetStatus().LastAction)))
             .WithName("GetViGEmBusLog")
             .WithSummary("Log de la última operación")
             .WithDescription("Últimas líneas del log generado durante install/uninstall.");

        group.MapPost("/install", async (ViGEmBusManager mgr) =>
                await RunAsync(mgr, mgr.InstallAsync))
             .WithName("InstallViGEmBus")
             .WithSummary("Instalar ViGEmBus")
             .WithDescription("""
                Descarga la última release de nefarius/ViGEmBus y la ejecuta en modo silencioso
                (/quiet /install /norestart). El instalador puede devolver código 3010 indicando
                que hace falta reiniciar Windows para activar el driver por completo.
                """);

        group.MapPost("/uninstall", async (ViGEmBusManager mgr) =>
                await RunAsync(mgr, mgr.UninstallAsync))
             .WithName("UninstallViGEmBus")
             .WithSummary("Desinstalar ViGEmBus")
             .WithDescription("Lanza el comando de desinstalación registrado del bundle WiX en modo silencioso.");

        return app;
    }

    private static Task<Results<Accepted, BadRequest<string>>> RunAsync(
        ViGEmBusManager mgr, Func<CancellationToken, Task> op)
    {
        if (mgr.IsBusy)
            return Task.FromResult<Results<Accepted, BadRequest<string>>>(
                TypedResults.BadRequest("Ya hay una operación de ViGEmBus en curso."));

        _ = Task.Run(() => op(CancellationToken.None));
        return Task.FromResult<Results<Accepted, BadRequest<string>>>(
            TypedResults.Accepted("/api/vigembus/status"));
    }
}
