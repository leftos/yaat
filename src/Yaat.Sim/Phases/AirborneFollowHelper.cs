using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Phases;

/// <summary>
/// Provides speed and timing adjustments for airborne aircraft following
/// another aircraft (visual separation). Pattern and approach phases call
/// these methods each tick when <see cref="AircraftState.Approach.FollowingCallsign"/>
/// is set.
/// </summary>
public static class AirborneFollowHelper
{
    private static readonly ILogger Log = SimLog.CreateLogger("AirborneFollowHelper");

    /// <summary>Desired following distance when leader is a piston/helicopter (nm).</summary>
    private const double DesiredDistanceSmallNm = 1.0;

    /// <summary>Desired following distance when leader is a turboprop (nm).</summary>
    private const double DesiredDistanceMediumNm = 1.5;

    /// <summary>Desired following distance when leader is a jet (nm).</summary>
    private const double DesiredDistanceLargeNm = 2.0;

    // Free-flight spacing is wider than pattern-tight spacing: pilots maintaining
    // visual separation outside the pattern have less context (no runway cues,
    // higher closure risk at distance), so give them more room. On pattern join
    // the tighter pattern constants above take over.
    private const double FreeFlightDistanceSmallNm = 1.5;
    private const double FreeFlightDistanceMediumNm = 2.0;
    private const double FreeFlightDistanceLargeNm = 2.5;

    /// <summary>Speed correction gain: kts per nm of distance error.</summary>
    private const double SpeedGainPerNm = 25.0;

    /// <summary>
    /// Default ceiling above normal speed (kts). Used on downwind, base, pattern
    /// entry, and free-flight pursuit, where the follower has time and altitude
    /// to reshape the approach if it closes at a small speed advantage.
    /// </summary>
    public const double MaxSpeedAdjustKts = 20.0;

    /// <summary>
    /// Tighter ceiling above normal speed for final approach (kts). Once the
    /// follower is established on final it's already converging toward Vref,
    /// and any excess speed trips the unstabilized-go-around gate
    /// (IAS &gt; 1.3·Vref). A 10-kt margin keeps the follower under that gate
    /// while still allowing a modest chase. Beyond this, <see cref="FinalApproachPhase"/>
    /// stops adjusting altogether inside its stabilization window.
    /// </summary>
    public const double MaxSpeedAdjustFinalKts = 10.0;

    /// <summary>
    /// Fraction of desired distance below which downwind should be extended
    /// to avoid turning base too close to the leader.
    /// </summary>
    private const double ExtendDownwindThreshold = 0.6;

    /// <summary>
    /// Returns an adjusted target speed based on distance to the followed aircraft,
    /// or null if no follow is active (or the target has disappeared).
    /// The <paramref name="normalSpeed"/> MUST be the phase's baseline speed
    /// (e.g. <see cref="AircraftPerformance.DownwindSpeed"/>), not the previous
    /// tick's target — feeding the previous tick's output back in compounds the
    /// +<paramref name="maxSpeedAdjustKts"/> clamp over ticks and lets IAS escape
    /// the stabilized-approach gate.
    /// </summary>
    /// <param name="ctx">Current phase context (must have AircraftLookup set).</param>
    /// <param name="normalSpeed">The phase's baseline target speed.</param>
    /// <param name="minSpeed">Absolute floor — never returns below this (e.g. Vref on final).</param>
    /// <param name="maxSpeedAdjustKts">
    /// Symmetric clamp on the per-tick speed correction. Use
    /// <see cref="MaxSpeedAdjustKts"/> for early-pattern and free-flight callers,
    /// <see cref="MaxSpeedAdjustFinalKts"/> for final-approach callers so the
    /// follower can't blow through the unstabilized-GA threshold while chasing.
    /// </param>
    public static double? GetAdjustedSpeed(PhaseContext ctx, double normalSpeed, double minSpeed, double maxSpeedAdjustKts)
    {
        string? targetCallsign = ctx.Aircraft.Approach.FollowingCallsign;
        if (targetCallsign is null)
        {
            return null;
        }

        var target = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (target is null)
        {
            // Leader disappeared — clear follow state, continue with normal speed
            Log.LogDebug("[Follow] {Callsign}: target {Target} no longer found, clearing follow", ctx.Aircraft.Callsign, targetCallsign);
            ctx.Aircraft.Approach.FollowingCallsign = null;
            return null;
        }

        return ComputeAdjustedSpeed(ctx.Aircraft, target, normalSpeed, minSpeed, maxSpeedAdjustKts, Log);
    }

