using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The two hand-off bugs around a ROLLING line-up that still carries its <see cref="LinedUpAndWaitingPhase"/>:
/// a takeoff clearance issued while the line-up is already active pre-satisfies the upcoming LUAW (the
/// rolling-takeoff fallback) and upgrades the line-up to rolling (<see cref="LineUpPhase.TryUpgradeToRolling"/>),
/// so the line-up hands off at 10-15 kt to a phase that will complete on its first tick.
///
/// <list type="number">
///   <item>A phase ported into <see cref="LinedUpAndWaitingPhase.OnStart"/> stops the aircraft where it is, but a
///         pre-satisfied one is only passing through for one sub-tick — publishing the stop brakes the hand-off
///         speed (a jet's 5 kt/s taxi brake = 1.25 kt in 0.25 s) and the takeoff roll then seeds its clock from
///         the lowered speed. The zero is skipped only when the phase starts already satisfied; a LUAW reached
///         unsatisfied still stops the aircraft.</item>
///   <item><c>CTOC</c> while the line-up is active with the LUAW still upcoming held position but left the line-up
///         in rolling mode with the graph navigator's speed floor raised, so a later release flowed the route at
///         the floor to the end of the rollout. Both cancel branches revert the mode and drop the floor.</item>
/// </list>
///
/// Driven through the SFO engine harness of <see cref="GroundNavigatorRouteEndSpeedTests"/>: the aircraft taxis
/// the tail of taxiway E to the 28R bar, lines up on <c>LUAW</c>, and is then cleared while that line-up is
/// rolling onto the runway. Physics is the only speed integrator, so the hand-off is read sub-tick by sub-tick
/// through <see cref="SimulationEngine.TickPhysics"/>.
/// </summary>
[Collection("NavDbMutator")]
public class LuawRollingHandoffTests(ITestOutputHelper output)
{
    /// <summary>The scenario's single aircraft, placed just short of the 28R bar on SFO taxiway E.</summary>
    private const string Callsign = "TEST1";

    /// <summary>One physics sub-tick; the shipping sim runs four per second.</summary>
    private const double SubTick = 1.0 / SimulationEngine.PhysicsSubTickRate;

    /// <summary>On taxiway E, short of the 28R bar and pointing at it — the last straight of the taxi.</summary>
    private static readonly LatLon ApproachPose = new(37.622442, -122.375395);

    private const double ApproachHeadingDeg = 225.3;

    /// <summary>A Large jet: eligible for a rolling takeoff, and its 5 kt/s taxi brake is the 1.25 kt bite.</summary>
    private const string JetType = "B738";

    /// <summary><see cref="LineUpPhase.Name"/> — the phase that owns the rolling hand-off.</summary>
    private const string LineUpPhaseName = "LiningUp";

    /// <summary><see cref="TakeoffPhase.Name"/> — the phase whose arrival ends a hand-off.</summary>
    private const string TakeoffPhaseName = "Takeoff";

    /// <summary><see cref="LinedUpAndWaitingPhase.Name"/> — the phase the rolling line-up passes through.</summary>
    private const string LuawPhaseName = "LinedUpAndWaiting";

    /// <summary>How long the line-up may take to reach the LUAW before the test gives up (sub-ticks: 120 s of sim at four per second).</summary>
    private const int HandOffBudgetSubTicks = 4 * 120;

    // -------------------------------------------------------------------------
    // (b) The pass-through must not stop the rolling hand-off
    // -------------------------------------------------------------------------

