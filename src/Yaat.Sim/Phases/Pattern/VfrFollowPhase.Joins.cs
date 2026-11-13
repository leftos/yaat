using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases.Pattern;

public sealed partial class VfrFollowPhase
{
    /// <summary>
    /// Lead-landed sequencing: if the traffic we were following has landed and we
    /// know its runway, follow it onto that runway's final to await a landing
    /// clearance — rather than cancelling the follow and free-flying level over the
    /// field. Runs before CheckLeadLifecycle, which would otherwise cancel here.
    /// The final join's geometry gates apply (<see cref="CanSequenceOntoFinal"/>), all but the in-trail gate (the lead
    /// is down, so spacing is moot): never from beyond the threshold, never at a steep or wide intercept, never across a
    /// parallel's final (AIM §4-3-3 FIG 4-3-3 note 7). When a gate refuses, fall through to CheckLeadLifecycle, which ends
    /// the follow (lead on the ground); the follower then re-enters the pattern for the landed runway
    /// (<see cref="LifecycleReturn"/>).
    /// </summary>
    private bool TrySequenceBehindLandedLead(PhaseContext ctx, AircraftState? lead)
    {
        if ((lead is not { IsOnGround: true }) || (_leadLandingRunway is not { } landedRunway))
        {
            return false;
        }

        if (!CanSequenceOntoFinal(ctx.Aircraft, landedRunway))
        {
            Log.LogDebug(
                "[VfrFollow] {Callsign}: {Lead} landed, but no sane join of the {Rwy} final from here; re-entering the pattern",
                ctx.Aircraft.Callsign,
                TargetCallsign,
                landedRunway.Designator
            );
            return false;
        }

        SequenceOntoFinal(ctx, landedRunway);
        return true;
    }

