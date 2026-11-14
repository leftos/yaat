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
/// <param name="Bounds">Block bounds, padding included.</param>
/// <param name="Fields">Every laid-out field: the single list drawing and hit-testing both read.</param>
public sealed record ScopeBlockLayout(SKRect Bounds, IReadOnlyList<FieldRect> Fields)
{
    /// <summary>Returns this layout with its text origin moved to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public ScopeBlockLayout At(float x, float y)
    {
        var bounds = new SKRect(Bounds.Left + x, Bounds.Top + y, Bounds.Right + x, Bounds.Bottom + y);
        return this with { Bounds = bounds, Fields = [.. Fields.Select(f => f.Offset(x, y))] };
    }
}

/// <summary>
/// The template engine: lays a <see cref="TrackPresentation"/>'s fields into its template's lines.
/// <para>
/// A field takes part when it has text, or when its slot is reserved and its stable text is non-empty; a
/// reserved field is measured on its stable text, so neither its rect nor the block bounds change between
/// flash phases. A line takes a row when one of its fields takes part, or when the template always shows it.
/// </para>
/// </summary>
public static class DatablockTemplate
{
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

        foreach (ScopeTemplateLine line in template.Lines)
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
        return new ScopeBlockLayout(bounds, rects);
    }

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
