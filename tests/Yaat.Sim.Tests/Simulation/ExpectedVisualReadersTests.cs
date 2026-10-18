using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The readers of an expected visual approach (<c>VIS{rwy}</c>) outside the dispatcher: the arrival stream's runway, the
/// scenario's runway signals and the spawn line all take the runway from the id, with no published approach needed.
/// </summary>
public class ExpectedVisualReadersTests
{
    public ExpectedVisualReadersTests() => TestVnasData.EnsureInitialized();

    private static string ScenarioWithPresets(params string[] presets) =>
        $$"""
            {
              "id": "expected-visual-readers",
              "name": "Expected visual readers",
              "artccId": "ZOA",
              "primaryAirportId": "OAK",
              "aircraft": [{
                "aircraftId": "N123",
                "aircraftType": "A320",
                "startingConditions": {
                  "type": "Coordinates",
                  "coordinates": { "lat": 37.735, "lon": -122.12 },
                  "altitude": 3000,
                  "speed": 180,
                  "heading": 295
                },
                "flightplan": {
                  "rules": "IFR", "departure": "KLAX", "destination": "KOAK", "cruiseAltitude": 30000, "cruiseSpeed": 450, "route": ""
                },
                "presetCommands": [ {{string.Join(", ", presets.Select((p, i) => $$"""{ "id": "p{{i}}", "command": "{{p}}", "timeOffset": 0 }"""))}} ]
              }]
            }
            """;

    /// <summary>
    /// An airborne arrival expecting the visual to KOAK 33, a runway with no instrument approach to resolve through CIFP, is
    /// in 33's arrival stream before any clearance.
    /// </summary>
    [Fact]
    public void ArrivalRunway_OfAnExpectedVisual_IsItsRunway()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "C172",
            Altitude = 3000,
            Position = new LatLon(37.80, -122.10),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        aircraft.Approach.Expected = "VIS33";

        RunwayInfo? runway = engine.ResolveArrivalRunway(aircraft);

        Assert.NotNull(runway);
        Assert.Equal("33", runway.Designator);
    }

    /// <summary>
    /// A preset <c>EAPP VIS28R</c> names no runway use itself, but a later bare <c>CAPP</c> or bare <c>CVA</c> clears that
    /// visual, so each sends the aircraft to 28R as an arrival.
    /// </summary>
    [Theory]
    [InlineData("CAPP")]
    [InlineData("CVA")]
    public void RunwaySignals_OfAClearanceAfterAnExpectedVisual_NameItsRunway(string clearance)
    {
        ScenarioLoadResult load = ScenarioLoader.Load(
            ScenarioWithPresets("EAPP VIS28R", clearance),
            null,
            new SerializableRandom(42),
            MagneticDeclination.EvaluationDateUtc
        );

        IReadOnlyList<AircraftRunwaySignal> signals = ScenarioRunwaySignals.AircraftRunways(Assert.Single(load.ImmediateAircraft));

        AircraftRunwaySignal signal = Assert.Single(signals);
        Assert.Equal("28R", signal.Runway.Designator);
        Assert.Equal(ActiveRunwayUse.Arrival, signal.Use);
    }

    [Theory]
    [InlineData("VIS28R", "Expecting visual approach runway 28R")]
    [InlineData("VIS1R", "Expecting visual approach runway 1R")]
    [InlineData("I28R", "Expecting I28R approach")]
    public void SpawnLine_NamesTheVisualsRunway(string expected, string line) => Assert.Equal(line, ApproachIds.ExpectingText(expected));
}
