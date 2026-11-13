using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases.Pattern;

public sealed partial class VfrFollowPhase
{
    /// <summary>
    /// Free pursuit: match the lead's speed with spacing correction, then steer to keep trail behind the lead
    /// (<see cref="AirborneFollowHelper.ComputeFreePursuitHeading"/>). Altitude is deliberately not touched — the
    /// controller's last assignment stands. True when the phase ends: spacing can no longer be kept, or an extension
    /// past the lead's base turn point ran out of room.
    /// </summary>
    private bool TickFreePursuit(PhaseContext ctx, AircraftState lead)
    {
        double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        FreePursuitSpacing spacing = FreePursuitSpacingFor(ctx, lead);
        double? adjusted = AirborneFollowHelper.AdjustedFreeFlightSpeed(
            ctx,
            lead,
            minSpeed,
            spacing.DesiredNm + AirborneFollowHelper.TrailGapHysteresisNm,
            Log
        );
        if (adjusted is null)
        {
            // Helper has already added a one-shot "unable to maintain separation"
            // warning and cleared Approach.FollowingCallsign. End the phase so the
            // helper isn't re-entered every tick (which would re-spam the warning). Spacing lost behind the lead never takes
            // the return's base or final join, which would put the follower back in front of or onto it.
            ReturnToPattern(ctx, PatternReturn, forwardJoins: false);
            return true;
        }

        if (TryBreakOffBaseExtension(ctx, lead, spacing.Excursion.OffsetCapNm))
        {
            return true;
        }

        bool wasWidening = _widen.Active;
        ctx.Targets.TargetTrueHeading = AirborneFollowHelper.ComputeFreePursuitHeading(ctx.Aircraft, lead, spacing, _leadPath, _widen);
        if (ParallelHoldStalled(ctx, lead, spacing) && TryStartTurnOut(ctx, lead, stalledHold: true))
        {
            return false;
        }

        AnnounceNewExcursion(ctx, spacing, wasWidening);
        ctx.Targets.TargetSpeed = PursuitSpeed(ctx.Aircraft, lead, spacing, minSpeed, adjusted.Value);
        return false;
    }

    /// <summary>
    /// True when the excursion has held the follower alongside the lead at its offset cap, more than
    /// <see cref="StallShortfallNm"/> short of the gap, and the gap has grown by less than <see cref="StallMinGrowthNm"/> over
    /// the last <see cref="StallWindowSeconds"/>: at the same speed the follower cannot fall behind (the measured parallel hold).
    /// </summary>
    private bool ParallelHoldStalled(PhaseContext ctx, AircraftState lead, FreePursuitSpacing spacing)
    {
        LeadPathProjection projection = _leadPath.Project(ctx.Aircraft.Position, lead.Position, lead.TrueTrack);
        bool holding =
            _widen.Active
            && (spacing.ExtendedLeg is null)
            && (projection.OffPathNm >= (spacing.Excursion.OffsetCapNm - StallCapToleranceNm))
            && ((spacing.DesiredNm - projection.GapNm) > StallShortfallNm);
        _parallelHold = AdvanceParallelHoldWindow(_parallelHold, holding, projection.GapNm, ctx.DeltaSeconds, out bool stalled);
        return stalled;
    }

    /// <summary>
    /// Advances the parallel-hold window: closed (null) while the follower is not <paramref name="holding"/>, opened at the
    /// present gap when the hold begins, and after <see cref="StallWindowSeconds"/> judged and reopened at the present gap.
    /// <paramref name="stalled"/> is true on the tick the window closes with the gap grown by less than
    /// <see cref="StallMinGrowthNm"/>.
    /// </summary>
    internal static ParallelHoldWindow? AdvanceParallelHoldWindow(
        ParallelHoldWindow? window,
        bool holding,
        double gapNm,
        double deltaSeconds,
        out bool stalled
    )
    {
        stalled = false;
        if (!holding)
        {
            return null;
        }

        if (window is not { } open)
        {
            return new ParallelHoldWindow(gapNm, 0.0);
        }

        double seconds = open.Seconds + deltaSeconds;
        if (seconds < StallWindowSeconds)
        {
            return open with { Seconds = seconds };
        }

        stalled = gapNm - open.StartGapNm < StallMinGrowthNm;
        return new ParallelHoldWindow(gapNm, 0.0);
    }

