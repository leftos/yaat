using SkiaSharp;

namespace Yaat.Client.Views.Map;

/// <summary>
/// A paired <see cref="SKFont"/> (size, typeface, subpixel positioning) and <see cref="SKPaint"/>
/// (color, style, stroke) for drawing and measuring one kind of text.
/// <para>
/// SkiaSharp splits text state across the two types: the font decides glyph geometry and metrics,
/// the paint decides how those glyphs are filled. Every measure/draw call therefore needs both, and
/// they must describe the same text — a font measured against one size and drawn at another puts
/// the hit rect somewhere the glyphs are not. Bundling them makes that pairing a single value that
/// callers cannot split by accident.
/// </para>
/// <para>
/// This matters most for the datablock stack, where geometry is computed twice — once on the draw
/// path (<c>TargetRenderer</c>) and once on the hit-test path (<c>RadarCanvas</c>) — against
/// separate paint instances. Passing one <see cref="TextStyle"/> keeps both sides measuring with
/// the same font.
/// </para>
/// <para>
/// This is a non-owning view over two objects the renderer allocates and disposes; copying a
/// <see cref="TextStyle"/> does not copy the font or paint.
/// </para>
/// </summary>
public readonly record struct TextStyle(SKFont Font, SKPaint Paint)
{
    /// <summary>Baseline-to-baseline spacing used by every multi-line block in the radar/ground views.</summary>
    public float LineHeight => Font.Size + 2f;

    /// <summary>Font size in pixels — the ascent budget above a baseline that block rects reserve.</summary>
    public float Size => Font.Size;

    /// <summary>
    /// Advance width of <paramref name="text"/> as this style will draw it. The paint is passed so the
    /// measurement tracks anything that widens the drawn glyphs (a stroke, a path effect) — today every
    /// text paint here is fill-only, so this matches the font-only measurement.
    /// </summary>
    public float Measure(string text) => Font.MeasureText(text, Paint);
}