    /// <summary>
    /// A rolling line-up that reaches a pre-satisfied LUAW must not publish a stop on its way past: the phase
    /// completes on its first tick, so the stop would brake the hand-off speed for a sub-tick and
    /// <see cref="TakeoffPhase.OnStart"/> then seeds the roll clock from the lowered speed. Read sub-tick by
    /// sub-tick: the speed target never drops to 0 across the hand-off and the IAS does not dip.
    /// </summary>
    [Fact]
    public void PreSatisfiedLuaw_AcrossTheHandOff_DoesNotZeroTheSpeedTarget()
    {
        SimulationEngine? engine = BuildEngine(JetType);
        if (engine is null)
        {
            return;
        }

        _ = ReachRollingLineUp(engine);

        List<SubTickSample> samples = RunHandOff(engine);
        int lastLiningUp = samples.FindLastIndex(s => s.Phase == LineUpPhaseName);
        Assert.True(lastLiningUp >= 0, $"the line-up never completed (last phase={samples[^1].Phase})");
        int firstTakeoff = samples.FindIndex(lastLiningUp, s => s.Phase == TakeoffPhaseName);
        Assert.True(firstTakeoff > lastLiningUp, $"the line-up never handed off to the takeoff roll (last phase={samples[^1].Phase})");

        // The window between the two is the pass-through itself, and it must contain the LUAW whose zero the
        // test is about — otherwise the zero check below is vacuous.
        List<SubTickSample> passThrough = [.. samples.Skip(lastLiningUp + 1).Take(firstTakeoff - lastLiningUp - 1)];
        Assert.Contains(passThrough, s => s.Phase == LuawPhaseName);

        foreach (SubTickSample sample in passThrough)
        {
            output.WriteLine($"hand-off: phase={sample.Phase} target={Describe(sample.TargetSpeedKts)} ias={sample.IasKts:F2}kt");
            Assert.True(
                sample.TargetSpeedKts != 0.0,
                $"a 0 kt speed target was published in {sample.Phase} during the LUAW hand-off "
                    + $"(ias={sample.IasKts:F2}kt) — the pass-through stopped a rolling takeoff"
            );
        }

        // The hand-off speed the roll clock seeds from: the aircraft flowed through the line-up at 10-15 kt,
        // so a dip here is the pass-through's own brake, not the line-up braking it deliberately.
        double handOffIas = samples[lastLiningUp].IasKts;
        double lowestIas = samples.Skip(lastLiningUp).Take(firstTakeoff - lastLiningUp + 1).Min(s => s.IasKts);
        output.WriteLine($"hand-off at {handOffIas:F2}kt, lowest through the roll start {lowestIas:F2}kt");
        Assert.True(
            lowestIas > handOffIas - 0.3,
            $"the aircraft dipped from {handOffIas:F2}kt to {lowestIas:F2}kt across the LUAW hand-off — a "
                + $"pre-satisfied LUAW must not brake the speed the takeoff roll seeds from"
        );

        // The roll itself continues from that speed: the LUAW leaves no standing start behind.
        Assert.True(
            samples[firstTakeoff].IasKts > 8.0,
            $"the takeoff roll began at {samples[firstTakeoff].IasKts:F2} kt — a rolling hand-off enters the roll "
                + "an order of magnitude above a standing start"
        );
    }

    /// <summary>
    /// The absent state beside it, end to end: a LUAW reached UNSATISFIED is the aircraft waiting on the
    /// controller, and it must still come to rest where it lines up. An end-to-end smoke check only — the
    /// non-rolling line-up has already braked the aircraft before the phase starts, so
    /// <see cref="UnsatisfiedLuaw_StillPublishesTheStop"/> is the guard that the phase itself commands the stop.
    /// </summary>
    [Fact]
    public void UnsatisfiedLuaw_StillStopsTheAircraft()
    {
        SimulationEngine? engine = BuildEngine(JetType);
        if (engine is null)
        {
            return;
        }

        bool holdingShort = TickUntil(engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 60);
        Assert.True(holdingShort, $"the taxi never reached the 28R hold short (last phase={CurrentPhaseName(engine)})");

        CommandResult luaw = engine.SendCommand(Callsign, "LUAW");
        Assert.True(luaw.Success, $"LUAW at the hold short should line the aircraft up: {luaw.Message}");

        bool linedUp = TickUntil(engine, ac => ac.Phases?.CurrentPhase is LinedUpAndWaitingPhase, 90);
        Assert.True(linedUp, $"the aircraft never reached the LUAW (last phase={CurrentPhaseName(engine)})");

        AircraftState ac = engine.FindAircraft(Callsign)!;
        output.WriteLine($"at the LUAW: target={Describe(ac.Targets.TargetSpeed)} ias={ac.IndicatedAirspeed:F2}kt");
        Assert.True(
            ac.Targets.TargetSpeed is null or 0.0,
            $"a LUAW waiting on the controller published {Describe(ac.Targets.TargetSpeed)} — it must stop the aircraft"
        );
        Assert.True(ac.IndicatedAirspeed < 0.5, $"the lined-up aircraft is still rolling at {ac.IndicatedAirspeed:F2} kt");
    }

