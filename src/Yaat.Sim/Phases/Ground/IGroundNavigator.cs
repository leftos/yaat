using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Result of a single <see cref="IGroundNavigator.Tick"/> call. Phases use this
/// to decide when to advance the route segment index, terminate the phase, etc.
/// </summary>
public enum NavigatorResult
{
    /// <summary>Still moving toward the current target node.</summary>
    Navigating,

    /// <summary>Target node reached; the phase should advance to the next segment.</summary>
    ArrivedAtNode,
}

/// <summary>
/// Per-tick controller that drives an aircraft along a resolved
/// <see cref="TaxiRoute"/>. Owned and ticked by the ground phases
/// (<c>TaxiingPhase</c>, <c>RunwayExitPhase</c>, <c>LineUpPhase</c>).
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Steer the aircraft along each segment of the route (straight or arc).</item>
///   <item>Manage speed: slow down for upcoming turns, brake to a stop at
///         hold-shorts, stop at the end of the route.</item>
///   <item>Detect per-segment arrival and report it via
///         <see cref="NavigatorResult"/>.</item>
/// </list>
///
/// Not responsible for: route building, hold-short insertion, phase handoff,
/// runway assignment.
///
/// Implementations write <see cref="ControlTargets.TargetTrueHeading"/> and
/// <see cref="ControlTargets.TargetSpeed"/> each tick. Some implementations
/// may additionally write <see cref="AircraftState.Latitude"/>,
/// <see cref="AircraftState.Longitude"/>, and
/// <see cref="AircraftState.TrueHeading"/> directly when following a
/// closed-form arc segment.
/// </summary>
public interface IGroundNavigator
{
    /// <summary>
    /// Target node for the current segment. Exposed for phase logging and
    /// snapshot diagnostics.
    /// </summary>
    int TargetNodeId { get; }

    /// <summary>
    /// Target lat/lon for the current segment. Settable because
    /// <c>TaxiingPhase</c> overrides this with a precomputed hold-short
    /// position after calling <see cref="SetupSegment"/>.
    /// </summary>
    double TargetLat { get; set; }

    /// <inheritdoc cref="TargetLat"/>
    double TargetLon { get; set; }

    /// <summary>
    /// Previous tick's distance to target. Held on the navigator so overshoot
    /// detection and snapshot round-trip stay consistent across ticks.
    /// </summary>
    double PrevDistToTarget { get; set; }

    /// <summary>
    /// Diagnostic information captured during the most recent <see cref="Tick"/>
    /// call. Null until the first tick. Consumed by
    /// <c>TickRecorder</c> for CSV traces.
    /// </summary>
    NavTickDiag? LastTickDiag { get; }

    /// <summary>
    /// Global taxi/exit/lineup speed cap. Set by the owning phase at
    /// construction or per-segment; the navigator clamps <c>TargetSpeed</c> to
    /// this value before applying kinematic brake curves.
    /// </summary>
    double MaxSpeedKts { get; set; }

    /// <summary>
    /// Directly set the target node id. Used by snapshot restore paths that
    /// do not go through <see cref="SetupSegment"/>.
    /// </summary>
    void SetTargetNodeId(int nodeId);

    /// <summary>
    /// Configure the navigator for the route's current segment
    /// (<c>route.CurrentSegment</c>). Builds whatever per-segment state the
    /// implementation needs — speed-profile constraints, arc geometry, etc.
    /// </summary>
    /// <param name="route">The taxi route being followed.</param>
    /// <param name="ctx">Phase context (ground layout, aircraft state, category).</param>
    /// <param name="isHoldShortCleared">
    /// Callback: returns true if the hold-short at the given node id is cleared
    /// (or absent). <c>TaxiingPhase</c> checks the route's hold-short list;
    /// <c>RunwayExitPhase</c> typically passes <c>_ => true</c>.
    /// </param>
    void SetupSegment(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared);

    /// <summary>
    /// Advance one tick: compute steering and speed targets for the current
    /// segment, update arc state, detect arrival.
    /// </summary>
    /// <param name="ctx">Phase context.</param>
    /// <param name="isLastSegment">
    /// True when the current segment is the last in the route. Implementations
    /// may tighten the arrival threshold for the final segment.
    /// </param>
    /// <param name="isHoldShortCleared">Hold-short cleared callback (see <see cref="SetupSegment"/>).</param>
    /// <returns>
    /// <see cref="NavigatorResult.Navigating"/> while the segment is in progress,
    /// <see cref="NavigatorResult.ArrivedAtNode"/> when the segment is complete.
    /// </returns>
    NavigatorResult Tick(PhaseContext ctx, bool isLastSegment, Func<int, bool> isHoldShortCleared);

    /// <summary>
    /// Capture implementation state for snapshot. The returned DTO is consumed
    /// by <see cref="GroundNavigatorFactory.FromSnapshot"/>, which dispatches on
    /// <see cref="GroundNavigatorDto.ImplVersion"/> to the correct
    /// implementation's restore path.
    /// </summary>
    GroundNavigatorDto ToSnapshot();
}

/// <summary>
/// Per-tick diagnostic snapshot produced by an <see cref="IGroundNavigator"/>.
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
