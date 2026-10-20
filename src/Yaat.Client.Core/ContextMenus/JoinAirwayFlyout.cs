using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Join airway flyout (<c>JAWY</c>): the Airway box on top, focused (it takes any airway id but a military training route;
/// typing filters the rows by prefix; Enter sends <c>JAWY V25</c> when the box names a known airway, else shows its refusal), the
/// caption of what Enter sends (<c>Join V25</c>), then the filed airways marked ◆, then the airways through the route's fixes
/// and the fixes within <see cref="NearbyRadiusNm"/> NM, nearest first (<see cref="NavigationDatabase.AirwaysNear"/>), the
/// first <see cref="MaxNearby"/>. Each row names the airway, the fix the aircraft would proceed to once it intercepts the airway
/// on its present heading (<see cref="AirwayBracketing.AheadFix"/>, as the sim flies it) with that fix's distance and magnetic
/// bearing, or <see cref="NoSegmentAhead"/>, and the command; a click sends it. Rows are read as the flyout opens and never
/// move; each beat redraws their fixes, distances and bearings.
/// </summary>
public static class JoinAirwayFlyout
{
    /// <summary>The Airway box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "JoinAirwayBox";

    /// <summary>What the empty Airway box reads.</summary>
    public const string BoxPlaceholder = "e.g. V23, J1, Q5";

    /// <summary>The refusal under the Airway box while its text names no airway the navigation data knows.</summary>
    public const string UnknownAirway = "unknown airway";

    /// <summary>
    /// The refusal under the Airway box for a military training route, which <c>JAWY</c> would fly without its direction or
    /// altitude blocks (7110.65 9-2-6): it is cleared with <c>CMTR</c>.
    /// </summary>
    public const string MilitaryRoute = "military route: use CMTR";

    /// <summary>A row's note when the sim would find no segment of the airway ahead of the aircraft to intercept.</summary>
    public const string NoSegmentAhead = "no segment ahead";

    /// <summary>How far from the aircraft a fix brings its airways into the list.</summary>
    public const double NearbyRadiusNm = 30;

    /// <summary>The most nearby airways the list shows after the filed ones.</summary>
    public const int MaxNearby = 12;

    /// <summary>The Join airway entry <paramref name="label"/> names: one menu row holding the Join airway flyout.</summary>
    public static MenuItem Build(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(entry, new JoinAirwayFlyoutHost(), context, host, (choose, _) => new JoinAirwayFlyoutPanel(aircraft, choose));
        return entry;
    }

    /// <summary>The <c>JAWY</c> command: <c>JAWY V25</c>.</summary>
    public static string Command(string airway) => $"JAWY {airway}";

    /// <summary>The instruction as a controller says it (7110.65 4-4-1.a): <c>Join V25</c>.</summary>
    public static string Caption(string airway) => $"Join {airway}";

    /// <summary>
    /// The airway the box's <paramref name="text"/> names: one word, upper-cased, that the navigation data knows as an airway
    /// and not a military training route (any word while it is not loaded); null for anything else (<see cref="Refusal"/>).
    /// </summary>
    public static string? TypedAirway(string text) => ((Refusal(text) is null) && (Word(text) is { } airway)) ? airway : null;

    /// <summary>
    /// The refusal the box shows for <paramref name="text"/>: none for a blank box or a known airway, <see cref="MilitaryRoute"/>
    /// for a military training route, else <see cref="UnknownAirway"/>.
    /// </summary>
    public static string? Refusal(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || (NavigationDatabase.InstanceOrNull is not { } db))
        {
            return null;
        }

        if (Word(text) is not { } airway)
        {
            return UnknownAirway;
        }

        if (db.IsMilitaryRoute(airway))
        {
            return MilitaryRoute;
        }

        return db.IsAirway(airway) ? null : UnknownAirway;
    }

    /// <summary>
    /// The airways through the aircraft's route fixes and the fixes within <see cref="NearbyRadiusNm"/> NM, nearest first,
    /// leaving out <paramref name="excluded"/> (ignoring case), the first <see cref="MaxNearby"/>; none without the navigation data.
    /// </summary>
    public static IReadOnlyList<NearbyAirway> Nearby(IMenuAircraft aircraft, IReadOnlyCollection<string> excluded)
    {
        if (NavigationDatabase.InstanceOrNull is not { } db)
        {
            return [];
        }

        var skip = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<NearbyAirway> near = db.AirwaysNear(aircraft.RouteFixNames(), aircraft.Position, NearbyRadiusNm);
        return [.. near.Where(a => !skip.Contains(a.Id)).Take(MaxNearby)];
    }

    /// <summary>
    /// The fix of <paramref name="airway"/> the aircraft proceeds to once a <c>JAWY</c> intercepts it on its present true heading
    /// (<see cref="AirwayBracketing.AheadFix"/>); null without the navigation data or where the sim finds no segment ahead.
    /// </summary>
    public static string? AheadFix(string airway, IMenuAircraft aircraft) =>
        (NavigationDatabase.InstanceOrNull is { } db)
            ? AirwayBracketing.AheadFix(db, airway, aircraft.Position, new TrueHeading(aircraft.HeadingDegrees))
            : null;

    /// <summary>The box's one word, trimmed and upper-cased; null for a blank box or several words.</summary>
    private static string? Word(string text)
    {
        string word = text.Trim().ToUpperInvariant();
        return ((word.Length == 0) || word.Any(char.IsWhiteSpace)) ? null : word;
    }
}

