using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.ContextMenus;

/// <summary>One row of a STAR's tab: its name, a note after it, and the command a click sends.</summary>
/// <param name="Name">What the row names: <c>Join</c>, <c>Join at RUSME</c>, <c>For runway 28R</c>.</param>
/// <param name="Note">The note after the name (<c>nearest fix ahead</c>), or null.</param>
/// <param name="Command">What the row sends: <c>JARR OAKES3 28R</c>.</param>
public sealed record JoinStarRow(string Name, string? Note, string Command)
{
    /// <summary>The row as one line: <c>Join · filed · nearest fix ahead — JARR OAKES3</c>.</summary>
    public override string ToString() => (Note is null) ? $"{Name} — {Command}" : $"{Name} · {Note} — {Command}";
}

/// <summary>One STAR's tab in the Join STAR flyout: the STAR, whether the flight plan files it, and its rows.</summary>
/// <param name="StarId">The STAR's procedure id: <c>OAKES3</c>.</param>
/// <param name="IsFiled">Whether the flight plan files it, marked ◆ on the tab.</param>
/// <param name="Rows">Join, then Join at each entry fix, then For runway each runway the STAR's runway transitions serve.</param>
public sealed record JoinStarTab(string StarId, bool IsFiled, IReadOnlyList<JoinStarRow> Rows)
{
    /// <summary>The tab's text: the STAR, with ◆ after a filed one.</summary>
    public string Label => IsFiled ? $"{StarId} {JoinStarFlyout.FiledMark}" : StarId;
}

/// <summary>
/// The Join STAR flyout (<c>JARR</c>): one tab per STAR at the destination, the filed STAR first marked ◆, else every STAR in
/// alphabetical order, the first tab selected. A tab's rows: Join (<c>JARR {star}</c>, the nearest fix ahead), Join at each
/// entry transition's fix (<c>JARR {star} {fix}</c>), For runway each runway its runway transitions serve
/// (<c>JARR {star} {rwy}</c>; a both-runway transition such as RW28B gives one row per runway it serves). Descend via STAR
/// (<c>DVIA</c>) sits under the tabs. A click sends and closes the flyout and the menu.
/// </summary>
public static class JoinStarFlyout
{
    /// <summary>The mark after a filed STAR's tab and in its Join row's note.</summary>
    public const string FiledMark = "◆";

    /// <summary>The Join row's note: the sim joins at the nearest fix ahead of the aircraft.</summary>
    public const string NearestFixNote = "nearest fix ahead";

    /// <summary>The Descend via STAR row's label.</summary>
    public const string DescendViaLabel = "Descend via STAR";

    /// <summary>The Descend via STAR row's command.</summary>
    public const string DescendViaCommand = "DVIA";

    /// <summary>The Join STAR entry labelled <paramref name="label"/>: one menu row holding the flyout over <paramref name="tabs"/>.</summary>
    public static MenuItem Build(string label, IReadOnlyList<JoinStarTab> tabs, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(entry, new JoinStarFlyoutHost(), context, host, (choose, _) => new JoinStarFlyoutPanel(tabs, choose));
        return entry;
    }

    /// <summary>
    /// The tabs for <paramref name="destination"/>'s STARs, alphabetical ignoring case, <paramref name="filedStar"/> moved first
    /// and marked filed; none when the navigation data publishes no STAR there.
    /// </summary>
    public static IReadOnlyList<JoinStarTab> Tabs(string destination, string? filedStar)
    {
        IReadOnlyList<string> runways = RunwayDesignators.ForAirport(destination);
        List<CifpStarProcedure> stars =
        [
            .. NavigationDatabase.Instance.GetStars(destination).OrderBy(star => star.ProcedureId, StringComparer.OrdinalIgnoreCase),
        ];
        int filed = stars.FindIndex(star => string.Equals(star.ProcedureId, filedStar, StringComparison.OrdinalIgnoreCase));
        if (filed > 0)
        {
            CifpStarProcedure first = stars[filed];
            stars.RemoveAt(filed);
            stars.Insert(0, first);
        }

        return [.. stars.Select((star, index) => Tab(star, (filed >= 0) && (index == 0), runways))];
    }

