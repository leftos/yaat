using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Result of a single <see cref="GroundNavigator.Tick"/> call. Phases use this to decide when to advance
/// the route segment index, terminate the phase, etc.
/// </summary>
public enum NavigatorResult
{
    /// <summary>Still moving toward the current target node.</summary>
    Navigating,

    /// <summary>Target node reached; the phase should advance to the next segment.</summary>
    ArrivedAtNode,
}

/// <summary>
/// Per-tick diagnostic snapshot produced by <see cref="GroundNavigator"/>. Consumed by
/// <c>TickRecorder</c> for CSV traces and by <c>Yaat.LayoutInspector --tick-table</c> for post-hoc analysis.
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
/// The ground navigator: the per-tick ground-steering contract the taxi / runway-exit / runway-crossing
/// phases drive. Drives an aircraft
/// along a resolved <see cref="TaxiRoute"/> over the filleted ground graph via closed-form playback over
/// <see cref="PathPrimitive"/>s (invariant I2 — during a curve, position and heading are both functions of
/// one scalar, so they cannot drift apart). Straight segments use pure-pursuit steering; fillet arcs play the
/// actual cubic Bézier by arc-length (<see cref="PathPrimitiveBezier"/>, ending exactly on the corner node);
/// synthesised slow-turns advance a closed-form circular integrator. Curve playback writes lat/lon/heading
/// directly from playback state. Speed comes from corner-speed limits — angle-based plus a turn-rate-feasibility cap that slows
/// the aircraft into bends too tight to track at the angle-only speed (see <see cref="CornerSpeed"/>) —
/// backward-propagated by kinematic braking, and capped by the lateral-accel arc speed model.
///
/// <para>
/// The fillet generator emits a single arc per real taxiway corner, so the navigator follows clean arcs
/// with no chord-chain compensations. But corners <em>tighter</em> than the nose-wheel radius — ramp/apron
/// bends the fillet generator cannot widen, which stay sharp vertices between short straight segments —
/// still cannot be tracked by pure-pursuit at any allowed speed (the orbit radius v/ω exceeds the segment
/// scale). Those are rounded by the entry-alignment slow-turn, which fires for <em>any</em> corner past
/// <see cref="EntryAlignmentThresholdDeg"/> — a misaligned parking-out start or a tight mid-route ramp bend
/// — tracing a nose-wheel-radius arc at walking pace. This is geometric corner-rounding.
/// </para>
///
/// <para>
/// Curved ramp taxiways (e.g. SFO CG) arrive as chains of ~15 ft straight chords with shallow bends. An
/// aircraft carrying taxi speed into such a chain overshoots a chord's to-node and pure-pursuit then
/// circles it. Two guards prevent that: <see cref="TickStraight"/> advances to
/// the next segment as soon as the aircraft's along-track projection passes the to-node (so an off-line
/// overshoot advances instead of circling), and <see cref="Tick"/> enforces a hard orbit invariant —
/// a single segment can never net <see cref="OrbitTurnLimitDeg"/> of turn (throws in tests; logs and
/// force-advances in the app).
/// </para>
///
/// <para>
/// Responsibilities: steer along each route segment; manage speed; detect per-segment arrival. Not
/// responsible for: route building, hold-short insertion, phase handoff, runway assignment.
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

    /// <summary>
    /// Maximum total straight-run length (feet) between two bracketing turns for that run to count as a
    /// <em>short connector</em> — a lane change across parallel taxiways via a short cross taxiway (e.g. SFO
    /// A→F1→B, ~228 ft of straight F1 between the A/F1 and F1/B ~90° corners). At or below this a real crew
    /// flows through as one continuous low-speed maneuver rather than settling wings-level on the connector
    /// centerline and accelerating between the two turns (AC 120-74B; aviation-reviewed, ~connector
    /// center-to-center ≲ 250 ft for narrowbodies). The navigator holds <see cref="_connectorFlowSpeedKts"/>
    /// across such a run instead of surging up to the braking-curve ceiling and braking back down (issue
    /// #236). Above this a genuine straight segment exists and the normal accelerate-then-brake profile is
    /// correct.
    /// </summary>
    private const double ShortConnectorMaxLenFt = 250.0;

    /// <summary>
    /// Heading change (degrees) between two consecutive straight segments (or a fillet <see cref="GroundArc"/>
    /// segment) that marks a <em>turn</em> bracketing a short connector. Well above a chord-chain tessellation
    /// bend and below the ~90° lane-change corners. The connector's flow-speed cap is derived from the turn
    /// angle, so a gentle bracketing turn yields a high (no-op) cap and only genuinely sharp corners slow the
    /// transit — the length window alone never forces a slowdown.
    /// </summary>
    private const double ConnectorCornerThresholdDeg = 30.0;

    public int TargetNodeId { get; private set; }
    public double TargetLat { get; private set; }
    public double TargetLon { get; private set; }
    public double PrevDistToTarget { get; private set; } = double.MaxValue;

    public NavTickDiag? LastTickDiag { get; private set; }

    /// <summary>Maximum forward speed (kts) the navigator may command on straights; the owning phase sets it per category / expedite state.</summary>
    public double MaxSpeedKts { get; set; }

    /// <summary>
    /// Minimum forward speed (kts) the navigator commands while following — a speed floor that overrides the
    /// internal braking curve, corner-speed caps, and re-acquire gate (but never the conflict/airport
    /// <see cref="AircraftState.GroundSpeedLimit"/> ceiling, which always wins for safety). Default 0 (no
    /// floor). <see cref="CrossingRunwayPhase"/> sets it to the runway-crossing speed so a cleared crossing
    /// is taken "without delay" (7110.65 §3-7-2) and never brakes toward a stop on the runway or at the
    /// far-side slice end before handing off to the onward taxi.
    /// </summary>
    public double MinSpeedKts { get; set; }

    /// <summary>
    /// Deceleration rate (kts/s) used by the braking curve and backward-propagated
    /// speed constraints. Null = the category taxi decel rate (normal taxi/exit).
    /// <see cref="RunwayExitPhase"/> raises it to
    /// <see cref="CategoryPerformance.ExpediteExitDecelRate"/> for an expedited
    /// exit so the aircraft brakes firmly to the hold-short stop after the turn-off.
    /// </summary>
    public double? DecelRateKts { get; set; }

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
    /// <see cref="PathPrimitiveSlowTurn"/>. Advanced each tick by <c>speed·dt/r</c>
    /// (signed by <see cref="PathPrimitiveSlowTurn.RightTurn"/>).
    /// </summary>
    private double _arcBearingFromCenterDeg;

    /// <summary>Remaining sweep in degrees; decreases monotonically to 0 as the arc completes.</summary>
    private double _arcRemainingSweepDeg;

    /// <summary>
    /// Bézier-playback parameter for the current <see cref="PathPrimitiveBezier"/>: 0 at the
    /// segment's from-node, 1 at its to-node. Advanced each tick by Δt = v·dt / |B'(t)|.
    /// </summary>
    private double _bezierT;

    /// <summary>Arc-length (ft) covered along the current Bézier so far, for the braking-curve remaining-distance estimate.</summary>
    private double _bezierTraveledFt;

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
    /// <see cref="TickBezier"/> would then write position directly from curve
    /// state (invariant I2), producing a visible teleport. Set by
    /// <see cref="BuildSpeedConstraints"/> alongside <see cref="_nextSegmentBearing"/>.
    /// </summary>
    private bool _nextSegmentIsArc;

    /// <summary>
    /// True when the immediately-following route segment is shorter than the loose arrival threshold
    /// (typically a virtual tail-clear stub past a hold-short, or a short fillet sub-segment). Used by
    /// <see cref="TickStraight"/> to switch to the tight arrival threshold: arriving loosely (~91 ft early)
    /// onto a segment that is itself only ~10-20 ft long places the aircraft most of the loose threshold
    /// <em>off</em> that segment's centerline, which trips the establish-straight re-acquire gate into a
    /// needless crawl to the stop. Arriving tight keeps the aircraft on the short segment's line so it
    /// brakes straight to the node. Set by <see cref="BuildSpeedConstraints"/>.
    /// </summary>
    private bool _nextSegmentIsShort;

    /// <summary>
    /// True when the current straight segment is part of a <em>short connector</em> — a straight run
    /// bracketed by two fillet corner arcs within <see cref="ShortConnectorMaxLenFt"/> (a lane change across
    /// parallel taxiways, e.g. SFO A→F1→B). While set, <see cref="ComputeTargetSpeed"/> caps the target to
    /// <see cref="_connectorFlowSpeedKts"/> so the aircraft flows through at a steady low speed instead of
    /// accelerating on the short straight and braking back down for the next turn (issue #236). Recomputed
    /// each <see cref="BuildSpeedConstraints"/> from the route, so it round-trips through a snapshot for free.
    /// </summary>
    private bool _onShortConnector;

    /// <summary>
    /// Speed cap (knots) held across a <see cref="_onShortConnector"/> run: the higher of the two bracketing
    /// corner arcs' <see cref="GroundArc.MaxSafeSpeedKts"/> — the aircraft flows through at the gentler
    /// corner's speed rather than surging. <see cref="double.MaxValue"/> (no cap) when not on a short connector.
    /// </summary>
    private double _connectorFlowSpeedKts = double.MaxValue;

    /// <summary>
    /// Speed cap (knots) for the <em>current</em> segment when it is a corner <see cref="GroundArc"/>: the
    /// arc's own <see cref="GroundArc.MaxSafeSpeedKts"/>. The braking-curve planner only treats a corner
    /// arc's safe speed as a <em>future</em> braking target (an approach limit at the arc's entry node), so
    /// once the aircraft is on a long arc — entered slow, next corner far ahead — nothing holds it to the
    /// arc's cornering speed and it accelerates toward taxi max mid-arc (the issue #236 surge, relocated onto
    /// the arc once the pathfinder routes over it). This is the flat throughout-cap: a corner arc is never
    /// flown faster than its safe cornering speed. <see cref="double.MaxValue"/> (no cap) when the current
    /// segment is not an arc. Recomputed each <see cref="BuildSpeedConstraints"/>, so it round-trips through
    /// a snapshot for free.
    /// </summary>
    private double _currentSegmentArcMaxKts = double.MaxValue;

    /// <summary>
    /// Net signed heading change (degrees) accumulated since the current segment was set up. Reset to 0
    /// whenever a primitive begins (<see cref="SetupSegment"/> or the entry-alignment→real-segment swap)
    /// and incremented each tick by the signed heading delta. Backstops the orbit invariant
    /// (<see cref="OrbitTurnLimitDeg"/>): a single segment's playback can never net a full circle —
    /// an arc sweeps &lt;180° (admissibility), a slow-turn &lt;180°, a straight ~0° — so reaching ±360°
    /// without advancing means the navigator is circling a node it cannot converge on (a pure-pursuit
    /// orbit). See the throw in <see cref="Tick"/>.
    /// </summary>
    private double _cumulativeTurnSinceAdvanceDeg;

    /// <summary>
    /// Hard ceiling on net turn within a single segment before the navigator is declared to be orbiting.
    /// A full circle (360°) is unreachable by any legitimate single-segment maneuver, so crossing it is a
    /// definitive pure-pursuit orbit — surfaced as a hard failure rather than an indefinite crawl.
    /// </summary>
    private const double OrbitTurnLimitDeg = 360.0;

    /// <summary>
    /// When true, a detected orbit (<see cref="OrbitTurnLimitDeg"/>) throws so the failure is impossible to
    /// miss. The test assembly's module initializer sets this so every test that drives an aircraft into a
    /// pure-pursuit orbit fails hard with an actionable message. In the shipping app it stays false: an
    /// orbit is logged as an error and recovered by force-advancing past the unconvergeable node, never
    /// crashing a live training session.
    /// </summary>
    public static bool ThrowOnOrbit { get; set; }

    /// <summary>
    /// Rounding radius (ft) for the corner at the END of the current segment — adaptive: tightened from the
    /// comfortable nose-wheel radius toward the tight-turn floor when the approach/departure legs are shorter
    /// than the comfortable tangent length (two close junctions), so the corner-rounding arc still exits on
    /// the outgoing centerline instead of finishing wide and forcing a pure-pursuit re-acquisition. Set in
    /// <see cref="BuildSpeedConstraints"/>; read by <see cref="TickStraight"/>'s arrival threshold. The
    /// entry-alignment arc computes its own radius from the route directly (restore-safe), using the same
    /// <see cref="AdaptiveCornerRadiusFt"/> rule.
    /// </summary>
    private double _cornerRoundingRadiusFt = CategoryPerformance.NoseWheelTurnRadiusFt(AircraftCategory.Jet);

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
    /// start tangent. Without this, an arc primitive's first <see cref="TickBezier"/>
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
    /// A straight→straight corner sharper than this (deg) is treated as an UNFILLETED kink: the fillet
    /// generator leaves GeoJSON shape-point doglegs and non-arcable junctions unsmoothed, so they arrive
    /// as two consecutive straight segments meeting at a sharp angle with no Bézier between them. At the
    /// low corner speed such a kink orbits under pure-pursuit (the turn-rate-limited heading can't track
    /// the offset before the look-ahead bearing swings away). Below it, a straight→straight bend is a gentle
    /// corner or a chord-chain tessellation (≪1° per bend) that pure-pursuit tracks fine. See issue #213
    /// (OAK taxiway G, node 360 — a 32° dogleg ~22 ft past the 28R exit fillet).
    /// </summary>
    private const double UnfilletedKinkGeometryThresholdDeg = 28.0;

    /// <summary>
    /// Reduced <see cref="EntryAlignmentThresholdDeg"/> applied at an unfilleted kink
    /// (<see cref="UnfilletedKinkGeometryThresholdDeg"/>): round the kink with the entry-alignment slow-turn
    /// whenever the aircraft still enters this far off the new segment's tangent, even though the heading
    /// delta is below the 45° gate (the pre-turn blend off the short incoming leg only takes out part of the
    /// turn). A near-aligned entry (below this) is left to pure-pursuit.
    /// </summary>
    private const double UnfilletedKinkAlignmentThresholdDeg = 20.0;

    /// <summary>
    /// Turn angles at or below this are treated as collinear chords (arc tessellation, dead-straight
    /// legs): no meaningful corner, and the turn-rate feasibility cap's <c>1/θ</c> term would blow up.
    /// Above it, <see cref="CornerSpeed"/> applies the feasibility cap — including the shallow (sub-30°)
    /// bends a chord-chain ramp curve is built from, which the angle cap alone would wave through at full
    /// taxi speed.
    /// </summary>
    private const double NearCollinearAngleDeg = 1.0;

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
        _cumulativeTurnSinceAdvanceDeg = 0.0;

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

        // Unfilleted-kink rounding: a sharp angle between this segment and a STRAIGHT incoming segment is a
        // dogleg the fillet generator left unsmoothed (a GeoJSON shape-point or a non-arcable junction).
        // Pure-pursuit orbits such a kink at the low corner speed, so reduce the entry-alignment gate to
        // round it with a closed-form slow-turn. A filleted corner has a Bézier (arc) incoming segment and
        // a chord-chain bend stays far below the geometry threshold, so neither lowers the gate. (Issue #213.)
        double incomingKinkDeg =
            (route.CurrentSegmentIndex > 0 && route.Segments[route.CurrentSegmentIndex - 1].Edge.Edge is not GroundArc)
                ? GeoMath.AbsBearingDifference(route.Segments[route.CurrentSegmentIndex - 1].Edge.ArrivalBearing, segDepartureBearing)
                : 0.0;
        double entryAlignmentThreshold =
            incomingKinkDeg > UnfilletedKinkGeometryThresholdDeg ? UnfilletedKinkAlignmentThresholdDeg : EntryAlignmentThresholdDeg;

        if (headingDelta > entryAlignmentThreshold)
        {
            // Adaptive rounding radius: tighten toward the tight-turn floor when the incoming leg (the
            // segment the aircraft is turning off) or the outgoing leg (this segment) is shorter than the
            // comfortable nose-wheel tangent length, so the arc still exits on this segment's centerline
            // rather than finishing wide and forcing a pure-pursuit re-acquisition (the SfoM2 M2→A spin:
            // B and A crossings only ~22 ft apart). Computed from the route here (restore-safe).
            double incomingRunFt =
                route.CurrentSegmentIndex > 0 ? route.Segments[route.CurrentSegmentIndex - 1].Edge.DistanceNm * GeoMath.FeetPerNm : double.MaxValue;
            double outgoingRunFt = seg.Edge.DistanceNm * GeoMath.FeetPerNm;
            double roundingRadiusFt = AdaptiveCornerRadiusFt(ctx.Category, headingDelta, incomingRunFt, outgoingRunFt);
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
                "[Nav] SetupSegment seg={SegIdx}/{Total}: entry-align slow-turn "
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

            if (_currentPrimitive is PathPrimitiveSlowTurn slowPrim)
            {
                _arcBearingFromCenterDeg = slowPrim.StartBearingFromCenterDeg;
                _arcRemainingSweepDeg = slowPrim.SweepDeg;
            }
            else if (_currentPrimitive is PathPrimitiveBezier)
            {
                _bezierT = 0;
                _bezierTraveledFt = 0;
            }
            else
            {
                _arcRemainingSweepDeg = 0;
            }
        }

        BuildSpeedConstraints(route, ctx, isHoldShortCleared);

        Log.LogDebug(
            "[Nav] SetupSegment seg={SegIdx}/{Total} target={NodeId} kind={Kind} dist={Dist:F4}nm "
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
        double headingBeforeDeg = ctx.Aircraft.TrueHeading.Degrees;

        var result = _currentPrimitive switch
        {
            PathPrimitiveStraight s => TickStraight(ctx, s, isLastSegment, isHoldShortCleared),
            PathPrimitiveBezier b => TickBezier(ctx, b, isHoldShortCleared),
            PathPrimitiveSlowTurn t => TickSlowTurn(ctx, t),
            _ => NavigatorResult.ArrivedAtNode,
        };

        // Orbit invariant: accumulate net signed heading change within the current primitive and hard-fail
        // if it reaches a full circle without advancing. No legitimate single-segment maneuver nets 360°
        // (an arc sweeps <180° by admissibility, a slow-turn <180°, a straight ~0°), so crossing it means
        // the navigator is circling a node it cannot converge on — a pure-pursuit orbit that would otherwise
        // crawl indefinitely at the slow-turn floor. Surfacing it as a throw makes every such case a hard
        // test failure with an actionable message instead of a silent slow taxi.
        _cumulativeTurnSinceAdvanceDeg += GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, headingBeforeDeg);
        if (Math.Abs(_cumulativeTurnSinceAdvanceDeg) >= OrbitTurnLimitDeg)
        {
            string message =
                $"[Nav] pure-pursuit orbit: {ctx.Aircraft.Callsign} accumulated {_cumulativeTurnSinceAdvanceDeg:F0}° of net turn on "
                + $"segment→node {TargetNodeId} ({_currentPrimitive?.Kind}) without advancing — it is circling a node it cannot "
                + $"converge on. pos=({ctx.Aircraft.Position.Lat:F6},{ctx.Aircraft.Position.Lon:F6}) gs={ctx.Aircraft.GroundSpeed:F1}kt.";

            if (ThrowOnOrbit)
            {
                throw new InvalidOperationException(message);
            }

            // Shipping app: never crash a live session. Log the invariant breach and recover by
            // advancing past the node the navigator cannot converge on (the advance-on-pass guard in
            // TickStraight should already prevent reaching here; this is the belt-and-suspenders path).
            Log.LogError("{OrbitMessage}", message);
            _cumulativeTurnSinceAdvanceDeg = 0.0;
            return NavigatorResult.ArrivedAtNode;
        }

        // When the entry-alignment slow-turn finishes, swap in the deferred
        // segment primitive and continue navigating in the same tick. The
        // synthetic arrival is internal — the route's own segment counter
        // hasn't advanced yet.
        if (result == NavigatorResult.ArrivedAtNode && _pendingSegmentPrimitive is not null)
        {
            var seg = _pendingSegmentPrimitive;
            _pendingSegmentPrimitive = null;
            _currentPrimitive = seg;
            if (seg is PathPrimitiveSlowTurn slowPrim)
            {
                _arcBearingFromCenterDeg = slowPrim.StartBearingFromCenterDeg;
                _arcRemainingSweepDeg = slowPrim.SweepDeg;
            }
            else if (seg is PathPrimitiveBezier)
            {
                _bezierT = 0;
                _bezierTraveledFt = 0;
            }
            else
            {
                _arcRemainingSweepDeg = 0;
            }
            PrevDistToTarget = double.MaxValue;
            // A new primitive begins — give it its own full-circle budget so a legitimate
            // entry-alignment turn plus the segment's own turn don't sum across the swap.
            _cumulativeTurnSinceAdvanceDeg = 0.0;
            Log.LogDebug("[Nav] Entry alignment complete; engaging real segment primitive {Kind}", seg.Kind);
            return NavigatorResult.Navigating;
        }

        return result;
    }

    /// <summary>
    /// Corner-rounding radius (ft) for a turn of <paramref name="deflectionDeg"/> whose approach and
    /// departure legs are <paramref name="incomingRunFt"/> / <paramref name="outgoingRunFt"/> long.
    /// Tightens from the comfortable nose-wheel radius toward the tight-turn floor when the shorter leg
    /// can't contain the comfortable tangent length (the radius whose tangent T = r·tan(δ/2) fits the
    /// shorter leg), so the rounding arc exits on the outgoing centerline rather than finishing wide.
    /// Returns the comfortable radius for a near-straight turn. Pure.
    /// </summary>
    internal static double AdaptiveCornerRadiusFt(AircraftCategory category, double deflectionDeg, double incomingRunFt, double outgoingRunFt)
    {
        double comfortable = CategoryPerformance.NoseWheelTurnRadiusFt(category);
        double halfTan = Math.Tan(deflectionDeg * 0.5 * Math.PI / 180.0);
        if (halfTan <= 1e-6)
        {
            return comfortable;
        }

        double fit = Math.Min(incomingRunFt, outgoingRunFt) / halfTan;
        return Math.Clamp(fit, CategoryPerformance.TightTurnFloorRadiusFt(category), comfortable);
    }

    /// <summary>
    /// Arrival threshold (nm) for a straight segment. When a sharp turn onto the next straight leg is
    /// coming up, applies tangent corner-rounding — arrive at the tangent point T = r·tan(δ/2)
    /// (r = <paramref name="roundingRadiusFt"/>, δ = corner deflection) and report
    /// <paramref name="roundingActive"/> true. Normally T is capped at 0.45·leg so rounding can't start
    /// before the midpoint; on a leg shorter than the COMFORTABLE tangent length (two close junctions —
    /// e.g. SFO M2 between the B and A crossings) the cap is relaxed to the whole leg so the tightened
    /// arc can begin at the leg start and still exit on the outgoing centerline. Floored at the
    /// final-node threshold; on a leg with no room it falls back to the standard threshold and reports
    /// <paramref name="roundingActive"/> false (never an inverted [min, max] clamp, which would throw).
    /// Pure — extracted for unit testing.
    /// </summary>
    internal static double StraightArrivalThresholdNm(
        double cornerTurnDeg,
        double edgeLengthNm,
        AircraftCategory category,
        double roundingRadiusFt,
        bool isLastSegment,
        bool isStopTarget,
        bool shortEdge,
        bool nextSegmentIsArc,
        bool nextSegmentIsShort,
        out bool roundingActive
    )
    {
        double halfTan = Math.Tan(cornerTurnDeg * 0.5 * Math.PI / 180.0);
        double comfortableTangentNm = CategoryPerformance.NoseWheelTurnRadiusFt(category) * halfTan / GeoMath.FeetPerNm;
        bool tightLeg = edgeLengthNm < comfortableTangentNm;
        double maxRoundingNm = tightLeg ? edgeLengthNm : 0.45 * edgeLengthNm;
        roundingActive = !isLastSegment && !isStopTarget && cornerTurnDeg > EntryAlignmentThresholdDeg && maxRoundingNm > FinalNodeArrivalThresholdNm;

        if (roundingActive)
        {
            double tFt = roundingRadiusFt * halfTan;
            return Math.Clamp(tFt / GeoMath.FeetPerNm, FinalNodeArrivalThresholdNm, maxRoundingNm);
        }

        return (isLastSegment || shortEdge || isStopTarget || nextSegmentIsArc || nextSegmentIsShort)
            ? FinalNodeArrivalThresholdNm
            : NodeArrivalThresholdNm;
    }

    private NavigatorResult TickStraight(PhaseContext ctx, PathPrimitiveStraight prim, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        double distNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        double edgeLengthNm = GeoMath.DistanceNm(new LatLon(_segmentFromLat, _segmentFromLon), new LatLon(TargetLat, TargetLon));

        // Foot-of-perpendicular along the segment line: along-track progress (alongNm) and cross-track
        // offset (crossTrackOffsetFt). Computed once here for both the segment-advance test below and the
        // pure-pursuit steering further down. A zero-length segment has nothing to project onto.
        double alongNm = 0.0;
        double crossTrackOffsetFt = 0.0;
        if (edgeLengthNm >= 1e-9)
        {
            var (foot, along, _) = GeoMath.FootOfPerpendicular(
                ctx.Aircraft.Position,
                new LatLon(_segmentFromLat, _segmentFromLon),
                new LatLon(TargetLat, TargetLon)
            );
            alongNm = along;
            crossTrackOffsetFt = GeoMath.DistanceNm(ctx.Aircraft.Position, foot) * GeoMath.FeetPerNm;
        }

        // Tight arrival threshold when any of:
        //   - last segment of the route (always stop precisely),
        //   - the current target is a stop (_currentNodeRequiredSpeed == 0),
        //   - the next segment is an arc — TickBezier writes position directly
        //     from curve state at engagement (invariant I2), so the
        //     loose 91 ft threshold would teleport the aircraft up to 91 ft
        //     to the arc entry node on the first TickBezier call. Tight
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
            _cornerRoundingRadiusFt,
            isLastSegment,
            isStopTarget,
            shortEdge,
            _nextSegmentIsArc,
            _nextSegmentIsShort,
            out bool sharpCornerAhead
        );

        bool overshot = distNm > PrevDistToTarget && PrevDistToTarget < OvershootDetectionNm;
        bool stalledAtThreshold = ctx.Aircraft.GroundSpeed < 0.5 && distNm < arrivalThresholdNm + 0.001;
        bool straightArrived = distNm <= arrivalThresholdNm;

        // Advance-on-pass: the aircraft's along-track projection has reached/passed the to-node. On the
        // centerline this coincides with normal arrival; off the centerline (a pure-pursuit overshoot of a
        // short segment) it advances instead of circling the node it can no longer converge on — the
        // orbit the invariant in Tick() backstops. Excluded for stop targets and the last segment, where
        // the aircraft must arrive precisely at the node rather than pass it.
        bool passedAlongTrack = (edgeLengthNm >= 1e-9) && (alongNm >= edgeLengthNm) && !isStopTarget && !isLastSegment;

        // A stop target the aircraft has already passed along-track cannot be reached without
        // reversing ~180°. Arrive in place (stop here) instead of steering backward onto it. This
        // fires only once the aircraft is past the stop — a normal approach (alongNm < edgeLengthNm)
        // still arrives precisely at the hold-short line via straightArrived. (Issue #172.)
        bool stopTargetBehind = isStopTarget && (edgeLengthNm >= 1e-9) && (alongNm >= edgeLengthNm);

        if (straightArrived || overshot || stalledAtThreshold || passedAlongTrack || stopTargetBehind)
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
        if (edgeLengthNm < 1e-9)
        {
            bearingToSteerDeg = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        }
        else
        {
            // alongNm and crossTrackOffsetFt were computed once at the top of the tick.
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
                "[Nav] TickStraight cs={Callsign} seg→{Target} pos=({Lat:F6},{Lon:F6}) hdg={Hdg:F1} steer={Steer:F1} hdgErr={HdgErr:F1} "
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

    /// <summary>
    /// Play a fillet's true cubic Bézier by arc-length. Each tick advances the curve parameter by
    /// Δt = ds / |B'(t)| (ds = v·dt) and writes position + tangent heading directly from the curve
    /// (invariant I2). Because the curve's endpoints are the segment's graph nodes, playback ends
    /// exactly on the to-node — there is no circle-approximation undershoot, so the next segment
    /// starts on-centerline rather than tripping the re-acquire speed gate.
    /// </summary>
    private NavigatorResult TickBezier(PhaseContext ctx, PathPrimitiveBezier prim, Func<int, bool> isHoldShortCleared)
    {
        // Speed floor (I7: no pivot-in-place). If effectively stopped, hold the current tangent
        // and target speed and bail — physics re-accelerates before the curve can advance.
        double vKts = ctx.Aircraft.IndicatedAirspeed;
        if (vKts < ArcSpeedFloorKts)
        {
            double tang = prim.Curve.TangentBearing(_bezierT);
            ctx.Targets.TargetTrueHeading = new TrueHeading(tang);
            double ts0 = ComputeTargetSpeed(ctx, BezierRemainingNm(prim), isHoldShortCleared);
            ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, ts0);
            AdjustSpeed(ctx, ctx.Targets.TargetSpeed ?? ts0);
            return NavigatorResult.Navigating;
        }

        // Advance arc-length ds = v·dt, stepping the parameter by ds / |B'(t)|.
        double vFtPerSec = vKts * GeoMath.FeetPerNm / 3600.0;
        double dsFt = vFtPerSec * ctx.DeltaSeconds;
        double paramSpeedFt = prim.Curve.DerivativeMagnitudeFt(_bezierT);
        _bezierT = paramSpeedFt > 1e-6 ? Math.Min(1.0, _bezierT + (dsFt / paramSpeedFt)) : 1.0;
        _bezierTraveledFt += dsFt;

        // Write position + heading directly from the playback state (invariant I2).
        var (lat, lon) = prim.Curve.Evaluate(_bezierT);
        double tangentDeg = prim.Curve.TangentBearing(_bezierT);
        ctx.Aircraft.Position = new LatLon(lat, lon);
        ctx.Aircraft.TrueHeading = new TrueHeading(tangentDeg);

        // Mirror into targets so physics does not fight the closed-form state.
        ctx.Targets.TargetTrueHeading = new TrueHeading(tangentDeg);
        double targetSpeed = ComputeTargetSpeed(ctx, BezierRemainingNm(prim), isHoldShortCleared);
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, targetSpeed);
        AdjustSpeed(ctx, ctx.Targets.TargetSpeed ?? targetSpeed);

        double distToNode = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        PrevDistToTarget = distToNode;
        UpdateDiag(ctx, distToNode, tangentDeg, targetSpeed, onArc: true);

        Log.LogDebug(
            "[Nav] TickBezier cs={Callsign} seg→{Target} pos=({Lat:F6},{Lon:F6}) tan={Tan:F1} t={T:F3} "
                + "ds={Ds:F2}ft v={V:F1}kt traveledFt={Trav:F1} distFt={Dist:F1}",
            ctx.Aircraft.Callsign,
            TargetNodeId,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            tangentDeg,
            _bezierT,
            dsFt,
            vKts,
            _bezierTraveledFt,
            distToNode * GeoMath.FeetPerNm
        );

        if (_bezierT >= 1.0 - 1e-9)
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

    /// <summary>Remaining arc length (nm) along the current Bézier, for the braking-curve distance-to-endpoint.</summary>
    private double BezierRemainingNm(PathPrimitiveBezier prim) => Math.Max(0.0, prim.LengthFt - _bezierTraveledFt) / GeoMath.FeetPerNm;

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

    private double ComputeTargetSpeed(PhaseContext ctx, double distToEndpointNm, Func<int, bool> isHoldShortCleared)
    {
        double decelRate = DecelRateKts ?? CategoryPerformance.TaxiDecelRate(ctx.Category);

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
        // large re-alignments. On a Bézier this is ~1 (we write the exact
        // tangent heading each tick) so it is a no-op.
        double bearingDeg = _currentPrimitive is PathPrimitiveBezier bezPrim
            ? bezPrim.Curve.TangentBearing(_bezierT)
            : GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        double angleDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearingDeg));
        double normalized = Math.Clamp(angleDiff / 90.0, 0.0, 1.0);
        double speedFraction = Math.Max(0.03, 1.0 - normalized * normalized);

        double target = Math.Min(MaxSpeedKts * speedFraction, brakingLimit);

        // Short-connector transit: hold a steady low speed across a short straight bracketed by two fillet
        // corner arcs (a lane change like SFO A→F1→B) instead of accelerating up to the braking-curve ceiling
        // on the connector and slamming back down for the next turn — a real crew flows through as one
        // continuous low-speed maneuver (issue #236; aviation-reviewed).
        if (_onShortConnector)
        {
            target = Math.Min(target, _connectorFlowSpeedKts);
        }

        // Flat cap on the current corner arc: never exceed its safe cornering speed while on it.
        target = Math.Min(target, _currentSegmentArcMaxKts);

        return target;
    }

    /// <summary>
    /// Detect a <em>short connector</em>: the current straight segment sits in a straight run bracketed on
    /// both ends by a turn (a fillet <see cref="GroundArc"/> or a &gt; <see cref="ConnectorCornerThresholdDeg"/>
    /// heading change between straights) whose total length is at most <see cref="ShortConnectorMaxLenFt"/> —
    /// a lane change across parallel taxiways via a short cross taxiway (SFO A→F1→B). Sets
    /// <see cref="_onShortConnector"/> and <see cref="_connectorFlowSpeedKts"/> (the higher of the two
    /// bracketing corners' comfortable speeds) so the aircraft flows through at a steady low speed rather than
    /// accelerating on the short straight and braking back down for the next turn (issue #236).
    ///
    /// <para>
    /// Only straight segments qualify — an arc already self-caps via its own max-safe-speed. The cap is
    /// self-limiting: a gentle bracketing turn yields a high (no-op) flow speed, so the length window alone
    /// never slows a run; only genuinely sharp corners (the ~90° lane-change turns) pull the cap down. Both
    /// ends must be a turn, so a single corner or a from-rest spot-exit pivot (one turn, then a long straight)
    /// is unaffected. Direction-agnostic — an S-turn (lane change) and a compounding turn both benefit.
    /// Recomputed each call, so it round-trips through a snapshot for free.
    /// </para>
    /// </summary>
    private void DetectShortConnector(TaxiRoute route, PhaseContext ctx, TaxiRouteSegment seg)
    {
        _onShortConnector = false;
        _connectorFlowSpeedKts = double.MaxValue;

        if (seg.Edge.Edge is GroundArc)
        {
            return;
        }

        double runFt = seg.Edge.DistanceNm * GeoMath.FeetPerNm;

        double? behindSpeed = FindBracketingCornerSpeed(route, ctx, route.CurrentSegmentIndex, dir: -1, ref runFt);
        if (behindSpeed is null)
        {
            return;
        }

        double? aheadSpeed = FindBracketingCornerSpeed(route, ctx, route.CurrentSegmentIndex, dir: +1, ref runFt);
        if (aheadSpeed is null)
        {
            return;
        }

        _onShortConnector = true;
        _connectorFlowSpeedKts = Math.Max(behindSpeed.Value, aheadSpeed.Value);

        Log.LogDebug(
            "[Nav] short-connector transit seg={SegIdx}/{Total} runFt={Run:F0} flowKts={Flow:F1}",
            route.CurrentSegmentIndex,
            route.Segments.Count,
            runFt,
            _connectorFlowSpeedKts
        );
    }

    /// <summary>
    /// Walk from <paramref name="idx"/> in direction <paramref name="dir"/> (-1 behind, +1 ahead) over
    /// straight continuation segments, adding their length to <paramref name="runFt"/>, until the bracketing
    /// turn: a <see cref="GroundArc"/> neighbor (→ its <see cref="GroundArc.MaxSafeSpeedKts"/>) or a
    /// &gt; <see cref="ConnectorCornerThresholdDeg"/> heading change at the intervening node
    /// (→ <see cref="CategoryPerformance.CornerSpeedForAngle"/>). Returns that corner's comfortable speed, or
    /// null when the run runs off the route (no bracketing turn) or exceeds <see cref="ShortConnectorMaxLenFt"/>.
    /// </summary>
    private static double? FindBracketingCornerSpeed(TaxiRoute route, PhaseContext ctx, int idx, int dir, ref double runFt)
    {
        int i = idx;
        while (true)
        {
            int next = i + dir;
            if (next < 0 || next >= route.Segments.Count)
            {
                return null;
            }

            var neighbor = route.Segments[next];
            if (neighbor.Edge.Edge is GroundArc arc)
            {
                return arc.MaxSafeSpeedKts(ctx.Category);
            }

            // Turn angle at the node between segment i and its neighbor. SingleCornerTurnAngle(route, k) reads
            // the turn at the node ending segment k, so index by the lower of the two segments.
            double turn = SingleCornerTurnAngle(route, dir < 0 ? next : i);
            if (turn > ConnectorCornerThresholdDeg)
            {
                // A sharp corner (over the entry-alignment threshold) is rounded by a nose-wheel-radius
                // slow-turn at ~TurnRateLimitedSpeedKts (~5 kt for a jet), well below the angle-only comfort
                // cap; a gentler one is taken at the angle comfort speed. Use the actual traversal speed so
                // the whole connector holds one steady low speed rather than surging between the turns.
                return turn > EntryAlignmentThresholdDeg
                    ? CategoryPerformance.TurnRateLimitedSpeedKts(ctx.Category, CategoryPerformance.NoseWheelTurnRadiusFt(ctx.Category))
                    : CategoryPerformance.CornerSpeedForAngle(ctx.Category, turn);
            }

            // Straight continuation — extend the run and keep walking outward.
            runFt += neighbor.Edge.DistanceNm * GeoMath.FeetPerNm;
            if (runFt > ShortConnectorMaxLenFt)
            {
                return null;
            }
            i = next;
        }
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

        _cornerRoundingRadiusFt = CategoryPerformance.NoseWheelTurnRadiusFt(ctx.Category);

        // A corner arc must never be flown faster than its safe cornering speed anywhere along it — the
        // braking curve only treats it as a future approach limit, so hold the current arc to its own cap.
        _currentSegmentArcMaxKts = seg.Edge.Edge is GroundArc currentArc ? currentArc.MaxSafeSpeedKts(ctx.Category) : double.MaxValue;

        DetectShortConnector(route, ctx, seg);

        if (!isHoldShortCleared(TargetNodeId))
        {
            _currentNodeRequiredSpeed = 0;
            _nextSegmentBearing = null;
            _nextSegmentIsArc = false;
            _nextSegmentIsShort = false;
        }
        else if (!isLastSegment)
        {
            int nextIdx = route.CurrentSegmentIndex + 1;
            var nextSeg = route.Segments[nextIdx];
            double turnAngle = SingleCornerTurnAngle(route, route.CurrentSegmentIndex);
            _currentNodeRequiredSpeed = CornerSpeed(ctx.Category, turnAngle, seg.Edge.DistanceNm, nextSeg.Edge.DistanceNm);
            _nextSegmentBearing = nextSeg.Edge.DepartureBearing;
            _nextSegmentIsArc = nextSeg.Edge.Edge is GroundArc;
            _nextSegmentIsShort = nextSeg.Edge.DistanceNm < NodeArrivalThresholdNm;

            // Adaptive rounding radius for the corner at this segment's end (matches the entry-alignment
            // radius the next segment's setup will use): tighten when the approach/departure legs are
            // shorter than the comfortable tangent so the rounding arc exits on the outgoing centerline.
            if (!_nextSegmentIsArc)
            {
                double deflectionDeg = GeoMath.AbsBearingDifference(seg.Edge.DepartureBearing, nextSeg.Edge.DepartureBearing);
                _cornerRoundingRadiusFt = AdaptiveCornerRadiusFt(
                    ctx.Category,
                    deflectionDeg,
                    seg.Edge.DistanceNm * GeoMath.FeetPerNm,
                    nextSeg.Edge.DistanceNm * GeoMath.FeetPerNm
                );
            }
        }
        else
        {
            _currentNodeRequiredSpeed = 0;
            _nextSegmentBearing = null;
            _nextSegmentIsArc = false;
            _nextSegmentIsShort = false;
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
        double decelRate = DecelRateKts ?? CategoryPerformance.TaxiDecelRate(ctx.Category);
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
    /// Comfortable taxi speed (kts) through a single corner of <paramref name="turnAngleDeg"/> between legs
    /// of <paramref name="intoNm"/> and <paramref name="outNm"/>. The lower of an angle-based comfort cap
    /// (<see cref="CategoryPerformance.CornerSpeedForAngle"/>) and a turn-rate-feasibility cap: rotating
    /// <c>θ</c>° across distance <c>L</c> at the gear-limited ground turn rate <c>ω</c> needs
    /// <c>θ·v/L ≤ ω</c>, i.e. <c>v ≤ ω·L/θ</c>; the <c>½·L</c> centres the rounding on the vertex.
    ///
    /// <para>
    /// The feasibility cap is what gives a chord-chain ramp curve (a polyline of shallow per-bend kinks the
    /// fillet generator could not widen into one arc) a realistic aggregate speed: each kink's short leg
    /// drives the cap down even though its angle is gentle, so the chain self-limits to the curve's true
    /// safe speed. It MUST therefore apply across the whole shallow-angle range, not just sharp corners —
    /// a 50 ft-radius / 90° apron curve subdivided into ~10° chords is comfortable at ~9 kt
    /// (0.13 g lateral-accel), not the 30 kt the angle cap alone would allow. On a real arc-derived chain the
    /// cap reduces to <c>v = ω·r</c> (chord length <c>L ≈ R·θ</c> cancels <c>θ</c>), so it is invariant to
    /// chord count and converges on the same physical limit a genuine arc carries via
    /// <see cref="GroundArc.MaxSafeSpeedKts"/>. On an isolated gentle kink over a long leg the cap is a no-op
    /// (<c>ω·½L/θ</c> ≫ taxi speed, so the <c>min</c> keeps taxi speed).
    /// </para>
    /// </summary>
    public static double CornerSpeed(AircraftCategory cat, double turnAngleDeg, double intoNm, double outNm)
    {
        double angleCap = CategoryPerformance.CornerSpeedForAngle(cat, turnAngleDeg);

        // Near-collinear chords (arc tessellation, dead-straight legs): no meaningful turn, and dividing by
        // a near-zero angle would blow up. Everything above this gets the feasibility cap — including the
        // shallow (sub-30°) bends a chord-chain ramp curve is built from.
        if (turnAngleDeg <= NearCollinearAngleDeg)
        {
            return angleCap;
        }

        double lFt = Math.Min(intoNm, outNm) * GeoMath.FeetPerNm;
        double feasibleFtPerSec = CategoryPerformance.GroundTurnRate(cat) * (0.5 * lFt) / turnAngleDeg;
        double feasibleKts = feasibleFtPerSec * 3600.0 / GeoMath.FeetPerNm;
        return Math.Max(Math.Min(angleCap, feasibleKts), CategoryPerformance.SlowTurnSpeedKts);
    }

    /// <summary>
    /// Turn angle (deg) at the node where <paramref name="turnNodeSegIdx"/> ends — the single corner
    /// between this segment's arrival bearing and the next segment's departure bearing. The fillet generator
    /// emits proper arcs for real corners, so there is no fillet chord-chain to aggregate: the corner the
    /// aircraft actually turns is the one between the two adjacent segments.
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
            DecelRateKts = DecelRateKts,
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
            DecelRateKts = dto.DecelRateKts,
            _nextSegmentBearing = dto.NextSegmentBearing,
        };
}
