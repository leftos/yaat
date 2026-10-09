using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What a clickable menu item's glyph row shows, drawn by <see cref="MenuGlyphRowTemplate"/>: a quick-command glyph, the
/// label, an optional note after it, and the command the item sends; a dimmed row is drawn faded but stays clickable. The
/// pattern entries' runway rows (<c>Runway 28R · short — ELD 28R</c>, the glyph rotated to the runway) are built from it.
/// </summary>
public sealed record MenuGlyphRow
{
    /// <summary>The glyph leading the row, drawn as the strip draws it, turned as it says.</summary>
    public required QuickCommandGlyph Glyph { get; init; }

    /// <summary>What the row names: <c>Runway 28R</c>.</summary>
    public required string Label { get; init; }

    /// <summary>The note after the label, prefixed <c> · </c> and dimmed: <c>short</c>; null for none.</summary>
    public required string? Note { get; init; }

    /// <summary>Whether the whole row is drawn faded, as one the menu offers last and does not recommend.</summary>
    public required bool IsDimmed { get; init; }

    /// <summary>The command the row sends, shown last in the monospace font.</summary>
    public required string Command { get; init; }

    /// <summary>The row as one line of text, <c>Label · Note — Command</c>, the note left out with its separator when there is none.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(Label);
        if (Note is { } note)
        {
            text.Append(" · ").Append(note);
        }

        return text.Append(" — ").Append(Command).ToString();
    }
}

/// <summary>
/// The view of a <see cref="MenuGlyphRow"/>: the glyph, the label with its dimmed note, then the command right-aligned in
/// the dimmed monospace font, the whole row faded when <see cref="MenuGlyphRow.IsDimmed"/>.
/// </summary>
public sealed class MenuGlyphRowTemplate : FuncDataTemplate<MenuGlyphRow>
{
    /// <summary>How opaque a dimmed row is drawn.</summary>
    public const double DimmedOpacity = 0.5;

    /// <summary>The one template every glyph row's menu item uses.</summary>
    public static MenuGlyphRowTemplate Instance { get; } = new();

    private MenuGlyphRowTemplate()
        : base((row, _) => Build(row)) { }

    /// <summary>Builds <paramref name="row"/>'s view over the columns glyph, label, command.</summary>
    private static Grid Build(MenuGlyphRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Opacity = row.IsDimmed ? DimmedOpacity : 1 };
        Viewbox glyph = QuickCommandStrip.GlyphIcon(row.Glyph);
        glyph.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(glyph);

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        if (row.Note is { } note)
        {
            label.Children.Add(
                new TextBlock
                {
                    Text = $" · {note}",
                    FontSize = 12,
                    Opacity = 0.8,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var command = new TextBlock
        {
            Text = row.Command,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        return grid;
    }
}
