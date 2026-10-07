using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
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
/// directly from playback state. An aircraft that begins a curve <em>off</em> it — a residual cross-track at
/// a fillet entry, a snapshot restored mid-curve — keeps that displacement as one constant captured when the
/// primitive becomes current, added to every position the playback writes and faded out over a blend
/// distance sized from that offset (<see cref="ArcEntryBlendFt"/>, <see cref="MaxBlendTrackDeg"/>), so the
/// aircraft is driven back onto the painted line at a taxi-plausible sideways rate instead of being written
/// onto it in a single sub-tick; an offset past <see cref="MaxArcEntryOffsetFt"/> is refused outright. I2
/// still holds: position remains a pure function of the one progress scalar plus that constant. Any write
/// further than the integrator advanced is a teleport (<see cref="CheckNoTeleport"/>) — a throw in tests, a
/// logged error in the app. Speed comes from corner-speed limits — angle-based plus a turn-rate-feasibility
/// cap that slows the aircraft into bends too tight to track at the angle-only speed (see
/// <see cref="CornerSpeed"/>) — backward-propagated by kinematic braking, and capped by the lateral-accel
/// arc speed model.
///
/// <para>
/// The fillet generator emits a single arc per real taxiway corner, so the navigator follows clean arcs
/// with no chord-chain compensations. But corners <em>tighter</em> than the main-gear turn radius — ramp/apron
/// bends the fillet generator cannot widen, which stay sharp vertices between short straight segments —
/// still cannot be tracked by pure-pursuit at any allowed speed (the orbit radius v/ω exceeds the segment
/// scale). Those are rounded by the entry-alignment slow-turn, which fires for <em>any</em> corner past
/// <see cref="EntryAlignmentThresholdDeg"/> — a misaligned parking-out start or a tight mid-route ramp bend
/// — tracing a main-gear-turn-radius arc at walking pace. This is geometric corner-rounding.
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
/// Responsibilities: steer along each route segment; publish the speed the segment calls for; detect
/// per-segment arrival. The navigator never integrates speed itself — it writes
/// <see cref="ControlTargets.TargetSpeed"/> (and <see cref="ControlTargets.DesiredDecelRate"/> when it wants
/// a braking rate other than the category default) and <see cref="FlightPhysics"/> closes the gap at the
/// ground rates, so the braking curve planned here and the rate actually flown are the same number. Not
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
    /// Floor (ft) on the distance of travel over which an arc-entry offset is bled off — about twice a jet's
    /// main-gear turn radius, short enough that a small residual cross-track is gone well before a fillet's
    /// tight part. A larger offset stretches the blend past this floor instead of bleeding off faster, so the
    /// sideways rate stays taxi-plausible whatever the offset is (see <see cref="MaxBlendTrackDeg"/>).
    /// </summary>
    private const double ArcEntryBlendFt = 50.0;

    /// <summary>
    /// Largest angle (degrees) between the track the entry blend actually flies and the heading the playback
    /// writes. The blend moves the aircraft offset/blend sideways per foot driven, a track divergence of
    /// atan(offset/blend); a flat blend therefore implies 11° of sideslip at a 10 ft offset, which no ground
    /// vehicle flies, and trips <see cref="CheckNoTeleport"/> at an offset that depends on speed rather than
    /// on anything the geometry says. Stretching the blend to offset/tan(5°) holds the divergence at 5°, so
    /// the lateral step is at most tan 5°·ds ≈ 0.09·ds — always inside <see cref="TeleportToleranceFt"/>.
    ///
    /// <para>
    /// On an arc with less travel left than that, the cap yields: the blend is clamped to the distance
    /// remaining on the primitive so the offset is gone when playback reaches the to-node, which is the
    /// guarantee the Bézier playback exists for — the next segment starts on its centerline (a 12 ft offset
    /// over the 87.5 ft OAK 763→762 fillet implies 7.8°, not 5°). An offset large enough for that to matter
    /// is refused upstream by <see cref="MaxArcEntryOffsetFt"/>.
    /// </para>
    /// </summary>
    private const double MaxBlendTrackDeg = 5.0;

    /// <summary>
    /// Largest arc-entry offset (ft) the playback will blend off. Beyond it the aircraft is not merely off
    /// the painted line: the route started a curve it is not on (a primitive built from the wrong pose, a
    /// route picked up at the wrong segment), and no blend rate makes that drive legitimate. The capture
    /// refuses it — a throw under <see cref="ThrowOnTeleport"/>, an error log otherwise — and then blends
    /// anyway, because a live training session is never worth crashing.
    /// </summary>
    public const double MaxArcEntryOffsetFt = 100.0;

    /// <summary>
    /// Slack (ft) on the per-sub-tick displacement check: how much further than the arc integrator advanced
    /// the aircraft may be written before <see cref="CheckNoTeleport"/> calls it a teleport. Covers the
    /// entry-offset blend's own lateral contribution at normal taxi speeds and the great-circle-vs-curve
    /// rounding, without hiding a jump.
    /// </summary>
    private const double TeleportToleranceFt = 2.0;

    /// <summary>Refinement iterations for resolving a resumed Bézier's parameter from the live position.</summary>
    private const int BezierResumeIterations = 24;

    /// <summary>Polyline steps used to measure a resumed Bézier's travelled arc length.</summary>
    private const int BezierResumeArcLengthSteps = 32;

    /// <summary>
    /// Pure-pursuit look-ahead distance floor in feet on straight segments: the category's main-gear
    /// turn radius (<see cref="CategoryPerformance.MainGearTurnRadiusFt"/>). Pure pursuit commands the
    /// curvature 2·sin α / L toward the look-ahead point; a look-ahead shorter than the gear's own
    /// minimum radius asks for more curvature than the nose wheel can deliver, so a few feet of offset
    /// left by a corner became a ~20° steer and the nose hunted across the line (S2-OAK-2, SWA2600 off
    /// the OAK U/W corner). The floor also keeps the look-ahead point from collapsing onto the
    /// aircraft's foot-of-perpendicular when nearly stationary, which would leave steering undefined.
    /// </summary>
    private static double LookAheadFloorFt(AircraftCategory category) => CategoryPerformance.MainGearTurnRadiusFt(category);

    /// <summary>
    /// Pure-pursuit look-ahead distance cap in feet on straight segments.
    /// Keeps the look-ahead from anticipating the next turn too aggressively
    /// on long straights. Public because the fillet generator sizes its
    /// minimum navigable straight (<see cref="Data.Airport.Fillet.FilletConstants.MinSharedArmClearGapFt"/>)
    /// from it: a straight shorter than the look-ahead is an orbit trap.
    /// </summary>
    public const double LookAheadCapFt = 50.0;

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
    /// Speed (kts) the navigator plans to arrive at the route's FINAL node with, instead of a stop. Default 0 —
    /// brake to a stop — which is what every caller but a cleared-for-takeoff taxi wants: <c>RunwayExitPhase</c>,
    /// <see cref="CrossingRunwayPhase"/>, <see cref="PushbackPhase"/> and a taxi to a gate or spot all end at
    /// rest. <see cref="TaxiingPhase"/> raises it to the category taxi-corner speed when a stored takeoff
    /// clearance has already cleared the destination-runway bar the route ends at, so the aircraft flows into
    /// the line-up at the speed that phase takes over at rather than braking to the bar and re-accelerating.
    /// Unlike <see cref="MinSpeedKts"/> this is not a floor: it only replaces the route-end stop in the speed
    /// plan, and every uncleared hold-short on the way still plans a stop.
    /// </summary>
    public double RouteEndSpeedKts { get; set; }

    /// <summary>
    /// Deceleration rate (kts/s) for the whole route: the braking curve to every corner, arc and stop, and the rate published
    /// to physics. Null = the category taxi decel rate (normal taxi/exit). <see cref="RunwayExitPhase"/> sets it to
    /// <see cref="CategoryPerformance.ExpediteExitDecelRate"/> for an expedited (<c>EXP</c>) exit, so the aircraft brakes at
    /// max effort through the turn-off and to the hold-short stop, and leaves it null otherwise.
    /// </summary>
    public double? DecelRateKts { get; set; }

    /// <summary>
    /// Deceleration rate (kts/s) for the route's in-route slowdowns — corner and arc speeds — when set; null leaves them at
    /// <see cref="DecelRateKts"/>'s rate. Stops — the route end, and every bar ahead not cleared — are planned each on its own
    /// braking curve at <see cref="DecelRateKts"/>'s rate (the category taxi rate when null), and that rate is published
    /// whenever a stop's curve sets the target, so a softer or firmer slowdown rate never moves a stop. A bar's stop is dropped
    /// once the bar is cleared; the route-end stop never is. <see cref="RunwayExitPhase"/> sets it on the turn-off to the rate the
    /// rollout chose the exit with. Not serialized: the owning phase re-applies it before every tick and segment set-up.
    /// </summary>
    public double? SlowdownDecelRateKts { get; set; }

    // Set by ComputeTargetSpeed when a stop's braking curve set the target while SlowdownDecelRateKts applies, so
    // PublishSpeed publishes the stop rate rather than the slowdown rate. Reset at the start of every tick.
    private bool _stopCurveBinds;

    public void SetTargetNodeId(int nodeId) => TargetNodeId = nodeId;

    /// <summary>
    /// Override the target position to the painted hold-short bar offset (the owning phase calls this
    /// after <see cref="SetupSegment"/> when stopping short of an uncleared hold-short). The arrival
    /// threshold depends on this position, so it is an explicit seam rather than a free setter. A stop is approached on
    /// the centreline, square to the bar, so when the current primitive is the straight a turn about holds on its offset
    /// line (<see cref="_turnAboutRollOutOffsetFt"/>), it drops the offset and re-lays the straight on the edge's centreline
    /// through the stop (<see cref="LayStraightSquareToStop"/>): a hold short issued while that line is held would otherwise
    /// aim at the bar from the offset line's start and stop skewed. The node turn laid from the offset line keeps the
    /// offset: the bar it aims at lies on a straight that does not follow a turn about.
    /// </summary>
    public void OverrideTargetPosition(PhaseContext ctx, double lat, double lon)
    {
        if ((_turnAboutRollOutOffsetFt > 0.0) && (_pendingSegmentPrimitive is null) && (_currentPrimitive is PathPrimitiveStraight held))
        {
            ReLayHeldStraightSquareToStop(ctx, new LatLon(lat, lon), new TrueHeading(held.BearingDeg));
        }

        TargetLat = lat;
        TargetLon = lon;
    }

    /// <summary>
    /// Drop the offset line the held straight is on and lay it on <paramref name="centreline"/> (the edge's own bearing)
    /// through <paramref name="stop"/>, from abeam the offset line's start; the offset is dropped whether or not the stop is
    /// ahead on that line.
    /// </summary>
    private void ReLayHeldStraightSquareToStop(PhaseContext ctx, LatLon stop, TrueHeading centreline)
    {
        double offsetFt = _turnAboutRollOutOffsetFt;
        _turnAboutRollOutOffsetFt = 0.0;
        if (LayStraightSquareToStop(stop, new LatLon(_segmentFromLat, _segmentFromLon), centreline))
        {
            Log.LogDebug(
                "[Nav] {Callsign}: stop at node {Node} on the offset line held {Offset:F1} ft inside the turn; re-laid the straight on "
                    + "the centreline through it on {Bearing:F1}°",
                ctx.Aircraft.Callsign,
                TargetNodeId,
                offsetFt,
                centreline.Degrees
            );
            return;
        }

        Log.LogDebug(
            "[Nav] {Callsign}: stop at node {Node} on the offset line held {Offset:F1} ft inside the turn is not ahead on the "
                + "centreline {Bearing:F1}°; the offset is dropped and the straight left on its line",
            ctx.Aircraft.Callsign,
            TargetNodeId,
            offsetFt,
            centreline.Degrees
        );
    }

    /// <summary>
    /// Lay the straight to <paramref name="stop"/> on the line through it on <paramref name="bearing"/> (the taxiway edge's
    /// own bearing, off whose centreline the aircraft rolled out of a turn about), from abeam <paramref name="from"/>, so
    /// the aircraft re-acquires the centreline and stops on it square to the bar rather than on the chord from where it
    /// stands. Returns false, leaving the line as it is, when the stop is not ahead of <paramref name="from"/> on that
    /// bearing.
    /// </summary>
    private bool LayStraightSquareToStop(LatLon stop, LatLon from, TrueHeading bearing)
    {
        double lineLengthNm = GeoMath.AlongTrackDistanceNm(stop, from, bearing);
        if (lineLengthNm <= 0.0)
        {
            return false;
        }

        LatLon lineFrom = GeoMath.ProjectPoint(stop, new TrueHeading(bearing.Degrees + 180.0), lineLengthNm);
        _segmentFromLat = lineFrom.Lat;
        _segmentFromLon = lineFrom.Lon;
        _turnAboutSquareStopLine = true;
        return true;
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

    /// <summary>
    /// The aircraft's position minus the curve point the current arc primitive starts playing from, in
    /// degrees of latitude/longitude, captured once when that primitive becomes current
    /// (<see cref="BeginPrimitive"/>). Arc playback adds it to every position it writes, faded out linearly
    /// over <see cref="ArcEntryBlendFt"/> of travel, so an aircraft that enters an arc off the painted line
    /// — a residual cross-track at a fillet entry, a snapshot restored mid-curve — is steered back onto the
    /// curve as it drives rather than being written onto it in one sub-tick. Zero on straights and on a
    /// slow-turn built from the aircraft's own pose.
    /// </summary>
    private double _arcEntryOffsetLatDeg;
    private double _arcEntryOffsetLonDeg;

    /// <summary>
    /// Distance (ft) the aircraft has travelled on the current arc primitive since
    /// <see cref="_arcEntryOffsetLatDeg"/> was captured — lead-in and curve alike. The blend fades over
    /// <see cref="ArcEntryBlendFt"/> of it. Kept separate from <see cref="_bezierTraveledFt"/>, which measures
    /// curve arc-length from the from-node and keys the arc's speed profile (<see cref="ArcProfileLimitKts"/>).
    /// </summary>
    private double _arcEntryTravelledFt;

    /// <summary>
    /// Distance (ft) of travel over which the current arc primitive's entry offset is faded out, computed
    /// once from that offset when it is captured: <see cref="ArcEntryBlendFt"/> for a small one, stretched to
    /// offset/tan(<see cref="MaxBlendTrackDeg"/>) for a larger one so the implied track stays within
    /// <see cref="MaxBlendTrackDeg"/> of the heading the playback writes, then clamped to the travel the
    /// primitive has left so the offset is always gone by its to-node.
    /// </summary>
    private double _arcEntryBlendFt = ArcEntryBlendFt;

    /// <summary>
    /// Distance (ft) still to be driven up the current Bézier's entry tangent before its curve starts. A
    /// segment can become current while the aircraft is still short of the curve's start point along that
    /// tangent (a straight that handed off early, a route picked up behind its first node); that shortfall is
    /// distance the aircraft has not driven yet, so playback drives it — rolling straight up the tangent at
    /// the sub-tick's own <c>v·dt</c> — instead of blending it away, which would hand the aircraft ground
    /// speed it never had. Zero whenever the playback is on the curve itself.
    /// </summary>
    private double _bezierLeadInRemainingFt;

    /// <summary>
    /// True from the moment an arc primitive becomes current until its first tick, which is where the entry
    /// state is actually captured. Phases run <em>before</em> physics, so the position at that first tick
    /// already carries the previous sub-tick's physics step along the old heading; capturing at install time
    /// instead would freeze the pre-step position into the offset and the first write would undo that step —
    /// an apparent jump of up to 2·v·dt at every straight→arc transition.
    /// </summary>
    private bool _arcEntryPending;

    /// <summary>
    /// Anchor of the current segment's line: pure-pursuit steering, along-track advance and cross-track all
    /// project onto it. Re-anchored on the aircraft for a free-space leg (see
    /// <see cref="ReanchorFreeSpaceLine"/>).
    /// </summary>
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
    /// Cornering-speed profile of the <em>current</em> segment when it is a corner <see cref="GroundArc"/>,
    /// measured from the segment's from-node (<see cref="OrientedProfile"/>). The braking-curve planner only
    /// treats an arc's speed as a <em>future</em> braking target (an approach limit at its entry), so once the
    /// aircraft is on a long arc — entered slow, next corner far ahead — nothing else holds it to the arc's
    /// cornering speed and it accelerates toward taxi max mid-arc (the issue #236 surge, relocated onto the arc
    /// once the pathfinder routes over it). <see cref="ComputeTargetSpeed"/> therefore caps the target by the
    /// local cornering speed ahead along the curve, each sample reached on the braking curve — never faster
    /// than the painted radius allows where the aircraft is, slowing in time for a tighter stretch further on,
    /// and not held at the tightest point's speed over a gentle sweep. The visible consequence: the aircraft
    /// may accelerate along a fillet whose entry is gentle, bounded by the arc's whole-turn angle-comfort
    /// ceiling (see <see cref="GroundArc.SafeSpeedForRadiusKts"/>) and by how much curve is left to do it
    /// in. Null when the current segment is not an arc. Recomputed each <see cref="BuildSpeedConstraints"/>,
    /// so it round-trips through a snapshot for free.
    /// </summary>
    private IReadOnlyList<GroundArc.SpeedSample>? _currentArcProfile;

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
    /// When true, an arc primitive writing the aircraft further than it drove in one sub-tick
    /// (<see cref="CheckNoTeleport"/>) throws so the failure is impossible to miss. The test assembly's
    /// module initializer sets this so every test that snaps an aircraft across the ground fails hard with
    /// an actionable message. In the shipping app it stays false: the jump is logged as an error and the
    /// aircraft carries on, never crashing a live training session.
    /// </summary>
    public static bool ThrowOnTeleport { get; set; }

    /// <summary>
    /// Rounding radius (ft) for the corner at the END of the current segment — adaptive: tightened from the
    /// comfortable main-gear turn radius toward the tight-turn floor when the approach/departure legs are shorter
    /// than the comfortable tangent length (two close junctions), so the corner-rounding arc still exits on
    /// the outgoing centerline instead of finishing wide and forcing a pure-pursuit re-acquisition. Set in
    /// <see cref="BuildSpeedConstraints"/>; read by <see cref="TickStraight"/>'s arrival threshold. The
    /// entry-alignment arc computes its own radius from the route directly (restore-safe), using the same
    /// <see cref="AdaptiveCornerRadiusFt"/> rule.
    /// </summary>
    private double _cornerRoundingRadiusFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet);

    /// <summary>
    /// Speed constraints from future segments, each as a tuple of:
    /// (path distance from current target, required speed at that point, node id, whether it is the stop for a bar).
    /// Computed during <see cref="SetupSegment"/> via forward-walk + backward-
    /// propagation, mirroring V1's approach but populated directly from
    /// <see cref="TaxiRouteSegment"/> iteration. A bar stop is dropped once its bar is cleared; every other constraint,
    /// the route-end stop included, always applies.
    /// </summary>
    private readonly List<(double PathDistNm, double RequiredSpeedKts, int NodeId, bool IsBarStop)> _speedConstraints = [];

    /// <summary>
    /// Heading-misalignment threshold (deg) above which a new segment gets a
    /// pre-segment slow-turn from the aircraft's current pose to the segment's
    /// start tangent. Without this, an arc primitive's first <see cref="TickBezier"/>
    /// would write the arc tangent into <c>TrueHeading</c> directly, snapping
    /// a stationary aircraft (e.g. just after pushback) to the route start
    /// direction. The slow-turn lets the aircraft taxi forward at the
    /// turn-rate-limited speed for the main-gear turn radius
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
    /// main-gear-min radius (OAK GA15 corner #472: 95° turn, availIn 8.4 ft,
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
    /// <remarks>
    /// Public because the pathfinder's Fastest cost prices a straight-to-straight bend the way this
    /// navigator flies it: sharper than this and the corner is a main-gear-turn-radius slow-turn.
    /// </remarks>
    public const double EntryAlignmentThresholdDeg = 45.0;

    /// <summary>
    /// An entry sharper than this (deg) is a reversal, not a corner. The pathfinder refuses any junction whose
    /// heading change exceeds <see cref="Data.Airport.Pathfinding.CategoryLimits.MaxHeadingChangeDeg"/> (135° for
    /// a jet), so no routed corner can start this far off the segment's tangent — only the first-edge exemption,
    /// which lets a route leave from wherever the aircraft happens to stand, produces one. The aircraft is turning
    /// around rather than rounding a corner, and which way round it turns is free to choose: both sides reach the
    /// same tangent, and at exactly 180° the short way is a coin flip.
    /// </summary>
    private const double ReversalEntryThresholdDeg = 135.0;

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

    /// <summary>
    /// True while the current segment starts at a virtual node — a free-space leg (the approach leg
    /// <see cref="Data.Airport.TaxiApproachLeg"/> prepends, a ramp-lane cut) rather than a painted edge. Such a
    /// leg's line is anchored at the aircraft itself, not at a node the layout holds, so it is re-anchored on
    /// the live position when the straight takes over.
    /// </summary>
    private bool _segmentFromIsVirtual;

    /// <summary>
    /// The route the active node-aimed entry-alignment arc was built from, held so the legs the arc is aimed past
    /// can be retired, and a fillet it rolls out onto handed over on the aimed line, when it completes. Null when no
    /// entry alignment is in progress or the active one is aimed at a bearing.
    /// </summary>
    private TaxiRoute? _alignmentRoute;

    /// <summary>
    /// The segment whose to-node the active node-aimed alignment arc — free-space or reversal — is aimed at,
    /// whether that is the segment being aligned onto or one beyond it. -1 when the arc is aimed at a bearing.
    /// When that segment is a fillet, the arc rolls out on the straight line to the fillet's far end rather than
    /// on the curve, so it is handed over on that line (<see cref="InstallAimedLineOverFillet"/>).
    /// </summary>
    private int _nodeAimSegmentIndex = -1;

    /// <summary>
    /// True while the current primitive is the straight <see cref="InstallAimedLineOverFillet"/> laid over a fillet
    /// segment. Round-trips through the snapshot so a restore mid-way along it rebuilds the straight, not the curve
    /// the aircraft is not standing on.
    /// </summary>
    private bool _onAimedLineOverFillet;

    /// <summary>
    /// The from-node of the fillet <see cref="_onAimedLineOverFillet"/> is flying as the aimed line; null when no aimed
    /// line is being flown. Round-trips with it, so the restore rebuilds the line only on the fillet it was laid over.
    /// </summary>
    private int? _aimedLineFilletFromNodeId;

    /// <summary>
    /// The segment whose to-node the active node-aimed alignment arc — free-space or reversal — is aimed at,
    /// when that lies BEYOND the segment being aligned onto. The legs in between are shorter than the arc, so
    /// the aircraft rolls out past them: they retire when the arc completes rather than being steered back to.
    /// -1 when the arc is aimed at the current segment's own to-node or at a bearing.
    /// </summary>
    private int _aimedPastThroughSegmentIndex = -1;

    /// <summary>
    /// True while the active entry-alignment arc is a REVERSAL aimed at a route node off a painted leg. The arc
    /// rolls out on the line from where it ends to that node, which is not the leg's own line — the aircraft has
    /// swept a turning diameter clear of the from-node it started at — so the straight that follows is anchored
    /// on the live position like a free-space leg (<see cref="ReanchorFreeSpaceLine"/>). Steering onto the leg's
    /// own line instead sends pure pursuit back toward a from-node the aircraft has already turned its back on,
    /// adding rotation the reversal had just finished paying for.
    /// </summary>
    private bool _entryArcAimedAtNodeOffRealLeg;

    /// <summary>
    /// The reversal arc of a turn about on a taxiway (<see cref="BuildTaxiwayTurnAbout"/>), held while the jog that centres
    /// it on the centreline plays, and swapped in when the jog completes. Null when no jog is playing. Round-trips through
    /// the snapshot (<see cref="GroundNavigatorPlaybackDto.PendingTurnAboutArc"/>), so a restore mid-jog goes on into the
    /// same reversal.
    /// </summary>
    private PathPrimitiveSlowTurn? _pendingTurnAboutArc;

    /// <summary>
    /// The primitive now playing is the reversal of a turn about on a taxiway (<see cref="BuildTaxiwayTurnAbout"/>), so the
    /// yaw that re-acquires the next bearing as it completes is limited on its own tight radius
    /// (<see cref="CategoryPerformance.GroundYawRateOnRadius"/>) rather than the comfortable main-gear one. False while the
    /// jog that centres the reversal plays: the jog hands straight on to the reversal, which starts on the jog's own exit
    /// tangent, with no nudge between the two. Round-trips through the snapshot
    /// (<see cref="GroundNavigatorPlaybackDto.TurnAboutReversalPlaying"/>), so a restore that lands mid-reversal goes on to
    /// the same nudge the run that was never interrupted would take.
    /// </summary>
    private bool _turnAboutReversalPlaying;

    /// <summary>
    /// The turn-about reversal now playing rolls out on its taxiway edge's own bearing, off the centreline (a turning radius
    /// after the jog, a diameter for a helicopter), toward a node where the route then turns to that same side. When it
    /// completes, the straight to the node is laid along that bearing to abeam the node
    /// (<see cref="TryLayTurnAboutRollOutLine"/>) instead of re-anchored on the node itself: steering out to the
    /// centreline only to turn back in at the node is yaw the turn about has no use for. Round-trips through the snapshot
    /// (<see cref="GroundNavigatorPlaybackDto.TurnAboutRollsOutAlongEdge"/>).
    /// </summary>
    private bool _turnAboutRollsOutAlongEdge;

    /// <summary>
    /// The turn about now playing (its jog, then its reversal) rolls out on its taxiway edge's own bearing rather than
    /// re-aimed past the bend, so when the reversal completes and the straight after it ends in a stop, that straight is
    /// laid on the centreline through the stop (<see cref="TryLayTurnAboutRollOutLine"/>) instead of on the chord from the
    /// off-centre roll-out to the stop. Round-trips through the snapshot
    /// (<see cref="GroundNavigatorPlaybackDto.TurnAboutReversalOnEdgeBearing"/>).
    /// </summary>
    private bool _turnAboutReversalOnEdgeBearing;

    /// <summary>
    /// The straight now playing is laid on the centreline through a stop after a turn about
    /// (<see cref="LayStraightSquareToStop"/>): in its last look-ahead window it is steered along the line past the stop
    /// rather than at the stop itself, so a residual offset re-acquiring the centreline does not become the heading the
    /// aircraft stops on. Round-trips through the snapshot (<see cref="GroundNavigatorPlaybackDto.TurnAboutSquareStopLine"/>).
    /// </summary>
    private bool _turnAboutSquareStopLine;

    /// <summary>
    /// How far (ft) the straight a turn about holds on its roll-out bearing runs inside the coming turn, off the segment's
    /// centreline (<see cref="TryLayTurnAboutRollOutLine"/>), kept through the node turn that straight ends in. Zero on
    /// every other straight and turn. On the straight it moves the arrival point so the node turn is laid tangent to the
    /// outgoing centreline from the offset line (<see cref="OffsetLineArrivalThresholdNm"/>); through the node turn it
    /// stops the end-of-arc nudge toward the bearing after the outgoing segment, which would turn the aircraft off the
    /// centreline the arc has just put it on. Round-trips through the snapshot
    /// (<see cref="GroundNavigatorPlaybackDto.TurnAboutRollOutOffsetFt"/>).
    /// </summary>
    private double _turnAboutRollOutOffsetFt;

    /// <summary>
    /// The primitive and playback state a snapshot carried (<see cref="FromSnapshot"/>), waiting for the owning phase's first
    /// <see cref="SetupSegment"/> to resume it (<see cref="TryResumeRestoredPlayback"/>). Dropped by that set-up whether it
    /// resumes or not, and by the first <see cref="Tick"/>, so it never reaches a later segment.
    /// </summary>
    private GroundNavigatorPlaybackDto? _restoredPlayback;

    /// <summary>
    /// The from-node of the segment the current primitive plays (the segment last set up, or the fillet an aimed line is laid
    /// over), snapshotted with the playback so a resume can check both ends.
    /// </summary>
    private int _segmentFromNodeId;

    public void SetupSegment(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        TaxiRouteSegment? seg = route.CurrentSegment;
        if (seg is null)
        {
            return;
        }

        _segmentFromNodeId = seg.FromNodeId;
        if (TryResumeRestoredPlayback(route, seg, ctx, isHoldShortCleared))
        {
            return;
        }

        if (TryRebuildAimedLine(route, seg, ctx, isHoldShortCleared))
        {
            return;
        }

        PathPrimitive segmentPrimitive = PathPrimitiveBuilder.FromSegment(seg);

        // The line the straight just ended was held on, read before the set-up re-anchors it: a node turn laid from a turn
        // about's offset line measures where the node lies along it. Null when the straight was not held off the centreline.
        double offsetLineFt = _turnAboutRollOutOffsetFt;
        TrueHeading? heldLine = HeldOffsetLineBearing();
        GroundNode from = seg.Edge.FromNode;
        GroundNode to = seg.Edge.ToNode;
        TargetNodeId = seg.ToNodeId;
        TargetLat = to.Position.Lat;
        TargetLon = to.Position.Lon;
        _segmentFromLat = from.Position.Lat;
        _segmentFromLon = from.Position.Lon;
        _segmentFromIsVirtual = (seg.FromNodeId < 0) && VirtualNode.IsVirtualEdge(seg.Edge.Edge);
        PrevDistToTarget = double.MaxValue;
        _cumulativeTurnSinceAdvanceDeg = 0.0;
        ResetAimAndTurnAboutState();

        // Corner rounding: when the aircraft heading is significantly off the segment's first tangent,
        // build a slow-turn from its current pose to the segment's start direction and stash the real
        // segment primitive for swap-in when the slow-turn completes. The aircraft taxis forward through
        // the arc at the turn-rate-limited speed for the main-gear turn radius (TurnRateLimitedSpeedKts —
        // v = ω·r, ~5 kt for a jet), rounding the corner at the main-gear turn radius instead of snapping to
        // the tangent.
        //
        // Fires for any corner sharper than EntryAlignmentThresholdDeg regardless of segment length. A bend
        // tighter than the main-gear turn radius — common in ramp clusters the fillet generator cannot widen —
        // cannot be tracked by pure-pursuit at any allowed speed: the orbit radius v/ω exceeds the
        // short-segment scale even at the SlowTurnSpeedKts floor, so the aircraft would circle the corner
        // node forever. It MUST be rounded. The speed planner (see CornerSpeed / BuildSpeedConstraints) has
        // already slowed the aircraft to the corner speed before it arrives, so the rounding begins from a
        // near-crawl and any overshoot of a very short segment is small and recovered by the normal
        // arrival/overshoot advance on the (near-collinear) segments that follow.
        double segDepartureBearing = seg.Edge.DepartureBearing;
        double headingDelta = new TrueHeading(segDepartureBearing).AbsAngleTo(ctx.Aircraft.TrueHeading);
        if (headingDelta > EntryAlignmentGateDeg(route, segDepartureBearing))
        {
            _turnAboutRollOutOffsetFt = offsetLineFt;
            (PathPrimitiveSlowTurn? alignmentArc, string? aim, bool reversalFlip) = BuildEntryAlignmentArc(route, seg, ctx, headingDelta, heldLine);
            _pendingSegmentPrimitive = segmentPrimitive;
            _currentPrimitive = alignmentArc;
            BeginPrimitive(alignmentArc);
            LogEntryAlignment(route, seg, ctx, alignmentArc, aim, reversalFlip);
        }
        else
        {
            _pendingSegmentPrimitive = null;
            _currentPrimitive = segmentPrimitive;
            ReleaseHeadingHold(ctx, segmentPrimitive);
            ReanchorFreeSpaceLine(ctx);
            BeginPrimitive(segmentPrimitive);
        }

        BuildSpeedConstraints(route, ctx, isHoldShortCleared);
        LogSegmentSetup(route, seg, ctx);
    }

    /// <summary>
    /// The bearing of the offset line the current straight is held on after a turn about (<see cref="_turnAboutRollOutOffsetFt"/>),
    /// from the line's start to abeam the node; null when the straight is not held off the centreline.
    /// </summary>
    private TrueHeading? HeldOffsetLineBearing() =>
        (_turnAboutRollOutOffsetFt > 0.0)
            ? new TrueHeading(GeoMath.BearingTo(new LatLon(_segmentFromLat, _segmentFromLon), new LatLon(TargetLat, TargetLon)))
            : null;

    /// <summary>
    /// Clear the aim and turn-about bookkeeping a primitive carried, before a new segment or an aimed line over a fillet is
    /// laid: no arc aim, no pending turn-about arc, no reversal flags, no offset or square-stop line, not on an aimed line.
    /// </summary>
    private void ResetAimAndTurnAboutState()
    {
        _alignmentRoute = null;
        _aimedPastThroughSegmentIndex = -1;
        _nodeAimSegmentIndex = -1;
        _entryArcAimedAtNodeOffRealLeg = false;
        _pendingTurnAboutArc = null;
        _turnAboutReversalPlaying = false;
        _turnAboutRollsOutAlongEdge = false;
        _turnAboutReversalOnEdgeBearing = false;
        _turnAboutSquareStopLine = false;
        _turnAboutRollOutOffsetFt = 0.0;
        _onAimedLineOverFillet = false;
        _aimedLineFilletFromNodeId = null;
    }

    /// <summary>
    /// The heading difference (deg) from the segment's first tangent above which <see cref="SetupSegment"/> rounds the
    /// corner with an entry-alignment slow turn. Unfilleted-kink rounding: a sharp angle between this segment and a
    /// STRAIGHT incoming segment is a dogleg the fillet generator left unsmoothed (a GeoJSON shape-point or a non-arcable
    /// junction). Pure-pursuit orbits such a kink at the low corner speed, so the gate is lowered to round it with a
    /// closed-form slow-turn. A filleted corner has a Bézier (arc) incoming segment and a chord-chain bend stays far below
    /// the geometry threshold, so neither lowers the gate. (Issue #213.)
    /// </summary>
    private static double EntryAlignmentGateDeg(TaxiRoute route, double segDepartureBearing)
    {
        int index = route.CurrentSegmentIndex;
        double incomingKinkDeg =
            ((index > 0) && (route.Segments[index - 1].Edge.Edge is not GroundArc))
                ? GeoMath.AbsBearingDifference(route.Segments[index - 1].Edge.ArrivalBearing, segDepartureBearing)
                : 0.0;
        return (incomingKinkDeg > UnfilletedKinkGeometryThresholdDeg) ? UnfilletedKinkAlignmentThresholdDeg : EntryAlignmentThresholdDeg;
    }

    /// <summary>
    /// Resume the primitive a snapshot was taken in the middle of, when <paramref name="seg"/> is the segment it was playing:
    /// the primitive itself, its playback progress and arc-entry blend, the line anchor and the entry-alignment bookkeeping,
    /// all as the snapshot held them, so the restored aircraft goes on along the same curve as the run that was never
    /// interrupted. Rebuilding instead would start a different curve: an entry-alignment turn is solved from the pose the
    /// aircraft had when it began, which it no longer has. The speed plan is rebuilt from the route, as for any set-up.
    /// Returns false, leaving the ordinary set-up to run, when no playback was restored or the segment is not the one it
    /// was saved on (its to-node is not the saved target, or its from-node is not the saved one).
    /// </summary>
    private bool TryResumeRestoredPlayback(TaxiRoute route, TaxiRouteSegment seg, PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        if (_restoredPlayback is not { } saved)
        {
            return false;
        }

        _restoredPlayback = null;
        if (!WasPlaybackSavedOnSegment(seg, saved))
        {
            Log.LogWarning(
                "[Nav] {Callsign}: restored playback dropped — saved on segment {SavedFrom}→{SavedTo}, set-up is for {From}→{To}",
                ctx.Aircraft.Callsign,
                saved.FromNodeId,
                TargetNodeId,
                seg.FromNodeId,
                seg.ToNodeId
            );
            return false;
        }

        _currentPrimitive = FromPrimitiveDto(saved.Primitive);
        _pendingSegmentPrimitive = saved.HasPendingSegmentPrimitive ? PathPrimitiveBuilder.FromSegment(seg) : null;
        _segmentFromIsVirtual = (seg.FromNodeId < 0) && VirtualNode.IsVirtualEdge(seg.Edge.Edge);
        _alignmentRoute = saved.AimedAtRouteNode ? route : null;
        _nodeAimSegmentIndex = saved.NodeAimSegmentIndex;
        _aimedPastThroughSegmentIndex = saved.AimedPastThroughSegmentIndex;
        _entryArcAimedAtNodeOffRealLeg = saved.EntryArcAimedAtNodeOffRealLeg;
        _pendingTurnAboutArc = saved.PendingTurnAboutArc is { } pendingTurnAbout ? FromSlowTurnDto(pendingTurnAbout) : null;
        _turnAboutReversalPlaying = saved.TurnAboutReversalPlaying == true;
        _turnAboutRollsOutAlongEdge = saved.TurnAboutRollsOutAlongEdge == true;
        _turnAboutReversalOnEdgeBearing = saved.TurnAboutReversalOnEdgeBearing == true;
        _turnAboutSquareStopLine = saved.TurnAboutSquareStopLine == true;
        _turnAboutRollOutOffsetFt = saved.TurnAboutRollOutOffsetFt ?? 0.0;
        RestorePlaybackProgress(saved);
        BuildSpeedConstraints(route, ctx, isHoldShortCleared);
        Log.LogDebug(
            "[Nav] seg={SegIdx}/{Total}: resumed the restored {Kind} toward node {NodeId} (pendingSeg={Pending})",
            route.CurrentSegmentIndex,
            route.Segments.Count,
            _currentPrimitive.Kind,
            TargetNodeId,
            _pendingSegmentPrimitive is not null
        );
        return true;
    }

    /// <summary>
    /// Rebuild the line an aimed alignment arc rolled out on when <paramref name="seg"/> is the fillet being flown as it
    /// (a snapshot restored mid-way along it, or the owning phase re-running setup on the same segment), from the line's
    /// own anchor. The fillet is recognised by both its ends — the to-node the line runs to and the from-node recorded
    /// when it was laid — so a different fillet into the same node is never rebuilt as this one's line. Its Bézier would
    /// write the aircraft onto a curve it is not standing on. Returns false, leaving the ordinary setup to run, otherwise.
    /// </summary>
    private bool TryRebuildAimedLine(TaxiRoute route, TaxiRouteSegment seg, PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        if (
            !_onAimedLineOverFillet
            || (seg.Edge.Edge is not GroundArc)
            || (seg.ToNodeId != TargetNodeId)
            || (seg.FromNodeId != _aimedLineFilletFromNodeId)
        )
        {
            return false;
        }

        GroundNode filletEnd = seg.Edge.ToNode;
        TargetLat = filletEnd.Position.Lat;
        TargetLon = filletEnd.Position.Lon;
        InstallAimedLineOverFillet(route, seg, ctx, isHoldShortCleared, new LatLon(_segmentFromLat, _segmentFromLon));
        return true;
    }

    /// <summary>
    /// Trace the entry-alignment slow-turn just installed for <paramref name="seg"/>: the heading it starts
    /// from, the tangent it aligns to, the arc solved to get there, the sense it sweeps, and whether the
    /// reversal tie-break (<see cref="ShouldReverseAgainstShortWay"/>) turned it against its short way.
    /// Recomputes the heading delta from the same two headings the caller gated on, so the line reports what
    /// was actually installed.
    /// </summary>
    private static void LogEntryAlignment(
        TaxiRoute route,
        TaxiRouteSegment seg,
        PhaseContext ctx,
        PathPrimitiveSlowTurn alignmentArc,
        string aim,
        bool reversalFlip
    )
    {
        double segDepartureBearing = seg.Edge.DepartureBearing;
        Log.LogDebug(
            "[Nav] SetupSegment seg={SegIdx}/{Total}: entry-align slow-turn "
                + "(hdgFrom={From:F0} -> hdgTo={To:F0}, delta={Delta:F0}, r={R:F0}ft, sweep={Sweep:F0}, "
                + "sense={Sense}, reversalFlip={Flip}, aim={Aim})",
            route.CurrentSegmentIndex,
            route.Segments.Count,
            ctx.Aircraft.TrueHeading.Degrees,
            segDepartureBearing,
            new TrueHeading(segDepartureBearing).AbsAngleTo(ctx.Aircraft.TrueHeading),
            alignmentArc.RadiusFt,
            alignmentArc.SweepDeg,
            alignmentArc.RightTurn ? "right" : "left",
            reversalFlip,
            aim
        );
    }

    /// <summary>
    /// Trace the segment now current: its endpoints, the primitive playing it, and whether an entry-alignment
    /// arc is holding the real primitive back. The one line a taxi trace is read from, so it carries both
    /// node positions verbatim.
    /// </summary>
    private void LogSegmentSetup(TaxiRoute route, TaxiRouteSegment seg, PhaseContext ctx)
    {
        GroundNode from = seg.Edge.FromNode;
        GroundNode to = seg.Edge.ToNode;
        double segDepartureBearing = seg.Edge.DepartureBearing;
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
            new TrueHeading(segDepartureBearing).AbsAngleTo(ctx.Aircraft.TrueHeading),
            _pendingSegmentPrimitive is not null,
            _pendingSegmentPrimitive?.Kind.ToString() ?? "none"
        );
    }

    /// <summary>
    /// The entry-alignment slow-turn for a segment the aircraft begins <paramref name="headingDelta"/>° off its
    /// first tangent, which aim built it ("node", "node-ahead", "node-reversal" or "bearing"), and whether the
    /// reversal tie-break turned it against its short way.
    ///
    /// <para>
    /// A segment starting at a virtual node is a free-space leg: no painted centerline runs out of its
    /// from-node, so an arc that merely ends on the leg's BEARING rolls out up to a diameter abeam the line to
    /// the leg's node and leaves pure pursuit to re-acquire from there. That arc is solved instead to exit on
    /// the line through the node (<see cref="PathPrimitiveBuilder.SlowTurnToPoint"/>) at the comfortable
    /// main-gear turn radius — there is no shorter outgoing leg for the adaptive fit to protect. When no such
    /// tangent exists (the point sits inside the turning circle, or reaching it needs more than
    /// <see cref="PathPrimitiveBuilder.MaxAimSweepDeg"/>),
    /// the bearing aim below is the fallback.
    /// </para>
    ///
    /// <para>
    /// A REVERSAL onto a painted leg (<see cref="ReversalEntryThresholdDeg"/>) is aimed at a node for the same
    /// reason, in the direction the tie-break has already chosen
    /// (<see cref="PathPrimitiveBuilder.SlowTurnToPointDirected"/>). A half turn ends exactly one diameter abeam
    /// the outgoing centerline — a semicircle finishes on a line PARALLEL to the one it aimed at, never on it —
    /// and pure pursuit then steers tens of degrees off the tangent to re-acquire, which is rotation on top of
    /// the reversal the operator sees as part of the same loop. A sub-threshold entry is an ordinary corner: it
    /// keeps the bearing aim, whose exit the adaptive rounding radius already fits to the outgoing leg.
    /// </para>
    ///
    /// <para>
    /// A reversal on a taxiway — free-space leg or painted edge alike — is built ahead of both as a turn about centred on
    /// the centreline (<see cref="BuildTaxiwayTurnAbout"/>), at the tight-turn radius and its own pivot speed; the
    /// comfortable-radius arcs above stay for reversals on ramps, aprons and stand lead-ins.
    /// </para>
    ///
    /// <para>
    /// <paramref name="heldLine"/> is the bearing of the line the straight before this segment was held on off the
    /// centreline, which a node turn laid from a turn about's offset line measures the node's distance along
    /// (<see cref="OffsetLineNodeTurnRadiusFt"/>); null when that straight was not held off the centreline.
    /// </para>
    /// </summary>
    private (PathPrimitiveSlowTurn Arc, string Aim, bool ReversalFlip) BuildEntryAlignmentArc(
        TaxiRoute route,
        TaxiRouteSegment seg,
        PhaseContext ctx,
        double headingDelta,
        TrueHeading? heldLine
    )
    {
        if ((headingDelta >= ReversalEntryThresholdDeg) && (BuildTaxiwayTurnAbout(route, seg, ctx) is { } turnAbout))
        {
            return turnAbout;
        }

        if (_segmentFromIsVirtual)
        {
            double freeSpaceRadiusFt = CategoryPerformance.MainGearTurnRadiusFt(ctx.Category);
            if (FindAimNode(route, ctx, route.CurrentSegmentIndex, ctx.Aircraft.Position, 2.0 * freeSpaceRadiusFt) is { } aimNode)
            {
                PathPrimitiveSlowTurn? aimed = PathPrimitiveBuilder.SlowTurnToPoint(
                    fromLat: ctx.Aircraft.Position.Lat,
                    fromLon: ctx.Aircraft.Position.Lon,
                    fromHdgDeg: ctx.Aircraft.TrueHeading.Degrees,
                    radiusFt: freeSpaceRadiusFt,
                    targetLat: aimNode.Lat,
                    targetLon: aimNode.Lon,
                    maxSpeedKts: CategoryPerformance.TurnRateLimitedSpeedKts(ctx.Category, freeSpaceRadiusFt),
                    toNodeId: seg.FromNodeId
                );

                if (aimed is not null)
                {
                    bool aimedPast = aimNode.SegmentIndex > route.CurrentSegmentIndex;
                    _alignmentRoute = route;
                    _nodeAimSegmentIndex = aimNode.SegmentIndex;
                    _aimedPastThroughSegmentIndex = aimedPast ? aimNode.SegmentIndex : -1;
                    return (aimed, aimedPast ? "node-ahead" : "node", false);
                }
            }
        }

        // Adaptive rounding radius: tighten toward the tight-turn floor when the incoming leg (the
        // segment the aircraft is turning off) or the outgoing leg (this segment) is shorter than the
        // comfortable main-gear tangent length, so the arc still exits on this segment's centerline
        // rather than finishing wide and forcing a pure-pursuit re-acquisition (the SfoM2 M2→A spin:
        // B and A crossings only ~22 ft apart). Computed from the route here (restore-safe).
        double incomingRunFt =
            route.CurrentSegmentIndex > 0 ? route.Segments[route.CurrentSegmentIndex - 1].Edge.DistanceNm * GeoMath.FeetPerNm : double.MaxValue;
        double outgoingRunFt = seg.Edge.DistanceNm * GeoMath.FeetPerNm;
        double roundingRadiusFt = AdaptiveCornerRadiusFt(ctx.Category, headingDelta, incomingRunFt, outgoingRunFt);
        if (heldLine is { } line)
        {
            roundingRadiusFt = OffsetLineNodeTurnRadiusFt(ctx, seg, headingDelta, roundingRadiusFt, line);
        }

        // Which way round to turn. The short way is right for anything that is really a corner; a reversal
        // whose short way runs into the route's own next turn is taken the other way instead, so the two
        // sweeps cancel rather than compounding into one loop round the compass.
        double dthetaDeg = GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, seg.Edge.DepartureBearing);
        bool reversalFlip = ShouldReverseAgainstShortWay(dthetaDeg, SignedTurnAfterEntry(route, seg));
        bool rightTurn = (dthetaDeg > 0) != reversalFlip;

        // A reversal is aimed at a node, in that chosen direction: its half turn cannot end ON the outgoing
        // centerline, only parallel to it a diameter away.
        if (
            Math.Abs(dthetaDeg) >= ReversalEntryThresholdDeg
            && FindAimNode(route, ctx, route.CurrentSegmentIndex, ctx.Aircraft.Position, 2.0 * roundingRadiusFt) is { } reversalAim
        )
        {
            PathPrimitiveSlowTurn? aimedReversal = PathPrimitiveBuilder.SlowTurnToPointDirected(
                fromLat: ctx.Aircraft.Position.Lat,
                fromLon: ctx.Aircraft.Position.Lon,
                fromHdgDeg: ctx.Aircraft.TrueHeading.Degrees,
                radiusFt: roundingRadiusFt,
                targetLat: reversalAim.Lat,
                targetLon: reversalAim.Lon,
                maxSpeedKts: CategoryPerformance.TurnRateLimitedSpeedKts(ctx.Category, roundingRadiusFt),
                toNodeId: seg.FromNodeId,
                rightTurn: rightTurn
            );

            if (aimedReversal is not null)
            {
                bool reversalAimedPast = reversalAim.SegmentIndex > route.CurrentSegmentIndex;
                _alignmentRoute = route;
                _nodeAimSegmentIndex = reversalAim.SegmentIndex;
                _aimedPastThroughSegmentIndex = reversalAimedPast ? reversalAim.SegmentIndex : -1;
                _entryArcAimedAtNodeOffRealLeg = true;
                return (aimedReversal, reversalAimedPast ? "node-reversal-ahead" : "node-reversal", reversalFlip);
            }
        }

        PathPrimitiveSlowTurn arc = PathPrimitiveBuilder.SlowTurnDirected(
            fromLat: ctx.Aircraft.Position.Lat,
            fromLon: ctx.Aircraft.Position.Lon,
            fromHdgDeg: ctx.Aircraft.TrueHeading.Degrees,
            toHdgDeg: seg.Edge.DepartureBearing,
            radiusFt: roundingRadiusFt,
            // Round at the fastest speed the gear-limited turn rate can track this radius (v = ω·r),
            // not a flat 3 kt creep — a jet rounds a sharp corner at its 25 ft main-gear radius near
            // ~5 kt (aviation-reviewed). Floored at SlowTurnSpeedKts for degenerate radii.
            maxSpeedKts: CategoryPerformance.TurnRateLimitedSpeedKts(ctx.Category, roundingRadiusFt),
            toNodeId: seg.FromNodeId,
            rightTurn: rightTurn
        );

        return (arc, "bearing", reversalFlip);
    }

    /// <summary>
    /// The radius (ft) of the node turn laid onto <paramref name="seg"/> from the offset line a turn about held
    /// (<see cref="_turnAboutRollOutOffsetFt"/>, d), turning <paramref name="deflectionDeg"/> (δ): the arc tangent to the
    /// outgoing centreline from where the aircraft actually stands. <see cref="OffsetLineArrivalThresholdNm"/> ends the
    /// straight at the tangent point for <paramref name="plannedRadiusFt"/>, but a sub-tick of travel at taxi speed overruns
    /// that point by up to several feet, and an arc of the planned radius begun late crosses the centreline by the overrun
    /// times sin δ (N152SP: 5 ft off H). From the actual start the tangent length is T' = d·cot δ + a, a being how far the
    /// node is still ahead along the line (its foot on the line is the abeam point, beyond which the offset line meets the
    /// outgoing centreline d·cot δ on), so the arc tangent to both lines has radius T' / tan(δ/2): never wider than planned,
    /// never tighter than <see cref="CategoryPerformance.TightTurnFloorRadiusFt"/>. a is measured along
    /// <paramref name="heldLine"/>, the bearing of the offset line itself, not the aircraft's heading, which the straight's
    /// steering may have left a little off it.
    /// </summary>
    private double OffsetLineNodeTurnRadiusFt(
        PhaseContext ctx,
        TaxiRouteSegment seg,
        double deflectionDeg,
        double plannedRadiusFt,
        TrueHeading heldLine
    )
    {
        double deltaRad = deflectionDeg * Math.PI / 180.0;
        double aheadFt = GeoMath.AlongTrackDistanceNm(seg.Edge.FromNode.Position, ctx.Aircraft.Position, heldLine) * GeoMath.FeetPerNm;
        double tangentFt = (_turnAboutRollOutOffsetFt / Math.Tan(deltaRad)) + aheadFt;
        double fitFt = tangentFt / Math.Tan(deltaRad / 2.0);
        double radiusFt = Math.Clamp(fitFt, CategoryPerformance.TightTurnFloorRadiusFt(ctx.Category), plannedRadiusFt);
        Log.LogDebug(
            "[Nav] {Callsign}: node turn from the offset line: node {Node} {Ahead:F1} ft ahead, {Offset:F1} ft inside; "
                + "tangent {Tangent:F1} ft, r={R:F1}ft (planned {Planned:F0}ft)",
            ctx.Aircraft.Callsign,
            seg.FromNodeId,
            aheadFt,
            _turnAboutRollOutOffsetFt,
            tangentFt,
            radiusFt,
            plannedRadiusFt
        );
        return radiusFt;
    }

    /// <summary>
    /// A reversal on a taxiway (<see cref="TurnAboutTaxiwayEdge"/>), built as a turn about that stays inside the taxiway
    /// (<see cref="SolveTaxiwayTurnAbout"/>), with the aim bookkeeping committed and the reversal parked in
    /// <see cref="_pendingTurnAboutArc"/> while the jog plays. Returns the primitive to play first — the jog, or the
    /// reversal alone when no jog is needed — or null, leaving the comfortable-radius aims to run.
    /// </summary>
    private (PathPrimitiveSlowTurn Arc, string Aim, bool ReversalFlip)? BuildTaxiwayTurnAbout(TaxiRoute route, TaxiRouteSegment seg, PhaseContext ctx)
    {
        if (SolveTaxiwayTurnAbout(route, seg, ctx) is not { } plan)
        {
            return null;
        }

        bool reAimed = plan.AimSegmentIndex >= 0;
        _alignmentRoute = reAimed ? route : null;
        _nodeAimSegmentIndex = plan.AimSegmentIndex;
        _aimedPastThroughSegmentIndex = reAimed ? plan.AimSegmentIndex : -1;
        _entryArcAimedAtNodeOffRealLeg = reAimed && !_segmentFromIsVirtual;
        _pendingTurnAboutArc = plan.Jog is null ? null : plan.Reversal;
        _turnAboutReversalPlaying = plan.Jog is null;
        _turnAboutRollsOutAlongEdge = plan.RollsOutAlongEdge;
        _turnAboutReversalOnEdgeBearing = !reAimed;
        LogTaxiwayTurnAbout(ctx, plan, reAimed);
        return (plan.Jog ?? plan.Reversal, reAimed ? "turn-about-reaimed" : "turn-about", plan.ReversalFlip);
    }

    private static void LogTaxiwayTurnAbout(PhaseContext ctx, TaxiwayTurnAboutPlan plan, bool reAimed)
    {
        double jogDeg = plan.Jog?.SweepDeg ?? 0.0;
        string sense = plan.Reversal.RightTurn ? "right" : "left";
        string aim = reAimed ? "re-aimed past the bend" : "rolled out on the edge's bearing";
        Log.LogDebug(
            "[Nav] {Callsign}: turn about on {Taxiway} edge {A}-{B}: r={R:F0}ft at {Speed:F2}kt, jog {Jog:F0}° then reversal {Sweep:F0}° {Sense} "
                + "({Aim}, aimed at segment {AimSegment}'s node, holding the edge's bearing after it: {AlongEdge})",
            ctx.Aircraft.Callsign,
            plan.Edge.TaxiwayName,
            plan.Edge.Nodes[0].Id,
            plan.Edge.Nodes[1].Id,
            plan.Reversal.RadiusFt,
            plan.Reversal.MaxSpeedKts,
            jogDeg,
            plan.Reversal.SweepDeg,
            sense,
            aim,
            plan.AimSegmentIndex,
            plan.RollsOutAlongEdge
        );
    }

    /// <summary>
    /// The arcs of a turn about on a taxiway (<see cref="SolveTaxiwayTurnAbout"/>) and what they were solved against.
    /// </summary>
    private readonly record struct TaxiwayTurnAboutPlan
    {
        /// <summary>The taxiway edge the turn about is made on.</summary>
        public required GroundEdge Edge { get; init; }

        /// <summary>The jog played before the reversal, or null for none.</summary>
        public required PathPrimitiveSlowTurn? Jog { get; init; }

        /// <summary>The reversal arc itself, played after the jog.</summary>
        public required PathPrimitiveSlowTurn Reversal { get; init; }

        /// <summary>The segment whose to-node a re-aimed reversal is aimed at, or -1 for one rolled out on the edge's own bearing.</summary>
        public required int AimSegmentIndex { get; init; }

        /// <summary>Whether the tie-break swept the reversal against its short way.</summary>
        public required bool ReversalFlip { get; init; }

        /// <summary>The plan's <see cref="_turnAboutRollsOutAlongEdge"/>.</summary>
        public required bool RollsOutAlongEdge { get; init; }
    }

    /// <summary>Where a turn about's reversal starts and how it turns.</summary>
    private readonly record struct TurnAboutStart
    {
        /// <summary>The taxiway edge the turn about is made on.</summary>
        public required GroundEdge Edge { get; init; }

        /// <summary>The jog played before the reversal, or null for none.</summary>
        public required PathPrimitiveSlowTurn? Jog { get; init; }

        /// <summary>Where the reversal starts.</summary>
        public required LatLon From { get; init; }

        /// <summary>The heading (deg true) the reversal starts on.</summary>
        public required double FromHdgDeg { get; init; }

        /// <summary>The reversal's turning radius (ft), the tight-turn floor for the category.</summary>
        public required double RadiusFt { get; init; }

        /// <summary>Whether the reversal turns right.</summary>
        public required bool RightTurn { get; init; }

        /// <summary>Whether the tie-break swept the reversal against its short way.</summary>
        public required bool ReversalFlip { get; init; }
    }

    /// <summary>
    /// Solve a reversal on a taxiway as a turn about that stays inside the taxiway: both arcs at the type's turn-about radius
    /// (<see cref="TurnAboutFit"/>) and played at <see cref="CategoryPerformance.TurnAboutSpeedKts"/> on it,
    /// a jog against the reversal's sense (<see cref="PathPrimitiveBuilder.TurnAboutJogDeg"/>) that puts the reversal's
    /// turning circle on the centreline, then the reversal itself, in the direction the tie-break chose
    /// (<see cref="ShouldReverseAgainstShortWay"/>), rolled out on the edge's own bearing back toward the node the route
    /// reverses to (<see cref="BuildRolledOutTurnAbout"/>). A half turn begun on the centreline ends a whole diameter off it —
    /// at the comfortable radius a C172 swung 30 ft off a 25 ft-wide taxiway — where this one spans about one radius either
    /// side of it and ends a radius off it, parallel. A helicopter takes no jog: the reversal arc alone, which ends a
    /// diameter off.
    ///
    /// <para>
    /// When the route bends at the first node a turning diameter from the jog's exit (<see cref="FindAimNode"/>) against the
    /// reversal's sense, and cutting straight onto the leg out of that bend keeps the main gear on the taxiways' pavement,
    /// the reversal is aimed past the bend instead, with no jog (<see cref="ReAimPastTheBend"/>).
    /// </para>
    ///
    /// <para>
    /// Null when the aircraft is not standing inside a taxiway edge it reverses over, no route node lies a turning diameter
    /// from the jog's exit, or the reversal from the jog's exit has no tangent through that node
    /// (<see cref="ReversalReachesAimNode"/>). Reads navigator state; changes none.
    /// </para>
    /// </summary>
    private TaxiwayTurnAboutPlan? SolveTaxiwayTurnAbout(TaxiRoute route, TaxiRouteSegment seg, PhaseContext ctx)
    {
        double radiusFt = TurnAboutFitOf(ctx).RadiusFt;
        if (TurnAboutTaxiwayEdge(seg, ctx, radiusFt) is not { } edge)
        {
            return null;
        }

        double dthetaDeg = GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, seg.Edge.DepartureBearing);
        double nextTurnDeg = SignedTurnAfterEntry(route, seg);
        bool reversalFlip = ShouldReverseAgainstShortWay(dthetaDeg, nextTurnDeg);
        bool rightTurn = (dthetaDeg > 0) != reversalFlip;

        PathPrimitiveSlowTurn? jog =
            (ctx.Category == AircraftCategory.Helicopter) ? null : BuildTurnAboutJog(edge, ctx, radiusFt, seg.FromNodeId, rightTurn);
        (LatLon reversalFrom, double reversalFromHdgDeg) = jog is null
            ? (ctx.Aircraft.Position, ctx.Aircraft.TrueHeading.Degrees)
            : PathPrimitiveBuilder.ExitPose(jog);
        var start = new TurnAboutStart
        {
            Edge = edge,
            Jog = jog,
            From = reversalFrom,
            FromHdgDeg = reversalFromHdgDeg,
            RadiusFt = radiusFt,
            RightTurn = rightTurn,
            ReversalFlip = reversalFlip,
        };
        if (
            (FindAimNode(route, ctx, route.CurrentSegmentIndex, reversalFrom, 2.0 * radiusFt) is not { } aimNode)
            || !ReversalReachesAimNode(ctx, start, seg.FromNodeId, new LatLon(aimNode.Lat, aimNode.Lon))
        )
        {
            return null;
        }

        if (ReAimPastTheBend(route, ctx, edge, aimNode.SegmentIndex, rightTurn) is { } reAimed)
        {
            return new TaxiwayTurnAboutPlan
            {
                Edge = edge,
                Jog = null,
                Reversal = reAimed.Arc,
                AimSegmentIndex = reAimed.SegmentIndex,
                ReversalFlip = reversalFlip,
                RollsOutAlongEdge = false,
            };
        }

        return BuildRolledOutTurnAbout(route, seg, ctx, start, nextTurnDeg);
    }

    /// <summary>
    /// Whether the reversal from <paramref name="start"/>'s pose has a tangent through <paramref name="aimNode"/>, the node
    /// a turning diameter away it would have been aimed at. Without one there is no turn about at all and the alignment
    /// falls back to the comfortable-radius aims, whatever the re-aim or the roll-out would make of it.
    /// </summary>
    private static bool ReversalReachesAimNode(PhaseContext ctx, TurnAboutStart start, int toNodeId, LatLon aimNode)
    {
        PathPrimitiveSlowTurn? reversalToAimNode = PathPrimitiveBuilder.SlowTurnToPointDirected(
            fromLat: start.From.Lat,
            fromLon: start.From.Lon,
            fromHdgDeg: start.FromHdgDeg,
            radiusFt: start.RadiusFt,
            targetLat: aimNode.Lat,
            targetLon: aimNode.Lon,
            maxSpeedKts: CategoryPerformance.TurnAboutSpeedKts(ctx.Category, start.RadiusFt),
            toNodeId: toNodeId,
            rightTurn: start.RightTurn
        );
        if (reversalToAimNode is not null)
        {
            return true;
        }

        Log.LogDebug(
            "[Nav] {Callsign}: no turn about on {Taxiway} at r={R:F0}ft (jog {Jog:F0}°): the reversal has no tangent to its aim node",
            ctx.Aircraft.Callsign,
            start.Edge.TaxiwayName,
            start.RadiusFt,
            start.Jog?.SweepDeg ?? 0.0
        );
        return false;
    }

    /// <summary>
    /// The turn about's plan when its reversal is not re-aimed: the jog, then the reversal rolled out on the edge's own
    /// bearing back toward <paramref name="seg"/>'s to-node, with whether the straight after it holds that bearing to
    /// abeam the node (<see cref="HoldsRollOutBearing"/>).
    /// </summary>
    private static TaxiwayTurnAboutPlan BuildRolledOutTurnAbout(
        TaxiRoute route,
        TaxiRouteSegment seg,
        PhaseContext ctx,
        TurnAboutStart start,
        double nextTurnDeg
    )
    {
        PathPrimitiveSlowTurn reversal = PathPrimitiveBuilder.SlowTurnDirected(
            fromLat: start.From.Lat,
            fromLon: start.From.Lon,
            fromHdgDeg: start.FromHdgDeg,
            toHdgDeg: ReversedEdgeBearingDeg(start.Edge, seg.ToNodeId),
            radiusFt: start.RadiusFt,
            maxSpeedKts: CategoryPerformance.TurnAboutSpeedKts(ctx.Category, start.RadiusFt),
            toNodeId: seg.FromNodeId,
            rightTurn: start.RightTurn
        );
        return new TaxiwayTurnAboutPlan
        {
            Edge = start.Edge,
            Jog = start.Jog,
            Reversal = reversal,
            AimSegmentIndex = -1,
            ReversalFlip = start.ReversalFlip,
            RollsOutAlongEdge = HoldsRollOutBearing(route, seg, ctx, reversal, nextTurnDeg),
        };
    }

    /// <summary>
    /// Whether the straight after a rolled-out <paramref name="reversal"/> holds its bearing to abeam <paramref name="seg"/>'s
    /// to-node (<see cref="_turnAboutRollsOutAlongEdge"/>) rather than re-centring on the node. The reversal ends off the
    /// centreline on the side opposite its own sense (a radius off after the jog, a diameter after a helicopter's lone
    /// arc), so this is only when the route then turns more than <see cref="EntryAlignmentThresholdDeg"/> to that side
    /// onto a straight edge, the aircraft already on the inside of the turn; when the main gear at that offset stays
    /// within the type's taxiway half-width (<see cref="TurnAboutFitResult.HalfWidthFt"/>) of the centreline; and when the straight is no longer than
    /// <see cref="RollOutHoldMaxRadii"/> turning radii. An aircraft type with no FAA main-gear width never holds it.
    /// </summary>
    private static bool HoldsRollOutBearing(
        TaxiRoute route,
        TaxiRouteSegment seg,
        PhaseContext ctx,
        PathPrimitiveSlowTurn reversal,
        double nextTurnDeg
    )
    {
        int nextIndex = route.CurrentSegmentIndex + 1;
        bool straightNext = (nextIndex < route.Segments.Count) && (route.Segments[nextIndex].Edge.Edge is GroundEdge);
        bool insideNextTurn = (Math.Abs(nextTurnDeg) > EntryAlignmentThresholdDeg) && ((nextTurnDeg > 0.0) != reversal.RightTurn);
        if (!straightNext || !insideNextTurn)
        {
            return false;
        }

        if (IsBarNode(route, ctx, seg.ToNodeId))
        {
            Log.LogDebug(
                "[Nav] {Callsign}: turn about ends at the bar at node {Node}; it re-centres after its reversal to stop on the centreline",
                ctx.Aircraft.Callsign,
                seg.ToNodeId
            );
            return false;
        }

        if (MainGearWidthFt(ctx.Aircraft.AircraftType) is not { } gearWidthFt)
        {
            Log.LogDebug(
                "[Nav] {Callsign}: no FAA main-gear width for {Type}; the turn about re-centres after its reversal",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.AircraftType
            );
            return false;
        }

        return RollOutHoldFits(ctx, seg, reversal, gearWidthFt);
    }

    /// <summary>
    /// Whether the main gear of a <paramref name="gearWidthFt"/>-wide type stays within its taxiway half-width
    /// (<see cref="TurnAboutFitResult.HalfWidthFt"/>) of the centreline at the offset a rolled-out <paramref name="reversal"/>
    /// ends at, and the straight from its end to abeam <paramref name="seg"/>'s to-node is no longer than
    /// <see cref="RollOutHoldMaxRadii"/> turning radii.
    /// </summary>
    private static bool RollOutHoldFits(PhaseContext ctx, TaxiRouteSegment seg, PathPrimitiveSlowTurn reversal, double gearWidthFt)
    {
        double offsetFt = (ctx.Category == AircraftCategory.Helicopter) ? 2.0 * reversal.RadiusFt : reversal.RadiusFt;
        var bearing = new TrueHeading(reversal.ExitTangentBearingDeg);
        double straightFt =
            GeoMath.AlongTrackDistanceNm(seg.Edge.ToNode.Position, PathPrimitiveBuilder.ExitPose(reversal).Position, bearing) * GeoMath.FeetPerNm;
        bool gearFits = (offsetFt + (gearWidthFt / 2.0)) <= TurnAboutFitOf(ctx).HalfWidthFt;
        bool shortEnough = straightFt <= (RollOutHoldMaxRadii * reversal.RadiusFt);
        Log.LogDebug(
            "[Nav] {Callsign}: turn about ends {Offset:F0} ft inside the turn at node {Node}, {Straight:F0} ft short of it: "
                + "gear fits {GearFits}, short enough {Short}",
            ctx.Aircraft.Callsign,
            offsetFt,
            seg.ToNodeId,
            straightFt,
            gearFits,
            shortEnough
        );
        return gearFits && shortEnough;
    }

    /// <summary>
    /// The longest straight (in turning radii, three turning diameters) a turn about holds its roll-out bearing over to
    /// abeam the node; over a longer straight the pilot re-centres on the taxiway centreline first (AIM 2-3-4.b.1).
    /// </summary>
    private const double RollOutHoldMaxRadii = 6.0;

    /// <summary>
    /// The bearing (deg true) along <paramref name="edge"/> toward its end node <paramref name="toNodeId"/>: the way a turn
    /// about on it faces once reversed.
    /// </summary>
    private static double ReversedEdgeBearingDeg(GroundEdge edge, int toNodeId)
    {
        GroundNode to = (edge.Nodes[0].Id == toNodeId) ? edge.Nodes[0] : edge.Nodes[1];
        return GeoMath.BearingTo(edge.OtherNode(to).Position, to.Position);
    }

    /// <summary>
    /// A turn-about reversal aimed past the bend at the to-node of segment <paramref name="bendSegmentIndex"/>, from the
    /// aircraft's own pose with no jog, at the first node on the leg out of the bend a turning diameter from the aircraft
    /// (<see cref="FindAimNode"/>, which still stops at a bar), with the index of the segment that node ends.
    ///
    /// <para>
    /// Only when the bend qualifies (<see cref="ReAimBoundFt"/>) and the arc and the straight from it to that node stay, at
    /// every sample, within that bound of the turn-about edge's centreline or the outgoing leg's
    /// (<see cref="CutStaysOnPavement"/>). A cut that leaves those strips crosses the unpaved wedge between the two
    /// taxiways. Null otherwise.
    /// </para>
    /// </summary>
    private static (PathPrimitiveSlowTurn Arc, int SegmentIndex)? ReAimPastTheBend(
        TaxiRoute route,
        PhaseContext ctx,
        GroundEdge edge,
        int bendSegmentIndex,
        bool rightTurn
    )
    {
        if (ReAimBoundFt(route, ctx, bendSegmentIndex, rightTurn) is not { } boundFt)
        {
            return null;
        }

        int outgoingIndex = bendSegmentIndex + 1;
        double radiusFt = TurnAboutFitOf(ctx).RadiusFt;
        if (FindAimNode(route, ctx, outgoingIndex, ctx.Aircraft.Position, 2.0 * radiusFt) is not { } aim)
        {
            return null;
        }

        if (PavedCentrelines(edge, route, outgoingIndex, aim.SegmentIndex) is not { } centrelines)
        {
            return null;
        }

        var aimNode = new LatLon(aim.Lat, aim.Lon);
        PathPrimitiveSlowTurn? arc = PathPrimitiveBuilder.SlowTurnToPointDirected(
            fromLat: ctx.Aircraft.Position.Lat,
            fromLon: ctx.Aircraft.Position.Lon,
            fromHdgDeg: ctx.Aircraft.TrueHeading.Degrees,
            radiusFt: radiusFt,
            targetLat: aimNode.Lat,
            targetLon: aimNode.Lon,
            maxSpeedKts: CategoryPerformance.TurnAboutSpeedKts(ctx.Category, radiusFt),
            toNodeId: route.Segments[route.CurrentSegmentIndex].FromNodeId,
            rightTurn: rightTurn
        );
        if (arc is null)
        {
            return null;
        }

        bool fits = CutStaysOnPavement(arc, aimNode, centrelines, boundFt);
        Log.LogDebug(
            "[Nav] {Callsign}: bend at segment {Seg}'s node against the reversal; cut to segment {Aim}'s node {Fits} within {Bound:F1} ft",
            ctx.Aircraft.Callsign,
            bendSegmentIndex,
            aim.SegmentIndex,
            fits ? "stays" : "does not stay",
            boundFt
        );
        return fits ? (arc, aim.SegmentIndex) : null;
    }

    /// <summary>
    /// The bound (ft) a cut past the bend at segment <paramref name="bendSegmentIndex"/>'s to-node must stay within of a
    /// centreline (<see cref="ReAimCutBoundFt"/>): the type's taxiway half-width less the further of its main and nose gear's
    /// reach outside the path. Null, and no re-aim, when no leg follows the bend, the bend node is a bar (a cut past it
    /// would drive through the hold-short without arriving at it), the bend does not run against the reversal's sense
    /// (<paramref name="rightTurn"/>) or is no sharper than <see cref="ReAimMinBendDeg"/> — the route would otherwise turn
    /// about to the node only to turn most of the way back — the type has no FAA main-gear width or wheelbase, or its gear
    /// reaches past the taxiway's half-width.
    /// </summary>
    private static double? ReAimBoundFt(TaxiRoute route, PhaseContext ctx, int bendSegmentIndex, bool rightTurn)
    {
        int outgoingIndex = bendSegmentIndex + 1;
        int bendNodeId = route.Segments[bendSegmentIndex].ToNodeId;
        if ((outgoingIndex >= route.Segments.Count) || IsBarNode(route, ctx, bendNodeId))
        {
            Log.LogDebug(
                "[Nav] {Callsign}: no re-aim past node {Node}: the route ends there or holds short of it",
                ctx.Aircraft.Callsign,
                bendNodeId
            );
            return null;
        }

        double bendDeg = GeoMath.SignedBearingDifference(
            route.Segments[bendSegmentIndex].Edge.ArrivalBearing,
            route.Segments[outgoingIndex].Edge.DepartureBearing
        );
        if (((bendDeg > 0.0) == rightTurn) || (Math.Abs(bendDeg) <= ReAimMinBendDeg))
        {
            return null;
        }

        if (MainGearWidthFt(ctx.Aircraft.AircraftType) is not { } gearWidthFt)
        {
            Log.LogDebug("[Nav] {Callsign}: no re-aim: no FAA main-gear width for {Type}", ctx.Aircraft.Callsign, ctx.Aircraft.AircraftType);
            return null;
        }

        if (FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.WheelbaseFt is not { } wheelbaseFt)
        {
            Log.LogDebug("[Nav] {Callsign}: no re-aim: no FAA wheelbase for {Type}", ctx.Aircraft.Callsign, ctx.Aircraft.AircraftType);
            return null;
        }

        double boundFt = ReAimCutBoundFt(TurnAboutFitOf(ctx), gearWidthFt, wheelbaseFt);
        Log.LogDebug(
            "[Nav] {Callsign}: bend {Bend:F0}° at node {Node} against the reversal; cut bound {Bound:F1} ft",
            ctx.Aircraft.Callsign,
            bendDeg,
            bendNodeId,
            boundFt
        );
        if (boundFt <= 0.0)
        {
            Log.LogDebug("[Nav] {Callsign}: no re-aim: gear reaches past the type's taxiway half-width", ctx.Aircraft.Callsign);
            return null;
        }

        return boundFt;
    }

    /// <summary>
    /// How far (ft) the path of the main-gear midpoint on a re-aimed cut may stray from a centreline for a type with
    /// <paramref name="fit"/>, a main gear <paramref name="gearWidthFt"/> wide and <paramref name="wheelbaseFt"/> of
    /// wheelbase: its taxiway half-width less how far its gear reaches outside that path — half the main-gear width, or,
    /// when it is further, the nose gear's reach outside a turn of the type's radius R, √(R² + WB²) − R.
    /// </summary>
    public static double ReAimCutBoundFt(TurnAboutFitResult fit, double gearWidthFt, double wheelbaseFt)
    {
        double noseOutsideFt = Math.Sqrt((fit.RadiusFt * fit.RadiusFt) + (wheelbaseFt * wheelbaseFt)) - fit.RadiusFt;
        return fit.HalfWidthFt - Math.Max(gearWidthFt / 2.0, noseOutsideFt);
    }

    /// <summary>
    /// The main-gear width (ft) of <paramref name="aircraftType"/> from its FAA aircraft characteristics record, or null
    /// for a type with no record or a record without the figure: the pavement checks then fail closed.
    /// </summary>
    private static double? MainGearWidthFt(string aircraftType) => FaaAircraftDatabase.Get(aircraftType)?.MainGearWidthFt;

    /// <summary>
    /// The centrelines a re-aimed cut may stay near: <paramref name="edge"/>'s and those of route segments
    /// <paramref name="firstOutgoing"/> through <paramref name="lastOutgoing"/>. Null when one of those segments is a
    /// curve: a leg that bends is not checked, and the reversal is not re-aimed onto it.
    /// </summary>
    private static List<(LatLon A, LatLon B)>? PavedCentrelines(GroundEdge edge, TaxiRoute route, int firstOutgoing, int lastOutgoing)
    {
        List<(LatLon A, LatLon B)> centrelines = [(edge.Nodes[0].Position, edge.Nodes[1].Position)];
        for (int i = firstOutgoing; i <= lastOutgoing; i++)
        {
            TaxiRouteSegment outgoing = route.Segments[i];
            if (outgoing.Edge.Edge is not GroundEdge)
            {
                return null;
            }

            centrelines.Add((outgoing.Edge.FromNode.Position, outgoing.Edge.ToNode.Position));
        }

        return centrelines;
    }

    /// <summary>
    /// Whether every sample of <paramref name="arc"/> and of the straight from its exit to <paramref name="aimNode"/> lies
    /// within <paramref name="boundFt"/> of one of <paramref name="centrelines"/>. The samples are the aircraft's reference
    /// point, and the bound already takes off the further of the main gear's and the nose gear's reach outside that path
    /// (<see cref="ReAimCutBoundFt"/>).
    /// </summary>
    private static bool CutStaysOnPavement(PathPrimitiveSlowTurn arc, LatLon aimNode, List<(LatLon A, LatLon B)> centrelines, double boundFt) =>
        CutSamples(arc, aimNode).All(p => centrelines.Any(l => GeoMath.DistanceToSegmentFt(p, l.A, l.B) <= boundFt));

    /// <summary>
    /// Points every <see cref="CutSampleSpacingFt"/> along <paramref name="arc"/> and the straight from its exit to
    /// <paramref name="aimNode"/>.
    /// </summary>
    private static IEnumerable<LatLon> CutSamples(PathPrimitiveSlowTurn arc, LatLon aimNode)
    {
        var centre = new LatLon(arc.CenterLat, arc.CenterLon);
        double arcFt = arc.RadiusFt * arc.SweepDeg * Math.PI / 180.0;
        int arcSteps = Math.Max(1, (int)Math.Ceiling(arcFt / CutSampleSpacingFt));
        for (int i = 0; i <= arcSteps; i++)
        {
            double sweptDeg = arc.SweepDeg * i / arcSteps;
            double fromCentreDeg = arc.StartBearingFromCenterDeg + (arc.RightTurn ? sweptDeg : -sweptDeg);
            yield return GeoMath.ProjectPoint(centre, new TrueHeading(fromCentreDeg), arc.RadiusNm);
        }

        LatLon exit = PathPrimitiveBuilder.ExitPose(arc).Position;
        double straightFt = GeoMath.DistanceNm(exit, aimNode) * GeoMath.FeetPerNm;
        int straightSteps = Math.Max(1, (int)Math.Ceiling(straightFt / CutSampleSpacingFt));
        for (int i = 1; i <= straightSteps; i++)
        {
            double fraction = (double)i / straightSteps;
            yield return new LatLon(exit.Lat + ((aimNode.Lat - exit.Lat) * fraction), exit.Lon + ((aimNode.Lon - exit.Lon) * fraction));
        }
    }

    /// <summary>
    /// The turn-about fit of the aircraft's type (<see cref="TurnAboutFit"/>): the radius its turn about is drawn on and the
    /// half-width its pavement checks use, the same geometry the taxi gate decided on. The layout carries no taxiway design
    /// group, so the checks assume a taxiway of the type's own group.
    /// </summary>
    private static TurnAboutFitResult TurnAboutFitOf(PhaseContext ctx) => TurnAboutFit.Evaluate(ctx.Aircraft.AircraftType, ctx.Category);

    /// <summary>A bend (deg) at the turn about's aim node sharper than this, against the reversal's sense, may be cut instead.</summary>
    private const double ReAimMinBendDeg = 90.0;

    /// <summary>Spacing (ft) of the samples a re-aimed cut is checked on.</summary>
    private const double CutSampleSpacingFt = 1.0;

    /// <summary>
    /// The jog that centres a turn about on <paramref name="edge"/>'s centreline (<see cref="PathPrimitiveBuilder.TurnAboutJogDeg"/>),
    /// measured in the frame of the aircraft's forward direction along the edge, turned against the reversal's sense
    /// (<paramref name="rightTurn"/>) at the turn about's pivot speed. Null when the aircraft already stands where the
    /// reversal alone is centred.
    /// </summary>
    private static PathPrimitiveSlowTurn? BuildTurnAboutJog(GroundEdge edge, PhaseContext ctx, double radiusFt, int toNodeId, bool rightTurn)
    {
        double headingDeg = ctx.Aircraft.TrueHeading.Degrees;
        double edgeBearingDeg = GeoMath.BearingTo(edge.Nodes[0].Position, edge.Nodes[1].Position);
        double forwardDeg = (GeoMath.AbsBearingDifference(edgeBearingDeg, headingDeg) <= 90.0) ? edgeBearingDeg : edgeBearingDeg + 180.0;
        double offsetRightFt =
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, edge.Nodes[0].Position, new TrueHeading(forwardDeg)) * GeoMath.FeetPerNm;
        double headingOffDeg = GeoMath.SignedBearingDifference(forwardDeg, headingDeg);
        double jogDeg = PathPrimitiveBuilder.TurnAboutJogDeg(offsetRightFt, headingOffDeg, radiusFt, rightTurn);
        if (jogDeg < MinTurnAboutJogDeg)
        {
            return null;
        }

        return PathPrimitiveBuilder.SlowTurnDirected(
            fromLat: ctx.Aircraft.Position.Lat,
            fromLon: ctx.Aircraft.Position.Lon,
            fromHdgDeg: headingDeg,
            toHdgDeg: headingDeg + (rightTurn ? -jogDeg : jogDeg),
            radiusFt: radiusFt,
            maxSpeedKts: CategoryPerformance.TurnAboutSpeedKts(ctx.Category, radiusFt),
            toNodeId: toNodeId,
            rightTurn: !rightTurn
        );
    }

    /// <summary>A turn-about jog smaller than this (deg) is not flown: the reversal alone is already centred.</summary>
    private const double MinTurnAboutJogDeg = 1.0;

    /// <summary>
    /// The taxiway edge a reversal turns about on: the straight edge the aircraft stands inside
    /// (<see cref="AirportGroundLayout.FindOccupiedTaxiEdge"/>: within <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/>
    /// of its centreline, its foot strictly inside the edge) and more than <paramref name="radiusFt"/> from both its end
    /// nodes, when the route reverses back over that same edge — a free-space leg running to one of its end nodes, or the
    /// painted segment being the edge itself — and the edge is a turn-about taxiway (<see cref="IsTurnAboutTaxiway"/>).
    ///
    /// <para>
    /// Null otherwise, and the comfortable-radius aims run: a mid-route reversal at a junction (the aircraft arrives at the
    /// node, at a tangent point on the edge it is leaving, not inside the edge it reverses over, or inside that edge but
    /// within <paramref name="radiusFt"/> of one of its end nodes, where the turn about would not fit before the node) keeps
    /// rounding over the fillet at its corner speed, as do reversals on ramps, aprons, stand lead-ins and runways.
    /// </para>
    /// </summary>
    private GroundEdge? TurnAboutTaxiwayEdge(TaxiRouteSegment seg, PhaseContext ctx, double radiusFt)
    {
        if ((ctx.GroundLayout is not { } layout) || (layout.FindOccupiedTaxiEdge(ctx.Aircraft.Position) is not { } edge))
        {
            return null;
        }

        bool reversesOverIt = _segmentFromIsVirtual ? edge.Nodes.Any(n => n.Id == seg.ToNodeId) : ReferenceEquals(seg.Edge.Edge, edge);
        bool clearOfEnds = edge.Nodes.All(n => (GeoMath.DistanceNm(ctx.Aircraft.Position, n.Position) * GeoMath.FeetPerNm) > radiusFt);
        return (reversesOverIt && clearOfEnds && IsTurnAboutTaxiway(edge, layout)) ? edge : null;
    }

    /// <summary>
    /// Whether a turn about on <paramref name="edge"/> must stay inside its width: a named movement-area taxiway
    /// (<see cref="MovementAreaClassification.IsMovementArea"/>: not apron, not a ramp taxilane), no ramp connector or runway
    /// centreline, touching no parking or helipad node (no stand lead-in).
    /// </summary>
    public static bool IsTurnAboutTaxiway(GroundEdge edge, AirportGroundLayout layout) =>
        !edge.IsRamp
        && !edge.IsRunwayCenterline
        && (edge.TaxiwayName.Length > 0)
        && edge.Nodes.All(n => n.Type is not (GroundNodeType.Parking or GroundNodeType.Helipad))
        && MovementAreaClassification.For(layout).IsMovementArea(edge.TaxiwayName);

    /// <summary>
    /// The route node a free-space alignment arc aims at: walking forward from the to-node of segment
    /// <paramref name="fromSegmentIndex"/> (the current segment, or the leg out of a bend a turn about is re-aimed past), the
    /// first one at least <paramref name="minDistanceFt"/> — the turning circle's diameter — from the aircraft, with the
    /// index of the segment it ends.
    ///
    /// <para>
    /// A node inside the turning circle has no tangent at all (<see cref="PathPrimitiveBuilder.SlowTurnToPoint"/>
    /// returns null for it), and a node just outside one is reached by an arc longer than the leg that runs to
    /// it: the arc rolls out past the node and pure pursuit then re-acquires a line the aircraft has already
    /// left. Aiming at the first node the arc cannot overshoot covers both — the turn rolls out pointing down a
    /// leg the aircraft still has to drive. Null when the route has no such node, leaving the bearing aim.
    /// </para>
    ///
    /// <para>
    /// The walk stops at a BAR whatever the distance: the legs an aimed arc rolls out past are retired by
    /// <see cref="TryRetireLegsTheArcAimedPast"/>, which advances the route's own segment index without running
    /// <c>TaxiingPhase.ArriveAtNode</c> — the AT-ground / AT-taxiway triggers, hold-short insertion and the
    /// pre-cleared-crossing handoff all live there. A hold-short or a runway hold-short node inside the span
    /// would be driven past with none of that having fired, so the walk aims AT the bar instead of past it,
    /// where the arrival still happens normally.
    /// </para>
    /// </summary>
    private static (int SegmentIndex, double Lat, double Lon)? FindAimNode(
        TaxiRoute route,
        PhaseContext ctx,
        int fromSegmentIndex,
        LatLon from,
        double minDistanceFt
    )
    {
        for (int i = fromSegmentIndex; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment segment = route.Segments[i];
            GroundNode to = segment.Edge.ToNode;
            double distFt = GeoMath.DistanceNm(from, to.Position) * GeoMath.FeetPerNm;
            if ((distFt >= minDistanceFt) || IsBarNode(route, ctx, segment.ToNodeId))
            {
                return (i, to.Position.Lat, to.Position.Lon);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="nodeId"/> is a bar an aimed alignment arc may never be solved past: a node the
    /// route carries a <see cref="HoldShortPoint"/> at (cleared or not — a cleared runway crossing still hands
    /// off to <c>CrossingRunwayPhase</c> on arrival), or a runway hold-short node of the layout, which is the
    /// boundary of a runway crossing whether or not the route annotated it.
    /// </summary>
    private static bool IsBarNode(TaxiRoute route, PhaseContext ctx, int nodeId) =>
        (route.GetHoldShortAt(nodeId) is not null)
        || (
            (ctx.GroundLayout is { } layout)
            && layout.Nodes.TryGetValue(nodeId, out GroundNode? node)
            && (node.Type == GroundNodeType.RunwayHoldShort)
        );

    /// <summary>
    /// The signed turn (deg, right positive) the route makes immediately after the aircraft has aligned with
    /// <paramref name="seg"/>'s first tangent: the segment's own sweep when it is a fillet arc, otherwise the
    /// bend from its arrival bearing onto the next segment's departure bearing. Zero at the end of the route,
    /// where nothing follows the entry to compound with it. Pure.
    /// </summary>
    private static double SignedTurnAfterEntry(TaxiRoute route, TaxiRouteSegment seg)
    {
        if (seg.Edge.Edge is GroundArc)
        {
            return GeoMath.SignedBearingDifference(seg.Edge.DepartureBearing, seg.Edge.ArrivalBearing);
        }

        int nextIndex = route.CurrentSegmentIndex + 1;
        return nextIndex < route.Segments.Count
            ? GeoMath.SignedBearingDifference(seg.Edge.ArrivalBearing, route.Segments[nextIndex].Edge.DepartureBearing)
            : 0.0;
    }

    /// <summary>
    /// Whether an entry turn of <paramref name="dthetaDeg"/> (signed, right positive) should be swept against
    /// its short way because the route's next turn (<paramref name="nextTurnDeg"/>, signed) runs the same way.
    ///
    /// <para>
    /// Three conditions. The entry must be a reversal (<see cref="ReversalEntryThresholdDeg"/>) — only then is
    /// the direction free to choose. The next turn must run the same sense, or there is nothing to compound
    /// with. And the two sweeps taken the short way — <c>|dtheta| + |next|</c> — must exceed what the other way
    /// round costs: turning against the short way sweeps <c>360 - |dtheta|</c> and then unwinds by the next
    /// turn, so the comparison rearranges to <c>2·|dtheta| + |next| &gt; 360</c>. UAL58 off SFO spot 9 scores
    /// 2·180 + 90 = 450 and flips; an ordinary 60° entry into a 90° corner scores 120 + 90 = 210 and does not.
    /// </para>
    /// </summary>
    private static bool ShouldReverseAgainstShortWay(double dthetaDeg, double nextTurnDeg) =>
        (Math.Abs(dthetaDeg) >= ReversalEntryThresholdDeg)
        && (nextTurnDeg != 0.0)
        && (Math.Sign(nextTurnDeg) == Math.Sign(dthetaDeg))
        && (((2.0 * Math.Abs(dthetaDeg)) + Math.Abs(nextTurnDeg)) > 360.0);

    /// <summary>
    /// Anchor a free-space leg's line on the aircraft's own position. The leg runs from a virtual node fixed
    /// where the aircraft stood when the route was built, not from a painted node the layout holds: by the time
    /// the straight takes over the aircraft is at the alignment arc's exit, and after a snapshot restore it is
    /// wherever it now stands while <see cref="TaxiRoute.FromSnapshot"/> has rebuilt the virtual node at its
    /// ORIGINAL position. Steering to the line through that stale origin chases a line the aircraft left;
    /// anchored here, the line runs from the aircraft straight onto the leg's node.
    ///
    /// <para>
    /// A painted leg entered off a node-aimed REVERSAL (<see cref="_entryArcAimedAtNodeOffRealLeg"/>) is
    /// anchored the same way and for the same reason: the arc has turned the aircraft around a turning circle
    /// clear of the leg's from-node and rolled out pointing at its to-node, so the leg's own line now runs
    /// BEHIND the aircraft and pure pursuit would swing back toward it.
    /// </para>
    /// </summary>
    private void ReanchorFreeSpaceLine(PhaseContext ctx)
    {
        if (!_segmentFromIsVirtual && !_entryArcAimedAtNodeOffRealLeg)
        {
            return;
        }

        _segmentFromLat = ctx.Aircraft.Position.Lat;
        _segmentFromLon = ctx.Aircraft.Position.Lon;
    }

    /// <summary>
    /// A fillet or slow-turn primitive mirrors its tangent into <see cref="ControlTargets.TargetTrueHeading"/> so
    /// physics does not fight the closed-form playback; a straight is steered by pure pursuit writing the heading
    /// directly. The target is persistent on the aircraft, so it must be released when a straight takes over:
    /// otherwise physics turns the nose back toward the stale tangent every substep, the navigator nudges it out
    /// again, the aircraft drifts off a straight that leaves the fillet a fraction of a degree off its exit
    /// tangent, and the orbit guard (which sees only the navigator's half of the tug-of-war) declares a full
    /// circle on an aircraft that never turned.
    /// </summary>
    private static void ReleaseHeadingHold(PhaseContext ctx, PathPrimitive primitive)
    {
        if (primitive is PathPrimitiveStraight)
        {
            ctx.Targets.TargetTrueHeading = null;
        }
    }

    /// <summary>
    /// Start playing <paramref name="primitive"/>: reset the playback state it advances and arm the
    /// entry capture, which happens on the primitive's first tick (<see cref="_arcEntryPending"/>) rather
    /// than here. Called wherever a primitive becomes current — both <see cref="SetupSegment"/> branches and
    /// the entry-alignment swap in <see cref="Tick"/>.
    /// </summary>
    private void BeginPrimitive(PathPrimitive primitive)
    {
        _arcEntryTravelledFt = 0.0;
        _arcEntryOffsetLatDeg = 0.0;
        _arcEntryOffsetLonDeg = 0.0;
        _arcEntryBlendFt = ArcEntryBlendFt;
        _bezierLeadInRemainingFt = 0.0;

        switch (primitive)
        {
            case PathPrimitiveSlowTurn slowTurn:
                _arcBearingFromCenterDeg = slowTurn.StartBearingFromCenterDeg;
                _arcRemainingSweepDeg = slowTurn.SweepDeg;
                _arcEntryPending = true;
                break;

            case PathPrimitiveBezier:
                _bezierT = 0.0;
                _bezierTraveledFt = 0.0;
                _arcEntryPending = true;
                break;

            default:
                _arcRemainingSweepDeg = 0.0;
                _arcEntryPending = false;
                break;
        }
    }

    /// <summary>
    /// Capture a slow-turn's entry offset on its first tick: the arc was solved from the aircraft's pose, so
    /// its progress needs no re-projection — only the displacement accumulated since the solve (the physics
    /// step between the install and this tick) is carried, and bled off over the entry blend.
    /// </summary>
    private void CaptureSlowTurnEntry(PhaseContext ctx, PathPrimitiveSlowTurn prim)
    {
        LatLon entry = GeoMath.ProjectPoint(new LatLon(prim.CenterLat, prim.CenterLon), new TrueHeading(_arcBearingFromCenterDeg), prim.RadiusNm);
        _arcEntryTravelledFt = 0.0;
        CaptureArcEntryOffset(ctx, entry.Lat, entry.Lon, prim.Kind, prim.LengthFt);
    }

    /// <summary>
    /// Set a Bézier primitive's playback progress from where the aircraft actually stands, on the primitive's
    /// first tick. A curve whose playback restarted at <c>t = 0</c> wrote the aircraft back onto its start
    /// point on that tick — a rewind of the whole distance already covered whenever the primitive is rebuilt
    /// mid-curve (a snapshot written before the navigator carried its playback state, so
    /// <c>TaxiingPhase</c> rebuilds the primitive from the route's segment index). Standing within
    /// <see cref="AirportGroundLayout.AtNodeToleranceFt"/> of the curve's start point is the normal entry,
    /// which starts at <c>t = 0</c> with the residual cross-track as the entry offset.
    ///
    /// <para>
    /// An aircraft still <em>short of</em> the start point along the entry tangent is the third case: the
    /// projection lands on (or beside) <c>t = 0</c> and the shortfall is distance it has yet to drive, not a
    /// displacement to bleed off. That part becomes the lead-in (<see cref="_bezierLeadInRemainingFt"/>) and
    /// only the cross-track remainder is captured as the entry offset.
    /// </para>
    /// </summary>
    private void ResumeBezierFromPosition(PhaseContext ctx, PathPrimitiveBezier bezier)
    {
        var curveStart = new LatLon(bezier.Curve.P0Lat, bezier.Curve.P0Lon);
        double fromStartFt = GeoMath.DistanceNm(ctx.Aircraft.Position, curveStart) * GeoMath.FeetPerNm;

        if (fromStartFt > AirportGroundLayout.AtNodeToleranceFt)
        {
            _bezierT = bezier.Curve.ClosestT(ctx.Aircraft.Position, BezierResumeIterations);
            _bezierTraveledFt = bezier.Curve.ArcLengthToNm(_bezierT, BezierResumeArcLengthSteps) * GeoMath.FeetPerNm;
        }
        else
        {
            _bezierT = 0.0;
            _bezierTraveledFt = 0.0;
        }

        _arcEntryTravelledFt = 0.0;
        _bezierLeadInRemainingFt = LeadInShortfallFt(ctx.Aircraft.Position, bezier);
        (double lat, double lon, double _) = BezierPlaybackPose(bezier);
        double remainingArcLengthFt = _bezierLeadInRemainingFt + Math.Max(0.0, bezier.LengthFt - _bezierTraveledFt);
        CaptureArcEntryOffset(ctx, lat, lon, bezier.Kind, remainingArcLengthFt);

        if ((fromStartFt > AirportGroundLayout.AtNodeToleranceFt) || (_bezierLeadInRemainingFt > 0.0))
        {
            Log.LogDebug(
                "[Nav] Bezier entry: cs={Callsign} t={T:F3} traveled={Trav:F1}ft leadIn={LeadIn:F1}ft "
                    + "(aircraft {Dist:F1}ft from the curve start)",
                ctx.Aircraft.Callsign,
                _bezierT,
                _bezierTraveledFt,
                _bezierLeadInRemainingFt,
                fromStartFt
            );
        }
    }

    /// <summary>
    /// How far short of the curve's start point the aircraft is along the entry tangent, in feet — 0 unless
    /// the playback is starting at (or beside) that start point and the aircraft is still measurably behind
    /// it. Anything at or below <see cref="TeleportToleranceFt"/> is rounding, not a drive.
    /// </summary>
    private double LeadInShortfallFt(LatLon position, PathPrimitiveBezier bezier)
    {
        (double Lat, double Lon) playbackStart = bezier.Curve.Evaluate(_bezierT);
        var curveStart = new LatLon(bezier.Curve.P0Lat, bezier.Curve.P0Lon);
        double fromPlaybackStartFt = GeoMath.DistanceNm(new LatLon(playbackStart.Lat, playbackStart.Lon), curveStart) * GeoMath.FeetPerNm;
        if (fromPlaybackStartFt > AirportGroundLayout.AtNodeToleranceFt)
        {
            return 0.0;
        }

        double entryTangentDeg = bezier.Curve.TangentBearing(0.0);
        double distFt = GeoMath.DistanceNm(position, curveStart) * GeoMath.FeetPerNm;
        double deltaDeg = GeoMath.SignedBearingDifference(GeoMath.BearingTo(position, curveStart), entryTangentDeg);
        double alongFt = distFt * Math.Cos(deltaDeg * (Math.PI / 180.0));

        return alongFt > TeleportToleranceFt ? alongFt : 0.0;
    }

    /// <summary>
    /// Record how far the aircraft stands from the point an arc primitive is about to play from, as a
    /// constant lat/lon offset the playback adds to every position it writes until the blend distance
    /// (<see cref="_arcEntryBlendFt"/>, sized here from the offset) has bled it off. The blend never outlasts
    /// <paramref name="remainingArcLengthFt"/>, the travel the primitive has left, so playback still reaches
    /// the to-node on the painted line. An offset past <see cref="MaxArcEntryOffsetFt"/> is refused with its
    /// own message before the blend is set up: at that distance the route handed the playback a curve the
    /// aircraft is not on, which no blend rate repairs.
    /// </summary>
    private void CaptureArcEntryOffset(PhaseContext ctx, double curveLat, double curveLon, PathPrimitiveKind kind, double remainingArcLengthFt)
    {
        _arcEntryOffsetLatDeg = ctx.Aircraft.Position.Lat - curveLat;
        _arcEntryOffsetLonDeg = ctx.Aircraft.Position.Lon - curveLon;

        double offsetFt = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(curveLat, curveLon)) * GeoMath.FeetPerNm;
        double rateCappedFt = Math.Max(ArcEntryBlendFt, offsetFt / Math.Tan(MaxBlendTrackDeg * (Math.PI / 180.0)));
        _arcEntryBlendFt = remainingArcLengthFt > 0.0 ? Math.Min(rateCappedFt, remainingArcLengthFt) : rateCappedFt;

        if (offsetFt <= MaxArcEntryOffsetFt)
        {
            return;
        }

        string message =
            $"[Nav] arc entry: {ctx.Aircraft.Callsign} is {offsetFt:F1} ft from the {kind} it was told to play — "
            + "the route started a curve the aircraft is not on";

        if (ThrowOnTeleport)
        {
            throw new InvalidOperationException(message);
        }

        Log.LogError("{ArcEntryMessage}", message);
    }

    /// <summary>
    /// The fraction of the captured arc-entry offset still applied after <paramref name="traveledSinceEntryFt"/>
    /// of travel along the primitive: 1 at the entry, falling linearly to 0 over the blend distance sized at
    /// the entry (<see cref="_arcEntryBlendFt"/>). Position stays a pure function of playback progress plus
    /// one constant captured at the entry, so invariant I2 (position and heading cannot drift apart) holds.
    /// </summary>
    private double ArcEntryBlendFactor(double traveledSinceEntryFt) => Math.Max(0.0, 1.0 - (traveledSinceEntryFt / _arcEntryBlendFt));

    /// <summary>
    /// Assert that closed-form arc playback wrote the aircraft no further than it drove this sub-tick.
    /// <paramref name="dsFt"/> is the arc-length the integrator advanced; anything materially beyond it is a
    /// position the aircraft did not taxi to — an offset applied whole rather than bled off, a restore
    /// resuming at the wrong progress, or a primitive built from the wrong pose. Throws under
    /// <see cref="ThrowOnTeleport"/> (tests), logs an error otherwise (the shipping app).
    /// </summary>
    internal static void CheckNoTeleport(string callsign, LatLon before, LatLon after, double dsFt, PathPrimitiveKind kind)
    {
        double movedFt = GeoMath.DistanceNm(before, after) * GeoMath.FeetPerNm;
        if (movedFt <= dsFt + TeleportToleranceFt)
        {
            return;
        }

        string message =
            $"[Nav] teleport: {callsign} moved {movedFt:F1} ft in one sub-tick on a {kind} that advanced {dsFt:F1} ft — "
            + $"closed-form playback wrote the aircraft somewhere it did not drive to. "
            + $"from=({before.Lat:F6},{before.Lon:F6}) to=({after.Lat:F6},{after.Lon:F6}).";

        if (ThrowOnTeleport)
        {
            throw new InvalidOperationException(message);
        }

        Log.LogError("{TeleportMessage}", message);
    }

    public NavigatorResult Tick(PhaseContext ctx, bool isLastSegment, Func<int, bool> isHoldShortCleared)
    {
        _restoredPlayback = null;
        double headingBeforeDeg = ctx.Aircraft.TrueHeading.Degrees;
        _stopCurveBinds = false;

        NavigatorResult result = _currentPrimitive switch
        {
            PathPrimitiveStraight s => TickStraight(ctx, s, isLastSegment, isHoldShortCleared),
            PathPrimitiveBezier b => TickBezier(ctx, b, isHoldShortCleared),
            PathPrimitiveSlowTurn t => TickSlowTurn(ctx, t),
            _ => NavigatorResult.ArrivedAtNode,
        };

        if (OrbitLimitReached(ctx, headingBeforeDeg))
        {
            return NavigatorResult.ArrivedAtNode;
        }

        if ((result == NavigatorResult.ArrivedAtNode) && TryEngagePendingTurnAbout(ctx))
        {
            return NavigatorResult.Navigating;
        }

        // When the entry-alignment slow-turn finishes, swap in the deferred
        // segment primitive and continue navigating in the same tick. The
        // synthetic arrival is internal — the route's own segment counter
        // hasn't advanced yet.
        if (result == NavigatorResult.ArrivedAtNode && _pendingSegmentPrimitive is not null)
        {
            CompleteEntryAlignment(ctx, isHoldShortCleared);
            return NavigatorResult.Navigating;
        }

        return result;
    }

    /// <summary>
    /// A turn about's jog has finished: engage the reversal parked behind it (<see cref="_pendingTurnAboutArc"/>), which
    /// starts on the jog's exit tangent, and report the jog's arrival as internal, as the entry-alignment swap does. False,
    /// changing nothing, when no reversal is parked.
    /// </summary>
    private bool TryEngagePendingTurnAbout(PhaseContext ctx)
    {
        if (_pendingTurnAboutArc is not { } reversal)
        {
            return false;
        }

        _pendingTurnAboutArc = null;
        _turnAboutReversalPlaying = true;
        _currentPrimitive = reversal;
        BeginPrimitive(reversal);
        PrevDistToTarget = double.MaxValue;
        Log.LogDebug("[Nav] {Callsign}: turn-about jog complete; engaging the {Sweep:F0}° reversal", ctx.Aircraft.Callsign, reversal.SweepDeg);
        return true;
    }

    /// <summary>
    /// Orbit invariant: accumulate net signed heading change within the current primitive and hard-fail
    /// if it reaches a full circle without advancing. No legitimate single-segment maneuver nets 360°
    /// (an arc sweeps &lt;180° by admissibility, a straight ~0°, and a slow-turn &lt;180° except a point-aimed
    /// alignment arc, which may sweep up to PathPrimitiveBuilder.MaxAimSweepDeg), so crossing it means
    /// the navigator is circling a node it cannot converge on — a pure-pursuit orbit that would otherwise
    /// crawl indefinitely at the slow-turn floor. Surfacing it as a throw makes every such case a hard
    /// test failure with an actionable message instead of a silent slow taxi. Returns true when the shipping
    /// app has logged the breach and the tick is to report an arrival past the node.
    /// </summary>
    private bool OrbitLimitReached(PhaseContext ctx, double headingBeforeDeg)
    {
        _cumulativeTurnSinceAdvanceDeg += GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, headingBeforeDeg);
        if (Math.Abs(_cumulativeTurnSinceAdvanceDeg) < OrbitTurnLimitDeg)
        {
            return false;
        }

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
        return true;
    }

    /// <summary>
    /// The entry-alignment slow-turn has finished: retire the legs it was aimed past, or hand a fillet it was aimed
    /// at the end of over on the aimed line, or else engage the deferred segment primitive. Whichever it does, the offset
    /// line a turn about held (<see cref="_turnAboutRollOutOffsetFt"/>) ends with the turn it was kept for, before any
    /// segment set-up the retirement runs could carry it on.
    /// </summary>
    private void CompleteEntryAlignment(PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        _turnAboutRollOutOffsetFt = 0.0;
        if (TryRetireLegsTheArcAimedPast(ctx, isHoldShortCleared) || TryHandOverOnAimedLine(ctx, isHoldShortCleared))
        {
            return;
        }

        PathPrimitive? completed = _currentPrimitive;
        PathPrimitive seg = _pendingSegmentPrimitive!;
        _pendingSegmentPrimitive = null;
        _currentPrimitive = seg;
        ReleaseHeadingHold(ctx, seg);
        if (!TryLayTurnAboutRollOutLine(ctx, completed))
        {
            ReanchorFreeSpaceLine(ctx);
        }

        BeginPrimitive(seg);
        PrevDistToTarget = double.MaxValue;
        // A new primitive begins — give it its own full-circle budget so a legitimate
        // entry-alignment turn plus the segment's own turn don't sum across the swap.
        _cumulativeTurnSinceAdvanceDeg = 0.0;
        Log.LogDebug("[Nav] Entry alignment complete; engaging real segment primitive {Kind}", seg.Kind);
    }

    /// <summary>
    /// Lay the straight after a turn-about reversal that rolled out on its edge's bearing
    /// (<see cref="_turnAboutReversalOnEdgeBearing"/>). When the straight ends in a stop, it is laid on the centreline
    /// through the stop, from abeam the aircraft (<see cref="LayStraightSquareToStop"/>), so the aircraft stops on the
    /// centreline square to the bar. Otherwise, when the plan holds the roll-out bearing
    /// (<see cref="_turnAboutRollsOutAlongEdge"/>), it is laid along that bearing from the aircraft to abeam the segment's
    /// to-node: the reversal ends off the centreline on the side the route turns to at the node
    /// (<see cref="HoldsRollOutBearing"/>), so the aircraft holds its heading into that turn. Returns false, changing
    /// nothing, when <paramref name="completed"/> was no such reversal; returns false with the flags cleared, leaving the
    /// line re-centred as after any other reversal, when the plan does not hold the bearing or the node or the stop is not
    /// ahead of the aircraft.
    /// </summary>
    private bool TryLayTurnAboutRollOutLine(PhaseContext ctx, PathPrimitive? completed)
    {
        if (!_turnAboutReversalOnEdgeBearing || (completed is not PathPrimitiveSlowTurn reversal))
        {
            return false;
        }

        bool holdsBearing = _turnAboutRollsOutAlongEdge;
        _turnAboutReversalOnEdgeBearing = false;
        _turnAboutRollsOutAlongEdge = false;
        LatLon from = ctx.Aircraft.Position;
        var bearing = new TrueHeading(reversal.ExitTangentBearingDeg);
        if (_currentNodeRequiredSpeed <= 0.0)
        {
            TrueHeading centreline = (_currentPrimitive is PathPrimitiveStraight edgeLine) ? new TrueHeading(edgeLine.BearingDeg) : bearing;
            Log.LogDebug(
                "[Nav] {Callsign}: turn about rolled out toward a stop at node {Node}; re-centring on the centreline {Centreline:F1}° "
                    + "through it instead of holding {Bearing:F1}°",
                ctx.Aircraft.Callsign,
                TargetNodeId,
                centreline.Degrees,
                bearing.Degrees
            );
            return LayStraightSquareToStop(new LatLon(TargetLat, TargetLon), from, centreline);
        }

        if (!holdsBearing)
        {
            return false;
        }

        double aheadNm = GeoMath.AlongTrackDistanceNm(new LatLon(TargetLat, TargetLon), from, bearing);
        if (aheadNm <= 0.0)
        {
            Log.LogDebug(
                "[Nav] {Callsign}: turn about rolled out past abeam node {Node}; re-centring instead of holding {Bearing:F1}°",
                ctx.Aircraft.Callsign,
                TargetNodeId,
                bearing.Degrees
            );
            return false;
        }

        LatLon abeam = GeoMath.ProjectPoint(from, bearing, aheadNm);
        _turnAboutRollOutOffsetFt = GeoMath.DistanceNm(abeam, new LatLon(TargetLat, TargetLon)) * GeoMath.FeetPerNm;
        _segmentFromLat = from.Lat;
        _segmentFromLon = from.Lon;
        TargetLat = abeam.Lat;
        TargetLon = abeam.Lon;
        Log.LogDebug(
            "[Nav] {Callsign}: turn about rolled out on {Bearing:F1}°; holding it {Ahead:F0} ft to abeam node {Node}, "
                + "{Offset:F1} ft inside the turn there",
            ctx.Aircraft.Callsign,
            bearing.Degrees,
            aheadNm * GeoMath.FeetPerNm,
            TargetNodeId,
            _turnAboutRollOutOffsetFt
        );
        return true;
    }

    /// <summary>
    /// Hand an alignment arc aimed at the current segment's OWN to-node over on the line it rolled out on, when that
    /// segment is a fillet: the arc's exit tangent passes through the fillet's far end, not along its curve, so the
    /// fillet is flown as that line (<see cref="InstallAimedLineOverFillet"/>). The counterpart of
    /// <see cref="TryRetireLegsTheArcAimedPast"/> for an arc aimed at its own segment's end. Returns false (leaving the
    /// ordinary swap to run) when the arc was aimed at a bearing, past the current segment, or onto a segment that is
    /// not a fillet.
    /// </summary>
    private bool TryHandOverOnAimedLine(PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        if (
            (_pendingSegmentPrimitive is not PathPrimitiveBezier)
            || (_alignmentRoute is not { } aimedRoute)
            || (_nodeAimSegmentIndex != aimedRoute.CurrentSegmentIndex)
            || (aimedRoute.CurrentSegment is not { Edge.Edge: GroundArc } aimedFillet)
        )
        {
            return false;
        }

        InstallAimedLineOverFillet(aimedRoute, aimedFillet, ctx, isHoldShortCleared, ctx.Aircraft.Position);
        return true;
    }

    /// <summary>
    /// Retire the legs an aimed entry-alignment arc rolled out past, when one has just completed. The arc is
    /// aimed at the first route node it cannot overshoot (<see cref="FindAimNode"/>), so any leg between the
    /// segment being aligned onto and that node is behind the aircraft by the time the arc ends. Leaving one of
    /// them current would send the straight that follows back to a node the aircraft has already driven past —
    /// at SFO gate G10 that is a 21 ft ramp leg the 169° arc ends 50 ft beyond, and chasing it cost another 84°
    /// of turn away from the route. Setting up the aimed segment instead leaves the aircraft already pointing
    /// down the leg it has to drive. Returns false (leaving the ordinary swap to run) when the active arc was
    /// aimed at its own segment's to-node or at a bearing.
    /// </summary>
    private bool TryRetireLegsTheArcAimedPast(PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        if (_alignmentRoute is not { } route || _aimedPastThroughSegmentIndex <= route.CurrentSegmentIndex)
        {
            return false;
        }

        Log.LogDebug(
            "[Nav] Entry alignment complete; retiring segments {From}..{To} the aimed arc rolled out past",
            route.CurrentSegmentIndex,
            _aimedPastThroughSegmentIndex - 1
        );

        _pendingSegmentPrimitive = null;
        route.CurrentSegmentIndex = _aimedPastThroughSegmentIndex;

        if (route.CurrentSegment is { Edge.Edge: GroundArc } aimedFillet)
        {
            GroundNode filletEnd = aimedFillet.Edge.ToNode;
            TargetNodeId = aimedFillet.ToNodeId;
            TargetLat = filletEnd.Position.Lat;
            TargetLon = filletEnd.Position.Lon;
            InstallAimedLineOverFillet(route, aimedFillet, ctx, isHoldShortCleared, ctx.Aircraft.Position);
            return true;
        }

        // SetupSegment clears the aim bookkeeping before it builds anything, so this retirement cannot cascade:
        // the arc has rolled out on the line to the aimed segment's own to-node, and whatever that segment
        // installs is a fresh aim solved from where the aircraft now stands.
        SetupSegment(route, ctx, isHoldShortCleared);
        return true;
    }

    /// <summary>
    /// Hand a node-aimed alignment arc over to the fillet segment <paramref name="fillet"/> it was aimed at the far end
    /// of, on the straight line it rolled out on rather than on the fillet's curve. The arc's exit tangent passes
    /// through the fillet's to-node, so the aircraft now points straight at it from somewhere off the curve — at SFO
    /// gate E2 ~50 ft from the fillet's start and ~27 ft abeam it. Played as its Bézier from the nearest curve point,
    /// that offset would be bled off over the few feet of arc left, far faster than the aircraft drives (invariant
    /// I8). The straight runs from the live position onto the to-node, which is where the route's next segment starts,
    /// and arrives there through the ordinary straight arrival, so the owning phase's node arrival still fires.
    ///
    /// <para>
    /// <paramref name="lineFrom"/> anchors the line: the live position when an alignment arc hands over, the line's
    /// original anchor when a snapshot restored mid-way along it rebuilds it. The target node is the caller's.
    /// </para>
    /// </summary>
    private void InstallAimedLineOverFillet(
        TaxiRoute route,
        TaxiRouteSegment fillet,
        PhaseContext ctx,
        Func<int, bool> isHoldShortCleared,
        LatLon lineFrom
    )
    {
        GroundNode filletEnd = fillet.Edge.ToNode;
        var straight = new PathPrimitiveStraight
        {
            Kind = PathPrimitiveKind.Straight,
            LengthFt = GeoMath.DistanceNm(lineFrom, filletEnd.Position) * GeoMath.FeetPerNm,
            ToNodeId = fillet.ToNodeId,
            FromLat = lineFrom.Lat,
            FromLon = lineFrom.Lon,
            ToLat = filletEnd.Position.Lat,
            ToLon = filletEnd.Position.Lon,
            BearingDeg = GeoMath.BearingTo(lineFrom, filletEnd.Position),
        };

        _pendingSegmentPrimitive = null;
        _currentPrimitive = straight;
        _segmentFromLat = lineFrom.Lat;
        _segmentFromLon = lineFrom.Lon;
        _segmentFromIsVirtual = false;
        ResetAimAndTurnAboutState();
        _onAimedLineOverFillet = true;
        _aimedLineFilletFromNodeId = fillet.FromNodeId;
        _segmentFromNodeId = fillet.FromNodeId;
        PrevDistToTarget = double.MaxValue;
        _cumulativeTurnSinceAdvanceDeg = 0.0;
        ReleaseHeadingHold(ctx, straight);
        BeginPrimitive(straight);
        BuildSpeedConstraints(route, ctx, isHoldShortCleared);
        Log.LogDebug(
            "[Nav] seg={SegIdx}/{Total}: fillet {FromId}->{ToId} flown as the aimed line ({LengthFt:F0} ft from ({Lat:F6},{Lon:F6}))",
            route.CurrentSegmentIndex,
            route.Segments.Count,
            fillet.FromNodeId,
            fillet.ToNodeId,
            straight.LengthFt,
            lineFrom.Lat,
            lineFrom.Lon
        );
    }

    /// <summary>
    /// Corner-rounding radius (ft) for a turn of <paramref name="deflectionDeg"/> whose approach and
    /// departure legs are <paramref name="incomingRunFt"/> / <paramref name="outgoingRunFt"/> long.
    /// Tightens from the comfortable main-gear turn radius toward the tight-turn floor when the shorter leg
    /// can't contain the comfortable tangent length (the radius whose tangent T = r·tan(δ/2) fits the
    /// shorter leg), so the rounding arc exits on the outgoing centerline rather than finishing wide.
    /// Returns the comfortable radius for a near-straight turn. Pure.
    /// </summary>
    internal static double AdaptiveCornerRadiusFt(AircraftCategory category, double deflectionDeg, double incomingRunFt, double outgoingRunFt)
    {
        double comfortable = CategoryPerformance.MainGearTurnRadiusFt(category);
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
        double comfortableTangentNm = CategoryPerformance.MainGearTurnRadiusFt(category) * halfTan / GeoMath.FeetPerNm;
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

    /// <summary>
    /// Arrival threshold (nm) for the straight a turn about holds on its roll-out bearing <paramref name="offsetFt"/> (d)
    /// inside the coming turn of <paramref name="cornerTurnDeg"/> (δ) onto a straight leg: the tangent length that lays the
    /// node turn of radius <paramref name="roundingRadiusFt"/> (R) tangent to both the offset line and the outgoing
    /// centreline, so the arc exits on that centreline with no offset left for pure pursuit to steer out.
    ///
    /// <para>
    /// Derivation. Put the node at the origin, the incoming centreline along +x and the outgoing centreline leaving the
    /// node at δ toward the inside, where the offset line runs at y = d. An arc of radius R tangent to both lines has its
    /// centre R from each: at y = d + R, and R inside the outgoing line, so x_c·sin δ = (d + R)·cos δ − R. The arc leaves
    /// the offset line at x = x_c = −R·tan(δ/2) + d·cot δ, which is R·tan(δ/2) − d·cot δ short of abeam the node: the
    /// plain tangent length R·tan(δ/2) when d = 0, shorter for a bend under 90° (the inside line meets the outgoing one
    /// sooner) and longer for one over 90°. It joins the outgoing centreline R·tan(δ/2) + d / sin δ beyond the node and
    /// sweeps δ. With the plain tangent length instead, the arc ends d·cos δ off the outgoing centreline, inside it for a
    /// bend under 90° and across it for one over 90° (N152SP at KOAK: 8 ft inside a 104° bend, 2 ft across H's line, then
    /// steered 15° past H's bearing to get back). Clamped to the final-node floor and to <paramref name="edgeLengthNm"/>,
    /// the held line itself, which begins where the aircraft rolled out.
    /// </para>
    /// </summary>
    internal static double OffsetLineArrivalThresholdNm(double cornerTurnDeg, double edgeLengthNm, double roundingRadiusFt, double offsetFt)
    {
        double deltaRad = cornerTurnDeg * Math.PI / 180.0;
        double tangentFt = (roundingRadiusFt * Math.Tan(deltaRad / 2.0)) - (offsetFt / Math.Tan(deltaRad));
        return Math.Clamp(tangentFt / GeoMath.FeetPerNm, FinalNodeArrivalThresholdNm, Math.Max(FinalNodeArrivalThresholdNm, edgeLengthNm));
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
            (LatLon foot, double along, bool _) = GeoMath.FootOfPerpendicular(
                ctx.Aircraft.Position,
                new LatLon(_segmentFromLat, _segmentFromLon),
                new LatLon(TargetLat, TargetLon)
            );
            alongNm = along;
            crossTrackOffsetFt = GeoMath.DistanceNm(ctx.Aircraft.Position, foot) * GeoMath.FeetPerNm;
        }

        // Tight arrival threshold when any of:
        //   - last segment of the route (always stop precisely),
        //   - the current target is a stop (_currentNodeRequiredSpeed <= 0),
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
        bool isStopTarget = _currentNodeRequiredSpeed <= 0.0;

        // Tangent corner-rounding: when a SHARP turn onto the next (straight)
        // segment is coming up, arrive at the tangent point T = r·tan(δ/2)
        // before the vertex (r = main-gear radius, δ = corner deflection)
        // instead of at the vertex. The next segment's entry-alignment slow-turn
        // then anchors at that tangent point, so its main-gear-turn-radius arc is
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
        if (sharpCornerAhead && (_turnAboutRollOutOffsetFt > 0.0))
        {
            arrivalThresholdNm = OffsetLineArrivalThresholdNm(cornerTurnDeg, edgeLengthNm, _cornerRoundingRadiusFt, _turnAboutRollOutOffsetFt);
        }

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
                double maxTurn = CategoryPerformance.GroundYawRateAtSpeed(ctx.Category, ctx.Aircraft.GroundSpeed) * ctx.DeltaSeconds;
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
                LookAheadFloorFt(ctx.Category),
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
            if (_turnAboutSquareStopLine && isStopTarget && (lookAheadAlongNm >= edgeLengthNm - 1e-9))
            {
                (double pastLat, double pastLon) = GeoMath.ProjectPointRaw(
                    new LatLon(_segmentFromLat, _segmentFromLon),
                    segBearingDeg,
                    alongNm + lookAheadNm
                );
                bearingToSteerDeg = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(pastLat, pastLon));
            }
            else if (lookAheadAlongNm >= edgeLengthNm - 1e-9)
            {
                bearingToSteerDeg = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
            }
            else
            {
                (double lookLat, double lookLon) = GeoMath.ProjectPointRaw(
                    new LatLon(_segmentFromLat, _segmentFromLon),
                    segBearingDeg,
                    lookAheadAlongNm
                );
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

        double maxTurnDeg = CategoryPerformance.GroundYawRateAtSpeed(ctx.Category, ctx.Aircraft.GroundSpeed) * ctx.DeltaSeconds;
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
        // than ~80% of the remaining distance in a single tick. It applies only
        // where the aircraft must arrive at the node PRECISELY rather than merely
        // pass it:
        //   - a stop target (the hold-short bar it has to come to rest on),
        //   - the last segment of the route (same),
        //   - an arc entry — TickBezier writes position from curve state at
        //     engagement (invariant I2), so arriving late here teleports the
        //     aircraft up to the arrival threshold onto the arc's start point.
        // A pass-through node on a straight polyline needs none of that: the
        // advance-on-pass rule above retires the segment as soon as the
        // along-track projection passes the node, and the overshoot watchdog
        // backstops the rest. Applying it there instead ratchets the speed down
        // a chord chain — every chord of the SFO 28R taxiway-T exit is short
        // enough to arrive on the tight 1.8 ft threshold, so the cap (~1.9 kt per
        // remaining foot at Δt = 0.25 s) slashed the target over the last ~16 ft
        // of each one, and physics could not recover the loss at the 1.0 kt/s
        // taxi accel rate before the next node (SKW3398: 37 kt down to 17, 39 s
        // of runway occupancy over 1,242 ft).
        if (ctx.DeltaSeconds > 0 && distNm > 0 && (isStopTarget || isLastSegment || _nextSegmentIsArc))
        {
            double maxSpeedForDist = distNm * 0.8 / ctx.DeltaSeconds * 3600.0;
            targetSpeed = Math.Min(targetSpeed, maxSpeedForDist);
        }

        PublishSpeed(ctx, targetSpeed);

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
        LatLon positionBefore = ctx.Aircraft.Position;

        // First tick of this primitive: resolve where along the curve the aircraft stands and capture the
        // entry offset from its live position — after the physics step the install could not see.
        if (_arcEntryPending)
        {
            ResumeBezierFromPosition(ctx, prim);
            _arcEntryPending = false;
        }

        // Speed floor (I7: no pivot-in-place). If effectively stopped, hold the current tangent
        // and target speed and bail — physics re-accelerates before the curve can advance.
        double vKts = ctx.Aircraft.IndicatedAirspeed;
        if (vKts < ArcSpeedFloorKts)
        {
            double tang = prim.Curve.TangentBearing(_bezierT);
            ctx.Targets.TargetTrueHeading = new TrueHeading(tang);
            PublishSpeed(ctx, ComputeTargetSpeed(ctx, BezierRemainingNm(prim), isHoldShortCleared));
            return NavigatorResult.Navigating;
        }

        // Advance arc-length ds = v·dt: down the lead-in first (a shortfall the aircraft has still to drive),
        // then along the curve, stepping the parameter by ds / |B'(t)|.
        double vFtPerSec = vKts * GeoMath.FeetPerNm / 3600.0;
        double dsFt = vFtPerSec * ctx.DeltaSeconds;
        AdvanceBezier(prim, dsFt);
        _arcEntryTravelledFt += dsFt;

        // Write position + heading directly from the playback state (invariant I2), plus what is left of the
        // entry offset — the aircraft converges onto the painted curve over ArcEntryBlendFt of travel
        // instead of being written onto it in one sub-tick.
        (double lat, double lon, double tangentDeg) = BezierPlaybackPose(prim);
        double entryBlend = ArcEntryBlendFactor(_arcEntryTravelledFt);
        ctx.Aircraft.Position = new LatLon(lat + (_arcEntryOffsetLatDeg * entryBlend), lon + (_arcEntryOffsetLonDeg * entryBlend));
        ctx.Aircraft.TrueHeading = new TrueHeading(tangentDeg);
        CheckNoTeleport(ctx.Aircraft.Callsign, positionBefore, ctx.Aircraft.Position, dsFt, prim.Kind);

        // Mirror into targets so physics does not fight the closed-form state.
        ctx.Targets.TargetTrueHeading = new TrueHeading(tangentDeg);
        double targetSpeed = ComputeTargetSpeed(ctx, BezierRemainingNm(prim), isHoldShortCleared);
        PublishSpeed(ctx, targetSpeed);

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
                double maxTurnArc = CategoryPerformance.GroundYawRateAtSpeed(ctx.Category, ctx.Aircraft.GroundSpeed) * ctx.DeltaSeconds;
                ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, nextBrg, maxTurnArc);
            }
            PrevDistToTarget = double.MaxValue;
            return NavigatorResult.ArrivedAtNode;
        }

        return NavigatorResult.Navigating;
    }

    /// <summary>
    /// Spend one sub-tick's <paramref name="dsFt"/> of travel on the primitive: the lead-in comes first (the
    /// aircraft is still rolling up to the curve's start point), and whatever is left of the step advances the
    /// curve parameter, so no distance is created or lost at the hand-over.
    /// </summary>
    private void AdvanceBezier(PathPrimitiveBezier prim, double dsFt)
    {
        double remainingFt = dsFt;
        if (_bezierLeadInRemainingFt > 0.0)
        {
            double consumedFt = Math.Min(remainingFt, _bezierLeadInRemainingFt);
            _bezierLeadInRemainingFt -= consumedFt;
            remainingFt -= consumedFt;
            if (_bezierLeadInRemainingFt > 0.0)
            {
                return;
            }

            _bezierLeadInRemainingFt = 0.0;
        }

        double paramSpeedFt = prim.Curve.DerivativeMagnitudeFt(_bezierT);
        _bezierT = paramSpeedFt > 1e-6 ? Math.Min(1.0, _bezierT + (remainingFt / paramSpeedFt)) : 1.0;
        _bezierTraveledFt += remainingFt;
    }

    /// <summary>
    /// Where the Bézier playback currently sits, before the entry offset is added: on the lead-in, back along
    /// the entry tangent from the curve's start point; otherwise on the curve itself.
    /// </summary>
    private (double Lat, double Lon, double TangentDeg) BezierPlaybackPose(PathPrimitiveBezier prim)
    {
        if (_bezierLeadInRemainingFt <= 0.0)
        {
            (double curveLat, double curveLon) = prim.Curve.Evaluate(_bezierT);
            return (curveLat, curveLon, prim.Curve.TangentBearing(_bezierT));
        }

        double entryTangentDeg = prim.Curve.TangentBearing(0.0);
        LatLon lead = GeoMath.ProjectPoint(
            new LatLon(prim.Curve.P0Lat, prim.Curve.P0Lon),
            new TrueHeading((entryTangentDeg + 180.0) % 360.0),
            _bezierLeadInRemainingFt / GeoMath.FeetPerNm
        );
        return (lead.Lat, lead.Lon, entryTangentDeg);
    }

    /// <summary>
    /// Remaining distance (nm) to the end of the current Bézier, for the braking-curve distance-to-endpoint:
    /// the lead-in still to be driven plus the arc length still to be played.
    /// </summary>
    private double BezierRemainingNm(PathPrimitiveBezier prim) =>
        (Math.Max(0.0, prim.LengthFt - _bezierTraveledFt) + _bezierLeadInRemainingFt) / GeoMath.FeetPerNm;

    private double CurrentSlowTurnTangentDeg(PathPrimitiveSlowTurn prim)
    {
        double tangent = prim.RightTurn ? _arcBearingFromCenterDeg + 90.0 : _arcBearingFromCenterDeg - 90.0;
        return ((tangent % 360.0) + 360.0) % 360.0;
    }

    private NavigatorResult TickSlowTurn(PhaseContext ctx, PathPrimitiveSlowTurn prim)
    {
        LatLon positionBefore = ctx.Aircraft.Position;

        // First tick of this primitive: capture the entry offset from the live position (see TickBezier).
        if (_arcEntryPending)
        {
            CaptureSlowTurnEntry(ctx, prim);
            _arcEntryPending = false;
        }

        // I7 speed floor — aircraft must be moving forward before the arc can advance.
        // Target speed is held at the primitive's cap so physics re-accelerates us.
        double vKts = ctx.Aircraft.IndicatedAirspeed;
        double cappedTarget = ClampBySpeedLimit(ctx, prim.MaxSpeedKts);
        if (vKts < ArcSpeedFloorKts)
        {
            double currentTangent = CurrentSlowTurnTangentDeg(prim);
            ctx.Targets.TargetTrueHeading = new TrueHeading(currentTangent);
            PublishSpeed(ctx, cappedTarget);
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

        // Write position + heading directly from playback state (invariant I2), plus what is left of the
        // entry offset (zero for an arc built from the aircraft's own pose, which is the common case).
        (double lat, double lon) = GeoMath.ProjectPoint(
            new LatLon(prim.CenterLat, prim.CenterLon),
            new TrueHeading(_arcBearingFromCenterDeg),
            prim.RadiusNm
        );
        _arcEntryTravelledFt += dAngleDeg * (Math.PI / 180.0) * prim.RadiusFt;
        double entryBlend = ArcEntryBlendFactor(_arcEntryTravelledFt);
        double tangentDeg = CurrentSlowTurnTangentDeg(prim);
        ctx.Aircraft.Position = new LatLon(lat + (_arcEntryOffsetLatDeg * entryBlend), lon + (_arcEntryOffsetLonDeg * entryBlend));
        ctx.Aircraft.TrueHeading = new TrueHeading(tangentDeg);
        CheckNoTeleport(ctx.Aircraft.Callsign, positionBefore, ctx.Aircraft.Position, dsFt, prim.Kind);

        // Speed policy: cap to the primitive's own MaxSpeedKts — no
        // ComputeTargetSpeed braking-curve logic because SlowTurn primitives
        // don't participate in the multi-segment speed constraint system.
        ctx.Targets.TargetTrueHeading = new TrueHeading(tangentDeg);
        PublishSpeed(ctx, cappedTarget);

        double distToNode = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        PrevDistToTarget = distToNode;
        UpdateDiag(ctx, distToNode, tangentDeg, cappedTarget, onArc: true);

        if (_arcRemainingSweepDeg <= 0.01)
        {
            // A turn-about jog hands straight on to its reversal, which starts on the jog's own exit tangent, so no nudge
            // plays between them. A completing turn-about reversal re-acquires the next bearing at the yaw its own tight
            // radius gives (ω·r on that radius); every other slow turn keeps the comfortable main-gear rate. A reversal whose
            // straight holds its roll-out bearing to abeam the node takes no nudge: that bearing is the one it keeps. Nor does
            // the node turn laid from that offset line: it exits on the outgoing centreline, which the nudge would turn it off.
            if (TakesEndOfArcNudge() && (_nextSegmentBearing is { } nextBrg))
            {
                double yawRateDegPerSec = _turnAboutReversalPlaying
                    ? CategoryPerformance.GroundYawRateOnRadius(ctx.Category, ctx.Aircraft.GroundSpeed, prim.RadiusFt)
                    : CategoryPerformance.GroundYawRateAtSpeed(ctx.Category, ctx.Aircraft.GroundSpeed);
                double maxTurnArc = yawRateDegPerSec * ctx.DeltaSeconds;
                ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, nextBrg, maxTurnArc);
            }
            _turnAboutReversalPlaying = false;
            PrevDistToTarget = double.MaxValue;
            return NavigatorResult.ArrivedAtNode;
        }

        return NavigatorResult.Navigating;
    }

    /// <summary>
    /// Whether a completing slow turn takes the end-of-arc nudge toward the next segment's bearing: not a turn-about jog
    /// handing on to its reversal, not a reversal whose straight holds its roll-out bearing, not the node turn laid from
    /// that offset line.
    /// </summary>
    private bool TakesEndOfArcNudge() => (_pendingTurnAboutArc is null) && !_turnAboutRollsOutAlongEdge && (_turnAboutRollOutOffsetFt <= 0.0);

    private double ComputeTargetSpeed(PhaseContext ctx, double distToEndpointNm, Func<int, bool> isHoldShortCleared)
    {
        double decelRate = DecelRateKts ?? CategoryPerformance.TaxiDecelRate(ctx.Category);
        bool stopCurveBinds = false;
        double brakingLimit = SlowdownDecelRateKts is { } slowdownRate
            ? SplitRateBrakingLimit(distToEndpointNm, isHoldShortCleared, (decelRate, slowdownRate), out stopCurveBinds)
            : SingleRateBrakingLimit(distToEndpointNm, isHoldShortCleared, decelRate);
        decelRate = SlowdownDecelRateKts ?? decelRate;

        // Quadratic scaling by heading error so the aircraft slows during
        // large re-alignments. On a Bézier this is ~1 (we write the exact
        // tangent heading each tick) so it is a no-op.
        double bearingDeg = _currentPrimitive is PathPrimitiveBezier bezPrim
            ? bezPrim.Curve.TangentBearing(_bezierT)
            : GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLat, TargetLon));
        double angleDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearingDeg));
        double normalized = Math.Clamp(angleDiff / 90.0, 0.0, 1.0);
        double speedFraction = Math.Max(0.03, 1.0 - normalized * normalized);

        double headingCap = MaxSpeedKts * speedFraction;
        double target = Math.Min(headingCap, brakingLimit);

        // Short-connector transit: hold a steady low speed across a short straight bracketed by two fillet
        // corner arcs (a lane change like SFO A→F1→B) instead of accelerating up to the braking-curve ceiling
        // on the connector and slamming back down for the next turn — a real crew flows through as one
        // continuous low-speed maneuver (issue #236; aviation-reviewed).
        double connectorCap = _onShortConnector ? _connectorFlowSpeedKts : double.MaxValue;
        target = Math.Min(target, connectorCap);

        // Cap on the current corner arc: the local cornering speed ahead along the curve on the braking curve.
        double arcCap = BezierArcCapKts(decelRate);
        target = Math.Min(target, arcCap);

        // The stop rate is published only while the stop's curve is what sets the target.
        _stopCurveBinds = stopCurveBinds && (brakingLimit <= target);

        if (Log.IsEnabled(LogLevel.Debug))
        {
            LogSpeedCaps(ctx, distToEndpointNm, target, headingCap, angleDiff, speedFraction, brakingLimit, connectorCap, arcCap);
        }

        return target;
    }

    /// <summary>
    /// The braking-curve ceiling (kts) at <paramref name="decelRate"/> to the current node's required speed and every future
    /// constraint, which <see cref="BuildSpeedConstraints"/> back-propagated at the same rate.
    /// </summary>
    private double SingleRateBrakingLimit(double distToEndpointNm, Func<int, bool> isHoldShortCleared, double decelRate)
    {
        double brakingLimit = Math.Sqrt(_currentNodeRequiredSpeed * _currentNodeRequiredSpeed + 2.0 * decelRate * distToEndpointNm * 3600.0);

        foreach ((double pathDist, double reqSpeed, int nodeId, bool isBarStop) in _speedConstraints)
        {
            if (isBarStop && isHoldShortCleared(nodeId))
            {
                continue;
            }
            double totalDist = Math.Max(0.0, distToEndpointNm + pathDist);
            double limit = Math.Sqrt(reqSpeed * reqSpeed + 2.0 * decelRate * totalDist * 3600.0);
            brakingLimit = Math.Min(brakingLimit, limit);
        }

        return brakingLimit;
    }

    /// <summary>
    /// The braking-curve ceiling (kts) with <see cref="SlowdownDecelRateKts"/> set: each stop (a zero required speed) on its own
    /// curve at <paramref name="rates"/>' stop rate and each slowdown on its own curve at its slowdown rate.
    /// <see cref="BuildSpeedConstraints"/> skips its back-propagation in this mode, so every constraint keeps its own speed and
    /// the minimum over the individual curves is the plan. <paramref name="stopBinds"/> says whether a stop's curve set it.
    /// </summary>
    private double SplitRateBrakingLimit(
        double distToEndpointNm,
        Func<int, bool> isHoldShortCleared,
        (double Stop, double Slowdown) rates,
        out bool stopBinds
    )
    {
        double CurveKts(double reqSpeed, double distNm) =>
            Math.Sqrt(reqSpeed * reqSpeed + 2.0 * (reqSpeed == 0 ? rates.Stop : rates.Slowdown) * Math.Max(0.0, distNm) * 3600.0);

        double brakingLimit = CurveKts(_currentNodeRequiredSpeed, distToEndpointNm);
        stopBinds = _currentNodeRequiredSpeed <= 0.0;

        foreach ((double pathDist, double reqSpeed, int nodeId, bool isBarStop) in _speedConstraints)
        {
            if (isBarStop && isHoldShortCleared(nodeId))
            {
                continue;
            }

            double limit = CurveKts(reqSpeed, distToEndpointNm + pathDist);
            if (limit < brakingLimit)
            {
                brakingLimit = limit;
                stopBinds = reqSpeed == 0;
            }
        }

        return brakingLimit;
    }

    /// <summary>
    /// Per-tick speed-cap attribution: the target the navigator published and each of the four ceilings that
    /// could have produced it — the heading-error scaling of <see cref="MaxSpeedKts"/>, the back-propagated
    /// braking curve, the short-connector flow speed, and the current fillet arc's profile. <c>bind</c> names
    /// the one that won. Note that <see cref="TickStraight"/> clamps this target again afterwards (the
    /// re-acquire speed and the one-tick overshoot backstop), so its own <c>tgt</c> is the number that reaches
    /// <see cref="FlightPhysics"/>.
    /// </summary>
    private void LogSpeedCaps(
        PhaseContext ctx,
        double distToEndpointNm,
        double target,
        double headingCap,
        double angleDiff,
        double speedFraction,
        double brakingLimit,
        double connectorCap,
        double arcCap
    )
    {
        Log.LogDebug(
            "[Nav] {Callsign} t={Elapsed:F2}: node={Node} dist={DistFt:F0}ft target={Target:F1} bind={Bind} "
                + "heading={HeadingCap:F1} (max={Max:F1} err={AngleDiff:F0}deg frac={Frac:F2}) "
                + "braking={Braking:F1} (nodeReq={NodeReq:F1}) connector={Connector} arc={Arc}",
            ctx.Aircraft.Callsign,
            ctx.ScenarioElapsedSeconds,
            TargetNodeId,
            distToEndpointNm * GeoMath.FeetPerNm,
            target,
            BindingCapName(headingCap, brakingLimit, connectorCap, arcCap),
            headingCap,
            MaxSpeedKts,
            angleDiff,
            speedFraction,
            brakingLimit,
            _currentNodeRequiredSpeed,
            CapText(connectorCap),
            CapText(arcCap)
        );
    }

    /// <summary>A speed ceiling for the per-tick diagnostic, or <c>none</c> when that ceiling is not in play this tick.</summary>
    private static string CapText(double kts) => kts >= 1e6 ? "none" : kts.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Which of the four per-tick speed ceilings produced the target this tick — the heading-error scaling of
    /// <see cref="MaxSpeedKts"/>, the back-propagated braking curve, the short-connector flow speed, or the
    /// current fillet arc's profile. Diagnostic only; ties resolve in that order.
    /// </summary>
    private static string BindingCapName(double headingCap, double brakingLimit, double connectorCap, double arcCap)
    {
        double min = Math.Min(Math.Min(headingCap, brakingLimit), Math.Min(connectorCap, arcCap));
        return min >= headingCap ? "heading"
            : min >= brakingLimit ? "braking"
            : min >= connectorCap ? "connector"
            : "arc";
    }

    /// <summary>
    /// The tightest of the braking curves onto the arc's remaining samples: each sample's local cornering
    /// speed, reachable from the aircraft's arc-length position at <paramref name="decelRateKtsPerSec"/>.
    /// The sample just behind the aircraft is included so the cap between two samples is the local one.
    /// </summary>
    private static double ArcProfileLimitKts(IReadOnlyList<GroundArc.SpeedSample> profile, double traveledFt, double decelRateKtsPerSec)
    {
        int first = 0;
        while (first + 1 < profile.Count && profile[first + 1].LengthFt <= traveledFt)
        {
            first++;
        }

        double limit = double.MaxValue;
        for (int i = first; i < profile.Count; i++)
        {
            double aheadNm = Math.Max(0.0, profile[i].LengthFt - traveledFt) / GeoMath.FeetPerNm;
            double reachKts = Math.Sqrt((profile[i].SpeedKts * profile[i].SpeedKts) + (2.0 * decelRateKtsPerSec * aheadNm * 3600.0));
            limit = Math.Min(limit, reachKts);
        }

        return limit;
    }

    /// <summary>
    /// The arc's <see cref="GroundArc.SpeedProfile"/> measured from the segment's from-node: the stored
    /// profile runs from <c>Nodes[0]</c>, so a reversed traversal mirrors it.
    /// </summary>
    private static IReadOnlyList<GroundArc.SpeedSample> OrientedProfile(GroundArc arc, DirectionalEdge edge, AircraftCategory category)
    {
        IReadOnlyList<GroundArc.SpeedSample> stored = arc.SpeedProfile(category);
        if (edge.FromNodeId == arc.Nodes[0].Id)
        {
            return stored;
        }

        double totalFt = stored[^1].LengthFt;
        var reversed = new GroundArc.SpeedSample[stored.Count];
        for (int i = 0; i < stored.Count; i++)
        {
            GroundArc.SpeedSample mirror = stored[stored.Count - 1 - i];
            reversed[i] = new GroundArc.SpeedSample(totalFt - mirror.LengthFt, mirror.SpeedKts);
        }

        return reversed;
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
            // The run is already longer than a connector, so whatever brackets it no longer matters: a genuine straight
            // segment exists and the normal accelerate-then-brake profile is correct. This has to be tested BEFORE the
            // bracket lookups below, each of which returns a corner speed directly — testing only after extending the
            // run let a single long segment bracketed by two corners escape the window entirely, pinning the whole leg
            // at cornering speed.
            if (runFt > ShortConnectorMaxLenFt)
            {
                return null;
            }

            int next = i + dir;
            if (next < 0 || next >= route.Segments.Count)
            {
                return null;
            }

            TaxiRouteSegment neighbor = route.Segments[next];
            if (neighbor.Edge.Edge is GroundArc arc)
            {
                return arc.MaxSafeSpeedKts(ctx.Category);
            }

            // Turn angle at the node between segment i and its neighbor. SingleCornerTurnAngle(route, k) reads
            // the turn at the node ending segment k, so index by the lower of the two segments.
            double turn = SingleCornerTurnAngle(route, dir < 0 ? next : i);
            if (turn > ConnectorCornerThresholdDeg)
            {
                // A sharp corner (over the entry-alignment threshold) is rounded by a main-gear-turn-radius
                // slow-turn at ~TurnRateLimitedSpeedKts (~5 kt for a jet), well below the angle-only comfort
                // cap; a gentler one is taken at the angle comfort speed. Use the actual traversal speed so
                // the whole connector holds one steady low speed rather than surging between the turns.
                return turn > EntryAlignmentThresholdDeg
                    ? CategoryPerformance.TurnRateLimitedSpeedKts(ctx.Category, CategoryPerformance.MainGearTurnRadiusFt(ctx.Category))
                    : CategoryPerformance.CornerSpeedForAngle(ctx.Category, turn);
            }

            // Straight continuation — extend the run and keep walking outward. The loop head re-tests the length.
            runFt += neighbor.Edge.DistanceNm * GeoMath.FeetPerNm;
            i = next;
        }
    }

    /// <summary>
    /// Re-plan the speed profile for the segment already in progress, after the owning phase changed an input to
    /// it — a takeoff clearance that arrives mid-segment clears the bar the route ends at, and the stop planned
    /// for it has to go now rather than at the next node. Deliberately narrower than <see cref="SetupSegment"/>,
    /// which would rebuild the path primitive and rewind arc playback to the segment start.
    /// </summary>
    internal void RefreshSpeedConstraints(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared) =>
        BuildSpeedConstraints(route, ctx, isHoldShortCleared);

    /// <summary>
    /// How far short (ft) of a set-back bar's stop the braking curve reaches zero, so the aircraft comes to rest with its
    /// nose at or behind the marking (AIM 2-3-5.c: "no part of the aircraft extends beyond"); the owning phase takes the
    /// hold inside this margin plus a foot.
    /// </summary>
    internal const double SetBackStopMarginFt = 2.0;

    /// <summary>
    /// The current target's own bar. The owning phase aims the target at a stop that lies on this segment; one set back
    /// past the segment's start is not aimed at, so brake for it here, at its (negative) distance before the node.
    /// </summary>
    private void AddSetBackStopAtTarget(TaxiRoute route, TaxiRouteSegment seg)
    {
        if (
            (seg.ToNodeId == TargetNodeId)
            && (route.GetHoldShortAt(TargetNodeId) is { } bar)
            && !route.StopLiesOnSegment(route.CurrentSegmentIndex, bar)
        )
        {
            _speedConstraints.Add((-StopDistanceBeforeNodeNm(route, route.CurrentSegmentIndex, bar), 0, TargetNodeId, true));
        }
    }

    /// <summary>
    /// The zero for the first uncleared bar ahead, the far node of segment <paramref name="barSegmentIndex"/> at
    /// <paramref name="nodeDistNm"/>. The stop is the bar's painted position, which can sit several segments back from the
    /// junction it protects, so it may land before constraints already collected — or before the current segment's end,
    /// at a negative distance — and the list is re-sorted for the backward propagation.
    /// </summary>
    private void AddStopAtFutureBar(TaxiRoute route, int barSegmentIndex, double nodeDistNm)
    {
        int nodeId = route.Segments[barSegmentIndex].ToNodeId;
        double beforeNodeNm = route.GetHoldShortAt(nodeId) is { } bar ? StopDistanceBeforeNodeNm(route, barSegmentIndex, bar) : 0.0;
        _speedConstraints.Add((nodeDistNm - beforeNodeNm, 0, nodeId, true));
        _speedConstraints.Sort((a, b) => a.PathDistNm.CompareTo(b.PathDistNm));
    }

    /// <summary>
    /// Where the braking curve for <paramref name="bar"/> reaches zero, in nm before its node: the stop itself when it lies
    /// on the bar's own segment (the phase aims there and takes the hold on arrival), else the stop less
    /// <see cref="SetBackStopMarginFt"/>.
    /// </summary>
    private static double StopDistanceBeforeNodeNm(TaxiRoute route, int barSegmentIndex, HoldShortPoint bar)
    {
        double setbackNm = route.HoldShortSetbackNm(barSegmentIndex, bar);
        return route.StopLiesOnSegment(barSegmentIndex, bar) ? setbackNm : setbackNm + (SetBackStopMarginFt / GeoMath.FeetPerNm);
    }

    private void BuildSpeedConstraints(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared)
    {
        _speedConstraints.Clear();

        TaxiRouteSegment? seg = route.CurrentSegment;
        if (seg is null)
        {
            return;
        }

        bool isLastSegment = route.CurrentSegmentIndex + 1 >= route.Segments.Count;

        _cornerRoundingRadiusFt = CategoryPerformance.MainGearTurnRadiusFt(ctx.Category);

        // A corner arc must never be flown faster than its local cornering speed anywhere along it — the
        // braking curve only treats it as a future approach limit, so hold the current arc to its own profile.
        _currentArcProfile = seg.Edge.Edge is GroundArc currentArc ? OrientedProfile(currentArc, seg.Edge, ctx.Category) : null;

        DetectShortConnector(route, ctx, seg);

        if (!isHoldShortCleared(TargetNodeId))
        {
            _currentNodeRequiredSpeed = 0;
            _nextSegmentBearing = null;
            _nextSegmentIsArc = false;
            _nextSegmentIsShort = false;
            AddSetBackStopAtTarget(route, seg);
        }
        else if (!isLastSegment)
        {
            int nextIdx = route.CurrentSegmentIndex + 1;
            TaxiRouteSegment nextSeg = route.Segments[nextIdx];
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
            _currentNodeRequiredSpeed = RouteEndSpeedKts;
            _nextSegmentBearing = null;
            _nextSegmentIsArc = false;
            _nextSegmentIsShort = false;
        }

        // Forward walk: collect future speed constraints.
        double cumulativeDistNm = 0;
        for (int i = route.CurrentSegmentIndex + 1; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment futureSeg = route.Segments[i];
            cumulativeDistNm += futureSeg.Edge.DistanceNm;

            if (futureSeg.Edge.Edge is GroundArc futureArc)
            {
                // One constraint per profile sample, so the braking curve targets the arc's local cornering
                // speed where the curve is actually tight rather than its tightest point at the entry.
                double arcStartDist = cumulativeDistNm - futureSeg.Edge.DistanceNm;
                foreach (GroundArc.SpeedSample sample in OrientedProfile(futureArc, futureSeg.Edge, ctx.Category))
                {
                    if (sample.SpeedKts < MaxSpeedKts)
                    {
                        _speedConstraints.Add(
                            (arcStartDist + (sample.LengthFt / GeoMath.FeetPerNm), sample.SpeedKts, futureSeg.Edge.FromNodeId, false)
                        );
                    }
                }
            }

            if (!isHoldShortCleared(futureSeg.ToNodeId))
            {
                AddStopAtFutureBar(route, i, cumulativeDistNm);
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
                reqSpeed = RouteEndSpeedKts;
            }

            if (reqSpeed < MaxSpeedKts)
            {
                _speedConstraints.Add((cumulativeDistNm, reqSpeed, futureSeg.ToNodeId, false));
            }
        }

        // With a separate slowdown rate each constraint keeps its own speed: SplitRateBrakingLimit plans every one on its own
        // curve at its own rate, which is what back-propagating at one rate would otherwise compute.
        if (SlowdownDecelRateKts is not null)
        {
            return;
        }

        // Backward propagation: apply kinematic decel between adjacent constraints.
        double decelRate = DecelRateKts ?? CategoryPerformance.TaxiDecelRate(ctx.Category);
        for (int i = _speedConstraints.Count - 2; i >= 0; i--)
        {
            (double dist, double speed, int nodeId, bool isBarStop) = _speedConstraints[i];
            (double nextDist, double nextSpeed, int _, bool _) = _speedConstraints[i + 1];
            double legDist = nextDist - dist;
            double backProp = Math.Sqrt(nextSpeed * nextSpeed + 2.0 * decelRate * legDist * 3600.0);
            if (backProp < speed)
            {
                _speedConstraints[i] = (dist, backProp, nodeId, isBarStop);
            }
        }

        // Propagate the first future constraint back into the current node's required speed.
        if (_speedConstraints.Count > 0)
        {
            (double firstDist, double firstSpeed, int _, bool _) = _speedConstraints[0];
            double backProp = Math.Sqrt(firstSpeed * firstSpeed + 2.0 * decelRate * Math.Max(0.0, firstDist) * 3600.0);
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
    ///
    /// <para>The floor is itself capped by <see cref="CurrentCurveCapKts"/>: a runway crossing's 15 kt no-stop
    /// floor is not licence to corner, and the tail-clearance extension past the exit bar routinely takes a whole
    /// fillet. Driving a 22-72 ft corner at 15 kt is several times the lateral-acceleration budget
    /// <see cref="GroundArc.SafeSpeedForRadiusKts"/> allows, so on a curve the floor never exceeds what the curve
    /// itself permits.</para>
    /// </summary>
    private double ClampBySpeedLimit(PhaseContext ctx, double requested)
    {
        double floored = Math.Max(requested, Math.Min(MinSpeedKts, CurrentCurveCapKts(ctx)));
        return ctx.Aircraft.Ground.SpeedLimit is { } limit ? Math.Min(floored, limit) : floored;
    }

    /// <summary>
    /// The cornering cap (kts) of the primitive being played right now: the active Bézier's arc-speed profile
    /// limit at the distance travelled along it (the same limit <see cref="ComputeTargetSpeed"/> applies), a
    /// slow turn's own <see cref="PathPrimitiveSlowTurn.MaxSpeedKts"/>, and <see cref="double.MaxValue"/> on a
    /// straight, which has no curvature to cap anything.
    /// </summary>
    private double CurrentCurveCapKts(PhaseContext ctx)
    {
        if (_currentPrimitive is PathPrimitiveBezier)
        {
            return BezierArcCapKts(SlowdownDecelRateKts ?? DecelRateKts ?? CategoryPerformance.TaxiDecelRate(ctx.Category));
        }

        return _currentPrimitive is PathPrimitiveSlowTurn slowTurn ? slowTurn.MaxSpeedKts : double.MaxValue;
    }

    /// <summary>
    /// The arc-speed profile limit (kts) of the Bézier being played, at the distance travelled along it; <see cref="double.MaxValue"/>
    /// when the current primitive is not a Bézier or its segment carries no profile. The profile belongs to the curve:
    /// a fillet flown as the straight an aimed alignment arc rolled out on (<see cref="InstallAimedLineOverFillet"/>)
    /// corners nowhere, so its profile caps nothing.
    /// </summary>
    private double BezierArcCapKts(double decelRateKtsPerSec) =>
        (_currentPrimitive is PathPrimitiveBezier) && (_currentArcProfile is { } arcProfile)
            ? ArcProfileLimitKts(arcProfile, _bezierTraveledFt, decelRateKtsPerSec)
            : double.MaxValue;

    /// <summary>
    /// The navigator's only speed-publishing site: physics owns ground speed, and it needs the braking
    /// rate alongside the target or it closes the gap at the category default. Routing every tick path
    /// through here is what stops a future site from publishing a target without its rate. Clamping is
    /// idempotent, so a caller that already capped by the speed limit may pass the capped value.
    /// </summary>
    private void PublishSpeed(PhaseContext ctx, double targetKts)
    {
        ctx.Targets.TargetSpeed = ClampBySpeedLimit(ctx, targetKts);
        ctx.Targets.DesiredDecelRate = ((SlowdownDecelRateKts is { } slowdownRate) && !_stopCurveBinds) ? slowdownRate : DecelRateKts;
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

        TaxiRouteSegment thisSeg = route.Segments[turnNodeSegIdx];
        TaxiRouteSegment nextSeg = route.Segments[nextIdx];
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
    // The snapshot carries the active primitive and its playback state (GroundNavigatorDto.Playback). FromSnapshot holds it
    // back, and the owning phase's first SetupSegment after the restore resumes it (TryResumeRestoredPlayback) instead of
    // building a new primitive from the aircraft's pose, so a restore mid-curve goes on along the same curve. The speed plan
    // is rebuilt from the route by that set-up, as for any other.

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
            OnAimedLineOverFillet = _onAimedLineOverFillet,
            AimedLineFilletFromNodeId = _aimedLineFilletFromNodeId,
            // A restored navigator not yet set up still holds the playback it was restored with.
            Playback = _currentPrimitive is { } primitive ? CapturePlayback(primitive) : _restoredPlayback,
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
            _onAimedLineOverFillet = dto.OnAimedLineOverFillet,
            _aimedLineFilletFromNodeId = dto.AimedLineFilletFromNodeId,
            _restoredPlayback = dto.Playback,
        };

    /// <summary>
    /// Whether <paramref name="seg"/> is the segment <paramref name="saved"/> was captured on: its to-node is the saved
    /// target, and its from-node is the saved one. A virtual from-node (negative id) is not compared: its id is a hash of its
    /// position (<c>VirtualNode.IdFor</c>), but it sits at the aircraft's pose when the route is built (the approach
    /// leg <see cref="RunwayExitPhase"/> builds from where the aircraft stands), so a rebuild puts it elsewhere.
    /// </summary>
    private bool WasPlaybackSavedOnSegment(TaxiRouteSegment seg, GroundNavigatorPlaybackDto saved) =>
        (seg.ToNodeId == TargetNodeId) && ((saved.FromNodeId < 0) || (seg.FromNodeId < 0) || (seg.FromNodeId == saved.FromNodeId));

    private GroundNavigatorPlaybackDto CapturePlayback(PathPrimitive primitive) =>
        new()
        {
            Primitive = ToPrimitiveDto(primitive),
            FromNodeId = _segmentFromNodeId,
            HasPendingSegmentPrimitive = _pendingSegmentPrimitive is not null,
            ArcBearingFromCenterDeg = _arcBearingFromCenterDeg,
            ArcRemainingSweepDeg = _arcRemainingSweepDeg,
            BezierT = _bezierT,
            BezierTraveledFt = _bezierTraveledFt,
            BezierLeadInRemainingFt = _bezierLeadInRemainingFt,
            ArcEntryOffsetLatDeg = _arcEntryOffsetLatDeg,
            ArcEntryOffsetLonDeg = _arcEntryOffsetLonDeg,
            ArcEntryTravelledFt = _arcEntryTravelledFt,
            ArcEntryBlendFt = _arcEntryBlendFt,
            ArcEntryPending = _arcEntryPending,
            CumulativeTurnSinceAdvanceDeg = _cumulativeTurnSinceAdvanceDeg,
            AimedAtRouteNode = _alignmentRoute is not null,
            NodeAimSegmentIndex = _nodeAimSegmentIndex,
            AimedPastThroughSegmentIndex = _aimedPastThroughSegmentIndex,
            EntryArcAimedAtNodeOffRealLeg = _entryArcAimedAtNodeOffRealLeg,
            PendingTurnAboutArc = _pendingTurnAboutArc is null ? null : ToSlowTurnDto(_pendingTurnAboutArc),
            TurnAboutReversalPlaying = _turnAboutReversalPlaying ? true : null,
            TurnAboutRollsOutAlongEdge = _turnAboutRollsOutAlongEdge ? true : null,
            TurnAboutRollOutOffsetFt = (_turnAboutRollOutOffsetFt > 0.0) ? _turnAboutRollOutOffsetFt : null,
            TurnAboutReversalOnEdgeBearing = _turnAboutReversalOnEdgeBearing ? true : null,
            TurnAboutSquareStopLine = _turnAboutSquareStopLine ? true : null,
        };

    private void RestorePlaybackProgress(GroundNavigatorPlaybackDto saved)
    {
        _arcBearingFromCenterDeg = saved.ArcBearingFromCenterDeg;
        _arcRemainingSweepDeg = saved.ArcRemainingSweepDeg;
        _bezierT = saved.BezierT;
        _bezierTraveledFt = saved.BezierTraveledFt;
        _bezierLeadInRemainingFt = saved.BezierLeadInRemainingFt;
        _arcEntryOffsetLatDeg = saved.ArcEntryOffsetLatDeg;
        _arcEntryOffsetLonDeg = saved.ArcEntryOffsetLonDeg;
        _arcEntryTravelledFt = saved.ArcEntryTravelledFt;
        _arcEntryBlendFt = saved.ArcEntryBlendFt;
        _arcEntryPending = saved.ArcEntryPending;
        _cumulativeTurnSinceAdvanceDeg = saved.CumulativeTurnSinceAdvanceDeg;
    }

    private static PathPrimitiveDto ToPrimitiveDto(PathPrimitive primitive) =>
        primitive switch
        {
            PathPrimitiveStraight s => new StraightPrimitiveDto
            {
                LengthFt = s.LengthFt,
                ToNodeId = s.ToNodeId,
                FromLat = s.FromLat,
                FromLon = s.FromLon,
                ToLat = s.ToLat,
                ToLon = s.ToLon,
                BearingDeg = s.BearingDeg,
            },
            PathPrimitiveBezier b => new BezierPrimitiveDto
            {
                LengthFt = b.LengthFt,
                ToNodeId = b.ToNodeId,
                P0Lat = b.Curve.P0Lat,
                P0Lon = b.Curve.P0Lon,
                P1Lat = b.Curve.P1Lat,
                P1Lon = b.Curve.P1Lon,
                P2Lat = b.Curve.P2Lat,
                P2Lon = b.Curve.P2Lon,
                P3Lat = b.Curve.P3Lat,
                P3Lon = b.Curve.P3Lon,
                EntryTangentBearingDeg = b.EntryTangentBearingDeg,
                ExitTangentBearingDeg = b.ExitTangentBearingDeg,
            },
            PathPrimitiveSlowTurn t => ToSlowTurnDto(t),
            _ => throw new InvalidOperationException($"[Nav] snapshot: no DTO for path primitive {primitive.GetType().Name}"),
        };

    private static SlowTurnPrimitiveDto ToSlowTurnDto(PathPrimitiveSlowTurn t) =>
        new()
        {
            LengthFt = t.LengthFt,
            ToNodeId = t.ToNodeId,
            CenterLat = t.CenterLat,
            CenterLon = t.CenterLon,
            RadiusFt = t.RadiusFt,
            StartBearingFromCenterDeg = t.StartBearingFromCenterDeg,
            SweepDeg = t.SweepDeg,
            RightTurn = t.RightTurn,
            EntryTangentBearingDeg = t.EntryTangentBearingDeg,
            ExitTangentBearingDeg = t.ExitTangentBearingDeg,
            MaxSpeedKts = t.MaxSpeedKts,
        };

    private static PathPrimitiveSlowTurn FromSlowTurnDto(SlowTurnPrimitiveDto t) =>
        new()
        {
            Kind = PathPrimitiveKind.SlowTurn,
            LengthFt = t.LengthFt,
            ToNodeId = t.ToNodeId,
            CenterLat = t.CenterLat,
            CenterLon = t.CenterLon,
            RadiusFt = t.RadiusFt,
            StartBearingFromCenterDeg = t.StartBearingFromCenterDeg,
            SweepDeg = t.SweepDeg,
            RightTurn = t.RightTurn,
            EntryTangentBearingDeg = t.EntryTangentBearingDeg,
            ExitTangentBearingDeg = t.ExitTangentBearingDeg,
            MaxSpeedKts = t.MaxSpeedKts,
        };

    private static PathPrimitive FromPrimitiveDto(PathPrimitiveDto dto) =>
        dto switch
        {
            StraightPrimitiveDto s => new PathPrimitiveStraight
            {
                Kind = PathPrimitiveKind.Straight,
                LengthFt = s.LengthFt,
                ToNodeId = s.ToNodeId,
                FromLat = s.FromLat,
                FromLon = s.FromLon,
                ToLat = s.ToLat,
                ToLon = s.ToLon,
                BearingDeg = s.BearingDeg,
            },
            BezierPrimitiveDto b => new PathPrimitiveBezier
            {
                Kind = PathPrimitiveKind.Bezier,
                LengthFt = b.LengthFt,
                ToNodeId = b.ToNodeId,
                Curve = new CubicBezier(b.P0Lat, b.P0Lon, b.P1Lat, b.P1Lon, b.P2Lat, b.P2Lon, b.P3Lat, b.P3Lon),
                EntryTangentBearingDeg = b.EntryTangentBearingDeg,
                ExitTangentBearingDeg = b.ExitTangentBearingDeg,
            },
            SlowTurnPrimitiveDto t => FromSlowTurnDto(t),
            _ => throw new InvalidOperationException($"[Nav] snapshot restore: unknown path primitive DTO {dto.GetType().Name}"),
        };
}
