using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>The box a Cleared for takeoff flyout row carries for its argument.</summary>
internal enum TakeoffRowInput
{
    /// <summary>No box: the row sends a fixed command.</summary>
    None,

    /// <summary>A heading box (<c>270</c>), sent as three digits.</summary>
    Heading,

    /// <summary>A fix box that suggests fixes as the command bar does.</summary>
    Fix,
}

/// <summary>
/// The Cleared for takeoff flyout's one initial-altitude box, at its top: the text as typed, which every departure row that
/// takes an altitude appends to its command when it is not blank. It starts from a filed <c>VFR/NNN</c> altitude.
/// </summary>
/// <param name="text">The text the box starts with: the filed VFR cruise in hundreds (<c>045</c>), else empty.</param>
internal sealed class TakeoffAltitudeBox(string text)
{
    /// <summary>What the box is labelled.</summary>
    public const string Label = "Initial altitude";

    /// <summary>What the empty box reads: a row then sends no altitude and the aircraft climbs as it would by default.</summary>
    public const string Placeholder = "default climb";

    private string _text = text;

    /// <summary>The glyph leading the box.</summary>
    public QuickCommandGlyph Glyph => TakeoffFlyout.AltitudeGlyph;

    /// <summary>The box's text as typed; setting it raises <see cref="Changed"/> when it differs.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The altitude a row appends: the text trimmed, which the sim's altitude reader takes; empty for none.</summary>
    public string Token => Text.Trim();

    /// <summary>
    /// Whether the box can be sent: empty, or text the sim's altitude reader takes (<see cref="AltitudeResolver.Resolve"/>:
    /// <c>045</c>, <c>4500</c>, <c>KOAK+010</c>). While it cannot, every row that takes an altitude cannot send.
    /// </summary>
    public bool IsReadable => (Token.Length == 0) || (AltitudeResolver.Resolve(Token) is not null);

    /// <summary>Raised when <see cref="Text"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The box as one line: <c>Initial altitude · 045</c>, or the placeholder while it is empty.</summary>
    public override string ToString() => $"{Label} · {((Token.Length > 0) ? Token : Placeholder)}";
}

/// <summary>
/// One Cleared for takeoff flyout row, drawn by <see cref="TakeoffFlyoutRowTemplate"/>: its glyph, its label, a heading or
/// fix box when it takes one, and the command it sends now, which follows the box and the shared altitude box as they are
/// typed in. A row that cannot send (a box empty or unreadable) shows the command with <c>···</c> in place of the argument,
/// is drawn faded and sends nothing when clicked.
/// </summary>
internal sealed class TakeoffFlyoutRow
{
    private string _inputText = "";

    /// <summary>The glyph leading the row, turned to the departure runway when it is drawn off one.</summary>
    public required QuickCommandGlyph Glyph { get; init; }

    /// <summary>What the row names: <c>Straight out</c>.</summary>
    public required string Label { get; init; }

    /// <summary>The box the row carries, if any.</summary>
    public required TakeoffRowInput Input { get; init; }

    /// <summary>The command for the row's box text, without the altitude; null when the text cannot be sent.</summary>
    public required Func<string, string?> CommandFor { get; init; }

    /// <summary>What the command column shows while the row cannot send: <c>CTO TLDCT ···</c>.</summary>
    public required string Placeholder { get; init; }

    /// <summary>The altitude box whose text the command appends; null for a row that takes no altitude.</summary>
    public required TakeoffAltitudeBox? Altitude { get; init; }

    /// <summary>The fixes a fix box offers for a partial name (<see cref="IMenuHost.SuggestFixes"/>).</summary>
    public required Func<string, IReadOnlyList<string>> FixSuggestions { get; init; }

    /// <summary>The row's box text as typed; setting it raises <see cref="Changed"/> when it differs.</summary>
    public string InputText
    {
        get => _inputText;
        set
        {
            if (_inputText == value)
            {
                return;
            }

            _inputText = value;
            RaiseChanged();
        }
    }

    /// <summary>
    /// The command the row sends now, the altitude appended when the row takes one and the box holds one; null when it
    /// cannot send: its own box is empty or unreadable, or it takes the altitude and the altitude box is unreadable.
    /// </summary>
    public string? Command
    {
        get
        {
            if ((CommandFor(InputText) is not { } command) || (Altitude is { IsReadable: false }))
            {
                return null;
            }

            return (Altitude is { Token.Length: > 0 } altitude) ? $"{command} {altitude.Token}" : command;
        }
    }

