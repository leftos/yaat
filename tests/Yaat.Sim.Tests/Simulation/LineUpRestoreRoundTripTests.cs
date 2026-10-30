using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A <see cref="LineUpPhase"/> restored from a snapshot taken part-way through its maneuver onto the runway resumes that
/// maneuver where it stood, and so goes on exactly as the line-up that was never interrupted: the graph-taxi navigator's turn
/// (<see cref="LineUpPhase.State.GraphTaxi"/>), the aligned fillet arc (<see cref="LineUpPhase.State.Arc"/>), both slow
/// turns of a pivot (<see cref="LineUpPhase.State.PivotTurn1"/>, <see cref="LineUpPhase.State.PivotTurn2"/>) and the
/// rollout after them (<see cref="LineUpPhase.State.Rollout"/>), held or not. Every snapshot goes through the recording JSON
/// before it is restored. A snapshot without the saved maneuver, or with one that no longer plans, still lines up from the
/// aircraft's pose.
///
/// <para>All of them line up on KSFO 01R under LUAW. The graph-taxi pose is UAL859's recorded hold short of 01R at A1
/// (<see cref="Issue438SfoLuawPivotTests"/>). The synthetic arc and pivot are flown only where no taxiway route resolves
/// onto the runway, which every hold-short pose at A1 and M1 now finds, so those two start on the 01R pavement itself,
/// <see cref="OffCentrelineFt"/> left of the centreline and <see cref="DownfieldFt"/> from the threshold: nosed
/// <see cref="ArcNoseOffDeg"/> right of the runway heading for the aligned arc, <see cref="PivotNoseOffDeg"/> for the
/// pivot.</para>
/// </summary>
public class LineUpRestoreRoundTripTests(ITestOutputHelper output)
{
    private const string Callsign = "UAL859";
    private const string AircraftType = "B738";
    private const string AirportId = "SFO";
    private const string RunwayId = "01R";

    /// <summary>Signed cross-track of the arc and pivot poses from the 01R centreline (negative = left of it), feet.</summary>
    private const double OffCentrelineFt = -90.0;

    /// <summary>Along-track distance of the arc and pivot poses from the 01R threshold, feet.</summary>
    private const double DownfieldFt = 600.0;

    /// <summary>Heading of the aligned-arc pose relative to the runway heading, degrees (positive = right).</summary>
    private const double ArcNoseOffDeg = 60.0;

    /// <summary>Heading of the pivot pose relative to the runway heading, degrees (positive = right).</summary>
    private const double PivotNoseOffDeg = -30.0;

    /// <summary>How far the nose must already have swung in the turn for the snapshot to be part-way round it.</summary>
    private const double MinSwungDeg = 10.0;

    /// <summary>Seconds the held twins are compared before the hold is lifted.</summary>
    private const int HeldSeconds = 5;

    private const int StateBudgetSeconds = 90;
    private const int CompareSeconds = 60;

    /// <summary>The snapshot fields that carry the saved maneuver's state and start pose.</summary>
    private static readonly string[] SavedManeuverFields = ["ManeuverState", "PlanStartLat", "PlanStartLon", "PlanStartHeadingDeg"];

    /// <summary>
    /// A snapshot taken part-way round the graph-taxi navigator's turn onto 01R, restored into a second engine, keeps the
    /// restored aircraft on the original's phase, position, heading and speed every second.
    /// </summary>
    [Fact]
    public void GraphTaxi_SurvivesSnapshotRoundTripMidTurn() => AssertResumesMidTurn(GraphTaxiPose, LineUpPhase.State.GraphTaxi, MinSwungDeg);

    /// <summary>A snapshot taken part-way round the aligned fillet arc onto 01R resumes the arc where it stood.</summary>
    [Fact]
    public void Arc_SurvivesSnapshotRoundTripMidTurn() =>
        AssertResumesMidTurn(runway => PavementPose(runway, ArcNoseOffDeg), LineUpPhase.State.Arc, MinSwungDeg);

    /// <summary>A snapshot taken part-way round a pivot's first slow turn (toward the centreline) resumes that turn.</summary>
    [Fact]
    public void PivotTurn1_SurvivesSnapshotRoundTripMidTurn() =>
        AssertResumesMidTurn(runway => PavementPose(runway, PivotNoseOffDeg), LineUpPhase.State.PivotTurn1, MinSwungDeg);

