using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Push back to… rows (<see cref="PushbackToMenu"/>), one builder for the All Commands submenu and the strip button's
/// flyout: over the real client host at OAK gate 26 with a seed keyed current, their sections, badges, distance hints,
/// facing chips and blocked rows; over a <see cref="RecordingMenuHost"/>, how both surfaces follow a
/// <see cref="PushTargetList"/> that is still filling — the computing and refining rows, the in-place refill that keeps a
/// surviving row, the empty state once the plan lands with nothing — and what a row, a facing chip and a push-anyway chip
/// send.
/// </summary>
public class PushbackToMenuTests(OakPushTargetSeedCopy seedCopy) : IClassFixture<OakPushTargetSeedCopy>
{
    private const string Callsign = ClientMenuHostGroundTests.Callsign;
    private const string PushBackTo = "Push back to…";
    private const string BehindTheAircraft = "Behind the aircraft";
    private const string TaxiSpots = "Taxi spots";
    private const string Refining = "Refining targets…";
    private static readonly string[] Cardinals = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>What <see cref="LandedTargets"/> shows as, in <see cref="Texts"/>'s terms.</summary>
    private static readonly string[] LandedTexts = [PushbackToMenu.TaxiwaysHeader, "PUSH TE", "PUSH W", "-", PushbackToMenu.SpotsHeader, "PUSH $1"];

    /// <summary>
    /// Gate 26's group-III seed holds taxilanes and taxiways only, so the submenu has one section: every row badged TL or
    /// TW, showing its distance hint and command, by planned length.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_KoakGate26_TaxiwaySectionBadgesAndHints()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, _, ClientMenuHost host) = ClientMenuHostGroundTests.AtGate26(seedCopy.NewSeed());

