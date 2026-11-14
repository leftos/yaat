using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Ground rule 4: a landed aircraft that has called clear of the runway asking for taxi to a parking spot gets
/// <c>TAXIAUTO @spot</c> to that spot — or, when the spot has been taken in the meantime, to the pilot's next choice
/// (the readback tells the pilot where it is actually going). A taxi-in request that names no spot is left unanswered
/// for the instructor to assign one.
/// </summary>
public sealed class AnswerTaxiInRule : IDecisionRule
{
    private static readonly ILogger Log = SimLog.CreateLogger("AnswerTaxiInRule");

    /// <summary>Aircraft already logged as left for the instructor because no other spot fits once theirs was taken.</summary>
    private readonly HashSet<string> _leftForInstructor = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "answer-taxi-in";

    public void Evaluate(AiRuleScope scope)
    {
        foreach (AircraftState aircraft in scope.Jurisdiction)
        {
            AiAircraftMemo memo = scope.MemoFor(aircraft);
            if (!Applies(aircraft))
            {
                memo.ForgetObservation(Name);
                continue;
            }

            if (!memo.CanAct(scope.Now) || (aircraft.PendingPilotRequest?.ParkingName is not { } requested))
            {
                continue;
            }

            HashSet<string> taken = ArrivalParkingPicker.TakenSpots(scope.Tick.Snapshot, aircraft.Callsign);
            string? parking = requested;
            string why = $"taxi-in request answered with the parking the pilot asked for, {requested}";
            if (taken.Contains(requested))
            {
                parking = ArrivalParkingPicker.Pick(
                    aircraft,
                    scope.Tick.LayoutFor(aircraft),
                    scope.Tick.Snapshot,
                    1,
                    scope.Tick.NavDb.AirportSidecars
                );
                why = $"{requested} is taken; re-picked {parking}";
            }

            if (parking is null)
            {
                LogLeftForInstructorOnce(aircraft.Callsign, requested);
                continue;
            }

            if (scope.TryIssue(aircraft, memo, $"TAXIAUTO @{parking}", new AiIntent(Name, why)))
            {
                memo.Intent = GroundIntent.TaxiInIssued;
            }
        }
    }

    private void LogLeftForInstructorOnce(string callsign, string requested)
    {
        if (_leftForInstructor.Add(callsign))
        {
            Log.LogDebug("{Callsign}: {Requested} is taken and no other spot fits; left for the instructor", callsign, requested);
        }
    }

    public static bool Applies(AircraftState aircraft) =>
        aircraft.IsOnGround
        && aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi, TaxiIn: true, ParkingName: not null }
        // A taxiway spawn bound for its own field asks for parking from where it spawned (AtParkingPhase).
        && aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase or HoldingInPositionPhase or AtParkingPhase;
}
