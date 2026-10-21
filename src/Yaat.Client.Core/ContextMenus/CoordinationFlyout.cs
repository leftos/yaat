using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Client.Services;

namespace Yaat.Client.ContextMenus;

/// <summary>Which coordination command a <see cref="CoordinationFlyout"/> sends: a handoff (<c>HO</c>) or a point out (<c>PO</c>).</summary>
public enum CoordinationVerb
{
    Handoff,
    PointOut,
}

/// <summary>One position a coordination flyout lists.</summary>
/// <param name="Code">The code the row shows: the TCP, else the server's handoff code, else the callsign.</param>
/// <param name="Sent">The code the row sends: the handoff code for <c>HO</c>, the TCP for <c>PO</c>.</param>
/// <param name="Name">The position's name; a tower's radio name (<c>Oakland Tower</c>); empty when unknown.</param>
/// <param name="Frequency">The frequency, MHz (<c>127.200</c>); empty when unknown.</param>
/// <param name="Callsign">The position's callsign, which the box filters on but the row never shows; null when unknown.</param>
/// <param name="IsOwner">Whether the position owns the track: greyed, captioned <see cref="CoordinationFlyout.OwnsTheTrack"/>, never sent.</param>
public sealed record CoordinationPosition(string Code, string Sent, string Name, string Frequency, string? Callsign, bool IsOwner);

/// <summary>One group of a coordination flyout's list: its <paramref name="Header"/> (none for the student's top row) and its positions.</summary>
public sealed record CoordinationGroup(string? Header, IReadOnlyList<CoordinationPosition> Positions);

/// <summary>
/// The Initiate handoff… (<c>HO</c>) and Point out… (<c>PO</c>) flyouts: the title line, the box (focused; typing filters the rows by
/// the prefix of their code, name, a word of the name, callsign or frequency, and outlines the first match; Enter sends the
/// outlined row, else the typed text upper-cased, as typed), then the staffed positions read once as the flyout opens, grouped
/// by facility: the student's own facility first, then the towers, then the Center, then the rest, each group by code. The
/// student's own position is the top row unless it owns the track. The track owner's row stays in place, greyed, captioned
/// <see cref="OwnsTheTrack"/>, and cannot be chosen. Point out lists only positions with a STARS TCP.
/// </summary>
public static class CoordinationFlyout
{
    /// <summary>The box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "CoordinationBox";

    /// <summary>What the empty box reads.</summary>
    public const string BoxPlaceholder = "TCP, sector, callsign or frequency";

    /// <summary>The track owner's caption in place of its command.</summary>
    public const string OwnsTheTrack = "owns the track";

    /// <summary>The note under the box while its text matches no listed position.</summary>
    public const string NotStaffed = "Not a staffed position: sent as typed";

    /// <summary>The header of the towers' group.</summary>
    public const string TowersGroup = "Towers";

    private const string AtctType = "Atct";
    private const string ArtccType = "Artcc";

    /// <summary>The entry <paramref name="label"/> names: one menu row holding the <paramref name="verb"/>'s coordination flyout.</summary>
    public static MenuItem Build(string label, CoordinationVerb verb, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(
            entry,
            new CoordinationFlyoutHost(),
            context,
            host,
            (choose, _) => new CoordinationFlyoutPanel(verb, aircraft, host, choose)
        );
        return entry;
    }

    /// <summary>The verb's command word: <c>HO</c> or <c>PO</c>.</summary>
    public static string CommandWord(CoordinationVerb verb) => (verb == CoordinationVerb.Handoff) ? "HO" : "PO";

    /// <summary>The command: <c>HO 2B</c>, <c>PO 2B</c>.</summary>
    public static string Command(CoordinationVerb verb, string code) => $"{CommandWord(verb)} {code}";

    /// <summary>The flyout's title: <c>Initiate handoff</c> or <c>Point out</c>.</summary>
    public static string Title(CoordinationVerb verb) => (verb == CoordinationVerb.Handoff) ? "Initiate handoff" : "Point out";

    /// <summary>The box's label: <c>Hand off to</c> or <c>Point out to</c>.</summary>
    public static string BoxLabel(CoordinationVerb verb) => (verb == CoordinationVerb.Handoff) ? "Hand off to" : "Point out to";

    /// <summary>The line the list shows while <paramref name="typed"/> (upper-cased) matches no listed position.</summary>
    public static string NoMatch(CoordinationVerb verb, string typed) =>
        $"No staffed position matches {typed}. Enter sends {Command(verb, typed)} as typed; the sim answers after the flyout closes.";

