using SkiaSharp;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope renderer's datablock text fonts: each track's text draws in its owner kind's font, today's compact font at the
/// frame's CHAR size, or the kind's Terminus strike when that kind's text is realistic. Realistic text draws glyph by glyph
/// at its strike's glyph pitch (<see cref="ScopeTextFonts.GlyphPitch"/>), the pitch its layout measured with.
/// </summary>
public sealed partial class ScopeRenderer
{
    // The realistic fonts by strike, and each one's glyph pitch by font: text in a font found here draws glyph by glyph.
    private readonly Dictionary<int, SKFont> _realisticFonts = [];
    private readonly Dictionary<SKFont, float> _glyphPitches = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The font <paramref name="track"/>'s text draws in: its kind's Terminus strike (<see cref="ScopeRenderSnapshot.EramStrikePx"/>
    /// for an ERAM-look track, <see cref="ScopeRenderSnapshot.StarsStrikePx"/> for any other) when it has one, else the compact
    /// font at the frame's size.
    /// </summary>
    private SKFont FontOf(ScopeTrack track, ScopeRenderSnapshot snapshot) =>
        ((track.EramLook ? snapshot.EramStrikePx : snapshot.StarsStrikePx) is { } strikePx) ? RealisticFont(strikePx) : FontFor(snapshot.FontSize);

    private SKFont RealisticFont(int strikePx)
    {
        if (!_realisticFonts.TryGetValue(strikePx, out SKFont? font))
        {
            font = ScopeTextFonts.MakeFont(strikePx);
            _realisticFonts[strikePx] = font;
            _glyphPitches[font] = ScopeTextFonts.GlyphPitch(strikePx);
        }

        return font;
    }

    /// <summary>The glyph pitch <paramref name="font"/> draws at when it is a realistic font; null for the compact font.</summary>
    private float? GlyphPitchOf(SKFont font) => _glyphPitches.TryGetValue(font, out float pitch) ? pitch : null;

    /// <summary>How wide <paramref name="text"/> draws in <paramref name="font"/>: its length in glyph pitches, or the font's own measure.</summary>
    private float TextWidth(SKFont font, string text) => (GlyphPitchOf(font) is { } pitch) ? text.Length * pitch : font.MeasureText(text);

    /// <summary>
    /// Draws <paramref name="text"/> left-aligned with its baseline at <paramref name="at"/>: a realistic font glyph by glyph at
    /// its glyph pitch, the compact font as one run.
    /// </summary>
    private void DrawTextAt(SKCanvas canvas, string text, SKPoint at, SKFont font, SKPaint paint)
    {
        if (GlyphPitchOf(font) is not { } pitch)
        {
            canvas.DrawText(text, at.X, at.Y, SKTextAlign.Left, font, paint);
            return;
        }

        ushort[] glyphs = font.GetGlyphs(text);
        if (glyphs.Length == 0)
        {
            return;
        }

        var positions = new SKPoint[glyphs.Length];
        for (int i = 0; i < glyphs.Length; i++)
        {
            positions[i] = new SKPoint(at.X + (i * pitch), at.Y);
        }

        using var builder = new SKTextBlobBuilder();
        SKPositionedRunBuffer run = builder.AllocatePositionedRun(font, glyphs.Length);
        run.SetGlyphs(glyphs);
        run.SetPositions(positions);
        using SKTextBlob? blob = builder.Build();
        if (blob is not null)
        {
            canvas.DrawText(blob, 0f, 0f, paint);
        }
    }

    private void DisposeTextFonts()
    {
        foreach (SKFont font in _realisticFonts.Values)
        {
            font.Dispose();
        }

        _realisticFonts.Clear();
        _glyphPitches.Clear();
    }
}