    /// <summary>
    /// Variant of <see cref="GetAdjustedSpeed"/> that uses the wider
    /// free-flight desired spacing instead of pattern-tight spacing. Used by
    /// phases that are navigating toward the pattern but not yet established
    /// on a downwind/base/final leg — real pilots don't tighten to pattern
    /// spacing until they're actually flying the rhythm of the pattern.
    /// </summary>
    public static double? GetAdjustedSpeedFreeFlight(PhaseContext ctx, double normalSpeed, double minSpeed)
    {
        string? targetCallsign = ctx.Aircraft.Approach.FollowingCallsign;
        if (targetCallsign is null)
        {
            return null;
        }

        var target = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (target is null)
        {
            Log.LogDebug("[Follow] {Callsign}: target {Target} no longer found, clearing follow", ctx.Aircraft.Callsign, targetCallsign);
            ctx.Aircraft.Approach.FollowingCallsign = null;
            return null;
        }

        var leaderCategory = AircraftCategorization.Categorize(target.AircraftType);
        double desired = FreeFlightDistanceForLeader(leaderCategory);
        return ComputeAdjustedSpeedWithDesired(ctx.Aircraft, target, normalSpeed, minSpeed, desired, MaxSpeedAdjustKts, Log);
    }

    /// <summary>
    /// Phase-context-free variant used by <see cref="VfrFollowPhase"/> during free
    /// pursuit (lead not in a pattern). Treats the lead's ground speed as the
    /// "normal" target so the follower tracks the lead's speed with distance-based
    /// correction, using the wider free-flight desired distances. Does not read
    /// or clear <see cref="AircraftState.Approach.FollowingCallsign"/> — the caller
    /// (VfrFollowPhase) owns that lifecycle.
    /// </summary>
    /// <param name="follower">Follower aircraft.</param>
    /// <param name="lead">Lead aircraft.</param>
    /// <param name="minSpeed">Absolute floor — never returns below this.</param>
    /// <param name="logger">Logger for warnings when separation cannot be maintained.</param>
    public static double AdjustedFreeFlightSpeed(AircraftState follower, AircraftState lead, double minSpeed, ILogger logger)
    {
        double normalSpeed = Math.Max(lead.IndicatedAirspeed, minSpeed);
        var leaderCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double desired = FreeFlightDistanceForLeader(leaderCategory);
        var result = ComputeAdjustedSpeedWithDesired(follower, lead, normalSpeed, minSpeed, desired, MaxSpeedAdjustKts, logger);
        return result ?? normalSpeed;
    }

    /// <summary>
    /// Core distance/error/clamp math shared by both the pattern-phase path and
    /// the free-flight path. Returns null only when separation cannot be maintained
    /// (follower too close at min speed) — in that case the caller decides how to
    /// cancel follow.
    /// </summary>
    private static double? ComputeAdjustedSpeed(
        AircraftState follower,
        AircraftState lead,
        double normalSpeed,
        double minSpeed,
        double maxSpeedAdjustKts,
        ILogger logger
    )
    {
        var leaderCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double desired = DesiredDistanceForLeader(leaderCategory);
        return ComputeAdjustedSpeedWithDesired(follower, lead, normalSpeed, minSpeed, desired, maxSpeedAdjustKts, logger);
    }

