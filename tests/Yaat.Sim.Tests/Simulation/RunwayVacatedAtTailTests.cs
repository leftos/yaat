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

        List<Sample> samples = StepExit(arrival, layout, stopAfterFirstClear: false);

        Assert.True(exit.StopsTailInsideRunwayLine, "the B772 on K stops with its tail inside 28R's line");
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
