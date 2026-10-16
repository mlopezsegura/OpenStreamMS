using Microsoft.AspNetCore.Http.HttpResults;
using OpenStreamMS.Services.Seats;

namespace OpenStreamMS.Core.Api;

// ── DTOs ──
public record SeatMonitorDto(string DevicePath, string FriendlyName, int Width, int Height, int RefreshHz);
public record SeatDeviceDto(SeatDeviceKind Kind, string UsbPort, string Label);

public record SeatDto(
    Guid Id, string Name, SeatMonitorDto? Monitor, string? AudioOutId, string? AudioOutName,
    List<SeatDeviceDto> Devices, Guid? SessionId, bool AutoStart, bool ExclusiveSession,
    string State, string? StatusDetail, string? ErrorMessage);

public record SeatUpsertRequest(
    string Name, SeatMonitorDto? Monitor, string? AudioOutId, string? AudioOutName,
    List<SeatDeviceDto>? Devices, Guid? SessionId, bool AutoStart = true, bool ExclusiveSession = true);

public record MonitorDto(string DevicePath, string FriendlyName, bool Active, int Width, int Height);
public record AudioOutputDto(string Id, string Name);
public record InputDeviceDto(string Kind, string UsbPort, string Name);
public record InventoryResponse(List<MonitorDto> Monitors, List<AudioOutputDto> AudioOutputs, List<InputDeviceDto> Devices);

/// <summary>Endpoints REST de los puestos locales. Mismo estilo que <see cref="SessionEndpoints"/>.</summary>
public static class SeatEndpoints
{
    public static IEndpointRouteBuilder MapSeatApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/seats").WithTags("Seats");

        group.MapGet("/", (SeatManager mgr) => TypedResults.Ok(mgr.GetAll().Select(ToDto)))
             .WithSummary("Lista los puestos locales.");

        group.MapGet("/inventory", (SeatManager mgr) => TypedResults.Ok(new InventoryResponse(
                mgr.AvailableMonitors().Select(m => new MonitorDto(m.DevicePath, m.FriendlyName, m.Active, m.Width, m.Height)).ToList(),
                mgr.AvailableAudioOutputs().Select(a => new AudioOutputDto(a.Id, a.Name)).ToList(),
                mgr.AvailableInputDevices().Select(d => new InputDeviceDto(d.Kind.ToString(), d.UsbPort, d.Name)).ToList())))
             .WithSummary("Monitores, salidas de audio y dispositivos USB libres para asignar a un puesto.");

        group.MapPost("/", (SeatUpsertRequest req, SeatManager mgr) =>
             {
                 try { return Results.Ok(ToDto(mgr.Create(FromRequest(new Seat(), req)))); }
                 catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
             })
             .WithSummary("Crea un puesto.");

        group.MapPut("/{id:guid}", (Guid id, SeatUpsertRequest req, SeatManager mgr) =>
             {
                 try { return Results.Ok(ToDto(mgr.Update(id, FromRequest(new Seat(), req)))); }
                 catch (KeyNotFoundException) { return Results.NotFound(); }
                 catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
                 catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
             })
             .WithSummary("Edita un puesto (debe estar detenido).");

        group.MapDelete("/{id:guid}", (Guid id, SeatManager mgr) =>
             {
                 try { mgr.Delete(id); return Results.NoContent(); }
                 catch (KeyNotFoundException) { return Results.NotFound(); }
             })
             .WithSummary("Borra un puesto.");

        group.MapPost("/{id:guid}/start", (Guid id, SeatManager mgr) => Action(() => mgr.Start(id)))
             .WithSummary("Enciende el puesto: ajusta la sesión y activa su monitor.");

        group.MapPost("/{id:guid}/stop", (Guid id, SeatManager mgr) => Action(() => mgr.Stop(id)))
             .WithSummary("Apaga el puesto y saca su monitor del escritorio.");

        return app;
    }

    private static IResult Action(Action op)
    {
        try { op(); return Results.Accepted(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
    }

    private static Seat FromRequest(Seat seat, SeatUpsertRequest req)
    {
        seat.Name = req.Name;
        seat.Monitor = req.Monitor is { } m ? new SeatMonitor
        {
            DevicePath = m.DevicePath, FriendlyName = m.FriendlyName,
            Width = m.Width, Height = m.Height, RefreshHz = m.RefreshHz,
        } : null;
        seat.AudioOutId = req.AudioOutId;
        seat.AudioOutName = req.AudioOutName;
        seat.Devices = req.Devices?.Select(d => new SeatDevice { Kind = d.Kind, UsbPort = d.UsbPort, Label = d.Label }).ToList() ?? [];
        seat.SessionId = req.SessionId;
        seat.AutoStart = req.AutoStart;
        seat.ExclusiveSession = req.ExclusiveSession;
        return seat;
    }

    private static SeatDto ToDto(Seat s) => new(
        s.Id, s.Name,
        s.Monitor is { } m ? new SeatMonitorDto(m.DevicePath, m.FriendlyName, m.Width, m.Height, m.RefreshHz) : null,
        s.AudioOutId, s.AudioOutName,
        s.Devices.Select(d => new SeatDeviceDto(d.Kind, d.UsbPort, d.Label)).ToList(),
        s.SessionId, s.AutoStart, s.ExclusiveSession,
        s.State.ToString(), s.StatusDetail, s.ErrorMessage);
}
