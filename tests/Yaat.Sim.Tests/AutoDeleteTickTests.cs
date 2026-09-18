using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="SimulationEngine.TickAutoDelete"/> — the engine-owned auto-delete pass that decides which
/// aircraft to remove, removes them, and returns the removed states so a host can fan out delete
/// broadcasts. The server calls it in place of running the delete decision + removal itself.
/// </summary>
public class AutoDeleteTickTests
{
    [Fact]
    public void TickAutoDelete_PendingAutoDelete_RemovesAndReturnsState()
    {
        SimulationEngine engine = EngineWithMode(null);
        AircraftState ac = Aircraft("N1");
        ac.Ground.PendingAutoDelete = true;
        engine.World.AddAircraft(ac);

        IReadOnlyList<AircraftState> removed = engine.TickAutoDelete();

        Assert.Same(ac, Assert.Single(removed));
        Assert.Null(engine.World.FindAircraft("N1"));
    }

    [Fact]
    public void TickAutoDelete_AutoDeleteExemptWithoutPending_IsNotRemoved()
    {
        SimulationEngine engine = EngineWithMode("OnLanding");
        AircraftState ac = Aircraft("N1");
        ac.Ground.AutoDeleteExempt = true;
        engine.World.AddAircraft(ac);

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft("N1"));
    }

    [Fact]
    public void TickAutoDelete_PendingAutoDelete_BypassesExempt()
    {
        SimulationEngine engine = EngineWithMode("None");
        AircraftState ac = Aircraft("N1");
        ac.Ground.AutoDeleteExempt = true;
        ac.Ground.PendingAutoDelete = true;
        engine.World.AddAircraft(ac);

        Assert.Same(ac, Assert.Single(engine.TickAutoDelete()));
    }

    [Fact]
    public void TickAutoDelete_OverflightPastItsExitRadius_IsStampedTransited_AndRecorded()
    {
        TestVnasData.EnsureInitialized();
        SimulationEngine engine = EngineWithMode("OnLanding");
        engine.Scenario!.PrimaryAirportId = "KOAK";
        engine.Scenario.ElapsedSeconds = 600;
        (double oakLat, double oakLon) = NavigationDatabase.Instance.GetFixPosition("KOAK")!.Value;
        var oak = new LatLon(oakLat, oakLon);
        AircraftState transit = Aircraft("N1");
        transit.IsGeneratedOverflight = true;
        transit.OverflightExitDistanceNm = 20;
        transit.Position = GeoMath.ProjectPoint(oak, new TrueHeading(90), 25);
        transit.SpawnedAtSeconds = 100;
        engine.World.AddAircraft(transit);

        Assert.Same(transit, Assert.Single(engine.TickAutoDelete()));

        Assert.Equal(CompletionReason.Transited, transit.CompletionReason);
        Assert.Equal(600, transit.CompletedAtSeconds);
        CompletedAircraftRecord record = Assert.Single(engine.World.GetCompletedAircraft());
        Assert.Equal("N1", record.Callsign);
        Assert.Equal(CompletionReason.Transited, record.Reason);
    }

    [Fact]
    public void TickAutoDelete_OverflightStillInsideItsExitRadius_Stays()
    {
        TestVnasData.EnsureInitialized();
        SimulationEngine engine = EngineWithMode("OnLanding");
        engine.Scenario!.PrimaryAirportId = "KOAK";
        (double oakLat, double oakLon) = NavigationDatabase.Instance.GetFixPosition("KOAK")!.Value;
        var oak = new LatLon(oakLat, oakLon);
        AircraftState transit = Aircraft("N1");
        transit.IsGeneratedOverflight = true;
        transit.OverflightExitDistanceNm = 20;
        transit.Position = GeoMath.ProjectPoint(oak, new TrueHeading(90), 15);
        engine.World.AddAircraft(transit);

        Assert.Empty(engine.TickAutoDelete());
        Assert.Equal(CompletionReason.Active, transit.CompletionReason);
    }

    [Theory]
    [InlineData(null, true, "Parked")]
    [InlineData("None", true, "Parked")]
    [InlineData("none", true, "Parked")]
    [InlineData("None", false, "None")]
    [InlineData(null, false, null)]
    [InlineData("OnLanding", true, "OnLanding")]
    [InlineData("Parked", false, "Parked")]
    public void EffectiveAutoDeleteMode_UnsetModeDefaultsToParked_OnlyWhileTrafficKeepsComing(string? scenarioMode, bool ongoing, string? expected)
    {
        var scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            ScenarioAutoDeleteMode = scenarioMode,
            HasOngoingTrafficSource = ongoing,
        };

        Assert.Equal(expected, scenario.EffectiveAutoDeleteMode);

        // The client's choice always wins.
        scenario.ClientAutoDeleteOverride = "Never";
        Assert.Equal("Never", scenario.EffectiveAutoDeleteMode);
    }

    [Fact]
    public void TickAutoDelete_DerivedParkedDefault_SweepsAParkedArrival_ButNotOnAStaticScenario()
    {
        foreach (bool ongoing in new[] { true, false })
        {
            SimulationEngine engine = EngineWithMode("None");
            engine.Scenario!.HasOngoingTrafficSource = ongoing;
            engine.Scenario.PrimaryAirportId = "OAK";
            AircraftState arrival = Aircraft("N1");
            arrival.FlightPlan.Departure = "KSFO";
            arrival.FlightPlan.Destination = "KOAK";
            arrival.Phases = new Yaat.Sim.Phases.PhaseList();
            arrival.Phases.Add(new Yaat.Sim.Phases.Ground.AtParkingPhase());
            engine.World.AddAircraft(arrival);

            IReadOnlyList<AircraftState> removed = engine.TickAutoDelete();

            Assert.Equal(ongoing ? 1 : 0, removed.Count);
            Assert.Equal(ongoing, engine.World.FindAircraft("N1") is null);
        }
    }

    [Fact]
    public void LoadScenario_RecordsWhetherTrafficKeepsComing_AndItSurvivesASnapshot()
    {
        TestVnasData.EnsureInitialized();
        var staticEngine = new SimulationEngine(new TestAirportGroundData());
        staticEngine.LoadScenario(ControllerAi.AiTestFixture.ParkedAtOak, 1, MagneticDeclination.EvaluationDateUtc);
        Assert.False(staticEngine.Scenario!.HasOngoingTrafficSource);
        Assert.Null(staticEngine.Scenario.EffectiveAutoDeleteMode);

        string timedJson = ControllerAi.AiTestFixture.ParkedAtOak.Replace(
            "\"aircraft\": [",
            "\"aircraft\": [ { \"id\": \"a0\", \"aircraftId\": \"N2AR\", \"aircraftType\": \"C172\", \"transponderMode\": \"C\", \"spawnDelay\": 600, "
                + "\"startingConditions\": { \"type\": \"Parking\", \"parking\": \"SIG2\" }, "
                + "\"flightplan\": { \"rules\": \"VFR\", \"departure\": \"KOAK\", \"destination\": \"KOAK\", \"cruiseAltitude\": 1500, \"cruiseSpeed\": 100, \"route\": \"\", \"remarks\": \"\", \"aircraftType\": \"C172\" } },"
        );
        var timedEngine = new SimulationEngine(new TestAirportGroundData());
        timedEngine.LoadScenario(timedJson, 1, MagneticDeclination.EvaluationDateUtc);
        Assert.True(timedEngine.Scenario!.HasOngoingTrafficSource);
        Assert.Equal("Parked", timedEngine.Scenario.EffectiveAutoDeleteMode);

        StateSnapshotDto snapshot = timedEngine.CaptureSnapshot();
        Assert.True(snapshot.Scenario.HasOngoingTrafficSource);
        staticEngine.RestoreFromSnapshot(snapshot);
        Assert.True(staticEngine.Scenario.HasOngoingTrafficSource);
        Assert.Equal("Parked", staticEngine.Scenario.EffectiveAutoDeleteMode);
    }

    /// <summary>
    /// Issue #429: at an airport with no ground layout the arrival stops on the runway and holds there —
    /// it cannot taxi anywhere. Under <c>OnLanding</c> that holding arrival is the mode's own delete case,
    /// and it is what <see cref="Yaat.Sim.Phases.Ground.RunwayExitPhase"/> now hands the engine when it
    /// completes without a layout.
    /// </summary>
    [Fact]
    public void TickAutoDelete_OnLanding_LayoutlessArrivalHoldingAfterExit_IsRemoved()
    {
        TestVnasData.EnsureInitialized();
        SimulationEngine engine = EngineWithMode("OnLanding");
        engine.Scenario!.PrimaryAirportId = "KFAT";
        AircraftState arrival = Aircraft("N200WM");
        arrival.IsOnGround = true;
        arrival.FlightPlan.Departure = "KSFO";
        arrival.FlightPlan.Destination = "KFAT";
        arrival.Phases = new Yaat.Sim.Phases.PhaseList();
        arrival.Phases.Add(new Yaat.Sim.Phases.Ground.HoldingAfterExitPhase());
        engine.World.AddAircraft(arrival);

        Assert.Null(engine.World.GroundLayout);

        Assert.Same(arrival, Assert.Single(engine.TickAutoDelete()));
        Assert.Null(engine.World.FindAircraft("N200WM"));
    }

    private static AircraftState Aircraft(string callsign) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = new LatLon(37.72, -122.22),
            FlightPlan = new AircraftFlightPlan(),
        };

    private static SimulationEngine EngineWithMode(string? autoDeleteMode) =>
        new(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
                ScenarioAutoDeleteMode = autoDeleteMode,
            },
        };
}
