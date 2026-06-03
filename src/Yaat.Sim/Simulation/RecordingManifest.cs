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

    /// <summary>
    /// Whether the recorded session's weather had dynamic METAR re-issuance enabled (file/API
    /// weather rather than live-fetched). Lets a loaded recording resume dynamic METARs after Take
    /// Control even when the weather was set only at scenario start (no recorded weather change to
    /// carry the intent). Bundles written before this field deserialize as false.
    /// </summary>
    public bool MetarReissuanceEnabled { get; init; }

    /// <summary>
    /// True when the archive bundles the ARTCC configuration (entry
    /// <c>artcc-config.json.br</c>) used at record time. Self-contained recordings
    /// can replay without consulting the live server config — important for
    /// long-lived bug bundles whose ARTCC structure may have drifted since.
    /// Bundles written before the bundle-config feature deserialize as false.
    /// </summary>
    public bool HasArtccConfig { get; init; }

    public string? ScenarioName { get; init; }
    public string? ScenarioId { get; init; }
    public string? ArtccId { get; init; }
    public DateTime? RecordedAtUtc { get; init; }
    public string? RecordedBy { get; init; }

    public required List<SnapshotIndexEntry> Snapshots { get; init; }

    /// <summary>
    /// Airport IDs whose ground layouts are stored as separate entries in the archive.
    /// Present in v4+ archives. Null or empty for v3.
    /// </summary>
    public List<string>? LayoutAirportIds { get; init; }
}

/// <summary>
/// One entry in the snapshot index — points to a snapshot ZIP entry by ordinal position.
/// </summary>
public sealed class SnapshotIndexEntry
{
    public required double ElapsedSeconds { get; init; }
    public required int ActionIndex { get; init; }
}
