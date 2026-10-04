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
///
/// A follower level with or ahead of a lead on base or final, stuck alongside it at the excursion's offset cap, or breaking
/// off its own base for spacing turns out to the downwind heading with one call, holds an offset band from the final, and
/// turns base behind the lead once it has passed abeam and the base-now path leaves the pattern spacing
/// (<see cref="TryStartTurnOut"/>).
/// </summary>
public sealed class VfrFollowPhase(string targetCallsign, FollowPatternReturn? patternReturn) : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("VfrFollowPhase");

    /// <summary>Distance from the lead's downwind abeam point at which we auto-join the pattern.</summary>
    public const double JoinRangeNm = 3.0;

    /// <summary>Maximum distance follower-to-lead allowed at pattern join — guards against joining a stale pattern when the lead has moved.</summary>
    public const double MaxJoinGapNm = 5.0;

    /// <summary>
    /// The final-join in-trail floor behind a lead with no wake minimum for this follower: 1.5 NM, a YAAT convention (the pattern spacing behind a
    /// turboprop), not an FAA figure. It keeps the follower behind the traffic (AIM 4-3-4.d). Behind a lead that carries a wake minimum for this
    /// follower, <see cref="WakeLeadInTrailFloorNm"/> applies instead.
    /// </summary>
    public const double SameRunwayInTrailFloorNm = 1.5;

    /// <summary>
    /// The final-join in-trail floor behind a lead that carries a wake minimum for this follower. A judgement call: no interval is published for a
    /// pilot landing behind a larger aircraft on a visual follow, so 2.5 NM borrows the two-minute interval the AIM recommends to pilots landing
    /// after a larger aircraft's low approach, missed approach or touch-and-go ("an interval of at least 2 minutes", AIM 7-4-6.b.8), at a light
    /// single's ~75 kt final groundspeed.
    /// </summary>
    public const double WakeLeadInTrailFloorNm = 2.5;

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

    /// <summary>Farthest (nm) from the runway's threshold a follower may be and still turn out behind a lead on base or final.</summary>
    public const double TurnOutRangeNm = 5.0;

    /// <summary>Width (nm) of a turned-out follower's offset band from the final centerline, above its floor (the excursion's offset cap).</summary>
    public const double TurnOutBandWidthNm = 1.0;

    /// <summary>Angle (deg) off the downwind heading a turned-out follower flies to regain its offset band.</summary>
    public const double TurnOutCorrectionDeg = 30.0;

    /// <summary>How far (nm) past the pattern spacing the base-now path must leave the follower behind the lead before it turns base.</summary>
    public const double TurnOutExitMarginNm = 0.1;

    /// <summary>How far (nm, along the final) past its turn-out point a follower flies the downwind heading waiting for the lead.</summary>
    public const double TurnOutMaxExtensionNm = 2.0;

    /// <summary>Farthest (nm, along the final from the threshold) a follower flies the downwind heading waiting for the lead.</summary>
    public const double TurnOutMaxAlongFinalNm = 6.0;

    /// <summary>How far (nm) short of its gap a follower alongside the lead at the offset cap must be for its hold to count as stalled.</summary>
    public const double StallShortfallNm = 0.1;

    /// <summary>Least growth (nm) of the gap over <see cref="StallWindowSeconds"/> for a hold alongside the lead not to count as stalled.</summary>
    public const double StallMinGrowthNm = 0.05;

    /// <summary>How long (s) a hold alongside the lead at the offset cap is watched before it is judged stalled or not.</summary>
    public const double StallWindowSeconds = 20.0;

    /// <summary>How far (nm) inside the offset cap a follower still counts as at the cap.</summary>
    private const double StallCapToleranceNm = 0.05;

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

    /// <summary>The turn-out to the downwind heading under way (<see cref="TryStartTurnOut"/>), or null.</summary>
    private FollowTurnOut? _turnOut;

    /// <summary>The departure-leg hold owed before this pursuit steers at its lead, or null (<see cref="ClimbOutGate"/>).</summary>
    private FollowClimbOutGate? _climbOutGate;

    /// <summary>A base break-off asked this pursuit to start with a turn-out (<see cref="RequestTurnOut"/>).</summary>
    private bool _turnOutRequested;

    /// <summary>The open parallel-hold stall window (<see cref="ParallelHoldStalled"/>), or null.</summary>
    private ParallelHoldWindow? _parallelHold;

    public string TargetCallsign { get; private set; } = targetCallsign;

    /// <summary>
    /// The circuit the follower left when FOLLOW moved it off a pattern leg into this pursuit, or null
    /// when the pursuit did not start from a pattern leg.
    /// </summary>
    public FollowPatternReturn? PatternReturn { get; } = patternReturn;

    /// <summary>
    /// The departure leg this pursuit owes before it may steer at its lead: the gate the install handed it
    /// (<see cref="Commands.CommandDispatcher.InstallVfrFollowPhase"/>), else null. Read-only outside this phase: only the hold
    /// itself clears it, on the first tick the crosswind turn is legal, after which this is an ordinary free pursuit.
    /// </summary>
    internal FollowClimbOutGate? ClimbOutGate
    {
        get => _climbOutGate;
        init => _climbOutGate = value;
    }

    /// <summary>
    /// The departure-leg hold pursuing <paramref name="aircraft"/> owes: its upwind leg's gate when that is the leg it is on (a
    /// FOLLOW issued on the upwind, or the hand-over from a climb-out to one); when it is still climbing out (a go-around, or a
    /// closed-traffic climb off its pattern runway or a close parallel of it), the runway it is flying: that runway's heading,
    /// past the farther departure end of it and the pattern runway (<see cref="PatternGeometry.TransitionDepartureEnd"/>), at
    /// the pattern runway's pattern altitude less the turn margin; else null — a pursuit started anywhere else may steer at its
    /// lead at once. A crossing-runway transition (<see cref="CrossingTransitionGate"/>) has its own gate.
    /// </summary>
    internal static FollowClimbOutGate? ClimbOutGateFor(AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (CrossingTransitionGate(aircraft, groundLayout) is { } crossingGate)
        {
            return crossingGate;
        }

        Phase? current = aircraft.Phases?.CurrentPhase;
        if (current is UpwindPhase { Waypoints: { } waypoints })
        {
            return FollowClimbOutGate.FromWaypoints(waypoints);
        }

        bool closedTrafficClimb = (current is TakeoffPhase) && AirborneFollowHelper.IsClosedTrafficClimb(aircraft);
        bool climbingOutOfPattern = (current is GoAroundPhase) || closedTrafficClimb;
        if (climbingOutOfPattern && (aircraft.Phases?.AssignedRunway is { } patternRunway))
        {
            // A closed-traffic climb off a close parallel flies that runway's centerline; the pattern runway sets the altitude.
            RunwayInfo flownRunway = (closedTrafficClimb ? aircraft.Phases.DepartureRunway : null) ?? patternRunway;
            return FollowClimbOutGate.ForClimbOut(flownRunway, patternRunway, ResolvePatternAltitudeFt(aircraft, patternRunway, groundLayout));
        }

        return null;
    }

    /// <summary>
    /// The departure-leg hold of a closed-traffic climb off a runway crossing its pattern runway, or of its upwind
    /// (<see cref="AirborneFollowHelper.IsCrossingTransitionClimb"/>): the flown runway's heading, held past that runway's own
    /// departure end (the upwind's crosswind-turn point; the crossing runway's end may lie anywhere along another heading), at
    /// the pattern runway's pattern altitude less the turn margin (AIM §4-3-2.c.1, FIG 4-3-2/4-3-3 keys 4–5). Null for any
    /// other aircraft.
    /// </summary>
    private static FollowClimbOutGate? CrossingTransitionGate(AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (
            !AirborneFollowHelper.IsCrossingTransitionClimb(aircraft)
            || (aircraft.Phases?.AssignedRunway is not { } patternRunway)
            || (aircraft.Phases.DepartureRunway is not { } flown)
        )
        {
            return null;
        }

        if (aircraft.Phases.CurrentPhase is UpwindPhase { Waypoints: { } waypoints })
        {
            return FollowClimbOutGate.FromWaypoints(waypoints) with
            {
                MinTurnAltitude = CrossingTurnAltitudeFt(aircraft, patternRunway, groundLayout),
            };
        }

        return FollowClimbOutGate.ForClimbOut(flown, flown, ResolvePatternAltitudeFt(aircraft, patternRunway, groundLayout));
    }

    /// <summary>
    /// The legal crosswind-turn altitude of a crossing transition's upwind: <paramref name="patternRunway"/>'s pattern altitude
    /// (<see cref="ResolvePatternAltitudeFt"/>) less <see cref="UpwindPhase.PatternHandoffMarginFt"/>, not the runway flown's.
    /// </summary>
    private static double CrossingTurnAltitudeFt(AircraftState aircraft, RunwayInfo patternRunway, AirportGroundLayout? groundLayout) =>
        ResolvePatternAltitudeFt(aircraft, patternRunway, groundLayout) - UpwindPhase.PatternHandoffMarginFt;

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
        _turnOut = null;
        _turnOutRequested = false;
        _parallelHold = null;
    }

    /// <summary>True while the follower is turned out to the downwind heading behind its lead (<see cref="TryStartTurnOut"/>).</summary>
    public bool TurningOut => _turnOut is not null;

    /// <summary>
    /// The circuit a new lead chosen during this pursuit is compared on: the turn-out's own circuit while turning out, else
    /// the circuit this pursuit left from base (<see cref="FollowPatternReturn.FromBase"/>); null otherwise.
    /// </summary>
    internal FollowPatternReturn? NewLeadCircuit => _turnOut?.Circuit ?? ((PatternReturn is { FromBase: true } fromBase) ? fromBase : null);

    /// <summary>
    /// Starts this pursuit with a turn-out on its first tick, when the lead is on base or final to a runway and the follower
    /// on its pattern side in range (<see cref="TurnOutCircuitFor"/>): a base follower breaking off for spacing
    /// (<see cref="BaseFollowSpacing"/>) is level with or ahead of the lead by the break-off's own projection.
    /// </summary>
    internal void RequestTurnOut() => _turnOutRequested = true;

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

        if (_turnOut is { } turnOut)
        {
            return TickTurnOut(ctx, lead, turnOut);
        }

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
            ReturnToPattern(ctx, LifecycleReturn(ctx, lead));
            return true;
        }

        // CheckLeadLifecycle ends the follow when the lead is not found, so a null here ends it the same way.
        lead = ctx.AircraftLookup?.Invoke(TargetCallsign);
        if (lead is null)
        {
            AirborneFollowHelper.ClearFollowState(ctx.Aircraft);
            ReturnToPattern(ctx, PatternReturn);
            return true;
        }

        // A pursuit that started on the upwind holds the departure leg until the crosswind turn is legal: no pursuit heading,
        // no join and no spacing from a few hundred feet over the runway, whatever the traffic ahead is doing (AIM §4-3-2.c.1,
        // FIG 4-3-2 keys 4–5). The gate clears on the first tick the turn is legal.
        if (HoldDepartureLeg(ctx))
        {
            return false;
        }

        SeedLeadPath(lead);
        _leadPath.Record(lead.Position);
        RememberLeadBase(ctx.Aircraft, lead);
        double gapNm = GeoMath.DistanceNm(ctx.Aircraft.Position, lead.Position);

        // A base break-off that asked for a turn-out takes it before any join: joining the lead's circuit from here would
        // put the follower back on the base it just broke off.
        if (_turnOutRequested && TryStartTurnOut(ctx, lead, stalledHold: false))
        {
            return false;
        }

        // If the lead is flying its base, join that base at the point it began; otherwise, if the
        // lead is in a pattern, see if we're close enough to join; if it is on a straight-in
        // final/landing (no pattern waypoints to join), sequence onto its runway's final once we
        // are trailing and aligned. Every join replaces the phase list, so this phase is no
        // longer current.
        if (TryJoinLeadBase(ctx, lead, gapNm) || TryJoinLeadPattern(ctx, lead, gapNm) || TryJoinLeadFinal(ctx, lead))
        {
            return true;
        }

        // Level with or ahead of a lead on base or final (or asked to by a base break-off): turn out to the downwind heading.
        if (TryStartTurnOut(ctx, lead, stalledHold: false))
        {
            return false;
        }

        return TickFreePursuit(ctx, lead);
    }

    /// <summary>
    /// Holds the departure leg while <see cref="ClimbOutGate"/> is set: the upwind heading and the leg's speed, with no pursuit
    /// heading and no join, until the aircraft is past the departure end at a legal turn altitude (AIM §4-3-2.c.1). The
    /// altitude target is left alone — a circuit's pattern altitude is set once in <see cref="OnStart"/> (AIM 4-3-3), so a
    /// controller's later CM or DM stands through the hold. True while it holds, so the caller's tick ends there; the gate is
    /// cleared on the first tick the turn is legal, after which the pursuit flies as any other.
    /// </summary>
    private bool HoldDepartureLeg(PhaseContext ctx)
    {
        if (_climbOutGate is not { } gate)
        {
            return false;
        }

        if (UpwindPhase.PastDepartureEnd(ctx.Aircraft.Position, gate.DepartureEnd, gate.UpwindHeading) && AtLegalTurnAltitude(ctx, gate))
        {
            _climbOutGate = null;
            Log.LogDebug(
                "[VfrFollow] {Callsign}: past the departure end at {Alt:F0}ft, steering for {Target}",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.Altitude,
                TargetCallsign
            );
            return false;
        }

        ctx.Targets.TargetTrueHeading = gate.UpwindHeading;
        HoldUpwindSpeed(ctx);
        return true;
    }

    /// <summary>
    /// The altitude half of the crosswind-turn condition (AIM §4-3-2.c.1): the leg's own minimum turn altitude, or — when the
    /// controller has cleared the aircraft below it — that cleared altitude reached within the physics' capture tolerance
    /// (<see cref="FlightPhysics.AltitudeSnapFt"/>). A CM or DM below pattern altitude less the margin is the aircraft's
    /// clearance, so the pursuit turns when it gets there instead of holding the upwind for an altitude it was told not to
    /// climb to. The assigned altitude stands in for the target once the physics has captured it, which is what clears the
    /// target.
    /// </summary>
    private static bool AtLegalTurnAltitude(PhaseContext ctx, FollowClimbOutGate gate)
    {
        if (ctx.Aircraft.Altitude >= gate.MinTurnAltitude)
        {
            return true;
        }

        double? cleared = ctx.Targets.TargetAltitude ?? ctx.Targets.AssignedAltitude;
        return cleared is { } target && (target < gate.MinTurnAltitude) && (ctx.Aircraft.Altitude >= (target - FlightPhysics.AltitudeSnapFt));
    }

    /// <summary>
    /// The speed a departure leg flies, exactly as <see cref="UpwindPhase"/> flies it so the two legs agree: the downwind
    /// baseline unless the controller assigned a speed (7110.65 §5-7-4), slowed for a lead followed too closely and never sped
    /// up to chase one.
    /// </summary>
    private static void HoldUpwindSpeed(PhaseContext ctx)
    {
        double baseline = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
        if (!ctx.Targets.HasExplicitSpeedCommand)
        {
            ctx.Targets.TargetSpeed = baseline;
        }

        if (ctx.Aircraft.Approach.FollowingCallsign is null)
        {
            return;
        }

        double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        double? adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
        if (adjusted is not null)
        {
            ctx.Targets.TargetSpeed = Math.Min(adjusted.Value, baseline);
        }
    }

    /// <summary>
    /// A new follow (or a new lead) seeds the path from the lead's position history, so a follower told to follow mid-leg
    /// measures its gap along the path the lead has just flown rather than a straight line.
    /// </summary>
    private void SeedLeadPath(AircraftState lead)
    {
        if (_leadPath.Points.Count > 0)
        {
            return;
        }

        foreach ((double lat, double lon) in lead.PositionHistory)
        {
            _leadPath.Record(new LatLon(lat, lon));
        }
    }

    /// <summary>
    /// The circuit a follow ended by <see cref="AirborneFollowHelper.CheckLeadLifecycle"/> re-enters: the landed lead's
    /// runway when the lead is down and its runway is known (the follower could not be sequenced onto that final,
    /// <see cref="TrySequenceBehindLandedLead"/>), otherwise the pattern return.
    /// </summary>
    private FollowPatternReturn? LifecycleReturn(PhaseContext ctx, AircraftState? lead) =>
        ((lead is { IsOnGround: true }) && (_leadLandingRunway is { } landedRunway)) ? ReturnFor(ctx, landedRunway) : PatternReturn;

    /// <summary>
    /// The pattern return for <paramref name="runway"/>: this pursuit's own when it names that runway
    /// (<see cref="OwnReturnFor"/>), else resolved as FOLLOW would.
    /// </summary>
    private FollowPatternReturn ReturnFor(PhaseContext ctx, RunwayInfo runway) =>
        OwnReturnFor(runway) ?? BuildFollowPatternReturn(ctx.Aircraft, runway, ctx.GroundLayout);

    /// <summary>This pursuit's own pattern return when it names <paramref name="runway"/>; otherwise null.</summary>
    private FollowPatternReturn? OwnReturnFor(RunwayInfo runway) =>
        ((PatternReturn is { } patternReturn) && AirborneFollowHelper.IsSameRunway(patternReturn.Runway, runway)) ? patternReturn : null;

    /// <summary>
    /// Remember the lead's landing runway while it is established on a straight-in
    /// final/landing, so the follower can be sequenced onto that runway even after
    /// the lead touches down. Pattern-flying leads are handled by TryJoinLeadPattern.
    /// </summary>
    private void RememberLeadLandingRunway(AircraftState? lead)
    {
        if (
            (lead is { IsOnGround: false })
            && (lead.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase)
            && (lead.Phases.AssignedRunway is { } leadRunway)
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
    /// The final join's geometry gates apply (<see cref="CanSequenceOntoLandedFinal"/>), all but the in-trail gate (the lead
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

        if (!CanSequenceOntoLandedFinal(ctx.Aircraft, landedRunway))
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
    /// </summary>
    internal static bool CanSequenceOntoLandedFinal(AircraftState follower, RunwayInfo runway)
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
        if (ParallelHoldStalled(ctx, lead, spacing) && TryStartTurnOut(ctx, lead, stalledHold: true))
        {
            return false;
        }

        AnnounceNewExcursion(ctx, wasWidening);
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
    /// Ends the follow into an extended downwind of <paramref name="runway"/>'s <paramref name="direction"/> circuit at
    /// <paramref name="altitudeFt"/>, from where the follower is: the downwind heading, parallel to the final and never
    /// converging on it, until the controller turns it base (TB) or re-sequences it.
    /// </summary>
    private void InstallExtendedDownwind(PhaseContext ctx, RunwayInfo runway, PatternDirection direction, double altitudeFt)
    {
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            runway,
            ctx.Category,
            ctx.Aircraft.AircraftType,
            ctx.Aircraft.WindSpeedKts,
            direction,
            PatternEntryLeg.Downwind,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: altitudeFt,
            airportRunways: NavigationDatabase.Instance.GetRunways(runway.AirportId),
            authoredRunway: (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator)
        );
        int downwindIndex = circuit.FindIndex(phase => phase is DownwindPhase);
        if (downwindIndex < 0)
        {
            throw new InvalidOperationException($"A downwind circuit for runway {runway.Designator} was built without a downwind leg");
        }

        ((DownwindPhase)circuit[downwindIndex]).IsExtended = true;
        InstallJoinedCircuit(ctx, runway, direction, circuit[downwindIndex..], keepFollowing: false);
    }

    /// <summary>
    /// The circuit a follower leaves when it goes into pursuit from a pattern leg (a FOLLOW, or a base follower breaking off
    /// for spacing): its runway, and the direction and pattern altitude of the circuit it was flying (read from that
    /// circuit's waypoints, or resolved as the circuit builder would when no leg carries them).
    /// </summary>
    internal static FollowPatternReturn BuildFollowPatternReturn(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout? groundLayout)
    {
        bool fromBase = aircraft.Phases?.CurrentPhase is BasePhase;
        PatternWaypoints? waypoints = AirborneFollowHelper.FirstPatternWaypoints(aircraft.Phases);
        if (waypoints is not null)
        {
            return new FollowPatternReturn(runway, waypoints.Direction, waypoints.PatternAltitude, fromBase);
        }

        PatternDirection direction =
            aircraft.Phases?.TrafficDirection
            ?? aircraft.Pattern.TrafficDirection
            ?? GoAroundHelper.InferDefaultPatternDirection(runway)
            ?? PatternDirection.Left;
        return new FollowPatternReturn(runway, direction, ResolvePatternAltitudeFt(aircraft, runway, groundLayout), fromBase);
    }

    /// <summary>
    /// The pattern altitude (feet MSL) of <paramref name="aircraft"/>'s circuit to <paramref name="runway"/>, resolved as the
    /// circuit builder would: an authored or commanded override (<see cref="PatternGeometry.ResolveAuthoredOverrides"/>, with
    /// the aircraft's own <c>Pattern.AltitudeOverrideFt</c>), else its category's pattern altitude above the field.
    /// </summary>
    internal static double ResolvePatternAltitudeFt(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout? groundLayout)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        (double? _, double? altitudeOverrideFt) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
            category,
            commandSizeNm: null,
            aircraft.Pattern.AltitudeOverrideFt
        );
        return altitudeOverrideFt ?? (runway.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(category));
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
        var armedBefore = ArmedPatternState.Of(phases);
        phases.AssignedRunway = patternReturn.Runway;
        CommandResult entry = Commands.PatternCommandHandler.TryEnterPattern(
            aircraft,
            patternReturn.Direction,
            ReturnEntryLeg(aircraft, patternReturn.Runway),
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
        KeepArmedPatternRunway(aircraft, armedBefore, patternReturn);
    }

    /// <summary>
    /// <see cref="Commands.PatternCommandHandler.TryEnterPattern"/> builds a new list without a
    /// <see cref="PhaseList.PatternRunway"/>, so a pattern runway armed before the follow (<c>COPT MLT 28L</c> on the 28R
    /// circuit) would be dropped by the re-entry. Carry it over as a join does (<see cref="CarryArmedPatternRunway"/>),
    /// unless the entry itself armed one (a pre-issued clearance's pattern modifier).
    /// </summary>
    private static void KeepArmedPatternRunway(AircraftState aircraft, ArmedPatternState armedBefore, FollowPatternReturn patternReturn)
    {
        if (aircraft.Phases is not { PatternRunway: null } rebuilt)
        {
            return;
        }

        RunwayInfo patternRunway = CarryArmedPatternRunway(aircraft, armedBefore, patternReturn.Runway, patternReturn.Direction);
        if (!AirborneFollowHelper.IsSameRunway(patternRunway, patternReturn.Runway))
        {
            rebuilt.PatternRunway = patternRunway;
        }
    }

    /// <summary>
    /// The leg a follow's pattern return enters by: the upwind for a follower already past <paramref name="runway"/>'s
    /// threshold and flying the runway's way (it continues upwind, crosswind and downwind, AIM 4-3-3, never turning back onto
    /// the final), otherwise the downwind.
    /// </summary>
    private static PatternEntryLeg ReturnEntryLeg(AircraftState aircraft, RunwayInfo runway)
    {
        double alongNm = AirborneFollowHelper.AlongFinalNm(aircraft.Position, runway);
        bool pastThresholdOutbound = (alongNm < 0.0) && (aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading) < 90.0);
        return pastThresholdOutbound ? PatternEntryLeg.Upwind : PatternEntryLeg.Downwind;
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

        if (
            (lead.Phases?.CurrentPhase is not BasePhase)
            || !CanJoinLeadBase(ctx.Aircraft, join, gapToLeadNm, AirborneFollowHelper.PatternSpacingNm(lead))
        )
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

        // In-trail floor: stay behind the traffic (a pilot "should not take advantage of another aircraft, which is on final approach to land,
        // by cutting in front of, or overtaking that aircraft", AIM 4-3-4.d) and no closer than the same-runway floor, or the wake floor
        // behind a lead that carries a wake minimum for this follower. Until that spacing exists, keep pursuing rather than rolling onto final
        // too close.
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
        RunwayInfo patternRunway = CarryArmedPatternRunway(ctx.Aircraft, ArmedPatternState.Of(phases), runway, direction);
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

    // ─── Turn-out to the downwind heading ───

    /// <summary>
    /// Starts a turn-out to the downwind heading when the lead is on base or final to a runway and the follower is on that
    /// runway's pattern side within <see cref="TurnOutRangeNm"/> of its threshold (<see cref="TurnOutCircuitFor"/>), and the
    /// follower is level with or ahead of the lead (its shortest path to the threshold, <see cref="ShortestPathToThresholdNm"/>,
    /// no longer than the lead's remaining path), its hold alongside the lead has stalled (<paramref name="stalledHold"/>), or
    /// a base break-off asked for it (<see cref="RequestTurnOut"/>). The follower cannot fall behind a same-speed lead, and
    /// continuing to the final ahead of it would be cutting in (AIM 4-3-4).
    /// </summary>
    private bool TryStartTurnOut(PhaseContext ctx, AircraftState lead, bool stalledHold)
    {
        bool requested = _turnOutRequested;
        _turnOutRequested = false;
        if (TurnOutCircuitFor(ctx, lead, requested) is not { } circuit)
        {
            if (requested)
            {
                Log.LogDebug(
                    "[VfrFollow] {Callsign}: no turn-out, {Lead} is no longer on base or final in range",
                    ctx.Aircraft.Callsign,
                    TargetCallsign
                );
            }

            return false;
        }

        string? reason =
            requested ? "base break-off"
            : stalledHold ? "stalled alongside at the offset cap"
            : IsLevelOrAhead(ctx, lead, circuit) ? "level with or ahead"
            : null;
        if (reason is null)
        {
            return false;
        }

        StartTurnOut(ctx, circuit, reason);
        return true;
    }

    /// <summary>
    /// The circuit a turn-out rejoins: the runway the lead is flying its base or final to while it is airborne on either (on the
    /// final in <see cref="FinalApproachPhase"/> or by geometry, <see cref="AirborneFollowHelper.IsOnFinalByGeometry"/>), in
    /// <see cref="TurnOutDirection"/> traffic, at its pattern altitude; null when the lead is on neither, or the follower is not
    /// on that circuit's pattern side, or (unless a base break-off <paramref name="requested"/> the turn-out) not within
    /// <see cref="TurnOutRangeNm"/> of the threshold.
    /// </summary>
    private FollowPatternReturn? TurnOutCircuitFor(PhaseContext ctx, AircraftState lead, bool requested)
    {
        if (lead.IsOnGround || (lead.Phases is not { AssignedRunway: { } runway } leadPhases) || !IsOnBaseOrFinalLeg(lead, runway))
        {
            return null;
        }

        FollowPatternReturn circuit = OwnReturnFor(runway) ?? NewTurnOutCircuit(ctx, runway, leadPhases);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        bool inRange = requested || (GeoMath.DistanceNm(ctx.Aircraft.Position, threshold) <= TurnOutRangeNm);
        return (inRange && IsOnPatternSide(ctx.Aircraft, runway, circuit.Direction)) ? circuit with { FromBase = false } : null;
    }

    /// <summary>
    /// A turn-out circuit to <paramref name="runway"/> this pursuit did not come from: in <see cref="TurnOutDirection"/> traffic,
    /// at the follower's pattern altitude for it (<see cref="ResolvePatternAltitudeFt"/>).
    /// </summary>
    private static FollowPatternReturn NewTurnOutCircuit(PhaseContext ctx, RunwayInfo runway, PhaseList leadPhases) =>
        new(runway, TurnOutDirection(runway, leadPhases), ResolvePatternAltitudeFt(ctx.Aircraft, runway, ctx.GroundLayout), FromBase: false);

    /// <summary>
    /// True when <paramref name="lead"/> flies its base, or its final (in <see cref="FinalApproachPhase"/> or by geometry), to
    /// <paramref name="runway"/>, its own assigned runway.
    /// </summary>
    private static bool IsOnBaseOrFinalLeg(AircraftState lead, RunwayInfo runway) =>
        (lead.Phases?.CurrentPhase is BasePhase or FinalApproachPhase) || AirborneFollowHelper.IsOnFinalByGeometry(lead, runway);

    /// <summary>The traffic direction of a turn-out circuit this pursuit did not come from: the lead's, else the runway's default.</summary>
    private static PatternDirection TurnOutDirection(RunwayInfo runway, PhaseList leadPhases) =>
        leadPhases.TrafficDirection ?? GoAroundHelper.InferDefaultPatternDirection(runway) ?? PatternDirection.Left;

    /// <summary>
    /// True when the follower's shortest path to <paramref name="circuit"/>'s threshold is no longer than the lead's remaining
    /// path (<see cref="AirborneFollowHelper.LeadRemainingPathNm"/>).
    /// </summary>
    private static bool IsLevelOrAhead(PhaseContext ctx, AircraftState lead, FollowPatternReturn circuit) =>
        FollowerPathToThresholdNm(ctx.Aircraft, circuit) <= AirborneFollowHelper.LeadRemainingPathNm(lead, circuit.Runway);

    /// <summary>
    /// <paramref name="aircraft"/>'s shortest path to <paramref name="circuit"/>'s threshold (<see cref="ShortestPathToThresholdNm"/>)
    /// from its present position and heading, at its own turn radius.
    /// </summary>
    internal static double FollowerPathToThresholdNm(AircraftState aircraft, FollowPatternReturn circuit)
    {
        FinalFramePosition frame = FinalFrameOf(aircraft.Position, aircraft.TrueHeading, circuit.Runway, circuit.Direction);
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        return ShortestPathToThresholdNm(frame, BasePhase.TurnRadiusNm(aircraft.GroundSpeed, category));
    }

    /// <summary>
    /// Begins the turn-out: level at the circuit's pattern altitude, climbing back to it from a base descent (an assigned
    /// altitude stands), the spacing excursion dropped, the turn toward the downwind heading, and the one call.
    /// </summary>
    private void StartTurnOut(PhaseContext ctx, FollowPatternReturn circuit, string reason)
    {
        var turnOut = new FollowTurnOut(circuit, ctx.Aircraft.Position);
        _turnOut = turnOut;
        _widen.Active = false;
        _widen.Side = 0;
        _parallelHold = null;
        if (ctx.Targets.AssignedAltitude is null)
        {
            ctx.Targets.TargetAltitude = circuit.PatternAltitudeFt;
            ctx.Targets.DesiredVerticalRate = null;
        }

        Log.LogDebug(
            "[VfrFollow] {Callsign}: turning out to the {Rwy} downwind heading behind {Lead} ({Reason})",
            ctx.Aircraft.Callsign,
            circuit.Runway.Designator,
            TargetCallsign,
            reason
        );
        SteerTurnOut(ctx, turnOut);
        ctx.Targets.TargetSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            Pilot.PilotResponder.BuildTurningDownwindForSpacing(ctx.Aircraft, TargetCallsign),
            Pilot.PilotResponder.SoloPositionsTowerApproach
        );
    }

    /// <summary>
    /// One tick of the turn-out. A lead that lands, goes around or leaves the final ends the follow: the follower is already on a
    /// pattern-side downwind, so it re-enters the circuit as a downwind with its clearance, and says nothing. A lead lost from
    /// sight ends it the same way, with the loss handled as any follow's.
    /// </summary>
    private bool TickTurnOut(PhaseContext ctx, AircraftState? lead, FollowTurnOut turnOut)
    {
        if ((lead is not null) && !IsOnBaseOrFinalTo(lead, turnOut.Circuit.Runway))
        {
            Log.LogDebug(
                "[VfrFollow] {Callsign}: {Lead} is no longer on base or final to {Rwy}, ending the turn-out",
                ctx.Aircraft.Callsign,
                TargetCallsign,
                turnOut.Circuit.Runway.Designator
            );
            AirborneFollowHelper.ClearFollowState(ctx.Aircraft);
            ReturnToPattern(ctx, turnOut.Circuit);
            return true;
        }

        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            ReturnToPattern(ctx, turnOut.Circuit);
            return true;
        }

        // CheckLeadLifecycle ends the follow when the lead is not found, so a null here ends it the same way.
        if (lead is null)
        {
            AirborneFollowHelper.ClearFollowState(ctx.Aircraft);
            ReturnToPattern(ctx, turnOut.Circuit);
            return true;
        }

        return TickTurnOutLeg(ctx, lead, turnOut);
    }

    /// <summary>
    /// True while <paramref name="lead"/> is airborne on its base, its final (<see cref="IsOnBaseOrFinalLeg"/>) or its landing to
    /// <paramref name="runway"/>.
    /// </summary>
    private static bool IsOnBaseOrFinalTo(AircraftState lead, RunwayInfo runway) =>
        !lead.IsOnGround
        && (lead.Phases?.AssignedRunway is { } leadRunway)
        && AirborneFollowHelper.IsSameRunway(leadRunway, runway)
        && ((lead.Phases.CurrentPhase is LandingPhase) || IsOnBaseOrFinalLeg(lead, runway));

    /// <summary>
    /// Flies the turned-out downwind: turns base behind the lead once the reversal is done, the exit holds
    /// (<see cref="ShouldExitTurnOut"/>, judged by the base projection, <see cref="BaseFollowSpacing.ProjectedBaseGapNm"/>, so
    /// the base it installs keeps) and the base crosses no parallel runway's final; past the distance limit
    /// (<see cref="TurnOutPastLimit"/>) holds the heading for the controller; otherwise keeps the offset band at the speed floor.
    /// </summary>
    private bool TickTurnOutLeg(PhaseContext ctx, AircraftState lead, FollowTurnOut turnOut)
    {
        RunwayInfo runway = turnOut.Circuit.Runway;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        FinalFramePosition frame = FinalFrameOf(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, runway, turnOut.Circuit.Direction);
        bool reversed = ctx.Aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading.ToReciprocal()) <= TurnOutCorrectionDeg;
        bool leadPassedAbeam = AirborneFollowHelper.AlongFinalNm(lead.Position, runway) < frame.AlongNm;
        if (
            reversed
            && ShouldExitTurnOut(
                leadPassedAbeam,
                BaseFollowSpacing.ProjectedBaseGapNm(ctx, lead, runway),
                AirborneFollowHelper.PatternSpacingNm(lead)
            )
            && !CapturePathCrossesParallelFinal(
                ctx.Aircraft.Position,
                GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), frame.AlongNm),
                runway
            )
        )
        {
            TurnBaseBehindLead(ctx, turnOut, frame);
            return true;
        }

        double startAlongNm = AirborneFollowHelper.AlongFinalNm(turnOut.StartPoint, runway);
        if (TurnOutPastLimit(frame.AlongNm, startAlongNm))
        {
            HoldForBaseTurn(ctx, turnOut, frame);
            return true;
        }

        SteerTurnOut(ctx, turnOut);
        ctx.Targets.TargetSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        return false;
    }

    /// <summary>
    /// Ends the turn-out with a base turn in the pattern direction from where the follower is: the base, final and landing on
    /// the circuit's runway, the base's final-turn distance the follower's present distance out, at the level it held, still
    /// following, with its clearance carried.
    /// </summary>
    private void TurnBaseBehindLead(PhaseContext ctx, FollowTurnOut turnOut, FinalFramePosition frame)
    {
        FollowPatternReturn circuit = turnOut.Circuit;
        List<Phase> legs = PatternBuilder.BuildCircuit(
            circuit.Runway,
            ctx.Category,
            ctx.Aircraft.AircraftType,
            ctx.Aircraft.WindSpeedKts,
            circuit.Direction,
            PatternEntryLeg.Base,
            touchAndGo: false,
            finalDistanceNm: frame.AlongNm,
            patternSizeNm: null,
            altitudeOverrideFt: circuit.PatternAltitudeFt,
            airportRunways: NavigationDatabase.Instance.GetRunways(circuit.Runway.AirportId),
            authoredRunway: (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(circuit.Runway.Designator)
        );
        Log.LogDebug(
            "[VfrFollow] {Callsign}: {Lead} has passed, turning base to {Rwy} {Along:F2} nm out",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            circuit.Runway.Designator,
            frame.AlongNm
        );
        InstallJoinedCircuit(ctx, circuit.Runway, circuit.Direction, legs, keepFollowing: true);
    }

    /// <summary>
    /// The turn-out ran out of room before the lead passed and left space behind it: the follow ends into an extended downwind
    /// from where the follower is, holding the heading for the controller's base turn (B's no-room rule), with an RPO note and
    /// no second call.
    /// </summary>
    private void HoldForBaseTurn(PhaseContext ctx, FollowTurnOut turnOut, FinalFramePosition frame)
    {
        Log.LogDebug(
            "[VfrFollow] {Callsign}: no room to turn base behind {Lead}, {Along:F2} nm out; holding the downwind heading",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            frame.AlongNm
        );
        ctx.Aircraft.PendingWarnings.Add(
            $"{ctx.Aircraft.Callsign} turned out behind {TargetCallsign} with no room to turn base behind it, holding the downwind heading, awaiting a base turn"
        );
        InstallExtendedDownwind(ctx, turnOut.Circuit.Runway, turnOut.Circuit.Direction, turnOut.Circuit.PatternAltitudeFt);
    }

    /// <summary>
    /// Steers the turn-out. Until the heading is within <see cref="TurnOutCorrectionDeg"/> of the downwind heading, the reversal
    /// turn toward it, the way that first moves away from the final (<see cref="ReversalTurn"/>). Then the downwind heading,
    /// corrected <see cref="TurnOutCorrectionDeg"/> outward below the offset band's floor (the excursion's offset cap,
    /// <see cref="ExcursionLimitsFor"/>) or inward above its ceiling (<see cref="TurnOutOffsetCorrection"/>), unless the
    /// correction would carry the follower toward a parallel runway's final.
    /// </summary>
    private static void SteerTurnOut(PhaseContext ctx, FollowTurnOut turnOut)
    {
        RunwayInfo runway = turnOut.Circuit.Runway;
        PatternDirection direction = turnOut.Circuit.Direction;
        TrueHeading downwind = runway.TrueHeading.ToReciprocal();
        FinalFramePosition frame = FinalFrameOf(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, runway, direction);
        if (ctx.Aircraft.TrueHeading.AbsAngleTo(downwind) > TurnOutCorrectionDeg)
        {
            ctx.Targets.TargetTrueHeading = downwind;
            ctx.Targets.PreferredTurnDirection = ReversalTurn(frame.HeadingDeg, direction);
            return;
        }

        ctx.Targets.PreferredTurnDirection = null;
        double floorNm = ExcursionLimitsFor(ctx).OffsetCapNm;
        int correction = TurnOutOffsetCorrection(frame.PatternSideNm, floorNm, floorNm + TurnOutBandWidthNm);
        if ((correction != 0) && CorrectionMeetsParallelFinal(ctx.Aircraft.Position, runway, direction, correction, frame.PatternSideNm, floorNm))
        {
            correction = 0;
        }

        ctx.Targets.TargetTrueHeading = TurnOutHeading(downwind, direction, correction);
    }

    /// <summary>
    /// True when an offset correction (+1 outward, -1 inward) would carry the follower toward a parallel runway's extended
    /// centerline within its distance to the band edge plus <see cref="AirborneFollowHelper.TrailParallelFinalMarginNm"/>.
    /// </summary>
    internal static bool CorrectionMeetsParallelFinal(
        LatLon position,
        RunwayInfo runway,
        PatternDirection direction,
        int correction,
        double patternSideNm,
        double floorNm
    )
    {
        int sign = direction == PatternDirection.Right ? 1 : -1;
        // Outward from the downwind heading is to its left in right traffic, to its right in left traffic.
        int side = -sign * correction;
        double toBandEdgeNm = correction > 0 ? floorNm - patternSideNm : patternSideNm - (floorNm + TurnOutBandWidthNm);
        double reachNm = toBandEdgeNm + AirborneFollowHelper.TrailParallelFinalMarginNm;
        return ExcursionMeetsParallelFinal(position, runway.TrueHeading.ToReciprocal(), side, runway, reachNm);
    }

    /// <summary>
    /// The turn toward the downwind heading that first moves the follower away from the final centerline: against the pattern
    /// direction from a heading on the base side of the downwind heading (from base, the 90° turn back), with it otherwise
    /// (from a heading parallel to the final, the 180° turn outward).
    /// </summary>
    internal static TurnDirection ReversalTurn(double headingDeg, PatternDirection direction)
    {
        bool againstPattern = headingDeg < 0.0;
        bool right = (direction == PatternDirection.Right) != againstPattern;
        return right ? TurnDirection.Right : TurnDirection.Left;
    }

    /// <summary>
    /// The offset correction for a follower <paramref name="patternSideNm"/> from the final centerline: +1 (outward) below
    /// <paramref name="floorNm"/>, -1 (inward) above <paramref name="ceilingNm"/>, 0 within the band.
    /// </summary>
    internal static int TurnOutOffsetCorrection(double patternSideNm, double floorNm, double ceilingNm)
    {
        if (patternSideNm < floorNm)
        {
            return 1;
        }

        return patternSideNm > ceilingNm ? -1 : 0;
    }

    /// <summary>The downwind heading turned <see cref="TurnOutCorrectionDeg"/> outward (+1), inward (-1) or not at all (0).</summary>
    internal static TrueHeading TurnOutHeading(TrueHeading downwind, PatternDirection direction, int correction)
    {
        int sign = direction == PatternDirection.Right ? 1 : -1;
        return downwind + (-sign * correction * TurnOutCorrectionDeg);
    }

    /// <summary>
    /// The turn-out's exit: the lead has passed abeam and the gap a base turned now would roll out with
    /// (<see cref="BaseFollowSpacing.ProjectedBaseGapNm"/>) is at least the pattern spacing <paramref name="requiredNm"/> plus
    /// <see cref="TurnOutExitMarginNm"/>.
    /// </summary>
    internal static bool ShouldExitTurnOut(bool leadPassedAbeam, double exitGapNm, double requiredNm) =>
        leadPassedAbeam && (exitGapNm >= (requiredNm + TurnOutExitMarginNm));

    /// <summary>
    /// True when the turned-out follower is <see cref="TurnOutMaxExtensionNm"/> along the final past its turn-out point, or
    /// <see cref="TurnOutMaxAlongFinalNm"/> out from the threshold.
    /// </summary>
    internal static bool TurnOutPastLimit(double alongNm, double startAlongNm) =>
        (alongNm - startAlongNm >= TurnOutMaxExtensionNm) || (alongNm >= TurnOutMaxAlongFinalNm);

    /// <summary>
    /// <paramref name="position"/> and <paramref name="heading"/> in <paramref name="runway"/>'s final frame for a
    /// <paramref name="direction"/> circuit.
    /// </summary>
    internal static FinalFramePosition FinalFrameOf(LatLon position, TrueHeading heading, RunwayInfo runway, PatternDirection direction)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double sign = direction == PatternDirection.Right ? 1.0 : -1.0;
        double alongNm = AirborneFollowHelper.AlongFinalNm(position, runway);
        double patternSideNm = sign * GeoMath.SignedCrossTrackDistanceNm(position, threshold, runway.TrueHeading);
        // Compass bearings grow clockwise; the frame's heading grows toward the pattern side, which is clockwise of the
        // downwind heading in left traffic and counter-clockwise of it in right traffic.
        double offDownwindDeg = ((((runway.TrueHeading.ToReciprocal().Degrees - heading.Degrees) % 360.0) + 540.0) % 360.0) - 180.0;
        double headingDeg = sign * offDownwindDeg;
        return new FinalFramePosition(alongNm, patternSideNm, headingDeg <= -180.0 ? headingDeg + 360.0 : headingDeg);
    }

    /// <summary>
    /// The follower's shortest path (nm) to the threshold, turning to the final in the pattern direction at
    /// <paramref name="turnRadiusNm"/>:
    /// <list type="bullet">
    /// <item><description>from a heading between the base heading and the heading straight away from the centerline, the turn
    /// in the pattern direction onto the base heading, the base to the final-turn point and the quarter-circle final turn
    /// (from the downwind heading, the base-now path);</description></item>
    /// <item><description>from a heading between the base heading and the final heading, the straight leg to the final-turn
    /// point on its own intercept, the turn onto the final and the final; an intercept at or past the threshold, straight
    /// there;</description></item>
    /// <item><description>from a heading in toward the threshold and away from or parallel to the centerline, straight
    /// there.</description></item>
    /// </list>
    /// </summary>
    internal static double ShortestPathToThresholdNm(FinalFramePosition frame, double turnRadiusNm)
    {
        double x = frame.AlongNm;
        double y = frame.PatternSideNm;
        double r = turnRadiusNm;
        if (frame.HeadingDeg is >= -90.0 and <= 90.0)
        {
            double headingRad = frame.HeadingDeg * Math.PI / 180.0;
            double turnRad = headingRad + (Math.PI / 2.0);
            // The turn onto the base heading ends at the circle's point abeam its centre, one radius outward along the final.
            double endX = x + (r * (1.0 + Math.Sin(headingRad)));
            double endY = y - (r * Math.Cos(headingRad));
            return (r * turnRad) + BaseAndFinalNm(endX, endY, r);
        }

        return frame.HeadingDeg < -90.0 ? InterceptPathNm(x, y, frame.HeadingDeg, r) : Math.Sqrt((x * x) + (y * y));
    }

    /// <summary>
    /// A base flown from (<paramref name="x"/>, <paramref name="y"/>) to the final-turn point, the quarter-circle turn and the
    /// final.
    /// </summary>
    private static double BaseAndFinalNm(double x, double y, double r) => Math.Max(0.0, y - r) + (Math.PI / 2.0 * r) + Math.Max(0.0, x - r);

    /// <summary>
    /// A leg flown on <paramref name="headingDeg"/> (between the base heading and the final heading) to the turn onto the final,
    /// the turn and the final; straight to the threshold when the leg meets the centerline at or past it.
    /// </summary>
    private static double InterceptPathNm(double x, double y, double headingDeg, double r)
    {
        double interceptRad = (headingDeg + 180.0) * Math.PI / 180.0;
        double interceptAlongNm = x - (y / Math.Tan(interceptRad));
        if (interceptAlongNm <= 0.0)
        {
            return Math.Sqrt((x * x) + (y * y));
        }

        double legNm = y / Math.Sin(interceptRad);
        double turnLeadNm = r * Math.Tan(interceptRad / 2.0);
        return Math.Max(0.0, legNm - turnLeadNm) + (r * interceptRad) + Math.Max(0.0, interceptAlongNm - turnLeadNm);
    }

    /// <summary>A turn-out under way: the circuit it rejoins (runway, direction, and the altitude it levels at) and where it began.</summary>
    private sealed record FollowTurnOut(FollowPatternReturn Circuit, LatLon StartPoint);

    /// <summary>
    /// The pattern-runway arming a phase list carried before a follow rebuilt it: the armed <see cref="PhaseList.PatternRunway"/>,
    /// the runway being flown (<see cref="PhaseList.AssignedRunway"/>) and the clearance the arming rode on.
    /// </summary>
    private readonly record struct ArmedPatternState(RunwayInfo? PatternRunway, RunwayInfo? FlownRunway, ClearanceType? LandingClearance)
    {
        internal static ArmedPatternState Of(PhaseList phases) => new(phases.PatternRunway, phases.AssignedRunway, phases.LandingClearance);
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
            TurnOut = _turnOut is { } turnOut
                ? new FollowTurnOutDto
                {
                    Runway = turnOut.Circuit.Runway.ToSnapshot(),
                    Direction = (int)turnOut.Circuit.Direction,
                    PatternAltitudeFt = turnOut.Circuit.PatternAltitudeFt,
                    StartLat = turnOut.StartPoint.Lat,
                    StartLon = turnOut.StartPoint.Lon,
                }
                : null,
            TurnOutRequested = _turnOutRequested ? true : null,
            ClimbOutGate = ClimbOutGate is { } climbOutGate
                ? new FollowClimbOutGateDto
                {
                    DepartureEndLat = climbOutGate.DepartureEnd.Lat,
                    DepartureEndLon = climbOutGate.DepartureEnd.Lon,
                    UpwindHeadingDeg = climbOutGate.UpwindHeading.Degrees,
                    MinTurnAltitude = climbOutGate.MinTurnAltitude,
                }
                : null,
            StallWindowStartGapNm = _parallelHold?.StartGapNm,
            StallWindowSeconds = _parallelHold?.Seconds,
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

    /// <summary>Restores the turn-out, its request and the parallel-hold window; each is absent in older snapshots.</summary>
    private void RestoreTurnOutState(VfrFollowPhaseDto dto)
    {
        _turnOut = dto.TurnOut is { } turnOut
            ? new FollowTurnOut(
                new FollowPatternReturn(
                    RunwayInfo.FromSnapshot(turnOut.Runway),
                    (PatternDirection)turnOut.Direction,
                    turnOut.PatternAltitudeFt,
                    false
                ),
                new LatLon(turnOut.StartLat, turnOut.StartLon)
            )
            : null;
        _turnOutRequested = dto.TurnOutRequested ?? false;
        _parallelHold =
            (dto.StallWindowStartGapNm is { } startGapNm) && (dto.StallWindowSeconds is { } seconds)
                ? new ParallelHoldWindow(startGapNm, seconds)
                : null;
    }

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
        var phase = new VfrFollowPhase(dto.TargetCallsign, patternReturn)
        {
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            ClimbOutGate = dto.ClimbOutGate is { } climbOutGate
                ? new FollowClimbOutGate(
                    new LatLon(climbOutGate.DepartureEndLat, climbOutGate.DepartureEndLon),
                    new TrueHeading(climbOutGate.UpwindHeadingDeg),
                    climbOutGate.MinTurnAltitude
                )
                : null,
        };
        phase.RestoreRequirements(dto.Requirements);
        if (dto.LeadLandingRunway is not null)
        {
            phase._leadLandingRunway = RunwayInfo.FromSnapshot(dto.LeadLandingRunway);
        }
        phase._widen.Active = dto.WidenActive;
        phase._widen.Side = dto.WidenSide;
        phase._sTurnCallCooldownSeconds = dto.STurnCallCooldownSeconds;
        phase._leadPath.RestoreSnapshot(dto.LeadPath);
        phase.RestoreTurnOutState(dto);
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

/// <summary>
/// The departure leg a pursuit owes before it may steer at its lead: the departure-end / crosswind-turn point, the upwind
/// heading held until the aircraft is over it, and the altitude that (with the point behind) makes the crosswind turn legal
/// (AIM §4-3-2.c.1, FIG 4-3-2 keys 4–5). Derived from the circuit's <see cref="PatternWaypoints"/> exactly as the upwind leg
/// derives its own crosswind turn (<see cref="UpwindPhase.PastDepartureEndAtTurnAltitude"/>), so a follow that starts on the
/// upwind holds it rather than turning at traffic in the downwind at a few hundred feet.
/// </summary>
/// <param name="DepartureEnd">The departure-end / crosswind-turn point, past which the hold ends.</param>
/// <param name="UpwindHeading">The upwind heading held while the gate is set.</param>
/// <param name="MinTurnAltitude">Pattern altitude less <see cref="UpwindPhase.PatternHandoffMarginFt"/>, feet MSL.</param>
internal sealed record FollowClimbOutGate(LatLon DepartureEnd, TrueHeading UpwindHeading, double MinTurnAltitude)
{
    /// <summary>The gate a circuit's waypoints put on a pursuit: its crosswind-turn point, its upwind heading and its turn altitude.</summary>
    internal static FollowClimbOutGate FromWaypoints(PatternWaypoints waypoints) =>
        new(
            new LatLon(waypoints.CrosswindTurnLat, waypoints.CrosswindTurnLon),
            waypoints.UpwindHeading,
            waypoints.PatternAltitude - UpwindPhase.PatternHandoffMarginFt
        );

    /// <summary>
    /// The gate a climb-out on <paramref name="flownRunway"/> into <paramref name="patternRunway"/>'s circuit puts on a
    /// departure-leg pursuit with no circuit waypoints to read: the farther of the two runways' departure ends along the pattern
    /// runway's heading (<see cref="PatternGeometry.TransitionDepartureEnd"/>, the point a transition circuit anchors its
    /// crosswind turn at; the runway's own pavement end when the two are one), the flown runway's true heading (AIM 4-3-2.c.1),
    /// and the pattern altitude less the legal-turn margin.
    /// </summary>
    internal static FollowClimbOutGate ForClimbOut(RunwayInfo flownRunway, RunwayInfo patternRunway, double patternAltitudeFt) =>
        new(
            PatternGeometry.TransitionDepartureEnd(flownRunway, patternRunway),
            flownRunway.TrueHeading,
            patternAltitudeFt - UpwindPhase.PatternHandoffMarginFt
        );
}

/// <summary>A position and heading in a runway's final frame, for the turn-out geometry (<see cref="VfrFollowPhase.FinalFrameOf"/>).</summary>
/// <param name="AlongNm">Distance out along the extended centerline from the threshold (negative past it).</param>
/// <param name="PatternSideNm">Distance from the extended centerline toward the circuit's pattern side (negative on the far side).</param>
/// <param name="HeadingDeg">
/// Heading off the downwind heading, positive toward the pattern side, in (-180, 180]: 0 the downwind heading, -90 the base
/// heading, 180 the final heading.
/// </param>
internal readonly record struct FinalFramePosition(double AlongNm, double PatternSideNm, double HeadingDeg);

/// <summary>An open parallel-hold stall window (<see cref="VfrFollowPhase.AdvanceParallelHoldWindow"/>): the gap it opened at and its age.</summary>
internal readonly record struct ParallelHoldWindow(double StartGapNm, double Seconds);