    private static JoinStarTab Tab(CifpStarProcedure star, bool isFiled, IReadOnlyList<string> airportRunways)
    {
        string id = star.ProcedureId;
        List<JoinStarRow> rows = [new("Join", isFiled ? $"filed · {NearestFixNote}" : NearestFixNote, $"JARR {id}")];
        rows.AddRange(
            star.EnrouteTransitions.Keys.Order(StringComparer.OrdinalIgnoreCase)
                .Select(fix => new JoinStarRow($"Join at {fix}", null, $"JARR {id} {fix}"))
        );
        rows.AddRange(ServedRunways(star, airportRunways).Select(runway => new JoinStarRow($"For runway {runway}", null, $"JARR {id} {runway}")));
        return new JoinStarTab(id, isFiled, rows);
    }

    /// <summary>
    /// The runways of <paramref name="airportRunways"/> the STAR's runway transitions serve, in display form and runway order:
    /// each whose own transition (<c>RW28R</c>) or both-runway transition (<c>RW28B</c>) the STAR publishes, as the sim looks
    /// a runway transition up.
    /// </summary>
    internal static IReadOnlyList<string> ServedRunways(CifpStarProcedure star, IReadOnlyList<string> airportRunways) =>
        [
            .. airportRunways
                .Select(RunwayIdentifier.NormalizeDesignator)
                .Where(runway => Serves(star, runway))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(RunwayDesignatorComparer.Instance)
                .Select(RunwayIdentifier.ToDisplayDesignator),
        ];

    private static bool Serves(CifpStarProcedure star, string designator) =>
        star.RunwayTransitions.ContainsKey($"RW{designator}") || star.RunwayTransitions.ContainsKey($"RW{designator.TrimEnd('L', 'R', 'C')}B");
}

/// <summary>The Join STAR flyout's menu row header, which holds the <see cref="JoinStarFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class JoinStarFlyoutHost() : FlyoutPanelHost("join STAR flyout", "join-star-flyout-panel");

/// <summary>
/// The Join STAR flyout's panel (<see cref="JoinStarFlyout"/>): the title line, the STAR tabs (<see cref="FlyoutTabStrip"/>)
/// over the selected tab's rows in a scroller, a separator, then Descend via STAR. Focus lands on the selected tab's first row;
/// Up/Down and Tab/Shift+Tab walk the rows and Descend via STAR, stopping at either end; Left/Right choose the previous or
/// next tab and focus its first row.
/// </summary>
public sealed class JoinStarFlyoutPanel : StackPanel, IFlyoutPanel
{
    /// <summary>The flyout's title.</summary>
    public const string Title = "Join STAR";

    private readonly IReadOnlyList<JoinStarTab> _tabs;
    private readonly Func<string, Task> _choose;
    private readonly StackPanel _rows = new();
    private List<Button> _tabRows = [];