    /// <summary>Whether the row sends a command now.</summary>
    public bool CanSend => Command is not null;

    /// <summary>
    /// The command column's text: the command; while the altitude box is unreadable, the row's command with <c>···</c>
    /// for the altitude; else the placeholder.
    /// </summary>
    public string ShownCommand
    {
        get
        {
            if (Command is { } command)
            {
                return command;
            }

            return ((Altitude is { IsReadable: false }) && (CommandFor(InputText) is { } withoutAltitude))
                ? $"{withoutAltitude} {TakeoffFlyout.Unfilled}"
                : Placeholder;
        }
    }

    /// <summary>Raised when the row's command may have changed: its box or the altitude box was typed in.</summary>
    public event EventHandler? Changed;

    /// <summary>Raises <see cref="Changed"/>.</summary>
    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The row as one line, <c>Label — Command</c>.</summary>
    public override string ToString() => $"{Label} — {ShownCommand}";
}

/// <summary>
/// The rows of the Cleared for takeoff flyout, which the quick-command strip and All Commands › Tower share. A VFR departure
/// gets the initial-altitude box, then: Cleared for takeoff (<c>CTO</c>); Make left / right closed traffic
/// (<c>CTO MLT</c> / <c>MRT</c>); Straight out (<c>CTO MSO</c>); Left / right crosswind departure (<c>MLC</c> /
/// <c>MRC</c>); Left / right downwind departure (<c>MLD</c> / <c>MRD</c>); Left / right turnout (45°) (<c>ML45</c> /
/// <c>MR45</c>); Fly heading (<c>CTO {hdg}</c>); On course (<c>CTO OC</c>, disabled without a destination other than the
/// departure field); Turn left / right direct (<c>CTO TLDCT</c> / <c>TRDCT {fix}</c>). Every one of those but bare CTO and
/// closed traffic appends the altitude box's text. An IFR departure gets Cleared for takeoff, Fly heading and Fly runway
/// heading (<c>CTO RH</c>), none taking the altitude; under "VFR commands for IFR aircraft: All" a separator follows, then
/// the altitude box and the VFR rows it lacks. An altitude box the sim cannot read stops every row that takes it from
/// sending. Every glyph is drawn in the pattern purple; the ones drawn off the runway (departing
/// west) are turned by the departure end's true course less 270°.
/// </summary>
internal static class TakeoffFlyout
{
    /// <summary>The name of the altitude box's text box.</summary>
    public const string AltitudeBoxName = "TakeoffAltitudeBox";

    /// <summary>The name of the heading row's text box.</summary>
    public const string HeadingBoxName = "TakeoffHeadingBox";

    /// <summary>What a command shows in place of an argument its box cannot give yet.</summary>
    internal const string Unfilled = "···";
    private const string RunwayPath = "M9 11h10v2H9z";
    private const string StraightOutPath = RunwayPath + " M9 12H2.5 M2.5 12l2.2-2.2 M2.5 12l2.2 2.2";
    private const string OnCoursePath = RunwayPath + " M9 12H6L3.9 6.7 M3 2.4a1.8 1.8 0 1 0 0 3.6a1.8 1.8 0 1 0 0-3.6";
    private const string LeftClosedPath = RunwayPath + " M9 12H4V18.5H21.5V12H19 M12 16.3l2.2 2.2-2.2 2.2";
    private const string RightClosedPath = RunwayPath + " M9 12H4V5.5H21.5V12H19 M12 3.3l2.2 2.2-2.2 2.2";
    private const string LeftCrosswindPath = RunwayPath + " M9 12H4.5V20.5 M4.5 20.5l-2.2-2.2 M4.5 20.5l2.2-2.2";
    private const string RightCrosswindPath = RunwayPath + " M9 12H4.5V3.5 M4.5 3.5l-2.2 2.2 M4.5 3.5l2.2 2.2";
    private const string LeftDownwindPath = RunwayPath + " M9 12H4V18.5H21 M21 18.5l-2.2-2.2 M21 18.5l-2.2 2.2";
    private const string RightDownwindPath = RunwayPath + " M9 12H4V5.5H21 M21 5.5l-2.2-2.2 M21 5.5l-2.2 2.2";
    private const string LeftTurnoutPath = RunwayPath + " M9 12H6.5L3 15.5 M3 15.5h3 M3 15.5v-3";
    private const string RightTurnoutPath = RunwayPath + " M9 12H6.5L3 8.5 M3 8.5h3 M3 8.5v3";
    private const string LeftDirectPath =
        RunwayPath + " M9 12H6.5A3.5 3.5 0 0 0 3 15.5A3.5 3.5 0 0 0 6.5 19H10.5 M10.5 19l-2-2 M10.5 19l-2 2 M13.5 16.6l2.3 4h-4.6z";
    private const string RightDirectPath =
        RunwayPath + " M9 12H6.5A3.5 3.5 0 0 1 3 8.5A3.5 3.5 0 0 1 6.5 5H10.5 M10.5 5l-2-2 M10.5 5l-2 2 M13.5 7.4l2.3-4h-4.6z";

