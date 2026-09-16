using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="ControlTargets.SpeedCommandIsControllerIssued"/> records who owns an aircraft's speed:
/// a human controller, or a scenario preset / AI position. Both set
/// <see cref="ControlTargets.HasExplicitSpeedCommand"/>, so that flag alone cannot tell a scripted
/// <c>AT HEMAN SPD 180 DUYET</c> from an instructor typing <c>SPD 180</c> — the provenance flag can.
///
/// <para>Every command that sets the explicit flag must carry the provenance with it, and every
/// <em>command</em> that clears the explicit flag must clear the provenance too — a stale
/// <see langword="true"/> disables every consumer that reads it, the same-runway arrival protection among
/// them, for the rest of that aircraft's life. The one deliberate exception is a <em>phase</em> borrowing
/// speed authority: <see cref="MakeTurnPhase"/> and its siblings hand the speed back at the end of the
/// maneuver, which neither grants nor revokes a controller's ownership (§5-7-4 retains an assignment until
/// it is deleted). All four rules are pinned below.</para>
/// </summary>
public class ControlTargetsSpeedProvenanceTests
{
    private static AircraftState Arrival() =>
        new()
        {
            Callsign = "SKW5536",
            AircraftType = "CRJ2",
            IsOnGround = false,
            Altitude = 6000,
            IndicatedAirspeed = 250,
        };

    private static CommandResult Dispatch(AircraftState aircraft, ParsedCommand command, bool isScenarioScripted) =>
        CommandDispatcher.Dispatch(command, aircraft, TestDispatch.Context(Random.Shared, isScenarioScripted: isScenarioScripted));

    /// <summary>A fresh arrival with <paramref name="command"/> applied; asserts the dispatch itself succeeded.</summary>
    private static AircraftState AfterDispatch(ParsedCommand command, bool isScenarioScripted)
    {
        TestVnasData.EnsureInitialized();
        var aircraft = Arrival();
        var result = Dispatch(aircraft, command, isScenarioScripted);
        Assert.True(result.Success, result.Message);
        return aircraft;
    }

    /// <summary>An arrival already carrying an instructor's speed assignment — both flags set.</summary>
    private static AircraftState WithControllerIssuedSpeed()
    {
        var aircraft = AfterDispatch(new SpeedCommand(180), isScenarioScripted: false);
        Assert.True(aircraft.Targets.SpeedCommandIsControllerIssued);
        return aircraft;
    }

    // ---- Commands that take speed authority -------------------------------------------

    [Fact]
    public void ScriptedSpeed_SetsExplicitFlag_ButNotControllerProvenance()
    {
        var aircraft = AfterDispatch(new SpeedCommand(180), isScenarioScripted: true);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ControllerSpeed_SetsBothFlags()
    {
        var aircraft = AfterDispatch(new SpeedCommand(180), isScenarioScripted: false);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.True(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ScriptedReduceToFinalApproachSpeed_SetsExplicitFlag_ButNotControllerProvenance()
    {
        var aircraft = AfterDispatch(new ReduceToFinalApproachSpeedCommand(), isScenarioScripted: true);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ControllerReduceToFinalApproachSpeed_SetsBothFlags()
    {
        var aircraft = AfterDispatch(new ReduceToFinalApproachSpeedCommand(), isScenarioScripted: false);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.True(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ScriptedMach_SetsExplicitFlag_ButNotControllerProvenance()
    {
        var aircraft = AfterDispatch(new MachCommand(0.78), isScenarioScripted: true);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ControllerMach_SetsBothFlags()
    {
        var aircraft = AfterDispatch(new MachCommand(0.78), isScenarioScripted: false);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.True(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ScriptedForceSpeed_SetsExplicitFlag_ButNotControllerProvenance()
    {
        var aircraft = AfterDispatch(new ForceSpeedCommand(180), isScenarioScripted: true);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ControllerForceSpeed_SetsBothFlags()
    {
        var aircraft = AfterDispatch(new ForceSpeedCommand(180), isScenarioScripted: false);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.True(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    // ---- Commands that give it back ---------------------------------------------------

    [Fact]
    public void ResumeNormalSpeed_ClearsBothFlags()
    {
        var aircraft = WithControllerIssuedSpeed();

        var result = Dispatch(aircraft, new ResumeNormalSpeedCommand(), isScenarioScripted: false);

        Assert.True(result.Success, result.Message);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void ClimbMaintain_ClearsBothFlags()
    {
        var aircraft = WithControllerIssuedSpeed();

        var result = Dispatch(aircraft, new ClimbMaintainCommand(10000, AltitudeAssignmentModifier.None), isScenarioScripted: false);

        Assert.True(result.Success, result.Message);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void DescendMaintain_ClearsBothFlags()
    {
        var aircraft = WithControllerIssuedSpeed();

        var result = Dispatch(aircraft, new DescendMaintainCommand(4000), isScenarioScripted: false);

        Assert.True(result.Success, result.Message);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    [Fact]
    public void VfrAltitudeRestriction_ClearsBothFlags()
    {
        var aircraft = WithControllerIssuedSpeed();

        var result = Dispatch(aircraft, new ClimbMaintainCommand(8000, AltitudeAssignmentModifier.AtOrBelow), isScenarioScripted: false);

        Assert.True(result.Success, result.Message);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }

    // ---- The phase-borrow exception ---------------------------------------------------

    /// <summary>
    /// <c>ManeuverSpeedController</c> — the shared slow-down/resume a 360, a 270, a VFR hold or an S-turn
    /// drives — takes the aircraft's speed for the length of the maneuver and hands it back at the end,
    /// re-setting <see cref="ControlTargets.HasExplicitSpeedCommand"/> as it goes. It must leave the
    /// provenance flag untouched in both directions: a phase is not a controller, so the borrow neither
    /// revokes the instructor's ownership on the way in nor manufactures one on the way out. This is the
    /// exception <see cref="ControlTargets.SpeedCommandIsControllerIssued"/>'s own doc calls out.
    /// </summary>
    [Fact]
    public void ManeuverSpeedBorrow_LeavesControllerProvenanceAlone()
    {
        var aircraft = WithControllerIssuedSpeed();
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        var maneuver = new MakeTurnPhase { Direction = TurnDirection.Right, TargetDegrees = 360 };

        maneuver.OnStart(ctx);
        Assert.True(aircraft.Targets.SpeedCommandIsControllerIssued, "taking speed for a maneuver must not revoke the controller's ownership of it");

        maneuver.OnEnd(ctx, PhaseStatus.Completed);

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand); // the prior assignment is restored (§5-7-4)
        Assert.True(
            aircraft.Targets.SpeedCommandIsControllerIssued,
            "handing speed back at the end of a maneuver must not drop the controller's ownership either"
        );
    }

    /// <summary>
    /// The other direction of the same invariant: a maneuver flown by an aircraft under a <em>scripted</em>
    /// speed hands the speed back without inventing a controller. Resume's no-prior-assignment branch
    /// clears the explicit flag, and the provenance must stay false rather than be mirrored from it.
    /// </summary>
    [Fact]
    public void ManeuverSpeedBorrow_DoesNotManufactureControllerProvenance()
    {
        var aircraft = AfterDispatch(new SpeedCommand(180), isScenarioScripted: true);
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        var maneuver = new MakeTurnPhase { Direction = TurnDirection.Left, TargetDegrees = 360 };

        maneuver.OnStart(ctx);
        maneuver.OnEnd(ctx, PhaseStatus.Completed);

        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);
    }
}
