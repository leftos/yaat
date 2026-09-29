using System.Text.Json;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="SimulationEngine.TickEramVerticalConformance"/> latches <see cref="AircraftEramState.ReachedAssignedAltitude"/>
/// once the measured altitude is within 200 ft of the assigned altitude (the block, or anything above for ABV), holds it
/// when the aircraft leaves the band, and clears it only when the assignment changes. It does not evaluate while the
/// transponder is in standby or the track is coasted or frozen, and the latch survives a snapshot.
/// </summary>
public class EramVerticalConformanceTests
{
    private const string Callsign = "AAL123";

    private static readonly TrueHeading East = new(90);
    private static readonly TrueHeading West = new(270);

    public EramVerticalConformanceTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LatLon Sjc =>
        NavigationDatabase.Instance.GetFixPosition("SJC") is { } sjc
            ? new LatLon(sjc.Lat, sjc.Lon)
            : throw new InvalidOperationException("SJC is not in NavData");

    [Theory]
    [InlineData(17000, true)]
    [InlineData(16800, true)]
    [InlineData(17200, true)]
    [InlineData(16799, false)]
    [InlineData(17201, false)]
    public void Latch_SetsWithin200Feet(double altitude, bool reached)
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), altitude);

        engine.TickEramVerticalConformance();

        Assert.Equal(reached, ac.Eram.ReachedAssignedAltitude);
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(18000)]
    public void Latch_StaysSet_AfterLeavingTheBand(double altitude)
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), 17000);
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        ac.Altitude = altitude;
        engine.TickEramVerticalConformance();

        Assert.True(ac.Eram.ReachedAssignedAltitude);
    }

    [Fact]
    public void Latch_ClearsWhenTheAssignedAltitudeChanges()
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), 17000);
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(false, Altitude: PlannedAltitude.Ifr(19000)));
        engine.TickEramVerticalConformance();
        Assert.False(ac.Eram.ReachedAssignedAltitude);

        ac.Altitude = 18850;
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);
    }

    [Fact]
    public void Latch_ClearsOnEramAssignedAltitude()
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), 17000);
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        ac.FlightPlan.AssignEramAltitude(21000);
        engine.TickEramVerticalConformance();

        Assert.False(ac.Eram.ReachedAssignedAltitude);
    }

    [Fact]
    public void Latch_ClearsWhenTheAfterFixAltitudeTakesOver()
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.UntilFix(17000, "SJC", 11000), 17000);
        FlyEastPastSjc(engine, ac, [-1]);
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        FlyEastPastSjc(engine, ac, [1]);

        Assert.True(ac.FlightPlan.AltitudeFixPassed);
        Assert.False(ac.Eram.ReachedAssignedAltitude);
    }

    [Theory]
    [InlineData(false, 17000, true)]
    [InlineData(false, 11000, false)]
    [InlineData(true, 11000, true)]
    [InlineData(true, 17000, false)]
    public void FixQualified_LatchComparesTheAltitudeInEffect(bool passed, double altitude, bool reached)
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.UntilFix(17000, "SJC", 11000), altitude);

        FlyEastPastSjc(engine, ac, passed ? [-1, 1] : [-1]);

        Assert.Equal(passed, ac.FlightPlan.AltitudeFixPassed);
        Assert.Equal(reached, ac.Eram.ReachedAssignedAltitude);
    }

    [Fact]
    public void SameValueReassignment_KeepsTheLatch()
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), 17000);
        engine.TickEramVerticalConformance();
        ac.Altitude = 16000;
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(false, Altitude: PlannedAltitude.Ifr(17000)));
        engine.TickEramVerticalConformance();

        Assert.True(ac.Eram.ReachedAssignedAltitude);
    }

    [Theory]
    [InlineData(16000, true)]
    [InlineData(14800, true)]
    [InlineData(19200, true)]
    [InlineData(14799, false)]
    [InlineData(19201, false)]
    public void BlockAltitude_UsesFloorAndCeiling(double altitude, bool reached)
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Block(15000, 19000), altitude);

        engine.TickEramVerticalConformance();

        Assert.Equal(reached, ac.Eram.ReachedAssignedAltitude);
    }

    [Theory]
    [InlineData(14800, true)]
    [InlineData(15000, true)]
    [InlineData(41000, true)]
    [InlineData(14799, false)]
    public void AbvAltitude_HasNoUpperBound(double altitude, bool reached)
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Above(15000), altitude);

        engine.TickEramVerticalConformance();

        Assert.Equal(reached, ac.Eram.ReachedAssignedAltitude);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void NoAssignedAltitude_IsConformed(int? cruiseFeet)
    {
        PlannedAltitude altitude = cruiseFeet is { } feet ? PlannedAltitude.Ifr(feet) : PlannedAltitude.None;
        (SimulationEngine engine, AircraftState ac) = Build(altitude, 5000);

        engine.TickEramVerticalConformance();

        Assert.True(ac.Eram.ReachedAssignedAltitude);
    }

    [Fact]
    public void SpawnAtAssignedAltitude_IsConformedOnTheFirstTick()
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), 17000);
        Assert.False(ac.Eram.ReachedAssignedAltitude);
        Assert.Null(ac.Eram.ConformanceKey);

        engine.TickEramVerticalConformance();

        Assert.True(ac.Eram.ReachedAssignedAltitude);
        Assert.Equal(new EramConformanceKey(17000, null, false), ac.Eram.ConformanceKey);
    }

    [Fact]
    public void Standby_DoesNotEvaluate()
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), 17000);
        ac.Transponder.Mode = "Standby";

        engine.TickEramVerticalConformance();
        Assert.False(ac.Eram.ReachedAssignedAltitude);
        Assert.Null(ac.Eram.ConformanceKey);

        ac.Transponder.Mode = "C";
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        // A reassignment during standby is not seen until Mode C returns.
        ac.Transponder.Mode = "Standby";
        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(false, Altitude: PlannedAltitude.Ifr(21000)));
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        ac.Transponder.Mode = "C";
        engine.TickEramVerticalConformance();
        Assert.False(ac.Eram.ReachedAssignedAltitude);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoastedOrFrozenTrack_DoesNotEvaluate(bool frozen)
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), 17000);
        SetCoastedOrFrozen(ac, frozen, true);

        engine.TickEramVerticalConformance();
        Assert.False(ac.Eram.ReachedAssignedAltitude);
        Assert.Null(ac.Eram.ConformanceKey);

        SetCoastedOrFrozen(ac, frozen, false);
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        // A reassignment while coasted or frozen is not seen until the track resumes.
        SetCoastedOrFrozen(ac, frozen, true);
        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(false, Altitude: PlannedAltitude.Ifr(21000)));
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        SetCoastedOrFrozen(ac, frozen, false);
        engine.TickEramVerticalConformance();
        Assert.False(ac.Eram.ReachedAssignedAltitude);
    }

    [Fact]
    public void FloorOnlyChange_ClearsTheLatch()
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Block(15000, 19000), 16000);
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(false, Altitude: PlannedAltitude.Ifr(19000)));
        engine.TickEramVerticalConformance();

        Assert.False(ac.Eram.ReachedAssignedAltitude);
    }

    [Fact]
    public void AboveFlagOnlyChange_ClearsTheLatch()
    {
        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(15000), 15000);
        engine.TickEramVerticalConformance();
        ac.Altitude = 14000;
        engine.TickEramVerticalConformance();
        Assert.True(ac.Eram.ReachedAssignedAltitude);

        engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(false, Altitude: PlannedAltitude.Above(15000)));
        engine.TickEramVerticalConformance();

        Assert.False(ac.Eram.ReachedAssignedAltitude);
    }

    private static void SetCoastedOrFrozen(AircraftState ac, bool frozen, bool on)
    {
        ac.Eram.IsFrozen = frozen && on;
        ac.Eram.IsCoastTrack = !frozen && on;
    }

    [Fact]
    public void Latch_RoundTripsThroughASnapshot()
    {
        (SimulationEngine engine, AircraftState live) = Build(PlannedAltitude.Ifr(17000), 17000);
        engine.TickEramVerticalConformance();
        live.Altitude = 16000;
        engine.TickEramVerticalConformance();
        Assert.True(live.Eram.ReachedAssignedAltitude);

        AircraftState restored = RestoreThroughJson(live);

        Assert.True(restored.Eram.ReachedAssignedAltitude);
        Assert.Equal(live.Eram.ConformanceKey, restored.Eram.ConformanceKey);

        // The restored latch holds outside the band: the key is unchanged, so nothing re-evaluates it away.
        SimulationEngine restoredEngine = EngineWith(restored);
        restoredEngine.TickEramVerticalConformance();
        Assert.True(restored.Eram.ReachedAssignedAltitude);
    }

    [Fact]
    public void OlderSnapshot_WithoutTheLatch_ReEvaluates()
    {
        // A current-schema aircraft's ERAM state written before the latch carries no conformance fields.
        string json = $$"""
            {
              "SchemaVersion": {{SnapshotSchemaMigrator.CurrentSchemaVersion}},
              "ElapsedSeconds": 10,
              "Rng": { "S0": 1, "S1": 2, "S2": 3, "S3": 4 },
              "Aircraft": [ { "Callsign": "AAL1", "AircraftType": "B738", "Eram": { "IsFrozen": false, "LeaderLength": 2 } } ],
              "Scenario": { "ScenarioId": "t", "ScenarioName": "T", "RngSeed": 1, "ElapsedSeconds": 10, "SimRate": 1 }
            }
            """;
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
        SnapshotSchemaMigrator.Migrate(snapshot);
        var eram = AircraftEramState.FromSnapshot(Assert.Single(snapshot.Aircraft).Eram);
        Assert.False(eram.ReachedAssignedAltitude);
        Assert.Null(eram.ConformanceKey);

        (SimulationEngine engine, AircraftState ac) = Build(PlannedAltitude.Ifr(17000), 17100);
        ac.Eram = eram;
        engine.TickEramVerticalConformance();

        Assert.True(ac.Eram.ReachedAssignedAltitude);
        Assert.Equal(new EramConformanceKey(17000, null, false), ac.Eram.ConformanceKey);
    }

    /// <summary>Places the aircraft at each east offset from SJC in turn, tracking east, and runs the fix and conformance steps.</summary>
    private static void FlyEastPastSjc(SimulationEngine engine, AircraftState ac, double[] eastOffsetsNm)
    {
        foreach (double east in eastOffsetsNm)
        {
            ac.Position = east >= 0 ? GeoMath.ProjectPoint(Sjc, East, east) : GeoMath.ProjectPoint(Sjc, West, -east);
            ac.TrueTrack = East;
            engine.TickAltitudeFixPassage();
            engine.TickEramVerticalConformance();
        }
    }

    private static AircraftState RestoreThroughJson(AircraftState ac)
    {
        var snapshot = new StateSnapshotDto
        {
            SchemaVersion = SnapshotSchemaMigrator.CurrentSchemaVersion,
            ElapsedSeconds = 30,
            Rng = new RngState(0, 0, 0, 0),
            Aircraft = [ac.ToSnapshot()],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "s",
                ScenarioName = "s",
                RngSeed = 0,
                ElapsedSeconds = 30,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };
        string json = JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default);
        StateSnapshotDto read = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
        SnapshotSchemaMigrator.Migrate(read);
        return AircraftState.FromSnapshot(Assert.Single(read.Aircraft), null);
    }

    private static (SimulationEngine Engine, AircraftState Aircraft) Build(PlannedAltitude planned, double altitude)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = Sjc,
            Altitude = altitude,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "IFR",
                Altitude = planned,
            },
            Track = new AircraftTrack(),
        };
        return (EngineWith(aircraft), aircraft);
    }

    private static SimulationEngine EngineWith(AircraftState aircraft)
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "eram-conformance",
                ScenarioName = "ERAM vertical conformance",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
        engine.World.AddAircraft(aircraft);
        return engine;
    }
}
