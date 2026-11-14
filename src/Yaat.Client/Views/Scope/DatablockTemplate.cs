using SkiaSharp;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>The text measurements a template layout needs, decoupled from any one font object.</summary>
/// <param name="Measure">Advance width of a string as it will draw.</param>
/// <param name="Size">Font size: the ascent budget above each baseline.</param>
/// <param name="LineHeight">Baseline-to-baseline spacing.</param>
public sealed record ScopeTextMetrics(Func<string, float> Measure, float Size, float LineHeight)
{
    /// <summary>Measures with <paramref name="style"/>, as today's radar layouts do.</summary>
    public static ScopeTextMetrics FromStyle(TextStyle style) => new(style.Measure, style.Size, style.LineHeight);
}

/// <summary>
/// A laid-out datablock. Rects are relative to the block's text origin (the first line's baseline at x = 0)
/// until placed with <see cref="At"/>. Drawing reads each field's rect and text; there is no per-line text.
/// </summary>
/// <param name="Bounds">Block bounds, padding and column 0 included; hit-testing reads them.</param>
/// <param name="Fields">Every laid-out field: the single list drawing and hit-testing both read.</param>
/// <param name="TextBounds">
/// The bounds of the text block without column 0, padding included; the leader attaches to them. Equal to
/// <paramref name="Bounds"/> when the template has no column 0 or no mark shows.
/// </param>
public sealed record ScopeBlockLayout(SKRect Bounds, IReadOnlyList<FieldRect> Fields, SKRect TextBounds)
{
    /// <summary>Returns this layout with its text origin moved to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public ScopeBlockLayout At(float x, float y) =>
        this with
        {
            Bounds = Offset(Bounds, x, y),
            TextBounds = Offset(TextBounds, x, y),
            Fields = [.. Fields.Select(f => f.Offset(x, y))],
        };

    private static SKRect Offset(SKRect rect, float x, float y) => new(rect.Left + x, rect.Top + y, rect.Right + x, rect.Bottom + y);
}

/// <summary>
/// The template engine: lays a <see cref="TrackPresentation"/>'s fields into its template's lines for its level
/// (<see cref="ScopeTemplate.LinesAt"/>).
/// <para>
/// A field takes part when it has text, or when its slot is reserved and its stable text is non-empty; a
/// reserved field is measured on its stable text, so neither its rect nor the block bounds change between
/// flash phases. A line takes a row when one of its fields takes part, or when the template always shows it.
/// </para>
/// </summary>
public static class DatablockTemplate
{
    /// <summary>Gap between a column-0 mark and the text block (CRC <c>FdbRenderObject</c>: 2 + 1 + 3 px left of the fields).</summary>
    public const float ColumnGap = 6f;

    private readonly record struct PlacedField(ScopeSlotPlacement Placement, ScopeField Field)
    {
        public string MeasuredText => Placement.Reserved ? Field.StableText : Field.Text;
    }

    private readonly record struct LaidLine(IReadOnlyList<FieldRect> Rects, float Right);

    /// <summary>Lays <paramref name="presentation"/> out with the template its id names.</summary>
    public static ScopeBlockLayout Layout(TrackPresentation presentation, ScopeTextMetrics metrics) =>
        Layout(presentation, ScopeTemplates.Get(presentation.TemplateId), metrics);

    /// <summary>Lays <paramref name="presentation"/> out with <paramref name="template"/>, measuring with <paramref name="metrics"/>.</summary>
    public static ScopeBlockLayout Layout(TrackPresentation presentation, ScopeTemplate template, ScopeTextMetrics metrics)
    {
        var bySlot = presentation.Fields.ToDictionary(f => f.Slot);
        var rects = new List<FieldRect>();
        int rows = 0;
        float top = -metrics.Size;
        float maxRight = 0f;

        foreach (ScopeTemplateLine line in template.LinesAt(presentation.Level))
        {
            List<PlacedField> present = PresentFields(line, bySlot);
            if ((present.Count == 0) && !line.AlwaysShown)
            {
                continue;
            }

            LaidLine laid = LayLine(template, metrics, present, top);
            rects.AddRange(laid.Rects);
            float trailing = ((present.Count > 0) && line.TrailingGapInBounds) ? template.FieldGap : 0f;
            maxRight = MathF.Max(maxRight, laid.Right + trailing);
            rows++;
            top += metrics.LineHeight;
        }

        float pad = template.Padding;
        var bounds = new SKRect(-pad, -metrics.Size - pad, maxRight + pad, ((rows - 1) * metrics.LineHeight) + pad);
        if (template.Column0 is not { } marks)
        {
            return new ScopeBlockLayout(bounds, rects, bounds);
        }

        return new ScopeBlockLayout(Widen(bounds, LayColumn0(marks, metrics, bySlot, rects), template.Padding), rects, bounds);
    }

