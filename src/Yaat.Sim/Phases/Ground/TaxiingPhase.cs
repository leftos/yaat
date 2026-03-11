using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft follows a TaxiRoute edge-by-edge at taxi speed.
/// Turns at nodes using ground turn rate.
/// Auto-stops at hold-short points (inserts HoldingShortPhase).
/// Completes when all segments have been traversed.
/// </summary>
public sealed class TaxiingPhase : Phase
{
    private const double NodeArrivalThresholdNm = 0.015;
    private const double OvershootDetectionNm = 0.03;
    private const double LogIntervalSeconds = 3.0;

    private int _targetNodeId;
    private double _targetLat;
    private double _targetLon;
    private double _nextSegmentBearing = double.NaN; // bearing from _targetNode toward the node after it
    private bool _initialized;
    private double _timeSinceLastLog;
    private double _prevDistToTarget = double.MaxValue;
    private double _pathDistToHoldShortNm; // path distance from _targetNodeId to next uncleared hold-short
    private bool _hasHoldShortAhead;

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
        SetupCurrentSegment(ctx);

        ctx.Logger.LogDebug(
            "[Taxi] {Callsign}: started, {SegCount} segments, first target node {NodeId} at ({Lat:F6}, {Lon:F6})",
            ctx.Aircraft.Callsign,
            route.Segments.Count,
            _targetNodeId,
            _targetLat,
            _targetLon
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
            SetupCurrentSegment(ctx);
        }

        // Check if held
        if (ctx.Aircraft.IsHeld)
        {
            AdjustSpeed(ctx, 0);
            return false;
        }

