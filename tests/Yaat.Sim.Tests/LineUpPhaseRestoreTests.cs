using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// A <see cref="LineUpPhase"/> that comes back from a snapshot must still fly the line-up it was in the middle of.
/// The snapshot carries the phase's mode and progress but not its maneuver — the plan, the graph route, the
/// navigator and the arc playback are live objects — and <see cref="PhaseRunner"/> only calls <c>OnStart</c> on a
/// Pending phase, so a restored Active instance rebuilds itself on its first <c>OnTick</c> from the aircraft's
/// current pose (the same contract <see cref="Phases.Ground.CrossingRunwayPhase"/> uses for its navigator).
///
/// <para>
/// The rebuild has to hold at <em>every</em> second of the maneuver, not just the taxiway end of it: a restore
/// taken in the back half of the turn sits within a nose-wheel radius of the centerline, where the taxiway graph
/// has nothing to route and <see cref="LineUpGeometry.Compute"/> declines to plan. The theories below restore one
/// snapshot per second of the whole line-up and require each one to finish it.
/// </para>
///
/// <para>
/// Recording: S2-OAK-4 diagonal-lineup fixture — N436MS receives <c>CTO</c> at t=47 from hold-short of 28R on
/// taxiway B, the same fixture <see cref="Simulation.DiagonalLineup28rTests"/> uses for the un-restored contract.
/// </para>
/// </summary>
public class LineUpPhaseRestoreTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/diagonal-lineup-28r-recording.zip";
    private const string Callsign = "N436MS";
    private const int CtoSecond = 47;
    private const int BudgetSeconds = 90;

    /// <summary>Seconds of maneuver the bands must cover, so a shorter fixture cannot silently gut the theories.</summary>
    private const int MinimumBandSeconds = 20;

    /// <summary>Offset used by the single-case tests: far enough in to be past the taxiway, short of the centerline.</summary>
    private const int MidManeuverSeconds = 2;

    /// <summary>Seconds allowed for the hold to settle after the line-up hands off, before the end state is judged.</summary>
    private const int SettleSeconds = 15;

    /// <summary>Widest the LUAW hold sits off the centerline when the line-up was resumed on the straight rollout.</summary>
    private const double ResumedRolloutMaxCrossFt = 25.0;

    /// <summary>Most the LUAW hold sits off the runway heading when the line-up was resumed on the straight rollout.</summary>
    private const double ResumedRolloutMaxHeadingOffDeg = 10.0;

    // One pass per mode captures a snapshot for every second the aircraft spends in LineUpPhase, so each theory
    // case pays only for its own restore. The DTOs are read-only to the restore path (it builds fresh state).
    private static readonly Lazy<IReadOnlyList<StateSnapshotDto>> RollingBand = new(() => CaptureBand(luaw: false));
    private static readonly Lazy<IReadOnlyList<StateSnapshotDto>> LuawBand = new(() => CaptureBand(luaw: true));

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private static SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Replay to the clearance, then tick one second at a time, capturing the world at every second the aircraft
    /// spends in <see cref="LineUpPhase"/>. Empty when the fixture is unavailable — the tests skip on that.
    /// </summary>
    private static IReadOnlyList<StateSnapshotDto> CaptureBand(bool luaw)
    {
        var band = new List<StateSnapshotDto>();
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return band;
        }

        // LUAW replaces the recorded CTO: stop one second short of it and issue the clearance instead.
        engine.Replay(recording, luaw ? CtoSecond - 1 : CtoSecond);
        if (luaw)
        {
            engine.SendCommand(Callsign, "LUAW");
        }

        for (int i = 0; i < 30; i++)
        {
            if (engine.FindAircraft(Callsign)?.Phases?.CurrentPhase is LineUpPhase)
            {
                break;
            }
            engine.TickOneSecond();
        }

        for (int second = 1; second <= BudgetSeconds; second++)
        {
            engine.TickOneSecond();
            if (engine.FindAircraft(Callsign)?.Phases?.CurrentPhase is not LineUpPhase)
            {
                break;
            }
            band.Add(engine.CaptureSnapshot(0));
        }

        return band;
    }

    /// <summary>Load the scenario into a second engine and hand it the snapshot's world.</summary>
    private static SimulationEngine? RestoreInFreshEngine(StateSnapshotDto snapshot)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return null;
        }

        engine.Replay(recording, 1);
        engine.RestoreFromSnapshot(snapshot);
        return engine;
    }

    private readonly record struct RunResult(bool Reached, int Seconds, LatLon Position, double GroundSpeedKts, string EndPhase);

    /// <summary>Tick until the aircraft leaves <see cref="LineUpPhase"/>, reporting where and when it happened.</summary>
    private static RunResult RunUntilLineUpEnds(SimulationEngine engine, int budgetSeconds)
    {
        for (int second = 1; second <= budgetSeconds; second++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            var phase = ac.Phases?.CurrentPhase;
            if (phase is not LineUpPhase)
            {
                return new RunResult(true, second, ac.Position, ac.GroundSpeed, phase?.GetType().Name ?? "(none)");
            }
        }

        var last = engine.FindAircraft(Callsign);
        return new RunResult(
            false,
            budgetSeconds,
            last?.Position ?? new LatLon(0, 0),
            last?.GroundSpeed ?? 0,
            last?.Phases?.CurrentPhase?.GetType().Name ?? "(none)"
        );
    }

    private static RunwayInfo Runway28R()
    {
        var runway = TestVnasData.NavigationDb!.GetRunway("KOAK", "28R");
        Assert.NotNull(runway);
        return runway;
    }

    /// <summary>Cross-track (ft, unsigned) and heading offset (°) of the restored pose — the two the rebuild judges.</summary>
    private static (double CrossFt, double HeadingOffDeg) PoseAgainstRunway(AircraftState ac, RunwayInfo runway)
    {
        double crossFt =
            Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(ac.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading)
            ) * GeoMath.FeetPerNm;
        return (crossFt, Math.Abs(runway.TrueHeading.SignedAngleTo(ac.TrueHeading)));
    }

    /// <summary>Restore one band snapshot and run it out, logging the pose it started from.</summary>
    private RunResult RestoreAndRun(StateSnapshotDto snapshot, int offsetSeconds)
    {
        var engine = RestoreInFreshEngine(snapshot);
        Assert.NotNull(engine);
        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.IsType<LineUpPhase>(ac.Phases?.CurrentPhase);

        var (crossFt, headingOffDeg) = PoseAgainstRunway(ac, Runway28R());
        var run = RunUntilLineUpEnds(engine, BudgetSeconds);
        output.WriteLine(
            $"+{offsetSeconds}s: restored at cross={crossFt:F1}ft hdgOff={headingOffDeg:F1}° gs={ac.GroundSpeed:F1}kt "
                + $"-> {run.EndPhase} at +{run.Seconds}s gs={run.GroundSpeedKts:F1}kt"
        );
        return run;
    }

    /// <summary>Both bands must cover the whole maneuver, or the per-second theories below prove nothing.</summary>
    [Fact]
    public void LineUpBands_CoverTheWholeManeuver()
    {
        if (RollingBand.Value.Count == 0)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        output.WriteLine($"rolling band: {RollingBand.Value.Count}s, LUAW band: {LuawBand.Value.Count}s");
        Assert.True(RollingBand.Value.Count >= MinimumBandSeconds, $"rolling line-up band is only {RollingBand.Value.Count}s");
        Assert.True(LuawBand.Value.Count >= MinimumBandSeconds, $"LUAW line-up band is only {LuawBand.Value.Count}s");
    }

    /// <summary>
    /// A rolling CTO restored at any second of the line-up still reaches <see cref="TakeoffPhase"/>. The back-half
    /// offsets are the ones that bite: the aircraft is inside a nose-wheel radius of the centerline, where the
    /// taxiway graph cannot route and the synthetic geometry declines to plan.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public void RollingCto_RestoredAtEachSecondOfTheLineUp_ReachesTakeoff(int offsetSeconds)
    {
        var band = RollingBand.Value;
        if (band.Count == 0)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        if (offsetSeconds > band.Count)
        {
            output.WriteLine($"SKIP: +{offsetSeconds}s is past the {band.Count}s maneuver");
            return;
        }

        var run = RestoreAndRun(band[offsetSeconds - 1], offsetSeconds);
        Assert.Equal("TakeoffPhase", run.EndPhase);
    }

    /// <summary>
    /// The LUAW counterpart: a restore at any second of the line-up ends stopped on the centerline in
    /// <see cref="LinedUpAndWaitingPhase"/>, never parked short of the pavement by the fault branch.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public void Luaw_RestoredAtEachSecondOfTheLineUp_HoldsInPositionOnTheCentreline(int offsetSeconds)
    {
        var band = LuawBand.Value;
        if (band.Count == 0)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        if (offsetSeconds > band.Count)
        {
            output.WriteLine($"SKIP: +{offsetSeconds}s is past the {band.Count}s maneuver");
            return;
        }

        var engine = RestoreInFreshEngine(band[offsetSeconds - 1]);
        Assert.NotNull(engine);
        var restored = engine.FindAircraft(Callsign);
        Assert.NotNull(restored);
        var restoredPhase = Assert.IsType<LineUpPhase>(restored.Phases?.CurrentPhase);
        Assert.False(restoredPhase.RollingMode, "fixture check: a LUAW line-up is not rolling");

        var runway = Runway28R();
        var (crossFt, headingOffDeg) = PoseAgainstRunway(restored, runway);
        var run = RunUntilLineUpEnds(engine, BudgetSeconds);
        output.WriteLine($"+{offsetSeconds}s: restored at cross={crossFt:F1}ft hdgOff={headingOffDeg:F1}° -> {run.EndPhase} at +{run.Seconds}s");

        Assert.Equal("LinedUpAndWaitingPhase", run.EndPhase);

        // A line-up that was already aligned when the snapshot was taken hands off while still rolling, and
        // LinedUpAndWaitingPhase brakes it to a stop of its own (TargetSpeed = 0 in OnStart). Judge the hold once
        // it has settled rather than at the instant of hand-off.
        for (int i = 0; i < SettleSeconds; i++)
        {
            if (engine.FindAircraft(Callsign) is { GroundSpeed: < 0.5 })
            {
                break;
            }
            engine.TickOneSecond();
        }

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.IsType<LinedUpAndWaitingPhase>(ac.Phases?.CurrentPhase);

        var (endCrossFt, endHeadingOffDeg) = PoseAgainstRunway(ac, runway);
        output.WriteLine($"+{offsetSeconds}s: settled at cross={endCrossFt:F1}ft hdgOff={endHeadingOffDeg:F1}° gs={ac.GroundSpeed:F2}kt");

        Assert.True(RunwayOccupancy.IsWithinPavement(ac.Position, runway), $"holding off the pavement ({endCrossFt:F1}ft from centerline)");
        Assert.True(ac.GroundSpeed < 1.0, $"a LUAW hold must be stopped, gs={ac.GroundSpeed:F2}kt");

        // Bounds of the resumed rollout, not of a normal line-up. A restore taken in the front half re-plans the
        // turn and holds on the paint (< 5 ft); one taken deep in the turn finishes on the straight rollout, which
        // carries the aircraft outward while the nose comes round and can stop it a few degrees short of the
        // runway heading. These pin that band where it is — the un-restored contract (cross < 5 ft, hdg < 2°)
        // lives in DiagonalLineup28rTests.
        Assert.True(endCrossFt <= ResumedRolloutMaxCrossFt, $"cross-centerline {endCrossFt:F1}ft exceeds {ResumedRolloutMaxCrossFt:F1}ft");
        Assert.True(endHeadingOffDeg <= ResumedRolloutMaxHeadingOffDeg, $"holding {endHeadingOffDeg:F1}° off the runway heading");
    }

    /// <summary>
    /// The restored aircraft flies the rest of the line-up the way the un-restored one does: same hand-off second
    /// (within two) from the same place (within 50 ft).
    /// </summary>
    [Fact]
    public void RollingCto_RestoredMidLineUp_ReachesTakeoffLikeTheUnrestoredTwin()
    {
        var recording = LoadRecording();
        var twin = BuildEngine();
        if (recording is null || twin is null)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        twin.Replay(recording, CtoSecond);
        for (int i = 0; i < 30; i++)
        {
            if (twin.FindAircraft(Callsign)?.Phases?.CurrentPhase is LineUpPhase)
            {
                break;
            }
            twin.TickOneSecond();
        }

        for (int i = 0; i < MidManeuverSeconds; i++)
        {
            twin.TickOneSecond();
        }

        var live = twin.FindAircraft(Callsign);
        Assert.NotNull(live);
        var livePhase = Assert.IsType<LineUpPhase>(live.Phases?.CurrentPhase);
        Assert.True(livePhase.RollingMode, "fixture check: the recorded CTO must put the line-up in rolling mode");

        var restoredEngine = RestoreInFreshEngine(twin.CaptureSnapshot(0));
        Assert.NotNull(restoredEngine);
        var restoredAc = restoredEngine.FindAircraft(Callsign);
        Assert.NotNull(restoredAc);
        var restoredPhase = Assert.IsType<LineUpPhase>(restoredAc.Phases?.CurrentPhase);
        Assert.True(restoredPhase.RollingMode, "RollingMode must survive the snapshot round trip");

        var twinRun = RunUntilLineUpEnds(twin, BudgetSeconds);
        var restoredRun = RunUntilLineUpEnds(restoredEngine, BudgetSeconds);

        output.WriteLine($"twin:     +{twinRun.Seconds}s -> {twinRun.EndPhase} gs={twinRun.GroundSpeedKts:F1}kt");
        output.WriteLine($"restored: +{restoredRun.Seconds}s -> {restoredRun.EndPhase} gs={restoredRun.GroundSpeedKts:F1}kt");

        Assert.True(twinRun.Reached, $"fixture check: the un-restored twin never left LineUpPhase within {BudgetSeconds}s");
        Assert.Equal("TakeoffPhase", twinRun.EndPhase);
        Assert.Equal("TakeoffPhase", restoredRun.EndPhase);

        int secondsDelta = Math.Abs(restoredRun.Seconds - twinRun.Seconds);
        Assert.True(secondsDelta <= 2, $"restored reached takeoff {secondsDelta}s from the twin (+{restoredRun.Seconds}s vs +{twinRun.Seconds}s)");

        double offsetFt = GeoMath.DistanceNm(twinRun.Position, restoredRun.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"hand-off offset: {offsetFt:F1}ft");
        Assert.True(offsetFt <= 50.0, $"restored handed off {offsetFt:F1}ft from the twin's hand-off position");
    }

    /// <summary>
    /// A <c>CTO</c> that lands on a restored line-up before its first tick must upgrade it to rolling. The phase
    /// has not rebuilt yet, so its state machine still reads <c>Setup</c> — the upgrade gate must not mistake that
    /// for a phase that never started.
    /// </summary>
    [Fact]
    public void RestoredLuaw_CtoBeforeTheFirstTick_UpgradesToRollingAndTakesOff()
    {
        var band = LuawBand.Value;
        if (band.Count == 0)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        var engine = RestoreInFreshEngine(band[band.Count / 2]);
        Assert.NotNull(engine);
        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        var phase = Assert.IsType<LineUpPhase>(ac.Phases?.CurrentPhase);
        Assert.False(phase.RollingMode);
        Assert.True(
            ac.IndicatedAirspeed > LineUpPhase.RollingUpgradeMinSpeedKts,
            $"fixture check: the upgrade gate needs the aircraft still moving, ias={ac.IndicatedAirspeed:F1}kt"
        );

        var result = engine.SendCommand(Callsign, "CTO");
        Assert.True(result.Success, $"CTO refused: {result.Message}");
        Assert.True(phase.RollingMode, "CTO on a restored, not-yet-rebuilt line-up must upgrade it to rolling");

        var run = RunUntilLineUpEnds(engine, BudgetSeconds);
        output.WriteLine($"restored LUAW + CTO: -> {run.EndPhase} at +{run.Seconds}s gs={run.GroundSpeedKts:F1}kt");
        Assert.Equal("TakeoffPhase", run.EndPhase);
    }

    /// <summary>
    /// A restore taken with the aircraft already aligned on the runway has no line-up left to fly: the phase
    /// completes on its first tick and hands off, rather than re-planning (or faulting) around a pose whose
    /// maneuver already ran.
    /// </summary>
    [Fact]
    public void RestoredAlreadyAlignedOnRunway_CompletesOnTheFirstTick()
    {
        const double rwyHeadingDeg = 280.0;
        const double threshLat = 37.0;
        const double threshLon = -122.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(threshLat, threshLon, new TrueHeading(rwyHeadingDeg), 2.0);
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            thresholdLat: threshLat,
            thresholdLon: threshLon,
            endLat: endLat,
            endLon: endLon,
            heading: rwyHeadingDeg
        );

        // On the centerline 500 ft downfield, on runway heading: the pose a rolling line-up ends in.
        var (acLat, acLon) = GeoMath.ProjectPoint(threshLat, threshLon, new TrueHeading(rwyHeadingDeg), 500.0 / GeoMath.FeetPerNm);
        var aircraft = new AircraftState
        {
            Callsign = "LUTEST",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = new TrueHeading(rwyHeadingDeg),
            IndicatedAirspeed = 8,
            IsOnGround = true,
        };

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Runway = runway,
            FieldElevation = 0,
            GroundLayout = new AirportGroundLayout { AirportId = "KTEST" },
            Logger = NullLogger.Instance,
        };

        var phase = new LineUpPhase();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new TakeoffPhase());
        phase.OnStart(ctx);
        phase.Status = PhaseStatus.Active;
        Assert.True(phase.RollingMode, "fixture check: a TakeoffPhase next in the list means rolling mode");

        var dto = Assert.IsType<LineUpPhaseDto>(phase.ToSnapshot());
        var restored = LineUpPhase.FromSnapshot(dto);

        Assert.True(restored.RollingMode, "RollingMode must survive the snapshot round trip");
        Assert.True(
            restored.OnTick(ctx),
            $"a restored line-up already aligned on the runway must complete on its first tick (state={restored.CurrentState})"
        );
    }
}
