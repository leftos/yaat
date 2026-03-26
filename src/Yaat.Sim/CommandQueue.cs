using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

public enum BlockTriggerType
{
    ReachAltitude,
    ReachFix,
    InterceptRadial,
    ReachFrdPoint,
    GiveWay,
    DistanceFinal,
    OnHandoff,
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

    /// <summary>
    /// Target aircraft callsign for GiveWay triggers.
    /// </summary>
    public string? TargetCallsign { get; init; }

    /// <summary>Distance from runway threshold in nm for DistanceFinal triggers.</summary>
    public double? DistanceFinalNm { get; init; }

    public BlockTriggerDto ToSnapshot() =>
        new()
        {
            Type = (int)Type,
            Altitude = Altitude,
            FixName = FixName,
            FixLat = FixLat,
            FixLon = FixLon,
            Radial = Radial,
            DistanceNm = DistanceNm,
            TargetLat = TargetLat,
            TargetLon = TargetLon,
            TargetCallsign = TargetCallsign,
            DistanceFinalNm = DistanceFinalNm,
        };

    public static BlockTrigger FromSnapshot(BlockTriggerDto dto) =>
        new()
        {
            Type = (BlockTriggerType)dto.Type,
            Altitude = dto.Altitude,
            FixName = dto.FixName,
            FixLat = dto.FixLat,
            FixLon = dto.FixLon,
            Radial = dto.Radial,
            DistanceNm = dto.DistanceNm,
            TargetLat = dto.TargetLat,
            TargetLon = dto.TargetLon,
            TargetCallsign = dto.TargetCallsign,
            DistanceFinalNm = dto.DistanceFinalNm,
        };
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

[Flags]
public enum CommandDimension
{
    None = 0,
    Lateral = 1 << 0,
    Vertical = 1 << 1,
    Speed = 1 << 2,
    All = Lateral | Vertical | Speed,
}

public class TrackedCommand
{
    public required TrackedCommandType Type { get; init; }
    public bool IsComplete { get; set; }

    public TrackedCommandDto ToSnapshot() => new() { Type = (int)Type, IsComplete = IsComplete };

    public static TrackedCommand FromSnapshot(TrackedCommandDto dto) => new() { Type = (TrackedCommandType)dto.Type, IsComplete = dto.IsComplete };
}

public class CommandBlock
{
    public BlockTrigger? Trigger { get; init; }
    public List<TrackedCommand> Commands { get; init; } = [];
    public bool IsApplied { get; set; }
    public bool TriggerMet { get; set; }
    public bool AllComplete => Commands.TrueForAll(c => c.IsComplete);

    /// <summary>
    /// Aggregate command dimensions for this block (Lateral, Vertical, Speed).
    /// Set by CommandDispatcher during <see cref="CommandDispatcher.EnqueueBlocks"/>.
    /// </summary>
    public CommandDimension Dimensions { get; set; }

    /// <summary>
    /// The parsed commands that produced this block. Stored to enable block splitting
    /// when a new command only partially conflicts with this block's dimensions.
    /// NOT serialized — only needed during the current dispatch lifecycle.
    /// </summary>
    public List<ParsedCommand>? ParsedCommands { get; init; }

    /// <summary>
    /// Whether this block is ready for the queue to advance past it.
    /// If the block has lateral commands (Heading/Navigation), advancement is gated only
    /// by those — altitude and speed are concurrent and don't block the next instruction.
    /// If no lateral commands exist, all commands must complete (original behavior).
    /// </summary>
    public bool ReadyToAdvance
    {
        get
        {
            bool hasLateral = Commands.Exists(c => c.Type is TrackedCommandType.Heading or TrackedCommandType.Navigation);

            if (hasLateral)
            {
                return Commands.TrueForAll(c => c.IsComplete || c.Type is not (TrackedCommandType.Heading or TrackedCommandType.Navigation));
            }

            return AllComplete;
        }
    }

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
    /// Natural-language description for broadcast when a triggered block executes
    /// (e.g., "At 5,000 ft: Climb and maintain 5000"). Set by CommandDispatcher.
    /// </summary>
    public string NaturalDescription { get; set; } = "";

    /// <summary>
    /// Deferred action that applies this block's commands to the aircraft.
    /// Set by the CommandDispatcher when building the queue.
    /// Returns a CommandResult with success/failure and optional message.
    /// </summary>
    public Func<AircraftState, Commands.CommandResult>? ApplyAction { get; init; }

    /// <summary>
    /// Original canonical command text that produced this block.
    /// Used by snapshot serialization to re-derive <see cref="ApplyAction"/> on restore.
    /// </summary>
    public string? SourceCommandText { get; init; }

