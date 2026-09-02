using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Ground rule 4: a landed aircraft that has called clear of the runway asking for taxi to a parking spot gets
/// <c>TAXIAUTO @spot</c> to that spot — or, when the spot has been taken in the meantime, to the pilot's next choice
/// (the readback tells the pilot where it is actually going).
/// </summary>
public sealed class AnswerTaxiInRule : IDecisionRule
{
    public string Name => "answer-taxi-in";

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

            var requested = aircraft.PendingPilotRequest!.ParkingName!;
            var taken = ArrivalParkingPicker.TakenSpots(scope.Tick.Snapshot, aircraft.Callsign);
            string? parking = requested;
            string why = $"taxi-in request answered with the parking the pilot asked for, {requested}";
            if (taken.Contains(requested))
            {
                parking = ArrivalParkingPicker.Pick(aircraft, scope.Tick.LayoutFor(aircraft), scope.Tick.Snapshot, 1);
                why = $"{requested} is taken; re-picked {parking}";
            }

            if (parking is null)
            {
                continue;
            }

            if (scope.TryIssue(aircraft, memo, $"TAXIAUTO @{parking}", new AiIntent(Name, why)))
            {
                memo.Intent = GroundIntent.TaxiInIssued;
            }
        }
    }

    public static bool Applies(AircraftState aircraft) =>
        aircraft.IsOnGround
        && aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi, ParkingName: not null }
        && aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase or HoldingInPositionPhase;
}
