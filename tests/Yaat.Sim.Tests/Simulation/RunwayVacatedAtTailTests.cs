using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The runway counts as vacated the moment the tail passes the hold bar (AIM 4-3-21.b), whatever the speed: the exit's vacate
/// side effects (the taxi-in call armed, the held bar marked occupied) happen on the first tick the aircraft is no longer
/// vacating by <see cref="RunwayOccupancy.IsVacating"/>, not when it reaches its stop tail + 10 ft past the bar. A crossover exit
/// whose tail stays inside the line never vacates.
///
/// <para>The arrival is landed by the engine at SFO 28R; from the turn-off on, the test drives the aircraft itself, one physics
/// sub-tick at a time (<see cref="PhaseRunner.Tick"/>, then <see cref="FlightPhysics.Update(AircraftState, double)"/>), so it can
/// judge the pose the phase judges and see what the phase marked occupied in that tick.</para>
/// </summary>
public class RunwayVacatedAtTailTests(ITestOutputHelper output)
{
    private const string Callsign = "UAL1";
    private const double SubTickSeconds = 0.25;
    private const int MaxSubTicks = 2000;

    /// <summary>One sub-tick of the exit: the state before the phase ticked and what the tick did.</summary>
    private sealed record Sample(int Index, bool VacatingBefore, double GroundSpeedBefore, bool AwaitingAfter, bool VacatedAfter, List<int> Marked);

    [Fact]
    public void ExitAtSfo28R_TaxiInCallArms_WhenTheTailPassesTheBar_NotAtTheStop()
    {
        (AirportGroundLayout layout, AircraftState arrival) = LandToTurnOff("B738", explicitExit: null);
        Assert.False(arrival.Ground.AwaitingTaxiInCall, "no taxi-in call is owed before the turn-off");

        List<Sample> samples = StepExit(arrival, layout, stopAfterFirstClear: false);

        Sample firstClear = FirstClear(samples);
        Sample? firstArmed = samples.FirstOrDefault(s => s.AwaitingAfter);
        Assert.NotNull(firstArmed);
        Assert.Equal(firstClear.Index, firstArmed.Index);
        Assert.True(
            firstClear.GroundSpeedBefore > RunwayOccupancy.StoppedGroundSpeedKts,
            $"the tail passed the bar at {firstClear.GroundSpeedBefore:F1} kt; the call must arm then, while still moving"
        );
    }

    [Fact]
    public void ExitAtSfo28R_HeldBarMarkedOccupied_WhenTheTailPassesTheBar()
    {
        (AirportGroundLayout layout, AircraftState arrival) = LandToTurnOff("B738", explicitExit: null);
        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(arrival.Phases?.CurrentPhase);
        int? bar = exit.TargetHoldShortNodeId;
        Assert.NotNull(bar);

        List<Sample> samples = StepExit(arrival, layout, stopAfterFirstClear: false);

        Sample firstClear = FirstClear(samples);
        Assert.Contains(bar.Value, firstClear.Marked);
        Assert.DoesNotContain(samples.Where(s => s.Index < firstClear.Index), s => s.Marked.Contains(bar.Value));
    }

    [Fact]
    public void CrossoverExit_TailInsideTheLine_NeverLatchesVacated()
    {
        (AirportGroundLayout layout, AircraftState arrival) = LandToTurnOff("B772", explicitExit: "EL K");
        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(arrival.Phases?.CurrentPhase);
        Assert.True(exit.StopsTailInsideRunwayLine, "the B772 on K stops with its tail inside 28R's line");

        List<Sample> samples = StepExit(arrival, layout, stopAfterFirstClear: false);

        Assert.DoesNotContain(samples, s => s.VacatedAfter);
        Assert.DoesNotContain(samples, s => s.AwaitingAfter);
        Assert.False(arrival.Ground.AwaitingTaxiInCall, "the B772 is not clear of 28R, so it owes no taxi-in call");
        Assert.IsType<HoldingAfterExitPhase>(arrival.Phases?.CurrentPhase);
    }

    [Fact]
    public void Vacated_RoundTripsThroughASnapshot()
    {
        (AirportGroundLayout layout, AircraftState arrival) = LandToTurnOff("B738", explicitExit: null);
        List<Sample> samples = StepExit(arrival, layout, stopAfterFirstClear: true);
        FirstClear(samples);
        Assert.IsType<RunwayExitPhase>(arrival.Phases?.CurrentPhase);

        AircraftSnapshotDto dto = arrival.ToSnapshot();
        var restored = AircraftState.FromSnapshot(dto, layout);
        restored.Ground.Layout = layout;
        RunwayExitPhase restoredExit = Assert.IsType<RunwayExitPhase>(restored.Phases?.CurrentPhase);
        Assert.True(restoredExit.Vacated, "the restored exit has already vacated 28R");

        // The call already armed (and, say, made since): the restored exit must not arm it, or mark the bar, a second time.
        restored.Ground.AwaitingTaxiInCall = false;
        List<Sample> after = StepExit(restored, layout, stopAfterFirstClear: false);

        Assert.DoesNotContain(after, s => s.AwaitingAfter);
        Assert.DoesNotContain(after, s => s.Marked.Count > 0);
        Assert.IsType<HoldingAfterExitPhase>(restored.Phases?.CurrentPhase);
    }