    /// <summary>A snapshot taken part-way round a pivot's second slow turn (onto the runway heading) resumes that turn.</summary>
    [Fact]
    public void PivotTurn2_SurvivesSnapshotRoundTripMidTurn() =>
        AssertResumesMidTurn(runway => PavementPose(runway, PivotNoseOffDeg), LineUpPhase.State.PivotTurn2, MinSwungDeg);

    /// <summary>
    /// A snapshot taken on the rollout after the aligned arc, with the aircraft already lined up, resumes the rollout to the
    /// stop point rather than completing the line-up on the first tick while the original is still rolling.
    /// </summary>
    [Fact]
    public void Rollout_SurvivesSnapshotRoundTrip() =>
        AssertResumesMidTurn(runway => PavementPose(runway, ArcNoseOffDeg), LineUpPhase.State.Rollout, minSwungDeg: 0.0);

    /// <summary>
    /// A HOLD part-way round the aligned arc keeps the arc's steering tick running while the aircraft brakes. A snapshot taken
    /// during the braking, restored into a second engine, steers on round the arc the same way, so the restored aircraft stays
    /// on the original's position, heading and speed while held and after a re-issued LUAW lifts the hold.
    /// </summary>
    [Fact]
    public void Arc_HeldMidTurn_SurvivesSnapshotRoundTripWhileBrakingAndAfterRelease()
    {
        if (Stage(runway => PavementPose(runway, ArcNoseOffDeg)) is not { } staged)
        {
            output.WriteLine("SKIP: navdata or KSFO layout unavailable");
            return;
        }

        CatchMidTurn(staged.Engine, staged.Aircraft, LineUpPhase.State.Arc, MinSwungDeg);
        AssertSent(staged.Engine, "HOLD");
        staged.Engine.TickOneSecond();
        Assert.True(staged.Aircraft.IndicatedAirspeed > 0.5, $"fixture check: still braking, ias={staged.Aircraft.IndicatedAirspeed:F2}kt");
        output.WriteLine($"held and braking: hdg={staged.Aircraft.TrueHeading.Degrees:F1} ias={staged.Aircraft.IndicatedAirspeed:F1}");

        (SimulationEngine restoredEngine, AircraftState restored) = RestoreTwin(staged, RoundTrip(staged.Engine.CaptureSnapshot()));
        AssertTwinsAgree(staged.Engine, staged.Aircraft, restoredEngine, restored, HeldSeconds);

        AssertSent(staged.Engine, "LUAW");
        AssertSent(restoredEngine, "LUAW");
        AssertTwinsAgree(staged.Engine, staged.Aircraft, restoredEngine, restored, CompareSeconds);
        Assert.IsType<LinedUpAndWaitingPhase>(staged.Aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// An immediate CTO in the middle of a graph-taxi line-up upgrades it to rolling and marks the line-up expedited, but
    /// leaves the navigator's speed cap as it was. A snapshot taken then, restored into a second engine, keeps that cap, so the
    /// restored aircraft stays on the original's position, heading and speed until the line-up hands off.
    /// </summary>
    [Fact]
    public void GraphTaxi_ImmediateCtoMidLineUp_KeepsTheNavigatorSpeedCapThroughARestore()
    {
        if (Stage(GraphTaxiPose) is not { } staged)
        {
            output.WriteLine("SKIP: navdata or KSFO layout unavailable");
            return;
        }

        LineUpPhase phase = TickUntil(
            staged.Engine,
            staged.Aircraft,
            "rolling in the graph taxi fast enough for a rolling upgrade",
            lineUp =>
                (lineUp.CurrentState == LineUpPhase.State.GraphTaxi) && (staged.Aircraft.IndicatedAirspeed >= LineUpPhase.RollingUpgradeMinSpeedKts)
        );
        double capBeforeCto = phase.ManeuverSpeedKts;
        AssertSent(staged.Engine, "CTO IMM");
        Assert.True(phase.RollingMode, "fixture check: the immediate CTO upgrades the line-up to rolling");
        Assert.True(staged.Aircraft.Ground.IsExpeditingLineup, "fixture check: the immediate CTO expedites the line-up");
        Assert.Equal(capBeforeCto, phase.ManeuverSpeedKts);

        (SimulationEngine restoredEngine, AircraftState restored) = RestoreTwin(staged, RoundTrip(staged.Engine.CaptureSnapshot()));
        for (int second = 1; (second <= StateBudgetSeconds) && (staged.Aircraft.Phases?.CurrentPhase is LineUpPhase); second++)
        {
            staged.Engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            AssertSameState(second, staged.Aircraft, restored);
            if ((staged.Aircraft.Phases?.CurrentPhase is LineUpPhase original) && (restored.Phases?.CurrentPhase is LineUpPhase resumed))
            {
                Assert.Equal(original.ManeuverSpeedKts, resumed.ManeuverSpeedKts);
            }
        }

        Assert.False(
            staged.Aircraft.Phases?.CurrentPhase is LineUpPhase,
            $"fixture check: the rolling line-up never handed off within {StateBudgetSeconds}s"
        );
    }

    /// <summary>
    /// A snapshot taken part-way round the aligned arc, with the saved maneuver's state and start pose taken out (an older
    /// snapshot's shape), still lines up: the restore rebuilds the line-up from the aircraft's pose.
    /// </summary>
    [Fact]
    public void Arc_SnapshotWithoutTheSavedManeuver_StillLinesUpFromThePose()
    {
        if (Stage(runway => PavementPose(runway, ArcNoseOffDeg)) is not { } staged)
        {
            output.WriteLine("SKIP: navdata or KSFO layout unavailable");
            return;
        }

        CatchMidTurn(staged.Engine, staged.Aircraft, LineUpPhase.State.Arc, MinSwungDeg);
        StateSnapshotDto snapshot = RoundTripEditingLineUp(
            staged.Engine.CaptureSnapshot(),
            lineUp =>
            {
                foreach (string field in SavedManeuverFields)
                {
                    Assert.True(lineUp.Remove(field), $"fixture check: the captured line-up carries {field}");
                }
            }
        );

        (SimulationEngine restoredEngine, AircraftState restored) = RestoreTwin(staged, snapshot);
        Assert.IsType<LinedUpAndWaitingPhase>(RunUntilLineUpEnds(restoredEngine, restored));
    }

    /// <summary>
    /// A snapshot taken part-way round the aligned arc whose saved start pose now plans a pivot (the start heading turned to
    /// the pivot pose's) cannot resume the arc: the restore drops the saved maneuver with a warning and still lines up from
    /// the aircraft's pose.
    /// </summary>
    [Fact]
    public void Arc_SavedManeuverThatNoLongerPlansAnArc_IsDroppedAndStillLinesUp()
    {
        if (Stage(runway => PavementPose(runway, ArcNoseOffDeg)) is not { } staged)
        {
            output.WriteLine("SKIP: navdata or KSFO layout unavailable");
            return;
        }

        CatchMidTurn(staged.Engine, staged.Aircraft, LineUpPhase.State.Arc, MinSwungDeg);
        double pivotStartHeadingDeg = PavementPose(staged.Runway, PivotNoseOffDeg).Heading.Degrees;
        StateSnapshotDto snapshot = RoundTripEditingLineUp(
            staged.Engine.CaptureSnapshot(),
            lineUp => lineUp["PlanStartHeadingDeg"] = pivotStartHeadingDeg
        );

        (SimulationEngine restoredEngine, AircraftState restored) = RestoreTwin(staged, snapshot);
        var tap = new CapturingSimLogProvider(LogLevel.Warning, capacity: 100);
        SimLogBuilder.CreateForTest(output).CaptureInto(tap).InitializeSimLog();

        Assert.IsType<LinedUpAndWaitingPhase>(RunUntilLineUpEnds(restoredEngine, restored));
        Assert.Contains(tap.Drain(), record => record.Message.Contains("maneuver dropped", StringComparison.Ordinal));
    }

    /// <summary>
    /// Start a LUAW line-up from the pose <paramref name="pose"/> gives, tick until the phase is in <paramref name="state"/>
    /// with the nose swung at least <paramref name="minSwungDeg"/> since the state was first seen, round-trip the snapshot
    /// through JSON into a second engine, and require the restored aircraft on the original's phase, position, heading and
    /// speed every second after.
    /// </summary>
    private void AssertResumesMidTurn(Func<RunwayInfo, (LatLon Position, TrueHeading Heading)> pose, LineUpPhase.State state, double minSwungDeg)
    {
        if (Stage(pose) is not { } staged)
        {
            output.WriteLine("SKIP: navdata or KSFO layout unavailable");
            return;
        }

        AircraftState aircraft = staged.Aircraft;
        LineUpPhase caught = CatchMidTurn(staged.Engine, aircraft, state, minSwungDeg);
        if (state == LineUpPhase.State.GraphTaxi)
        {
            Assert.NotEmpty(
                GroundNavigatorArcRestoreTests.PlaybackObjects(
                    JsonSerializer.SerializeToNode<PhaseDto>(caught.ToSnapshot(), RecordingJsonOptions.Default)
                )
            );
        }

        output.WriteLine($"caught in {state}: hdg={aircraft.TrueHeading.Degrees:F1} gs={aircraft.GroundSpeed:F1} at {aircraft.Position}");
        (SimulationEngine restoredEngine, AircraftState restored) = RestoreTwin(staged, RoundTrip(staged.Engine.CaptureSnapshot()));
        AssertTwinsAgree(staged.Engine, aircraft, restoredEngine, restored, CompareSeconds);

        Assert.IsType<LinedUpAndWaitingPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine($"after {CompareSeconds}s: {aircraft.Phases?.CurrentPhase?.Name} at {aircraft.Position}");
    }

    /// <summary>Tick both engines <paramref name="seconds"/> seconds, requiring the twins in the same state after each.</summary>
    private static void AssertTwinsAgree(
        SimulationEngine engine,
        AircraftState original,
        SimulationEngine restoredEngine,
        AircraftState restored,
        int seconds
    )
    {
        for (int second = 1; second <= seconds; second++)
        {
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            AssertSameState(second, original, restored);
        }
    }

    /// <summary>The restored aircraft is on the original's phase, position, heading and speed.</summary>
    private static void AssertSameState(int second, AircraftState original, AircraftState restored)
    {
        string originalPhase = original.Phases?.CurrentPhase?.Name ?? "(none)";
        string restoredPhase = restored.Phases?.CurrentPhase?.Name ?? "(none)";
        Assert.True(originalPhase == restoredPhase, $"second {second}: phase {originalPhase} vs restored {restoredPhase}");
        Assert.True(original.Position.Equals(restored.Position), $"second {second}: position {original.Position} vs restored {restored.Position}");
        Assert.True(
            original.TrueHeading.Equals(restored.TrueHeading),
            $"second {second}: heading {original.TrueHeading.Degrees} vs restored {restored.TrueHeading.Degrees}"
        );
        Assert.True(
            original.IndicatedAirspeed.Equals(restored.IndicatedAirspeed),
            $"second {second}: ias {original.IndicatedAirspeed} vs restored {restored.IndicatedAirspeed}"
        );
    }

    /// <summary>
    /// Tick until the line-up is in <paramref name="state"/> with the nose swung at least <paramref name="minSwungDeg"/> since
    /// the state was first seen (for the graph taxi, with its navigator on a curve), and return the phase.
    /// </summary>
    private static LineUpPhase CatchMidTurn(SimulationEngine engine, AircraftState aircraft, LineUpPhase.State state, double minSwungDeg)
    {
        TrueHeading? stateSeenAt =
            aircraft.Phases?.CurrentPhase is LineUpPhase { CurrentState: var startState } && (startState == state) ? aircraft.TrueHeading : null;
        return TickUntil(
            engine,
            aircraft,
            $"part-way through {state}",
            phase =>
            {
                if (phase.CurrentState != state)
                {
                    return false;
                }

                stateSeenAt ??= aircraft.TrueHeading;
                bool swung = Math.Abs(stateSeenAt.Value.SignedAngleTo(aircraft.TrueHeading)) >= minSwungDeg;
                return swung && ((state != LineUpPhase.State.GraphTaxi) || phase.IsNavigatorOnCurve);
            }
        );
    }

    /// <summary>Tick one second at a time until the aircraft's line-up satisfies <paramref name="caught"/>, and return it.</summary>
    private static LineUpPhase TickUntil(SimulationEngine engine, AircraftState aircraft, string what, Func<LineUpPhase, bool> caught)
    {
        for (int second = 1; second <= StateBudgetSeconds; second++)
        {
            engine.TickOneSecond();
            if ((aircraft.Phases?.CurrentPhase is LineUpPhase phase) && caught(phase))
            {
                return phase;
            }
        }

        Assert.Fail($"the line-up was never caught {what} within {StateBudgetSeconds}s");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Tick until the aircraft leaves <see cref="LineUpPhase"/>, and return the phase it is then in.</summary>
    private static Phase? RunUntilLineUpEnds(SimulationEngine engine, AircraftState aircraft)
    {
        for (int second = 1; (second <= StateBudgetSeconds) && (aircraft.Phases?.CurrentPhase is LineUpPhase); second++)
        {
            engine.TickOneSecond();
        }

        return aircraft.Phases?.CurrentPhase;
    }

    private static void AssertSent(SimulationEngine engine, string command)
    {
        CommandResult result = engine.SendCommand(Callsign, command);
        Assert.True(result.Success, $"{command} refused: {result.Message}");
    }

    /// <summary>UAL859's recorded hold short of 01R at A1, from which the line-up follows the taxiway graph.</summary>
    private static (LatLon Position, TrueHeading Heading) GraphTaxiPose(RunwayInfo _) =>
        (new LatLon(37.60687155024075, -122.381946548138), new TrueHeading(120.85));

    /// <summary>
    /// A pose on the 01R pavement, <see cref="OffCentrelineFt"/> off the centreline, nosed <paramref name="noseOffDeg"/> off
    /// the runway heading.
    /// </summary>
    private static (LatLon Position, TrueHeading Heading) PavementPose(RunwayInfo runway, double noseOffDeg)
    {
        (double alongLat, double alongLon) = GeoMath.ProjectPoint(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading,
            DownfieldFt / GeoMath.FeetPerNm
        );
        (double lat, double lon) = GeoMath.ProjectPoint(
            alongLat,
            alongLon,
            new TrueHeading(runway.TrueHeading.Degrees + 90.0),
            OffCentrelineFt / GeoMath.FeetPerNm
        );
        return (new LatLon(lat, lon), new TrueHeading(runway.TrueHeading.Degrees + noseOffDeg));
    }

    private sealed record Staged(TestAirportGroundData GroundData, SimulationEngine Engine, AircraftState Aircraft, RunwayInfo Runway);

    /// <summary>A first engine with the aircraft lining up from the pose <paramref name="pose"/> gives; null when data is missing.</summary>
    private Staged? Stage(Func<RunwayInfo, (LatLon Position, TrueHeading Heading)> pose)
    {
        var groundData = new TestAirportGroundData();
        if (
            (NewEngine(groundData) is not { } engine)
            || (groundData.GetLayout(AirportId) is not { } layout)
            || (TestVnasData.NavigationDb?.GetRunway("KSFO", RunwayId) is not { } runway)
        )
        {
            return null;
        }

        (LatLon position, TrueHeading heading) = pose(runway);
        return new Staged(groundData, engine, Spawn(engine, layout, runway, position, heading), runway);
    }

    /// <summary>A second engine restored from <paramref name="snapshot"/>, and the aircraft in it.</summary>
    private (SimulationEngine Engine, AircraftState Aircraft) RestoreTwin(Staged staged, StateSnapshotDto snapshot)
    {
        SimulationEngine restoredEngine = Assert.IsType<SimulationEngine>(NewEngine(staged.GroundData));
        restoredEngine.RestoreFromSnapshot(snapshot);
        return (restoredEngine, Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign)));
    }

    private static AircraftState Spawn(SimulationEngine engine, AirportGroundLayout layout, RunwayInfo runway, LatLon position, TrueHeading heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = heading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = AirportId,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Add(new LineUpPhase());
        aircraft.Phases.Add(new LinedUpAndWaitingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) => RoundTripEditingLineUp(snapshot, _ => { });

    /// <summary>
    /// Round-trip <paramref name="snapshot"/> through the recording JSON, applying <paramref name="editLineUp"/> to every line-up
    /// phase object in it on the way.
    /// </summary>
    private static StateSnapshotDto RoundTripEditingLineUp(StateSnapshotDto snapshot, Action<JsonObject> editLineUp)
    {
        JsonNode node = Assert.IsAssignableFrom<JsonNode>(JsonSerializer.SerializeToNode(snapshot, RecordingJsonOptions.Default));
        List<JsonObject> lineUps = [.. LineUpPhaseObjects(node)];
        Assert.NotEmpty(lineUps);
        foreach (JsonObject lineUp in lineUps)
        {
            editLineUp(lineUp);
        }

        return Assert.IsType<StateSnapshotDto>(node.Deserialize<StateSnapshotDto>(RecordingJsonOptions.Default));
    }

    /// <summary>Every JSON object under <paramref name="node"/> that is a <see cref="LineUpPhaseDto"/>.</summary>
    private static IEnumerable<JsonObject> LineUpPhaseObjects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if ((obj["$type"] is JsonValue type) && (type.GetValueKind() == JsonValueKind.String) && (type.GetValue<string>() == "LineUp"))
            {
                yield return obj;
            }

            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                foreach (JsonObject found in LineUpPhaseObjects(property.Value))
                {
                    yield return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                foreach (JsonObject found in LineUpPhaseObjects(item))
                {
                    yield return found;
                }
            }
        }
    }

    private SimulationEngine? NewEngine(TestAirportGroundData groundData)
    {
        TestVnasData.EnsureInitialized();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout(AirportId) is null))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-sfo-lineup-restore",
                ScenarioName = "SFO line-up restore",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
            },
        };
    }
}
