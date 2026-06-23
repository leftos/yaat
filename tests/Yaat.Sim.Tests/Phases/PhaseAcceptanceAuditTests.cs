using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
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

    /// <summary>
    /// Inside the 5 nm gate a speed-class command (RFAS, SPD, RNS, DSR, Mach) must be
    /// <em>rejected</em> with a pilot "unable" — NOT clear the phase. The aircraft is
    /// committed to its stabilized final approach speed (§5-7-1.b.4); tearing the
    /// approach down for a speed instruction wiped an established ILS final (SWA4587 on
    /// the OAK ILS 30 lost its approach to a stray RFAS).
    /// </summary>
    [Theory]
    [MemberData(nameof(SpeedFamily))]
    public void FinalApproachPhase_SpeedFamily_RejectedInsideFiveNm(CanonicalCommandType cmd)
    {
        var phase = new FinalApproachPhase { DistanceToThresholdNm = 3.0 };
        var acceptance = phase.CanAcceptCommand(cmd);
        Assert.True(acceptance.IsRejected, $"{cmd} inside 5 nm should be rejected, not clear the approach");
        Assert.False(acceptance.ClearsThePhase);
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

    // -----------------------------------------------------------------------------
    // Lateral-maneuver / approach phases: speed and altitude families are additive
    // and must NOT cancel the lateral maneuver. The reported bug was RFAS clearing
    // an R360 (MakeTurnPhase); these phases had drifted speed allow-lists (Speed +
    // Mach but missing RFAS/RNS/DSR).
    // -----------------------------------------------------------------------------

    private static MakeTurnPhase NewMakeTurn() => new() { Direction = TurnDirection.Right, TargetDegrees = 360 };

    private static STurnPhase NewSTurn() => new() { InitialDirection = TurnDirection.Left };

    private static HoldingPatternPhase NewHolding() =>
        new()
        {
            FixName = "FIXXX",
            FixLat = 0,
            FixLon = 0,
            InboundCourse = 90,
            LegLength = 1,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
        };

    private static ProcedureTurnPhase NewProcedureTurn() =>
        new()
        {
            FixName = "FIXXX",
            FixLat = 0,
            FixLon = 0,
            InboundCourseDeg = 90,
            PtOutboundCourseDeg = 270,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 2000,
        };

    private static InterceptCoursePhase NewInterceptCourse() =>
        new()
        {
            FinalApproachCourse = new TrueHeading(280),
            ThresholdLat = 0,
            ThresholdLon = 0,
        };

    private static ApproachNavigationPhase NewApproachNav() => new() { Fixes = [] };

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void MakeTurnPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.Allowed, NewMakeTurn().CanAcceptCommand(cmd));

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.TurnLeft)]
    [InlineData(CanonicalCommandType.DirectTo)]
    public void MakeTurnPhase_LateralCommands_ClearPhase(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.ClearsPhase, NewMakeTurn().CanAcceptCommand(cmd));

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void STurnPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.Allowed, NewSTurn().CanAcceptCommand(cmd));

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.DirectTo)]
    public void STurnPhase_LateralCommands_ClearPhase(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.ClearsPhase, NewSTurn().CanAcceptCommand(cmd));

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void HoldingPatternPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.Allowed, NewHolding().CanAcceptCommand(cmd));

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void ProcedureTurnPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.Allowed, NewProcedureTurn().CanAcceptCommand(cmd));

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void InterceptCoursePhase_AdditiveCommands_Allowed(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.Allowed, NewInterceptCourse().CanAcceptCommand(cmd));

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.DirectTo)]
    public void InterceptCoursePhase_LateralCommands_ClearPhase(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.ClearsPhase, NewInterceptCourse().CanAcceptCommand(cmd));

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void ApproachNavigationPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.Allowed, NewApproachNav().CanAcceptCommand(cmd));

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.DirectTo)]
    public void ApproachNavigationPhase_LateralCommands_ClearPhase(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.ClearsPhase, NewApproachNav().CanAcceptCommand(cmd));

    [Theory]
    [MemberData(nameof(AdditiveAirborneFamily))]
    public void VfrHoldPhase_AdditiveCommands_Allowed(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.Allowed, new VfrHoldPhase().CanAcceptCommand(cmd));

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.DirectTo)]
    public void VfrHoldPhase_LateralCommands_ClearPhase(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.ClearsPhase, new VfrHoldPhase().CanAcceptCommand(cmd));

    // -----------------------------------------------------------------------------
    // Pattern phases: previously allowed Speed/RFAS/RNS/DSR but omitted Mach. The
    // whole speed family is now accepted additively (incl. Mach) via the shared
    // helper. Altitude (CM/DM) is additive on the pattern *legs* but deliberately
    // clears pattern *entry* (PhaseClearWarningTests) — so they're tested apart.
    // -----------------------------------------------------------------------------

    private static PatternEntryPhase NewPatternEntry() =>
        new()
        {
            EntryLat = 0,
            EntryLon = 0,
            PatternAltitude = 1000,
            Kind = PatternEntryKind.Direct,
        };

    public static TheoryData<Phase> PatternPhases() =>
        new() { new BasePhase(), new CrosswindPhase(), new DownwindPhase(), new UpwindPhase(), NewPatternEntry() };

    public static TheoryData<Phase> PatternLegPhases() => new() { new BasePhase(), new CrosswindPhase(), new DownwindPhase(), new UpwindPhase() };

    [Theory]
    [MemberData(nameof(PatternPhases))]
    public void PatternPhase_SpeedFamilyAllowed(Phase phase)
    {
        foreach (
            var cmd in new[]
            {
                CanonicalCommandType.Speed,
                CanonicalCommandType.Mach,
                CanonicalCommandType.ReduceToFinalApproachSpeed,
                CanonicalCommandType.ResumeNormalSpeed,
                CanonicalCommandType.DeleteSpeedRestrictions,
            }
        )
        {
            Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
        }
    }

    [Theory]
    [MemberData(nameof(PatternLegPhases))]
    public void PatternLegPhase_AltitudeFamilyAllowed(Phase phase)
    {
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.DescendMaintain));
    }

    [Theory]
    [InlineData(CanonicalCommandType.ClimbMaintain)]
    [InlineData(CanonicalCommandType.DescendMaintain)]
    public void PatternEntryPhase_AltitudeCommands_ClearPhase(CanonicalCommandType cmd) =>
        Assert.Equal(CommandAcceptance.ClearsPhase, NewPatternEntry().CanAcceptCommand(cmd));
}
