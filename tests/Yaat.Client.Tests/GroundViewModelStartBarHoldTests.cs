using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// The Ground View's route reconstruction request carries the aircraft's start pose (position as main gear, heading, type body),
/// the same pose the simulation's taxi clearance resolves with. Real OAK layout.
/// </summary>
public class GroundViewModelStartBarHoldTests
{
    public GroundViewModelStartBarHoldTests() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

    /// <summary>
    /// The remaining-route request for a C172 on OAK's B carries the pose the simulation builds for it: its position as the
    /// main gear, its heading and its type's body.
    /// </summary>
    [Fact]
    public void RemainingRouteRequest_CarriesTheAircraftsStartPose()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        AirportGroundLayout layout = GeoJsonParser.Parse(
            "OAK",
            File.ReadAllText(path),
            null,
            new LayoutInputs([], VideoMap: null),
            FilletMode.Standard
        );
        GroundNode onB = layout.Nodes.Values.Where(n => n.Edges.Any(e => e.TaxiwayName == "B")).OrderBy(n => n.Id).First();
        var ac = new AircraftModel
        {
            Callsign = "N172SP",
            AircraftType = "C172",
            Position = onB.Position,
            Heading = new TrueHeading(163.0),
            CurrentTaxiway = "B",
            TaxiRoute = "B",
            AssignedRunway = "",
        };

        GroundViewModel.RemainingRouteRequest request = Assert.IsType<GroundViewModel.RemainingRouteRequest>(
            GroundViewModel.RemainingRouteRequestFor(layout, ac)
        );

        Assert.Equal(StartPose.Of(ac.Position, ac.Heading.Degrees, ac.AircraftType), request.Options.StartPose);
    }

    /// <summary>
    /// A preset taxi row is resolved from the aircraft's own start, so its options carry the aircraft's pose: its position as
    /// the main gear, its heading and its type's body.
    /// </summary>
    [Fact]
    public void PresetTaxiRow_CarriesTheAircraftsStartPose()
    {
        var ac = new AircraftModel
        {
            Callsign = "N172SP",
            AircraftType = "C172",
            Position = new LatLon(37.7213, -122.2208),
            Heading = new TrueHeading(163.0),
        };
        var preset = new TaxiRouteDefinition
        {
            AirportId = "OAK",
            Name = "B to 30",
            Path = "B",
            DestinationRunway = "30",
        };

        ExplicitPathOptions options = GroundViewModel.PresetTaxiRowOptions(ac, preset);

        Assert.Equal(StartPose.Of(ac.Position, ac.Heading.Degrees, ac.AircraftType), options.StartPose);
    }
}
