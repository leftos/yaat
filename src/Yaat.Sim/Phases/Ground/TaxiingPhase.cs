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
/// </summary>
public sealed class TaxiingPhase : Phase
{
    private const double NodeArrivalThresholdNm = 0.015;
    private const double OvershootDetectionNm = 0.03;
    private const double LogIntervalSeconds = 3.0;

    private int _targetNodeId;
    private double _targetLat;
    private double _targetLon;
    private bool _initialized;
    private double _timeSinceLastLog;
    private double _prevDistToTarget = double.MaxValue;
    private double _currentNodeRequiredSpeed;
    private List<(double PathDistNm, double RequiredSpeedKts, int NodeId)> _speedConstraints = [];

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
        // When the braking curve decelerates to zero right at the arrival threshold,
        // floating-point precision can leave dist slightly above NodeArrivalThresholdNm.
        // Detect this stall and force arrival to prevent the aircraft from getting stuck.
        // But don't trigger when the aircraft was stopped by GroundConflictDetector — that
        // stop is intentional (e.g., traffic ahead at a hold-short node).
        bool stoppedByConflict = (ctx.Aircraft.GroundSpeedLimit is not null) && (ctx.Aircraft.GroundSpeedLimit.Value < 0.5);
        bool stalledAtThreshold = !stoppedByConflict && ctx.Aircraft.GroundSpeed < 0.5 && dist < NodeArrivalThresholdNm + 0.001;
        _prevDistToTarget = dist;

        if (dist <= NodeArrivalThresholdNm || overshot || stalledAtThreshold)
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
        double angleDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
        double maxSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        double speedFraction = Math.Clamp(1.0 - (angleDiff / 120.0), 0.15, 1.0);
        double targetSpeed = maxSpeed * speedFraction;

