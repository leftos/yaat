using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// Regression: PUSH $<spot> ends in "Holding After Pushback" (a plain PUSH always did), but both
// context menus gated "Push back" on "At Parking". An aircraft resting on a ramp spot therefore
// lost the menu path to being pushed again, even though GroundCommandHandler.TryPushback accepts
// a completed pushback. The menus must offer it for both phases — and for neither of the two
// other holds ("Holding After Exit", "Holding In Position"), which TryPushback refuses.
//
// That drift is why the three ground-movement gates ("Push back", "Hold position", "Resume taxi")
// live in AircraftCommandApplicability. The parity harness further down pins the full header
// sequence of both menus per phase, so a consolidation that duplicates or reorders an item fails.
public class GroundMovementMenuTests
{
    private const double Lat = 37.620;
    private const double Lon = -122.380;

    private static List<string> Headers(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!).ToList();

    /// <summary>
    /// Pins the whole top-level header sequence of <paramref name="menu"/>. Membership assertions alone
    /// cannot catch an item emitted twice or emitted out of order, which is the failure mode when the two
    /// menus' phase gates are consolidated behind shared predicates.
    /// </summary>
    private static void AssertHeaderSequence(ContextMenu menu, params string[] expected) => Assert.Equal(expected, Headers(menu));

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
    /// Builds the ground-map right-click menu for a single aircraft in <paramref name="phase"/>. The view is
    /// parented to a host carrying the MainViewModel so GroundView.FindMainViewModel resolves, which is what
    /// the Follow… submenu needs; a second ground aircraft supplies the follow candidate.
    /// </summary>
    private static ContextMenu BuildGroundMenu(string phase, bool held)
    {
        AircraftModel ac = GroundAircraft("UAL100", phase, held);
        var mainVm = new MainViewModel(new FakeFilePickerService());
        mainVm.Aircraft.Add(ac);
        mainVm.Aircraft.Add(GroundAircraft("SWA200", "Taxiing", held: false));

        var groundVm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);
        var view = new GroundView { DataContext = groundVm };
        var host = new Grid { DataContext = mainVm };
        host.Children.Add(view);

        var menu = new ContextMenu();
        view.AddSimulatedAircraftItems(menu, groundVm, new GroundMenuTarget(ac, PrevSelected: null, ac.Callsign, "AB"));
        return menu;
    }

    private static ContextMenu BuildAircraftListMenu(string phase, bool held)
    {
        AircraftModel ac = GroundAircraft("UAL100", phase, held);
        var vm = new MainViewModel(new FakeFilePickerService());
        var menu = new ContextMenu();
        DataGridView.AddPhaseAwareItems(menu, ac, vm, ac.Callsign, "AB");
        return menu;
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

    // AddParkingAndTaxiItems and AddHoldingItems both build Follow… submenus. Widening the
    // pushback gate must not let a "Holding After Pushback" aircraft collect one from each.
    [AvaloniaTheory]
    [InlineData("At Parking")]
    [InlineData("Holding After Pushback")]
    public void GroundMenu_HasExactlyOneFollowSubmenu(string phase) =>
        Assert.Equal(1, Headers(BuildGroundMenu(phase, held: false)).Count(h => h == "Follow..."));

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
        AssertHeaderSequence(BuildGroundMenu("At Parking", held: false), "Push back", "Push route...", "Follow...", "Draw taxi route...");

    [AvaloniaFact]
    public void GroundMenu_Taxiing_PinsHeaderSequence()
    {
        AssertHeaderSequence(
            BuildGroundMenu("Taxiing", held: false),
            "Hold position",
            "Follow...",
            "Give way to...",
            "Break conflict",
            "Draw taxi route..."
        );
    }

    // FollowingPhase.Name is "Following <target>", so the client never sees a bare "Following".
    [AvaloniaFact]
    public void GroundMenu_Following_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildGroundMenu("Following SWA200", held: false), "Hold position", "Draw taxi route...");

    [AvaloniaFact]
    public void GroundMenu_HoldingInPosition_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildGroundMenu("Holding In Position", held: true), "Resume taxi", "Follow...", "Give way to...", "Draw taxi route...");

    [AvaloniaFact]
    public void GroundMenu_HoldingInPositionUnheld_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildGroundMenu("Holding In Position", held: false), "Follow...", "Give way to...", "Draw taxi route...");

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterPushback_PinsHeaderSequence()
    {
        AssertHeaderSequence(
            BuildGroundMenu("Holding After Pushback", held: true),
            "Push back",
            "Push route...",
            "Resume taxi",
            "Follow...",
            "Draw taxi route..."
        );
    }

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterPushbackUnheld_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildGroundMenu("Holding After Pushback", held: false), "Push back", "Push route...", "Follow...", "Draw taxi route...");

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterExit_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildGroundMenu("Holding After Exit", held: true), "Resume taxi", "Follow...", "Draw taxi route...");

    [AvaloniaFact]
    public void GroundMenu_HoldingAfterExitUnheld_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildGroundMenu("Holding After Exit", held: false), "Follow...", "Draw taxi route...");

    [AvaloniaFact]
    public void AircraftListMenu_AtParking_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildAircraftListMenu("At Parking", held: false), "Push back");

    [AvaloniaFact]
    public void AircraftListMenu_Taxiing_PinsHeaderSequence() => AssertHeaderSequence(BuildAircraftListMenu("Taxiing", held: false), "Hold position");

    [AvaloniaFact]
    public void AircraftListMenu_Following_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildAircraftListMenu("Following SWA200", held: false), "Hold position");

    [AvaloniaFact]
    public void AircraftListMenu_HoldingInPosition_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildAircraftListMenu("Holding In Position", held: true), "Resume taxi");

    [AvaloniaFact]
    public void AircraftListMenu_HoldingInPositionUnheld_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildAircraftListMenu("Holding In Position", held: false));

    [AvaloniaFact]
    public void AircraftListMenu_HoldingAfterPushback_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildAircraftListMenu("Holding After Pushback", held: true), "Push back", "Resume taxi");

    [AvaloniaFact]
    public void AircraftListMenu_HoldingAfterPushbackUnheld_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildAircraftListMenu("Holding After Pushback", held: false), "Push back");

    [AvaloniaFact]
    public void AircraftListMenu_HoldingAfterExit_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildAircraftListMenu("Holding After Exit", held: true), "Resume taxi");

    [AvaloniaFact]
    public void AircraftListMenu_HoldingAfterExitUnheld_PinsHeaderSequence() =>
        AssertHeaderSequence(BuildAircraftListMenu("Holding After Exit", held: false));

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
    [InlineData("Pushback to Spot")]
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
