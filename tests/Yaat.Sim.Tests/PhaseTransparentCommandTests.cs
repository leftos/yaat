using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that phase-transparent commands (squawk, ident, say, etc.) do NOT
/// destroy active phases or clear the command queue when dispatched.
///
/// Bug: Phase.CanAcceptCommand defaults to ClearsPhase for any unrecognized command
/// type. Squawk/ident/say commands aren't handled by any phase, so they trigger
/// phase destruction even though they only modify transponder/metadata state.
/// </summary>
public class PhaseTransparentCommandTests
{
    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28R", heading: 280, elevationFt: 100);

    private static AircraftState MakeAircraftInUpwind()
    {
        var rwy = DefaultRunway();
        var ac = new AircraftState
        {
            Callsign = "N569SX",
            AircraftType = "C172",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = 800,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Transponder = new AircraftTransponder { AssignedCode = 7110, Code = 7110 },
        };

        var waypoints = PatternGeometry.Compute(rwy, AircraftCategory.Piston, PatternDirection.Right, null, null, null);
        var phases = new PhaseList { AssignedRunway = rwy };
        phases.Add(new UpwindPhase { Waypoints = waypoints });
        phases.Add(new CrosswindPhase { Waypoints = waypoints });
        phases.Add(new DownwindPhase { Waypoints = waypoints });
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        return ac;
    }

