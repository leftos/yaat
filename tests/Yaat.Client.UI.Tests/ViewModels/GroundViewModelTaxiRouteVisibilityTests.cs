using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.ViewModels;

// Covers the two ground-view taxi-route display settings: the pure effective-visibility set logic,
// the per-aircraft display-mode override (always show / always hide / follow), and the transient
// hover overlay.
public class GroundViewModelTaxiRouteVisibilityTests
{
    private static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items);

    private static IReadOnlyList<(string, bool)> Fleet(params (string Callsign, bool HasRoute)[] acs) =>
        acs.Select(a => ((string, bool))(a.Callsign, a.HasRoute)).ToList();

    // --- Pure visibility set logic (no view-model, no geometry) ---

    [Fact]
    public void Compute_ShowAllOff_OnlyForcedShown()
    {
        var result = GroundViewModel.ComputeVisibleTaxiRouteCallsigns(Set("A"), Set(), showAll: false, Fleet(("A", true), ("B", true)));

        Assert.Equal(new[] { "A" }, result);
    }

    [Fact]
    public void Compute_ShowAllOn_IncludesTaxiingMinusHidden()
    {
        var result = GroundViewModel.ComputeVisibleTaxiRouteCallsigns(Set(), Set("B"), showAll: true, Fleet(("A", true), ("B", true), ("C", false)));

        // A taxiing and not hidden -> shown; B hidden -> out; C has no active route -> out.
        Assert.Equal(new[] { "A" }, result);
    }

    [Fact]
    public void Compute_ForcedShownWinsOverHidden_AndNoDuplicates()
    {
        var result = GroundViewModel.ComputeVisibleTaxiRouteCallsigns(Set("A"), Set("A"), showAll: true, Fleet(("A", true), ("B", true)));

        // A explicitly shown (wins over an overlapping hide) and listed once; B added via show-all.
        Assert.Equal(new[] { "A", "B" }, result);
    }

    [Fact]
    public void Compute_ShowAllOff_IgnoresHiddenSetAndFleet()
    {
        var result = GroundViewModel.ComputeVisibleTaxiRouteCallsigns(Set(), Set("A"), showAll: false, Fleet(("A", true), ("B", true)));

        Assert.Empty(result);
    }

    // --- Instance state machine via IsTaxiRouteVisible / ToggleTaxiRouteVisibility (no geometry) ---

    private static GroundViewModel BuildVm(params AircraftModel[] aircraft)
    {
        var list = aircraft.ToList();
        var vm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);
        vm.SetAircraftLookup(cs => list.FirstOrDefault(a => a.Callsign == cs));
        vm.SetAircraftProvider(() => list);
        return vm;
    }

    private static AircraftModel Taxiing(string callsign) => new() { Callsign = callsign, HasActiveTaxiRoute = true };

    [AvaloniaFact]
    public void Mode_ShowAllOff_AlwaysShowThenFollow()
    {
        var vm = BuildVm(Taxiing("A"));

        Assert.Equal(TaxiRouteDisplayMode.Follow, vm.GetTaxiRouteMode("A"));
        Assert.False(vm.IsTaxiRouteVisible("A"));

        vm.SetTaxiRouteMode("A", TaxiRouteDisplayMode.AlwaysShow);
        Assert.True(vm.IsTaxiRouteVisible("A"));

        vm.SetTaxiRouteMode("A", TaxiRouteDisplayMode.Follow);
        Assert.False(vm.IsTaxiRouteVisible("A")); // follow + show-all off -> hidden
    }

    [AvaloniaFact]
    public void Mode_ShowAllOn_FollowVisible_AlwaysHideOptsOut()
    {
        var vm = BuildVm(Taxiing("A"));
        vm.ShowAllTaxiRoutes = true;

        Assert.True(vm.IsTaxiRouteVisible("A")); // follow + show-all on -> visible

        vm.SetTaxiRouteMode("A", TaxiRouteDisplayMode.AlwaysHide);
        Assert.False(vm.IsTaxiRouteVisible("A"));

        // The opt-out survives a refresh (as aircraft-update batches stream in).
        vm.RefreshShownTaxiRoutes();
        Assert.False(vm.IsTaxiRouteVisible("A"));

        vm.SetTaxiRouteMode("A", TaxiRouteDisplayMode.Follow);
        Assert.True(vm.IsTaxiRouteVisible("A")); // back to follow -> visible again
    }

    [AvaloniaFact]
    public void Mode_AlwaysShow_SurvivesShowAllTurnedOff()
    {
        var vm = BuildVm(Taxiing("A"));
        vm.ShowAllTaxiRoutes = true;
        vm.SetTaxiRouteMode("A", TaxiRouteDisplayMode.AlwaysShow);

        vm.ShowAllTaxiRoutes = false;

        // The explicit pin persists even after the global setting is turned off (the tri-state's point).
        Assert.True(vm.IsTaxiRouteVisible("A"));
        Assert.Equal(TaxiRouteDisplayMode.AlwaysShow, vm.GetTaxiRouteMode("A"));
    }

    [AvaloniaFact]
    public void IsVisible_ShowAllOn_NonTaxiingNotShown()
    {
        var vm = BuildVm(new AircraftModel { Callsign = "A", HasActiveTaxiRoute = false });
        vm.ShowAllTaxiRoutes = true;

        Assert.False(vm.IsTaxiRouteVisible("A"));
    }

    // --- Transient hover overlay (geometry-backed) ---

    private static GroundViewModel BuildLinearTaxiwayVm()
    {
        const double lat = 37.62;
        const double lon = -122.38;

        var dto = new GroundLayoutDto(
            "TST",
            [
                new GroundNodeDto(1, lat, lon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, lat + 0.001, lon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(3, lat + 0.002, lon, "TaxiwayIntersection", null, null, null),
            ],
            [
                new GroundEdgeDto(1, 2, "C", DistanceNm: 0.06, IntermediatePoints: null),
                new GroundEdgeDto(2, 3, "C", DistanceNm: 0.06, IntermediatePoints: null),
            ],
            null,
            null
        );

        var ac = new AircraftModel
        {
            Callsign = "A",
            Position = new LatLon(lat, lon),
            TaxiRoute = "C",
            HasActiveTaxiRoute = true,
        };

        var vm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);
        vm.SetLayoutForTesting(dto);
        vm.SetAircraftLookup(cs => cs == "A" ? ac : null);
        vm.SetAircraftProvider(() => new List<AircraftModel> { ac });
        return vm;
    }

    [AvaloniaFact]
    public void Hover_RevealsRouteEvenWhenHidden_AndClearsWhenDisabled()
    {
        var vm = BuildLinearTaxiwayVm();
        vm.SetTaxiRouteMode("A", TaxiRouteDisplayMode.AlwaysHide); // opt A out of the persistent overlay
        Assert.False(vm.IsTaxiRouteVisible("A"));

        // Hover reveals the route even for an opted-out aircraft (transient, explicit gesture).
        vm.ShowTaxiRouteOnHover = true;
        vm.SetHoveredAircraft("A");
        Assert.NotNull(vm.HoverTaxiRoute);

        // Disabling the setting clears the transient route immediately.
        vm.ShowTaxiRouteOnHover = false;
        Assert.Null(vm.HoverTaxiRoute);

        // And hovering while disabled does nothing.
        vm.SetHoveredAircraft("A");
        Assert.Null(vm.HoverTaxiRoute);
    }

    [AvaloniaFact]
    public void Hover_ClearsWhenCursorLeaves()
    {
        var vm = BuildLinearTaxiwayVm();
        vm.ShowTaxiRouteOnHover = true;

        vm.SetHoveredAircraft("A");
        Assert.NotNull(vm.HoverTaxiRoute);

        vm.SetHoveredAircraft(null);
        Assert.Null(vm.HoverTaxiRoute);
    }
}