        // Multi-segment braking: use precomputed speed profile from SetupCurrentSegment.
        // Effective distance accounts for arrival threshold snap (aircraft "arrives" at NodeArrivalThresholdNm).
        double effectiveDist = Math.Max(0, dist - NodeArrivalThresholdNm);
        double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);

        // Brake for current target node
        double brakingLimit = Math.Sqrt(_currentNodeRequiredSpeed * _currentNodeRequiredSpeed + 2.0 * decelRate * effectiveDist * 3600.0);
        targetSpeed = Math.Min(targetSpeed, brakingLimit);

        // Brake for all future constraints
        foreach (var (pathDist, reqSpeed, nodeId) in _speedConstraints)
        {
            if (reqSpeed == 0 && route.GetHoldShortAt(nodeId) is null or { IsCleared: true })
            {
                continue;
            }

            double totalDist = effectiveDist + pathDist;
            double limit = Math.Sqrt(reqSpeed * reqSpeed + 2.0 * decelRate * totalDist * 3600.0);
            brakingLimit = Math.Min(brakingLimit, limit);
        }
        targetSpeed = Math.Min(targetSpeed, brakingLimit);

        // Safety backstop: cap speed so we can't overshoot the target node in one tick.
        if (ctx.DeltaSeconds > 0 && effectiveDist > 0)
        {
            double maxSpeedForDist = effectiveDist * 0.8 / ctx.DeltaSeconds * 3600.0;
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
            ctx.Logger.LogTrace(
                "[Taxi] {Callsign}: seg {SegIdx}/{SegCount} on {Taxiway}, target node {NodeId}, dist={Dist:F4}nm, gs={Gs:F1}kts, hdg={Hdg:F0}, bearing={Brg:F0}, pos=({Lat:F6},{Lon:F6})",
                ctx.Aircraft.Callsign,
                route.CurrentSegmentIndex,
                route.Segments.Count,
                seg?.TaxiwayName ?? "?",
                _targetNodeId,
                dist,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.TrueHeading.Degrees,
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

    public override PhaseDto ToSnapshot()
    {
        List<SpeedConstraintDto>? constraints = null;
        if (_speedConstraints.Count > 0)
        {
            constraints = new List<SpeedConstraintDto>(_speedConstraints.Count);
            foreach (var (pathDist, reqSpeed, nodeId) in _speedConstraints)
            {
                constraints.Add(
                    new SpeedConstraintDto
                    {
                        PathDistNm = pathDist,
                        RequiredSpeedKts = reqSpeed,
                        NodeId = nodeId,
                    }
                );
            }
        }

        return new TaxiingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetNodeId = _targetNodeId,
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            Initialized = _initialized,
            TimeSinceLastLog = _timeSinceLastLog,
            PrevDistToTarget = _prevDistToTarget,
            CurrentNodeRequiredSpeed = _currentNodeRequiredSpeed,
            SpeedConstraints = constraints,
        };
    }

    public static TaxiingPhase FromSnapshot(TaxiingPhaseDto dto)
    {
        var phase = new TaxiingPhase();
        phase._targetNodeId = dto.TargetNodeId;
        phase._targetLat = dto.TargetLat;
        phase._targetLon = dto.TargetLon;
        phase._initialized = dto.Initialized;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase._prevDistToTarget = dto.PrevDistToTarget;
        phase._currentNodeRequiredSpeed = dto.CurrentNodeRequiredSpeed;

        if (dto.SpeedConstraints is not null)
        {
            phase._speedConstraints = new List<(double PathDistNm, double RequiredSpeedKts, int NodeId)>(dto.SpeedConstraints.Count);
            foreach (var sc in dto.SpeedConstraints)
            {
                phase._speedConstraints.Add((sc.PathDistNm, sc.RequiredSpeedKts, sc.NodeId));
            }
        }

        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
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

        if (ctx.GroundLayout is not null && ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var targetNode))
        {
            _targetLat = targetNode.Latitude;
            _targetLon = targetNode.Longitude;

            double maxSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
            double cornerSpeed = CategoryPerformance.TaxiCornerSpeed(ctx.Category);

            // A. Compute _currentNodeRequiredSpeed for the immediate target node
            var nextHoldShort = route.GetHoldShortAt(_targetNodeId);
            if (nextHoldShort is not null && !nextHoldShort.IsCleared)
            {
                _currentNodeRequiredSpeed = 0;
            }
            else
            {
                int nextIdx = route.CurrentSegmentIndex + 1;
                if (nextIdx < route.Segments.Count)
                {
                    int nextToNodeId = route.Segments[nextIdx].ToNodeId;
                    if (ctx.GroundLayout.Nodes.TryGetValue(nextToNodeId, out var nextNode))
                    {
                        double segBearing = GeoMath.BearingTo(targetNode.Latitude, targetNode.Longitude, nextNode.Latitude, nextNode.Longitude);
                        double inboundBearing = GeoMath.BearingTo(
                            ctx.Aircraft.Latitude,
                            ctx.Aircraft.Longitude,
                            targetNode.Latitude,
                            targetNode.Longitude
                        );
                        double turnAngle = GeoMath.AbsBearingDifference(inboundBearing, segBearing);
                        double frac = Math.Clamp((turnAngle - 30.0) / 60.0, 0.0, 1.0);
                        _currentNodeRequiredSpeed = maxSpeed - (maxSpeed - cornerSpeed) * frac;
                    }
                    else
                    {
                        _currentNodeRequiredSpeed = maxSpeed;
                    }
                }
                else
                {
                    // Last segment — route ends at this node, stop
                    _currentNodeRequiredSpeed = 0;
                }
            }

            // B. Forward walk: collect speed constraints at future nodes
            _speedConstraints = [];
            double cumulativeDistNm = 0;
            int prevNodeId = _targetNodeId;
            for (int i = route.CurrentSegmentIndex + 1; i < route.Segments.Count; i++)
            {
                var futureSeg = route.Segments[i];
                if (
                    !ctx.GroundLayout.Nodes.TryGetValue(prevNodeId, out var fromNode)
                    || !ctx.GroundLayout.Nodes.TryGetValue(futureSeg.ToNodeId, out var toNode)
                )
                {
                    break;
                }

                cumulativeDistNm += GeoMath.DistanceNm(fromNode.Latitude, fromNode.Longitude, toNode.Latitude, toNode.Longitude);
                prevNodeId = futureSeg.ToNodeId;

                // Determine required speed at this node
                double reqSpeed;
                var hs = route.GetHoldShortAt(futureSeg.ToNodeId);
                if (hs is not null && !hs.IsCleared)
                {
                    reqSpeed = 0;
                    _speedConstraints.Add((cumulativeDistNm, reqSpeed, futureSeg.ToNodeId));
                    break; // No need to look past an uncleared hold-short
                }

                // Check turn angle to the next-next segment
                int nextNextIdx = i + 1;
                if (nextNextIdx < route.Segments.Count)
                {
                    int nextNextNodeId = route.Segments[nextNextIdx].ToNodeId;
                    if (ctx.GroundLayout.Nodes.TryGetValue(nextNextNodeId, out var nextNextNode))
                    {
                        double inBearing = GeoMath.BearingTo(fromNode.Latitude, fromNode.Longitude, toNode.Latitude, toNode.Longitude);
                        double outBearing = GeoMath.BearingTo(toNode.Latitude, toNode.Longitude, nextNextNode.Latitude, nextNextNode.Longitude);
                        double turnAngle = GeoMath.AbsBearingDifference(inBearing, outBearing);
                        double frac = Math.Clamp((turnAngle - 30.0) / 60.0, 0.0, 1.0);
                        reqSpeed = maxSpeed - (maxSpeed - cornerSpeed) * frac;
                    }
                    else
                    {
                        reqSpeed = maxSpeed;
                    }
                }
                else
                {
                    // Last segment — route ends, stop
                    reqSpeed = 0;
                }

                if (reqSpeed < maxSpeed)
                {
                    _speedConstraints.Add((cumulativeDistNm, reqSpeed, futureSeg.ToNodeId));
                }
            }

            // C. Backward pass: back-propagate constraints using v = sqrt(v_next² + 2·a·d·3600)
            double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            for (int i = _speedConstraints.Count - 2; i >= 0; i--)
            {
                var (dist, speed, nodeId) = _speedConstraints[i];
                var (nextDist, nextSpeed, _) = _speedConstraints[i + 1];
                double legDist = nextDist - dist;
                double backPropSpeed = Math.Sqrt(nextSpeed * nextSpeed + 2.0 * decelRate * legDist * 3600.0);
                if (backPropSpeed < speed)
                {
                    _speedConstraints[i] = (dist, backPropSpeed, nodeId);
                }
            }

            // D. Back-propagate first constraint into _currentNodeRequiredSpeed
            if (_speedConstraints.Count > 0)
            {
                var (firstDist, firstSpeed, _) = _speedConstraints[0];
                double backProp = Math.Sqrt(firstSpeed * firstSpeed + 2.0 * decelRate * firstDist * 3600.0);
                if (backProp < _currentNodeRequiredSpeed)
                {
                    _currentNodeRequiredSpeed = backProp;
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
            // Safety net: if another aircraft is already holding at this node, don't snap to it.
            // Stop at the current position and let GroundConflictDetector manage separation.
            if (ctx.IsHoldShortNodeOccupied?.Invoke(_targetNodeId) == true)
            {
                ctx.Aircraft.IndicatedAirspeed = 0;
                ctx.Targets.TargetSpeed = 0;
                ctx.Logger.LogDebug(
                    "[Taxi] {Callsign}: hold-short node {NodeId} occupied by another aircraft, waiting",
                    ctx.Aircraft.Callsign,
                    _targetNodeId
                );
                return false;
            }

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
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, targetBearing, maxTurn);
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
