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

    // Fallback fuselage length (ft) for the nose-at-spot setback when the aircraft type has no FAA
    // length. Matches GroundConflictDetector's default footprint so both reason about the same length.
    private const double DefaultSpotStopLengthFt = 60.0;

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
        // before the first segment, re-checked each tick until the hold binds or stops applying.
        if (!held && !_startNodeHoldDone && TryHoldAtRouteStartNode(ctx, route))
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
            // the pinned target at the ground decel rate.
            ctx.Targets.TargetSpeed = 0;

            if (result == NavigatorResult.ArrivedAtNode)
            {
                // At the node and already braking: clean up the residual and leave the arrival itself to the
                // first un-held tick. A hold must not be able to insert a HoldingShortPhase or complete the
                // route — and so start a stored takeoff clearance's line-up — while the controller has said
                // hold.
                ctx.Aircraft.IndicatedAirspeed = 0;
            }

            return false;
        }

        if (result == NavigatorResult.ArrivedAtNode)
        {
            return ArriveAtNode(ctx, route);
        }

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
        };

    public static TaxiingPhase FromSnapshot(TaxiingPhaseDto dto)
    {
        var phase = new TaxiingPhase
        {
            // GroundNavigator's snapshot does not carry the active PathPrimitive
            // (or its arc/synthesis derived state). Force a re-init on the next
            // OnTick: SetupCurrentSegment will rebuild the primitive and speed
            // constraints from route.CurrentSegmentIndex. Without this, the next
            // Tick would see _currentPrimitive=null, return ArrivedAtNode, and
            // skip the segment the aircraft was traversing.
            _initialized = false,
            _timeSinceLastLog = dto.TimeSinceLastLog,
            _unableStopNodeId = dto.UnableStopNodeId,
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
    /// less the setback the bar sits back from that node. Negative once the bar is behind the aircraft, and
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
        if (
            holdShort.Latitude is not { } barLat
            || holdShort.Longitude is not { } barLon
            || !layout.Nodes.TryGetValue(holdShort.NodeId, out GroundNode? barNode)
        )
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
                return alongFt - (GeoMath.DistanceNm(new LatLon(barLat, barLon), barNode.Position) * GeoMath.FeetPerNm);
            }
        }

        return double.PositiveInfinity;
    }

    /// <summary>
    /// True when <paramref name="holdShort"/> is closer than the distance the aircraft needs to brake to a
    /// stop: the painted bar cannot be made and the aircraft will come to rest past it whatever it does.
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
    ) => AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, holdShort) < HoldShortBrakingDistanceFt(aircraft.GroundSpeed, category);

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

            _nav.OverrideTargetPosition(bar.Latitude.Value, bar.Longitude.Value);
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
    /// </summary>
    private void AimAtPaintedBar(TaxiRoute route)
    {
        if (route.GetHoldShortAt(_nav.TargetNodeId) is { IsCleared: false, Latitude: { } barLat, Longitude: { } barLon })
        {
            _nav.OverrideTargetPosition(barLat, barLon);
        }
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

        double lengthFt = FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? DefaultSpotStopLengthFt;
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
    /// the aircraft is close and essentially stopped.
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

        double distFt = GeoMath.DistanceNm(ctx.Aircraft.Position, startNode.Position) * GeoMath.FeetPerNm;
        if ((distFt > StartNodeHoldRadiusFt) || (ctx.Aircraft.IndicatedAirspeed > StartNodeHoldArmSpeedKts))
        {
            // Still rolling toward the bar: cap the navigator to a braking curve that reaches
            // zero just short of it (same form as the navigator's own hold-short braking), and
            // check again next tick.
            double stopDistNm = Math.Max(0.0, distFt - StartNodeHoldStopShortFt) / GeoMath.FeetPerNm;
            double decelRate = _nav.DecelRateKts ?? CategoryPerformance.TaxiDecelRate(ctx.Category);
            _nav.MaxSpeedKts = Math.Min(_nav.MaxSpeedKts, Math.Sqrt(2.0 * decelRate * stopDistNm * 3600.0));
            return false;
        }

        _startNodeHoldDone = true;
        TakeHoldShort(ctx, route, holdShort);
        return true;
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
