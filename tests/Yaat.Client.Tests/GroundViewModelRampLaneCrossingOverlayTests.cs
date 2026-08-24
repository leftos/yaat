using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// Issue #396: while the pilot cuts across the SFO Terminal 1 ramp from gate B20S onto lane M4 (which the map
/// only joins to the field at M1), the aircraft's nearest graph node is still the gate / an M3 node and the named
/// route "M4 M1 A A1" does not resolve from it. The overlay reconstruction must fall back to the same free-space
/// leg the server planned, so the drawn route follows the crossing instead of vanishing until the aircraft is on M4.
/// </summary>
public class GroundViewModelRampLaneCrossingOverlayTests
{
    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static AirportGroundLayout? LoadSfoLayout()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    [Fact]
    public void CrossingFromGateOntoM4_OverlayStartsWithTheFreeSpaceLeg()
    {
        var layout = LoadSfoLayout();
        if (layout is null)
        {
            return; // test data absent — skip
        }

        var vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        var gate = layout.FindParkingByName("B20S")!;
        var ac = new AircraftModel
        {
            Callsign = "AAL436",
            AircraftType = "B77W",
            Position = gate.Position,
            Heading = gate.TrueHeading!.Value,
            CurrentTaxiway = "M4",
            TaxiRoute = "M4 M1 A A1",
            AssignedRunway = "1R",
        };

        var route = vm.ResolveRemainingRoute(ac);

        Assert.NotNull(route);
        Assert.True(route!.Segments[0].FromNodeId < 0, "the overlay must start with the free-space leg from the aircraft");
        Assert.Equal(gate.Position, route.Segments[0].Edge.FromNode.Position);
        Assert.Equal("M4", route.Segments[0].TaxiwayName);
        Assert.Contains(route.Segments, s => s.TaxiwayName.Contains("M1", StringComparison.OrdinalIgnoreCase));
    }
}