    private static double? ComputeAdjustedSpeedWithDesired(
        AircraftState follower,
        AircraftState lead,
        double normalSpeed,
        double minSpeed,
        double desired,
        double maxSpeedAdjustKts,
        ILogger logger
    )
    {
        double distance = GeoMath.DistanceNm(follower.Position, lead.Position);
        double error = distance - desired;
        double speedAdjust = Math.Clamp(error * SpeedGainPerNm, -maxSpeedAdjustKts, maxSpeedAdjustKts);
        double adjusted = normalSpeed + speedAdjust;
        double clamped = Math.Clamp(adjusted, minSpeed, normalSpeed + maxSpeedAdjustKts);

        // Speed clamped to minimum AND too close — the follower can't maintain
        // separation. Cancel follow and warn once so the controller can intervene.
        if ((adjusted < minSpeed) && (distance < desired * 0.5))
        {
            follower.Approach.FollowingCallsign = null;
            follower.PendingWarnings.Add($"{follower.Callsign} unable to maintain separation from {lead.Callsign}, cancelling follow");
            logger.LogWarning(
                "[Follow] {Callsign}: cancelled follow on {Target}, at min speed with dist={Dist:F2}nm (desired={Desired:F1}nm)",
                follower.Callsign,
                lead.Callsign,
                distance,
                desired
            );
            return null;
        }

        return clamped;
    }

    /// <summary>
    /// Returns true if the follower is too close to the leader and should
    /// extend downwind rather than turning base.
    /// </summary>
    public static bool ShouldExtendDownwind(PhaseContext ctx)
    {
        string? targetCallsign = ctx.Aircraft.Approach.FollowingCallsign;
        if (targetCallsign is null)
        {
            return false;
        }

        var target = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (target is null)
        {
            return false;
        }

        double distance = GeoMath.DistanceNm(ctx.Aircraft.Position, target.Position);

        var leaderCategory = AircraftCategorization.Categorize(target.AircraftType);
        double desired = DesiredDistanceForLeader(leaderCategory);

        bool shouldExtend = distance < (desired * ExtendDownwindThreshold);
        if (shouldExtend)
        {
            Log.LogDebug(
                "[Follow] {Callsign}: extending downwind, dist={Dist:F2}nm to {Target} (desired={Desired:F1}nm)",
                ctx.Aircraft.Callsign,
                distance,
                targetCallsign,
                desired
            );
        }

        return shouldExtend;
    }

    /// <summary>
    /// Returns the desired following distance (pattern-tight) based on the leader's
    /// aircraft category. Used by <see cref="DownwindPhase"/> / <see cref="BasePhase"/>
    /// / <see cref="FinalApproachPhase"/>. Larger/faster leaders require more spacing.
    /// </summary>
    public static double DesiredDistanceForLeader(AircraftCategory leaderCategory)
    {
        return leaderCategory switch
        {
            AircraftCategory.Jet => DesiredDistanceLargeNm,
            AircraftCategory.Turboprop => DesiredDistanceMediumNm,
            AircraftCategory.Piston => DesiredDistanceSmallNm,
            AircraftCategory.Helicopter => DesiredDistanceSmallNm,
            _ => DesiredDistanceMediumNm,
        };
    }

    /// <summary>
    /// Wider free-flight desired distance (used by <see cref="VfrFollowPhase"/>
    /// outside the pattern) — pilots maintaining visual separation without
    /// pattern cues want more margin than the pattern-tight values.
    /// </summary>
    public static double FreeFlightDistanceForLeader(AircraftCategory leaderCategory)
    {
        return leaderCategory switch
        {
            AircraftCategory.Jet => FreeFlightDistanceLargeNm,
            AircraftCategory.Turboprop => FreeFlightDistanceMediumNm,
            AircraftCategory.Piston => FreeFlightDistanceSmallNm,
            AircraftCategory.Helicopter => FreeFlightDistanceSmallNm,
            _ => FreeFlightDistanceMediumNm,
        };
    }
}
