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
    /// <summary>
    /// When true, the factory builds <see cref="GroundNavigatorV2"/>; otherwise the V1
    /// <see cref="GroundNavigator"/>. Defaults to true (V2) — the joint flip. Set at startup or in
    /// single-threaded test setup; not thread-safe across concurrent assignment.
    /// </summary>
    public static bool UseV2 { get; set; } = true;

    /// <summary>Build a fresh navigator for a new segment-follow.</summary>
    public static IGroundNavigator Create() => UseV2 ? new GroundNavigatorV2() : new GroundNavigator();

    /// <summary>Rebuild a navigator from a snapshot DTO (lossy — the primitive re-derives on the next <see cref="IGroundNavigator.SetupSegment"/>).</summary>
    public static IGroundNavigator FromSnapshot(GroundNavigatorDto dto) =>
        UseV2 ? GroundNavigatorV2.FromSnapshot(dto) : GroundNavigator.FromSnapshot(dto);
}
