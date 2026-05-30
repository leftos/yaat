using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// The per-tick ground-steering contract the taxi / runway-exit / runway-crossing phases drive. A
/// navigator physically moves an aircraft along an already-resolved <see cref="TaxiRoute"/> over the
/// ground graph, one tick at a time, and reports per-segment arrival. Route building, hold-short
/// insertion, runway crossing, and phase handoff are the owning phase's job — the navigator only follows
/// the route and reports when it reaches the current target node.
///
/// <para>
/// Implementations are selected through <see cref="GroundNavigatorRouter"/>. A navigator is stateful per
/// aircraft, so the router is a factory (it builds instances), not a holder.
/// </para>
/// </summary>
public interface IGroundNavigator
{
    /// <summary>Maximum forward speed (kts) the navigator may command on straights; the phase sets it per category / expedite state.</summary>
    double MaxSpeedKts { get; set; }

    /// <summary>The node the navigator is steering toward (the current segment's to-node).</summary>
    int TargetNodeId { get; }

    /// <summary>Target latitude — readable for snapshot/diagnostics; set only via segment setup or <see cref="OverrideTargetPosition"/>.</summary>
    double TargetLat { get; }

    /// <summary>Target longitude — readable for snapshot/diagnostics; set only via segment setup or <see cref="OverrideTargetPosition"/>.</summary>
    double TargetLon { get; }

    /// <summary>Distance (nm) to the target node at the previous tick; the owning phase persists this in its snapshot.</summary>
    double PrevDistToTarget { get; }

    /// <summary>
    /// Extra route-segment advances the consuming phase applies on top of the standard increment when the
    /// navigator returns <see cref="NavigatorResult.ArrivedAtNode"/>. A Legacy chord-chain concept; V2
    /// returns 0 and this member is retired at the joint flip.
    /// </summary>
    int ExtraSegmentsToAdvance => 0;

    /// <summary>Point the navigator at a node (used on restore and when the phase re-targets a segment).</summary>
    void SetTargetNodeId(int nodeId);

    /// <summary>
    /// Override the target position to the painted hold-short bar offset. The phase calls this after
    /// <see cref="SetupSegment"/> when the aircraft must stop short of an uncleared hold-short. The
    /// navigator's arrival threshold depends on this position, so it is an explicit seam rather than a
    /// free setter on <see cref="TargetLat"/> / <see cref="TargetLon"/>.
    /// </summary>
    void OverrideTargetPosition(double lat, double lon);

    /// <summary>
    /// Compile the route's current segment for following and build the forward speed-constraint profile.
    /// <paramref name="isHoldShortCleared"/> reports whether a given hold-short node has been cleared.
    /// </summary>
    void SetupSegment(TaxiRoute route, PhaseContext ctx, Func<int, bool> isHoldShortCleared);

    /// <summary>
    /// Advance one tick along the current segment. Returns <see cref="NavigatorResult.ArrivedAtNode"/> at
    /// the to-node so the owning phase advances the segment index; <see cref="NavigatorResult.Navigating"/>
    /// otherwise.
    /// </summary>
    NavigatorResult Tick(PhaseContext ctx, bool isLastSegment, Func<int, bool> isHoldShortCleared);

    /// <summary>Serialize navigator state for snapshot/replay (lossy by design — the primitive is rebuilt from the route index on resume).</summary>
    GroundNavigatorDto ToSnapshot();
}
