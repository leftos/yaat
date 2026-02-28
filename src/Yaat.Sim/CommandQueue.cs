namespace Yaat.Sim;

public enum BlockTriggerType
{
    ReachAltitude,
    ReachFix,
    InterceptRadial,
    ReachFrdPoint,
}

public class BlockTrigger
{
    public required BlockTriggerType Type { get; init; }
    public double? Altitude { get; init; }
    public string? FixName { get; init; }
    public double? FixLat { get; init; }
    public double? FixLon { get; init; }
    public int? Radial { get; init; }
    public int? DistanceNm { get; init; }
    public double? TargetLat { get; init; }
    public double? TargetLon { get; init; }
}

public enum TrackedCommandType
{
    Heading,
    Altitude,
    Speed,
    Navigation,
    Immediate,
    Wait,
}

public class TrackedCommand
{
    public required TrackedCommandType Type { get; init; }
    public bool IsComplete { get; set; }
}

public class CommandBlock
{
    public BlockTrigger? Trigger { get; init; }
    public List<TrackedCommand> Commands { get; init; } = [];
    public bool IsApplied { get; set; }
    public bool TriggerMet { get; set; }
    public bool AllComplete => Commands.TrueForAll(c => c.IsComplete);

    public double TriggerClosestApproach { get; set; } = double.MaxValue;
    public bool TriggerMissed { get; set; }

    public bool IsWaitBlock { get; init; }
    public double WaitRemainingSeconds { get; set; }
    public double WaitRemainingDistanceNm { get; set; }

    /// <summary>
    /// Human-readable summary of this block (e.g., "CM 040, FH 090" or "at FIXIE: CM 040").
    /// Set by CommandDispatcher when building the queue.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Deferred action that applies this block's commands to the aircraft.
    /// Set by the CommandDispatcher when building the queue.
    /// </summary>
    public Action<AircraftState>? ApplyAction { get; init; }
}

public class CommandQueue
{
    public List<CommandBlock> Blocks { get; } = [];
    public int CurrentBlockIndex { get; set; }

    public CommandBlock? CurrentBlock =>
        CurrentBlockIndex >= 0 && CurrentBlockIndex < Blocks.Count
            ? Blocks[CurrentBlockIndex]
            : null;

    public bool IsComplete => CurrentBlockIndex >= Blocks.Count;
}
