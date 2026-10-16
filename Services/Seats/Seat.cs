using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenStreamMS.Services.Seats;

public enum SeatState { Stopped, Starting, Running, Error }

public enum SeatDeviceKind { Mouse, Keyboard, Gamepad }

/// <summary>Monitor físico del puesto. Se identifica por la ruta de dispositivo del monitor (estable entre reinicios).</summary>
public class SeatMonitor
{
    public string DevicePath   { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    /// <summary>0 = el modo que elija Windows para ese monitor.</summary>
    public int    Width        { get; set; }
    public int    Height       { get; set; }
    public int    RefreshHz    { get; set; }
}

/// <summary>Dispositivo de entrada asignado por puerto USB: si se cambia el aparato de ese puerto, sigue en el puesto.</summary>
public class SeatDevice
{
    public SeatDeviceKind Kind    { get; set; }
    public string         UsbPort { get; set; } = "";
    public string         Label   { get; set; } = "";
}

/// <summary>
/// Puesto local: monitor + salida de audio + teclados/ratones/mandos físicos, con una sesión
/// de OpenStreamMS asignada que se muestra en el monitor y recibe la entrada de esos dispositivos.
/// </summary>
public class Seat
{
    public Guid              Id           { get; set; } = Guid.NewGuid();
    public string            Name         { get; set; } = "";
    public SeatMonitor?      Monitor      { get; set; }
    /// <summary>Id del endpoint MMDevice de salida, o null para no llevar audio al puesto.</summary>
    public string?           AudioOutId   { get; set; }
    public string?           AudioOutName { get; set; }
    public List<SeatDevice>  Devices      { get; set; } = [];
    public Guid?             SessionId    { get; set; }
    public bool              AutoStart    { get; set; }
    /// <summary>Mientras el puesto está activo, la sesión no se sirve por Moonlight/WebRTC (Sunshine en pausa).</summary>
    public bool              ExclusiveSession { get; set; } = true;
    public DateTime          CreatedAt    { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public SeatState State        { get; set; } = SeatState.Stopped;
    [JsonIgnore] public string?   ErrorMessage { get; set; }
    [JsonIgnore] public string?   StatusDetail { get; set; }
}

/// <summary>Persistencia de los puestos en <c>seats.json</c>, junto a <c>sessions.json</c>.</summary>
internal static class SeatStore
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "seats.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    public static List<Seat> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<Seat>>(File.ReadAllText(FilePath), Options) ?? [];
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seats] No se pudo leer seats.json: {ex.Message}");
            return [];
        }
    }

    public static void Save(IEnumerable<Seat> seats)
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(seats.ToList(), Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Seats] No se pudo guardar seats.json: {ex.Message}");
        }
    }
}
