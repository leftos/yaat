using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Ground rule 2: each runway a taxi route crosses needs local control's approval (7110.65 §3-1-3.a), one crossing at
/// a time (§3-7-2.a.3). When nobody — human or AI — holds Local at the airport, Ground works the runway as a combined
/// position and clears the crossing itself once <see cref="RunwayCrossingGate"/> says the runway is free, naming the
/// runway end the aircraft is at. When Local is staffed, Ground holds the aircraft short and asks once on the terminal
/// (the coordination bus that lets an AI Local answer is later work); a crossing still uncleared two minutes after the
/// request is a <see cref="AiAnomalyKind.CoordinationTimeout"/>. A route pre-cleared by the AutoCrossRunway setting
/// never shows an uncleared bar, so standing approval needs no special case.
/// </summary>
public sealed class RunwayCrossingRule : IDecisionRule
{
    /// <summary>How far before an uncleared crossing bar Ground pre-clears it instead of letting the aircraft stop.</summary>
    public const double PreClearDistanceFt = 500;

    public const double CoordinationTimeoutSeconds = 120;

    public string Name => "runway-crossing";

    public void Evaluate(AiRuleScope scope)
    {
        var timedOut = new HashSet<string>(StringComparer.Ordinal);
        foreach (var aircraft in scope.Jurisdiction)
        {
            var memo = scope.MemoFor(aircraft);
            var layout = scope.Tick.LayoutFor(aircraft);
            var pending = TaxiRouteProgress.NextUnclearedCrossing(aircraft, layout);
            if (pending is null || (pending.DistanceFt > PreClearDistanceFt))
            {
                memo.ForgetObservation(Name);
                if (memo.Intent is GroundIntent.CrossingRequested or GroundIntent.CrossingIssued)
                {
                    memo.Intent = GroundIntent.None;
                    memo.PendingCrossingNodeId = null;
                }

                continue;
            }

            var bar = pending.Point;
            var target = bar.TargetName ?? "";
            var airport = PilotContactRoster.SurfaceAirportOf(aircraft);
            var pavement = RunwayCrossingGate.PavementFor(target, scope.Tick.RunwaysFor(airport));
            if (pavement is null || !memo.CanAct(scope.Now))
            {
                continue;
            }

            var end = TaxiRouteProgress.NearestCrossingEnd(aircraft, target, layout);
            if (CabStaffing.LocalIsStaffed(scope, airport))
            {
                AskLocal(scope, aircraft, memo, bar, end, timedOut);
                continue;
            }

            if (!RunwayCrossingGate.IsClear(aircraft, pavement, scope.Tick.Snapshot, layout, out var blocked))
            {
                continue;
            }

            var intent = new AiIntent(
                Name,
                $"combined position, runway {end} clear ({(blocked.Length == 0 ? "no traffic" : blocked)}); cross at {aircraft.Ground.CurrentTaxiway ?? "the bar"}"
            );
            if (scope.TryIssue(aircraft, memo, $"CROSS {end}", intent))
            {
                memo.Intent = GroundIntent.CrossingIssued;
                memo.PendingCrossingNodeId = bar.NodeId;
            }
        }

        scope.CloseVanished(AiAnomalyKind.CoordinationTimeout, timedOut);
    }

    /// <summary>
    /// One paced terminal request per bar (the think time and the position's gap apply as to any transmission); after the
    /// timeout the anomaly opens and Ground asks again, once per timeout, rather than going silent.
    /// </summary>
    private void AskLocal(AiRuleScope scope, AircraftState aircraft, AiAircraftMemo memo, HoldShortPoint bar, string end, HashSet<string> timedOut)
    {
        bool fresh = (memo.Intent != GroundIntent.CrossingRequested) || (memo.PendingCrossingNodeId != bar.NodeId);
        bool overdue = !fresh && (scope.Now - memo.CoordinationRequestedAtSeconds >= CoordinationTimeoutSeconds);
        if (overdue)
        {
            timedOut.Add(aircraft.Callsign);
            scope.Tick.Anomalies.Open(
                AiAnomalyKind.CoordinationTimeout,
                scope.Position.PositionId,
                aircraft.Callsign,
                scope.Now,
                $"crossing of runway {end} requested at {memo.CoordinationRequestedAtSeconds:F0}s is still not approved"
            );
        }

        if (!fresh && !overdue)
        {
            return;
        }

        memo.Observe(Name, scope.Now);
        if ((scope.Now < memo.ObservedAtSeconds + AiPacing.ThinkTimeSeconds(aircraft.Callsign, Name)) || !scope.Pacing.CanTransmit(scope.Now))
        {
            return;
        }

        var taxiway = TaxiwayAt(aircraft, bar) ?? aircraft.Ground.CurrentTaxiway ?? "the hold-short";
        aircraft.PendingWarnings.Add($"[AI-COORD] {scope.Position.Callsign} requests cross runway {end} at {taxiway} for {aircraft.Callsign}");
        scope.Pacing.MarkTransmitted(scope.Now, scope.Tick.AiRng);
        memo.Intent = GroundIntent.CrossingRequested;
        memo.PendingCrossingNodeId = bar.NodeId;
        memo.CoordinationRequestedAtSeconds = scope.Now;
    }

    /// <summary>The taxiway the route reaches the bar on — the point §3-1-3.a wants named.</summary>
    private static string? TaxiwayAt(AircraftState aircraft, HoldShortPoint bar) =>
        aircraft.Ground.AssignedTaxiRoute?.Segments.FirstOrDefault(segment => segment.ToNodeId == bar.NodeId)?.TaxiwayName;
}
