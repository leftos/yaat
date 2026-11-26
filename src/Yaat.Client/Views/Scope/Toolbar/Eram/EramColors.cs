using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>
/// CRC's ERAM toolbar palette at full brightness, as the mock's footer records it: the grey bar, a menu's blue, a
/// toggle's black, an inc/dec's green and a command's teal pick area, white text and borders, the gold tear-off strip,
/// and the coral an open menu's button takes.
/// </summary>
public static class EramColors
{
    /// <summary>The bar's background, and a toggle's fill while it is on (CRC's active toggle).</summary>
    public static readonly Color Toolbar = Color.FromRgb(199, 199, 199);

    /// <summary>CRC's grey: the move-down pick area's border.</summary>
    public static readonly Color Gray = Toolbar;

    /// <summary>A menu button's fill.</summary>
    public static readonly Color MenuBlue = Color.FromRgb(0, 0, 212);

    /// <summary>A toggle's fill while it is off.</summary>
    public static readonly Color ToggleBlack = Color.FromRgb(0, 0, 0);

    /// <summary>An increment/decrement button's fill.</summary>
    public static readonly Color IncDecGreen = Color.FromRgb(0, 205, 0);

    /// <summary>A command button's fill.</summary>
    public static readonly Color CommandTeal = Color.FromRgb(0, 201, 212);

    /// <summary>Every button's text.</summary>
    public static readonly Color Text = Color.FromRgb(243, 243, 243);

    /// <summary>A button's 1 DIP border, and the bar's interior border line.</summary>
    public static readonly Color Border = Text;

    /// <summary>The gold tear-off strip down a button's left edge.</summary>
    public static readonly Color TearoffGold = Color.FromRgb(255, 255, 161);

    /// <summary>The fill of the menu button whose menu is the open one: CRC's BurntCoral.</summary>
    public static readonly Color BurntCoral = Color.FromRgb(220, 160, 155);

    /// <summary>How far a button YAAT has no feature for is dimmed, as CRC dims a button it cannot pick.</summary>
    public const double DisabledOpacity = 0.45;

    /// <summary>An immutable brush of <paramref name="color"/>, safe to share across threads.</summary>
    /// <param name="color">The colour to draw.</param>
    public static IBrush Solid(Color color) => new ImmutableSolidColorBrush(color);
}
