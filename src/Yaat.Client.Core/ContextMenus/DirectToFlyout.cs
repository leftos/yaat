using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Yaat.Client.ContextMenus;

/// <summary>What the Direct to flyout's rows and box send: a direct to the fix, or the fix appended to the route.</summary>
public enum DirectToVerb
{
    /// <summary><c>DCT</c>: proceed direct to the fix.</summary>
    DirectTo,

    /// <summary><c>ADCT</c>: append the fix to the route.</summary>
    AppendDirectTo,
}

/// <summary>
/// The Direct to flyout, one builder for Direct to… and Append direct to…, each preset to its own verb. Top to bottom: the title
/// line (the verb, and the fix the aircraft navigates to), the Direct to / Append to route toggle (only while the aircraft
/// navigates to a fix), the fix box with the command Enter sends (<see cref="UnknownFix"/> in red under it when it can send
/// nothing), a separator, then the list: the remaining route as rows (<see cref="FixList.DirectToRoute"/>: the fix, its
/// distance and magnetic bearing from the aircraft, its command, ● on the next fix), then as you type an FRD's own row and every
/// other fix whose name starts with the text, nearest first (<see cref="FixList.Matches"/>). Typing hides the route rows that do
/// not match, in place, and outlines the first shown row (the row naming the typed text exactly, when one does), which Enter
/// sends; with no row shown Enter sends the typed fix or FRD. The box holds focus at open; Down moves into the rows (onto the ●
/// row when shown), Down and Up walk them, Up from the top row returns to the box, and Enter sends the focused row.
/// While open, the rows follow the aircraft once a second (<see cref="FlyoutLiveTick"/>) and never move: a route fix before the
/// one the aircraft navigates to greys to "passed" where it is, stays passed, and still sends.
/// </summary>
public static class DirectToFlyout
{
    /// <summary>The fix box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "DirectToFixBox";

    /// <summary>What the empty fix box reads.</summary>
    public const string BoxPlaceholder = "Fix name or FRD";

    /// <summary>The refusal under the box when its text names no fix.</summary>
    public const string UnknownFix = "Not a known fix";

    /// <summary>What the list says under the route while nothing is typed.</summary>
    public const string SearchHint = "Type to search every fix, nearest first";

    /// <summary>The entry <paramref name="label"/> names: one menu row holding the flyout over <paramref name="aircraft"/>.</summary>
    /// <param name="label">The entry's label (<c>Direct to…</c>, <c>Append direct to…</c>).</param>
    /// <param name="preset">The verb the flyout opens on.</param>
    /// <param name="aircraft">The aircraft whose route the flyout lists.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the flyout sends through and whose clock it refreshes on.</param>
    public static MenuItem Build(string label, DirectToVerb preset, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(entry, new DirectToFlyoutHost(), context, host, (choose, _) => new DirectToFlyoutPanel(preset, aircraft, choose));
        return entry;
    }

    /// <summary>The verb's command text: <c>DCT</c> or <c>ADCT</c>.</summary>
    public static string VerbText(DirectToVerb verb) =>
        verb switch
        {
            DirectToVerb.DirectTo => "DCT",
            DirectToVerb.AppendDirectTo => "ADCT",
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown direct-to verb."),
        };

    /// <summary>The verb's toggle label: <c>Direct to</c> or <c>Append to route</c>.</summary>
    public static string ToggleLabel(DirectToVerb verb) =>
        verb switch
        {
            DirectToVerb.DirectTo => "Direct to",
            DirectToVerb.AppendDirectTo => "Append to route",
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown direct-to verb."),
        };

    /// <summary>The verb's title: <c>Direct to</c> or <c>Append direct to</c>.</summary>
    public static string Title(DirectToVerb verb) =>
        verb switch
        {
            DirectToVerb.DirectTo => "Direct to",
            DirectToVerb.AppendDirectTo => "Append direct to",
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown direct-to verb."),
        };

    /// <summary>The command for <paramref name="fix"/> under <paramref name="verb"/>: <c>DCT SUNOL</c>.</summary>
    public static string Command(DirectToVerb verb, string fix) => $"{VerbText(verb)} {fix}";

