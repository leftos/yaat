using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.ContextMenus;

/// <summary>Where a <see cref="MenuCommandRow"/> shows its detail against its name.</summary>
public enum MenuDetailPlacement
{
    /// <summary>Under the name, dimmed at 11 pt.</summary>
    Stacked,

    /// <summary>
    /// Under the name in the monospace font at 11.5 pt, dimmed: a route's taxiways. The row is capped at
    /// <see cref="MenuCommandRow.RowMaxWidth"/>; past it the line, the name and the command trim with an ellipsis and
    /// the row carries its whole text in its tooltip.
    /// </summary>
    Route,
}

/// <summary>
/// What a clickable menu item's command row shows, drawn by <see cref="MenuCommandRowTemplate"/>: an optional leading
/// glyph, the name with an optional detail, an optional distance, and the command the item sends. The exit rows and the
/// Preset taxi route rows (the preset glyph, <c>TERMINAL to 30</c> over <c>via T U W</c>, <c>~4,700 ft</c>,
/// <c>TAXI T U W RWY 30</c>) are built from it.
/// </summary>
public sealed record MenuCommandRow
{
    /// <summary>
    /// The widest a menu holding a route row grows, and the widest a designed flyout's rows are; past it a route row's
    /// text is trimmed and carried whole in its tooltip.
    /// </summary>
    public const double RowMaxWidth = 480;

    /// <summary>The widest a route row's command is drawn before it too is trimmed with an ellipsis.</summary>
    public const double RouteCommandMaxWidth = 180;

    /// <summary>The size a row's command is drawn at, and a catalog row's command alias (<see cref="MenuAliasTemplate"/>).</summary>
    public const double CommandFontSize = 12;

    /// <summary>The opacity that dims a row's command, and a catalog row's command alias (<see cref="MenuAliasTemplate"/>).</summary>
    public const double CommandOpacity = 0.7;

    /// <summary>The quick-command glyph leading the row, or null for none.</summary>
    public required QuickCommandGlyph? Glyph { get; init; }

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
    /// The row as one line of text, <c>Name · Detail · Distance — Command</c>, each missing segment left out with its
    /// separator: <c>S1 · crossing on S · ~150 ft — HS S1</c>.
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder();
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
/// The view of a <see cref="MenuCommandRow"/>: the glyph, the name with its detail under it, the distance, then the
/// command right-aligned in the dimmed monospace font. A missing glyph, detail or distance adds no element. A stacked
/// detail is clipped where the row is too narrow for it, and the row carries the whole detail as its tooltip while the
/// detail gets no width at all. A route row caps itself at
/// <see cref="MenuCommandRow.RowMaxWidth"/> and its command at <see cref="MenuCommandRow.RouteCommandMaxWidth"/>;
/// its name, route line and command each trim with an ellipsis, and while any is trimmed the row carries its whole text
/// as its tooltip.
/// </summary>
public sealed class MenuCommandRowTemplate : FuncDataTemplate<MenuCommandRow>
{
    /// <summary>The one template every command row's menu item uses.</summary>
    public static MenuCommandRowTemplate Instance { get; } = new();

    private MenuCommandRowTemplate()
        : base((row, _) => Build(row)) { }

    /// <summary>Builds <paramref name="row"/>'s view over the columns glyph, name, distance, command.</summary>
    private static Grid Build(MenuCommandRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        if (row.DetailPlacement == MenuDetailPlacement.Route)
        {
            grid.MaxWidth = MenuCommandRow.RowMaxWidth;
        }

        if (row.Glyph is { } glyphData)
        {
            Viewbox glyph = QuickCommandStrip.GlyphIcon(glyphData);
            glyph.Margin = new Thickness(0, 0, 8, 0);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(glyph);
        }

        Control name = NameView(row, out TextBlock nameText, out TextBlock? detailView);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        if ((row.DetailPlacement != MenuDetailPlacement.Route) && (detailView is not null) && (row.Detail is { } wholeDetail))
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
        if (row.DetailPlacement == MenuDetailPlacement.Route)
        {
            TrimRoute(grid, row, nameText, detailView, command);
        }

        return grid;
    }

