namespace Yaat.Sim.Data.Airport.Fillet.V2;

/// <summary>
/// A planning-time cut identifier, distinct from graph node IDs (<see cref="GroundNode.Id"/>).
/// Cut IDs are allocated at the start of each fillet pass (seeded at maxNodeId + 1_000_000) and
/// exist only during planning; they are resolved to materialized node IDs by
/// <see cref="FilletPlanExecutor"/> at execution time. The type-level distinction prevents
/// accidentally using a cut ID where a node ID is expected and vice versa.
/// </summary>
internal readonly record struct CutId(int Value);
