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
    public required List<AircraftSnapshotDto> Aircraft { get; init; }
    public required ScenarioSnapshotDto Scenario { get; init; }
    public ServerSnapshotDto? Server { get; init; }
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
