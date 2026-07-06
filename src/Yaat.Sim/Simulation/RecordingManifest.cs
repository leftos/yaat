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

    /// <summary>
    /// True when the archive bundles the room's broadcast terminal log (entry
    /// <c>terminal-log.json.br</c>) — the commands, responses, SAY, warnings, and chat the user saw,
    /// each with its scenario-elapsed time. A loaded recording repopulates the full terminal from it,
    /// making every line a replay-scrub target. Bundles written before this feature deserialize as
    /// false, and the terminal falls back to the legacy forward-playback command echo.
    /// </summary>
    public bool HasTerminalLog { get; init; }

    public string? ScenarioName { get; init; }
    public string? ScenarioId { get; init; }
    public string? ArtccId { get; init; }
    public DateTime? RecordedAtUtc { get; init; }
    public string? RecordedBy { get; init; }

    /// <summary>
    /// Version of the YAAT client (Yaat.Client) that produced this recording, e.g. "0.7.20-beta".
    /// Null for recordings exported before client/server versions were captured, or for recordings
    /// migrated from legacy formats. Lets triage tell whether the user's client predated a fix.
    /// </summary>
    public string? ClientVersion { get; init; }

    /// <summary>
    /// Whether the producing client was an installed Velopack release ("release") or a "dev build".
    /// Null when unknown (legacy or pre-capture recordings).
    /// </summary>
    public string? ClientBuildKind { get; init; }

    /// <summary>
    /// Informational version of the Yaat.Sim assembly that executed on the server and produced this
    /// recording — the authoritative simulation code (Yaat.Server has no independent version). Use
    /// this to tell whether a sim/physics/phase/ground fix was present in the build that ran the
    /// session. Null for legacy or pre-capture recordings.
    /// </summary>
    public string? ServerVersion { get; init; }

    public required List<SnapshotIndexEntry> Snapshots { get; init; }

    /// <summary>
    /// Airport IDs whose ground layouts are stored as separate entries in the archive.
    /// Present in v4+ archives. Null or empty for v3.
    /// </summary>
    public List<string>? LayoutAirportIds { get; init; }

    /// <summary>
    /// Airport IDs whose original source GeoJSON is stored as separate Brotli entries in the archive.
    /// Null or empty for archives written before source GeoJSON bundling.
    /// </summary>
    public List<string>? AirportGeoJsonIds { get; init; }
}

/// <summary>
/// One entry in the snapshot index — points to a snapshot ZIP entry by ordinal position.
/// </summary>
public sealed class SnapshotIndexEntry
{
    public required double ElapsedSeconds { get; init; }
    public required int ActionIndex { get; init; }
}

/// <summary>
/// Caller-supplied metadata stamped into a recording's <see cref="RecordingManifest"/> at
/// <see cref="RecordingArchiveWriter.Finish"/> time. The writer merges these values with the
/// counters it tracks while writing (action count, snapshot index, weather/config flags), so
/// callers only provide the facts the writer cannot derive on its own.
/// </summary>
public sealed record RecordingMetadata
{
    public required int RngSeed { get; init; }
    public required double TotalElapsedSeconds { get; init; }
    public string? ScenarioName { get; init; }
    public string? ScenarioId { get; init; }
    public string? ArtccId { get; init; }
    public DateTime? RecordedAtUtc { get; init; }
    public string? RecordedBy { get; init; }
    public string? ClientVersion { get; init; }
    public string? ClientBuildKind { get; init; }
    public string? ServerVersion { get; init; }
}