    /// <summary>
    /// The groups the <paramref name="verb"/>'s flyout lists from <paramref name="positions"/> for <paramref name="aircraft"/>:
    /// the student's own position alone on top unless it owns the track, then the staffed positions
    /// (<see cref="OnlineControllerDto.IsActive"/>; for a point out only those with a TCP) grouped by facility, the student's own
    /// first, then the towers, then the Center, then the rest by facility id, each group's rows by code.
    /// </summary>
    public static IReadOnlyList<CoordinationGroup> Arrange(CoordinationVerb verb, MenuPositions positions, IMenuAircraft? aircraft)
    {
        OnlineControllerDto? studentController = StudentController(positions);
        List<Seat> listed =
        [
            .. positions
                .Controllers.Where(c =>
                    c.IsActive && ((verb == CoordinationVerb.Handoff) || !string.IsNullOrEmpty(c.Tcp)) && !ReferenceEquals(c, studentController)
                )
                .Select(Seat.Of),
        ];
        var groups = new List<CoordinationGroup>();
        Seat? student = StudentSeat(positions, studentController);
        if (student is not null)
        {
            // A student who owns the track keeps its row in its facility group; one whose facility is unknown stays on top.
            if (OwnsTrack(student, aircraft) && (student.FacilityId is not null))
            {
                listed.Add(student);
            }
            else
            {
                groups.Add(new CoordinationGroup(null, [FromSeat(verb, student, aircraft)]));
            }
        }

        string? studentFacility = student?.FacilityId;
        groups.AddRange(
            listed
                .GroupBy(seat => GroupOf(seat, studentFacility))
                .OrderBy(g => g.Key.Rank)
                .ThenBy(g => g.Key.Facility, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CoordinationGroup(
                    g.Key.Header,
                    [.. g.Select(seat => FromSeat(verb, seat, aircraft)).OrderBy(p => p.Code, StringComparer.OrdinalIgnoreCase)]
                ))
        );
        return groups;
    }

    /// <summary>
    /// Whether <paramref name="position"/> matches the box's <paramref name="text"/>: blank matches every row, else the prefix of
    /// its code, name, a word of its name, callsign or frequency, ignoring case.
    /// </summary>
    public static bool Matches(CoordinationPosition position, string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        bool Starts(string? value) => value?.StartsWith(text, StringComparison.OrdinalIgnoreCase) ?? false;
        return Starts(position.Code)
            || Starts(position.Name)
            || Starts(position.Callsign)
            || Starts(position.Frequency)
            || position.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(Starts);
    }

    /// <summary>
    /// The listed controller that is the student's position: the one whose handoff code is the active position's (both name
    /// exactly one position), else the one whose handoff code is the student's TCP (the sim resolves the student's TCP to the
    /// student first), else one at the student's TCP, a staffed one first; null when none is listed.
    /// </summary>
    private static OnlineControllerDto? StudentController(MenuPositions positions)
    {
        if (positions.ActiveTcp is not { } tcp)
        {
            return null;
        }

        IReadOnlyList<OnlineControllerDto> controllers = positions.Controllers;
        string? exact = positions.ActivePosition?.HandoffCode;
        return controllers.FirstOrDefault(c => (exact is not null) && SameCode(c.HandoffCode, exact))
            ?? controllers.FirstOrDefault(c => SameCode(c.HandoffCode, tcp))
            ?? controllers.Where(c => SameCode(c.Tcp, tcp)).OrderByDescending(c => c.IsActive).FirstOrDefault();
    }

    /// <summary>
    /// The student's own position: the listed controller, else the active position the server describes at the student's TCP,
    /// else that TCP alone; null before a scenario loads.
    /// </summary>
    private static Seat? StudentSeat(MenuPositions positions, OnlineControllerDto? studentController)
    {
        if (positions.ActiveTcp is not { } tcp)
        {
            return null;
        }

        if (studentController is not null)
        {
            return Seat.Of(studentController);
        }

        return (positions.ActivePosition is { } p)
            ? new Seat(p.Callsign, p.Name, p.RadioName, p.Frequency, p.FacilityId, p.FacilityName, p.PositionType, tcp, p.HandoffCode)
            : new Seat(null, "", null, "", null, null, "", tcp, tcp);
    }

