using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft follows a TaxiRoute edge-by-edge at taxi speed.
/// Turns at nodes using ground turn rate.
/// Auto-stops at hold-short points (inserts HoldingShortPhase).
/// Completes when all segments have been traversed.
///
/// Core navigation (steering, speed profiling, braking, arrival detection) is
/// delegated to <see cref="GroundNavigator"/>. This phase handles route
/// management: hold-short insertion, runway crossing, departure clearance,
/// parking, and route completion.
/// </summary>
public sealed class TaxiingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("TaxiingPhase");

    private const double LogIntervalSeconds = 3.0;

    // Slow parking-approach speed (kts) applied within a fuselage of the destination spot so the
    // nose-at-spot terminal stop lands cleanly instead of braking abruptly from taxi speed.
    private const double SpotApproachSpeedKts = 4.0;

    /// <summary>
    /// The pull up the lane onto the spot at the end of a spot line-up (#456): from the start of the lined-up straight
    /// (<see cref="TaxiRoute.SpotLineUpPullFromSegment"/>) the taxi is held to this until the spot-approach crawl takes
    /// over, so the aircraft rolls out of its quarter turn and creeps up to the marking instead of accelerating.
    /// </summary>
    public const double SpotLineUpPullSpeedKts = 5.0;

    // How close to the route's start node the aircraft must be for a hold-short there to take the
    // stop. Well inside the hold-short standoff (>=125 ft from centerline), so honouring it can
    // never place the aircraft on the runway.
    private const double StartNodeHoldRadiusFt = 150.0;

    // Where the approach braking curve to a start-node hold-short reaches zero: just short of the
    // bar, so the aircraft creeps to a natural stop there instead of being frozen mid-approach.
    private const double StartNodeHoldStopShortFt = 15.0;

    // Speed below which the start-node hold-short may take its instant stop — an imperceptible snap
    // from a crawl, versus teleport-stopping an aircraft still at taxi speed.
    private const double StartNodeHoldArmSpeedKts = 3.0;

    // Along-route distance (ft) to a set-back bar's stop inside which the hold is taken. The braking curve reaches zero
    // GroundNavigator.SetBackStopMarginFt short of the stop, so the aircraft is at a crawl here and comes to rest with its
    // nose at or behind the marking; only its speed is snapped, never its position.
    private const double SetBackStopTakeFt = GroundNavigator.SetBackStopMarginFt + 1.0;

    // How far (deg) the aircraft's heading may be off the route's first segment for its nose to count as past the start
    // node's bar: a start node the route turns away from projects the nose ahead of the line well short of it. 45° is a
    // modelling threshold; no FAA document gives a figure.
    private const double StartBarAlignedDeg = 45.0;

    // Float slack (s) on the phase's elapsed time when telling its first tick from the rest.
    private const double FirstTickSlackSeconds = 1e-9;

    private GroundNavigator _nav = new();
    private bool _initialized;
    private bool _startNodeHoldDone;
    private double _timeSinceLastLog;

    // Set when a command changed the route's hold-shorts under a taxi already in progress. Transient —
    // raised by the command handler and consumed by the next tick, never snapshotted; a replayed command
    // raises it again at the same tick it did live.
    private bool _holdShortsDirty;

    /// <summary>Node of the bar whose stop was already moved forward for being unmakeable; moved once, never again.</summary>
    private int? _unableStopNodeId;

    // The GIVEWAY target the give-way stop was last logged for, so the log line appears once per give-way rather than
    // every held tick. Logging only: not snapshotted.
    private string? _loggedGiveWayTarget;

    // Set once a nose past an uncleared runway bar's marking has been logged — the bar the route starts on, or one whose line
    // was already lost when the route was given — so the warning appears once per phase rather than every braking tick.
    // Logging only: not snapshotted.
    private bool _loggedPastHoldLine;

    // Node of the uncleared runway bar the route starts on whose marking the nose was found past (PassedStartBar): latched so
    // the firm-rate stop it starts is never let go part-way as the aircraft slows and moves away from the node. Cleared when
    // the hold is taken or the bar is cleared. Snapshotted.
    private int? _passedStartBarNodeId;

    // Set when this phase completes to hand off to a still-moving CrossingRunwayPhase
    // (pre-cleared crossing), so OnEnd does not brake the aircraft to a stop. Transient —
    // set and consumed within the same completing tick, never snapshotted.
    private bool _completingIntoMovingCrossing;

    public override string Name => "Taxiing";

    internal double NavMaxSpeedKts => _nav.MaxSpeedKts;

    /// <summary>
    /// True when the navigator's last tick played a curve primitive (a fillet arc or a synthesised slow turn)
    /// rather than a straight. Read from <see cref="GroundNavigator.LastTickDiag"/>, the same per-tick
    /// diagnostic the tick recorder traces.
    /// </summary>
    internal bool IsNavigatorOnCurve => _nav.LastTickDiag?.OnArc == true;

    public override void OnStart(PhaseContext ctx)
    {
        TaxiRoute? route = ctx.Aircraft.Ground.AssignedTaxiRoute;
        if (route is not null && IsHoldAtStartOnly(route))
        {
            ctx.Aircraft.IsOnGround = true;
            Log.LogDebug("[Taxi] {Callsign}: started at the destination hold-short, nothing to taxi", ctx.Aircraft.Callsign);
            return;
        }

        if (route is null || route.IsComplete)
        {
            Log.LogWarning("[Taxi] {Callsign}: OnStart but route is {State}", ctx.Aircraft.Callsign, route is null ? "null" : "already complete");
            return;
        }

        ctx.Aircraft.IsOnGround = true;
        _nav.MaxSpeedKts = ctx.Aircraft.Ground.CommandedTaxiSpeedKts ?? CategoryPerformance.TaxiSpeed(ctx.Category);
        _nav.RouteEndSpeedKts = RouteEndSpeedKts(ctx, route);
        SetupCurrentSegment(ctx, route);

        Log.LogDebug(
            "[Taxi] {Callsign}: started, {SegCount} segments, first target node {NodeId} at ({Lat:F6}, {Lon:F6})",
            ctx.Aircraft.Callsign,
            route.Segments.Count,
            _nav.TargetNodeId,
            _nav.TargetLat,
            _nav.TargetLon
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        TaxiRoute? route = ctx.Aircraft.Ground.AssignedTaxiRoute;
        if (route is not null && IsHoldAtStartOnly(route))
        {
            return TickHoldAtStartOnly(ctx, route);
        }

        if (route is null || route.IsComplete)
        {
            Log.LogDebug("[Taxi] {Callsign}: OnTick exit — route {State}", ctx.Aircraft.Callsign, route is null ? "null" : "complete");
            return true;
        }

        if (!_initialized)
        {
            Log.LogDebug(
                "[Taxi] {Callsign}: late init in OnTick (groundLayout {HasLayout})",
                ctx.Aircraft.Callsign,
                ctx.GroundLayout is not null ? "present" : "NULL"
            );
            _nav.MaxSpeedKts = ctx.Aircraft.Ground.CommandedTaxiSpeedKts ?? CategoryPerformance.TaxiSpeed(ctx.Category);
            _nav.RouteEndSpeedKts = RouteEndSpeedKts(ctx, route);
            SetupCurrentSegment(ctx, route);
        }

        // A controller-commanded taxi speed replaces the category default outright (slower or
        // faster); it is mutually exclusive with expedite, so at most one branch is in effect.
        // Corner/arc/braking/conflict caps still win downstream via GroundNavigator's Math.Min chain.
        double baseTaxiSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        _nav.MaxSpeedKts =
            ctx.Aircraft.Ground.CommandedTaxiSpeedKts
            ?? (ctx.Aircraft.Ground.IsExpeditingTaxi ? baseTaxiSpeed * CategoryPerformance.TaxiExpediteMultiplier : baseTaxiSpeed);

        // A spot line-up pulls up its lane onto the spot slowly; the spot-approach crawl below still takes over.
        if ((route.SpotLineUpPullFromSegment is { } pullFrom) && (route.CurrentSegmentIndex >= pullFrom))
        {
            _nav.MaxSpeedKts = Math.Min(_nav.MaxSpeedKts, SpotLineUpPullSpeedKts);
        }

        // A takeoff clearance can arrive mid-segment, after the speed profile for the segment in progress was
        // built with a stop at the bar. Re-plan the profile then and there, or the aircraft brakes for a bar it
        // is already cleared through and the line-up has to re-accelerate from the crawl (issue: SFO 28R at E).
        // Re-planned even while held, so the release rolls out on the profile the clearance implies.
        double routeEndSpeed = RouteEndSpeedKts(ctx, route);
        if (Math.Abs(routeEndSpeed - _nav.RouteEndSpeedKts) > 1e-9)
        {
            _nav.RouteEndSpeedKts = routeEndSpeed;
            _nav.RefreshSpeedConstraints(route, ctx, nodeId => IsHoldShortCleared(route, nodeId));
            Log.LogDebug("[Taxi] {Callsign}: route-end speed re-planned to {Speed:F1}kt", ctx.Aircraft.Callsign, routeEndSpeed);
        }

        // A hold-short armed mid-segment (standalone HS, RES HS) after this segment's speed profile was
        // built: re-aim at the bar and re-plan the braking now. Left to the next SetupCurrentSegment, the
        // aircraft keeps the junction node as its target and only meets the bar on arrival, which it
        // overruns — the SFO B/T case this phase's re-aim exists for.
        if (_holdShortsDirty)
        {
            _holdShortsDirty = false;
            ReaimAtChangedHoldShort(ctx, route);
        }

        // HOLD / GIVEWAY: the aircraft stops where it is, but the steering tick below still runs — see the
        // held branch after it. Nothing that ends the phase or inserts another one may fire while it is held.
        bool held = ctx.Aircraft.Ground.IsImmobile;

        // A hold-short on the route's own start node: the aircraft was re-routed at or while
        // approaching the bar, so it must not enter the crossing until cleared. ArriveAtNode never
        // fires for that node — it is no segment's ToNodeId — so the stop has to be taken here,
        // before the first segment, re-checked each tick until the hold binds or stops applying. So is a set-back stop. A nose
        // already past the start bar's marking is checked every tick, whatever the start-node check decided, so an HS that
        // re-arms the start bar later, and a phase restored from a snapshot, take that hold alike.
        if (
            !held
            && (
                TryHoldPastStartBar(ctx, route)
                || ((_passedStartBarNodeId is null) && !_startNodeHoldDone && TryHoldAtRouteStartNode(ctx, route))
                || TryHoldAtSetBackStop(ctx, route)
            )
        )
        {
            return true;
        }

        // Nose-at-spot terminal stop (issue #234): a taxiing aircraft parks with the front of its
        // footprint (its nose) at the spot marking, not its centroid — otherwise the fuselage juts
        // ~half its length past the spot toward the movement area, and the conflict detector (which
        // models each aircraft as centroid ± half length) then slows traffic taxiing past on the
        // adjacent taxiway. This stops the aircraft once its nose reaches the spot, at whatever heading
        // the approach left it (a tight ramp lead-in may still be mid-turn — realistic for a taxi-in;
        // aircraft are normally pushed onto spots). Spots are non-movement areas (AIM 4-3-14/4-3-17).
        if (!held && TryStopNoseAtSpot(ctx, route))
        {
            return true;
        }

        bool isLastSegment = route.CurrentSegmentIndex + 1 >= route.Segments.Count;
        int targetBeforeTick = _nav.TargetNodeId;
        NavigatorResult result = _nav.Tick(ctx, isLastSegment, nodeId => IsHoldShortCleared(route, nodeId));
        if (_nav.TargetNodeId != targetBeforeTick)
        {
            AimAtPaintedBar(route);
        }

        if (held)
        {
            // A hold pins the published speed; it never skips the steering tick. The navigator's closed-form
            // curve playback writes the pose from one progress scalar, so a tick skipped while physics keeps
            // rolling the aircraft leaves that scalar behind it and the first un-held tick writes the aircraft
            // backwards onto the stale pose. Pinning the target after the navigator has ticked is equally what
            // keeps the speed it just published from staying live and physics accelerating toward it every
            // sub-tick (issue #407 — two "held" aircraft kept taxiing into a head-on); physics brakes toward
            // the pinned target at the ground decel rate. A GIVEWAY with a give-way point ahead pins it to the
            // braking curve onto that point instead (HeldSpeedKts). An uncleared bar ahead caps it after any arrival.
            double heldSpeedKts = HeldSpeedKts(ctx, route);
            ctx.Targets.TargetSpeed = heldSpeedKts;

            if (result == NavigatorResult.ArrivedAtNode)
            {
                if (IsPassThroughNode(route))
                {
                    // Still braking at a node that only joins two segments, or at a cleared bar: roll on along the route at the brake
                    // rate rather than dropping the speed left over, which can be most of the taxi speed when the
                    // hold came just short of the node. The arrival (a cleared runway bar's hands off to its crossing,
                    // where a GIVEWAY waits until the far marking is behind the tail) fires the node's AT triggers and sets up the
                    // next segment, and re-publishes the taxi target, so the held speed is pinned again after it.
                    if (ArriveAtNode(ctx, route))
                    {
                        return true;
                    }
                }
                else if (!IsRollingOntoUnclearedBar(ctx, route))
                {
                    // At an uncleared bar or the route's end and already braking: clean up the residual and leave the arrival
                    // itself to the first un-held tick. A hold must not be able to insert a HoldingShortPhase or
                    // complete the route — and so start a stored takeoff clearance's line-up — while the controller
                    // has said hold. An aircraft whose runway line was already lost gets to the bar still rolling: it keeps
                    // braking at the firm rate (CapHeldSpeedAtUnclearedBar) rather than being stopped dead from that speed.
                    ctx.Aircraft.IndicatedAirspeed = 0;
                }
            }

            ctx.Targets.TargetSpeed = CapHeldSpeedAtUnclearedBar(ctx, route, heldSpeedKts);
            return false;
        }

        if ((result == NavigatorResult.ArrivedAtNode) && !IsRollingOntoUnclearedBar(ctx, route))
        {
            bool done = ArriveAtNode(ctx, route);
            if (!done)
            {
                CapAtUnclearedBar(ctx, route);
            }

            return done;
        }

        CapAtUnclearedBar(ctx, route);

        // Update current taxiway name
        if (route.CurrentSegment is { } seg)
        {
            string? prev = ctx.Aircraft.Ground.CurrentTaxiway;
            ctx.Aircraft.Ground.CurrentTaxiway = seg.TaxiwayName;

            // Fire AT-taxiway triggers on transition only (avoid per-tick storm).
            if (!string.Equals(prev, seg.TaxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                FlightPhysics.NotifyGroundEntityReached(ctx.Aircraft, arrivedNodeId: null, newTaxiwayName: seg.TaxiwayName);
            }
        }

        LogPeriodic(ctx, route);
        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        Log.LogDebug("[Taxi] {Callsign}: OnEnd ({Status})", ctx.Aircraft.Callsign, endStatus);

        // GroundNavigator republishes DesiredDecelRate every tick (null when it has no override), so a
        // running taxi never inherits a stale rate. Clearing on exit is what keeps a rate from crossing
        // the phase boundary: ControlTargets persist, so a phase that publishes no rate of its own —
        // an airborne descent, an approach speed reduction — would otherwise brake at ground rates.
        ctx.Targets.DesiredDecelRate = null;

        // Two completions keep their speed instead of snapping to a standstill:
        //
        //  - into a moving runway crossing — the CrossingRunwayPhase owns the speed and must not
        //    re-accelerate from a dead stop on the runway approach;
        //  - at a route end the navigator planned to arrive at rolling (RouteEndSpeedKts > 0, set only
        //    when a stored takeoff/line-up clearance has already cleared the destination-runway bar the
        //    route ends at). Zeroing the speed at the phase boundary would contradict the plan the
        //    navigator just flew, and there was no stop to make: runway holding position markings mark
        //    where an aircraft must stop "when a clearance has not been issued to proceed onto the
        //    runway" (AIM 2-3-5.a.1) — the stop is conditional on the absence of a clearance, not a
        //    property of the paint. Braking at a bar the aircraft is cleared through and re-accelerating
        //    also defeats the anticipated-separation technique of 7110.65 3-9-5, which lets a takeoff
        //    clearance be issued before separation exists provided it exists "when the aircraft starts
        //    takeoff roll"; and 7110.65 3-9-6.c bans rolling takeoffs by super/heavy only, which is what
        //    makes them normal for everything else.
        if (endStatus == PhaseStatus.Completed && !_completingIntoMovingCrossing && !(_nav.RouteEndSpeedKts > 0))
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.CrossRunway => CommandAcceptance.Allowed,
            CanonicalCommandType.HoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.Speed or CanonicalCommandType.ResumeNormalSpeed => CommandAcceptance.Allowed,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is taxiing; only HOLD/RES, CROSS, HS, SPD, or FOLLOWG apply, or issue a new TAXI"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new TaxiingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetNodeId = _nav.TargetNodeId,
            TargetLat = _nav.TargetLat,
            TargetLon = _nav.TargetLon,
            Initialized = _initialized,
            TimeSinceLastLog = _timeSinceLastLog,
            PrevDistToTarget = _nav.PrevDistToTarget,
            Navigator = _nav.ToSnapshot(),
            UnableStopNodeId = _unableStopNodeId,
            PassedStartBarNodeId = _passedStartBarNodeId,
        };

    public static TaxiingPhase FromSnapshot(TaxiingPhaseDto dto)
    {
        var phase = new TaxiingPhase
        {
            // The restored navigator holds the primitive it was playing and its progress back until a segment set-up
            // (GroundNavigator.FromSnapshot). Force a re-init on the next OnTick: SetupCurrentSegment sets up
            // route.CurrentSegmentIndex, which resumes that primitive where it stood and rebuilds the speed plan from the
            // route (a snapshot without the primitive builds it afresh from the aircraft's pose). Without the set-up,
            // the next Tick would find no primitive, return ArrivedAtNode, and skip the segment the aircraft was on.
            _initialized = false,
            _timeSinceLastLog = dto.TimeSinceLastLog,
            _unableStopNodeId = dto.UnableStopNodeId,
            _passedStartBarNodeId = dto.PassedStartBarNodeId,
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
        };
        phase.RestoreRequirements(dto.Requirements);

        if (dto.Navigator is not null)
        {
            phase._nav = GroundNavigator.FromSnapshot(dto.Navigator);
        }
        else
        {
            // Legacy snapshot without navigator — reconstruct from old flat fields via the navigator DTO.
            phase._nav = GroundNavigator.FromSnapshot(
                new GroundNavigatorDto
                {
                    TargetNodeId = dto.TargetNodeId,
                    TargetLat = dto.TargetLat,
                    TargetLon = dto.TargetLon,
                    PrevDistToTarget = dto.PrevDistToTarget,
                    MaxSpeedKts = 30,
                }
            );
        }

        return phase;
    }

    /// <summary>
    /// Tells a taxi already under way that the route's hold-short set changed — a standalone <c>HS</c> or a
    /// <c>RES HS</c> armed a bar after the current segment's speed profile was built. The next tick re-aims
    /// the navigator at that bar and re-plans the braking for it.
    /// </summary>
    public void NotifyHoldShortsChanged() => _holdShortsDirty = true;

    /// <summary>
    /// Distance (ft) an aircraft needs to brake from <paramref name="groundSpeedKts"/> to a standstill at
    /// the category's taxi deceleration rate — v² / 2a, the same rate the navigator's braking curve and
    /// <see cref="FlightPhysics"/> fly, so a bar inside this distance is one the aircraft physically cannot
    /// stop at.
    ///
    /// <para>No reaction time is added, deliberately. At the jet taxi rate (5 kt/s) the figure already
    /// matches a crew reacting and then braking hard: from 28 kt, v²/2a gives 132.3 ft where a 1 s reaction
    /// (49.7 ft) plus a 0.42 g max-effort stop (82.7 ft) gives 132.4 ft, so a reaction term would double-count
    /// it. The equivalence is the jet's; a piston at 2 kt/s over-reads by about 74 ft from 20 kt, which is
    /// conservative — it calls a bar unmakeable a little early. No FAA document gives a taxi stopping
    /// distance, so both the rate and this reading of it are judgement calls.</para>
    /// </summary>
    /// <param name="groundSpeedKts">Current ground speed in knots.</param>
    /// <param name="category">Aircraft category, which sets the taxi brake rate.</param>
    /// <returns>Braking distance in feet.</returns>
    public static double HoldShortBrakingDistanceFt(double groundSpeedKts, AircraftCategory category)
    {
        double speedFtPerSec = groundSpeedKts * GeoMath.FeetPerNm / 3600.0;
        double decelFtPerSec2 = CategoryPerformance.TaxiDecelRate(category) * GeoMath.FeetPerNm / 3600.0;
        return decelFtPerSec2 <= 0 ? 0 : (speedFtPerSec * speedFtPerSec) / (2.0 * decelFtPerSec2);
    }

    /// <summary>
    /// Distance (ft) left along the route before the aircraft reaches <paramref name="holdShort"/>'s painted
    /// stop position: the remainder of the segment in progress plus every whole segment up to the bar's node,
    /// less the along-route setback the stop sits back from that node (<see cref="TaxiRoute.HoldShortSetbackNm"/>,
    /// which may span several segments). Negative once the stop is behind the aircraft, and
    /// <see cref="double.PositiveInfinity"/> when the bar has no computed position or is on no segment ahead
    /// (nothing to measure, so nothing is ever called unmakeable on it).
    /// </summary>
    /// <param name="layout">Ground layout the route is resolved on.</param>
    /// <param name="route">The route being taxied.</param>
    /// <param name="position">The aircraft's current position.</param>
    /// <param name="holdShort">The bar to measure to.</param>
    /// <returns>Distance in feet.</returns>
    public static double AlongRouteDistanceToHoldShortFt(AirportGroundLayout layout, TaxiRoute route, LatLon position, HoldShortPoint holdShort)
    {
        if (holdShort.Latitude is null || holdShort.Longitude is null || !layout.Nodes.ContainsKey(holdShort.NodeId))
        {
            return double.PositiveInfinity;
        }

        // The segment in progress is measured from the aircraft, whole segments by their own length. A route
        // that has not started yet (CurrentSegmentIndex < 0) is walked from segment 0, which is then the one
        // the aircraft is on. The in-progress leg is measured straight to the node rather than along the
        // navigator's primitive: GroundNavigator publishes no remaining-distance for the arc or Bézier it is
        // flying, so on a fillet this reads a little short — conservative, since it can only call a bar
        // unmakeable sooner.
        int firstIndex = Math.Max(0, route.CurrentSegmentIndex);
        double alongFt = 0;
        for (int i = firstIndex; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            alongFt +=
                i == firstIndex
                    ? GeoMath.DistanceNm(position, seg.Edge.ToNode.Position) * GeoMath.FeetPerNm
                    : seg.Edge.DistanceNm * GeoMath.FeetPerNm;

            if (seg.ToNodeId == holdShort.NodeId)
            {
                return alongFt - (route.HoldShortSetbackNm(i, holdShort) * GeoMath.FeetPerNm);
            }
        }

        return double.PositiveInfinity;
    }

    /// <summary>
    /// Ground speed (kts) at or below which this phase counts an aircraft as stopped. A stopped aircraft cannot overrun a
    /// bar, so it never reads one back as unmakeable.
    /// </summary>
    private const double StoppedGroundSpeedKts = 2.0;

    /// <summary>
    /// True when the aircraft is moving faster than <see cref="StoppedGroundSpeedKts"/> and <paramref name="holdShort"/> is
    /// closer than the distance it needs to brake to a stop: the painted bar cannot be made and the aircraft will come to
    /// rest past it whatever it does. A stopped aircraft whose stop point is already behind it holds where it stands.
    /// </summary>
    /// <param name="layout">Ground layout the route is resolved on.</param>
    /// <param name="route">The route being taxied.</param>
    /// <param name="aircraft">The aircraft the bar was armed for.</param>
    /// <param name="category">Aircraft category, which sets the taxi brake rate.</param>
    /// <param name="holdShort">The bar to judge.</param>
    /// <returns>True when the bar is unmakeable.</returns>
    public static bool IsHoldShortUnmakeable(
        AirportGroundLayout layout,
        TaxiRoute route,
        AircraftState aircraft,
        AircraftCategory category,
        HoldShortPoint holdShort
    ) =>
        (aircraft.GroundSpeed > StoppedGroundSpeedKts)
        && (
            AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, holdShort) < HoldShortBrakingDistanceFt(aircraft.GroundSpeed, category)
        );

    /// <summary>
    /// Re-aims the segment in progress at a bar armed after its profile was built, and — when that bar is
    /// inside the aircraft's braking distance — slides it forward to the stop the aircraft can actually make
    /// so the navigator brakes at the full taxi rate onto that point. Both halves end in a speed re-plan;
    /// a change that touched no bar on this segment's target node re-plans only, since a bar further along
    /// the route is picked up by the profile's forward walk.
    /// </summary>
    private void ReaimAtChangedHoldShort(PhaseContext ctx, TaxiRoute route)
    {
        if (
            route.GetHoldShortAt(_nav.TargetNodeId) is { IsCleared: false, Latitude: not null, Longitude: not null } bar
            && ctx.GroundLayout is { } layout
        )
        {
            if (!bar.Unable && IsHoldShortUnmakeable(layout, route, ctx.Aircraft, ctx.Category, bar))
            {
                bar.Unable = true;
            }

            // Once per bar. A later hold-short change re-enters here with the bar already moved, and
            // re-projecting from the aircraft's new position each time would ratchet the stop forward.
            if (bar.Unable && (_unableStopNodeId != bar.NodeId))
            {
                MoveBarToBrakingDistance(ctx, layout, bar);
                _unableStopNodeId = bar.NodeId;
            }

            AimAtPaintedBar(route);
        }

        _nav.RefreshSpeedConstraints(route, ctx, nodeId => IsHoldShortCleared(route, nodeId));
    }

    /// <summary>
    /// Moves an unmakeable bar to the point the aircraft can stop at — its braking distance ahead on the
    /// aircraft's current heading, clamped at the node the bar protects so the stop is never planned beyond
    /// the intersection itself. The heading is the path's tangent where the aircraft is now; the straight
    /// line to the node is its chord, which on a fillet arc would put the stop off the pavement.
    ///
    /// <para>Clamping at the node makes the modelled overrun a lower bound: an aircraft that cannot make the
    /// bar does not stop at the junction either, it keeps going. For a runway target that matters — AIM
    /// 2-3-5.a.1 makes the holding position marking the boundary of the runway safety area, so anything past
    /// it is already an incursion, and the modelled stop understates how far in the aircraft ends up.</para>
    /// </summary>
    private static void MoveBarToBrakingDistance(PhaseContext ctx, AirportGroundLayout layout, HoldShortPoint bar)
    {
        if (!layout.Nodes.TryGetValue(bar.NodeId, out GroundNode? barNode))
        {
            return;
        }

        double toNodeFt = GeoMath.DistanceNm(ctx.Aircraft.Position, barNode.Position) * GeoMath.FeetPerNm;
        double aheadFt = Math.Min(HoldShortBrakingDistanceFt(ctx.Aircraft.GroundSpeed, ctx.Category), toNodeFt);
        LatLon stop = GeoMath.ProjectPoint(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, aheadFt / GeoMath.FeetPerNm);

        Log.LogInformation(
            "[Taxi] {Callsign}: hold short of {Target} unmakeable at {Gs:F1}kt — stopping {Ahead:F0}ft ahead, {ToNode:F0}ft short of node {NodeId}",
            ctx.Aircraft.Callsign,
            bar.TargetName,
            ctx.Aircraft.GroundSpeed,
            aheadFt,
            toNodeFt - aheadFt,
            bar.NodeId
        );

        bar.Latitude = stop.Lat;
        bar.Longitude = stop.Lon;
    }

    private void SetupCurrentSegment(PhaseContext ctx, TaxiRoute route)
    {
        if (route.CurrentSegment is null)
        {
            Log.LogWarning(
                "[Taxi] {Callsign}: SetupCurrentSegment — no current segment (index={Idx})",
                ctx.Aircraft.Callsign,
                route.CurrentSegmentIndex
            );
            return;
        }

        _nav.SetupSegment(route, ctx, nodeId => IsHoldShortCleared(route, nodeId));
        AimAtPaintedBar(route);
        _initialized = true;
    }

    /// <summary>
    /// Aims the navigator at the painted bar of an uncleared hold-short on the node it is now targeting, rather than at
    /// the node itself: the bar sits back from the junction it protects. Run after every segment set-up, and after any
    /// navigator tick that moved the target on by itself — an entry-alignment arc that retires the legs it was aimed
    /// past, or hands a fillet over on the aimed line, sets up the next target without this phase's set-up running.
    /// Only a bar whose stop lies on the current segment is aimed at: a stop set back past the segment's start is behind
    /// the aircraft, and <see cref="TryHoldAtSetBackStop"/> takes that hold on an earlier segment.
    /// </summary>
    private void AimAtPaintedBar(TaxiRoute route)
    {
        if (
            route.GetHoldShortAt(_nav.TargetNodeId) is { IsCleared: false, Latitude: { } barLat, Longitude: { } barLon } bar
            && StopLiesOnCurrentSegment(route, bar)
        )
        {
            _nav.OverrideTargetPosition(barLat, barLon);
        }
    }

    private static bool StopLiesOnCurrentSegment(TaxiRoute route, HoldShortPoint bar) => route.StopLiesOnSegment(route.CurrentSegmentIndex, bar);

    /// <summary>
    /// Take the hold of the first uncleared bar ahead whose stop lies on a segment before the one that ends at the bar's
    /// node — a taxiway setback (length + 30 ft, or the wingtip floor) or a runway half-length longer than the bar's last
    /// segment. <see cref="ArriveAtNode"/> never sees that stop, since no segment ends there, so it is taken here, each tick,
    /// once the aircraft is within <see cref="SetBackStopTakeFt"/> of it and down to a crawl; the navigator's speed plan
    /// already brakes to zero just short of it. An aircraft already past the stop (a bar armed inside its braking distance)
    /// is braked to a halt and holds where it stops. The decision is the bar's: a stop on the bar's own segment is
    /// <see cref="ArriveAtNode"/>'s, so no along-route walk runs for it.
    /// </summary>
    private bool TryHoldAtSetBackStop(PhaseContext ctx, TaxiRoute route)
    {
        if ((ctx.GroundLayout is not { } layout) || (FirstUnclearedBarAhead(route) is not { } ahead))
        {
            return false;
        }

        (int barSegmentIndex, HoldShortPoint bar) = ahead;
        if ((bar.Latitude is null) || route.StopLiesOnSegment(barSegmentIndex, bar))
        {
            return false;
        }

        double alongFt = AlongRouteDistanceToHoldShortFt(layout, route, ctx.Aircraft.Position, bar);
        if (alongFt > SetBackStopTakeFt)
        {
            HoldToSetBackStopCurve(ctx, alongFt);
            return false;
        }

        if ((ctx.Aircraft.IndicatedAirspeed > StartNodeHoldArmSpeedKts) || IsHeldByAnother(ctx, bar))
        {
            _nav.MaxSpeedKts = 0;
            return false;
        }

        WarnIfStationaryPastRunwayLine(ctx, bar, alongFt);
        TakeSetBackHold(ctx, route, bar, barSegmentIndex);
        return true;
    }

    /// <summary>
    /// Warns once per phase (<see cref="WarnNosePastHoldLineStationary"/>) when a set-back hold is taken with the centre
    /// <paramref name="alongFt"/> along the route from <paramref name="bar"/>'s painted stop already past it — the nose over the
    /// holding position marking — and the bar protects a runway.
    /// </summary>
    private void WarnIfStationaryPastRunwayLine(PhaseContext ctx, HoldShortPoint bar, double alongFt)
    {
        if (_loggedPastHoldLine || (alongFt >= 0.0) || !HoldingShortPhase.ProtectsRunway(bar))
        {
            return;
        }

        _loggedPastHoldLine = true;
        WarnNosePastHoldLineStationary(ctx, bar, -alongFt);
    }

    /// <summary>
    /// Caps the navigator's speed at the braking curve that reaches zero <see cref="GroundNavigator.SetBackStopMarginFt"/>
    /// short of a set-back stop <paramref name="alongFt"/> ahead. The navigator plans the same curve, but publishes no speed
    /// on the sub-tick it arrives at a node, and a stop behind a run of short segments loses one braking sub-tick at each;
    /// <see cref="CapAtUnclearedBar"/>, after the navigator ticks, pins the published target to the bar's curve on those.
    /// </summary>
    private void HoldToSetBackStopCurve(PhaseContext ctx, double alongFt)
    {
        double toZeroNm = Math.Max(0.0, alongFt - GroundNavigator.SetBackStopMarginFt) / GeoMath.FeetPerNm;
        double decelRate = _nav.DecelRateKts ?? CategoryPerformance.TaxiDecelRate(ctx.Category);
        double curveKts = Math.Sqrt(2.0 * decelRate * toZeroNm * 3600.0);
        _nav.MaxSpeedKts = Math.Min(_nav.MaxSpeedKts, curveKts);
    }

    /// <summary>
    /// <see cref="ArriveAtNode"/>'s safety net at a set-back stop: another aircraft already holds at this bar, so this one
    /// stops where it is and waits rather than taking the same stop.
    /// </summary>
    private static bool IsHeldByAnother(PhaseContext ctx, HoldShortPoint bar) => ctx.IsHoldShortNodeOccupied?.Invoke(bar.NodeId) == true;

    /// <summary>The first uncleared bar on a segment from the current one on, with the index of the segment that ends at it.</summary>
    private static (int SegmentIndex, HoldShortPoint Bar)? FirstUnclearedBarAhead(TaxiRoute route)
    {
        for (int i = Math.Max(0, route.CurrentSegmentIndex); i < route.Segments.Count; i++)
        {
            if (route.GetHoldShortAt(route.Segments[i].ToNodeId) is { IsCleared: false } bar)
            {
                return (i, bar);
            }
        }

        return null;
    }

    /// <summary>
    /// Stop at a set-back bar's stop, short of the segments that lead on to its node, and queue the hold and what follows
    /// it. A route that ends at a runway bar is finished here, as it would be at the node: the rest of the route lies
    /// past the holding position marking, and a line-up plans from where the aircraft stands. Otherwise the release drives
    /// the rest of the way: a new <see cref="TaxiingPhase"/> picks up the segment the aircraft is on, and arriving at the
    /// then-cleared bar hands a runway crossing to <see cref="CrossingRunwayPhase"/> as a pre-cleared crossing.
    /// </summary>
    private static void TakeSetBackHold(PhaseContext ctx, TaxiRoute route, HoldShortPoint bar, int barSegmentIndex)
    {
        Log.LogDebug(
            "[Taxi] {Callsign}: holding short of {Target} at its set-back stop, segment {SegIdx}, "
                + "{Segs} segment(s) short of node {NodeId} (reason {Reason})",
            ctx.Aircraft.Callsign,
            bar.TargetName,
            route.CurrentSegmentIndex,
            barSegmentIndex - route.CurrentSegmentIndex + 1,
            bar.NodeId,
            bar.Reason
        );

        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.MarkHoldShortNodeOccupied?.Invoke(bar.NodeId);

        var holdPhase = new HoldingShortPhase(bar);
        List<Phase> resumePhases;
        if ((barSegmentIndex == route.Segments.Count - 1) && holdPhase.ProtectsARunway)
        {
            route.CurrentSegmentIndex = barSegmentIndex;
            resumePhases = BuildResumePhases(ctx, route, bar, advancePastCurrentSegment: true);
        }
        else
        {
            resumePhases = [new TaxiingPhase()];
        }

        var insertList = new List<Phase> { holdPhase };
        insertList.AddRange(resumePhases);
        ctx.Aircraft.Phases?.InsertAfterCurrent(insertList);
    }

    /// <summary>
    /// The speed to plan the end of <paramref name="route"/> at. A taxi ends in a stop — 0 — unless the route
    /// terminates at its destination-runway bar AND a takeoff or line-up clearance is already stored: the
    /// aircraft is going through that bar, so braking to it and re-accelerating is time the departure loses for
    /// nothing. The flow-through speed is <see cref="CategoryPerformance.TaxiCornerSpeed"/>, which is what
    /// <see cref="LineUpGraphRoute.TryPlan"/> caps the line-up at, so the hand-off carries no speed step.
    /// </summary>
    private static double RouteEndSpeedKts(PhaseContext ctx, TaxiRoute route)
    {
        if (ctx.Aircraft.Phases?.DepartureClearance is not { Type: ClearanceType.ClearedForTakeoff or ClearanceType.LineUpAndWait })
        {
            return 0;
        }

        if (route.Segments.Count == 0 || route.HoldShortPoints.Count == 0)
        {
            return 0;
        }

        HoldShortPoint lastHoldShort = route.HoldShortPoints[^1];
        if ((lastHoldShort.Reason != HoldShortReason.DestinationRunway) || (lastHoldShort.NodeId != route.Segments[^1].ToNodeId))
        {
            return 0;
        }

        return CategoryPerformance.TaxiCornerSpeed(ctx.Category);
    }

    private static bool IsHoldShortCleared(TaxiRoute route, int nodeId)
    {
        HoldShortPoint? hs = route.GetHoldShortAt(nodeId);
        return hs is null || hs.IsCleared;
    }

    /// <summary>
    /// The speed a held aircraft is pinned to once the navigator has ticked and any node arrival is done: the hold's own
    /// speed <paramref name="heldKts"/> (<see cref="HeldSpeedKts"/>), capped by the braking an uncleared bar ahead needs
    /// (<see cref="BrakeForUnclearedBar"/>) — so a HOLD or GIVEWAY that leaves the aircraft inside its taxi-rate stopping
    /// distance of the bar's painted stop never carries the nose over a runway holding position marking.
    /// </summary>
    private double CapHeldSpeedAtUnclearedBar(PhaseContext ctx, TaxiRoute route, double heldKts) =>
        BrakeForUnclearedBar(ctx, route) is { } barCapKts ? Math.Min(heldKts, barCapKts) : heldKts;

    /// <summary>
    /// The navigator has reached an uncleared bar the phase brakes for (<see cref="BrakeForUnclearedBar"/>) with the aircraft
    /// still faster than <see cref="StartNodeHoldArmSpeedKts"/>: only an aircraft whose line was already lost when the route
    /// was given gets there at speed. The arrival waits while it brakes at the firm rate, so the hold is taken at a crawl
    /// rather than by stopping it dead from taxi speed.
    /// </summary>
    private bool IsRollingOntoUnclearedBar(PhaseContext ctx, TaxiRoute route) =>
        (route.GetHoldShortAt(_nav.TargetNodeId) is { IsCleared: false, Unable: false, Reason: not HoldShortReason.RouteIncomplete })
        && (ctx.Aircraft.IndicatedAirspeed > StartNodeHoldArmSpeedKts);

    /// <summary>
    /// Caps the published taxi target of an aircraft that is not held by the braking an uncleared bar ahead needs
    /// (<see cref="BrakeForUnclearedBar"/>): on a routine approach the taxi-rate curve the navigator trails by a tick, and when
    /// a TAXI re-route that drops a crossing clearance leaves the bar's painted stop inside the taxi-rate stopping distance,
    /// the firm-rate braking that still makes the marking. A target the navigator left unset (physics clears it on arrival at
    /// a goal) is capped from the current speed, never raised to the cap.
    /// </summary>
    private void CapAtUnclearedBar(PhaseContext ctx, TaxiRoute route)
    {
        if (BrakeForUnclearedBar(ctx, route) is { } barCapKts)
        {
            ctx.Targets.TargetSpeed = Math.Min(ctx.Targets.TargetSpeed ?? ctx.Aircraft.IndicatedAirspeed, barCapKts);
        }
    }

    /// <summary>
    /// The speed cap the first uncleared bar ahead sets on the aircraft's approach to the bar's painted stop (centre at the
    /// stop, nose at the holding position marking, AIM 2-3-5.a.1), by the choice a follower makes at a runway bar
    /// (<see cref="GroundStopBraking.ChooseStopBraking"/>) judged against the painted stop itself. Null when there is no such
    /// bar. When the taxi rate makes the stop, returns the taxi-rate braking curve read where this tick's travel leaves the
    /// aircraft (<see cref="GroundStopBraking.StopCurveKts"/>), reaching zero at the navigator's own aim — the stop on the
    /// bar's segment, <see cref="GroundNavigator.SetBackStopMarginFt"/> short of a set-back one — less this tick's travel: the
    /// navigator reads its curve where the aircraft stands, a tick behind it, so riding that curve alone would carry the nose
    /// over a set-back marking. When only the firm rate (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>) makes it,
    /// publishes that as the brake rate and returns the braking curve at it, which reaches zero
    /// <see cref="GroundNavigator.SetBackStopMarginFt"/> (2 ft) short of the stop. When not even the firm rate
    /// makes it, publishes the firm rate and returns zero, and for a runway bar
    /// (<see cref="HoldingShortPhase.ProtectsRunway"/>) takes the last resort (<see cref="StopAtRunwayBarLastResort"/>).
    /// A taxiway bar is never stopped dead here: the firm rate brakes the aircraft toward its stop, and the hold is taken
    /// where it gets there — by <see cref="TryHoldAtSetBackStop"/> at a crawl for a set-back stop, by
    /// <see cref="ArriveAtNode"/> for a stop on the bar's own segment. Runs after the navigator ticks, since the navigator
    /// republishes the brake rate every tick. A bar already called unmakeable (<see cref="HoldShortPoint.Unable"/>) is left
    /// to its moved stop, and a route-incomplete end (<see cref="HoldShortReason.RouteIncomplete"/>) to the navigator's
    /// routine braking. An uncleared runway bar on the route's start node whose marking the nose is already at or past
    /// (<see cref="PassedStartBar"/>) comes first: the line is lost, so the aircraft stops as soon as the firm rate lets it,
    /// to keep off the runway's pavement, never dead from taxi speed.
    /// </summary>
    private double? BrakeForUnclearedBar(PhaseContext ctx, TaxiRoute route)
    {
        if (PassedStartBar(ctx, route) is { } startBar)
        {
            BrakeFirmPastStartBar(ctx, route, startBar);
            return 0.0;
        }

        if (
            (ctx.GroundLayout is not { } layout)
            || (
                FirstUnclearedBarAhead(route)
                is not { SegmentIndex: var barSegmentIndex, Bar: { Unable: false, Reason: not HoldShortReason.RouteIncomplete } bar }
            )
        )
        {
            return null;
        }

        double toStopFt = AlongRouteDistanceToHoldShortFt(layout, route, ctx.Aircraft.Position, bar);
        GroundStopBraking.StopBraking braking = GroundStopBraking.ChooseStopBraking(ctx, toStopFt);
        if (braking == GroundStopBraking.StopBraking.Routine)
        {
            double aimFt = route.StopLiesOnSegment(barSegmentIndex, bar) ? toStopFt + GroundNavigator.SetBackStopMarginFt : toStopFt;
            return GroundStopBraking.StopCurveKts(ctx, aimFt, CategoryPerformance.TaxiDecelRate(ctx.Category));
        }

        double firmRate = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        ctx.Targets.DesiredDecelRate = firmRate;
        if (braking == GroundStopBraking.StopBraking.MaxEffort)
        {
            return GroundStopBraking.StopCurveKts(ctx, toStopFt, firmRate);
        }

        if (HoldingShortPhase.ProtectsRunway(bar))
        {
            StopAtRunwayBarLastResort(ctx, bar, toStopFt);
        }

        return 0.0;
    }

    /// <summary>
    /// The last resort at an uncleared runway bar that not even the firm rate stops the aircraft at, braking at the firm rate
    /// already. With the nose short of the holding position marking, on the tick before the nose would reach it even braking at
    /// the firm rate, the aircraft is stopped dead where it is, logged as a warning. With the nose already past the marking
    /// (<paramref name="toStopFt"/> negative: the route was given with the line lost), the start-bar policy applies instead:
    /// the firm rate stops the aircraft as soon as it can, never dead from taxi speed, and the hold is taken once it is down
    /// to a crawl; the overrun is logged as a warning once.
    /// </summary>
    private void StopAtRunwayBarLastResort(PhaseContext ctx, HoldShortPoint bar, double toStopFt)
    {
        if (toStopFt < 0.0)
        {
            if (!_loggedPastHoldLine && (ctx.Aircraft.GroundSpeed > 0))
            {
                _loggedPastHoldLine = true;
                WarnNosePastHoldLineBraking(ctx, bar, -toStopFt);
            }

            return;
        }

        if ((ctx.Aircraft.IndicatedAirspeed > 0) && (toStopFt <= (GroundStopBraking.MaxEffortBrakingTravelThisTickFt(ctx) + SetBackStopTakeFt)))
        {
            Log.LogWarning(
                "[Taxi] {Callsign}: stopped dead short of {Target} at node {NodeId}, nose {ToStop:F1} ft from the holding position marking "
                    + "at {Speed:F1} kt: no brake rate made the line",
                ctx.Aircraft.Callsign,
                bar.TargetName,
                bar.NodeId,
                toStopFt,
                ctx.Aircraft.GroundSpeed
            );
            ctx.Aircraft.IndicatedAirspeed = 0;
        }
    }

    /// <summary>
    /// Warns that the aircraft's nose is <paramref name="pastFt"/> past the holding position marking of the uncleared runway bar
    /// <paramref name="bar"/> while it is still braking.
    /// </summary>
    private static void WarnNosePastHoldLineBraking(PhaseContext ctx, HoldShortPoint bar, double pastFt) =>
        Log.LogWarning(
            "[Taxi] {Callsign}: nose {PastFt:F0} ft past the hold line for {Target}, braking from {Speed:F1} kt",
            ctx.Aircraft.Callsign,
            pastFt,
            bar.TargetName,
            ctx.Aircraft.GroundSpeed
        );

    /// <summary>
    /// Warns that the aircraft's nose is <paramref name="pastFt"/> past the holding position marking of the uncleared runway bar
    /// <paramref name="bar"/> with the aircraft standing still.
    /// </summary>
    private static void WarnNosePastHoldLineStationary(PhaseContext ctx, HoldShortPoint bar, double pastFt) =>
        Log.LogWarning(
            "[Taxi] {Callsign}: nose {PastFt:F0} ft past the hold line for {Target}, stationary",
            ctx.Aircraft.Callsign,
            pastFt,
            bar.TargetName
        );

    /// <summary>
    /// Brakes an aircraft whose nose is over the marking of the uncleared runway bar its route starts on at the firm rate
    /// (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>), published as the brake rate, and logs it
    /// (<see cref="LogPassedStartBar"/>).
    /// </summary>
    private void BrakeFirmPastStartBar(PhaseContext ctx, TaxiRoute route, HoldShortPoint startBar)
    {
        ctx.Targets.DesiredDecelRate = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        LogPassedStartBar(ctx, route, startBar);
    }

    /// <summary>
    /// Logs, once per phase, a nose over the marking of the uncleared runway bar the route starts on: the stationary warning
    /// (<see cref="WarnNosePastHoldLineStationary"/>) when the aircraft is standing still; a warning when it was already over
    /// and moving on the phase's first tick (a re-route given past the line); otherwise at debug level (an approach to the
    /// start node that carried the nose onto the marking).
    /// </summary>
    private void LogPassedStartBar(PhaseContext ctx, TaxiRoute route, HoldShortPoint startBar)
    {
        if (_loggedPastHoldLine)
        {
            return;
        }

        _loggedPastHoldLine = true;
        if (ctx.Aircraft.GroundSpeed <= 0)
        {
            TaxiRouteSegment first = route.Segments[0];
            WarnNosePastHoldLineStationary(ctx, startBar, NosePastNodeFt(ctx.Aircraft, first.Edge.FromNode.Position, first.Edge.DepartureBearing));
            return;
        }

        if (ElapsedSeconds <= (ctx.DeltaSeconds + FirstTickSlackSeconds))
        {
            Log.LogWarning(
                "[Taxi] {Callsign}: route starts on the uncleared hold-short of {Target} at node {NodeId} with the nose already past its "
                    + "marking at {Speed:F1} kt: stopping at the firm rate",
                ctx.Aircraft.Callsign,
                startBar.TargetName,
                startBar.NodeId,
                ctx.Aircraft.GroundSpeed
            );
            return;
        }

        Log.LogDebug(
            "[Taxi] {Callsign}: nose over the marking of the start-node bar of {Target} at node {NodeId} at {Speed:F1} kt: stopping at the firm rate",
            ctx.Aircraft.Callsign,
            startBar.TargetName,
            startBar.NodeId,
            ctx.Aircraft.GroundSpeed
        );
    }

    /// <summary>
    /// The uncleared runway hold-short (<see cref="HoldingShortPhase.ProtectsRunway"/>) on the route's start node once the
    /// aircraft has been found heading along the first segment (within <see cref="StartBarAlignedDeg"/> of its departure) with
    /// its nose at or past the bar's marking — the line through the node square to that departure — no farther from the node
    /// than <see cref="StartNodeHoldRadiusFt"/> plus its firm-rate stopping distance; null otherwise. A TAXI starts the route
    /// on a bar's node only once the aircraft is that close to it, so the nose is often over already. The finding is latched
    /// on the bar's node (<see cref="_passedStartBarNodeId"/>), so the firm-rate stop it starts is never let go part-way as
    /// the aircraft slows and the stop carries it away from the node; the latch drops when the bar is cleared, and the hold
    /// clears it when taken.
    /// </summary>
    private HoldShortPoint? PassedStartBar(PhaseContext ctx, TaxiRoute route)
    {
        if (
            (route.Segments.Count == 0)
            || (ctx.GroundLayout is not { } layout)
            || (route.GetHoldShortAt(route.Segments[0].FromNodeId) is not { IsCleared: false } bar)
            || !HoldingShortPhase.ProtectsRunway(bar)
        )
        {
            _passedStartBarNodeId = null;
            return null;
        }

        if (_passedStartBarNodeId == bar.NodeId)
        {
            return bar;
        }

        TaxiRouteSegment first = route.Segments[0];
        if (!layout.Nodes.TryGetValue(first.FromNodeId, out GroundNode? node) || !IsNoseOverStartBar(ctx, node, first))
        {
            return null;
        }

        _passedStartBarNodeId = bar.NodeId;
        return bar;
    }

    /// <summary>
    /// Whether the aircraft is heading along <paramref name="first"/> (within <see cref="StartBarAlignedDeg"/> of its
    /// departure) with its nose at or past the line through <paramref name="node"/> square to that departure, no farther from
    /// the node than <see cref="StartNodeHoldRadiusFt"/> plus its firm-rate stopping distance.
    /// </summary>
    private static bool IsNoseOverStartBar(PhaseContext ctx, GroundNode node, TaxiRouteSegment first)
    {
        double firmStopFt = GroundStopBraking.StoppingDistanceFt(
            ctx.Aircraft.IndicatedAirspeed,
            CategoryPerformance.ExpediteExitDecelRate(ctx.Category)
        );
        bool nearNode = (GeoMath.DistanceNm(ctx.Aircraft.Position, node.Position) * GeoMath.FeetPerNm) <= (StartNodeHoldRadiusFt + firmStopFt);
        bool aligned = GeoMath.AbsBearingDifference(ctx.Aircraft.TrueHeading.Degrees, first.Edge.DepartureBearing) < StartBarAlignedDeg;
        return nearNode && aligned && (NosePastNodeFt(ctx.Aircraft, node.Position, first.Edge.DepartureBearing) >= 0.0);
    }

    /// <summary>
    /// How far (ft) the aircraft's nose — its centre projected half its length ahead — is past the line through
    /// <paramref name="node"/> square to <paramref name="departureBearingDeg"/>, measured along that bearing; negative when short.
    /// </summary>
    private static double NosePastNodeFt(AircraftState aircraft, LatLon node, double departureBearingDeg)
    {
        double halfLengthNm = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        LatLon nose = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, halfLengthNm);
        double distFt = GeoMath.DistanceNm(node, nose) * GeoMath.FeetPerNm;
        double offRad = GeoMath.SignedBearingDifference(departureBearingDeg, GeoMath.BearingTo(node, nose)) * Math.PI / 180.0;
        return distFt * Math.Cos(offRad);
    }

    /// <summary>
    /// The speed a held aircraft is pinned to by the hold itself. HOLD, and a GIVEWAY with no give-way point
    /// (<see cref="GroundConflictDetector.GiveWayStop"/> finds none), stop it where it is. A GIVEWAY whose route meets the
    /// traffic's ahead keeps it taxiing on its route toward the give-way point — where its centre, and its nose, first come
    /// within wingtip clearance of the traffic's track through the junction — or toward the first stop short of it
    /// (<see cref="DistanceToNextHeldStopFt"/>: a hold-short's painted stop of any kind not yet passed, or the route's end),
    /// whichever comes first, held to the braking curve that stops it there, so the arrival has no speed left to drop: at the
    /// taxi brake rate; at the firm rate (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>), published as the brake
    /// rate, when only that makes the point — the choice a follower makes at a runway bar
    /// (<see cref="GroundStopBraking.ChooseStopBraking"/>). Unlike the follower, it never stops dead for its give-way point:
    /// when not even the firm rate makes the point, or it is already inside the clearance, it brakes at the firm rate to a stop
    /// wherever that takes it — short of an uncleared bar, which <see cref="CapHeldSpeedAtUnclearedBar"/> still stops it at. Writes the
    /// published brake rate and the once-per-target log latch, which a hold that is no longer a GIVEWAY clears.
    /// </summary>
    private double HeldSpeedKts(PhaseContext ctx, TaxiRoute route)
    {
        if (ctx.Aircraft.Ground.Hold is not { Kind: HoldKind.GiveWay, YieldTarget: { } yieldTarget })
        {
            _loggedGiveWayTarget = null;
            return 0.0;
        }

        string? noStopReason = "the traffic was not found";
        if (
            (ctx.AircraftLookup?.Invoke(yieldTarget) is not { } target)
            || (GroundConflictDetector.GiveWayStop(ctx.Aircraft, target, out noStopReason) is not { } stop)
        )
        {
            if (_loggedGiveWayTarget != yieldTarget)
            {
                _loggedGiveWayTarget = yieldTarget;
                Log.LogDebug(
                    "[Taxi] {Callsign}: no give-way point for {Target} ({Reason}): stopping where it is",
                    ctx.Aircraft.Callsign,
                    yieldTarget,
                    noStopReason
                );
            }

            return 0.0;
        }

        // The stop is braked for at whichever comes first: the give-way point or the next bar's painted stop or route end.
        double toNodeStopFt = DistanceToNextHeldStopFt(ctx, route);
        double toStopFt = Math.Min(stop.ToStopFt, toNodeStopFt);
        GroundStopBraking.StopBraking braking = GroundStopBraking.ChooseStopBraking(ctx, toStopFt);
        if (_loggedGiveWayTarget != yieldTarget)
        {
            _loggedGiveWayTarget = yieldTarget;
            Log.LogDebug(
                "[Taxi] {Callsign}: giving way to {Target} at node {NodeId}, {ToStop:F1} ft from its stop (the give-way point clear of "
                    + "its track {GiveWay:F1} ft, the next node a held arrival stops at {NodeStop:F1} ft), {Speed:F1} kt, {Braking} braking",
                ctx.Aircraft.Callsign,
                yieldTarget,
                stop.NodeId,
                toStopFt,
                stop.ToStopFt,
                toNodeStopFt,
                ctx.Aircraft.GroundSpeed,
                braking
            );
        }

        double rate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        if (braking != GroundStopBraking.StopBraking.Routine)
        {
            rate = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
            ctx.Targets.DesiredDecelRate = rate;
        }

        // A GIVEWAY never stops dead: when not even the firm rate makes its stop, or the aircraft is already inside the
        // clearance, it brakes at the firm rate and stops where that takes it.
        if (braking == GroundStopBraking.StopBraking.Backstop)
        {
            return 0.0;
        }

        return Math.Min(ctx.Targets.TargetSpeed ?? 0.0, GroundStopBraking.StopCurveKts(ctx, toStopFt, rate));
    }

    /// <summary>
    /// How far (ft) along the route the aircraft is from the next place a GIVEWAY stops it: the first node ahead carrying a
    /// hold-short of any kind, or the route's last. A hold-short, cleared or not, is measured to its painted stop
    /// (<see cref="AlongRouteDistanceToHoldShortFt"/>, where the navigator is aimed at an uncleared bar), so a GIVEWAY never
    /// leaves the nose over a holding-position marking (AIM 2-3-5.a.1); the route's end is measured to the node. A cleared
    /// bar whose painted stop is already behind the aircraft is passed over for the next stop after it: the aircraft is
    /// past the hold line and carries on across rather than stopping between the marking and the runway (AIM 4-3-21.a). An
    /// uncleared one behind it still reads zero. The segment in progress is measured straight from the aircraft to its end
    /// node. Never negative; infinity with no segment ahead.
    /// </summary>
    private static double DistanceToNextHeldStopFt(PhaseContext ctx, TaxiRoute route)
    {
        int first = Math.Max(0, route.CurrentSegmentIndex);
        double alongFt = 0;
        for (int i = first; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            alongFt +=
                i == first
                    ? GeoMath.DistanceNm(ctx.Aircraft.Position, seg.Edge.ToNode.Position) * GeoMath.FeetPerNm
                    : seg.Edge.DistanceNm * GeoMath.FeetPerNm;
            if (route.GetHoldShortAt(seg.ToNodeId) is { } holdShort)
            {
                double toPaintedStopFt = ctx.GroundLayout is { } layout
                    ? AlongRouteDistanceToHoldShortFt(layout, route, ctx.Aircraft.Position, holdShort)
                    : double.PositiveInfinity;
                if (!holdShort.IsCleared || (toPaintedStopFt >= 0.0))
                {
                    return Math.Max(0.0, Math.Min(alongFt, toPaintedStopFt));
                }
            }

            if (i == route.Segments.Count - 1)
            {
                return alongFt;
            }
        }

        return double.PositiveInfinity;
    }

    /// <summary>
    /// The navigator's target node carries no uncleared hold-short and is not the route's last: arriving there under a hold
    /// runs <see cref="ArriveAtNode"/> rather than stopping, so a node with no bar only advances to the next segment and a
    /// cleared bar hands off to the crossing it clears (a held aircraft already past a cleared bar's hold line carries on
    /// across rather than stopping between the marking and the runway, AIM 4-3-21.a).
    /// </summary>
    private bool IsPassThroughNode(TaxiRoute route) =>
        (route.GetHoldShortAt(_nav.TargetNodeId) is null or { IsCleared: true }) && ((route.CurrentSegmentIndex + 1) < route.Segments.Count);

    private bool ArriveAtNode(PhaseContext ctx, TaxiRoute route)
    {
        Log.LogDebug(
            "[Taxi] {Callsign}: arrived at node {NodeId} (seg {SegIdx}/{SegCount}) gs={Gs:F2}",
            ctx.Aircraft.Callsign,
            _nav.TargetNodeId,
            route.CurrentSegmentIndex,
            route.Segments.Count,
            ctx.Aircraft.GroundSpeed
        );

        // Update taxiway name from the segment that brought us here
        string? arrivedTaxiway = null;
        if (route.CurrentSegment is { } arrivedSeg)
        {
            ctx.Aircraft.Ground.CurrentTaxiway = arrivedSeg.TaxiwayName;
            arrivedTaxiway = arrivedSeg.TaxiwayName;
        }

        // Fire AT-ground triggers for spot/parking/intersection (node match) and taxiway
        // (newTaxiwayName match). Idempotent against already-applied blocks.
        FlightPhysics.NotifyGroundEntityReached(ctx.Aircraft, arrivedNodeId: _nav.TargetNodeId, newTaxiwayName: arrivedTaxiway);

        // Check if this node is a hold-short point
        HoldShortPoint? holdShort = route.GetHoldShortAt(_nav.TargetNodeId);
        if (holdShort is not null && !holdShort.IsCleared)
        {
            // Safety net: if another aircraft is already holding at this node, don't snap to it.
            if (ctx.IsHoldShortNodeOccupied?.Invoke(_nav.TargetNodeId) == true)
            {
                ctx.Aircraft.IndicatedAirspeed = 0;
                ctx.Targets.TargetSpeed = 0;
                Log.LogDebug(
                    "[Taxi] {Callsign}: hold-short node {NodeId} occupied by another aircraft, waiting",
                    ctx.Aircraft.Callsign,
                    _nav.TargetNodeId
                );
                return false;
            }

            Log.LogDebug(
                "[Taxi] {Callsign}: hold short at node {NodeId} (target {Target}, reason {Reason}) gsAtArrival={Gs:F2}",
                ctx.Aircraft.Callsign,
                _nav.TargetNodeId,
                holdShort.TargetName,
                holdShort.Reason,
                ctx.Aircraft.GroundSpeed
            );

            ctx.MarkHoldShortNodeOccupied?.Invoke(_nav.TargetNodeId);

            // Residual cleanup: the navigator's pre-arrival BRAKE-CLAMP reduces gs to a
            // sub-kt residual (~0.5–1 kt) by enforcing the kinematic curve every sub-tick,
            // but can't reach exactly zero because the curve is asymptotically steep near
            // d=0 and the aircraft advances by gs·dt each physics sub-tick. This snap
            // cleans up that residual — cosmetically invisible at sub-kt.
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;

            var holdPhase = new HoldingShortPhase(holdShort);
            List<Phase> resumePhases = BuildResumePhases(ctx, route, holdShort, advancePastCurrentSegment: true);

            var insertList = new List<Phase> { holdPhase };
            insertList.AddRange(resumePhases);
            ctx.Aircraft.Phases?.InsertAfterCurrent(insertList);
            return true;
        }

        // Pre-cleared runway crossing: the hold-short was cleared (CROSS, auto-cross,
        // or exit clearance) before the aircraft reached it, so it doesn't stop here.
        // Still hand off to a CrossingRunwayPhase so it tracks the painted line across
        // the runway and clears its tail past the far side — but only for a genuine
        // forward crossing (a near-side hold-short with a matching far-side hold-short
        // of the same runway ahead). The far-side hold-short of a runway the aircraft
        // has already left (landing-rollout vacate, or a crossing it just finished) has
        // no forward same-runway exit and stays in TaxiingPhase.
        if (
            holdShort is { IsCleared: true }
            && NeedsRunwayCrossing(holdShort, ctx.GroundLayout)
            && FindRunwayCrossingExitNode(route, holdShort, ctx.GroundLayout, requireSameRunwayExit: true) is { } crossExitNodeId
        )
        {
            ctx.Aircraft.Phases?.InsertAfterCurrent(BuildPreClearedCrossingPhases(ctx, route, holdShort, crossExitNodeId));
            _completingIntoMovingCrossing = true;
            return true;
        }

        // Advance to next segment.
        int prevIdx = route.CurrentSegmentIndex;
        route.CurrentSegmentIndex += 1;
        if (route.CurrentSegmentIndex > route.Segments.Count)
        {
            route.CurrentSegmentIndex = route.Segments.Count;
        }
        Log.LogDebug(
            "[Taxi] {Callsign}: advance segment {Prev}→{Next}/{Total} pos=({Lat:F6},{Lon:F6}) hdg={Hdg:F1} ias={Ias:F1}",
            ctx.Aircraft.Callsign,
            prevIdx,
            route.CurrentSegmentIndex,
            route.Segments.Count,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            ctx.Aircraft.TrueHeading.Degrees,
            ctx.Aircraft.IndicatedAirspeed
        );

        if (route.IsComplete)
        {
            return CompleteRoute(ctx, route);
        }

        SetupCurrentSegment(ctx, route);
        return false;
    }

    /// <summary>
    /// Finish the route: apply any pending departure clearance and insert the terminal phase
    /// (<see cref="AtParkingPhase"/> for a gate the route actually reaches — see
    /// <see cref="EndsAtParkingStand"/> — otherwise <see cref="HoldingInPositionPhase"/>, since a spot is an
    /// intermediate waypoint where the aircraft awaits further instructions, not a parked gate).
    /// Shared by normal last-segment arrival and the nose-at-spot terminal stop.
    /// </summary>
    private static bool CompleteRoute(PhaseContext ctx, TaxiRoute route)
    {
        Log.LogDebug("[Taxi] {Callsign}: route complete after {SegCount} segments", ctx.Aircraft.Callsign, route.Segments.Count);

        ApplyDepartureClearanceIfPending(ctx);

        PhaseList? phases = ctx.Aircraft.Phases;
        if (phases is not null && phases.Phases.Count <= phases.CurrentIndex + 1)
        {
            if (EndsAtParkingStand(ctx, route))
            {
                ctx.Aircraft.Ground.ParkingSpot = route.DestinationParking;
                phases.InsertAfterCurrent(new AtParkingPhase());
            }
            else
            {
                phases.InsertAfterCurrent(new HoldingInPositionPhase());
            }
        }

        return true;
    }

    /// <summary>
    /// The route really ended on the stand it named: its last node <em>is</em> that stand's node. A route that
    /// stops short — refused an extension, cut off at a via, truncated at a crossing — leaves the aircraft
    /// somewhere else entirely, and parking it there would put an <see cref="AtParkingPhase"/> on a gate the
    /// aircraft never reached. With no layout to resolve the name against, the named destination is all there
    /// is and it is taken at face value.
    /// </summary>
    /// <param name="ctx">Phase context, for the layout.</param>
    /// <param name="route">The completed route.</param>
    /// <returns>True when the terminal phase is <see cref="AtParkingPhase"/>.</returns>
    private static bool EndsAtParkingStand(PhaseContext ctx, TaxiRoute route)
    {
        if (route.DestinationParking is not { } parking)
        {
            return false;
        }

        if (ctx.GroundLayout is not { } layout)
        {
            return true;
        }

        GroundNode? stand = layout.FindHelipadByName(parking) ?? layout.FindParkingByName(parking);
        if ((stand is null) || (route.Segments.Count == 0))
        {
            return true;
        }

        return route.Segments[^1].ToNodeId == stand.Id;
    }

    /// <summary>
    /// Parking terminal stop for a taxi-to-spot route: brings the aircraft to rest with its nose at the
    /// spot marking (a half-fuselage short of the spot node) rather than its centroid, and slows the
    /// final approach so the stop lands cleanly. Returns true (route completed, aircraft stopped) once
    /// the nose reaches the spot; otherwise applies the slow-approach cap and returns false. No-op for
    /// non-spot routes. The half-length setback mirrors the runway-hold-short "nose at line" offset in
    /// <see cref="HoldShortAnnotator.ComputeHoldShortPositions"/>. See issue #234 (SFO spot 7A over A).
    /// </summary>
    private bool TryStopNoseAtSpot(PhaseContext ctx, TaxiRoute route)
    {
        // Only a taxi-to-spot destination gets the setback, and only on the final approach segments
        // (guards against a long route that merely passes near the spot node earlier).
        if (
            route.DestinationSpot is null
            || ctx.GroundLayout is not { } layout
            || route.Segments.Count == 0
            || route.CurrentSegmentIndex < route.Segments.Count - 2
            || !layout.Nodes.TryGetValue(route.Segments[^1].ToNodeId, out GroundNode? spotNode)
        )
        {
            return false;
        }

        double lengthFt = AircraftLength.ResolveFt(ctx.Aircraft.AircraftType);
        double halfLenNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;
        double distToSpotNm = GeoMath.DistanceNm(ctx.Aircraft.Position, spotNode.Position);

        if (distToSpotNm <= halfLenNm)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            route.CurrentSegmentIndex = route.Segments.Count;
            TaxiRouteSegment lastLeg = route.Segments[^1];
            Log.LogDebug(
                "[Taxi] {Callsign}: nose-at-spot stop at {Spot} — centroid {Dist:F0}ft from spot node (half-length {Half:F0}ft), "
                    + "hdg {Hdg:F0} against last leg {Leg} bearing {LegBrg:F0}",
                ctx.Aircraft.Callsign,
                route.DestinationSpot,
                distToSpotNm * GeoMath.FeetPerNm,
                lengthFt / 2.0,
                ctx.Aircraft.TrueHeading.Degrees,
                lastLeg.TaxiwayName,
                lastLeg.Edge.ArrivalBearing
            );
            return CompleteRoute(ctx, route);
        }

        // Within one fuselage of the spot: slow to a parking crawl so the stop above lands cleanly.
        if (distToSpotNm <= 2.0 * halfLenNm)
        {
            _nav.MaxSpeedKts = Math.Min(_nav.MaxSpeedKts, SpotApproachSpeedKts);
        }

        return false;
    }

    /// <summary>
    /// A bare <c>TAXI &lt;rwy&gt;</c> issued at the runway's own bar resolves to a route with no segments and one
    /// destination hold-short (<see cref="TaxiPathfinder.FindAdjacentRunwayRoute"/>). There is nothing to
    /// navigate — the aircraft holds where it stands, or lines up straight away when a LUAW/CTO issued behind
    /// the TAXI has already cleared the bar.
    /// </summary>
    private static bool IsHoldAtStartOnly(TaxiRoute route) => (route.Segments.Count == 0) && (route.HoldShortPoints.Count == 1);

    /// <summary>
    /// Finish a segment-less route: roll to a stop if still moving, then either take the hold (uncleared bar)
    /// exactly as a route that reaches its bar does, or complete the route so a stored departure clearance
    /// applies (the bar was pre-cleared by <c>DepartureClearanceHandler.StoreDepartureClearanceDuringTaxi</c>).
    /// </summary>
    private static bool TickHoldAtStartOnly(PhaseContext ctx, TaxiRoute route)
    {
        if (ctx.Aircraft.IndicatedAirspeed > StartNodeHoldArmSpeedKts)
        {
            // Physics brakes toward the pinned target at the ground decel rate.
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        HoldShortPoint holdShort = route.HoldShortPoints[0];
        if (holdShort.IsCleared)
        {
            return CompleteRoute(ctx, route);
        }

        TakeHoldShort(ctx, route, holdShort);
        return true;
    }

    /// <summary>
    /// Take the hold-short sitting on the route's own start node, if any is still binding. Used when a
    /// TAXI re-route is issued to an aircraft at or approaching a runway holding position and the new
    /// route crosses that runway: the bar the route starts on is the one to honour, so the aircraft
    /// holds there rather than driving over the runway to the bar on the far side (issue #316).
    ///
    /// Checked every tick while the hold-short could still bind — not once. A re-route can arrive with
    /// the aircraft still rolling toward the bar from beyond the parked radius (a runway-exit hand-off
    /// on a sparse stretch whose nearest node is the bar); a single early check would let it sail
    /// through the bar and across the runway uncleared. While approaching, the navigator's speed is
    /// clamped to a braking curve that reaches ~0 just short of the bar; the hold itself is taken once
    /// the aircraft is close and essentially stopped. Not run while the nose is past the bar's marking: that hold is
    /// <see cref="TryHoldPastStartBar"/>'s.
    /// </summary>
    private bool TryHoldAtRouteStartNode(PhaseContext ctx, TaxiRoute route)
    {
        if ((route.CurrentSegmentIndex != 0) || (route.Segments.Count == 0) || (ctx.GroundLayout is null))
        {
            _startNodeHoldDone = true;
            return false;
        }

        int startNodeId = route.Segments[0].FromNodeId;
        HoldShortPoint? holdShort = route.GetHoldShortAt(startNodeId);
        if (holdShort is null || holdShort.IsCleared)
        {
            _startNodeHoldDone = true;
            return false;
        }

        if (!ctx.GroundLayout.Nodes.TryGetValue(startNodeId, out GroundNode? startNode))
        {
            _startNodeHoldDone = true;
            return false;
        }

        if (!IsReadyForStartNodeHold(ctx, startNode))
        {
            return false;
        }

        _startNodeHoldDone = true;
        TakeHoldShort(ctx, route, holdShort);
        return true;
    }

    /// <summary>
    /// Take the hold of the uncleared runway bar the route starts on once the nose has been found past its marking
    /// (<see cref="PassedStartBar"/>): the aircraft is braking at the firm rate (<see cref="BrakeForUnclearedBar"/>), and the
    /// hold is taken once it is down to one sub-tick of that braking, wherever that leaves it — on whichever segment, since
    /// the stop can carry it past a short first one. An aircraft already standing still there takes the hold at once.
    /// Checked every tick, never only until <see cref="TryHoldAtRouteStartNode"/> has settled: an <c>HS</c> that re-arms the
    /// start bar after that, and a phase restored from a snapshot (which does not carry that settlement), take the hold alike.
    /// </summary>
    private bool TryHoldPastStartBar(PhaseContext ctx, TaxiRoute route)
    {
        if (PassedStartBar(ctx, route) is not { } passedBar)
        {
            return false;
        }

        LogPassedStartBar(ctx, route, passedBar);
        if (ctx.Aircraft.IndicatedAirspeed > (CategoryPerformance.ExpediteExitDecelRate(ctx.Category) * ctx.DeltaSeconds))
        {
            return false;
        }

        _startNodeHoldDone = true;
        _passedStartBarNodeId = null;
        TakeHoldShort(ctx, route, passedBar);
        return true;
    }

    /// <summary>
    /// Whether the start-node hold can be taken this tick: the aircraft is within <see cref="StartNodeHoldRadiusFt"/> of the
    /// bar and down to a crawl. While it is still rolling toward the bar the navigator is capped to a braking curve that
    /// reaches zero just short of it (same form as the navigator's own hold-short braking).
    /// </summary>
    private bool IsReadyForStartNodeHold(PhaseContext ctx, GroundNode startNode)
    {
        double distFt = GeoMath.DistanceNm(ctx.Aircraft.Position, startNode.Position) * GeoMath.FeetPerNm;
        if ((distFt <= StartNodeHoldRadiusFt) && (ctx.Aircraft.IndicatedAirspeed <= StartNodeHoldArmSpeedKts))
        {
            return true;
        }

        double stopDistNm = Math.Max(0.0, distFt - StartNodeHoldStopShortFt) / GeoMath.FeetPerNm;
        double decelRate = _nav.DecelRateKts ?? CategoryPerformance.TaxiDecelRate(ctx.Category);
        _nav.MaxSpeedKts = Math.Min(_nav.MaxSpeedKts, Math.Sqrt(2.0 * decelRate * stopDistNm * 3600.0));
        return false;
    }

    /// <summary>Stop on <paramref name="holdShort"/> (a bar no segment leads to) and queue the hold + resume phases.</summary>
    private static void TakeHoldShort(PhaseContext ctx, TaxiRoute route, HoldShortPoint holdShort)
    {
        Log.LogDebug(
            "[Taxi] {Callsign}: holding short at route start node {NodeId} (target {Target}, reason {Reason})",
            ctx.Aircraft.Callsign,
            holdShort.NodeId,
            holdShort.TargetName,
            holdShort.Reason
        );

        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.MarkHoldShortNodeOccupied?.Invoke(holdShort.NodeId);

        var insertList = new List<Phase> { new HoldingShortPhase(holdShort) };
        insertList.AddRange(BuildResumePhases(ctx, route, holdShort, advancePastCurrentSegment: false));
        ctx.Aircraft.Phases?.InsertAfterCurrent(insertList);
    }

    /// <summary>
    /// Phases to run once <paramref name="holdShort"/> is released. <paramref name="advancePastCurrentSegment"/>
    /// is true when the aircraft reached the bar by arriving at the current segment's far end (that segment
    /// is spent), false when the bar is the route's start node and no segment has been traversed yet.
    ///
    /// <para>
    /// That one-segment advance is the whole of this method's cursor bookkeeping. A runway crossing does
    /// <i>not</i> walk <see cref="TaxiRoute.CurrentSegmentIndex"/> across its slice here — how far past the
    /// exit bar the aircraft ends up depends on the tail-clearance extension, so
    /// <see cref="CrossingRunwayPhase"/> writes the cursor itself when it completes and this method only
    /// asks <see cref="CrossingRunwayPhase.RouteIndexAfterCrossing"/> whether anything is left to taxi.
    /// Walking it here left the cursor on a segment the crossing had already driven past (issue #172).
    /// </para>
    /// </summary>
    private static List<Phase> BuildResumePhases(PhaseContext ctx, TaxiRoute route, HoldShortPoint holdShort, bool advancePastCurrentSegment)
    {
        var phases = new List<Phase>();
        if (advancePastCurrentSegment)
        {
            route.CurrentSegmentIndex++;
        }

        if (holdShort.Reason == HoldShortReason.DestinationRunway)
        {
            ApplyDepartureClearanceIfPending(ctx);
            PhaseList? phaseList = ctx.Aircraft.Phases;
            if (phaseList is not null && phaseList.Phases.Count <= phaseList.CurrentIndex + 1)
            {
                phases.Add(new HoldingInPositionPhase());
            }
            return phases;
        }

        int? crossingExitNodeId = null;
        bool routeContinues = !route.IsComplete;
        if (NeedsRunwayCrossing(holdShort, ctx.GroundLayout))
        {
            crossingExitNodeId = FindRunwayCrossingExitNode(route, holdShort, ctx.GroundLayout, requireSameRunwayExit: false);
            if (crossingExitNodeId is { } exitNodeId)
            {
                phases.Add(new CrossingRunwayPhase(holdShort.NodeId, exitNodeId, holdShort.TargetName));
                routeContinues = CrossingRunwayPhase.RouteIndexAfterCrossing(ctx, route, holdShort.NodeId, exitNodeId) < route.Segments.Count;
            }
        }

        if (routeContinues)
        {
            phases.Add(new TaxiingPhase());
        }
        else if (crossingExitNodeId is { } crossedTo)
        {
            phases.AddRange(BuildTerminalPhasesAtCrossingExit(ctx, route, crossedTo));
        }
        else
        {
            phases.Add(new HoldingInPositionPhase());
        }

        return phases;
    }

    /// <summary>
    /// Terminal phases for a route that ends where a runway crossing exits.
    ///
    /// <para>
    /// When the exit node carries an uncleared hold-short the aircraft is holding <i>short</i>, not holding in
    /// position: on parallel-runway geometry the far side of the crossed runway <i>is</i> the next runway's hold
    /// line, so the crossing slice swallows the destination-runway bar (SFO taxiway C — cross 10R/28L, hold short
    /// 28R, ~190 ft apart with a single painted bar between them). Emitting <see cref="HoldingShortPhase"/> keeps
    /// the pilot's "holding short runway 28R" report and routes LUAW/CTO through
    /// <c>DepartureClearanceHandler.LineUpFromHoldShort</c> rather than the position-based path (issue #315).
    /// </para>
    ///
    /// <para>
    /// Otherwise the crossing really did end the route, so a departure clearance stored during the taxi has to be
    /// consumed here the same way <see cref="CompleteRoute"/> consumes it — the crossing paths previously skipped
    /// that, stranding an aircraft whose route crossed a runway immediately before its departure runway.
    /// </para>
    /// </summary>
    private static List<Phase> BuildTerminalPhasesAtCrossingExit(PhaseContext ctx, TaxiRoute route, int exitNodeId)
    {
        if (route.GetHoldShortAt(exitNodeId) is { IsCleared: false } pendingHoldShort)
        {
            return [new HoldingShortPhase(pendingHoldShort), new HoldingInPositionPhase()];
        }

        ApplyDepartureClearanceIfPending(ctx);

        // ApplyDepartureClearanceIfPending inserts the tower phases after the current one; when it did, they own
        // the rest of the sequence and a trailing terminal phase would strand the aircraft behind them. Ordered
        // exactly as CompleteRoute does it — clearance first, then the terminal — so a stored clearance is never
        // dropped by a route that also names a parking destination.
        PhaseList? phaseList = ctx.Aircraft.Phases;
        if (phaseList is not null && phaseList.Phases.Count > phaseList.CurrentIndex + 1)
        {
            return [];
        }

        if (EndsAtParkingStand(ctx, route))
        {
            ctx.Aircraft.Ground.ParkingSpot = route.DestinationParking;
            return [new AtParkingPhase()];
        }

        return [new HoldingInPositionPhase()];
    }

    /// <summary>
    /// Build the phase sequence for a runway crossing whose hold-short was cleared
    /// before arrival (so no <see cref="HoldingShortPhase"/> stop): the
    /// <see cref="CrossingRunwayPhase"/> across the painted line, then the onward
    /// <see cref="TaxiingPhase"/> (or <see cref="BuildTerminalPhasesAtCrossingExit"/>
    /// if the route ends at the far side). Advances <see cref="TaxiRoute.CurrentSegmentIndex"/> past the
    /// arrived-at hold-short segment only — the crossing slice itself is the crossing phase's to walk, as
    /// in <see cref="BuildResumePhases"/>, but entered straight from a moving <see cref="TaxiingPhase"/>.
    /// </summary>
    private static List<Phase> BuildPreClearedCrossingPhases(PhaseContext ctx, TaxiRoute route, HoldShortPoint holdShort, int exitNodeId)
    {
        var phases = new List<Phase> { new CrossingRunwayPhase(holdShort.NodeId, exitNodeId, holdShort.TargetName) };

        // Skip past the arrived-at hold-short segment. The crossing slice ahead of it is consumed by the
        // CrossingRunwayPhase, which writes the cursor to wherever its tail-clearance extension ends.
        route.CurrentSegmentIndex++;

        if (CrossingRunwayPhase.RouteIndexAfterCrossing(ctx, route, holdShort.NodeId, exitNodeId) < route.Segments.Count)
        {
            phases.Add(new TaxiingPhase());
        }
        else
        {
            phases.AddRange(BuildTerminalPhasesAtCrossingExit(ctx, route, exitNodeId));
        }

        return phases;
    }

    /// <summary>
    /// The <see cref="CrossingRunwayPhase"/> for releasing a hold whose onward phases have been replaced by an
    /// armed follow (<c>FOLLOWG</c> at a runway bar), built from the route exactly as <see cref="BuildResumePhases"/>
    /// builds it when the hold is taken — but with no onward <see cref="TaxiingPhase"/> or terminal phase, because
    /// the follow, not the route, takes the aircraft on from the far side. Null when the bar is not a runway
    /// crossing, or when the route does not resume from <paramref name="holdShort"/> (its cursor segment starts
    /// elsewhere — a follower stopped at a bar its route never passes), so the caller finds a crossing another way.
    /// The route cursor is left where the hold put it; the crossing phase writes it when it completes.
    /// </summary>
    internal static CrossingRunwayPhase? BuildCrossingFromRouteBar(TaxiRoute route, HoldShortPoint holdShort, AirportGroundLayout? layout)
    {
        if (
            !NeedsRunwayCrossing(holdShort, layout)
            || (route.CurrentSegment is not { } resumeSegment)
            || (resumeSegment.FromNodeId != holdShort.NodeId)
        )
        {
            return null;
        }

        return FindRunwayCrossingExitNode(route, holdShort, layout, requireSameRunwayExit: false) is { } exitNodeId
            ? new CrossingRunwayPhase(holdShort.NodeId, exitNodeId, holdShort.TargetName)
            : null;
    }

    /// <summary>
    /// Whether crossing <paramref name="holdShort"/> means driving over a runway, so the aircraft needs
    /// a <see cref="CrossingRunwayPhase"/> rather than a plain segment advance. A hold-short sitting on
    /// a runway bar qualifies whether it is an implicit <see cref="HoldShortReason.RunwayCrossing"/> or an
    /// <see cref="HoldShortReason.ExplicitHoldShort"/> the controller armed there with <c>HS &lt;rwy&gt;</c> —
    /// otherwise the aircraft creeps across at taxi speed and the far-side bar is never consumed.
    /// </summary>
    private static bool NeedsRunwayCrossing(HoldShortPoint holdShort, AirportGroundLayout? layout)
    {
        if (holdShort.Reason == HoldShortReason.RunwayCrossing)
        {
            return true;
        }

        return holdShort.Reason == HoldShortReason.ExplicitHoldShort
            && layout is not null
            && layout.Nodes.TryGetValue(holdShort.NodeId, out GroundNode? node)
            && node.Type == GroundNodeType.RunwayHoldShort;
    }

    /// <summary>
    /// True when the route still has an already-cleared runway crossing ahead of the cursor — i.e. the
    /// aircraft will drive into a <see cref="CrossingRunwayPhase"/> without stopping first. Read-only:
    /// it asks the same three questions as the pre-cleared-crossing arm in <see cref="OnTick"/> but
    /// leaves the route untouched, so <see cref="Commands.CommandDispatcher"/> can use it to decide
    /// whether a <c>CROSS</c> just armed a crossing that later blocks in the compound should wait for.
    /// </summary>
    internal static bool HasPendingClearedRunwayCrossing(TaxiRoute? route, AirportGroundLayout? layout)
    {
        if (route is null)
        {
            return false;
        }

        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            if (route.GetHoldShortAt(route.Segments[i].ToNodeId) is not { IsCleared: true } holdShort)
            {
                continue;
            }

            if (NeedsRunwayCrossing(holdShort, layout) && FindSameRunwayExitNode(route, holdShort, layout) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scan the route forward from the current segment for the exit-side hold-short of the same runway
    /// as <paramref name="entryHoldShort"/> (the far side of the crossing), without touching it.
    /// Returns null when the route has no such far side ahead — which is how a genuine forward crossing
    /// is told apart from the far-side hold-short of a runway already behind the aircraft (a
    /// landing-rollout vacate, or a crossing it just completed).
    /// </summary>
    private static int? FindSameRunwayExitNode(TaxiRoute route, HoldShortPoint entryHoldShort, AirportGroundLayout? layout)
    {
        if (layout is null || entryHoldShort.TargetName is null)
        {
            return null;
        }

        var entryRwyId = RunwayIdentifier.Parse(entryHoldShort.TargetName);

        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            int nodeId = route.Segments[i].ToNodeId;

            if (
                nodeId != entryHoldShort.NodeId
                && layout.Nodes.TryGetValue(nodeId, out GroundNode? node)
                && node.Type == GroundNodeType.RunwayHoldShort
                && node.RunwayId is { } nodeRwyId
                && nodeRwyId.Equals(entryRwyId)
            )
            {
                return nodeId;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve the node the aircraft rolls to on the far side of the crossing, and clear that exit-side
    /// hold-short so it doesn't stop the aircraft again on the way out.
    ///
    /// <para>
    /// With <paramref name="requireSameRunwayExit"/> = false, a fallback returns the current segment's
    /// target node when no matching far-side hold-short exists (used by the resume-from-hold flow,
    /// where the route may represent a crossing with only one annotated hold-short). With it = true, a
    /// missing far-side hold-short returns null — see <see cref="FindSameRunwayExitNode"/>.
    /// </para>
    /// </summary>
    private static int? FindRunwayCrossingExitNode(
        TaxiRoute route,
        HoldShortPoint entryHoldShort,
        AirportGroundLayout? layout,
        bool requireSameRunwayExit
    )
    {
        if (FindSameRunwayExitNode(route, entryHoldShort, layout) is { } exitNodeId)
        {
            if (route.GetHoldShortAt(exitNodeId) is { } exitHs)
            {
                exitHs.IsCleared = true;
            }

            return exitNodeId;
        }

        if (requireSameRunwayExit)
        {
            return null;
        }

        if (route.CurrentSegmentIndex < route.Segments.Count)
        {
            return route.Segments[route.CurrentSegmentIndex].ToNodeId;
        }

        return null;
    }

    private void LogPeriodic(PhaseContext ctx, TaxiRoute route)
    {
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            TaxiRouteSegment? seg = route.CurrentSegment;
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_nav.TargetLat, _nav.TargetLon));
            Log.LogTrace(
                "[Taxi] {Callsign}: seg {SegIdx}/{SegCount} on {Taxiway}, target node {NodeId}, dist={Dist:F4}nm, gs={Gs:F1}kts, hdg={Hdg:F0}",
                ctx.Aircraft.Callsign,
                route.CurrentSegmentIndex,
                route.Segments.Count,
                seg?.TaxiwayName ?? "?",
                _nav.TargetNodeId,
                dist,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.TrueHeading.Degrees
            );
        }
    }

    internal static void ApplyDepartureClearanceIfPending(PhaseContext ctx)
    {
        PhaseList? phases = ctx.Aircraft.Phases;
        DepartureClearanceInfo? dep = phases?.DepartureClearance;
        if (dep is null || phases is null)
        {
            return;
        }

        // Hold-for-release: a held departure must not consume a takeoff clearance issued before its
        // airport was armed — it holds short of the runway until released (REL clears the flag).
        if (ctx.Aircraft.Ground.HeldForRelease)
        {
            return;
        }

        var lineup = new LineUpPhase();
        bool isHeli = ctx.Category == AircraftCategory.Helicopter;
        Phase takeoffPhase = isHeli ? new HelicopterTakeoffPhase() : new TakeoffPhase();

        // Rolling takeoff: if CTO is already in hand when the taxi phase
        // consumes the stored clearance, omit LinedUpAndWaitingPhase. See
        // DepartureClearanceHandler.InsertTowerPhasesAfterCurrent for the
        // holding-short insertion site that mirrors this branch.
        //
        // Super and Heavy aircraft are prohibited from rolling takeoffs per
        // 7110.65 §3-9-5.3. Fall back to the traditional stop-then-go
        // sequence with a pre-satisfied LUAW for those categories.
        bool rolling = dep.Type == ClearanceType.ClearedForTakeoff && LineUpPhase.IsAircraftEligibleForRollingTakeoff(ctx.Aircraft.AircraftType);
        bool isCircuit = Commands.DepartureClearanceHandler.IsCircuitDeparture(dep.Departure);
        LinedUpAndWaitingPhase? luawPhase = rolling ? null : new LinedUpAndWaitingPhase();

        if (isCircuit)
        {
            if (rolling)
            {
                phases.InsertAfterCurrent([lineup, takeoffPhase]);
            }
            else
            {
                phases.InsertAfterCurrent([lineup, luawPhase!, takeoffPhase]);
            }
        }
        else
        {
            var climb = new InitialClimbPhase
            {
                Departure = dep.Departure,
                AssignedAltitude = dep.AssignedAltitude,
                DepartureRoute = dep.DepartureRoute,
                DepartureProcedureLegs = dep.DepartureProcedureLegs,
                DepartureSidId = dep.DepartureSidId,
                SidDepartureHeadingMagnetic = dep.SidDepartureHeadingMagnetic,
                RvSidDeferHeadingUntilMinAlt = dep.RvSidDeferHeadingUntilMinAlt,
                RvSidHoldRunwayHeading = dep.RvSidHoldRunwayHeading,
                IsVfr = ctx.Aircraft.FlightPlan.IsVfr,
                CruiseAltitude = ctx.Aircraft.FlightPlan.Altitude.CruiseFeet ?? 0,
            };
            if (rolling)
            {
                phases.InsertAfterCurrent([lineup, takeoffPhase, climb]);
            }
            else
            {
                phases.InsertAfterCurrent([lineup, luawPhase!, takeoffPhase, climb]);
            }
        }

        if (dep.Type == ClearanceType.ClearedForTakeoff)
        {
            // For the non-rolling CTO path, LUAW must be pre-satisfied so
            // the aircraft doesn't hang waiting for an already-given
            // clearance. Rolling mode skips LUAW entirely.
            if (luawPhase is not null)
            {
                luawPhase.SatisfyClearance(ClearanceType.ClearedForTakeoff);
                luawPhase.Departure = dep.Departure;
                luawPhase.AssignedAltitude = dep.AssignedAltitude;
            }

            if (takeoffPhase is TakeoffPhase fwT)
            {
                fwT.SetAssignedDeparture(dep.Departure);
            }
            else if (takeoffPhase is HelicopterTakeoffPhase hpT)
            {
                hpT.SetAssignedDeparture(dep.Departure);
            }

            if (Commands.DepartureClearanceHandler.IsCircuitDeparture(dep.Departure) && phases.AssignedRunway is { } rwy)
            {
                // rwy is the departure runway here (AssignedRunway hasn't yet been overwritten to the
                // pattern runway). The circuit apply resolves the pattern runway and cross-runway case.
                Commands.DepartureClearanceHandler.ApplyCircuitDeparture(
                    dep.Departure,
                    ctx.Aircraft,
                    phases,
                    rwy,
                    dep.AssignedAltitude,
                    removeInitialClimb: false
                );
            }
        }

        phases.DepartureClearance = null;
        Log.LogDebug(
            "[Taxi] {Callsign}: departure clearance {Type} applied at route end (rolling={Rolling})",
            ctx.Aircraft.Callsign,
            dep.Type,
            rolling
        );
    }
}
