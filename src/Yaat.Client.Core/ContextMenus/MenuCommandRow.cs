using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.ContextMenus;

/// <summary>Where a <see cref="MenuCommandRow"/> shows its detail against its name.</summary>
public enum MenuDetailPlacement
{
    /// <summary>After the name on the same line, dimmed at 12 pt and prefixed <c> · </c>.</summary>
    Inline,

    /// <summary>Under the name, dimmed at 11 pt.</summary>
    Stacked,
}

/// <summary>A command row's leading badge: its text and the brush that outlines and colours it.</summary>
public sealed record MenuDetailBadge(string Text, IBrush Brush);

/// <summary>
/// What a clickable menu item's command row shows, drawn by <see cref="MenuCommandRowTemplate"/>: an optional leading
/// badge, the name with an optional detail, an optional distance, and the command the item sends. The hold-short rows
/// (<c>TW S1 · crossing on S · ~1,500 ft — HS S1@S</c>) and the Follow… and Give way to… traffic rows
/// (<c>SWA1182 · B737 · at parking · gate 1 · ~600 ft — FOLLOWG SWA1182</c>) are built from it.
/// </summary>
public sealed record MenuCommandRow
{
    /// <summary>The widest a menu holding an inline-detail row grows; past it the detail is trimmed and carried whole in the row's tooltip.</summary>
    public const double InlineRowMaxWidth = 480;

    /// <summary>The badge leading the row, or null for none.</summary>
    public required MenuDetailBadge? Badge { get; init; }

    /// <summary>What the row names: a hold bar, an aircraft's callsign and type.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the name is drawn semibold.</summary>
    public required bool EmphasizeName { get; init; }

    /// <summary>Whether the row is one its submenu recommends, its name, detail and distance drawn bold.</summary>
    public required bool IsHighlighted { get; init; }

    /// <summary>The dimmed detail beside or under the name, or null for none.</summary>
    public required string? Detail { get; init; }

    /// <summary>Where <see cref="Detail"/> sits against the name.</summary>
    public required MenuDetailPlacement DetailPlacement { get; init; }

    /// <summary>The distance text after the name, right-aligned in its column, or null for none.</summary>
    public required string? Distance { get; init; }

    /// <summary>The command the row sends, shown last in the monospace font.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// The row as one line of text, <c>Badge Name · Detail · Distance — Command</c>, each missing segment left out with
    /// its separator: <c>TW S1 · crossing on S · ~150 ft — HS S1</c>.
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        if (Badge is { } badge)
        {
            text.Append(badge.Text).Append(' ');
        }

        text.Append(Name);
        if (Detail is { } detail)
        {
            text.Append(" · ").Append(detail);
        }

        if (Distance is { } distance)
        {
            text.Append(" · ").Append(distance);
        }

        return text.Append(" — ").Append(Command).ToString();
    }
}

/// <summary>
/// The view of a <see cref="MenuCommandRow"/>: the badge outlined in its brush, the name with its detail, the distance,
/// then the command right-aligned in the dimmed monospace font. A missing badge, detail or distance adds no element. A row
/// whose detail sits inline gives it its own column, so the menu widens with it up to
/// <see cref="MenuCommandRow.InlineRowMaxWidth"/> (the menu's own cap) and past it the detail is ellipsis-trimmed rather
/// than running under the distance, the row carrying the whole detail as its tooltip.
/// </summary>
public sealed class MenuCommandRowTemplate : FuncDataTemplate<MenuCommandRow>
{
    /// <summary>The one template every command row's menu item uses.</summary>
    public static MenuCommandRowTemplate Instance { get; } = new();

    private MenuCommandRowTemplate()
        : base((row, _) => Build(row)) { }

