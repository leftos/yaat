using System.Text.Json.Serialization;
using Yaat.Sim.Pilot;

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
    /// The active frequency's pending queue and gates. Written only when the frequency is not quiet: an empty frequency
    /// is left out of the JSON, exactly as a snapshot written before they were captured, and either restores an empty
    /// one. An absent property therefore needs no schema bump.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FrequencyStateDto? Frequency { get; init; }

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
/// The active frequency's queue and gates: the lines waiting to speak in queue order, the read-backs it is holding for,
/// the second it is next free, and the controller-response gate.
/// </summary>
public sealed class FrequencyStateDto
{
    public List<PilotTransmissionDto> Pending { get; init; } = [];
    public List<AwaitedReadbackDto> AwaitingReadbacks { get; init; } = [];
    public double NextAvailableAtSeconds { get; init; }
    public string? AwaitingControllerResponseTo { get; init; }
    public bool AwaitingControllerResponseIsCheckIn { get; init; }
    public double AwaitingControllerResponseSinceSeconds { get; init; }
}

/// <summary>
/// One queued pilot line. <see cref="PilotTransmission.LoadBand"/> is not carried: it is stamped only when the line is
/// dequeued, so every queued entry holds its default.
/// </summary>
public sealed record PilotTransmissionDto
{
    public required string Callsign { get; init; }
    public required string Text { get; init; }
    public required string SpeechText { get; init; }
    public required string SourceKind { get; init; }
    public required PilotTransmissionKind Kind { get; init; }
    public required double CanonicalAirtimeSeconds { get; init; }
    public bool IsInitialContact { get; init; }

    public static PilotTransmissionDto From(PilotTransmission transmission) =>
        new()
        {
            Callsign = transmission.Callsign,
            Text = transmission.Text,
            SpeechText = transmission.SpeechText,
            SourceKind = transmission.SourceKind,
            Kind = transmission.Kind,
            CanonicalAirtimeSeconds = transmission.CanonicalAirtimeSeconds,
            IsInitialContact = transmission.IsInitialContact,
        };

    public PilotTransmission ToTransmission() =>
        new(Callsign, Text, SpeechText, SourceKind, Kind, CanonicalAirtimeSeconds) { IsInitialContact = IsInitialContact };
}

/// <summary>
/// One read-back the frequency is holding for, from the second its instruction was dispatched.
/// <see cref="PendingIndex"/> is its transmission's index in the restored pending queue — the same object, so the
/// reference match that gives it airtime priority survives the round-trip. Only an entry whose transmission is still
/// queued is written: one that has left the queue could never be reference-equal to a restored object.
/// </summary>
public sealed record AwaitedReadbackDto
{
    public required int PendingIndex { get; init; }
    public required double SinceSeconds { get; init; }
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
