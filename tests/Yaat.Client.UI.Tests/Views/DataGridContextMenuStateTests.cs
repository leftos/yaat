using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the context-menu cleanup: the aircraft-list right-click menu must only
// offer commands that fit the aircraft's state. Previously an airborne aircraft was shown
// a "Tower" submenu containing "Cleared for takeoff" (and landing departures showed landing
// clearances). The phase-aware items now flow through AircraftCommandApplicability.
public class DataGridContextMenuStateTests
{
    private static List<string> Headers(ContextMenu menu)
    {
        return menu.Items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!).ToList();
    }

    private static ContextMenu Build(AircraftModel ac)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var menu = new ContextMenu();
        DataGridView.AddPhaseAwareItems(menu, ac, vm, ac.Callsign, "AB");
        return menu;
    }

    [AvaloniaFact]
    public void AirborneIfrOnFinal_ShowsLandingAndGoAround_NotDepartures()
    {
        var menu = Build(
            new AircraftModel
            {
                Callsign = "AAL123",
                IsOnGround = false,
                CurrentPhase = "FinalApproach",
                FlightRules = "IFR",
                AssignedRunway = "28R",
            }
        );

        var headers = Headers(menu);
        Assert.Contains("Cleared to land 28R", headers);
        Assert.Contains("Go around 28R", headers);

        // No departure clearances for an arriving aircraft.
        Assert.DoesNotContain(headers, h => h.StartsWith("Line up and wait", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));

        // VFR-only option clearances hidden for IFR.
        Assert.DoesNotContain(headers, h => h.StartsWith("Touch and go", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Stop and go", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Low approach", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for the option", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void AirborneVfrOnFinal_ShowsOptionClearances()
    {
        var menu = Build(
            new AircraftModel
            {
                Callsign = "N12345",
                IsOnGround = false,
                CurrentPhase = "FinalApproach",
                FlightRules = "VFR",
                AssignedRunway = "28L",
            }
        );

        var headers = Headers(menu);
        Assert.Contains("Cleared to land 28L", headers);
        Assert.Contains("Touch and go 28L", headers);
        Assert.Contains("Stop and go 28L", headers);
        Assert.Contains("Low approach 28L", headers);
        Assert.Contains("Cleared for the option 28L", headers);
    }

    [AvaloniaFact]
    public void AirborneDeparture_ShowsNoTowerClearances()
    {
        var menu = Build(
            new AircraftModel
            {
                Callsign = "UAL456",
                IsOnGround = false,
                CurrentPhase = "InitialClimb",
                FlightRules = "IFR",
                AssignedRunway = "1L",
            }
        );

        var headers = Headers(menu);
        Assert.DoesNotContain(headers, h => h.StartsWith("Line up and wait", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared to land", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Go around", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Exit ", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void GroundDeparture_ShowsTakeoffClearances_NotLanding()
    {
        var menu = Build(
            new AircraftModel
            {
                Callsign = "SWA789",
                IsOnGround = true,
                CurrentPhase = "LinedUpAndWaiting",
                FlightRules = "IFR",
                AssignedRunway = "30",
            }
        );

        var headers = Headers(menu);
        Assert.Contains("Cleared for takeoff 30", headers);
        Assert.Contains("Cancel takeoff clearance", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared to land", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Landing_ShowsExits_NotTowerClearances()
    {
        var menu = Build(
            new AircraftModel
            {
                Callsign = "DAL111",
                IsOnGround = true,
                CurrentPhase = "Landing",
                FlightRules = "IFR",
                AssignedRunway = "28R",
            }
        );

        var headers = Headers(menu);
        Assert.Contains("Exit left", headers);
        Assert.Contains("Exit right", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Line up and wait", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared for takeoff", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.StartsWith("Cleared to land", StringComparison.Ordinal));
    }
}