    /// <summary>The altitude box's glyph, upright.</summary>
    internal static readonly QuickCommandGlyph AltitudeGlyph = Upright("M3 18l7-7 4 4 7-7 M15 8h6v6");

    private static readonly QuickCommandGlyph TakeoffGlyph = Upright("M2 20h20 M5 16L19 6 M13 6h6v6");
    private static readonly QuickCommandGlyph HeadingGlyph = Upright("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7l3 8-3-2-3 2z");

    /// <summary>The flyout's items for <paramref name="aircraft"/>, in order, without the trailing separator and Custom….</summary>
    internal static IReadOnlyList<Control> Items(IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        bool isVfr = AircraftCommandApplicability.IsVfr(aircraft);
        bool showVfrRows = AircraftCommandApplicability.ShowVfrTakeoffModifiers(aircraft, context.VfrCommandsForIfr);
        TakeoffAltitudeBox? altitude = showVfrRows ? new TakeoffAltitudeBox(Prefill(aircraft)) : null;
        var rows = new RowMaker(DepartureTurn(aircraft), altitude, context, host);
        List<Control> items = [];
        if (!isVfr)
        {
            items.Add(rows.Fixed("Cleared for takeoff", TakeoffGlyph, "CTO", false));
            items.Add(rows.Heading(false));
            items.Add(rows.Fixed("Fly runway heading", rows.Turned(StraightOutPath), "CTO RH", false));
        }

        if (altitude is not null)
        {
            if (!isVfr)
            {
                items.Add(new Separator());
            }

            items.Add(AltitudeItem(altitude));
            items.AddRange(VfrRows(rows, aircraft, isVfr));
        }

        return items;
    }

    /// <summary>The VFR rows in their ruled order; <paramref name="withSharedRows"/> false leaves out the two the IFR set already has.</summary>
    private static List<Control> VfrRows(RowMaker rows, IMenuAircraft? aircraft, bool withSharedRows)
    {
        List<Control> items = [];
        if (withSharedRows)
        {
            items.Add(rows.Fixed("Cleared for takeoff", TakeoffGlyph, "CTO", false));
        }

        items.Add(rows.Fixed("Make left closed traffic", rows.Turned(LeftClosedPath), "CTO MLT", false));
        items.Add(rows.Fixed("Make right closed traffic", rows.Turned(RightClosedPath), "CTO MRT", false));
        items.Add(rows.Fixed("Straight out", rows.Turned(StraightOutPath), "CTO MSO", true));
        items.Add(rows.Fixed("Left crosswind departure", rows.Turned(LeftCrosswindPath), "CTO MLC", true));
        items.Add(rows.Fixed("Right crosswind departure", rows.Turned(RightCrosswindPath), "CTO MRC", true));
        items.Add(rows.Fixed("Left downwind departure", rows.Turned(LeftDownwindPath), "CTO MLD", true));
        items.Add(rows.Fixed("Right downwind departure", rows.Turned(RightDownwindPath), "CTO MRD", true));
        items.Add(rows.Fixed("Left turnout (45°)", rows.Turned(LeftTurnoutPath), "CTO ML45", true));
        items.Add(rows.Fixed("Right turnout (45°)", rows.Turned(RightTurnoutPath), "CTO MR45", true));
        if (withSharedRows)
        {
            items.Add(rows.Heading(true));
        }

        items.Add(rows.OnCourse(rows.Turned(OnCoursePath), HasOnCourse(aircraft)));
        items.Add(rows.Direct("Turn left direct", rows.Turned(LeftDirectPath), "TLDCT"));
        items.Add(rows.Direct("Turn right direct", rows.Turned(RightDirectPath), "TRDCT"));
        return items;
    }

