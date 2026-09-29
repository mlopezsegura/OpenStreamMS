using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services.Sandboxie;

namespace OpenStreamMS.Core.Api;

public record SandboxieLogResponse(string[] Lines, bool Busy, string? LastAction);

public record SandboxieConfigResponse(
    string  BoxName,
    bool    SandboxedSteamEnabled,
    string? StartExePath,
    bool    BinariesPresent);

public record SandboxieConfigRequest(
    bool?   SandboxedSteamEnabled,
    string? BoxName);

public static class SandboxieEndpoints
{
    public static IEndpointRouteBuilder MapSandboxieApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sandboxie").WithTags("Sandboxie");

        group.MapGet("/status", (SandboxieManager mgr) =>
                TypedResults.Ok(mgr.GetStatus()))
             .WithName("GetSandboxieStatus")
             .WithSummary("Estado de Sandboxie")
             .WithDescription("Detecta si el servicio SbieSvc está registrado y si los binarios portables embebidos están presentes.");

        group.MapGet("/log", (SandboxieManager mgr) =>
                TypedResults.Ok(new SandboxieLogResponse(mgr.GetLog(), mgr.IsBusy, mgr.GetStatus().LastAction)))
             .WithName("GetSandboxieLog")
             .WithSummary("Log de la última operación")
             .WithDescription("Últimas líneas del log generado durante install/uninstall.");

        group.MapPost("/install", async (SandboxieManager mgr) =>
                await RunAsync(mgr, mgr.InstallAsync))
             .WithName("InstallSandboxie")
             .WithSummary("Instalar Sandboxie")
             .WithDescription("""
                Registra el servicio SbieSvc usando el Start.exe embebido y mergea la sección
                del box configurado en %WINDIR%\Sandboxie.ini. Requiere que los binarios
                portables estén copiados en la carpeta Sandboxie\ del servicio.
                """);

        group.MapPost("/uninstall", async (SandboxieManager mgr) =>
                await RunAsync(mgr, mgr.UninstallAsync))
             .WithName("UninstallSandboxie")
             .WithSummary("Desinstalar Sandboxie")
             .WithDescription("Ejecuta Start.exe /uninstall_service para quitar SbieSvc + driver SbieDrv.");

        group.MapPost("/sync-box", async (SandboxieManager mgr) =>
                await RunAsync(mgr, mgr.SyncBoxAsync))
             .WithName("SyncSandboxieBox")
             .WithSummary("Re-mergear box OpenStream en Sandboxie.ini")
             .WithDescription("""
                Re-escribe la sección del box en el Sandboxie.ini activo (UTF-16 LE)
                y llama Start.exe /reload_conf. Útil tras editar el .ini a mano o si
                un install anterior dejó el box mal codificado.
                """);

        group.MapGet("/config", (ServiceConfig cfg, SandboxieManager mgr) =>
        {
            var startExe = mgr.GetStartExePath();
            return TypedResults.Ok(new SandboxieConfigResponse(
                BoxName:               cfg.SandboxBoxName,
                SandboxedSteamEnabled: cfg.SandboxedSteamEnabled,
                StartExePath:          startExe,
                BinariesPresent:       startExe is not null && File.Exists(startExe)));
        })
             .WithName("GetSandboxieConfig")
             .WithSummary("Lee config de Sandboxie del servicio");

        group.MapPut("/config", (SandboxieConfigRequest req, ServiceConfig cfg, SandboxieManager mgr) =>
        {
            bool changed = false;

            if (req.SandboxedSteamEnabled is bool en && en != cfg.SandboxedSteamEnabled)
            {
                cfg.SandboxedSteamEnabled = en;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(req.BoxName) &&
                !string.Equals(req.BoxName, cfg.SandboxBoxName, StringComparison.Ordinal))
            {
                cfg.SandboxBoxName = req.BoxName.Trim();
                changed = true;
            }

            if (changed)
            {
                cfg.Save();
                OpenStreamMS.Services.Logger.Log($"[Sandboxie] Config actualizada: enabled={cfg.SandboxedSteamEnabled}, box={cfg.SandboxBoxName}");
            }

            var startExe = mgr.GetStartExePath();
            return TypedResults.Ok(new SandboxieConfigResponse(
                BoxName:               cfg.SandboxBoxName,
                SandboxedSteamEnabled: cfg.SandboxedSteamEnabled,
                StartExePath:          startExe,
                BinariesPresent:       startExe is not null && File.Exists(startExe)));
        })
             .WithName("SetSandboxieConfig")
             .WithSummary("Actualiza config de Sandboxie")
             .WithDescription("""
                Persiste el flag SandboxedSteamEnabled y el nombre del box. Para que los
                cambios afecten a Sunshine hay que detener y arrancar la sesión: el patch
                de apps.json se aplica en la primera configuración de cada SunshineManager.
                """);

        return app;
    }

    private static Task<Results<Accepted, BadRequest<string>>> RunAsync(
        SandboxieManager mgr, Func<CancellationToken, Task> op)
    {
        if (mgr.IsBusy)
            return Task.FromResult<Results<Accepted, BadRequest<string>>>(
                TypedResults.BadRequest("Ya hay una operación de Sandboxie en curso."));

        _ = Task.Run(() => op(CancellationToken.None));
        return Task.FromResult<Results<Accepted, BadRequest<string>>>(
            TypedResults.Accepted("/api/sandboxie/status"));
    }
}
