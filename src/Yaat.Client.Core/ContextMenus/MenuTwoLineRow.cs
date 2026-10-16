using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// A variant row drawn on two lines by <see cref="MenuTwoLineRowTemplate"/>: the glyph and the label on the first, the whole
/// command the row sends under it in the monospace font, wrapped and never trimmed; a dimmed row is drawn faded.
/// </summary>
/// <param name="Glyph">The quick-command glyph leading the row.</param>
/// <param name="Label">What the row names: <c>Hold short 10R at A</c>.</param>
/// <param name="Command">The whole command the row sends.</param>
/// <param name="IsDimmed">Whether the row is drawn faded.</param>
public sealed record MenuTwoLineRow(QuickCommandGlyph Glyph, string Label, string Command, bool IsDimmed)
{
    /// <summary>The row as one line of text, <c>Label — Command</c>, which is also its accessible name.</summary>
    public override string ToString() => $"{Label} — {Command}";
}

/// <summary>
/// The view of a <see cref="MenuTwoLineRow"/>: the glyph beside a label over the command, laid out as the Preset taxi route
/// rows put the taxiways under the name, except that the command wraps instead of trimming.
/// </summary>
public static class MenuTwoLineRowTemplate
{
    /// <summary>Builds <paramref name="row"/>'s view, named for assistive technology by <see cref="MenuTwoLineRow.ToString"/>.</summary>
    public static Control Build(MenuTwoLineRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Opacity = row.IsDimmed ? MenuGlyphRowTemplate.DimmedOpacity : 1 };
        AutomationProperties.SetName(grid, row.ToString());

        Viewbox glyph = QuickCommandStrip.GlyphIcon(row.Glyph);
        glyph.Margin = new Thickness(0, 0, 8, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(glyph);

        var command = new TextBlock
        {
            Text = row.Command,
            FontSize = 11.5,
            Opacity = 0.8,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));

        var lines = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = row.Label,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                command,
            },
        };
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);
        return grid;
    }
}
