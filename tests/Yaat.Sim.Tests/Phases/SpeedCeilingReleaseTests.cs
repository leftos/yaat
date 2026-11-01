using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// A phase that imposes a <see cref="ControlTargets.SpeedCeiling"/> must release it on the way out.
///
/// <c>SpeedCeiling</c> has no UI representation — it is not an <c>Assigned*</c> field — so one left behind is a silent,
/// permanent cap. <see cref="ProcedureTurnPhase"/> re-applied the AIM 5-4-9.a.3 200 KIAS limit every tick but had no
/// <c>OnEnd</c>, and neither <c>PhaseList.Clear</c> nor the dispatcher's phase-clear block releases it, so a jet pulled
/// off the procedure turn with a vector stayed capped at 200 KIAS for the rest of the session.
/// </summary>
public sealed class SpeedCeilingReleaseTests
{
    public SpeedCeilingReleaseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture()
    {
        var aircraft = new AircraftState
        {
            Callsign = "SKW5131",
            AircraftType = "CRJ2",
            Position = new LatLon(37.99, -122.05),
            TrueHeading = new TrueHeading(360),
            Altitude = 4000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { Destination = "CCR" },
            Phases = new PhaseList(),
        };

        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            FieldElevation = 26,
            Logger = NullLogger.Instance,
        };

        return (aircraft, ctx);
    }

    [Fact]
    public void ProcedureTurn_ReleasesItsSpeedCeiling_WhenThePhaseEnds()
    {
        (AircraftState? aircraft, PhaseContext? ctx) = MakeFixture();

        var pt = new ProcedureTurnPhase
        {
            FixName = "PTFIX",
            FixLat = 37.99,
            FixLon = -122.05,
            InboundCourseDeg = 360,
            PtOutboundCourseDeg = 225,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 3000,
        };
        aircraft.Phases!.Add(pt);

        pt.OnStart(ctx);
        pt.OnTick(ctx);

        // Precondition: the phase really did impose its cap, so this cannot pass vacuously.
        Assert.Equal(200.0, ctx.Targets.SpeedCeiling);

        pt.OnEnd(ctx, PhaseStatus.Completed);

        Assert.Null(ctx.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A ceiling the controller set tighter than the procedure-turn cap was never overwritten by the phase, so it has
    /// to outlive it — releasing unconditionally would silently drop a real speed restriction.
    /// </summary>
    [Fact]
    public void ProcedureTurn_LeavesATighterControllerCeilingAlone()
    {
        (AircraftState? aircraft, PhaseContext? ctx) = MakeFixture();

        var pt = new ProcedureTurnPhase
        {
            FixName = "PTFIX",
            FixLat = 37.99,
            FixLon = -122.05,
            InboundCourseDeg = 360,
            PtOutboundCourseDeg = 225,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 3000,
        };
        aircraft.Phases!.Add(pt);

        ctx.Targets.SpeedCeiling = 180.0;

        pt.OnStart(ctx);
        pt.OnTick(ctx);
        Assert.Equal(180.0, ctx.Targets.SpeedCeiling);

        pt.OnEnd(ctx, PhaseStatus.Completed);

        Assert.Equal(180.0, ctx.Targets.SpeedCeiling);
    }

    private static ProcedureTurnPhase MakeProcedureTurn() =>
        new()
        {
            FixName = "PTFIX",
            FixLat = 37.99,
            FixLon = -122.05,
            InboundCourseDeg = 360,
            PtOutboundCourseDeg = 225,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 3000,
        };

    private static CommandResult Dispatch(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    /// <summary>
    /// An exact speed assignment survives the turn: the 200 KIAS cap the phase imposes for AIM 5-4-9.a.3 is the
    /// procedure's, not the controller's bound, so releasing it must not end a <c>SPD 200</c> the pilot still flies.
    /// </summary>
    [Fact]
    public void ProcedureTurn_KeepsAnExactAssignedSpeed()
    {
        (AircraftState aircraft, PhaseContext ctx) = MakeFixture();
        Assert.True(Dispatch(aircraft, "SPD 200").Success);
        Assert.Equal(200, Assert.IsType<double>(aircraft.Targets.AssignedSpeed), 0.5);

        ProcedureTurnPhase pt = MakeProcedureTurn();
        aircraft.Phases!.Add(pt);
        pt.OnStart(ctx);
        pt.OnTick(ctx);
        Assert.Equal(200.0, ctx.Targets.SpeedCeiling); // precondition: the phase imposed its own cap
        pt.OnEnd(ctx, PhaseStatus.Completed);

        Assert.Equal(200, Assert.IsType<double>(aircraft.Targets.AssignedSpeed), 0.5);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Null(ctx.Targets.SpeedCeiling);
    }

    /// <summary>
    /// "Do not exceed 200" is a controller bound, and the phase leaves a ceiling that tight alone: its release ends the
    /// assignment the bound stood for, as any cleared bound does.
    /// </summary>
    [Fact]
    public void ProcedureTurn_EndsAControllerBoundWithItsAssignment()
    {
        (AircraftState aircraft, PhaseContext ctx) = MakeFixture();
        Assert.True(Dispatch(aircraft, "SPD 200-").Success);
        Assert.Equal(200.0, ctx.Targets.SpeedCeiling);

        ProcedureTurnPhase pt = MakeProcedureTurn();
        aircraft.Phases!.Add(pt);
        pt.OnStart(ctx);
        pt.OnTick(ctx);
        Assert.Equal(200.0, ctx.Targets.SpeedCeiling); // precondition: the controller's cap was already there
        pt.OnEnd(ctx, PhaseStatus.Completed);

        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.Null(ctx.Targets.SpeedCeiling);
    }

    /// <summary>
    /// Who imposed the cap rides the phase's own snapshot, so a restore mid-turn ends the right one: an imposed cap
    /// leaves an exact assignment standing, and a controller bound goes with its assignment.
    /// </summary>
    [Theory]
    [InlineData("SPD 200", true)]
    [InlineData("SPD 200-", false)]
    public void ProcedureTurn_CeilingImposed_SurvivesSnapshotRoundTrip(string command, bool assignmentKept)
    {
        (AircraftState aircraft, PhaseContext ctx) = MakeFixture();
        Assert.True(Dispatch(aircraft, command).Success, $"'{command}' should be accepted");

        ProcedureTurnPhase pt = MakeProcedureTurn();
        aircraft.Phases!.Add(pt);
        pt.OnStart(ctx);
        pt.OnTick(ctx);

        string json = JsonSerializer.Serialize(pt.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = ProcedureTurnPhase.FromSnapshot(JsonSerializer.Deserialize<ProcedureTurnPhaseDto>(json, RecordingJsonOptions.Default)!);
        restored.OnEnd(ctx, PhaseStatus.Completed);

        if (assignmentKept)
        {
            Assert.Equal(200, Assert.IsType<double>(aircraft.Targets.AssignedSpeed), 0.5);
            Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        }
        else
        {
            Assert.Null(aircraft.Targets.AssignedSpeed);
            Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        }

        Assert.Null(ctx.Targets.SpeedCeiling);
    }
}
