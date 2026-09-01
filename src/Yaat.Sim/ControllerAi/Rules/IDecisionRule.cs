namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>What one rule evaluation sees: the tick, the position it runs for, that position's jurisdiction, and the brain's per-aircraft memos.</summary>
public sealed class AiRuleScope
{
    public required AiTickContext Tick { get; init; }

    public required AiPositionConfig Position { get; init; }

    public required IReadOnlyList<AircraftState> Jurisdiction { get; init; }

    public required Dictionary<string, AiAircraftMemo> Memos { get; init; }

    public double Now => Tick.ElapsedSeconds;

    public AiAircraftMemo MemoFor(AircraftState aircraft)
    {
        if (!Memos.TryGetValue(aircraft.Callsign, out var memo))
        {
            memo = new AiAircraftMemo();
            Memos[aircraft.Callsign] = memo;
        }

        return memo;
    }

    /// <summary>Closes every open episode of <paramref name="kind"/> for this position whose subject is not in <paramref name="stillPresent"/>.</summary>
    public void CloseVanished(AiAnomalyKind kind, IReadOnlySet<string> stillPresent)
    {
        foreach (var subject in Tick.Anomalies.OpenSubjects(kind, Position.PositionId))
        {
            if (!stillPresent.Contains(subject))
            {
                Tick.Anomalies.Close(kind, Position.PositionId, subject, Now);
            }
        }
    }
}

/// <summary>One decision or watchdog rule of a brain. Rules run in the brain's fixed order and must iterate deterministically.</summary>
public interface IDecisionRule
{
    string Name { get; }

    void Evaluate(AiRuleScope scope);
}
