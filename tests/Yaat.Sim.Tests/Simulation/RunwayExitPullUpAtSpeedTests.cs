using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// With automatic pull-up on, an exit between two parallel runways counts its runway vacated the moment the tail passes the hold
/// bar (AIM 4-3-21.b) and hands straight to the pull-up's taxi at its current speed: one braking curve from the exit to the
/// parallel runway's hold-short, no stop at the exit, no speed-up before the parallel bar. With pull-up off the exit still stops
/// tail + 10 ft past its bar.
/// </summary>
public sealed class RunwayExitPullUpAtSpeedTests(ITestOutputHelper output)
{
    private const string Callsign = "TST738";
    private const int MaxSeconds = 500;

    /// <summary>
    /// One engine second of the run: the phase after it, the ground speed, the position, the centre's distance to the parallel bar's
    /// stop, and whether the aircraft is still vacating its landing runway (<see cref="RunwayOccupancy.IsVacating"/>; null with no
    /// landing runway or layout to judge it on).
    /// </summary>
    internal sealed record Second(int T, Phase? Phase, double GroundSpeed, LatLon Position, double? ToBarStopFt, bool? Vacating);

    // The ground data a test stages on: the shared test layouts unless a test needs a layout of its own.
    private IAirportGroundData _groundData = new TestAirportGroundData();

    /// <summary>A staged arrival: its engine, callsign and the parallel runway the pull-up holds short of.</summary>
    internal sealed record Staged(SimulationEngine Engine, string Callsign, string Parallel);

    /// <summary>
    /// A run to the parallel bar: every second, the hand-off index (the first second ended in TaxiingPhase), and the crew's "unable"
    /// calls.
    /// </summary>
    internal sealed record Run(List<Second> Seconds, int HandOff, List<string> Unable);

    [Theory]
    [InlineData("SFO-19L-G")]
    [InlineData("OAK-28L-G")]
    public void PullUp_HandsOffAtSpeed_NoStopBeforeTheParallelBar(string row) => AssertHandsOffAtSpeed(Stage(row), output);

    internal static void AssertHandsOffAtSpeed(Staged staged, ITestOutputHelper output)
    {
        Run run = RunToParallelBar(staged, output);

        Second handOff = run.Seconds[run.HandOff];
        Assert.IsType<TaxiingPhase>(handOff.Phase);
        Assert.DoesNotContain(run.Seconds, s => s.Phase is HoldingAfterExitPhase);
        foreach (Second s in run.Seconds.Skip(run.HandOff).TakeWhile(s => !(s.ToBarStopFt < 30.0)))
        {
            Assert.True(
                s.GroundSpeed > TaxiingPhase.StartNodeHoldArmSpeedKts,
                $"t={s.T}: {s.GroundSpeed:F1} kt with the parallel bar's stop {s.ToBarStopFt:F0} ft away; no stop before the bar"
            );
        }

        AircraftState aircraft = Aircraft(staged);
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(run.Seconds[^1].Phase);
        Assert.Contains(staged.Parallel, holding.HoldShort.TargetName ?? "");
        Assert.True(aircraft.GroundSpeed < 1.0, $"holding short of {staged.Parallel} at {aircraft.GroundSpeed:F2} kt");
        HoldShortPoint bar = ParallelBar(aircraft, staged.Parallel);
        Assert.False(bar.Unable, "the parallel bar was judged unmakeable");
        Assert.False(bar.StopMoved, "the parallel bar's stop was moved");
        Assert.Empty(run.Unable);

        // From the latch, the hand-off tick, the landing runway stays vacated until the aircraft holds at the parallel bar.
        foreach (Second s in run.Seconds.Skip(run.HandOff))
        {
            Assert.True(s.Vacating == false, $"t={s.T}: {s.Phase?.Name} is still vacating its landing runway ({s.Vacating})");
        }
    }

    [Theory]
    [InlineData("SFO-19L-G")]
    [InlineData("OAK-28L-G")]
    public void PullUp_NeverExceedsTheHandOffSpeedBeforeTheParallelBar(string row) => AssertNeverExceedsTheHandOffSpeed(Stage(row), output);

    internal static void AssertNeverExceedsTheHandOffSpeed(Staged staged, ITestOutputHelper output)
    {
        Run run = RunToParallelBar(staged, output);

        // The taxi's cap is the ground speed at the hand-off.
        double? handOffKts = Assert.IsType<TaxiingPhase>(run.Seconds[run.HandOff].Phase).PullUpCapKts;
        Assert.NotNull(handOffKts);
        foreach (Second s in run.Seconds.Skip(run.HandOff))
        {
            Assert.True(
                s.GroundSpeed <= handOffKts.Value + 0.05,
                $"t={s.T}: {s.GroundSpeed:F2} kt before the parallel bar, above the {handOffKts:F2} kt hand-off speed"
            );
        }
    }