    private static CoordinationPosition FromSeat(CoordinationVerb verb, Seat seat, IMenuAircraft? aircraft)
    {
        string code = seat.Tcp ?? seat.HandoffCode ?? seat.Callsign ?? "";
        string sent = (verb == CoordinationVerb.Handoff) ? (seat.HandoffCode ?? code) : code;
        string name = (seat.PositionType == AtctType) ? (seat.RadioName ?? seat.Name) : seat.Name;
        return new CoordinationPosition(code, sent, name, seat.Frequency, seat.Callsign, OwnsTrack(seat, aircraft));
    }

    /// <summary>
    /// Whether <paramref name="seat"/> owns <paramref name="aircraft"/>'s track. A seat without a TCP (an ERAM sector, a tower
    /// without one) is decided by the owner's callsign where both are known; any other must match the owner's callsign and have
    /// the owner's sector code as its TCP or its handoff code, each where the track names it.
    /// </summary>
    private static bool OwnsTrack(Seat seat, IMenuAircraft? aircraft)
    {
        if ((aircraft is not { } ac) || !AircraftCommandApplicability.IsOwned(ac))
        {
            return false;
        }

        bool callsignKnown = !string.IsNullOrEmpty(ac.Owner) && (seat.Callsign is not null);
        bool callsignMatches = !callsignKnown || string.Equals(ac.Owner, seat.Callsign, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(seat.Tcp) && callsignKnown)
        {
            return callsignMatches;
        }

        string? ownerCode = ac.OwnerSectorCode;
        bool codeMatches = string.IsNullOrEmpty(ownerCode) || SameCode(seat.Tcp, ownerCode) || SameCode(seat.HandoffCode, ownerCode);
        return callsignMatches && codeMatches;
    }

    private static bool SameCode(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static (int Rank, string Facility, string Header) GroupOf(Seat c, string? studentFacility)
    {
        string facility = c.FacilityId ?? "";
        string header = string.IsNullOrEmpty(c.FacilityName) ? facility : $"{c.FacilityName} · {facility}";
        if ((studentFacility is not null) && string.Equals(facility, studentFacility, StringComparison.OrdinalIgnoreCase))
        {
            return (0, facility, header);
        }

        return c.PositionType switch
        {
            AtctType => (1, "", TowersGroup),
            ArtccType => (2, facility, header),
            _ => (3, facility, header),
        };
    }

    /// <summary>
    /// A position the flyout may list, from the controller list or from the active position the server describes; the callsign
    /// is null and the facility unknown for a student known only by its TCP.
    /// </summary>
    private sealed record Seat(
        string? Callsign,
        string Name,
        string? RadioName,
        string Frequency,
        string? FacilityId,
        string? FacilityName,
        string PositionType,
        string? Tcp,
        string? HandoffCode
    )
    {
        public static Seat Of(OnlineControllerDto c) =>
            new(c.Callsign, c.Name, c.RadioName, c.Frequency, c.FacilityId, c.FacilityName, c.PositionType, c.Tcp, c.HandoffCode);
    }
}

/// <summary>A coordination flyout's menu row header, which holds the <see cref="CoordinationFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class CoordinationFlyoutHost() : FlyoutPanelHost("coordination flyout", "coordination-flyout-panel");

/// <summary>
/// One position row of a coordination flyout: the code, the name and the frequency, and the command a click sends, or, on the
/// track owner's row, greyed and never sent, <see cref="CoordinationFlyout.OwnsTheTrack"/>.
/// </summary>
public sealed class CoordinationRow : Button
{
    internal CoordinationRow(CoordinationVerb verb, CoordinationPosition position, Func<CoordinationRow, Task> chosen)
    {
        Position = position;
        SentCommand = position.IsOwner ? null : CoordinationFlyout.Command(verb, position.Sent);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(10, 3) };
        var code = new TextBlock
        {
            Text = position.Code,
            MinWidth = 40,
            VerticalAlignment = VerticalAlignment.Center,
        };
        FlyoutPanelParts.Mono(code);
        grid.Children.Add(code);
        var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        label.Children.Add(new TextBlock { Text = position.Name, VerticalAlignment = VerticalAlignment.Center });
        var frequency = new TextBlock
        {
            Text = position.Frequency,
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        FlyoutPanelParts.Mono(frequency);
        label.Children.Add(frequency);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        TextBlock command = FlyoutPanelParts.CommandText(CommandColumn);
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        Content = grid;
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderBrush = FlyoutPanelParts.NowEdge;
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (position.IsOwner)
        {
            IsEnabled = false;
            Focusable = false;
            Opacity = 0.5;
        }

        MenuCommandText.SetCommand(this, SentCommand);
        AutomationProperties.SetName(this, $"{position.Code} {position.Name} — {CommandColumn}");
        Click += async (_, e) =>
        {
            e.Handled = true;
            if (SentCommand is not null)
            {
                await chosen(this);
            }
        };
    }

