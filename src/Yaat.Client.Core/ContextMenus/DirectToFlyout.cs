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
/// The Direct to flyout's one menu row (<see cref="DirectToFlyout"/>): the title line, the verb toggle, the fix box with its
/// refusal, a separator, then the fix list (<see cref="FixListView"/>): the route rows and the matches.
/// </summary>
public sealed class DirectToFlyoutPanel : StackPanel, IFlyoutPanel
{
    private readonly DirectToVerb _preset;
    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly TextBlock _title = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly List<ToggleButton> _toggles = [];
    private readonly FixListView _fixes;

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
        _fixes = new FixListView(
            aircraft,
            Box,
            "Remaining route · next ●",
            new FixListBehaviour(RowCommand, FrdRow, SendRowAsync, e => ReferenceEquals(e.Source, Box) || Box.IsKeyboardFocusWithin, SendBoxAsync)
        );
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
        Children.Add(_fixes.View);
        AddHandler(KeyDownEvent, _fixes.OnKeyDown, RoutingStrategies.Tunnel);
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
    public IReadOnlyList<FixRow> RouteRows => _fixes.RouteRows;

    /// <summary>The rows the box's text adds: the typed FRD's own row first when it is one, then every other matching fix, nearest first.</summary>
    public IReadOnlyList<FixRow> MatchRows => _fixes.MatchRows;

    /// <summary>The row Enter in the box sends, outlined: the first shown row while the box holds text; null otherwise.</summary>
    public FixRow? OutlinedRow => _fixes.OutlinedRow;

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
            lines.AddRange(_fixes.Lines);
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
        _fixes.Reset((_aircraft is null) ? [] : FixList.DirectToRoute(_aircraft));
        Box.Text = "";
        SetVerb(_preset);
        OnTyped();
    }

    /// <summary>
    /// Follows the aircraft, in place: the title's next fix, the toggle's presence, each row's distance and bearing, the ● and
    /// the fixes passed since the flyout opened (<see cref="FixListView.Refresh"/>).
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
        _fixes.Refresh();
    }

    private (string? Sent, string Shown) RowCommand(string fix)
    {
        string command = DirectToFlyout.Command(Verb, fix);
        return (command, command);
    }

    /// <summary>The typed text's own row when it is an FRD, upper-cased; none otherwise.</summary>
    private static IReadOnlyList<string> FrdRow(string prefix)
    {
        string typed = prefix.ToUpperInvariant();
        return ((typed.Length > 0) && FixList.IsFrd(typed)) ? [typed] : [];
    }

    private Task SendRowAsync(FixRow row) => (row.SentCommand is { } command) ? _choose(command) : Task.CompletedTask;

    private Task SendBoxAsync() => (BoxCommand is { } command) ? _choose(command) : Task.CompletedTask;

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
        _fixes.UpdateCommands();
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

    /// <summary>Shows what the box now reads in the list (<see cref="FixListView.Filter"/>), then the box's command.</summary>
    private void OnTyped()
    {
        _fixes.Filter();
        ShowBoxCommand();
    }
}