        // Navigate toward the current target node
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);

        bool overshot = dist > _prevDistToTarget && _prevDistToTarget < OvershootDetectionNm;
        _prevDistToTarget = dist;

        if (dist <= NodeArrivalThresholdNm || overshot)
        {
            if (overshot)
            {
                ctx.Logger.LogDebug(
                    "[Taxi] {Callsign}: overshoot detected at node {NodeId} (dist={Dist:F4}nm, prev={Prev:F4}nm)",
                    ctx.Aircraft.Callsign,
                    _targetNodeId,
                    dist,
                    _prevDistToTarget
                );
            }

            _prevDistToTarget = double.MaxValue;
            return ArriveAtNode(ctx, route);
        }

        // Turn toward target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
        TurnToward(ctx, bearing);

        // Speed scales with current heading error: straight = full, turning = crawl
        double angleDiff = Math.Abs(FlightPhysics.NormalizeAngle(bearing - ctx.Aircraft.Heading));
        double maxSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        double speedFraction = Math.Clamp(1.0 - (angleDiff / 120.0), 0.15, 1.0);
        double targetSpeed = maxSpeed * speedFraction;

        // Look-ahead braking: slow to the required arrival speed before reaching the target node.
        // Required arrival speed is 0 for hold-short nodes, or the corner speed for turn nodes.
        var nextHoldShort = route.GetHoldShortAt(_targetNodeId);
        bool isHoldShortNode = nextHoldShort is not null && !nextHoldShort.IsCleared;

        double requiredArrivalSpeed;
        if (isHoldShortNode)
        {
            requiredArrivalSpeed = 0;
        }
        else if (!double.IsNaN(_nextSegmentBearing))
        {
            // Scale corner speed by turn sharpness: no reduction below 30°, full reduction at 90°+
            double upcomingTurnAngle = Math.Abs(FlightPhysics.NormalizeAngle(_nextSegmentBearing - bearing));
            double cornerFraction = Math.Clamp((upcomingTurnAngle - 30.0) / 60.0, 0.0, 1.0);
            double cornerSpeed = CategoryPerformance.TaxiCornerSpeed(ctx.Category);
            requiredArrivalSpeed = maxSpeed - (maxSpeed - cornerSpeed) * cornerFraction;
        }
        else
        {
            requiredArrivalSpeed = maxSpeed; // last segment: no slowdown needed
        }

        // Physics-correct braking ramp: max speed at current distance to reach requiredArrivalSpeed.
        // Derived from v² = v_final² + 2·a·d  →  v_max = sqrt(v_final² + 2·a·d·3600)
        double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        double brakingLimit = Math.Sqrt(requiredArrivalSpeed * requiredArrivalSpeed + 2.0 * decelRate * dist * 3600.0);
        targetSpeed = Math.Min(targetSpeed, brakingLimit);

        // If there's a hold-short ahead in future segments, also brake for that.
        if (!isHoldShortNode && _hasHoldShortAhead)
        {
            double totalDist = dist + _pathDistToHoldShortNm;
            double holdShortBrakingLimit = Math.Sqrt(2.0 * decelRate * totalDist * 3600.0);
            targetSpeed = Math.Min(targetSpeed, holdShortBrakingLimit);
        }

        // Safety backstop: cap speed so we can't overshoot the target node in one tick.
        if (ctx.DeltaSeconds > 0 && dist > 0)
        {
            double maxSpeedForDist = dist * 0.8 / ctx.DeltaSeconds * 3600.0;
            targetSpeed = Math.Min(targetSpeed, maxSpeedForDist);
        }

        AdjustSpeed(ctx, targetSpeed);

        // Update current taxiway name
        var seg = route.CurrentSegment;
        if (seg is not null)
        {
            ctx.Aircraft.CurrentTaxiway = seg.TaxiwayName;
        }

        // Periodic logging
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: seg {SegIdx}/{SegCount} on {Taxiway}, target node {NodeId}, dist={Dist:F4}nm, gs={Gs:F1}kts, hdg={Hdg:F0}, bearing={Brg:F0}, pos=({Lat:F6},{Lon:F6})",
                ctx.Aircraft.Callsign,
                route.CurrentSegmentIndex,
                route.Segments.Count,
                seg?.TaxiwayName ?? "?",
                _targetNodeId,
                dist,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.Heading,
                bearing,
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude
            );
        }

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

    private void SetupCurrentSegment(PhaseContext ctx)
    {
        var route = ctx.Aircraft.AssignedTaxiRoute;
        if (route?.CurrentSegment is null)
        {
            ctx.Logger.LogWarning(
                "[Taxi] {Callsign}: SetupCurrentSegment — no current segment (index={Idx})",
                ctx.Aircraft.Callsign,
                route?.CurrentSegmentIndex ?? -1
            );
            return;
        }

        var seg = route.CurrentSegment;
        _targetNodeId = seg.ToNodeId;
        _nextSegmentBearing = double.NaN;

        if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var targetNode))
        {
            _targetLat = targetNode.Latitude;
            _targetLon = targetNode.Longitude;

            // Look one segment ahead to know the upcoming turn angle at this node.
            int nextIdx = route.CurrentSegmentIndex + 1;
            if (nextIdx < route.Segments.Count)
            {
                int nextToNodeId = route.Segments[nextIdx].ToNodeId;
                if (ctx.GroundLayout.Nodes.TryGetValue(nextToNodeId, out var nextNode))
                {
                    _nextSegmentBearing = GeoMath.BearingTo(targetNode.Latitude, targetNode.Longitude, nextNode.Latitude, nextNode.Longitude);
                }
            }

            // Walk remaining segments to find total path distance to next uncleared hold-short.
            _pathDistToHoldShortNm = 0;
            _hasHoldShortAhead = false;
            int prevId = _targetNodeId;
            for (int i = route.CurrentSegmentIndex + 1; i < route.Segments.Count; i++)
            {
                var futureSeg = route.Segments[i];
                if (
                    ctx.GroundLayout.Nodes.TryGetValue(prevId, out var fromFuture)
                    && ctx.GroundLayout.Nodes.TryGetValue(futureSeg.ToNodeId, out var toFuture)
                )
                {
                    _pathDistToHoldShortNm += GeoMath.DistanceNm(fromFuture.Latitude, fromFuture.Longitude, toFuture.Latitude, toFuture.Longitude);
                    prevId = futureSeg.ToNodeId;
                    if (route.GetHoldShortAt(futureSeg.ToNodeId) is { IsCleared: false })
                    {
                        _hasHoldShortAhead = true;
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
        }
        else
        {
            ctx.Logger.LogWarning(
                "[Taxi] {Callsign}: cannot resolve node {NodeId} — groundLayout {HasLayout}",
                ctx.Aircraft.Callsign,
                _targetNodeId,
                ctx.GroundLayout is not null ? "present but node missing" : "NULL"
            );
        }

        _initialized = true;
        _prevDistToTarget = double.MaxValue;
    }

    private bool ArriveAtNode(PhaseContext ctx, TaxiRoute route)
    {
        ctx.Logger.LogDebug(
            "[Taxi] {Callsign}: arrived at node {NodeId} (seg {SegIdx}/{SegCount})",
            ctx.Aircraft.Callsign,
            _targetNodeId,
            route.CurrentSegmentIndex,
            route.Segments.Count
        );

        // Update taxiway name from the segment that brought us here
        if (route.CurrentSegment is { } arrivedSeg)
        {
            ctx.Aircraft.CurrentTaxiway = arrivedSeg.TaxiwayName;
        }

        // Check if this node is a hold-short point
        var holdShort = route.GetHoldShortAt(_targetNodeId);
        if (holdShort is not null && !holdShort.IsCleared)
        {
            ctx.Logger.LogDebug(
                "[Taxi] {Callsign}: hold short at node {NodeId} (target {Target}, reason {Reason})",
                ctx.Aircraft.Callsign,
                _targetNodeId,
                holdShort.TargetName,
                holdShort.Reason
            );

            // Snap to exact hold-short node position to prevent runway encroachment.
            if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var hsNode))
            {
                ctx.Aircraft.Latitude = hsNode.Latitude;
                ctx.Aircraft.Longitude = hsNode.Longitude;
            }

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

            ApplyDepartureClearanceIfPending(ctx);

            // If no departure clearance was consumed, insert an idle phase so the
            // aircraft remains in a ground state that accepts subsequent commands.
            // ApplyDepartureClearanceIfPending inserts after current; check if anything follows.
            var phases = ctx.Aircraft.Phases;
            if (phases is not null && phases.Phases.Count <= phases.CurrentIndex + 1)
            {
                phases.InsertAfterCurrent(new HoldingInPositionPhase());
            }

            return true;
        }

        SetupCurrentSegment(ctx);
        return false;
    }

    /// <summary>
    /// Build the phases to insert after a HoldingShortPhase, depending on the hold-short reason.
    /// Advances CurrentSegmentIndex past the hold-short segment (and crossing segments for runway crossings).
    /// </summary>
    private static List<Phase> BuildResumePhases(PhaseContext ctx, TaxiRoute route, HoldShortPoint holdShort)
    {
        var phases = new List<Phase>();

        // Advance past the hold-short segment
        route.CurrentSegmentIndex++;

        if (holdShort.Reason == HoldShortReason.DestinationRunway)
        {
            // Destination runway: departure clearance takes over if present,
            // otherwise hold in position so the aircraft stays in a ground state.
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

                // Advance past crossing segments (runway edges) up to and including the exit node
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

        // If there are remaining segments, resume taxiing;
        // otherwise hold in position so the aircraft stays in a ground state.
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

    /// <summary>
    /// Scan remaining segments for the paired exit hold-short node (same RunwayId) on the far side of the crossing.
    /// Falls back to the next segment's ToNodeId if no explicit exit node is found.
    /// </summary>
    private static int? FindRunwayCrossingExitNode(TaxiRoute route, HoldShortPoint entryHoldShort, AirportGroundLayout? layout)
    {
        // Parse the entry runway identifier for matching
        var entryRwyId = entryHoldShort.TargetName is not null ? RunwayIdentifier.Parse(entryHoldShort.TargetName) : (RunwayIdentifier?)null;

        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];

            // Check the layout node directly — exit-side HS nodes are not in HoldShortPoints
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
                // Mark exit hold-short as cleared if it exists in the route's hold-short list
                if (route.GetHoldShortAt(seg.ToNodeId) is { } exitHs)
                {
                    exitHs.IsCleared = true;
                }

                return seg.ToNodeId;
            }
        }

        // Fallback: use the next segment's target node
        if (route.CurrentSegmentIndex < route.Segments.Count)
        {
            return route.Segments[route.CurrentSegmentIndex].ToNodeId;
        }

        return null;
    }

    private static void TurnToward(PhaseContext ctx, double targetBearing)
    {
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(ctx.Aircraft.Heading, targetBearing, maxTurn);
    }

    private static void AdjustSpeed(PhaseContext ctx, double targetSpeed)
    {
        // Respect ground conflict speed limit set by GroundConflictDetector.
        if (ctx.Aircraft.GroundSpeedLimit is { } limit)
        {
            targetSpeed = Math.Min(targetSpeed, limit);
        }

        double current = ctx.Aircraft.IndicatedAirspeed;
        if (current < targetSpeed)
        {
            double rate = CategoryPerformance.TaxiAccelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Min(targetSpeed, current + rate * ctx.DeltaSeconds);
        }
        else if (current > targetSpeed)
        {
            double rate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Max(targetSpeed, current - rate * ctx.DeltaSeconds);
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

        // Find the last hold-short node ID from the taxi route
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
                phases.TrafficDirection = ct.Direction;
                var patternRunway = dep.PatternRunway ?? rwy;
                var cat = AircraftCategorization.Categorize(ctx.Aircraft.AircraftType);
                var circuit = PatternBuilder.BuildCircuit(patternRunway, cat, ct.Direction, PatternEntryLeg.Upwind, true);
                phases.Phases.AddRange(circuit);
                phases.PatternRunway = patternRunway;
            }
        }

        phases.DepartureClearance = null;
        ctx.Logger.LogDebug("[Taxi] {Callsign}: departure clearance {Type} applied at route end", ctx.Aircraft.Callsign, dep.Type);
    }
}
