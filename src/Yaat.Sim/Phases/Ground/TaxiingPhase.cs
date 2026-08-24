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

    // Set when this phase completes to hand off to a still-moving CrossingRunwayPhase
    // (pre-cleared crossing), so OnEnd does not brake the aircraft to a stop. Transient —
    // set and consumed within the same completing tick, never snapshotted.
    private bool _completingIntoMovingCrossing;

    public override string Name => "Taxiing";

    internal double NavMaxSpeedKts => _nav.MaxSpeedKts;

    public override void OnStart(PhaseContext ctx)
    {
        var route = ctx.Aircraft.Ground.AssignedTaxiRoute;
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
        var route = ctx.Aircraft.Ground.AssignedTaxiRoute;
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
            SetupCurrentSegment(ctx, route);
        }

        // A controller-commanded taxi speed replaces the category default outright (slower or
        // faster); it is mutually exclusive with expedite, so at most one branch is in effect.
        // Corner/arc/braking/conflict caps still win downstream via GroundNavigator's Math.Min chain.
        double baseTaxiSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        _nav.MaxSpeedKts =
            ctx.Aircraft.Ground.CommandedTaxiSpeedKts
            ?? (ctx.Aircraft.Ground.IsExpeditingTaxi ? baseTaxiSpeed * CategoryPerformance.TaxiExpediteMultiplier : baseTaxiSpeed);

        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Aircraft.IndicatedAirspeed = Math.Max(
                0,
                ctx.Aircraft.IndicatedAirspeed - CategoryPerformance.TaxiDecelRate(ctx.Category) * ctx.DeltaSeconds
            );
            return false;
        }

        // A hold-short on the route's own start node: the aircraft was re-routed at or while
        // approaching the bar, so it must not enter the crossing until cleared. ArriveAtNode never
        // fires for that node — it is no segment's ToNodeId — so the stop has to be taken here,
        // before the first segment, re-checked each tick until the hold binds or stops applying.
        if (!_startNodeHoldDone && TryHoldAtRouteStartNode(ctx, route))
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
        if (TryStopNoseAtSpot(ctx, route))
        {
            return true;
        }

        bool isLastSegment = route.CurrentSegmentIndex + 1 >= route.Segments.Count;
        var result = _nav.Tick(ctx, isLastSegment, nodeId => IsHoldShortCleared(route, nodeId));

        if (result == NavigatorResult.ArrivedAtNode)
        {
            return ArriveAtNode(ctx, route);
        }

        // Update current taxiway name
        if (route.CurrentSegment is { } seg)
        {
            var prev = ctx.Aircraft.Ground.CurrentTaxiway;
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

        // Completing into a moving runway crossing: keep rolling — the CrossingRunwayPhase
        // owns the speed and must not re-accelerate from a dead stop on the runway approach.
        if (endStatus == PhaseStatus.Completed && !_completingIntoMovingCrossing)
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
        };

    public static TaxiingPhase FromSnapshot(TaxiingPhaseDto dto)
    {
        var phase = new TaxiingPhase();

        // GroundNavigator's snapshot does not carry the active PathPrimitive
        // (or its arc/synthesis derived state). Force a re-init on the next
        // OnTick: SetupCurrentSegment will rebuild the primitive and speed
        // constraints from route.CurrentSegmentIndex. Without this, the next
        // Tick would see _currentPrimitive=null, return ArrivedAtNode, and
        // skip the segment the aircraft was traversing.
        phase._initialized = false;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
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

        // Override target position with hold-short offset if applicable
        var hs = route.GetHoldShortAt(_nav.TargetNodeId);
        if (hs is not null && !hs.IsCleared && hs.Latitude is not null && hs.Longitude is not null)
        {
            _nav.OverrideTargetPosition(hs.Latitude.Value, hs.Longitude.Value);
        }

        _initialized = true;
    }

    private static bool IsHoldShortCleared(TaxiRoute route, int nodeId)
    {
        var hs = route.GetHoldShortAt(nodeId);
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
        var holdShort = route.GetHoldShortAt(_nav.TargetNodeId);
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
            var resumePhases = BuildResumePhases(ctx, route, holdShort, advancePastCurrentSegment: true);

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
    /// (<see cref="AtParkingPhase"/> for a gate, otherwise <see cref="HoldingInPositionPhase"/> — a spot
    /// is an intermediate waypoint where the aircraft awaits further instructions, not a parked gate).
    /// Shared by normal last-segment arrival and the nose-at-spot terminal stop.
    /// </summary>
    private static bool CompleteRoute(PhaseContext ctx, TaxiRoute route)
    {
        Log.LogDebug("[Taxi] {Callsign}: route complete after {SegCount} segments", ctx.Aircraft.Callsign, route.Segments.Count);

        ApplyDepartureClearanceIfPending(ctx);

        var phases = ctx.Aircraft.Phases;
        if (phases is not null && phases.Phases.Count <= phases.CurrentIndex + 1)
        {
            if (route.DestinationParking is not null)
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
            || !layout.Nodes.TryGetValue(route.Segments[^1].ToNodeId, out var spotNode)
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
            Log.LogDebug(
                "[Taxi] {Callsign}: nose-at-spot stop at {Spot} — centroid {Dist:F0}ft from spot node (half-length {Half:F0}ft, hdg {Hdg:F0})",
                ctx.Aircraft.Callsign,
                route.DestinationSpot,
                distToSpotNm * GeoMath.FeetPerNm,
                lengthFt / 2.0,
                ctx.Aircraft.TrueHeading.Degrees
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
            ctx.Aircraft.IndicatedAirspeed = Math.Max(
                0,
                ctx.Aircraft.IndicatedAirspeed - CategoryPerformance.TaxiDecelRate(ctx.Category) * ctx.DeltaSeconds
            );
            return false;
        }

        var holdShort = route.HoldShortPoints[0];
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
        var holdShort = route.GetHoldShortAt(startNodeId);
        if (holdShort is null || holdShort.IsCleared)
        {
            _startNodeHoldDone = true;
            return false;
        }

        if (!ctx.GroundLayout.Nodes.TryGetValue(startNodeId, out var startNode))
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
            var phaseList = ctx.Aircraft.Phases;
            if (phaseList is not null && phaseList.Phases.Count <= phaseList.CurrentIndex + 1)
            {
                phases.Add(new HoldingInPositionPhase());
            }
            return phases;
        }

        int? crossingExitNodeId = null;
        if (NeedsRunwayCrossing(holdShort, ctx.GroundLayout))
        {
            crossingExitNodeId = FindRunwayCrossingExitNode(route, holdShort, ctx.GroundLayout, requireSameRunwayExit: false);
            if (crossingExitNodeId is { } exitNodeId)
            {
                phases.Add(new CrossingRunwayPhase(holdShort.NodeId, exitNodeId, holdShort.TargetName));

                while (!route.IsComplete)
                {
                    var seg = route.CurrentSegment;
                    if (seg is null)
                    {
                        break;
                    }

                    route.CurrentSegmentIndex++;
                    if (seg.ToNodeId == exitNodeId)
                    {
                        break;
                    }
                }
            }
        }

        if (!route.IsComplete)
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
        var phaseList = ctx.Aircraft.Phases;
        if (phaseList is not null && phaseList.Phases.Count > phaseList.CurrentIndex + 1)
        {
            return [];
        }

        if (route.DestinationParking is not null)
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
    /// if the route ends at the far side). Advances <see cref="TaxiRoute.CurrentSegmentIndex"/> past the crossing
    /// slice — identical to the resume flow in <see cref="BuildResumePhases"/>, but
    /// entered straight from a moving <see cref="TaxiingPhase"/>.
    /// </summary>
    private static List<Phase> BuildPreClearedCrossingPhases(PhaseContext ctx, TaxiRoute route, HoldShortPoint holdShort, int exitNodeId)
    {
        var phases = new List<Phase> { new CrossingRunwayPhase(holdShort.NodeId, exitNodeId, holdShort.TargetName) };

        // Skip past the arrived-at hold-short segment, then consume the crossing slice
        // up to and including the segment that reaches the far-side hold-short.
        route.CurrentSegmentIndex++;
        while (!route.IsComplete)
        {
            var seg = route.CurrentSegment;
            if (seg is null)
            {
                break;
            }

            route.CurrentSegmentIndex++;
            if (seg.ToNodeId == exitNodeId)
            {
                break;
            }
        }

        if (!route.IsComplete)
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
            && layout.Nodes.TryGetValue(holdShort.NodeId, out var node)
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
                && layout.Nodes.TryGetValue(nodeId, out var node)
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
            var seg = route.CurrentSegment;
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
        var phases = ctx.Aircraft.Phases;
        var dep = phases?.DepartureClearance;
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
