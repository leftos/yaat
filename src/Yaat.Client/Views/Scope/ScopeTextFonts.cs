using System.Collections.Concurrent;
using SkiaSharp;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope's realistic datablock text: Terminus TTF Bold, bundled as an embedded resource and drawn from its hand-drawn
/// bitmap strikes, and CRC's STARS and ERAM text-size ladders mapped onto those strikes with CRC's row pitch rules.
/// </summary>
public static class ScopeTextFonts
{
    /// <summary>The manifest resource name of the bundled Terminus TTF Bold.</summary>
    public const string ResourceName = "Yaat.Client.Fonts.TerminusTTF-Bold.ttf";

    /// <summary>The pixel sizes Terminus TTF Bold carries a hand-drawn bitmap strike for.</summary>
    public static IReadOnlyList<int> Strikes { get; } = [12, 14, 16, 18, 20, 22, 24, 28, 32];

    /// <summary>The Terminus strike, in px, for each CRC ERAM text size 0-4.</summary>
    // Nearest strike by cap height, CRC ERAM caps 8, 9, 11, 13, 15 against the strikes' capitals (12: 8, 14: 10,
    // 16: 10, 18: 12, 20: 13, 22: 14, 24: 15, 28: 18, 32: 20); a tie takes the larger cap, as the issue lists.
    public static IReadOnlyList<int> EramStrikePx { get; } = [12, 14, 18, 20, 24];

    /// <summary>The Terminus strike, in px, for each CRC STARS text size 0-6.</summary>
    // Nearest strike by cap height, CRC STARS caps 7, 8, 9, 11, 12, 14, 17 against the same strike capitals; the issue
    // lists STARS 2-4 = 14, 18, 18, and 0-1 and 5-6 follow the same rule (a tie takes the larger cap).
    public static IReadOnlyList<int> StarsStrikePx { get; } = [12, 12, 14, 18, 18, 22, 28];

    /// <summary>The baseline-to-baseline row pitch, in px, for each CRC ERAM text size 0-4: CRC's cap height plus 7.</summary>
    public static IReadOnlyList<int> EramRowPitch { get; } = [8 + 7, 9 + 7, 11 + 7, 13 + 7, 15 + 7];

    /// <summary>
    /// The baseline-to-baseline row pitch, in px, for each CRC STARS text size 0-6: CRC's line height (8, 9, 10, 12, 13,
    /// 15, 18) plus its line spacing (2, 2, 3, 3, 4, 4, 4).
    /// </summary>
    public static IReadOnlyList<int> StarsRowPitch { get; } = [8 + 2, 9 + 2, 10 + 3, 12 + 3, 13 + 4, 15 + 4, 18 + 4];

    private static readonly Lazy<SKTypeface> LazyTypeface = new(() => LoadTypeface(ResourceName));
    private static readonly ConcurrentDictionary<int, float> FontAdvances = new();

    /// <summary>The bundled Terminus TTF Bold, loaded once and never disposed.</summary>
    public static SKTypeface Typeface => LazyTypeface.Value;

    /// <summary>
    /// A Terminus font at one strike size that draws the strike's bitmap: aliased, fully hinted, whole-pixel positioned.
    /// The caller owns the font.
    /// </summary>
    public static SKFont MakeFont(int strikePx) =>
        new(Typeface, strikePx)
        {
            Edging = SKFontEdging.Alias,
            Hinting = SKFontHinting.Full,
            Subpixel = false,
            EmbeddedBitmaps = true,
        };

    /// <summary>
    /// The pitch, in px, from one glyph's origin to the next at a strike size. The 14 and 18 px strikes' glyphs are a
    /// pixel wider than the font's advance, so they are placed at the strike cell (8 and 10 px); every other strike
    /// keeps its font advance.
    /// </summary>
    public static float GlyphPitch(int strikePx) =>
        strikePx switch
        {
            14 => 8f,
            18 => 10f,
            _ => FontAdvances.GetOrAdd(strikePx, FontAdvance),
        };

    /// <summary>Reads a font from this assembly's manifest resources; a missing resource throws naming it.</summary>
    internal static SKTypeface LoadTypeface(string resourceName)
    {
        using Stream stream =
            typeof(ScopeTextFonts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded font resource '{resourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var data = SKData.CreateCopy(buffer.ToArray());
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException($"The embedded font resource '{resourceName}' is not a font.");
    }

    private static float FontAdvance(int strikePx)
    {
        using SKFont font = MakeFont(strikePx);
        return font.MeasureText("0");
    }
}