    /// <summary>
    /// The command the box sends for <paramref name="text"/>: one fix name or FRD, upper-cased, that the navigation data knows
    /// (<see cref="FixList.IsKnownFix"/>); null for a blank box, several words or an unknown fix.
    /// </summary>
    public static string? BoxCommand(string text, DirectToVerb verb)
    {
        string fix = text.Trim().ToUpperInvariant();
        if ((fix.Length == 0) || fix.Any(char.IsWhiteSpace) || !FixList.IsKnownFix(fix))
        {
            return null;
        }

        return Command(verb, fix);
    }
}

/// <summary>The Direct to flyout's menu row header, which holds the <see cref="DirectToFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class DirectToFlyoutHost() : FlyoutPanelHost("direct to flyout", "direct-to-flyout-panel");

/// <summary>
/// One fix row of the Direct to flyout: the ● mark on the next fix, the fix, its distance and magnetic bearing (or
/// <c>passed</c>), and the command a click sends, in the outline the box draws round the row Enter sends.
/// </summary>
public sealed class DirectToFixRow : Button
{
    private static readonly IImmutableSolidColorBrush OutlineBrush = new ImmutableSolidColorBrush(Color.Parse("#8FC1FF"));

    private readonly Border _outline = new()
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        BorderBrush = Brushes.Transparent,
        Padding = new Thickness(10, 3),
    };
    private readonly TextBlock _mark = new() { Width = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _note = new()
    {
        FontSize = 12,
        Opacity = 0.8,
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _command = FlyoutPanelParts.CommandText("");

    internal DirectToFixRow(string fix, Func<string, Task> choose)
    {
        Fix = fix;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        grid.Children.Add(_mark);
        var name = new TextBlock { Text = fix, VerticalAlignment = VerticalAlignment.Center };
        FlyoutPanelParts.Mono(name);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(_note, 2);
        grid.Children.Add(_note);
        Grid.SetColumn(_command, 3);
        grid.Children.Add(_command);
        _outline.Child = grid;
        Content = _outline;
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(3, 0, 0, 0);
        BorderBrush = Brushes.Transparent;
        CornerRadius = new CornerRadius(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Click += async (_, e) =>
        {
            e.Handled = true;
            await choose(SentCommand);
        };
    }

    /// <summary>The fix the row sends.</summary>
    public string Fix { get; }

    /// <summary>The command a click sends now.</summary>
    public string SentCommand { get; private set; } = "";

    /// <summary>The distance and magnetic bearing, <c>passed</c>, or empty when the fix has no position.</summary>
    public string Note => _note.Text ?? "";

    /// <summary>Whether the row is the fix the aircraft navigates to, filled and marked ●.</summary>
    public bool IsNext { get; private set; }

    /// <summary>Whether the aircraft has passed the fix since the flyout opened: greyed, and still sending; once passed, it stays so.</summary>
    public bool IsPassed { get; private set; }

    /// <summary>Whether the row is outlined: the row Enter in the box sends.</summary>
    public bool IsOutlined { get; private set; }

    /// <summary>The row as one line: the mark, the fix, the note and the command.</summary>
    public string Line => $"{(IsNext ? "●" : " ")} {Fix}{((Note.Length > 0) ? $" · {Note}" : "")} — {SentCommand}";

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    internal void SetVerb(DirectToVerb verb)
    {
        SentCommand = DirectToFlyout.Command(verb, Fix);
        _command.Text = SentCommand;
        MenuCommandText.SetCommand(this, SentCommand);
        AutomationProperties.SetName(this, $"{Fix} — {SentCommand}");
    }

    /// <summary>Shows the row's state now: the ● when <paramref name="isNext"/>, and passed from now on once <paramref name="passes"/>.</summary>
    internal void Show(bool isNext, bool passes, string? note)
    {
        IsPassed |= passes;
        IsNext = isNext && !IsPassed;
        _mark.Text = IsNext ? "●" : "";
        _note.Text = IsPassed ? "passed" : (note ?? "");
        Background = IsNext ? FlyoutPanelParts.NowFill : Brushes.Transparent;
        BorderBrush = IsNext ? FlyoutPanelParts.NowEdge : Brushes.Transparent;
        Opacity = IsPassed ? MenuGlyphRowTemplate.DimmedOpacity : 1;
    }

    internal void SetOutlined(bool outlined)
    {
        IsOutlined = outlined;
        _outline.BorderBrush = outlined ? OutlineBrush : Brushes.Transparent;
    }
}

/// <summary>
/// The Direct to flyout's one menu row (<see cref="DirectToFlyout"/>): the title line, the verb toggle, the fix box with its
/// refusal, a separator, the route rows and the matches.
/// </summary>
public sealed class DirectToFlyoutPanel : StackPanel, IFlyoutPanel
{
    private const double ListMaxHeight = 300;

    private readonly DirectToVerb _preset;
    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly TextBlock _title = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly List<ToggleButton> _toggles = [];
    private readonly StackPanel _list = new();
    private readonly TextBlock _routeHeader = GroupHeader("Remaining route · next ●");
    private readonly TextBlock _matchHeader = GroupHeader("");
    private readonly List<DirectToFixRow> _routeRows = [];
    private readonly List<DirectToFixRow> _matchRows = [];

    internal DirectToFlyoutPanel(DirectToVerb preset, IMenuAircraft? aircraft, Func<string, Task> choose)
    {
        _preset = preset;
        _aircraft = aircraft;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        QuickCommandGlyph glyph =
            QuickCommandGlyphs.For(MenuIds.NavigationDirectTo) ?? throw new InvalidOperationException($"No glyph for {MenuIds.NavigationDirectTo}.");
        Children.Add(FlyoutPanelParts.TitleLine(_title, _titleNow));
        foreach (DirectToVerb verb in Enum.GetValues<DirectToVerb>())
        {
            _toggles.Add(
                FlyoutPanelParts.VerbToggle(
                    DirectToFlyout.ToggleLabel(verb),
                    DirectToFlyout.VerbText(verb),
                    verb == DirectToVerb.DirectTo,
                    () => SetVerb(verb)
                )
            );
        }

        VerbToggles = FlyoutPanelParts.ToggleStrip(_toggles);
        Children.Add(VerbToggles);
        Box = new TextBox
        {
            Name = DirectToFlyout.BoxName,
            PlaceholderText = DirectToFlyout.BoxPlaceholder,
            Width = 190,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(Box, "Fix");
        TakeoffFlyout.KeepClicksInside(Box);
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTyped();
            }
        };
        Children.Add(FlyoutPanelParts.BoxRow(glyph, "Fix", Box, BoxCommandText));
        Children.Add(BoxError);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Children.Add(new ScrollViewer { MaxHeight = ListMaxHeight, Content = _list });
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        SetVerb(preset);
        Refresh();
    }

    /// <summary>The verb the rows and the box send now, which the toggle sets.</summary>
    public DirectToVerb Verb { get; private set; }

    /// <summary>The Direct to / Append to route toggle, shown only while the aircraft navigates to a fix.</summary>
    public Border VerbToggles { get; }

    /// <summary>The fix box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter in it sends, or the verb with <c>···</c>, faded, while it cannot send.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The refusal under the box (<see cref="DirectToFlyout.UnknownFix"/>), shown while the box names no fix.</summary>
    public TextBlock BoxError { get; } =
        new()
        {
            Text = DirectToFlyout.UnknownFix,
            FontSize = 11,
            Foreground = FlyoutPanelParts.RefusalBrush,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    /// <summary>
    /// The command Enter in the box sends now: the outlined row's, else the typed fix or FRD's when the navigation data knows it;
    /// null while it cannot send.
    /// </summary>
    public string? BoxCommand => OutlinedRow?.SentCommand ?? DirectToFlyout.BoxCommand(Box.Text ?? "", Verb);

    /// <summary>The remaining route's rows as the flyout opened, in route order; they never move while it stays open.</summary>
    public IReadOnlyList<DirectToFixRow> RouteRows => _routeRows;

    /// <summary>The rows the box's text adds: the typed FRD's own row first when it is one, then every other matching fix, nearest first.</summary>
    public IReadOnlyList<DirectToFixRow> MatchRows => _matchRows;

    /// <summary>The row Enter in the box sends, outlined: the first shown row while the box holds text; null otherwise.</summary>
    public DirectToFixRow? OutlinedRow { get; private set; }

    /// <summary>
    /// The panel as text, one line per part: the title line, the toggle (the verb on in brackets) while shown, the box with its
    /// command, the refusal while shown, <c>---</c>, then each shown group header and row of the list.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            List<string> lines = [string.IsNullOrEmpty(_titleNow.Text) ? _title.Text ?? "" : $"{_title.Text} · {_titleNow.Text}"];
            if (VerbToggles.IsVisible)
            {
                lines.Add(string.Join(" | ", Enum.GetValues<DirectToVerb>().Select(ToggleText)));
            }

            lines.Add($"Fix — {BoxCommandText.Text}");
            if (BoxError.IsVisible)
            {
                lines.Add(BoxError.Text ?? "");
            }

            lines.Add("---");
            foreach (Control part in _list.Children.Where(part => part.IsVisible))
            {
                lines.Add((part is DirectToFixRow row) ? row.Line : ((part as TextBlock)?.Text ?? ""));
            }

            return lines;
        }
    }

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Reset();

    void IFlyoutPanel.Refresh() => Refresh();

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    /// <summary>The toggle button of <paramref name="verb"/>.</summary>
    public ToggleButton Toggle(DirectToVerb verb) => _toggles[(int)verb];

    /// <summary>
    /// Puts the flyout back as it opens: the toggle on the preset's verb, the box empty, the route rows read afresh. The route
    /// (and its filed-route expansion) is read only here, never on a refresh.
    /// </summary>
    public void Reset()
    {
        _routeRows.Clear();
        if (_aircraft is not null)
        {
            _routeRows.AddRange(FixList.DirectToRoute(_aircraft).Select(fix => new DirectToFixRow(fix, _choose)));
        }

        Box.Text = "";
        SetVerb(_preset);
        OnTyped();
    }

    /// <summary>
    /// Follows the aircraft, in place: the title's next fix, the toggle's presence, each row's distance and bearing, the ● and
    /// the fixes passed since the flyout opened. Passing advances only while the aircraft navigates to a route row's fix: the
    /// rows before it turn passed and stay so; when it navigates nowhere (or off the route) no row changes and the ● goes.
    /// </summary>
    public void Refresh()
    {
        string next = _aircraft?.NavigatingTo ?? "";
        bool navigating = next.Length > 0;
        VerbToggles.IsVisible = navigating;
        if (!navigating && (Verb == DirectToVerb.AppendDirectTo))
        {
            SetVerb(DirectToVerb.DirectTo);
        }

        _titleNow.Text = navigating ? $"→ {next}" : "";
        int nextIndex = navigating ? _routeRows.FindIndex(row => string.Equals(row.Fix, next, StringComparison.OrdinalIgnoreCase)) : -1;
        for (int i = 0; i < _routeRows.Count; i++)
        {
            _routeRows[i].Show(i == nextIndex, i < nextIndex, Note(_routeRows[i].Fix));
        }

        foreach (DirectToFixRow row in _matchRows)
        {
            row.Show(false, false, Note(row.Fix));
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

    private string? Note(string fix) => (_aircraft is null) ? null : FixList.DistanceAndBearing(_aircraft.Position, fix);

    private string ToggleText(DirectToVerb verb)
    {
        string text = $"{DirectToFlyout.ToggleLabel(verb)} {DirectToFlyout.VerbText(verb)}";
        return (verb == Verb) ? $"[{text}]" : text;
    }

    private void SetVerb(DirectToVerb verb)
    {
        Verb = verb;
        foreach (DirectToVerb each in Enum.GetValues<DirectToVerb>())
        {
            Toggle(each).IsChecked = (each == verb);
        }

        _title.Text = DirectToFlyout.Title(verb);
        foreach (DirectToFixRow row in _routeRows.Concat(_matchRows))
        {
            row.SetVerb(verb);
        }

        ShowBoxCommand();
    }

    private void ShowBoxCommand()
    {
        string? command = BoxCommand;
        BoxCommandText.Text = command ?? $"{DirectToFlyout.VerbText(Verb)} {TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        BoxError.IsVisible = (command is null) && !string.IsNullOrWhiteSpace(Box.Text);
        MenuCommandText.SetCommand(Box, command);
    }

    /// <summary>
    /// Shows what the box now reads: the route rows that match (hidden in place otherwise), the typed FRD's own row and every
    /// other match, nearest first, under a header naming their count; then outlines the row Enter sends and shows its command.
    /// </summary>
    private void OnTyped()
    {
        string prefix = (Box.Text ?? "").Trim();
        foreach (DirectToFixRow row in _routeRows)
        {
            row.IsVisible = (prefix.Length == 0) || row.Fix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        bool frd = FillMatches(prefix);
        int fixMatches = _matchRows.Count - (frd ? 1 : 0);
        _routeHeader.IsVisible = _routeRows.Any(row => row.IsVisible);
        _matchHeader.IsVisible = !frd || (fixMatches > 0);
        _matchHeader.Text =
            (prefix.Length == 0) ? DirectToFlyout.SearchHint
            : (fixMatches > 0) ? $"All fixes · nearest first ({fixMatches})"
            : $"No other fix starts with {prefix.ToUpperInvariant()}";
        _list.Children.Clear();
        _list.Children.Add(_routeHeader);
        _list.Children.AddRange(_routeRows);
        _list.Children.Add(_matchHeader);
        _list.Children.AddRange(_matchRows);
        Outline(prefix);
    }

    /// <summary>
    /// Lists the rows <paramref name="prefix"/> adds: its own row when it is an FRD, then every other matching fix; true for an FRD.
    /// </summary>
    private bool FillMatches(string prefix)
    {
        _matchRows.Clear();
        string typed = prefix.ToUpperInvariant();
        bool frd = (typed.Length > 0) && FixList.IsFrd(typed);
        if (frd)
        {
            _matchRows.Add(new DirectToFixRow(typed, _choose));
        }

        if (_aircraft is not null)
        {
            IReadOnlyList<string> matches = FixList.Matches(prefix, _aircraft.Position, [.. _routeRows.Select(row => row.Fix)]);
            _matchRows.AddRange(matches.Select(fix => new DirectToFixRow(fix, _choose)));
        }

        foreach (DirectToFixRow row in _matchRows)
        {
            row.SetVerb(Verb);
            row.Show(false, false, Note(row.Fix));
        }

        return frd;
    }

    /// <summary>
    /// Outlines the row Enter sends while the box holds text: the shown row naming <paramref name="prefix"/> exactly, else the
    /// first shown row; then shows the box's command.
    /// </summary>
    private void Outline(string prefix)
    {
        List<DirectToFixRow> shown = ShownRows();
        OutlinedRow =
            (prefix.Length == 0)
                ? null
                : shown.FirstOrDefault(row => string.Equals(row.Fix, prefix, StringComparison.OrdinalIgnoreCase)) ?? shown.FirstOrDefault();
        foreach (DirectToFixRow row in _routeRows.Concat(_matchRows))
        {
            row.SetOutlined(ReferenceEquals(row, OutlinedRow));
        }

        OutlinedRow?.BringIntoView();
        ShowBoxCommand();
    }

    /// <summary>The rows the list shows now, top to bottom: the route rows that match the box, then the rows it adds.</summary>
    private List<DirectToFixRow> ShownRows() => [.. _routeRows.Where(row => row.IsVisible), .. _matchRows];

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        List<DirectToFixRow> rows = ShownRows();
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
                await _choose(rows[focused].SentCommand);
                break;
            case Key.Down or Key.Up:
                OnWalkKey(e, rows, inBox, focused);
                break;
        }
    }

    /// <summary>
    /// Down from the box focuses the ● row (else the first shown row); Down and Up walk the rows; Up from the top row returns to
    /// the box.
    /// </summary>
    private void OnWalkKey(KeyEventArgs e, List<DirectToFixRow> rows, bool inBox, int focused)
    {
        int? next = (inBox, focused, e.Key) switch
        {
            (true, _, Key.Down) when rows.Count > 0 => Math.Max(0, rows.FindIndex(row => row.IsNext)),
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
