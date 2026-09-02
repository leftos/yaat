using Yaat.Sim.Pilot;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Ground rule 5: a departure taxiing up to its runway, with every crossing behind it, is transferred to the tower
/// (<c>CT OAK_TWR</c>) shortly before the hold-short bar (7110.65 §2-1-17.a), so the pilot's "holding short, ready"
/// call goes to Local. Only while someone — an AI position or a human — holds Local at the airport: a combined cab
/// transfers nothing.
/// </summary>
public sealed class HandToLocalRule : IDecisionRule
{
    /// <summary>Along-route distance to the departure-runway bar at which the transfer is made (about forty seconds of taxi).</summary>
    public const double HandoffDistanceFt = 1200;

    private readonly Dictionary<string, string?> _localByAirport = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "hand-to-local";

    public void Evaluate(AiRuleScope scope)
    {
        foreach (var aircraft in scope.Jurisdiction)
        {
            var memo = scope.MemoFor(aircraft);
            var layout = scope.Tick.LayoutFor(aircraft);
            if (memo.Intent == GroundIntent.HandedToLocal || TaxiRouteProgress.DistanceToDestinationBarFt(aircraft, layout) is not { } distanceFt)
            {
                memo.ForgetObservation(Name);
                continue;
            }

            if ((distanceFt > HandoffDistanceFt) || (TaxiRouteProgress.NextUnclearedCrossing(aircraft, layout) is not null))
            {
                memo.ForgetObservation(Name);
                continue;
            }

            if (!memo.CanAct(scope.Now))
            {
                continue;
            }

            var airport = PilotContactRoster.SurfaceAirportOf(aircraft);
            if (LocalCallsign(scope, airport) is not { } local)
            {
                continue;
            }

            var intent = new AiIntent(Name, $"{distanceFt:F0} ft from the departure-runway bar, no crossing ahead: transferred to {local}");
            if (scope.TryIssue(aircraft, memo, $"CT {local}", intent))
            {
                memo.Intent = GroundIntent.HandedToLocal;
            }
        }
    }

    /// <summary>The Local position to transfer to, or null while nobody holds one — a combined cab transfers nothing (§2-1-17.a).</summary>
    private string? LocalCallsign(AiRuleScope scope, string? airport)
    {
        if (string.IsNullOrWhiteSpace(airport) || !CabStaffing.LocalIsStaffed(scope, airport))
        {
            return null;
        }

        if (_localByAirport.TryGetValue(airport, out var cached))
        {
            return cached;
        }

        var callsign = CabStaffing.LocalCatalog(scope, airport).FirstOrDefault()?.Callsign;
        _localByAirport[airport] = callsign;
        return callsign;
    }

    /// <summary>Forgets the per-airport Local lookup (after a scenario reload the ARTCC config may differ).</summary>
    public void Reset() => _localByAirport.Clear();
}
