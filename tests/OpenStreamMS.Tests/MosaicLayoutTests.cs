using OpenStreamMS.Services.Mosaic;
using OpenStreamMS.Services.Seats;

namespace OpenStreamMS.Tests;

public class MosaicLayoutTests
{
    [Fact]
    public void Areas_AdaptToTheNumberOfSessions()
    {
        Assert.Equal([SeatScreenArea.Full], MosaicLayout.Areas(1));
        Assert.Equal([SeatScreenArea.Left, SeatScreenArea.Right], MosaicLayout.Areas(2));
        Assert.Equal([SeatScreenArea.TopLeft, SeatScreenArea.TopRight, SeatScreenArea.BottomLeft], MosaicLayout.Areas(3));
        Assert.Equal([SeatScreenArea.TopLeft, SeatScreenArea.TopRight, SeatScreenArea.BottomLeft, SeatScreenArea.BottomRight],
                     MosaicLayout.Areas(4));
    }

    [Theory]
    [InlineData(1, 0b1111)]
    [InlineData(2, 0b1111)]
    [InlineData(3, 0b0111)]
    [InlineData(4, 0b1111)]
    public void Areas_NeverOverlap(int count, int expectedCells)
    {
        int used = 0;
        foreach (var a in MosaicLayout.Areas(count))
        {
            int cells = SeatAreas.Cells(a);
            Assert.Equal(0, used & cells);
            used |= cells;
        }
        Assert.Equal(expectedCells, used);
    }

    [Theory]
    [InlineData(SeatScreenArea.Full, 1920, 1080)]
    [InlineData(SeatScreenArea.Left, 960, 1080)]
    [InlineData(SeatScreenArea.Right, 960, 1080)]
    [InlineData(SeatScreenArea.TopLeft, 960, 540)]
    [InlineData(SeatScreenArea.BottomRight, 960, 540)]
    public void PartSize_SplitsTheCanvas(SeatScreenArea area, int w, int h)
    {
        Assert.Equal((w, h), MosaicLayout.PartSize(area, 1920, 1080));
    }

    [Fact]
    public void PartSize_GivesTheOddPixelToTheRightAndBottom()
    {
        // Mismo reparto que Sunshine (mosaic_rect) y que la pantalla dividida de los puestos.
        Assert.Equal((960, 540), MosaicLayout.PartSize(SeatScreenArea.TopLeft, 1921, 1081));
        Assert.Equal((961, 541), MosaicLayout.PartSize(SeatScreenArea.BottomRight, 1921, 1081));
    }

    [Fact]
    public void Render_ListsCanvasAndSessionsInArrivalOrder()
    {
        var text = MosaicLayout.Render(2560, 1440, [7u, 9u, 12u]);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("#", lines[0]);
        Assert.Equal(["canvas 2560 1440", "7 TopLeft", "9 TopRight", "12 BottomLeft"], lines[1..]);
    }

    [Fact]
    public void Render_UsesLfOnly()
    {
        Assert.DoesNotContain('\r', MosaicLayout.Render(1920, 1080, [1u, 2u]));
    }
}
