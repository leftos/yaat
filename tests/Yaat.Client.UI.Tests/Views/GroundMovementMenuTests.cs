using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// Regression: PUSH $<spot> ends in "Holding After Pushback" (a plain PUSH always did), but both
// context menus gated "Push back" on "At Parking". An aircraft resting on a ramp spot therefore
// lost the menu path to being pushed again, even though GroundCommandHandler.TryPushback accepts
// a completed pushback. The menus must offer it for both phases — and for neither of the two
// other holds ("Holding After Exit", "Holding In Position"), which TryPushback refuses.
//
// That drift is why the three ground-movement gates ("Push back", "Hold position", "Resume taxi")
// live in AircraftCommandApplicability. The parity harness further down pins the ground block on the
// ground, phase by phase, and compares the list's block to it, so a consolidation that duplicates or
// reorders an item fails.
public class GroundMovementMenuTests
{
    private const double Lat = 37.620;
    private const double Lon = -122.380;

    private static List<string> Headers(ContextMenu menu) =>
        [.. menu.Items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    /// <summary>
    /// Builds a ground aircraft in <paramref name="phase"/>. <paramref name="held"/> mirrors the wire form of an
    /// active hold directive (<c>HoldKind</c> carries the <see cref="HoldKind"/> name), which is what drives
    /// <c>AircraftModel.IsHeld</c>.
    /// </summary>
    private static AircraftModel GroundAircraft(string callsign, string phase, bool held)
    {
        return new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = true,
            FlightRules = "IFR",
            CurrentPhase = phase,
            Position = new LatLon(Lat, Lon),
            HoldKind = held ? nameof(HoldKind.HoldPosition) : null,
        };
    }

    /// <summary>
    /// Builds the ground-map right-click menu for a single aircraft in <paramref name="phase"/> through the ground view's
    /// right-click path (<see cref="MenuHostHarness.BuildGroundMenu"/>), over a MainViewModel holding a second ground
    /// aircraft that supplies the follow candidate.
    /// </summary>
    private static ContextMenu BuildGroundMenu(string phase, bool held)
    {
        AircraftModel ac = GroundAircraft("UAL100", phase, held);
        var mainVm = new MainViewModel(new FakeFilePickerService());
        mainVm.Aircraft.Add(ac);
        mainVm.Aircraft.Add(GroundAircraft("SWA200", "Taxiing", held: false));
        return MenuHostHarness.BuildGroundMenu(mainVm, ac, null);
    }

    /// <summary>The headers of the ground-movement and taxi-route items: the ground pins below compare these, in menu order.</summary>
    private static readonly HashSet<string> GroundBlockHeaders =
    [
        "Push back",
        "Push back to…",
        "Push route…",
        "Hold position",
        "Hold short of…",
        "Follow…",
        "Give way to…",
        "Break conflict",
        "Resume taxi",
        "Preset taxi route",
        "Draw taxi route…",
    ];

    /// <summary>Pins the ground-movement and taxi-route items of <paramref name="menu"/> in full, ignoring the shared groups around them.</summary>
    private static void AssertGroundBlockSequence(ContextMenu menu, params string[] expected) => Assert.Equal(expected, GroundBlock(menu));

    /// <summary>The ground-movement and taxi-route items of <paramref name="menu"/>, in menu order.</summary>
    private static List<string> GroundBlock(ContextMenu menu) =>
        [.. Headers(menu).Where(h => GroundBlockHeaders.Contains(h) || h.StartsWith("Cross ", StringComparison.Ordinal))];

    /// <summary>
    /// The aircraft-list right-click menu for a single aircraft in <paramref name="phase"/>, through the list's whole-menu
    /// builder, over the same two ground aircraft <see cref="BuildGroundMenu"/> holds.
    /// </summary>
    private static ContextMenu BuildAircraftListMenu(string phase, bool held)
    {
        AircraftModel ac = GroundAircraft("UAL100", phase, held);
        var mainVm = new MainViewModel(new FakeFilePickerService());
        mainVm.Aircraft.Add(ac);
        mainVm.Aircraft.Add(GroundAircraft("SWA200", "Taxiing", held: false));
        return DataGridView.BuildAircraftMenu(mainVm, new DataGrid(), ac, null, [ac]);
    }

