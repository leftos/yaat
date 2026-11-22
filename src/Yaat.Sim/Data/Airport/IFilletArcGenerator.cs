namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Public contract for fillet arc generation on an airport ground layout.
/// Implementations must be stateless and may be called concurrently against different
/// layout instances. Mutates <paramref name="layout"/> in place. Callers comparing
/// implementations must pass independent layout clones.
/// </summary>
public interface IFilletArcGenerator
{
    /// <summary>Stable machine id for logs and diff reports (e.g. "none", "standard").</summary>
    string Id { get; }

    /// <summary>Human-readable label for LayoutInspector output and test reports.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Apply fillet arcs to all eligible intersection nodes. Returns per-pass tallies.
    /// </summary>
    /// <param name="layout">The layout to fillet, mutated in place.</param>
    /// <param name="overrides">
    /// The airport's per-corner fillet overrides (<see cref="LayoutInputs.CornerFillets"/>), keyed against the pre-fillet
    /// layout; <see cref="CornerFilletOverrides.None"/> for none.
    /// </param>
    /// <returns>The pass's tallies.</returns>
    FilletStatistics Apply(AirportGroundLayout layout, CornerFilletOverrides overrides);
}
