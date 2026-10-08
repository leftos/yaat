using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

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

    /// <summary>The headers of the command tree under All Commands, where the ground block and the flight groups live.</summary>
    private static List<string> Headers(ContextMenu menu) => HeadersIn(AllCommands(menu).Items);

    /// <summary>The headers of the menu's top level.</summary>
    private static List<string> TopHeaders(ContextMenu menu) => HeadersIn(menu.Items);

    private static List<string> HeadersIn(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    private static MenuItem AllCommands(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);

    /// <summary>
    /// Builds a ground aircraft in <paramref name="phase"/>. <paramref name="held"/> mirrors the wire form of an
    /// active hold directive (<c>HoldKind</c> carries the <see cref="HoldKind"/> name), which is what drives
    /// <c>AircraftModel.IsHeld</c>.
    /// </summary>
    private static AircraftModel GroundAircraft(string callsign, string phase, bool held) =>
        GroundAircraft(callsign, phase, held, standDeparture: null);

    /// <summary><see cref="GroundAircraft(string, string, bool)"/> with the stand departure the server sent for it.</summary>
    private static AircraftModel GroundAircraft(string callsign, string phase, bool held, StandDeparture? standDeparture)
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
            StandDeparture = standDeparture,
        };
    }

    /// <summary>
    /// Builds the ground-map right-click menu for a single aircraft in <paramref name="phase"/> through the ground view's
    /// right-click path (<see cref="MenuHostHarness.BuildGroundMenu"/>), over a MainViewModel holding a second ground
    /// aircraft that supplies the follow candidate.
    /// </summary>
    private static ContextMenu BuildGroundMenu(string phase, bool held) => BuildGroundMenu(phase, held, standDeparture: null);

    private static ContextMenu BuildGroundMenu(string phase, bool held, StandDeparture? standDeparture)
    {
        AircraftModel ac = GroundAircraft("UAL100", phase, held, standDeparture);
        var mainVm = new MainViewModel(new FakeFilePickerService());
        mainVm.Aircraft.Add(ac);
        mainVm.Aircraft.Add(GroundAircraft("SWA200", "Taxiing", held: false));
        return MenuHostHarness.BuildGroundMenu(mainVm, ac, null);
    }

    /// <summary>Every push entry of the aircraft menus: Push back, Push back, face, Push back to… and Push route….</summary>
    private static bool IsPushEntry(string header) => header.StartsWith("Push", StringComparison.Ordinal);

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
        "Ignore ground conflicts (15 s)",
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
    private static ContextMenu BuildAircraftListMenu(string phase, bool held) => BuildAircraftListMenu(phase, held, standDeparture: null);

    private static ContextMenu BuildAircraftListMenu(string phase, bool held, StandDeparture? standDeparture)
    {
        AircraftModel ac = GroundAircraft("UAL100", phase, held, standDeparture);
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
        ContextMenu menu = BuildWholeGroundMenu(AirborneIfr("AAL601", "ApproachNav"));
        List<string> headers = Headers(menu);

        string[] expected = ["Heading", "Altitude", "Speed", "Navigation", "Approach", "Procedures", "Coordination", "Edit flight plan"];
        Assert.All(expected, header => Assert.Contains(header, headers));
        Assert.All(["Track", "Data Block", "Squawk"], header => Assert.Contains(header, TopHeaders(menu)));
    }

    // The ground's canvas items live in its view section: one Display submenu between Squawk and Favorites, and none of
    // them flat at the top level or in the command tree.
    [AvaloniaFact]
    public void GroundMenu_DisplayIsASubmenuInTheViewSection()
    {
        ContextMenu menu = BuildWholeGroundMenu(GroundAircraft("UAL100", "Taxiing", held: false));
        List<object?> items = [.. menu.Items];

        MenuItem display = Assert.Single(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "Display");
        List<string> displayHeaders = [.. display.Items.OfType<MenuItem>().Select(m => m.Header as string ?? "")];
        Assert.Equal(["Taxi route", "Hide datablock", "Measure from UAL100"], displayHeaders);

        int displayIndex = items.IndexOf(display);
        Assert.Equal("Squawk", Assert.IsType<MenuItem>(items[displayIndex - 1]).Header);
        Assert.Equal(MenuCatalog.Get(MenuIds.FavoritesMenu).Label, Assert.IsType<MenuItem>(items[displayIndex + 1]).Header);
        Assert.Contains("Edit flight plan", Headers(menu));

        foreach (List<string> headers in (List<string>[])[Headers(menu), TopHeaders(menu)])
        {
            Assert.DoesNotContain("Taxi route", headers);
            Assert.DoesNotContain("Hide datablock", headers);
            Assert.DoesNotContain(headers, h => h.StartsWith("Measure", StringComparison.Ordinal));
        }
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

    // A taxi-out stand (the server's StandDeparture) offers none of the push entries: Push back, Push back, face,
    // Push back to… and Push route… all hang off CanPushBack.
    [AvaloniaFact]
    public void GroundMenu_AtTaxiOutStand_OffersNoPushEntry() =>
        Assert.DoesNotContain(Headers(BuildGroundMenu("At Parking", held: false, StandDeparture.TaxiOut)), IsPushEntry);

    [AvaloniaFact]
    public void AircraftListMenu_AtTaxiOutStand_OffersNoPushEntry() =>
        Assert.DoesNotContain(Headers(BuildAircraftListMenu("At Parking", held: false, StandDeparture.TaxiOut)), IsPushEntry);

    [AvaloniaFact]
    public void GroundMenu_AtPushBackStand_OffersPushBackAndPushRoute()
    {
        List<string> headers = Headers(BuildGroundMenu("At Parking", held: false, StandDeparture.PushBack));
        Assert.Contains("Push back", headers);
        Assert.Contains("Push route…", headers);
    }

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
            "Ignore ground conflicts (15 s)",
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

    [Fact]
    public void CanPushBack_AtTaxiOutStand_IsFalse() =>
        Assert.False(AircraftCommandApplicability.CanPushBack(GroundAircraft("UAL100", "At Parking", held: false, StandDeparture.TaxiOut)));

    [Fact]
    public void CanPushBack_AtPushBackStand_IsTrue() =>
        Assert.True(AircraftCommandApplicability.CanPushBack(GroundAircraft("UAL100", "At Parking", held: false, StandDeparture.PushBack)));

    [Fact]
    public void CanPushBack_AfterACompletedPushback_IgnoresTheStand() =>
        Assert.True(
            AircraftCommandApplicability.CanPushBack(GroundAircraft("UAL100", "Holding After Pushback", held: false, StandDeparture.TaxiOut))
        );

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
