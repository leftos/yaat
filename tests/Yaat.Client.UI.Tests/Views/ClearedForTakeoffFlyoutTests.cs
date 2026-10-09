using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Cleared for takeoff flyout, shared by the quick-command strip and All Commands › Tower: a VFR departure's rows in the
/// ruled order under one initial-altitude box (pre-filled from a filed <c>VFR/NNN</c>), which every departure row that takes
/// an altitude appends and the closed-traffic rows never do; an IFR departure's three rows; both sets under "VFR commands
/// for IFR aircraft: All"; the direct rows' fix boxes and the heading row's box; Custom… last; each runway-drawn glyph purple
/// and turned to the departure runway.
/// </summary>
public class ClearedForTakeoffFlyoutTests
{
    private const string Callsign = "N123AB";
    private const string Initials = "AB";
    private const string Oak = "KOAK";

    private static readonly string[] VfrRows =
    [
        "Cleared for takeoff — CTO",
        "Make left closed traffic — CTO MLT",
        "Make right closed traffic — CTO MRT",
        "Straight out — CTO MSO",
        "Left crosswind departure — CTO MLC",
        "Right crosswind departure — CTO MRC",
        "Left downwind departure — CTO MLD",
        "Right downwind departure — CTO MRD",
        "Left turnout (45°) — CTO ML45",
        "Right turnout (45°) — CTO MR45",
        "Fly heading — CTO ···",
        "On course — CTO OC",
        "Turn left direct — CTO TLDCT ···",
        "Turn right direct — CTO TRDCT ···",
    ];

    private static MenuContext Context(VfrCommandsForIfr mode) => TestMenuContext.Create(Callsign, Initials, null, false, mode);

    private static AircraftModel Departure(string rules, string destination) =>
        new()
        {
            Callsign = Callsign,
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            FlightRules = rules,
            AircraftType = "C172",
            AssignedRunway = "30",
            Departure = Oak,
            Destination = destination,
        };