    private static AircraftModel AirborneIfr(string callsign, string phase) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = false,
            FlightRules = "IFR",
            CurrentPhase = phase,
        };

    /// <summary>The whole ground-view menu for <paramref name="ac"/>, through the view's right-click path.</summary>
    private static ContextMenu BuildWholeGroundMenu(AircraftModel ac)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.Aircraft.Add(ac);
        return MenuHostHarness.BuildGroundMenu(main, ac, null);
    }

    // The ground view builds the same aircraft menu every view builds, so an airborne aircraft clicked on the ground
    // map gets the flight submenus and the always-present groups, as on the radar.
    [AvaloniaFact]
    public void GroundMenu_AirborneAircraft_OffersTheFlightSubmenus()
    {
        List<string> headers = Headers(BuildWholeGroundMenu(AirborneIfr("AAL601", "ApproachNav")));

        string[] expected =
        [
            "Heading",
            "Altitude",
            "Speed",
            "Navigation",
            "Approach",
            "Procedures",
            "Track",
            "Data Block",
            "Squawk",
            "Coordination",
            "Edit flight plan",
        ];
        Assert.All(expected, header => Assert.Contains(header, headers));
    }

    // The ground's canvas items live in its view section: one Display submenu after Edit flight plan, directly above
    // the foot, and none of them flat at the top level.
    [AvaloniaFact]
    public void GroundMenu_DisplayIsASubmenuInTheViewSection()
    {
        ContextMenu menu = BuildWholeGroundMenu(GroundAircraft("UAL100", "Taxiing", held: false));
        List<object?> items = [.. menu.Items];
        List<string> headers = Headers(menu);

        MenuItem display = Assert.Single(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "Display");
        List<string> displayHeaders = [.. display.Items.OfType<MenuItem>().Select(m => m.Header as string ?? "")];
        Assert.Equal(["Taxi route", "Hide datablock", "Measure from UAL100"], displayHeaders);

        int displayIndex = items.IndexOf(display);
        Assert.True(headers.IndexOf("Edit flight plan") >= 0, string.Join(" | ", headers));
        Assert.True(items.IndexOf(menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == "Edit flight plan")) < displayIndex);
        Assert.IsType<Separator>(items[displayIndex + 1]);
        Assert.Equal("Warp…", Assert.IsType<MenuItem>(items[displayIndex + 2]).Header);

        Assert.DoesNotContain("Taxi route", headers);
        Assert.DoesNotContain("Hide datablock", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Measure", StringComparison.Ordinal));
    }

    [AvaloniaTheory]
    [InlineData("At Parking")]
    [InlineData("Holding After Pushback")]
    public void GroundMenu_OffersPushBack_AtParkingAndAfterPushback(string phase) =>
        Assert.Contains("Push back", Headers(BuildGroundMenu(phase, held: false)));

    [AvaloniaTheory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding In Position")]
    public void GroundMenu_DoesNotOfferPushBack_ForHoldsTryPushbackRefuses(string phase) =>
        Assert.DoesNotContain(Headers(BuildGroundMenu(phase, held: false)), h => h.StartsWith("Push back", StringComparison.Ordinal));

    // The pushback block's Parking position and the Hold position both build Follow… submenus. Widening the
    // pushback gate must not let a "Holding After Pushback" aircraft collect one from each.
    [AvaloniaTheory]
    [InlineData("At Parking")]
    [InlineData("Holding After Pushback")]
    public void GroundMenu_HasExactlyOneFollowSubmenu(string phase) =>
        Assert.Equal(1, Headers(BuildGroundMenu(phase, held: false)).Count(h => h == "Follow…"));

    [AvaloniaTheory]
    [InlineData("At Parking")]
    [InlineData("Holding After Pushback")]
    public void AircraftListMenu_OffersPushBack_AtParkingAndAfterPushback(string phase) =>
        Assert.Contains("Push back", Headers(BuildAircraftListMenu(phase, held: false)));

    [AvaloniaTheory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding In Position")]
    public void AircraftListMenu_DoesNotOfferPushBack_ForHoldsTryPushbackRefuses(string phase) =>
        Assert.DoesNotContain(Headers(BuildAircraftListMenu(phase, held: false)), h => h.StartsWith("Push back", StringComparison.Ordinal));

    // The ground menu emits pushback before the after-pushback hold's "Resume taxi"; the
    // aircraft list must read the same way round.
    [AvaloniaFact]
    public void AircraftListMenu_AfterPushback_OrdersPushBackBeforeResumeTaxi()
    {
        List<string> headers = Headers(BuildAircraftListMenu("Holding After Pushback", held: true));
        Assert.True(headers.IndexOf("Push back") >= 0 && headers.IndexOf("Push back") < headers.IndexOf("Resume taxi"), string.Join(" | ", headers));
    }

    // RES on these three holds routes to GroundCommandHandler.TryResumeTaxi, which refuses
    // ("Aircraft is not held") unless a hold directive is active. Holding In Position is also
    // reached by WARPG, a completed taxi to a spot and a rejected/cancelled takeoff, none of
    // which is held — so the phase alone cannot gate the item, and both menus must agree.
    [AvaloniaTheory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding After Pushback")]
    [InlineData("Holding In Position")]
    public void GroundMenu_OffersResumeTaxi_WhenHeld(string phase) => Assert.Contains("Resume taxi", Headers(BuildGroundMenu(phase, held: true)));

    [AvaloniaTheory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding After Pushback")]
    [InlineData("Holding In Position")]
    public void GroundMenu_HidesResumeTaxi_WhenNotHeld(string phase) =>
        Assert.DoesNotContain("Resume taxi", Headers(BuildGroundMenu(phase, held: false)));

    [AvaloniaTheory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding After Pushback")]
    [InlineData("Holding In Position")]
    public void AircraftListMenu_OffersResumeTaxi_WhenHeld(string phase) =>
        Assert.Contains("Resume taxi", Headers(BuildAircraftListMenu(phase, held: true)));

    [AvaloniaTheory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding After Pushback")]
    [InlineData("Holding In Position")]
    public void AircraftListMenu_HidesResumeTaxi_WhenNotHeld(string phase) =>
        Assert.DoesNotContain("Resume taxi", Headers(BuildAircraftListMenu(phase, held: false)));

    // --- Parity harness -------------------------------------------------------------------
    // The ground-movement gates ("Push back", "Hold position", "Resume taxi") live in
    // AircraftCommandApplicability, consulted from both menus. Consolidating two disjoint
    // per-phase emissions into one guarded emission must not duplicate an item or move it
    // relative to the submenus around it, so every sequence below is pinned in full.

    [AvaloniaFact]
    public void GroundMenu_AtParking_PinsHeaderSequence() =>
        AssertGroundBlockSequence(BuildGroundMenu("At Parking", held: false), "Push back", "Push route…", "Follow…", "Draw taxi route…");

    [AvaloniaFact]
    public void GroundMenu_Taxiing_PinsHeaderSequence()
    {
        AssertGroundBlockSequence(
            BuildGroundMenu("Taxiing", held: false),
            "Hold position",
            "Follow…",
            "Give way to…",
            "Break conflict",
            "Draw taxi route…"
        );
    }

    // FollowingPhase.Name is "Following <target>", so the client never sees a bare "Following".
    [AvaloniaFact]
    public void GroundMenu_Following_PinsHeaderSequence() =>
        AssertGroundBlockSequence(BuildGroundMenu("Following SWA200", held: false), "Hold position", "Draw taxi route…");

    [AvaloniaFact]
    public void GroundMenu_HoldingInPosition_PinsHeaderSequence() =>
        AssertGroundBlockSequence(BuildGroundMenu("Holding In Position", held: true), "Follow…", "Give way to…", "Resume taxi", "Draw taxi route…");

    [AvaloniaFact]
    public void GroundMenu_HoldingInPositionUnheld_PinsHeaderSequence() =>
        AssertGroundBlockSequence(BuildGroundMenu("Holding In Position", held: false), "Follow…", "Give way to…", "Draw taxi route…");

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterPushback_PinsHeaderSequence()
    {
        AssertGroundBlockSequence(
            BuildGroundMenu("Holding After Pushback", held: true),
            "Push back",
            "Push route…",
            "Follow…",
            "Resume taxi",
            "Draw taxi route…"
        );
    }

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterPushbackUnheld_PinsHeaderSequence() =>
        AssertGroundBlockSequence(BuildGroundMenu("Holding After Pushback", held: false), "Push back", "Push route…", "Follow…", "Draw taxi route…");

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterExit_PinsHeaderSequence() =>
        AssertGroundBlockSequence(BuildGroundMenu("Holding After Exit", held: true), "Follow…", "Resume taxi", "Draw taxi route…");

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterExitUnheld_PinsHeaderSequence() =>
        AssertGroundBlockSequence(BuildGroundMenu("Holding After Exit", held: false), "Follow…", "Draw taxi route…");

    // The list builds the aircraft menu every view builds, so its ground-movement block is the ground's, pinned above,
    // phase by phase.
    [AvaloniaTheory]
    [InlineData("At Parking", false)]
    [InlineData("Taxiing", false)]
    [InlineData("Following SWA200", false)]
    [InlineData("Holding In Position", true)]
    [InlineData("Holding In Position", false)]
    [InlineData("Holding After Pushback", true)]
    [InlineData("Holding After Pushback", false)]
    [InlineData("Holding After Exit", true)]
    [InlineData("Holding After Exit", false)]
    public void AircraftListMenu_GroundBlock_MatchesTheGround(string phase, bool held)
    {
        List<string> ground = GroundBlock(BuildGroundMenu(phase, held));

        Assert.NotEmpty(ground);
        Assert.Equal(ground, GroundBlock(BuildAircraftListMenu(phase, held)));
    }

    // --- The shared ground-movement predicates --------------------------------------------

    [Theory]
    [InlineData("At Parking")]
    [InlineData("Holding After Pushback")]
    public void CanPushBack_AcceptsStandAndCompletedPushback(string phase) =>
        Assert.True(AircraftCommandApplicability.CanPushBack(GroundAircraft("UAL100", phase, held: false)));

    // The ground map's node menu offers "Push to <spot>" behind this predicate, so an aircraft resting on a
    // ramp spot can be pushed on to another one — the phase TryPushback accepts and the node menu used to miss.
    // The node menu itself is not buildable from a test (it needs a loaded layout, node hit-testing and a live
    // canvas to show on), so the gate is pinned here instead.
    [Fact]
    public void CanPushBack_AllowsPushToSpot_AfterACompletedPushback() =>
        Assert.True(AircraftCommandApplicability.CanPushBack(GroundAircraft("UAL100", "Holding After Pushback", held: true)));

    [Theory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding In Position")]
    [InlineData("Taxiing")]
    [InlineData("Pushback")]
    [InlineData("Holding Short 28L")]
    public void CanPushBack_RejectsPhasesTryPushbackRefuses(string phase) =>
        Assert.False(AircraftCommandApplicability.CanPushBack(GroundAircraft("UAL100", phase, held: false)));

    [Theory]
    [InlineData("Pushback")]
    [InlineData("Taxiing")]
    [InlineData("Following SWA200")]
    public void CanHoldPosition_AcceptsMovingGroundPhases(string phase) =>
        Assert.True(AircraftCommandApplicability.CanHoldPosition(GroundAircraft("UAL100", phase, held: false)));

    [Theory]
    [InlineData("At Parking")]
    [InlineData("Holding After Exit")]
    [InlineData("Holding After Pushback")]
    [InlineData("Holding In Position")]
    [InlineData("Holding Short 28L")]
    [InlineData("VFR Follow")]
    public void CanHoldPosition_RejectsStationaryAndAirborneFollow(string phase) =>
        Assert.False(AircraftCommandApplicability.CanHoldPosition(GroundAircraft("UAL100", phase, held: false)));

    [Theory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding After Pushback")]
    [InlineData("Holding In Position")]
    public void CanResumeTaxi_AcceptsTheStationaryHolds_WhenHeld(string phase) =>
        Assert.True(AircraftCommandApplicability.CanResumeTaxi(GroundAircraft("UAL100", phase, held: true)));

    [Theory]
    [InlineData("Holding After Exit")]
    [InlineData("Holding After Pushback")]
    [InlineData("Holding In Position")]
    public void CanResumeTaxi_RejectsTheStationaryHolds_WhenNotHeld(string phase) =>
        Assert.False(AircraftCommandApplicability.CanResumeTaxi(GroundAircraft("UAL100", phase, held: false)));

    // Hold-short RES satisfies a crossing clearance instead of clearing a hold directive; it is a
    // different path with its own menu items, so it stays out of this predicate.
    [Theory]
    [InlineData("Holding Short 28L")]
    [InlineData("Taxiing")]
    [InlineData("At Parking")]
    public void CanResumeTaxi_RejectsPhasesWithoutAHoldDirective(string phase) =>
        Assert.False(AircraftCommandApplicability.CanResumeTaxi(GroundAircraft("UAL100", phase, held: true)));

    // Every predicate refuses a live-traffic shadow, which takes no ground command until assumed.
    [Fact]
    public void GroundMovementPredicates_RejectLiveTrafficShadows()
    {
        AircraftModel shadow = GroundAircraft("SKW42", "At Parking", held: true);
        shadow.IsLiveTraffic = true;
        Assert.False(AircraftCommandApplicability.CanPushBack(shadow));

        shadow.CurrentPhase = "Taxiing";
        Assert.False(AircraftCommandApplicability.CanHoldPosition(shadow));

        shadow.CurrentPhase = "Holding In Position";
        Assert.False(AircraftCommandApplicability.CanResumeTaxi(shadow));
    }
}
