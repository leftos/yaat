using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The parts the panel flyouts (Taxi to runway, Air taxi, Cleared / Expect approach) share: the style that keeps the one
/// menu row holding a panel at its resting background, and the panel's separator line.
/// </summary>
internal static class FlyoutPanelParts
{
    private static readonly IImmutableSolidColorBrush SeparatorBrush = new ImmutableSolidColorBrush(Color.Parse("#3D3D3D"));

    /// <summary>
    /// Keeps a menu row of class <paramref name="panelClass"/> at its resting background while it is selected or under the
    /// pointer.
    /// </summary>
    public static Style PlainPanelRowStyle(string panelClass)
    {
        Selector LayoutRoot(Selector? x, string state) =>
            x.OfType<MenuItem>().Class(panelClass).Class(state).Template().OfType<Border>().Name("PART_LayoutRoot");

        return new Style(x => Selectors.Or(LayoutRoot(x, ":selected"), LayoutRoot(x, ":pointerover")))
        {
            Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent) },
        };
    }

    /// <summary>A one-pixel separator line across the panel.</summary>
    public static Border SeparatorLine() =>
        new()
        {
            Height = 1,
            Background = SeparatorBrush,
            Margin = new Thickness(2, 4),
        };
}