    /// <summary>
    /// The same absent state at the phase itself: the skip is only for a phase that will complete on its first
    /// tick, so an unsatisfied LUAW must still publish the stop from both of its ticks — a stale positive target
    /// is what a phase that does nothing would leave behind.
    /// </summary>
    [Fact]
    public void UnsatisfiedLuaw_StillPublishesTheStop()
    {
        var aircraft = new AircraftState
        {
            Callsign = "LUAW01",
            AircraftType = JetType,
            IsOnGround = true,
        };
        aircraft.Targets.TargetSpeed = 12.0;
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = SubTick,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };
        var luaw = new LinedUpAndWaitingPhase();

        luaw.OnStart(ctx);
        Assert.Equal(0.0, ctx.Targets.TargetSpeed);

        aircraft.Targets.TargetSpeed = 12.0;
        Assert.False(luaw.OnTick(ctx), "a LUAW without a takeoff clearance must keep waiting");
        Assert.Equal(0.0, ctx.Targets.TargetSpeed);
    }

    // -------------------------------------------------------------------------
    // (c) The cancel branch that keeps the rolling floor
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>CTOC</c> on a rolling line-up whose LUAW is still upcoming must hold position AND revert the two things
    /// the rolling clearance raised: <see cref="LineUpPhase.RollingMode"/> and the graph navigator's speed floor.
    /// Left raised, a line-up released by LUAW instead of a fresh CTO flows the graph route at the floor all the
    /// way to the end of the rollout and hard-snaps to zero there instead of braking onto the centerline.
    /// </summary>
    [Fact]
    public void Ctoc_DuringRollingLineUpWithUpcomingLuaw_RevertsRollingAndClearsTheFloor()
    {
        SimulationEngine? engine = BuildEngine(JetType);
        if (engine is null)
        {
            return;
        }

        LineUpPhase lineup = ReachRollingLineUp(engine);

        output.WriteLine($"rolling line-up: floor={lineup.NavigatorSpeedFloorKts:F1}kt rolling={lineup.RollingMode}");
        Assert.True(lineup.RollingMode, "the takeoff clearance issued during the line-up must have flipped it to rolling");
        Assert.True(
            lineup.NavigatorSpeedFloorKts > 0.0,
            $"the rolling upgrade must raise the graph navigator's floor (got {lineup.NavigatorSpeedFloorKts:F2} kt) — "
                + "the cancel branch only has a floor to clear if the line-up is flying one"
        );

        // The LUAW is the phase that handles this cancel: it is still upcoming in the list, pre-satisfied by the
        // clearance the line-up just upgraded from.
        AircraftState ac = engine.FindAircraft(Callsign)!;
        Assert.Contains(ac.Phases!.Phases, p => p is LinedUpAndWaitingPhase { Status: PhaseStatus.Pending });

        CommandResult cancel = engine.SendCommand(Callsign, "CTOC");
        Assert.True(cancel.Success, $"CTOC during the line-up should succeed: {cancel.Message}");
        Assert.True(lineup.HoldPosition, "CTOC must stop the aircraft where it is (7110.65 §3-9-11)");
        Assert.False(lineup.RollingMode, "a cancelled takeoff clearance must revert the rolling line-up");
        Assert.Equal(0.0, lineup.NavigatorSpeedFloorKts, 3);

        // The cancel's brake: with rolling mode reverted and the floor dropped, the aircraft still rolls forward
        // and brakes at the category's taxi rate (a jet's 5 kt/s = 1.25 kt per sub-tick). A floor left raised
        // shows up here as a flow to the end of the rollout and a hard snap to zero instead.
        double brakeLimitKts = (CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet) * SubTick) + 0.1;
        double previousIas = ac.IndicatedAirspeed;
        double largestDropKts = 0;
        int brakeSubTicks = 0;
        for (; brakeSubTicks < 4 * 30; brakeSubTicks++)
        {
            engine.TickPhysics(SubTick);
            AircraftState braking = engine.FindAircraft(Callsign)!;
            largestDropKts = Math.Max(largestDropKts, previousIas - braking.IndicatedAirspeed);
            previousIas = braking.IndicatedAirspeed;
            if (previousIas < 0.05)
            {
                break;
            }
        }

        output.WriteLine(
            $"after the cancel: stopped in {brakeSubTicks} sub-ticks, largest single drop {largestDropKts:F2}kt (limit {brakeLimitKts:F2}kt)"
        );
        Assert.True(previousIas < 0.05, $"the cancelled line-up never came to a stop ({previousIas:F2} kt after {brakeSubTicks} sub-ticks)");
        Assert.True(
            largestDropKts <= brakeLimitKts,
            $"the aircraft lost {largestDropKts:F2} kt in one sub-tick after the cancel — more than the {brakeLimitKts:F2} kt a "
                + $"{CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet):F1} kt/s jet taxi brake takes, so the line-up snapped to a stop "
                + "instead of braking onto the centerline"
        );
    }

    // -------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------

    /// <summary>One sub-tick of the hand-off: the phase that owns the speed, its target and the aircraft's IAS.</summary>
    private sealed record SubTickSample(string Phase, double? TargetSpeedKts, double IasKts);

    /// <summary>One aircraft on SFO taxiway E short of the 28R bar, with one preset taxi to it.</summary>
    private static string BuildScenarioJson(int headingMag, string aircraftType)
    {
        return $$"""
            {
              "id": "test-luaw-rolling-handoff",
              "name": "LUAW Rolling Hand-off Test",
              "artccId": "ZOA",
              "primaryAirportId": "SFO",
              "initializationTriggers": [],
              "aircraftGenerators": [],
              "aircraft": [
                {
                  "id": "test-luaw-rolling-handoff-1",
                  "aircraftId": "{{Callsign}}",
                  "aircraftType": "{{aircraftType}}",
                  "transponderMode": "Standby",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": {"lat": {{ApproachPose.Lat}}, "lon": {{ApproachPose.Lon}}},
                    "heading": {{headingMag}}
                  },
                  "onAltitudeProfile": false,
                  "flightplan": {
                    "rules": "IFR",
                    "departure": "KSFO",
                    "destination": "KLAX",
                    "cruiseAltitude": 8000,
                    "cruiseSpeed": 170,
                    "route": "",
                    "remarks": "",
                    "aircraftType": "{{aircraftType}}/G"
                  },
                  "presetCommands": [
                    {"id": "p1", "command": "TAXI E RWY 28R", "timeOffset": 0}
                  ],
                  "spawnDelay": 0,
                  "airportId": "SFO",
                  "difficulty": "Easy"
                }
              ],
              "atc": [],
              "studentPositionId": "",
              "autoDeleteMode": "Parked",
              "flightStripConfigurations": []
            }
            """;
    }

    /// <summary>
    /// The SFO engine with one aircraft taxiing to the 28R bar, or null (after printing why) when the fixture
    /// data is missing.
    /// </summary>
    private SimulationEngine? BuildEngine(string aircraftType)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            output.WriteLine("SKIP: SFO ground layout not available");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());

        int headingMag = (int)
            Math.Round(
                MagneticDeclination.TrueToMagnetic(ApproachHeadingDeg, ApproachPose.Lat, ApproachPose.Lon, MagneticDeclination.EvaluationDateUtc)
            );
        foreach (
            string warning in engine.LoadScenario(
                BuildScenarioJson(headingMag, aircraftType),
                rngSeed: 42,
                sessionStartUtc: MagneticDeclination.EvaluationDateUtc
            )
        )
        {
            output.WriteLine($"[WARN] {warning}");
        }

        return engine;
    }

    /// <summary>
    /// Taxi to the bar and line up on <c>LUAW</c> (so the phase list keeps its LUAW), then issue <c>CTO</c> while
    /// the line-up is rolling onto the runway at or above the upgrade speed: that pre-satisfies the upcoming LUAW
    /// and flips the line-up to rolling, which is the phase-list shape both bugs live in. Returns the upgraded
    /// line-up; fails the test when the run never reaches that state.
    /// </summary>
    private LineUpPhase ReachRollingLineUp(SimulationEngine engine)
    {
        bool holdingShort = TickUntil(engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 60);
        Assert.True(holdingShort, $"the taxi never reached the 28R hold short (last phase={CurrentPhaseName(engine)})");

        CommandResult luaw = engine.SendCommand(Callsign, "LUAW");
        Assert.True(luaw.Success, $"LUAW at the hold short should line the aircraft up: {luaw.Message}");

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            AircraftState? ac = engine.FindAircraft(Callsign);
            if (ac?.Phases?.CurrentPhase is not LineUpPhase active || active.RollingMode)
            {
                continue;
            }

            output.WriteLine(
                $"[t={t}] line-up active: ias={ac.IndicatedAirspeed:F1}kt state={active.CurrentState} floor={active.NavigatorSpeedFloorKts:F1}kt"
            );
            if (ac.IndicatedAirspeed < LineUpPhase.RollingUpgradeMinSpeedKts)
            {
                continue;
            }

            CommandResult upgrade = engine.SendCommand(Callsign, "CTO");
            Assert.True(upgrade.Success, $"CTO during the line-up should pre-satisfy the LUAW: {upgrade.Message}");
            if (active.RollingMode)
            {
                output.WriteLine($"[t={t}] upgraded to rolling: floor={active.NavigatorSpeedFloorKts:F1}kt ias={ac.IndicatedAirspeed:F1}kt");
                return active;
            }

            output.WriteLine($"[t={t}] rolling upgrade rejected at {ac.IndicatedAirspeed:F1}kt (state={active.CurrentState})");
        }

        Assert.Fail($"never reached a rolling line-up with an upcoming LUAW (last phase={CurrentPhaseName(engine)})");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Advance the sim one physics sub-tick at a time from the rolling line-up until the takeoff roll has run a
    /// second, recording the phase, speed target and IAS after each sub-tick.
    /// </summary>
    private List<SubTickSample> RunHandOff(SimulationEngine engine)
    {
        var samples = new List<SubTickSample>();
        int takeoffSubTicks = 0;
        for (int k = 0; k < HandOffBudgetSubTicks; k++)
        {
            engine.TickPhysics(SubTick);
            AircraftState? ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            string phase = ac.Phases?.CurrentPhase?.Name ?? "(none)";
            samples.Add(new SubTickSample(phase, ac.Targets.TargetSpeed, ac.IndicatedAirspeed));
            output.WriteLine($"sub-tick {k}: phase={phase} target={Describe(ac.Targets.TargetSpeed)} ias={ac.IndicatedAirspeed:F2}kt");

            if (phase == TakeoffPhaseName)
            {
                takeoffSubTicks++;
                if (takeoffSubTicks > SimulationEngine.PhysicsSubTickRate)
                {
                    break;
                }
            }
        }

        Assert.NotEmpty(samples);
        return samples;
    }

    /// <summary>Tick one second at a time until <paramref name="satisfied"/> holds; false when it never does.</summary>
    private bool TickUntil(SimulationEngine engine, Func<AircraftState, bool> satisfied, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState? ac = engine.FindAircraft(Callsign);
            if (ac is not null && satisfied(ac))
            {
                output.WriteLine($"[t={t}] reached the expected state ({ac.Phases?.CurrentPhase?.Name ?? "(none)"})");
                return true;
            }
        }

        return false;
    }

    private static string CurrentPhaseName(SimulationEngine engine) => engine.FindAircraft(Callsign)?.Phases?.CurrentPhase?.Name ?? "(none)";

    private static string Describe(double? targetSpeedKts) => targetSpeedKts is { } target ? $"{target:F2}kt" : "null";
}
