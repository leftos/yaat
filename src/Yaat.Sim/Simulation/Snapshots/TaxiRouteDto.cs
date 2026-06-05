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
    /// True when this runway-crossing hold-short was synthesized for an arrival auto-pulling-up to
    /// hold short of a parallel runway after landing (issue #175). Defaults to false on legacy
    /// snapshots. Suppresses the solo "ready for departure" report for the landed aircraft.
    /// </summary>
    public bool IsArrivalCrossing { get; init; }

    /// <summary>
    /// Runway hold-short node the aircraft's tail hangs over while holding short of this taxiway
    /// (issue #172 tail-over-runway state). Null in the normal case and on legacy snapshots.
    /// </summary>
    public int? TailOverRunwayNodeId { get; init; }
}
