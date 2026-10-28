using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A TAXI given to an aircraft standing well short of an uncleared runway bar, on the straight taxiway edge that ends at the
/// bar's node, stops the aircraft with its nose at the holding position marking (AIM 2-3-5.a.1): the bar's painted stop is set
/// back half the fuselage from the bar node, as for any bar the route reaches, though the taxi start pick puts the route's
/// first node on the bar itself.
/// </summary>
public class TaxiFromShortOfARunwayBarTests
{
    private const string Callsign = "N738SP";

    /// <summary>How far short of the bar node the aircraft stands, ft: well beyond a B738's half fuselage.</summary>
    private const double StartShortOfBarFt = 250.0;

    private const double StopToleranceFt = 1.0;

    private const double NoseOverLineToleranceFt = 1.0;

    private const int RestBudgetSeconds = 120;

    private const string ParkedAtGa16 = """{ "type": "Parking", "parking": "GA16" }""";

    private const string Scenario = """
        {
          "id": "01JH02G8F0L1W5T7R9N2K4X6QC",
          "name": "TAXI from well short of an uncleared runway bar",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "studentPositionId": "01GEAMCGAZ418Q26GEPYNXWZ4A",
          "autoDeleteMode": "None",
          "initializationTriggers": [],
          "aircraftGenerators": [],
          "aircraft": [
            {
              "id": "01JH02N738SPG8F0L1W5T7R9ND",
              "aircraftId": "N738SP",
              "aircraftType": "B738",
              "transponderMode": "Standby",
              "startingConditions": { "type": "Parking", "parking": "GA16" },
              "onAltitudeProfile": false,
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KLVK",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 0,
                "route": "",
                "remarks": "/V/",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ],
          "atc": [
            {
              "id": "01G9CQW3XEE3RW84EBYKEBCPQD",
              "artccId": "ZOA",
              "facilityId": "OAK",
              "positionId": "01GEAMB98RKCPP9HCNPW5AVDA5",
              "autoConnect": true,
              "autoTrackAirportIds": []
            }
          ],
          "flightStripConfigurations": []
        }
        """;

    public TaxiFromShortOfARunwayBarTests() => TestVnasData.EnsureInitialized();

