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
    private const double LogIntervalSeconds = 3.0;

    private GroundNavigator _nav = new();
    private bool _initialized;
    private double _timeSinceLastLog;

    public override string Name => "Taxiing";

    public override void OnStart(PhaseContext ctx)
    {
        var route = ctx.Aircraft.AssignedTaxiRoute;
        if (route is null || route.IsComplete)
        {
            ctx.Logger.LogWarning(
                "[Taxi] {Callsign}: OnStart but route is {State}",
                ctx.Aircraft.Callsign,
                route is null ? "null" : "already complete"
            );
            return;
        }

        ctx.Aircraft.IsOnGround = true;
        _nav.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
        _nav.CornerSpeedKts = CategoryPerformance.TaxiCornerSpeed(ctx.Category);
        SetupCurrentSegment(ctx, route);

        ctx.Logger.LogDebug(
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
        var route = ctx.Aircraft.AssignedTaxiRoute;
        if (route is null || route.IsComplete)
        {
            ctx.Logger.LogDebug("[Taxi] {Callsign}: OnTick exit — route {State}", ctx.Aircraft.Callsign, route is null ? "null" : "complete");
            return true;
        }

        if (!_initialized)
        {
            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: late init in OnTick (groundLayout {HasLayout})",
                ctx.Aircraft.Callsign,
                ctx.GroundLayout is not null ? "present" : "NULL"
            );
            _nav.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
            _nav.CornerSpeedKts = CategoryPerformance.TaxiCornerSpeed(ctx.Category);
            SetupCurrentSegment(ctx, route);
        }

        if (ctx.Aircraft.IsHeld)
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
            ctx.Aircraft.CurrentTaxiway = seg.TaxiwayName;
        }

        LogPeriodic(ctx, route);
        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Logger.LogDebug("[Taxi] {Callsign}: OnEnd ({Status})", ctx.Aircraft.Callsign, endStatus);

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
            _ => CommandAcceptance.Rejected,
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
            CurrentNodeRequiredSpeed = 0,
            SpeedConstraints = null,
            Navigator = _nav.ToSnapshot(),
        };

    public static TaxiingPhase FromSnapshot(TaxiingPhaseDto dto)
    {
        var phase = new TaxiingPhase();
        phase._initialized = dto.Initialized;
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
            // Legacy snapshot without navigator — reconstruct from old fields
            phase._nav = new GroundNavigator
            {
                TargetLat = dto.TargetLat,
                TargetLon = dto.TargetLon,
                PrevDistToTarget = dto.PrevDistToTarget,
                MaxSpeedKts = 30,
                CornerSpeedKts = 15,
            };
            phase._nav.SetTargetNodeId(dto.TargetNodeId);
        }

        return phase;
    }

    private void SetupCurrentSegment(PhaseContext ctx, TaxiRoute route)
    {
        if (route.CurrentSegment is null)
        {
            ctx.Logger.LogWarning(
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
        ctx.Logger.LogTrace(
            "[Taxi] {Callsign}: arrived at node {NodeId} (seg {SegIdx}/{SegCount})",
            ctx.Aircraft.Callsign,
            _nav.TargetNodeId,
            route.CurrentSegmentIndex,
            route.Segments.Count
        );

        // Update taxiway name from the segment that brought us here
        if (route.CurrentSegment is { } arrivedSeg)
        {
            ctx.Aircraft.CurrentTaxiway = arrivedSeg.TaxiwayName;
        }

        // Check if this node is a hold-short point
        var holdShort = route.GetHoldShortAt(_nav.TargetNodeId);
        if (holdShort is not null && !holdShort.IsCleared)
        {
            // Safety net: if another aircraft is already holding at this node, don't snap to it.
            if (ctx.IsHoldShortNodeOccupied?.Invoke(_nav.TargetNodeId) == true)
            {
                ctx.Aircraft.IndicatedAirspeed = 0;
                ctx.Targets.TargetSpeed = 0;
                ctx.Logger.LogDebug(
                    "[Taxi] {Callsign}: hold-short node {NodeId} occupied by another aircraft, waiting",
                    ctx.Aircraft.Callsign,
                    _nav.TargetNodeId
                );
                return false;
            }

            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: hold short at node {NodeId} (target {Target}, reason {Reason})",
                ctx.Aircraft.Callsign,
                _nav.TargetNodeId,
                holdShort.TargetName,
                holdShort.Reason
            );

            // Snap to exact hold-short position
            if (holdShort.Latitude is not null && holdShort.Longitude is not null)
            {
                ctx.Aircraft.Latitude = holdShort.Latitude.Value;
                ctx.Aircraft.Longitude = holdShort.Longitude.Value;
            }
            else if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_nav.TargetNodeId, out var hsNode))
            {
                ctx.Aircraft.Latitude = hsNode.Latitude;
                ctx.Aircraft.Longitude = hsNode.Longitude;
            }

            ctx.MarkHoldShortNodeOccupied?.Invoke(_nav.TargetNodeId);

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
            ctx.Logger.LogDebug("[Taxi] {Callsign}: route complete after {SegCount} segments", ctx.Aircraft.Callsign, route.Segments.Count);

            // Snap to the final destination node
            if (route.Segments.Count > 0 && ctx.GroundLayout is not null)
            {
                int finalNodeId = route.Segments[^1].ToNodeId;
                if (ctx.GroundLayout.Nodes.TryGetValue(finalNodeId, out var finalNode))
                {
                    ctx.Aircraft.Latitude = finalNode.Latitude;
                    ctx.Aircraft.Longitude = finalNode.Longitude;
                }
            }

            ApplyDepartureClearanceIfPending(ctx);

            var phases = ctx.Aircraft.Phases;
            if (phases is not null && phases.Phases.Count <= phases.CurrentIndex + 1)
            {
                var parkingName = route.DestinationParking ?? route.DestinationSpot;
                if (parkingName is not null)
                {
                    ctx.Aircraft.ParkingSpot = parkingName;
                    phases.InsertAfterCurrent(new AtParkingPhase());
                }
                else
                {
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

        if (holdShort.Reason == HoldShortReason.RunwayCrossing)
        {
            int? exitNodeId = FindRunwayCrossingExitNode(route, holdShort, ctx.GroundLayout);
            if (exitNodeId is not null)
            {
                phases.Add(new CrossingRunwayPhase(exitNodeId.Value));

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
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _nav.TargetLat, _nav.TargetLon);
            ctx.Logger.LogTrace(
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

    private static void ApplyDepartureClearanceIfPending(PhaseContext ctx)
    {
        var phases = ctx.Aircraft.Phases;
        var dep = phases?.DepartureClearance;
        if (dep is null || phases is null)
        {
            return;
        }

        int? holdShortNodeId = null;
        var route = ctx.Aircraft.AssignedTaxiRoute;
        if (route is not null)
        {
            foreach (var hs in route.HoldShortPoints)
            {
                if (hs.Reason is HoldShortReason.DestinationRunway or HoldShortReason.ExplicitHoldShort)
                {
                    holdShortNodeId = hs.NodeId;
                }
            }
        }

        var lineup = new LineUpPhase(holdShortNodeId);
        var luaw = new LinedUpAndWaitingPhase();
        bool isHeli = ctx.Category == AircraftCategory.Helicopter;
        Phase takeoffPhase = isHeli ? new HelicopterTakeoffPhase() : new TakeoffPhase();
        bool isClosedTraffic = dep.Departure is ClosedTrafficDeparture;
        if (isClosedTraffic)
        {
            phases.InsertAfterCurrent([lineup, luaw, takeoffPhase]);
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
                IsVfr = ctx.Aircraft.IsVfr,
                CruiseAltitude = ctx.Aircraft.CruiseAltitude,
            };
            phases.InsertAfterCurrent([lineup, luaw, takeoffPhase, climb]);
        }

        if (dep.Type == ClearanceType.ClearedForTakeoff)
        {
            luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);
            luaw.Departure = dep.Departure;
            luaw.AssignedAltitude = dep.AssignedAltitude;
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
        ctx.Logger.LogDebug("[Taxi] {Callsign}: departure clearance {Type} applied at route end", ctx.Aircraft.Callsign, dep.Type);
    }
}