    /// <summary>The position the row lists.</summary>
    public CoordinationPosition Position { get; }

    /// <summary>The command a click sends: <c>HO 2B</c>; null on the track owner's row, which sends nothing.</summary>
    public string? SentCommand { get; }

    /// <summary>Whether the row is outlined as the box's first match, which Enter sends.</summary>
    public bool IsOutlined { get; private set; }

    /// <summary>The command column: the command, or <see cref="CoordinationFlyout.OwnsTheTrack"/> on the owner's row.</summary>
    public string CommandColumn => SentCommand ?? CoordinationFlyout.OwnsTheTrack;

    /// <summary>The row as one line: <c>▸</c> while outlined, the code, the name, the frequency and the command column.</summary>
    public string Line =>
        $"{(IsOutlined ? "▸" : " ")} {Position.Code}"
        + ((Position.Name.Length > 0) ? $" · {Position.Name}" : "")
        + ((Position.Frequency.Length > 0) ? $" · {Position.Frequency}" : "")
        + $" — {CommandColumn}";

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    internal void Outline(bool outlined)
    {
        IsOutlined = outlined;
        BorderThickness = new Thickness(outlined ? 1 : 0);
    }
}

/// <summary>
/// A coordination flyout's panel (<see cref="CoordinationFlyout"/>): the title line, the box with the command Enter sends and its
/// note, a separator, then the groups of position rows and the no-match line; the keyboard walks them (Down from the box onto
/// the first shown row, Down and Up through the rows, Up from the top row back to the box, Enter on a row sends it).
/// </summary>
public sealed class CoordinationFlyoutPanel : StackPanel, IFlyoutPanel
{
    private const double ListMaxHeight = 300;

    private readonly CoordinationVerb _verb;
    private readonly IMenuAircraft? _aircraft;
    private readonly IMenuHost _host;
    private readonly Func<string, Task> _choose;
    private readonly StackPanel _list = new();
    private readonly List<CoordinationRow> _rows = [];
    private readonly List<(TextBlock Header, List<CoordinationRow> Rows)> _headers = [];
    private readonly TextBlock _owner = new();

    internal CoordinationFlyoutPanel(CoordinationVerb verb, IMenuAircraft? aircraft, IMenuHost host, Func<string, Task> choose)
    {
        _verb = verb;
        _aircraft = aircraft;
        _host = host;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Children.Add(
            FlyoutPanelParts.TitleLine(
                new TextBlock
                {
                    Text = CoordinationFlyout.Title(verb),
                    FontSize = 11,
                    Foreground = FlyoutPanelParts.TitleBrush,
                },
                _owner
            )
        );
        Box = new TextBox
        {
            Name = CoordinationFlyout.BoxName,
            PlaceholderText = CoordinationFlyout.BoxPlaceholder,
            Width = 230,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(Box, CoordinationFlyout.BoxLabel(verb));
        TakeoffFlyout.KeepClicksInside(Box);
        string glyphId = (verb == CoordinationVerb.Handoff) ? MenuIds.TrackInitiateHandoff : MenuIds.TrackPointOut;
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(glyphId) ?? throw new InvalidOperationException($"No {glyphId} glyph.");
        Children.Add(FlyoutPanelParts.BoxRow(glyph, CoordinationFlyout.BoxLabel(verb), Box, BoxCommandText));
        Children.Add(BoxNote);
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
    }

    /// <summary>The box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter sends, or <c>HO ···</c>, faded, while the box is blank.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The note under the box, <see cref="CoordinationFlyout.NotStaffed"/>, shown while its text matches no listed position.</summary>
    public TextBlock BoxNote { get; } =
        new()
        {
            Text = CoordinationFlyout.NotStaffed,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    /// <summary>The line closing the list while the box's text matches no listed position (<see cref="CoordinationFlyout.NoMatch"/>).</summary>
    public TextBlock NoMatchText { get; } =
        new()
        {
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(11, 4, 11, 2),
            IsVisible = false,
        };

    /// <summary>The command Enter in the box sends now: the outlined row's, else the typed text upper-cased; null while it is blank.</summary>
    public string? BoxCommand
    {
        get
        {
            string text = Typed();
            if (text.Length == 0)
            {
                return null;
            }

            return Outlined?.SentCommand ?? CoordinationFlyout.Command(_verb, text.ToUpperInvariant());
        }
    }

    /// <summary>The row the box's text matches first, outlined; null while the box is blank or matches no row that can be sent.</summary>
    public CoordinationRow? Outlined => _rows.FirstOrDefault(row => row.IsOutlined);

    /// <summary>Every row as the flyout opened, in list order; they never change while it is open.</summary>
    public IReadOnlyList<CoordinationRow> Rows => _rows;

    /// <summary>
    /// The panel as text, one line per part: the title with the aircraft and its owner, the box with its command, the note while
    /// shown, <c>---</c>, then each shown group header, row and the no-match line.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            string title = CoordinationFlyout.Title(_verb);
            List<string> lines =
            [
                string.IsNullOrEmpty(_owner.Text) ? title : $"{title} · {_owner.Text}",
                $"{CoordinationFlyout.BoxLabel(_verb)} — {BoxCommandText.Text}",
            ];
            if (BoxNote.IsVisible)
            {
                lines.Add(BoxNote.Text ?? "");
            }

            lines.Add("---");
            lines.AddRange(_list.Children.Where(part => part.IsVisible).Select(PartLine));
            return lines;
        }
    }

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Reset();

