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
/// <c>Yaat.LayoutInspector --tick-table</c> for post-hoc analysis.
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
    public double TargetLat { get; set; }
    public double TargetLon { get; set; }
    public double PrevDistToTarget { get; set; } = double.MaxValue;
    public NavTickDiag? LastTickDiag { get; private set; }
    public double MaxSpeedKts { get; set; }

    public void SetTargetNodeId(int nodeId) => TargetNodeId = nodeId;

    // --- Internal state ---

    /// <summary>The compiled primitive for the current segment. Null until first <see cref="SetupSegment"/>.</summary>
    private PathPrimitive? _currentPrimitive;

    /// <summary>Test-only accessor for the currently-executing primitive.</summary>
    internal PathPrimitive? CurrentPrimitive => _currentPrimitive;

    /// <summary>Test-only accessor for the currently-active synthesis plan (non-null while the aircraft is approaching a planned tangent-entry point).</summary>
    internal PlannedSynthesis? ActiveSynthesisPlan => _plannedSynthesis;

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
    /// Minimum turn angle (deg) below which slow-turn synthesis is skipped.
    /// Under this threshold the pure-pursuit blend on straight segments
    /// handles realignment cleanly without a dedicated primitive.
    /// </summary>
    private const double SlowTurnSynthesisMinAngleDeg = 45.0;

    /// <summary>
    /// Synthesise a slow-turn when the natural turn arc length at
    /// <see cref="CategoryPerformance.CornerSpeedForAngle"/> exceeds this
    /// fraction of the upcoming straight segment's length. Factor 0.5 = the
    /// natural turn would consume more than half the next segment — too tight
    /// for the normal pre-turn blend.
    /// </summary>
    private const double SlowTurnSynthesisSegmentFraction = 0.5;

    /// <summary>
    /// Forward lookahead cap (feet) when scanning for upcoming corners that
    /// need synthesis. Corners past this distance will be planned in a later
    /// <see cref="SetupSegment"/> call. Prevents runaway scans on long routes.
    /// </summary>
    private const double SynthesisLookaheadCapFt = 500.0;

    /// <summary>
    /// Maximum bearing difference (deg) for two consecutive straight segments
    /// to be treated as collinear when walking backward from a corner to find
    /// the tangent-entry segment. Small jogs below this threshold are treated
    /// as effectively straight so the backward walk can cross them.
    /// </summary>
    private const double CollinearBearingToleranceDeg = 5.0;

    /// <summary>
    /// Strict-geometry tolerance (feet): when the plan's trigger fires, the
    /// aircraft's position must be within this distance of the planned tangent
    /// entry point on the segment line. Otherwise synthesis is skipped and a
    /// warning is logged — the straight's pure-pursuit steering carries on
    /// and the aircraft cuts the corner without a dedicated primitive.
    /// </summary>
    private const double TangentEntryStrictToleranceFt = 3.0;

    /// <summary>
    /// Cached plan for an upcoming synthesised slow-turn: when and where in
    /// the current segment to engage the arc, plus the pre-built
    /// <see cref="PathPrimitiveSlowTurn"/> to swap in. Populated by
    /// <see cref="PlanSynthesisLookahead"/> in <see cref="SetupSegment"/>;
    /// consumed by <see cref="TickStraight"/> when the aircraft reaches the
    /// trigger distance; cleared when the plan fires or when a new segment
    /// is set up. Null when no synthesis is planned for the current segment.
    /// </summary>
    private PlannedSynthesis? _plannedSynthesis;

    /// <summary>
    /// Plan record describing a pre-computed synthesised slow-turn to be
    /// engaged mid-segment. All positions are absolute; geometry is fixed
    /// at plan time and does not adapt to the aircraft's actual pose.
    /// </summary>
    internal readonly record struct PlannedSynthesis(
        /// <summary>Distance along the current segment (feet from segment start) at which the arc engages. Typically `segmentLengthFt - tangentInsetFt`.</summary>
        double TriggerDistFromSegStartFt,
        /// <summary>Pre-built slow-turn primitive (entry on segment line, exit aligned with post-corner segment bearing).</summary>
        PathPrimitiveSlowTurn SlowTurn,
        /// <summary>Tangent-entry latitude (for strict-geometry verification at trigger time).</summary>
        double TangentEntryLat,
        /// <summary>Tangent-entry longitude (for strict-geometry verification at trigger time).</summary>
        double TangentEntryLon,
        /// <summary>Corner node id (logged on synthesis engagement).</summary>
        int CornerNodeId,
        /// <summary>Tangent inset in nautical miles — how far before the corner the arc engages. Used by <c>ComputeTargetSpeed</c> for the pre-trigger brake curve.</summary>
        double TangentInsetNm,
        /// <summary>Chosen target speed (knots) at the trigger point, derived from the chosen radius via <c>v = r × ω</c> and clamped to <c>[SlowTurnSpeedKts, CornerSpeedForAngle(θ)]</c>. Used by the pre-trigger brake constraint so the aircraft decelerates to the correct entry speed, not the global <c>SlowTurnSpeedKts</c> floor.</summary>
        double ChosenSpeedKts
    );

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
        TargetLat = to.Position.Lat;
        TargetLon = to.Position.Lon;
        _segmentFromLat = from.Position.Lat;
        _segmentFromLon = from.Position.Lon;
        PrevDistToTarget = double.MaxValue;

        // Slow-turn synthesis lookahead: when the pathfinder emits two
        // consecutive straights at a sharp corner (e.g. the same-taxiway apex
        // at SFO A1 node 507) and the natural turn radius at corner speed
        // exceeds what the post-corner segment can accommodate, plan a
        // tangent-entry arc that engages part-way through the incoming
        // straight so the aircraft traces a proper fillet-style turn
        // through the corner instead of orbiting inside it at node-arrival
        // time. Plan lives in `_plannedSynthesis` until `TickStraight`
        // triggers at the tangent-entry distance.
        _plannedSynthesis = PlanSynthesisLookahead(route, ctx, seg);

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

    /// <summary>
    /// Inject a primitive directly, bypassing the <see cref="TaxiRoute"/>
    /// machinery. Used by phases that synthesise primitives programmatically
    /// (e.g. <c>LineUpPhase</c> building <see cref="PathPrimitiveSlowTurn"/>
    /// primitives for its pivot states). Does not build future-segment speed
    /// constraints — callers driving synthesised primitives are responsible
    /// for their own speed policy (which for <see cref="PathPrimitiveSlowTurn"/>
    /// is the primitive's own <see cref="PathPrimitiveSlowTurn.MaxSpeedKts"/>).
    /// </summary>
    public void SetupPrimitive(
        PathPrimitive primitive,
        double fromLat,
        double fromLon,
        double targetLat,
        double targetLon,
        double? nextSegmentBearingDeg
    )
    {
        _plannedSynthesis = null;
        _currentPrimitive = primitive;
        TargetNodeId = primitive.ToNodeId;
        TargetLat = targetLat;
        TargetLon = targetLon;
        _segmentFromLat = fromLat;
        _segmentFromLon = fromLon;
        PrevDistToTarget = double.MaxValue;

        if (primitive is PathPrimitiveArc arcPrim)
        {
            _arcBearingFromCenterDeg = arcPrim.StartBearingFromCenterDeg;
            _arcRemainingSweepDeg = arcPrim.SweepDeg;
        }
        else if (primitive is PathPrimitiveSlowTurn slowPrim)
        {
            _arcBearingFromCenterDeg = slowPrim.StartBearingFromCenterDeg;
            _arcRemainingSweepDeg = slowPrim.SweepDeg;
        }
        else
        {
            _arcRemainingSweepDeg = 0;
        }

        _speedConstraints.Clear();
        _currentNodeRequiredSpeed = 0;
        _nextSegmentBearing = nextSegmentBearingDeg;
        _nextSegmentIsArc = false;
    }

    /// <summary>
    /// Forward-scan the route for the next corner that needs slow-turn
    /// synthesis, then walk backward through collinear straight segments to
    /// find the segment + along-track distance where the arc should engage
    /// (tangent-entry geometry: <c>r × tan(θ/2)</c> before the corner node).
    /// Returns a plan iff the trigger point lands inside the CURRENT segment;
    /// otherwise returns null (either deferred to a later <c>SetupSegment</c>
    /// or impossible geometry — logged in the latter case).
    /// </summary>
    /// <remarks>
    /// Synthesis is needed when the natural turn radius at corner speed
    /// (<c>cornerSpeed / turnRate</c>) produces an arc longer than
    /// <see cref="SlowTurnSynthesisSegmentFraction"/> of the post-corner
    /// segment — i.e. physics can't complete the turn before running off the
    /// outgoing segment. The synthesised arc uses the category's nose-wheel
    /// minimum radius at <see cref="CategoryPerformance.SlowTurnSpeedKts"/>
    /// so it fits where a natural-radius turn cannot.
    ///
    /// <para>
    /// The backward walk stops at the first segment long enough to fit the
    /// remaining tangent inset, or at the first non-collinear transition
    /// (bearing difference &gt; <see cref="CollinearBearingToleranceDeg"/>),
    /// or at the current segment boundary. Forward scan is bounded by
    /// <see cref="SynthesisLookaheadCapFt"/>.
    /// </para>
    /// </remarks>
    private static PlannedSynthesis? PlanSynthesisLookahead(TaxiRoute route, PhaseContext ctx, TaxiRouteSegment currentSeg)
    {
        int curIdx = route.CurrentSegmentIndex;
        double distFromCurEndFt = 0;

        for (int k = curIdx + 1; k < route.Segments.Count; k++)
        {
            if (distFromCurEndFt > SynthesisLookaheadCapFt)
            {
                break;
            }

            var prevK = route.Segments[k - 1];
            var segK = route.Segments[k];
            double segKLengthFt = segK.Edge.DistanceNm * GeoMath.FeetPerNm;

            if ((prevK.Edge.Edge is GroundArc) || (segK.Edge.Edge is GroundArc))
            {
                distFromCurEndFt += segKLengthFt;
                continue;
            }

            double inbound = prevK.Edge.ArrivalBearing;
            double outbound = segK.Edge.DepartureBearing;
            double turnAngleDeg = GeoMath.AbsBearingDifference(inbound, outbound);

            if (turnAngleDeg < SlowTurnSynthesisMinAngleDeg)
            {
                distFromCurEndFt += segKLengthFt;
                continue;
            }

            double cornerSpeedKts = CategoryPerformance.CornerSpeedForAngle(ctx.Category, turnAngleDeg);
            double cornerSpeedFtPerSec = cornerSpeedKts * GeoMath.FeetPerNm / 3600.0;
            double turnRateRadPerSec = CategoryPerformance.GroundTurnRate(ctx.Category) * Math.PI / 180.0;
            double naturalRadiusFt = cornerSpeedFtPerSec / turnRateRadPerSec;
            double naturalArcLengthFt = naturalRadiusFt * turnAngleDeg * Math.PI / 180.0;

            if (naturalArcLengthFt <= segKLengthFt * SlowTurnSynthesisSegmentFraction)
            {
                distFromCurEndFt += segKLengthFt;
                continue;
            }

            // Sharp corner into short segment — synthesis needed.
            //
            // Walk backward through collinear straight segments to measure
            // how much pre-corner "incoming room" is available along the
            // tangent direction. The walk stops at: (a) an arc segment,
            // (b) a non-collinear transition (> CollinearBearingToleranceDeg),
            // or (c) the current segment boundary (segments before curIdx
            // are already traversed and unavailable).
            double availIncomingFt = 0;
            int earliestReachableSegIdx = k;
            for (int b = k - 1; b >= curIdx; b--)
            {
                var segB = route.Segments[b];
                if (segB.Edge.Edge is GroundArc)
                {
                    break;
                }
                double bearingDiff = GeoMath.AbsBearingDifference(segB.Edge.ArrivalBearing, inbound);
                if (bearingDiff > CollinearBearingToleranceDeg)
                {
                    break;
                }
                availIncomingFt += segB.Edge.DistanceNm * GeoMath.FeetPerNm;
                earliestReachableSegIdx = b;
            }

            // Available outgoing room is the post-corner segment length
            // (not extending through further collinear segments — keeping
            // the tangent exit conservatively inside segK).
            double availOutgoingFt = segKLengthFt;

            // Pick the largest radius that fits both tangent insets, with
            // 20% safety margin for pure-pursuit lag, fillet-paint variance,
            // and the outgoing segment's own pre-turn blend for any
            // follow-on corner. Floor at the category's nose-wheel minimum
            // — when geometry wants tighter than that, log a warning (graph
            // or scenario-design issue worth surfacing) and clamp to the
            // floor anyway; the strict-geometry check will still gate
            // engagement on the aircraft being near the planned entry.
            const double GeometrySafetyFactor = 0.8;
            double minRadiusFt = CategoryPerformance.NoseWheelTurnRadiusFt(ctx.Category);
            double maxInsetFt = GeometrySafetyFactor * Math.Min(availIncomingFt, availOutgoingFt);
            double halfAngleRad = turnAngleDeg * 0.5 * Math.PI / 180.0;
            double maxRadiusFt = maxInsetFt / Math.Tan(halfAngleRad);

            double chosenRadiusFt;
            if (maxRadiusFt < minRadiusFt)
            {
                Log.LogWarning(
                    "[NavV2] Synth geometry tight: corner node {NodeId} (seg {K}, {TurnDeg:F1}°) availIn={AvailIn:F1}ft availOut={AvailOut:F1}ft — geometry wants r={MaxR:F1}ft below nose-wheel min {MinR:F1}ft; clamping to min (may fall outside segment)",
                    prevK.Edge.ToNodeId,
                    k,
                    turnAngleDeg,
                    availIncomingFt,
                    availOutgoingFt,
                    maxRadiusFt,
                    minRadiusFt
                );
                chosenRadiusFt = minRadiusFt;
            }
            else
            {
                chosenRadiusFt = maxRadiusFt;
            }

            // Speed derives from the geometry via v = r × ω (same turn-rate
            // the existing cornerSpeed calibration uses), so lateral
            // acceleration stays consistent with natural turns. Floor at
            // SlowTurnSpeedKts (3 kt) for pathological tight cases; cap at
            // CornerSpeedForAngle so 150°+ reversals respect the tight-
            // corner schedule (8 kt for jets).
            double chosenSpeedFtPerSec = chosenRadiusFt * turnRateRadPerSec;
            double chosenSpeedKts = chosenSpeedFtPerSec * 3600.0 / GeoMath.FeetPerNm;
            chosenSpeedKts = Math.Clamp(chosenSpeedKts, CategoryPerformance.SlowTurnSpeedKts, cornerSpeedKts);

            double tangentInsetFt = chosenRadiusFt * Math.Tan(halfAngleRad);

            // Find the trigger segment by walking backward from the corner
            // by tangentInsetFt through the (already-verified-collinear)
            // incoming chain. Uses the same stop criteria as the availIn
            // walk above — they're a matched pair.
            double remainingInsetFt = tangentInsetFt;
            int triggerIdx = -1;
            double triggerDistFromSegStartFt = 0;
            for (int b = k - 1; b >= earliestReachableSegIdx; b--)
            {
                var segB = route.Segments[b];
                double segBLengthFt = segB.Edge.DistanceNm * GeoMath.FeetPerNm;
                if (segBLengthFt >= remainingInsetFt)
                {
                    triggerIdx = b;
                    triggerDistFromSegStartFt = segBLengthFt - remainingInsetFt;
                    break;
                }
                remainingInsetFt -= segBLengthFt;
            }

            if (triggerIdx < 0)
            {
                // Even clamped to minRadius the inset exceeded the reachable
                // incoming chain. Skip synthesis — the aircraft will cut the
                // corner as a plain straight-to-straight and rely on pure-
                // pursuit + natural physics. Warning already logged above
                // when maxRadius fell below min.
                return null;
            }

            if (triggerIdx != curIdx)
            {
                // Trigger is in a future segment — defer to that segment's SetupSegment.
                return null;
            }

            var cornerNode = prevK.Edge.ToNode;
            double tangentInsetNm = tangentInsetFt / GeoMath.FeetPerNm;
            var (tangentEntryLat, tangentEntryLon) = GeoMath.ProjectPoint(
                cornerNode.Position,
                new TrueHeading(((inbound + 180.0) % 360.0 + 360.0) % 360.0),
                tangentInsetNm
            );

            var slowTurn = PathPrimitiveBuilder.SlowTurn(
                fromLat: tangentEntryLat,
                fromLon: tangentEntryLon,
                fromHdgDeg: inbound,
                toHdgDeg: outbound,
                radiusFt: chosenRadiusFt,
                maxSpeedKts: chosenSpeedKts,
                toNodeId: cornerNode.Id
            );

            Log.LogDebug(
                "[NavV2] Synth plan: corner node {NodeId} (seg {K}) turn={TurnDeg:F1}° natArc={NatArc:F1}ft segLen={SegLen:F1}ft availIn={AvailIn:F1}ft availOut={AvailOut:F1}ft chosenR={ChosenR:F1}ft chosenV={ChosenV:F1}kt inset={Inset:F1}ft triggerDist={TrigDist:F1}ft",
                cornerNode.Id,
                k,
                turnAngleDeg,
                naturalArcLengthFt,
                segKLengthFt,
                availIncomingFt,
                availOutgoingFt,
                chosenRadiusFt,
                chosenSpeedKts,
                tangentInsetFt,
                triggerDistFromSegStartFt
            );

            return new PlannedSynthesis(
                TriggerDistFromSegStartFt: triggerDistFromSegStartFt,
                SlowTurn: slowTurn,
                TangentEntryLat: tangentEntryLat,
                TangentEntryLon: tangentEntryLon,
                CornerNodeId: cornerNode.Id,
                TangentInsetNm: tangentInsetNm,
                ChosenSpeedKts: chosenSpeedKts
            );
        }

        return null;
    }

    public NavigatorResult Tick(PhaseContext ctx, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        return _currentPrimitive switch
        {
            PathPrimitiveStraight s => TickStraight(ctx, s, isLastSegment, isHoldShortCleared),
            PathPrimitiveArc a => TickArc(ctx, a, isLastSegment, isHoldShortCleared),
            PathPrimitiveSlowTurn t => TickSlowTurn(ctx, t),
            _ => NavigatorResult.ArrivedAtNode,
        };
    }

    private NavigatorResult TickStraight(PhaseContext ctx, PathPrimitiveStraight prim, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        double distNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        double edgeLengthNm = GeoMath.DistanceNm(new LatLon(_segmentFromLat, _segmentFromLon), new LatLon(TargetLat, TargetLon));

        // Planned-synthesis trigger: when the aircraft reaches the tangent-
        // entry point on the current segment (distance to target drops below
        // the tangent inset), swap in the pre-built slow-turn and let
        // TickSlowTurn drive the arc through the corner. Strict-geometry
        // check ensures we only engage when the aircraft is actually on the
        // segment line within tolerance — if it's drifted off-line, skip
        // synthesis (logged) and let the straight finish as-is.
        if (_plannedSynthesis is { } plan && (distNm <= plan.TangentInsetNm + 1e-9))
        {
            double distFromEntryFt =
                GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(plan.TangentEntryLat, plan.TangentEntryLon)) * GeoMath.FeetPerNm;
            // Scale strict-geometry tolerance with chosen radius so piston
            // aircraft with a 10 ft nose-wheel minimum don't constantly trip
            // the warning path from small pure-pursuit lag. 15% of radius
            // matches typical pure-pursuit tracking error as a fraction of
            // turn radius; the 3 ft floor handles the jet-minimum case.
            double strictToleranceFt = Math.Max(TangentEntryStrictToleranceFt, 0.15 * plan.SlowTurn.RadiusFt);
            if (distFromEntryFt > strictToleranceFt)
            {
                Log.LogWarning(
                    "[NavV2] Synth trigger: aircraft {DistFt:F1}ft from planned tangent entry (tol {Tol:F1}ft for r={RadiusFt:F1}ft) — skipping synthesis for corner node {NodeId}",
                    distFromEntryFt,
                    strictToleranceFt,
                    plan.SlowTurn.RadiusFt,
                    plan.CornerNodeId
                );
                _plannedSynthesis = null;
            }
            else
            {
                Log.LogDebug(
                    "[NavV2] Synth engaged: corner node {NodeId} r={RadiusFt:F1}ft v={SpeedKts:F1}kt entry-dist={EntryFt:F2}ft gs={Gs:F1}kt",
                    plan.CornerNodeId,
                    plan.SlowTurn.RadiusFt,
                    plan.ChosenSpeedKts,
                    distFromEntryFt,
                    ctx.Aircraft.IndicatedAirspeed
                );
                _currentPrimitive = plan.SlowTurn;
                _arcBearingFromCenterDeg = plan.SlowTurn.StartBearingFromCenterDeg;
                _arcRemainingSweepDeg = plan.SlowTurn.SweepDeg;
                _plannedSynthesis = null;
                PrevDistToTarget = double.MaxValue;
                return NavigatorResult.Navigating;
            }
        }

        // Tight arrival threshold when any of:
        //   - last segment of the route (always stop precisely),
        //   - the current target is a stop (_currentNodeRequiredSpeed == 0),
        //   - a synthesis plan is active (trigger above owns arrival; the
        //     loose 91 ft threshold would fire before the tangent inset),
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
        bool synthPlanActive = _plannedSynthesis is not null;
        double arrivalThresholdNm =
            (isLastSegment || shortEdge || isStopTarget || synthPlanActive || _nextSegmentIsArc)
                ? FinalNodeArrivalThresholdNm
                : NodeArrivalThresholdNm;

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
        // segment's departure bearing. Gated by turn angle — large turns
        // (>60°) get no blend to avoid yanking the tail early.
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

        // Planned-synthesis trigger constraint: the tangent-entry arc engages
        // `plan.TangentInsetNm` before the current target, at
        // `plan.ChosenSpeedKts`. Ensure the aircraft can brake to the
        // chosen speed by the trigger point. Handled outside
        // `_speedConstraints` because this is a PRE-target constraint
        // (negative path distance) and the backward-propagation machinery
        // assumes non-negative distances.
        if (_plannedSynthesis is { } plan)
        {
            double triggerDistNm = distToEndpointNm - plan.TangentInsetNm;
            if (triggerDistNm < 0)
            {
                triggerDistNm = 0;
            }
            double triggerLimit = Math.Sqrt(plan.ChosenSpeedKts * plan.ChosenSpeedKts + 2.0 * decelRate * triggerDistNm * 3600.0);
            brakingLimit = Math.Min(brakingLimit, triggerLimit);
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
            double inbound = seg.Edge.ArrivalBearing;
            double outbound = nextSeg.Edge.DepartureBearing;
            double turnAngle = GeoMath.AbsBearingDifference(inbound, outbound);
            _currentNodeRequiredSpeed = CategoryPerformance.CornerSpeedForAngle(ctx.Category, turnAngle);
            _nextSegmentBearing = outbound;
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
