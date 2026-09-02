namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>What one rule evaluation sees: the tick, the position it runs for, that position's jurisdiction, and the brain's per-aircraft memos.</summary>
public sealed class AiRuleScope
{
    public required AiTickContext Tick { get; init; }

    public required AiPositionConfig Position { get; init; }

    public required IReadOnlyList<AircraftState> Jurisdiction { get; init; }

    public required Dictionary<string, AiAircraftMemo> Memos { get; init; }

    /// <summary>The position's transmission pacing (one per brain).</summary>
    public required AiPacing Pacing { get; init; }

    /// <summary>Grace past the pilot-reaction delay after which an issued command whose effect never showed counts as rejected.</summary>
    public const double EffectGraceSeconds = 15;

    public double Now => Tick.ElapsedSeconds;

    /// <summary>
    /// The single way a decision rule acts: starts (or continues) the aircraft's think-time clock for the rule, and once
    /// the think time has elapsed and the position may transmit, issues the command and records it as in flight on the
    /// memo. Returns false when the rule must try again on a later tick.
    /// </summary>
    public bool TryIssue(AircraftState aircraft, AiAircraftMemo memo, string canonical, AiIntent intent)
    {
        memo.Observe(intent.Rule, Now);
        if ((Now < memo.ObservedAtSeconds + AiPacing.ThinkTimeSeconds(aircraft.Callsign, intent.Rule)) || !Pacing.CanTransmit(Now))
        {
            return false;
        }

        var request = new AiCommandRequest(Position, aircraft.Callsign, canonical, intent);
        Tick.Sink.Issue(request);
        memo.MarkIssued(request, Now, Tick.Scenario.CommandRunDelayMaxSeconds + EffectGraceSeconds);
        Pacing.MarkTransmitted(Now, Tick.AiRng);
        return true;
    }

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
