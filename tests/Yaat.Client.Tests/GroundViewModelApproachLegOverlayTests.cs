using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// The server bridges the gap between where an aircraft actually stands and where its resolved route starts
/// with a free-space RAMP leg (<see cref="TaxiApproachLeg"/>) — a plain PUSH leaves an aircraft out on the
/// apron ahead of the ramp node the pathfinder picks up from. The client never receives route geometry, so
/// the overlay reconstruction has to rebuild the same leg or the drawn route starts a hundred feet away from
/// the aircraft symbol instead of at its nose.
/// </summary>
public class GroundViewModelApproachLegOverlayTests
{
    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    [Fact]
    public void PushedOntoTheApron_OverlayStartsWithTheApproachLegFromTheAircraft()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return; // test data absent — skip
        }

        var vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        var position = new LatLon(37.710217680439534, -122.21728593336832);
        var ac = new AircraftModel
        {
            Callsign = "DAL2150",
            AircraftType = "B738",
            Position = position,
            Heading = new TrueHeading(53),
            CurrentTaxiway = "RAMP",
            TaxiRoute = "T U W W1",
            AssignedRunway = "30",
            HasActiveTaxiRoute = true,
        };

        var route = vm.ResolveRemainingRoute(ac);

        Assert.NotNull(route);
        Assert.True(route!.Segments[0].FromNodeId < 0, "the overlay must start with the free-space leg from the aircraft");

        double legStartFt = GeoMath.DistanceNm(position, route.Segments[0].Edge.FromNode.Position) * GeoMath.FeetPerNm;
        Assert.True(legStartFt < 1.0, $"the leg must start at the aircraft, but starts {legStartFt:F1} ft away");

        var startNode = layout.FindNearestNode(position);
        Assert.NotNull(startNode);
        Assert.Equal(startNode!.Id, route.Segments[0].ToNodeId);
    }
}