    void IFlyoutPanel.Refresh() { }

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    /// <summary>Puts the flyout back as it opens: the box empty, the positions read afresh from the host.</summary>
    public void Reset()
    {
        _owner.Text = OwnerText(_aircraft);
        _rows.Clear();
        _headers.Clear();
        _list.Children.Clear();
        foreach (CoordinationGroup group in CoordinationFlyout.Arrange(_verb, _host.GetPositions(), _aircraft))
        {
            List<CoordinationRow> rows = [.. group.Positions.Select(position => new CoordinationRow(_verb, position, SendRowAsync))];
            if (group.Header is { } text)
            {
                TextBlock header = GroupHeader(text);
                _headers.Add((header, rows));
                _list.Children.Add(header);
            }

            _list.Children.AddRange(rows);
            _rows.AddRange(rows);
        }

        _list.Children.Add(NoMatchText);
        Box.Text = "";
        OnTyped();
    }

    private static string OwnerText(IMenuAircraft? aircraft)
    {
        if (aircraft is null)
        {
            return "";
        }

        string? owner = !string.IsNullOrEmpty(aircraft.OwnerSectorCode) ? aircraft.OwnerSectorCode : aircraft.Owner;
        return string.IsNullOrEmpty(owner) ? aircraft.Callsign : $"{aircraft.Callsign} · owned by {owner}";
    }

    private static TextBlock GroupHeader(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(11, 4, 11, 2),
        };

    private static string PartLine(Control part) => (part is CoordinationRow row) ? row.Line : ((part as TextBlock)?.Text ?? "");

    private string Typed() => (Box.Text ?? "").Trim();

    private Task SendRowAsync(CoordinationRow row) => (row.SentCommand is { } command) ? _choose(command) : Task.CompletedTask;

    private List<CoordinationRow> ShownRows() => [.. _rows.Where(row => row.IsVisible && (row.SentCommand is not null))];

    /// <summary>Filters the rows by the box's text, outlines the first match, then shows the box's command and note.</summary>
    private void OnTyped()
    {
        string text = Typed();
        foreach (CoordinationRow row in _rows)
        {
            row.IsVisible = CoordinationFlyout.Matches(row.Position, text);
        }

        foreach ((TextBlock header, List<CoordinationRow> rows) in _headers)
        {
            header.IsVisible = rows.Any(row => row.IsVisible);
        }

        CoordinationRow? first = (text.Length == 0) ? null : _rows.FirstOrDefault(row => row.IsVisible && (row.SentCommand is not null));
        foreach (CoordinationRow row in _rows)
        {
            row.Outline(ReferenceEquals(row, first));
        }

        string typed = text.ToUpperInvariant();
        NoMatchText.Text = CoordinationFlyout.NoMatch(_verb, typed);
        NoMatchText.IsVisible = (text.Length > 0) && !_rows.Any(row => row.IsVisible);
        string? command = BoxCommand;
        BoxCommandText.Text = command ?? $"{CoordinationFlyout.CommandWord(_verb)} {TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        BoxNote.IsVisible = (text.Length > 0) && (first is null);
        MenuCommandText.SetCommand(Box, command);
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        List<CoordinationRow> rows = ShownRows();
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
    private void Walk(KeyEventArgs e, List<CoordinationRow> rows, bool inBox, int focused)
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
