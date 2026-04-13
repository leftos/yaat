using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Result of a single <see cref="GroundNavigator.Tick"/> call. Phases use
/// this to decide when to advance the route segment index, terminate the
/// phase, etc.
/// </summary>
public enum NavigatorResult
{
    /// <summary>Still moving toward the current target node.</summary>
    Navigating,

    /// <summary>Target node reached; the phase should advance to the next segment.</summary>
    ArrivedAtNode,
}

/// <summary>
/// Per-tick diagnostic snapshot produced by <see cref="GroundNavigator"/>.
/// Consumed by <c>TickRecorder</c> for CSV traces and by
/// <c>Yaat.TickInspector</c> for post-hoc analysis.
/// </summary>
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

/// <summary>
/// Per-tick controller that drives an aircraft along a resolved
/// <see cref="TaxiRoute"/> via Design B closed-form playback over
/// <see cref="PathPrimitive"/>s. Each <see cref="SetupSegment"/> call
/// compiles the route's current segment into a <see cref="PathPrimitive"/>
/// and walks forward through remaining segments to build a kinematic speed-
/// constraint profile. Each <see cref="Tick"/> call dispatches on the
/// primitive's kind: straight segments use bearing-to-target steering;
/// arc segments advance a closed-form circular integrator and write the
/// aircraft's lat/lon/heading DIRECTLY from the playback state.
///
/// <para>
/// The key structural property (Design B invariant I2) is that during an arc
/// primitive, position and heading are both functions of a single scalar
/// (the aircraft's current compass bearing from the arc centre). They
/// cannot drift apart, so the feedback-saturation knife-edge that dogged
/// the old Bezier-waypoint approach cannot occur here by construction.
/// </para>
///
/// <para>
/// Responsibilities: steer the aircraft along each segment of the route
/// (straight or arc); manage speed (slow for upcoming turns, brake at
/// hold-shorts, stop at route end); detect per-segment arrival.
/// Not responsible for: route building, hold-short insertion, phase
/// handoff, runway assignment.
/// </para>
/// </summary>
public sealed class GroundNavigator
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundNavigator");

    /// <summary>Standard arrival threshold in nautical miles (~91 ft).</summary>
    private const double NodeArrivalThresholdNm = 0.015;

    /// <summary>Tight arrival threshold used on the last segment and before arcs (~1.8 ft).</summary>
    private const double FinalNodeArrivalThresholdNm = 0.0003;

    /// <summary>Distance at which the overshoot watchdog arms (~182 ft).</summary>
    private const double OvershootDetectionNm = 0.03;

    /// <summary>Speed floor below which the arc integrator refuses to advance (I7: no pivot-in-place).</summary>
    private const double ArcSpeedFloorKts = 0.1;

    public int TargetNodeId { get; private set; }
    public double TargetLat { get; set; }
    public double TargetLon { get; set; }
    public double PrevDistToTarget { get; set; } = double.MaxValue;
    public NavTickDiag? LastTickDiag { get; private set; }
    public double MaxSpeedKts { get; set; }

    public void SetTargetNodeId(int nodeId) => TargetNodeId = nodeId;

    // --- Internal state ---

    /// <summary>The compiled primitive for the current segment. Null until first <see cref="SetupSegment"/>.</summary>
    private PathPrimitive? _currentPrimitive;

    /// <summary>
    /// Working arc-playback state: the aircraft's current compass bearing
    /// from the centre of <c>_currentPrimitive</c> when that primitive is a
    /// <see cref="PathPrimitiveArc"/>. Advanced each tick by <c>speed·dt/r</c>
    /// (signed by <see cref="PathPrimitiveArc.RightTurn"/>).
    /// </summary>
    private double _arcBearingFromCenterDeg;

    /// <summary>Remaining sweep in degrees; decreases monotonically to 0 as the arc completes.</summary>
    private double _arcRemainingSweepDeg;

    /// <summary>Starting lat/lon of the current segment, for diagnostic & cross-track logging.</summary>
    private double _segmentFromLat;
    private double _segmentFromLon;

    /// <summary>
    /// Required ground speed at the current target node. 0 for stop targets
    /// (uncleared hold-shorts, last segment of route). For transit nodes,
    /// computed from the turn angle to the next segment via
    /// <see cref="CategoryPerformance.CornerSpeedForAngle"/>.
    /// </summary>
    private double _currentNodeRequiredSpeed;

    /// <summary>
    /// Outbound bearing of the next segment, for the pre-turn blend on
    /// straight approaches. Null when there is no next segment or when the
    /// current target is a stop.
    /// </summary>
    private double? _nextSegmentBearing;

    /// <summary>
    /// Speed constraints from future segments, each as a tuple of:
    /// (path distance from current target, required speed at that point, node id).
    /// Computed during <see cref="SetupSegment"/> via forward-walk + backward-
    /// propagation, mirroring V1's approach but populated directly from
    /// <see cref="TaxiRouteSegment"/> iteration.
    /// </summary>
    private List<(double PathDistNm, double RequiredSpeedKts, int NodeId)> _speedConstraints = [];

    public void SetupSegment(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        var seg = route.CurrentSegment;
        if (seg is null)
        {
            return;
        }

        _currentPrimitive = PathPrimitiveBuilder.FromSegment(seg);

        var from = seg.Edge.FromNode;
        var to = seg.Edge.ToNode;
        TargetNodeId = seg.ToNodeId;
        TargetLat = to.Latitude;
        TargetLon = to.Longitude;
        _segmentFromLat = from.Latitude;
        _segmentFromLon = from.Longitude;
        PrevDistToTarget = double.MaxValue;

        if (_currentPrimitive is PathPrimitiveArc arcPrim)
        {
            _arcBearingFromCenterDeg = arcPrim.StartBearingFromCenterDeg;
            _arcRemainingSweepDeg = arcPrim.SweepDeg;
        }
        else
        {
            _arcRemainingSweepDeg = 0;
        }

        BuildSpeedConstraints(route, ctx, isHoldShortCleared);

        Log.LogDebug(
            "[NavV2] SetupSegment seg={SegIdx}/{Total} target={NodeId} kind={Kind} dist={Dist:F4}nm",
            route.CurrentSegmentIndex,
            route.Segments.Count,
            TargetNodeId,
            _currentPrimitive?.Kind,
            seg.Edge.DistanceNm
        );
    }

    public NavigatorResult Tick(PhaseContext ctx, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        return _currentPrimitive switch
        {
            PathPrimitiveStraight s => TickStraight(ctx, s, isLastSegment, isHoldShortCleared),
            PathPrimitiveArc a => TickArc(ctx, a, isLastSegment, isHoldShortCleared),
            _ => NavigatorResult.ArrivedAtNode,
        };
    }

    private NavigatorResult TickStraight(PhaseContext ctx, PathPrimitiveStraight prim, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        double distNm = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);

        // Tight arrival threshold when any of:
        //   - last segment of the route (always stop precisely),
        //   - the current target is a stop (_currentNodeRequiredSpeed == 0),
        //   - the effective edge (segment start to current TargetLat/Lon) is
        //     shorter than 1.5× the loose threshold.
        // The last case handles the hold-short override — TaxiingPhase moves
        // the target from the graph to-node to a virtual HS position closer
        // to the aircraft, which makes the effective edge short even when
        // the underlying segment is long. Without this check, the loose
        // 91 ft arrival threshold can fire 10-80 ft short of a hold-short
        // stop, leaving the aircraft parked well behind the painted line.
        double edgeLengthNm = GeoMath.DistanceNm(_segmentFromLat, _segmentFromLon, TargetLat, TargetLon);
        bool shortEdge = edgeLengthNm < NodeArrivalThresholdNm * 1.5;
        bool isStopTarget = _currentNodeRequiredSpeed == 0;
        double arrivalThresholdNm = (isLastSegment || shortEdge || isStopTarget) ? FinalNodeArrivalThresholdNm : NodeArrivalThresholdNm;

        bool overshot = distNm > PrevDistToTarget && PrevDistToTarget < OvershootDetectionNm;
        bool stalledAtThreshold = ctx.Aircraft.GroundSpeed < 0.5 && distNm < arrivalThresholdNm + 0.001;
        bool straightArrived = distNm <= arrivalThresholdNm;

        if (straightArrived || overshot || stalledAtThreshold)
        {
            // Corrective nudge toward next segment bearing, bounded by turn rate.
            if (_nextSegmentBearing is { } nextBrg)
            {
                double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
                ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, nextBrg, maxTurn);
            }
            PrevDistToTarget = double.MaxValue;
            return NavigatorResult.ArrivedAtNode;
        }

        double bearingToTargetDeg = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);

        // Pre-turn blend: in the last ~50 ft of a straight that precedes a
        // gentle turn, start blending the steer target toward the next
        // segment's departure bearing. Gated by turn angle — large turns
        // (>60°) get no blend to avoid yanking the tail early.
        if (_nextSegmentBearing is { } nextBearingDeg)
        {
            double turnAngle = GeoMath.AbsBearingDifference(bearingToTargetDeg, nextBearingDeg);
            double angleScale = Math.Clamp(1.0 - ((turnAngle - 30.0) / 60.0), 0.0, 1.0);
            const double preturnDistNm = 0.008; // ~50 ft
            if (distNm < preturnDistNm && angleScale > 0.01)
            {
                double blend = (1.0 - distNm / preturnDistNm) * angleScale;
                bearingToTargetDeg = GeoMath.BlendBearings(bearingToTargetDeg, nextBearingDeg, blend);
            }
        }

        double maxTurnDeg = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearingToTargetDeg, maxTurnDeg);

        double targetSpeed = ComputeTargetSpeed(ctx, distNm, isHoldShortCleared);

        // Safety backstop: cap target speed so the aircraft cannot cover more
        // than ~80% of the remaining distance in a single tick (would
        // overshoot the arrival threshold).
        if (ctx.DeltaSeconds > 0 && distNm > 0)
        {
            double maxSpeedForDist = distNm * 0.8 / ctx.DeltaSeconds * 3600.0;
            targetSpeed = Math.Min(targetSpeed, maxSpeedForDist);
        }

        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, targetSpeed);

        AdjustSpeed(ctx, ctx.Targets.TargetSpeed ?? targetSpeed);

        PrevDistToTarget = distNm;
        UpdateDiag(ctx, distNm, bearingToTargetDeg, targetSpeed, onArc: false);
        return NavigatorResult.Navigating;
    }

    private NavigatorResult TickArc(PhaseContext ctx, PathPrimitiveArc prim, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        // Speed floor (I7: no pivot-in-place). If the aircraft is effectively
        // stopped, set speed target and bail — physics will re-accelerate.
        double vKts = ctx.Aircraft.IndicatedAirspeed;
        if (vKts < ArcSpeedFloorKts)
        {
            double currentTangent = CurrentArcTangentDeg(prim);
            ctx.Targets.TargetTrueHeading = new TrueHeading(currentTangent);
            double targetSpeed = ComputeTargetSpeed(ctx, ArcRemainingLengthNm(prim), isHoldShortCleared);
            ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, targetSpeed);
            AdjustSpeed(ctx, ctx.Targets.TargetSpeed ?? targetSpeed);
            return NavigatorResult.Navigating;
        }

        // Advance the arc by ds = v·dt.
        double vFtPerSec = vKts * GeoMath.FeetPerNm / 3600.0;
        double dsFt = vFtPerSec * ctx.DeltaSeconds;
        double dAngleRad = dsFt / prim.RadiusFt;
        double dAngleDeg = dAngleRad * (180.0 / Math.PI);
        dAngleDeg = Math.Min(dAngleDeg, _arcRemainingSweepDeg);

        double signed = prim.RightTurn ? +dAngleDeg : -dAngleDeg;
        _arcBearingFromCenterDeg = (((_arcBearingFromCenterDeg + signed) % 360.0) + 360.0) % 360.0;
        _arcRemainingSweepDeg = Math.Max(0.0, _arcRemainingSweepDeg - dAngleDeg);

        // Write position + heading directly from the playback state (invariant I2).
        var (lat, lon) = GeoMath.ProjectPoint(prim.CenterLat, prim.CenterLon, new TrueHeading(_arcBearingFromCenterDeg), prim.RadiusNm);
        double tangentDeg = CurrentArcTangentDeg(prim);
        ctx.Aircraft.Latitude = lat;
        ctx.Aircraft.Longitude = lon;
        ctx.Aircraft.TrueHeading = new TrueHeading(tangentDeg);

        // Mirror into targets so physics does not fight the closed-form state.
        ctx.Targets.TargetTrueHeading = new TrueHeading(tangentDeg);
        double arcTargetSpeed = ComputeTargetSpeed(ctx, ArcRemainingLengthNm(prim), isHoldShortCleared);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, arcTargetSpeed);
        AdjustSpeed(ctx, ctx.Targets.TargetSpeed ?? arcTargetSpeed);

        double distToNode = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);
        PrevDistToTarget = distToNode;
        UpdateDiag(ctx, distToNode, tangentDeg, arcTargetSpeed, onArc: true);

        if (_arcRemainingSweepDeg <= 0.01)
        {
            if (_nextSegmentBearing is { } nextBrg)
            {
                double maxTurnArc = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
                ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, nextBrg, maxTurnArc);
            }
            PrevDistToTarget = double.MaxValue;
            return NavigatorResult.ArrivedAtNode;
        }

        return NavigatorResult.Navigating;
    }

    private double CurrentArcTangentDeg(PathPrimitiveArc prim)
    {
        double tangent = prim.RightTurn ? _arcBearingFromCenterDeg + 90.0 : _arcBearingFromCenterDeg - 90.0;
        return ((tangent % 360.0) + 360.0) % 360.0;
    }

    private double ArcRemainingLengthNm(PathPrimitiveArc prim) => _arcRemainingSweepDeg * prim.RadiusFt * Math.PI / 180.0 / GeoMath.FeetPerNm;

    private double ComputeTargetSpeed(PhaseContext ctx, double distToEndpointNm, Func<int, bool> isHoldShortCleared)
    {
        double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);

        // Brake curve from the current node's required speed.
        double brakingLimit = Math.Sqrt(_currentNodeRequiredSpeed * _currentNodeRequiredSpeed + 2.0 * decelRate * distToEndpointNm * 3600.0);

        // Apply each future constraint.
        foreach (var (pathDist, reqSpeed, nodeId) in _speedConstraints)
        {
            if (reqSpeed == 0 && isHoldShortCleared(nodeId))
            {
                continue;
            }
            double totalDist = distToEndpointNm + pathDist;
            double limit = Math.Sqrt(reqSpeed * reqSpeed + 2.0 * decelRate * totalDist * 3600.0);
            brakingLimit = Math.Min(brakingLimit, limit);
        }

        // Quadratic scaling by heading error so the aircraft slows during
        // large re-alignments. For arcs this is ~1 (we write the exact
        // tangent heading each tick) so it is a no-op.
        double bearingDeg = _currentPrimitive is PathPrimitiveArc arcPrim
            ? CurrentArcTangentDeg(arcPrim)
            : GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLat, TargetLon);
        double angleDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearingDeg));
        double normalized = Math.Clamp(angleDiff / 90.0, 0.0, 1.0);
        double speedFraction = Math.Max(0.03, 1.0 - normalized * normalized);

        return Math.Min(MaxSpeedKts * speedFraction, brakingLimit);
    }

    private void BuildSpeedConstraints(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        _speedConstraints.Clear();

        var seg = route.CurrentSegment;
        if (seg is null)
        {
            return;
        }

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
            double inbound = seg.Edge.ArrivalBearing;
            double outbound = nextSeg.Edge.DepartureBearing;
            double turnAngle = GeoMath.AbsBearingDifference(inbound, outbound);
            _currentNodeRequiredSpeed = CategoryPerformance.CornerSpeedForAngle(ctx.Category, turnAngle);
            _nextSegmentBearing = outbound;
        }
        else
        {
            _currentNodeRequiredSpeed = 0;
            _nextSegmentBearing = null;
        }

        // Forward walk: collect future speed constraints.
        double cumulativeDistNm = 0;
        double turnRate = CategoryPerformance.GroundTurnRate(ctx.Category);
        for (int i = route.CurrentSegmentIndex + 1; i < route.Segments.Count; i++)
        {
            var futureSeg = route.Segments[i];
            cumulativeDistNm += futureSeg.Edge.DistanceNm;

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
                double inBearing = futureSeg.Edge.ArrivalBearing;
                double outBearing = nextNextSeg.Edge.DepartureBearing;
                double futureTurnAngle = GeoMath.AbsBearingDifference(inBearing, outBearing);
                reqSpeed = CategoryPerformance.CornerSpeedForAngle(ctx.Category, futureTurnAngle);
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

        // Backward propagation: apply kinematic decel between adjacent constraints.
        double decelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        for (int i = _speedConstraints.Count - 2; i >= 0; i--)
        {
            var (dist, speed, nodeId) = _speedConstraints[i];
            var (nextDist, nextSpeed, _) = _speedConstraints[i + 1];
            double legDist = nextDist - dist;
            double backProp = Math.Sqrt(nextSpeed * nextSpeed + 2.0 * decelRate * legDist * 3600.0);
            if (backProp < speed)
            {
                _speedConstraints[i] = (dist, backProp, nodeId);
            }
        }

        // Propagate the first future constraint back into the current node's required speed.
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
    /// Clamp the requested target speed by <see cref="AircraftState.GroundSpeedLimit"/>.
    /// Keeps V2 from overrunning conflict-imposed or airport-imposed speed
    /// caps that physics layers above us enforce.
    /// </summary>
    private static double ClampBySpeedLimit(PhaseContext ctx, double requested) =>
        ctx.Aircraft.GroundSpeedLimit is { } limit ? Math.Min(requested, limit) : requested;

    /// <summary>
    /// Accelerate/decelerate toward <paramref name="targetSpeed"/> bounded by
    /// the category's taxi accel/decel rates. Mirrors V1's AdjustSpeed so
    /// physics behaviour at the straight-segment level matches.
    /// </summary>
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

    private void UpdateDiag(PhaseContext ctx, double distNm, double bearingDeg, double targetSpeed, bool onArc)
    {
        double angleDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearingDeg));
        var diag = new NavTickDiag(
            TargetNodeId: TargetNodeId,
            DistToTargetNm: distNm,
            BearingToTargetDeg: bearingDeg,
            AngleDiffDeg: angleDiff,
            TargetSpeedKts: targetSpeed,
            BrakingLimitKts: targetSpeed,
            ArcSpeedLimitKts: double.MaxValue,
            OnArc: onArc,
            NodeRequiredSpeedKts: _currentNodeRequiredSpeed,
            PathDeviationFt: 0.0,
            SegFromLat: _segmentFromLat,
            SegFromLon: _segmentFromLon
        );
        LastTickDiag = diag;
        ctx.Aircraft.LastNavDiag = diag;
    }

    // ---- Snapshot ----
    // Non-round-tripping: ToSnapshot writes the minimum state needed for
    // diagnostic continuity; FromSnapshot returns an instance that re-runs
    // SetupSegment on its next call. A mid-arc snapshot/restore resumes from
    // where the plan puts the aircraft geometrically, not from an exact arc
    // progress point. Acceptable because arc segments are 2-3 seconds and
    // mid-arc saves are rare.

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
        };

    public static GroundNavigator FromSnapshot(GroundNavigatorDto dto) =>
        new()
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
}
