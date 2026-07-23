using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="SimulationEngine.TickSoloTrainingEvaluation"/> — the engine-owned solo-training evaluation
/// pass that builds the eval context from the active scenario, runs the evaluator, and returns the events
/// for a host to broadcast. The server calls it in place of hand-building the context itself.
/// </summary>
public class SoloTrainingEvaluationTickTests
{
    [Fact]
    public void TickSoloTrainingEvaluation_NotSoloMode_ReturnsEmpty()
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: false, elapsedSeconds: 100) };
        engine.World.AddAircraft(IfrAircraft("AAL1", new LatLon(37.6213, -122.3790)));
        engine.World.AddAircraft(IfrAircraft("UAL2", GeoMath.ProjectPoint(new LatLon(37.6213, -122.3790), new TrueHeading(90), 2.5)));

        Assert.Empty(engine.TickSoloTrainingEvaluation());
    }

    [Fact]
    public void TickSoloTrainingEvaluation_SoloModeIfrPairInsideThreeMiles_ReturnsSeparationEvent()
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 100) };
        var a = IfrAircraft("AAL1", new LatLon(37.6213, -122.3790));
        var b = IfrAircraft("UAL2", GeoMath.ProjectPoint(a.Position, new TrueHeading(90), 2.5));
        engine.World.AddAircraft(a);
        engine.World.AddAircraft(b);

        var events = engine.TickSoloTrainingEvaluation();

        var separation = Assert.Single(events, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(SoloTrainingEventSeverity.Safety, separation.Severity);
    }

    private static AircraftState IfrAircraft(string callsign, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = 5000,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            HasMadeInitialContact = true,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                AircraftType = "B738",
                FlightRules = "IFR",
            },
        };

    private static SimScenarioState NewScenario(bool soloTrainingMode, double elapsedSeconds) =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsedSeconds,
            SoloTrainingMode = soloTrainingMode,
        };
}
