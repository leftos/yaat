using Avalonia.Controls;
using Avalonia.Styling;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Shared appearance settings for the EuroScope-style tag flyouts. The radar view writes
/// these from user preferences (RadarFlyoutFontSize); each flyout's Build() reads them
/// when constructing menu items, so a font-size change applied via SyncAssignmentTint
/// takes effect on the next flyout open without flyouts having to take a preferences arg.
/// </summary>
internal static class FlyoutAppearance
{
    /// <summary>MenuItem font size in pixels. Default matches Avalonia Fluent's body font.</summary>
    public static double FontSize { get; set; } = 12.0;

    /// <summary>
    /// Apply the current <see cref="FontSize"/> to every MenuItem inside the given context menu
    /// via a single Style — cheaper and more forwards-compatible than mutating each item.
    /// </summary>
    public static void ApplyFontSize(ContextMenu menu)
    {
        var style = new Style(x => x.OfType<MenuItem>()) { Setters = { new Avalonia.Styling.Setter(MenuItem.FontSizeProperty, FontSize) } };
        menu.Styles.Add(style);
    }
}
