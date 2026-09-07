namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// The engine's flight strips: every strip, the bay/rack layout, both printer queues and the blank-id counter.
/// Absent in pre-feature snapshots, which restore an empty strip state.
/// </summary>
public sealed class FlightStripSnapshotDto
{
    public required List<StripItemSnapshotDto> Items { get; init; }
    public required List<StripBayRackSnapshotDto> BayRacks { get; init; }
    public required List<string> DeparturePrinterQueue { get; init; }
    public required List<string> ArrivalPrinterQueue { get; init; }
    public required int NextBlankId { get; init; }
}

public sealed class StripItemSnapshotDto
{
    public required string Id { get; init; }
    public string? AircraftId { get; init; }
    public required int Type { get; init; }
    public required bool IsOffset { get; init; }
    public required string[] FieldValues { get; init; }
    public required string FacilityId { get; init; }
    public required string BayId { get; init; }
    public required int Rack { get; init; }
    public required int Index { get; init; }
}

/// <summary>One rack of one bay: the columns of strip ids it holds, in order.</summary>
public sealed class StripBayRackSnapshotDto
{
    public required string BayId { get; init; }
    public required string RackKey { get; init; }
    public required List<List<string>> Columns { get; init; }
}
