using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Services.Session;

namespace OpenStreamMS.Core.Api;

public record TermWrapLogResponse(string[] Lines, bool Busy, string? LastAction);

/// <summary>
/// API REST para gestionar TermWrap (<see href="https://github.com/llccd/TermWrap"/>).
/// Reemplazo recomendado de RDPWrap; <see cref="RdpWrapperEndpoints"/> sigue expuesto
/// como fallback por si una build de Windows rompe TermWrap.
/// </summary>
public static class TermWrapEndpoints
{
    public static IEndpointRouteBuilder MapTermWrapApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/termwrap").WithTags("TermWrap");

        group.MapGet("/status", (TermWrapManager mgr) =>
                TypedResults.Ok(mgr.GetStatus()))
             .WithName("GetTermWrapStatus")
             .WithSummary("Estado de TermWrap")
             .WithDescription("Devuelve si TermWrap está instalado y activo, versión del bundle, versión de termsrv.dll y destino actual de ServiceDll.");

        group.MapGet("/log", (TermWrapManager mgr) =>
                TypedResults.Ok(new TermWrapLogResponse(mgr.GetLog(), mgr.IsBusy, mgr.GetStatus().LastAction)))
             .WithName("GetTermWrapLog")
             .WithSummary("Log de la última operación");

        group.MapPost("/install", async (TermWrapManager mgr) =>
                await RunAsync(mgr, mgr.InstallAsync))
             .WithName("InstallTermWrap")
             .WithSummary("Instalar TermWrap")
             .WithDescription("""
                Descarga la última release de llccd/TermWrap, copia las DLLs (arquitectura del SO)
                a %ProgramFiles%\\RDP Wrapper, registra TermWrap.dll como ServiceDll de TermService
                y reinicia el servicio. IMPORTANTE: las sesiones RDP activas se cerrarán.
                """);

        group.MapPost("/uninstall", async (TermWrapManager mgr) =>
                await RunAsync(mgr, mgr.UninstallAsync))
             .WithName("UninstallTermWrap")
             .WithSummary("Desinstalar TermWrap")
             .WithDescription("Restaura ServiceDll a %SystemRoot%\\System32\\termsrv.dll, reinicia TermService y borra TermWrap.dll/UmWrap.dll/EndpWrap.dll/Zydis.dll.");

        return app;
    }

    private static Task<Results<Accepted, BadRequest<string>>> RunAsync(
        TermWrapManager mgr, Func<CancellationToken, Task> op)
    {
        if (mgr.IsBusy)
            return Task.FromResult<Results<Accepted, BadRequest<string>>>(
                TypedResults.BadRequest("Ya hay una operación de TermWrap en curso."));

        _ = Task.Run(() => op(CancellationToken.None));
        return Task.FromResult<Results<Accepted, BadRequest<string>>>(
            TypedResults.Accepted("/api/termwrap/status"));
    }
}
