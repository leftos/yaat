using System.Text.Json.Serialization;

namespace Yaat.Sim.Simulation.Snapshots;

public sealed class CommandQueueDto
{
    public required List<CommandBlockDto> Blocks { get; init; }
    public required int CurrentBlockIndex { get; init; }

    // The aircraft's transmission counter. Additive, no schema bump: 0 (omitted) before its first transmission and in
    // older snapshots.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LastTransmissionId { get; init; }
}

public sealed class CommandBlockDto
{
    public BlockTriggerDto? Trigger { get; init; }
    public required List<TrackedCommandDto> Commands { get; init; }
    public required bool IsApplied { get; init; }
    public required bool TriggerMet { get; init; }
    public required double TriggerClosestApproach { get; init; }
    public required bool TriggerMissed { get; init; }
    public bool TriggerCrossingObserved { get; init; }

    // Latch for the OTG condition's AfterCycleTerminator trigger. Defaults false so older snapshots —
    // and every block with a different trigger — deserialize as "no terminator seen yet".
    public bool TriggerTerminatorObserved { get; init; }
    public required bool IsWaitBlock { get; init; }
    public required double WaitRemainingSeconds { get; init; }
    public required double WaitRemainingDistanceNm { get; init; }
    public required string Description { get; init; }
    public required string NaturalDescription { get; init; }
    public string? SourceCommandText { get; init; }
    public int Dimensions { get; init; }

    // Track commands (HO/TRACK/DROP/…) in a queued block are dispatched through the track engine at
    // trigger-fire time, not through CommandDispatcher.ApplyCommand. HasTrackCommand flags such blocks so
    // SimulationEngine.ProcessTriggeredTrackBlocks knows to route them; TrackApplied guards against
    // re-dispatch (and survives snapshot restore). Both default false so older snapshots — and blocks
    // with no track command — are simply skipped.
    public bool HasTrackCommand { get; init; }
    public bool TrackApplied { get; init; }

    // Flags a queued block that carries a DeleteCommand (ONHS DEL, CROSS 28R; DEL, AT FIXIE DEL) so
    // NODEL can strip it and the server can show the datablock "*" marker. Serialized because
    // ParsedCommands is not: a restored queue would otherwise hold an invisible, uncancellable delete.
    // Defaults false so older snapshots deserialize as blocks with no pending delete.
    public bool HasDeleteCommand { get; init; }

    // Whether the dispatch that created this queued block was scenario-scripted (a preset or an AI
    // position, not the student). Serialized because the restored ApplyAction is rebuilt from a fresh
    // DispatchContext: without the flag a preset queued behind a trigger fires as if the instructor
    // issued it. Defaults false so older snapshots deserialize as not scripted.
    public bool IsScenarioScripted { get; init; }

    // The transmission that queued this block, so a restored block's rebuilt ApplyAction stamps the same id.
    // Additive, no schema bump: 0 (omitted) in older snapshots.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TransmissionId { get; init; }
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

    /// <summary>
    /// The trigger's true bearing from the fix in whole degrees (<see cref="BlockTrigger.TrueRadial"/>). <c>set</c> so the
    /// V33→V34 migration step can fill it from <see cref="LegacyRadial"/>.
    /// </summary>
    public int? TrueRadial { get; set; }

    /// <summary>
    /// The magnetic radial as the controller issued it, null for a trigger no controller radial produced
    /// (<see cref="BlockTrigger.IssuedRadial"/>). <c>set</c> so the V33→V34 migration step can fill it from <see cref="LegacyRadial"/>.
    /// </summary>
    public int? IssuedRadial { get; set; }

    /// <summary>
    /// Before V34: the trigger's radial, as the engine then compared it (a true bearing from the fix). Read only by
    /// <see cref="SnapshotSchemaMigrator"/> to fill <see cref="TrueRadial"/> (and <see cref="IssuedRadial"/> for an FRD trigger),
    /// which then nulls it so a rewritten snapshot drops it; never written.
    /// </summary>
    [JsonPropertyName("Radial")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyRadial { get; set; }

    public int? DistanceNm { get; init; }
    public double? TargetLat { get; init; }
    public double? TargetLon { get; init; }
    public string? TargetCallsign { get; init; }
    public double? DistanceFinalNm { get; init; }

    public int? GroundKind { get; init; }
    public int? GroundNodeId { get; init; }
    public string? GroundTaxiwayName { get; init; }
    public string? GroundEntityToken { get; init; }

    /// <summary>
    /// The runways an <c>AfterRunwayCrossing</c> trigger behind a <c>FOLLOWG</c> follower's pre-clearance waits for the follow to be
    /// done with (<see cref="BlockTrigger.FollowCrossingRunways"/>). Null for a taxi <c>CROSS</c>'s trigger and in snapshots before V40.
    /// </summary>
    public List<string>? FollowCrossingRunways { get; init; }
}

public sealed class DeferredDispatchDto
{
    public required double RemainingSeconds { get; init; }
    public required double RemainingDistanceNm { get; init; }
    public required bool IsDistanceBased { get; init; }
    public string? GiveWayTarget { get; init; }
    public string? SourceText { get; init; }

    // Optional — defaults false so older snapshots deserialize as controller-authored WAIT/BEHIND
    // deferrals (predates the command-run delay).
    public bool IsReactionDelay { get; init; }

    // Optional — defaults false so older snapshots deserialize as live/controller-origin deferrals.
    public bool IsScenarioScripted { get; init; }
}
