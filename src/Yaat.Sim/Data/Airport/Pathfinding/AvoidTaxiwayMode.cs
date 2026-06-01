namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// How a search treats the per-airport avoided-taxiway set
/// (<see cref="SearchContext.AvoidedTaxiways"/>). Set by <see cref="SearchContext.Compile"/> and
/// flipped between the two passes of <see cref="Yaat.Sim.Data.Airport.TaxiPathfinder"/>.
/// </summary>
public enum AvoidTaxiwayMode
{
    /// <summary>No avoidance — feature off, airport unconfigured, or explicit-path (named-taxiway) search.</summary>
    Off,

    /// <summary>Pass 1: edges on an avoided taxiway are skipped entirely (never expanded).</summary>
    HardExclude,

    /// <summary>Pass 2 (only when pass 1 found no route): avoided edges are allowed but carry a heavy
    /// soft penalty so their use is minimised.</summary>
    SoftPenalty,
}