    /// <summary>
    /// The pursuit's target speed. While the S-turn builds the gap, or the follower extends the lead's pre-base leg until it
    /// can turn base, it slows as far as it can: the speed loop measures the straight-line distance, which the excursion's
    /// own offset lengthens and the extension shortens. For the same reason a follower more than
    /// <see cref="SpeedCapOffPathNm"/> off the lead's path flies no faster than the lead, so an ended excursion does not
    /// close the gap it built and start another.
    /// </summary>
    private double PursuitSpeed(AircraftState follower, AircraftState lead, FreePursuitSpacing spacing, double minSpeed, double adjusted)
    {
        if (_widen.Active || (spacing.ExtendedLeg is not null))
        {
            return minSpeed;
        }

        double offPathNm = _leadPath.Project(follower.Position, lead.Position, lead.TrueTrack).OffPathNm;
        return offPathNm > SpeedCapOffPathNm ? Math.Min(adjusted, Math.Max(lead.IndicatedAirspeed, minSpeed)) : adjusted;
    }

    /// <summary>
    /// Tell the controller when a spacing excursion starts (AIM 4-3-5: a pilot maneuvering for spacing says so), no more than
    /// once every <see cref="STurnCallIntervalSeconds"/>: "extending downwind for spacing" while the follower is extending
    /// the lead's pre-base leg along the downwind (<see cref="IsExtendingDownwind"/>), else "S-turning for spacing". Both
    /// calls share the one cooldown.
    /// </summary>
    private void AnnounceNewExcursion(PhaseContext ctx, FreePursuitSpacing spacing, bool wasWidening)
    {
        _sTurnCallCooldownSeconds = Math.Max(0.0, _sTurnCallCooldownSeconds - ctx.DeltaSeconds);
        if (wasWidening || !_widen.Active || (_sTurnCallCooldownSeconds > 0))
        {
            return;
        }

        _sTurnCallCooldownSeconds = STurnCallIntervalSeconds;
        Pilot.PilotSpeechText call = IsExtendingDownwind(spacing)
            ? Pilot.PilotResponder.BuildExtendingDownwindForSpacing(ctx.Aircraft, TargetCallsign)
            : Pilot.PilotResponder.BuildSTurnsForSpacing(ctx.Aircraft, TargetCallsign);
        Pilot.PilotResponder.RouteByTunedPosition(ctx.Aircraft, Pilot.PilotReportRouting.From(ctx), call);
    }

    /// <summary>
    /// True when the follower is extending the lead's pre-base leg (<see cref="FreePursuitSpacing.ExtendedLeg"/>) and that
    /// leg tracks within <see cref="ExtendingDownwindMaxOffDeg"/> of the excursion circuit's downwind heading: the leg it
    /// extends is a downwind.
    /// </summary>
    internal static bool IsExtendingDownwind(FreePursuitSpacing spacing) =>
        (spacing.ExtendedLeg is { } leg)
        && (spacing.Circuit is { } circuit)
        && (leg.Track.AbsAngleTo(circuit.Runway.TrueHeading.ToReciprocal()) <= ExtendingDownwindMaxOffDeg);

