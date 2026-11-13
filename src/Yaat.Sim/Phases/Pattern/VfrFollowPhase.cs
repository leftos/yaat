using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
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
public sealed partial class VfrFollowPhase(string targetCallsign, FollowPatternReturn? patternReturn) : Phase
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

    /// <summary>
    /// Maximum intercept angle (track vs final approach course) allowed when committing onto final — the standard 30° final intercept.
    /// </summary>
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

    /// <summary>
    /// Least final (nm) a follow's pattern return leaves beyond the final turn when it turns base from where the follower is:
    /// a quarter mile, the FAA figure for the final turn's completion (AIM §4-3-3, FIG 4-3-2 key 3). Adding the final-turn
    /// radius to it (<see cref="CanTurnBaseFromHere"/>) is a judgement call.
    /// </summary>
    public const double ReturnBaseMinFinalNm = 0.25;

    /// <summary>
    /// Most (deg) the lead's pre-base leg a follower is extending may track off the circuit's downwind heading for the
    /// excursion call to say "extending downwind" rather than "S-turning" (a judgement figure).
    /// </summary>
    public const double ExtendingDownwindMaxOffDeg = 30.0;

    /// <summary>Least time (s) between two spacing-excursion calls from one follow (AIM 4-3-5).</summary>
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

    /// <summary>
    /// The follow ended because the lead is now landing another runway
    /// (<see cref="AirborneFollowHelper.TryEndFollowLeadChangedRunway"/>), whose instructor line explains the end.
    /// </summary>
    private bool _followEndedByLeadRunwayChange;

    /// <summary>The open parallel-hold stall window (<see cref="ParallelHoldStalled"/>), or null.</summary>
    private ParallelHoldWindow? _parallelHold;

    public string TargetCallsign { get; private set; } = targetCallsign;

    /// <summary>
    /// The circuit the follower left when FOLLOW moved it off a pattern leg into this pursuit, or null
    /// when the pursuit did not start from a pattern leg.
    /// </summary>
    public FollowPatternReturn? PatternReturn { get; } = patternReturn;

    /// <summary>
    /// The at-or-above altitude (feet MSL) of a restricted low approach (<c>LA 600</c>, 7110.65 3-10-10) standing when the
    /// pursuit was installed (<see cref="Commands.CommandDispatcher.InstallVfrFollowPhase"/>), carried onto the low approach
    /// the join rebuilds; null when none was. A pass-through: nothing else reads it.
    /// </summary>
    internal int? LowApproachAtOrAboveFt { get; init; }

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
    /// closed-traffic climb off its pattern runway or a parallel runway of it), the runway it is flying: that runway's heading,
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
            // A closed-traffic climb off a parallel runway flies that runway's centerline; the pattern runway sets the altitude.
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

    /// <summary>
    /// Note that the follow ended because the lead is now landing another runway
    /// (<see cref="AirborneFollowHelper.TryEndFollowLeadChangedRunway"/>): the instructor line that ended it already explains
    /// the return, so <see cref="ReturnToPattern"/> adds none of its own for that cause.
    /// </summary>
    internal void MarkFollowEndedByLeadRunwayChange() => _followEndedByLeadRunwayChange = true;

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
            ReturnToPattern(ctx, LifecycleReturn(ctx, lead), forwardJoins: true);
            return true;
        }

        // CheckLeadLifecycle ends the follow when the lead is not found, so a null here ends it the same way.
        lead = ctx.AircraftLookup?.Invoke(TargetCallsign);
        if (lead is null)
        {
            AirborneFollowHelper.ClearFollowState(ctx.Aircraft);
            ReturnToPattern(ctx, PatternReturn, forwardJoins: true);
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
        HoldUpwindSpeed(ctx, gate);
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
    private static void HoldUpwindSpeed(PhaseContext ctx, FollowClimbOutGate gate)
    {
        double baseline = gate.CompressedDownwindSpeedKts ?? AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
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
            ?? PatternGeometry.InferDefaultPatternDirection(runway)
            ?? PatternDirection.Left;
        return new FollowPatternReturn(runway, direction, ResolvePatternAltitudeFt(aircraft, runway, groundLayout), fromBase);
    }

    /// <summary>
    /// The pattern altitude (feet MSL) of <paramref name="aircraft"/>'s circuit to <paramref name="runway"/>, resolved as the
    /// circuit builder would: an authored or commanded override (<see cref="PatternGeometry.ResolveAuthoredOverrides"/>, with
    /// the aircraft's own <c>Pattern.AltitudeOverrideFt</c>), else its category's pattern altitude above the field. When
    /// <paramref name="runway"/> is the armed pattern runway, the altitude an option clearance deferred to it (or its clear)
    /// stands in for the standing override, as a join onto it would claim it; it is read here, not claimed.
    /// </summary>
    internal static double ResolvePatternAltitudeFt(AircraftState aircraft, RunwayInfo runway, AirportGroundLayout? groundLayout)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        DeferredPatternAltitude? armedDeferral = aircraft.Phases is { } phases
            ? Commands.PatternCommandHandler.SatisfiedTransitionPatternAltitude(phases, runway)
            : null;
        (double? _, double? altitudeOverrideFt) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
            category,
            commandSizeNm: null,
            armedDeferral is { } deferred ? deferred.AltitudeFt : aircraft.Pattern.AltitudeOverrideFt
        );
        return altitudeOverrideFt ?? (runway.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(category));
    }

    /// <summary>
    /// When a pursuit that left a pattern leg ends (lead lost or despawned, lead landed with no captured runway,
    /// spacing unmaintainable), re-enter the circuit <paramref name="patternReturn"/> names by the leg
    /// <see cref="ReturnEntryLeg"/> picks, with an RPO note: the final (<see cref="JoinReturnFinal"/>) or a base from
    /// here (<see cref="BuildBaseFromHere"/>), both built here with the clearance carried, else a pattern entry to its
    /// runway on the same side. <paramref name="forwardJoins"/> is false for a spacing loss behind the lead, which never
    /// takes the base or final join. The follow state itself was already cleared by the caller.
    /// </summary>
    private void ReturnToPattern(PhaseContext ctx, FollowPatternReturn? patternReturn, bool forwardJoins)
    {
        bool leadChangedRunway = _followEndedByLeadRunwayChange;
        _followEndedByLeadRunwayChange = false;
        if (patternReturn is null)
        {
            return;
        }

        PatternEntryLeg leg = ReturnEntryLeg(ctx, patternReturn, forwardJoins);
        if (leg == PatternEntryLeg.Final)
        {
            JoinReturnFinal(ctx, patternReturn);
        }
        else if (leg == PatternEntryLeg.Base)
        {
            JoinReturnBase(ctx, patternReturn);
        }
        else if (!EnterReturnPattern(ctx, patternReturn, leg))
        {
            return;
        }

        AircraftState aircraft = ctx.Aircraft;
        string runwayDisplay = RunwayIdentifier.ToDisplayDesignator(patternReturn.Runway.Designator);
        string side = patternReturn.Direction == PatternDirection.Right ? "right" : "left";
        Log.LogDebug(
            "[VfrFollow] {Callsign}: follow of {Lead} ended, re-entering {Side} traffic runway {Rwy} by the {Leg}",
            aircraft.Callsign,
            TargetCallsign,
            side,
            runwayDisplay,
            leg
        );
        if (!leadChangedRunway)
        {
            aircraft.PendingWarnings.Add($"{aircraft.Callsign} follow ended, re-entering {side} traffic runway {runwayDisplay}");
        }
    }

    /// <summary>
    /// The pattern return's final join: onto <paramref name="circuit"/>'s final in its direction
    /// (<see cref="BuildFinalJoinLegs"/>), the follow over, with the standing clearance carried.
    /// </summary>
    private void JoinReturnFinal(PhaseContext ctx, FollowPatternReturn circuit)
    {
        List<Phase> legs = BuildFinalJoinLegs(ctx, circuit.Runway, circuit.Direction);
        Log.LogDebug(
            "[VfrFollow] {Callsign}: follow of {Lead} ended on the {Rwy} final, joining it ({Dir})",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            circuit.Runway.Designator,
            circuit.Direction
        );
        InstallJoinedCircuit(ctx, circuit.Runway, circuit.Direction, legs, keepFollowing: false);
    }

    /// <summary>
    /// The pattern return's base join: a base turned where the follower is (<see cref="BuildBaseFromHere"/>), its final-turn
    /// distance the follower's present distance out, the follow over.
    /// </summary>
    private void JoinReturnBase(PhaseContext ctx, FollowPatternReturn circuit)
    {
        double alongNm = AirborneFollowHelper.AlongFinalNm(ctx.Aircraft.Position, circuit.Runway);
        List<Phase> legs = BuildBaseFromHere(ctx, circuit, alongNm);
        InstallJoinedCircuit(ctx, circuit.Runway, circuit.Direction, legs, keepFollowing: false);
    }

    /// <summary>
    /// The pattern return by a pattern entry (<see cref="Commands.PatternCommandHandler.TryEnterPattern"/>) on
    /// <paramref name="leg"/> to <paramref name="patternReturn"/>'s runway on the same side; false, with an RPO warning, when
    /// the entry is refused.
    /// </summary>
    private bool EnterReturnPattern(PhaseContext ctx, FollowPatternReturn patternReturn, PatternEntryLeg leg)
    {
        AircraftState aircraft = ctx.Aircraft;

        // Assign the return runway itself rather than naming it: a designator would be resolved at the
        // follower's airport context, which need not be the airport the circuit belongs to.
        // This phase is ticking, so the list it runs from is present.
        PhaseList phases = aircraft.Phases!;
        RunwayInfo? previousRunway = phases.AssignedRunway;
        var armedBefore = ArmedPatternState.Of(phases);
        phases.AssignedRunway = patternReturn.Runway;
        CommandResult entry = Commands.PatternCommandHandler.TryEnterPattern(
            aircraft,
            new EnterPatternRequest
            {
                RequestedDirection = patternReturn.Direction,
                EntryLeg = leg,
                RunwayId = null,
                FinalDistanceNm = null,
                ActiveRunways = ctx.ActiveRunways,
                WarnRunwayCrossing = false,
                GroundLayout = ctx.GroundLayout,
            }
        );
        if (entry.Success)
        {
            KeepArmedPatternRunway(aircraft, armedBefore, patternReturn);
            return true;
        }

        phases.AssignedRunway = previousRunway;
        string runwayDisplay = RunwayIdentifier.ToDisplayDesignator(patternReturn.Runway.Designator);
        Log.LogWarning(
            "[VfrFollow] {Callsign}: follow of {Lead} ended but re-entering runway {Rwy} failed: {Reason}",
            aircraft.Callsign,
            TargetCallsign,
            runwayDisplay,
            entry.Message
        );
        aircraft.PendingWarnings.Add($"{aircraft.Callsign} follow ended, unable to re-enter the pattern for runway {runwayDisplay}: {entry.Message}");
        return false;
    }

    /// <summary>
    /// <see cref="Commands.PatternCommandHandler.TryEnterPattern"/> builds a new list without a
    /// <see cref="PhaseList.PatternRunway"/>, so a pattern runway armed before the follow (<c>COPT MLT 28L</c> on the 28R
    /// circuit) would be dropped by the re-entry. Carry it over as a join does (<see cref="CarryArmedPatternRunway"/>),
    /// with the pattern altitude its modifier deferred to it, unless the entry itself armed one (a pre-issued clearance's
    /// pattern modifier). A return to the armed runway itself has built that circuit at the deferred altitude
    /// (the entry reads it off the list it replaces), so nothing is carried.
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
            rebuilt.TransitionPatternAltitude = armedBefore.TransitionPatternAltitude;
        }
    }

    /// <summary>
    /// The leg a follow's pattern return enters by. Past <paramref name="circuit"/>'s threshold: the upwind when flying the
    /// runway's way (it continues upwind, crosswind and downwind, AIM 4-3-3, never turning back onto the final), else the
    /// downwind. On the approach side with <paramref name="forwardJoins"/>, no farther out than the circuit's base turn point
    /// (<see cref="ReturnWaypoints"/>) plus <see cref="BaseExtensionLimitNm"/>, the room a follower extending a leg for
    /// spacing may take past a base turn point: the circuit continued forward from where the follower is
    /// (<see cref="ForwardReturnLeg"/>). Otherwise the downwind.
    /// </summary>
    private static PatternEntryLeg ReturnEntryLeg(PhaseContext ctx, FollowPatternReturn circuit, bool forwardJoins)
    {
        AircraftState aircraft = ctx.Aircraft;
        RunwayInfo runway = circuit.Runway;
        double alongNm = AirborneFollowHelper.AlongFinalNm(aircraft.Position, runway);
        if (alongNm < 0.0)
        {
            return aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading) < 90.0 ? PatternEntryLeg.Upwind : PatternEntryLeg.Downwind;
        }

        if (!forwardJoins)
        {
            return PatternEntryLeg.Downwind;
        }

        PatternWaypoints waypoints = ReturnWaypoints(ctx, circuit);
        double limitNm = AirborneFollowHelper.AlongFinalNm(new LatLon(waypoints.BaseTurnLat, waypoints.BaseTurnLon), runway) + BaseExtensionLimitNm;
        return alongNm <= limitNm ? ForwardReturnLeg(ctx, circuit, waypoints, alongNm) : PatternEntryLeg.Downwind;
    }

    /// <summary>
    /// The leg that continues <paramref name="circuit"/> forward from a follower <paramref name="alongNm"/> out on the
    /// approach side, never reversing it, in this order:
    /// <list type="number">
    /// <item><description>the final, when the follower passes the final join's gates (<see cref="CanSequenceOntoFinal"/>);</description></item>
    /// <item><description>on the non-pattern side, the downwind: the pattern entry's wrong-side handling (the midfield
    /// crossing);</description></item>
    /// <item><description>the base, when heading toward the final with room for the final turn
    /// (<see cref="CanTurnBaseFromHere"/>);</description></item>
    /// <item><description>the downwind, when heading within 90° of the downwind heading;</description></item>
    /// <item><description>otherwise the upwind (inbound too close in for a base, or crossing the final).</description></item>
    /// </list>
    /// </summary>
    private static PatternEntryLeg ForwardReturnLeg(PhaseContext ctx, FollowPatternReturn circuit, PatternWaypoints waypoints, double alongNm)
    {
        AircraftState aircraft = ctx.Aircraft;
        RunwayInfo runway = circuit.Runway;
        if (CanSequenceOntoFinal(aircraft, runway))
        {
            return PatternEntryLeg.Final;
        }

        if (!IsOnPatternSide(aircraft, runway, circuit.Direction))
        {
            return PatternEntryLeg.Downwind;
        }

        if (CanTurnBaseFromHere(ctx, waypoints, runway, alongNm))
        {
            return PatternEntryLeg.Base;
        }

        return aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading.ToReciprocal()) < 90.0 ? PatternEntryLeg.Downwind : PatternEntryLeg.Upwind;
    }

    /// <summary>
    /// True when a base turned from here works: the follower heads within 90° of the circuit's base heading (toward the
    /// final), and <paramref name="alongNm"/> leaves room for the final turn
    /// (<see cref="BasePhase.TurnRadiusNm(double, AircraftCategory, PatternWaypoints?)"/> at its ground speed) at least
    /// <see cref="ReturnBaseMinFinalNm"/> out, and the follower is at least that radius from the extended centerline, so the
    /// turn to final never overshoots onto the non-pattern side (toward a parallel's final).
    /// </summary>
    private static bool CanTurnBaseFromHere(PhaseContext ctx, PatternWaypoints waypoints, RunwayInfo runway, double alongNm)
    {
        AircraftState aircraft = ctx.Aircraft;
        double turnRadiusNm = BasePhase.TurnRadiusNm(aircraft.GroundSpeed, ctx.Category, waypoints);
        return (aircraft.TrueHeading.AbsAngleTo(waypoints.BaseHeading) < 90.0)
            && (alongNm >= ReturnBaseMinFinalNm + turnRadiusNm)
            && (Math.Abs(SignedCrossTrackNm(aircraft.Position, runway)) >= turnRadiusNm);
    }

    /// <summary>The pattern return circuit's waypoints for this aircraft (<see cref="PatternGeometry.Compute"/>).</summary>
    private static PatternWaypoints ReturnWaypoints(PhaseContext ctx, FollowPatternReturn circuit)
    {
        RunwayInfo runway = circuit.Runway;
        return PatternGeometry.Compute(
            new PatternGeometryRequest(runway, ctx.Category, ctx.Aircraft.AircraftType, ctx.Aircraft.WindSpeedKts, circuit.Direction)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = circuit.PatternAltitudeFt,
                AirportRunways = Data.NavigationDatabase.Instance.GetRunways(runway.AirportId),
                AuthoredRunway = (ctx.GroundLayout ?? ctx.Aircraft.Ground.Layout)?.FindRunway(runway.Designator),
                ActiveRunways = ctx.ActiveRunways,
                Commanded = PatternCommanded.Of(ctx.Aircraft),
            }
        );
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
    /// Returns true if <paramref name="follower"/> is on the same side of the
    /// runway centerline as the pattern (the side the downwind lies on).
    /// A left pattern has downwind to the left of the runway when viewed in the
    /// direction of landing; follower must be on that same side.
    /// </summary>
    private static bool IsOnPatternSide(AircraftState follower, RunwayInfo runway, PatternDirection direction)
    {
        double crossTrack = SignedCrossTrackNm(follower.Position, runway);
        return direction == PatternDirection.Left ? crossTrack <= 0 : crossTrack >= 0;
    }

    /// <summary>
    /// <paramref name="position"/>'s signed distance (nm) from <paramref name="runway"/>'s extended centerline: positive right
    /// of the landing direction (the right-traffic side), negative left. The one side convention <see cref="IsOnPatternSide"/>
    /// and <see cref="SideOfCenterline"/> share.
    /// </summary>
    private static double SignedCrossTrackNm(LatLon position, RunwayInfo runway) =>
        GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);

    /// <summary>A turn-out under way: the circuit it rejoins (runway, direction, and the altitude it levels at) and where it began.</summary>
    private sealed record FollowTurnOut(FollowPatternReturn Circuit, LatLon StartPoint);

    /// <summary>
    /// The pattern-runway arming a phase list carried before a follow rebuilt it: the armed <see cref="PhaseList.PatternRunway"/>,
    /// the runway being flown (<see cref="PhaseList.AssignedRunway"/>), the clearance the arming rode on and the pattern
    /// altitude its modifier deferred to the armed runway (<see cref="PhaseList.TransitionPatternAltitude"/>).
    /// </summary>
    private readonly record struct ArmedPatternState(
        RunwayInfo? PatternRunway,
        RunwayInfo? FlownRunway,
        ClearanceType? LandingClearance,
        DeferredPatternAltitude? TransitionPatternAltitude
    )
    {
        internal static ArmedPatternState Of(PhaseList phases) =>
            new(phases.PatternRunway, phases.AssignedRunway, phases.LandingClearance, phases.TransitionPatternAltitude);
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
                    CompressedDownwindSpeedKts = climbOutGate.CompressedDownwindSpeedKts,
                }
                : null,
            LowApproachAtOrAboveFt = LowApproachAtOrAboveFt,
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
                    climbOutGate.MinTurnAltitude,
                    climbOutGate.CompressedDownwindSpeedKts
                )
                : null,
            LowApproachAtOrAboveFt = dto.LowApproachAtOrAboveFt,
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
internal sealed record FollowClimbOutGate(LatLon DepartureEnd, TrueHeading UpwindHeading, double MinTurnAltitude, double? CompressedDownwindSpeedKts)
{
    /// <summary>The gate a circuit's waypoints put on a pursuit: its crosswind-turn point, its upwind heading and its turn altitude.</summary>
    internal static FollowClimbOutGate FromWaypoints(PatternWaypoints waypoints) =>
        new(
            new LatLon(waypoints.CrosswindTurnLat, waypoints.CrosswindTurnLon),
            waypoints.UpwindHeading,
            waypoints.PatternAltitude - UpwindPhase.PatternHandoffMarginFt,
            waypoints.CompressedDownwindSpeedKts
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
            patternAltitudeFt - UpwindPhase.PatternHandoffMarginFt,
            null
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
