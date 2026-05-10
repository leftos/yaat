using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
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

    private GroundNavigator _nav = new();
    private bool _initialized;
    private double _timeSinceLastLog;

    public override string Name => "Taxiing";

    internal double NavMaxSpeedKts => _nav.MaxSpeedKts;

    public override void OnStart(PhaseContext ctx)
    {
        var route = ctx.Aircraft.Ground.AssignedTaxiRoute;
        if (route is null || route.IsComplete)
        {
            Log.LogWarning("[Taxi] {Callsign}: OnStart but route is {State}", ctx.Aircraft.Callsign, route is null ? "null" : "already complete");
            return;
        }

        ctx.Aircraft.IsOnGround = true;
        _nav.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
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
            _nav.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
            SetupCurrentSegment(ctx, route);
        }

        double baseTaxiSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        _nav.MaxSpeedKts = ctx.Aircraft.Ground.IsExpeditingTaxi ? baseTaxiSpeed * CategoryPerformance.TaxiExpediteMultiplier : baseTaxiSpeed;

        if (ctx.Aircraft.Ground.IsHeld)
        {
            ctx.Aircraft.IndicatedAirspeed = Math.Max(
                0,
                ctx.Aircraft.IndicatedAirspeed - CategoryPerformance.TaxiDecelRate(ctx.Category) * ctx.DeltaSeconds
            );
            return false;
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

        if (endStatus == PhaseStatus.Completed)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.CrossRunway => CommandAcceptance.Allowed,
            CanonicalCommandType.HoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is taxiing; only HOLD/RES, CROSS, or HS apply, or issue a new TAXI"),
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
            // Legacy snapshot without navigator — reconstruct from old fields.
            phase._nav = new GroundNavigator
            {
                TargetLat = dto.TargetLat,
                TargetLon = dto.TargetLon,
                PrevDistToTarget = dto.PrevDistToTarget,
                MaxSpeedKts = 30,
            };
            phase._nav.SetTargetNodeId(dto.TargetNodeId);
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
            _nav.TargetLat = hs.Latitude.Value;
            _nav.TargetLon = hs.Longitude.Value;
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
            var resumePhases = BuildResumePhases(ctx, route, holdShort);

            var insertList = new List<Phase> { holdPhase };
            insertList.AddRange(resumePhases);
            ctx.Aircraft.Phases?.InsertAfterCurrent(insertList);
            return true;
        }

        // Advance to next segment
        route.CurrentSegmentIndex++;

        if (route.IsComplete)
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
                    // DestinationSpot is an intermediate waypoint — the aircraft is awaiting
                    // further instructions, not parked. HoldingInPositionPhase is the catch-all
                    // idle state that accepts Taxi/Pushback/FollowGround/LineUpAndWait/etc.
                    phases.InsertAfterCurrent(new HoldingInPositionPhase());
                }
            }

            return true;
        }

        SetupCurrentSegment(ctx, route);
        return false;
    }

    private static List<Phase> BuildResumePhases(PhaseContext ctx, TaxiRoute route, HoldShortPoint holdShort)
    {
        var phases = new List<Phase>();
        route.CurrentSegmentIndex++;

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

        // ExplicitHoldShort upgraded from a RunwayCrossing (HoldShortAnnotator promotes
        // the entry-side runway HS when "HS <rwy>" is in the command) still needs the
        // runway-crossing flow on resume — otherwise the aircraft taxis across at 15 kt
        // without a CrossingRunwayPhase and the route's exit-side HS isn't skipped.
        bool isRunwayHs =
            ctx.GroundLayout is not null
            && ctx.GroundLayout.Nodes.TryGetValue(holdShort.NodeId, out var hsNode)
            && hsNode.Type == GroundNodeType.RunwayHoldShort;
        bool needsRunwayCrossing =
            holdShort.Reason == HoldShortReason.RunwayCrossing || (holdShort.Reason == HoldShortReason.ExplicitHoldShort && isRunwayHs);

        if (needsRunwayCrossing)
        {
            int? exitNodeId = FindRunwayCrossingExitNode(route, holdShort, ctx.GroundLayout);
            if (exitNodeId is not null)
            {
                phases.Add(new CrossingRunwayPhase(holdShort.NodeId, exitNodeId.Value));

                while (!route.IsComplete)
                {
                    var seg = route.CurrentSegment;
                    if (seg is null)
                    {
                        break;
                    }

                    route.CurrentSegmentIndex++;
                    if (seg.ToNodeId == exitNodeId.Value)
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
        else
        {
            phases.Add(new HoldingInPositionPhase());
        }

        return phases;
    }

    private static int? FindRunwayCrossingExitNode(TaxiRoute route, HoldShortPoint entryHoldShort, AirportGroundLayout? layout)
    {
        var entryRwyId = entryHoldShort.TargetName is not null ? RunwayIdentifier.Parse(entryHoldShort.TargetName) : (RunwayIdentifier?)null;

        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];

            if (
                layout is not null
                && layout.Nodes.TryGetValue(seg.ToNodeId, out var node)
                && node.Type == GroundNodeType.RunwayHoldShort
                && node.RunwayId is { } nodeRwyId
                && seg.ToNodeId != entryHoldShort.NodeId
                && entryRwyId is not null
                && nodeRwyId.Equals(entryRwyId.Value)
            )
            {
                if (route.GetHoldShortAt(seg.ToNodeId) is { } exitHs)
                {
                    exitHs.IsCleared = true;
                }

                return seg.ToNodeId;
            }
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
        bool isClosedTraffic = dep.Departure is ClosedTrafficDeparture;
        LinedUpAndWaitingPhase? luawPhase = rolling ? null : new LinedUpAndWaitingPhase();

        if (isClosedTraffic)
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
                DepartureSidId = dep.DepartureSidId,
                SidDepartureHeadingMagnetic = dep.SidDepartureHeadingMagnetic,
                IsVfr = ctx.Aircraft.FlightPlan.IsVfr,
                CruiseAltitude = ctx.Aircraft.FlightPlan.CruiseAltitude,
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

            if (dep.Departure is ClosedTrafficDeparture ct && phases.AssignedRunway is { } rwy)
            {
                Commands.DepartureClearanceHandler.ApplyClosedTraffic(ct, ctx.Aircraft, phases, dep.PatternRunway ?? rwy, removeInitialClimb: false);
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
