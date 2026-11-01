using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenStreamMS.Services.Seats;

public enum SeatState { Stopped, Starting, Running, Error }

public enum SeatDeviceKind { Mouse, Keyboard, Gamepad }

/// <summary>
/// Parte del monitor que ocupa el puesto. El monitor es una cuadrícula de 2×2: entero, una mitad
/// (vertical: izquierda/derecha; horizontal: arriba/abajo) o un cuarto. Hasta cuatro puestos
/// comparten un monitor mientras sus partes no se pisen (ver <see cref="SeatAreas"/>).
/// </summary>
public enum SeatScreenArea { Full, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Las partes del monitor como celdas de la cuadrícula de 2×2.</summary>
public static class SeatAreas
{
    // Bits: 1 = arriba-izquierda, 2 = arriba-derecha, 4 = abajo-izquierda, 8 = abajo-derecha.
    public const int AllCells = 0b1111;

    public static int Cells(SeatScreenArea a) => a switch
    {
        SeatScreenArea.Left        => 0b0101,
        SeatScreenArea.Right       => 0b1010,
        SeatScreenArea.Top         => 0b0011,
        SeatScreenArea.Bottom      => 0b1100,
        SeatScreenArea.TopLeft     => 0b0001,
        SeatScreenArea.TopRight    => 0b0010,
        SeatScreenArea.BottomLeft  => 0b0100,
        SeatScreenArea.BottomRight => 0b1000,
        _                          => AllCells,
    };

    /// <summary>Por orden de preferencia: las más grandes primero.</summary>
    private static readonly SeatScreenArea[] BySize =
    [
        SeatScreenArea.Full, SeatScreenArea.Left, SeatScreenArea.Right, SeatScreenArea.Top, SeatScreenArea.Bottom,
        SeatScreenArea.TopLeft, SeatScreenArea.TopRight, SeatScreenArea.BottomLeft, SeatScreenArea.BottomRight,
    ];

    /// <summary>
    /// Cubre las celdas libres con el menor número de partes, de mayor a menor (la primera es la
    /// más grande). Lo usa la consola para quedarse lo libre del monitor principal; el panel
    /// (seats.js, coverFree) dibuja el mapa con el mismo criterio.
    /// </summary>
    public static List<SeatScreenArea> Cover(int freeCells)
    {
        var parts = new List<SeatScreenArea>();
        foreach (var a in BySize)
        {
            int c = Cells(a);
            if ((freeCells & c) == c) { parts.Add(a); freeCells &= ~c; }
        }
        return parts;
    }
}

/// <summary>Monitor físico del puesto. Se identifica por la ruta de dispositivo del monitor (estable entre reinicios).</summary>
public class SeatMonitor
{
    public string DevicePath   { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    /// <summary>
    /// Resolución de la sesión. 0 = la del monitor según lo elija Windows (o la de su parte,
    /// si el puesto usa pantalla dividida).
    /// </summary>
    public int    Width        { get; set; }
    public int    Height       { get; set; }
    public int    RefreshHz    { get; set; }
    public SeatScreenArea Area { get; set; } = SeatScreenArea.Full;

    /// <summary>Rectángulo del puesto dentro del monitor que está en (x, y) con tamaño w×h.</summary>
    public (int X, int Y, int W, int H) Region(int x, int y, int w, int h)
    {
        int cells = SeatAreas.Cells(Area);
        int w1 = w / 2, h1 = h / 2;
        bool left = (cells & 0b0101) != 0, right = (cells & 0b1010) != 0;
        bool top = (cells & 0b0011) != 0, bottom = (cells & 0b1100) != 0;
        int rx = left ? x : x + w1, rw = left && right ? w : left ? w1 : w - w1;
        int ry = top ? y : y + h1, rh = top && bottom ? h : top ? h1 : h - h1;
        return (rx, ry, rw, rh);
    }

    /// <summary>Dos puestos en el mismo monitor se pisan si comparten alguna celda de la cuadrícula.</summary>
    public bool Overlaps(SeatMonitor other) =>
        DevicePath == other.DevicePath && (SeatAreas.Cells(Area) & SeatAreas.Cells(other.Area)) != 0;
}

/// <summary>Dispositivo de entrada asignado por puerto USB: si se cambia el aparato de ese puerto, sigue en el puesto.</summary>
public class SeatDevice
{
    public SeatDeviceKind Kind    { get; set; }
    public string         UsbPort { get; set; } = "";
    public string         Label   { get; set; } = "";
}

/// <summary>
/// Puesto local: monitor + salida y entrada de audio + teclados/ratones/mandos físicos, con una sesión
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
    /// <summary>
    /// Id del endpoint MMDevice de captura (micrófono), o null si el puesto no lleva micrófono.
    /// Llega a la sesión por la redirección de audio de RDP: dentro aparece como "Remote Audio".
    /// </summary>
    public string?           AudioInId    { get; set; }
    public string?           AudioInName  { get; set; }
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
