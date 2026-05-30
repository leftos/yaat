using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Factory selector for the active <see cref="IGroundNavigator"/> implementation. Production phases build
/// every navigator through here so the V1→V2 switch flips in one place at the joint flip.
///
/// <para>
/// A navigator is stateful per aircraft, so this is a factory — it builds fresh instances — not an
/// instance holder like the stateless <c>TaxiPathfinderRouter</c>.
/// </para>
/// </summary>
public static class GroundNavigatorRouter
{
    /// <summary>Build a fresh navigator for a new segment-follow.</summary>
    public static IGroundNavigator Create() => new GroundNavigator();

    /// <summary>Rebuild a navigator from a snapshot DTO (lossy — the primitive re-derives on the next <see cref="IGroundNavigator.SetupSegment"/>).</summary>
    public static IGroundNavigator FromSnapshot(GroundNavigatorDto dto) => GroundNavigator.FromSnapshot(dto);
}