    /// <summary>
    /// What the pursuit steers to (<see cref="FreePursuitSpacing"/>): the desired gap (<see cref="DesiredSpacingNm"/>), the
    /// excursion's limits (<see cref="ExcursionLimitsFor"/>), the circuit whose outside an excursion takes
    /// (<see cref="ExcursionCircuit"/>), the lead's pre-base leg while the follower is extending it, with the gap measured
    /// along the lead's path from its base start plus the extension, and the runway whose parallel finals an excursion keeps
    /// clear of (the circuit's, else the lead's).
    /// </summary>
    private FreePursuitSpacing FreePursuitSpacingFor(PhaseContext ctx, AircraftState lead)
    {
        LeadBaseJoin? leadBase = ActiveLeadBase(lead);
        FollowExcursionLimits limits = ExcursionLimitsFor(ctx);
        FollowCircuit? circuit = ExcursionCircuit(lead, leadBase);
        FollowExtendedLeg? extendedLeg =
            (leadBase is { } join) && (BaseExtensionNm(ctx.Aircraft.Position, join, limits.OffsetCapNm) is { } extensionNm)
                ? new FollowExtendedLeg(join.StartPoint, join.LegTrack, _leadPath.LengthFromNm(join.StartPoint, lead.Position) + extensionNm)
                : null;
        RunwayInfo? parallelsOf = circuit?.Runway ?? lead.Phases?.AssignedRunway;
        return new FreePursuitSpacing(DesiredSpacingNm(ctx, lead, leadBase), limits, circuit, extendedLeg, parallelsOf);
    }

    /// <summary>
    /// The gap the pursuit keeps: pattern spacing (<see cref="AirborneFollowHelper.PatternSpacingNm"/>) behind a lead in the pattern or bound for
    /// it, the in-trail spacing the final join needs behind a lead on a straight-in final (<see cref="RequiredFinalInTrailNm"/>),
    /// the wider free-flight spacing behind any other.
    /// </summary>
    private static double DesiredSpacingNm(PhaseContext ctx, AircraftState lead, LeadBaseJoin? leadBase)
    {
        if ((leadBase is not null) || (LeadCircuit(lead) is not null) || Commands.PatternCommandHandler.HasQueuedPatternEntry(lead))
        {
            return AirborneFollowHelper.PatternSpacingNm(lead);
        }

        bool leadOnStraightInFinal =
            !lead.IsOnGround && (lead.Phases is { CurrentPhase: FinalApproachPhase or LandingPhase, AssignedRunway: not null });
        return leadOnStraightInFinal
            ? RequiredFinalInTrailNm(ctx, lead)
            : AirborneFollowHelper.FreeFlightDistanceForLeader(AircraftCategorization.Categorize(lead.AircraftType));
    }

    /// <summary>The circuit whose outside an excursion takes: the lead's, the base it flew, or the follower's own pattern return.</summary>
    private FollowCircuit? ExcursionCircuit(AircraftState lead, LeadBaseJoin? leadBase) =>
        LeadCircuit(lead)
        ?? (leadBase is { } join ? new FollowCircuit(join.Runway, join.Waypoints.Direction) : null)
        ?? (PatternReturn is { } patternReturn ? new FollowCircuit(patternReturn.Runway, patternReturn.Direction) : null);

    /// <summary>
    /// The excursion's offset cap, max(<see cref="AirborneFollowHelper.TrailMinOffsetCapNm"/>,
    /// <see cref="AirborneFollowHelper.TrailOffsetCapTurnRadii"/> turn radii), and its largest turn off the track. A turboprop
    /// or jet caps the offset at <see cref="AirborneFollowHelper.TrailFastOffsetCapNm"/> and turns only
    /// <see cref="AirborneFollowHelper.TrailExcursionDeg"/>: its turn radius would otherwise make the S-turn a turn-out.
    /// </summary>
    private static FollowExcursionLimits ExcursionLimitsFor(PhaseContext ctx)
    {
        double capNm = Math.Max(
            AirborneFollowHelper.TrailMinOffsetCapNm,
            AirborneFollowHelper.TrailOffsetCapTurnRadii * BasePhase.TurnRadiusNm(ctx.Aircraft.GroundSpeed, ctx.Category)
        );
        return ctx.Category is AircraftCategory.Turboprop or AircraftCategory.Jet
            ? new FollowExcursionLimits(Math.Min(capNm, AirborneFollowHelper.TrailFastOffsetCapNm), AirborneFollowHelper.TrailExcursionDeg)
            : new FollowExcursionLimits(capNm, AirborneFollowHelper.TrailWideExcursionDeg);
    }

