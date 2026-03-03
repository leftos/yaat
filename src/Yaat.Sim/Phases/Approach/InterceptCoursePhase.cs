using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// Flies the aircraft on its current heading until it intercepts the
/// final approach course. Completes when the aircraft is aligned with
/// the course and within cross-track tolerance.
/// </summary>
public sealed class InterceptCoursePhase : Phase
{
    private const double CrossTrackThresholdNm = 0.15;
    private const double HeadingAlignmentDeg = 20.0;

    /// <summary>Final approach course heading (true).</summary>
    public required double FinalApproachCourse { get; init; }

    /// <summary>Runway threshold latitude (course target point).</summary>
    public required double ThresholdLat { get; init; }

    /// <summary>Runway threshold longitude (course target point).</summary>
    public required double ThresholdLon { get; init; }

    public override string Name => "InterceptCourse";

    public override void OnStart(PhaseContext ctx)
    {
        // Aircraft continues on its current heading — no target change.
        // Approach speed set by the phase that follows (FinalApproachPhase).
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, ThresholdLat, ThresholdLon, FinalApproachCourse)
        );

        double headingDiff = Math.Abs(FlightPhysics.NormalizeAngle(ctx.Aircraft.Heading - FinalApproachCourse));

        if (crossTrack < CrossTrackThresholdNm && headingDiff < HeadingAlignmentDeg)
        {
            // Established — turn onto the final approach course
            ctx.Targets.TargetHeading = FinalApproachCourse;
            ctx.Targets.PreferredTurnDirection = null;
            ctx.Targets.NavigationRoute.Clear();
            return true;
        }

        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            // Approach-related commands pass through
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            // Speed/altitude adjust targets without leaving the approach
            CanonicalCommandType.Speed => CommandAcceptance.Allowed,
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            // Everything else (heading, direct-to, etc.) takes the aircraft off the approach
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
