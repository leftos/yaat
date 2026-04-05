using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

public enum NavigatorResult
{
    Navigating,
    ArrivedAtNode,
}

/// <summary>
/// Core ground navigation: steers toward a target node, manages speed profiling
/// with angle-based scaling and multi-segment kinematic braking, and detects
/// arrival/overshoot. Used by both TaxiingPhase and RunwayExitPhase.
///
/// The owning phase configures MaxSpeedKts/CornerSpeedKts and handles what
/// happens on arrival (hold-short insertion, route completion, etc.).
/// </summary>
public sealed class GroundNavigator
{
    private const double NodeArrivalThresholdNm = 0.015;
    private const double FinalNodeArrivalThresholdNm = 0.0003;
    private const double OvershootDetectionNm = 0.03;

    public int TargetNodeId { get; private set; }
    public double TargetLat { get; set; }
    public double TargetLon { get; set; }
    public double PrevDistToTarget { get; set; } = double.MaxValue;

    public void SetTargetNodeId(int nodeId) => TargetNodeId = nodeId;

    public double MaxSpeedKts { get; set; }
    public double CornerSpeedKts { get; set; }

    private double _currentNodeRequiredSpeed;
    private double? _nextSegmentBearing;
    private List<(double PathDistNm, double RequiredSpeedKts, int NodeId)> _speedConstraints = [];

    /// <summary>
    /// Set up navigation for the current segment of a route. Computes speed
    /// constraints by walking future segments and back-propagating braking limits.
    /// </summary>
    /// <param name="route">The taxi route being followed.</param>
    /// <param name="ctx">Phase context (for ground layout, category).</param>
    /// <param name="isHoldShortCleared">Returns true if a hold-short at the given node ID
    /// is cleared or absent. TaxiingPhase checks the route; RunwayExitPhase returns true for all.</param>
    public void SetupSegment(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        var seg = route.CurrentSegment;
        if (seg is null || ctx.GroundLayout is null || !ctx.GroundLayout.Nodes.TryGetValue(seg.ToNodeId, out var targetNode))
        {
            return;
        }

        TargetNodeId = seg.ToNodeId;
        TargetLat = targetNode.Latitude;
        TargetLon = targetNode.Longitude;
        PrevDistToTarget = double.MaxValue;

        // A. Compute required speed at the immediate target node
        bool isLastSegment = route.CurrentSegmentIndex + 1 >= route.Segments.Count;

        if (!isHoldShortCleared(TargetNodeId))
        {
            _currentNodeRequiredSpeed = 0;
            _nextSegmentBearing = null;
        }
        else if (!isLastSegment)
        {
            int nextIdx = route.CurrentSegmentIndex + 1;
            int nextToNodeId = route.Segments[nextIdx].ToNodeId;
            if (ctx.GroundLayout.Nodes.TryGetValue(nextToNodeId, out var nextNode))
            {
                double segBearing = GeoMath.BearingTo(targetNode.Latitude, targetNode.Longitude, nextNode.Latitude, nextNode.Longitude);
                double inboundBearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, targetNode.Latitude, targetNode.Longitude);
                double turnAngle = GeoMath.AbsBearingDifference(inboundBearing, segBearing);
                _currentNodeRequiredSpeed = CategoryPerformance.CornerSpeedForAngle(ctx.Category, turnAngle);
                _nextSegmentBearing = segBearing;
            }
            else
            {
                _currentNodeRequiredSpeed = MaxSpeedKts;
                _nextSegmentBearing = null;
            }
        }
        else
        {
            _currentNodeRequiredSpeed = 0;
            _nextSegmentBearing = null;
        }

        // B. Forward walk: collect speed constraints at future nodes
        _speedConstraints = [];
        double cumulativeDistNm = 0;
        int prevNodeId = TargetNodeId;
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

            double reqSpeed;
            if (!isHoldShortCleared(futureSeg.ToNodeId))
            {
                reqSpeed = 0;
                _speedConstraints.Add((cumulativeDistNm, reqSpeed, futureSeg.ToNodeId));
                break;
            }

            int nextNextIdx = i + 1;
            if (nextNextIdx < route.Segments.Count)
            {
                int nextNextNodeId = route.Segments[nextNextIdx].ToNodeId;
                if (ctx.GroundLayout.Nodes.TryGetValue(nextNextNodeId, out var nextNextNode))
                {
                    double inBearing = GeoMath.BearingTo(fromNode.Latitude, fromNode.Longitude, toNode.Latitude, toNode.Longitude);
                    double outBearing = GeoMath.BearingTo(toNode.Latitude, toNode.Longitude, nextNextNode.Latitude, nextNextNode.Longitude);
                    double turnAngle = GeoMath.AbsBearingDifference(inBearing, outBearing);
                    reqSpeed = CategoryPerformance.CornerSpeedForAngle(ctx.Category, turnAngle);
                }
                else
                {
                    reqSpeed = MaxSpeedKts;
                }
            }
            else
            {
                reqSpeed = 0;
            }