    /// <summary>
    /// The final join's geometry gates without its in-trail gate: the follower is on the approach side of
    /// <paramref name="runway"/>'s threshold at least <see cref="MinFinalJoinDistNm"/> out, within
    /// <see cref="MaxFinalJoinCrossTrackNm"/> of the extended centerline, tracking within
    /// <see cref="MaxFinalJoinInterceptDeg"/> of the final course, and its capture path crosses no parallel runway's final.
    /// The landed-lead sequence applies them alone; <see cref="TryJoinLeadFinal"/> applies them after its in-trail gate.
    /// </summary>
    internal static bool CanSequenceOntoFinal(AircraftState follower, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double alongNm = AirborneFollowHelper.AlongFinalNm(follower.Position, runway);
        double crossTrackNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(follower.Position, threshold, runway.TrueHeading));
        return (alongNm >= MinFinalJoinDistNm)
            && (crossTrackNm <= MaxFinalJoinCrossTrackNm)
            && (follower.TrueTrack.AbsAngleTo(runway.TrueHeading) <= MaxFinalJoinInterceptDeg)
            && !JoinCapturePathCrossesParallelFinal(follower.Position, runway);
    }

    /// <summary>
    /// If the lead is in a pattern phase and the follower is close enough to the
    /// lead's pattern entry, rebuild the follower's phase list with a pattern
    /// circuit copying the lead's runway/direction/altitude and return true.
    /// </summary>
    private bool TryJoinLeadPattern(PhaseContext ctx, AircraftState lead, double gapToLeadNm)
    {
        // Extract pattern waypoints from the lead's current phase.
        PatternWaypoints? leadWaypoints = ExtractPatternWaypoints(lead);
        if (leadWaypoints is null)
        {
            return false;
        }

        RunwayInfo? leadRunway = lead.Phases?.AssignedRunway;
        if (leadRunway is null)
        {
            return false;
        }

        // Gate 1: follower must be close to the lead's downwind abeam point.
        double distToEntry = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon));
        if (distToEntry > JoinRangeNm)
        {
            return false;
        }

        // Gate 2: and reasonably close to the lead itself. Guards against joining
        // a stale pattern fix when the lead has already moved on (e.g., turning base).
        if (gapToLeadNm > MaxJoinGapNm)
        {
            return false;
        }

        // Gate 3: follower must be on the pattern side of the runway centerline.
        // A follower on the opposite side would have to cross final to reach
        // the abeam point — a real pilot would refuse, so reject the auto-join.
        if (!IsOnPatternSide(ctx.Aircraft, leadRunway, leadWaypoints.Direction))
        {
            return false;
        }

        Log.LogDebug(
            "[VfrFollow] {Callsign}: joining pattern copied from {Lead} on runway {Rwy}, direction {Dir}, dist={Dist:F2}nm",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            leadRunway.Designator,
            leadWaypoints.Direction,
            distToEntry
        );

        // Build the pattern circuit using the follower's own category (spacing
        // depends on what *we* can fly, not the lead).
        double patternAltitudeFt = ClaimSatisfiedDeferral(ctx, leadRunway) ?? leadWaypoints.PatternAltitude;
        IReadOnlyList<RunwayInfo> airportRunways = NavigationDatabase.Instance.GetRunways(leadRunway.AirportId);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = leadRunway,
                Category = ctx.Category,
                AircraftType = ctx.Aircraft.AircraftType,
                WindSpeedKt = ctx.Aircraft.WindSpeedKts,
                Direction = leadWaypoints.Direction,
                EntryLeg = PatternEntryLeg.Downwind,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = patternAltitudeFt,
                AirportRunways = airportRunways,
                AuthoredRunway = (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(leadRunway.Designator),
                ActiveRunways = ctx.ActiveRunways,
                Commanded = PatternCommanded.Of(ctx.Aircraft),
            }
        );

        // Replace the follower's phase list entirely (InstallJoinedCircuit carries any armed clearance over).
        List<Phase> legs = DownwindJoinEntry(ctx.Aircraft, leadRunway, leadWaypoints, patternAltitudeFt) is { } entry ? [entry, .. circuit] : circuit;
        InstallJoinedCircuit(ctx, leadRunway, leadWaypoints.Direction, legs, keepFollowing: true);
        return true;
    }

    /// <summary>
    /// The pattern entry that leads a follower joining the lead's pattern to the downwind abeam point, with a
    /// lead-in a mile before it on the downwind course; null when the follower is already established on the
    /// downwind leg (track aligned with the downwind heading and past the abeam point), where the circuit's
    /// DownwindPhase engages directly. Routing such a follower through PatternEntryPhase would command a turn
    /// toward the lead-in waypoint (which sits behind the aircraft on the reciprocal heading), making it fly backward.
    /// The entry is flown at <paramref name="patternAltitudeFt"/>, the altitude the joined circuit is built at.
    /// </summary>
    private static PatternEntryPhase? DownwindJoinEntry(
        AircraftState follower,
        RunwayInfo leadRunway,
        PatternWaypoints leadWaypoints,
        double patternAltitudeFt
    )
    {
        double trackToDownwindDelta = follower.TrueTrack.AbsAngleTo(leadWaypoints.DownwindHeading);
        double aircraftAlongTrack = GeoMath.AlongTrackDistanceNm(
            follower.Position,
            new LatLon(leadWaypoints.ThresholdLat, leadWaypoints.ThresholdLon),
            leadWaypoints.DownwindHeading
        );
        double abeamAlongTrack = GeoMath.AlongTrackDistanceNm(
            new LatLon(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon),
            new LatLon(leadWaypoints.ThresholdLat, leadWaypoints.ThresholdLon),
            leadWaypoints.DownwindHeading
        );
        if ((trackToDownwindDelta <= 30.0) && (aircraftAlongTrack >= abeamAlongTrack))
        {
            return null;
        }

        TrueHeading reverseDownwind = leadWaypoints.DownwindHeading.ToReciprocal();
        (double Lat, double Lon) leadIn = GeoMath.ProjectPoint(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon, reverseDownwind, 1.0);
        return new PatternEntryPhase
        {
            EntryLat = leadWaypoints.DownwindAbeamLat,
            EntryLon = leadWaypoints.DownwindAbeamLon,
            PatternAltitude = patternAltitudeFt,
            Kind = PatternEntryPhase.ClassifyDownwindEntry(
                follower.Position,
                follower.TrueTrack,
                new LatLon(leadRunway.ThresholdLatitude, leadRunway.ThresholdLongitude),
                leadRunway.TrueHeading,
                leadWaypoints.DownwindHeading,
                leadWaypoints.Direction
            ),
            LeadInLat = leadIn.Lat,
            LeadInLon = leadIn.Lon,
        };
    }

    /// <summary>
    /// If the lead is flying its base leg, join that base: a base entry to the point the lead's base began, then the
    /// base, final and landing on the lead's runway, in the lead's pattern direction, at the lead's final-turn distance
    /// and <see cref="BaseJoinAltitudeFt"/>. The pattern spacing then keeps the follower behind by speed. A follower that cannot make
    /// that join sanely (<see cref="CanJoinLeadBase"/>) keeps pursuing and joins on final instead.
    /// </summary>
    private bool TryJoinLeadBase(PhaseContext ctx, AircraftState lead, double gapToLeadNm)
    {
        if (ActiveLeadBase(lead) is not { } join)
        {
            return false;
        }

        if (BaseExtensionNm(ctx.Aircraft.Position, join, ExcursionLimitsFor(ctx).OffsetCapNm) is { } extensionNm)
        {
            return TryJoinExtendedBase(ctx, lead, join, extensionNm);
        }

        if (
            (lead.Phases?.CurrentPhase is not BasePhase)
            || !CanJoinLeadBase(ctx.Aircraft, join, gapToLeadNm, AirborneFollowHelper.PatternSpacingNm(lead))
        )
        {
            return false;
        }

        RunwayInfo runway = join.Runway;
        PatternWaypoints waypoints = join.Waypoints;
        double joinAltitudeFt = ClaimSatisfiedDeferral(ctx, runway) ?? BaseJoinAltitudeFt(ctx, join);

        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = runway,
                Category = ctx.Category,
                AircraftType = ctx.Aircraft.AircraftType,
                WindSpeedKt = ctx.Aircraft.WindSpeedKts,
                Direction = waypoints.Direction,
                EntryLeg = PatternEntryLeg.Base,
                TouchAndGo = false,
                FinalDistanceNm = join.FinalDistanceNm,
                PatternSizeNm = null,
                AltitudeOverrideFt = joinAltitudeFt,
                AirportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId),
                AuthoredRunway = (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator),
                ActiveRunways = ctx.ActiveRunways,
                Commanded = PatternCommanded.Of(ctx.Aircraft),
            }
        );
        var entry = new PatternEntryPhase
        {
            EntryLat = join.StartPoint.Lat,
            EntryLon = join.StartPoint.Lon,
            PatternAltitude = joinAltitudeFt,
            Kind = PatternEntryKind.Base,
        };

        Log.LogDebug(
            "[VfrFollow] {Callsign}: joining {Lead}'s {Dir} base to {Rwy} at {Lat:F5},{Lon:F5}, {Dist:F2}nm away",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            waypoints.Direction,
            runway.Designator,
            join.StartPoint.Lat,
            join.StartPoint.Lon,
            GeoMath.DistanceNm(ctx.Aircraft.Position, join.StartPoint)
        );
        InstallJoinedCircuit(ctx, runway, waypoints.Direction, [entry, .. circuit], keepFollowing: true);
        return true;
    }

    /// <summary>
    /// The altitude a base join enters at: the lowest of the lead's pattern altitude, the follower's own category pattern
    /// altitude (a piston never climbs to a jet's) and, for a pursuit that left the base leg, its present target altitude.
    /// An altitude the controller assigned stands instead.
    /// </summary>
    private double BaseJoinAltitudeFt(PhaseContext ctx, LeadBaseJoin join)
    {
        if (ctx.Targets.AssignedAltitude is { } assignedFt)
        {
            return assignedFt;
        }

        double ownPatternFt = join.Runway.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(ctx.Category);
        double altitudeFt = Math.Min(join.Waypoints.PatternAltitude, ownPatternFt);
        return ((PatternReturn is { FromBase: true }) && (ctx.Targets.TargetAltitude is { } targetFt)) ? Math.Min(altitudeFt, targetFt) : altitudeFt;
    }

    /// <summary>
    /// A follower that extended the leg the lead flew into its base turn, to build spacing, turns base where it is once the
    /// spacing is built: no excursion under way and the gap (the lead's path from its base start plus the follower's
    /// <paramref name="extensionNm"/>) at least the pattern spacing (<see cref="AirborneFollowHelper.PatternSpacingNm"/>). It turns on the pattern
    /// side, without crossing a parallel's final to reach the lead's final: the base, final and landing on the lead's runway,
    /// at <see cref="BaseJoinAltitudeFt"/>, the base's final-turn distance read from where the follower turns.
    /// </summary>
    private bool TryJoinExtendedBase(PhaseContext ctx, AircraftState lead, LeadBaseJoin join, double extensionNm)
    {
        RunwayInfo runway = join.Runway;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double alongFinalNm = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, threshold, join.Waypoints.FinalHeading.ToReciprocal());
        LatLon finalAbeam = GeoMath.ProjectPoint(threshold, join.Waypoints.FinalHeading.ToReciprocal(), alongFinalNm);
        double pathGapNm = _leadPath.LengthFromNm(join.StartPoint, lead.Position) + extensionNm;
        if (
            _widen.Active
            || (pathGapNm < AirborneFollowHelper.PatternSpacingNm(lead))
            || !IsOnPatternSide(ctx.Aircraft, runway, join.Waypoints.Direction)
            || CapturePathCrossesParallelFinal(ctx.Aircraft.Position, finalAbeam, runway)
        )
        {
            return false;
        }

        double joinAltitudeFt = ClaimSatisfiedDeferral(ctx, runway) ?? BaseJoinAltitudeFt(ctx, join);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = runway,
                Category = ctx.Category,
                AircraftType = ctx.Aircraft.AircraftType,
                WindSpeedKt = ctx.Aircraft.WindSpeedKts,
                Direction = join.Waypoints.Direction,
                EntryLeg = PatternEntryLeg.Base,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = joinAltitudeFt,
                AirportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId),
                AuthoredRunway = (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator),
                ActiveRunways = ctx.ActiveRunways,
                Commanded = PatternCommanded.Of(ctx.Aircraft),
            }
        );

        Log.LogDebug(
            "[VfrFollow] {Callsign}: spaced {Gap:F2} nm behind {Lead}, turning base to {Rwy} {Ext:F2} nm past its base turn point",
            ctx.Aircraft.Callsign,
            pathGapNm,
            TargetCallsign,
            runway.Designator,
            GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, join.StartPoint, join.LegTrack)
        );
        InstallJoinedCircuit(ctx, runway, join.Waypoints.Direction, circuit, keepFollowing: true);
        return true;
    }

    /// <summary>
    /// The gates on joining the lead's base: the follower is on the pattern side of the lead's runway, its path to the
    /// base start point crosses no parallel runway's final, it is at least <paramref name="desiredGapNm"/> (the pattern
    /// spacing, <see cref="AirborneFollowHelper.PatternSpacingNm"/>) from the lead, and the join geometry is sane
    /// (<see cref="IsBaseJoinGeometrySane"/>).
    /// </summary>
    private static bool CanJoinLeadBase(AircraftState follower, LeadBaseJoin join, double gapToLeadNm, double desiredGapNm)
    {
        return IsOnPatternSide(follower, join.Runway, join.Waypoints.Direction)
            && !CapturePathCrossesParallelFinal(follower.Position, join.StartPoint, join.Runway)
            && (gapToLeadNm >= desiredGapNm)
            && IsBaseJoinGeometrySane(follower, join);
    }

    /// <summary>
    /// The follower is within <see cref="BaseJoinRangeNm"/> of the base start point, not more than
    /// <see cref="BaseJoinPastLineToleranceNm"/> inside the lead's base line (along the final), and arrives at the
    /// start point needing no more than <see cref="MaxBaseJoinTurnDeg"/> of turn onto the base heading — which also
    /// rules out a follower inboard of the start point, which would have to turn back outbound to reach it.
    /// </summary>
    private static bool IsBaseJoinGeometrySane(AircraftState follower, LeadBaseJoin join)
    {
        double distToStartNm = GeoMath.DistanceNm(follower.Position, join.StartPoint);
        if (distToStartNm > BaseJoinRangeNm)
        {
            return false;
        }

        var threshold = new LatLon(join.Runway.ThresholdLatitude, join.Runway.ThresholdLongitude);
        TrueHeading outboundFinal = join.Waypoints.FinalHeading.ToReciprocal();
        double followerAlongNm = GeoMath.AlongTrackDistanceNm(follower.Position, threshold, outboundFinal);
        double startAlongNm = GeoMath.AlongTrackDistanceNm(join.StartPoint, threshold, outboundFinal);
        if (followerAlongNm < startAlongNm - BaseJoinPastLineToleranceNm)
        {
            return false;
        }

        TrueHeading arrivalTrack =
            distToStartNm < BaseJoinBearingMinDistNm ? follower.TrueTrack : new TrueHeading(GeoMath.BearingTo(follower.Position, join.StartPoint));
        return arrivalTrack.AbsAngleTo(join.Waypoints.BaseHeading) <= MaxBaseJoinTurnDeg;
    }

    /// <summary>
    /// If the lead is established on a straight-in final/landing to a known runway
    /// (no extractable pattern waypoints — e.g. an IFR aircraft that never flew a
    /// VFR circuit) and the follower is genuinely trailing it and aligned for a sane
    /// intercept, sequence the follower onto that runway's final and return true.
    /// The final chain is built without a landing clearance, so the follower descends
    /// behind the traffic and holds for a separate CLAND (FAA 7110.65 3-10-6).
    /// </summary>
    private bool TryJoinLeadFinal(PhaseContext ctx, AircraftState lead)
    {
        if (lead.IsOnGround)
        {
            return false;
        }
        if (lead.Phases?.CurrentPhase is not (FinalApproachPhase or LandingPhase))
        {
            return false;
        }
        if (lead.Phases.AssignedRunway is not { } runway)
        {
            return false;
        }

        // Distances are measured along the final, as the final's geometry gates do (CanSequenceOntoFinal): a follower past
        // the threshold, upwind or abeam it, is never out on the final however far it is from the threshold in a straight line.
        double followerAlongNm = AirborneFollowHelper.AlongFinalNm(ctx.Aircraft.Position, runway);
        double leadAlongNm = AirborneFollowHelper.AlongFinalNm(lead.Position, runway);

        // In-trail floor: stay behind the traffic (a pilot "should not take advantage of another aircraft, which is on final approach to land,
        // by cutting in front of, or overtaking that aircraft", AIM 4-3-4.d) and no closer than the same-runway floor, or the wake floor
        // behind a lead that carries a wake minimum for this follower. Until that spacing exists, keep pursuing rather than rolling onto final
        // too close.
        if (followerAlongNm - leadAlongNm < RequiredFinalInTrailNm(ctx, lead))
        {
            return false;
        }
        // The final's geometry gates: on the approach side at least MinFinalJoinDistNm out, a sane visual intercept (near the
        // extended centerline at a shallow angle), and never capturing through a parallel runway's final, whose 1.0 nm
        // cross-track allowance dwarfs closely-spaced parallel separation (AIM §4-3-3 FIG 4-3-3 note 7). Keep pursuing
        // otherwise; the gate re-evaluates every tick as geometry improves.
        if (!CanSequenceOntoFinal(ctx.Aircraft, runway))
        {
            return false;
        }

        SequenceOntoFinal(ctx, runway);
        return true;
    }

    /// <summary>
    /// The in-trail spacing a follower needs before it is sequenced onto a straight-in lead's final: the same-runway floor
    /// (<see cref="SameRunwayInTrailFloorNm"/>), or <see cref="WakeLeadInTrailFloorNm"/> behind a lead with a wake minimum
    /// for this follower. The radar wake minimum itself does not apply: accepting instructions to follow an aircraft puts
    /// wake turbulence separation on the pilot (AIM 7-4-8.b).
    /// </summary>
    internal static double RequiredFinalInTrailNm(PhaseContext ctx, AircraftState lead)
    {
        double leadWakeMinNm = WakeTurbulenceData.OnApproachWakeSeparationNm(
            lead.AircraftType,
            AircraftCategorization.Categorize(lead.AircraftType),
            ctx.AircraftType,
            ctx.Category
        );
        return leadWakeMinNm > 0 ? WakeLeadInTrailFloorNm : SameRunwayInTrailFloorNm;
    }

    /// <summary>
    /// True when a near-parallel runway's extended centerline lies laterally between the
    /// follower and <paramref name="runway"/>'s centerline — the geometry where capturing
    /// the lead's final means crossing the parallel's final approach course at low altitude.
    /// Navdata stores one <see cref="RunwayInfo"/> per physical runway oriented to an
    /// arbitrary end (KOAK stores 10L/10R, not 28R/28L), so both ends' headings are tested
    /// and the matching end's coordinates are used — comparing only the stored orientation
    /// makes the gate a silent no-op whenever the stored end points the other way.
    /// </summary>
    internal static bool JoinCapturePathCrossesParallelFinal(LatLon followerPos, RunwayInfo runway) =>
        CapturePathCrossesParallelFinal(followerPos, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway);

    /// <summary>
    /// True when a near-parallel runway's extended centerline lies laterally (across <paramref name="runway"/>'s
    /// centerline) strictly between <paramref name="from"/> and <paramref name="to"/>: flying from one to the other
    /// crosses that parallel's final approach course. See <see cref="JoinCapturePathCrossesParallelFinal"/>.
    /// </summary>
    internal static bool CapturePathCrossesParallelFinal(LatLon from, LatLon to, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double fromCrossNm = GeoMath.SignedCrossTrackDistanceNm(from, threshold, runway.TrueHeading);
        double toCrossNm = GeoMath.SignedCrossTrackDistanceNm(to, threshold, runway.TrueHeading);
        double lowNm = Math.Min(fromCrossNm, toCrossNm);
        double highNm = Math.Max(fromCrossNm, toCrossNm);
        foreach (double otherNm in ParallelCenterlineOffsetsNm(runway, threshold))
        {
            if ((Math.Abs(otherNm) > 1e-3) && (otherNm > lowNm) && (otherNm < highNm))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when a spacing excursion from <paramref name="position"/> to <paramref name="side"/> of
    /// <paramref name="refTrack"/> (+1 right, -1 left) would carry the follower toward a near-parallel runway's extended
    /// centerline lying within <paramref name="reachNm"/> across <paramref name="runway"/>'s centerline. An excursion side
    /// that runs along the final's direction rather than across it meets no parallel.
    /// </summary>
    internal static bool ExcursionMeetsParallelFinal(LatLon position, TrueHeading refTrack, int side, RunwayInfo runway, double reachNm)
    {
        double towardRightOfRunway = Math.Cos((refTrack + (90.0 * side)).AbsAngleTo(runway.TrueHeading + 90.0) * Math.PI / 180.0);
        if (Math.Abs(towardRightOfRunway) <= AirborneFollowHelper.OutsideSideTieCosine)
        {
            return false;
        }

        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double crossNm = GeoMath.SignedCrossTrackDistanceNm(position, threshold, runway.TrueHeading);
        double direction = Math.Sign(towardRightOfRunway);
        foreach (double otherNm in ParallelCenterlineOffsetsNm(runway, threshold))
        {
            double aheadNm = (otherNm - crossNm) * direction;
            if ((Math.Abs(otherNm) > 1e-3) && (aheadNm > 0) && (aheadNm <= reachNm))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Signed cross-track offsets (nm, right of <paramref name="runway"/>'s landing direction positive) of every other
    /// runway at the airport within <see cref="MaxParallelHeadingDeltaDeg"/> of its heading, measured on the end that
    /// points the same way.
    /// </summary>
    private static IEnumerable<double> ParallelCenterlineOffsetsNm(RunwayInfo runway, LatLon threshold)
    {
        foreach (RunwayInfo other in NavigationDatabase.Instance.GetRunways(runway.AirportId))
        {
            // Skip the target's own pavement in either orientation (28R matches a stored 10L entry).
            bool samePavement =
                string.Equals(other.Id.End1, runway.Designator, StringComparison.OrdinalIgnoreCase)
                || string.Equals(other.Id.End2, runway.Designator, StringComparison.OrdinalIgnoreCase);
            if (samePavement)
            {
                continue;
            }
            double delta1 = other.TrueHeading1.AbsAngleTo(runway.TrueHeading);
            double delta2 = other.TrueHeading2.AbsAngleTo(runway.TrueHeading);
            if (Math.Min(delta1, delta2) > MaxParallelHeadingDeltaDeg)
            {
                continue;
            }
            LatLon otherOnCenterline = delta1 <= delta2 ? new LatLon(other.Lat1, other.Lon1) : new LatLon(other.Lat2, other.Lon2);
            yield return GeoMath.SignedCrossTrackDistanceNm(otherOnCenterline, threshold, runway.TrueHeading);
        }
    }

    /// <summary>
    /// Replace the follower's phase list with a straight-in final + landing chain for
    /// <paramref name="runway"/> (<see cref="BuildFinalJoinLegs"/>), copying the follower's own category and inferring
    /// pattern direction from which side of the centerline it is on, still following the lead. The standing clearance is
    /// carried (<see cref="InstallJoinedCircuit"/>); with none, the follower descends behind the lead and awaits CLAND
    /// (going around at minimums if never cleared, per FinalApproachPhase).
    /// </summary>
    private void SequenceOntoFinal(PhaseContext ctx, RunwayInfo runway)
    {
        double crossTrack = SignedCrossTrackNm(ctx.Aircraft.Position, runway);
        PatternDirection direction = crossTrack >= 0 ? PatternDirection.Right : PatternDirection.Left;
        List<Phase> legs = BuildFinalJoinLegs(ctx, runway, direction);
        Log.LogDebug(
            "[VfrFollow] {Callsign}: sequenced onto {Rwy} final behind {Lead} ({Dir}) — awaiting landing clearance",
            ctx.Aircraft.Callsign,
            runway.Designator,
            TargetCallsign,
            direction
        );

        InstallJoinedCircuit(ctx, runway, direction, legs, keepFollowing: true);
    }

    /// <summary>
    /// A final join's legs to <paramref name="runway"/> in <paramref name="direction"/> traffic: the final and landing chain,
    /// led by a <see cref="PatternEntryPhase"/> that flies the follower onto the extended centerline before the
    /// final-approach phase. This both routes an offset follower onto the centerline at a sane intercept and defers the
    /// runway-dependent <see cref="FinalApproachPhase"/> to a later tick — its OnStart needs <c>ctx.Runway</c>, which is only
    /// populated from the new AssignedRunway on the tick after the swap. (Starting FinalApproachPhase directly here would run
    /// its OnStart with the stale follow-phase context, whose Runway is the pursuit's own runway, not necessarily this one.)
    /// </summary>
    private static List<Phase> BuildFinalJoinLegs(PhaseContext ctx, RunwayInfo runway, PatternDirection direction)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        TrueHeading finalCourse = runway.TrueHeading;
        double crossTrack = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, threshold, finalCourse);

        // Entry point on the extended centerline, led ahead of the follower's
        // perpendicular foot by its cross-track so the join is a ~45° intercept rather
        // than a square turn. Clamp so the entry never lands at/behind the threshold.
        double alongFinalNm = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, threshold, finalCourse.ToReciprocal());
        double entryDistNm = Math.Max(alongFinalNm - Math.Abs(crossTrack), MinFinalJoinDistNm);
        LatLon entry = GeoMath.ProjectPoint(threshold, finalCourse.ToReciprocal(), entryDistNm);
        double entryAltitude = GlideSlopeGeometry.AltitudeAtDistance(entryDistNm, runway.ElevationFt, ctx.Category);

        double? deferredAltitudeFt = ClaimSatisfiedDeferral(ctx, runway);
        IReadOnlyList<RunwayInfo> airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = runway,
                Category = ctx.Category,
                AircraftType = ctx.Aircraft.AircraftType,
                WindSpeedKt = ctx.Aircraft.WindSpeedKts,
                Direction = direction,
                EntryLeg = PatternEntryLeg.Final,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = deferredAltitudeFt,
                AirportRunways = airportRunways,
                AuthoredRunway = (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator),
                ActiveRunways = ctx.ActiveRunways,
                Commanded = PatternCommanded.Of(ctx.Aircraft),
            }
        );

        var entryPhase = new PatternEntryPhase
        {
            EntryLat = entry.Lat,
            EntryLon = entry.Lon,
            PatternAltitude = entryAltitude,
            Kind = PatternEntryKind.Final,
        };
        return [entryPhase, .. circuit];
    }

    /// <summary>
    /// Replace the follower's phase list with <paramref name="legs"/> flown to <paramref name="runway"/> in
    /// <paramref name="direction"/> traffic, and start it. With <paramref name="keepFollowing"/> the follow target is kept
    /// so the pattern phases keep spacing behind the lead; without it the follow is over. The clearance standing on the
    /// pursuit phase list (a CLAND, TG, SG, LA or COPT issued while the follower was still pursuing its lead) is captured
    /// first and carried onto the new list (<see cref="ApplyArmedLandingClearance"/>), as is an armed pattern runway
    /// (<see cref="CarryArmedPatternRunway"/>) with the pattern altitude its modifier deferred to it. A join onto the
    /// armed runway itself satisfies the arming: the join builder has already claimed the deferral
    /// (<see cref="ClaimSatisfiedDeferral"/>) and built <paramref name="legs"/> at it, so nothing is left deferred.
    /// </summary>
    private void InstallJoinedCircuit(PhaseContext ctx, RunwayInfo runway, PatternDirection direction, List<Phase> legs, bool keepFollowing)
    {
        PhaseList phases = ctx.Aircraft.Phases ?? new PhaseList();
        ClearanceType? armedClearance = phases.LandingClearance;
        string? armedClearedRunwayId = phases.ClearedRunwayId;
        var armedBefore = ArmedPatternState.Of(phases);
        RunwayInfo patternRunway = CarryArmedPatternRunway(ctx.Aircraft, armedBefore, runway, direction);
        bool joinSatisfiesTheArming = AirborneFollowHelper.IsSameRunway(patternRunway, runway);
        if (joinSatisfiesTheArming && (armedBefore.TransitionPatternAltitude is { } droppedDeferral))
        {
            Log.LogDebug(
                "[VfrFollow] {Callsign}: join onto {Rwy} drops the deferred altitude {Altitude} armed for {Armed}, which no join claimed",
                ctx.Aircraft.Callsign,
                runway.Designator,
                droppedDeferral.AltitudeFt,
                armedBefore.PatternRunway?.Designator
            );
        }

        phases.Clear(ctx);
        ctx.Aircraft.Phases = new PhaseList
        {
            AssignedRunway = runway,
            TrafficDirection = direction,
            PatternRunway = patternRunway,
            TransitionPatternAltitude = joinSatisfiesTheArming ? null : armedBefore.TransitionPatternAltitude,
        };
        foreach (Phase p in legs)
        {
            ctx.Aircraft.Phases.Add(p);
        }

        ctx.Aircraft.Approach.FollowingCallsign = keepFollowing ? TargetCallsign : null;
        ctx.Aircraft.Procedure.DestinationRunway = runway.Designator;
        ApplyArmedLandingClearance(ctx.Aircraft, armedClearance, armedClearedRunwayId, runway);
        ctx.Aircraft.Phases.Start(ctx);
    }

    /// <summary>
    /// The pattern altitude (feet MSL) a join onto <paramref name="joinRunway"/> is flown at when the join satisfies an armed
    /// pattern runway (<c>COPT MLT 28L 20</c> flown on 28R, then a join of 28L traffic): the altitude the modifier deferred
    /// to that runway. Null when the deferral is the runway change's clear or the join satisfies no deferral; the join
    /// builder then keeps its own altitude. A satisfied deferral is claimed here, before the legs are built, as a pattern
    /// entry onto the armed runway claims it: it becomes the standing <see cref="AircraftPattern.AltitudeOverrideFt"/> —
    /// null for a clear, so the flown runway's altitude is not carried onto another runway — and is disarmed.
    /// </summary>
    private static double? ClaimSatisfiedDeferral(PhaseContext ctx, RunwayInfo joinRunway)
    {
        if (
            (ctx.Aircraft.Phases is not { } phases)
            || (Commands.PatternCommandHandler.SatisfiedTransitionPatternAltitude(phases, joinRunway) is not { } deferred)
        )
        {
            return null;
        }

        Log.LogDebug(
            "[VfrFollow] {Callsign}: join onto armed runway {Rwy} satisfies the arming; flying its deferred altitude {Altitude}",
            ctx.Aircraft.Callsign,
            joinRunway.Designator,
            deferred.AltitudeFt
        );
        ctx.Aircraft.Pattern.SetAltitudeOverride(deferred.AltitudeFt, phases, ctx.Category);
        phases.TransitionPatternAltitude = null;
        return deferred.AltitudeFt;
    }

    /// <summary>
    /// The <see cref="PhaseList.PatternRunway"/> the rebuilt list starts on. An option clearance's
    /// pattern modifier (<c>COPT MLT 28L</c> flown on 28R) arms the pattern runway ahead of
    /// <see cref="PhaseList.AssignedRunway"/> and waits for the next cycle terminator to build the
    /// transition circuit; a FOLLOW issued in between replaces the whole phase list, and stamping
    /// <paramref name="joinRunway"/> into both fields would cancel that armed clearance without saying
    /// so. The arming is carried over instead: the follow's own circuit belongs to the lead's runway,
    /// and the armed transition still applies after the terminator that follows it. When the follower
    /// joins the armed runway itself the arming is already satisfied, so both fields become that
    /// runway. A carried-over arming is always announced: the follower will break out of the sequence it
    /// was just put into one terminator later, which is not what "follow that traffic" led the
    /// controller to expect (AIM 4-3-5 — an unexpected maneuver in the pattern). When the transition
    /// also crosses the field, the midfield notice comes on top.
    /// </summary>
    private static RunwayInfo CarryArmedPatternRunway(
        AircraftState aircraft,
        ArmedPatternState previous,
        RunwayInfo joinRunway,
        PatternDirection direction
    )
    {
        if (
            (previous.PatternRunway is not { } armed)
            || (previous.FlownRunway is not { } flown)
            || string.Equals(armed.Designator, flown.Designator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(armed.Designator, joinRunway.Designator, StringComparison.OrdinalIgnoreCase)
            // A pattern runway at another airport is not this circuit's business — the follower is
            // joining traffic here, and carrying it would arm a transition to a field it is not at.
            || !string.Equals(armed.AirportId, joinRunway.AirportId, StringComparison.OrdinalIgnoreCase)
        )
        {
            return joinRunway;
        }

        Log.LogDebug(
            "[VfrFollow] {Callsign}: carrying the armed pattern runway {Armed} onto the {Join} follow circuit",
            aircraft.Callsign,
            armed.Designator,
            joinRunway.Designator
        );
        string directionWord = direction == PatternDirection.Right ? "right" : "left";
        aircraft.PendingWarnings.Add(
            $"{aircraft.Callsign}: {directionWord} traffic runway {RunwayIdentifier.ToDisplayDesignator(armed.Designator)} stays armed — "
                + $"will leave the {RunwayIdentifier.ToDisplayDesignator(joinRunway.Designator)} sequence after the next "
                + $"{DescribeArmedTerminator(previous.LandingClearance)}"
        );
        Commands.PatternCommandHandler.WarnIfArmedTransitionCrossesField(aircraft, joinRunway, armed, direction);
        return armed;
    }

    /// <summary>
    /// The cycle terminator the armed transition will follow, named from the clearance the modifier rode
    /// on. With no clearance the aircraft is doing pattern work, whose default terminal is a
    /// touch-and-go (the circuit builders' <c>touchAndGo: true</c>).
    /// </summary>
    private static string DescribeArmedTerminator(ClearanceType? clearance) =>
        clearance switch
        {
            ClearanceType.ClearedForOption => "option",
            ClearanceType.ClearedStopAndGo => "stop-and-go",
            ClearanceType.ClearedLowApproach => "low approach",
            _ => "touch-and-go",
        };

    /// <summary>
    /// Carry an armed landing-family clearance (land, touch-and-go, option, stop-and-go or
    /// low approach, set while the follower was still pursuing its lead) onto the freshly
    /// built pattern/final chain so the follower flies it behind the traffic without a
    /// second clearance. A clearance issued without a runway
    /// (<paramref name="armedRunwayId"/> null) applies to whichever runway the follower
    /// joins; a named runway is honored only if it matches that runway — otherwise the
    /// follower keeps descending behind the traffic and awaits an explicit clearance on the
    /// actual runway, so it never lands or touches down on a runway the controller didn't clear.
    /// An option-family clearance also swaps the chain's landing for its own terminal, as
    /// issuing it does.
    /// </summary>
    internal void ApplyArmedLandingClearance(AircraftState aircraft, ClearanceType? armedClearance, string? armedRunwayId, RunwayInfo runway)
    {
        if (
            (armedClearance is not { } clearance)
            || !ArmedClearanceTerminal(clearance, LowApproachAtOrAboveFt, out Phase? terminal)
            || (aircraft.Phases is null)
        )
        {
            return;
        }

        if (
            (armedRunwayId is not null)
            && !string.Equals(RunwayIdentifier.NormalizeDesignator(armedRunwayId), runway.Designator, StringComparison.OrdinalIgnoreCase)
        )
        {
            Log.LogDebug(
                "[VfrFollow] {Callsign}: armed to land {Armed} but joining {Actual} behind {Lead}; awaiting explicit clearance",
                aircraft.Callsign,
                armedRunwayId,
                runway.Designator,
                TargetCallsign
            );
            return;
        }

        if ((terminal is not null) && !CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, terminal))
        {
            Log.LogWarning(
                "[VfrFollow] {Callsign}: armed {Clearance} not carried onto {Rwy} behind {Lead}: the joined circuit has no landing to replace",
                aircraft.Callsign,
                clearance,
                runway.Designator,
                TargetCallsign
            );
            return;
        }

        aircraft.Phases.LandingClearance = clearance;
        aircraft.Phases.ClearedRunwayId = runway.Designator;
        Log.LogDebug(
            "[VfrFollow] {Callsign}: applied armed {Clearance} on {Rwy} behind {Lead}",
            aircraft.Callsign,
            clearance,
            runway.Designator,
            TargetCallsign
        );
    }

    /// <summary>
    /// True when <paramref name="clearance"/> is a landing-family clearance a join carries over, with the phase it
    /// ends the approach with in <paramref name="terminal"/> — the terminal issuing the clearance installs (a
    /// touch-and-go for the option) — or null for a landing clearance, whose chain already ends in the landing. A low approach
    /// keeps the <paramref name="lowApproachAtOrAboveFt"/> restriction it was cleared with.
    /// </summary>
    private static bool ArmedClearanceTerminal(ClearanceType clearance, int? lowApproachAtOrAboveFt, out Phase? terminal)
    {
        bool optionFamily =
            clearance
            is ClearanceType.ClearedTouchAndGo
                or ClearanceType.ClearedForOption
                or ClearanceType.ClearedStopAndGo
                or ClearanceType.ClearedLowApproach;
        terminal = optionFamily ? Commands.PatternCommandHandler.OptionClearanceTerminal(clearance, lowApproachAtOrAboveFt) : null;
        return optionFamily || (clearance == ClearanceType.ClearedToLand);
    }

    /// <summary>
    /// Returns the lead's current pattern waypoints if the lead is in a pattern
    /// leg phase (Downwind/Base/Crosswind/Upwind). When the lead is in
    /// <see cref="PatternEntryPhase"/> — navigating to downwind abeam before
    /// the real circuit begins — the waypoints already exist on the next
    /// pattern-leg phase in the phase list (populated by
    /// <see cref="PatternBuilder.BuildCircuit"/>), so we look ahead.
    /// When the lead is on <see cref="FinalApproachPhase"/> or
    /// <see cref="LandingPhase"/> (still airborne), we look back through the
    /// completed pattern legs — all pattern-leg phases share the same
    /// <see cref="PatternWaypoints"/> instance, so the most recent completed
    /// Base/Downwind still carries it.
    /// </summary>
    private static PatternWaypoints? ExtractPatternWaypoints(AircraftState lead)
    {
        Phase? current = lead.Phases?.CurrentPhase;
        PatternWaypoints? fromCurrent = WaypointsOf(current);
        if (fromCurrent is not null)
        {
            return fromCurrent;
        }

        if (lead.Phases is not { } phases)
        {
            return null;
        }

        if (current is PatternEntryPhase)
        {
            for (int i = phases.CurrentIndex + 1; i < phases.Phases.Count; i++)
            {
                PatternWaypoints? waypoints = WaypointsOf(phases.Phases[i]);
                if (waypoints is not null)
                {
                    return waypoints;
                }
            }
        }

        // Lead on final or rolling out (still airborne) — look back for the
        // most recent pattern leg whose waypoints are still attached.
        if ((current is FinalApproachPhase || current is LandingPhase) && !lead.IsOnGround)
        {
            for (int i = phases.CurrentIndex - 1; i >= 0; i--)
            {
                PatternWaypoints? waypoints = WaypointsOf(phases.Phases[i]);
                if (waypoints is not null)
                {
                    return waypoints;
                }
            }
        }

        return null;
    }

    private static PatternWaypoints? WaypointsOf(Phase? phase) =>
        phase switch
        {
            DownwindPhase d => d.Waypoints,
            BasePhase b => b.Waypoints,
            CrosswindPhase c => c.Waypoints,
            UpwindPhase u => u.Waypoints,
            _ => null,
        };
}
