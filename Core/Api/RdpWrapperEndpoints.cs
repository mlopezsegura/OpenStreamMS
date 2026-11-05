using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Services.Session;

namespace OpenStreamMS.Core.Api;

public record RdpWrapperLogResponse(string[] Lines, bool Busy, string? LastAction);

public static class RdpWrapperEndpoints
{
    public static IEndpointRouteBuilder MapRdpWrapperApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rdpwrap").WithTags("Multi-RDP (TermWrap)");

        group.MapGet("/status", (RdpWrapperManager mgr) =>
                TypedResults.Ok(mgr.GetStatus()))
             .WithName("GetRdpWrapperStatus")
             .WithSummary("Estado del multi-RDP")
             .WithDescription("Devuelve a qué apunta ServiceDll de TermService (TermWrap, o asmtron en una instalación antigua), si está cargado de verdad en el proceso, la versión de TermWrap y la de termsrv.dll.");

        group.MapGet("/log", (RdpWrapperManager mgr) =>
                TypedResults.Ok(new RdpWrapperLogResponse(mgr.GetLog(), mgr.IsBusy, mgr.GetStatus().LastAction)))
             .WithName("GetRdpWrapperLog")
             .WithSummary("Log de la última operación")
             .WithDescription("Últimas líneas del log de instalar/reparar. Se refresca desde la UI cada pocos segundos.");

        group.MapPost("/install", async (RdpWrapperManager mgr) =>
                await RunAsync(mgr, mgr.InstallAsync))
             .WithName("InstallRdpWrapper")
             .WithSummary("Instalar o reparar TermWrap")
             .WithDescription("""
                Descarga TermWrap 0.7 de llccd/TermWrap (hash fijo) si falta, lo copia en
                %ProgramFiles%\RDP Wrapper, pone ServiceDll de TermService en TermWrap.dll y
                reinicia TermService si no hay sesiones RDP abiertas; si las hay, el cambio se
                aplica al reiniciar el equipo.
                """);

        return app;
    }

    private static Task<Results<Accepted, BadRequest<string>>> RunAsync(
        RdpWrapperManager mgr, Func<CancellationToken, Task> op)
    {
        if (mgr.IsBusy)
            return Task.FromResult<Results<Accepted, BadRequest<string>>>(
                TypedResults.BadRequest("Ya hay una operación de multi-RDP en curso."));

        // Lanzado en background: la UI hace polling a /log y /status para el progreso.
        _ = Task.Run(() => op(CancellationToken.None));
        return Task.FromResult<Results<Accepted, BadRequest<string>>>(
            TypedResults.Accepted("/api/rdpwrap/status"));
    }
}
