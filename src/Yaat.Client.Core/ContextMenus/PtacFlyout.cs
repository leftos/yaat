using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Sim.Data;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The PTAC flyout (<c>PTAC</c> / <c>PTACF</c>, 7110.65 5-9-4: a heading to fly, an altitude to maintain until established and
/// the approach clearance): the title with the aircraft's present heading and altitude, the PTAC / Forced toggle (Forced skips the
/// intercept-angle check), the Heading box (001 to 360; empty sends <c>PH</c>) and the Altitude box (empty sends <c>PA</c>), each
/// placeholder naming what that is now, then the approach rows: <see cref="ExpectedRowName"/> first while an approach is
/// expected (it sends no approach id, so the sim resolves it), then the destination's approaches (<see cref="ApproachChoices"/>).
/// Every row shows its whole command (<c>PTAC 280 025 I28R</c>) and sends on a click; Enter in a box sends the command without an
/// approach id. Without approaches a note replaces the rows and Enter sends nothing.
/// </summary>
public static class PtacFlyout
{
    /// <summary>The Heading box's name, by which a test or UI Automation finds it.</summary>
    public const string HeadingBoxName = "PtacHeadingBox";

    /// <summary>The Altitude box's name.</summary>
    public const string AltitudeBoxName = "PtacAltitudeBox";

    /// <summary>The caption under the toggle while Forced is on.</summary>
    public const string ForcedCaption = "skip the intercept-angle check";

    /// <summary>The name of the first row, which sends no approach id.</summary>
    public const string ExpectedRowName = "The expected approach";

    /// <summary>The note in the rows' place when the aircraft has no destination.</summary>
    public const string NoDestination = "No destination filed";

    /// <summary>The PTAC entry <paramref name="label"/> names: one menu row holding the PTAC flyout.</summary>
    public static MenuItem Build(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(entry, new PtacFlyoutHost(), context, host, (choose, _) => new PtacFlyoutPanel(aircraft, choose));
        return entry;
    }

    /// <summary>The verb: <c>PTACF</c> when <paramref name="forced"/>, else <c>PTAC</c>.</summary>
    public static string Verb(bool forced) => forced ? "PTACF" : "PTAC";

    /// <summary>The toggle label: <c>Forced</c> or <c>PTAC</c>.</summary>
    public static string ToggleLabel(bool forced) => forced ? "Forced" : "PTAC";

    /// <summary>The command: <c>PTAC 280 025 I28R</c>, or without the approach id when <paramref name="approachId"/> is null.</summary>
    public static string Command(bool forced, string heading, string altitude, string? approachId) =>
        (approachId is null) ? $"{Verb(forced)} {heading} {altitude}" : $"{Verb(forced)} {heading} {altitude} {approachId}";

    /// <summary>
    /// The heading token the Heading box's <paramref name="text"/> sends: <c>PH</c> when blank, the heading in three digits, or
    /// null.
    /// </summary>
    public static string? HeadingToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "PH";
        }

        return (FixValueFlyout.ParseHeading(text) is { } heading) ? HoldFlyout.Three(heading) : null;
    }

    /// <summary>
    /// The altitude token the Altitude box's <paramref name="text"/> sends: <c>PA</c> when blank, the altitude in hundreds (three
    /// digits), or null.
    /// </summary>
    public static string? AltitudeToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "PA";
        }

        return (FixValueFlyout.ParseAltitude(text) is { } feet) ? (feet / 100).ToString("D3", CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// What the empty Heading box reads: <c>PH (assigned 250)</c> while the aircraft flies an assigned heading with an empty
    /// navigation route, else <c>PH (present 250)</c>; <c>PH</c> without an aircraft.
    /// </summary>
    public static string HeadingPlaceholder(IMenuAircraft? aircraft)
    {
        if (aircraft is null)
        {
            return "PH";
        }

        return ((aircraft.AssignedHeading is { } assigned) && (aircraft.NavigationRoute.Count == 0))
            ? $"PH (assigned {Three(assigned.Degrees)})"
            : $"PH (present {Three(aircraft.MagneticHeadingDegrees)})";
    }

    /// <summary>
    /// What the empty Altitude box reads: <c>PA (assigned 3,000)</c> while an altitude is assigned, else
    /// <c>PA (present 3,000)</c>.
    /// </summary>
    public static string AltitudePlaceholder(IMenuAircraft? aircraft) =>
        aircraft switch
        {
            null => "PA",
            { AssignedAltitude: { } assigned } => $"PA (assigned {AltitudeFlyout.PresentText(assigned)})",
            _ => $"PA (present {AltitudeFlyout.PresentText(aircraft.AltitudeFeet)})",
        };

    /// <summary>The title's right side: <c>hdg 250 · 3,000</c>, the present heading and altitude; empty without an aircraft.</summary>
    public static string Now(IMenuAircraft? aircraft) =>
        (aircraft is null) ? "" : $"hdg {Three(aircraft.MagneticHeadingDegrees)} · {AltitudeFlyout.PresentText(aircraft.AltitudeFeet)}";

    /// <summary>The note in the rows' place when the destination has no approach (or the navigation data is not loaded).</summary>
    public static string NoApproachesAt(string destination) => $"No approaches at {destination}";

    /// <summary>
    /// The destination's approaches in flyout order and the expected one among them; with none, the note to show instead
    /// (<see cref="NoDestination"/>, <see cref="NoApproachesAt"/>).
    /// </summary>
    internal static (List<ApproachChoices.ApproachChoice> Choices, ApproachChoices.ApproachChoice? Expected, string? Empty) Approaches(
        IMenuAircraft? aircraft
    )
    {
        if (aircraft is not { Destination.Length: > 0 })
        {
            return ([], null, NoDestination);
        }

        List<ApproachChoices.ApproachChoice> choices = (NavigationDatabase.InstanceOrNull is null) ? [] : ApproachChoices.Choices(aircraft);
        return (choices.Count == 0) ? ([], null, NoApproachesAt(aircraft.Destination)) : (choices, ApproachChoices.Expected(aircraft, choices), null);
    }

    private static string Three(double degrees) => HoldFlyout.Three(HeadingFlyout.WholeHeading(degrees));
}

