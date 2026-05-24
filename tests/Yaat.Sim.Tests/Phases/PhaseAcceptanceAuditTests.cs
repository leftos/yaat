using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for the broader phase-acceptance audit (follow-up to the CTO/CM
/// fix). Altitude- and speed-class commands should not cancel airborne phases
/// whose primary responsibility is heading or following another aircraft —
/// they're additive adjustments to the current target.
/// </summary>
public class PhaseAcceptanceAuditTests
{
    public static TheoryData<CanonicalCommandType> SpeedFamily =>
        new()
        {
            CanonicalCommandType.Speed,
            CanonicalCommandType.Mach,
            CanonicalCommandType.ReduceToFinalApproachSpeed,
            CanonicalCommandType.ResumeNormalSpeed,
            CanonicalCommandType.DeleteSpeedRestrictions,
        };

    public static TheoryData<CanonicalCommandType> AltitudeFamily =>
        new() { CanonicalCommandType.ClimbMaintain, CanonicalCommandType.DescendMaintain };

    public static TheoryData<CanonicalCommandType> AdditiveAirborneFamily =>
        new()
        {
            CanonicalCommandType.ClimbMaintain,
            CanonicalCommandType.DescendMaintain,
            CanonicalCommandType.Speed,
            CanonicalCommandType.Mach,
            CanonicalCommandType.ReduceToFinalApproachSpeed,
            CanonicalCommandType.ResumeNormalSpeed,
            CanonicalCommandType.DeleteSpeedRestrictions,
        };

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void InitialClimbPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd)
    {
        var phase = new InitialClimbPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    /// <summary>
    /// InitialClimb accepts lateral instructions (heading + direct-to families)
    /// additively as well. The CTO-assigned climb-to altitude continues to drive
    /// the phase; the controller's lateral instruction takes effect immediately
    /// just as a real pilot would expect during the initial climb after takeoff.
    /// </summary>
    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.TurnLeft)]
    [InlineData(CanonicalCommandType.TurnRight)]
    [InlineData(CanonicalCommandType.RelativeLeft)]
    [InlineData(CanonicalCommandType.RelativeRight)]
    [InlineData(CanonicalCommandType.FlyPresentHeading)]
    [InlineData(CanonicalCommandType.ForceHeading)]
    [InlineData(CanonicalCommandType.DirectTo)]
    [InlineData(CanonicalCommandType.AppendDirectTo)]
    [InlineData(CanonicalCommandType.TurnLeftDirectTo)]
    [InlineData(CanonicalCommandType.TurnRightDirectTo)]
    [InlineData(CanonicalCommandType.ForceDirectTo)]
    [InlineData(CanonicalCommandType.AppendForceDirectTo)]
    public void InitialClimbPhase_LateralCommands_Allowed(CanonicalCommandType cmd)
    {
        var phase = new InitialClimbPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    /// <summary>
    /// TakeoffPhase has two acceptance regimes (ground roll vs airborne). The
    /// airborne path must accept speed-class commands without clearing — same
    /// rationale as InitialClimbPhase. Use FromSnapshot to construct the phase
    /// directly in the airborne state since <c>_airborne</c> is private.
    /// </summary>
    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void TakeoffPhase_Airborne_AdditiveCommands_Allowed(CanonicalCommandType cmd)
    {
        var dto = new TakeoffPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 30,
            Airborne = true,
            FieldElevation = 0,
            RunwayHeadingDeg = 280,
            ThresholdLat = 0,
            ThresholdLon = 0,
        };
        var phase = TakeoffPhase.FromSnapshot(dto);
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Fact]
    public void TakeoffPhase_GroundRoll_SpeedCommandsRejected()
    {
        var dto = new TakeoffPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 5,
            Airborne = false,
            FieldElevation = 0,
            RunwayHeadingDeg = 280,
            ThresholdLat = 0,
            ThresholdLon = 0,
        };
        var phase = TakeoffPhase.FromSnapshot(dto);
        var acceptance = phase.CanAcceptCommand(CanonicalCommandType.Speed);
        Assert.True(acceptance.IsRejected, "Speed should be rejected during takeoff roll");
    }

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void GoAroundPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd)
    {
        var phase = new GoAroundPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void VfrFollowPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd)
    {
        var phase = new VfrFollowPhase("LEAD123");
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.TurnRight)]
    [InlineData(CanonicalCommandType.DirectTo)]
    public void VfrFollowPhase_HeadingNavCommands_ClearPhase(CanonicalCommandType cmd)
    {
        var phase = new VfrFollowPhase("LEAD123");
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(cmd));
    }

    [Fact]
    public void VfrFollowPhase_FollowStillAllowed()
    {
        var phase = new VfrFollowPhase("LEAD123");
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Follow));
    }

    /// <summary>
    /// FinalApproachPhase accepts speed-class commands additively only when the
    /// aircraft is outside 5 nm from the threshold. Inside that gate the
    /// aircraft is committed to the final approach speed and the controller
    /// should send GA instead of a speed change.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpeedFamily))]
    public void FinalApproachPhase_SpeedFamily_AllowedOutsideFiveNm(CanonicalCommandType cmd)
    {
        var phase = new FinalApproachPhase { DistanceToThresholdNm = 8.0 };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [MemberData(nameof(SpeedFamily))]
    public void FinalApproachPhase_SpeedFamily_ClearsPhaseInsideFiveNm(CanonicalCommandType cmd)
    {
        var phase = new FinalApproachPhase { DistanceToThresholdNm = 3.0 };
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(cmd));
    }

    [Fact]
    public void FinalApproachPhase_ClearedToLand_AlwaysAllowed()
    {
        var inside = new FinalApproachPhase { DistanceToThresholdNm = 1.0 };
        var outside = new FinalApproachPhase { DistanceToThresholdNm = 10.0 };

        Assert.Equal(CommandAcceptance.Allowed, inside.CanAcceptCommand(CanonicalCommandType.ClearedToLand));
        Assert.Equal(CommandAcceptance.Allowed, outside.CanAcceptCommand(CanonicalCommandType.ClearedToLand));
        Assert.Equal(CommandAcceptance.Allowed, inside.CanAcceptCommand(CanonicalCommandType.GoAround));
    }
}