    /// <summary>
    /// KOAK runway 33's bars on K and on C (west of the runway) each end a straight approach edge hundreds of feet long, so a
    /// TAXI from 250 ft short of either starts the route on the bar's node. The aircraft must still stop with its nose at the
    /// marking, its centre half a fuselage short of the bar.
    /// </summary>
    [Theory]
    [InlineData("K", "TAXI K")]
    [InlineData("C", "TAXI C B RWY 28R")]
    public void TaxiFromWellShortOfAnUnclearedRunwayBar_StopsTheNoseAtTheMarking(string taxiway, string taxi)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, double awayDeg) = RunwayBarAtTheEndOfItsLongestApproachEdge(layout, "33", taxiway);
        LatLon start = GeoMath.ProjectPoint(bar.Position, new TrueHeading(awayDeg), StartShortOfBarFt / GeoMath.FeetPerNm);
        double towardDeg = GeoMath.BearingTo(start, bar.Position);
        double headingMag = MagneticDeclination.TrueToMagnetic(towardDeg, start, MagneticDeclination.EvaluationDateUtc);
        string startingConditions =
            $$"""{ "type": "Coordinates", "coordinates": { "lat": {{start.Lat:R}}, "lon": {{start.Lon:R}} }, "heading": {{headingMag:R}} }""";
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(Scenario.Replace(ParkedAtGa16, startingConditions, StringComparison.Ordinal), 1, MagneticDeclination.EvaluationDateUtc);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        double halfLengthFt = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0;

        Assert.True(engine.SendCommand(Callsign, taxi).Success, $"{taxi} was rejected");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint hold = Assert.IsType<HoldShortPoint>(route.GetHoldShortAt(bar.Id));
        Assert.False(hold.IsCleared, $"{taxi} cleared runway 33 at node {bar.Id}");
        var stop = new LatLon(Assert.IsType<double>(hold.Latitude), Assert.IsType<double>(hold.Longitude));
        double stopShortOfBarFt = GeoMath.DistanceNm(stop, bar.Position) * GeoMath.FeetPerNm;
        Assert.True(
            Math.Abs(stopShortOfBarFt - halfLengthFt) <= StopToleranceFt,
            $"{taxi} from {StartShortOfBarFt:F0} ft short of bar node {bar.Id} on {taxiway} puts its stop {stopShortOfBarFt:F1} ft from the "
                + $"node, not half the fuselage ({halfLengthFt:F1} ft); the route starts at node {route.Segments[0].FromNodeId}"
        );

        for (int second = 0; (second < RestBudgetSeconds) && !((second > 5) && (aircraft.GroundSpeed < 0.01)); second++)
        {
            engine.TickOneSecond();
        }

        LatLon nose = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, halfLengthFt / GeoMath.FeetPerNm);
        double noseFromBarFt = GeoMath.DistanceNm(bar.Position, nose) * GeoMath.FeetPerNm;
        double offRad = GeoMath.SignedBearingDifference(awayDeg, GeoMath.BearingTo(bar.Position, nose)) * Math.PI / 180.0;
        double nosePastFt = -(noseFromBarFt * Math.Cos(offRad));
        Assert.True(
            aircraft.GroundSpeed < 0.01,
            $"the B738 is still rolling at {aircraft.GroundSpeed:F2} kt after the {RestBudgetSeconds} s budget, its nose {nosePastFt:F1} ft "
                + $"past runway 33's holding position marking on {taxiway}"
        );
        Assert.True(
            nosePastFt <= NoseOverLineToleranceFt,
            $"the B738 came to rest with its nose {nosePastFt:F1} ft past runway 33's holding position marking on {taxiway} (gs "
                + $"{aircraft.GroundSpeed:F2}, phase {aircraft.Phases?.CurrentPhase?.Name})"
        );
        Assert.True(
            nosePastFt >= (-StopToleranceFt - 3.0),
            $"the B738 came to rest with its nose {-nosePastFt:F1} ft short of runway 33's holding position marking on {taxiway}, " + "not at it"
        );
    }

    /// <summary>
    /// The hold-line bar of <paramref name="runway"/> on <paramref name="taxiway"/> whose straight edge away from the runway is
    /// the longest, and the bearing (deg true) from the bar along that edge.
    /// </summary>
    private static (GroundNode Bar, double AwayDeg) RunwayBarAtTheEndOfItsLongestApproachEdge(
        AirportGroundLayout layout,
        string runway,
        string taxiway
    )
    {
        GroundRunway ground = layout.Runways.Single(r => r.Id.Contains(runway));
        var axisStart = new LatLon(ground.Coordinates[0].Lat, ground.Coordinates[0].Lon);
        var axisEnd = new LatLon(ground.Coordinates[^1].Lat, ground.Coordinates[^1].Lon);
        var heading = new TrueHeading(GeoMath.BearingTo(axisStart, axisEnd));
        double CrossFt(LatLon point) => Math.Abs(GeoMath.SignedCrossTrackDistanceNm(point, axisStart, heading)) * GeoMath.FeetPerNm;

        List<(GroundNode Bar, GroundNode Outboard)> pairs =
        [
            .. layout
                .GetRunwayHoldShortNodes(runway)
                .Where(node => node.IsShortBar == false)
                .SelectMany(node =>
                    node.Edges.OfType<GroundEdge>()
                        .Where(edge => edge.MatchesTaxiway(taxiway) && (CrossFt(edge.OtherNode(node).Position) > CrossFt(node.Position)))
                        .Select(edge => (Bar: node, Outboard: edge.OtherNode(node)))
                ),
        ];
        Assert.True(pairs.Count > 0, $"no runway {runway} bar on {taxiway} has a straight {taxiway} edge leading away from the runway");
        (GroundNode bar, GroundNode outboard) = pairs.MaxBy(pair => GeoMath.DistanceNm(pair.Bar.Position, pair.Outboard.Position));
        return (bar, GeoMath.BearingTo(bar.Position, outboard.Position));
    }
}
