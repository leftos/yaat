using Avalonia;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>
/// CRC's horizontal DCB flow (DisplayElementDcb.cs <c>BuildMenu</c>): the tiles are walked in order, a full or weather
/// tile fills a column of its own, and a half tile stacks under the previous half while that column holds exactly one, else
/// starts a new column. Columns sit left to right with no gap; a column's width is its widest tile.
/// </summary>
public static class DcbLayout
{
    /// <summary>The rectangle of each of <paramref name="shapes"/> in a bar at YAAT size, in the order given.</summary>
    public static IReadOnlyList<Rect> Flow(IReadOnlyList<DcbTileShape> shapes)
    {
        var rects = new List<Rect>(shapes.Count);
        double columnLeft = 0;
        double columnWidth = 0;
        double top = 0;
        bool started = false;
        foreach (DcbTileShape shape in shapes)
        {
            double width = shape.Width();
            double height = shape.Height();
            if (!started || ((top + height) > DcbTileSizes.BarHeight))
            {
                columnLeft = started ? (columnLeft + columnWidth) : 0;
                columnWidth = width;
                top = 0;
                started = true;
            }
            else
            {
                columnWidth = Math.Max(columnWidth, width);
            }

            rects.Add(new Rect(columnLeft, top, width, height));
            top += height + ((height < DcbTileSizes.BarHeight) ? DcbTileSizes.HalfGap : 0);
        }

        return rects;
    }
}
