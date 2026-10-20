using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Cross fix and Depart fix flyouts, each a fix list with the value its command needs in boxes under the fix box. Cross
/// fix: the Cross at toggle (At / At or above <c>A</c> / At or below <c>B</c>, opening on At), the Altitude box and the
/// optional Speed box, sending <c>CFIX SPAMY A110 250</c>. Depart fix: the Heading box, sending <c>DEPART SPAMY 200</c>.
/// The fix box takes a fix's own name only (the parser resolves no fix-radial-distance for these verbs). The list is the
/// aircraft's route (<see cref="IMenuAircraft.RouteFixNames"/>) as rows with distance, magnetic bearing, ● on the next fix and
/// passed fixes greyed in place, then as you type every other matching fix nearest first; each row shows the whole command it
/// sends with the boxes' values. A row click, or Enter, with a required box empty or a box holding no value sends nothing: it
/// puts the fix in the fix box and focuses the box the command still needs, which shows its red refusal.
/// </summary>
public static class FixValueFlyout
{
    /// <summary>The fix box's name, by which a test or UI Automation finds it.</summary>
    public const string FixBoxName = "FixValueFixBox";

    /// <summary>What the empty fix box reads.</summary>
    public const string FixPlaceholder = "Fix name";

    /// <summary>The refusal under the Altitude box while it is empty and the command needs it.</summary>
    public const string AltitudeRequired = "Altitude required";

    /// <summary>The refusal under the Heading box while it is empty and the command needs it.</summary>
    public const string HeadingRequired = "Heading required";

    /// <summary>The refusal under the Heading box for text that is no heading from 001 to 360.</summary>
    public const string InvalidHeading = "Not a valid heading";

    /// <summary>The refusal under the Speed box for a speed that is not a multiple of 5 knots.</summary>
    public const string SpeedSteps = "Speed in 5-knot steps";

    /// <summary>The Cross fix entry <paramref name="label"/> names: one menu row holding the Cross fix flyout.</summary>
    public static MenuItem BuildCross(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(entry, new CrossFixFlyoutHost(), context, host, (choose, _) => new CrossFixFlyoutPanel(aircraft, choose));
        return entry;
    }

    /// <summary>The Depart fix entry <paramref name="label"/> names: one menu row holding the Depart fix flyout.</summary>
    public static MenuItem BuildDepart(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(entry, new DepartFixFlyoutHost(), context, host, (choose, _) => new DepartFixFlyoutPanel(aircraft, choose));
        return entry;
    }

    /// <summary>The prefix of <paramref name="at"/> on the altitude: none, <c>A</c> or <c>B</c>.</summary>
    public static string Prefix(CrossFixAltitudeType at) =>
        at switch
        {
            CrossFixAltitudeType.At => "",
            CrossFixAltitudeType.AtOrAbove => "A",
            CrossFixAltitudeType.AtOrBelow => "B",
            _ => throw new ArgumentOutOfRangeException(nameof(at), at, "Unknown crossing restriction."),
        };

    /// <summary>The toggle label of <paramref name="at"/>, which is also how the caption reads it, capitalised.</summary>
    public static string Label(CrossFixAltitudeType at) =>
        at switch
        {
            CrossFixAltitudeType.At => "At",
            CrossFixAltitudeType.AtOrAbove => "At or above",
            CrossFixAltitudeType.AtOrBelow => "At or below",
            _ => throw new ArgumentOutOfRangeException(nameof(at), at, "Unknown crossing restriction."),
        };

    /// <summary>
    /// The altitude in feet the Altitude box reads (<see cref="MenuCatalog.ParseAltitudeFeet"/>: under 1000 is hundreds, as the
    /// <c>CFIX</c> parser reads it), at least 100 ft; null for text that is none.
    /// </summary>
    public static int? ParseAltitude(string text) => ((MenuCatalog.ParseAltitudeFeet(text) is { } feet) && (feet >= 100)) ? feet : null;

