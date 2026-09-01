namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// A terminal conflict alert on an aircraft in this position's jurisdiction is a finding against the AI (or, in
/// observer mode, against whoever is working the traffic). Keyed by the conflict id so the episode closes when the
/// engine clears the alert or a controller acknowledges it. A pair split across two AI positions is one episode per
/// position — each brain's ledger is its own.
/// </summary>
public sealed class ConflictAlertInAiJurisdictionRule : IDecisionRule
{
    public string Name => "conflict-alert-in-jurisdiction";

    public void Evaluate(AiRuleScope scope)
    {
        var mine = new HashSet<string>(scope.Jurisdiction.Select(ac => ac.Callsign), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conflict in scope.Tick.ActiveConflicts.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (conflict.IsAcknowledged || (!mine.Contains(conflict.CallsignA) && !mine.Contains(conflict.CallsignB)))
            {
                continue;
            }

            seen.Add(conflict.Id);
            scope.Tick.Anomalies.Open(
                AiAnomalyKind.ConflictAlertInAiJurisdiction,
                scope.Position.PositionId,
                conflict.Id,
                scope.Now,
                $"CA {conflict.CallsignA} / {conflict.CallsignB}"
            );
        }

        scope.CloseVanished(AiAnomalyKind.ConflictAlertInAiJurisdiction, seen);
    }
}