    /// <summary>Builds <paramref name="row"/>'s view over the columns badge, name, distance, command.</summary>
    private static Grid Build(MenuCommandRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        if (row.Badge is { } badge)
        {
            grid.Children.Add(BadgeView(badge));
        }

        Control name = NameView(row, out TextBlock? detailView);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        if ((detailView is not null) && (row.Detail is { } wholeDetail))
        {
            TooltipTrimmedDetail(detailView, grid, wholeDetail);
        }

        if (row.Distance is { } distanceText)
        {
            var distance = new TextBlock
            {
                Text = distanceText,
                Margin = new Thickness(16, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            BoldWhenHighlighted(distance, row);
            Grid.SetColumn(distance, 2);
            grid.Children.Add(distance);
        }

        TextBlock command = CommandView(row.Command);
        Grid.SetColumn(command, 3);
        grid.Children.Add(command);
        return grid;
    }

    private static Border BadgeView(MenuDetailBadge badge) =>
        new()
        {
            BorderBrush = badge.Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badge.Text,
                FontSize = 11,
                Foreground = badge.Brush,
            },
        };

    /// <summary>
    /// The name, then its detail beside it (<see cref="MenuDetailPlacement.Inline"/>) or under it. An inline detail gets
    /// its own column so it is trimmed rather than spilling into the distance, and is handed back for its row's tooltip.
    /// </summary>
    private static Control NameView(MenuCommandRow row, out TextBlock? detailView)
    {
        detailView = null;
        TextBlock name = NameText(row);
        if (row.Detail is not { } detail)
        {
            return NamePanel(row, name, null);
        }

        detailView = DetailView(detail, row.DetailPlacement);
        BoldWhenHighlighted(detailView, row);
        if (row.DetailPlacement == MenuDetailPlacement.Inline)
        {
            var inline = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(name, Dock.Left);
            inline.Children.Add(name);
            inline.Children.Add(detailView);
            return inline;
        }

        return NamePanel(row, name, detailView);
    }

    private static TextBlock NameText(MenuCommandRow row)
    {
        var name = new TextBlock { Text = row.Name };
        if (row.EmphasizeName)
        {
            name.FontWeight = FontWeight.SemiBold;
        }

        BoldWhenHighlighted(name, row);
        return name;
    }

    /// <summary>The name over or beside <paramref name="detailView"/>, or alone when a row has no detail.</summary>
    private static StackPanel NamePanel(MenuCommandRow row, TextBlock name, TextBlock? detailView)
    {
        var panel = new StackPanel
        {
            Orientation = (row.DetailPlacement == MenuDetailPlacement.Inline) ? Orientation.Horizontal : Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(name);
        if (detailView is not null)
        {
            panel.Children.Add(detailView);
        }

        return panel;
    }

    /// <summary>
    /// Gives <paramref name="row"/> the whole <paramref name="detail"/> as its tooltip while <paramref name="detailView"/>
    /// has too little width to draw it whole, and takes the tooltip away again while it fits.
    /// </summary>
    private static void TooltipTrimmedDetail(TextBlock detailView, Grid row, string detail) =>
        detailView.SizeChanged += (_, _) => ToolTip.SetTip(row, NeedsTrimming(detailView) ? detail : null);

    /// <summary>Whether <paramref name="detailView"/> drew its text collapsed to an ellipsis, or had no room to draw it.</summary>
    private static bool NeedsTrimming(TextBlock detailView) =>
        ((detailView.Bounds.Width < 1) && !string.IsNullOrEmpty(detailView.Text)) || detailView.TextLayout.TextLines.Any(line => line.HasCollapsed);

    /// <summary>Draws <paramref name="text"/> bold when <paramref name="row"/> is highlighted; leaves it as it is otherwise.</summary>
    private static void BoldWhenHighlighted(TextBlock text, MenuCommandRow row)
    {
        if (row.IsHighlighted)
        {
            text.FontWeight = FontWeight.Bold;
        }
    }

    private static TextBlock DetailView(string detail, MenuDetailPlacement placement) =>
        placement switch
        {
            MenuDetailPlacement.Inline => new TextBlock
            {
                Text = $" · {detail}",
                FontSize = 12,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            MenuDetailPlacement.Stacked => new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Opacity = 0.8,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown detail placement."),
        };

    private static TextBlock CommandView(string commandText)
    {
        var command = new TextBlock
        {
            Text = commandText,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        return command;
    }
}
