using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// VFR follow phase: the follower pursues another VFR aircraft in free flight,
/// matching heading toward the lead's position and the lead's speed with
/// distance-based spacing correction. Altitude is left unchanged — real pilots
/// told "follow traffic" maintain their current/assigned altitude (often staying
/// visually above the lead), and the pattern phases take over altitude on join.
///
/// When the lead is in a pattern phase and the follower is within
/// <see cref="JoinRangeNm"/> of the lead's downwind abeam point, within
/// <see cref="MaxJoinGapNm"/> of the lead itself, and on the same side of the
/// runway as the pattern, this phase swaps itself out for a full pattern circuit
/// (PatternEntryPhase → DownwindPhase → BasePhase → FinalApproachPhase → LandingPhase)
/// copying the lead's runway, direction, and altitude — after which the existing
/// <see cref="AirborneFollowHelper"/> machinery in the pattern phases takes over.
/// </summary>
public sealed class VfrFollowPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("VfrFollowPhase");

    /// <summary>Distance from the lead's downwind abeam point at which we auto-join the pattern.</summary>
    public const double JoinRangeNm = 3.0;

    /// <summary>Maximum distance follower-to-lead allowed at pattern join — guards against joining a stale pattern when the lead has moved.</summary>
    public const double MaxJoinGapNm = 5.0;

    /// <summary>
    /// Minimum in-trail spacing (follower distance-to-threshold minus the lead's) before
    /// sequencing onto a straight-in lead's final. Keeps the follower genuinely behind the
    /// traffic (AIM 4-3-4.4 "no cutting in front") and at the same-runway separation floor
    /// for a light single behind same/lighter traffic (7110.65 3-10-3); a heavier lead
    /// raises the requirement to its wake minimum (see <see cref="TryJoinLeadFinal"/>).
    /// </summary>
    public const double SameRunwayInTrailFloorNm = 1.5;

    /// <summary>Maximum cross-track from the extended centerline allowed when committing the turn onto a straight-in lead's final.</summary>
    public const double MaxFinalJoinCrossTrackNm = 1.0;

    /// <summary>Maximum intercept angle (track vs final approach course) allowed when committing onto final — the standard 30° final intercept.</summary>
    public const double MaxFinalJoinInterceptDeg = 30.0;

    /// <summary>Never newly turn a follower onto final closer than this to the threshold.</summary>
    public const double MinFinalJoinDistNm = 0.5;

    public string TargetCallsign { get; private set; }

    /// <summary>
    /// The runway the followed traffic is landing on, captured while the lead is
    /// airborne on a straight-in final/landing. Lets the follower be sequenced onto
    /// that runway's final even after the lead has touched down, instead of cancelling
    /// the follow and levelling off over the field.
    /// </summary>
    private RunwayInfo? _leadLandingRunway;

    public override string Name => "VFR Follow";
    public override bool ManagesSpeed => true;

    public VfrFollowPhase(string targetCallsign)
    {
        TargetCallsign = targetCallsign;
    }

    /// <summary>Update the follow target without recreating the phase.</summary>
    public void UpdateTarget(string targetCallsign)
    {
        TargetCallsign = targetCallsign;
    }

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.PreferredTurnDirection = null;
        Log.LogDebug("[VfrFollow] {Callsign}: following {Target}", ctx.Aircraft.Callsign, TargetCallsign);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        var lead = ctx.AircraftLookup?.Invoke(TargetCallsign);

        // Remember the lead's landing runway while it is established on a straight-in
        // final/landing, so the follower can be sequenced onto that runway even after
        // the lead touches down. Pattern-flying leads are handled by TryJoinLeadPattern.
        if (
            lead is { IsOnGround: false }
            && lead.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase
            && lead.Phases.AssignedRunway is { } leadRunway
        )
        {
            _leadLandingRunway = leadRunway;
        }

        // Lead-landed sequencing: if the traffic we were following has landed and we
        // know its runway, follow it onto that runway's final to await a landing
        // clearance — rather than cancelling the follow and free-flying level over the
        // field. Runs before CheckLeadLifecycle, which would otherwise cancel here.
        if (lead is { IsOnGround: true } && _leadLandingRunway is { } landedRunway)
        {
            SequenceOntoFinal(ctx, landedRunway);
            return true;
        }

        // Lead-not-found / lead-on-ground / runaway-distance checks are shared
        // with pattern-phase followers via AirborneFollowHelper.CheckLeadLifecycle.
        // It mutates Approach.FollowingCallsign + the runaway state on the follower
        // and emits the appropriate pilot transmission. When it returns true, this
        // phase has nothing left to do.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            return true;
        }

        // CheckLeadLifecycle already verified the lead exists.
        lead = ctx.AircraftLookup!.Invoke(TargetCallsign)!;
        double gapNm = GeoMath.DistanceNm(ctx.Aircraft.Position, lead.Position);

        // If the lead is in a pattern, see if we're close enough to join.
        if (TryJoinLeadPattern(ctx, lead, gapNm))
        {
            // Phase list has been replaced — this phase is no longer current.
            return true;
        }

        // If the lead is on a straight-in final/landing (no pattern waypoints to join),
        // sequence onto its runway's final once we are trailing and aligned.
        if (TryJoinLeadFinal(ctx, lead))
        {
            // Phase list has been replaced — this phase is no longer current.
            return true;
        }

        // Free pursuit: steer toward the lead and match speed with spacing correction.
        // Altitude is deliberately not touched — the controller's last assignment stands.
        double targetBearing = GeoMath.BearingTo(ctx.Aircraft.Position, lead.Position);
        ctx.Targets.TargetTrueHeading = new TrueHeading(targetBearing);

        double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        double? adjusted = AirborneFollowHelper.AdjustedFreeFlightSpeed(
            ctx.Aircraft,
            lead,
            minSpeed,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Log
        );
        if (adjusted is null)
        {
            // Helper has already added a one-shot "unable to maintain separation"
            // warning and cleared Approach.FollowingCallsign. End the phase so the
            // helper isn't re-entered every tick (which would re-spam the warning).
            return true;
        }
        ctx.Targets.TargetSpeed = adjusted;

        return false;
    }

    /// <summary>
    /// If the lead is in a pattern phase and the follower is close enough to the
    /// lead's pattern entry, rebuild the follower's phase list with a pattern
    /// circuit copying the lead's runway/direction/altitude and return true.
    /// </summary>
    private bool TryJoinLeadPattern(PhaseContext ctx, AircraftState lead, double gapToLeadNm)
    {
        // Extract pattern waypoints from the lead's current phase.
        var leadWaypoints = ExtractPatternWaypoints(lead);
        if (leadWaypoints is null)
        {
            return false;
        }

        var leadRunway = lead.Phases?.AssignedRunway;
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
        var airportRunways = NavigationDatabase.Instance.GetRunways(leadRunway.AirportId);
        var circuit = PatternBuilder.BuildCircuit(
            leadRunway,
            ctx.Category,
            leadWaypoints.Direction,
            PatternEntryLeg.Downwind,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: leadWaypoints.PatternAltitude,
            airportRunways: airportRunways
        );

        // If the follower is already established on the downwind leg (track
        // aligned with downwind heading and past the abeam point), skip
        // PatternEntryPhase and engage the circuit's DownwindPhase directly.
        // Routing such a follower through PatternEntryPhase would command a
        // turn toward the lead-in waypoint (which sits behind the aircraft on
        // the reciprocal heading), making it fly backward.
        double trackToDownwindDelta = ctx.Aircraft.TrueTrack.AbsAngleTo(leadWaypoints.DownwindHeading);
        double aircraftAlongTrack = GeoMath.AlongTrackDistanceNm(
            ctx.Aircraft.Position,
            new LatLon(leadWaypoints.ThresholdLat, leadWaypoints.ThresholdLon),
            leadWaypoints.DownwindHeading
        );
        double abeamAlongTrack = GeoMath.AlongTrackDistanceNm(
            new LatLon(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon),
            new LatLon(leadWaypoints.ThresholdLat, leadWaypoints.ThresholdLon),
            leadWaypoints.DownwindHeading
        );
        bool alreadyOnDownwind = trackToDownwindDelta <= 30.0 && aircraftAlongTrack >= abeamAlongTrack;

        // Replace the follower's phase list entirely. Capture any armed landing
        // clearance first: a CLAND issued while the follower was still pursuing its
        // lead set it on this pursuit phase list, and the rebuilt circuit must carry
        // it over (see ApplyArmedLandingClearance).
        var phases = ctx.Aircraft.Phases ?? new PhaseList();
        var armedClearance = phases.LandingClearance;
        string? armedClearedRunwayId = phases.ClearedRunwayId;
        phases.Clear(ctx);
        ctx.Aircraft.Phases = new PhaseList
        {
            AssignedRunway = leadRunway,
            TrafficDirection = leadWaypoints.Direction,
            PatternRunway = leadRunway,
        };
        if (!alreadyOnDownwind)
        {
            TrueHeading reverseDownwind = leadWaypoints.DownwindHeading.ToReciprocal();
            var leadIn = GeoMath.ProjectPoint(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon, reverseDownwind, 1.0);
            var entry = new PatternEntryPhase
            {
                EntryLat = leadWaypoints.DownwindAbeamLat,
                EntryLon = leadWaypoints.DownwindAbeamLon,
                PatternAltitude = leadWaypoints.PatternAltitude,
                Kind = PatternEntryPhase.ClassifyDownwindEntry(
                    ctx.Aircraft.Position,
                    ctx.Aircraft.TrueTrack,
                    new LatLon(leadRunway.ThresholdLatitude, leadRunway.ThresholdLongitude),
                    leadRunway.TrueHeading,
                    leadWaypoints.DownwindHeading,
                    leadWaypoints.Direction
                ),
                LeadInLat = leadIn.Lat,
                LeadInLon = leadIn.Lon,
            };
            ctx.Aircraft.Phases.Add(entry);
        }
        foreach (var p in circuit)
        {
            ctx.Aircraft.Phases.Add(p);
        }

        // Preserve the follow target so the pattern phases keep adjusting spacing.
        // Reset runaway tracking — the pattern phases use AirborneFollowHelper for
        // tighter spacing than the free-flight pursuit, so the gap dynamics that
        // applied during VfrFollowPhase no longer apply. Without this reset, a
        // follower whose gap was creeping outward by 1-2 ft / s under loose free-
        // flight spacing would carry that runaway timer into PatternEntry and trip
        // a false-positive cancel before the new spacing has time to converge.
        ctx.Aircraft.Approach.FollowingCallsign = TargetCallsign;
        AirborneFollowHelper.ResetRunawayTracking(ctx.Aircraft);
        ctx.Aircraft.Procedure.DestinationRunway = leadRunway.Designator;
        ApplyArmedLandingClearance(ctx.Aircraft, armedClearance, armedClearedRunwayId, leadRunway);

        // Start the first phase in the new list.
        ctx.Aircraft.Phases.Start(ctx);
        return true;
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

        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double followerDistNm = GeoMath.DistanceNm(ctx.Aircraft.Position, threshold);
        double leadDistNm = GeoMath.DistanceNm(lead.Position, threshold);
        double crossTrackNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, threshold, runway.TrueHeading));
        double interceptDeg = ctx.Aircraft.TrueTrack.AbsAngleTo(runway.TrueHeading);

        // In-trail floor: stay genuinely behind the traffic (AIM 4-3-4.4 "no cutting in
        // front") and no closer than the same-runway separation minimum (7110.65 3-10-3) —
        // or the wake-turbulence minimum when the lead is heavier (TBL 5-5-2). Until that
        // spacing exists, keep pursuing rather than rolling onto final too close.
        double leadWakeMinNm = WakeTurbulenceData.OnApproachWakeSeparationNm(
            lead.AircraftType,
            AircraftCategorization.Categorize(lead.AircraftType),
            ctx.AircraftType,
            ctx.Category
        );
        double requiredInTrailNm = Math.Max(SameRunwayInTrailFloorNm, leadWakeMinNm);
        if (followerDistNm - leadDistNm < requiredInTrailNm)
        {
            return false;
        }
        // Don't newly turn onto final unreasonably close to the threshold.
        if (followerDistNm < MinFinalJoinDistNm)
        {
            return false;
        }
        // Sane visual intercept: near the extended centerline at a shallow angle.
        if (crossTrackNm > MaxFinalJoinCrossTrackNm)
        {
            return false;
        }
        if (interceptDeg > MaxFinalJoinInterceptDeg)
        {
            return false;
        }

        SequenceOntoFinal(ctx, runway);
        return true;
    }

    /// <summary>
    /// Replace the follower's phase list with a straight-in final + landing chain for
    /// <paramref name="runway"/>, copying the follower's own category and inferring
    /// pattern direction from which side of the centerline it is on. No landing
    /// clearance is set — the follower descends behind the lead and awaits CLAND
    /// (going around at minimums if never cleared, per FinalApproachPhase).
    ///
    /// The chain is led by a <see cref="PatternEntryPhase"/> that flies the follower
    /// onto the extended centerline before the final-approach phase. This both routes
    /// an offset follower onto the centerline at a sane intercept and defers the
    /// runway-dependent <see cref="FinalApproachPhase"/> to a later tick — its OnStart
    /// needs <c>ctx.Runway</c>, which is only populated from the new AssignedRunway on
    /// the tick after the swap. (Starting FinalApproachPhase directly here would run its
    /// OnStart with the stale follow-phase context whose Runway is null.)
    /// </summary>
    private void SequenceOntoFinal(PhaseContext ctx, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var finalCourse = runway.TrueHeading;
        double crossTrack = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, threshold, finalCourse);
        var direction = crossTrack >= 0 ? PatternDirection.Right : PatternDirection.Left;

        // Entry point on the extended centerline, led ahead of the follower's
        // perpendicular foot by its cross-track so the join is a ~45° intercept rather
        // than a square turn. Clamp so the entry never lands at/behind the threshold.
        double alongFinalNm = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, threshold, finalCourse.ToReciprocal());
        double entryDistNm = Math.Max(alongFinalNm - Math.Abs(crossTrack), MinFinalJoinDistNm);
        var entry = GeoMath.ProjectPoint(threshold, finalCourse.ToReciprocal(), entryDistNm);
        double gsAngle = GlideSlopeGeometry.AngleForCategory(ctx.Category);
        double entryAltitude = GlideSlopeGeometry.AltitudeAtDistance(entryDistNm, runway.ElevationFt, gsAngle);

        var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        var circuit = PatternBuilder.BuildCircuit(
            runway,
            ctx.Category,
            direction,
            PatternEntryLeg.Final,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            airportRunways: airportRunways
        );

        var phases = ctx.Aircraft.Phases ?? new PhaseList();
        var armedClearance = phases.LandingClearance;
        string? armedClearedRunwayId = phases.ClearedRunwayId;
        phases.Clear(ctx);
        ctx.Aircraft.Phases = new PhaseList
        {
            AssignedRunway = runway,
            TrafficDirection = direction,
            PatternRunway = runway,
        };
        ctx.Aircraft.Phases.Add(
            new PatternEntryPhase
            {
                EntryLat = entry.Lat,
                EntryLon = entry.Lon,
                PatternAltitude = entryAltitude,
                Kind = PatternEntryKind.Final,
            }
        );
        foreach (var p in circuit)
        {
            ctx.Aircraft.Phases.Add(p);
        }

        // Keep the follow target so FinalApproachPhase keeps tightening spacing behind
        // the lead; reset runaway tracking since pattern-phase spacing differs from the
        // free-flight pursuit (mirrors TryJoinLeadPattern).
        ctx.Aircraft.Approach.FollowingCallsign = TargetCallsign;
        AirborneFollowHelper.ResetRunawayTracking(ctx.Aircraft);
        ctx.Aircraft.Procedure.DestinationRunway = runway.Designator;
        ApplyArmedLandingClearance(ctx.Aircraft, armedClearance, armedClearedRunwayId, runway);

        Log.LogDebug(
            "[VfrFollow] {Callsign}: sequenced onto {Rwy} final behind {Lead} ({Dir}) — awaiting landing clearance",
            ctx.Aircraft.Callsign,
            runway.Designator,
            TargetCallsign,
            direction
        );

        ctx.Aircraft.Phases.Start(ctx);
    }

    /// <summary>
    /// Carry an armed landing clearance (set by <c>CLAND</c> while the follower was
    /// still pursuing its lead) onto the freshly built pattern/final chain so the
    /// follower lands behind the traffic without a second clearance. A bare CLAND
    /// (<paramref name="armedRunwayId"/> null) lands on whichever runway the follower
    /// joins; a named runway is honored only if it matches that runway — otherwise the
    /// follower keeps descending behind the traffic and awaits an explicit CLAND on the
    /// actual runway, so it never auto-lands on a runway the controller didn't clear.
    /// </summary>
    internal void ApplyArmedLandingClearance(AircraftState aircraft, ClearanceType? armedClearance, string? armedRunwayId, RunwayInfo runway)
    {
        if ((armedClearance is not ClearanceType.ClearedToLand) || aircraft.Phases is null)
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

        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = runway.Designator;
        Log.LogDebug(
            "[VfrFollow] {Callsign}: applied armed landing clearance on {Rwy} behind {Lead}",
            aircraft.Callsign,
            runway.Designator,
            TargetCallsign
        );
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
        var current = lead.Phases?.CurrentPhase;
        var fromCurrent = WaypointsOf(current);
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
                var waypoints = WaypointsOf(phases.Phases[i]);
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
                var waypoints = WaypointsOf(phases.Phases[i]);
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

    /// <summary>
    /// Returns true if <paramref name="follower"/> is on the same side of the
    /// runway centerline as the pattern (the side the downwind lies on).
    /// A left pattern has downwind to the left of the runway when viewed in the
    /// direction of landing; follower must be on that same side.
    /// </summary>
    private static bool IsOnPatternSide(AircraftState follower, RunwayInfo runway, PatternDirection direction)
    {
        // Signed cross-track distance from the runway centerline: positive = right
        // of runway heading, negative = left.
        double crossTrack = GeoMath.SignedCrossTrackDistanceNm(
            follower.Position,
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            runway.TrueHeading
        );
        return direction == PatternDirection.Left ? crossTrack <= 0 : crossTrack >= 0;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Altitude/speed adjustments don't cancel the follow — controllers
        // adjust trailing-aircraft separation without breaking the visual.
        if (IsAdditiveAirborneAdjustment(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        return cmd switch
        {
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            // Any other command (heading/pattern-leg/etc.) clears this phase
            // and hands control back to the controller's direct targets.
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new VfrFollowPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            TargetCallsign = TargetCallsign,
            LeadLandingRunway = _leadLandingRunway?.ToSnapshot(),
        };

    public static VfrFollowPhase FromSnapshot(VfrFollowPhaseDto dto)
    {
        var phase = new VfrFollowPhase(dto.TargetCallsign) { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
        phase.RestoreRequirements(dto.Requirements);
        if (dto.LeadLandingRunway is not null)
        {
            phase._leadLandingRunway = RunwayInfo.FromSnapshot(dto.LeadLandingRunway);
        }
        return phase;
    }
}