            if (reqSpeed < MaxSpeedKts)
            {
                _speedConstraints.Add((cumulativeDistNm, reqSpeed, futureSeg.ToNodeId));
            }
        }

        // C. Backward pass: back-propagate constraints
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

        // D. Back-propagate first constraint into current node required speed
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

    /// <summary>
    /// Advance one tick: steer toward target, compute speed, detect arrival.
    /// </summary>
    /// <param name="ctx">Phase context.</param>
    /// <param name="isLastSegment">True if this is the last segment (tighter arrival threshold).</param>
    /// <param name="isHoldShortCleared">Hold-short cleared check (for future constraint skipping).</param>
    public NavigatorResult Tick(PhaseContext ctx, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);

        double arrivalThreshold = isLastSegment ? FinalNodeArrivalThresholdNm : NodeArrivalThresholdNm;

        // Turn anticipation: declare early arrival when approaching a turn node so the
        // aircraft starts steering toward the next segment sooner, creating a smooth arc
        // (like real taxiway fillet markings). Skip for hold-short nodes and last segment.
        if (!isLastSegment && (_nextSegmentBearing is not null) && (_currentNodeRequiredSpeed > 0.5))
        {
            double inboundBearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);
            double turnAngle = GeoMath.AbsBearingDifference(inboundBearing, _nextSegmentBearing.Value);
            if (turnAngle > 20)
            {
                double turnRateRad = CategoryPerformance.GroundTurnRate(ctx.Category) * Math.PI / 180.0;
                double speedNmSec = Math.Max(ctx.Aircraft.GroundSpeed, _currentNodeRequiredSpeed) / 3600.0;
                double radiusNm = speedNmSec / turnRateRad;
                double halfAngleRad = turnAngle * Math.PI / 360.0;
                double anticipation = radiusNm * Math.Tan(halfAngleRad);
                arrivalThreshold = Math.Max(arrivalThreshold, Math.Min(anticipation, 0.05));
            }
        }

        bool overshot = (dist > PrevDistToTarget) && (PrevDistToTarget < OvershootDetectionNm);
        bool stoppedByConflict = (ctx.Aircraft.GroundSpeedLimit is not null) && (ctx.Aircraft.GroundSpeedLimit.Value < 0.5);
        bool stalledAtThreshold = !stoppedByConflict && (ctx.Aircraft.GroundSpeed < 0.5) && (dist < arrivalThreshold + 0.001);
        PrevDistToTarget = dist;

        if (dist <= arrivalThreshold || overshot || stalledAtThreshold)
        {
            PrevDistToTarget = double.MaxValue;
            return NavigatorResult.ArrivedAtNode;
        }

        // Steer toward target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);
        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        // Speed: scale by heading error
        double angleDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
        double speedFraction = Math.Clamp(1.0 - (angleDiff / 120.0), 0.15, 1.0);
        double targetSpeed = MaxSpeedKts * speedFraction;

        // Multi-segment braking
        double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        double brakingLimit = Math.Sqrt(_currentNodeRequiredSpeed * _currentNodeRequiredSpeed + 2.0 * decelRate * dist * 3600.0);
        targetSpeed = Math.Min(targetSpeed, brakingLimit);

        foreach (var (pathDist, reqSpeed, nodeId) in _speedConstraints)
        {
            if ((reqSpeed == 0) && isHoldShortCleared(nodeId))
            {
                continue;
            }

            double totalDist = dist + pathDist;
            double limit = Math.Sqrt(reqSpeed * reqSpeed + 2.0 * decelRate * totalDist * 3600.0);
            brakingLimit = Math.Min(brakingLimit, limit);
        }
        targetSpeed = Math.Min(targetSpeed, brakingLimit);

        // Safety backstop: prevent overshoot in one tick
        if ((ctx.DeltaSeconds > 0) && (dist > 0))
        {
            double maxSpeedForDist = dist * 0.8 / ctx.DeltaSeconds * 3600.0;
            targetSpeed = Math.Min(targetSpeed, maxSpeedForDist);
        }

        AdjustSpeed(ctx, targetSpeed);
        return NavigatorResult.Navigating;
    }

    private static void AdjustSpeed(PhaseContext ctx, double targetSpeed)
    {
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

    public GroundNavigatorDto ToSnapshot() =>
        new()
        {
            TargetNodeId = TargetNodeId,
            TargetLat = TargetLat,
            TargetLon = TargetLon,
            PrevDistToTarget = PrevDistToTarget,
            CurrentNodeRequiredSpeed = _currentNodeRequiredSpeed,
            MaxSpeedKts = MaxSpeedKts,
            CornerSpeedKts = CornerSpeedKts,
            NextSegmentBearing = _nextSegmentBearing,
            SpeedConstraints =
                _speedConstraints.Count > 0
                    ? _speedConstraints
                        .Select(c => new SpeedConstraintDto
                        {
                            PathDistNm = c.PathDistNm,
                            RequiredSpeedKts = c.RequiredSpeedKts,
                            NodeId = c.NodeId,
                        })
                        .ToList()
                    : null,
        };

    public static GroundNavigator FromSnapshot(GroundNavigatorDto dto)
    {
        var nav = new GroundNavigator
        {
            TargetNodeId = dto.TargetNodeId,
            TargetLat = dto.TargetLat,
            TargetLon = dto.TargetLon,
            PrevDistToTarget = dto.PrevDistToTarget,
            _currentNodeRequiredSpeed = dto.CurrentNodeRequiredSpeed,
            MaxSpeedKts = dto.MaxSpeedKts,
            CornerSpeedKts = dto.CornerSpeedKts,
            _nextSegmentBearing = dto.NextSegmentBearing,
        };

        if (dto.SpeedConstraints is not null)
        {
            nav._speedConstraints = dto.SpeedConstraints.Select(c => (c.PathDistNm, c.RequiredSpeedKts, c.NodeId)).ToList();
        }

        return nav;
    }
}