    /// <summary>
    /// Adds a rect for each column-0 mark with text, right-aligned <see cref="ColumnGap"/> left of the text block on its row,
    /// and returns the marks' extent; <see cref="SKRect.Empty"/> when no mark shows.
    /// </summary>
    private static SKRect LayColumn0(
        IReadOnlyList<ScopeColumn0Mark> marks,
        ScopeTextMetrics metrics,
        Dictionary<ScopeFieldSlot, ScopeField> bySlot,
        List<FieldRect> rects
    )
    {
        SKRect extent = SKRect.Empty;
        foreach (ScopeColumn0Mark mark in marks)
        {
            if (!bySlot.TryGetValue(mark.Slot, out ScopeField? field) || (field.Text.Length == 0))
            {
                continue;
            }

            float width = metrics.Measure(field.Text);
            float left = -(width + ColumnGap);
            float top = -metrics.Size + ((mark.Row - 1) * metrics.LineHeight);
            var rect = new SKRect(left, top, left + width, top + metrics.Size);
            rects.Add(new FieldRect(mark.Slot, rect, field.Text, field.Token, field.Style));
            extent = extent.IsEmpty ? rect : SKRect.Union(extent, rect);
        }
        return extent;
    }

    /// <summary>
    /// <paramref name="bounds"/> widened left, and down when the block has fewer rows than a mark's, to hold
    /// <paramref name="marks"/> with <paramref name="pad"/>; unchanged when no mark shows.
    /// </summary>
    private static SKRect Widen(SKRect bounds, SKRect marks, float pad) =>
        marks.IsEmpty
            ? bounds
            : new SKRect(MathF.Min(bounds.Left, marks.Left - pad), bounds.Top, bounds.Right, MathF.Max(bounds.Bottom, marks.Bottom + pad));

    private static List<PlacedField> PresentFields(ScopeTemplateLine line, Dictionary<ScopeFieldSlot, ScopeField> bySlot)
    {
        var present = new List<PlacedField>(line.Slots.Count);
        foreach (ScopeSlotPlacement placement in line.Slots)
        {
            if (!bySlot.TryGetValue(placement.Slot, out ScopeField? field))
            {
                continue;
            }
            var placed = new PlacedField(placement, field);
            if (placed.MeasuredText.Length > 0)
            {
                present.Add(placed);
            }
        }
        return present;
    }

    private static LaidLine LayLine(ScopeTemplate template, ScopeTextMetrics metrics, List<PlacedField> fields, float top)
    {
        var rects = new List<FieldRect>(fields.Count);
        string run = "";
        float runStart = 0f;
        float right = 0f;

        foreach (PlacedField placed in fields)
        {
            string lead = run.Length > 0 ? run + placed.Placement.Separator : "";
            float left = lead.Length > 0 ? runStart + metrics.Measure(lead) : runStart;
            right = runStart + metrics.Measure(lead + placed.MeasuredText);
            var rect = new SKRect(left, top, right, top + metrics.Size);
            rects.Add(new FieldRect(placed.Placement.Slot, rect, placed.Field.Text, placed.Field.Token, placed.Field.Style));

            if (template.GapMode == ScopeGapMode.PixelGap)
            {
                runStart = right + template.FieldGap;
            }
            else
            {
                run = lead + placed.MeasuredText;
            }
        }

        return new LaidLine(rects, right);
    }
}