    /// <summary>Whether On course has somewhere to go: a filed destination that is not the departure field.</summary>
    private static bool HasOnCourse(IMenuAircraft? aircraft) =>
        (aircraft is { Destination.Length: > 0 })
        && !string.Equals(aircraft.Destination.Trim(), aircraft.Departure.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The altitude box's starting text: a filed <c>VFR/NNN</c> cruise in the command bar's hundreds (<c>045</c>), else empty.</summary>
    private static string Prefill(IMenuAircraft? aircraft) =>
        (aircraft?.FiledVfrCruiseFeet is { } feet) ? (feet / 100).ToString("D3", CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// How far a departs-west glyph turns for <paramref name="aircraft"/>'s departure end: the held runway, else the
    /// assigned one, at the departure airport (else the ground layout's), its true course less 270°; 0 when either is
    /// unknown or the navigation data does not resolve it, which draws the glyph departing west.
    /// </summary>
    private static double DepartureTurn(IMenuAircraft? aircraft)
    {
        if ((aircraft is null) || (HoldShortMenuHelper.HeldRunway(aircraft) is not { Length: > 0 } runway))
        {
            return 0;
        }

        string airport = (aircraft.Departure.Length > 0) ? aircraft.Departure : aircraft.GroundAirportId ?? "";
        if ((airport.Length == 0) || (NavigationDatabase.InstanceOrNull is not { } navigation))
        {
            return 0;
        }

        return (navigation.GetRunway(airport, RunwayIdentifier.NormalizeDesignator(runway)) is { } end) ? end.TrueHeading.Degrees - 270 : 0;
    }

    /// <summary>The altitude box's item: it stays open when clicked and sends nothing, Enter in its box included.</summary>
    private static MenuItem AltitudeItem(TakeoffAltitudeBox box)
    {
        var item = new MenuItem
        {
            Header = box,
            HeaderTemplate = TakeoffAltitudeTemplate.Instance,
            StaysOpenOnClick = true,
        };
        AutomationProperties.SetName(item, box.ToString());
        box.Changed += (_, _) => AutomationProperties.SetName(item, box.ToString());
        item.Click += (_, e) => e.Handled = true;
        HandFocusToTheBox(item);
        return item;
    }

    /// <summary>The command for a heading box's text: a whole heading 1 to 360, sent as three digits; null otherwise.</summary>
    private static string? HeadingCommand(string text) =>
        (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int heading) && (heading is >= 1 and <= 360))
            ? $"CTO {heading:D3}"
            : null;

    /// <summary>The command for a fix box's text: one name, upper-cased, after <paramref name="verb"/>; null for a blank or spaced one.</summary>
    private static string? DirectCommand(string verb, string text)
    {
        string fix = text.Trim().ToUpperInvariant();
        return ((fix.Length > 0) && !fix.Any(char.IsWhiteSpace)) ? $"CTO {verb} {fix}" : null;
    }

    private static QuickCommandGlyph Upright(string path) => new(path, QuickCommandGlyphFamily.Pattern, 0, false);

    /// <summary>Builds the flyout's row items for one aircraft: its departure turn, its altitude box and its send path.</summary>
    private sealed class RowMaker(double turn, TakeoffAltitudeBox? altitude, MenuContext context, IMenuHost host)
    {
        /// <summary>A departs-west glyph turned to the departure runway.</summary>
        public QuickCommandGlyph Turned(string path) => new(path, QuickCommandGlyphFamily.Pattern, turn, false);

        public MenuItem Fixed(string label, QuickCommandGlyph glyph, string command, bool takesAltitude) =>
            Item(
                new TakeoffFlyoutRow
                {
                    Glyph = glyph,
                    Label = label,
                    Input = TakeoffRowInput.None,
                    CommandFor = _ => command,
                    Placeholder = command,
                    Altitude = takesAltitude ? altitude : null,
                    FixSuggestions = host.SuggestFixes,
                },
                true
            );

        public MenuItem Heading(bool takesAltitude) =>
            Item(
                new TakeoffFlyoutRow
                {
                    Glyph = HeadingGlyph,
                    Label = "Fly heading",
                    Input = TakeoffRowInput.Heading,
                    CommandFor = HeadingCommand,
                    Placeholder = $"CTO {Unfilled}",
                    Altitude = takesAltitude ? altitude : null,
                    FixSuggestions = host.SuggestFixes,
                },
                true
            );

        public MenuItem OnCourse(QuickCommandGlyph glyph, bool available) =>
            Item(
                new TakeoffFlyoutRow
                {
                    Glyph = glyph,
                    Label = "On course",
                    Input = TakeoffRowInput.None,
                    CommandFor = _ => available ? "CTO OC" : null,
                    Placeholder = "CTO OC",
                    Altitude = altitude,
                    FixSuggestions = host.SuggestFixes,
                },
                available
            );

        public MenuItem Direct(string label, QuickCommandGlyph glyph, string verb) =>
            Item(
                new TakeoffFlyoutRow
                {
                    Glyph = glyph,
                    Label = label,
                    Input = TakeoffRowInput.Fix,
                    CommandFor = text => DirectCommand(verb, text),
                    Placeholder = $"CTO {verb} {Unfilled}",
                    Altitude = altitude,
                    FixSuggestions = host.SuggestFixes,
                },
                true
            );

        /// <summary>
        /// The row's menu item: its command, accessible name and stay-open flag follow the row as its boxes are typed in;
        /// clicked (or Enter in its box) it sends the row's command, or, while the row cannot send, nothing, leaving the
        /// menu open and stopping the click there.
        /// </summary>
        private MenuItem Item(TakeoffFlyoutRow row, bool isEnabled)
        {
            var item = new MenuItem
            {
                Header = row,
                HeaderTemplate = TakeoffFlyoutRowTemplate.Instance,
                IsEnabled = isEnabled,
            };

            void Follow()
            {
                MenuCommandText.SetCommand(item, row.Command);
                AutomationProperties.SetName(item, row.ToString());
                item.StaysOpenOnClick = !row.CanSend;
            }

            Follow();
            row.Changed += (_, _) => Follow();
            if (row.Input != TakeoffRowInput.None)
            {
                HandFocusToTheBox(item);
            }

            if (row.Altitude is { } box)
            {
                box.Changed += (_, _) => row.RaiseChanged();
            }

            item.Click += async (_, e) =>
            {
                if (row.Command is not { } command)
                {
                    e.Handled = true;
                    return;
                }

                await host.SendAsync(context.Callsign, command, context.Initials);
            };
            return item;
        }
    }

    /// <summary>
    /// Passes keyboard focus that lands on <paramref name="item"/> itself on to the text box in its header. A menu selects
    /// and focuses the row under the pointer or the arrow keys, and focuses it again once a click in its box is over,
    /// which would leave the box unable to take typing; handed on, the caret goes into the box instead.
    /// </summary>
    private static void HandFocusToTheBox(MenuItem item) =>
        item.AddHandler(
            InputElement.GotFocusEvent,
            (_, e) =>
            {
                if (ReferenceEquals(e.Source, item) && (item.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box))
                {
                    box.Focus(e.NavigationMethod);
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );

    /// <summary>
    /// Keeps a click in a box inside a menu row to the box: the release is marked handled, so the menu does not take it
    /// for a click on the row (which would send it).
    /// </summary>
    internal static void KeepClicksInside(Control box) =>
        box.AddHandler(InputElement.PointerReleasedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble, handledEventsToo: true);
}

/// <summary>
/// The view of a <see cref="TakeoffFlyoutRow"/>: the glyph, the label with the row's heading or fix box under it, and the
/// command right-aligned in the dimmed monospace font, which follows the row as it is typed in. While the row cannot send,
/// its glyph, label and command are drawn faded; the box never is.
/// </summary>
internal sealed class TakeoffFlyoutRowTemplate : FuncDataTemplate<TakeoffFlyoutRow>
{
    private const double BoxWidth = 96;

    /// <summary>The one template every row uses.</summary>
    public static TakeoffFlyoutRowTemplate Instance { get; } = new();

    private TakeoffFlyoutRowTemplate()
        : base((row, _) => Build(row)) { }

    private static Grid Build(TakeoffFlyoutRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Viewbox glyph = QuickCommandStrip.GlyphIcon(row.Glyph);
        glyph.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(glyph);

        var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center };
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { label } };
        if (InputBox(row) is { } box)
        {
            name.Children.Add(box);
        }

        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var command = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);

        void Show()
        {
            double faded = row.CanSend ? 1 : MenuGlyphRowTemplate.DimmedOpacity;
            command.Text = row.ShownCommand;
            command.Opacity = 0.7 * faded;
            glyph.Opacity = faded;
            label.Opacity = faded;
        }

        Show();
        row.Changed += (_, _) => Show();
        return grid;
    }

