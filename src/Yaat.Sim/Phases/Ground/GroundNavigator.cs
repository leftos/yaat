using Microsoft.Extensions.Logging;
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
    private static readonly ILogger Log = SimLog.CreateLogger("GroundNavigator");

    private const double NodeArrivalThresholdNm = 0.015;
    private const double FinalNodeArrivalThresholdNm = 0.0003;
    private const double OvershootDetectionNm = 0.03;

    public int TargetNodeId { get; private set; }
    public double TargetLat { get; set; }
    public double TargetLon { get; set; }
    public double PrevDistToTarget { get; set; } = double.MaxValue;
    public NavTickDiag? LastTickDiag { get; private set; }

    public void SetTargetNodeId(int nodeId) => TargetNodeId = nodeId;

    public double MaxSpeedKts { get; set; }
    public double CornerSpeedKts { get; set; }

    private double _currentNodeRequiredSpeed;
    private double? _nextSegmentBearing;
    private List<(double PathDistNm, double RequiredSpeedKts, int NodeId)> _speedConstraints = [];

    /// <summary>
    /// When the current segment is a <see cref="GroundArc"/>, this holds the arc geometry
    /// and the traversal direction (from/to node). Null for straight edges.
    /// </summary>
    private (GroundArc Arc, bool FromNodeIsZero)? _currentArc;

    private double _segmentFromLat;
    private double _segmentFromLon;

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
        if (seg is null)
        {
            return;
        }

        var targetNode = seg.Edge.ToNode;
        TargetNodeId = seg.ToNodeId;
        TargetLat = targetNode.Latitude;
        TargetLon = targetNode.Longitude;
        _segmentFromLat = seg.Edge.FromNode.Latitude;
        _segmentFromLon = seg.Edge.FromNode.Longitude;
        PrevDistToTarget = double.MaxValue;

        // Detect arc segments for carrot-on-a-stick path following
        if (seg.Edge.Edge is GroundArc arc)
        {
            bool fromIsZero = arc.Nodes[0].Id == seg.Edge.FromNode.Id;
            _currentArc = (arc, fromIsZero);
        }
        else
        {
            _currentArc = null;
        }

        string segType = _currentArc is not null ? "arc" : "straight";
        Log.LogDebug(
            "[Nav] SetupSegment seg={SegIdx}/{Total} target={NodeId} type={Type} edge={Edge} dist={Dist:F4}nm",
            route.CurrentSegmentIndex,
            route.Segments.Count,
            TargetNodeId,
            segType,
            seg.TaxiwayName,
            seg.Edge.DistanceNm
        );

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
            var nextSeg = route.Segments[nextIdx];
            if (nextSeg.Edge.ToNode is not null)
            {
                // Use edge-aware bearings: arc tangent directions instead of node-to-node
                double inboundBearing = seg.Edge.ArrivalBearing;
                double outboundBearing = nextSeg.Edge.DepartureBearing;
                double turnAngle = GeoMath.AbsBearingDifference(inboundBearing, outboundBearing);
                _currentNodeRequiredSpeed = CategoryPerformance.CornerSpeedForAngle(ctx.Category, turnAngle);
                _nextSegmentBearing = outboundBearing;
                Log.LogDebug(
                    "[Nav]   turn at node {NodeId}: inbound={In:F1}° outbound={Out:F1}° angle={Angle:F1}° reqSpeed={Speed:F1}kts",
                    TargetNodeId,
                    inboundBearing,
                    outboundBearing,
                    turnAngle,
                    _currentNodeRequiredSpeed
                );
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
            Log.LogDebug("[Nav]   last segment, reqSpeed=0");
        }

        // B. Forward walk: collect speed constraints at future nodes
        _speedConstraints = [];
        double cumulativeDistNm = 0;
        double turnRate = CategoryPerformance.GroundTurnRate(ctx.Category);
        for (int i = route.CurrentSegmentIndex + 1; i < route.Segments.Count; i++)
        {
            var futureSeg = route.Segments[i];
            var fromNode = futureSeg.Edge.FromNode;
            var toNode = futureSeg.Edge.ToNode;
            if (fromNode is null || toNode is null)
            {
                break;
            }

            // Use edge distance (arc length for arcs) instead of straight-line between nodes
            cumulativeDistNm += futureSeg.Edge.DistanceNm;

            // Arc speed constraint: the arc's radius limits max speed through it.
            // Add this at the arc's start (cumulative distance minus the arc length).
            if (futureSeg.Edge.Edge is GroundArc futureArc)
            {
                double arcMaxSpeed = futureArc.MaxSafeSpeedKts(turnRate);
                if (arcMaxSpeed < MaxSpeedKts)
                {
                    double arcStartDist = cumulativeDistNm - futureSeg.Edge.DistanceNm;
                    _speedConstraints.Add((arcStartDist, arcMaxSpeed, futureSeg.Edge.FromNodeId));
                }
            }

            if (!isHoldShortCleared(futureSeg.ToNodeId))
            {
                _speedConstraints.Add((cumulativeDistNm, 0, futureSeg.ToNodeId));
                break;
            }

            int nextNextIdx = i + 1;
            double reqSpeed;
            if (nextNextIdx < route.Segments.Count)
            {
                var nextNextSeg = route.Segments[nextNextIdx];
                if (nextNextSeg.Edge.ToNode is not null)
                {
                    // Use edge-aware bearings for turn angle at the junction
                    double inBearing = futureSeg.Edge.ArrivalBearing;
                    double outBearing = nextNextSeg.Edge.DepartureBearing;
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

        Log.LogDebug(
            "[Nav]   constraints: reqSpeed={ReqSpeed:F1}kts, {Count} future constraints",
            _currentNodeRequiredSpeed,
            _speedConstraints.Count
        );
        foreach (var (d, s, n) in _speedConstraints)
        {
            Log.LogDebug("[Nav]     dist={Dist:F4}nm speed={Speed:F1}kts node={Node}", d, s, n);
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

        bool overshot = (dist > PrevDistToTarget) && (PrevDistToTarget < OvershootDetectionNm);
        bool stoppedByConflict = (ctx.Aircraft.GroundSpeedLimit is not null) && (ctx.Aircraft.GroundSpeedLimit.Value < 0.5);
        bool stalledAtThreshold = !stoppedByConflict && (ctx.Aircraft.GroundSpeed < 0.5) && (dist < arrivalThreshold + 0.001);
        PrevDistToTarget = dist;

        if (dist <= arrivalThreshold || overshot || stalledAtThreshold)
        {
            Log.LogDebug(
                "[Nav] ARRIVED at {NodeId}: dist={Dist:F4}nm threshold={Thr:F4}nm overshot={Over} stalled={Stall}",
                TargetNodeId,
                dist,
                arrivalThreshold,
                overshot,
                stalledAtThreshold
            );
            PrevDistToTarget = double.MaxValue;
            return NavigatorResult.ArrivedAtNode;
        }

        // Steer: arc-following or straight-to-target
        double bearing;
        double arcSpeedLimit = double.MaxValue;

        if (_currentArc is { } ca)
        {
            var (steerBearing, speedLimit) = ComputeArcSteering(ctx, ca.Arc, ca.FromNodeIsZero);
            bearing = steerBearing;
            arcSpeedLimit = speedLimit;
        }
        else
        {
            bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);
        }

        // Pre-turning: when approaching a junction with a known next-segment bearing,
        // blend the steer target toward the outbound bearing. This starts the turn
        // before reaching the node, reducing overshoot at junctions.
        if (_nextSegmentBearing is { } nextBrg && _currentArc is null)
        {
            const double preturndDistNm = 0.008; // ~50ft
            if (dist < preturndDistNm)
            {
                double blend = 1.0 - (dist / preturndDistNm);
                bearing = GeoMath.BlendBearings(bearing, nextBrg, blend);
            }
        }

        double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        // Speed: quadratic scaling by heading error — large errors nearly stop the aircraft
        double angleDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearing));
        double normalized = Math.Clamp(angleDiff / 90.0, 0.0, 1.0);
        double speedFraction = Math.Max(0.03, 1.0 - (normalized * normalized));
        double targetSpeed = Math.Min(MaxSpeedKts * speedFraction, arcSpeedLimit);

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

        Log.LogTrace(
            "[Nav] Tick node={NodeId} dist={Dist:F4}nm brg={Brg:F1}° hdg={Hdg:F1}° angleDiff={Diff:F1}° gs={Gs:F1} target={TgtSpd:F1} brake={Brake:F1} arcLim={ArcLim:F1} isArc={IsArc}",
            TargetNodeId,
            dist,
            bearing,
            ctx.Aircraft.TrueHeading.Degrees,
            angleDiff,
            ctx.Aircraft.GroundSpeed,
            targetSpeed,
            brakingLimit,
            arcSpeedLimit,
            _currentArc is not null
        );

        double deviationFt = ComputePathDeviation(ctx, dist);

        var diag = new NavTickDiag(
            TargetNodeId,
            dist,
            bearing,
            angleDiff,
            targetSpeed,
            brakingLimit,
            arcSpeedLimit,
            _currentArc is not null,
            _currentNodeRequiredSpeed,
            deviationFt
        );
        LastTickDiag = diag;
        ctx.Aircraft.LastNavDiag = diag;

        AdjustSpeed(ctx, targetSpeed);
        return NavigatorResult.Navigating;
    }

    /// <summary>
    /// Perpendicular distance in feet from the aircraft to the current route segment.
    /// For arcs: projects onto the bezier curve. For straight edges: point-to-segment.
    /// </summary>
    private double ComputePathDeviation(PhaseContext ctx, double distToTargetNm)
    {
        double acLat = ctx.Aircraft.Latitude;
        double acLon = ctx.Aircraft.Longitude;

        if (_currentArc is { } ca)
        {
            CubicBezier bezier;
            if (ca.FromNodeIsZero)
            {
                bezier = ca.Arc.ToBezier();
            }
            else
            {
                bezier = new CubicBezier(
                    ca.Arc.Nodes[1].Latitude,
                    ca.Arc.Nodes[1].Longitude,
                    ca.Arc.P2Lat,
                    ca.Arc.P2Lon,
                    ca.Arc.P1Lat,
                    ca.Arc.P1Lon,
                    ca.Arc.Nodes[0].Latitude,
                    ca.Arc.Nodes[0].Longitude
                );
            }

            double t = bezier.ClosestT(acLat, acLon, 20);
            var (nearLat, nearLon) = bezier.Evaluate(t);
            return GeoMath.DistanceNm(acLat, acLon, nearLat, nearLon) * GeoMath.FeetPerNm;
        }

        return GeoMath.DistanceToSegmentFt(acLat, acLon, _segmentFromLat, _segmentFromLon, TargetLat, TargetLon);
    }

    /// <summary>
    /// "Carrot on a stick" arc following. Projects the aircraft position onto the arc,
    /// computes a lookahead point along the curve, and returns the bearing to steer toward.
    /// Also returns the max speed for the arc radius.
    /// </summary>
    public static (double Bearing, double MaxSpeedKts) ComputeArcSteering(PhaseContext ctx, GroundArc arc, bool fromNodeIsZero)
    {
        double acLat = ctx.Aircraft.Latitude;
        double acLon = ctx.Aircraft.Longitude;

        // Build bezier in traversal direction
        CubicBezier bezier;
        if (fromNodeIsZero)
        {
            bezier = arc.ToBezier();
        }
        else
        {
            // Reversed traversal: swap endpoints and control points
            bezier = new CubicBezier(
                arc.Nodes[1].Latitude,
                arc.Nodes[1].Longitude,
                arc.P2Lat,
                arc.P2Lon,
                arc.P1Lat,
                arc.P1Lon,
                arc.Nodes[0].Latitude,
                arc.Nodes[0].Longitude
            );
        }

        // Project aircraft onto curve
        double t = bezier.ClosestT(acLat, acLon, 20);

        // Lookahead: advance by ~40ft along the curve (distance-based, not parameter-based).
        // Walk forward from t in small parameter steps, accumulating arc length,
        // until we reach the target distance or the end of the curve.
        const double lookaheadFt = 40.0;
        double lookaheadNm = lookaheadFt / GeoMath.FeetPerNm;
        double lookaheadT = AdvanceByDistance(bezier, t, lookaheadNm);
        var (laLat, laLon) = bezier.Evaluate(lookaheadT);

        // Bearing from aircraft to lookahead point
        double steerBearing = GeoMath.BearingTo(acLat, acLon, laLat, laLon);

        // Speed limit from local curvature at the lookahead point. The radius of
        // curvature varies along the bezier — gentle at entry/exit, tight in the
        // middle. Using local curvature lets the aircraft travel faster on the
        // gentle portions. The braking constraints in SetupSegment ensure the
        // aircraft decelerates before reaching the tightest section.
        double turnRateDegSec = CategoryPerformance.GroundTurnRate(ctx.Category);
        double turnRateRadSec = turnRateDegSec * (Math.PI / 180.0);
        var (refLat, _) = bezier.Evaluate(lookaheadT);
        double localRadiusFt = bezier.RadiusOfCurvatureFt(lookaheadT, refLat);
        double localRadiusNm = localRadiusFt / GeoMath.FeetPerNm;
        double localMaxSpeed = turnRateRadSec * localRadiusNm * 3600.0;

        // Floor at the min-radius speed to prevent overshoot when entering
        // a section where curvature tightens faster than we can sample
        double minRadiusSpeed = arc.MaxSafeSpeedKts(turnRateDegSec);
        double maxSpeedKts = Math.Max(localMaxSpeed, minRadiusSpeed);

        return (steerBearing, maxSpeedKts);
    }

    /// <summary>
    /// Walk forward along a bezier from parameter <paramref name="startT"/>,
    /// accumulating arc length until <paramref name="distNm"/> is reached.
    /// Returns the parameter t at that point, clamped to 1.0.
    /// </summary>
    private static double AdvanceByDistance(CubicBezier bezier, double startT, double distNm)
    {
        const int steps = 20;
        double stepSize = (1.0 - startT) / steps;
        if (stepSize <= 0)
        {
            return 1.0;
        }

        double accumulated = 0;
        var (prevLat, prevLon) = bezier.Evaluate(startT);

        for (int i = 1; i <= steps; i++)
        {
            double t = startT + (i * stepSize);
            var (lat, lon) = bezier.Evaluate(t);
            accumulated += GeoMath.DistanceNm(prevLat, prevLon, lat, lon);
            if (accumulated >= distNm)
            {
                return t;
            }

            prevLat = lat;
            prevLon = lon;
        }

        return 1.0;
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
            SegmentFromLat = _segmentFromLat,
            SegmentFromLon = _segmentFromLon,
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
            _segmentFromLat = dto.SegmentFromLat,
            _segmentFromLon = dto.SegmentFromLon,
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

public record NavTickDiag(
    int TargetNodeId,
    double DistToTargetNm,
    double BearingToTargetDeg,
    double AngleDiffDeg,
    double TargetSpeedKts,
    double BrakingLimitKts,
    double ArcSpeedLimitKts,
    bool OnArc,
    double NodeRequiredSpeedKts,
    double PathDeviationFt
);
