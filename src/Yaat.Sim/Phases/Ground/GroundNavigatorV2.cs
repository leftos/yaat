using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Clean-room V2 ground navigator (selected via <see cref="GroundNavigatorRouter"/>). Drives an aircraft
/// along a resolved <see cref="TaxiRoute"/> over V2 fillet geometry via closed-form playback over
/// <see cref="PathPrimitive"/>s (invariant I2 — during an arc, position and heading are both functions of
/// one scalar, so they cannot drift apart). Straight segments use pure-pursuit steering; arc and slow-turn
/// segments advance a closed-form circular integrator and write lat/lon/heading directly from playback
/// state. Speed comes from corner-speed limits + backward-propagated kinematic braking, capped by the
/// lateral-accel arc speed model.
///
/// <para>
/// Built for clean V2 geometry, this deliberately drops the Legacy-fillet compensations the shared V1
/// <see cref="GroundNavigator"/> carries (slow-turn synthesis, short-segment cluster detection, chord-chain
/// aggregate-turn, the orbit-stall backstop): V2 emits proper single arcs for real corners, so those
/// mechanisms are unnecessary. The entry-alignment slow-turn (for misaligned parking-out starts) is kept.
/// </para>
///
/// <para>
/// Responsibilities: steer along each route segment; manage speed; detect per-segment arrival. Not
/// responsible for: route building, hold-short insertion, phase handoff, runway assignment.
/// </para>
/// </summary>
public sealed class GroundNavigatorV2 : IGroundNavigator
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundNavigatorV2");

    /// <summary>Standard arrival threshold in nautical miles (~91 ft).</summary>
    private const double NodeArrivalThresholdNm = 0.015;

    /// <summary>Tight arrival threshold used on the last segment and before arcs (~1.8 ft).</summary>
    private const double FinalNodeArrivalThresholdNm = 0.0003;

    /// <summary>Distance at which the overshoot watchdog arms (~182 ft).</summary>
    private const double OvershootDetectionNm = 0.03;

    /// <summary>Speed floor below which the arc integrator refuses to advance (I7: no pivot-in-place).</summary>
    private const double ArcSpeedFloorKts = 0.1;

    /// <summary>
    /// Pure-pursuit look-ahead distance floor in feet, used on straight
    /// segments when the aircraft is nearly stationary. Prevents the
    /// look-ahead point from collapsing onto the aircraft's foot-of-
    /// perpendicular — which would leave steering undefined.
    /// </summary>
    private const double LookAheadFloorFt = 10.0;

    /// <summary>
    /// Pure-pursuit look-ahead distance cap in feet on straight segments.
    /// Keeps the look-ahead from anticipating the next turn too aggressively
    /// on long straights.
    /// </summary>
    private const double LookAheadCapFt = 50.0;

    public int TargetNodeId { get; private set; }
    public double TargetLat { get; private set; }
    public double TargetLon { get; private set; }
    public double PrevDistToTarget { get; private set; } = double.MaxValue;

    public NavTickDiag? LastTickDiag { get; private set; }
    public double MaxSpeedKts { get; set; }

    public void SetTargetNodeId(int nodeId) => TargetNodeId = nodeId;

    /// <summary>
    /// Override the target position to the painted hold-short bar offset (the owning phase calls this
    /// after <see cref="SetupSegment"/> when stopping short of an uncleared hold-short). The arrival
    /// threshold depends on this position, so it is an explicit seam rather than a free setter.
    /// </summary>
    public void OverrideTargetPosition(double lat, double lon)
    {
        TargetLat = lat;
        TargetLon = lon;
    }

    // --- Internal state ---

    /// <summary>The compiled primitive for the current segment. Null until first <see cref="SetupSegment"/>.</summary>
    private PathPrimitive? _currentPrimitive;

    /// <summary>Test-only accessor for the currently-executing primitive.</summary>
    internal PathPrimitive? CurrentPrimitive => _currentPrimitive;

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
    /// True when the immediately-following route segment is a
    /// <see cref="GroundArc"/> (fillet, junction, etc.). Used by
    /// <see cref="TickStraight"/> to switch to the tight arrival threshold —
    /// the loose 91 ft threshold would fire with the aircraft still a
    /// visible distance from the arc's entry node, and the next
    /// <see cref="TickArc"/> would then write position directly from arc
    /// state (invariant I2), producing a visible teleport. Set by
    /// <see cref="BuildSpeedConstraints"/> alongside <see cref="_nextSegmentBearing"/>.
    /// </summary>
    private bool _nextSegmentIsArc;

    /// <summary>
    /// Speed constraints from future segments, each as a tuple of:
    /// (path distance from current target, required speed at that point, node id).
    /// Computed during <see cref="SetupSegment"/> via forward-walk + backward-
    /// propagation, mirroring V1's approach but populated directly from
    /// <see cref="TaxiRouteSegment"/> iteration.
    /// </summary>
    private List<(double PathDistNm, double RequiredSpeedKts, int NodeId)> _speedConstraints = [];

    /// <summary>
    /// Heading-misalignment threshold (deg) above which a new segment gets a
    /// pre-segment slow-turn from the aircraft's current pose to the segment's
    /// start tangent. Without this, an arc primitive's first <see cref="TickArc"/>
    /// would write the arc tangent into <c>TrueHeading</c> directly, snapping
    /// a stationary aircraft (e.g. just after pushback) to the route start
    /// direction. The slow-turn lets the aircraft taxi forward at
    /// <see cref="CategoryPerformance.SlowTurnSpeedKts"/> while gradually
    /// rotating through a real arc geometry — no in-place pivot, no snap.
    ///
    /// <para>
    /// The threshold catches the OAK GA3 case (TWY801 at hdg 290°, segBrg 209°,
    /// delta 80.9°) where the pure-pursuit lookahead loop diverges: at low
    /// speed the lookahead point shifts faster than the aircraft can turn to
    /// chase it, producing an orbit. The same divergence happens mid-route
    /// when the synthesised slow-turn at a sharp corner fails to engage —
    /// either because the post-corner segment is too short for the clamped
    /// nose-wheel-min radius (OAK GA15 corner #472: 95° turn, availIn 8.4 ft,
    /// availOut 55 ft), or because the aircraft drifted off the planned
    /// tangent line by more than the strict-geometry tolerance. Entry-
    /// alignment is the safety net: any segment with a starting heading
    /// delta above the threshold gets a slow-turn at the segment-start node
    /// regardless of route position, and any segment with a smaller delta
    /// proceeds directly to the real primitive. Normal corners are below this
    /// threshold by construction (fillet arcs split sharp turns into
    /// multiple sub-segments each well under it).
    /// </para>
    /// </summary>
    private const double EntryAlignmentThresholdDeg = 45.0;

    /// <summary>
    /// When entry-alignment is active, this holds the segment's real primitive,
    /// to be swapped in once the alignment slow-turn completes. Null when no
    /// entry alignment is in progress.
    /// </summary>
    private PathPrimitive? _pendingSegmentPrimitive;

    public void SetupSegment(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        var seg = route.CurrentSegment;
        if (seg is null)
        {
            return;
        }

        var segmentPrimitive = PathPrimitiveBuilder.FromSegment(seg);

        var from = seg.Edge.FromNode;
        var to = seg.Edge.ToNode;
        TargetNodeId = seg.ToNodeId;
        TargetLat = to.Position.Lat;
        TargetLon = to.Position.Lon;
        _segmentFromLat = from.Position.Lat;
        _segmentFromLon = from.Position.Lon;
        PrevDistToTarget = double.MaxValue;

        // Entry alignment: if the aircraft heading is significantly off the
        // segment's first tangent, build a slow-turn from its current pose to
        // the segment's start direction and stash the real segment primitive
        // for swap-in when the slow-turn completes. Aircraft taxis forward
        // through the arc at SlowTurnSpeedKts (~3 kt) instead of snapping to
        // the segment's tangent at first tick.
        //
        // Gates (all required):
        //   - Heading delta > EntryAlignmentThresholdDeg: normal
        //     fillet-smoothed corners stay below this; only wrong-way starts,
        //     post-pushback U-turns, and mid-route corners where synthesis
        //     failed to engage produce deltas this large.
        //   - Segment length > 2 × alignment chord: short segments (e.g. M2
        //     entrance ~12 ft) can't absorb the displacement; the aircraft
        //     would end up well past the segment endpoint with pure-pursuit
        //     unable to recover. Defer alignment in those cases and accept
        //     the snap (rare and brief on those tiny segments).
        double segDepartureBearing = seg.Edge.DepartureBearing;
        double headingDelta = new TrueHeading(segDepartureBearing).AbsAngleTo(ctx.Aircraft.TrueHeading);
        double alignmentRadiusFt = CategoryPerformance.NoseWheelTurnRadiusFt(ctx.Category);
        double sweepRad = headingDelta * Math.PI / 180.0;
        double alignmentChordFt = 2.0 * alignmentRadiusFt * Math.Sin(sweepRad / 2.0);
        double segmentLengthFt = seg.Edge.DistanceNm * GeoMath.FeetPerNm;
        // Segment must accommodate the slow-turn chord plus a small pure-pursuit
        // recovery margin. The chord is the straight-line displacement during
        // the alignment arc; after the swap to the real segment primitive,
        // pure-pursuit handles the remainder. Factor 1.2 covers numerical drift
        // (segments slightly shorter than chord overshoot the target node);
        // anything tighter than that risks an orbit, anything looser starves
        // tight-ramp jet exits (e.g. OAK JSX1 RAMP, 38 ft segment vs C700's
        // 31.5 ft chord for a 78° turn).
        bool segmentLongEnough = segmentLengthFt > 1.2 * alignmentChordFt;

        if (headingDelta > EntryAlignmentThresholdDeg && segmentLongEnough)
        {
            var alignmentArc = PathPrimitiveBuilder.SlowTurn(
                fromLat: ctx.Aircraft.Position.Lat,
                fromLon: ctx.Aircraft.Position.Lon,
                fromHdgDeg: ctx.Aircraft.TrueHeading.Degrees,
                toHdgDeg: segDepartureBearing,
                radiusFt: CategoryPerformance.NoseWheelTurnRadiusFt(ctx.Category),
                maxSpeedKts: CategoryPerformance.SlowTurnSpeedKts,
                toNodeId: seg.FromNodeId
            );
            _pendingSegmentPrimitive = segmentPrimitive;
            _currentPrimitive = alignmentArc;
            _arcBearingFromCenterDeg = alignmentArc.StartBearingFromCenterDeg;
            _arcRemainingSweepDeg = alignmentArc.SweepDeg;

            Log.LogDebug(
                "[NavV2] SetupSegment seg={SegIdx}/{Total}: entry-align slow-turn "
                    + "(hdgFrom={From:F0} -> hdgTo={To:F0}, delta={Delta:F0}, r={R:F0}ft, sweep={Sweep:F0})",
                route.CurrentSegmentIndex,
                route.Segments.Count,
                ctx.Aircraft.TrueHeading.Degrees,
                segDepartureBearing,
                headingDelta,
                alignmentArc.RadiusFt,
                alignmentArc.SweepDeg
            );
        }
        else
        {
            _pendingSegmentPrimitive = null;
            _currentPrimitive = segmentPrimitive;

            if (_currentPrimitive is PathPrimitiveArc arcPrim)
            {
                _arcBearingFromCenterDeg = arcPrim.StartBearingFromCenterDeg;
                _arcRemainingSweepDeg = arcPrim.SweepDeg;
            }
            else if (_currentPrimitive is PathPrimitiveSlowTurn slowPrim)
            {
                _arcBearingFromCenterDeg = slowPrim.StartBearingFromCenterDeg;
                _arcRemainingSweepDeg = slowPrim.SweepDeg;
            }
            else
            {
                _arcRemainingSweepDeg = 0;
            }
        }

        BuildSpeedConstraints(route, ctx, isHoldShortCleared);

        Log.LogDebug(
            "[NavV2] SetupSegment seg={SegIdx}/{Total} target={NodeId} kind={Kind} dist={Dist:F4}nm "
                + "fromNode={FromId}@({FromLat:F6},{FromLon:F6}) toNode={ToId}@({ToLat:F6},{ToLon:F6}) "
                + "twy={Twy} segBrg={SegBrg:F1} acHdg={Hdg:F1} hdgDelta={HdgDelta:F1} entryAlign={EntryAlign} pendingSeg={Pending}",
            route.CurrentSegmentIndex,
            route.Segments.Count,
            TargetNodeId,
            _currentPrimitive?.Kind,
            seg.Edge.DistanceNm,
            seg.FromNodeId,
            from.Position.Lat,
            from.Position.Lon,
            seg.ToNodeId,
            to.Position.Lat,
            to.Position.Lon,
            seg.TaxiwayName,
            segDepartureBearing,
            ctx.Aircraft.TrueHeading.Degrees,
            headingDelta,
            _pendingSegmentPrimitive is not null,
            _pendingSegmentPrimitive?.Kind.ToString() ?? "none"
        );
    }

    public NavigatorResult Tick(PhaseContext ctx, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        var result = _currentPrimitive switch
        {
            PathPrimitiveStraight s => TickStraight(ctx, s, isLastSegment, isHoldShortCleared),
            PathPrimitiveArc a => TickArc(ctx, a, isLastSegment, isHoldShortCleared),
            PathPrimitiveSlowTurn t => TickSlowTurn(ctx, t),
            _ => NavigatorResult.ArrivedAtNode,
        };

        // When the entry-alignment slow-turn finishes, swap in the deferred
        // segment primitive and continue navigating in the same tick. The
        // synthetic arrival is internal — the route's own segment counter
        // hasn't advanced yet.
        if (result == NavigatorResult.ArrivedAtNode && _pendingSegmentPrimitive is not null)
        {
            var seg = _pendingSegmentPrimitive;
            _pendingSegmentPrimitive = null;
            _currentPrimitive = seg;
            if (seg is PathPrimitiveArc arcPrim)
            {
                _arcBearingFromCenterDeg = arcPrim.StartBearingFromCenterDeg;
                _arcRemainingSweepDeg = arcPrim.SweepDeg;
            }
            else if (seg is PathPrimitiveSlowTurn slowPrim)
            {
                _arcBearingFromCenterDeg = slowPrim.StartBearingFromCenterDeg;
                _arcRemainingSweepDeg = slowPrim.SweepDeg;
            }
            else
            {
                _arcRemainingSweepDeg = 0;
            }
            PrevDistToTarget = double.MaxValue;
            Log.LogDebug("[NavV2] Entry alignment complete; engaging real segment primitive {Kind}", seg.Kind);
            return NavigatorResult.Navigating;
        }

        return result;
    }

    private NavigatorResult TickStraight(PhaseContext ctx, PathPrimitiveStraight prim, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        double distNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        double edgeLengthNm = GeoMath.DistanceNm(new LatLon(_segmentFromLat, _segmentFromLon), new LatLon(TargetLat, TargetLon));

        // Tight arrival threshold when any of:
        //   - last segment of the route (always stop precisely),
        //   - the current target is a stop (_currentNodeRequiredSpeed == 0),
        //   - the next segment is an arc — TickArc writes position directly
        //     from arc-centre state at engagement (invariant I2), so the
        //     loose 91 ft threshold would teleport the aircraft up to 91 ft
        //     to the arc entry node on the first TickArc call. Tight
        //     threshold bounds the teleport to <2 ft (imperceptible).
        //   - the effective edge (segment start to current TargetLat/Lon) is
        //     shorter than 1.5× the loose threshold.
        // The last case handles the hold-short override — TaxiingPhase moves
        // the target from the graph to-node to a virtual HS position closer
        // to the aircraft, which makes the effective edge short even when
        // the underlying segment is long. Without this check, the loose
        // 91 ft arrival threshold can fire 10-80 ft short of a hold-short
        // stop, leaving the aircraft parked well behind the painted line.
        bool shortEdge = edgeLengthNm < NodeArrivalThresholdNm * 1.5;
        bool isStopTarget = _currentNodeRequiredSpeed == 0;
        double arrivalThresholdNm =
            (isLastSegment || shortEdge || isStopTarget || _nextSegmentIsArc) ? FinalNodeArrivalThresholdNm : NodeArrivalThresholdNm;

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

        // Pure-pursuit steering on straight segments: steer toward a look-ahead
        // point on the segment line, not toward the target node directly.
        //
        // Why: if the aircraft is off-segment (e.g. spawned at Coordinates
        // slightly off a taxiway, or nudged by a prior corner), bearing-to-
        // target cuts diagonally across terrain rather than re-acquiring the
        // segment line. The look-ahead projects the aircraft's foot-of-
        // perpendicular forward along the segment, so the steering target
        // sits on the segment — convergence onto the line is first-class
        // instead of implicit-on-arrival.
        //
        // Fallback: a zero-length segment means we have nothing to project
        // onto. Steer at the target directly (matches pre-change behaviour).
        double bearingToSteerDeg;
        if (edgeLengthNm < 1e-9)
        {
            bearingToSteerDeg = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        }
        else
        {
            var (_, alongNm, _) = GeoMath.FootOfPerpendicular(
                ctx.Aircraft.Position,
                new LatLon(_segmentFromLat, _segmentFromLon),
                new LatLon(TargetLat, TargetLon)
            );

            double speedFtPerSec = ctx.Aircraft.IndicatedAirspeed * GeoMath.FeetPerNm / 3600.0;
            double lookAheadFt = Math.Clamp(2.0 * speedFtPerSec * ctx.DeltaSeconds, LookAheadFloorFt, LookAheadCapFt);
            double lookAheadNm = lookAheadFt / GeoMath.FeetPerNm;
            double lookAheadAlongNm = Math.Min(edgeLengthNm, alongNm + lookAheadNm);

            // Look-ahead point = segment start projected forward by
            // lookAheadAlongNm along the segment bearing. Clamping to the
            // target when we'd run past preserves arrival detection semantics
            // and keeps bearingToSteerDeg identical to bearing-to-target in
            // the last look-ahead window.
            double segBearingDeg = GeoMath.BearingTo(new LatLon(_segmentFromLat, _segmentFromLon), new LatLon(TargetLat, TargetLon));
            if (lookAheadAlongNm >= edgeLengthNm - 1e-9)
            {
                bearingToSteerDeg = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
            }
            else
            {
                var (lookLat, lookLon) = GeoMath.ProjectPointRaw(new LatLon(_segmentFromLat, _segmentFromLon), segBearingDeg, lookAheadAlongNm);
                bearingToSteerDeg = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(lookLat, lookLon));
            }
        }

        // Pre-turn blend: in the last ~50 ft of a straight that precedes a
        // gentle turn, start blending the steer target toward the next
        // segment's departure bearing. Scaled by turn angle — full blend at
        // ≤30°, ramping linearly to zero by 90°, so sharp turns get little or
        // no blend (they are handled by synthesis or entry alignment instead)
        // and the tail isn't yanked early.
        if (_nextSegmentBearing is { } nextBearingDeg)
        {
            double turnAngle = GeoMath.AbsBearingDifference(bearingToSteerDeg, nextBearingDeg);
            double angleScale = Math.Clamp(1.0 - ((turnAngle - 30.0) / 60.0), 0.0, 1.0);
            const double preturnDistNm = 0.008; // ~50 ft
            if (distNm < preturnDistNm && angleScale > 0.01)
            {
                double blend = (1.0 - distNm / preturnDistNm) * angleScale;
                bearingToSteerDeg = GeoMath.BlendBearings(bearingToSteerDeg, nextBearingDeg, blend);
            }
        }

        double maxTurnDeg = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearingToSteerDeg, maxTurnDeg);

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
        UpdateDiag(ctx, distNm, bearingToSteerDeg, targetSpeed, onArc: false);

        if (Log.IsEnabled(LogLevel.Debug))
        {
            double hdgErr = GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, bearingToSteerDeg);
            double segBearingDeg = GeoMath.BearingTo(new LatLon(_segmentFromLat, _segmentFromLon), new LatLon(TargetLat, TargetLon));
            Log.LogDebug(
                "[NavV2] TickStraight cs={Callsign} seg→{Target} pos=({Lat:F6},{Lon:F6}) hdg={Hdg:F1} steer={Steer:F1} hdgErr={HdgErr:F1} "
                    + "distFt={DistFt:F1} edgeFt={EdgeFt:F1} segBrg={SegBrg:F1} ias={Ias:F1} tgt={Tgt:F1} "
                    + "thrArrNm={ThrArr:F4} preTurnBlend={Preturn} stalledThr={Stalled} nextBrg={NextBrg}",
                ctx.Aircraft.Callsign,
                TargetNodeId,
                ctx.Aircraft.Position.Lat,
                ctx.Aircraft.Position.Lon,
                ctx.Aircraft.TrueHeading.Degrees,
                bearingToSteerDeg,
                hdgErr,
                distNm * GeoMath.FeetPerNm,
                edgeLengthNm * GeoMath.FeetPerNm,
                segBearingDeg,
                ctx.Aircraft.IndicatedAirspeed,
                targetSpeed,
                arrivalThresholdNm,
                _nextSegmentBearing.HasValue,
                stalledAtThreshold,
                _nextSegmentBearing?.ToString("F1") ?? "(none)"
            );
        }

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
        var (lat, lon) = GeoMath.ProjectPoint(new LatLon(prim.CenterLat, prim.CenterLon), new TrueHeading(_arcBearingFromCenterDeg), prim.RadiusNm);
        double tangentDeg = CurrentArcTangentDeg(prim);
        ctx.Aircraft.Position = new LatLon(lat, lon);
        ctx.Aircraft.TrueHeading = new TrueHeading(tangentDeg);

        // Mirror into targets so physics does not fight the closed-form state.
        ctx.Targets.TargetTrueHeading = new TrueHeading(tangentDeg);
        double arcTargetSpeed = ComputeTargetSpeed(ctx, ArcRemainingLengthNm(prim), isHoldShortCleared);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, arcTargetSpeed);
        AdjustSpeed(ctx, ctx.Targets.TargetSpeed ?? arcTargetSpeed);

        double distToNode = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        PrevDistToTarget = distToNode;
        UpdateDiag(ctx, distToNode, tangentDeg, arcTargetSpeed, onArc: true);

        Log.LogDebug(
            "[NavV2] TickArc cs={Callsign} seg→{Target} pos=({Lat:F6},{Lon:F6}) tan={Tan:F1} bearingFromCenter={BFC:F1} "
                + "remainingSweep={Rem:F2}° r={R:F0}ft right={Right} ds={Ds:F2}ft v={V:F1}kt distFt={Dist:F1}",
            ctx.Aircraft.Callsign,
            TargetNodeId,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            tangentDeg,
            _arcBearingFromCenterDeg,
            _arcRemainingSweepDeg,
            prim.RadiusFt,
            prim.RightTurn,
            dsFt,
            vKts,
            distToNode * GeoMath.FeetPerNm
        );

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

    private double CurrentSlowTurnTangentDeg(PathPrimitiveSlowTurn prim)
    {
        double tangent = prim.RightTurn ? _arcBearingFromCenterDeg + 90.0 : _arcBearingFromCenterDeg - 90.0;
        return ((tangent % 360.0) + 360.0) % 360.0;
    }

    private NavigatorResult TickSlowTurn(PhaseContext ctx, PathPrimitiveSlowTurn prim)
    {
        // I7 speed floor — aircraft must be moving forward before the arc can advance.
        // Target speed is held at the primitive's cap so physics re-accelerates us.
        double vKts = ctx.Aircraft.IndicatedAirspeed;
        double cappedTarget = ClampBySpeedLimit(ctx, prim.MaxSpeedKts);
        if (vKts < ArcSpeedFloorKts)
        {
            double currentTangent = CurrentSlowTurnTangentDeg(prim);
            ctx.Targets.TargetTrueHeading = new TrueHeading(currentTangent);
            ctx.Targets.TargetSpeed = cappedTarget;
            AdjustSpeed(ctx, cappedTarget);
            return NavigatorResult.Navigating;
        }

        // Advance the arc by ds = v·dt, clamped to remaining sweep.
        double vFtPerSec = vKts * GeoMath.FeetPerNm / 3600.0;
        double dsFt = vFtPerSec * ctx.DeltaSeconds;
        double dAngleRad = dsFt / prim.RadiusFt;
        double dAngleDeg = dAngleRad * (180.0 / Math.PI);
        dAngleDeg = Math.Min(dAngleDeg, _arcRemainingSweepDeg);

        double signed = prim.RightTurn ? +dAngleDeg : -dAngleDeg;
        _arcBearingFromCenterDeg = (((_arcBearingFromCenterDeg + signed) % 360.0) + 360.0) % 360.0;
        _arcRemainingSweepDeg = Math.Max(0.0, _arcRemainingSweepDeg - dAngleDeg);

        // Write position + heading directly from playback state (invariant I2).
        var (lat, lon) = GeoMath.ProjectPoint(new LatLon(prim.CenterLat, prim.CenterLon), new TrueHeading(_arcBearingFromCenterDeg), prim.RadiusNm);
        double tangentDeg = CurrentSlowTurnTangentDeg(prim);
        ctx.Aircraft.Position = new LatLon(lat, lon);
        ctx.Aircraft.TrueHeading = new TrueHeading(tangentDeg);

        // Speed policy: cap to the primitive's own MaxSpeedKts — no
        // ComputeTargetSpeed braking-curve logic because SlowTurn primitives
        // don't participate in the multi-segment speed constraint system.
        ctx.Targets.TargetTrueHeading = new TrueHeading(tangentDeg);
        ctx.Targets.TargetSpeed = cappedTarget;
        AdjustSpeed(ctx, cappedTarget);

        double distToNode = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        PrevDistToTarget = distToNode;
        UpdateDiag(ctx, distToNode, tangentDeg, cappedTarget, onArc: true);

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
            : GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
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
            _nextSegmentIsArc = false;
        }
        else if (!isLastSegment)
        {
            int nextIdx = route.CurrentSegmentIndex + 1;
            var nextSeg = route.Segments[nextIdx];
            double turnAngle = SingleCornerTurnAngle(route, route.CurrentSegmentIndex);
            _currentNodeRequiredSpeed = CategoryPerformance.CornerSpeedForAngle(ctx.Category, turnAngle);
            _nextSegmentBearing = nextSeg.Edge.DepartureBearing;
            _nextSegmentIsArc = nextSeg.Edge.Edge is GroundArc;
        }
        else
        {
            _currentNodeRequiredSpeed = 0;
            _nextSegmentBearing = null;
            _nextSegmentIsArc = false;
        }

        // Forward walk: collect future speed constraints.
        double cumulativeDistNm = 0;
        for (int i = route.CurrentSegmentIndex + 1; i < route.Segments.Count; i++)
        {
            var futureSeg = route.Segments[i];
            cumulativeDistNm += futureSeg.Edge.DistanceNm;

            if (futureSeg.Edge.Edge is GroundArc futureArc)
            {
                double arcMaxSpeed = futureArc.MaxSafeSpeedKts(ctx.Category);
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
                double futureTurnAngle = SingleCornerTurnAngle(route, i);
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
        ctx.Aircraft.Ground.SpeedLimit is { } limit ? Math.Min(requested, limit) : requested;

    /// <summary>
    /// Accelerate/decelerate toward <paramref name="targetSpeed"/> bounded by
    /// the category's taxi accel/decel rates. Mirrors V1's AdjustSpeed so
    /// physics behaviour at the straight-segment level matches.
    /// </summary>
    private static void AdjustSpeed(PhaseContext ctx, double targetSpeed)
    {
        if (ctx.Aircraft.Ground.SpeedLimit is { } limit)
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

    /// <summary>
    /// Turn angle (deg) at the node where <paramref name="turnNodeSegIdx"/> ends — the single corner
    /// between this segment's arrival bearing and the next segment's departure bearing. V2 geometry emits
    /// proper arcs for real corners, so there is no fillet chord-chain to aggregate: the corner the
    /// aircraft actually turns is the one between the two adjacent segments. (This replaces V1's
    /// forward-window sum, a Legacy-fillet compensation for ArcSplit chord chains that V2 does not produce.)
    /// </summary>
    private static double SingleCornerTurnAngle(TaxiRoute route, int turnNodeSegIdx)
    {
        int nextIdx = turnNodeSegIdx + 1;
        if (nextIdx >= route.Segments.Count)
        {
            return 0;
        }

        var thisSeg = route.Segments[turnNodeSegIdx];
        var nextSeg = route.Segments[nextIdx];
        return GeoMath.AbsBearingDifference(thisSeg.Edge.ArrivalBearing, nextSeg.Edge.DepartureBearing);
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
        ctx.Aircraft.Ground.LastNavDiag = diag;
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

    public static GroundNavigatorV2 FromSnapshot(GroundNavigatorDto dto) =>
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
