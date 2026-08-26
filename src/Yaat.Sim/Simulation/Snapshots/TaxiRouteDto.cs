using Yaat.Sim.Data.Airport;

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
    public string? DestinationParking { get; init; }
    public string? DestinationSpot { get; init; }
}

public sealed class TaxiSegmentDto
{
    public required int FromNodeId { get; init; }
    public required int ToNodeId { get; init; }
    public string? TaxiwayName { get; init; }

    /// <summary>
    /// A free-space leg between two layout nodes the graph does not join — a destination-end ramp cut
    /// (issue #400). Restore rebuilds the virtual edge instead of looking one up; false (and absent on older
    /// snapshots) means the segment is a layout edge, and a missing edge still voids the route so a snapshot
    /// whose node ids no longer match the layout is dropped rather than turned into stray legs.
    /// </summary>
    public bool IsFreeSpace { get; init; }

    /// <summary>
    /// Position of a <see cref="Yaat.Sim.Data.Airport.VirtualNode"/> endpoint — a free-space leg such as a
    /// ramp-lane cut whose node is not in the ground layout. Null for graph nodes and on older snapshots;
    /// restore then resolves the endpoint by id.
    /// </summary>
    public double? FromLatitude { get; init; }
    public double? FromLongitude { get; init; }
    public double? ToLatitude { get; init; }
    public double? ToLongitude { get; init; }
}

public sealed class HoldShortPointDto
{
    public required int NodeId { get; init; }
    public required string RunwayId { get; init; }
    public required bool IsSatisfied { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// <summary>
    /// Why the route holds short here (destination runway / runway crossing / explicit HSC).
    /// Null on legacy snapshots (schema &lt; 12); restore then falls back to ExplicitHoldShort,
    /// the value those snapshots were reconstructed with before this field existed.
    /// </summary>
    public HoldShortReason? Reason { get; init; }

    /// <summary>
    /// Tracks whether <see cref="IsSatisfied"/> was driven by the AutoCrossRunway
    /// scenario toggle. Defaults to false on legacy snapshots (schema &lt; 7), which
    /// is correct: pre-feature recordings had no notion of AutoCross-attributed
    /// clearance, so a subsequent toggle-OFF on replay must not revert their
    /// hold-shorts.
    /// </summary>
    public bool ClearedByAutoCross { get; init; }

    /// <summary>
    /// Runway hold-short node the aircraft's tail hangs over while holding short of this taxiway
    /// (issue #172 tail-over-runway state). Null in the normal case and on legacy snapshots.
    /// </summary>
    public int? TailOverRunwayNodeId { get; init; }
}