    /// <summary>The heading the Heading box reads: one to three digits, 001 to 360; null for anything else.</summary>
    public static int? ParseHeading(string text)
    {
        string trimmed = text.Trim();
        if (!((trimmed.Length is >= 1 and <= 3) && trimmed.All(char.IsAsciiDigit)))
        {
            return null;
        }

        int heading = int.Parse(trimmed, CultureInfo.InvariantCulture);
        return (heading is >= 1 and <= 360) ? heading : null;
    }

    /// <summary>
    /// What the Speed box's <paramref name="text"/> holds: no knots and no refusal for a blank box, the knots for a whole positive
    /// multiple of 5, else the refusal (<see cref="SpeedSteps"/> for another whole number, <see cref="SpeedFlyout.InvalidSpeed"/>
    /// for anything else).
    /// </summary>
    public static (int? Knots, string? Refusal) ParseSpeed(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return (null, null);
        }

        if (!trimmed.All(char.IsAsciiDigit) || !int.TryParse(trimmed, CultureInfo.InvariantCulture, out int knots) || (knots <= 0))
        {
            return (null, SpeedFlyout.InvalidSpeed);
        }

        return ((knots % 5) == 0) ? (knots, null) : (null, SpeedSteps);
    }

    /// <summary>The <c>CFIX</c> command: <c>CFIX SPAMY A110 250</c>, the altitude in hundreds, the speed left off when null.</summary>
    public static string CrossCommand(string fix, CrossFixAltitudeType at, int feet, int? knots)
    {
        string hundreds = (feet / 100).ToString("D3", CultureInfo.InvariantCulture);
        string speed = (knots is { } value) ? $" {value.ToString(CultureInfo.InvariantCulture)}" : "";
        return $"CFIX {fix} {Prefix(at)}{hundreds}{speed}";
    }

    /// <summary>The crossing restriction as a controller says it: <c>Cross SPAMY at or above 11,000 at 250 knots</c>.</summary>
    public static string CrossCaption(string fix, CrossFixAltitudeType at, int feet, int? knots)
    {
        string altitude = MenuCatalog.FormatAltitude(feet / 100 * 100);
        string speed = (knots is { } value) ? $" at {value.ToString(CultureInfo.InvariantCulture)} knots" : "";
        return $"Cross {fix} {Label(at).ToLowerInvariant()} {altitude}{speed}";
    }

    /// <summary>The <c>DEPART</c> command: <c>DEPART SPAMY 200</c>, the heading in three digits.</summary>
    public static string DepartCommand(string fix, int heading) => $"DEPART {fix} {heading.ToString("D3", CultureInfo.InvariantCulture)}";

    /// <summary>The departure instruction as a controller says it: <c>Depart SPAMY heading 200</c>.</summary>
    public static string DepartCaption(string fix, int heading) => $"Depart {fix} heading {heading.ToString("D3", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The fix the box names for these verbs: one word, upper-cased, that is a fix's own name in the navigation data (any word
    /// while it is not loaded); null for a blank box, several words, an unknown fix or a fix-radial-distance.
    /// </summary>
    public static string? TypedFix(string text)
    {
        string fix = text.Trim().ToUpperInvariant();
        if ((fix.Length == 0) || fix.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return ((NavigationDatabase.InstanceOrNull is not { } db) || (db.GetFixPosition(fix) is not null)) ? fix : null;
    }
}

/// <summary>The Cross fix flyout's menu row header, which holds the <see cref="CrossFixFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class CrossFixFlyoutHost() : FlyoutPanelHost("cross fix flyout", "cross-fix-flyout-panel");

/// <summary>The Depart fix flyout's menu row header, which holds the <see cref="DepartFixFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class DepartFixFlyoutHost() : FlyoutPanelHost("depart fix flyout", "depart-fix-flyout-panel");