/// <summary>The PTAC flyout's menu row header, which holds the <see cref="PtacFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class PtacFlyoutHost() : FlyoutPanelHost("ptac flyout", "ptac-flyout-panel");

/// <summary>
/// One approach row of the PTAC flyout: the ● on the expected-approach row, the name, the note (the expected approach's id, or
/// the approach's kind) and the command column, amber while Forced is on.
/// </summary>
public sealed class PtacRow : Button
{
    private readonly TextBlock _command = FlyoutPanelParts.CommandText("");

    internal PtacRow(string? approachId, string name, string note, bool isExpected, Func<PtacRow, Task> chosen)
    {
        ApproachId = approachId;
        ApproachName = name;
        Note = note;
        IsExpected = isExpected;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        grid.Children.Add(
            new TextBlock
            {
                Text = isExpected ? "●" : "",
                Width = 14,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var nameText = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameText, 1);
        grid.Children.Add(nameText);
        var noteText = new TextBlock
        {
            Text = note,
            FontSize = 12,
            Opacity = 0.8,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(noteText, 2);
        grid.Children.Add(noteText);
        Grid.SetColumn(_command, 3);
        grid.Children.Add(_command);
        Content = new Border { Padding = new Thickness(10, 3), Child = grid };
        Padding = new Thickness(0);
        Background = isExpected ? FlyoutPanelParts.NowFill : Brushes.Transparent;
        BorderThickness = new Thickness(3, 0, 0, 0);
        BorderBrush = isExpected ? FlyoutPanelParts.NowEdge : Brushes.Transparent;
        CornerRadius = new CornerRadius(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Click += async (_, e) =>
        {
            e.Handled = true;
            await chosen(this);
        };
    }

    /// <summary>The approach id the row sends; null for the expected-approach row, which sends none.</summary>
    public string? ApproachId { get; }

    /// <summary>The row's name: <see cref="PtacFlyout.ExpectedRowName"/> or the approach's published name.</summary>
    public string ApproachName { get; }

    /// <summary>The note: <c>I28R expected</c> on the expected-approach row, else the approach's kind.</summary>
    public string Note { get; }

    /// <summary>Whether the row is the expected-approach row, marked ●.</summary>
    public bool IsExpected { get; }

    /// <summary>The command a click sends now; null while a box holds no heading or altitude.</summary>
    public string? SentCommand { get; private set; }

    /// <summary>The command column: <see cref="SentCommand"/>, or the verb with <c>···</c> while it is null.</summary>
    public string ShownCommand => _command.Text ?? "";

    /// <summary>The command column's brush: <see cref="ApproachRowView.ForcedCommand"/> while Forced is on.</summary>
    public IBrush? CommandForeground => _command.Foreground;

    /// <summary>The row as one line: the mark, the name, the note and the command column.</summary>
    public string Line => $"{(IsExpected ? "●" : " ")} {ApproachName} · {Note} — {ShownCommand}";

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Sets the command a click sends (<paramref name="sent"/>, null for none), the column's text and its force colour.</summary>
    internal void SetCommand(string? sent, string shown, bool forced)
    {
        SentCommand = sent;
        _command.Text = shown;
        _command.Opacity = forced ? 0.95 : 0.7;
        if (forced)
        {
            _command.Foreground = ApproachRowView.ForcedCommand;
        }
        else
        {
            _command.ClearValue(TextBlock.ForegroundProperty);
        }

        MenuCommandText.SetCommand(this, sent);
        AutomationProperties.SetName(this, $"{ApproachName} — {shown}");
    }
}

/// <summary>
/// The PTAC flyout's panel (<see cref="PtacFlyout"/>). The keyboard: the Heading box holds focus at open; Down from either box
/// onto the first row, Down and Up through the rows, Up from the top row back to the Altitude box; Enter in a box sends the
/// command without an approach id, Enter on a row sends it.
/// </summary>
public sealed class PtacFlyoutPanel : StackPanel, IFlyoutPanel
{
    private const double ListMaxHeight = 200;
    private static readonly IImmutableSolidColorBrush NoteBrush = new ImmutableSolidColorBrush(Color.Parse("#8A929C"));

    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly TextBlock _title = new()
    {
        Text = "PTAC",
        FontSize = 11,
        Foreground = FlyoutPanelParts.TitleBrush,
    };
    private readonly TextBlock _now = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _headingToken = new();
    private readonly TextBlock _altitudeToken = new();
    private readonly TextBlock _group = new()
    {
        Text = "Approach",
        FontSize = 11,
        Foreground = FlyoutPanelParts.TitleBrush,
        Margin = new Thickness(11, 4, 11, 2),
    };
    private readonly StackPanel _list = new();
    private readonly List<PtacRow> _rows = [];
    private bool _hasApproaches;

    internal PtacFlyoutPanel(IMenuAircraft? aircraft, Func<string, Task> choose)
    {
        _aircraft = aircraft;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        PtacToggle = FlyoutPanelParts.VerbToggle(PtacFlyout.ToggleLabel(false), PtacFlyout.Verb(false), true, () => SetForced(false));
        ForcedToggle = FlyoutPanelParts.VerbToggle(PtacFlyout.ToggleLabel(true), PtacFlyout.Verb(true), false, () => SetForced(true));
        HeadingBox = Box(PtacFlyout.HeadingBoxName, "Heading");
        AltitudeBox = Box(PtacFlyout.AltitudeBoxName, "Altitude");
        QuickCommandGlyph headingGlyph = QuickCommandGlyphs.For(MenuIds.HeadingFly) ?? throw new InvalidOperationException("No heading glyph.");
        QuickCommandGlyph altitudeGlyph =
            QuickCommandGlyphs.For(MenuIds.AltitudeMaintain) ?? throw new InvalidOperationException("No altitude glyph.");

        Children.Add(FlyoutPanelParts.TitleLine(_title, _now));
        Children.Add(FlyoutPanelParts.ToggleStrip([PtacToggle, ForcedToggle]));
        Children.Add(ForcedCaption);
        Children.Add(FlyoutPanelParts.BoxRow(headingGlyph, "Heading", HeadingBox, _headingToken));
        Children.Add(HeadingError);
        Children.Add(FlyoutPanelParts.BoxRow(altitudeGlyph, "Altitude", AltitudeBox, _altitudeToken));
        Children.Add(AltitudeError);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Children.Add(_group);
        Children.Add(EmptyNote);
        Children.Add(new ScrollViewer { MaxHeight = ListMaxHeight, Content = _list });
        HeadingBox.PropertyChanged += OnBoxChanged;
        AltitudeBox.PropertyChanged += OnBoxChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Reset();
        Refresh();
    }

    /// <summary>Whether Forced is on: the rows send <c>PTACF</c>.</summary>
    public bool IsForced { get; private set; }

    /// <summary>The toggle's PTAC button.</summary>
    public ToggleButton PtacToggle { get; }

    /// <summary>The toggle's Forced button.</summary>
    public ToggleButton ForcedToggle { get; }

    /// <summary>The Heading box: 001 to 360; empty sends <c>PH</c>.</summary>
    public TextBox HeadingBox { get; }

    /// <summary>The Altitude box: hundreds or feet; empty sends <c>PA</c>.</summary>
    public TextBox AltitudeBox { get; }

    /// <summary>The caption under the toggle while Forced is on (<see cref="PtacFlyout.ForcedCaption"/>).</summary>
    public TextBlock ForcedCaption { get; } =
        new()
        {
            Text = PtacFlyout.ForcedCaption,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(11, 0, 11, 2),
            IsVisible = false,
        };

    /// <summary>The refusal under the Heading box (<see cref="FixValueFlyout.InvalidHeading"/>).</summary>
    public TextBlock HeadingError { get; } = Refusal(FixValueFlyout.InvalidHeading);

    /// <summary>The refusal under the Altitude box (<see cref="AltitudeFlyout.InvalidAltitude"/>).</summary>
    public TextBlock AltitudeError { get; } = Refusal(AltitudeFlyout.InvalidAltitude);

    /// <summary>The faint note in the rows' place when there is no approach to list.</summary>
    public TextBlock EmptyNote { get; } =
        new()
        {
            FontSize = 12,
            Foreground = NoteBrush,
            Margin = new Thickness(11, 2, 11, 4),
            IsVisible = false,
        };

    /// <summary>The title line's right side: the present heading and altitude.</summary>
    public string Now => _now.Text ?? "";

    /// <summary>The approach rows: the expected-approach row first when an approach is expected, then the destination's approaches.</summary>
    public IReadOnlyList<PtacRow> Rows => _rows;

    /// <summary>What Enter in a box sends: the command without an approach id; null while a box holds no value or there is no approach.</summary>
    public string? BoxCommand => BoxCommandFor(PtacFlyout.HeadingToken(HeadingBox.Text ?? ""), PtacFlyout.AltitudeToken(AltitudeBox.Text ?? ""));

    /// <summary>
    /// The panel as text, one line per shown part: the title and its right side, the toggle (the verb on in brackets), the caption,
    /// each box with its placeholder and token and its refusal, <c>---</c>, the group line, then the note or the rows.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            List<string> lines = [$"PTAC · {Now}", $"{ToggleText(false)} | {ToggleText(true)}"];
            AddShown(lines, ForcedCaption);
            lines.Add($"Heading [{HeadingBox.Text}] {HeadingBox.PlaceholderText} — {_headingToken.Text}");
            AddShown(lines, HeadingError);
            lines.Add($"Altitude [{AltitudeBox.Text}] {AltitudeBox.PlaceholderText} — {_altitudeToken.Text}");
            AddShown(lines, AltitudeError);
            lines.Add("---");
            lines.Add(_group.Text ?? "");
            AddShown(lines, EmptyNote);
            lines.AddRange(_rows.Select(row => row.Line));
            return lines;
        }
    }

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Reset();

    void IFlyoutPanel.Refresh() => Refresh();

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(HeadingBox, isOpen, () => { });

    /// <summary>Puts the flyout back as it opens: PTAC on, the boxes empty, the approach rows read afresh.</summary>
    public void Reset()
    {
        (List<ApproachChoices.ApproachChoice> choices, ApproachChoices.ApproachChoice? expected, string? empty) = PtacFlyout.Approaches(_aircraft);
        _hasApproaches = choices.Count > 0;
        _rows.Clear();
        if (expected is not null)
        {
            _rows.Add(new PtacRow(null, PtacFlyout.ExpectedRowName, $"{expected.Id} expected", true, SendRowAsync));
        }

        _rows.AddRange(choices.Select(choice => new PtacRow(choice.Id, choice.FullName, choice.Kind, false, SendRowAsync)));
        _list.Children.Clear();
        _list.Children.AddRange(_rows);
        EmptyNote.Text = empty ?? "";
        EmptyNote.IsVisible = empty is not null;
        HeadingBox.Text = "";
        AltitudeBox.Text = "";
        SetForced(false);
    }

    /// <summary>Follows the aircraft, in place: the title's present heading and altitude, the placeholders and every command.</summary>
    public void Refresh()
    {
        _now.Text = PtacFlyout.Now(_aircraft);
        HeadingBox.PlaceholderText = PtacFlyout.HeadingPlaceholder(_aircraft);
        AltitudeBox.PlaceholderText = PtacFlyout.AltitudePlaceholder(_aircraft);
        ShowAll();
    }

    private static TextBox Box(string name, string automationName)
    {
        var box = new TextBox
        {
            Name = name,
            Width = 150,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(box, automationName);
        TakeoffFlyout.KeepClicksInside(box);
        return box;
    }

    private static TextBlock Refusal(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = FlyoutPanelParts.RefusalBrush,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    private static void AddShown(List<string> lines, TextBlock text)
    {
        if (text.IsVisible)
        {
            lines.Add(text.Text ?? "");
        }
    }

    /// <summary>The command Enter in a box sends for the boxes' tokens; null while either token is null or there is no approach.</summary>
    private string? BoxCommandFor(string? heading, string? altitude) =>
        (_hasApproaches && (heading is not null) && (altitude is not null)) ? PtacFlyout.Command(IsForced, heading, altitude, null) : null;

    private string ToggleText(bool forced)
    {
        string text = $"{PtacFlyout.ToggleLabel(forced)} {PtacFlyout.Verb(forced)}";
        return (forced == IsForced) ? $"[{text}]" : text;
    }

    private Task SendRowAsync(PtacRow row) => (row.SentCommand is { } command) ? _choose(command) : Task.CompletedTask;

    private void SetForced(bool forced)
    {
        IsForced = forced;
        PtacToggle.IsChecked = !forced;
        ForcedToggle.IsChecked = forced;
        ForcedCaption.IsVisible = forced;
        ShowAll();
    }

    private void OnBoxChanged(object? _, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty)
        {
            ShowAll();
        }
    }

    /// <summary>Shows each box's token and refusal, every row's command, and what Enter in a box sends.</summary>
    private void ShowAll()
    {
        string? heading = PtacFlyout.HeadingToken(HeadingBox.Text ?? "");
        string? altitude = PtacFlyout.AltitudeToken(AltitudeBox.Text ?? "");
        _headingToken.Text = heading ?? "";
        _altitudeToken.Text = altitude ?? "";
        HeadingError.IsVisible = heading is null;
        AltitudeError.IsVisible = altitude is null;
        foreach (PtacRow row in _rows)
        {
            string? command =
                ((heading is not null) && (altitude is not null)) ? PtacFlyout.Command(IsForced, heading, altitude, row.ApproachId) : null;
            row.SetCommand(command, command ?? $"{PtacFlyout.Verb(IsForced)} {TakeoffFlyout.Unfilled}", IsForced);
        }

        string? boxCommand = BoxCommandFor(heading, altitude);
        MenuCommandText.SetCommand(HeadingBox, boxCommand);
        MenuCommandText.SetCommand(AltitudeBox, boxCommand);
    }

    private async void OnKeyDown(object? _, KeyEventArgs e)
    {
        bool inBox =
            HeadingBox.IsKeyboardFocusWithin
            || AltitudeBox.IsKeyboardFocusWithin
            || ReferenceEquals(e.Source, HeadingBox)
            || ReferenceEquals(e.Source, AltitudeBox);
        int focused = _rows.FindIndex(row => row.IsKeyboardFocusWithin);
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
                await SendRowAsync(_rows[focused]);
                break;
            case Key.Down or Key.Up:
                Walk(e, inBox, focused);
                break;
        }
    }

    /// <summary>Down from a box focuses the first row; Down and Up walk the rows; Up from the top row returns to the Altitude box.</summary>
    private void Walk(KeyEventArgs e, bool inBox, int focused)
    {
        int? next = (inBox, focused, e.Key) switch
        {
            (true, _, Key.Down) when _rows.Count > 0 => 0,
            (false, 0, Key.Up) => -1,
            (false, >= 0, Key.Up) => focused - 1,
            (false, >= 0, Key.Down) => Math.Min(focused + 1, _rows.Count - 1),
            _ => null,
        };
        if (next is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (target < 0)
        {
            AltitudeBox.Focus(NavigationMethod.Directional);
            return;
        }

        _rows[target].Focus(NavigationMethod.Directional);
        _rows[target].BringIntoView();
    }
}
