using System.Text.Json.Serialization;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Top-level snapshot of the entire simulation state at a point in time.
/// </summary>
public sealed class StateSnapshotDto
{
    /// <summary>
    /// Schema version of the snapshot DTO structure. Incremented when fields are
    /// added, renamed, or change type. On deserialization, <see cref="SnapshotSchemaMigrator"/>
    /// upgrades older schemas to <see cref="SnapshotSchemaMigrator.CurrentSchemaVersion"/>.
    /// If migration is not possible, it throws <see cref="SnapshotSchemaException"/>.
    /// </summary>
    public int SchemaVersion { get; set; } = SnapshotSchemaMigrator.CurrentSchemaVersion;

    public required double ElapsedSeconds { get; init; }
    public required RngState Rng { get; init; }
    public string? WeatherJson { get; init; }

    /// <summary>
    /// The active frequency's recorded transmissions, in the order the meter recorded them, so a rewind or restore
    /// resumes in the load band the run was in. Written only when the meter holds one: an empty meter is left out of the
    /// JSON, exactly as a snapshot written before the meter was captured, and either restores an empty meter. Settable for
    /// <see cref="SnapshotSchemaMigrator"/>, which fills it from a pre-V39 <see cref="LegacyFrequencyTransmissionTimes"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FrequencyTransmissionDto>? FrequencyTransmissions { get; set; }

    /// <summary>
    /// The pre-V39 meter: the second each transmission started, with no duration. Read only so
    /// <see cref="SnapshotSchemaMigrator"/> can turn it into <see cref="FrequencyTransmissions"/>; nulled there, so a
    /// rewritten snapshot drops it.
    /// </summary>
    [JsonPropertyName("FrequencyTransmissionTimes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<double>? LegacyFrequencyTransmissionTimes { get; set; }

    public required List<AircraftSnapshotDto> Aircraft { get; init; }
    public required ScenarioSnapshotDto Scenario { get; init; }
    public ServerSnapshotDto? Server { get; init; }
}

/// <summary>
/// One transmission on the active frequency's meter: keyed at <see cref="Start"/> (sim seconds) for
/// <see cref="Duration"/> seconds.
/// </summary>
public sealed record FrequencyTransmissionDto
{
    public required double Start { get; init; }
    public required double Duration { get; init; }
}

/// <summary>
/// Timestamped snapshot within a recording. Pairs a snapshot with its position
/// in the action log so hybrid replay can start from the correct action index.
/// </summary>
public sealed class TimedSnapshot
{
    public required double ElapsedSeconds { get; init; }
    public required int ActionIndex { get; init; }
    public required StateSnapshotDto State { get; init; }
}
