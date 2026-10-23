using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Situation;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Report when… flyout: the turn rows only in a pattern circuit, the Mile final row only with an assigned runway, its chips
/// and checked miles box, the checked fix box, the reporting points (nearby for VFR, the route's fixes for IFR) with distance and
/// magnetic bearing, and the Stop section listing exactly what is armed.
/// </summary>
public class ReportWhenFlyoutTests
{
    private const string Callsign = "N123AB";
    private const string Initials = "AB";

    /// <summary>Oakland, where the declination is about 13° east.</summary>
    private static readonly LatLon Oakland = new(37.7213, -122.2207);

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.EnterFinalOnly);

    /// <summary>A VFR aircraft on the downwind of a circuit to runway 28R at Oakland, nothing armed.</summary>
    private static FakeMenuAircraft PatternAircraft() =>
        new()
        {
            FlightRules = "VFR",
            CurrentPhase = "Downwind",
            PhaseSequence = "Downwind > Base > Final > Landing",
            AssignedRunway = "28R",
            Position = Oakland,
        };

    /// <summary>Eight fixes the navigation data places, every one a report fix name: a route longer than the flyout lists.</summary>
    private static readonly string[] RouteFixes = ["SUNOL", "MOD", "ALTAM", "VPCOL", "VPMOR", "VPCBT", "VPBEL", "VPBDS"];

    /// <summary>A host placing <see cref="RouteFixes"/> and suggesting no fix.</summary>
    private static RecordingMenuHost Host() => Host([]);

    /// <summary>
    /// A host that places SUNOL, MOD and six more fixes where the navigation data does, and answers every partial fix name with
    /// <paramref name="suggestions"/>.
    /// </summary>
    private static RecordingMenuHost Host(IReadOnlyList<string> suggestions)
    {
        TestVnasData.EnsureInitialized();
        var host = new RecordingMenuHost("") { FixSuggestions = suggestions };
        foreach (string fix in RouteFixes)
        {
            (double lat, double lon) =
                NavigationDatabase.Instance.GetFixPosition(fix) ?? throw new InvalidOperationException($"The navigation data does not place {fix}.");
            host.FixPositions[fix] = new LatLon(lat, lon);
        }

        return host;
    }

    private static MenuItem Flyout(IMenuAircraft? aircraft, RecordingMenuHost host) =>
        Assert.IsType<MenuItem>(MenuCatalog.Get(MenuIds.ApproachReportWhen).Build(aircraft, Context(), host));

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void PressEnter(Control box) => box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    /// <summary>The flyout's items as lines: a row's one-line text, <c>---</c> for a separator.</summary>
    private static List<string> Lines(MenuItem flyout) =>
        [.. flyout.Items.Select(item => (item is Separator) ? "---" : ((MenuItem)item!).Header?.ToString() ?? "")];

    /// <summary>The flyout's send rows (turn, Stop and reporting-point rows), in order, with their items.</summary>
    private static List<(ReportWhenRow Row, MenuItem Item)> SendRows(MenuItem flyout) =>
        [.. flyout.Items.OfType<MenuItem>().Where(item => item.Header is ReportWhenRow).Select(item => ((ReportWhenRow)item.Header!, item))];

    private static MenuItem SendRow(MenuItem flyout, string label) => Assert.Single(SendRows(flyout), pair => pair.Row.Label == label).Item;

    private static InlineBoxRow? BoxRow(MenuItem flyout, string boxName) =>
        flyout.Items.OfType<MenuItem>().Select(item => item.Header).OfType<InlineBoxRow>().SingleOrDefault(row => row.Input.Name == boxName);

    private static InlineBoxRow MilesRow(MenuItem flyout) => Assert.IsType<InlineBoxRow>(BoxRow(flyout, ReportWhenFlyout.MilesBoxName));

    private static InlineBoxRow FixRow(MenuItem flyout) => Assert.IsType<InlineBoxRow>(BoxRow(flyout, ReportWhenFlyout.FixBoxName));

    /// <summary>The Mile final row's chip reading <paramref name="miles"/>.</summary>
    private static Button Chip(InlineBoxRow milesRow, int miles) =>
        Assert.Single(
            milesRow.GetLogicalDescendants().OfType<Button>(),
            chip => (chip.Content as string) == miles.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );

    private static string[] TurnLabels => ["Turning crosswind", "Turning downwind", "Midfield downwind", "Turning base", "Turning final"];

    /// <summary>In a pattern circuit the five arm rows show, in circuit order, under their group line, and each sends its leg.</summary>
    [AvaloniaFact]
    public void InPattern_TheFiveArmRowsShow_AndSendTheirCommands()
    {
        RecordingMenuHost host = Host();
        MenuItem flyout = Flyout(PatternAircraft(), host);

        Assert.Contains(ReportWhenFlyout.TurningGroup, Lines(flyout));
        Assert.Equal(TurnLabels, SendRows(flyout).Select(pair => pair.Row.Label));
        foreach (string label in TurnLabels)
        {
            Click(SendRow(flyout, label));
        }

        Assert.Equal(
            ["REPORT CROSSWIND", "REPORT DOWNWIND", "REPORT MIDFIELD", "REPORT BASE", "REPORT FINAL"],
            host.Sent.Select(sent => sent.Command)
        );
        Assert.All(host.Sent, sent => Assert.Equal((Callsign, Initials), (sent.Callsign, sent.Initials)));
    }

    /// <summary>Out of a pattern circuit (a straight-in final) no turn row shows, the sim would refuse each.</summary>
    [AvaloniaFact]
    public void OutOfPattern_NoArmRowShows()
    {
        MenuItem flyout = Flyout(PatternAircraft() with { CurrentPhase = "Final", PhaseSequence = "Final > Landing" }, Host());

        Assert.DoesNotContain(ReportWhenFlyout.TurningGroup, Lines(flyout));
        Assert.DoesNotContain(SendRows(flyout), pair => TurnLabels.Contains(pair.Row.Label));
        Assert.NotNull(BoxRow(flyout, ReportWhenFlyout.FixBoxName));
    }

    /// <summary>Without an assigned runway the Mile final row is hidden; the fix box stays.</summary>
    [AvaloniaFact]
    public void NoRunway_HidesMileFinal()
    {
        MenuItem flyout = Flyout(PatternAircraft() with { AssignedRunway = "" }, Host());

        Assert.Null(BoxRow(flyout, ReportWhenFlyout.MilesBoxName));
        Assert.NotNull(BoxRow(flyout, ReportWhenFlyout.FixBoxName));
    }

    /// <summary>The title line names the flyout, the aircraft and its runway.</summary>
    [AvaloniaFact]
    public void TitleLine_NamesTheAircraftAndRunway()
    {
        Assert.Contains("Report when… · N123AB · RWY 28R", Lines(Flyout(PatternAircraft(), Host())));
        Assert.Contains("Report when… · N123AB", Lines(Flyout(PatternAircraft() with { AssignedRunway = "" }, Host())));
    }

    /// <summary>Each chip sends its mile final at once.</summary>
    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void Chip_SendsItsMileFinal(int miles)
    {
        RecordingMenuHost host = Host();
        Button chip = Chip(MilesRow(Flyout(PatternAircraft(), host)), miles);

        chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([(Callsign, $"REPORT {miles} FINAL", Initials)], host.Sent);
    }

    /// <summary>The miles box refuses a decimal, zero and a word inline, and Enter sends nothing.</summary>
    [AvaloniaTheory]
    [InlineData("5.5", ReportWhenFlyout.WholeMilesOnly)]
    [InlineData("0", ReportWhenFlyout.OneMileOrMore)]
    [InlineData("x", ReportWhenFlyout.OneMileOrMore)]
    public void MilesBox_RefusesInline(string typed, string reason)
    {
        RecordingMenuHost host = Host();
        InlineBoxRow row = MilesRow(Flyout(PatternAircraft(), host));

        row.Text = typed;
        PressEnter(row.Input);

        Assert.True(row.IsRefused);
        Assert.Equal(reason, row.Refusal.ReasonLine.Text);
        Assert.Equal(InlineBoxRow.CantSend, row.CommandColumn.Text);
        Assert.Empty(host.Sent);
    }

    /// <summary>Whole miles read back as the pilot's answer, and Enter sends the mile final.</summary>
    [AvaloniaFact]
    public void MilesBox_WholeMiles_EnterSends()
    {
        RecordingMenuHost host = Host();
        InlineBoxRow row = MilesRow(Flyout(PatternAircraft(), host));

        row.Text = "7";
        Assert.Equal("Pilot: will report 7-mile final", row.NoteLine.Text);
        PressEnter(row.Input);

        Assert.Equal([(Callsign, "REPORT 7 FINAL", Initials)], host.Sent);
    }

    /// <summary>A blank miles box sends nothing.</summary>
    [AvaloniaFact]
    public void MilesBox_Blank_SendsNothing()
    {
        RecordingMenuHost host = Host();
        InlineBoxRow row = MilesRow(Flyout(PatternAircraft(), host));

        PressEnter(row.Input);

        Assert.False(row.IsRefused);
        Assert.Empty(host.Sent);
    }

    /// <summary>A click on a box row focuses its box and keeps the menu open: it never sends.</summary>
    [AvaloniaFact]
    public void BoxRow_Click_StaysOpenAndSendsNothing()
    {
        RecordingMenuHost host = Host();
        MenuItem flyout = Flyout(PatternAircraft(), host);
        InlineBoxRow row = MilesRow(flyout);
        row.Text = "7";

        Click(row.Item);

        Assert.True(row.Item.StaysOpenOnClick);
        Assert.Empty(host.Sent);
    }

    /// <summary>The fix box refuses a word that is no fix name, and a fix name the navigation data does not know.</summary>
    [AvaloniaTheory]
    [InlineData("OFF", ReportWhenFlyout.NotAFixName)]
    [InlineData("ZZZZZ", ReportWhenFlyout.UnknownFix)]
    public void FixBox_RefusesInline(string typed, string reason)
    {
        RecordingMenuHost host = Host();
        InlineBoxRow row = FixRow(Flyout(PatternAircraft(), host));

        row.Text = typed;
        PressEnter(row.Input);

        Assert.True(row.IsRefused);
        Assert.Equal(reason, row.Refusal.ReasonLine.Text);
        Assert.Empty(host.Sent);
    }

    /// <summary>A known fix, typed in any case, reads back as the pilot's answer and Enter sends it upper-cased.</summary>
    [AvaloniaFact]
    public void FixBox_KnownFix_EnterSendsItUpperCased()
    {
        RecordingMenuHost host = Host();
        InlineBoxRow row = FixRow(Flyout(PatternAircraft(), host));

        row.Text = "sunol";
        Assert.Equal("Pilot: will report passing SUNOL", row.NoteLine.Text);
        PressEnter(row.Input);

        Assert.Equal([(Callsign, "REPORT SUNOL", Initials)], host.Sent);
    }

    /// <summary>The fix box is a fix box whose suggestions are the host's.</summary>
    [AvaloniaFact]
    public async Task FixBox_SuggestsTheHostsFixes()
    {
        RecordingMenuHost host = Host(["SUNOL"]);
        TakeoffFixBox box = Assert.IsType<TakeoffFixBox>(FixRow(Flyout(PatternAircraft(), host)).Input);

        Assert.NotNull(box.AsyncPopulator);
        IEnumerable<object> suggested = await box.AsyncPopulator("SU", CancellationToken.None);

        Assert.Equal(["SUNOL"], suggested.Cast<string>());
        Assert.Contains("SU", host.FixSuggestionRequests);
    }

    /// <summary>
    /// A partial name the host still suggests fixes for waits, neither read nor refused, as Warp's FRD box does; the whole name
    /// reads and sends.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("S")]
    [InlineData("SUN")]
    public void FixBox_PartialNameWithSuggestions_Waits(string typed)
    {
        RecordingMenuHost host = Host(["SUNOL"]);
        InlineBoxRow row = FixRow(Flyout(PatternAircraft(), host));

        row.Text = typed;
        PressEnter(row.Input);

        Assert.False(row.IsRefused);
        Assert.Null(row.Command);
        Assert.Empty(host.Sent);

        row.Text = "SUNOL";
        PressEnter(row.Input);

        Assert.Equal([(Callsign, "REPORT SUNOL", Initials)], host.Sent);
    }

    /// <summary>A VFR aircraft lists the host's nearby reporting points, nearest first, and a point's row sends it.</summary>
    [AvaloniaFact]
    public void Vfr_ListsTheNearbyPoints_AndARowSendsIt()
    {
        RecordingMenuHost host = Host();
        host.NearbyReportingPoints.Add(new NearbyReportingPoint("VPCOL", 0.8, 0.0));
        host.NearbyReportingPoints.Add(new NearbyReportingPoint("VPMOR", 4.1, 10.0));
        MenuItem flyout = Flyout(PatternAircraft() with { NavigationRoute = ["SUNOL"] }, host);

        List<string> lines = Lines(flyout);
        Assert.Contains(ReportWhenFlyout.NearbyGroup, lines);
        Assert.DoesNotContain(ReportWhenFlyout.RouteGroup, lines);
        Assert.Equal(
            ["VPCOL", "VPMOR"],
            SendRows(flyout).Select(pair => pair.Row.Label).Where(label => label.StartsWith("VP", StringComparison.Ordinal))
        );
        Assert.DoesNotContain(SendRows(flyout), pair => pair.Row.Label == "SUNOL");

        Click(SendRow(flyout, "VPMOR"));

        Assert.Equal([(Callsign, "REPORT VPMOR", Initials)], host.Sent);
    }

    /// <summary>
    /// An IFR aircraft lists its route's fixes in route order, leaving out one the host does not place, under its own group line,
    /// and no nearby point; a route fix's row sends it.
    /// </summary>
    [AvaloniaFact]
    public void Ifr_ListsTheRouteFixesInOrder_AndARowSendsIt()
    {
        RecordingMenuHost host = Host();
        host.NearbyReportingPoints.Add(new NearbyReportingPoint("VPCOL", 0.8, 0.0));
        FakeMenuAircraft aircraft = PatternAircraft() with { FlightRules = "IFR", NavigationRoute = ["SUNOL", "ZZZZZ", "MOD"] };
        MenuItem flyout = Flyout(aircraft, host);

        List<string> lines = Lines(flyout);
        Assert.Contains(ReportWhenFlyout.RouteGroup, lines);
        Assert.DoesNotContain(ReportWhenFlyout.NearbyGroup, lines);
        Assert.Equal(["SUNOL", "MOD"], SendRows(flyout).Select(pair => pair.Row.Label).Where(label => !TurnLabels.Contains(label)));

        Click(SendRow(flyout, "SUNOL"));

        Assert.Equal([(Callsign, "REPORT SUNOL", Initials)], host.Sent);
    }

    /// <summary>The route list starts at the fix the aircraft is navigating to: the fixes behind it are left out.</summary>
    [AvaloniaFact]
    public void Ifr_RouteListStartsAtTheNextFix()
    {
        FakeMenuAircraft aircraft = PatternAircraft() with { FlightRules = "IFR", NavigationRoute = ["SUNOL", "MOD"], NavigatingTo = "MOD" };

        MenuItem flyout = Flyout(aircraft, Host());

        Assert.Equal(["MOD"], SendRows(flyout).Select(pair => pair.Row.Label).Where(label => !TurnLabels.Contains(label)));
    }

    /// <summary>A route longer than six fixes lists its first six.</summary>
    [AvaloniaFact]
    public void Ifr_ListsAtMostSixRouteFixes()
    {
        MenuItem flyout = Flyout(PatternAircraft() with { FlightRules = "IFR", NavigationRoute = RouteFixes }, Host());

        Assert.Equal(RouteFixes.Take(6), SendRows(flyout).Select(pair => pair.Row.Label).Where(label => !TurnLabels.Contains(label)));
    }

    /// <summary>An armed fix is marked in the IFR route list too.</summary>
    [AvaloniaFact]
    public void Ifr_ArmedFixIsMarkedInTheRouteList()
    {
        FakeMenuAircraft aircraft = PatternAircraft() with
        {
            FlightRules = "IFR",
            NavigationRoute = ["SUNOL", "MOD"],
            ArmedReports = new ArmedReportsDto(ArmedReportLegs.None, null, "MOD"),
        };

        MenuItem flyout = Flyout(aircraft, Host());

        Assert.Equal(["MOD"], SendRows(flyout).Where(pair => pair.Row.IsArmed).Select(pair => pair.Row.Label));
    }

    /// <summary>A chip click sends its mile final and closes the menu.</summary>
    [AvaloniaFact]
    public void Chip_Click_ClosesTheMenu()
    {
        RecordingMenuHost host = Host();
        MenuItem flyout = Flyout(PatternAircraft(), host);
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        var menu = new ContextMenu { Items = { flyout } };
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            flyout.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);

            Chip(MilesRow(flyout), 5).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([(Callsign, "REPORT 5 FINAL", Initials)], host.Sent);
            Assert.False(menu.IsOpen, "A chip click should close the menu.");
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>An IFR aircraft with no route fix left lists nothing, and its fix box stays.</summary>
    [AvaloniaFact]
    public void Ifr_NoRouteFix_ShowsNoList()
    {
        RecordingMenuHost host = Host();
        host.NearbyReportingPoints.Add(new NearbyReportingPoint("VPCOL", 0.8, 0.0));
        MenuItem flyout = Flyout(PatternAircraft() with { FlightRules = "IFR", NavigationRoute = ["ZZZZZ"] }, host);

        List<string> lines = Lines(flyout);
        Assert.DoesNotContain(ReportWhenFlyout.RouteGroup, lines);
        Assert.DoesNotContain(ReportWhenFlyout.NearbyGroup, lines);
        Assert.NotNull(BoxRow(flyout, ReportWhenFlyout.FixBoxName));
    }

    /// <summary>
    /// A point's note is its distance and its bearing from the aircraft, magnetic: at Oakland, about 13° east declination, a point
    /// due true north reads 347°.
    /// </summary>
    [AvaloniaFact]
    public void PointNote_IsDistanceAndMagneticBearing()
    {
        RecordingMenuHost host = Host();
        host.NearbyReportingPoints.Add(new NearbyReportingPoint("VPCOL", 0.84, 0.0));
        MenuItem flyout = Flyout(PatternAircraft(), host);

        Assert.Equal("0.8 NM · 347°", Assert.Single(SendRows(flyout), pair => pair.Row.Label == "VPCOL").Row.Note);
    }

    /// <summary>
    /// With Turning base, a 5-mile final and SUNOL armed, the Stop section comes first and lists exactly those and All reports,
    /// each sending its single cancel; the base row carries the armed badge and chip 5 is marked.
    /// </summary>
    [AvaloniaFact]
    public void Armed_StopSectionFirst_ListsExactlyWhatIsArmed()
    {
        RecordingMenuHost host = Host();
        FakeMenuAircraft aircraft = PatternAircraft() with { ArmedReports = new ArmedReportsDto(ArmedReportLegs.Base, 5, "SUNOL") };
        MenuItem flyout = Flyout(aircraft, host);

        Assert.Equal(
            [
                ReportWhenFlyout.StopGroup,
                "Turning base — REPORT OFF BASE",
                "5-mile final — REPORT OFF 5 FINAL",
                "Passing SUNOL — REPORT OFF SUNOL",
                "All reports — REPORT OFF",
                "---",
                "Report when… · N123AB · RWY 28R",
            ],
            Lines(flyout).Take(7)
        );
        foreach (MenuItem stop in flyout.Items.OfType<MenuItem>().Skip(1).Take(4))
        {
            Click(stop);
        }

        Assert.Equal(["REPORT OFF BASE", "REPORT OFF 5 FINAL", "REPORT OFF SUNOL", "REPORT OFF"], host.Sent.Select(sent => sent.Command));
        Assert.Equal(["Turning base"], SendRows(flyout).Skip(4).Where(pair => pair.Row.IsArmed).Select(pair => pair.Row.Label));
        InlineBoxRow miles = MilesRow(flyout);
        Assert.Contains(ReportWhenFlyout.ArmedClass, Chip(miles, 5).Classes);
        Assert.DoesNotContain(ReportWhenFlyout.ArmedClass, Chip(miles, 3).Classes);
    }

    /// <summary>An armed midfield report's Stop row sends the midfield cancel.</summary>
    [AvaloniaFact]
    public void ArmedMidfield_StopRowSendsReportOffMidfield()
    {
        RecordingMenuHost host = Host();
        MenuItem flyout = Flyout(PatternAircraft() with { ArmedReports = new ArmedReportsDto(ArmedReportLegs.Midfield, null, null) }, host);

        Click(Assert.IsType<MenuItem>(flyout.Items[1]));

        Assert.Equal("Midfield downwind — REPORT OFF MIDFIELD", Lines(flyout)[1]);
        Assert.Equal([(Callsign, "REPORT OFF MIDFIELD", Initials)], host.Sent);
    }

    /// <summary>An armed mile final that is no chip's value is named in the Mile final row's note.</summary>
    [AvaloniaFact]
    public void ArmedMilesOffTheChips_MileFinalNoteNamesIt()
    {
        MenuItem flyout = Flyout(PatternAircraft() with { ArmedReports = new ArmedReportsDto(ArmedReportLegs.None, 7, null) }, Host());

        InlineBoxRow miles = MilesRow(flyout);
        Assert.Contains("armed: 7-mile final", miles.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(miles.GetLogicalDescendants().OfType<Button>(), chip => chip.Classes.Contains(ReportWhenFlyout.ArmedClass));
    }

    /// <summary>A new fix report replaces the armed one, and the fix box says so; the armed fix's point row is marked.</summary>
    [AvaloniaFact]
    public void ArmedFix_FixRowSaysANewOneReplacesIt_AndThePointIsMarked()
    {
        RecordingMenuHost host = Host();
        host.NearbyReportingPoints.Add(new NearbyReportingPoint("VPCOL", 0.8, 0.0));
        host.NearbyReportingPoints.Add(new NearbyReportingPoint("VPMOR", 4.1, 10.0));
        MenuItem flyout = Flyout(PatternAircraft() with { ArmedReports = new ArmedReportsDto(ArmedReportLegs.None, null, "VPMOR") }, host);

        Assert.Contains("replaces passing VPMOR", FixRow(flyout).ToString(), StringComparison.Ordinal);
        Assert.Equal(["VPMOR"], SendRows(flyout).Where(pair => pair.Row.IsArmed).Select(pair => pair.Row.Label));
    }

    /// <summary>With nothing armed there is no Stop section: the title line comes first.</summary>
    [AvaloniaFact]
    public void NothingArmed_NoStopSection()
    {
        List<string> lines = Lines(Flyout(PatternAircraft(), Host()));

        Assert.DoesNotContain(ReportWhenFlyout.StopGroup, lines);
        Assert.Equal("Report when… · N123AB · RWY 28R", lines[0]);
    }

    /// <summary>
    /// Opened from the quick-command strip, Report when… focuses the miles box with a runway assigned and the fix box without one;
    /// At N-mile final… focuses the miles box, and At fix… the fix box.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.ApproachReportWhen, "28R", ReportWhenFlyout.MilesBoxName)]
    [InlineData(MenuIds.ApproachReportWhen, "", ReportWhenFlyout.FixBoxName)]
    [InlineData(MenuIds.ApproachReportNMileFinal, "28R", ReportWhenFlyout.MilesBoxName)]
    [InlineData(MenuIds.ApproachReportAtFix, "28R", ReportWhenFlyout.FixBoxName)]
    public void StripButton_OpensTheFlyoutWithFocusInItsBox(string id, string runway, string focusedBox)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        MenuItem built = Assert.IsType<MenuItem>(entry.Build(PatternAircraft() with { AssignedRunway = runway }, Context(), Host()));
        InlineBoxRow row = Assert.IsType<InlineBoxRow>(BoxRow(built, focusedBox));
        var menu = new ContextMenu();
        MenuItem strip = Assert.IsType<MenuItem>(
            QuickCommandStrip.FromBuilt(menu, [(new QuickCommandStripItem(entry, QuickCommandGlyphs.For(id)!), built)])
        );
        menu.Items.Add(strip);
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        try
        {
            menu.Open(window);
            Dispatcher.UIThread.RunJobs();
            menu.UpdateLayout();
            Button button = Assert.Single(QuickCommandStrip.Buttons(strip));
            QuickCommandStrip.OpenSubmenu(button);
            Dispatcher.UIThread.RunJobs();
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
            Assert.IsAssignableFrom<Control>(flyout.Popup.Child).UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(row.Input.IsKeyboardFocusWithin, $"{focusedBox} did not take focus when the strip opened {id}'s flyout");
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    /// <summary>The Report when… button and both alias buttons carry the armed dot while a report is armed, and none without.</summary>
    [AvaloniaTheory]
    [InlineData(MenuIds.ApproachReportWhen)]
    [InlineData(MenuIds.ApproachReportNMileFinal)]
    [InlineData(MenuIds.ApproachReportAtFix)]
    public void StripButton_ShowsTheArmedDot_OnlyWhileAReportIsArmed(string id)
    {
        Assert.True(QuickCommandStrip.ShowsArmedDot(StripButton(id, new ArmedReportsDto(ArmedReportLegs.None, null, "SUNOL"))));
        Assert.False(QuickCommandStrip.ShowsArmedDot(StripButton(id, null)));
    }

    /// <summary>The strip button for <paramref name="id"/> over a pattern aircraft with <paramref name="armed"/> reports armed.</summary>
    private static Button StripButton(string id, ArmedReportsDto? armed)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(id);
        MenuItem built = Assert.IsType<MenuItem>(entry.Build(PatternAircraft() with { ArmedReports = armed }, Context(), Host()));
        MenuItem strip = Assert.IsType<MenuItem>(
            QuickCommandStrip.FromBuilt(new ContextMenu(), [(new QuickCommandStripItem(entry, QuickCommandGlyphs.For(id)!), built)])
        );
        return Assert.Single(QuickCommandStrip.Buttons(strip));
    }

    /// <summary>The VFR arrival and departure defaults offer Report when… in place of the two aliases.</summary>
    [AvaloniaTheory]
    [InlineData(AircraftSituation.VfrArrivalInbound)]
    [InlineData(AircraftSituation.VfrDeparting)]
    public void Defaults_OfferReportWhen_NotTheAliases(AircraftSituation situation)
    {
        List<string> ids = [.. QuickCommandDefaults.For(situation).OfType<CatalogQuickCommandEntry>().Select(entry => entry.CatalogId)];

        Assert.Contains(MenuIds.ApproachReportWhen, ids);
        Assert.DoesNotContain(MenuIds.ApproachReportNMileFinal, ids);
        Assert.DoesNotContain(MenuIds.ApproachReportAtFix, ids);
    }

    /// <summary>All Commands › Pilot reports shows one Report when… flyout item, with no nested submenu left in it.</summary>
    [AvaloniaFact]
    public void PilotReports_ShowsOneReportWhenFlyout_WithNoNestedSubmenu()
    {
        MenuItem pilotReports = SharedMenuGroups.PilotReports(
            PatternAircraft() with
            {
                ArmedReports = new ArmedReportsDto(ArmedReportLegs.Base, null, null),
            },
            Context(),
            Host(),
            hidesFlightCommands: false
        )!;

        MenuItem reportWhen = Assert.Single(pilotReports.Items.OfType<MenuItem>(), item => (item.Header as string) == "Report when…");
        Assert.DoesNotContain(reportWhen.Items.OfType<MenuItem>(), item => item.Items.Count > 0);
        Assert.Contains(reportWhen.Items.OfType<MenuItem>(), item => item.Header is ReportWhenTitleLine);
        Assert.DoesNotContain(pilotReports.Items.OfType<MenuItem>(), item => (item.Header as string) == "Stop reporting");
    }
}
