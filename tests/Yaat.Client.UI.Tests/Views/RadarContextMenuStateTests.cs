using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the reported bug: an airborne aircraft showed "Cleared for takeoff" in the
// radar Tower submenu. The Tower/Pattern submenus are now state-aware and return null (and are
// omitted) when nothing applies. Builds run through AircraftCommandApplicability.
public class RadarContextMenuStateTests
{
    private static (RadarView View, RadarViewModel Vm) Harness()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        return (new RadarView(), main.Radar);
    }

    private static List<string> Headers(MenuItem menu)
    {
        return menu.Items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!).ToList();
    }

    [AvaloniaFact]
    public void AirborneIfrOnFinal_TowerHasLanding_NotTakeoff()
    {
        var (view, vm) = Harness();
        var ac = new AircraftModel
        {
            Callsign = "AAL123",
            IsOnGround = false,
            CurrentPhase = "FinalApproach",
            FlightRules = "IFR",
            AssignedRunway = "28R",
        };

        var tower = view.BuildTowerSubmenu(vm, "AAL123", "AB", ac);

        Assert.NotNull(tower);
        var headers = Headers(tower!);
        Assert.Contains("Cleared to land 28R", headers);
        Assert.Contains("Go around 28R", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Line up and wait", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        // VFR-only options hidden for IFR.
        Assert.DoesNotContain(headers, h => h.StartsWith("Touch and go", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void GroundDeparture_TowerHasTakeoff_NotLanding()
    {
        var (view, vm) = Harness();
        var ac = new AircraftModel
        {
            Callsign = "SWA1",
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            FlightRules = "IFR",
            AssignedRunway = "30",
        };

        var tower = view.BuildTowerSubmenu(vm, "SWA1", "AB", ac);

        Assert.NotNull(tower);
        var headers = Headers(tower!);
        Assert.Contains("Cancel takeoff clearance", headers);
        Assert.Contains(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared to land", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void AirborneDeparture_TowerOmitted()
    {
        var (view, vm) = Harness();
        var ac = new AircraftModel
        {
            Callsign = "UAL9",
            IsOnGround = false,
            CurrentPhase = "InitialClimb",
            FlightRules = "IFR",
            AssignedRunway = "1L",
        };

        var tower = view.BuildTowerSubmenu(vm, "UAL9", "AB", ac);

        // Nothing tower-related applies to a climbing departure — the submenu is dropped.
        Assert.Null(tower);
    }

    [AvaloniaFact]
    public void Landing_TowerHasExits()
    {
        var (view, vm) = Harness();
        var ac = new AircraftModel
        {
            Callsign = "DAL5",
            IsOnGround = true,
            CurrentPhase = "Landing",
            FlightRules = "IFR",
            AssignedRunway = "28R",
        };

        var tower = view.BuildTowerSubmenu(vm, "DAL5", "AB", ac);

        Assert.NotNull(tower);
        var headers = Headers(tower!);
        Assert.Contains("Exit left", headers);
        Assert.Contains("Exit right", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void IfrTakeoffClearance_HidesVfrModifiers()
    {
        var (view, vm) = Harness();
        var ac = new AircraftModel
        {
            Callsign = "AAL2",
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            FlightRules = "IFR",
            AssignedRunway = "30",
        };

        var tower = view.BuildTowerSubmenu(vm, "AAL2", "AB", ac);
        Assert.NotNull(tower);

        var cto = tower!.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header is "Cleared for takeoff");
        Assert.NotNull(cto);
        var ctoHeaders = Headers(cto!);
        // IFR gets the default (follow-SID) clearance and an explicit runway-heading clearance (issue #221).
        Assert.Contains("Default (SID/on course)", ctoHeaders);
        Assert.Contains("Fly runway heading", ctoHeaders);
        // On-course and pattern modifiers are VFR-only — hidden for IFR.
        Assert.DoesNotContain("Fly on course", ctoHeaders);
        Assert.DoesNotContain("Make left traffic", ctoHeaders);
        Assert.DoesNotContain("360 overhead", ctoHeaders);
    }

    [AvaloniaFact]
    public void VfrTakeoffClearance_ShowsRunwayHeadingAndOnCourseAndModifiers()
    {
        var (view, vm) = Harness();
        var ac = new AircraftModel
        {
            Callsign = "N123",
            IsOnGround = true,
            CurrentPhase = "LinedUpAndWaiting",
            FlightRules = "VFR",
            AssignedRunway = "30",
        };

        var tower = view.BuildTowerSubmenu(vm, "N123", "AB", ac);
        Assert.NotNull(tower);

        var cto = tower!.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header is "Cleared for takeoff");
        Assert.NotNull(cto);
        var ctoHeaders = Headers(cto!);
        Assert.Contains("Default (SID/on course)", ctoHeaders);
        Assert.Contains("Fly runway heading", ctoHeaders);
        Assert.Contains("Fly on course", ctoHeaders);
        Assert.Contains("Make left traffic", ctoHeaders);
        Assert.Contains("360 overhead", ctoHeaders);
    }

    [AvaloniaFact]
    public void VfrPatternAircraft_PatternSubmenuLegGated()
    {
        var (view, vm) = Harness();
        var ac = new AircraftModel
        {
            Callsign = "N77",
            IsOnGround = false,
            CurrentPhase = "Upwind",
            FlightRules = "VFR",
            AssignedRunway = "28L",
        };

        var pattern = view.BuildPatternSubmenu(vm, "N77", "AB", ac);

        Assert.NotNull(pattern);
        var headers = Headers(pattern!);
        // From upwind only the crosswind turn is valid.
        Assert.Contains("Turn crosswind", headers);
        Assert.DoesNotContain("Turn downwind", headers);
        Assert.DoesNotContain("Turn base", headers);
    }

    [AvaloniaFact]
    public void IfrAircraft_PatternSubmenuOmitted()
    {
        var (view, vm) = Harness();
        var ac = new AircraftModel
        {
            Callsign = "AAL3",
            IsOnGround = false,
            CurrentPhase = "ApproachNav",
            FlightRules = "IFR",
            AssignedRunway = "28R",
        };

        var pattern = view.BuildPatternSubmenu(vm, "AAL3", "AB", ac);

        // Pattern ops are VFR-only.
        Assert.Null(pattern);
    }
}
