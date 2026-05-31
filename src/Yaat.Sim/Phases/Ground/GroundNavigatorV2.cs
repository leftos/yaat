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
/// state. Speed comes from corner-speed limits — angle-based plus a turn-rate-feasibility cap that slows
/// the aircraft into bends too tight to track at the angle-only speed (see <see cref="CornerSpeed"/>) —
/// backward-propagated by kinematic braking, and capped by the lateral-accel arc speed model.
///
/// <para>
/// Built for clean V2 geometry, this deliberately drops the V1 chord-chain compensations that worked around
/// Legacy ArcSplit artifacts (short-segment cluster detection, chord-chain aggregate-turn, the orbit-stall
/// backstop): V2 fillets emit a single arc per real taxiway corner, so those do not apply. But corners
/// <em>tighter</em> than the nose-wheel radius — ramp/apron bends the fillet generator cannot widen, which
/// stay sharp vertices between short straight segments — still cannot be tracked by pure-pursuit at any
/// allowed speed (the orbit radius v/ω exceeds the segment scale). Those are rounded by the entry-alignment
/// slow-turn, which fires for <em>any</em> corner past <see cref="EntryAlignmentThresholdDeg"/> — a
/// misaligned parking-out start or a tight mid-route ramp bend — tracing a nose-wheel-radius arc at walking
/// pace. This is geometric corner-rounding, not the dropped Legacy synthesis.
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

    /// <summary>
    /// Cross-track offset (feet) above which the aircraft is "not established"
    /// on the segment centerline and must re-acquire it at
    /// <see cref="ReacquireSpeedKts"/> before accelerating. See the
    /// establish-straight gate in <see cref="TickStraight"/>.
    /// </summary>
    private const double ReacquireOffsetFt = 4.0;

    /// <summary>
    /// Speed cap (knots) while re-acquiring the centerline from a cross-track
    /// offset &gt; <see cref="ReacquireOffsetFt"/>. Holds a slow taxi so
    /// pure-pursuit converges onto the line without the over-speed overshoot
    /// (Boeing FCTM "roll straight, then add thrust"). Tangent-rounded corners
    /// exit on-line (offset ≈ 0), so this never fires for them; it governs the
    /// from-rest spot-exit pivot, which has no incoming leg to round tangent.
    /// </summary>
    private const double ReacquireSpeedKts = 5.0;

    public int TargetNodeId { get; private set; }
    public double TargetLat { get; private set; }
    public double TargetLon { get; private set; }
    public double PrevDistToTarget { get; private set; } = double.MaxValue;

    public NavTickDiag? LastTickDiag { get; private set; }
    public double MaxSpeedKts { get; set; }

    /// <summary>Speed floor (kts); see <see cref="IGroundNavigator.MinSpeedKts"/>. 0 = no floor.</summary>
    public double MinSpeedKts { get; set; }

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
    /// direction. The slow-turn lets the aircraft taxi forward at the
    /// turn-rate-limited speed for the nose-wheel radius
    /// (<see cref="CategoryPerformance.TurnRateLimitedSpeedKts"/>, ~5 kt for a jet) while
    /// gradually rotating through a real arc geometry — no in-place pivot, no snap.
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

        // Corner rounding: when the aircraft heading is significantly off the segment's first tangent,
        // build a slow-turn from its current pose to the segment's start direction and stash the real
        // segment primitive for swap-in when the slow-turn completes. The aircraft taxis forward through
        // the arc at the turn-rate-limited speed for the nose-wheel radius (TurnRateLimitedSpeedKts —
        // v = ω·r, ~5 kt for a jet), rounding the corner at the nose-wheel radius instead of snapping to
        // the tangent.
        //
        // Fires for any corner sharper than EntryAlignmentThresholdDeg regardless of segment length. A bend
        // tighter than the nose-wheel radius — common in ramp clusters the fillet generator cannot widen —
        // cannot be tracked by pure-pursuit at any allowed speed: the orbit radius v/ω exceeds the
        // short-segment scale even at the SlowTurnSpeedKts floor, so the aircraft would circle the corner
        // node forever. It MUST be rounded. The speed planner (see CornerSpeed / BuildSpeedConstraints) has
        // already slowed the aircraft to the corner speed before it arrives, so the rounding begins from a
        // near-crawl and any overshoot of a very short segment is small and recovered by the normal
        // arrival/overshoot advance on the (near-collinear) segments that follow.
        double segDepartureBearing = seg.Edge.DepartureBearing;
        double headingDelta = new TrueHeading(segDepartureBearing).AbsAngleTo(ctx.Aircraft.TrueHeading);

        if (headingDelta > EntryAlignmentThresholdDeg)
        {
            double roundingRadiusFt = CategoryPerformance.NoseWheelTurnRadiusFt(ctx.Category);
            var alignmentArc = PathPrimitiveBuilder.SlowTurn(
                fromLat: ctx.Aircraft.Position.Lat,
                fromLon: ctx.Aircraft.Position.Lon,
                fromHdgDeg: ctx.Aircraft.TrueHeading.Degrees,
                toHdgDeg: segDepartureBearing,
                radiusFt: roundingRadiusFt,
                // Round at the fastest speed the gear-limited turn rate can track this radius (v = ω·r),
                // not a flat 3 kt creep — a jet rounds a sharp corner at its 25 ft nose-wheel radius near
                // ~5 kt (aviation-reviewed). Floored at SlowTurnSpeedKts for degenerate radii.
                maxSpeedKts: CategoryPerformance.TurnRateLimitedSpeedKts(ctx.Category, roundingRadiusFt),
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

    /// <summary>
    /// Arrival threshold (nm) for a straight segment. When a sharp turn onto the next straight leg is
    /// coming up and the leg is long enough to round within, applies tangent corner-rounding — arrive
    /// at the tangent point T = r·tan(δ/2) (r = nose-wheel radius, δ = corner deflection), capped at
    /// 0.45·leg so rounding can't start before the midpoint and floored at the final-node threshold —
    /// and reports <paramref name="roundingActive"/> true. On a leg too short to round above that floor
    /// (0.45·leg ≤ <see cref="FinalNodeArrivalThresholdNm"/>), there is no room: it falls back to the
    /// standard arrival threshold and reports <paramref name="roundingActive"/> false, never clamping
    /// with an inverted [min, max] range (which would throw). Pure — extracted for unit testing.
    /// </summary>
    internal static double StraightArrivalThresholdNm(
        double cornerTurnDeg,
        double edgeLengthNm,
        AircraftCategory category,
        bool isLastSegment,
        bool isStopTarget,
        bool shortEdge,
        bool nextSegmentIsArc,
        out bool roundingActive
    )
    {
        double maxRoundingNm = 0.45 * edgeLengthNm;
        roundingActive = !isLastSegment && !isStopTarget && cornerTurnDeg > EntryAlignmentThresholdDeg && maxRoundingNm > FinalNodeArrivalThresholdNm;

        if (roundingActive)
        {
            double rFt = CategoryPerformance.NoseWheelTurnRadiusFt(category);
            double tFt = rFt * Math.Tan(cornerTurnDeg * 0.5 * Math.PI / 180.0);
            return Math.Clamp(tFt / GeoMath.FeetPerNm, FinalNodeArrivalThresholdNm, maxRoundingNm);
        }

        return (isLastSegment || shortEdge || isStopTarget || nextSegmentIsArc) ? FinalNodeArrivalThresholdNm : NodeArrivalThresholdNm;
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

        // Tangent corner-rounding: when a SHARP turn onto the next (straight)
        // segment is coming up, arrive at the tangent point T = r·tan(δ/2)
        // before the vertex (r = nose-wheel radius, δ = corner deflection)
        // instead of at the vertex. The next segment's entry-alignment slow-turn
        // then anchors at that tangent point, so its nose-wheel-radius arc is
        // tangent to BOTH legs and exits ON the outgoing centerline (aligned, no
        // lateral offset) — eliminating the pure-pursuit re-acquisition that
        // otherwise overshoots ~40° per corner. This is judgmental oversteer /
        // corner-cutting (aviation-reviewed: T is the tangent length of a simple
        // circular curve, AC 150/5300-13). Skipped when the next segment is
        // itself an arc (the arc rounds the corner) or the target is a stop /
        // route end. T is clamped into the current leg so the arc start can't
        // precede the segment.
        double cornerTurnDeg = (!_nextSegmentIsArc && _nextSegmentBearing is { } nb) ? GeoMath.AbsBearingDifference(prim.BearingDeg, nb) : 0.0;
        double arrivalThresholdNm = StraightArrivalThresholdNm(
            cornerTurnDeg,
            edgeLengthNm,
            ctx.Category,
            isLastSegment,
            isStopTarget,
            shortEdge,
            _nextSegmentIsArc,
            out bool sharpCornerAhead
        );

        bool overshot = distNm > PrevDistToTarget && PrevDistToTarget < OvershootDetectionNm;
        bool stalledAtThreshold = ctx.Aircraft.GroundSpeed < 0.5 && distNm < arrivalThresholdNm + 0.001;
        bool straightArrived = distNm <= arrivalThresholdNm;

        if (straightArrived || overshot || stalledAtThreshold)
        {
            // Corrective nudge toward next segment bearing, bounded by turn rate.
            // Skipped for a sharp upcoming corner: the entry-alignment slow-turn
            // built next must start at the incoming heading to round tangent.
            if (!sharpCornerAhead && _nextSegmentBearing is { } nextBrg)
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
        double crossTrackOffsetFt = 0.0;
        if (edgeLengthNm < 1e-9)
        {
            bearingToSteerDeg = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        }
        else
        {
            var (foot, alongNm, _) = GeoMath.FootOfPerpendicular(
                ctx.Aircraft.Position,
                new LatLon(_segmentFromLat, _segmentFromLon),
                new LatLon(TargetLat, TargetLon)
            );
            crossTrackOffsetFt = GeoMath.DistanceNm(ctx.Aircraft.Position, foot) * GeoMath.FeetPerNm;

            // Look-ahead scales with speed AND with the current cross-track
            // offset: re-acquiring a large offset (e.g. the from-rest spot-exit
            // pivot, which finishes ~30 ft off the line) with the short
            // speed-only floor steers too hard at the near point and overshoots
            // the line. Reaching toward a point ~1.5× the offset ahead bounds
            // the re-acquisition steer angle (atan(offset / lookAhead)) and
            // converges asymptotically. No effect once on-line (offset ≈ 0).
            double speedFtPerSec = ctx.Aircraft.IndicatedAirspeed * GeoMath.FeetPerNm / 3600.0;
            double lookAheadFt = Math.Clamp(
                Math.Max(2.0 * speedFtPerSec * ctx.DeltaSeconds, 1.5 * crossTrackOffsetFt),
                LookAheadFloorFt,
                LookAheadCapFt
            );
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

        // Establish-straight gate (Boeing FCTM "roll straight, then add thrust";
        // AIM 4-3-19.4 positive control): while displaced off the segment
        // centerline, hold a slow re-acquire speed instead of accelerating.
        // Pure-pursuit at taxi speed onto an off-line segment overshoots the
        // line and swings back (~40°+ of wasted rotation). Tangent-rounded
        // corners exit on-line (offset ≈ 0) so this is a no-op there; it bites
        // the from-rest spot-exit pivot, which has no incoming leg to round
        // tangent and so unavoidably finishes off the outgoing centerline.
        if (crossTrackOffsetFt > ReacquireOffsetFt)
        {
            targetSpeed = Math.Min(targetSpeed, ReacquireSpeedKts);
        }

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
                    + "distFt={DistFt:F1} edgeFt={EdgeFt:F1} segBrg={SegBrg:F1} ias={Ias:F1} tgt={Tgt:F1} xTrkFt={XTrk:F1} extLimit={ExtLimit} "
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
                crossTrackOffsetFt,
                ctx.Aircraft.Ground.SpeedLimit?.ToString("F1") ?? "(none)",
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
            _currentNodeRequiredSpeed = CornerSpeed(ctx.Category, turnAngle, seg.Edge.DistanceNm, nextSeg.Edge.DistanceNm);
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
                reqSpeed = CornerSpeed(ctx.Category, futureTurnAngle, futureSeg.Edge.DistanceNm, route.Segments[nextNextIdx].Edge.DistanceNm);
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
    /// Apply the <see cref="MinSpeedKts"/> floor then clamp by the conflict/airport
    /// <see cref="AircraftState.GroundSpeedLimit"/> ceiling. The ceiling always wins (a conflict-imposed
    /// stop overrides the crossing floor); the floor only lifts the requested speed when no ceiling binds.
    /// </summary>
    private double ClampBySpeedLimit(PhaseContext ctx, double requested)
    {
        double floored = Math.Max(requested, MinSpeedKts);
        return ctx.Aircraft.Ground.SpeedLimit is { } limit ? Math.Min(floored, limit) : floored;
    }

    /// <summary>
    /// Accelerate/decelerate toward <paramref name="targetSpeed"/> bounded by
    /// the category's taxi accel/decel rates. Mirrors V1's AdjustSpeed so
    /// physics behaviour at the straight-segment level matches.
    /// </summary>
    private void AdjustSpeed(PhaseContext ctx, double targetSpeed)
    {
        targetSpeed = Math.Max(targetSpeed, MinSpeedKts);
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
    /// <summary>
    /// Required ground speed at a corner of <paramref name="turnAngleDeg"/> whose heading change must be
    /// executed across the shorter of the two segments meeting at the corner (<paramref name="intoNm"/>,
    /// <paramref name="outNm"/>). Combines the angle-based comfort cap
    /// (<see cref="CategoryPerformance.CornerSpeedForAngle"/>) with a turn-rate-feasibility cap: rotating
    /// <c>θ</c>° across distance <c>L</c> at the ground turn rate <c>ω</c> demands <c>θ·v/L ≤ ω</c>, i.e.
    /// <c>v ≤ ω·L/θ</c>. The <c>½·L</c> reflects the rounding being centred on the vertex (the aircraft must
    /// be mid-turn at the node, not just beginning it). Backward-propagated by the caller's braking curve,
    /// this slows the aircraft <em>before</em> a tight bend — a pilot reads the ramp ahead and eases off —
    /// instead of barrelling in.
    ///
    /// <para>
    /// Without the feasibility term a sharp bend over a very short ramp segment keeps the angle-only speed
    /// (~18 kt for a 52° jet corner); the aircraft covers the segment in a fraction of a tick, cannot rotate
    /// far enough to track the turn, overshoots the corner node, and orbits a target now behind it. Floored
    /// at <see cref="CategoryPerformance.SlowTurnSpeedKts"/>: anything tighter than walking pace is rounded
    /// by the entry-alignment slow-turn at the nose-wheel radius, not commanded slower here. The
    /// lateral-accel comfort cap (§4.4a) is non-binding for these straight-segment vertices — it governs
    /// genuine arc primitives, which carry their own <see cref="GroundArc.MaxSafeSpeedKts"/>. Validated by
    /// aviation-sim-expert (turn-rate model, ½ factor, 3 kt floor, θ&gt;30° engagement).
    /// </para>
    /// </summary>
    private static double CornerSpeed(AircraftCategory cat, double turnAngleDeg, double intoNm, double outNm)
    {
        double angleCap = CategoryPerformance.CornerSpeedForAngle(cat, turnAngleDeg);

        // Below the corner knee the turn is gentle (angle cap holds at full taxi speed) and dividing by a
        // near-zero angle would blow up — leave near-collinear ramp kinks at taxi speed.
        if (turnAngleDeg <= 30.0)
        {
            return angleCap;
        }

        double lFt = Math.Min(intoNm, outNm) * GeoMath.FeetPerNm;
        double feasibleFtPerSec = CategoryPerformance.GroundTurnRate(cat) * (0.5 * lFt) / turnAngleDeg;
        double feasibleKts = feasibleFtPerSec * 3600.0 / GeoMath.FeetPerNm;
        return Math.Max(Math.Min(angleCap, feasibleKts), CategoryPerformance.SlowTurnSpeedKts);
    }

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