    /// <summary>The row's heading or fix box, its text written back to the row as typed; null for a row without one.</summary>
    private static Control? InputBox(TakeoffFlyoutRow row)
    {
        Control? box = row.Input switch
        {
            TakeoffRowInput.Heading => HeadingBox(row),
            TakeoffRowInput.Fix => FixBox(row),
            _ => null,
        };
        if (box is not null)
        {
            box.Width = BoxWidth;
            box.Margin = new Thickness(0, 3, 0, 0);
            box.HorizontalAlignment = HorizontalAlignment.Left;
            TakeoffFlyout.KeepClicksInside(box);
        }

        return box;
    }

    private static TextBox HeadingBox(TakeoffFlyoutRow row)
    {
        var box = new TextBox
        {
            Name = TakeoffFlyout.HeadingBoxName,
            Text = row.InputText,
            PlaceholderText = "270",
            MaxLength = 3,
        };
        box.TextChanged += (_, _) => row.InputText = box.Text ?? "";
        return box;
    }

    /// <summary>
    /// A fix box: an autocomplete over the host's fix suggestions (<see cref="TakeoffFlyoutRow.FixSuggestions"/>), from
    /// the first letter, whose Down and Enter keys leave the menu its own (<see cref="TakeoffFixBox"/>).
    /// </summary>
    private static TakeoffFixBox FixBox(TakeoffFlyoutRow row)
    {
        var box = new TakeoffFixBox
        {
            Text = row.InputText,
            PlaceholderText = "fix",
            MinimumPrefixLength = 1,
            FilterMode = AutoCompleteFilterMode.None,
            AsyncPopulator = (text, _) => Task.FromResult<IEnumerable<object>>([.. row.FixSuggestions(text ?? "")]),
        };
        box.TextChanged += (_, _) => row.InputText = box.Text ?? "";
        return box;
    }
}

