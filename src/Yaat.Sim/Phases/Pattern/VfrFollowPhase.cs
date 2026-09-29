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
/// The exception is a pursuit that FOLLOW started from a pattern leg
/// (<see cref="PatternReturn"/>): it levels at the lower of its present altitude and
/// its circuit's pattern altitude, and when the follow ends it re-enters that circuit.
///
/// When the lead is flying its base leg and the follower can reach the point that base began from the pattern
/// side, outside the lead's base line, at a sane turn and at least the pattern spacing behind the lead, this phase
/// swaps itself out for a base entry to that point followed by the base, final and landing on the lead's runway
/// (<see cref="TryJoinLeadBase"/>).
///
/// When the lead is in a pattern phase and the follower is within
/// <see cref="JoinRangeNm"/> of the lead's downwind abeam point, within
/// <see cref="MaxJoinGapNm"/> of the lead itself, and on the same side of the
/// runway as the pattern, this phase swaps itself out for a full pattern circuit
/// (PatternEntryPhase → DownwindPhase → BasePhase → FinalApproachPhase → LandingPhase)
/// copying the lead's runway, direction, and altitude — after which the existing
/// <see cref="AirborneFollowHelper"/> machinery in the pattern phases takes over.
/// </summary>
public sealed class VfrFollowPhase(string targetCallsign, FollowPatternReturn? patternReturn) : Phase
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

    /// <summary>
    /// Heading delta under which another runway at the airport counts as a parallel whose
    /// final approach course the join capture path must not cross. True parallels differ
    /// by well under 1°; CIFP/mag-var rounding can push apparent deltas to a few degrees.
    /// </summary>
    private const double MaxParallelHeadingDeltaDeg = 10.0;

    /// <summary>Never newly turn a follower onto final closer than this to the threshold.</summary>
    public const double MinFinalJoinDistNm = 0.5;

    /// <summary>
    /// Farthest (nm, along the leg the lead flew into its base turn) a follower still building spacing extends past the
    /// point the lead's base began before it gives up the follow, keeps flying the leg and asks for a base turn.
    /// </summary>
    public const double BaseExtensionLimitNm = 2.0;

    /// <summary>Least time (s) between two "S-turning for spacing" calls from one follow (AIM 4-3-5).</summary>
    public const double STurnCallIntervalSeconds = 60.0;

    /// <summary>
    /// Farther than this (nm) off the lead's path, the follower flies no faster than the lead: the straight-line distance
    /// the speed loop holds overstates the gap along the path, and closing on it would undo the spacing just built.
    /// </summary>
    public const double SpeedCapOffPathNm = 0.3;

    /// <summary>How far beyond the excursion's offset cap (nm) off the lead's pre-base leg a follower still counts as extending that leg.</summary>
    private const double BaseExtensionLegToleranceNm = 0.25;

    /// <summary>
    /// Closest (nm) an extending follower comes to the lead's final centerline before the extension has run out of room
    /// and it breaks off: a pre-base leg that converges on the final must not carry the follower toward it.
    /// </summary>
    public const double BaseExtensionMinFinalClearanceNm = 1.0;

    /// <summary>How far back along the lead's recorded path (nm) the track into its base turn point is read from.</summary>
    private const double LegTrackSampleNm = 0.3;

    /// <summary>Farthest the follower may be from the point the lead's base began and still join that base.</summary>
    public const double BaseJoinRangeNm = 5.0;

    /// <summary>
    /// How far inside the lead's base line (closer to the threshold, measured along the final) the follower may be
    /// and still join the lead's base; farther in, it keeps pursuing and joins on final.
    /// </summary>
    public const double BaseJoinPastLineToleranceNm = 0.2;

    /// <summary>Largest turn onto the base heading at the lead's base start point that the base join may ask for.</summary>
    public const double MaxBaseJoinTurnDeg = 120.0;

    /// <summary>Inside this distance of the base start point the bearing to it is unstable, so the follower's track stands in.</summary>
    private const double BaseJoinBearingMinDistNm = 0.1;

    /// <summary>Hysteresis state for the free-pursuit spacing excursion (lateral spacing tool).</summary>
    private readonly FollowWidenState _widen = new();

    /// <summary>The lead's recent ground track: the follower's gap is measured along it and the lead's turns are flown from it.</summary>
    private readonly LeadPathTrail _leadPath = new();

    /// <summary>
    /// The lead's base, remembered from the tick it was flying it, so a follower still building spacing can extend past it
    /// and join later.
    /// </summary>
    private LeadBaseJoin? _leadBase;

    /// <summary>Whether the start of an extension past the lead's base turn point has been logged (log-only, not serialized).</summary>
    private bool _extensionLogged;

    /// <summary>Seconds until another "S-turning for spacing" call may be made (<see cref="STurnCallIntervalSeconds"/>).</summary>
    private double _sTurnCallCooldownSeconds;

    public string TargetCallsign { get; private set; } = targetCallsign;

    /// <summary>
    /// The circuit the follower left when FOLLOW moved it off a pattern leg into this pursuit, or null
    /// when the pursuit did not start from a pattern leg.
    /// </summary>
    public FollowPatternReturn? PatternReturn { get; } = patternReturn;

    /// <summary>
    /// The runway the followed traffic is landing on, captured while the lead is
    /// airborne on a straight-in final/landing. Lets the follower be sequenced onto
    /// that runway's final even after the lead has touched down, instead of cancelling
    /// the follow and levelling off over the field.
    /// </summary>
    private RunwayInfo? _leadLandingRunway;

    public override string Name => "VFR Follow";
    public override bool ManagesSpeed => true;

    /// <summary>
    /// Update the follow target without recreating the phase. A new lead drops everything learnt about the old one: its path,
    /// its base, its landing runway and any spacing excursion flown behind it.
    /// </summary>
    public void UpdateTarget(string targetCallsign)
    {
        if (string.Equals(TargetCallsign, targetCallsign, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TargetCallsign = targetCallsign;
        _leadPath.Clear();
        _leadBase = null;
        _widen.Active = false;
        _widen.Side = 0;
        _leadLandingRunway = null;
        _extensionLogged = false;
    }

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.PreferredTurnDirection = null;
        _widen.Active = false;

        // A pursuit that left a pattern leg flies its circuit's pattern altitude, set once here (a later CM
        // overrides it; AIM 4-3-3): a follower below it, on upwind, keeps climbing to it. From base the target is
        // the lower of present and pattern altitude — never a climb there, and never the glideslope descent the
        // base leg was flying.
        if (PatternReturn is { } patternReturn)
        {
            ctx.Targets.TargetAltitude = patternReturn.FromBase
                ? Math.Min(ctx.Aircraft.Altitude, patternReturn.PatternAltitudeFt)
                : patternReturn.PatternAltitudeFt;
            ctx.Targets.DesiredVerticalRate = null;
        }

        Log.LogDebug("[VfrFollow] {Callsign}: following {Target}", ctx.Aircraft.Callsign, TargetCallsign);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        AircraftState? lead = ctx.AircraftLookup?.Invoke(TargetCallsign);
        RememberLeadLandingRunway(lead);

        if (TrySequenceBehindLandedLead(ctx, lead))
        {
            return true;
        }

        // Lead-not-found / lead-on-ground / runaway-distance checks are shared
        // with pattern-phase followers via AirborneFollowHelper.CheckLeadLifecycle.
        // It mutates Approach.FollowingCallsign + the runaway state on the follower
        // and emits the appropriate pilot transmission. When it returns true, this
        // phase has nothing left to do.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            ReturnToPattern(ctx, PatternReturn);
            return true;
        }

        // CheckLeadLifecycle already verified the lead exists.
        lead = ctx.AircraftLookup!.Invoke(TargetCallsign)!;
        // A new follow (or a new lead) seeds the path from the lead's position history, so a follower told to follow mid-leg
        // measures its gap along the path the lead has just flown rather than a straight line.
        if (_leadPath.Points.Count == 0)
        {
            foreach ((double lat, double lon) in lead.PositionHistory)
            {
                _leadPath.Record(new LatLon(lat, lon));
            }
        }
        _leadPath.Record(lead.Position);
        RememberLeadBase(ctx.Aircraft, lead);
        double gapNm = GeoMath.DistanceNm(ctx.Aircraft.Position, lead.Position);

        // If the lead is flying its base, join that base at the point it began; otherwise, if the
        // lead is in a pattern, see if we're close enough to join; if it is on a straight-in
        // final/landing (no pattern waypoints to join), sequence onto its runway's final once we
        // are trailing and aligned. Every join replaces the phase list, so this phase is no
        // longer current.
        if (TryJoinLeadBase(ctx, lead, gapNm) || TryJoinLeadPattern(ctx, lead, gapNm) || TryJoinLeadFinal(ctx, lead))
        {
            return true;
        }

        return TickFreePursuit(ctx, lead);
    }

    /// <summary>
    /// Remember the lead's landing runway while it is established on a straight-in
    /// final/landing, so the follower can be sequenced onto that runway even after
    /// the lead touches down. Pattern-flying leads are handled by TryJoinLeadPattern.
    /// </summary>
    private void RememberLeadLandingRunway(AircraftState? lead)
    {
        if (
            lead is { IsOnGround: false }
            && lead.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase
            && lead.Phases.AssignedRunway is { } leadRunway
        )
        {
            _leadLandingRunway = leadRunway;
        }
    }

    /// <summary>
    /// Lead-landed sequencing: if the traffic we were following has landed and we
    /// know its runway, follow it onto that runway's final to await a landing
    /// clearance — rather than cancelling the follow and free-flying level over the
    /// field. Runs before CheckLeadLifecycle, which would otherwise cancel here.
    /// Same parallel-final gate as TryJoinLeadFinal: this shortcut skips the in-trail
    /// and intercept gates by design (the lead is down, so spacing is moot), but
    /// capturing the runway from the far side of a close parallel would still cross
    /// that parallel's final approach course (AIM §4-3-3 FIG 4-3-3 note 7). When the
    /// gate refuses, fall through to CheckLeadLifecycle, which ends the follow
    /// (lead on the ground) and leaves the re-sequence to the controller.
    /// </summary>
    private bool TrySequenceBehindLandedLead(PhaseContext ctx, AircraftState? lead)
    {
        if (
            lead is { IsOnGround: true }
            && _leadLandingRunway is { } landedRunway
            && !JoinCapturePathCrossesParallelFinal(ctx.Aircraft.Position, landedRunway)
        )
        {
            SequenceOntoFinal(ctx, landedRunway);
            return true;
        }
        return false;
    }

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
            // helper isn't re-entered every tick (which would re-spam the warning).
            ReturnToPattern(ctx, PatternReturn);
            return true;
        }

        if (TryBreakOffBaseExtension(ctx, lead, spacing.Excursion.OffsetCapNm))
        {
            return true;
        }

        bool wasWidening = _widen.Active;
        ctx.Targets.TargetTrueHeading = AirborneFollowHelper.ComputeFreePursuitHeading(ctx.Aircraft, lead, spacing, _leadPath, _widen);
        AnnounceNewExcursion(ctx, wasWidening);
        ctx.Targets.TargetSpeed = PursuitSpeed(ctx.Aircraft, lead, spacing, minSpeed, adjusted.Value);
        return false;
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
    /// once every <see cref="STurnCallIntervalSeconds"/>.
    /// </summary>
    private void AnnounceNewExcursion(PhaseContext ctx, bool wasWidening)
    {
        _sTurnCallCooldownSeconds = Math.Max(0.0, _sTurnCallCooldownSeconds - ctx.DeltaSeconds);
        if (wasWidening || !_widen.Active || (_sTurnCallCooldownSeconds > 0))
        {
            return;
        }

        _sTurnCallCooldownSeconds = STurnCallIntervalSeconds;
        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            Pilot.PilotResponder.BuildSTurnsForSpacing(ctx.Aircraft, TargetCallsign),
            Pilot.PilotResponder.SoloPositionsTowerApproach
        );
    }

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
    /// The gap the pursuit keeps: pattern spacing (<see cref="PatternSpacingNm"/>) behind a lead in the pattern or bound for
    /// it, the in-trail spacing the final join needs behind a lead on a straight-in final (<see cref="RequiredFinalInTrailNm"/>),
    /// the wider free-flight spacing behind any other.
    /// </summary>
    private static double DesiredSpacingNm(PhaseContext ctx, AircraftState lead, LeadBaseJoin? leadBase)
    {
        if ((leadBase is not null) || (LeadCircuit(lead) is not null) || Commands.PatternCommandHandler.HasQueuedPatternEntry(lead))
        {
            return PatternSpacingNm(ctx, lead);
        }

        bool leadOnStraightInFinal =
            !lead.IsOnGround && (lead.Phases is { CurrentPhase: FinalApproachPhase or LandingPhase, AssignedRunway: not null });
        return leadOnStraightInFinal
            ? RequiredFinalInTrailNm(ctx, lead)
            : AirborneFollowHelper.FreeFlightDistanceForLeader(AircraftCategorization.Categorize(lead.AircraftType));
    }

    /// <summary>
    /// Spacing behind a lead in the pattern: the pattern spacing (<see cref="AirborneFollowHelper.DesiredDistanceForLeader"/>),
    /// or the on-approach wake-turbulence minimum behind a heavier lead when that is more.
    /// </summary>
    private static double PatternSpacingNm(PhaseContext ctx, AircraftState lead)
    {
        AircraftCategory leadCategory = AircraftCategorization.Categorize(lead.AircraftType);
        double wakeNm = WakeTurbulenceData.OnApproachWakeSeparationNm(lead.AircraftType, leadCategory, ctx.AircraftType, ctx.Category);
        return Math.Max(AirborneFollowHelper.DesiredDistanceForLeader(leadCategory), wakeNm);
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
        (_leadBase is { } join) && !lead.IsOnGround && (lead.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase) ? join : null;

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
        RunwayInfo runway = join.Runway;
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            runway,
            ctx.Category,
            ctx.Aircraft.AircraftType,
            ctx.Aircraft.WindSpeedKts,
            join.Waypoints.Direction,
            PatternEntryLeg.Downwind,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: BaseJoinAltitudeFt(ctx, join),
            airportRunways: NavigationDatabase.Instance.GetRunways(runway.AirportId),
            authoredRunway: (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator)
        );
        int downwindIndex = circuit.FindIndex(phase => phase is DownwindPhase);
        if (downwindIndex < 0)
        {
            throw new InvalidOperationException($"A downwind circuit for runway {runway.Designator} was built without a downwind leg");
        }

        ((DownwindPhase)circuit[downwindIndex]).IsExtended = true;
        InstallJoinedCircuit(ctx, runway, join.Waypoints.Direction, circuit[downwindIndex..], keepFollowing: false);
        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            Pilot.PilotResponder.BuildUnableToFollowExtendingDownwind(ctx.Aircraft, TargetCallsign),
            Pilot.PilotResponder.SoloPositionsTowerApproach
        );
    }

    /// <summary>
    /// When a pursuit that left a pattern leg ends (lead lost or despawned, lead landed with no captured runway,
    /// spacing unmaintainable), re-enter the circuit
    /// <paramref name="patternReturn"/> names: a pattern entry to its runway on the same side, with an RPO note. The
    /// follow state itself was already cleared by the caller.
    /// </summary>
    private void ReturnToPattern(PhaseContext ctx, FollowPatternReturn? patternReturn)
    {
        if (patternReturn is null)
        {
            return;
        }

        AircraftState aircraft = ctx.Aircraft;
        string runwayDisplay = RunwayIdentifier.ToDisplayDesignator(patternReturn.Runway.Designator);
        string side = patternReturn.Direction == PatternDirection.Right ? "right" : "left";

        // Assign the return runway itself rather than naming it: a designator would be resolved at the
        // follower's airport context, which need not be the airport the circuit belongs to.
        // This phase is ticking, so the list it runs from is present.
        PhaseList phases = aircraft.Phases!;
        RunwayInfo? previousRunway = phases.AssignedRunway;
        phases.AssignedRunway = patternReturn.Runway;
        CommandResult entry = Commands.PatternCommandHandler.TryEnterPattern(
            aircraft,
            patternReturn.Direction,
            PatternEntryLeg.Downwind,
            runwayId: null,
            finalDistanceNm: null,
            groundLayout: ctx.GroundLayout
        );
        if (!entry.Success)
        {
            phases.AssignedRunway = previousRunway;
            Log.LogWarning(
                "[VfrFollow] {Callsign}: follow of {Lead} ended but re-entering runway {Rwy} failed: {Reason}",
                aircraft.Callsign,
                TargetCallsign,
                runwayDisplay,
                entry.Message
            );
            aircraft.PendingWarnings.Add(
                $"{aircraft.Callsign} follow ended, unable to re-enter the pattern for runway {runwayDisplay}: {entry.Message}"
            );
            return;
        }

        Log.LogDebug(
            "[VfrFollow] {Callsign}: follow of {Lead} ended, re-entering {Side} traffic runway {Rwy}",
            aircraft.Callsign,
            TargetCallsign,
            side,
            runwayDisplay
        );
        aircraft.PendingWarnings.Add($"{aircraft.Callsign} follow ended, re-entering {side} traffic runway {runwayDisplay}");
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
        IReadOnlyList<RunwayInfo> airportRunways = NavigationDatabase.Instance.GetRunways(leadRunway.AirportId);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            leadRunway,
            ctx.Category,
            ctx.Aircraft.AircraftType,
            ctx.Aircraft.WindSpeedKts,
            leadWaypoints.Direction,
            PatternEntryLeg.Downwind,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: leadWaypoints.PatternAltitude,
            airportRunways: airportRunways,
            authoredRunway: (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(leadRunway.Designator)
        );

        // Replace the follower's phase list entirely (InstallJoinedCircuit carries any armed clearance over).
        List<Phase> legs = DownwindJoinEntry(ctx.Aircraft, leadRunway, leadWaypoints) is { } entry ? [entry, .. circuit] : circuit;
        InstallJoinedCircuit(ctx, leadRunway, leadWaypoints.Direction, legs, keepFollowing: true);
        return true;
    }

    /// <summary>
    /// The pattern entry that leads a follower joining the lead's pattern to the downwind abeam point, with a
    /// lead-in a mile before it on the downwind course; null when the follower is already established on the
    /// downwind leg (track aligned with the downwind heading and past the abeam point), where the circuit's
    /// DownwindPhase engages directly. Routing such a follower through PatternEntryPhase would command a turn
    /// toward the lead-in waypoint (which sits behind the aircraft on the reciprocal heading), making it fly backward.
    /// </summary>
    private static PatternEntryPhase? DownwindJoinEntry(AircraftState follower, RunwayInfo leadRunway, PatternWaypoints leadWaypoints)
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
        if (trackToDownwindDelta <= 30.0 && aircraftAlongTrack >= abeamAlongTrack)
        {
            return null;
        }

        TrueHeading reverseDownwind = leadWaypoints.DownwindHeading.ToReciprocal();
        (double Lat, double Lon) leadIn = GeoMath.ProjectPoint(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon, reverseDownwind, 1.0);
        return new PatternEntryPhase
        {
            EntryLat = leadWaypoints.DownwindAbeamLat,
            EntryLon = leadWaypoints.DownwindAbeamLon,
            PatternAltitude = leadWaypoints.PatternAltitude,
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

        if ((lead.Phases?.CurrentPhase is not BasePhase) || !CanJoinLeadBase(ctx.Aircraft, join, gapToLeadNm, PatternSpacingNm(ctx, lead)))
        {
            return false;
        }

        RunwayInfo runway = join.Runway;
        PatternWaypoints waypoints = join.Waypoints;
        double joinAltitudeFt = BaseJoinAltitudeFt(ctx, join);

        List<Phase> circuit = PatternBuilder.BuildCircuit(
            runway,
            ctx.Category,
            ctx.Aircraft.AircraftType,
            ctx.Aircraft.WindSpeedKts,
            waypoints.Direction,
            PatternEntryLeg.Base,
            touchAndGo: false,
            finalDistanceNm: join.FinalDistanceNm,
            patternSizeNm: null,
            altitudeOverrideFt: joinAltitudeFt,
            airportRunways: NavigationDatabase.Instance.GetRunways(runway.AirportId),
            authoredRunway: (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator)
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
        return (PatternReturn is { FromBase: true }) && (ctx.Targets.TargetAltitude is { } targetFt) ? Math.Min(altitudeFt, targetFt) : altitudeFt;
    }

    /// <summary>
    /// A follower that extended the leg the lead flew into its base turn, to build spacing, turns base where it is once the
    /// spacing is built: no excursion under way and the gap (the lead's path from its base start plus the follower's
    /// <paramref name="extensionNm"/>) at least the pattern spacing (<see cref="PatternSpacingNm"/>). It turns on the pattern
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
            || (pathGapNm < PatternSpacingNm(ctx, lead))
            || !IsOnPatternSide(ctx.Aircraft, runway, join.Waypoints.Direction)
            || CapturePathCrossesParallelFinal(ctx.Aircraft.Position, finalAbeam, runway)
        )
        {
            return false;
        }

        List<Phase> circuit = PatternBuilder.BuildCircuit(
            runway,
            ctx.Category,
            ctx.Aircraft.AircraftType,
            ctx.Aircraft.WindSpeedKts,
            join.Waypoints.Direction,
            PatternEntryLeg.Base,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: BaseJoinAltitudeFt(ctx, join),
            airportRunways: NavigationDatabase.Instance.GetRunways(runway.AirportId),
            authoredRunway: (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator)
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
    /// spacing, <see cref="PatternSpacingNm"/>) from the lead, and the join geometry is sane (<see cref="IsBaseJoinGeometrySane"/>).
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
    /// The lead's base a follower may join: its runway, its circuit's waypoints, where it began, its final-turn distance,
    /// and the track the lead flew into the point it began.
    /// </summary>
    private sealed record LeadBaseJoin(
        RunwayInfo Runway,
        PatternWaypoints Waypoints,
        LatLon StartPoint,
        double? FinalDistanceNm,
        TrueHeading LegTrack
    );

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
        if (followerDistNm - leadDistNm < RequiredFinalInTrailNm(ctx, lead))
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
        // Never capture through a parallel runway's final: the cross-track allowance
        // (1.0 nm) dwarfs closely-spaced parallel separation (OAK 28L/28R ≈ 0.165 nm), so
        // a follower on the far side of the parallel would descend across its final
        // approach course to reach the lead's centerline (AIM §4-3-3 FIG 4-3-3 note 7).
        // Keep pursuing instead; the gate re-evaluates every tick as geometry improves.
        if (JoinCapturePathCrossesParallelFinal(ctx.Aircraft.Position, runway))
        {
            return false;
        }

        SequenceOntoFinal(ctx, runway);
        return true;
    }

    /// <summary>
    /// The in-trail spacing a follower needs before it is sequenced onto a straight-in lead's final: the same-runway floor
    /// (<see cref="SameRunwayInTrailFloorNm"/>), or the wake-turbulence minimum behind a heavier lead.
    /// </summary>
    private static double RequiredFinalInTrailNm(PhaseContext ctx, AircraftState lead)
    {
        double leadWakeMinNm = WakeTurbulenceData.OnApproachWakeSeparationNm(
            lead.AircraftType,
            AircraftCategorization.Categorize(lead.AircraftType),
            ctx.AircraftType,
            ctx.Category
        );
        return Math.Max(SameRunwayInTrailFloorNm, leadWakeMinNm);
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
        TrueHeading finalCourse = runway.TrueHeading;
        double crossTrack = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, threshold, finalCourse);
        PatternDirection direction = crossTrack >= 0 ? PatternDirection.Right : PatternDirection.Left;

        // Entry point on the extended centerline, led ahead of the follower's
        // perpendicular foot by its cross-track so the join is a ~45° intercept rather
        // than a square turn. Clamp so the entry never lands at/behind the threshold.
        double alongFinalNm = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, threshold, finalCourse.ToReciprocal());
        double entryDistNm = Math.Max(alongFinalNm - Math.Abs(crossTrack), MinFinalJoinDistNm);
        LatLon entry = GeoMath.ProjectPoint(threshold, finalCourse.ToReciprocal(), entryDistNm);
        double entryAltitude = GlideSlopeGeometry.AltitudeAtDistance(entryDistNm, runway.ElevationFt, ctx.Category);

        IReadOnlyList<RunwayInfo> airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            runway,
            ctx.Category,
            ctx.Aircraft.AircraftType,
            ctx.Aircraft.WindSpeedKts,
            direction,
            PatternEntryLeg.Final,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            airportRunways: airportRunways,
            authoredRunway: (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator)
        );

        var entryPhase = new PatternEntryPhase
        {
            EntryLat = entry.Lat,
            EntryLon = entry.Lon,
            PatternAltitude = entryAltitude,
            Kind = PatternEntryKind.Final,
        };

        Log.LogDebug(
            "[VfrFollow] {Callsign}: sequenced onto {Rwy} final behind {Lead} ({Dir}) — awaiting landing clearance",
            ctx.Aircraft.Callsign,
            runway.Designator,
            TargetCallsign,
            direction
        );

        InstallJoinedCircuit(ctx, runway, direction, [entryPhase, .. circuit], keepFollowing: true);
    }

    /// <summary>
    /// Replace the follower's phase list with <paramref name="legs"/> flown to <paramref name="runway"/> in
    /// <paramref name="direction"/> traffic, and start it. With <paramref name="keepFollowing"/> the follow target is kept
    /// so the pattern phases keep spacing behind the lead; without it the follow is over. The clearance standing on the
    /// pursuit phase list (a CLAND, TG, SG, LA or COPT issued while the follower was still pursuing its lead) is captured
    /// first and carried onto the new list (<see cref="ApplyArmedLandingClearance"/>), as is an armed pattern runway
    /// (<see cref="CarryArmedPatternRunway"/>).
    /// </summary>
    private void InstallJoinedCircuit(PhaseContext ctx, RunwayInfo runway, PatternDirection direction, List<Phase> legs, bool keepFollowing)
    {
        PhaseList phases = ctx.Aircraft.Phases ?? new PhaseList();
        ClearanceType? armedClearance = phases.LandingClearance;
        string? armedClearedRunwayId = phases.ClearedRunwayId;
        RunwayInfo patternRunway = CarryArmedPatternRunway(ctx.Aircraft, phases, runway, direction);
        phases.Clear(ctx);
        ctx.Aircraft.Phases = new PhaseList
        {
            AssignedRunway = runway,
            TrafficDirection = direction,
            PatternRunway = patternRunway,
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
    private static RunwayInfo CarryArmedPatternRunway(AircraftState aircraft, PhaseList previous, RunwayInfo joinRunway, PatternDirection direction)
    {
        if (
            (previous.PatternRunway is not { } armed)
            || (previous.AssignedRunway is not { } flown)
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
            $"{aircraft.Callsign}: {directionWord} traffic runway {RunwayIdentifier.ToDisplayDesignator(armed.Designator)} stays armed — will leave the {RunwayIdentifier.ToDisplayDesignator(joinRunway.Designator)} sequence after the next {DescribeArmedTerminator(previous.LandingClearance)}"
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
        if ((armedClearance is not { } clearance) || !ArmedClearanceTerminal(clearance, out Phase? terminal) || (aircraft.Phases is null))
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
    /// touch-and-go for the option) — or null for a landing clearance, whose chain already ends in the landing.
    /// </summary>
    private static bool ArmedClearanceTerminal(ClearanceType clearance, out Phase? terminal)
    {
        bool optionFamily =
            clearance
            is ClearanceType.ClearedTouchAndGo
                or ClearanceType.ClearedForOption
                or ClearanceType.ClearedStopAndGo
                or ClearanceType.ClearedLowApproach;
        terminal = optionFamily ? Commands.PatternCommandHandler.OptionClearanceTerminal(clearance) : null;
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
            Requirements = Requirements.Count > 0 ? [.. Requirements.Select(r => r.ToSnapshot())] : null,
            TargetCallsign = TargetCallsign,
            LeadLandingRunway = _leadLandingRunway?.ToSnapshot(),
            WidenActive = _widen.Active,
            WidenSide = _widen.Side,
            STurnCallCooldownSeconds = _sTurnCallCooldownSeconds,
            PatternReturn = PatternReturn is { } patternReturn
                ? new FollowPatternReturnDto
                {
                    Runway = patternReturn.Runway.ToSnapshot(),
                    Direction = (int)patternReturn.Direction,
                    PatternAltitudeFt = patternReturn.PatternAltitudeFt,
                    FromBase = patternReturn.FromBase,
                }
                : null,
            LeadPath = _leadPath.ToSnapshot(),
            LeadBase = _leadBase is { } leadBase
                ? new FollowLeadBaseDto
                {
                    Runway = leadBase.Runway.ToSnapshot(),
                    Waypoints = leadBase.Waypoints.ToSnapshot(),
                    StartLat = leadBase.StartPoint.Lat,
                    StartLon = leadBase.StartPoint.Lon,
                    FinalDistanceNm = leadBase.FinalDistanceNm,
                    LegTrackDeg = leadBase.LegTrack.Degrees,
                }
                : null,
        };

    public static VfrFollowPhase FromSnapshot(VfrFollowPhaseDto dto)
    {
        FollowPatternReturn? patternReturn = dto.PatternReturn is { } returnDto
            ? new FollowPatternReturn(
                RunwayInfo.FromSnapshot(returnDto.Runway),
                (PatternDirection)returnDto.Direction,
                returnDto.PatternAltitudeFt,
                returnDto.FromBase ?? false
            )
            : null;
        var phase = new VfrFollowPhase(dto.TargetCallsign, patternReturn) { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
        phase.RestoreRequirements(dto.Requirements);
        if (dto.LeadLandingRunway is not null)
        {
            phase._leadLandingRunway = RunwayInfo.FromSnapshot(dto.LeadLandingRunway);
        }
        phase._widen.Active = dto.WidenActive;
        phase._widen.Side = dto.WidenSide;
        phase._sTurnCallCooldownSeconds = dto.STurnCallCooldownSeconds;
        phase._leadPath.RestoreSnapshot(dto.LeadPath);
        if (dto.LeadBase is { } leadBase)
        {
            phase._leadBase = new LeadBaseJoin(
                RunwayInfo.FromSnapshot(leadBase.Runway),
                PatternWaypoints.FromSnapshot(leadBase.Waypoints),
                new LatLon(leadBase.StartLat, leadBase.StartLon),
                leadBase.FinalDistanceNm,
                new TrueHeading(leadBase.LegTrackDeg)
            );
        }
        return phase;
    }
}

/// <summary>
/// The pattern a follower flew before FOLLOW sent it into free pursuit of a lead with no runway: the runway,
/// the circuit direction and the pattern altitude (feet MSL) it re-enters when the follow ends.
/// </summary>
/// <see cref="FromBase"/> is true when the pursuit started from the base leg, which caps its altitude at the
/// present altitude rather than climbing back to pattern altitude.
public sealed record FollowPatternReturn(RunwayInfo Runway, PatternDirection Direction, double PatternAltitudeFt, bool FromBase);
