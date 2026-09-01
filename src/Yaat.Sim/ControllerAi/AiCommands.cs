namespace Yaat.Sim.ControllerAi;

/// <summary>Why a brain issued a command: the rule that fired and its one-line rationale (for the anomaly log and the soak report).</summary>
public sealed record AiIntent(string Rule, string Rationale);

/// <summary>
/// One command an AI position wants dispatched, in the same canonical text a human would type. Track verbs need no
/// <c>AS</c> prefix: the AI connection id (<c>"AI:{positionId}"</c>) names the acting position, and every identity
/// resolver — the engine's replay applier and the server's track handler — resolves it from the ARTCC config, live and
/// on replay alike.
/// </summary>
public sealed record AiCommandRequest(AiPositionConfig From, string Callsign, string Canonical, AiIntent Intent);

/// <summary>What became of an issued command once the host dispatched it.</summary>
public sealed record AiCommandOutcome(AiCommandRequest Request, bool Success, string? Reason);

/// <summary>
/// Where brains send commands. The host owns dispatch (the pure engine dispatches synchronously; a live room queues the
/// request and dispatches it through <c>RoomEngine.SendCommandAsync</c> between ticks) and reports outcomes back
/// through <see cref="DrainOutcomes"/> on the next AI tick.
/// </summary>
public interface IAiCommandSink
{
    void Issue(AiCommandRequest request);

    IReadOnlyList<AiCommandOutcome> DrainOutcomes();
}