/// <summary>The view of the <see cref="TakeoffAltitudeBox"/>: the glyph, the label, and the box, its text written back as typed.</summary>
internal sealed class TakeoffAltitudeTemplate : FuncDataTemplate<TakeoffAltitudeBox>
{
    /// <summary>The one template the altitude box uses.</summary>
    public static TakeoffAltitudeTemplate Instance { get; } = new();

    private TakeoffAltitudeTemplate()
        : base((box, _) => Build(box)) { }

    private static StackPanel Build(TakeoffAltitudeBox altitude)
    {
        Viewbox glyph = QuickCommandStrip.GlyphIcon(altitude.Glyph);
        glyph.Margin = new Thickness(0, 0, 8, 0);
        var box = new TextBox
        {
            Name = TakeoffFlyout.AltitudeBoxName,
            Text = altitude.Text,
            PlaceholderText = TakeoffAltitudeBox.Placeholder,
            MaxLength = 5,
            Width = 110,
            Margin = new Thickness(10, 0, 0, 0),
        };
        box.TextChanged += (_, _) => altitude.Text = box.Text ?? "";
        TakeoffFlyout.KeepClicksInside(box);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                glyph,
                new TextBlock { Text = TakeoffAltitudeBox.Label, VerticalAlignment = VerticalAlignment.Center },
                box,
            },
        };
    }
}

/// <summary>
/// The direct rows' fix box: an <see cref="AutoCompleteBox"/>, styled as one, that leaves two keys to the menu around it.
/// Down with the suggestion list closed moves to the next row rather than opening the list (the list opens as the fix is
/// typed). Enter with the list open and no suggestion highlighted, or the highlighted one already the box's text, closes
/// the list and goes on to the row, which sends; Enter on another highlighted suggestion only accepts it.
/// </summary>
internal sealed class TakeoffFixBox : AutoCompleteBox
{
    protected override Type StyleKeyOverride => typeof(AutoCompleteBox);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.Key == Key.Down) && !IsDropDownOpen)
        {
            return;
        }

        if ((e.Key == Key.Enter) && IsDropDownOpen && !HighlightsAnotherFix())
        {
            SetCurrentValue(IsDropDownOpenProperty, false);
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Whether the list has a suggestion highlighted that differs from the box's text.</summary>
    private bool HighlightsAnotherFix() =>
        (SelectionAdapter?.SelectedItem is { } highlighted)
        && !string.Equals(highlighted.ToString(), Text?.Trim(), StringComparison.OrdinalIgnoreCase);
}
