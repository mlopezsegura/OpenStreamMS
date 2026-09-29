using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Services.Session;

namespace OpenStreamMS.Core.Api;

public record RdpWrapperLogResponse(string[] Lines, bool Busy, string? LastAction);

public static class RdpWrapperEndpoints
{
    public static IEndpointRouteBuilder MapRdpWrapperApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rdpwrap").WithTags("RDP Wrapper");

        group.MapGet("/status", (RdpWrapperManager mgr) =>
                TypedResults.Ok(mgr.GetStatus()))
             .WithName("GetRdpWrapperStatus")
             .WithSummary("Estado del RDP Wrapper")
             .WithDescription("Devuelve si RDP Wrapper está instalado y activo, versión del bundle de asmtron, versión de termsrv.dll y destino actual de ServiceDll.");

        group.MapGet("/log", (RdpWrapperManager mgr) =>
                TypedResults.Ok(new RdpWrapperLogResponse(mgr.GetLog(), mgr.IsBusy, mgr.GetStatus().LastAction)))
             .WithName("GetRdpWrapperLog")
             .WithSummary("Log de la última operación")
             .WithDescription("Últimas líneas del log generado durante install/update/uninstall. Se refresca desde la UI cada pocos segundos.");

        group.MapPost("/install", async (RdpWrapperManager mgr) =>
                await RunAsync(mgr, mgr.InstallAsync))
             .WithName("InstallRdpWrapper")
             .WithSummary("Instalar RDP Wrapper")
             .WithDescription("""
                Descarga la última release portable de asmtron/rdpwrap, la extrae en
                %ProgramFiles%\\RDP Wrapper y ejecuta autoupdate.bat para aplicar el parche
                y reiniciar TermService. IMPORTANTE: reinicia TermService; las sesiones RDP
                activas se cerrarán.
                """);

        group.MapPost("/update", async (RdpWrapperManager mgr) =>
                await RunAsync(mgr, mgr.UpdateAsync))
             .WithName("UpdateRdpWrapper")
             .WithSummary("Actualizar rdpwrap.ini")
             .WithDescription("Re-ejecuta autoupdate.bat para refrescar rdpwrap.ini si el build de Windows cambió. Requiere tener el bundle ya instalado.");

        group.MapPost("/uninstall", async (RdpWrapperManager mgr) =>
                await RunAsync(mgr, mgr.UninstallAsync))
             .WithName("UninstallRdpWrapper")
             .WithSummary("Desinstalar RDP Wrapper")
             .WithDescription("Ejecuta RDPWInst.exe -u para restaurar la termsrv.dll original. Reinicia TermService; las sesiones RDP activas se cerrarán.");

        return app;
    }

    private static Task<Results<Accepted, BadRequest<string>>> RunAsync(
        RdpWrapperManager mgr, Func<CancellationToken, Task> op)
    {
        if (mgr.IsBusy)
            return Task.FromResult<Results<Accepted, BadRequest<string>>>(
                TypedResults.BadRequest("Ya hay una operación de RDP Wrapper en curso."));

        // Lanzado en background: la UI hace polling a /log y /status para el progreso.
        _ = Task.Run(() => op(CancellationToken.None));
        return Task.FromResult<Results<Accepted, BadRequest<string>>>(
            TypedResults.Accepted("/api/rdpwrap/status"));
    }
}
