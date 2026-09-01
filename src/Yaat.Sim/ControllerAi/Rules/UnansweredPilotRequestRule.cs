using Yaat.Sim.Pilot;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Watchdog: a pilot request this position should answer (taxi for Ground; takeoff and landing for Local; approach and
/// airspace entry for the radar roles) that is still open after the pilot has had to ask again — the follow-up the
/// pilot makes on its own clock (<see cref="PilotRequestTracker.NormalFollowUpDelaySeconds"/>, re-based by a STANDBY, so an
/// acknowledged wait never counts). A departure holding for release cannot be cleared (AIM 5-2-7), so its takeoff
/// request is never overdue while the hold stands.
/// </summary>
public sealed class UnansweredPilotRequestRule : IDecisionRule
{
    public string Name => "unanswered-pilot-request";

    public void Evaluate(AiRuleScope scope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var aircraft in scope.Jurisdiction)
        {
            var request = aircraft.PendingPilotRequest;
            if (request is not { IsOpen: true } || !Answers(scope.Position.Role, request.Kind))
            {
                continue;
            }

            bool askedAgain = request.LastRequestedAtSeconds > request.FirstRequestedAtSeconds;
            bool heldForRelease = (request.Kind == PilotPendingRequestKind.Takeoff) && aircraft.Ground.HeldForRelease;
            if (!askedAgain || heldForRelease)
            {
                continue;
            }

            seen.Add(aircraft.Callsign);
            scope.Tick.Anomalies.Open(
                AiAnomalyKind.UnansweredPilotRequest,
                scope.Position.PositionId,
                aircraft.Callsign,
                scope.Now,
                $"{request.Kind} request first made at {request.FirstRequestedAtSeconds:F0}s, asked again at {request.LastRequestedAtSeconds:F0}s: {request.LastPilotLine}"
            );
        }

        scope.CloseVanished(AiAnomalyKind.UnansweredPilotRequest, seen);
    }

    public static bool Answers(ControlRole role, PilotPendingRequestKind kind) =>
        kind switch
        {
            PilotPendingRequestKind.Taxi => role == ControlRole.Ground,
            PilotPendingRequestKind.Takeoff or PilotPendingRequestKind.Landing => role == ControlRole.Local,
            PilotPendingRequestKind.Approach or PilotPendingRequestKind.AirspaceEntry => role is ControlRole.Approach or ControlRole.Center,
            _ => false,
        };
}
