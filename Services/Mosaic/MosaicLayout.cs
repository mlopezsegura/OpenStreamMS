using System.Text;
using OpenStreamMS.Services.Seats;

namespace OpenStreamMS.Services.Mosaic;

/// <summary>
/// Reparto del mosaico: qué parte de la cuadrícula de 2×2 ocupa cada sesión según cuántas
/// haya, qué tamaño tiene esa parte y el fichero <c>osms-mosaic.txt</c> que lee Sunshine.
/// Las partes son las mismas que las de la pantalla dividida de los puestos
/// (<see cref="SeatScreenArea"/>) y se calculan igual (<see cref="SeatMonitor.Region"/>).
/// </summary>
public static class MosaicLayout
{
    /// <summary>Sesiones como máximo en un mosaico (la anfitriona incluida).</summary>
    public const int MaxMembers = 4;

    /// <summary>Nombre del fichero de control, junto a <c>sunshine.conf</c> de la anfitriona.</summary>
    public const string FileName = "osms-mosaic.txt";

    /// <summary>
    /// Partes por orden de llegada: 1 sesión, pantalla entera; 2, mitades izquierda/derecha
    /// (o arriba/abajo con <paramref name="stacked"/>); 3 o 4, cuartos (con 3, el de abajo a la
    /// derecha queda en negro).
    /// </summary>
    public static SeatScreenArea[] Areas(int count, bool stacked = false) => count switch
    {
        <= 1 => [SeatScreenArea.Full],
        2    => stacked ? [SeatScreenArea.Top, SeatScreenArea.Bottom] : [SeatScreenArea.Left, SeatScreenArea.Right],
        3    => [SeatScreenArea.TopLeft, SeatScreenArea.TopRight, SeatScreenArea.BottomLeft],
        _    => [SeatScreenArea.TopLeft, SeatScreenArea.TopRight, SeatScreenArea.BottomLeft, SeatScreenArea.BottomRight],
    };

    /// <summary>Tamaño de una parte en un lienzo de <paramref name="width"/>×<paramref name="height"/>.</summary>
    public static (int Width, int Height) PartSize(SeatScreenArea area, int width, int height)
    {
        var (_, _, w, h) = new SeatMonitor { Area = area }.Region(0, 0, width, height);
        return (w, h);
    }

    /// <summary>
    /// Contenido de <c>osms-mosaic.txt</c>: el lienzo y una línea por sesión
    /// ("&lt;id de sesión de Windows&gt; &lt;parte&gt;"), en orden de llegada.
    /// </summary>
    public static string Render(int canvasWidth, int canvasHeight, IReadOnlyList<uint> windowsSessions, bool stacked = false)
    {
        var areas = Areas(windowsSessions.Count, stacked);
        var sb = new StringBuilder();
        sb.Append("# OpenStreamMS mosaic: <Windows session id> <part>\n");
        sb.Append($"canvas {canvasWidth} {canvasHeight}\n");
        for (int i = 0; i < windowsSessions.Count && i < areas.Length; i++)
            sb.Append($"{windowsSessions[i]} {areas[i]}\n");
        return sb.ToString();
    }
}