    /// <summary>
    /// The hand-off speed is planned, not guaranteed: what must hold is the stop at the parallel bar within normal taxi braking. Each
    /// engine second from the hand-off to the stop brakes no harder than the pull-up's planned rate, the lower of 3 kt/s and the
    /// category taxi rate, with 0.3 kt/s of slack. The second the stop completes (the hold-short takes over, or the second ends
    /// below 1 kt) is exempt from that bound, but must begin at a crawl.
    /// </summary>
    [Theory]
    [InlineData("SFO-19L-G")]
    [InlineData("OAK-28L-G")]
    public void PullUp_StopsAtTheParallelBarWithinTheTaxiBrakingRate(string row) => AssertStopsWithinTheTaxiBrakingRate(Stage(row), output);

    internal static void AssertStopsWithinTheTaxiBrakingRate(Staged staged, ITestOutputHelper output)
    {
        AircraftState aircraft = Aircraft(staged);
        string type = aircraft.AircraftType;
        AircraftCategory category = AircraftCategorization.Categorize(type);

        Run run = RunToParallelBar(staged, output);

        // The pull-up route starts at the landing runway's bar; the parallel bar's stop puts the nose at its bar, and the exit's stop
        // the tail RunwayTailClearance.MarginFt past the landing bar.
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint bar = ParallelBar(aircraft, staged.Parallel);
        double halfLengthFt = AircraftLength.ResolveFt(type) / 2.0;
        double barStopFt = AlongToNodeFt(route, bar.NodeId) - halfLengthFt;
        double exitStopFt = halfLengthFt + RunwayTailClearance.MarginFt;
        double roomFt = barStopFt - exitStopFt - 10.0;
        double rate = RunwayExitPhase.PullUpBrakeRate(category);
        double plannedKts = Math.Min(
            RunwayExitPhase.ExitTaxiCeilingKts(category, type, expediting: false),
            Math.Sqrt(2.0 * rate * roomFt * 3600.0 / GeoMath.FeetPerNm)
        );

        double? handOffKts = Assert.IsType<TaxiingPhase>(run.Seconds[run.HandOff].Phase).PullUpCapKts;
        output.WriteLine($"{staged.Callsign}: room {roomFt:F0} ft, planned {plannedKts:F1} kt, handed off at {handOffKts:F1} kt");

        const double SecondsPerSample = 1.0;
        double boundRate = rate + 0.3;
        (double Decel, int T) peak = (0.0, 0);
        (double Kts, int T) holdTaken = (0.0, 0);
        for (int i = run.HandOff + 1; i < run.Seconds.Count; i++)
        {
            (Second before, Second now) = (run.Seconds[i - 1], run.Seconds[i]);

            // The second the stop completes, by the hold-short's take-over or the last braking sub-tick onto the stop (which may brake at
            // the firm rate from a couple of knots), snaps the speed to zero from a crawl, as at any runway bar; it must be a crawl.
            if ((now.Phase is not TaxiingPhase) || (now.GroundSpeed < 1.0))
            {
                holdTaken = before.GroundSpeed > holdTaken.Kts ? (before.GroundSpeed, now.T) : holdTaken;
                continue;
            }

            double decel = (before.GroundSpeed - now.GroundSpeed) / SecondsPerSample;
            peak = decel > peak.Decel ? (decel, now.T) : peak;
        }

        output.WriteLine(
            $"{staged.Callsign}: peak braking {peak.Decel:F2} kt/s at t={peak.T}, bound {boundRate:F2} kt/s; "
                + $"hold took the stop from {holdTaken.Kts:F2} kt at t={holdTaken.T}"
        );
        Assert.True(holdTaken.Kts <= 5.0, $"t={holdTaken.T}: the hold took the stop from {holdTaken.Kts:F2} kt, not a crawl");
        Assert.True(peak.Decel <= boundRate, $"t={peak.T}: braked at {peak.Decel:F2} kt/s, bound {boundRate:F2} kt/s");

        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(run.Seconds[^1].Phase);
        Assert.Contains(staged.Parallel, holding.HoldShort.TargetName ?? "");
        Assert.True(aircraft.GroundSpeed < 1.0, $"holding short of {staged.Parallel} at {aircraft.GroundSpeed:F2} kt");
    }

    [Theory]
    [InlineData("SFO-19L-G")]
    [InlineData("OAK-28L-G")]
    public void PullUp_NeverTrailsThePullUpBrakingCurve(string row) => AssertRidesThePullUpCurve(Stage(row), output);

