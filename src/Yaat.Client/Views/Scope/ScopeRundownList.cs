using SkiaSharp;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// One line of the Radar view's RUNDOWN overlay: the title or one row, with its colour resolved for this frame's blink phase
/// and its text box in screen points.
/// </summary>
/// <param name="Row">The row the line shows; null for the title.</param>
/// <param name="Text">The text the line draws.</param>
/// <param name="Color">The colour the line draws in this frame.</param>
/// <param name="Rect">The line's text box: its left edge, its top a font size above its baseline, one line height tall.</param>
/// <param name="Baseline">The y of the line's text baseline.</param>
public sealed record ScopeRundownLine(RundownRow? Row, string Text, SKColor Color, SKRect Rect, float Baseline);

/// <summary>
/// The Radar view's merged RUNDOWN overlay as one frame lays it out: a <see cref="Title"/> line, then one line per row,
/// screen-space from an origin the user can drag (<see cref="DefaultOrigin"/> until moved). With no rows nothing is laid out
/// (CRC draws a coordination list only when it has entries).
/// </summary>
/// <param name="Lines">The lines top to bottom: the title first, then the rows; empty when there are no rows.</param>
public sealed record ScopeRundownList(IReadOnlyList<ScopeRundownLine> Lines)
{
    /// <summary>
    /// Where the title line's text box sits until the user moves the list, in canvas points: top-left, below the entry
    /// preview at its largest and clear of the SSA card at its default place.
    /// </summary>
    public static readonly SKPoint DefaultOrigin = new(8f, 190f);

    /// <summary>The list's title line.</summary>
    public const string Title = "RUNDOWN";

    /// <summary>The brightness a blinking row's off phase draws at, as CRC's <c>SystemListCoordination.SetColors</c> does.</summary>
    private const int BlinkOffPercent = 25;

    /// <summary>The list with no lines.</summary>
    public static readonly ScopeRundownList Empty = new([]);

    /// <summary>
    /// Lays out <paramref name="rows"/> under the title, each line one <see cref="ScopeTextMetrics.LineHeight"/> below the last,
    /// a blinking row drawn at a quarter brightness when <paramref name="flashOn"/> is false, the title's text box at
    /// <paramref name="origin"/>. No rows give <see cref="Empty"/>.
    /// </summary>
    public static ScopeRundownList Layout(IReadOnlyList<RundownRow> rows, ScopeTextMetrics metrics, bool flashOn, SKPoint origin)
    {
        if (rows.Count == 0)
        {
            return Empty;
        }

        var lines = new List<ScopeRundownLine>(rows.Count + 1) { Line(null, Title, SKColors.LimeGreen, origin, metrics) };
        for (int i = 0; i < rows.Count; i++)
        {
            RundownRow row = rows[i];
            var topLeft = new SKPoint(origin.X, origin.Y + ((i + 1) * metrics.LineHeight));
            lines.Add(Line(row, row.Text, ColorOf(row, flashOn), topLeft, metrics));
        }

        return new ScopeRundownList(lines);
    }

    /// <summary>Whether any line's text box, the title's included, contains the point; false everywhere with no lines.</summary>
    public bool Contains(float x, float y)
    {
        foreach (ScopeRundownLine line in Lines)
        {
            if (line.Rect.Contains(x, y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The row whose line's text box contains the point; the title line and empty space give null.</summary>
    public RundownRow? RowAt(float x, float y)
    {
        foreach (ScopeRundownLine line in Lines)
        {
            if ((line.Row is not null) && line.Rect.Contains(x, y))
            {
                return line.Row;
            }
        }

        return null;
    }

    // One line whose text box's top-left corner is topLeft, its baseline a font size below that top.
    private static ScopeRundownLine Line(RundownRow? row, string text, SKColor color, SKPoint topLeft, ScopeTextMetrics metrics)
    {
        float baseline = topLeft.Y + metrics.Size;
        var rect = new SKRect(topLeft.X, topLeft.Y, topLeft.X + metrics.Measure(text), topLeft.Y + metrics.LineHeight);
        return new ScopeRundownLine(row, text, color, rect, baseline);
    }

    private static SKColor ColorOf(RundownRow row, bool flashOn)
    {
        SKColor color = (row.Color == RundownRowColor.Caution) ? SKColors.Yellow : SKColors.LimeGreen;
        return (row.Blinks && !flashOn) ? ScopeBrightness.Apply(color, BlinkOffPercent) : color;
    }
}