/// <summary>
/// The panel the Cross fix, Depart fix and Hold at fix flyouts share (<see cref="FixValueFlyout"/>, <see cref="HoldFlyout"/>): the
/// title line, the fix box with its refusal, the verb's value boxes, the caption of what Enter sends, a separator, then the fix
/// list (<see cref="FixListView"/>). Each verb's panel adds its boxes and says what command a fix makes with them.
/// </summary>
public abstract class FixValueFlyoutPanel : StackPanel, IFlyoutPanel
{
    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly FixListView _fixes;

    /// <summary>
    /// A panel titled <paramref name="title"/> for <paramref name="verb"/>, its fix box drawn with the glyph of
    /// <paramref name="glyphId"/>.
    /// </summary>
    protected FixValueFlyoutPanel(string title, string verb, string glyphId, IMenuAircraft? aircraft, Func<string, Task> choose)
    {
        Verb = verb;
        _aircraft = aircraft;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Glyph = QuickCommandGlyphs.For(glyphId) ?? throw new InvalidOperationException($"No glyph for {glyphId}.");
        Title = new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
        };
        Box = ValueBox(FixValueFlyout.FixBoxName, FixValueFlyout.FixPlaceholder, "Fix", 190);
        _fixes = new FixListView(
            aircraft,
            Box,
            "Route · next ●",
            new FixListBehaviour(RowCommand, _ => [], row => SendOrAskAsync(row.Fix), InABox, OnBoxEnterAsync)
        );
        AddHandler(KeyDownEvent, _fixes.OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The fix box.</summary>
    public TextBox Box { get; }

    /// <summary>
    /// The command column beside the fix box: what Enter sends, or the verb (and fix) with <c>···</c>, faded, while it cannot
    /// send.
    /// </summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The refusal under the fix box (<see cref="DirectToFlyout.UnknownFix"/>), shown while its text names no fix.</summary>
    public TextBlock BoxError { get; } = Refusal(DirectToFlyout.UnknownFix);

    /// <summary>The caption under the boxes: what Enter sends, as a controller says it; hidden while it cannot send.</summary>
    public TextBlock Caption { get; } =
        new()
        {
            FontSize = 12,
            Margin = new Thickness(11, 2, 11, 2),
            IsVisible = false,
        };

    /// <summary>The fix Enter in a box sends for: the outlined row's, else the typed fix; null while the box names none.</summary>
    public string? BoxFix => OutlinedRow?.Fix ?? FixValueFlyout.TypedFix(Box.Text ?? "");

    /// <summary>The command Enter in a box sends now; null while there is no fix or a value is missing.</summary>
    public string? BoxCommand => (BoxFix is { } fix) ? CommandFor(fix) : null;

    /// <summary>The route's rows as the flyout opened, in route order; they never move while it stays open.</summary>
    public IReadOnlyList<FixRow> RouteRows => _fixes.RouteRows;

    /// <summary>The rows the fix box's text adds: every other matching fix, nearest first.</summary>
    public IReadOnlyList<FixRow> MatchRows => _fixes.MatchRows;

    /// <summary>The row Enter in a box sends, outlined: the first shown row while the fix box holds text; null otherwise.</summary>
    public FixRow? OutlinedRow => _fixes.OutlinedRow;

    /// <summary>
    /// The panel as text, one line per part: the title line, the fix box with its command, its refusal while shown, the value
    /// parts, the caption while shown, <c>---</c>, then each shown group header and row of the list.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            List<string> lines = [string.IsNullOrEmpty(_titleNow.Text) ? Title.Text ?? "" : $"{Title.Text} · {_titleNow.Text}"];
            lines.Add($"Fix — {BoxCommandText.Text}");
            if (BoxError.IsVisible)
            {
                lines.Add(BoxError.Text ?? "");
            }

            lines.AddRange(ValueLines());
            if (Caption.IsVisible)
            {
                lines.Add(Caption.Text ?? "");
            }

            lines.Add("---");
            lines.AddRange(_fixes.Lines);
            return lines;
        }
    }

    /// <summary>The verb the panel sends: <c>CFIX</c>, <c>DEPART</c> or <c>HOLDP</c>.</summary>
    protected string Verb { get; }

    /// <summary>Whether the panel shows the red state of a required box left empty, which a send that found it missing turns on.</summary>
    protected bool RequiredShown { get; private set; }

    /// <summary>The glyph the fix box row draws.</summary>
    protected QuickCommandGlyph Glyph { get; }

    /// <summary>The title line's left part.</summary>
    protected TextBlock Title { get; }

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Reset();

    void IFlyoutPanel.Refresh() => Refresh();

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    /// <summary>
    /// Puts the flyout back as it opens: the boxes empty, the toggles on their opening state, the red required state off, the
    /// route rows read afresh. The route is read only here, never on a refresh.
    /// </summary>
    public void Reset()
    {
        _fixes.Reset((_aircraft is null) ? [] : _aircraft.RouteFixNames());
        RequiredShown = false;
        ResetValues();
        Box.Text = "";
        OnTyped();
    }

    /// <summary>
    /// Follows the aircraft, in place: the title's next fix, each row's distance and bearing, the ● and the fixes passed since
    /// the flyout opened (<see cref="FixListView.Refresh"/>), then whatever the verb's panel follows (<see cref="OnRefreshed"/>).
    /// </summary>
    public void Refresh()
    {
        string next = _aircraft?.NavigatingTo ?? "";
        _titleNow.Text = (next.Length > 0) ? $"→ {next}" : "";
        _fixes.Refresh();
        OnRefreshed();
    }

    /// <summary>What the verb's panel follows on each refresh after the list; nothing by default.</summary>
    protected virtual void OnRefreshed() { }

    /// <summary>The command <paramref name="fix"/> makes with the boxes' values now; null while a value is missing or invalid.</summary>
    public abstract string? CommandFor(string fix);

    /// <summary>
    /// Lays the panel out once the verb's boxes exist: the title line, the fix box, its refusal, <paramref name="valueParts"/>,
    /// the caption, a separator and the list; then shows it as it opens.
    /// </summary>
    protected void Compose(IEnumerable<Control> valueParts)
    {
        Children.Add(FlyoutPanelParts.TitleLine(Title, _titleNow));
        Children.Add(FlyoutPanelParts.BoxRow(Glyph, "Fix", Box, BoxCommandText));
        Children.Add(BoxError);
        Children.AddRange(valueParts);
        Children.Add(Caption);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Children.Add(_fixes.View);
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTyped();
            }
        };
        foreach (TextBox box in ValueBoxes())
        {
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    ShowValues();
                }
            };
        }

        Reset();
        Refresh();
    }

    /// <summary>
    /// A box named <paramref name="name"/> reading <paramref name="placeholder"/>, <paramref name="width"/> wide, that keeps its
    /// clicks inside the panel.
    /// </summary>
    protected static TextBox ValueBox(string name, string placeholder, string automationName, double width)
    {
        var box = new TextBox
        {
            Name = name,
            PlaceholderText = placeholder,
            Width = width,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(box, automationName);
        TakeoffFlyout.KeepClicksInside(box);
        return box;
    }

    /// <summary>A red refusal line under a box reading <paramref name="text"/>, hidden.</summary>
    protected static TextBlock Refusal(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = FlyoutPanelParts.RefusalBrush,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    /// <summary>The verb's value boxes, top to bottom.</summary>
    protected abstract IReadOnlyList<TextBox> ValueBoxes();

    /// <summary>The verb's value parts as text lines, for <see cref="Lines"/>.</summary>
    protected abstract IEnumerable<string> ValueLines();

    /// <summary>What Enter would send for <paramref name="fix"/>, as a controller says it, when the values make a command.</summary>
    protected abstract string? CaptionFor(string fix);

    /// <summary>The first value box the command still needs: empty and required, or holding no value; null when none.</summary>
    protected abstract TextBox? FirstMissingBox();

    /// <summary>Empties the value boxes and puts the toggles on their opening state.</summary>
    protected abstract void ResetValues();

    /// <summary>Shows each value box's refusal for its text now (the required one only while <see cref="RequiredShown"/>).</summary>
    protected abstract void ShowRefusals();

    /// <summary>Shows every command and the caption anew after a value or toggle changed.</summary>
    protected void ShowValues()
    {
        ShowRefusals();
        _fixes.UpdateCommands();
        string? fix = BoxFix;
        string? command = BoxCommand;
        BoxCommandText.Text = command ?? ((fix is null) ? $"{Verb} {TakeoffFlyout.Unfilled}" : Pending(fix));
        BoxCommandText.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        BoxError.IsVisible = (fix is null) && !string.IsNullOrWhiteSpace(Box.Text);
        MenuCommandText.SetCommand(Box, command);
        string? caption = ((fix is not null) && (command is not null)) ? CaptionFor(fix) : null;
        Caption.Text = caption ?? "";
        Caption.IsVisible = caption is not null;
    }

    private string Pending(string fix) => $"{Verb} {fix} {TakeoffFlyout.Unfilled}";

    private (string? Sent, string Shown) RowCommand(string fix)
    {
        string? command = CommandFor(fix);
        return (command, command ?? Pending(fix));
    }

    private bool InABox(KeyEventArgs e) => ValueBoxes().Prepend(Box).Any(box => ReferenceEquals(e.Source, box) || box.IsKeyboardFocusWithin);

    /// <summary>
    /// Sends <paramref name="fix"/>'s command when the values make one; otherwise puts the fix in the fix box, turns on the red
    /// required state and focuses the box the command still needs.
    /// </summary>
    private async Task SendOrAskAsync(string fix)
    {
        if (CommandFor(fix) is { } command)
        {
            await _choose(command);
            return;
        }

        if (!string.Equals(BoxFix, fix, StringComparison.Ordinal))
        {
            Box.Text = fix;
        }

        RequiredShown = true;
        ShowValues();
        FirstMissingBox()?.Focus(NavigationMethod.Directional);
    }

    /// <summary>Enter in a box: sends the box's command, else asks for the missing value, else (no fix named) focuses the fix box.</summary>
    private async Task OnBoxEnterAsync()
    {
        if (BoxFix is { } fix)
        {
            await SendOrAskAsync(fix);
            return;
        }

        Box.Focus(NavigationMethod.Directional);
    }

    /// <summary>Shows what the fix box now reads in the list (<see cref="FixListView.Filter"/>), then every command and the caption.</summary>
    private void OnTyped()
    {
        _fixes.Filter();
        ShowValues();
    }
}

/// <summary>
/// The Cross fix flyout's panel (<see cref="FixValueFlyout"/>): under the fix box the Cross at toggle (opening on At), the
/// Altitude box and the optional Speed box.
/// </summary>
public sealed class CrossFixFlyoutPanel : FixValueFlyoutPanel
{
    private readonly List<ToggleButton> _toggles = [];

    internal CrossFixFlyoutPanel(IMenuAircraft? aircraft, Func<string, Task> choose)
        : base("Cross fix", "CFIX", MenuIds.ProceduresCrossFix, aircraft, choose)
    {
        foreach (CrossFixAltitudeType at in Enum.GetValues<CrossFixAltitudeType>())
        {
            _toggles.Add(
                FlyoutPanelParts.VerbToggle(FixValueFlyout.Label(at), FixValueFlyout.Prefix(at), at == CrossFixAltitudeType.At, () => SetAt(at))
            );
        }

        AltitudeBox = ValueBox("CrossFixAltitudeBox", AltitudeFlyout.BoxPlaceholder, "Altitude", 150);
        SpeedBox = ValueBox("CrossFixSpeedBox", SpeedFlyout.BoxPlaceholder, "Speed", 110);
        var atRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        atRow.Children.Add(
            new TextBlock
            {
                Text = "Cross at",
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        atRow.Children.Add(FlyoutPanelParts.ToggleStrip(_toggles));
        QuickCommandGlyph altitudeGlyph =
            QuickCommandGlyphs.For(MenuIds.AltitudeMaintain) ?? throw new InvalidOperationException("No altitude glyph.");
        QuickCommandGlyph speedGlyph = QuickCommandGlyphs.For(MenuIds.SpeedAssign) ?? throw new InvalidOperationException("No speed glyph.");
        Compose([
            atRow,
            FlyoutPanelParts.BoxRow(altitudeGlyph, "Altitude", AltitudeBox, new TextBlock()),
            AltitudeError,
            FlyoutPanelParts.BoxRow(speedGlyph, "Speed (knots), optional", SpeedBox, new TextBlock()),
            SpeedError,
        ]);
    }

    /// <summary>How the restriction holds the altitude now, which the toggle sets.</summary>
    public CrossFixAltitudeType At { get; private set; }

    /// <summary>The Altitude box: hundreds or feet, as the altitude flyout reads it.</summary>
    public TextBox AltitudeBox { get; }

    /// <summary>The optional Speed box: knots in 5-knot steps.</summary>
    public TextBox SpeedBox { get; }

    /// <summary>The Altitude box's refusal: <see cref="FixValueFlyout.AltitudeRequired"/> or <see cref="AltitudeFlyout.InvalidAltitude"/>.</summary>
    public TextBlock AltitudeError { get; } = Refusal(FixValueFlyout.AltitudeRequired);

    /// <summary>The Speed box's refusal: <see cref="FixValueFlyout.SpeedSteps"/> or <see cref="SpeedFlyout.InvalidSpeed"/>.</summary>
    public TextBlock SpeedError { get; } = Refusal(FixValueFlyout.SpeedSteps);

    /// <summary>The toggle button of <paramref name="at"/>.</summary>
    public ToggleButton Toggle(CrossFixAltitudeType at) => _toggles[(int)at];

    /// <inheritdoc />
    public override string? CommandFor(string fix) => (Values() is (var feet, var knots)) ? FixValueFlyout.CrossCommand(fix, At, feet, knots) : null;

    /// <inheritdoc />
    protected override IReadOnlyList<TextBox> ValueBoxes() => [AltitudeBox, SpeedBox];

    /// <inheritdoc />
    protected override IEnumerable<string> ValueLines()
    {
        yield return "Cross at " + string.Join(" | ", Enum.GetValues<CrossFixAltitudeType>().Select(ToggleText));
        yield return $"Altitude [{AltitudeBox.Text}]";
        if (AltitudeError.IsVisible)
        {
            yield return AltitudeError.Text ?? "";
        }

        yield return $"Speed [{SpeedBox.Text}]";
        if (SpeedError.IsVisible)
        {
            yield return SpeedError.Text ?? "";
        }
    }

    /// <inheritdoc />
    protected override string? CaptionFor(string fix) =>
        (Values() is (var feet, var knots)) ? FixValueFlyout.CrossCaption(fix, At, feet, knots) : null;

    /// <inheritdoc />
    protected override TextBox? FirstMissingBox()
    {
        if (FixValueFlyout.ParseAltitude(AltitudeBox.Text ?? "") is null)
        {
            return AltitudeBox;
        }

        return (FixValueFlyout.ParseSpeed(SpeedBox.Text ?? "").Refusal is null) ? null : SpeedBox;
    }

    /// <inheritdoc />
    protected override void ResetValues()
    {
        AltitudeBox.Text = "";
        SpeedBox.Text = "";
        SetAt(CrossFixAltitudeType.At);
    }

    /// <inheritdoc />
    protected override void ShowRefusals()
    {
        string altitude = AltitudeBox.Text ?? "";
        bool blank = string.IsNullOrWhiteSpace(altitude);
        AltitudeError.Text = blank ? FixValueFlyout.AltitudeRequired : AltitudeFlyout.InvalidAltitude;
        AltitudeError.IsVisible = blank ? RequiredShown : (FixValueFlyout.ParseAltitude(altitude) is null);
        string? speedRefusal = FixValueFlyout.ParseSpeed(SpeedBox.Text ?? "").Refusal;
        SpeedError.Text = speedRefusal ?? "";
        SpeedError.IsVisible = speedRefusal is not null;
    }

    /// <summary>The altitude and the speed the boxes hold, or null while the altitude is missing or either box holds no value.</summary>
    private (int Feet, int? Knots)? Values()
    {
        (int? knots, string? refusal) = FixValueFlyout.ParseSpeed(SpeedBox.Text ?? "");
        return ((FixValueFlyout.ParseAltitude(AltitudeBox.Text ?? "") is { } feet) && (refusal is null)) ? (feet, knots) : null;
    }

    private string ToggleText(CrossFixAltitudeType at)
    {
        string text = FixValueFlyout.Label(at);
        return (at == At) ? $"[{text}]" : text;
    }

    private void SetAt(CrossFixAltitudeType at)
    {
        At = at;
        foreach (CrossFixAltitudeType each in Enum.GetValues<CrossFixAltitudeType>())
        {
            Toggle(each).IsChecked = (each == at);
        }

        ShowValues();
    }
}

/// <summary>The Depart fix flyout's panel (<see cref="FixValueFlyout"/>): under the fix box the Heading box.</summary>
public sealed class DepartFixFlyoutPanel : FixValueFlyoutPanel
{
    internal DepartFixFlyoutPanel(IMenuAircraft? aircraft, Func<string, Task> choose)
        : base("Depart fix", "DEPART", MenuIds.ProceduresDepartFix, aircraft, choose)
    {
        HeadingBox = ValueBox("DepartFixHeadingBox", "e.g. 200", "Heading", 110);
        QuickCommandGlyph headingGlyph = QuickCommandGlyphs.For(MenuIds.HeadingFly) ?? throw new InvalidOperationException("No heading glyph.");
        Compose([FlyoutPanelParts.BoxRow(headingGlyph, "Heading after the fix", HeadingBox, new TextBlock()), HeadingError]);
    }

    /// <summary>The Heading box: the magnetic heading after the fix, 001 to 360.</summary>
    public TextBox HeadingBox { get; }

    /// <summary>The Heading box's refusal: <see cref="FixValueFlyout.HeadingRequired"/> or <see cref="FixValueFlyout.InvalidHeading"/>.</summary>
    public TextBlock HeadingError { get; } = Refusal(FixValueFlyout.HeadingRequired);

    /// <inheritdoc />
    public override string? CommandFor(string fix) =>
        (FixValueFlyout.ParseHeading(HeadingBox.Text ?? "") is { } heading) ? FixValueFlyout.DepartCommand(fix, heading) : null;

    /// <inheritdoc />
    protected override IReadOnlyList<TextBox> ValueBoxes() => [HeadingBox];

    /// <inheritdoc />
    protected override IEnumerable<string> ValueLines()
    {
        yield return $"Heading [{HeadingBox.Text}]";
        if (HeadingError.IsVisible)
        {
            yield return HeadingError.Text ?? "";
        }
    }

    /// <inheritdoc />
    protected override string? CaptionFor(string fix) =>
        (FixValueFlyout.ParseHeading(HeadingBox.Text ?? "") is { } heading) ? FixValueFlyout.DepartCaption(fix, heading) : null;

    /// <inheritdoc />
    protected override TextBox? FirstMissingBox() => (FixValueFlyout.ParseHeading(HeadingBox.Text ?? "") is null) ? HeadingBox : null;

    /// <inheritdoc />
    protected override void ResetValues() => HeadingBox.Text = "";

    /// <inheritdoc />
    protected override void ShowRefusals()
    {
        string heading = HeadingBox.Text ?? "";
        bool blank = string.IsNullOrWhiteSpace(heading);
        HeadingError.Text = blank ? FixValueFlyout.HeadingRequired : FixValueFlyout.InvalidHeading;
        HeadingError.IsVisible = blank ? RequiredShown : (FixValueFlyout.ParseHeading(heading) is null);
    }
}