    /// <summary>
    /// The pull-up's parallel bar flagged unable with its stop moved (kept at its painted stop) the second the taxi takes over: the
    /// taxi rate still makes that stop within the taxi curve's slack, so the firm rate is never published while the taxi rolls onto
    /// it (above 1 kt).
    /// </summary>
    internal static void AssertUnableParallelBarGetsTheTaxiCurveSlack(Staged staged, ITestOutputHelper output)
    {
        AircraftState aircraft = Aircraft(staged);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(aircraft.AircraftType));
        bool flagged = false;
        for (int t = 1; (t <= MaxSeconds) && !IsHeldAtParallel(aircraft); t++)
        {
            bool exiting = aircraft.Phases?.CurrentPhase is RunwayExitPhase;
            staged.Engine.TickOneSecond();
            if (!flagged)
            {
                if (exiting && (aircraft.Phases?.CurrentPhase is TaxiingPhase))
                {
                    HoldShortPoint bar = ParallelBar(aircraft, staged.Parallel);
                    bar.Unable = true;
                    bar.StopMoved = true;
                    flagged = true;
                    output.WriteLine($"t={t}: {staged.Parallel} bar flagged unable, stop moved, at {aircraft.GroundSpeed:F1} kt");
                }

                continue;
            }

            output.WriteLine(
                $"t={t}: {aircraft.Phases?.CurrentPhase?.Name} {aircraft.GroundSpeed:F2} kt, rate {aircraft.Targets.DesiredDecelRate:F2}"
            );
            if ((aircraft.Phases?.CurrentPhase is TaxiingPhase) && (aircraft.GroundSpeed >= 1.0))
            {
                Assert.False(
                    aircraft.Targets.DesiredDecelRate >= firmRate - 1e-9,
                    $"t={t}: firm rate {firmRate:F2} kt/s published at {aircraft.GroundSpeed:F2} kt onto the unable {staged.Parallel} bar"
                );
            }
        }