    internal JoinStarFlyoutPanel(IReadOnlyList<JoinStarTab> tabs, Func<string, Task> choose)
    {
        _tabs = tabs;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Children.Add(
            FlyoutPanelParts.TitleLine(
                new TextBlock
                {
                    Text = Title,
                    FontSize = 11,
                    Foreground = FlyoutPanelParts.TitleBrush,
                },
                new TextBlock()
            )
        );
        Strip.SetTabs([.. tabs.Select(tab => new FlyoutTab(tab.Label, FlyoutTabState.Ready))]);
        Strip.SelectionChanged += (_, _) =>
        {
            ShowTab();
            Dispatcher.UIThread.Post(() => Focus(_tabRows.FirstOrDefault()), DispatcherPriority.Loaded);
        };
        Children.Add(Strip);
        Scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = TaxiToRunwayFlyout.RowsMaxHeight,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Focusable = true,
        };
        AutomationProperties.SetName(Scroll, "STAR rows");
        Children.Add(Scroll);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        QuickCommandGlyph descendGlyph =
            QuickCommandGlyphs.For(MenuIds.ProceduresDescendViaStar) ?? throw new InvalidOperationException("No Descend via STAR glyph.");
        DescendViaRow = RowButton(new JoinStarRow(JoinStarFlyout.DescendViaLabel, null, JoinStarFlyout.DescendViaCommand), descendGlyph);
        Children.Add(DescendViaRow);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ShowTab();
    }

    /// <summary>The STAR tabs.</summary>
    public FlyoutTabStrip Strip { get; } = new();

    /// <summary>The scrolling host of the selected tab's rows.</summary>
    public ScrollViewer Scroll { get; }

    /// <summary>The selected tab's rows, in order.</summary>
    public IReadOnlyList<Button> Rows => _tabRows;

    /// <summary>The Descend via STAR row under the tabs.</summary>
    public Button DescendViaRow { get; }

    /// <inheritdoc />
    public Control View => this;

    /// <summary>
    /// The panel as text, one line per part: the title, the tabs (<c>[OAKES3 ◆] | AANET1</c>, the selected one bracketed), each
    /// row of the selected tab, <c>---</c>, then Descend via STAR.
    /// </summary>
    public IReadOnlyList<string> Lines =>
        [
            Title,
            string.Join(" | ", _tabs.Select((tab, index) => (index == Strip.SelectedIndex) ? $"[{tab.Label}]" : tab.Label)),
            .. _tabRows.Select(RowOf),
            "---",
            RowOf(DescendViaRow),
        ];

    /// <summary>The row a row button stands for, as one line.</summary>
    private static string RowOf(Button button) => (button.Tag as JoinStarRow)?.ToString() ?? "";

    /// <inheritdoc />
    public void Reset()
    {
        if (_tabs.Count > 0)
        {
            Strip.Select(0);
        }
    }

    /// <inheritdoc />
    public void Refresh() { }

    /// <inheritdoc />
    public void FocusOpeningLater(Func<bool> isOpen) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (isOpen())
                {
                    Focus(_tabRows.FirstOrDefault());
                }
            },
            DispatcherPriority.Loaded
        );

    private void ShowTab()
    {
        _tabRows = (Strip.SelectedIndex < 0) ? [] : [.. _tabs[Strip.SelectedIndex].Rows.Select(row => RowButton(row, null))];
        _rows.Children.Clear();
        _rows.Children.AddRange(_tabRows);
        Scroll.Offset = default;
    }

    /// <summary>A row's button: <paramref name="glyph"/> (a blank of its width without one), the name, the note and the command.</summary>
    private Button RowButton(JoinStarRow row, QuickCommandGlyph? glyph)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Control icon = (glyph is null) ? new Border { Width = 22, Height = 22 } : QuickCommandStrip.GlyphIcon(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(icon);
        var name = new TextBlock { Text = (row.Note is null) ? row.Name : $"{row.Name} · {row.Note}", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        TextBlock command = FlyoutPanelParts.CommandText(row.Command);
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        var button = new Button
        {
            Content = grid,
            Tag = row,
            Padding = new Thickness(11, 5),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        MenuCommandText.SetCommand(button, row.Command);
        AutomationProperties.SetName(button, row.ToString());
        button.Click += async (_, e) =>
        {
            e.Handled = true;
            await _choose(row.Command);
        };
        return button;
    }

    private void Focus(Control? control)
    {
        if (control is null)
        {
            Scroll.Focus(NavigationMethod.Directional);
            return;
        }

        control.Focus(NavigationMethod.Directional);
        control.BringIntoView();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.Right when _tabs.Count > 0:
                e.Handled = true;
                Strip.Move((e.Key == Key.Left) ? -1 : 1);
                break;
            case Key.Up or Key.Down or Key.Tab:
                e.Handled = true;
                Step(((e.Key == Key.Down) || ((e.Key == Key.Tab) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))) ? 1 : -1);
                break;
        }
    }

    /// <summary>Focuses the row after (<paramref name="step"/> 1) or before (-1) the focused one, staying put at either end.</summary>
    private void Step(int step)
    {
        List<Button> order = [.. _tabRows, DescendViaRow];
        int at = order.FindIndex(button => button.IsKeyboardFocusWithin);
        int next = (at < 0) ? 0 : at + step;
        if ((next >= 0) && (next < order.Count))
        {
            Focus(order[next]);
        }
    }
}
