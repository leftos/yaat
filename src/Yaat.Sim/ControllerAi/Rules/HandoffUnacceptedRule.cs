using Yaat.Sim.Commands;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Watchdog for the radar roles: a handoff to or from this position still pending
/// <see cref="GraceSeconds"/> beyond the room's auto-accept delay. Cab positions do not track, so the rule is a no-op
/// for them. A handoff auto-accept withholds (<see cref="TrackEngine.IsAutoAcceptWithheld"/>: below ERAM coverage, on the
/// ground, or a QT coast track) is pending by design, so it opens no anomaly however long it waits.
/// </summary>
public sealed class HandoffUnacceptedRule : IDecisionRule
{
    public const double GraceSeconds = 60.0;

    public string Name => "handoff-unaccepted";

    public void Evaluate(AiRuleScope scope)
    {
        if (scope.Position.Role is not (ControlRole.Approach or ControlRole.Center))
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        double horizon = scope.Tick.AutoAcceptDelaySeconds + GraceSeconds;
        foreach (AircraftState aircraft in scope.Tick.Snapshot)
        {
            AircraftTrack track = aircraft.Track;
            bool involvesMe =
                (track.Owner is { } owner && owner.MatchesPosition(scope.Position.Identity))
                || (track.HandoffPeer is { } peer && peer.MatchesPosition(scope.Position.Identity));
            if (!involvesMe || !track.OnHandoff || track.HandoffAccepted || track.HandoffInitiatedAt is not { } initiatedAt)
            {
                continue;
            }

            if (TrackEngine.IsAutoAcceptWithheld(aircraft, scope.Tick.NavDb))
            {
                continue;
            }

            double pending = scope.Now - initiatedAt;
            if (pending > horizon)
            {
                seen.Add(aircraft.Callsign);
                scope.Tick.Anomalies.Open(
                    AiAnomalyKind.HandoffUnaccepted,
                    scope.Position.PositionId,
                    aircraft.Callsign,
                    scope.Now,
                    $"handoff {track.Owner?.Callsign} → {track.HandoffPeer?.Callsign} pending for {pending:F0}s"
                );
            }
        }

        scope.CloseVanished(AiAnomalyKind.HandoffUnaccepted, seen);
    }
}