    /// <summary>
    /// A HOLD given once the aircraft can no longer stop before its tail clears the marking: it rolls past the bar while held, and
    /// the runway is vacated then, not at a stop the held exit never reaches.
    /// </summary>
    [Fact]
    public void Vacated_LatchesWhileHeld()
    {
        double latchFt = TravelToLatchFt();
        (AirportGroundLayout layout, AircraftState arrival) = LandToTurnOff("B738", explicitExit: null);
        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(arrival.Phases?.CurrentPhase);
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);
        var body = AircraftBody.Of(arrival.AircraftType);
        double holdRateKtsPerSec = CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(arrival.AircraftType));

        bool held = false;
        double travelledFt = 0;
        for (int i = 0; (i < MaxSubTicks) && !(held && (arrival.GroundSpeed < 0.01)); i++)
        {
            Assert.Same(exit, arrival.Phases?.CurrentPhase);
            double toClearFt = latchFt - travelledFt;
            double stopFt = arrival.GroundSpeed * arrival.GroundSpeed / (2.0 * holdRateKtsPerSec) * GeoMath.FeetPerNm / 3600.0;
            if (!held && (toClearFt > 0) && (toClearFt < stopFt))
            {
                output.WriteLine($"sub-tick {i}: HOLD at gs={arrival.GroundSpeed:F1}, tail {toClearFt:F1} ft to clear, stops in {stopFt:F1} ft");
                Assert.False(exit.Vacated, "the exit latched before the HOLD");
                Assert.True(RunwayOccupancy.IsVacating(arrival, body, runway, layout), "the tail is already clear at the HOLD");
                arrival.Ground.Hold = HoldDirective.HoldPosition;
                held = true;
            }

            LatLon before = arrival.Position;
            PhaseRunner.Tick(arrival, Context(arrival, layout, []));
            FlightPhysics.Update(arrival, SubTickSeconds);
            travelledFt += GeoMath.DistanceNm(before, arrival.Position) * GeoMath.FeetPerNm;
        }

        Assert.True(held, "the HOLD was never given");
        Assert.True(arrival.GroundSpeed < 0.01, $"the held aircraft is still moving at {arrival.GroundSpeed:F2} kt");
        Assert.True(arrival.Ground.IsImmobile, "the aircraft is still held");
        Assert.False(RunwayOccupancy.IsVacating(arrival, body, runway, layout), "the held aircraft rolled past the bar");
        Assert.True(exit.Vacated, "the held aircraft past the bar has vacated 28R");
        Assert.True(arrival.Ground.AwaitingTaxiInCall, "the held aircraft past the bar owes the taxi-in call");
    }

    /// <summary>
    /// ATL A4 ends at a bar short of 26R's holding distance (<see cref="ShortBranchExitTests"/>): the exit stops on the runway, so
    /// it never latches vacated and never arms the taxi-in call, even with pull-up on.
    /// </summary>
    [Fact]
    public void InsideHoldingDistanceExit_NeverLatchesVacated()
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        ShortFinalArrival.Spawned? spawned = ShortFinalArrival.SpawnClearedToLand("ATL", "26R", "C25A", "TST1", 1.0);
        Assert.NotNull(spawned);
        (SimulationEngine engine, AircraftState aircraft, RunwayInfo _) = spawned;
        Assert.NotNull(engine.Scenario);
        engine.Scenario.AutoPullUpToParallel = true;
        Assert.True(engine.SendCommand("TST1", "EXIT A4").Success);

        HoldingAfterExitPhase? holding = null;
        for (int t = 1; (t <= 400) && (holding is null); t++)
        {
            engine.TickOneSecond();
            Assert.False(aircraft.Phases?.CurrentPhase is RunwayExitPhase { Vacated: true }, $"t={t}: the A4 exit latched vacated");
            Assert.False(aircraft.Ground.AwaitingTaxiInCall, $"t={t}: the A4 exit armed the taxi-in call");
            holding = aircraft.Phases?.CurrentPhase as HoldingAfterExitPhase;
        }

        Assert.NotNull(holding);
        Assert.True(holding.StoppedInsideHoldingDistance);
    }

    /// <summary>
    /// A snapshot a pre-V50 build wrote past the bar carries no <c>Vacated</c> and no armed call (that build armed it at the stop):
    /// the restored exit latches on its first tick, runs the vacate side effects once, and stops as live.
    /// </summary>
    [Fact]
    public void PreV50Snapshot_PastTheBar_RunsVacateSideEffectsOnce()
    {
        (AirportGroundLayout layout, AircraftState arrival) = LandToTurnOff("B738", explicitExit: null);
        int? bar = Assert.IsType<RunwayExitPhase>(arrival.Phases?.CurrentPhase).TargetHoldShortNodeId;
        Assert.NotNull(bar);
        FirstClear(StepExit(arrival, layout, stopAfterFirstClear: true));
        Assert.True(Assert.IsType<RunwayExitPhase>(arrival.Phases?.CurrentPhase).Vacated);

        JsonNode json = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(arrival.ToSnapshot(), RecordingJsonOptions.Default));
        Assert.Equal(1, RemoveProperty(json, "Vacated"));
        AircraftSnapshotDto dto = Assert.IsType<AircraftSnapshotDto>(json.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default));
        var restored = AircraftState.FromSnapshot(dto, layout);
        restored.Ground.Layout = layout;
        restored.Ground.AwaitingTaxiInCall = false;
        Assert.False(Assert.IsType<RunwayExitPhase>(restored.Phases?.CurrentPhase).Vacated);

        List<Sample> after = StepExit(restored, layout, stopAfterFirstClear: false);

        Assert.True(after[0].AwaitingAfter, "the restored exit past the bar arms the taxi-in call on its first tick");
        Assert.True(after[0].VacatedAfter);
        Assert.Single(after, s => s.Marked.Contains(bar.Value));
        Assert.IsType<HoldingAfterExitPhase>(restored.Phases?.CurrentPhase);
    }

    /// <summary>
    /// How far (ft) an unheld B738 travels from the turn-off to the pose its exit latches vacated on: the distance its tail still has
    /// to go to clear 28R's marking, by the latch's own test, measured on an identical run.
    /// </summary>
    private double TravelToLatchFt()
    {
        (AirportGroundLayout layout, AircraftState arrival) = LandToTurnOff("B738", explicitExit: null);
        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(arrival.Phases?.CurrentPhase);
        double travelledFt = 0;
        for (int i = 0; i < MaxSubTicks; i++)
        {
            LatLon before = arrival.Position;
            PhaseRunner.Tick(arrival, Context(arrival, layout, []));
            FlightPhysics.Update(arrival, SubTickSeconds);
            if (exit.Vacated)
            {
                return travelledFt;
            }

            travelledFt += GeoMath.DistanceNm(before, arrival.Position) * GeoMath.FeetPerNm;
        }

        Assert.Fail("the unheld exit never latched vacated");
        return 0;
    }

    /// <summary>Removes every <paramref name="name"/> property in <paramref name="node"/>'s tree; returns how many it removed.</summary>
    private static int RemoveProperty(JsonNode? node, string name)
    {
        switch (node)
        {
            case JsonObject obj:
                int removed = obj.Remove(name) ? 1 : 0;
                return removed + obj.Select(p => p.Value).ToList().Sum(child => RemoveProperty(child, name));
            case JsonArray array:
                return array.ToList().Sum(child => RemoveProperty(child, name));
            default:
                return 0;
        }
    }

    /// <summary>
    /// A shallow high-speed exit whose bar lies far along the runway from where the tail leaves the pavement is still vacating 28R
    /// until its tail reaches that bar's marking (AIM 4-3-21.b, 2-3-5.a.1): T's only 28R bar is 241, Q's is 829.
    /// </summary>
    [Theory]
    [InlineData("EXIT T", 241)]
    [InlineData("EXIT Q", 829)]
    public void HighSpeedExit_StillVacatingUntilTheTailReachesItsBar(string exitCommand, int barNodeId)
    {
        (AirportGroundLayout layout, AircraftState arrival) = LandToTurnOff("E75L", exitCommand);
        Assert.Equal(barNodeId, Assert.IsType<RunwayExitPhase>(arrival.Phases?.CurrentPhase).TargetHoldShortNodeId);
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);
        GroundNode bar = layout.Nodes[barNodeId];
        var body = AircraftBody.Of(arrival.AircraftType);
        for (int i = 0; (i < MaxSubTicks) && (arrival.Phases?.CurrentPhase is RunwayExitPhase); i++)
        {
            if (!RunwayOccupancy.IsVacating(arrival, body, runway, layout))
            {
                LatLon tail = body.PointOf(AircraftBodyPart.Tail, arrival.Position, arrival.TrueHeading.Degrees);
                double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, tail, arrival.Position);
                Assert.True(
                    depthFt <= RunwayOccupancy.HoldLineFoulToleranceFt,
                    $"IsVacating released 28R with the tail {depthFt:F0} ft inside bar {barNodeId}'s marking on {exitCommand} "
                        + $"({RunwayHoldBars.CentrelineFt(runway, tail):F0} ft from the centreline) (AIM 4-3-21.b, 2-3-5.a.1)."
                );
                return;
            }

            PhaseRunner.Tick(arrival, Context(arrival, layout, []));
            FlightPhysics.Update(arrival, SubTickSeconds);
        }

        Assert.Fail("the exit ended without IsVacating releasing 28R");
    }

    private static Sample FirstClear(List<Sample> samples)
    {
        Sample? first = samples.FirstOrDefault(s => !s.VacatingBefore);
        Assert.NotNull(first);
        return first;
    }

    /// <summary>
    /// Lands a <paramref name="type"/> on SFO 28R with automatic pull-up off, sends <paramref name="explicitExit"/> when given,
    /// and ticks the engine until its exit has turned off the centreline.
    /// </summary>
    private (AirportGroundLayout Layout, AircraftState Arrival) LandToTurnOff(string type, string? explicitExit)
    {
        SimulationEngine engine = SfoExitCapacityTests.CreateEngine(output);
        Assert.NotNull(engine.Scenario);
        engine.Scenario.AutoPullUpToParallel = false;

        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        AircraftState arrival = SfoExitCapacityTests.AddArrival(engine, layout, Callsign, type, "28R");
        if (explicitExit is not null)
        {
            CommandResult result = engine.SendCommand(Callsign, explicitExit);
            Assert.True(result.Success, $"{explicitExit} failed: {result.Message}");
        }

        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            if (arrival.Phases?.CurrentPhase is RunwayExitPhase { IsOnCenterline: false } exit)
            {
                output.WriteLine($"t={t}: {type} turning off 28R on {exit.ExitTaxiway} toward bar {exit.TargetHoldShortNodeId}");
                return (layout, arrival);
            }
        }

        Assert.Fail($"{type} never turned off 28R in 400 s; phase {arrival.Phases?.CurrentPhase?.Name}");
        return default;
    }

    /// <summary>
    /// Drives <paramref name="aircraft"/> one sub-tick at a time while its exit phase runs (or until the first sub-tick it is no
    /// longer vacating 28R, when <paramref name="stopAfterFirstClear"/>), recording each sub-tick.
    /// </summary>
    private List<Sample> StepExit(AircraftState aircraft, AirportGroundLayout layout, bool stopAfterFirstClear)
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);
        var body = AircraftBody.Of(aircraft.AircraftType);
        var samples = new List<Sample>();

        for (int i = 0; i < MaxSubTicks; i++)
        {
            if (aircraft.Phases?.CurrentPhase is not RunwayExitPhase exit)
            {
                return samples;
            }

            bool vacating = RunwayOccupancy.IsVacating(aircraft, body, runway, layout);
            double groundSpeed = aircraft.GroundSpeed;
            var marked = new List<int>();
            PhaseRunner.Tick(aircraft, Context(aircraft, layout, marked));
            FlightPhysics.Update(aircraft, SubTickSeconds);

            var sample = new Sample(i, vacating, groundSpeed, aircraft.Ground.AwaitingTaxiInCall, exit.Vacated, marked);
            samples.Add(sample);
            if (!vacating || sample.AwaitingAfter || (marked.Count > 0))
            {
                output.WriteLine(
                    $"sub-tick {i}: vacating={vacating} gs={groundSpeed:F1} awaiting={sample.AwaitingAfter} vacated={sample.VacatedAfter} "
                        + $"marked=[{string.Join(",", marked)}]"
                );
            }

            if (stopAfterFirstClear && !vacating)
            {
                return samples;
            }
        }

        Assert.Fail($"{aircraft.Callsign} was still exiting after {MaxSubTicks} sub-ticks");
        return samples;
    }

    private static PhaseContext Context(AircraftState aircraft, AirportGroundLayout layout, List<int> marked)
    {
        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        return new PhaseContext
        {
            Aircraft = aircraft,
            ActiveRunways = ActiveRunways.Empty,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = SubTickSeconds,
            Runway = runway,
            FieldElevation = runway?.ElevationFt ?? 0,
            GroundLayout = layout,
            Logger = SimLog.CreateLogger("RunwayVacatedAtTailTests"),
            AutoPullUpToParallel = false,
            MarkHoldShortNodeOccupied = marked.Add,
        };
    }
}
