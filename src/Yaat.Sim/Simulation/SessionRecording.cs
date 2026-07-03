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

    /// <summary>
    /// Whether the recorded session's weather had dynamic METAR re-issuance enabled (file/API
    /// weather). Carried in the archive manifest so a loaded recording can resume dynamic METARs
    /// after Take Control. Defaults false for older bundles.
    /// </summary>
    public bool MetarReissuanceEnabled { get; init; }

    /// <summary>
    /// JSON-serialized ARTCC configuration captured at record time. Bundled into the
    /// archive as <c>artcc-config.json.br</c>; replay deserializes it into
    /// <c>SimScenarioState.ArtccConfig</c> so TCP/ERAM resolution works without a live
    /// ArtccConfigService. Null for older bundles that don't carry the config.
    /// </summary>
    public string? ArtccConfigJson { get; init; }

    /// <summary>
    /// Whether the recorded session had per-aircraft final-approach-speed reduction variety enabled
    /// (read from the initial snapshot's scenario block). Replay restores it into
    /// <c>SimScenarioState.FinalApproachSpeedVarietyEnabled</c> so the re-simulation reproduces the
    /// same varied slow-down distances. Defaults false for pre-feature bundles, which then replay
    /// with the original uniform behavior.
    /// </summary>
    public bool FinalApproachSpeedVarietyEnabled { get; init; }

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

    /// <summary>
    /// Runtime student position captured at record time (read from snapshot 0's scenario block).
    /// The scenario JSON does not carry the resolved student position — the server sets it at load
    /// via <c>InitializeTrackPositions</c> — so Sim-side replay restores it from here. Without it,
    /// <c>CanInitiateWithStudent</c>, proactive check-ins, and Class B/C boundary holds diverge from
    /// the live session. Null for recordings without snapshots (legacy v1).
    /// </summary>
    public ReplayStudentPosition? StudentPositionState { get; init; }

    public bool HasSnapshots => (Snapshots?.Count ?? 0) > 0;
}

/// <summary>
/// Resolved student-position state carried alongside a <see cref="SessionRecording"/> so Sim-side
/// replay can restore it the same way a snapshot restore does (<c>SimulationEngine.LoadScenario</c>).
/// </summary>
public sealed record ReplayStudentPosition(TrackOwner? Position, Tcp? Tcp, string? PositionType, bool IsTowerPosition);
