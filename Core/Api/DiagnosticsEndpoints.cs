using OpenStreamMS.Core.Helpers;
using OpenStreamMS.Services;
using OpenStreamMS.Services.Drivers;
using OpenStreamMS.Services.Seats;
using OpenStreamMS.Services.Session;
using OpenStreamMS.Services.Localization;

namespace OpenStreamMS.Core.Api;

/// <summary>
/// Paquete de diagnóstico para depurar un equipo: un .zip con datos del equipo y de Windows,
/// estado y logs de OpenStreamMS y de cada Sunshine y errores del registro de eventos. Ver
/// <see cref="DiagnosticsBundle"/>.
/// </summary>
public static class DiagnosticsEndpoints
{
    private static int _busy;

    public static IEndpointRouteBuilder MapDiagnosticsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diagnostics/bundle", async (int? days, StreamSessionService sessions, SeatManager seats,
                                                     RdpWrapperManager rdp, ViGEmBusManager vigem, HidHideManager hidhide,
                                                     ServiceConfig cfg) =>
            {
                // Leer los eventos y comprimir lleva unos segundos: uno cada vez.
                if (Interlocked.Exchange(ref _busy, 1) == 1)
                    return Results.Conflict(RequestCulture.T("diagnostics.busy"));
                var path = Path.Combine(Path.GetTempPath(), $"osms-diag-{Guid.NewGuid():N}.zip");
                // DeleteOnClose: el fichero desaparece cuando ASP.NET termina de enviarlo y cierra el stream.
                var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
                try
                {
                    var d = Math.Clamp(days ?? 3, 1, 30);
                    var src = new DiagnosticsBundle.Sources(
                        Sessions:   () => sessions.GetAll().Select(SessionEndpoints.ToResponse).ToList(),
                        Seats:      () => seats.GetAll().ToList(),
                        RdpWrapper: rdp,
                        Drivers:    [vigem, hidhide],
                        LogLevel:   cfg.LogThreshold.ToString(),
                        ExtraUsers: [cfg.AdminUsername]);
                    await Task.Run(() => DiagnosticsBundle.Write(fs, src, d));
                    fs.Position = 0;
                    Logger.Log($"[Diag] Paquete de diagnóstico generado ({fs.Length / 1024} KB, eventos de {d} día(s)).");
                    return Results.File(fs, "application/zip", $"openstreamms-diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                }
                catch (Exception ex)
                {
                    fs.Dispose();
                    Logger.Error($"[Diag] No se pudo generar el paquete de diagnóstico: {ex.Message}");
                    return Results.Problem(ex.Message);
                }
                finally { Interlocked.Exchange(ref _busy, 0); }
            })
           .WithTags("Diagnostics")
           .WithName("DiagnosticsBundle")
           .WithSummary("Descarga un .zip de diagnóstico")
           .WithDescription("Datos del equipo (versión y compilación de Windows, GPU, drivers), estado de OpenStreamMS, " +
                            "logs de OpenStreamMS y de cada Sunshine (con la ejecución anterior) y errores del registro de " +
                            "eventos de los últimos `days` días (1–30, por defecto 3). Nombre del equipo, cuentas, correos y " +
                            "credenciales de Sunshine van sustituidos.");
        return app;
    }

    /// <summary>
    /// Línea de comandos (<c>--diagnostics</c>): para cuando el panel no abre. Sin el servicio no
    /// hay sesiones ni puestos en memoria; el resto sale igual.
    /// </summary>
    internal static int RunCli(string[] args)
    {
        int daysIdx = Array.IndexOf(args, "--days");
        int days = daysIdx > 0 && daysIdx + 1 < args.Length && int.TryParse(args[daysIdx + 1], out var n) ? Math.Clamp(n, 1, 30) : 3;
        var output = args.Skip(1).FirstOrDefault(a => a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                     $"openstreamms-diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            Console.WriteLine(BackendText.Get("diagnostics.generating", days));
            string? admin = null;
            try { admin = ServiceConfig.Load().AdminUsername; } catch { }
            using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write))
                DiagnosticsBundle.Write(fs, new DiagnosticsBundle.Sources(
                    RdpWrapper: new RdpWrapperManager(),
                    Drivers:    [new ViGEmBusManager(), new HidHideManager()],
                    ExtraUsers: admin is null ? null : [admin]), days);
            Console.WriteLine(BackendText.Get("diagnostics.done", output));
            Console.WriteLine(BackendText.Get("diagnostics.review"));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(BackendText.Get("diagnostics.error", ex.Message));
            return 1;
        }
    }
}