        Assert.True(flagged, $"{staged.Callsign} never handed off to its pull-up");
        Assert.True(IsHeldAtParallel(aircraft), $"{staged.Callsign} never held short of {staged.Parallel}");
    }

    /// <summary>
    /// From the hand-off to the stop the taxi is never above the sub-ticked braking curve at the pull-up rate onto the parallel bar's
    /// stop, by more than one sub-tick of braking at that rate: an aircraft that trails it ends up inside its taxi-rate stopping distance
    /// before the bar and is braked at the firm rate.
    /// </summary>
    internal static void AssertRidesThePullUpCurve(Staged staged, ITestOutputHelper output)
    {
        double rate = RunwayExitPhase.PullUpBrakeRate(AircraftCategorization.Categorize(Aircraft(staged).AircraftType));
        double subTickSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;

        Run run = RunToParallelBar(staged, output);

        double slackKts = (rate * subTickSeconds) + 0.1;
        foreach (Second s in run.Seconds.Skip(run.HandOff).TakeWhile(s => s.Phase is TaxiingPhase))
        {
            Assert.NotNull(s.ToBarStopFt);
            double curveKts = GroundNavigator.CurveKts(0.0, s.ToBarStopFt.Value / GeoMath.FeetPerNm, rate, subTickSeconds);
            output.WriteLine($"t={s.T}: {s.GroundSpeed:F2} kt, {s.ToBarStopFt:F1} ft to the stop, curve {curveKts:F2} kt");
            Assert.True(
                s.GroundSpeed <= curveKts + slackKts,
                $"t={s.T}: {s.GroundSpeed:F2} kt with {s.ToBarStopFt:F1} ft to the parallel bar's stop, above the {curveKts:F2} kt pull-up curve"
            );
        }
    }

    /// <summary>
    /// The vacate predictor judges the pull-up exit by the exit's own hand-off speed: once the turn-off is under way it predicts the
    /// tail past the bar within the tolerance the SFO 28R occupancy tests hold it to.
    /// </summary>
    [Fact]
    public void Predictor_PullUpExit_PredictsTheVacateWithinTolerance()
    {
        Staged staged = Stage("SFO-19L-G");
        AircraftState aircraft = Aircraft(staged);
        double? predictedSeconds = null;
        int predictedAt = 0;
        int? clearAt = null;
        for (int t = 1; (t <= MaxSeconds) && (clearAt is null); t++)
        {
            staged.Engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is RunwayExitPhase exit)
            {
                if (
                    (predictedSeconds is null)
                    && exit.TurnStarted
                    && (exit.PlannedHandOffKts(aircraft) is not null)
                    && (SameRunwayArrivalProtection.TryBuildRollout(aircraft, 0.0) is { } rollout)
                )
                {
                    (predictedSeconds, predictedAt) = (SameRunwayArrivalProtection.SecondsToRunwayClear(rollout), t);
                }
            }
            else if (predictedSeconds is not null)
            {
                clearAt = t;
            }
        }

        Assert.NotNull(predictedSeconds);
        Assert.NotNull(clearAt);
        double tookSeconds = clearAt.Value - predictedAt;
        output.WriteLine($"predicted clear at t={predictedAt} in {predictedSeconds:F1}s; the pull-up handed off {tookSeconds:F0}s later");
        Assert.True(
            Math.Abs(predictedSeconds.Value - tookSeconds) <= Sfo28rArrivalOccupancyTests.MaxPredictionErrorSeconds,
            $"predicted clear in {predictedSeconds:F1}s, took {tookSeconds:F0}s"
        );
    }

    [Fact]
    public void PullUp_CapLiftsAfterCross()
    {
        Staged staged = Stage("SFO-19L-G");
        AircraftState aircraft = Aircraft(staged);
        double? capKts = null;
        for (int t = 1; (t <= MaxSeconds) && (aircraft.Phases?.CurrentPhase is not HoldingShortPhase); t++)
        {
            staged.Engine.TickOneSecond();
            capKts ??= (aircraft.Phases?.CurrentPhase as TaxiingPhase)?.PullUpCapKts;
        }

        Assert.NotNull(capKts);
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        CommandResult cross = staged.Engine.SendCommand(staged.Callsign, "CROSS");
        Assert.True(cross.Success, cross.Message);

        double maxKts = 0.0;
        for (int t = 1; (t <= 300) && (aircraft.Ground.AssignedTaxiRoute is { IsComplete: false }); t++)
        {
            staged.Engine.TickOneSecond();
            maxKts = Math.Max(maxKts, aircraft.GroundSpeed);
        }

        output.WriteLine($"pull-up cap {capKts:F1} kt; fastest after CROSS {maxKts:F1} kt");
        Assert.True(maxKts > capKts.Value + 2.0, $"never faster than {maxKts:F1} kt after CROSS; the {capKts:F1} kt cap should lift");
    }

    [Fact]
    public void PullUp_SnapshotMidPullUp_RestoresTheSameTrajectory()
    {
        Staged staged = Stage("SFO-19L-G");
        SimulationEngine engine = staged.Engine;
        StateSnapshotDto? beforeLatch = null;
        StateSnapshotDto? afterHandOff = null;
        int beforeLatchT = 0;
        int afterHandOffT = 0;
        var live = new List<Second>();
        for (int t = 1; (t <= MaxSeconds) && !IsHeldAtParallel(Aircraft(staged)); t++)
        {
            StateSnapshotDto snapshot = engine.CaptureSnapshot();
            bool exiting = Aircraft(staged).Phases?.CurrentPhase is RunwayExitPhase;
            engine.TickOneSecond();
            live.Add(Sample(t, Aircraft(staged), staged.Parallel, landing: null));
            if (exiting && (Aircraft(staged).Phases?.CurrentPhase is TaxiingPhase))
            {
                (beforeLatch, beforeLatchT) = (snapshot, t - 1);
            }
            else if ((beforeLatch is not null) && (afterHandOff is null))
            {
                (afterHandOff, afterHandOffT) = (engine.CaptureSnapshot(), t);
            }
        }

        Assert.NotNull(beforeLatch);
        Assert.NotNull(afterHandOff);
        AssertSameTrajectory(staged, beforeLatch, beforeLatchT, live);
        AssertSameTrajectory(staged, afterHandOff, afterHandOffT, live);
    }

    [Fact]
    public void PullUpOff_StillStopsTailPlusTenPastTheBar()
    {
        Staged staged = StageLanding(new LandingSpec("SFO", "19L", "19R", "EXIT G", "B738", AutoPullUp: false));
        AircraftState aircraft = Aircraft(staged);
        int? barNodeId = null;
        for (int t = 1; (t <= MaxSeconds) && (aircraft.Phases?.CurrentPhase is not HoldingAfterExitPhase); t++)
        {
            barNodeId = (aircraft.Phases?.CurrentPhase as RunwayExitPhase)?.TargetHoldShortNodeId ?? barNodeId;
            staged.Engine.TickOneSecond();
        }

        Assert.IsType<HoldingAfterExitPhase>(aircraft.Phases?.CurrentPhase);
        Assert.NotNull(barNodeId);
        Assert.True(aircraft.GroundSpeed < 1.0, $"still moving at {aircraft.GroundSpeed:F2} kt");
        GroundNode bar = aircraft.Ground.Layout!.Nodes[barNodeId.Value];
        double pastFt = GeoMath.DistanceNm(bar.Position, aircraft.Position) * GeoMath.FeetPerNm;
        double expectedFt = (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0) + RunwayTailClearance.MarginFt;
        output.WriteLine($"stopped {pastFt:F1} ft past the bar; tail + 10 ft is {expectedFt:F1} ft");
        Assert.InRange(pastFt, expectedFt - 5.0, expectedFt + 5.0);
    }

    /// <summary>
    /// A HOLD that stops the aircraft just past its bar, then released: the pull-up hands off from rest and still reaches the parallel
    /// runway's bar, its speed cap the planned hand-off speed rather than the near-zero speed it was released at.
    /// </summary>
    [Fact]
    public void PullUp_HeldPastTheBarThenReleased_ReachesTheParallelBar()
    {
        Staged staged = StageHeldPastTheBar();
        double? planned = Assert.IsType<RunwayExitPhase>(Aircraft(staged).Phases?.CurrentPhase).PlannedHandOffKts(Aircraft(staged));
        Assert.NotNull(planned);
        AircraftState aircraft = Aircraft(staged);
        aircraft.Ground.Hold = null;

        // The release tick that hands off to the taxi publishes the pull-up curve, not 0 kt.
        for (int t = 1; (t <= 60) && (aircraft.Phases?.CurrentPhase is RunwayExitPhase); t++)
        {
            staged.Engine.TickOneSecond();
        }

        TaxiingPhase taxi = Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine($"hand-off tick: target {aircraft.Targets.TargetSpeed:F2} kt at {aircraft.GroundSpeed:F2} kt");
        Assert.True(aircraft.Targets.TargetSpeed > 0, $"the hand-off tick's target is {aircraft.Targets.TargetSpeed:F2} kt");

        output.WriteLine($"released: pull-up cap {taxi.PullUpCapKts:F1} kt, planned {planned:F1} kt");
        Assert.NotNull(taxi.PullUpCapKts);
        Assert.Equal(planned.Value, taxi.PullUpCapKts.Value, 1);
        for (int t = 1; (t <= MaxSeconds) && !IsHeldAtParallel(aircraft); t++)
        {
            staged.Engine.TickOneSecond();
        }

        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Contains(staged.Parallel, holding.HoldShort.TargetName ?? "");
    }

    /// <summary>
    /// An exit held past its bar is vacated with its pull-up still pending; restored there and released, it flies the live run.
    /// </summary>
    [Fact]
    public void PullUp_RestoredVacatedWithThePullUpPending_FliesTheLiveRun()
    {
        Staged staged = StageHeldPastTheBar();
        StateSnapshotDto held = staged.Engine.CaptureSnapshot();
        Aircraft(staged).Ground.Hold = null;
        List<Second> live = RunToParallelBar(staged, output).Seconds;

        staged.Engine.RestoreFromSnapshot(held);
        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(Aircraft(staged).Phases?.CurrentPhase);
        Assert.True(exit.Vacated, "the restored exit has vacated 19L");
        Assert.NotNull(exit.PlannedHandOffKts(Aircraft(staged)));
        Aircraft(staged).Ground.Hold = null;
        AssertFliesTheLiveRun(staged, 0, live);
    }

    /// <summary>
    /// A follower ordered before the leader in the aircraft list judges the leader's vacate before the leader's first tick after a
    /// restore: the restored exit must predict what the live one did, so the pull-up plan comes back with the snapshot.
    /// </summary>
    [Fact]
    public void PullUp_RestoredMidExit_PredictsTheLiveVacateBeforeItsFirstTick()
    {
        Staged staged = Stage("SFO-19L-G");
        for (int t = 1; (t <= MaxSeconds) && !(Aircraft(staged).Phases?.CurrentPhase is RunwayExitPhase { TurnStarted: true }); t++)
        {
            staged.Engine.TickOneSecond();
        }

        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(Aircraft(staged).Phases?.CurrentPhase);
        Assert.False(exit.Vacated, "the leader has already vacated 19L");
        Assert.NotNull(exit.PlannedHandOffKts(Aircraft(staged)));
        StateSnapshotDto snapshot = staged.Engine.CaptureSnapshot();
        double liveSeconds = PredictedClearSeconds(Aircraft(staged));

        staged.Engine.RestoreFromSnapshot(snapshot);
        double restoredSeconds = PredictedClearSeconds(Aircraft(staged));

        output.WriteLine($"live predicts clear in {liveSeconds:F3}s, restored in {restoredSeconds:F3}s");
        Assert.Equal(liveSeconds, restoredSeconds, 1e-9);
    }

    /// <summary>
    /// <c>AutoPullUpToParallel</c> turned off mid-exit, before the latch: the exit drops its pull-up and re-plans its stop at once,
    /// stopping tail + 10 ft past the bar on the taxi braking curve with no speed snap.
    /// </summary>
    [Fact]
    public void PullUp_TurnedOffMidExit_StopsTailPlusTenPastTheBarWithoutASnap()
    {
        Staged staged = Stage("SFO-19L-G");
        AircraftState aircraft = Aircraft(staged);
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(aircraft.AircraftType));
        int? barNodeId = null;
        int? offAt = null;
        double peakDecel = 0.0;
        for (int t = 1; (t <= MaxSeconds) && (aircraft.Phases?.CurrentPhase is not HoldingAfterExitPhase); t++)
        {
            if ((offAt is null) && (aircraft.Phases?.CurrentPhase is RunwayExitPhase { TurnStarted: true, Vacated: false } exit))
            {
                Assert.NotNull(exit.PlannedHandOffKts(aircraft));
                barNodeId = exit.TargetHoldShortNodeId;
                staged.Engine.Scenario!.AutoPullUpToParallel = false;
                offAt = t;
                output.WriteLine($"t={t}: pull-up turned off at {aircraft.GroundSpeed:F1} kt");
            }

            double before = aircraft.GroundSpeed;
            staged.Engine.TickOneSecond();
            Assert.IsNotType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
            if (offAt is not null)
            {
                double decel = before - aircraft.GroundSpeed;
                peakDecel = Math.Max(peakDecel, decel);
                Assert.True(decel <= taxiRate + 0.05, $"t={t}: {before:F2} to {aircraft.GroundSpeed:F2} kt, braked at {decel:F2} kt/s");
            }
        }

        output.WriteLine($"peak braking after the switch {peakDecel:F2} kt/s, taxi rate {taxiRate:F2} kt/s");
        Assert.IsType<HoldingAfterExitPhase>(aircraft.Phases?.CurrentPhase);
        Assert.NotNull(barNodeId);
        Assert.True(aircraft.GroundSpeed < 1.0, $"still moving at {aircraft.GroundSpeed:F2} kt");
        GroundNode bar = aircraft.Ground.Layout!.Nodes[barNodeId.Value];
        double pastFt = GeoMath.DistanceNm(bar.Position, aircraft.Position) * GeoMath.FeetPerNm;
        double expectedFt = (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0) + RunwayTailClearance.MarginFt;
        output.WriteLine($"stopped {pastFt:F1} ft past the bar; tail + 10 ft is {expectedFt:F1} ft");
        Assert.InRange(pastFt, expectedFt - 5.0, expectedFt + 5.0);
    }

    /// <summary>
    /// A type too long for the room between SFO 19L's exit G bar and 19R's bar (d ≤ 0) plans no hand-off at speed: it stops at the
    /// exit as before and pulls up from rest, with no speed cap. The phase after the exit is read at sub-tick resolution, since the
    /// taxi that follows the stop can last a single sub-tick before the next phase takes over.
    /// </summary>
    [Fact]
    public void PullUp_NoRoomToPullUpAtSpeed_StopsFirst()
    {
        Staged staged = StageLanding(new LandingSpec("SFO", "19L", "19R", "EXIT G", "B753", AutoPullUp: true));
        bool sawExit = false;
        Phase? afterExit = null;
        SubTickDriver.TickSubTicksUntil(
            staged.Engine,
            staged.Callsign,
            _ => afterExit is not null,
            MaxSeconds,
            ac =>
            {
                if (ac.Phases?.CurrentPhase is RunwayExitPhase exit)
                {
                    sawExit |= exit.TurnStarted;
                    Assert.False(exit.StopsTailInsideRunwayLine, "the B753's exit stop leaves its tail inside 19L's line");
                    Assert.Null(exit.PlannedHandOffKts(ac));
                }
                else if (sawExit)
                {
                    afterExit ??= ac.Phases?.CurrentPhase;
                }
            }
        );

        Assert.True(sawExit, "the B753 never turned off onto G");
        output.WriteLine($"after the exit: {afterExit?.Name}");
        TaxiingPhase taxi = Assert.IsType<TaxiingPhase>(afterExit);
        Assert.Null(taxi.PullUpCapKts);
    }

    /// <summary>
    /// The pull-up planned at the route build cannot start at the hand-off (its route no longer exists on the layout): the exit drops
    /// it, stops at its end and holds there.
    /// </summary>
    [Fact]
    public void PullUp_HandOffFails_StopsAndHoldsAfterTheExit()
    {
        AirportGroundLayout? layout = TestAirportGroundData.LoadUncached("SFO");
        Assert.NotNull(layout);
        _groundData = new OneLayoutGroundData(layout);
        Staged staged = Stage("SFO-19L-G");
        AircraftState aircraft = Aircraft(staged);
        bool broken = false;
        for (int t = 1; (t <= MaxSeconds) && (aircraft.Phases?.CurrentPhase is not HoldingAfterExitPhase); t++)
        {
            if (!broken && (aircraft.Phases?.CurrentPhase is RunwayExitPhase { TurnStarted: true, Vacated: false } exit))
            {
                Assert.NotNull(exit.PlannedHandOffKts(aircraft));
                BreakPullUpRoute(layout, exit);
                broken = true;
            }

            staged.Engine.TickOneSecond();
            Assert.IsNotType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        }

        Assert.True(broken, "the exit never turned off");
        Assert.IsType<HoldingAfterExitPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(aircraft.GroundSpeed < 1.0, $"still moving at {aircraft.GroundSpeed:F2} kt");
    }

    /// <summary>
    /// Removes the first edge of the pull-up from the exit's bar toward the parallel runway, from this test's own layout instance, so
    /// the pull-up's route cannot be built at the hand-off.
    /// </summary>
    private static void BreakPullUpRoute(AirportGroundLayout layout, RunwayExitPhase exit)
    {
        GroundNode bar = Assert.IsType<GroundNode>(exit.TargetHoldShortNode);
        foreach (IGroundEdge edge in bar.Edges.ToList())
        {
            GroundNode from = layout.Nodes[edge.OtherNodeId(bar.Id)];
            if (layout.FindParallelRunwayCrossing(bar, from, "G", "19L") is not { } crossing)
            {
                continue;
            }

            GroundNode next = crossing.PullUpPath[1];
            IGroundEdge pullUpEdge = bar.Edges.First(e => e.OtherNodeId(bar.Id) == next.Id);
            bar.Edges.Remove(pullUpEdge);
            next.Edges.Remove(pullUpEdge);
            return;
        }

        Assert.Fail("no pull-up to 19R from the exit's bar");
    }

    /// <summary>
    /// SFO 19L, exit G, pull-up on, given a HOLD at the start of the second the live run hands off in: the held aircraft rolls past
    /// the bar, vacates 19L and stops there, still in its runway exit with the pull-up pending.
    /// </summary>
    private Staged StageHeldPastTheBar()
    {
        Staged probe = Stage("SFO-19L-G");
        int handOffT = 0;
        for (int t = 1; (t <= MaxSeconds) && (handOffT == 0); t++)
        {
            bool exiting = Aircraft(probe).Phases?.CurrentPhase is RunwayExitPhase;
            probe.Engine.TickOneSecond();
            handOffT = (exiting && (Aircraft(probe).Phases?.CurrentPhase is TaxiingPhase)) ? t : 0;
        }

        Assert.True(handOffT > 0, "the probe run never handed off");
        Staged staged = Stage("SFO-19L-G");
        for (int t = 1; t < handOffT; t++)
        {
            staged.Engine.TickOneSecond();
        }

        AircraftState aircraft = Aircraft(staged);
        output.WriteLine($"t={handOffT}: HOLD at {aircraft.GroundSpeed:F1} kt");
        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        for (int t = 0; (t < 60) && (aircraft.GroundSpeed >= 0.01); t++)
        {
            staged.Engine.TickOneSecond();
        }

        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(aircraft.GroundSpeed < 0.01, $"the held aircraft is still moving at {aircraft.GroundSpeed:F2} kt");
        Assert.True(exit.Vacated, "the held aircraft stopped short of vacating 19L");
        Assert.NotNull(exit.PlannedHandOffKts(aircraft));
        return staged;
    }

    private static double PredictedClearSeconds(AircraftState aircraft)
    {
        SameRunwayArrivalProtection.LeaderRollout? rollout = SameRunwayArrivalProtection.TryBuildRollout(aircraft, 0.0);
        Assert.NotNull(rollout);
        return SameRunwayArrivalProtection.SecondsToRunwayClear(rollout.Value);
    }

    /// <summary>Ground data serving one layout instance, for a test that alters its own copy of an airport.</summary>
    private sealed class OneLayoutGroundData(AirportGroundLayout layout) : IAirportGroundData
    {
        public GroundDataRead Read(string airportId) => new(true, layout, null);
    }

    private static void AssertSameTrajectory(Staged staged, StateSnapshotDto snapshot, int fromT, List<Second> live)
    {
        staged.Engine.RestoreFromSnapshot(snapshot);
        AssertFliesTheLiveRun(staged, fromT, live);
    }

    private static void AssertFliesTheLiveRun(Staged staged, int fromT, List<Second> live)
    {
        foreach (Second expected in live.Where(s => s.T > fromT))
        {
            staged.Engine.TickOneSecond();
            AircraftState aircraft = Aircraft(staged);
            double offNm = GeoMath.DistanceNm(expected.Position, aircraft.Position);
            Assert.True(offNm <= 1e-6, $"restored at t={fromT}: t={expected.T} is {offNm * GeoMath.FeetPerNm:F3} ft off the live run");
            Assert.True(
                Math.Abs(expected.GroundSpeed - aircraft.GroundSpeed) <= 1e-6,
                $"restored at t={fromT}: t={expected.T} at {aircraft.GroundSpeed:F6} kt, live {expected.GroundSpeed:F6} kt"
            );
        }
    }

    internal static Run RunToParallelBar(Staged staged, ITestOutputHelper output)
    {
        var unable = new List<string>();
        void Capture(string callsign, string line)
        {
            if ((callsign == staged.Callsign) && line.Contains("unable", StringComparison.OrdinalIgnoreCase))
            {
                unable.Add(line);
            }
        }

        staged.Engine.WarningEmitted += Capture;
        staged.Engine.TerminalEntryEmitted += entry => Capture(entry.Callsign, entry.Message);

        var seconds = new List<Second>();
        int handOff = -1;
        RunwayInfo? landing = Aircraft(staged).Phases?.AssignedRunway;
        for (int t = 1; (t <= MaxSeconds) && !IsHeldAtParallel(Aircraft(staged)); t++)
        {
            bool exiting = Aircraft(staged).Phases?.CurrentPhase is RunwayExitPhase;
            staged.Engine.TickOneSecond();
            Second s = Sample(t, Aircraft(staged), staged.Parallel, landing);
            seconds.Add(s);
            if (exiting && (s.Phase is not RunwayExitPhase))
            {
                handOff = seconds.Count - 1;
                output.WriteLine($"t={t}: {s.Phase?.Name} at {s.GroundSpeed:F1} kt, parallel bar stop {s.ToBarStopFt:F0} ft ahead");
            }
        }

        Assert.True(handOff >= 0, $"{staged.Callsign} never left its runway exit");
        Assert.True(IsHeldAtParallel(Aircraft(staged)), $"{staged.Callsign} never held short of {staged.Parallel}");
        return new Run(seconds, handOff, unable);
    }

    private static bool IsHeldAtParallel(AircraftState aircraft) =>
        (aircraft.Phases?.CurrentPhase is HoldingShortPhase or HoldingAfterExitPhase) && (aircraft.GroundSpeed < 1.0);

    private static Second Sample(int t, AircraftState aircraft, string parallel, RunwayInfo? landing)
    {
        double? toBarStopFt =
            (
                aircraft.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(hs => IsParallelBar(hs, parallel)) is
                { Latitude: { } lat, Longitude: { } lon }
            )
                ? GeoMath.DistanceNm(aircraft.Position, new LatLon(lat, lon)) * GeoMath.FeetPerNm
                : null;
        bool? vacating =
            ((landing is not null) && (aircraft.Ground.Layout is { } layout))
                ? RunwayOccupancy.IsVacating(aircraft, AircraftBody.Of(aircraft.AircraftType), landing, layout)
                : null;
        return new Second(t, aircraft.Phases?.CurrentPhase, aircraft.GroundSpeed, aircraft.Position, toBarStopFt, vacating);
    }

    private static bool IsParallelBar(HoldShortPoint hs, string parallel) =>
        (hs.Reason == HoldShortReason.RunwayCrossing) && (hs.TargetName ?? "").Contains(parallel);

    private static HoldShortPoint ParallelBar(AircraftState aircraft, string parallel)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint? bar = route.HoldShortPoints.FirstOrDefault(hs => IsParallelBar(hs, parallel));
        Assert.NotNull(bar);
        return bar;
    }

    private static double AlongToNodeFt(TaxiRoute route, int nodeId)
    {
        double alongNm = 0.0;
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            alongNm += segment.Edge.DistanceNm;
            if (segment.ToNodeId == nodeId)
            {
                return alongNm * GeoMath.FeetPerNm;
            }
        }

        Assert.Fail($"node {nodeId} is not on the pull-up route");
        return 0.0;
    }

    private static AircraftState Aircraft(Staged staged)
    {
        AircraftState? aircraft = staged.Engine.FindAircraft(staged.Callsign);
        Assert.NotNull(aircraft);
        return aircraft;
    }

    private Staged Stage(string row) =>
        row switch
        {
            "SFO-19L-G" => StageLanding(new LandingSpec("SFO", "19L", "19R", "EXIT G", "B738", AutoPullUp: true)),
            "OAK-28L-G" => StageLanding(new LandingSpec("OAK", "28L", "28R", "EXIT G", "C172", AutoPullUp: true)),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
        };

    /// <summary>
    /// A landing to stage: the <paramref name="Type"/> lands on <paramref name="Airport"/>'s <paramref name="RunwayId"/>, is told
    /// <paramref name="ExitCommand"/> and pulls up to <paramref name="Parallel"/>, with automatic pull-up set to
    /// <paramref name="AutoPullUp"/>.
    /// </summary>
    private sealed record LandingSpec(string Airport, string RunwayId, string Parallel, string ExitCommand, string Type, bool AutoPullUp);

    /// <summary>
    /// <paramref name="spec"/>'s type on a 1 nm final to its runway, cleared to land and told its exit command (staged as
    /// <c>ParallelRunwayCrossingAfterExitTests.SetupLanding</c>).
    /// </summary>
    private Staged StageLanding(LandingSpec spec)
    {
        (string airport, string runwayId, string parallel, string exitCommand, string type, bool autoPullUp) = spec;
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(_groundData, PushPlanStore.CreateShipped());
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway(airport, runwayId);
        Assert.NotNull(runway);
        AirportGroundLayout? layout = _groundData.Read(airport).Layout;
        Assert.NotNull(layout);

        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        (double lat, double lon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = type,
            Position = new LatLon(lat, lon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 318,
            IndicatedAirspeed = type is "C172" ? 75 : 145,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = airport,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "pull-up-at-speed",
            ScenarioName = "Pull-up at speed",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = airport,
            AutoPullUpToParallel = autoPullUp,
        };

        Assert.True(engine.SendCommand(Callsign, "CLAND").Success);
        Assert.True(engine.SendCommand(Callsign, exitCommand).Success);
        return new Staged(engine, Callsign, parallel);
    }
}
