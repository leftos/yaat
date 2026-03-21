namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Serializable representation of a resolved taxi route.
/// Node IDs reference the ground layout graph; on restore, the route
/// is re-resolved from the loaded ground layout.
/// </summary>
public sealed class TaxiRouteDto
{
    public required List<TaxiSegmentDto> Segments { get; init; }
    public required int CurrentSegmentIndex { get; init; }
    public List<HoldShortPointDto>? HoldShortPoints { get; init; }
    public string? Description { get; init; }
    public int? DestinationNodeId { get; init; }
}

public sealed class TaxiSegmentDto
{
    public required int FromNodeId { get; init; }
    public required int ToNodeId { get; init; }
    public string? TaxiwayName { get; init; }
}

public sealed class HoldShortPointDto
{
    public required int NodeId { get; init; }
    public required string RunwayId { get; init; }
    public required bool IsSatisfied { get; init; }
}
