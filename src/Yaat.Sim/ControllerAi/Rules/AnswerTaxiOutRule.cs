using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Ground rule 1: a parked (or pushed-back) departure with an open ready-to-taxi request gets <c>TAXIAUTO</c> to the
/// airport's runway in use (7110.65 §3-7-2; the pathfinder routes, the phases hold short of every runway on the way) — the
/// runway the facility's assignment policy gives this aircraft when the airport has a knowledge file.
/// </summary>
public sealed class AnswerTaxiOutRule : IDecisionRule
{
    public string Name => "answer-taxi-out";

    public void Evaluate(AiRuleScope scope)
    {
        foreach (var aircraft in scope.Jurisdiction)
        {
            var memo = scope.MemoFor(aircraft);
            if (!Applies(aircraft))
            {
                memo.ForgetObservation(Name);
                continue;
            }

            if (!memo.CanAct(scope.Now))
            {
                continue;
            }

            var airport = PilotContactRoster.SurfaceAirportOf(aircraft);
            if (string.IsNullOrWhiteSpace(airport) || scope.Tick.RunwayInUse.For(airport, scope.Tick, scope.Position.PositionId) is not { } decision)
            {
                continue;
            }

            var runway = scope.Tick.RunwayInUse.DepartureRunwayFor(aircraft, decision, scope.Tick);
            var intent = new AiIntent(Name, $"ready to taxi answered with runway {runway} ({decision.Rationale})");
            if (scope.TryIssue(aircraft, memo, $"TAXIAUTO {runway}", intent))
            {
                memo.Intent = GroundIntent.TaxiIssued;
            }
        }
    }

    public static bool Applies(AircraftState aircraft) =>
        aircraft.IsOnGround
        && aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi, ParkingName: null }
        && aircraft.Phases?.CurrentPhase is AtParkingPhase or HoldingAfterPushbackPhase;
}