    /// <summary>The lead's circuit while it flies a pattern leg or a pattern entry with a runway and direction; otherwise null.</summary>
    private static FollowCircuit? LeadCircuit(AircraftState lead) =>
        (lead.Phases is { CurrentPhase: PatternEntryPhase or UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase } phases)
        && (phases.AssignedRunway is { } runway)
        && (phases.TrafficDirection is { } direction)
            ? new FollowCircuit(runway, direction)
            : null;

    /// <summary>
    /// Remember the lead's base while it flies it: its runway, its circuit, where it began, and the track the lead flew into
    /// that point (read from the lead's recorded path, or the circuit's downwind heading when the path does not reach back).
    /// </summary>
    private void RememberLeadBase(AircraftState follower, AircraftState lead)
    {
        if ((lead.Phases?.CurrentPhase is not BasePhase { Waypoints: { } waypoints } leadBase) || (lead.Phases.AssignedRunway is not { } runway))
        {
            return;
        }

        // A base restored from a snapshot written before the start point was recorded has none; the lead's
        // position on the first tick it is seen on that base stands in for it.
        if ((_leadBase is { } known) && ((leadBase.StartPoint is null) || (known.StartPoint == leadBase.StartPoint)))
        {
            return;
        }

        LatLon start = leadBase.StartPoint ?? lead.Position;
        TrueHeading legTrack = _leadPath.TrackInto(start, LegTrackSampleNm) ?? waypoints.DownwindHeading;
        _leadBase = new LeadBaseJoin(runway, waypoints, start, leadBase.FinalDistanceNm, legTrack);
        _extensionLogged = false;
        Log.LogDebug(
            "[VfrFollow] {Callsign}: {Lead} began its base at {Lat:F5},{Lon:F5}, arriving on {Track:F0}°",
            follower.Callsign,
            TargetCallsign,
            start.Lat,
            start.Lon,
            legTrack.Degrees
        );
    }

    /// <summary>The remembered lead base while the lead is still airborne on that base, its final or its landing; otherwise null.</summary>
    private LeadBaseJoin? ActiveLeadBase(AircraftState lead) =>
        ((_leadBase is { } join) && !lead.IsOnGround && (lead.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase)) ? join : null;