    /// <summary>
    /// Caps a route row's <paramref name="command"/> and trims it with an ellipsis, then gives <paramref name="grid"/> the
    /// whole row as its tooltip while the name, the route line or the command is trimmed, and none while all fit.
    /// </summary>
    private static void TrimRoute(Grid grid, MenuCommandRow row, TextBlock name, TextBlock? route, TextBlock command)
    {
        command.MaxWidth = MenuCommandRow.RouteCommandMaxWidth;
        command.TextTrimming = TextTrimming.CharacterEllipsis;
        List<TextBlock> parts = (route is null) ? [name, command] : [name, route, command];
        string whole = row.ToString();
        foreach (TextBlock part in parts)
        {
            part.SizeChanged += (_, _) => ToolTip.SetTip(grid, parts.Any(NeedsTrimming) ? whole : null);
        }
    }

    /// <summary>
    /// The name with its detail under it; the detail is handed back for its row's tooltip, as is the name's own text
    /// (<paramref name="nameView"/>).
    /// </summary>
    private static Control NameView(MenuCommandRow row, out TextBlock nameView, out TextBlock? detailView)
    {
        detailView = null;
        TextBlock name = NameText(row);
        nameView = name;
        if (row.Detail is not { } detail)
        {
            return NamePanel(name, null);
        }

        detailView = DetailView(detail, row.DetailPlacement);
        BoldWhenHighlighted(detailView, row);
        return NamePanel(name, detailView);
    }

    private static TextBlock NameText(MenuCommandRow row)
    {
        var name = new TextBlock { Text = row.Name };
        if (row.DetailPlacement == MenuDetailPlacement.Route)
        {
            name.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        if (row.EmphasizeName)
        {
            name.FontWeight = FontWeight.SemiBold;
        }

        BoldWhenHighlighted(name, row);
        return name;
    }

    /// <summary>The name over <paramref name="detailView"/>, or alone when a row has no detail.</summary>
    private static StackPanel NamePanel(TextBlock name, TextBlock? detailView)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
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
            MenuDetailPlacement.Stacked => new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Opacity = 0.8,
            },
            MenuDetailPlacement.Route => RouteView(detail),
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown detail placement."),
        };

    /// <summary>A route's taxiways under its name: monospace at 11.5 pt, dimmed, ellipsis-trimmed past the row's width.</summary>
    private static TextBlock RouteView(string route)
    {
        var view = new TextBlock
        {
            Text = route,
            FontSize = 11.5,
            Opacity = 0.8,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        view.Bind(TextBlock.FontFamilyProperty, view.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        return view;
    }

    private static TextBlock CommandView(string commandText)
    {
        var command = new TextBlock
        {
            Text = commandText,
            FontSize = MenuCommandRow.CommandFontSize,
            Opacity = MenuCommandRow.CommandOpacity,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        return command;
    }
}

/// <summary>
/// The header of a disabled menu row that says why it offers nothing, drawn by <see cref="MenuDisabledReasonRowTemplate"/>:
/// the label, then the reason faint beside it. Its text form, which the golden menus and the row's automation name use,
/// reads <c>Follow… · no other aircraft on the ground</c>.
/// </summary>
/// <param name="Label">The entry's label, <c>Follow…</c>.</param>
/// <param name="Reason">Why the entry offers nothing, <c>no other aircraft on the ground</c>.</param>
public sealed record MenuDisabledReasonRow(string Label, string Reason)
{
    /// <inheritdoc />
    public override string ToString() => $"{Label} · {Reason}";

    /// <summary>
    /// Disables <paramref name="item"/> and heads it with <paramref name="label"/> and, faint beside it,
    /// <paramref name="reason"/>.
    /// </summary>
    /// <param name="item">The entry that offers nothing now.</param>
    /// <param name="label">The entry's label.</param>
    /// <param name="reason">Why it offers nothing.</param>
    public static void Apply(MenuItem item, string label, string reason)
    {
        var header = new MenuDisabledReasonRow(label, reason);
        item.IsEnabled = false;
        item.Header = header;
        item.HeaderTemplate = MenuDisabledReasonRowTemplate.Instance;
        AutomationProperties.SetName(item, header.ToString());
    }
}

/// <summary>The view of a <see cref="MenuDisabledReasonRow"/>: the label, then <c> · reason</c> at 12 px and fainter.</summary>
public sealed class MenuDisabledReasonRowTemplate : FuncDataTemplate<MenuDisabledReasonRow>
{
    /// <summary>The one template every disabled-with-reason row uses.</summary>
    public static MenuDisabledReasonRowTemplate Instance { get; } = new();

    private MenuDisabledReasonRowTemplate()
        : base((row, _) => Build(row)) { }

    private static StackPanel Build(MenuDisabledReasonRow row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(
            new TextBlock
            {
                Text = $" · {row.Reason}",
                FontSize = 12,
                Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        return panel;
    }
}
