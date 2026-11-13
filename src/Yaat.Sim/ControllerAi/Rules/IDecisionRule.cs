using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

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

    /// <summary>
    /// Whether <paramref name="aircraft"/>'s ground layout is loading: it carries none, the tick's pick
    /// (<see cref="AiTickContext.LayoutFor"/>, which reads through the engine's gate) found none, and an airport that pick
    /// reads — the assigned runway's or the aircraft's own, its departure, its destination — is in the room's deferred set
    /// (<see cref="SimScenarioState.DeferredLayoutAirports"/>), the set the gate answers loading from. A rule that would
    /// issue a command needing the layout waits instead, since <see cref="TryIssue"/> marks a command issued before its
    /// result and the dispatcher refuses that command while the layout loads. With the deferred set empty it answers false
    /// without reading the pick, so it relies on an earlier read through the gate (the tick's phase context, or another
    /// pick) having deferred a loading airport: one no read has reached yet is not loading here.
    /// </summary>
    public bool LayoutLoading(AircraftState aircraft)
    {
        List<string> deferred = Tick.Scenario.DeferredLayoutAirports;
        if ((deferred.Count == 0) || (aircraft.Ground.Layout is not null) || (Tick.LayoutFor(aircraft) is not null))
        {
            return false;
        }

        string? context = aircraft.Phases?.AssignedRunway?.AirportId;
        return IsDeferred(deferred, string.IsNullOrEmpty(context) ? aircraft.AirportId : context)
            || IsDeferred(deferred, aircraft.FlightPlan.Departure)
            || IsDeferred(deferred, aircraft.FlightPlan.Destination);
    }

    private static bool IsDeferred(List<string> deferred, string? airportId) =>
        !string.IsNullOrWhiteSpace(airportId) && deferred.Contains(NavigationDatabase.NormalizeAirport(airportId), StringComparer.Ordinal);

    public AiAircraftMemo MemoFor(AircraftState aircraft)
    {
        if (!Memos.TryGetValue(aircraft.Callsign, out AiAircraftMemo? memo))
        {
            memo = new AiAircraftMemo();
            Memos[aircraft.Callsign] = memo;
        }

        return memo;
    }

    /// <summary>Closes every open episode of <paramref name="kind"/> for this position whose subject is not in <paramref name="stillPresent"/>.</summary>
    public void CloseVanished(AiAnomalyKind kind, IReadOnlySet<string> stillPresent)
    {
        foreach (string subject in Tick.Anomalies.OpenSubjects(kind, Position.PositionId))
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