    private static MenuItem Flyout(AircraftModel aircraft, RecordingMenuHost host, VfrCommandsForIfr mode)
    {
        TestVnasData.EnsureInitialized();
        MenuItem? cto = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff).Build(aircraft, Context(mode), host);
        Assert.NotNull(cto);
        Assert.Equal("Cleared for takeoff 30", cto.Header as string);
        return cto;
    }

    /// <summary>Each item as one line: "---" for a separator, else its header's text.</summary>
    private static List<string> Outline(MenuItem flyout) =>
        [
            .. flyout.Items.Select(item =>
                item switch
                {
                    Separator => "---",
                    MenuItem menuItem => menuItem.Header?.ToString() ?? "",
                    _ => item?.GetType().Name ?? "null",
                }
            ),
        ];

    private static MenuItem RowItem(MenuItem flyout, string label) =>
        flyout.Items.OfType<MenuItem>().Single(item => (item.Header as TakeoffFlyoutRow)?.Label == label);

    private static TakeoffFlyoutRow Row(MenuItem flyout, string label) => Assert.IsType<TakeoffFlyoutRow>(RowItem(flyout, label).Header);

    private static TakeoffAltitudeBox Altitude(MenuItem flyout) =>
        Assert.Single(flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<TakeoffAltitudeBox>());

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static string SendOf(MenuItem flyout, string label)
    {
        var host = (RecordingMenuHost)flyout.Tag!;
        int before = host.Sent.Count;
        Click(RowItem(flyout, label));
        return Assert.Single(host.Sent.Skip(before)).Command;
    }

    private static MenuItem TaggedFlyout(AircraftModel aircraft, VfrCommandsForIfr mode)
    {
        var host = new RecordingMenuHost("");
        MenuItem flyout = Flyout(aircraft, host, mode);
        flyout.Tag = host;
        return flyout;
    }

    [AvaloniaFact]
    public void Vfr_RowsInTheRuledOrder_UnderTheAltitudeBox_CustomLast()
    {
        MenuItem flyout = Flyout(Departure("VFR", "KSFO"), new RecordingMenuHost(""), VfrCommandsForIfr.None);

        Assert.Equal(["Initial altitude · default climb", .. VfrRows, "---", "Custom…"], Outline(flyout));
        Assert.DoesNotContain(Outline(flyout), line => line.Contains("360", StringComparison.Ordinal));
        Assert.IsType<Separator>(flyout.Items[^2]);
        Assert.Equal("Custom…", Assert.IsType<MenuItem>(flyout.Items[^1]).Header as string);
    }

    [AvaloniaFact]
    public void Ifr_OffersTakeoffHeadingAndRunwayHeading_WithoutAnAltitudeBox()
    {
        MenuItem flyout = TaggedFlyout(Departure("IFR", "KSFO"), VfrCommandsForIfr.None);

        Assert.Equal(["Cleared for takeoff — CTO", "Fly heading — CTO ···", "Fly runway heading — CTO RH", "---", "Custom…"], Outline(flyout));
        Assert.False(Row(flyout, "Fly heading").CanSend);

        Row(flyout, "Fly heading").InputText = "270";

        Assert.Equal("CTO", SendOf(flyout, "Cleared for takeoff"));
        Assert.Equal("CTO 270", SendOf(flyout, "Fly heading"));
        Assert.Equal("CTO RH", SendOf(flyout, "Fly runway heading"));
    }

    [AvaloniaTheory]
    [InlineData("90", "CTO 090")]
    [InlineData(" 360 ", "CTO 360")]
    [InlineData("0", null)]
    [InlineData("361", null)]
    [InlineData("27O", null)]
    [InlineData("", null)]
    public void HeadingBox_SendsAThreeDigitHeading_AndDisablesTheRowOtherwise(string typed, string? command)
    {
        MenuItem flyout = Flyout(Departure("IFR", "KSFO"), new RecordingMenuHost(""), VfrCommandsForIfr.None);
        TakeoffFlyoutRow heading = Row(flyout, "Fly heading");

        heading.InputText = typed;

        Assert.Equal(command, heading.Command);
        Assert.Equal(command ?? "CTO ···", heading.ShownCommand);
    }

    /// <summary>
    /// An IFR departure under the full VFR set keeps its three rows, then a separator, the altitude box and the VFR rows it
    /// does not already have; the altitude box reaches only the VFR rows.
    /// </summary>
    [AvaloniaFact]
    public void Ifr_UnderVfrCommandsAll_GetsBothSets()
    {
        MenuItem flyout = TaggedFlyout(Departure("IFR", "KSFO"), VfrCommandsForIfr.All);

        Assert.Equal(
            [
                "Cleared for takeoff — CTO",
                "Fly heading — CTO ···",
                "Fly runway heading — CTO RH",
                "---",
                "Initial altitude · default climb",
                .. VfrRows.Where(row =>
                    !row.StartsWith("Cleared for takeoff", StringComparison.Ordinal) && !row.StartsWith("Fly heading", StringComparison.Ordinal)
                ),
                "---",
                "Custom…",
            ],
            Outline(flyout)
        );

        Altitude(flyout).Text = "045";
        Row(flyout, "Fly heading").InputText = "270";

        Assert.Equal("CTO 270", SendOf(flyout, "Fly heading"));
        Assert.Equal("CTO RH", SendOf(flyout, "Fly runway heading"));
        Assert.Equal("CTO MRC 045", SendOf(flyout, "Right crosswind departure"));
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData(Oak)]
    [InlineData("koak")]
    public void OnCourse_IsDisabled_WithoutADestinationOrBackToTheDepartureField(string destination)
    {
        MenuItem flyout = Flyout(Departure("VFR", destination), new RecordingMenuHost(""), VfrCommandsForIfr.None);

        Assert.False(RowItem(flyout, "On course").IsEnabled);
        Assert.False(Row(flyout, "On course").CanSend);
    }

    [AvaloniaFact]
    public void OnCourse_ToAnotherField_SendsWithTheAltitude()
    {
        MenuItem flyout = TaggedFlyout(Departure("VFR", "KSFO"), VfrCommandsForIfr.None);

        Assert.True(RowItem(flyout, "On course").IsEnabled);
        Assert.Equal("CTO OC", SendOf(flyout, "On course"));
        Altitude(flyout).Text = "4500";
        Assert.Equal("CTO OC 4500", SendOf(flyout, "On course"));
    }

    [AvaloniaFact]
    public void FiledVfr045_PrefillsTheBox_AndStraightOutSendsIt()
    {
        AircraftModel aircraft = Departure("VFR", "KSFO");
        aircraft.CruiseAltitude = 4500;
        Assert.Equal(4500, ((IMenuAircraft)aircraft).FiledVfrCruiseFeet);

        MenuItem flyout = TaggedFlyout(aircraft, VfrCommandsForIfr.None);

        Assert.Equal("045", Altitude(flyout).Text);
        Assert.Equal("Initial altitude · 045", Outline(flyout)[0]);
        Assert.Equal("Straight out — CTO MSO 045", Row(flyout, "Straight out").ToString());
        Assert.Equal("CTO MSO 045", SendOf(flyout, "Straight out"));
    }

    [AvaloniaFact]
    public void FiledIfrOrOnTopAltitude_IsNoVfrCruise()
    {
        AircraftModel ifr = Departure("IFR", "KSFO");
        ifr.CruiseAltitude = 4500;
        AircraftModel onTop = Departure("VFR", "KSFO");
        onTop.CruiseAltitude = 4500;
        onTop.IsVfrOnTop = true;
        AircraftModel none = Departure("VFR", "KSFO");

        Assert.Null(((IMenuAircraft)ifr).FiledVfrCruiseFeet);
        Assert.Null(((IMenuAircraft)onTop).FiledVfrCruiseFeet);
        Assert.Null(((IMenuAircraft)none).FiledVfrCruiseFeet);
        Assert.Equal("", Altitude(Flyout(none, new RecordingMenuHost(""), VfrCommandsForIfr.None)).Text);
    }

    [AvaloniaFact]
    public void ClosedTrafficAndBareTakeoff_NeverCarryTheAltitude()
    {
        MenuItem flyout = TaggedFlyout(Departure("VFR", "KSFO"), VfrCommandsForIfr.None);

        Altitude(flyout).Text = "045";

        Assert.Equal("CTO MLT", SendOf(flyout, "Make left closed traffic"));
        Assert.Equal("CTO MRT", SendOf(flyout, "Make right closed traffic"));
        Assert.Equal("CTO", SendOf(flyout, "Cleared for takeoff"));
        Assert.Equal("CTO ML45 045", SendOf(flyout, "Left turnout (45°)"));
        Assert.Equal("CTO MLD 045", SendOf(flyout, "Left downwind departure"));
        Row(flyout, "Fly heading").InputText = "270";
        Assert.Equal("CTO 270 045", SendOf(flyout, "Fly heading"));
    }

    [AvaloniaFact]
    public void DirectRow_WithAnEmptyFix_IsDisabled_AndSendsTheFixWithTheAltitude()
    {
        MenuItem flyout = TaggedFlyout(Departure("VFR", "KSFO"), VfrCommandsForIfr.None);
        var host = (RecordingMenuHost)flyout.Tag!;
        TakeoffFlyoutRow left = Row(flyout, "Turn left direct");

        Assert.False(left.CanSend);
        Assert.Equal("CTO TLDCT ···", left.ShownCommand);
        Click(RowItem(flyout, "Turn left direct"));
        Assert.Empty(host.Sent);

        left.InputText = " sunol ";
        Assert.Equal("CTO TLDCT SUNOL", SendOf(flyout, "Turn left direct"));

        Altitude(flyout).Text = "045";
        Assert.Equal("CTO TLDCT SUNOL 045", SendOf(flyout, "Turn left direct"));
        Row(flyout, "Turn right direct").InputText = "SUNOL";
        Assert.Equal("CTO TRDCT SUNOL 045", SendOf(flyout, "Turn right direct"));
    }

    [AvaloniaFact]
    public void FixBox_SuggestsTheHostsFixes()
    {
        var host = new RecordingMenuHost("") { FixSuggestions = ["SUNOL", "SUNNY"] };
        MenuItem flyout = Flyout(Departure("VFR", "KSFO"), host, VfrCommandsForIfr.None);

        IReadOnlyList<string> suggested = Row(flyout, "Turn left direct").FixSuggestions("SUN");

        Assert.Equal(["SUNOL", "SUNNY"], suggested);
        Assert.Equal(["SUN"], host.FixSuggestionRequests);
    }

    /// <summary>
    /// Every row glyph is drawn in the pattern purple; the runway-drawn ones turn by the departure end's true course less
    /// 270° (they are drawn departing west), the plain takeoff and heading glyphs stay upright.
    /// </summary>
    [AvaloniaFact]
    public void Glyphs_ArePurple_AndTheRunwayDrawnOnesTurnToTheDepartureRunway()
    {
        MenuItem flyout = Flyout(Departure("IFR", "KSFO"), new RecordingMenuHost(""), VfrCommandsForIfr.All);
        double turn =
            (NavigationDatabase.Instance.GetRunway(Oak, "30") ?? throw new InvalidOperationException("No KOAK 30")).TrueHeading.Degrees - 270;
        List<TakeoffFlyoutRow> rows = [.. flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<TakeoffFlyoutRow>()];

        Assert.All(rows, row => Assert.Equal(QuickCommandGlyphFamily.Pattern, row.Glyph.Family));
        Assert.Equal(QuickCommandGlyphFamily.Pattern, Altitude(flyout).Glyph.Family);
        Assert.Equal(0, Row(flyout, "Cleared for takeoff").Glyph.RotationDegrees);
        Assert.Equal(0, Row(flyout, "Fly heading").Glyph.RotationDegrees);
        foreach (TakeoffFlyoutRow row in rows.Where(row => row.Label is not ("Cleared for takeoff" or "Fly heading")))
        {
            Assert.Equal(turn, row.Glyph.RotationDegrees, 6);
            Assert.False(row.Glyph.MirroredTopToBottom);
        }

        Assert.NotEqual(0, turn);
    }

    /// <summary>
    /// In the strip's flyout, opened over a shown window: a click puts keyboard focus in the altitude box and typing fills
    /// it with the flyout and menu still open and nothing sent; typing a fix and Enter in a direct row's box sends that row
    /// and closes the menu.
    /// </summary>
    [AvaloniaFact]
    public void StripFlyout_BoxesTakeFocusAndTyping_AndEnterInAFixBoxSendsTheRow()
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(Departure("VFR", "KSFO"), host);

        TextBox altitudeBox = AltitudeTextBox(open.Items);
        TypeInto(altitudeBox, "045");

        Assert.True(altitudeBox.IsKeyboardFocusWithin, "the altitude box took no keyboard focus");
        Assert.Equal("045", altitudeBox.Text);
        Assert.True(open.Flyout.IsOpen);
        Assert.True(open.Menu.IsOpen);
        Assert.Empty(host.Sent);

        TextBox fixText = BoxOf(RowItem(open.Items, "Turn left direct"));
        TypeInto(fixText, "SUNOL");
        Assert.True(fixText.IsKeyboardFocusWithin, "the fix box took no keyboard focus");
        Assert.True(open.Menu.IsOpen);
        Assert.Empty(host.Sent);

        Press(open.Window, PhysicalKey.Enter);

        Assert.Equal([(Callsign, "CTO TLDCT SUNOL 045", Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>
    /// An altitude the sim cannot read stops every row that takes the altitude from sending, faded with <c>···</c> for it;
    /// the rows that take none still send. An empty or readable one leaves them all sending.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("4500 ft", false)]
    [InlineData("045", true)]
    [InlineData("", true)]
    public void AltitudeBox_TheSimCannotRead_DisablesEveryRowThatTakesIt(string typed, bool readable)
    {
        MenuItem flyout = TaggedFlyout(Departure("VFR", "KSFO"), VfrCommandsForIfr.None);
        var host = (RecordingMenuHost)flyout.Tag!;
        Altitude(flyout).Text = typed;
        Row(flyout, "Fly heading").InputText = "270";
        Row(flyout, "Turn left direct").InputText = "SUNOL";
        Row(flyout, "Turn right direct").InputText = "SUNOL";

        foreach (string label in AltitudeRows.Select(row => row.Label))
        {
            Assert.Equal(readable, Row(flyout, label).CanSend);
        }

        Assert.All(
            ["Cleared for takeoff", "Make left closed traffic", "Make right closed traffic"],
            label => Assert.True(Row(flyout, label).CanSend)
        );
        if (!readable)
        {
            Assert.Equal("CTO MSO ···", Row(flyout, "Straight out").ShownCommand);
            Assert.Equal("CTO TLDCT SUNOL ···", Row(flyout, "Turn left direct").ShownCommand);
            Click(RowItem(flyout, "Straight out"));
            Assert.Empty(host.Sent);
            Assert.Equal("CTO MLT", SendOf(flyout, "Make left closed traffic"));
        }
    }

    /// <summary>Every row that takes the altitude, the text its own box is typed with (none for a row without one) and what it then sends.</summary>
    public static readonly (string Label, string? Box, string Command)[] AltitudeRows =
    [
        ("Straight out", null, "CTO MSO 4500"),
        ("Left crosswind departure", null, "CTO MLC 4500"),
        ("Right crosswind departure", null, "CTO MRC 4500"),
        ("Left downwind departure", null, "CTO MLD 4500"),
        ("Right downwind departure", null, "CTO MRD 4500"),
        ("Left turnout (45°)", null, "CTO ML45 4500"),
        ("Right turnout (45°)", null, "CTO MR45 4500"),
        ("Fly heading", "270", "CTO 270 4500"),
        ("On course", null, "CTO OC 4500"),
        ("Turn left direct", "SUNOL", "CTO TLDCT SUNOL 4500"),
        ("Turn right direct", "SUNOL", "CTO TRDCT SUNOL 4500"),
    ];

    public static TheoryData<string, string?, string> AltitudeRowData()
    {
        var data = new TheoryData<string, string?, string>();
        foreach ((string label, string? box, string command) in AltitudeRows)
        {
            data.Add(label, box, command);
        }

        return data;
    }

    /// <summary>In the strip's flyout, the altitude typed into its box and the row's own box typed in, a click on the row sends both.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(AltitudeRowData))]
    public void TypedAltitude_ReachesEveryRowThatTakesIt(string label, string? box, string command)
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(Departure("VFR", "KSFO"), host);
        MenuItem row = RowItem(open.Items, label);

        TypeInto(AltitudeTextBox(open.Items), "4500");
        if (box is not null)
        {
            TypeInto(BoxOf(row), box);
        }

        ClickOn(LabelOf(row, label));

        Assert.Equal([(Callsign, command, Initials)], host.Sent);
        Assert.False(open.Menu.IsOpen);
    }

    /// <summary>Down from the top of the flyout walks every row, the boxes and the direct rows' fix boxes included, down to Custom….</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void DownArrow_FromTheTop_ReachesCustom(bool onTheStrip)
    {
        var host = new RecordingMenuHost("");
        AircraftModel aircraft = Departure("VFR", "KSFO");
        (Window window, IReadOnlyList<MenuItem> items) = onTheStrip ? StripItems(OpenStripFlyout(aircraft, host)) : OpenAllCommands(aircraft, host);
        items[0].Focus();
        Dispatcher.UIThread.RunJobs();

        List<string> visited = [FocusedRow(window)];
        for (int press = 0; (press < 30) && (visited[^1] != "Custom…"); press++)
        {
            Press(window, PhysicalKey.ArrowDown);
            visited.Add(FocusedRow(window));
        }

        Assert.Equal("Custom…", visited[^1]);
        Assert.Contains("Turn left direct — CTO TLDCT ···", visited);
        Assert.Contains("Turn right direct — CTO TRDCT ···", visited);
        Assert.Empty(host.Sent);
    }

    /// <summary>With the suggestion list open and none highlighted, one Enter in a fix box sends the row.</summary>
    [AvaloniaFact]
    public void FixBox_EnterWithTheListOpenAndNothingHighlighted_SendsTheRow()
    {
        var host = new RecordingMenuHost("") { FixSuggestions = ["SUNOL", "SUNNE"] };
        StripFlyout open = OpenStripFlyout(Departure("VFR", "KSFO"), host);
        MenuItem row = RowItem(open.Items, "Turn left direct");

        TypeInto(BoxOf(row), "SUNOL");
        Assert.True(FixBoxOf(row).IsDropDownOpen, "the suggestion list did not open");
        Press(open.Window, PhysicalKey.Enter);

        Assert.Equal([(Callsign, "CTO TLDCT SUNOL", Initials)], host.Sent);
    }

    /// <summary>Clicking a suggestion fills the fix box and sends nothing; the flyout and menu stay open.</summary>
    [AvaloniaFact]
    public void FixBox_ClickingASuggestion_FillsTheBox_AndSendsNothing()
    {
        var host = new RecordingMenuHost("") { FixSuggestions = ["SUNOL", "SUNNE"] };
        StripFlyout open = OpenStripFlyout(Departure("VFR", "KSFO"), host);
        MenuItem row = RowItem(open.Items, "Turn left direct");
        TypeInto(BoxOf(row), "SUN");
        Popup list = FixBoxOf(row).GetVisualDescendants().OfType<Popup>().Single();
        Assert.True(list.IsOpen, "the suggestion list did not open");
        Control listChild = Assert.IsAssignableFrom<Control>(list.Child);
        listChild.UpdateLayout();

        ClickOn(listChild.GetVisualDescendants().OfType<ListBoxItem>().First(item => item.Content as string == "SUNOL"));

        Assert.Empty(host.Sent);
        Assert.True(open.Flyout.IsOpen);
        Assert.True(open.Menu.IsOpen);
        Assert.Equal("CTO TLDCT SUNOL", Assert.IsType<TakeoffFlyoutRow>(row.Header).Command);
    }

    /// <summary>A pointer click on a faded row in the strip's flyout sends nothing and leaves the flyout and the menu open.</summary>
    [AvaloniaFact]
    public void StripFlyout_FadedRowClick_KeepsTheFlyoutOpen()
    {
        var host = new RecordingMenuHost("");
        StripFlyout open = OpenStripFlyout(Departure("VFR", "KSFO"), host);
        MenuItem row = RowItem(open.Items, "Turn left direct");

        ClickOn(LabelOf(row, "Turn left direct"));

        Assert.True(row.StaysOpenOnClick);
        Assert.Empty(host.Sent);
        Assert.True(open.Flyout.IsOpen);
        Assert.True(open.Menu.IsOpen);
    }

    /// <summary>The strip's Cleared for takeoff flyout opened over a shown window, and its items as laid out.</summary>
    private sealed record StripFlyout(Window Window, ContextMenu Menu, MenuFlyout Flyout, IReadOnlyList<MenuItem> Items);

    private static (Window Window, IReadOnlyList<MenuItem> Items) StripItems(StripFlyout open) => (open.Window, open.Items);

    private static StripFlyout OpenStripFlyout(AircraftModel aircraft, RecordingMenuHost host)
    {
        TestVnasData.EnsureInitialized();
        MenuCatalogEntry entry = MenuCatalog.Get(MenuIds.TowerClearedForTakeoff);
        MenuItem built = entry.Build(aircraft, Context(VfrCommandsForIfr.None), host) ?? throw new InvalidOperationException("no CTO item");
        var menu = new ContextMenu();
        MenuItem? strip = QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, QuickCommandGlyphs.For(entry.Id)!), built)]);
        Assert.NotNull(strip);
        menu.Items.Add(strip);
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Button button = Assert.Single(QuickCommandStrip.Buttons(strip));
        QuickCommandStrip.OpenSubmenu(button);
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Dispatcher.UIThread.RunJobs();
        Assert.IsAssignableFrom<Control>(flyout.Popup.Child).UpdateLayout();
        return new StripFlyout(window, menu, flyout, [.. flyout.Items.OfType<MenuItem>()]);
    }

    /// <summary>The flyout as All Commands › Tower shows it: the Cleared for takeoff submenu of a context menu, opened and laid out.</summary>
    private static (Window Window, IReadOnlyList<MenuItem> Items) OpenAllCommands(AircraftModel aircraft, RecordingMenuHost host)
    {
        TestVnasData.EnsureInitialized();
        MenuItem cto = Flyout(aircraft, host, VfrCommandsForIfr.None);
        var menu = new ContextMenu { Items = { cto } };
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        cto.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, [.. cto.Items.OfType<MenuItem>()]);
    }

    private static MenuItem RowItem(IReadOnlyList<MenuItem> items, string label) =>
        items.Single(item => (item.Header as TakeoffFlyoutRow)?.Label == label);

    private static TextBox AltitudeTextBox(IReadOnlyList<MenuItem> items) =>
        items.SelectMany(item => item.GetVisualDescendants()).OfType<TextBox>().First(box => box.Name == TakeoffFlyout.AltitudeBoxName);

    /// <summary>The text box a row's heading or fix box types into.</summary>
    private static TextBox BoxOf(MenuItem row) => row.GetVisualDescendants().OfType<TextBox>().First();

    private static AutoCompleteBox FixBoxOf(MenuItem row) => row.GetVisualDescendants().OfType<AutoCompleteBox>().Single();

    private static TextBlock LabelOf(MenuItem row, string label) => row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == label);

    /// <summary>Clicks into <paramref name="box"/> and types <paramref name="text"/> through the window's keyboard.</summary>
    private static void TypeInto(TextBox box, string text)
    {
        ClickOn(box);
        TopLevel.GetTopLevel(box)!.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The one-line text of the menu row holding keyboard focus, or "none".</summary>
    private static string FocusedRow(Window window) =>
        (window.FocusManager?.GetFocusedElement() is Visual focused)
            ? focused.GetSelfAndVisualAncestors().OfType<MenuItem>().FirstOrDefault()?.Header?.ToString() ?? "none"
            : "none";

    /// <summary>Presses and releases the left button over the middle of <paramref name="target"/> through its top level's mouse.</summary>
    private static void ClickOn(Control target)
    {
        TopLevel top = TopLevel.GetTopLevel(target) ?? throw new InvalidOperationException($"{target.GetType().Name} is in no top level");
        Point centre =
            target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top)
            ?? throw new InvalidOperationException("no position");
        top.MouseDown(centre, MouseButton.Left);
        top.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
