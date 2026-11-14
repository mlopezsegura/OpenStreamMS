using OpenStreamMS.Services.Mosaic;

namespace OpenStreamMS.Core.Api;

/// <param name="Id">Sesión de OpenStreamMS.</param>
/// <param name="Name">Nombre de la sesión.</param>
/// <param name="Area">Parte de la cuadrícula que ocupa (Full, Left, Right, TopLeft…).</param>
public record MosaicMemberDto(Guid Id, string Name, string Area);

/// <param name="Host">Sesión anfitriona (la que se retransmite).</param>
/// <param name="Members">Por orden de llegada; la primera es la anfitriona. El mando N va a la sesión N.</param>
/// <param name="Stacked">Con dos sesiones, una encima de otra en vez de una al lado de otra.</param>
public record MosaicDto(Guid Host, List<MosaicMemberDto> Members, bool Stacked);

/// <param name="Stacked">true: una encima de otra (arriba/abajo); false: una al lado de otra (izquierda/derecha).</param>
public record MosaicLayoutRequest(bool Stacked);

/// <summary>
/// Endpoints REST del mosaico (ver <see cref="MosaicService"/>). Mismo estilo que <see cref="SeatEndpoints"/>.
/// </summary>
public static class MosaicEndpoints
{
    public static IEndpointRouteBuilder MapMosaicApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions/{id:guid}/mosaic").WithTags("Mosaic");

        group.MapGet("/", (Guid id, MosaicService mosaic, StreamSessionService sessions) =>
                 sessions.Get(id) is null ? Results.NotFound() : Results.Ok(ToDto(id, mosaic, sessions)))
             .WithName("GetMosaic")
             .WithSummary("Mosaico de una sesión anfitriona")
             .WithDescription("Sesiones que comparten el stream de la anfitriona, por orden de llegada y con su parte de la pantalla. " +
                              "Sin mosaico, la lista solo tiene la anfitriona a pantalla entera.");

        group.MapPost("/{guestId:guid}", async (Guid id, Guid guestId, MosaicService mosaic, StreamSessionService sessions, CancellationToken ct) =>
                 await Action(() => mosaic.JoinAsync(id, guestId, ct), id, mosaic, sessions))
             .WithName("JoinMosaic")
             .WithSummary("Unir una sesión al mosaico")
             .WithDescription("""
                Une la sesión guestId al stream de la sesión anfitriona {id} (hasta cuatro en total), en la
                siguiente parte libre: 2 sesiones en mitades, 3 o 4 en cuartos. La invitada se arranca si
                hace falta y su Sunshine queda en pausa. Las sesiones se reconectan al tamaño de su parte
                (~1-2 s cada una). Requiere captura RemoteIdd y un cliente conectado a la anfitriona.
                """);

        group.MapDelete("/{memberId:guid}", async (Guid id, Guid memberId, MosaicService mosaic, StreamSessionService sessions, CancellationToken ct) =>
                 await Action(() => mosaic.LeaveAsync(id, memberId, ct), id, mosaic, sessions))
             .WithName("LeaveMosaic")
             .WithSummary("Sacar una sesión del mosaico")
             .WithDescription("Saca la sesión del mosaico: vuelve a su resolución y a servirse por su Sunshine. " +
                              "Sacar a la anfitriona deshace el mosaico.");

        group.MapPut("/layout", async (Guid id, MosaicLayoutRequest req, MosaicService mosaic, StreamSessionService sessions, CancellationToken ct) =>
                 await Action(() => mosaic.SetStackedAsync(id, req.Stacked, ct), id, mosaic, sessions))
             .WithName("SetMosaicLayout")
             .WithSummary("Disposición con dos sesiones")
             .WithDescription("Con dos sesiones, una al lado de otra (stacked: false) o una encima de otra (stacked: true). " +
                              "Las dos se reconectan al tamaño de su nueva mitad. Con tres o cuatro se recuerda para cuando vuelvan a ser dos.");

        return app;
    }

    private static async Task<IResult> Action(Func<Task> op, Guid host, MosaicService mosaic, StreamSessionService sessions)
    {
        try
        {
            await op();
            return Results.Ok(ToDto(host, mosaic, sessions));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
    }

    private static MosaicDto ToDto(Guid host, MosaicService mosaic, StreamSessionService sessions)
    {
        var ids = mosaic.Members(host);
        if (ids.Count == 0) ids = [host];
        var areas = MosaicLayout.Areas(ids.Count, mosaic.IsStacked(host));
        return new MosaicDto(host, ids.Select((id, i) =>
            new MosaicMemberDto(id, sessions.Get(id)?.Name ?? "", areas[i].ToString())).ToList(), mosaic.IsStacked(host));
    }
}
