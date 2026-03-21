namespace Yaat.Sim.Simulation.Snapshots;

public sealed class CommandQueueDto
{
    public required List<CommandBlockDto> Blocks { get; init; }
    public required int CurrentBlockIndex { get; init; }
}

public sealed class CommandBlockDto
{
    public BlockTriggerDto? Trigger { get; init; }
    public required List<TrackedCommandDto> Commands { get; init; }
    public required bool IsApplied { get; init; }
    public required bool TriggerMet { get; init; }
    public required double TriggerClosestApproach { get; init; }
    public required bool TriggerMissed { get; init; }
    public required bool IsWaitBlock { get; init; }
    public required double WaitRemainingSeconds { get; init; }
    public required double WaitRemainingDistanceNm { get; init; }
    public required string Description { get; init; }
    public required string NaturalDescription { get; init; }
    public string? SourceCommandText { get; init; }
}

public sealed class TrackedCommandDto
{
    public required int Type { get; init; }
    public required bool IsComplete { get; init; }
}

public sealed class BlockTriggerDto
{
    public required int Type { get; init; }
    public double? Altitude { get; init; }
    public string? FixName { get; init; }
    public double? FixLat { get; init; }
    public double? FixLon { get; init; }
    public int? Radial { get; init; }
    public int? DistanceNm { get; init; }
    public double? TargetLat { get; init; }
    public double? TargetLon { get; init; }
    public string? TargetCallsign { get; init; }
    public double? DistanceFinalNm { get; init; }
}

public sealed class DeferredDispatchDto
{
    public required double RemainingSeconds { get; init; }
    public required double RemainingDistanceNm { get; init; }
    public required bool IsDistanceBased { get; init; }
    public string? SourceText { get; init; }
}