/// <summary>The Join airway flyout's menu row header, which holds the <see cref="JoinAirwayFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class JoinAirwayFlyoutHost() : FlyoutPanelHost("join airway flyout", "join-airway-flyout-panel");

/// <summary>
/// One airway row of the Join airway flyout: ◆ on a filed airway, the airway, the fix the aircraft would proceed to after the
/// intercept with its distance and magnetic bearing (or <see cref="JoinAirwayFlyout.NoSegmentAhead"/>), and the command a click
/// sends.
/// </summary>
public sealed class JoinAirwayRow : Button
{
    private readonly TextBlock _note = new()
    {
        FontSize = 12,
        Opacity = 0.8,
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    internal JoinAirwayRow(string airway, bool isFiled, Func<JoinAirwayRow, Task> chosen)
    {
        Airway = airway;
        IsFiled = isFiled;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(10, 3) };
        grid.Children.Add(
            new TextBlock
            {
                Text = isFiled ? "◆" : "",
                Width = 14,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var name = new TextBlock { Text = airway, VerticalAlignment = VerticalAlignment.Center };
        FlyoutPanelParts.Mono(name);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(_note, 2);
        grid.Children.Add(_note);
        TextBlock command = FlyoutPanelParts.CommandText(SentCommand);
        Grid.SetColumn(command, 3);
        grid.Children.Add(command);
        Content = grid;
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        MenuCommandText.SetCommand(this, SentCommand);
        AutomationProperties.SetName(this, $"{airway} — {SentCommand}");
        Click += async (_, e) =>
        {
            e.Handled = true;
            await chosen(this);
        };
    }

    /// <summary>The airway the row names.</summary>
    public string Airway { get; }

    /// <summary>The fix the aircraft would proceed to after intercepting the airway now; null when the sim finds no segment ahead.</summary>
    public string? AheadFix { get; private set; }

    /// <summary>Whether the flight plan files the airway, marked ◆.</summary>
    public bool IsFiled { get; }

    /// <summary>The command a click sends: <c>JAWY V25</c>.</summary>
    public string SentCommand => JoinAirwayFlyout.Command(Airway);

    /// <summary>
    /// The ahead fix with its distance and magnetic bearing (<c>SUNOL · 12 NM · 184°</c>) or <see cref="JoinAirwayFlyout.NoSegmentAhead"/>,
    /// <c>filed · </c> first on a filed airway.
    /// </summary>
    public string Note => _note.Text ?? "";

    /// <summary>The row as one line: the mark, the airway, the note and the command.</summary>
    public string Line => $"{(IsFiled ? "◆" : " ")} {Airway}{((Note.Length > 0) ? $" · {Note}" : "")} — {SentCommand}";

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Redraws the ahead fix and its note for <paramref name="aircraft"/> where it is now; without one, the note is empty.</summary>
    internal void ShowFrom(IMenuAircraft? aircraft)
    {
        AheadFix = (aircraft is null) ? null : JoinAirwayFlyout.AheadFix(Airway, aircraft);
        string where = (aircraft is null) ? "" : JoinAirwayFlyout.NoSegmentAhead;
        if ((AheadFix is { } fix) && (aircraft is not null))
        {
            where = (FixList.DistanceAndBearing(aircraft.Position, fix) is { } distance) ? $"{fix} · {distance}" : fix;
        }

        _note.Text = IsFiled ? ((where.Length > 0) ? $"filed · {where}" : "filed") : where;
    }
}

/// <summary>
/// The Join airway flyout's panel (<see cref="JoinAirwayFlyout"/>): the title line, the Airway box with its command and refusal,
/// the caption, a separator, then the filed and nearby airway rows; the keyboard walks them (Down from the box onto the first
/// shown row, Down and Up through the rows, Up from the top row back to the box, Enter on a row sends it).
/// </summary>
public sealed class JoinAirwayFlyoutPanel : StackPanel, IFlyoutPanel
{
    private const double ListMaxHeight = 300;

    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly StackPanel _list = new();
    private readonly TextBlock _filedHeader = GroupHeader("Filed");
    private readonly TextBlock _nearbyHeader = GroupHeader(
        $"Through the route and fixes within {JoinAirwayFlyout.NearbyRadiusNm:0} NM · nearest first"
    );
    private readonly List<JoinAirwayRow> _rows = [];

    internal JoinAirwayFlyoutPanel(IMenuAircraft? aircraft, Func<string, Task> choose)
    {
        _aircraft = aircraft;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Children.Add(
            FlyoutPanelParts.TitleLine(
                new TextBlock
                {
                    Text = "Join airway",
                    FontSize = 11,
                    Foreground = FlyoutPanelParts.TitleBrush,
                },
                new TextBlock()
            )
        );
        Box = new TextBox
        {
            Name = JoinAirwayFlyout.BoxName,
            PlaceholderText = JoinAirwayFlyout.BoxPlaceholder,
            Width = 150,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(Box, "Airway");
        TakeoffFlyout.KeepClicksInside(Box);
        QuickCommandGlyph glyph =
            QuickCommandGlyphs.For(MenuIds.ProceduresJoinAirway) ?? throw new InvalidOperationException("No Join airway glyph.");
        Children.Add(FlyoutPanelParts.BoxRow(glyph, "Airway", Box, BoxCommandText));
        Children.Add(BoxError);
        Children.Add(Caption);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Children.Add(new ScrollViewer { MaxHeight = ListMaxHeight, Content = _list });
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTyped();
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Reset();
        Refresh();
    }

    /// <summary>The Airway box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter sends, or <c>JAWY ···</c>, faded, while it cannot send.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>
    /// The refusal under the box (<see cref="JoinAirwayFlyout.UnknownAirway"/> or <see cref="JoinAirwayFlyout.MilitaryRoute"/>),
    /// shown while its text names no airway the flyout sends.
    /// </summary>
    public TextBlock BoxError { get; } =
        new()
        {
            Text = JoinAirwayFlyout.UnknownAirway,
            FontSize = 11,
            Foreground = FlyoutPanelParts.RefusalBrush,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    /// <summary>The caption under the box: what Enter sends, as a controller says it; hidden while it cannot send.</summary>
    public TextBlock Caption { get; } =
        new()
        {
            FontSize = 12,
            Margin = new Thickness(11, 2, 11, 2),
            IsVisible = false,
        };

    /// <summary>The command Enter in the box sends now: <c>JAWY</c> with the airway it names; null while it names none.</summary>
    public string? BoxCommand => (JoinAirwayFlyout.TypedAirway(Box.Text ?? "") is { } airway) ? JoinAirwayFlyout.Command(airway) : null;

    /// <summary>Every row as the flyout opened: the filed airways, then the nearby ones, nearest first; they never move.</summary>
    public IReadOnlyList<JoinAirwayRow> Rows => _rows;

    /// <summary>
    /// The panel as text, one line per part: the title, the box with its command, the refusal and the caption while shown,
    /// <c>---</c>, then each shown group header and row.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            List<string> lines = ["Join airway", $"Airway — {BoxCommandText.Text}"];
            if (BoxError.IsVisible)
            {
                lines.Add(BoxError.Text ?? "");
            }

            if (Caption.IsVisible)
            {
                lines.Add(Caption.Text ?? "");
            }

            lines.Add("---");
            lines.AddRange(_list.Children.Where(part => part.IsVisible).Select(PartLine));
            return lines;
        }
    }

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Reset();

    void IFlyoutPanel.Refresh() => Refresh();

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    /// <summary>Puts the flyout back as it opens: the box empty, the filed and nearby airways read afresh.</summary>
    public void Reset()
    {
        _rows.Clear();
        IReadOnlyList<string> filed = MenuCatalog.FiledAirways(_aircraft);
        _rows.AddRange(filed.Select(airway => new JoinAirwayRow(airway, true, SendRowAsync)));
        if (_aircraft is not null)
        {
            _rows.AddRange(JoinAirwayFlyout.Nearby(_aircraft, filed).Select(nearby => new JoinAirwayRow(nearby.Id, false, SendRowAsync)));
        }

        _list.Children.Clear();
        _list.Children.Add(_filedHeader);
        _list.Children.AddRange(_rows.Where(row => row.IsFiled));
        _list.Children.Add(_nearbyHeader);
        _list.Children.AddRange(_rows.Where(row => !row.IsFiled));
        Box.Text = "";
        OnTyped();
    }

    /// <summary>Follows the aircraft, in place: each row's ahead fix and its distance and bearing.</summary>
    public void Refresh()
    {
        foreach (JoinAirwayRow row in _rows)
        {
            row.ShowFrom(_aircraft);
        }
    }

    private static TextBlock GroupHeader(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(11, 4, 11, 2),
        };

    private static string PartLine(Control part) => (part is JoinAirwayRow row) ? row.Line : ((part as TextBlock)?.Text ?? "");

    private Task SendRowAsync(JoinAirwayRow row) => _choose(row.SentCommand);

    private List<JoinAirwayRow> ShownRows() => [.. _rows.Where(row => row.IsVisible)];

    /// <summary>Filters the rows by the box's prefix, then shows the box's command, refusal and caption.</summary>
    private void OnTyped()
    {
        FilterRows((Box.Text ?? "").Trim());
        ShowBox();
    }

    /// <summary>Shows only the rows whose airway starts with <paramref name="prefix"/>, and each group header while it has one.</summary>
    private void FilterRows(string prefix)
    {
        foreach (JoinAirwayRow row in _rows)
        {
            row.IsVisible = row.Airway.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        _filedHeader.IsVisible = _rows.Any(row => row.IsFiled && row.IsVisible);
        _nearbyHeader.IsVisible = _rows.Any(row => !row.IsFiled && row.IsVisible);
    }

    /// <summary>Shows the box's command column, its refusal and the caption for what it holds now.</summary>
    private void ShowBox()
    {
        string text = Box.Text ?? "";
        string? airway = JoinAirwayFlyout.TypedAirway(text);
        string? command = BoxCommand;
        BoxCommandText.Text = command ?? $"JAWY {TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        string? refusal = JoinAirwayFlyout.Refusal(text);
        BoxError.Text = refusal ?? JoinAirwayFlyout.UnknownAirway;
        BoxError.IsVisible = refusal is not null;
        MenuCommandText.SetCommand(Box, command);
        Caption.Text = (airway is null) ? "" : JoinAirwayFlyout.Caption(airway);
        Caption.IsVisible = airway is not null;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        List<JoinAirwayRow> rows = ShownRows();
        int focused = rows.FindIndex(row => row.IsKeyboardFocusWithin);
        bool inBox = ReferenceEquals(e.Source, Box) || Box.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Enter when inBox:
                e.Handled = true;
                if (BoxCommand is { } command)
                {
                    await _choose(command);
                }

                break;
            case Key.Enter when focused >= 0:
                e.Handled = true;
                await SendRowAsync(rows[focused]);
                break;
            case Key.Down or Key.Up:
                Walk(e, rows, inBox, focused);
                break;
        }
    }

    /// <summary>Down from the box focuses the first shown row; Down and Up walk the rows; Up from the top row returns to the box.</summary>
    private void Walk(KeyEventArgs e, List<JoinAirwayRow> rows, bool inBox, int focused)
    {
        int? next = (inBox, focused, e.Key) switch
        {
            (true, _, Key.Down) when rows.Count > 0 => 0,
            (false, 0, Key.Up) => -1,
            (false, >= 0, Key.Up) => focused - 1,
            (false, >= 0, Key.Down) => Math.Min(focused + 1, rows.Count - 1),
            _ => null,
        };
        if (next is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (target < 0)
        {
            Box.Focus(NavigationMethod.Directional);
            return;
        }

        rows[target].Focus(NavigationMethod.Directional);
        rows[target].BringIntoView();
    }
}
