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
/// The owning phase configures MaxSpeedKts and handles what
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

    private double _currentNodeRequiredSpeed;
    private double? _nextSegmentBearing;
    private List<(double PathDistNm, double RequiredSpeedKts, int NodeId)> _speedConstraints = [];

    /// <summary>
    /// Polyline waypoints for arc traversal. When entering an arc segment, the bezier
    /// is subdivided into short straight segments (~15ft). The navigator walks through
    /// these sequentially, only advancing the route segment when the queue is empty.
    /// Non-null when traversing an arc; null for straight edges.
    /// </summary>
    private Queue<(double Lat, double Lon)>? _arcWaypoints;

    /// <summary>
    /// Min radius of curvature (ft) of the current arc segment. Zero when not on an arc.
    /// Used to compute the arc speed limit without needing the full <see cref="GroundArc"/>
    /// object, which enables snapshot restore.
    /// </summary>
    private double _arcMinRadiusFt;

    /// <summary>
    /// Distance in NM from the current arc waypoint through remaining waypoints to
    /// the segment endpoint. Zero when not traversing an arc. Used to correct braking
    /// distances — <c>dist</c> in Tick is only the distance to the next waypoint, but
    /// speed constraints are measured from the segment endpoint.
    /// </summary>
    private double _remainingArcDistNm;

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

        // Detect arc segments: subdivide bezier into polyline waypoints so the
        // aircraft traces the curve precisely instead of using a lookahead carrot.
        if (seg.Edge.Edge is GroundArc arc)
        {
            bool fromIsZero = arc.Nodes[0].Id == seg.Edge.FromNode.Id;
            _arcMinRadiusFt = arc.MinRadiusOfCurvatureFt;
            _arcWaypoints = SubdivideArc(arc, fromIsZero);

            // Target the first waypoint instead of the arc endpoint
            if (_arcWaypoints.Count > 0)
            {
                var first = _arcWaypoints.Dequeue();
                TargetLat = first.Lat;
                TargetLon = first.Lon;

                // Pre-compute remaining arc distance so braking uses full distance
                // to segment endpoint, not just distance to next waypoint.
                _remainingArcDistNm = 0;
                double prevLat = first.Lat,
                    prevLon = first.Lon;
                foreach (var (lat, lon) in _arcWaypoints)
                {
                    _remainingArcDistNm += GeoMath.DistanceNm(prevLat, prevLon, lat, lon);
                    prevLat = lat;
                    prevLon = lon;
                }
            }
        }
        else
        {
            _arcMinRadiusFt = 0;
            _arcWaypoints = null;
            _remainingArcDistNm = 0;
        }

        string segType = _arcWaypoints is not null ? "arc" : "straight";
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

        // Use a tight arrival threshold for arcs (polyline waypoints) and short
        // edges. Fillet tangent nodes are often spaced 25-65ft apart and define
        // the shape of turns — the generous 91ft threshold skips them, causing
        // the aircraft to cut corners instead of following the path.
        bool onArc = _arcWaypoints is not null;
        double edgeLengthNm = GeoMath.DistanceNm(_segmentFromLat, _segmentFromLon, TargetLat, TargetLon);
        bool shortEdge = edgeLengthNm < NodeArrivalThresholdNm * 1.5;
        double arrivalThreshold = (isLastSegment || onArc || shortEdge) ? FinalNodeArrivalThresholdNm : NodeArrivalThresholdNm;

        bool overshot = (dist > PrevDistToTarget) && (PrevDistToTarget < OvershootDetectionNm);
        bool stoppedByConflict = (ctx.Aircraft.GroundSpeedLimit is not null) && (ctx.Aircraft.GroundSpeedLimit.Value < 0.5);
        bool stalledAtThreshold = !stoppedByConflict && (ctx.Aircraft.GroundSpeed < 0.5) && (dist < arrivalThreshold + 0.001);
        PrevDistToTarget = dist;

        if (dist <= arrivalThreshold || overshot || stalledAtThreshold)
        {
            // Arc polyline: advance to the next waypoint if any remain
            if (_arcWaypoints is { Count: > 0 })
            {
                var next = _arcWaypoints.Dequeue();
                double wpDist = GeoMath.DistanceNm(TargetLat, TargetLon, next.Lat, next.Lon);
                _remainingArcDistNm = Math.Max(0, _remainingArcDistNm - wpDist);
                _segmentFromLat = TargetLat;
                _segmentFromLon = TargetLon;
                TargetLat = next.Lat;
                TargetLon = next.Lon;
                PrevDistToTarget = double.MaxValue;
                Log.LogTrace(
                    "[Nav] Arc waypoint reached, next wp dist={Dist:F4}nm, {Remaining} remaining",
                    GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, next.Lat, next.Lon),
                    _arcWaypoints.Count
                );
                // Don't return ArrivedAtNode — still on the same route segment
            }
            else
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
        }

        // Steer toward current target (waypoint or node)
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);
        double arcSpeedLimit = double.MaxValue;

        // Apply arc speed limit when traversing an arc's polyline waypoints
        if (_arcMinRadiusFt > 0)
        {
            double turnRateDegSec = CategoryPerformance.GroundTurnRate(ctx.Category);
            double turnRateRadSec = turnRateDegSec * (Math.PI / 180.0);
            arcSpeedLimit = turnRateRadSec * (_arcMinRadiusFt / GeoMath.FeetPerNm) * 3600.0;
        }

        // Pre-turning: when approaching a junction with a known next-segment bearing,
        // blend the steer target toward the outbound bearing. Scale the blend by
        // turn angle — gentle turns get full pre-turn, large turns (>60°) get very
        // little to avoid yanking inside the arc before entering it.
        if (_nextSegmentBearing is { } nextBrg && _arcWaypoints is null)
        {
            double turnAngle = GeoMath.AbsBearingDifference(bearing, nextBrg);
            double angleScale = Math.Clamp(1.0 - ((turnAngle - 30.0) / 60.0), 0.0, 1.0);
            const double preturnDistNm = 0.008; // ~50ft
            if ((dist < preturnDistNm) && (angleScale > 0.01))
            {
                double blend = (1.0 - (dist / preturnDistNm)) * angleScale;
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
        // During arc traversal, dist is only to the next arc waypoint (~15ft).
        // Add remaining arc distance to get the true distance to the segment endpoint,
        // which is where _currentNodeRequiredSpeed and _speedConstraints are measured from.
        double distToEndpoint = dist + _remainingArcDistNm;
        double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        double brakingLimit = Math.Sqrt(_currentNodeRequiredSpeed * _currentNodeRequiredSpeed + 2.0 * decelRate * distToEndpoint * 3600.0);
        targetSpeed = Math.Min(targetSpeed, brakingLimit);

        foreach (var (pathDist, reqSpeed, nodeId) in _speedConstraints)
        {
            if ((reqSpeed == 0) && isHoldShortCleared(nodeId))
            {
                continue;
            }

            double totalDist = distToEndpoint + pathDist;
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
            onArc
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
            onArc,
            _currentNodeRequiredSpeed,
            deviationFt,
            _segmentFromLat,
            _segmentFromLon
        );
        LastTickDiag = diag;
        ctx.Aircraft.LastNavDiag = diag;

        AdjustSpeed(ctx, targetSpeed);
        return NavigatorResult.Navigating;
    }

    /// <summary>
    /// Perpendicular distance in feet from the aircraft to the infinite line defined
    /// by the current segment's direction. Uses cross-track distance so the measurement
    /// is purely lateral — unaffected by the aircraft being ahead of or behind the
    /// segment endpoints (which happens when short edges are skipped).
    /// </summary>
    private double ComputePathDeviation(PhaseContext ctx, double distToTargetNm)
    {
        double edgeBearing = GeoMath.BearingTo(_segmentFromLat, _segmentFromLon, TargetLat, TargetLon);
        double crossNm = GeoMath.SignedCrossTrackDistanceNmRaw(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _segmentFromLat,
            _segmentFromLon,
            edgeBearing
        );
        return Math.Abs(crossNm) * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// Subdivide a bezier arc into polyline waypoints spaced ~15ft apart.
    /// The last waypoint is always the arc endpoint (ToNode).
    /// </summary>
    private static Queue<(double Lat, double Lon)> SubdivideArc(GroundArc arc, bool fromNodeIsZero)
    {
        CubicBezier bezier;
        if (fromNodeIsZero)
        {
            bezier = arc.ToBezier();
        }
        else
        {
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

        const double spacingFt = 15.0;
        double spacingNm = spacingFt / GeoMath.FeetPerNm;
        double totalLengthNm = bezier.ArcLengthNm(30);
        int count = Math.Max(2, (int)Math.Ceiling(totalLengthNm / spacingNm));

        var waypoints = new Queue<(double Lat, double Lon)>();
        for (int i = 1; i <= count; i++)
        {
            double t = (double)i / count;
            var (lat, lon) = bezier.Evaluate(t);
            waypoints.Enqueue((lat, lon));
        }

        return waypoints;
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
            ArcState = _arcWaypoints is not null
                ? new ArcStateDto
                {
                    MinRadiusOfCurvatureFt = _arcMinRadiusFt,
                    RemainingDistNm = _remainingArcDistNm,
                    Waypoints = _arcWaypoints.Select(w => new[] { w.Lat, w.Lon }).ToList(),
                }
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
            _nextSegmentBearing = dto.NextSegmentBearing,
        };

        if (dto.SpeedConstraints is not null)
        {
            nav._speedConstraints = dto.SpeedConstraints.Select(c => (c.PathDistNm, c.RequiredSpeedKts, c.NodeId)).ToList();
        }

        if (dto.ArcState is not null)
        {
            nav._arcMinRadiusFt = dto.ArcState.MinRadiusOfCurvatureFt;
            nav._remainingArcDistNm = dto.ArcState.RemainingDistNm;
            nav._arcWaypoints = new Queue<(double Lat, double Lon)>(dto.ArcState.Waypoints.Select(w => (w[0], w[1])));
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
    double PathDeviationFt,
    double SegFromLat,
    double SegFromLon
);
