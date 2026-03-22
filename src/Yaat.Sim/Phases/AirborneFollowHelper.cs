using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Phases;

/// <summary>
/// Provides speed and timing adjustments for airborne aircraft following
/// another aircraft (visual separation). Pattern and approach phases call
/// these methods each tick when <see cref="AircraftState.FollowingCallsign"/>
/// is set.
/// </summary>
public static class AirborneFollowHelper
{
    /// <summary>Desired following distance when leader is a piston/helicopter (nm).</summary>
    private const double DesiredDistanceSmallNm = 1.0;

    /// <summary>Desired following distance when leader is a turboprop (nm).</summary>
    private const double DesiredDistanceMediumNm = 1.5;

    /// <summary>Desired following distance when leader is a jet (nm).</summary>
    private const double DesiredDistanceLargeNm = 2.0;

    /// <summary>Speed correction gain: kts per nm of distance error.</summary>
    private const double SpeedGainPerNm = 25.0;

    /// <summary>Maximum speed adjustment above or below normal phase speed (kts).</summary>
    private const double MaxSpeedAdjustKts = 20.0;

    /// <summary>
    /// Fraction of desired distance below which downwind should be extended
    /// to avoid turning base too close to the leader.
    /// </summary>
    private const double ExtendDownwindThreshold = 0.6;

    /// <summary>
    /// Returns an adjusted target speed based on distance to the followed aircraft,
    /// or null if no follow is active (or the target has disappeared).
    /// </summary>
    /// <param name="ctx">Current phase context (must have AircraftLookup set).</param>
    /// <param name="normalSpeed">The speed this phase would normally target.</param>
    /// <param name="minSpeed">Absolute floor — never returns below this (e.g. Vref on final).</param>
    public static double? GetAdjustedSpeed(PhaseContext ctx, double normalSpeed, double minSpeed)
    {
        string? targetCallsign = ctx.Aircraft.FollowingCallsign;
        if (targetCallsign is null)
        {
            return null;
        }

        var target = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (target is null)
        {
            // Leader disappeared — clear follow state, continue with normal speed
            ctx.Logger.LogDebug("[Follow] {Callsign}: target {Target} no longer found, clearing follow", ctx.Aircraft.Callsign, targetCallsign);
            ctx.Aircraft.FollowingCallsign = null;
            return null;
        }

        double distance = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, target.Latitude, target.Longitude);

        var leaderCategory = AircraftCategorization.Categorize(target.AircraftType);
        double desired = DesiredDistanceForLeader(leaderCategory);
        double error = distance - desired;
        double speedAdjust = Math.Clamp(error * SpeedGainPerNm, -MaxSpeedAdjustKts, MaxSpeedAdjustKts);
        double adjusted = normalSpeed + speedAdjust;
        double clamped = Math.Clamp(adjusted, minSpeed, normalSpeed + MaxSpeedAdjustKts);

        // Speed clamped to minimum AND too close — the follower can't maintain
        // separation. Cancel follow and warn once so the controller can intervene.
        if ((adjusted < minSpeed) && (distance < desired * 0.5))
        {
            ctx.Aircraft.FollowingCallsign = null;
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} unable to maintain separation from {targetCallsign}, cancelling follow");
            ctx.Logger.LogWarning(
                "[Follow] {Callsign}: cancelled follow on {Target}, at min speed with dist={Dist:F2}nm (desired={Desired:F1}nm)",
                ctx.Aircraft.Callsign,
                targetCallsign,
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
        string? targetCallsign = ctx.Aircraft.FollowingCallsign;
        if (targetCallsign is null)
        {
            return false;
        }

        var target = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (target is null)
        {
            return false;
        }

        double distance = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, target.Latitude, target.Longitude);

        var leaderCategory = AircraftCategorization.Categorize(target.AircraftType);
        double desired = DesiredDistanceForLeader(leaderCategory);

        bool shouldExtend = distance < (desired * ExtendDownwindThreshold);
        if (shouldExtend)
        {
            ctx.Logger.LogDebug(
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
    /// Returns the desired following distance based on the leader's aircraft category.
    /// Larger/faster leaders require more spacing.
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
}
