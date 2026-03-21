namespace Yaat.Sim.Simulation;

/// <summary>
/// Lightweight metadata for a v3 recording archive. Contains the snapshot index
/// (elapsed seconds + action index per snapshot) but not the heavy snapshot state.
/// </summary>
public sealed class RecordingManifest
{
    public required int Version { get; init; }
    public required int RngSeed { get; init; }
    public required double TotalElapsedSeconds { get; init; }
    public required int ActionCount { get; init; }
    public bool HasWeather { get; init; }

    public string? ScenarioName { get; init; }
    public string? ScenarioId { get; init; }
    public string? ArtccId { get; init; }
    public DateTime? RecordedAtUtc { get; init; }
    public string? RecordedBy { get; init; }

    public required List<SnapshotIndexEntry> Snapshots { get; init; }
}

/// <summary>
/// One entry in the snapshot index — points to a snapshot ZIP entry by ordinal position.
/// </summary>
public sealed class SnapshotIndexEntry
{
    public required double ElapsedSeconds { get; init; }
    public required int ActionIndex { get; init; }
}