    /// <summary>
    /// How far (nm) the follower is past the lead's base start point along the leg the lead flew into it, when it is within
    /// the excursion cap of that leg — that is, extending the leg rather than joining behind it; otherwise null.
    /// </summary>
    private static double? BaseExtensionNm(LatLon position, LeadBaseJoin join, double offsetCapNm)
    {
        double pastNm = GeoMath.AlongTrackDistanceNm(position, join.StartPoint, join.LegTrack);
        double offNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, join.StartPoint, join.LegTrack));
        return (pastNm > 0) && (offNm <= offsetCapNm + BaseExtensionLegToleranceNm) ? pastNm : null;
    }

    /// <summary>
    /// True when an extending follower has come within <see cref="BaseExtensionMinFinalClearanceNm"/> of the lead's final
    /// centerline, or crossed it: a leg that converges on the final has no room left to extend.
    /// </summary>
    private static bool ExtensionReachedFinal(LatLon position, LeadBaseJoin join)
    {
        RunwayInfo runway = join.Runway;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double sign = join.Waypoints.Direction == PatternDirection.Right ? 1.0 : -1.0;
        double patternSideNm = sign * GeoMath.SignedCrossTrackDistanceNm(position, threshold, runway.TrueHeading);
        // A base that itself began closer in than the clearance leaves only the room it began with; one begun on the
        // non-pattern side leaves none, so the limit is the centerline itself.
        double startSideNm = sign * GeoMath.SignedCrossTrackDistanceNm(join.StartPoint, threshold, runway.TrueHeading);
        return patternSideNm < Math.Max(0.0, Math.Min(BaseExtensionMinFinalClearanceNm, startSideNm));
    }

    /// <summary>
    /// A follower still building spacing that has extended the lead's pre-base leg more than <see cref="BaseExtensionLimitNm"/>
    /// past the point the lead's base began, or that the leg has brought near the final, gives up the follow
    /// (<see cref="EndFollowExtendingDownwind"/>). Logs the start of the extension once.
    /// </summary>
    private bool TryBreakOffBaseExtension(PhaseContext ctx, AircraftState lead, double offsetCapNm)
    {
        if ((ActiveLeadBase(lead) is not { } join) || (BaseExtensionNm(ctx.Aircraft.Position, join, offsetCapNm) is not { } extensionNm))
        {
            return false;
        }

        double gapNm = GeoMath.DistanceNm(ctx.Aircraft.Position, lead.Position);
        if (!_extensionLogged)
        {
            _extensionLogged = true;
            Log.LogDebug(
                "[VfrFollow] {Callsign}: extending past {Lead}'s base turn point to build spacing, gap {Gap:F2} nm",
                ctx.Aircraft.Callsign,
                TargetCallsign,
                gapNm
            );
        }

        bool reachedFinal = ExtensionReachedFinal(ctx.Aircraft.Position, join);
        if ((extensionNm <= BaseExtensionLimitNm) && !reachedFinal)
        {
            return false;
        }

        Log.LogDebug(
            "[VfrFollow] {Callsign}: unable to follow {Lead}, {Ext:F2} nm past its base turn{Final}, {Gap:F2} nm behind",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            extensionNm,
            reachedFinal ? " nearing the final" : "",
            gapNm
        );
        EndFollowExtendingDownwind(ctx, join);
        return true;
    }

    /// <summary>
    /// Give up a follow whose extension ran out of room: the follower says it is unable to follow and asks for a base turn
    /// (AIM 5-5-12.a.2), and flies an extended downwind of the lead's circuit (runway, direction, and
    /// <see cref="BaseJoinAltitudeFt"/>) from where it is: the circuit's downwind heading, parallel to the final and never
    /// converging on it, until the controller turns it base (TB) or re-sequences it. It never re-enters by a downwind entry,
    /// which would turn it back against the downwind flow (AIM 4-3-5).
    /// </summary>
    private void EndFollowExtendingDownwind(PhaseContext ctx, LeadBaseJoin join)
    {
        InstallExtendedDownwind(ctx, join.Runway, join.Waypoints.Direction, BaseJoinAltitudeFt(ctx, join));
        Pilot.PilotResponder.RouteByTunedPosition(
            ctx.Aircraft,
            Pilot.PilotReportRouting.From(ctx),
            Pilot.PilotResponder.BuildUnableToFollowExtendingDownwind(ctx.Aircraft, TargetCallsign)
        );
    }

    /// <summary>
    /// Ends the follow into an extended downwind of <paramref name="runway"/>'s <paramref name="direction"/> circuit at
    /// <paramref name="altitudeFt"/>, from where the follower is: the downwind heading, parallel to the final and never
    /// converging on it, until the controller turns it base (TB) or re-sequences it.
    /// </summary>
    private void InstallExtendedDownwind(PhaseContext ctx, RunwayInfo runway, PatternDirection direction, double altitudeFt)
    {
        double patternAltitudeFt = ClaimSatisfiedDeferral(ctx, runway) ?? altitudeFt;
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = runway,
                Category = ctx.Category,
                AircraftType = ctx.Aircraft.AircraftType,
                WindSpeedKt = ctx.Aircraft.WindSpeedKts,
                Direction = direction,
                EntryLeg = PatternEntryLeg.Downwind,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = patternAltitudeFt,
                AirportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId),
                AuthoredRunway = (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator),
                ActiveRunways = ctx.ActiveRunways,
                Commanded = PatternCommanded.Of(ctx.Aircraft),
            }
        );
        int downwindIndex = circuit.FindIndex(phase => phase is DownwindPhase);
        if (downwindIndex < 0)
        {
            throw new InvalidOperationException($"A downwind circuit for runway {runway.Designator} was built without a downwind leg");
        }

        ((DownwindPhase)circuit[downwindIndex]).IsExtended = true;
        InstallJoinedCircuit(ctx, runway, direction, circuit[downwindIndex..], keepFollowing: false);
    }
}