    public CommandBlockDto ToSnapshot() =>
        new()
        {
            Trigger = Trigger?.ToSnapshot(),
            Commands = Commands.Select(c => c.ToSnapshot()).ToList(),
            IsApplied = IsApplied,
            TriggerMet = TriggerMet,
            TriggerClosestApproach = TriggerClosestApproach,
            TriggerMissed = TriggerMissed,
            IsWaitBlock = IsWaitBlock,
            WaitRemainingSeconds = WaitRemainingSeconds,
            WaitRemainingDistanceNm = WaitRemainingDistanceNm,
            Description = Description,
            NaturalDescription = NaturalDescription,
            SourceCommandText = SourceCommandText,
            Dimensions = (int)Dimensions,
        };

    public static CommandBlock FromSnapshot(CommandBlockDto dto) =>
        new()
        {
            Trigger = dto.Trigger is not null ? BlockTrigger.FromSnapshot(dto.Trigger) : null,
            Commands = dto.Commands.Select(TrackedCommand.FromSnapshot).ToList(),
            IsApplied = dto.IsApplied,
            TriggerMet = dto.TriggerMet,
            TriggerClosestApproach = dto.TriggerClosestApproach,
            TriggerMissed = dto.TriggerMissed,
            IsWaitBlock = dto.IsWaitBlock,
            WaitRemainingSeconds = dto.WaitRemainingSeconds,
            WaitRemainingDistanceNm = dto.WaitRemainingDistanceNm,
            Description = dto.Description,
            NaturalDescription = dto.NaturalDescription,
            SourceCommandText = dto.SourceCommandText,
            Dimensions = (CommandDimension)dto.Dimensions,
            // ApplyAction is NOT restored here — it's re-derived by CommandQueue.FromSnapshot
            // for unapplied blocks that have SourceCommandText
        };
}

public class CommandQueue
{
    public List<CommandBlock> Blocks { get; } = [];
    public int CurrentBlockIndex { get; set; }

    public CommandBlock? CurrentBlock => CurrentBlockIndex >= 0 && CurrentBlockIndex < Blocks.Count ? Blocks[CurrentBlockIndex] : null;

    public bool IsComplete => CurrentBlockIndex >= Blocks.Count;

    public CommandQueueDto ToSnapshot() => new() { Blocks = Blocks.Select(b => b.ToSnapshot()).ToList(), CurrentBlockIndex = CurrentBlockIndex };

    public static CommandQueue FromSnapshot(CommandQueueDto dto)
    {
        var queue = new CommandQueue { CurrentBlockIndex = dto.CurrentBlockIndex };
        foreach (var blockDto in dto.Blocks)
        {
            queue.Blocks.Add(CommandBlock.FromSnapshot(blockDto));
        }
        return queue;
    }
}

/// <summary>
/// A parsed compound command whose execution is deferred by a WAIT timer.
/// Ticks independently of the phase system; when expired, the payload dispatches
/// fresh through <see cref="Commands.CommandDispatcher.DispatchCompound"/>.
/// </summary>
public sealed class DeferredDispatch
{
    public double RemainingSeconds { get; set; }
    public double RemainingDistanceNm { get; set; }
    public bool IsDistanceBased { get; }
    public Commands.CompoundCommand Payload { get; }

    /// <summary>
    /// Original canonical command text (including the WAIT prefix) that produced this dispatch.
    /// Used by snapshot serialization to re-derive the payload on restore.
    /// </summary>
    public string? SourceText { get; init; }

    public DeferredDispatch(double seconds, Commands.CompoundCommand payload)
    {
        RemainingSeconds = seconds;
        Payload = payload;
    }

    public DeferredDispatch(Commands.CompoundCommand payload, double distanceNm)
    {
        IsDistanceBased = true;
        RemainingDistanceNm = distanceNm;
        Payload = payload;
    }

    public DeferredDispatchDto ToSnapshot() =>
        new()
        {
            RemainingSeconds = RemainingSeconds,
            RemainingDistanceNm = RemainingDistanceNm,
            IsDistanceBased = IsDistanceBased,
            SourceText = SourceText,
        };

    public static DeferredDispatch? FromSnapshot(DeferredDispatchDto dto)
    {
        if (dto.SourceText is null)
        {
            return null;
        }

        var parseResult = Commands.CommandParser.ParseCompound(dto.SourceText);
        if (!parseResult.IsSuccess)
        {
            return null;
        }

        if (dto.IsDistanceBased)
        {
            return new DeferredDispatch(parseResult.Value!, dto.RemainingDistanceNm) { SourceText = dto.SourceText };
        }

        return new DeferredDispatch(dto.RemainingSeconds, parseResult.Value!) { SourceText = dto.SourceText };
    }
}
