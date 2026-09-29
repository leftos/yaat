using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>What a follower on base does about its spacing behind the lead (<see cref="BaseFollowSpacing.Evaluate"/>).</summary>
internal enum BaseFollowSpacingAction
{
    /// <summary>The spacing is enough, or there is nothing to judge: fly the base; the speed spacing handles the rest.</summary>
    Keep,

    /// <summary>
    /// Behind the lead but a little too close: fly <see cref="BaseFollowSpacing.WidenOffBaseDeg"/> off the base heading, away
    /// from the field.
    /// </summary>
    Widen,

    /// <summary>
    /// Ahead of or level with the lead, or no widen can build the spacing: break off to pursuit, which turns out to the downwind
    /// heading behind the lead (<see cref="VfrFollowPhase.RequestTurnOut"/>). Never for a structural overtake, which the
    /// structural go-around keeps.
    /// </summary>
    BreakOff,

    /// <summary>
    /// A break-off with the follower within <see cref="BaseFollowSpacing.LateBreakOffMarginNm"/> of its final-turn point and the
    /// lead not yet abeam: no room is left to turn out, so the follower goes around.
    /// </summary>
    GoAround,
}

/// <summary>The action, and for <see cref="BaseFollowSpacingAction.Widen"/> the heading to fly (default otherwise).</summary>
internal readonly record struct BaseFollowSpacingDecision(BaseFollowSpacingAction Action, TrueHeading WidenHeading);

/// <summary>
/// Spacing for a follower already on its base, behind a lead on final or on base ahead of it to the same runway, checked
/// every tick by <see cref="BasePhase"/>. The follower's rollout on final is projected against where the lead will be at
/// that moment:
/// <list type="bullet">
/// <item><description>the follower flies its remaining base to the final-turn point and the quarter-circle turn at its
/// ground speed (<see cref="BasePhase.TurnRadiusNm"/>), rolling out one turn radius nearer the threshold than it is
/// now;</description></item>
/// <item><description>the lead flies its remaining path to the threshold
/// (<see cref="AirborneFollowHelper.LeadRemainingPathNm"/>) at its projected speed
/// (<see cref="AirborneFollowHelper.ProjectedLeadSpeedKts"/>);</description></item>
/// <item><description>the spacing gap is how far the lead is then ahead along the final; the runway-occupancy check asks that
/// the follower cross the threshold no sooner than the lead's occupancy allowance
/// (<see cref="AirborneFollowHelper.RunwayClearanceSeconds"/>) after the lead does;</description></item>
/// <item><description>the spacing required is the pattern spacing (<see cref="AirborneFollowHelper.PatternSpacingNm"/>).</description></item>
/// </list>
/// The base is kept while the spacing gap is within <see cref="KeepMarginNm"/> of the requirement and the occupancy check
/// holds. A lead on the final is on it by geometry (<see cref="AirborneFollowHelper.IsOnFinalByGeometry"/>), whatever phase
/// flies it; a lead still airborne in its landing counts for the occupancy check only. Otherwise a follower still behind the
/// lead widens, when the widen can build the rest (<see cref="WidenGapGainNm"/>) without carrying it toward a parallel's
/// final; every other case breaks off, or goes around when the break-off comes too late to turn out. A widen runs until the
/// gap is met or the follower reaches the widen floor, and breaks off as soon as it can no longer build the gap.
/// </summary>
internal static class BaseFollowSpacing
{
    private static readonly ILogger Log = SimLog.CreateLogger("BaseFollowSpacing");

    /// <summary>How far (nm) short of the required spacing the projected spacing gap may be and the base still be kept.</summary>
    public const double KeepMarginNm = 0.3;

    /// <summary>How far (deg) off the base heading, away from the field, a widening follower flies.</summary>
    public const double WidenOffBaseDeg = 30.0;

    /// <summary>The widen floor, in turn radii from the extended centerline: a widen never carries the follower closer.</summary>
    public const double WidenFloorTurnRadii = 1.5;

    /// <summary>
    /// Closest (nm) to its final-turn point a follower may be and still turn out behind a lead not yet abeam; closer, a
    /// break-off goes around.
    /// </summary>
    public const double LateBreakOffMarginNm = 0.4;

