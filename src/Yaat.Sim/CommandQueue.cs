namespace Yaat.Sim;

public enum BlockTriggerType
{
    ReachAltitude,
    ReachFix,
}

public class BlockTrigger
{
    public required BlockTriggerType Type { get; init; }
    public double? Altitude { get; init; }
    public string? FixName { get; init; }
    public double? FixLat { get; init; }
    public double? FixLon { get; init; }
}

public enum TrackedCommandType
{
    Heading,
    Altitude,
    Speed,
    Navigation,
    Immediate,
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