        // The menu built in the same UI-thread job shares this list, so its rows are these targets.
        PushTargetList list = host.GetPushbackTargets(Callsign);
        MenuItem pushTo = Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));

        Assert.Equal([PushbackToMenu.TaxiwaysHeader], SectionHeaders(pushTo));
        List<PushbackToMenu.PushTargetRow> rows = SectionRows(pushTo, PushbackToMenu.TaxiwaysHeader);
        Assert.NotEmpty(rows);
        Assert.Equal(list.Targets.Count, rows.Count);
        Assert.All(rows, row => Assert.Contains(row.Badge, new[] { "TL", "TW" }));
        Assert.All(rows, row => Assert.StartsWith($"{row.Badge} ", row.ToString()));
        Assert.All(rows, row => Assert.Contains($" {PushbackToMenu.DistanceHint(row.Target.PathLengthFt)} {row.Target.Command}", row.ToString()));
        Assert.Equal(rows.Select(r => r.Target.PathLengthFt).Order(), rows.Select(r => r.Target.PathLengthFt));

        await Settle(list);
    }

    /// <summary>
    /// Gate 12's group-III seed holds taxilanes, a taxiway and spot C: the taxilane and taxiway section comes first, a
    /// separator, then the spot section with its S rows.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_KoakGate12_SectionsInOrder()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, _, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());

        PushTargetList list = host.GetPushbackTargets(Callsign);
        MenuItem pushTo = Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));

        Assert.Equal([PushbackToMenu.TaxiwaysHeader, PushbackToMenu.SpotsHeader], SectionHeaders(pushTo));
        List<object?> items = [.. pushTo.Items];
        int spots = items.FindIndex(i => (i is MenuItem m) && ((m.Header as string) == PushbackToMenu.SpotsHeader));
        Assert.IsType<Separator>(items[spots - 1]);
        Assert.All(SectionRows(pushTo, PushbackToMenu.TaxiwaysHeader), row => Assert.Contains(row.Badge, new[] { "TL", "TW" }));
        Assert.Contains(SectionRows(pushTo, PushbackToMenu.SpotsHeader), row => row.Target.Command == "PUSH $C");
        Assert.All(SectionRows(pushTo, PushbackToMenu.SpotsHeader), row => Assert.Equal("S", row.Badge));

        await Settle(list);
    }

    [Fact]
    public void DistanceHint_RoundsToTheNearestFiveFeetWithAThousandsSeparator()
    {
        Assert.Equal("~250 ft", PushbackToMenu.DistanceHint(247.6));
        Assert.Equal("~1,355 ft", PushbackToMenu.DistanceHint(1357.4));
        Assert.Equal("~15 ft", PushbackToMenu.DistanceHint(12.5));
        Assert.Equal("~25 ft", PushbackToMenu.DistanceHint(22.5));
    }

    /// <summary>
    /// Gate 26's TE row carries one chip per facing, two opposite cardinals, each sending <c>PUSH TE FACE</c> it; the row
    /// itself sends the bare push.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_KoakGate26_TaxilaneRowOffersOppositeFacingChips()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, _, ClientMenuHost client) = ClientMenuHostGroundTests.AtGate26(seedCopy.NewSeed());
        var host = new SendCapturingHost(client, "AB");
        PushTargetList list = client.GetPushbackTargets(Callsign);
        MenuItem row = Row(Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => [])), "PUSH TE");

        List<Button> chips = [.. HeaderView(row).GetLogicalDescendants().OfType<Button>()];

        Assert.Equal(2, chips.Count);
        List<string> faced = [.. chips.Select(c => (string)c.Content!)];
        Assert.Equal(4, Math.Abs(Array.IndexOf(Cardinals, faced[0]) - Array.IndexOf(Cardinals, faced[1])));
        Assert.Equal(faced.Select(c => $"PUSH TE FACE {c}"), chips.Select(AutomationProperties.GetName));

        Click(row);
        chips[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, "PUSH TE", "AB"), (Callsign, $"PUSH TE FACE {faced[1]}", "AB")], host.Sent);

        await Settle(list);
    }

    /// <summary>
    /// Gate 26's TE row, blocked by the gate 27 neighbour, stays enabled, and a pointer click on it in the open All Commands
    /// submenu sends nothing and leaves the menu open; it names its blocker and offers only the push-anyway chip, which
    /// sends the forced push.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_KoakGate26_BlockedRow_NamesItsBlockerAndOffersPushAnyway()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        AircraftModel neighbour = ClientMenuHostGroundTests.ParkedAt("27", ClientMenuHostGroundTests.Gate27Neighbour);
        (AircraftModel target, _, ClientMenuHost client) = ClientMenuHostGroundTests.AtGate26(seedCopy.NewSeed(), neighbour);
        var host = new SendCapturingHost(client, "AB");
        PushTargetList list = client.GetPushbackTargets(Callsign);
        ContextMenu menu = OpenInWindow(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));
        MenuItem row = Row(OpenPushBackToSubmenu(menu), "PUSH TE");

        Assert.True(row.IsEnabled);
        var content = (PushbackToMenu.PushTargetRow)row.Header!;
        Assert.EndsWith($" · blocked by {ClientMenuHostGroundTests.Gate27Neighbour}", content.ToString());
        Button chip = Assert.Single(HeaderView(row).GetLogicalDescendants().OfType<Button>());
        Assert.Equal("PUSHF TE", AutomationProperties.GetName(chip));

        ClickOn(LabelOf(row, "TE"));
        Assert.Empty(host.Sent);
        Assert.True(menu.IsOpen);
        chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal([(Callsign, "PUSHF TE", "AB")], host.Sent);

        await Settle(list);
    }

    /// <summary>The sections read "Behind the aircraft" (taxilanes and taxiways) then "Taxi spots", apart by a separator.</summary>
    [AvaloniaFact]
    public void PushbackTo_Sections_AreBehindTheAircraftThenTaxiSpots()
    {
        MenuItem pushTo = Submenu(Build(new RecordingMenuHost("") { PushbackTargets = PushTargetList.Ready(LandedTargets()) }));

        Assert.Equal([BehindTheAircraft, "PUSH TE", "PUSH W", "-", TaxiSpots, "PUSH $1"], Texts(pushTo.Items));
    }

    /// <summary>An aircraft at parking carries Push back to… on its quick-command strip.</summary>
    [AvaloniaFact]
    public void PushbackTo_AtParking_IsOnTheQuickCommandStrip()
    {
        var host = new RecordingMenuHost("") { PushbackTargets = PushTargetList.Pending(null) };

        Assert.Contains(MenuIds.GroundPushbackTo, QuickCommandStrip.Buttons(Strip(Build(host))).Select(b => (string)b.Tag!));
    }

    /// <summary>The strip button's flyout shows the computing row while the list has nothing yet, as the submenu does.</summary>
    [AvaloniaFact]
    public void PushbackTo_StripFlyout_ShowsThePlaceholderWhileComputing()
    {
        var host = new RecordingMenuHost("") { PushbackTargets = PushTargetList.Pending(null) };

        MenuFlyout flyout = OpenStripFlyout(OpenInWindow(Build(host)));

        AssertOnlyRow(flyout.Items, PushbackToMenu.ComputingText);
    }

    /// <summary>
    /// The strip button's flyout follows the list as the submenu does: open when the plan lands, it rebuilds in place with
    /// the rows the submenu shows, and a row chosen from it sends its command and closes the flyout and the menu.
    /// </summary>
    [AvaloniaFact]
    public void PushbackTo_StripFlyout_FillsWhenTheListLandsWhileOpen()
    {
        var list = PushTargetList.Pending(null);
        var host = new RecordingMenuHost("") { PushbackTargets = list };
        ContextMenu menu = OpenInWindow(Build(host));
        MenuFlyout flyout = OpenStripFlyout(menu);

        list.Apply(LandedTargets());
        Layout(flyout);

        List<string> shown = Texts(flyout.Items);
        Assert.Equal(LandedTexts, shown);
        Click(RowOf(flyout.Items, "PUSH TE"));
        Assert.Equal([(Callsign, "PUSH TE", "AB")], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);

        MenuItem pushTo = Submenu(menu);
        pushTo.IsSubMenuOpen = true;
        Assert.Equal(Texts(pushTo.Items), shown);
    }

    /// <summary>
    /// A facing chip on a strip-flyout row sends that facing's push, not the row's bare one, and closes the flyout and the
    /// menu; the chips read the cardinals alone after "then face".
    /// </summary>
    [AvaloniaFact]
    public void StripFlyout_FacingChip_SendsTheFacedPushAndCloses()
    {
        var host = new RecordingMenuHost("")
        {
            PushbackTargets = PushTargetList.Ready([Target(MenuPushTargetKind.Taxilane, "TE", 250, ["N", "S"], null)]),
        };
        ContextMenu menu = OpenInWindow(Build(host));
        MenuFlyout flyout = OpenStripFlyout(menu);
        MenuItem row = RowOf(flyout.Items, "PUSH TE");

        Assert.Equal(["N", "S"], Chips(row).Select(c => c.Content as string));
        Assert.Contains("then face", RowTexts(row));
        ClickOn(Chip(row, "PUSH TE FACE N"));

        Assert.Equal([(Callsign, "PUSH TE FACE N", "AB")], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    /// <summary>A target that can end on one facing only names it, "faces E", and offers no chip.</summary>
    [AvaloniaFact]
    public void OneFacing_ShowsTheFacingAsANote_WithNoChip()
    {
        var host = new RecordingMenuHost("") { PushbackTargets = PushTargetList.Ready([Target(MenuPushTargetKind.Taxiway, "U", 980, ["E"], null)]) };
        MenuFlyout flyout = OpenStripFlyout(OpenInWindow(Build(host)));
        MenuItem row = RowOf(flyout.Items, "PUSH U");

        Assert.Empty(Chips(row));
        Assert.Contains("faces E", RowTexts(row));
    }

    /// <summary>A click on a blocked row sends nothing and leaves the flyout and the menu open; the row names its blocker.</summary>
    [AvaloniaFact]
    public void BlockedRow_ClickSendsNothingAndStaysOpen()
    {
        var host = new RecordingMenuHost("") { PushbackTargets = PushTargetList.Ready(TargetsWithBlockedTf()) };
        ContextMenu menu = OpenInWindow(Build(host));
        MenuFlyout flyout = OpenStripFlyout(menu);
        MenuItem row = RowOf(flyout.Items, "PUSH TF");

        ClickOn(LabelOf(row, "TF"));

        Assert.Empty(host.Sent);
        Assert.True(row.StaysOpenOnClick);
        Assert.True(flyout.IsOpen);
        Assert.True(menu.IsOpen);
        Assert.Contains("blocked by SWA919", RowTexts(row));
        Assert.DoesNotContain(Chips(row), c => AutomationProperties.GetName(c)!.Contains(" FACE ", StringComparison.Ordinal));
    }

    /// <summary>A blocked row's "push anyway" chip sends the forced push and closes the flyout and the menu.</summary>
    [AvaloniaFact]
    public void BlockedRow_PushAnyway_SendsTheForcedPush()
    {
        var host = new RecordingMenuHost("") { PushbackTargets = PushTargetList.Ready(TargetsWithBlockedTf()) };
        ContextMenu menu = OpenInWindow(Build(host));
        MenuFlyout flyout = OpenStripFlyout(menu);
        Button chip = Chip(RowOf(flyout.Items, "PUSH TF"), "PUSHF TF");

        Assert.Equal("push anyway", chip.Content);
        ClickOn(chip);

        Assert.Equal([(Callsign, "PUSHF TF", "AB")], host.Sent);
        Assert.False(flyout.IsOpen);
        Assert.False(menu.IsOpen);
    }

    /// <summary>While the seed shows, a disabled "Refining targets…" row leads the flyout; it goes when the plan lands.</summary>
    [AvaloniaFact]
    public void SeedShown_ShowsRefiningUntilThePlanLands()
    {
        var list = PushTargetList.Pending(SeedTargets());
        MenuFlyout flyout = OpenStripFlyout(OpenInWindow(Build(new RecordingMenuHost("") { PushbackTargets = list })));

        Assert.Equal([Refining, BehindTheAircraft, "PUSH TE", "PUSH W"], Texts(flyout.Items));
        Assert.False(Assert.IsType<MenuItem>(flyout.Items[0]).IsEnabled);

        list.Apply(LandedTargets());

        Assert.Equal(LandedTexts, Texts(flyout.Items));
    }

    /// <summary>
    /// When the plan lands, a target that survives keeps its row, the same menu item showing the landed target; a row
    /// whose target has gone leaves the flyout, and a click still pending on it sends nothing. The submenu does the same.
    /// </summary>
    [AvaloniaFact]
    public void PlanLands_KeepsTheRowOfASurvivingTarget()
    {
        var list = PushTargetList.Pending(SeedTargets());
        var host = new RecordingMenuHost("") { PushbackTargets = list };
        MenuFlyout flyout = OpenStripFlyout(OpenInWindow(Build(host)));
        MenuItem te = RowOf(flyout.Items, "PUSH TE");
        MenuItem w = RowOf(flyout.Items, "PUSH W");
        var submenuList = PushTargetList.Pending(SeedTargets());
        MenuItem pushTo = Submenu(Build(new RecordingMenuHost("") { PushbackTargets = submenuList }));
        pushTo.IsSubMenuOpen = true;
        MenuItem submenuTe = RowOf(pushTo.Items, "PUSH TE");

        list.Apply(LandedAfterSeed());
        submenuList.Apply(LandedAfterSeed());
        Layout(flyout);

        Assert.Same(te, RowOf(flyout.Items, "PUSH TE"));
        Assert.Equal(260, ((PushbackToMenu.PushTargetRow)te.Header!).Target.PathLengthFt);
        Assert.DoesNotContain(w, flyout.Items);
        Assert.Same(submenuTe, RowOf(pushTo.Items, "PUSH TE"));
        Click(w);
        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// A strip flyout closed before the plan lands keeps what it showed, as the submenu does, and shows the landed rows
    /// once it is opened again.
    /// </summary>
    [AvaloniaFact]
    public void StripFlyout_ClosedBeforeTheListLands_RefillsOnReopen()
    {
        var list = PushTargetList.Pending(null);
        ContextMenu menu = OpenInWindow(Build(new RecordingMenuHost("") { PushbackTargets = list }));
        MenuFlyout flyout = OpenStripFlyout(menu);
        flyout.Hide();

        list.Apply(LandedTargets());

        AssertOnlyRow(flyout.Items, PushbackToMenu.ComputingText);
        Assert.Same(flyout, OpenStripFlyout(menu));
        Assert.Equal(LandedTexts, Texts(flyout.Items));
    }

    /// <summary>A list pending with an empty seed is still computing: it shows the computing row, then the landed rows.</summary>
    [AvaloniaFact]
    public void PendingWithAnEmptySeed_ShowsComputingThenTheLandedRows()
    {
        var list = PushTargetList.Pending([]);
        MenuFlyout flyout = OpenStripFlyout(OpenInWindow(Build(new RecordingMenuHost("") { PushbackTargets = list })));

        AssertOnlyRow(flyout.Items, PushbackToMenu.ComputingText);

        list.Apply(LandedTargets());

        Assert.Equal(LandedTexts, Texts(flyout.Items));
    }

    /// <summary>A plan that settles empty while the strip flyout is open leaves the disabled no-targets row.</summary>
    [AvaloniaFact]
    public void PlanLandsEmptyWhileOpen_ShowsNoTargetsRow()
    {
        var list = PushTargetList.Pending(null);
        MenuFlyout flyout = OpenStripFlyout(OpenInWindow(Build(new RecordingMenuHost("") { PushbackTargets = list })));

        list.Apply([]);

        AssertOnlyRow(flyout.Items, PushbackToMenu.NoTargetsText);
    }

    /// <summary>
    /// A plan started for one stand is not handed to an open for another: moved from gate 26 to gate 25 before the first
    /// plan lands, the aircraft gets gate 25's targets.
    /// </summary>
    [AvaloniaFact]
    public async Task InFlightPlan_ForADifferentStand_IsNotReused()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtGate26(OakPushTargetSeedCopy.NoSeed());
        PushTargetList atGate26 = host.GetPushbackTargets(Callsign);
        AircraftModel atGate25 = ClientMenuHostGroundTests.ParkedAt("25", Callsign);
        target.Position = atGate25.Position;
        target.Heading = atGate25.Heading;
        target.ParkingSpot = atGate25.ParkingSpot;

        PushTargetList second = host.GetPushbackTargets(Callsign);
        await Settle(atGate26);
        await Settle(second);

        List<ClientMenuHostGroundTests.PushTargetRowKey> expected = ClientMenuHostGroundTests.LivePlanRows(main, target, "25");
        Assert.NotEqual(expected, ClientMenuHostGroundTests.RowKeys(atGate26));
        Assert.Equal(expected, ClientMenuHostGroundTests.RowKeys(second));
    }

    /// <summary>
    /// A plan started for one held pose is not handed to an open from a pose fifty feet away: moved along its heading
    /// before the first plan lands, the held aircraft gets a fresh list, planned from where it now stands.
    /// </summary>
    [AvaloniaFact]
    public async Task InFlightPlan_ForAHeldPoseFiftyFeetAway_IsNotReused()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());
        HoldAtSpotC(main, target);
        PushTargetList first = host.GetPushbackTargets(Callsign);
        target.Position = GeoMath.ProjectPoint(target.Position, target.Heading, 50.0 / GeoMath.FeetPerNm);

        PushTargetList second = host.GetPushbackTargets(Callsign);
        await Settle(first);
        await Settle(second);

        Assert.NotSame(first, second);
        Assert.Equal(HeldPlanRows(main, target), ClientMenuHostGroundTests.RowKeys(second));
    }

    /// <summary>
    /// A plan started for one held pose is handed to an open from a pose half a foot and half a degree off it: the two
    /// opens share one list, wherever the pose falls; here the two headings straddle a whole degree's rounding edge.
    /// </summary>
    [AvaloniaFact]
    public async Task InFlightPlan_ForTheSameHeldPose_IsReused()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());
        HoldAtSpotC(main, target);
        target.Heading = new TrueHeading(Math.Floor(target.Heading.Degrees) + 0.3);
        PushTargetList first = host.GetPushbackTargets(Callsign);
        target.Position = GeoMath.ProjectPoint(target.Position, new TrueHeading(target.Heading.Degrees + 90.0), 0.5 / GeoMath.FeetPerNm);
        target.Heading = new TrueHeading(target.Heading.Degrees + 0.5);

        PushTargetList second = host.GetPushbackTargets(Callsign);
        await Settle(first);
        await Settle(second);

        Assert.Same(first, second);
    }

    /// <summary>
    /// The held-pose tolerance: on the same heading, an open at the edge of <see cref="GroundViewModel.SameHeldPoseFt"/> from
    /// the in-flight plan's pose shares its list, and one half a foot past it does not. The edge open stands a hundredth
    /// of the tolerance inside it, as <see cref="GeoMath.DistanceNm(LatLon, LatLon)"/> measures it:
    /// <see cref="GeoMath.ProjectPoint(LatLon, TrueHeading, double)"/> projects on a flat 60 nm-per-degree grid, so a point
    /// projected exactly the tolerance away measures about 0.07% past it.
    /// </summary>
    [AvaloniaFact]
    public async Task InFlightPlan_AtTheHeldPoseTolerance_IsReusedAndJustPastItIsNot()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());
        HoldAtSpotC(main, target);
        LatLon held = target.Position;
        var abeam = new TrueHeading(target.Heading.Degrees + 90.0);
        PushTargetList first = host.GetPushbackTargets(Callsign);

        target.Position = GeoMath.ProjectPoint(held, abeam, 0.99 * GroundViewModel.SameHeldPoseFt / GeoMath.FeetPerNm);
        double edgeFt = GeoMath.DistanceNm(held, target.Position) * GeoMath.FeetPerNm;
        PushTargetList atTolerance = host.GetPushbackTargets(Callsign);
        target.Position = GeoMath.ProjectPoint(held, abeam, (GroundViewModel.SameHeldPoseFt + 0.5) / GeoMath.FeetPerNm);
        PushTargetList pastTolerance = host.GetPushbackTargets(Callsign);
        await Settle(first);
        await Settle(pastTolerance);

        Assert.InRange(edgeFt, 0.99 * GroundViewModel.SameHeldPoseFt, GroundViewModel.SameHeldPoseFt);
        Assert.Same(first, atTolerance);
        Assert.NotSame(first, pastTolerance);
    }

    /// <summary>
    /// An aircraft pushed from gate 12 to spot C and held there has no stand but keeps Push back to…: no seed covers a
    /// held aircraft, so the entry computes at once, and the live plan is the one made from where it stands, without
    /// spot C.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_HeldAfterPushToSpot_PlansFromWhereItStands()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtStand("12", seedCopy.NewSeed());
        AirportGroundLayout layout = main.Ground.DomainLayout!;
        GroundNode spotC = layout.FindSpotNodeByName("C") ?? throw new InvalidOperationException("No spot C at OAK");
        HoldAfterPush(main, target, "12", TugGoal.Spot(spotC));
        target.ParkingSpot = "";

        PushTargetList list = host.GetPushbackTargets(Callsign);
        MenuItem pushTo = Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));

        AssertOnlyRow(pushTo, PushbackToMenu.ComputingText);
        await Settle(list);
        List<ClientMenuHostGroundTests.PushTargetRowKey> expected = HeldPlanRows(main, target);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, ClientMenuHostGroundTests.RowKeys(list));
        Assert.DoesNotContain(list.Targets, t => t.Command == "PUSH $C");
    }

    /// <summary>
    /// Pushed from gate 26 onto TE and held there, the aircraft still carries stand 26, as the server leaves it after a push
    /// to a taxiway: the entry shows, computing rather than showing gate 26's seed, and the live plan is the one made from
    /// where it is held, not gate 26's.
    /// </summary>
    [AvaloniaFact]
    public async Task PushbackTo_HeldAfterPushOntoTaxiway_PlansHeldNotFromTheStand()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        (AircraftModel target, MainViewModel main, ClientMenuHost host) = ClientMenuHostGroundTests.AtGate26(seedCopy.NewSeed());
        AirportGroundLayout layout = main.Ground.DomainLayout!;
        GroundNode exit = layout.FindExitByTaxiway(StandNode(layout, "26").Position, "TE") ?? throw new InvalidOperationException("No exit onto TE");
        HoldAfterPush(main, target, "26", TugGoal.StraightBackTo(exit, "TE"));
        Assert.Equal("26", target.ParkingSpot);

        PushTargetList list = host.GetPushbackTargets(Callsign);
        MenuItem pushTo = Submenu(AircraftMenuBuilder.Build(target, new MenuClick(Callsign, null, null, []), host, _ => []));

        AssertOnlyRow(pushTo, PushbackToMenu.ComputingText);
        await Settle(list);
        List<ClientMenuHostGroundTests.PushTargetRowKey> expected = HeldPlanRows(main, target);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, ClientMenuHostGroundTests.RowKeys(list));
        Assert.NotEqual(ClientMenuHostGroundTests.LivePlanRows(main, target, "26"), ClientMenuHostGroundTests.RowKeys(list));
    }

    [AvaloniaFact]
    public void PushbackTo_Computing_ShowsThePlaceholder()
    {
        var host = new RecordingMenuHost("") { PushbackTargets = PushTargetList.Pending(null) };

        MenuItem pushTo = Submenu(Build(host));

        AssertOnlyRow(pushTo, PushbackToMenu.ComputingText);
    }

    /// <summary>
    /// An open submenu rebuilds its items in place when the plan lands; one closed when the plan lands keeps what it
    /// showed until it is opened again, and then shows the landed targets.
    /// </summary>
    [AvaloniaFact]
    public void PushbackTo_ListChangesWhileOpen_RebuildsInPlace()
    {
        var open = PushTargetList.Pending(null);
        MenuItem pushTo = Submenu(Build(new RecordingMenuHost("") { PushbackTargets = open }));
        var closed = PushTargetList.Pending(null);
        MenuItem later = Submenu(Build(new RecordingMenuHost("") { PushbackTargets = closed }));
        pushTo.IsSubMenuOpen = true;

        open.Apply(LandedTargets());
        closed.Apply(LandedTargets());

        Assert.Equal(LandedTexts, Texts(pushTo.Items));
        AssertOnlyRow(later, PushbackToMenu.ComputingText);

        later.IsSubMenuOpen = true;
        Assert.Equal(LandedTexts, Texts(later.Items));
    }

    [AvaloniaFact]
    public void PushbackTo_EmptyAfterPlan_ShowsNoTargets()
    {
        var list = PushTargetList.Pending(null);
        MenuItem pushTo = Submenu(Build(new RecordingMenuHost("") { PushbackTargets = list }));
        pushTo.IsSubMenuOpen = true;

        list.Apply([]);

        AssertOnlyRow(pushTo, PushbackToMenu.NoTargetsText);
    }

    /// <summary>A taxilane, a taxiway and a spot, the spot listed first, so the sections must order them.</summary>
    private static List<MenuPushTarget> LandedTargets() =>
        [
            new(MenuPushTargetKind.Spot, "1", null, 300, "PUSH $1", [], null),
            new(MenuPushTargetKind.Taxiway, "W", "across", 420, "PUSH W", [], null),
            new(MenuPushTargetKind.Taxilane, "TE", "alongside", 140, "PUSH TE", [], null),
        ];

    /// <summary>A seed of a taxilane and a taxiway, the plan still running.</summary>
    private static List<MenuPushTarget> SeedTargets() =>
        [Target(MenuPushTargetKind.Taxilane, "TE", 250, [], null), Target(MenuPushTargetKind.Taxiway, "W", 610, [], null)];

    /// <summary>What the plan lands after <see cref="SeedTargets"/>: TE again, longer, with facings; W gone; TF new.</summary>
    private static List<MenuPushTarget> LandedAfterSeed() =>
        [Target(MenuPushTargetKind.Taxilane, "TE", 260, ["N", "S"], null), Target(MenuPushTargetKind.Taxilane, "TF", 420, [], null)];

    /// <summary>TE clear and TF blocked by SWA919, both with two facings.</summary>
    private static List<MenuPushTarget> TargetsWithBlockedTf() =>
        [Target(MenuPushTargetKind.Taxilane, "TE", 250, ["N", "S"], null), Target(MenuPushTargetKind.Taxilane, "TF", 420, ["N", "S"], "SWA919")];

    /// <summary>A taxilane or taxiway target named <paramref name="name"/> sending <c>PUSH name</c>, a facing choice per cardinal.</summary>
    private static MenuPushTarget Target(MenuPushTargetKind kind, string name, double pathLengthFt, string[] facings, string? blockedBy) =>
        new(
            kind,
            name,
            "alongside",
            pathLengthFt,
            $"PUSH {name}",
            [.. facings.Select(c => new MenuPushFacing(c, $"PUSH {name} FACE {c}"))],
            blockedBy
        );

    private static GroundNode StandNode(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == name)).OrderBy(n => n.Id).First();

    /// <summary>Holds <paramref name="target"/> where a group-III push off gate 12 to spot C ends, its stand cleared.</summary>
    private static void HoldAtSpotC(MainViewModel main, AircraftModel target)
    {
        GroundNode spotC = main.Ground.DomainLayout!.FindSpotNodeByName("C") ?? throw new InvalidOperationException("No spot C at OAK");
        HoldAfterPush(main, target, "12", TugGoal.Spot(spotC));
        target.ParkingSpot = "";
    }

    /// <summary>
    /// Moves <paramref name="target"/> to where a group-III push off <paramref name="standName"/> to <paramref name="goal"/>
    /// ends, as the planner flies it, holding after the pushback there; its stand name is left as it was.
    /// </summary>
    private static void HoldAfterPush(MainViewModel main, AircraftModel target, string standName, TugGoal goal)
    {
        AirportGroundLayout layout = main.Ground.DomainLayout!;
        GroundNode stand = StandNode(layout, standName);
        TugRequest request = PushTargetPlanner.RequestFor(
            new PushbackPose(stand.Position, stand.TrueHeading!.Value.Degrees),
            DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.III),
            MovementAreaClassification.Build(layout, NavigationDatabase.Instance.AirportSidecars),
            goal,
            startsAtStand: true
        );
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        Assert.True(plan is not null, refusal);
        target.Position = plan.End.Position;
        target.Heading = new TrueHeading(plan.End.NoseTrueDeg);
        target.CurrentPhase = "Holding After Pushback";
    }

    /// <summary>
    /// The group-III targets <see cref="PushTargetPlanner.ComputeHeld"/> plans from <paramref name="target"/>'s pose, alone
    /// at the airport, each through the live check: the rows a settled held list should hold.
    /// </summary>
    private static List<ClientMenuHostGroundTests.PushTargetRowKey> HeldPlanRows(MainViewModel main, AircraftModel target)
    {
        IReadOnlyList<PrecomputedPushTarget>? held = PushTargetPlanner.ComputeHeld(
            main.Ground.DomainLayout!,
            DesignGroupEnvelopes.LoadShipped(),
            NavigationDatabase.Instance.AirportSidecars,
            new PushbackPose(target.Position, target.Heading.Degrees),
            AirplaneDesignGroup.III
        );
        Assert.NotNull(held);
        var request = new PushTargetLiveCheckRequest
        {
            Subject = GroundViewModel.TugCandidateOf(target),
            Actual = AircraftFootprint.FromType(target.AircraftType),
            Others = [GroundViewModel.TugCandidateOf(target)],
        };
        return
        [
            .. held.Select(t => (Target: t, Verdict: PushTargetLiveCheck.Check(t, request)))
                .Where(v => v.Verdict.Outcome != PushTargetLiveOutcome.Unflyable)
                .Select(v => new ClientMenuHostGroundTests.PushTargetRowKey(
                    v.Target.Kind.ToString(),
                    v.Target.Command,
                    v.Target.PathLengthFt,
                    v.Verdict.BlockerCallsign
                )),
        ];
    }

    private static ContextMenu Build(RecordingMenuHost host) =>
        AircraftMenuBuilder.Build(
            new AircraftModel
            {
                Callsign = Callsign,
                AircraftType = "B738",
                FlightRules = "IFR",
                IsOnGround = true,
                CurrentPhase = "At Parking",
                Situation = AircraftSituation.AtParking,
            },
            new MenuClick(Callsign, null, null, []),
            host,
            _ => []
        );

    private static void AssertOnlyRow(MenuItem pushTo, string text) => AssertOnlyRow(pushTo.Items, text);

    private static void AssertOnlyRow(ItemCollection items, string text)
    {
        MenuItem row = Assert.IsType<MenuItem>(Assert.Single(items));
        Assert.Equal(text, row.Header);
        Assert.False(row.IsEnabled);
    }

    /// <summary>Each item as its header text, a target row as its command, a separator as <c>-</c>.</summary>
    private static List<string> Texts(ItemCollection items) =>
        [.. items.Select(i => i is MenuItem { Header: string header } ? header : (i is MenuItem m ? MenuCommandText.GetCommand(m)! : "-"))];

    private static MenuItem Strip(ContextMenu menu) => Assert.Single(menu.Items.OfType<MenuItem>(), QuickCommandStrip.IsStrip);

    /// <summary>Clicks the strip's Push back to… button and returns the flyout it opened beside the menu, laid out.</summary>
    private static MenuFlyout OpenStripFlyout(ContextMenu menu)
    {
        Button button = QuickCommandStrip.Buttons(Strip(menu)).Single(b => (string)b.Tag! == MenuIds.GroundPushbackTo);
        QuickCommandStrip.OpenSubmenu(button);
        MenuFlyout flyout = Assert.IsType<MenuFlyout>(FlyoutBase.GetAttachedFlyout(button));
        Assert.True(flyout.IsOpen);
        Layout(flyout);
        return flyout;
    }

    /// <summary>Lays the open flyout out, so its rows' headers are built and have places to click.</summary>
    private static void Layout(MenuFlyout flyout)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.IsAssignableFrom<Control>(flyout.Popup.Child).UpdateLayout();
    }

    /// <summary>Opens <paramref name="menu"/> over a shown window and lays it out, so its strip buttons can show their flyouts.</summary>
    private static ContextMenu OpenInWindow(ContextMenu menu)
    {
        var window = new Window { Width = 600, Height = 900 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        menu.UpdateLayout();
        Assert.True(menu.IsOpen);
        return menu;
    }

    /// <summary>Opens the open menu's All Commands, then its Push back to… submenu, and lays them out; returns Push back to….</summary>
    private static MenuItem OpenPushBackToSubmenu(ContextMenu menu)
    {
        MenuItem all = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);
        all.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        MenuItem pushTo = Submenu(menu);
        pushTo.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        Control first = pushTo.Items.OfType<MenuItem>().First();
        (TopLevel.GetTopLevel(first) ?? throw new InvalidOperationException("The Push back to… submenu did not open")).UpdateLayout();
        return pushTo;
    }

    /// <summary>The target row among <paramref name="items"/> that sends <paramref name="command"/>.</summary>
    private static MenuItem RowOf(ItemCollection items, string command) =>
        items.OfType<MenuItem>().Single(m => (m.Header is PushbackToMenu.PushTargetRow) && (MenuCommandText.GetCommand(m) == command));

    /// <summary>The chips a laid-out row shows.</summary>
    private static List<Button> Chips(MenuItem row) => [.. row.GetVisualDescendants().OfType<Button>()];

    /// <summary>The chip of a laid-out row that sends <paramref name="command"/>, by its automation name.</summary>
    private static Button Chip(MenuItem row, string command) => Chips(row).Single(c => AutomationProperties.GetName(c) == command);

    /// <summary>Every text a laid-out row shows.</summary>
    private static List<string?> RowTexts(MenuItem row) => [.. row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)];

    private static TextBlock LabelOf(MenuItem row, string text) => row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);

    /// <summary>A row's header as its template builds it, apart from any layout: the row's own content when it has no template.</summary>
    private static Control HeaderView(MenuItem row) =>
        (row.HeaderTemplate is { } template) ? template.Build(row.Header)! : Assert.IsAssignableFrom<Control>(row.Header);

    /// <summary>Clicks the centre of <paramref name="target"/> with the mouse, as a user would.</summary>
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

    /// <summary>The Push back to… submenu under the menu's All Commands.</summary>
    private static MenuItem Submenu(ContextMenu menu)
    {
        MenuItem all = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);
        return all.Items.OfType<MenuItem>().Single(m => (m.Header as string) == PushBackTo);
    }

    private static MenuItem Row(MenuItem pushTo, string command) =>
        pushTo.Items.OfType<MenuItem>().Single(m => (m.Header is PushbackToMenu.PushTargetRow) && (MenuCommandText.GetCommand(m) == command));

    /// <summary>The section headers, leaving out the refining row a seeded list leads with.</summary>
    private static List<string> SectionHeaders(MenuItem pushTo) =>
        [
            .. pushTo
                .Items.OfType<MenuItem>()
                .Where(m => (m.Header is string header) && (header != Refining) && !m.IsEnabled)
                .Select(m => (string)m.Header!),
        ];

    /// <summary>The rows under <paramref name="header"/> in <paramref name="pushTo"/>, up to the next separator.</summary>
    private static List<PushbackToMenu.PushTargetRow> SectionRows(MenuItem pushTo, string header)
    {
        List<object?> items = [.. pushTo.Items];
        int start = items.FindIndex(i => (i is MenuItem m) && ((m.Header as string) == header));
        Assert.True(start >= 0, $"no section {header}");
        return
        [
            .. items
                .Skip(start + 1)
                .TakeWhile(i => i is MenuItem { Header: PushbackToMenu.PushTargetRow })
                .Select(i => (PushbackToMenu.PushTargetRow)((MenuItem)i!).Header!),
        ];
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>Waits for the list's background live plan to land, so no plan outlives its test.</summary>
    private static Task Settle(PushTargetList list) =>
        list.Settled.WaitAsync(ClientMenuHostGroundTests.SettleTimeout, TestContext.Current.CancellationToken);
}
