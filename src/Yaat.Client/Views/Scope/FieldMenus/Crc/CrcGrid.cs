using Avalonia;
using Avalonia.Media;

namespace Yaat.Client.Views.Scope.FieldMenus.Crc;

/// <summary>
/// CRC's ERAM character grid at the FDB menu size (FontSize.Two of EramText): every glyph sits in a 10×13 cell on a 12 px
/// advance and an 11 px line. The stand-in font is drawn per cell, centred, so every size matches CRC's.
/// </summary>
internal static class CrcGrid
{
    public const int Advance = 12;
    public const int LineHeight = 11;
    public const int GlyphWidth = 10;
    public const int GlyphHeight = 13;

    /// <summary>The pixel size JetBrains Mono is drawn at so its glyphs fit the 10×13 cell.</summary>
    public const float FontSize = 14f;

    /// <summary>The baseline's offset below a text's top edge.</summary>
    public const int Baseline = 10;

    /// <summary>CRC's HorizontalMeasurement: <paramref name="chars"/> characters wide plus <paramref name="add"/> pixels.</summary>
    public static int Chars(double chars, int add) => (int)Math.Truncate(Advance * chars) + add;

    /// <summary>CRC's HorizontalMeasurement: <paramref name="chars"/> characters wide.</summary>
    public static int Chars(double chars) => Chars(chars, 0);

    /// <summary>CRC's VerticalMeasurement with no line spacing: <paramref name="lines"/> lines high.</summary>
    public static int Lines(double lines) => (int)Math.Truncate(LineHeight * lines);

    /// <summary>A text's width: every glyph advances 12 px except the last, which is its 10 px cell.</summary>
    public static int TextWidth(string text) => (text.Length == 0) ? 0 : (Advance * (text.Length - 1)) + GlyphWidth;
}

/// <summary>
/// CRC's FDB menu colours at the default brightness settings: each style's EramColor scaled by its brightness group
/// (Bcg defaults) times the system brightness factor (66 + 90 / 100 × 34) / 100, truncated, as Style.Calculate does.
/// </summary>
internal static class CrcPalette
{
    /// <summary>FdbMenuCurrentValue and FdbMenuPickAreaBackground text: White (243) at Bcg.Text 90 → 86 %.</summary>
    public static readonly Color Text = Color.FromRgb(208, 208, 208);

    /// <summary>FdbMenuInvalid: LightGray (210) at Bcg.Text → 86 %, for an altitude at or above 290 without RVSM.</summary>
    public static readonly Color Invalid = Color.FromRgb(180, 180, 180);

    /// <summary>FdbMenuPickAreaBackground fill: Gray (199) at Bcg.Button 80 → 77 %.</summary>
    public static readonly Color PickBackground = Color.FromRgb(153, 153, 153);

    /// <summary>FdbMenuOutlines: White (243) at Bcg.Border 56 → 54 %.</summary>
    public static readonly Color Outline = Color.FromRgb(131, 131, 131);

    /// <summary>FdbMenuEmphasisBackground: BurntCoral (220, 160, 155) at Bcg.Button → 77 %, the lit pick area and the selected value.</summary>
    public static readonly Color Emphasis = Color.FromRgb(169, 123, 119);

    /// <summary>FdbMenuCursorSelectionEmphasis: White (243) at Bcg.PairedTarget 92 → 88 %, the outline under the cursor.</summary>
    public static readonly Color CursorEmphasis = Color.FromRgb(213, 213, 213);

    /// <summary>CheckListViewScrollPickAreasEmphasized: White at the derived brightness, an arrow that can scroll.</summary>
    public static readonly Color ScrollEnabled = Color.FromRgb(177, 177, 177);

    /// <summary>CheckListViewScrollPickAreasDeemphasized: Gray at the derived brightness, an arrow that cannot.</summary>
    public static readonly Color ScrollDisabled = Color.FromRgb(145, 145, 145);

    /// <summary>FdbMenuCurrentValue background.</summary>
    public static readonly Color Black = Color.FromRgb(0, 0, 0);
}

/// <summary>A pixel rectangle on the menu.</summary>
internal readonly record struct CrcRect(int X, int Y, int Width, int Height)
{
    public bool Contains(Point point) => (point.X >= X) && (point.X < X + Width) && (point.Y >= Y) && (point.Y < Y + Height);

    public Point Center => new(X + (Width / 2.0), Y + (Height / 2.0));
}

/// <summary>Top, right, bottom and left edges, in CRC's Padding / Margin order.</summary>
internal readonly record struct CrcEdges(int Top, int Right, int Bottom, int Left)
{
    public static readonly CrcEdges None = new(0, 0, 0, 0);
}

/// <summary>One thing the menu draws.</summary>
internal abstract record CrcDrawOp;

/// <summary>A filled or outlined rectangle; a negative <paramref name="Thickness"/> outlines inside it, a positive one outside.</summary>
internal sealed record CrcQuadOp(CrcRect Rect, Color? Fill, Color? Border, int Thickness) : CrcDrawOp;

/// <summary>A text whose glyph cells start at (<paramref name="X"/>, <paramref name="Y"/>).</summary>
internal sealed record CrcTextOp(int X, int Y, string Text, Color Color) : CrcDrawOp;

/// <summary>A pick area: <paramref name="Key"/> names it (its label or value), <paramref name="Pick"/> runs it with Shift.</summary>
internal sealed record CrcPickTarget(string Key, CrcRect Area, bool IsText, Action<bool> Pick);

/// <summary>What one layout pass draws and where its pick areas are, in drawing order.</summary>
internal sealed class CrcRenderSink
{
    public List<CrcDrawOp> Ops { get; } = [];

    public List<CrcPickTarget> Targets { get; } = [];
}
