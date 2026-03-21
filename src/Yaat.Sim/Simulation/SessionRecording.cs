using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

public sealed class SessionRecording
{
    /// <summary>
    /// Recording format version. Absent or 1 = v1 (commands only). 2 = v2 (commands + snapshots).
    /// </summary>
    public int Version { get; init; } = 1;

    public required string ScenarioJson { get; init; }
    public required int RngSeed { get; init; }
    public string? WeatherJson { get; init; }
    public required List<RecordedAction> Actions { get; init; }
    public required double TotalElapsedSeconds { get; init; }

    /// <summary>
    /// State snapshots captured at regular intervals (v2 only).
    /// Null or empty for v1 recordings. Each snapshot pairs an elapsed-seconds
    /// timestamp with a full <see cref="StateSnapshotDto"/>.
    /// </summary>
    public List<TimedSnapshot>? Snapshots { get; init; }

    public string? ScenarioName { get; init; }
    public string? ScenarioId { get; init; }
    public string? ArtccId { get; init; }
    public DateTime? RecordedAtUtc { get; init; }
    public string? RecordedBy { get; init; }

    public bool HasSnapshots => (Snapshots?.Count ?? 0) > 0;
}
