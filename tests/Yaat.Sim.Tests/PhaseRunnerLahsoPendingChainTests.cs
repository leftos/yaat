using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// A LAHSO stop leaves the aircraft on the runway short of the hold-short point, so it holds there (AIM 4-3-11.b.6)
/// whatever its phase chain had queued after the landing. <see cref="PhaseRunner"/> ends the phase the advance just
/// started, drops the rest, and installs the runway-hold chain it installs when the landing was the last phase.
/// </summary>
public class PhaseRunnerLahsoPendingChainTests(ITestOutputHelper output)
{
    private static readonly TestAirportGroundData GroundData = new();

    /// <summary>
    /// SFO 28R on 1 nm final, cleared to land and hold short of 1L, with a phase queued behind the landing. A B738
    /// has no exit before the 1L hold-short point and stops for LAHSO (see <c>LahsoRolloutTests</c>).
    /// </summary>
    [Fact]
    public void LahsoStop_WithPendingPhaseAfterLanding_HoldsOnRunway()
    {
        var placeholder = new HoldingInPositionPhase();
        if (FlyLahsoLandingWithQueuedPhase(placeholder, []) is not { } aircraft)
        {
            return;
        }

        List<Phase> chain = aircraft.Phases!.Phases;
        Assert.IsType<RunwayHoldingPhase>(aircraft.Phases.CurrentPhase);

        // The queued phase was ended as skipped and is behind the current index: it never runs.
        Assert.Equal(PhaseStatus.Skipped, placeholder.Status);
        Assert.True(chain.IndexOf(placeholder) < aircraft.Phases.CurrentIndex, "the dropped phase must not be ahead of the runway hold");

        // The upcoming chain is exactly the one a LAHSO stop with nothing queued gets.
        List<Type> expected = [typeof(RunwayHoldingPhase), typeof(RunwayExitPhase), typeof(HoldingAfterExitPhase)];
        List<Type> upcoming = [.. chain.Skip(aircraft.Phases.CurrentIndex).Select(p => p.GetType())];
        Assert.Equal(expected, upcoming);
        Assert.Null(aircraft.Phases.LahsoHoldShort);
    }

    /// <summary>
    /// The phase queued behind the landing is skipped by the LAHSO stop, so it must never be offered the queued
    /// command blocks. The placeholder is a <see cref="LinedUpAndWaitingPhase"/>: its unsatisfied takeoff-clearance
    /// requirement makes it a wait phase (the only kind <see cref="FlightPhysics.NotifyPhaseAdvanced"/> offers an
    /// untriggered block to), and it accepts CTO. The settled current phase, <see cref="RunwayHoldingPhase"/>, rejects
    /// CTO — so the expected outcome is unambiguous: the CTO block is still pending after the LAHSO transition tick.
    /// </summary>
    [Fact]
    public void LahsoStop_QueuedBlock_IsNotConsumedByTheSkippedPlaceholder()
    {
        var placeholder = new LinedUpAndWaitingPhase();
        int applyCount = 0;
        var ctoBlock = new CommandBlock
        {
            Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }],
            ParsedCommands = [new ClearedForTakeoffCommand(new DefaultDeparture())],
            Description = "CTO",
            NaturalDescription = "Cleared for takeoff",
            ApplyAction = _ =>
            {
                applyCount++;
                return new CommandResult(true);
            },
        };

        if (FlyLahsoLandingWithQueuedPhase(placeholder, [ctoBlock]) is not { } aircraft)
        {
            return;
        }

        Assert.IsType<RunwayHoldingPhase>(aircraft.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Skipped, placeholder.Status);

        Assert.False(ctoBlock.IsApplied, "the CTO block was applied against the skipped placeholder phase");
        Assert.Equal(0, applyCount);
        Assert.Contains(ctoBlock, aircraft.Queue.Blocks);
    }

    /// <summary>
    /// With nothing queued behind the landing, the LAHSO stop installs <see cref="RunwayHoldingPhase"/> — a wait phase
    /// (unsatisfied runway-crossing requirement) that accepts ER — and offers it the queued blocks once the list is
    /// settled. An untriggered ER block waiting in the queue therefore fires exactly once, on the tick the aircraft
    /// stops, against the runway hold.
    /// </summary>
    [Fact]
    public void LahsoStop_WithNothingQueuedBehind_FiresAQueuedExitBlockAtTheStop()
    {
        int applyCount = 0;
        Phase? appliedDuring = null;
        var exitBlock = new CommandBlock
        {
            Commands = [new TrackedCommand { Type = TrackedCommandType.Immediate }],
            ParsedCommands = [new ExitRightCommand()],
            Description = "ER",
            NaturalDescription = "Exit right",
            ApplyAction = a =>
            {
                applyCount++;
                appliedDuring = a.Phases?.CurrentPhase;
                return new CommandResult(true);
            },
        };

        if (FlyLahsoLandingWithQueuedPhase(null, [exitBlock]) is not { } aircraft)
        {
            return;
        }

        Assert.IsType<RunwayHoldingPhase>(aircraft.Phases!.CurrentPhase);
        Assert.True(exitBlock.IsApplied, "the queued ER block should fire when the aircraft stops for LAHSO");
        Assert.Equal(1, applyCount);
        Assert.IsType<RunwayHoldingPhase>(appliedDuring);
    }

    /// <summary>
    /// Flies the SFO 28R LAHSO-1L landing with <paramref name="queuedPhase"/> (when not null) queued behind the
    /// <see cref="LandingPhase"/> and each of <paramref name="queuedBlocks"/> appended to the command queue, until the
    /// landing completes. Returns null when the navdata or the SFO layout is unavailable.
    /// </summary>
    private AircraftState? FlyLahsoLandingWithQueuedPhase(Phase? queuedPhase, IReadOnlyList<CommandBlock> queuedBlocks)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        RunwayInfo? runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);

        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);
        var aircraft = new AircraftState
        {
            Callsign = "TST001",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            Altitude = runway28R.ElevationFt + 318,
            IndicatedAirspeed = 145,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway28R },
        };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));

        var engine = new SimulationEngine(GroundData);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-lahso-pending",
            ScenarioName = "SFO 28R LAHSO pending chain",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        CommandResult lahsoResult = engine.SendCommand("TST001", "LAHSO 1L");
        Assert.True(lahsoResult.Success, $"LAHSO 1L failed: {lahsoResult.Message}");

        if (queuedPhase is not null)
        {
            aircraft.Phases.Add(queuedPhase);
        }

        aircraft.Queue.Blocks.AddRange(queuedBlocks);
        LandingPhase landing = aircraft.Phases.Phases.OfType<LandingPhase>().Single();

        for (int t = 1; (t <= 420) && (landing.Status != PhaseStatus.Completed); t++)
        {
            engine.TickOneSecond();
        }

        output.WriteLine(
            $"current={aircraft.Phases.CurrentPhase?.Name ?? "none"} index={aircraft.Phases.CurrentIndex} "
                + $"chain=[{string.Join(", ", aircraft.Phases.Phases.Select(p => $"{p.Name}:{p.Status}"))}]"
        );

        Assert.Equal(PhaseStatus.Completed, landing.Status);
        Assert.True(landing.StoppedForLahso, "the B738 should have completed its landing as a LAHSO stop");
        return aircraft;
    }
}