    private static CommandResult DispatchSingle(AircraftState ac, ParsedCommand cmd)
    {
        var compound = new CompoundCommand([new ParsedBlock(null, [cmd])]);
        return CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));
    }

    [Fact]
    public void SquawkDuringPhase_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);

        var result = DispatchSingle(ac, new SquawkCommand(1234u));

        Assert.True(result.Success);
        Assert.Equal(1234u, ac.Transponder.Code);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void SquawkPreservesCommandQueue()
    {
        var ac = MakeAircraftInUpwind();

        // Pre-populate queue with a block
        var speedCmd = new SpeedCommand(120);
        var block = new CommandBlock
        {
            Commands = [new TrackedCommand { Type = TrackedCommandType.Speed }],
            Description = "Speed 120",
            NaturalDescription = "Speed 120",
            ApplyAction = (a) => FlightCommandHandler.ApplySpeed(speedCmd, a),
        };
        ac.Queue.Blocks.Add(block);
        Assert.Single(ac.Queue.Blocks);

        var result = DispatchSingle(ac, new SquawkCommand(4567u));

        Assert.True(result.Success);
        Assert.Equal(4567u, ac.Transponder.Code);
        Assert.Single(ac.Queue.Blocks);
    }

    [Fact]
    public void IdentDuringPhase_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        var result = DispatchSingle(ac, new IdentCommand());

        Assert.True(result.Success);
        Assert.True(ac.Transponder.IsIdenting);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void SquawkVfrDuringPhase_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        var result = DispatchSingle(ac, new SquawkVfrCommand());

        Assert.True(result.Success);
        Assert.Equal(1200u, ac.Transponder.Code);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void RandomSquawkDuringPhase_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        var result = DispatchSingle(ac, new RandomSquawkCommand());

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void MixedCompound_SquawkThenHeading_ClearsPhases()
    {
        var ac = MakeAircraftInUpwind();

        // SQ 1234; FH 360 — heading should clear phases as normal
        var compound = new CompoundCommand([
            new ParsedBlock(null, [new SquawkCommand(1234u)]),
            new ParsedBlock(null, [new FlyHeadingCommand(new MagneticHeading(360))]),
        ]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success);
        Assert.Equal(1234u, ac.Transponder.Code);
        Assert.Null(ac.Phases);
    }

    [Fact]
    public void AllTransparentCompound_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        // SQ 1234, ID — both transparent, phases should survive
        var compound = new CompoundCommand([new ParsedBlock(null, [new SquawkCommand(1234u), new IdentCommand()])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success);
        Assert.Equal(1234u, ac.Transponder.Code);
        Assert.True(ac.Transponder.IsIdenting);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    /// <summary>
    /// Regression: SAY-class verbs return Ok("") and surface their value via the
    /// TerminalEmitter rather than the result message. A compound of two SAY verbs
    /// (e.g. "SPOS, SALT") used to join the empty per-command messages with ", ",
    /// producing a stray "RSP &lt;callsign&gt; , " line in the terminal. Filter empties
    /// out of the join so an all-empty compound produces no Response broadcast.
    /// </summary>
    [Fact]
    public void TwoSayCompound_DoesNotEmitCommaResponse()
    {
        var ac = MakeAircraftInUpwind();

        var compound = new CompoundCommand([new ParsedBlock(null, [new SayPositionCommand(), new SayAltitudeCommand()])]);
        var captured = new List<TerminalEntry>();
        var ctx = TestDispatch.Context(new Random(42), validateDctFixes: false, terminalEmitter: captured.Add);
        var result = CommandDispatcher.DispatchCompound(compound, ac, ctx);

        Assert.True(result.Success);
        Assert.True(string.IsNullOrEmpty(result.Message), $"Expected empty result message, got '{result.Message}'");
        Assert.Equal(2, captured.Count);
        Assert.Contains(captured, e => e.Kind == "SayPosition");
        Assert.Contains(captured, e => e.Kind == "SayAltitude");
    }

    [Fact]
    public void TransparentWithoutPhases_WorksNormally()
    {
        var ac = MakeAircraftInUpwind();
        ac.Phases = null; // no phases

        var result = DispatchSingle(ac, new SquawkCommand(5678));

        Assert.True(result.Success);
        Assert.Equal(5678u, ac.Transponder.Code);
        Assert.Null(ac.Phases);
    }

    /// <summary>
    /// Bug: RFIS (Report Field In Sight) — a pure status-flag setter — was hitting the
    /// `_ => ClearsPhase` default in every phase's CanAcceptCommand override and nuking
    /// FinalApproach mid-approach. Fix: phase-transparent pre-screen in DispatchWithPhase.
    /// These tests exercise the forced variants (no NavigationDatabase needed) across a
    /// representative set of phases — Pattern, Tower, and Approach — so a future refactor
    /// that moves the pre-screen per-phase can't silently regress on a forgotten phase.
    /// </summary>
    [Fact]
    public void RfisForcedDuringPattern_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();
        Assert.IsType<UpwindPhase>(ac.Phases!.CurrentPhase);

        var result = DispatchSingle(ac, new ReportFieldInSightForcedCommand());

        Assert.True(result.Success);
        Assert.True(ac.Approach.HasReportedFieldInSight);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void RtisForcedDuringPattern_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        var result = DispatchSingle(ac, new ReportTrafficInSightForcedCommand("N99XY"));

        Assert.True(result.Success);
        Assert.True(ac.Approach.HasReportedTrafficInSight);
        Assert.Equal("N99XY", ac.Approach.LastReportedTrafficCallsign);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void CwtDuringPattern_PreservesPhases()
    {
        var ac = MakeAircraftInUpwind();

        var result = DispatchSingle(ac, new WakeAdvisoryCommand());

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases);
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void RfisForcedDuringFinalApproach_PreservesPhase()
    {
        var ac = MakeAircraftOnFinalApproach();
        Assert.IsType<FinalApproachPhase>(ac.Phases!.CurrentPhase);

        var result = DispatchSingle(ac, new ReportFieldInSightForcedCommand());

        Assert.True(result.Success);
        Assert.True(ac.Approach.HasReportedFieldInSight);
        Assert.NotNull(ac.Phases);
        Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void RfisForcedDuringInterceptCourse_PreservesPhase()
    {
        var ac = MakeAircraftOnInterceptCourse();
        Assert.IsType<InterceptCoursePhase>(ac.Phases!.CurrentPhase);

        var result = DispatchSingle(ac, new ReportFieldInSightForcedCommand());

        Assert.True(result.Success);
        Assert.True(ac.Approach.HasReportedFieldInSight);
        Assert.NotNull(ac.Phases);
        Assert.IsType<InterceptCoursePhase>(ac.Phases.CurrentPhase);
    }

    private static AircraftState MakeAircraftAtParking()
    {
        var ac = new AircraftState
        {
            Callsign = "SWA5115",
            AircraftType = "B738",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Transponder = new AircraftTransponder
            {
                AssignedCode = 233,
                Code = 7110,
                Mode = "Standby",
            },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    /// <summary>
    /// Bug: a parallel block mixing phase-transparent commands with one phase-interactive
    /// command (<c>SQ 0233, SQNORM, PUSH</c>) loses the all-transparent fast path, so it routes
    /// through DispatchWithPhase — which gated the block on its FIRST command. That first command
    /// was the transparent SQ, which AtParkingPhase.CanAcceptCommand rejects ("aircraft is parked
    /// with engines off"), even though each command succeeds when issued individually. The
    /// phase-interactive command must drive the gate regardless of its position in the block.
    /// </summary>
    [Fact]
    public void ParallelBlock_TransparentFirstThenPushback_AtParking_AppliesAll()
    {
        var ac = MakeAircraftAtParking();
        Assert.IsType<AtParkingPhase>(ac.Phases!.CurrentPhase);

        // Parse the real reported input: `,` must yield ONE block of three parallel commands.
        var parsed = CommandParser.ParseCompound("SQ, SQNORM, PUSH");
        Assert.True(parsed.IsSuccess);
        var compound = parsed.Value!;
        Assert.Single(compound.Blocks);
        Assert.Equal(3, compound.Blocks[0].Commands.Count);

        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success, result.Message);
        // Bare SQ is SquawkResetCommand → squawk the assigned code.
        Assert.Equal(233u, ac.Transponder.Code);
        Assert.Equal("C", ac.Transponder.Mode);
        Assert.False(ac.Phases?.CurrentPhase is AtParkingPhase, "PUSH should have moved the aircraft out of AtParkingPhase");
    }

    /// <summary>
    /// The mirror of the above: with the phase-interactive command first, the tower path applied
    /// PUSH and then re-dispatched the remaining parallel commands through TryApplyTowerCommand
    /// only — which returns null for squawk, so SQ/SQNORM were silently dropped. Transparent
    /// siblings must be applied via ApplyCommand.
    /// </summary>
    [Fact]
    public void ParallelBlock_PushbackFirstThenTransparent_AtParking_AppliesAll()
    {
        var ac = MakeAircraftAtParking();

        var compound = new CompoundCommand([
            new ParsedBlock(null, [new PushbackCommand(null, null, null, null, null), new SquawkCommand(233u), new SquawkNormalCommand()]),
        ]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success, result.Message);
        Assert.Equal(233u, ac.Transponder.Code);
        Assert.Equal("C", ac.Transponder.Mode);
        Assert.False(ac.Phases?.CurrentPhase is AtParkingPhase, "PUSH should have moved the aircraft out of AtParkingPhase");
    }

    private static AircraftState MakeAircraftOnFinalApproach()
    {
        var rwy = DefaultRunway();
        var ac = new AircraftState
        {
            Callsign = "JSX170",
            AircraftType = "E145",
            Position = new LatLon(rwy.ThresholdLatitude + 0.05, rwy.ThresholdLongitude + 0.05),
            TrueHeading = rwy.TrueHeading,
            Altitude = 2500,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Transponder = new AircraftTransponder { AssignedCode = 2407, Code = 2407 },
        };

        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var phases = new PhaseList { AssignedRunway = rwy };
        phases.Add(phase);
        phase.Status = PhaseStatus.Active;
        ac.Phases = phases;

        return ac;
    }

    private static AircraftState MakeAircraftOnInterceptCourse()
    {
        var rwy = DefaultRunway();
        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude + 0.05, rwy.ThresholdLongitude + 0.10),
            TrueHeading = new TrueHeading(rwy.TrueHeading.Degrees - 30),
            Altitude = 3000,
            IndicatedAirspeed = 200,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = rwy.TrueHeading,
            ThresholdLat = rwy.ThresholdLatitude,
            ThresholdLon = rwy.ThresholdLongitude,
            ApproachId = "I28R",
        };
        var phases = new PhaseList
        {
            AssignedRunway = rwy,
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = rwy.Designator,
                FinalApproachCourse = rwy.TrueHeading,
            },
        };
        phases.Add(phase);
        phase.Status = PhaseStatus.Active;
        ac.Phases = phases;

        return ac;
    }
}
