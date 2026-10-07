namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// Everything precomputed for one airport: the ground layout payload, the key that says whether it is stale, and the
/// push targets computed for it.
/// </summary>
public sealed record PrecomputeEntry(string AirportId, PrecomputeKey Key, AirportGroundLayout Layout, IReadOnlyList<PushTargetEntry> PushTargets);
