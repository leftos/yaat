namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Snapshot of SimScenarioState (queues, settings, ATC positions, timing).
/// </summary>
public sealed class ScenarioSnapshotDto
{
    public required string ScenarioId { get; init; }
    public required string ScenarioName { get; init; }
    public required int RngSeed { get; init; }
    public string? PrimaryAirportId { get; init; }
    public required double ElapsedSeconds { get; init; }

    // Settings
    public required bool AutoClearedToLand { get; init; }
    public required bool AutoCrossRunway { get; init; }

    // Optional — defaults to false so older snapshots and bundles (which predate issue #175)
    // replay faithfully without the parallel-runway auto-pull-up. New snapshots carry the actual
    // scenario value (true by default on live sessions).
    public bool AutoPullUpToParallel { get; init; }

    public required bool ValidateDctFixes { get; init; }
    public required bool IsPaused { get; init; }
    public required double SimRate { get; init; }

    // Optional so older snapshots deserialize cleanly with the default (false = instructor
    // topology). New snapshots always carry the current value.
    public bool SoloTrainingMode { get; init; }

    public int SoloParkingInitialCallupRatePercent { get; init; } = 100;

    public int SoloArrivalGeneratorRatePercent { get; init; } = 100;

    // Optional — defaults to 0 (no random GAs) so older snapshots and bundles preserve
    // the original behavior where AI pilots never spontaneously go around.
    public int SoloGoAroundProbabilityPercent { get; init; }

    public bool HasSoloParkingInitialCallupSource { get; init; }

    public bool HasSoloArrivalGeneratorSource { get; init; }

    public double NextSoloParkingInitialCallupSlotSeconds { get; init; }

    // Optional — defaults to false so older snapshots and bundles preserve the original
    // orange-Warning behavior for sim-initiated pilot transmissions.
    public bool RpoShowPilotSpeech { get; init; }

    // Command-run delay (pilot-reaction). Optional — defaults to 0 (disabled) so older snapshots and
    // bundles replay with no command-run delay.
    public int CommandRunDelayMinSeconds { get; init; }
    public int CommandRunDelayMaxSeconds { get; init; }

    // Timing
    public required double AutoAcceptDelaySeconds { get; init; }
    public required bool IsStudentTowerPosition { get; init; }

    // Auto delete
    public string? ScenarioAutoDeleteMode { get; init; }
    public string? ClientAutoDeleteOverride { get; init; }

    // ATC
    public string? ArtccId { get; init; }
    public TrackOwnerDto? StudentPosition { get; init; }
    public TcpDto? StudentTcp { get; init; }
    public string? StudentPositionType { get; init; }

    // Queues
    public List<DelayedSpawnDto>? DelayedQueue { get; init; }
    public List<ScheduledTriggerDto>? TriggerQueue { get; init; }
    public List<ScheduledPresetDto>? PresetQueue { get; init; }
    public List<GeneratorStateDto>? Generators { get; init; }
    public List<DelayedHandoffDto>? DelayedHandoffQueue { get; init; }

    // Coordination channels (stored as serializable DTOs)
    public Dictionary<string, CoordinationChannelDto>? CoordinationChannels { get; init; }

    // Hold-for-release. Optional so older snapshots deserialize cleanly (no airports armed, no
    // pending releases).
    public List<string>? HeldDepartureAirports { get; init; }
    public List<ScheduledReleaseDto>? ReleaseQueue { get; init; }

    // Active TIMER countdowns. Optional so older snapshots deserialize cleanly (no timers).
    public List<ActiveTimerDto>? ActiveTimers { get; init; }
    public int NextTimerId { get; init; }
}

public sealed class DelayedSpawnDto
{
    public required string AircraftJson { get; init; }
    public required int SpawnAtSeconds { get; init; }

    // Optional — defaults false so older snapshots deserialize with the spawn un-held.
    public bool HeldForRelease { get; init; }
}

public sealed class ScheduledReleaseDto
{
    public required string Airport { get; init; }
    public string? Callsign { get; init; }
    public required double FireAtSeconds { get; init; }
}

public sealed class ActiveTimerDto
{
    public required int Id { get; init; }
    public string? Callsign { get; init; }
    public string? Message { get; init; }
    public required double FireAtSeconds { get; init; }
    public required double TotalSeconds { get; init; }
}

public sealed class ScheduledTriggerDto
{
    public required string Command { get; init; }
    public required int FireAtSeconds { get; init; }
}

public sealed class ScheduledPresetDto
{
    public required string Callsign { get; init; }
    public required string Command { get; init; }
    public required double FireAtSeconds { get; init; }
}

public sealed class GeneratorStateDto
{
    public required string ConfigJson { get; init; }
    public required RunwayInfoDto Runway { get; init; }
    public required double NextSpawnSeconds { get; init; }
    public required double NextSpawnDistance { get; init; }
    public required bool IsExhausted { get; init; }
}

public sealed class DelayedHandoffDto
{
    public required string Callsign { get; init; }
    public required TrackOwnerDto Target { get; init; }
    public required int FireAtSeconds { get; init; }
}

public sealed class CoordinationChannelDto
{
    public required string Id { get; init; }
    public required string ListId { get; init; }
    public required string Title { get; init; }
    public List<TcpDto>? SendingTcps { get; init; }
    public List<CoordinationReceiverDto>? Receivers { get; init; }
    public List<CoordinationItemDto>? Items { get; init; }
    public required int NextSequence { get; init; }
}

public sealed class CoordinationReceiverDto
{
    public required TcpDto Tcp { get; init; }
    public required bool IsAutoRelease { get; init; }
}

public sealed class CoordinationItemDto
{
    public required string Id { get; init; }
    public required string AircraftId { get; init; }
    public required int Status { get; init; }
    public required string Message { get; init; }
    public double? ExpireTime { get; init; }
    public required TcpDto OriginTcp { get; init; }
    public required string ExitFix { get; init; }
    public required bool WasAutomaticRelease { get; init; }
    public required int SequenceNumber { get; init; }
}
