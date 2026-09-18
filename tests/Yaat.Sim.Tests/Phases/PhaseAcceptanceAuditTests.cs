using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
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
        [
            CanonicalCommandType.Speed,
            CanonicalCommandType.Mach,
            CanonicalCommandType.ReduceToFinalApproachSpeed,
            CanonicalCommandType.ResumeNormalSpeed,
            CanonicalCommandType.DeleteSpeedRestrictions,
        ];

    public static TheoryData<CanonicalCommandType> AltitudeFamily => [CanonicalCommandType.ClimbMaintain, CanonicalCommandType.DescendMaintain];

    public static TheoryData<CanonicalCommandType> AdditiveAirborneFamily =>
        [
            CanonicalCommandType.ClimbMaintain,
            CanonicalCommandType.DescendMaintain,
            CanonicalCommandType.Speed,
            CanonicalCommandType.Mach,
            CanonicalCommandType.ReduceToFinalApproachSpeed,
            CanonicalCommandType.ResumeNormalSpeed,
            CanonicalCommandType.DeleteSpeedRestrictions,
        ];

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
        CommandAcceptance acceptance = phase.CanAcceptCommand(CanonicalCommandType.Speed);
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
        CommandAcceptance acceptance = phase.CanAcceptCommand(cmd);
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
            AssignedInterceptHeading = null,
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
        [new BasePhase(), new CrosswindPhase(), new DownwindPhase(), new UpwindPhase(), NewPatternEntry()];

    public static TheoryData<Phase> PatternLegPhases() => [new BasePhase(), new CrosswindPhase(), new DownwindPhase(), new UpwindPhase()];

    [Theory]
    [MemberData(nameof(PatternPhases))]
    public void PatternPhase_SpeedFamilyAllowed(Phase phase)
    {
        foreach (
            CanonicalCommandType cmd in new[]
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

    /// <summary>
    /// The entry maneuvers that sit BETWEEN pattern legs. They were omitted from the leg lists above, which is how
    /// they came to be the only pattern phases accepting no additive adjustment at all — they carry the rest of the
    /// circuit in their phase list, so a plain speed adjustment tore down Downwind → Base → FinalApproach → Landing.
    ///
    /// They follow <see cref="PatternEntryPhase"/>'s split rather than the legs': speed is additive, altitude is not.
    /// A climb or descend during an entry maneuver usually means the aircraft is no longer being sequenced into this
    /// pattern, and the RPO has to be told the entry was cancelled.
    /// </summary>
    public static TheoryData<Phase> PatternInterLegPhases() =>
        [new MidfieldCrossingPhase(), new TeardropReentryPhase { Waypoints = new PatternWaypoints() }];

    [Theory]
    [MemberData(nameof(PatternInterLegPhases))]
    public void PatternInterLegPhase_SpeedFamilyAllowed(Phase phase)
    {
        foreach (
            CanonicalCommandType cmd in new[]
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
    [MemberData(nameof(PatternInterLegPhases))]
    public void PatternInterLegPhase_AltitudeCommands_ClearPhase(Phase phase)
    {
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.DescendMaintain));
    }

    /// <summary>
    /// A tug move (<c>PUSHM</c>) is offered from both stopped ground states — parked on a stand and holding in
    /// the alley after a pushback — because both are poses a ramp controller repositions an aircraft from.
    /// It is <see cref="CommandAcceptance.Allowed"/> rather than <c>ClearsPhase</c> on purpose: the handler
    /// reads which of the two phases is running to decide whether the first leg comes off a stand (push only)
    /// or may tow the aircraft forward, and it clears the phase itself once the plan holds — so a refused move
    /// leaves the aircraft where it was.
    /// </summary>
    [Theory]
    [MemberData(nameof(TugMoveStartPhases))]
    public void GroundStopPhases_AcceptPushbackMultiWithoutClearing(Phase phase)
    {
        CommandAcceptance acceptance = phase.CanAcceptCommand(CanonicalCommandType.PushbackMulti);

        Assert.Equal(CommandAcceptance.Allowed, acceptance);
        Assert.False(acceptance.ClearsThePhase, $"{phase.Name} must not be cleared before the tug move is planned");
    }

    public static TheoryData<Phase> TugMoveStartPhases => [new AtParkingPhase(), new HoldingAfterPushbackPhase()];

    private static HoldingShortPhase HoldingShortAt(string targetName, HoldShortReason reason) =>
        new(
            new HoldShortPoint
            {
                NodeId = 1,
                Reason = reason,
                TargetName = targetName,
            }
        );

    /// <summary>
    /// FOLLOWG applies at a bar that protects no runway. An aircraft staged at an explicit taxiway or spot
    /// hold-short is the normal starting point for handing it to the aircraft ahead when merging two taxi
    /// flows onto one runway, and COMMANDS.md documents FOLLOWG as working from any holding state.
    /// </summary>
    [Theory]
    [InlineData("F1", "a taxiway bar")]
    [InlineData("$17", "a spot bar")]
    public void HoldingShortPhase_NonRunwayBar_AcceptsFollowGround(string targetName, string because)
    {
        HoldingShortPhase phase = HoldingShortAt(targetName, HoldShortReason.ExplicitHoldShort);

        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FollowGround));
        Assert.False(phase.CanAcceptCommand(CanonicalCommandType.FollowGround).IsRejected, $"FOLLOWG must apply at {because}");

        // The catch-all names what a taxiway/spot bar actually offers — RES and FOLLOWG both apply here.
        CommandAcceptance refusal = phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain);
        Assert.Contains("RES/FOLLOWG/CROSS/HSC", refusal.Reason);
    }

    /// <summary>
    /// FOLLOWG is refused at any bar protecting a runway — an explicit <c>HS 1R</c>, a crossing, or the
    /// departure bar. Following a leader is not a crossing clearance: the aircraft would trail its leader
    /// onto the runway with nothing having authorised it.
    /// </summary>
    [Theory]
    [InlineData("1R", HoldShortReason.ExplicitHoldShort)]
    [InlineData("01R/19L", HoldShortReason.RunwayCrossing)]
    [InlineData("28L", HoldShortReason.DestinationRunway)]
    public void HoldingShortPhase_RunwayBar_RejectsFollowGround(string targetName, HoldShortReason reason)
    {
        HoldingShortPhase phase = HoldingShortAt(targetName, reason);

        CommandAcceptance acceptance = phase.CanAcceptCommand(CanonicalCommandType.FollowGround);

        Assert.True(acceptance.IsRejected, $"FOLLOWG must not apply while holding short of runway {targetName}");
        Assert.Contains("CROSS", acceptance.Reason);

        // The refusal points at the way across that does exist, rather than leaving the controller guessing.
        Assert.Contains("issue CROSS <rwy>; FOLLOWG <leader>", acceptance.Reason);
    }
}