    /// <summary>
    /// Judges a follower's spacing on its base (<paramref name="waypoints"/>). <paramref name="widening"/> says a widen is
    /// in progress: it then continues until the gap reaches the required spacing or the follower reaches the widen floor,
    /// and ends with <see cref="BaseFollowSpacingAction.Keep"/>, unless it can no longer build the gap.
    /// </summary>
    internal static BaseFollowSpacingDecision Evaluate(PhaseContext ctx, PatternWaypoints waypoints, bool widening)
    {
        if ((ctx.Aircraft.Phases?.AssignedRunway is not { } runway) || (LeadInScope(ctx, runway, waypoints) is not { } scope))
        {
            return new BaseFollowSpacingDecision(BaseFollowSpacingAction.Keep, default);
        }

        Projection projection = Project(ctx, scope, new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon), waypoints.FinalHeading);
        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace(
                "[BaseSpacing] {Callsign}: behind {Lead} gap={Gap:F2} runwayGap={Rwy:F2} required={Req:F2} xt={Xt:F2} floor={Floor:F2}",
                ctx.Aircraft.Callsign,
                scope.Lead.Callsign,
                projection.GapNm,
                projection.RunwayGapNm,
                projection.RequiredNm,
                projection.CrossTrackNm,
                projection.FloorNm
            );
        }

        return widening ? ContinueWiden(ctx, scope.Lead, runway, waypoints, projection) : Decide(ctx, scope.Lead, runway, waypoints, projection);
    }

    /// <summary>
    /// The gap (nm) a follower turning base now from where it is would have behind <paramref name="lead"/> at its rollout on
    /// <paramref name="runway"/>'s final, by the base projection (the spacing gap, or less when the runway-occupancy check
    /// fails): what a turn-out's exit to base is judged by, so the base it installs keeps.
    /// </summary>
    internal static double ProjectedBaseGapNm(PhaseContext ctx, AircraftState lead, RunwayInfo runway)
    {
        var scope = new LeadScope(lead, AirborneFollowHelper.LeadRemainingPathNm(lead, runway), lead.Phases?.CurrentPhase is LandingPhase);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        return Project(ctx, scope, threshold, runway.TrueHeading).EffectiveGapNm;
    }

    /// <summary>
    /// The gap (nm, in rollout-gap terms) a widen adds: flying <see cref="WidenOffBaseDeg"/> off the base from
    /// <paramref name="crossTrackNm"/> off the extended centerline down to <paramref name="floorNm"/>, the final lengthens by
    /// (xt − floor) × tan 30°, which the follower rolls out that much farther back, and the leg itself by
    /// (xt − floor) × (1/cos 30° − 1), which delays the follower by its length at <paramref name="followerSpeedKts"/>, time
    /// the lead gains at <paramref name="leadSpeedKts"/>. Zero at or inside the floor.
    /// </summary>
    internal static double WidenGapGainNm(double crossTrackNm, double floorNm, double followerSpeedKts, double leadSpeedKts)
    {
        double closingNm = crossTrackNm - floorNm;
        if (closingNm <= 0.0)
        {
            return 0.0;
        }

        double angleRad = WidenOffBaseDeg * Math.PI / 180.0;
        double longerFinalNm = closingNm * Math.Tan(angleRad);
        double longerLegNm = closingNm * ((1.0 / Math.Cos(angleRad)) - 1.0);
        return longerFinalNm + (longerLegNm * leadSpeedKts / followerSpeedKts);
    }

    /// <summary>
    /// True when a parallel runway's extended centerline lies across a widen's path toward <paramref name="runway"/>'s
    /// centerline from <paramref name="crossTrackNm"/> down to <paramref name="floorNm"/>, or within
    /// <see cref="AirborneFollowHelper.TrailParallelFinalMarginNm"/> beyond it, short of the runway's own centerline: a
    /// parallel on the far side of the follower's own final is never approached by a widen that stops at the floor.
    /// </summary>
    internal static bool WidenMeetsParallelFinal(LatLon position, RunwayInfo runway, double crossTrackNm, double floorNm)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        int towardCenterline = GeoMath.SignedCrossTrackDistanceNm(position, threshold, runway.TrueHeading) >= 0.0 ? -1 : 1;
        double reachNm = Math.Min((crossTrackNm - floorNm) + AirborneFollowHelper.TrailParallelFinalMarginNm, crossTrackNm);
        return VfrFollowPhase.ExcursionMeetsParallelFinal(position, runway.TrueHeading, towardCenterline, runway, reachNm);
    }

    /// <summary>The followed lead, its remaining path to the threshold, and whether it counts for the runway-occupancy check only.</summary>
    private readonly record struct LeadScope(AircraftState Lead, double RemainingNm, bool OccupancyOnly);

    /// <summary>The follower's rollout projected against the lead (see <see cref="BaseFollowSpacing"/>).</summary>
    /// <param name="GapNm">How far the lead is ahead along the final at the follower's rollout (≤ 0: ahead or level).</param>
    /// <param name="RunwayGapNm">
    /// The required spacing, less the lead's progress over any time the follower would cross the threshold before the lead's
    /// occupancy allowance ran out (more over any time after): at least the requirement when the occupancy check holds.
    /// </param>
    /// <param name="RequiredNm">The pattern spacing behind this lead.</param>
    /// <param name="CrossTrackNm">The follower's distance from the extended centerline.</param>
    /// <param name="FloorNm">The widen floor: <see cref="WidenFloorTurnRadii"/> turn radii from the extended centerline.</param>
    /// <param name="TurnRadiusNm">The follower's final-turn radius.</param>
    /// <param name="AlongNm">The follower's distance out along the final.</param>
    /// <param name="LeadAlongNm">The lead's distance out along the final.</param>
    /// <param name="FollowerSpeedKts">The follower's projected ground speed.</param>
    /// <param name="LeadSpeedKts">The lead's projected speed.</param>
    /// <param name="OccupancyOnly">The lead is in its landing: only the runway-occupancy check applies.</param>
    private readonly record struct Projection(
        double GapNm,
        double RunwayGapNm,
        double RequiredNm,
        double CrossTrackNm,
        double FloorNm,
        double TurnRadiusNm,
        double AlongNm,
        double LeadAlongNm,
        double FollowerSpeedKts,
        double LeadSpeedKts,
        bool OccupancyOnly
    )
    {
        /// <summary>The gap a widen must build to <see cref="RequiredNm"/>: the spacing gap, or less when the occupancy check fails.</summary>
        public double EffectiveGapNm => OccupancyOnly ? RunwayGapNm : Math.Min(GapNm, RunwayGapNm);

        /// <summary>The base is kept: the spacing gap within <see cref="KeepMarginNm"/> of the requirement, and the occupancy check met.</summary>
        public bool Kept => (OccupancyOnly || (GapNm >= RequiredNm - KeepMarginNm)) && (RunwayGapNm >= RequiredNm);

        /// <summary>The lead is nearer the threshold, along the final, than the follower.</summary>
        public bool LeadPassedAbeam => LeadAlongNm < AlongNm;
    }

    /// <summary>
    /// The followed lead when it is airborne and landing <paramref name="runway"/>, and on its final
    /// (<see cref="FinalApproachPhase"/>, or by geometry, <see cref="AirborneFollowHelper.IsOnFinalByGeometry"/>), in its
    /// landing (occupancy only), or on base ahead of the follower (less pattern path left to fly); otherwise null.
    /// </summary>
    private static LeadScope? LeadInScope(PhaseContext ctx, RunwayInfo runway, PatternWaypoints waypoints)
    {
        if ((ctx.Aircraft.Approach.FollowingCallsign is not { } target) || (ctx.AircraftLookup?.Invoke(target) is not { IsOnGround: false } lead))
        {
            return null;
        }

        if ((lead.Phases?.AssignedRunway is not { } leadRunway) || !AirborneFollowHelper.IsSameRunway(leadRunway, runway))
        {
            return null;
        }

        double leadRemainingNm = AirborneFollowHelper.LeadRemainingPathNm(lead, runway);
        if (lead.Phases.CurrentPhase is LandingPhase)
        {
            return new LeadScope(lead, leadRemainingNm, OccupancyOnly: true);
        }

        if ((lead.Phases.CurrentPhase is FinalApproachPhase) || AirborneFollowHelper.IsOnFinalByGeometry(lead, runway))
        {
            return new LeadScope(lead, leadRemainingNm, OccupancyOnly: false);
        }

        bool baseAhead =
            (lead.Phases.CurrentPhase is BasePhase)
            && (AirborneFollowHelper.RemainingPatternPathNm(lead, waypoints) < AirborneFollowHelper.RemainingPatternPathNm(ctx.Aircraft, waypoints));
        return baseAhead ? new LeadScope(lead, leadRemainingNm, OccupancyOnly: false) : null;
    }

    private static Projection Project(PhaseContext ctx, LeadScope scope, LatLon threshold, TrueHeading finalHeading)
    {
        AircraftState follower = ctx.Aircraft;
        TrueHeading outbound = finalHeading.ToReciprocal();
        double crossTrackNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(follower.Position, threshold, finalHeading));
        double alongFinalNm = GeoMath.AlongTrackDistanceNm(follower.Position, threshold, outbound);
        double turnRadiusNm = BasePhase.TurnRadiusNm(follower.GroundSpeed, ctx.Category);
        double followerKts = Math.Max(follower.GroundSpeed, AirborneFollowHelper.MinProjectionSpeedKts);

        // The base to the final-turn point, then the quarter-circle turn, which rolls out one radius nearer the threshold.
        double toRolloutNm = Math.Max(0.0, crossTrackNm - turnRadiusNm) + (Math.PI / 2.0 * turnRadiusNm);
        double rolloutSec = toRolloutNm / followerKts * 3600.0;
        double followerFinalNm = Math.Max(0.0, alongFinalNm - turnRadiusNm);

        AircraftCategory leadCategory = AircraftCategorization.Categorize(scope.Lead.AircraftType);
        double leadKts = AirborneFollowHelper.ProjectedLeadSpeedKts(scope.Lead, leadCategory);
        double gapNm = followerFinalNm - (scope.RemainingNm - (leadKts * rolloutSec / 3600.0));
        double requiredNm = AirborneFollowHelper.PatternSpacingNm(ctx, scope.Lead);

        // Runway occupancy: seconds the follower would cross the threshold after the lead clears the runway, as a gap at
        // the lead's speed on top of the requirement; negative seconds leave it short of the requirement.
        double followerThresholdSec = rolloutSec + (followerFinalNm / followerKts * 3600.0);
        double leadClearSec = (scope.RemainingNm / leadKts * 3600.0) + AirborneFollowHelper.RunwayClearanceSeconds(leadCategory);
        double runwayGapNm = requiredNm + ((followerThresholdSec - leadClearSec) * leadKts / 3600.0);

        double leadAlongNm = GeoMath.AlongTrackDistanceNm(scope.Lead.Position, threshold, outbound);
        return new Projection(
            gapNm,
            runwayGapNm,
            requiredNm,
            crossTrackNm,
            WidenFloorTurnRadii * turnRadiusNm,
            turnRadiusNm,
            alongFinalNm,
            leadAlongNm,
            followerKts,
            leadKts,
            scope.OccupancyOnly
        );
    }

    private static BaseFollowSpacingDecision Decide(
        PhaseContext ctx,
        AircraftState lead,
        RunwayInfo runway,
        PatternWaypoints waypoints,
        Projection projection
    )
    {
        if (projection.Kept)
        {
            return new BaseFollowSpacingDecision(BaseFollowSpacingAction.Keep, default);
        }

        if (BreakOffReason(ctx.Aircraft.Position, runway, projection) is { } reason)
        {
            return BreakOff(ctx, lead, projection, reason, new BaseFollowSpacingDecision(BaseFollowSpacingAction.Keep, default));
        }

        TrueHeading heading = WidenHeading(ctx.Aircraft.Position, runway, waypoints);
        Log.LogDebug(
            "[BaseSpacing] {Callsign}: widening the base to {Hdg:F0} (gap={Gap:F2} runwayGap={Rwy:F2} required={Req:F2} xt={Xt:F2} floor={Floor:F2})",
            ctx.Aircraft.Callsign,
            heading.Degrees,
            projection.GapNm,
            projection.RunwayGapNm,
            projection.RequiredNm,
            projection.CrossTrackNm,
            projection.FloorNm
        );
        return new BaseFollowSpacingDecision(BaseFollowSpacingAction.Widen, heading);
    }

    /// <summary>
    /// A widen under way: done when the gap is met or the follower reaches the floor; otherwise the break-off test runs again,
    /// and the widen breaks off as soon as it can no longer build the gap.
    /// </summary>
    private static BaseFollowSpacingDecision ContinueWiden(
        PhaseContext ctx,
        AircraftState lead,
        RunwayInfo runway,
        PatternWaypoints waypoints,
        Projection projection
    )
    {
        bool gapMet = projection.EffectiveGapNm >= projection.RequiredNm;
        if (gapMet || (projection.CrossTrackNm <= projection.FloorNm))
        {
            Log.LogDebug(
                "[BaseSpacing] {Callsign}: widen done, {Why} (gap={Gap:F2} runwayGap={Rwy:F2} required={Req:F2} xt={Xt:F2} floor={Floor:F2})",
                ctx.Aircraft.Callsign,
                gapMet ? "spacing met" : "at the floor",
                projection.GapNm,
                projection.RunwayGapNm,
                projection.RequiredNm,
                projection.CrossTrackNm,
                projection.FloorNm
            );
            return new BaseFollowSpacingDecision(BaseFollowSpacingAction.Keep, default);
        }

        var widen = new BaseFollowSpacingDecision(BaseFollowSpacingAction.Widen, WidenHeading(ctx.Aircraft.Position, runway, waypoints));
        return BreakOffReason(ctx.Aircraft.Position, runway, projection) is { } reason ? BreakOff(ctx, lead, projection, reason, widen) : widen;
    }

    /// <summary>
    /// The break-off for <paramref name="reason"/>: a go-around when it comes within <see cref="LateBreakOffMarginNm"/> of the
    /// final-turn point with the lead not yet abeam, a break-off otherwise. A structural overtake stays with the structural
    /// go-around (<see cref="BasePhase"/> checks it first), so it gets <paramref name="whenStructural"/> instead.
    /// </summary>
    private static BaseFollowSpacingDecision BreakOff(
        PhaseContext ctx,
        AircraftState lead,
        Projection projection,
        string reason,
        BaseFollowSpacingDecision whenStructural
    )
    {
        if (AirborneFollowHelper.IsStructuralOvertake(ctx, lead))
        {
            Log.LogTrace("[BaseSpacing] {Callsign}: {Reason}, but the overtake is structural: no break-off", ctx.Aircraft.Callsign, reason);
            return whenStructural;
        }

        bool late = (projection.CrossTrackNm - projection.TurnRadiusNm < LateBreakOffMarginNm) && !projection.LeadPassedAbeam;
        Log.LogDebug(
            "[BaseSpacing] {Callsign}: {Action}, {Reason} (gap={Gap:F2} runwayGap={Rwy:F2} required={Req:F2} xt={Xt:F2} floor={Floor:F2})",
            ctx.Aircraft.Callsign,
            late ? "too late to turn out, going around" : "breaking off the base",
            reason,
            projection.GapNm,
            projection.RunwayGapNm,
            projection.RequiredNm,
            projection.CrossTrackNm,
            projection.FloorNm
        );
        return new BaseFollowSpacingDecision(late ? BaseFollowSpacingAction.GoAround : BaseFollowSpacingAction.BreakOff, default);
    }

    /// <summary>Why a follower short of the spacing cannot widen for it, or null when a widen can build it.</summary>
    private static string? BreakOffReason(LatLon position, RunwayInfo runway, Projection projection)
    {
        if (!projection.OccupancyOnly && (projection.GapNm <= 0.0))
        {
            return "it would roll out ahead of or level with the lead";
        }

        if (projection.CrossTrackNm <= projection.FloorNm)
        {
            return "no room to widen inside the floor";
        }

        if (WidenMeetsParallelFinal(position, runway, projection.CrossTrackNm, projection.FloorNm))
        {
            return "a widen would meet a parallel final";
        }

        double gainNm = WidenGapGainNm(projection.CrossTrackNm, projection.FloorNm, projection.FollowerSpeedKts, projection.LeadSpeedKts);
        return (projection.EffectiveGapNm + gainNm < projection.RequiredNm) ? "a widen cannot build the spacing" : null;
    }

    /// <summary>
    /// The base heading turned <see cref="WidenOffBaseDeg"/> away from the field
    /// (<see cref="AirborneFollowHelper.PatternOutsideWidenSide"/>).
    /// </summary>
    private static TrueHeading WidenHeading(LatLon position, RunwayInfo runway, PatternWaypoints waypoints)
    {
        int side = AirborneFollowHelper.PatternOutsideWidenSide(position, waypoints.BaseHeading, runway, waypoints.Direction);
        return waypoints.BaseHeading + (side * WidenOffBaseDeg);
    }
}
