using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

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

    /// <summary>
    /// Desired following distance when leader is a jet (nm). 3.0 nm matches
    /// the FAA 7110.65 §5-5-4 IFR same-runway / same-altitude radar separation
    /// minimum, which is the floor real controllers aim for on jet-follows-jet
    /// approaches even under visual separation.
    /// </summary>
    private const double DesiredDistanceLargeNm = 3.0;

    // Free-flight spacing is wider than pattern-tight spacing: pilots maintaining
    // visual separation outside the pattern have less context (no runway cues,
    // higher closure risk at distance), so give them more room. On pattern join
    // the tighter pattern constants above take over.
    private const double FreeFlightDistanceSmallNm = 1.5;
    private const double FreeFlightDistanceMediumNm = 2.0;
    private const double FreeFlightDistanceLargeNm = 3.5;

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
    /// Seconds of monotonically increasing follower-to-lead gap after which the
    /// follow is auto-cancelled with an "unable to catch up" pilot transmission.
    /// Mirrors the original VfrFollowPhase constant; pattern-phase followers now
    /// share the same window via <see cref="CheckLeadLifecycle"/>.
    /// </summary>
    public const double RunawayGraceSeconds = 30.0;

    /// <summary>
    /// Minimum nm of gap growth above the running minimum that counts as "running
    /// away." Without this tolerance, a follower whose spacing-control loop is
    /// settling (gap creeping outward by ~0.001 nm/tick due to a 1-2 kt under-
    /// shoot) would trip the 30 s runaway timer even though there's no actual
    /// divergence — see FollowBreaksOnLeaderPatternEntryTests for the recorded
    /// case (best=1.30 nm, now=1.33 nm over 30 s of normal pattern-tight spacing).
    /// 0.1 nm = ~600 ft is well outside controller-perceptible "growing" while
    /// still tight enough to catch a lead that's genuinely outpacing the follower.
    /// </summary>
    private const double RunawayGapTolerance = 0.1;

    /// <summary>
    /// Per-tick lifecycle watchdog for any aircraft with
    /// <see cref="AircraftApproachState.FollowingCallsign"/> set. Cancels follow
    /// (clearing FollowingCallsign + emitting the appropriate pilot transmission)
    /// when:
    /// <list type="bullet">
    /// <item><description>The lead is no longer in the world (lookup returns null).</description></item>
    /// <item><description>The lead has transitioned to <see cref="AircraftState.IsOnGround"/>.</description></item>
    /// <item><description>The geographic gap to the lead has been monotonically growing
    /// for <see cref="RunawayGraceSeconds"/>.</description></item>
    /// </list>
    /// Pattern-phase OnTicks call this before applying their spacing adjustments;
    /// <see cref="Pattern.VfrFollowPhase.OnTick"/> delegates to it for the same checks.
    /// State for the runaway timer lives on <see cref="AircraftApproachState.FollowBestGapNm"/>
    /// and <see cref="AircraftApproachState.FollowRunawaySeconds"/> so it survives across
    /// pattern-phase transitions.
    /// </summary>
    /// <returns>True if the follow was cancelled this tick (caller should skip its spacing logic).</returns>
    public static bool CheckLeadLifecycle(PhaseContext ctx)
    {
        var follower = ctx.Aircraft;
        string? targetCallsign = follower.Approach.FollowingCallsign;
        if (targetCallsign is null)
        {
            return false;
        }

        var lead = ctx.AircraftLookup?.Invoke(targetCallsign);

        if (lead is null)
        {
            Log.LogDebug("[Follow] {Callsign}: target {Target} not found, ending follow", follower.Callsign, targetCallsign);
            ClearFollowState(follower);
            Pilot.PilotResponder.RouteSoloOrRpoTransmission(
                follower,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                Pilot.PilotResponder.BuildLostSightOfTraffic(follower, targetCallsign),
                $"{follower.Callsign} lost sight of {targetCallsign}, cancelling follow",
                Pilot.PilotResponder.SoloPositionsTowerApproach
            );
            return true;
        }

        if (lead.IsOnGround)
        {
            Log.LogDebug("[Follow] {Callsign}: target {Target} on ground, ending follow", follower.Callsign, targetCallsign);
            ClearFollowState(follower);
            Pilot.PilotResponder.RouteSoloOrRpoTransmission(
                follower,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                Pilot.PilotResponder.BuildTargetLanded(follower, targetCallsign),
                $"{follower.Callsign} {targetCallsign} has landed, cancelling follow",
                Pilot.PilotResponder.SoloPositionsTowerApproach
            );
            return true;
        }

        // Pattern-flow guard: when the lead is on a LATER pattern leg than the
        // follower on the same runway, geometric divergence is expected from the
        // leg geometry (e.g. follower on Downwind heading east, lead on Final
        // heading west). The gap will close again once the follower transitions
        // toward the lead's leg, so don't accumulate runaway seconds here. Reset
        // best gap so when the follower does turn base + final, the watchdog
        // measures growth from the new (smaller) baseline rather than firing the
        // moment the gap fails to shrink back to the pre-divergence minimum.
        if (IsLeadPatternFlowAhead(follower, lead))
        {
            follower.Approach.FollowBestGapNm = null;
            follower.Approach.FollowRunawaySeconds = 0;
            return false;
        }

        double gapNm = GeoMath.DistanceNm(follower.Position, lead.Position);
        double bestSoFar = follower.Approach.FollowBestGapNm ?? double.PositiveInfinity;
        if (gapNm <= bestSoFar)
        {
            follower.Approach.FollowBestGapNm = gapNm;
            follower.Approach.FollowRunawaySeconds = 0;
            return false;
        }

        // Inside the noise band — the lead isn't actually getting away, the
        // spacing loop is just settling. Don't tick the runaway timer (and
        // don't reset it either — a real long-running divergence will keep
        // pushing past tolerance and eventually fire).
        if (gapNm - bestSoFar < RunawayGapTolerance)
        {
            return false;
        }

        follower.Approach.FollowRunawaySeconds += ctx.DeltaSeconds;
        if (follower.Approach.FollowRunawaySeconds < RunawayGraceSeconds)
        {
            return false;
        }

        Log.LogDebug(
            "[Follow] {Callsign}: gap to {Target} growing >{Grace:F0}s (best={Best:F1}nm, now={Now:F1}nm), cancelling",
            follower.Callsign,
            targetCallsign,
            RunawayGraceSeconds,
            bestSoFar,
            gapNm
        );
        ClearFollowState(follower);
        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            follower,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            Pilot.PilotResponder.BuildUnableToCatchUp(follower, targetCallsign),
            $"{follower.Callsign} unable to catch up to {targetCallsign}, cancelling follow",
            Pilot.PilotResponder.SoloPositionsTowerApproach
        );
        return true;
    }

    /// <summary>
    /// Clear the follow target and reset the runaway-distance tracking on a
    /// follower. Call this whenever <see cref="AircraftApproachState.FollowingCallsign"/>
    /// is being set or cleared from outside the lifecycle check (FOLLOW dispatch,
    /// vector-command phase clear, separation-failure cancel).
    /// </summary>
    public static void ClearFollowState(AircraftState follower)
    {
        follower.Approach.FollowingCallsign = null;
        follower.Approach.FollowBestGapNm = null;
        follower.Approach.FollowRunawaySeconds = 0;
    }

    /// <summary>
    /// Reset the per-pair runaway-distance tracking (best gap + elapsed seconds)
    /// without touching <see cref="AircraftApproachState.FollowingCallsign"/>.
    /// Call this when a fresh FOLLOW is issued so the next
    /// <see cref="CheckLeadLifecycle"/> tick captures the new geometry.
    /// </summary>
    public static void ResetRunawayTracking(AircraftState follower)
    {
        follower.Approach.FollowBestGapNm = null;
        follower.Approach.FollowRunawaySeconds = 0;
    }

    /// <summary>
    /// Position of an aircraft within a single VFR pattern circuit, expressed as
    /// a monotonically increasing index. Used by <see cref="IsLeadPatternFlowBehind"/>
    /// to compare two aircraft's progress along the same pattern. Non-pattern
    /// phases return null.
    /// </summary>
    private static int? PatternLegIndex(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase switch
        {
            PatternEntryPhase => 0,
            UpwindPhase => 1,
            CrosswindPhase => 2,
            DownwindPhase => 3,
            BasePhase => 4,
            FinalApproachPhase => 5,
            LandingPhase or TouchAndGoPhase => 6,
            _ => null,
        };

    /// <summary>
    /// True when both aircraft are flying patterns to the same runway and the
    /// lead is on an earlier pattern leg than the follower — i.e. geographically
    /// close but pattern-flow-AHEAD on the follower's part. In this state the
    /// spacing helper should NOT slow the follower down: the lead has yet to
    /// catch up to the leg the follower is already on, and pulling the follower
    /// to Vref under the false belief that it's chasing produces multi-minute
    /// downwind extensions (audit observation: N172SP held Downwind for 160 s
    /// at 62 KIAS while N428KK was on PatternEntry feeder 0.67 nm away).
    /// Once the lead catches up to the same or later leg, the check returns
    /// false and normal spacing resumes.
    /// </summary>
    private static bool IsLeadPatternFlowBehind(AircraftState follower, AircraftState lead)
    {
        string? followerRwy = follower.Phases?.AssignedRunway?.Designator;
        string? leadRwy = lead.Phases?.AssignedRunway?.Designator;
        if (followerRwy is null || leadRwy is null)
        {
            return false;
        }
        if (!string.Equals(followerRwy, leadRwy, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int? followerLeg = PatternLegIndex(follower);
        int? leadLeg = PatternLegIndex(lead);
        if (followerLeg is null || leadLeg is null)
        {
            return false;
        }
        if (followerLeg > leadLeg)
        {
            return true;
        }
        // Same leg: the aircraft that's been on it longer is further along it
        // and therefore "ahead" in pattern flow. Compare phase elapsed time.
        if (followerLeg == leadLeg)
        {
            double followerElapsed = follower.Phases?.CurrentPhase?.ElapsedSeconds ?? 0;
            double leadElapsed = lead.Phases?.CurrentPhase?.ElapsedSeconds ?? 0;
            return followerElapsed > leadElapsed;
        }
        return false;
    }

    /// <summary>
    /// True when both aircraft are flying patterns to the same runway and the
    /// lead is on a LATER pattern leg than the follower — geographic gap growth
    /// during the follower's current leg is expected (e.g. follower on Downwind
    /// heading east, lead on Final heading west — they're on parallel-offset
    /// tracks pointing opposite directions, so the gap can only grow until the
    /// follower turns base). The runaway-distance watchdog should not fire here:
    /// pattern flow guarantees the gap will close again once the follower
    /// transitions toward the lead's leg.
    /// </summary>
    private static bool IsLeadPatternFlowAhead(AircraftState follower, AircraftState lead)
    {
        string? followerRwy = follower.Phases?.AssignedRunway?.Designator;
        string? leadRwy = lead.Phases?.AssignedRunway?.Designator;
        if (followerRwy is null || leadRwy is null)
        {
            return false;
        }
        if (!string.Equals(followerRwy, leadRwy, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int? followerLeg = PatternLegIndex(follower);
        int? leadLeg = PatternLegIndex(lead);
        if (followerLeg is null || leadLeg is null)
        {
            return false;
        }
        // Strict leg-ahead only. Same-leg cases are intentionally NOT short-
        // circuited: when both aircraft are on parallel tracks heading the same
        // way, gap growth is no longer "expected pattern geometry" — it means
        // the lead is genuinely outpacing the follower, which is what the
        // watchdog exists to catch.
        return leadLeg > followerLeg;
    }

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
            ClearFollowState(ctx.Aircraft);
            return null;
        }

        if (IsLeadPatternFlowBehind(ctx.Aircraft, target))
        {
            Log.LogDebug(
                "[Follow] {Callsign}: lead {Target} is pattern-flow-behind on same runway, holding baseline",
                ctx.Aircraft.Callsign,
                targetCallsign
            );
            return null;
        }

        return ComputeAdjustedSpeed(
            ctx.Aircraft,
            target,
            normalSpeed,
            minSpeed,
            maxSpeedAdjustKts,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Log
        );
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
            ClearFollowState(ctx.Aircraft);
            return null;
        }

        if (IsLeadPatternFlowBehind(ctx.Aircraft, target))
        {
            Log.LogDebug(
                "[Follow] {Callsign}: lead {Target} is pattern-flow-behind on same runway, holding baseline",
                ctx.Aircraft.Callsign,
                targetCallsign
            );
            return null;
        }

        var leaderCategory = AircraftCategorization.Categorize(target.AircraftType);
        double desired = FreeFlightDistanceForLeader(leaderCategory);
        return ComputeAdjustedSpeedWithDesired(
            ctx.Aircraft,
            target,
            normalSpeed,
            minSpeed,
            desired,
            MaxSpeedAdjustKts,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Log
        );
    }

    /// <summary>
    /// Phase-context-free variant used by <see cref="VfrFollowPhase"/> during free
    /// pursuit (lead not in a pattern). Treats the lead's ground speed as the
    /// "normal" target so the follower tracks the lead's speed with distance-based
    /// correction, using the wider free-flight desired distances.
    ///
    /// Returns the adjusted speed, or <c>null</c> when separation cannot be
    /// maintained — in that case the helper has already added a one-shot warning
    /// to <see cref="AircraftState.PendingWarnings"/> and cleared
    /// <see cref="AircraftState.Approach.FollowingCallsign"/>; the caller MUST
    /// end its follow phase or the warning will fire again next tick.
    /// </summary>
    /// <param name="follower">Follower aircraft.</param>
    /// <param name="lead">Lead aircraft.</param>
    /// <param name="minSpeed">Absolute floor — never returns below this.</param>
    /// <param name="logger">Logger for warnings when separation cannot be maintained.</param>
    public static double? AdjustedFreeFlightSpeed(
        AircraftState follower,
        AircraftState lead,
        double minSpeed,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
        ILogger logger
    )
    {
        double normalSpeed = Math.Max(lead.IndicatedAirspeed, minSpeed);
        var leaderCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double desired = FreeFlightDistanceForLeader(leaderCategory);
        return ComputeAdjustedSpeedWithDesired(
            follower,
            lead,
            normalSpeed,
            minSpeed,
            desired,
            MaxSpeedAdjustKts,
            soloTrainingMode,
            rpoShowPilotSpeech,
            logger
        );
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
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
        ILogger logger
    )
    {
        var leaderCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double desired = DesiredDistanceForLeader(leaderCategory);
        return ComputeAdjustedSpeedWithDesired(
            follower,
            lead,
            normalSpeed,
            minSpeed,
            desired,
            maxSpeedAdjustKts,
            soloTrainingMode,
            rpoShowPilotSpeech,
            logger
        );
    }

    private static double? ComputeAdjustedSpeedWithDesired(
        AircraftState follower,
        AircraftState lead,
        double normalSpeed,
        double minSpeed,
        double desired,
        double maxSpeedAdjustKts,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech,
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
            ClearFollowState(follower);
            Pilot.PilotResponder.RouteRpoTransmission(
                follower,
                soloTrainingMode,
                rpoShowPilotSpeech,
                Pilot.PilotResponder.BuildUnableToMaintainSeparation(follower, lead.Callsign),
                $"{follower.Callsign} unable to maintain separation from {lead.Callsign}, cancelling follow"
            );
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

        // Pattern-flow gate: don't extend downwind for a lead that hasn't
        // entered the same Downwind leg yet — we'd be extending to make room
        // for an aircraft that's still flying the feeder behind us.
        if (IsLeadPatternFlowBehind(ctx.Aircraft, target))
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
