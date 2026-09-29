using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Services;

namespace OpenStreamMS.Core.Api;

public record PowerActionRequest(int? DelaySeconds = null, string? Message = null);

public record PowerResponse(string Action, int DelaySeconds, string? Message);

/// <summary>
/// Endpoints para apagar/reiniciar/cancelar el equipo desde el panel web.
/// Usa <c>shutdown.exe</c> de Windows; requiere SeShutdownPrivilege (el servicio
/// corre como SYSTEM, asi que la tiene).
/// </summary>
public static class PowerEndpoints
{
    private const int DefaultDelaySeconds = 10;
    private const int MaxDelaySeconds = 3600;

    public static IEndpointRouteBuilder MapPowerApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/power").WithTags("Power");

        group.MapPost("/shutdown", (PowerActionRequest? req) =>
                RunShutdown(isRestart: false, req))
             .WithName("PowerShutdown")
             .WithSummary("Apaga el equipo")
             .WithDescription("Ejecuta `shutdown.exe /s /t <delay> /f`. Cancelable con /api/power/cancel mientras el countdown sigue activo.");

        group.MapPost("/restart", (PowerActionRequest? req) =>
                RunShutdown(isRestart: true, req))
             .WithName("PowerRestart")
             .WithSummary("Reinicia el equipo")
             .WithDescription("Ejecuta `shutdown.exe /r /t <delay> /f`. Cancelable con /api/power/cancel mientras el countdown sigue activo.");

        group.MapPost("/cancel", () =>
        {
            var (ok, output) = RunShutdownExe("/a");
            if (ok)
            {
                Logger.Log("[Power] Apagado/reinicio cancelado.");
                return Results.Ok(new PowerResponse("cancel", 0, null));
            }
            // exit code 1116 = no hay shutdown pendiente. No es error real.
            Logger.Log($"[Power] No hay apagado pendiente que cancelar ({output.Trim()}).");
            return Results.Ok(new PowerResponse("cancel-noop", 0, null));
        })
             .WithName("PowerCancel")
             .WithSummary("Cancela un apagado/reinicio pendiente");

        return app;
    }

    private static IResult RunShutdown(bool isRestart, PowerActionRequest? req)
    {
        var delay = ClampDelay(req?.DelaySeconds ?? DefaultDelaySeconds);
        var message = string.IsNullOrWhiteSpace(req?.Message)
            ? (isRestart ? "OpenStreamMS: reinicio remoto" : "OpenStreamMS: apagado remoto")
            : req!.Message!.Trim();

        var args = new List<string>
        {
            isRestart ? "/r" : "/s",
            "/t", delay.ToString(),
            "/f",
            "/c", message,
        };

        var (ok, output) = RunShutdownExe(args.ToArray());
        if (!ok)
        {
            Logger.Warning($"[Power] shutdown.exe fallo: {output.Trim()}");
            return Results.Problem(
                title:  "No se pudo iniciar el apagado.",
                detail: output.Trim(),
                statusCode: 500);
        }

        Logger.Log($"[Power] {(isRestart ? "Reinicio" : "Apagado")} programado en {delay}s.");
        return Results.Ok(new PowerResponse(
            isRestart ? "restart" : "shutdown", delay, message));
    }

    private static (bool ok, string output) RunShutdownExe(params string[] args)
    {
        var psi = new ProcessStartInfo("shutdown.exe")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, "No se pudo lanzar shutdown.exe");

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "shutdown.exe no termino en 10 s");
            }

            var combined = string.Join(
                Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return (p.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static int ClampDelay(int seconds) =>
        seconds < 0 ? 0 : seconds > MaxDelaySeconds ? MaxDelaySeconds : seconds;
}
