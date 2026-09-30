using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases.Approach;
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

    /// <summary>
    /// Body-frame relative bearing (deg) beyond which a lead counts as aft of the follower's
    /// 3-9 line, i.e. it has passed and a base turn is flown behind it rather than into it. A few
    /// degrees past the geometric 90° so a single bank-induced heading wobble at the abeam
    /// moment cannot release the (irreversible) base turn a tick early.
    /// </summary>
    private const double LeadPassedRelativeBearingDeg = 93.0;

    /// <summary>
    /// Floor (kt) on the lead's ground speed used when projecting its threshold ETA, so a lead
    /// caught mid-flare or in a momentary slow sample cannot project an infinite ETA.
    /// </summary>
    internal const double MinProjectionSpeedKts = 40.0;

    // Runway occupancy time — threshold crossing to clear of the runway — by lead category. The
    // publications give no per-category figure: the only runway-occupancy number in 7110.65 is
    // the 50 s average that qualifies a runway for 2.5 nm reduced separation (§5-5-4.j.2). These
    // are simulation allowances anchored to that centroid — a jet rolls further and slower off
    // the pavement than a light single — used to judge whether the runway will be clear when a
    // following pattern aircraft crosses the threshold (§3-10-3).
    private const double RunwayClearanceSecondsJet = 60.0;
    private const double RunwayClearanceSecondsTurboprop = 45.0;
    private const double RunwayClearanceSecondsLight = 40.0;

    // Free-flight spacing is wider than pattern-tight spacing: pilots maintaining
    // visual separation outside the pattern have less context (no runway cues,
    // higher closure risk at distance), so give them more room. On pattern join
    // the tighter pattern constants above take over.
    private const double FreeFlightDistanceSmallNm = 1.5;
    private const double FreeFlightDistanceMediumNm = 2.0;
    private const double FreeFlightDistanceLargeNm = 3.5;

    /// <summary>How far (nm) inside the desired trail distance a follower on final must be before it S-turns for spacing.</summary>
    internal const double TrailRegimeDeadbandNm = 0.3;

    /// <summary>
    /// How far past the desired trail distance (nm, along the lead's path) a spacing excursion keeps opening the gap
    /// before it ends: it starts once the gap falls below the desired distance and stops at desired + this.
    /// </summary>
    public const double TrailGapHysteresisNm = 0.1;

    /// <summary>Spacing excursion heading off the lead's track (deg): a shallow S-turn (AIM 4-3-5).</summary>
    public const double TrailExcursionDeg = 30.0;

    /// <summary>
    /// Spacing excursion heading off the lead's track (deg) for a piston or helicopter follower whose gap is short of its goal
    /// by more than <see cref="TrailWideExcursionShortfallNm"/>.
    /// </summary>
    public const double TrailWideExcursionDeg = 45.0;

    /// <summary>
    /// Shortfall (nm) below the excursion's goal beyond which a slow follower turns <see cref="TrailWideExcursionDeg"/> off the
    /// track instead of <see cref="TrailExcursionDeg"/>.
    /// </summary>
    public const double TrailWideExcursionShortfallNm = 0.3;

    /// <summary>Least lateral offset (nm) a spacing excursion may reach off the lead's path.</summary>
    public const double TrailMinOffsetCapNm = 1.0;

    /// <summary>
    /// A spacing excursion may reach this many of the follower's turn radii off the lead's path, when that is more than
    /// <see cref="TrailMinOffsetCapNm"/> (and, for a turboprop or jet, no more than <see cref="TrailFastOffsetCapNm"/>).
    /// </summary>
    public const double TrailOffsetCapTurnRadii = 3.0;

    /// <summary>Largest lateral offset (nm) a turboprop or jet follower's spacing excursion reaches: a shallow S-turn, not a turn-out.</summary>
    public const double TrailFastOffsetCapNm = 1.5;

    /// <summary>
    /// How far (nm) beyond the excursion's offset cap a parallel runway's extended centerline must lie for the excursion to
    /// turn toward it; nearer, that side is closed to the excursion.
    /// </summary>
    public const double TrailParallelFinalMarginNm = 0.5;

    /// <summary>
    /// Largest difference (deg) between any stretch of the lead's path ahead of the follower and the lead's present track
    /// for that path to count as one straight leg, on which the follower points its nose at the lead.
    /// </summary>
    public const double TrailStraightPathToleranceDeg = 15.0;

    /// <summary>Seconds of the follower's ground speed it looks ahead along the lead's path while flying the lead's turns.</summary>
    private const double TrailPathLookAheadSeconds = 8.0;

    /// <summary>Shortest look-ahead (nm) along the lead's path while flying the lead's turns.</summary>
    private const double TrailMinPathLookAheadNm = 0.15;

    /// <summary>
    /// Minimum lead ground speed (kt) for its ground track to be a reliable trail reference.
    /// Below this the lead is nearly stopped / just airborne, so the law degenerates to pure
    /// pursuit (pointing at the lead's current position).
    /// </summary>
    private const double TrailMinLeadGroundSpeedKt = 35.0;

    /// <summary>Speed correction gain: kts per nm of distance error.</summary>
    internal const double SpeedGainPerNm = 25.0;

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
    /// Margin (kt) by which a follower's minimum approach speed must exceed the lead's
    /// ground speed for a follow to count as a STRUCTURAL overtake — one the follower
    /// can never sequence by speed alone because even its slowest sustainable approach
    /// speed outruns the lead. Below this margin the in-pattern speed reduction can still
    /// open the gap, so no break-off is warranted (e.g. a C172 behind a C172).
    /// </summary>
    public const double StructuralOvertakeMarginKts = 10.0;

    /// <summary>
    /// Straight-line gap (nm) at or below which a follower in a structural overtake on
    /// base/final must break off the follow and go around rather than press on toward the
    /// lead. Sits above the same-runway separation minimum of 3,000 ft (≈0.5 nm) at the
    /// threshold for two Category I aircraft (FAA 7110.65 §3-10-3), giving the follower
    /// room to climb away cleanly before that minimum is breached. AIM 4-3-3 NOTE 1: do not
    /// overtake or cut in front of traffic on final.
    /// </summary>
    public const double FollowBreakOffGapNm = 0.8;

    /// <summary>
    /// Fraction of desired distance below which downwind should be extended
    /// to avoid turning base too close to the leader.
    /// </summary>
    private const double ExtendDownwindThreshold = 0.6;

    /// <summary>
    /// Maximum nm of along-track a downwind follower will extend past its normal
    /// base-turn point to sequence behind a pattern-flow-ahead lead. This is the
    /// spatial bound on <see cref="ShouldHoldForLeadSequencing"/> — the guard
    /// against an ahead lead crawling on a long final; the temporal bound is the
    /// lead-lifecycle in <see cref="CheckLeadLifecycle"/> (the lead lands, despawns,
    /// or is lost from sight).
    /// 4.0 nm keeps the extension inside the FAA 7110.65 §4-8-1.3 circling-approach
    /// area headroom while allowing the multi-nm downwind extensions that are normal
    /// when sequencing in a busy pattern; the controller can still call the base turn
    /// (TB) earlier or extend further (EXT) manually. The cap is <em>advisory</em>: a
    /// follower never self-turns to break the hold — past the cap the pilot transmits
    /// a one-shot "extending … unable to turn" so the controller can re-sequence, then
    /// keeps flying the leg.
    /// </summary>
    public const double MaxFollowExtensionNm = 4.0;

    /// <summary>Half-width (deg) of the cone about the follower's track inside which a lead counts as ahead.</summary>
    public const double LeadAheadOfTrackMaxDeg = 60.0;

    /// <summary>
    /// True when the bearing from <paramref name="follower"/> to <paramref name="lead"/> lies within
    /// <see cref="LeadAheadOfTrackMaxDeg"/> of the follower's ground track (bounds inclusive). The track, not
    /// the heading, is what closes on the lead, so a crab does not change the answer.
    /// </summary>
    public static bool IsLeadAheadOfTrack(AircraftState follower, AircraftState lead)
    {
        var bearingToLead = new TrueHeading(GeoMath.BearingTo(follower.Position, lead.Position));
        return follower.TrueTrack.AbsAngleTo(bearingToLead) <= LeadAheadOfTrackMaxDeg;
    }

    /// <summary>
    /// Per-tick lifecycle watchdog for any aircraft with
    /// <see cref="AircraftApproachState.FollowingCallsign"/> set. Cancels follow
    /// (clearing FollowingCallsign + emitting the appropriate pilot transmission)
    /// when:
    /// <list type="bullet">
    /// <item><description>The lead is no longer in the world (lookup returns null).</description></item>
    /// <item><description>The lead has transitioned to <see cref="AircraftState.IsOnGround"/>.</description></item>
    /// <item><description>The follower loses visual contact with the lead — a BKN/OVC deck slides
    /// between them, or flight visibility collapses below the gap (AIM §5-5-12.a.2 / §4-4-14
    /// NOTE). A lead that merely pulls ahead or slips behind is still followed; a growing gap
    /// increases separation and is the controller's to re-sequence. See
    /// <see cref="VisualDetection.TryMaintainTrafficContact"/> for why the type-detection-range /
    /// hemisphere / bank geometry is deliberately not re-checked here.</description></item>
    /// </list>
    /// Pattern-phase OnTicks call this before applying their spacing adjustments;
    /// <see cref="Pattern.VfrFollowPhase.OnTick"/> delegates to it for the same checks.
    /// </summary>
    /// <returns>True if the follow was cancelled this tick (caller should skip its spacing logic).</returns>
    public static bool CheckLeadLifecycle(PhaseContext ctx)
    {
        AircraftState follower = ctx.Aircraft;
        string? targetCallsign = follower.Approach.FollowingCallsign;
        if (targetCallsign is null)
        {
            return false;
        }

        AircraftState? lead = ctx.AircraftLookup?.Invoke(targetCallsign);

        if (lead is null)
        {
            Log.LogDebug("[Follow] {Callsign}: target {Target} not found, ending follow", follower.Callsign, targetCallsign);
            Tower.VisualApproachHelper.HandleTrafficContactLost(ctx, targetCallsign, false);
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
                Pilot.PilotResponder.SoloPositionsTowerApproach
            );
            return true;
        }

        // Loss of visual contact is the ONLY self-generated follow cancel (AIM
        // §5-5-12.a.2 / §4-4-14 NOTE). A lead that merely pulls ahead (it is faster)
        // or slips behind is still followed — a growing gap *increases* separation and
        // is the controller's to re-sequence, never the follower's cue to break off.
        // The maintained-contact check therefore runs only the weather obstructions (a
        // BKN/OVC deck lying between the two aircraft, or a flight-visibility collapse
        // below the gap) and skips the type-detection-range / forward-hemisphere /
        // bank-occlusion geometry, which models finding unknown traffic rather than
        // tracking traffic already called in sight.
        VisualAcquisitionResult contact = VisualAcquisition.TryMaintainTrafficContact(follower, lead, ctx.Weather);
        if (!contact.Acquired)
        {
            Log.LogDebug("[Follow] {Callsign}: lost visual on {Target} ({Reason}), ending follow", follower.Callsign, targetCallsign, contact.Reason);
            Tower.VisualApproachHelper.HandleTrafficContactLost(ctx, targetCallsign, false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Clear the follow target on a follower without holding its pattern leg — for cases where
    /// the follower should proceed, not freeze on present vector: the lead has LANDED (the
    /// follower is now number one and continues its approach), a controller command supersedes
    /// the follow (vector / new approach), or a Base/Final break-off to a go-around. Involuntary
    /// in-pattern cancels (lead lost/despawned, spacing unmaintainable) use
    /// <see cref="CancelFollowHoldingLeg"/> instead.
    /// </summary>
    public static void ClearFollowState(AircraftState follower) =>
        // Deliberately does NOT touch HasReportedTrafficInSight: ending the follow
        // instruction is not the same event as losing sight of the traffic. The
        // dispatcher's generic phase-clear runs this before every phase-clearing
        // command — including FOLLOW itself, whose bare form gates on the report —
        // and a vectored-off follower still sees the traffic it called. Sites where
        // the sight itself is lost (VisualApproachHelper.HandleTrafficContactLost,
        // VoidVisualApproach) clear the report themselves.
        follower.Approach.FollowingCallsign = null;

    /// <summary>
    /// End a follow that was cancelled involuntarily — the lead despawned or was lost from
    /// sight, or spacing could not be held — while the follower is still flying a pattern leg.
    /// The follower's only outstanding instruction was to follow, so it is NOT authorized to
    /// turn on its own; it holds its present leg (Upwind/Crosswind/Downwind) via the
    /// extended-leg hold and awaits a controller turn (TB) or re-sequence (AIM §5-5-12.a.2 —
    /// advise ATC and maintain, don't maneuver on your own). Base/Final are committed to the
    /// approach and free-flight legs have nothing to hold, so those continue unchanged. A lead
    /// that has LANDED is the exception (the follower is now number one): those callers use
    /// bare <see cref="ClearFollowState"/> so the follower continues its approach and lands.
    /// </summary>
    public static void CancelFollowHoldingLeg(AircraftState follower)
    {
        ClearFollowState(follower);
        switch (follower.Phases?.CurrentPhase)
        {
            case UpwindPhase uw:
                uw.IsExtended = true;
                break;
            case CrosswindPhase cw:
                cw.IsExtended = true;
                break;
            case DownwindPhase dw:
                dw.IsExtended = true;
                break;
        }
    }

    /// <summary>Leg index of a pattern entry (<see cref="PatternEntryPhase"/>, a midfield crossing, a teardrop) in <see cref="PatternLegIndex"/>.</summary>
    internal const int EntryLegIndex = 0;

    /// <summary>Leg index of the upwind in <see cref="PatternLegIndex"/>.</summary>
    private const int UpwindLegIndex = 1;

    /// <summary>Leg index of the crosswind in <see cref="PatternLegIndex"/>.</summary>
    private const int CrosswindLegIndex = 2;

    /// <summary>Leg index of the downwind in <see cref="PatternLegIndex"/>.</summary>
    private const int DownwindLegIndex = 3;

    /// <summary>Leg index of the base in <see cref="PatternLegIndex"/>.</summary>
    private const int BaseLegIndex = 4;

    /// <summary>Leg index of the final in <see cref="PatternLegIndex"/>.</summary>
    private const int FinalLegIndex = 5;

    /// <summary>Leg index of the terminal (landing, touch-and-go, stop-and-go, low approach) in <see cref="PatternLegIndex"/>.</summary>
    private const int TerminalLegIndex = 6;

    /// <summary>
    /// Position of an aircraft within a single VFR pattern circuit, expressed as
    /// a monotonically increasing index. Used by <see cref="IsLeadPatternFlowBehind"/>
    /// to compare two aircraft's progress along the same pattern. An aircraft flying no
    /// pattern leg but still in the landing sequence gets the leg it stands in for
    /// (<see cref="SequenceLegIndex"/>); anything else returns null.
    /// </summary>
    internal static int? PatternLegIndex(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase switch
        {
            // Wrong-side crossing/teardrop entries are pattern feeders like PatternEntryPhase:
            // a lead still on one of them is a leg BEHIND any follower already on a numbered
            // leg, and returning null here would suppress the flow-behind guard — the follower
            // would slow toward Vref chasing traffic that has not even joined the pattern yet.
            PatternEntryPhase or MidfieldCrossingPhase or TeardropReentryPhase => EntryLegIndex,
            UpwindPhase => UpwindLegIndex,
            CrosswindPhase => CrosswindLegIndex,
            DownwindPhase => DownwindLegIndex,
            BasePhase => BaseLegIndex,
            FinalApproachPhase => FinalLegIndex,
            // Every terminal a pattern circuit can end on. A helicopter's circuit ends on
            // HelicopterLandingPhase, and a stop-and-go or low-approach clearance swaps the terminal
            // via ReplaceApproachEnding — all four sit in the slot LandingPhase occupies, and all four
            // are still flown (the helicopter is airborne on its hover-descent until touchdown). Leaving
            // one out reads as leg null, which makes both the flow-ahead and flow-behind tests false and
            // drops the sequencing guard while the lead is still on the runway.
            LandingPhase or TouchAndGoPhase or HelicopterLandingPhase or StopAndGoPhase or LowApproachPhase => TerminalLegIndex,
            _ => SequenceLegIndex(aircraft),
        };

    /// <summary>
    /// The pattern leg an aircraft flying no pattern-leg phase stands in for in the landing sequence: an instrument approach
    /// (<see cref="InterceptCoursePhase"/>, <see cref="ApproachNavigationPhase"/>) or any other phase flown on its runway's final
    /// by geometry (<see cref="IsOnFinalByGeometry"/>) is the final; a go-around that re-enters the pattern
    /// (<see cref="GoAroundPhase.ReenterPattern"/>) and a closed-traffic takeoff climb are the upwind (AIM §4-3-2.a.3.2). Null
    /// otherwise: a missed approach, a departure leaving the pattern, a hold.
    /// </summary>
    private static int? SequenceLegIndex(AircraftState aircraft)
    {
        Phase? phase = aircraft.Phases?.CurrentPhase;
        if (phase is InterceptCoursePhase or ApproachNavigationPhase)
        {
            return FinalLegIndex;
        }

        if ((phase is GoAroundPhase { ReenterPattern: true }) || IsClosedTrafficClimb(aircraft))
        {
            return UpwindLegIndex;
        }

        return ((aircraft.Phases?.AssignedRunway is { } runway) && IsOnFinalByGeometry(aircraft, runway)) ? FinalLegIndex : null;
    }

    /// <summary>
    /// True when <paramref name="aircraft"/> is airborne on a closed-traffic takeoff off its pattern runway: the climb-out is the
    /// circuit's upwind. A cross-runway closed-traffic climb (<see cref="PhaseList.DepartureRunway"/> set) is not on the pattern
    /// runway's upwind, so it is left out.
    /// </summary>
    internal static bool IsClosedTrafficClimb(AircraftState aircraft) =>
        !aircraft.IsOnGround && (aircraft.Phases is { CurrentPhase: TakeoffPhase { Departure: ClosedTrafficDeparture }, DepartureRunway: null });

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
    /// false and normal spacing resumes. Across different legs the order is
    /// <see cref="IsLeadAheadAcrossLegs"/>. The command-time FOLLOW refusal uses the same test.
    /// </summary>
    internal static bool IsLeadPatternFlowBehind(AircraftState follower, AircraftState lead)
    {
        if (
            (follower.Phases?.AssignedRunway is not { } runway)
            || (lead.Phases?.AssignedRunway is not { } leadRunway)
            || !IsSameRunway(runway, leadRunway)
        )
        {
            return false;
        }
        if ((PatternLegIndex(follower) is not { } followerLeg) || (PatternLegIndex(lead) is not { } leadLeg))
        {
            return false;
        }
        // Same leg: ordered by SharedLegOrderNm (progress along an outbound leg, remaining path
        // to the threshold on the others). Time on the leg is no measure: a straight-in joins the
        // final closer in than a pattern aircraft already on it, and an S-turn restarts the
        // final's clock.
        if (followerLeg == leadLeg)
        {
            PatternWaypoints? wp = SequenceWaypoints(follower) ?? SequenceWaypoints(lead);
            return SharedLegOrderNm(follower, followerLeg, runway, wp) < SharedLegOrderNm(lead, followerLeg, runway, wp);
        }
        return !IsLeadAheadAcrossLegs(follower, lead, followerLeg, leadLeg, runway);
    }

    /// <summary>
    /// Sequence order of a same-runway pair on different legs, true when <paramref name="lead"/> is ahead. A follower on base
    /// or final (an instrument approach and final by geometry included) is ordered by remaining path to the threshold
    /// (<see cref="IsLeadNoFartherFromThreshold"/>): base, final and approach converge on one final, so an extension cannot
    /// flip the order there, while leg order would put a 6 nm straight-in behind a close base and a close base behind a 10 nm
    /// final. Against a lead still on a pattern entry, and for a follower on any other leg, the later leg is ahead: an
    /// outbound leg's extension adds path, so a path order would flip the moment the lead turned onto its next leg.
    /// </summary>
    private static bool IsLeadAheadAcrossLegs(AircraftState follower, AircraftState lead, int followerLeg, int leadLeg, RunwayInfo runway) =>
        ((followerLeg is BaseLegIndex or FinalLegIndex) && (leadLeg != EntryLegIndex))
            ? IsLeadNoFartherFromThreshold(follower, lead, runway)
            : (leadLeg > followerLeg);

    /// <summary>
    /// Order key (nm) of <paramref name="ac"/> on pattern leg <paramref name="leg"/> it shares with another aircraft, smaller
    /// meaning ahead. On an outbound leg (upwind, crosswind, downwind) it is progress along the leg, since the remaining pattern
    /// path of an aircraft extended past the leg's turn point grows the farther out it flies, while it is still the one ahead
    /// on that leg; on the other legs it is the remaining path to the threshold (<see cref="SequenceRemainingPathNm"/>).
    /// <see cref="double.PositiveInfinity"/> on an outbound leg with no geometry <paramref name="wp"/> to measure it on.
    /// </summary>
    private static double SharedLegOrderNm(AircraftState ac, int leg, RunwayInfo runway, PatternWaypoints? wp)
    {
        if (leg is < UpwindLegIndex or > DownwindLegIndex)
        {
            return SequenceRemainingPathNm(ac, runway, wp);
        }

        if (wp is null)
        {
            return double.PositiveInfinity;
        }

        // Along-track on the downwind axis from the threshold: it falls as the upwind is flown out and rises along the downwind.
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        double alongNm = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, wp.DownwindHeading);
        return leg switch
        {
            UpwindLegIndex => alongNm,
            CrosswindLegIndex => -Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, wp.DownwindHeading)),
            _ => -alongNm,
        };
    }

    /// <summary>
    /// True when <paramref name="lead"/>'s remaining path to <paramref name="runway"/>'s threshold
    /// (<see cref="SequenceRemainingPathNm"/>, each aircraft on its own pattern geometry) is no longer than
    /// <paramref name="follower"/>'s, with no tolerance. False when the lead's path cannot be measured (no leg in the sequence).
    /// </summary>
    private static bool IsLeadNoFartherFromThreshold(AircraftState follower, AircraftState lead, RunwayInfo runway)
    {
        double leadNm = SequenceRemainingPathNm(lead, runway, SequenceWaypoints(lead));
        return !double.IsPositiveInfinity(leadNm) && (leadNm <= SequenceRemainingPathNm(follower, runway, SequenceWaypoints(follower)));
    }

    /// <summary>
    /// Remaining path (nm) from <paramref name="ac"/> to <paramref name="runway"/>'s threshold, the sequence coordinate: its
    /// along-final distance when on final (<see cref="IsOnFinalForSequence"/>); on an instrument approach not yet on final,
    /// the path it still has to fly (<see cref="ApproachPathNm"/>) or, on a course intercept, its straight-line distance to
    /// the threshold; and otherwise its pattern path (<see cref="RemainingPatternPathNm"/>) on the reference geometry
    /// <paramref name="wp"/>. <see cref="double.PositiveInfinity"/> when it has no leg in the sequence, or flies a pattern leg
    /// with no geometry to measure it on.
    /// </summary>
    private static double SequenceRemainingPathNm(AircraftState ac, RunwayInfo runway, PatternWaypoints? wp)
    {
        int? leg = PatternLegIndex(ac);
        if (IsOnFinalForSequence(ac, leg, runway))
        {
            return Math.Max(0.0, AlongFinalNm(ac.Position, runway));
        }

        if (leg == FinalLegIndex)
        {
            return ac.Phases?.CurrentPhase is ApproachNavigationPhase navigation
                ? ApproachPathNm(ac.Position, navigation, runway)
                : GeoMath.DistanceNm(ac.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
        }

        return ((leg is null) || (wp is null)) ? double.PositiveInfinity : RemainingPatternPathNm(ac, wp);
    }

    /// <summary>
    /// Path (nm) still to fly on an instrument approach's fix sequence: from <paramref name="position"/> through every fix not
    /// yet reached (<see cref="ApproachNavigationPhase.CurrentFixIndex"/> onward), then from the last fix to
    /// <paramref name="runway"/>'s threshold.
    /// </summary>
    private static double ApproachPathNm(LatLon position, ApproachNavigationPhase navigation, RunwayInfo runway)
    {
        double pathNm = 0.0;
        LatLon from = position;
        for (int i = navigation.CurrentFixIndex; i < navigation.Fixes.Count; i++)
        {
            var fix = new LatLon(navigation.Fixes[i].Latitude, navigation.Fixes[i].Longitude);
            pathNm += GeoMath.DistanceNm(from, fix);
            from = fix;
        }

        return pathNm + GeoMath.DistanceNm(from, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
    }

    /// <summary>
    /// True when <paramref name="ac"/> (on pattern leg <paramref name="leg"/>) is on <paramref name="runway"/>'s final for
    /// sequencing: in <see cref="FinalApproachPhase"/>, on a terminal phase, or on the final by geometry
    /// (<see cref="IsOnFinalByGeometry"/>).
    /// </summary>
    private static bool IsOnFinalForSequence(AircraftState ac, int? leg, RunwayInfo runway) =>
        (ac.Phases?.CurrentPhase is FinalApproachPhase) || (leg == TerminalLegIndex) || IsOnFinalByGeometry(ac, runway);

    /// <summary>
    /// The pattern geometry <paramref name="ac"/> flies: its current leg's waypoints, else those of the first leg in its phase
    /// list that carries them (a follower on final keeps its flown legs; one on an entry has its downwind queued). Null when
    /// it has none.
    /// </summary>
    private static PatternWaypoints? SequenceWaypoints(AircraftState ac) =>
        PatternWaypointsOf(ac.Phases?.CurrentPhase) ?? FirstPatternWaypoints(ac.Phases);

    /// <summary>The waypoints of the first phase in <paramref name="phases"/> that carries pattern waypoints; null when none does.</summary>
    internal static PatternWaypoints? FirstPatternWaypoints(PhaseList? phases)
    {
        if (phases is null)
        {
            return null;
        }

        foreach (Phase phase in phases.Phases)
        {
            if (PatternWaypointsOf(phase) is { } waypoints)
            {
                return waypoints;
            }
        }

        return null;
    }

    /// <summary>The pattern waypoints <paramref name="phase"/> flies, for every phase that carries them; null otherwise.</summary>
    internal static PatternWaypoints? PatternWaypointsOf(Phase? phase) =>
        phase switch
        {
            UpwindPhase u => u.Waypoints,
            CrosswindPhase c => c.Waypoints,
            DownwindPhase d => d.Waypoints,
            BasePhase b => b.Waypoints,
            MidfieldCrossingPhase m => m.Waypoints,
            TeardropReentryPhase t => t.Waypoints,
            _ => null,
        };

    /// <summary>
    /// True when <paramref name="phase"/> is a pattern leg the controller has told
    /// the aircraft to EXTEND (<c>EXT</c>) — Downwind, Crosswind, or Upwind. An
    /// extended leg never self-completes, so the aircraft holds it until told to
    /// turn, deferring its place in the landing sequence.
    /// </summary>
    private static bool IsExtendedPatternLeg(Phase? phase) =>
        phase is DownwindPhase { IsExtended: true } or CrosswindPhase { IsExtended: true } or UpwindPhase { IsExtended: true };

    /// <summary>
    /// True when <paramref name="lead"/> is holding out on its current pattern leg rather
    /// than progressing, so a same-leg follower must sequence behind it. Covers a controller
    /// EXT on any pattern leg (<see cref="IsExtendedPatternLeg"/>) and — for the downwind — a
    /// lead still on the leg past its OWN base-turn point for any reason: its own follow-hold
    /// behind other traffic (a follow chain), a proximity hold, or an EXT that was cleared
    /// when the lead was itself told to follow. Recognizing the deferral by geometry rather
    /// than the fragile <c>IsExtended</c> flag keeps the follower sequenced whenever the lead
    /// is genuinely ahead. The base-turn test uses the lead's OWN downwind
    /// (<see cref="DownwindPhase.HasReachedBaseTurnPoint"/>), so a larger/faster lead that
    /// legitimately turns base farther out is judged on its own pattern.
    /// </summary>
    private static bool IsLeadHoldingSharedLeg(AircraftState lead)
    {
        Phase? phase = lead.Phases?.CurrentPhase;
        if (IsExtendedPatternLeg(phase))
        {
            return true;
        }
        return phase is DownwindPhase dw && dw.HasReachedBaseTurnPoint(lead.Position);
    }

    /// <summary>
    /// True when both aircraft are flying patterns to the same runway and the
    /// lead is ahead on a different leg (<see cref="IsLeadAheadAcrossLegs"/>: a LATER
    /// pattern leg than the follower, or for a follower on base or final less path
    /// to the threshold) — geographic gap growth
    /// during the follower's current leg is expected (e.g. follower on Downwind
    /// heading east, lead on Final heading west — they're on parallel-offset
    /// tracks pointing opposite directions, so the gap can only grow until the
    /// follower turns base). The follower still has a lateral option here (extend
    /// its leg, hold its base turn), so the "unable to maintain separation" cancel
    /// at minimum speed in <see cref="ComputeAdjustedSpeedWithDesired"/> is
    /// suppressed: the follower holds minimum speed and extends instead.
    ///
    /// <para>
    /// One same-leg case also counts as flow-ahead: a lead <em>holding out</em> on the
    /// shared leg (<see cref="IsLeadHoldingSharedLeg"/>) has deferred its progression, so it
    /// stays ahead in the landing sequence even though it shares the follower's leg index.
    /// Treating it as flow-ahead lets a downwind follower hold its base turn to sequence
    /// behind it and keeps the minimum-speed cancel suppressed while both run outbound.
    /// </para>
    /// </summary>
    internal static bool IsLeadPatternFlowAhead(AircraftState follower, AircraftState lead)
    {
        RunwayInfo? runway = follower.Phases?.AssignedRunway;
        string? leadRwy = lead.Phases?.AssignedRunway?.Designator;
        if ((runway is null) || (leadRwy is null) || !string.Equals(runway.Designator, leadRwy, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if ((PatternLegIndex(follower) is not { } followerLeg) || (PatternLegIndex(lead) is not { } leadLeg))
        {
            return false;
        }
        // Same-leg exception: a lead holding out on its current pattern leg has deferred its
        // progression, so it stays ahead in the landing sequence despite sharing the
        // follower's leg index. Without this a same-leg follower turns base at its fixed point
        // and rolls out ahead of the aircraft it was told to follow (AIM 4-3-5 broken
        // sequence), and the minimum-speed cancel drops the follow as the pair closes. "Holding out" is any deferral (see IsLeadHoldingSharedLeg) — a controller
        // EXT, the lead's own follow-hold behind other traffic, or a proximity hold — not just
        // the IsExtended flag, which is cleared the moment the lead is itself told to follow.
        // Generic same-leg cases are intentionally NOT flow-ahead: when both aircraft are on
        // parallel tracks heading the same way and the lead is not holding out, the follower has
        // no lateral option: a follower too close at minimum speed is cancelled.
        if (leadLeg == followerLeg)
        {
            return IsLeadHoldingSharedLeg(lead);
        }

        return IsLeadAheadAcrossLegs(follower, lead, followerLeg, leadLeg, runway);
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

        AircraftState? target = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (target is null)
        {
            // Leader disappeared — clear follow state, continue with normal speed
            Log.LogDebug("[Follow] {Callsign}: target {Target} no longer found, clearing follow", ctx.Aircraft.Callsign, targetCallsign);
            CancelFollowHoldingLeg(ctx.Aircraft);
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

        AircraftState? target = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (target is null)
        {
            Log.LogDebug("[Follow] {Callsign}: target {Target} no longer found, clearing follow", ctx.Aircraft.Callsign, targetCallsign);
            CancelFollowHoldingLeg(ctx.Aircraft);
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

        AircraftCategory leaderCategory = AircraftCategorization.Categorize(target.AircraftType);
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
    /// correction toward <paramref name="desiredNm"/>.
    ///
    /// Returns the adjusted speed, or <c>null</c> when separation cannot be
    /// maintained — in that case the helper has already added a one-shot warning
    /// to <see cref="AircraftState.PendingWarnings"/> and cleared
    /// <see cref="AircraftState.Approach.FollowingCallsign"/>; the caller MUST
    /// end its follow phase or the warning will fire again next tick.
    /// </summary>
    /// <param name="ctx">The follower's phase context.</param>
    /// <param name="lead">Lead aircraft.</param>
    /// <param name="minSpeed">Absolute floor — never returns below this.</param>
    /// <param name="desiredNm">Distance behind the lead the speed loop holds.</param>
    /// <param name="logger">Logger for warnings when separation cannot be maintained.</param>
    public static double? AdjustedFreeFlightSpeed(PhaseContext ctx, AircraftState lead, double minSpeed, double desiredNm, ILogger logger)
    {
        double normalSpeed = Math.Max(lead.IndicatedAirspeed, minSpeed);
        return ComputeAdjustedSpeedWithDesired(
            ctx.Aircraft,
            lead,
            normalSpeed,
            minSpeed,
            desiredNm,
            MaxSpeedAdjustKts,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
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
        AircraftCategory leaderCategory = AircraftCategorization.Categorize(lead.AircraftType);
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
        // separation by speed alone.
        if ((adjusted < minSpeed) && (distance < desired * 0.5))
        {
            // If the follower still has a lateral option — it is on an earlier pattern
            // leg than a flow-ahead lead on the same runway — do NOT cancel. Hold at min
            // speed and let the downwind extension / hold-base-turn logic build the
            // spacing; the lead-lifecycle watchdog releases the follow once the lead lands
            // or the geometry diverges. Cancelling here would clear FollowingCallsign,
            // drop the hold, and turn the follower base in front of the still-airborne
            // lead (7110.65 §3-10-3.a.1 same-runway separation; AIM §4-3-4.b.4 no cut-in).
            if (IsLeadPatternFlowAhead(follower, lead))
            {
                return minSpeed;
            }

            // No lateral option (free-flight follow, or a same/earlier-leg lead). Cancel
            // follow and warn once so the controller can intervene. Hold the present leg —
            // the pilot's only instruction was to follow, so it does not turn on its own.
            CancelFollowHoldingLeg(follower);
            Pilot.PilotResponder.RouteRpoTransmission(
                follower,
                soloTrainingMode,
                rpoShowPilotSpeech,
                Pilot.PilotResponder.BuildUnableToMaintainSeparation(follower, lead.Callsign)
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

        AircraftState? target = ctx.AircraftLookup?.Invoke(targetCallsign);
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

        // A lead on a later leg is on a reciprocal or perpendicular track (final vs downwind):
        // the straight-line gap to it is diverging geometry, not trail spacing. Its base-turn
        // hold is the projection in ShouldHoldForLeadSequencing, which owns that case.
        if (IsLeadPatternFlowAhead(ctx.Aircraft, target))
        {
            return false;
        }

        double distance = GeoMath.DistanceNm(ctx.Aircraft.Position, target.Position);

        AircraftCategory leaderCategory = AircraftCategorization.Categorize(target.AircraftType);
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
    /// Returns true when a downwind follower must keep extending (hold its base
    /// turn) to sequence behind a lead that is pattern-flow-AHEAD on the same runway
    /// (the lead is on a strictly later leg — Base/Final/Landing — while the follower
    /// is still on Downwind).
    ///
    /// <para>
    /// FOLLOW is a sequencing instruction (7110.65 §3-8-1 lists it beside EXTEND DOWNWIND as
    /// a separate instruction): the pilot who accepts it takes on the in-trail spacing (AIM
    /// §4-4-14.b, §5-5-12.a) and adjusts by extending rather than orbiting (AIM §4-3-5), and
    /// the traffic stops being a factor once it is in the landing phase (AIM §4-4-14.a.2
    /// NOTE) — not once it has cleared the runway. The follower therefore turns base when the
    /// traffic has passed and the spacing will work out — the pilot-side mirror of §3-10-6
    /// "anticipating separation", which judges the runway by where the aircraft will be when
    /// it crosses the threshold, not by where the two aircraft are right now. Comparing
    /// instantaneous positions ignores the base leg the follower still has to fly, during
    /// which a fast lead covers miles and lands; that is how a C172 ended up on a 3 nm final
    /// behind a Learjet that touched down a minute earlier.
    /// </para>
    ///
    /// <para>
    /// The turn is released only when all three hold:
    /// <list type="number">
    /// <item><description>The lead is aft of the follower's 3-9 line (body-frame relative
    /// bearing beyond ±90°, <see cref="IsFlowAheadLeadForwardOfWingline"/>), so the base leg
    /// is flown behind it — the cue a pilot actually uses; 14 CFR §91.113(g) forbids cutting
    /// in front of an aircraft on final (AIM §4-3-4.d restates it for non-towered fields).
    /// Judged in the follower's own frame rather than on the downwind axis, so a drifted or
    /// extended downwind does not distort it. This gate is never bypassed — a short approach
    /// (<c>SA</c>) waives the spacing projection below, not the prohibition on cutting in.
    /// </description></item>
    /// <item><description>Projecting both aircraft along their remaining pattern path
    /// (<see cref="RemainingPatternPathNm"/>) at their present ground speeds, the follower
    /// crosses the threshold at least <see cref="RunwayClearanceSeconds"/> (runway occupancy,
    /// threshold to clear) after the lead crosses it, so the runway is clear when the follower
    /// arrives (§3-10-3; when either aircraft is Category III the only permitted condition
    /// is "clear of the runway", §3-10-3.a.1).
    /// </description></item>
    /// <item><description>If the lead will still be airborne on final when the follower rolls
    /// out on final, the follower rolls out at least the category desired distance behind it
    /// (the in-trail spacing, applied to the projected geometry rather than the present one).
    /// </description></item>
    /// </list>
    /// The caller bounds the hold spatially with <see cref="MaxFollowExtensionNm"/>; the
    /// temporal bound (lead landed / despawned / lost from sight) lives in
    /// <see cref="CheckLeadLifecycle"/>, so the two caps are complementary.
    /// </para>
    /// </summary>
    public static bool ShouldHoldForLeadSequencing(PhaseContext ctx, PatternWaypoints wp)
    {
        AircraftState follower = ctx.Aircraft;
        AircraftState? lead = FlowAheadLead(ctx);
        if (lead is null)
        {
            return false;
        }

        if (IsFlowAheadLeadForwardOfWingline(ctx))
        {
            return true;
        }

        string targetCallsign = lead.Callsign;

        // Every projection term errs toward holding: the follower is projected at the faster of its
        // present and approach speeds along a path shortened by the two corners it will cut turning
        // base and final (so its ETA is never later than reality), and the lead at the slower of its
        // present and approach speeds (a decelerating lead's touchdown is never earlier than projected).
        AircraftCategory leadCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double followerNmPerSec = Math.Max(follower.GroundSpeed, AircraftPerformance.ApproachSpeed(follower.AircraftType, ctx.Category)) / 3600.0;
        double remainingFollowerNm = RemainingPatternPathNm(follower, wp) - PatternCornerCutNm(followerNmPerSec, ctx.Category, corners: 2);
        double remainingLeadNm = RemainingPatternPathNm(lead, wp);
        double leadNmPerSec = ProjectedLeadSpeedKts(lead, leadCategory) / 3600.0;
        double followerEtaSec = remainingFollowerNm / followerNmPerSec;
        double leadEtaSec = remainingLeadNm / leadNmPerSec;
        double clearanceSec = RunwayClearanceSeconds(leadCategory);

        if (followerEtaSec < leadEtaSec + clearanceSec)
        {
            Log.LogDebug(
                "[Follow] {Callsign}: holding downwind to sequence behind {Target} (followerEta={FE:F0}s leadEta={LE:F0}s clearance={C:F0}s)",
                follower.Callsign,
                targetCallsign,
                followerEtaSec,
                leadEtaSec,
                clearanceSec
            );
            return true;
        }

        // Where the lead will be when the follower rolls out on final: the follower's final leg
        // is its along-track from the threshold (longer than the nominal final if it has extended).
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        double followerAlongTrack = GeoMath.AlongTrackDistanceNm(follower.Position, threshold, wp.DownwindHeading);
        double baseTurnAlongTrack = GeoMath.AlongTrackDistanceNm(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), threshold, wp.DownwindHeading);
        double finalLengthNm = Math.Max(followerAlongTrack, baseTurnAlongTrack);
        double timeToRolloutSec = Math.Max(0, remainingFollowerNm - finalLengthNm) / followerNmPerSec;
        double leadRemainingAtRolloutNm = remainingLeadNm - (leadNmPerSec * timeToRolloutSec);
        double desired = DesiredDistanceForLeader(leadCategory);

        bool hold = (leadRemainingAtRolloutNm > 0) && ((finalLengthNm - leadRemainingAtRolloutNm) < desired);
        if (hold)
        {
            Log.LogDebug(
                "[Follow] {Callsign}: holding downwind to sequence behind {Target} (rollout final={F:F2}nm, "
                    + "lead would be {L:F2}nm out, desired={D:F1})",
                follower.Callsign,
                targetCallsign,
                finalLengthNm,
                leadRemainingAtRolloutNm,
                desired
            );
        }

        return hold;
    }

    /// <summary>
    /// The speed (kt) a lead's progress to the threshold is projected at: the slower of its present ground speed and its
    /// approach speed, so a decelerating lead's touchdown is never earlier than projected, floored at
    /// <see cref="MinProjectionSpeedKts"/> so a lead caught mid-flare or in a slow sample cannot project an infinite ETA.
    /// </summary>
    internal static double ProjectedLeadSpeedKts(AircraftState lead, AircraftCategory leadCategory)
    {
        double leadApproachKts = AircraftPerformance.ApproachSpeed(lead.AircraftType, leadCategory);
        return Math.Max(Math.Min(lead.GroundSpeed, leadApproachKts), MinProjectionSpeedKts);
    }

    /// <summary>
    /// The followed aircraft when it is pattern-flow-ahead of <paramref name="ctx"/>'s aircraft
    /// (<see cref="IsLeadPatternFlowAhead"/>: same runway, ahead on a different leg by
    /// <see cref="IsLeadAheadAcrossLegs"/>, or holding out on the shared leg); null when there is no
    /// follow, the lead is gone, or it trails in pattern flow — those are the speed / proximity
    /// paths' business, not the base-turn hold's.
    /// </summary>
    private static AircraftState? FlowAheadLead(PhaseContext ctx)
    {
        string? targetCallsign = ctx.Aircraft.Approach.FollowingCallsign;
        if (targetCallsign is null)
        {
            return null;
        }

        AircraftState? lead = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (lead is null)
        {
            return null;
        }

        return IsLeadPatternFlowAhead(ctx.Aircraft, lead) ? lead : null;
    }

    /// <summary>
    /// True while a pattern-flow-ahead lead is still forward of the follower's 3-9 line, i.e. a
    /// base turn now would be flown into it rather than behind it (14 CFR §91.113(g): never cut in
    /// front of an aircraft on final). The body-frame relative bearing carries a few degrees of
    /// margin (<see cref="LeadPassedRelativeBearingDeg"/>). <see cref="Pattern.DownwindPhase"/>
    /// applies this gate unconditionally — a short approach waives the spacing projection, not
    /// this.
    /// </summary>
    public static bool IsFlowAheadLeadForwardOfWingline(PhaseContext ctx)
    {
        AircraftState follower = ctx.Aircraft;
        AircraftState? lead = FlowAheadLead(ctx);
        if (lead is null)
        {
            return false;
        }

        double relativeBearing = GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, GeoMath.BearingTo(follower.Position, lead.Position));
        bool forward = Math.Abs(relativeBearing) <= LeadPassedRelativeBearingDeg;
        if (forward)
        {
            Log.LogDebug(
                "[Follow] {Callsign}: holding downwind, {Target} still forward of the 3-9 line (relative bearing {Rb:F0})",
                follower.Callsign,
                lead.Callsign,
                relativeBearing
            );
        }

        return forward;
    }

    /// <summary>
    /// Path a pattern aircraft saves by flying its 90° corners as turn arcs instead of the
    /// rectangular circuit <see cref="RemainingPatternPathNm"/> measures: each corner replaces two
    /// turn radii of straight track with a quarter circle, saving (2 − π/2)·r. The radius follows
    /// from the aircraft's speed and the category pattern turn rate.
    /// </summary>
    internal static double PatternCornerCutNm(double speedNmPerSec, AircraftCategory category, int corners)
    {
        double turnRateRadPerSec = CategoryPerformance.PatternTurnRate(category) * Math.PI / 180.0;
        double radiusNm = speedNmPerSec / turnRateRadPerSec;
        return corners * (2.0 - (Math.PI / 2.0)) * radiusNm;
    }

    /// <summary>
    /// Remaining distance to fly along the traffic pattern from an aircraft's current position to
    /// the landing threshold, in nautical miles, following the idealized rectangular circuit
    /// (upwind → crosswind → downwind → base → final). Monotonically decreases as the aircraft
    /// progresses toward landing, and correctly increases when a leg is EXTENDED (a longer upwind
    /// lengthens the downwind; a wider crosswind lengthens the base; a longer downwind lengthens
    /// the final). This is the sequence coordinate used to order two aircraft in the pattern — the
    /// one with more remaining path lands later — and, unlike a single along-track axis, it stays
    /// consistent across every leg (in particular the upwind leg, whose along-track runs opposite
    /// to the downwind sequence axis).
    ///
    /// <para>
    /// Evaluated against a single reference geometry <paramref name="wp"/> (the follower's own
    /// pattern waypoints) so two aircraft are compared on identical legs. A phase with no leg in
    /// <see cref="PatternLegIndex"/> returns <see cref="double.PositiveInfinity"/>; of the non-pattern
    /// phases, an instrument approach or any phase flown on the final by geometry is measured as the
    /// final, and a go-around that re-enters the pattern or a closed-traffic takeoff climb as the
    /// upwind (<see cref="SequenceLegIndex"/>).
    /// </para>
    /// </summary>
    public static double RemainingPatternPathNm(AircraftState ac, PatternWaypoints wp)
    {
        int? leg = PatternLegIndex(ac);
        if (leg is null)
        {
            return double.PositiveInfinity;
        }

        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        TrueHeading dwHeading = wp.DownwindHeading;

        double AlongTrack(LatLon p) => GeoMath.AlongTrackDistanceNm(p, threshold, dwHeading);
        double PerpOffset(LatLon p) => Math.Abs(GeoMath.SignedCrossTrackDistanceNm(p, threshold, dwHeading));

        // Fixed pattern reference distances along / across the downwind axis (threshold at 0,
        // positive toward the base turn). The DER (crosswind-turn) sits on the departure side at a
        // negative along-track; the base turn on the approach side at a positive along-track.
        double dDer = AlongTrack(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon));
        double dBase = AlongTrack(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon));
        double width = PerpOffset(new LatLon(wp.DownwindStartLat, wp.DownwindStartLon));

        double dwLen = dBase - dDer; // downwind leg length (DER-abeam to base turn)
        double finalLen = dBase; // final leg length (base turn along-track to threshold at 0)

        double dP = AlongTrack(ac.Position);
        double rhoP = PerpOffset(ac.Position);

        return leg switch
        {
            // Upwind: on the centerline, departure side. Remaining upwind to the DER (0 once past),
            // then crosswind + downwind (longer if extended past the DER) + base + final.
            1 => Math.Max(0, dP - dDer) + width + (dBase - Math.Min(dP, dDer)) + width + finalLen,
            // Crosswind: near the DER along-track, perpendicular 0→width. Remaining crosswind (0 if
            // extended wider) + downwind + base (wider if extended) + final.
            2 => Math.Max(0, width - rhoP) + dwLen + Math.Max(width, rhoP) + finalLen,
            // Downwind: at the downwind offset, along-track DER→base turn. Remaining downwind (0 if
            // extended past the base turn) + base (the aircraft's ACTUAL perpendicular offset, so a
            // wider downwind from a widened crosswind counts its longer base) + final (longer if
            // extended past the base turn).
            3 => Math.Max(0, dBase - dP) + rhoP + Math.Max(dP, dBase),
            // Base: near the base-turn along-track, perpendicular width→0. Remaining base + final.
            4 => rhoP + Math.Max(dP, 0),
            // Final: on the centerline, closing the threshold.
            _ => Math.Max(dP, GeoMath.DistanceNm(ac.Position, threshold)),
        };
    }

    /// <summary>
    /// Returns true when the follower must hold its CURRENT pattern leg (defer turning to the next
    /// leg) to stay in trail of the lead it is following, judged by remaining pattern path. The
    /// follower holds — extending its current leg — while its remaining path to the threshold is
    /// less than the lead's plus the category desired spacing, i.e. it would otherwise reach the
    /// landing before it is a full <c>desired</c> behind the lead. Used by
    /// <see cref="Pattern.UpwindPhase"/> and <see cref="Pattern.CrosswindPhase"/>.
    ///
    /// <para>
    /// Remaining path (<see cref="RemainingPatternPathNm"/>) is the correct sequence coordinate on
    /// every leg: extending the upwind lengthens the follower's path (it turns crosswind further
    /// out, flying a longer downwind), so the hold drives it the right way — unlike a single
    /// along-track axis, which runs backwards on the reciprocal-heading upwind leg. Because the
    /// lead is simultaneously shrinking its own remaining path, the follower converges to
    /// <c>desired</c> path behind without having to overtake the lead on the shared leg. The caller
    /// bounds this hold spatially with <see cref="MaxFollowExtensionNm"/> past the leg turn point.
    /// </para>
    /// </summary>
    public static bool ShouldHoldLegForRemainingPathSequencing(PhaseContext ctx, PatternWaypoints wp)
    {
        string? targetCallsign = ctx.Aircraft.Approach.FollowingCallsign;
        if (targetCallsign is null)
        {
            return false;
        }

        AircraftState? lead = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (lead is null)
        {
            return false;
        }

        string? followerRwy = ctx.Aircraft.Phases?.AssignedRunway?.Designator;
        string? leadRwy = lead.Phases?.AssignedRunway?.Designator;
        if ((followerRwy is null) || (leadRwy is null) || !string.Equals(followerRwy, leadRwy, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The lead must be on a pattern leg to have a finite remaining path; otherwise there is
        // nothing to sequence against (don't hold indefinitely against an infinite remaining).
        if (PatternLegIndex(lead) is null)
        {
            return false;
        }

        // Never extend a leg to fall in BEHIND traffic that is actually behind the follower in
        // pattern flow (lead on an earlier leg, or the same leg but further along). Mirrors the
        // guard on every other follow path — GetAdjustedSpeed, ShouldExtendDownwind — and prevents
        // the follower from flying a huge pattern to sequence behind e.g. a lead still on the
        // pattern-entry feeder (audit case: N172SP holding for a lead on PatternEntry behind it).
        if (IsLeadPatternFlowBehind(ctx.Aircraft, lead))
        {
            return false;
        }

        // The lead's remaining path is evaluated against the FOLLOWER's waypoints so the two are
        // compared on identical geometry; exact for same-runway same-category traffic, with a small
        // error if the lead flies a different-size pattern (jet lead / piston follower) — acceptable
        // for sequencing in a trainer.
        double remainingFollower = RemainingPatternPathNm(ctx.Aircraft, wp);
        double remainingLead = RemainingPatternPathNm(lead, wp);
        double desired = DesiredDistanceForLeader(AircraftCategorization.Categorize(lead.AircraftType));

        bool hold = remainingFollower < (remainingLead + desired);
        if (hold)
        {
            Log.LogDebug(
                "[Follow] {Callsign}: holding {Leg} to sequence behind {Target} (remFollower={RF:F2} remLead={RL:F2} desired={D:F1})",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.Phases?.CurrentPhase?.Name,
                targetCallsign,
                remainingFollower,
                remainingLead,
                desired
            );
        }

        return hold;
    }

    /// <summary>
    /// Returns true when a follower committed to a base or final leg can no longer
    /// keep clear of its lead and should break off the follow and go around rather than
    /// run into it — whether by overtaking from behind or cutting in front (AIM 4-3-3
    /// NOTE 1 prohibits both; AIM 5-5-12.a.2: notify ATC when unable to maintain visual
    /// separation). Fires only for a STRUCTURAL overtake — the follower's minimum
    /// approach speed (Vref) exceeds the lead's ground speed by more than
    /// <see cref="StructuralOvertakeMarginKts"/>, so the in-pattern speed reduction
    /// cannot open the gap — with the lead still airborne, the straight-line gap inside
    /// <see cref="FollowBreakOffGapNm"/>, and the two aircraft still closing. Recoverable
    /// (non-structural) follows return false and are left to the speed adjustment and the
    /// final-approach spacing S-turn.
    /// </summary>
    /// <param name="ctx">Current phase context (must have AircraftLookup set).</param>
    public static bool ShouldBreakOffFollowForSpacing(PhaseContext ctx)
    {
        string? targetCallsign = ctx.Aircraft.Approach.FollowingCallsign;
        if (targetCallsign is null)
        {
            return false;
        }

        AircraftState? lead = ctx.AircraftLookup?.Invoke(targetCallsign);
        if (lead is null || lead.IsOnGround)
        {
            return false;
        }

        if (!IsStructuralOvertake(ctx, lead))
        {
            return false;
        }

        double followerVref = FollowerVrefKts(ctx);

        double gap = GeoMath.DistanceNm(ctx.Aircraft.Position, lead.Position);
        if (gap >= FollowBreakOffGapNm)
        {
            return false;
        }

        // Only break off while the pair is still closing — once the follower has crossed
        // and is opening the gap, going around no longer helps. Direction-agnostic so it
        // catches both an in-trail overtake and a cut-in-front. Range rate = relative
        // velocity projected onto the follower→lead unit vector; negative ⇒ closing.
        if (!IsClosing(ctx.Aircraft, lead))
        {
            return false;
        }

        Log.LogDebug(
            "[Follow] {Callsign}: structural overtake of {Target} — gap={Gap:F2}nm (break-off floor {Floor:F1}nm), Vref {Vref:F0}kt vs lead IAS {LeadIas:F0}kt; breaking off + going around",
            ctx.Aircraft.Callsign,
            targetCallsign,
            gap,
            FollowBreakOffGapNm,
            followerVref,
            lead.IndicatedAirspeed
        );
        return true;
    }

    /// <summary>
    /// True when the follower's slowest sustainable approach speed still outruns the lead by more than
    /// <see cref="StructuralOvertakeMarginKts"/>: slowing down cannot keep it behind. Anything within the margin is recoverable
    /// by the speed adjustment or an S-turn. IAS is compared to IAS: both fly the same final into the same wind, so the airspeed
    /// difference is the wind-independent measure of "can I slow to the lead's speed" (the ground-frame closure is a separate
    /// check). The lead's instantaneous IAS includes the wind additive (it flies the same weather), so the follower's floor
    /// carries the gust part too, or the comparison is biased permissive by exactly the additive.
    /// </summary>
    internal static bool IsStructuralOvertake(PhaseContext ctx, AircraftState lead) =>
        FollowerVrefKts(ctx) > lead.IndicatedAirspeed + StructuralOvertakeMarginKts;

    private static double FollowerVrefKts(PhaseContext ctx) =>
        AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category) + AircraftPerformance.GustApproachAdditive(ctx.Weather);

    /// <summary>
    /// True when the straight-line range between two aircraft is decreasing this tick.
    /// Computes the range rate as the relative ground velocity projected onto the unit
    /// vector from <paramref name="follower"/> to <paramref name="lead"/>; a negative
    /// projection means the gap is shrinking.
    /// </summary>
    private static bool IsClosing(AircraftState follower, AircraftState lead)
    {
        const double degToRad = Math.PI / 180.0;
        double bearingRad = GeoMath.BearingTo(follower.Position, lead.Position) * degToRad;
        double uEast = Math.Sin(bearingRad);
        double uNorth = Math.Cos(bearingRad);

        double fTrack = follower.TrueTrack.Degrees * degToRad;
        double lTrack = lead.TrueTrack.Degrees * degToRad;
        double relEast = (lead.GroundSpeed * Math.Sin(lTrack)) - (follower.GroundSpeed * Math.Sin(fTrack));
        double relNorth = (lead.GroundSpeed * Math.Cos(lTrack)) - (follower.GroundSpeed * Math.Cos(fTrack));

        double rangeRate = (relEast * uEast) + (relNorth * uNorth);
        return rangeRate < 0;
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
    /// Farthest (nm) off a runway's extended centerline a lead may be and still count as on its final
    /// (<see cref="IsOnFinalByGeometry"/>).
    /// </summary>
    public const double OnFinalMaxCrossTrackNm = 0.5;

    /// <summary>Largest angle (deg) between a lead's track and a runway's heading for the lead to count as on its final.</summary>
    public const double OnFinalMaxTrackOffDeg = 30.0;

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are the same runway end at the same airport.</summary>
    internal static bool IsSameRunway(RunwayInfo a, RunwayInfo b) =>
        Data.NavigationDatabase.AirportIdsMatch(a.AirportId, b.AirportId)
        && string.Equals(a.Designator, b.Designator, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How far (nm) <paramref name="position"/> is out along <paramref name="runway"/>'s extended centerline from its threshold
    /// (negative past it).
    /// </summary>
    internal static double AlongFinalNm(LatLon position, RunwayInfo runway) =>
        GeoMath.AlongTrackDistanceNm(position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading.ToReciprocal());

    /// <summary>
    /// The lead's remaining path (nm) to <paramref name="runway"/>'s threshold: its pattern path on base
    /// (<see cref="RemainingPatternPathNm"/>), else its distance out along the final (never below zero).
    /// </summary>
    internal static double LeadRemainingPathNm(AircraftState lead, RunwayInfo runway) =>
        lead.Phases?.CurrentPhase is BasePhase { Waypoints: { } waypoints }
            ? RemainingPatternPathNm(lead, waypoints)
            : Math.Max(0.0, AlongFinalNm(lead.Position, runway));

    /// <summary>
    /// True when <paramref name="lead"/> is on <paramref name="runway"/>'s final by geometry, whatever phase flies it (a
    /// pattern final, an instrument approach's fix sequence, a course intercept): airborne, landing that runway, on the approach
    /// side of its threshold, within <see cref="OnFinalMaxCrossTrackNm"/> of the extended centerline and tracking within
    /// <see cref="OnFinalMaxTrackOffDeg"/> of the runway heading. A lead going around is leaving the final, wherever it is.
    /// </summary>
    internal static bool IsOnFinalByGeometry(AircraftState lead, RunwayInfo runway)
    {
        if (lead.IsOnGround || (lead.Phases is not { AssignedRunway: { } leadRunway } phases) || (phases.CurrentPhase is GoAroundPhase))
        {
            return false;
        }

        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        return IsSameRunway(leadRunway, runway)
            && (AlongFinalNm(lead.Position, runway) > 0.0)
            && (Math.Abs(GeoMath.SignedCrossTrackDistanceNm(lead.Position, threshold, runway.TrueHeading)) <= OnFinalMaxCrossTrackNm)
            && (lead.TrueTrack.AbsAngleTo(runway.TrueHeading) <= OnFinalMaxTrackOffDeg);
    }

    /// <summary>
    /// Spacing behind a lead in the pattern: the pattern spacing (<see cref="DesiredDistanceForLeader"/>), or the on-approach
    /// wake-turbulence minimum behind a heavier lead when that is more.
    /// </summary>
    internal static double PatternSpacingNm(PhaseContext ctx, AircraftState lead)
    {
        AircraftCategory leadCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double wakeNm = WakeTurbulenceData.OnApproachWakeSeparationNm(lead.AircraftType, leadCategory, ctx.AircraftType, ctx.Category);
        return Math.Max(DesiredDistanceForLeader(leadCategory), wakeNm);
    }

    /// <summary>
    /// Runway occupancy allowance for a landing lead of the given category: seconds from its
    /// threshold crossing until it is clear of the runway (see the constants' note: a simulation
    /// allowance anchored to 7110.65 §5-5-4.j.2's 50 s average, not a published per-category
    /// value). A follower projects its own threshold crossing at least this long after the
    /// lead's before turning base (§3-10-3).
    /// </summary>
    public static double RunwayClearanceSeconds(AircraftCategory leadCategory)
    {
        return leadCategory switch
        {
            AircraftCategory.Jet => RunwayClearanceSecondsJet,
            AircraftCategory.Turboprop => RunwayClearanceSecondsTurboprop,
            AircraftCategory.Piston => RunwayClearanceSecondsLight,
            AircraftCategory.Helicopter => RunwayClearanceSecondsLight,
            _ => RunwayClearanceSecondsTurboprop,
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

    /// <summary>
    /// Computes the free-pursuit target heading for a follower keeping trail behind its lead (AIM 5-5-12.a.1 "maneuver as
    /// necessary to maintain in-trail separation"; AIM 4-4-14.b). The gap is measured along the lead's recorded
    /// <paramref name="path"/>:
    /// <list type="bullet">
    /// <item><description><b>Spaced</b>: the nose points at the lead while the path from the follower to the lead is one
    /// straight leg; through the lead's turns the follower flies the lead's ground track and turns where it turned, rather
    /// than cutting the corner as pure pursuit would.</description></item>
    /// <item><description><b>Too close</b> (gap below the desired distance): a shallow S-turn (AIM 4-3-5) off the lead's
    /// track, <see cref="TrailExcursionDeg"/> (<see cref="TrailWideExcursionDeg"/> when well short), to the pattern's outside
    /// when there is a pattern, until the gap reaches desired + <see cref="TrailGapHysteresisNm"/>. At the offset cap it
    /// holds parallel; once the lead has turned base the reference is the leg
    /// it flew into that turn, so the follower extends that leg instead of turning with the lead.</description></item>
    /// </list>
    /// Degrades to pure pursuit when the lead's ground speed is too low for a reliable ground track. <paramref name="widen"/>
    /// carries the excursion's hysteresis and side across ticks.
    /// </summary>
    public static TrueHeading ComputeFreePursuitHeading(
        AircraftState follower,
        AircraftState lead,
        FreePursuitSpacing spacing,
        LeadPathTrail path,
        FollowWidenState widen
    )
    {
        // Lead nearly stopped / just airborne: its ground track is unreliable. Point at the
        // lead's current position until it is moving.
        if (lead.GroundSpeed < TrailMinLeadGroundSpeedKt)
        {
            widen.Active = false;
            return new TrueHeading(GeoMath.BearingTo(follower.Position, lead.Position));
        }

        LeadPathProjection projection = path.Project(follower.Position, lead.Position, lead.TrueTrack);
        ExcursionReference reference = ExcursionReferenceFor(follower.Position, spacing, projection);
        UpdateExcursion(follower, spacing, reference, projection.OffPathNm, widen);
        return widen.Active
            ? ExcursionSteer(spacing, reference, widen)
            : ChainHeading(follower, lead, spacing.Excursion.OffsetCapNm, path, projection);
    }

    /// <summary>
    /// What an excursion steers against: the leg the lead flew into its base turn while the follower extends it (its gap then
    /// the lead's path from its base start plus the extension), otherwise the lead's path at the follower (the gap along it).
    /// </summary>
    private static ExcursionReference ExcursionReferenceFor(LatLon position, FreePursuitSpacing spacing, LeadPathProjection projection) =>
        spacing.ExtendedLeg is { } leg
            ? new ExcursionReference(leg.Track, GeoMath.SignedCrossTrackDistanceNm(position, leg.StartPoint, leg.Track), leg.GapNm)
            : new ExcursionReference(projection.Track, projection.CrossNm, projection.GapNm);

    /// <summary>
    /// Starts a spacing excursion once the gap falls below the desired distance, latching its side against the same reference
    /// track the excursion steers by (<see cref="ExcursionSide"/>), and ends it once the gap reaches desired +
    /// <see cref="TrailGapHysteresisNm"/>. With no side open (a parallel final either way) no excursion starts: speed alone
    /// builds the spacing.
    /// </summary>
    private static void UpdateExcursion(
        AircraftState follower,
        FreePursuitSpacing spacing,
        ExcursionReference reference,
        double offPathNm,
        FollowWidenState widen
    )
    {
        double goalNm = spacing.DesiredNm + TrailGapHysteresisNm;
        if (!widen.Active && (reference.GapNm < spacing.DesiredNm))
        {
            if (ExcursionSide(follower.Position, spacing, reference) is not { } side)
            {
                return;
            }

            widen.Side = side;
            widen.Active = true;
            Log.LogDebug(
                "[Follow] {Callsign}: spacing excursion {Side} of the lead's track, gap {Gap:F2} nm along its path, goal {Goal:F2} nm",
                follower.Callsign,
                widen.Side > 0 ? "right" : "left",
                reference.GapNm,
                goalNm
            );
        }
        else if (widen.Active && (reference.GapNm >= goalNm))
        {
            widen.Active = false;
            Log.LogDebug(
                "[Follow] {Callsign}: spacing excursion ended, gap {Gap:F2} nm along the lead's path, {Off:F2} nm off it",
                follower.Callsign,
                reference.GapNm,
                offPathNm
            );
        }
    }

    /// <summary>
    /// The side (+1 right of the reference track, -1 left) a new excursion takes, or null when none may. With a circuit, the
    /// pattern's outside (<see cref="CircuitExcursionSide"/>); without one, the side the follower already sits on, or the other
    /// side when that one holds a parallel final. Any side whose excursion would come within the offset cap +
    /// <see cref="TrailParallelFinalMarginNm"/> of a parallel runway's extended centerline is closed.
    /// </summary>
    private static int? ExcursionSide(LatLon position, FreePursuitSpacing spacing, ExcursionReference reference)
    {
        int preferred = spacing.Circuit is { } circuit
            ? CircuitExcursionSide(position, reference.Track, circuit, spacing.Excursion.OffsetCapNm)
            : (reference.CrossNm >= 0 ? 1 : -1);
        if (spacing.ParallelsOf is not { } runway)
        {
            return preferred;
        }

        double reachNm = spacing.Excursion.OffsetCapNm + TrailParallelFinalMarginNm;
        if (!Pattern.VfrFollowPhase.ExcursionMeetsParallelFinal(position, reference.Track, preferred, runway, reachNm))
        {
            return preferred;
        }

        bool otherSideOpen =
            (spacing.Circuit is null) && !Pattern.VfrFollowPhase.ExcursionMeetsParallelFinal(position, reference.Track, -preferred, runway, reachNm);
        return otherSideOpen ? -preferred : null;
    }

    /// <summary>
    /// The pattern's outside (<see cref="PatternOutsideWidenSide"/>), except for a follower on the non-pattern side of the
    /// centerline within <paramref name="offsetCapNm"/> of it: that one turns away from the final rather than across it.
    /// </summary>
    private static int CircuitExcursionSide(LatLon position, TrueHeading refTrack, FollowCircuit circuit, double offsetCapNm)
    {
        int side = PatternOutsideWidenSide(position, refTrack, circuit.Runway, circuit.Direction);
        RunwayInfo runway = circuit.Runway;
        double patternSign = circuit.Direction == PatternDirection.Right ? 1.0 : -1.0;
        double patternSideNm =
            patternSign
            * GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);
        if ((patternSideNm >= 0) || (-patternSideNm > offsetCapNm))
        {
            return side;
        }

        TrueHeading patternSide = runway.TrueHeading + (90.0 * patternSign);
        double towardPatternSide = Math.Cos((refTrack + (90.0 * side)).AbsAngleTo(patternSide) * Math.PI / 180.0);
        return towardPatternSide > OutsideSideTieCosine ? -side : side;
    }

    /// <summary>
    /// The excursion heading: off the reference track (the lead's path at the follower, or the leg the lead flew into its
    /// base turn) to the latched side, parallel once the follower's offset from that reference reaches the cap. A piston or
    /// helicopter well short of its goal turns <see cref="TrailWideExcursionDeg"/>; otherwise <see cref="TrailExcursionDeg"/>.
    /// </summary>
    private static TrueHeading ExcursionSteer(FreePursuitSpacing spacing, ExcursionReference reference, FollowWidenState widen)
    {
        if ((widen.Side * reference.CrossNm) >= spacing.Excursion.OffsetCapNm)
        {
            return reference.Track;
        }

        double shortfallNm = spacing.DesiredNm + TrailGapHysteresisNm - reference.GapNm;
        double offDeg = shortfallNm > TrailWideExcursionShortfallNm ? TrailWideExcursionDeg : TrailExcursionDeg;
        return reference.Track + (widen.Side * Math.Min(offDeg, spacing.Excursion.MaxOffTrackDeg));
    }

    /// <summary>The track an excursion steers against, the follower's signed offset from it, and the gap it measures.</summary>
    private readonly record struct ExcursionReference(TrueHeading Track, double CrossNm, double GapNm);

    /// <summary>
    /// Following in a chain: the nose on the lead while the lead's path from the follower is one straight leg (or the
    /// follower is farther off that path than the excursion cap, where the path means nothing to it); otherwise a point
    /// on the path a short way ahead, so the follower flies the lead's turns where the lead flew them.
    /// </summary>
    private static TrueHeading ChainHeading(
        AircraftState follower,
        AircraftState lead,
        double offsetCapNm,
        LeadPathTrail path,
        LeadPathProjection projection
    )
    {
        if ((projection.OffPathNm > offsetCapNm) || path.IsStraightToLead(projection, lead.Position, lead.TrueTrack, TrailStraightPathToleranceDeg))
        {
            return new TrueHeading(GeoMath.BearingTo(follower.Position, lead.Position));
        }

        // Look at least as far ahead as the follower is off the path, so the capture back onto it stays within 45°.
        double lookAheadNm = Math.Max(
            Math.Max(TrailMinPathLookAheadNm, follower.GroundSpeed * TrailPathLookAheadSeconds / 3600.0),
            projection.OffPathNm
        );
        return new TrueHeading(GeoMath.BearingTo(follower.Position, path.PointAhead(projection, lead.Position, lookAheadNm)));
    }

    /// <summary>
    /// The excursion side (+1 right of <paramref name="refTrack"/>, -1 left) that takes a follower at
    /// <paramref name="position"/> to the outside of <paramref name="runway"/>'s <paramref name="direction"/> pattern: away
    /// from the runway and, above all, toward the pattern side, so never toward the final or a parallel's final beyond it. On the
    /// extended centerline that is the pattern side (right of the landing direction for right traffic); on a downwind or a
    /// base it is away from the field.
    /// </summary>
    public static int PatternOutsideWidenSide(LatLon position, TrueHeading refTrack, RunwayInfo runway, PatternDirection direction)
    {
        TrueHeading patternSide = direction == PatternDirection.Right ? runway.TrueHeading + 90.0 : runway.TrueHeading - 90.0;
        TrueHeading right = refTrack + 90.0;
        double rightTowardPatternSide = Math.Cos(right.AbsAngleTo(patternSide) * Math.PI / 180.0);
        if (Math.Abs(rightTowardPatternSide) > OutsideSideTieCosine)
        {
            return rightTowardPatternSide > 0 ? 1 : -1;
        }

        // Flying across the final's direction (a base, a base entry): both sides are level with the centerline, so the
        // outside is the side away from the runway.
        LatLon nearest = NearestOnRunway(position, runway);
        var awayFromRunway = new TrueHeading(GeoMath.BearingTo(nearest, position));
        return right.AbsAngleTo(awayFromRunway) <= 90.0 ? 1 : -1;
    }

    /// <summary>
    /// Below this cosine (about 15° either side of level) an excursion side counts as neither toward the pattern side nor
    /// toward the centerline, and <see cref="PatternOutsideWidenSide"/> picks the side away from the runway instead.
    /// </summary>
    public const double OutsideSideTieCosine = 0.26;

    /// <summary>The point on <paramref name="runway"/>'s centerline, between its two ends, nearest <paramref name="position"/>.</summary>
    private static LatLon NearestOnRunway(LatLon position, RunwayInfo runway)
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        double lengthNm = GeoMath.DistanceNm(end1, end2);
        var along = new TrueHeading(GeoMath.BearingTo(end1, end2));
        double alongNm = Math.Clamp(GeoMath.AlongTrackDistanceNm(position, end1, along), 0.0, lengthNm);
        return GeoMath.ProjectPoint(end1, along, alongNm);
    }
}

/// <summary>
/// What a free pursuit steers to (<see cref="AirborneFollowHelper.ComputeFreePursuitHeading"/>): the desired gap behind the
/// lead along its path, how far a spacing excursion may go, the circuit whose outside the excursion takes (null when there is
/// no pattern), the leg the lead flew into its base turn while the follower is extending it past that turn (null
/// otherwise), and the runway whose parallel finals the excursion keeps clear of (null when none is known).
/// </summary>
public sealed record FreePursuitSpacing(
    double DesiredNm,
    FollowExcursionLimits Excursion,
    FollowCircuit? Circuit,
    FollowExtendedLeg? ExtendedLeg,
    RunwayInfo? ParallelsOf
);

/// <summary>How far off the lead's path (nm) a spacing excursion may reach, and the most it turns off the lead's track (deg).</summary>
public sealed record FollowExcursionLimits(double OffsetCapNm, double MaxOffTrackDeg);

/// <summary>A traffic pattern: the runway and the direction of its circuit.</summary>
public sealed record FollowCircuit(RunwayInfo Runway, PatternDirection Direction);

/// <summary>
/// The leg a lead flew into its base turn, which the follower is extending past that turn: the point the lead's base began,
/// the track it arrived on, and the follower's gap (nm): the lead's path from that point plus the follower's extension.
/// </summary>
public sealed record FollowExtendedLeg(LatLon StartPoint, TrueHeading Track, double GapNm);

/// <summary>
/// Hysteresis state for the free-pursuit spacing excursion (<see cref="AirborneFollowHelper.ComputeFreePursuitHeading"/>).
/// Lives on the follow phase (and is serialized) so the S-turn does not chatter on/off across ticks.
/// </summary>
public sealed class FollowWidenState
{
    /// <summary>True while a spacing excursion is in progress.</summary>
    public bool Active { get; set; }

    /// <summary>Excursion side: +1 = right of the lead's track, -1 = left.</summary>
    public int Side { get; set; }
}

/// <summary>Where a follower sits against its lead's recorded path (<see cref="LeadPathTrail.Project"/>).</summary>
/// <param name="Segment">Index of the path segment nearest the follower (the last segment ends at the lead).</param>
/// <param name="AlongSegmentNm">How far along that segment the follower's foot lies (negative before the path's start).</param>
/// <param name="GapNm">Distance along the path from the follower's foot to the lead (negative when past the lead).</param>
/// <param name="CrossNm">Signed offset from the path, right of its direction positive.</param>
/// <param name="OffPathNm">Distance from the path.</param>
/// <param name="Track">The path's direction at the foot.</param>
public readonly record struct LeadPathProjection(
    int Segment,
    double AlongSegmentNm,
    double GapNm,
    double CrossNm,
    double OffPathNm,
    TrueHeading Track
);

/// <summary>
/// The lead's recent ground track as the follower saw it: a breadcrumb of the lead's positions, one every
/// <see cref="SampleSpacingNm"/>, holding the last <see cref="MaxLengthNm"/> of path. The follower measures its gap along
/// this path and flies the lead's turns from it. The lead's present position is always the path's last point.
/// </summary>
public sealed class LeadPathTrail
{
    /// <summary>Least distance (nm) the lead moves between breadcrumbs.</summary>
    public const double SampleSpacingNm = 0.05;

    /// <summary>Length of path (nm) the breadcrumb keeps behind the lead.</summary>
    public const double MaxLengthNm = 4.0;

    /// <summary>Segments shorter than this (nm) carry no direction and are skipped.</summary>
    private const double MinSegmentNm = 1e-4;

    /// <summary>Segments shorter than this (nm) are too short to judge the path's direction by.</summary>
    private const double MinDirectionSegmentNm = 0.02;

    private readonly List<LatLon> _points = [];

    /// <summary>The recorded breadcrumbs, oldest first.</summary>
    public IReadOnlyList<LatLon> Points => _points;

    /// <summary>Record the lead's position when it has moved at least <see cref="SampleSpacingNm"/> from the last breadcrumb.</summary>
    public void Record(LatLon leadPosition)
    {
        if ((_points.Count > 0) && (GeoMath.DistanceNm(_points[^1], leadPosition) < SampleSpacingNm))
        {
            return;
        }

        _points.Add(leadPosition);
        TrimToMaxLength();
    }

    private void TrimToMaxLength()
    {
        double lengthNm = 0;
        for (int i = _points.Count - 1; i > 0; i--)
        {
            lengthNm += GeoMath.DistanceNm(_points[i - 1], _points[i]);
            if (lengthNm > MaxLengthNm)
            {
                _points.RemoveRange(0, i - 1);
                return;
            }
        }
    }

    /// <summary>Forget the whole path (the follow now pursues another lead).</summary>
    public void Clear() => _points.Clear();

    /// <summary>
    /// How many vertices the path has: the breadcrumbs, then the lead's present position as a last vertex unless the last
    /// breadcrumb already sits on it.
    /// </summary>
    private int VertexCount(LatLon leadPosition) =>
        (_points.Count == 0) || (GeoMath.DistanceNm(_points[^1], leadPosition) > MinSegmentNm) ? _points.Count + 1 : _points.Count;

    /// <summary>Vertex <paramref name="index"/> of the path: a breadcrumb, or the lead's present position past the last one.</summary>
    private LatLon Vertex(int index, LatLon leadPosition) => index < _points.Count ? _points[index] : leadPosition;

    /// <summary>
    /// Where <paramref name="position"/> sits against the path: its foot on the nearest segment, the gap along the path from
    /// there to the lead, and its offset. With no breadcrumb yet, the lead's present <paramref name="leadTrack"/> through its
    /// position stands in for the path.
    /// </summary>
    public LeadPathProjection Project(LatLon position, LatLon leadPosition, TrueHeading leadTrack)
    {
        int vertexCount = VertexCount(leadPosition);
        if (vertexCount < 2)
        {
            double crossNm = GeoMath.SignedCrossTrackDistanceNm(position, leadPosition, leadTrack);
            return new LeadPathProjection(
                Segment: 0,
                AlongSegmentNm: 0,
                GapNm: -GeoMath.AlongTrackDistanceNm(position, leadPosition, leadTrack),
                CrossNm: crossNm,
                OffPathNm: Math.Abs(crossNm),
                Track: leadTrack
            );
        }

        LeadPathProjection best = default;
        double bestOffNm = double.MaxValue;
        for (int i = 0; i < vertexCount - 1; i++)
        {
            if (
                (SegmentFoot(position, Vertex(i, leadPosition), Vertex(i + 1, leadPosition), i, vertexCount - 1) is { } foot)
                && (foot.OffPathNm < bestOffNm)
            )
            {
                best = foot;
                bestOffNm = foot.OffPathNm;
            }
        }

        double remainingNm = 0;
        for (int i = best.Segment + 1; i < vertexCount - 1; i++)
        {
            remainingNm += GeoMath.DistanceNm(Vertex(i, leadPosition), Vertex(i + 1, leadPosition));
        }
        return best with { GapNm = best.GapNm + remainingNm };
    }

    /// <summary>
    /// The foot of <paramref name="position"/> on segment <paramref name="index"/> (<paramref name="from"/> to
    /// <paramref name="to"/>, one of <paramref name="segmentCount"/>), with the gap to that segment's end. The first segment
    /// extends backward and the last forward, so a follower behind the path's start or past the lead still projects onto it.
    /// Null for a segment too short to have a direction.
    /// </summary>
    private static LeadPathProjection? SegmentFoot(LatLon position, LatLon from, LatLon to, int index, int segmentCount)
    {
        double lengthNm = GeoMath.DistanceNm(from, to);
        if (lengthNm < MinSegmentNm)
        {
            return null;
        }

        var track = new TrueHeading(GeoMath.BearingTo(from, to));
        double alongNm = GeoMath.AlongTrackDistanceNm(position, from, track);
        double crossNm = GeoMath.SignedCrossTrackDistanceNm(position, from, track);
        double lowNm = index == 0 ? double.NegativeInfinity : 0.0;
        double highNm = index == segmentCount - 1 ? double.PositiveInfinity : lengthNm;
        double footNm = Math.Clamp(alongNm, lowNm, highNm);
        double offNm = footNm == alongNm ? Math.Abs(crossNm) : GeoMath.DistanceNm(position, GeoMath.ProjectPoint(from, track, footNm));
        return new LeadPathProjection(
            Segment: index,
            AlongSegmentNm: footNm,
            GapNm: lengthNm - footNm,
            CrossNm: crossNm,
            OffPathNm: offNm,
            Track: track
        );
    }

    /// <summary>
    /// True when every stretch of the path from the follower's foot to the lead runs within <paramref name="toleranceDeg"/>
    /// of the lead's present <paramref name="leadTrack"/>: the lead is on one straight leg with no turn between them.
    /// </summary>
    public bool IsStraightToLead(LeadPathProjection projection, LatLon leadPosition, TrueHeading leadTrack, double toleranceDeg)
    {
        int vertexCount = VertexCount(leadPosition);
        for (int i = Math.Max(projection.Segment, 0); i < vertexCount - 1; i++)
        {
            LatLon from = Vertex(i, leadPosition);
            LatLon to = Vertex(i + 1, leadPosition);
            if (GeoMath.DistanceNm(from, to) < MinDirectionSegmentNm)
            {
                continue;
            }
            if (new TrueHeading(GeoMath.BearingTo(from, to)).AbsAngleTo(leadTrack) > toleranceDeg)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The point <paramref name="aheadNm"/> along the path past the follower's foot, or the lead when the path ends first.</summary>
    public LatLon PointAhead(LeadPathProjection projection, LatLon leadPosition, double aheadNm)
    {
        int vertexCount = VertexCount(leadPosition);
        double remainingNm = aheadNm;
        double fromNm = Math.Max(projection.AlongSegmentNm, 0.0);
        for (int i = projection.Segment; i < vertexCount - 1; i++)
        {
            LatLon from = Vertex(i, leadPosition);
            LatLon to = Vertex(i + 1, leadPosition);
            double lengthNm = GeoMath.DistanceNm(from, to);
            if (fromNm + remainingNm <= lengthNm)
            {
                return GeoMath.ProjectPoint(from, new TrueHeading(GeoMath.BearingTo(from, to)), fromNm + remainingNm);
            }
            remainingNm -= Math.Max(lengthNm - fromNm, 0.0);
            fromNm = 0.0;
        }
        return leadPosition;
    }

    /// <summary>
    /// Length of the path (nm) from the breadcrumb nearest <paramref name="point"/> to the lead at
    /// <paramref name="leadPosition"/>; the straight distance when the path is empty.
    /// </summary>
    public double LengthFromNm(LatLon point, LatLon leadPosition)
    {
        int nearest = NearestIndex(point);
        if (nearest < 0)
        {
            return GeoMath.DistanceNm(point, leadPosition);
        }

        int vertexCount = VertexCount(leadPosition);
        double lengthNm = 0;
        for (int i = nearest; i < vertexCount - 1; i++)
        {
            lengthNm += GeoMath.DistanceNm(Vertex(i, leadPosition), Vertex(i + 1, leadPosition));
        }
        return lengthNm;
    }

    /// <summary>Index of the breadcrumb nearest <paramref name="point"/>, or -1 when there is none.</summary>
    private int NearestIndex(LatLon point)
    {
        int nearest = -1;
        double nearestNm = double.MaxValue;
        for (int i = 0; i < _points.Count; i++)
        {
            double distNm = GeoMath.DistanceNm(_points[i], point);
            if (distNm < nearestNm)
            {
                nearest = i;
                nearestNm = distNm;
            }
        }
        return nearest;
    }

    /// <summary>
    /// The track the lead flew into <paramref name="point"/>: the bearing to it from the breadcrumb about
    /// <paramref name="backNm"/> of path before the breadcrumb nearest it. Null when the breadcrumb does not reach that far back.
    /// </summary>
    public TrueHeading? TrackInto(LatLon point, double backNm)
    {
        int nearest = NearestIndex(point);
        double walkedNm = 0;
        for (int i = nearest; i > 0; i--)
        {
            walkedNm += GeoMath.DistanceNm(_points[i - 1], _points[i]);
            if (walkedNm >= backNm)
            {
                return new TrueHeading(GeoMath.BearingTo(_points[i - 1], point));
            }
        }
        return null;
    }

    /// <summary>The breadcrumbs as a flat [lat, lon, lat, lon, …] array for a snapshot; null when empty.</summary>
    public double[]? ToSnapshot()
    {
        if (_points.Count == 0)
        {
            return null;
        }

        double[] flat = new double[_points.Count * 2];
        for (int i = 0; i < _points.Count; i++)
        {
            flat[2 * i] = _points[i].Lat;
            flat[(2 * i) + 1] = _points[i].Lon;
        }
        return flat;
    }

    /// <summary>Restore the breadcrumbs from <see cref="ToSnapshot"/>'s flat array (null or empty leaves the path empty).</summary>
    public void RestoreSnapshot(double[]? flat)
    {
        _points.Clear();
        if (flat is null)
        {
            return;
        }
        for (int i = 0; i + 1 < flat.Length; i += 2)
        {
            _points.Add(new LatLon(flat[i], flat[i + 1]));
        }
    }
}
