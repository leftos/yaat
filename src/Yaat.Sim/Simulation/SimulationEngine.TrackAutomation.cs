using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation;

// The track-automation spine steps: the delayed-handoff queue in pre-physics, auto-accept and point-out
// auto-acknowledge in post-physics. They decide from engine state alone — the scenario's queue and delay, the
// recorded CRC attendance and the consolidation hierarchy — so every run kind reaches the same verdict.
public sealed partial class SimulationEngine
{
    internal void TickDelayedHandoffs()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        for (int i = scenario.DelayedHandoffQueue.Count - 1; i >= 0; i--)
        {
            var entry = scenario.DelayedHandoffQueue[i];
            if (scenario.ElapsedSeconds < entry.FireAtSeconds)
            {
                continue;
            }

            // Don't initiate handoffs to positions that are consolidated under
            // another attended TCP. If 3O is consolidated under 3G (because 3O
            // isn't active), 3G already owns those tracks — a handoff to 3O
            // would be a handoff to yourself. Once 3O activates and is no longer
            // consolidated under 3G, the handoff makes sense.
            var targetTcp = TrackResolver.FindTcpForOwner(entry.Target, scenario);
            if (targetTcp is not null)
            {
                var consolidationOwner = Attendance.ConsolidationOwnerOf(targetTcp, scenario, ConsolidationState);
                if (consolidationOwner is not null && consolidationOwner.Id != targetTcp.Id)
                {
                    continue;
                }
            }

            scenario.DelayedHandoffQueue.RemoveAt(i);

            var snapshot = World.GetSnapshot();
            var aircraft = snapshot.FirstOrDefault(a => a.Callsign.Equals(entry.Callsign, StringComparison.OrdinalIgnoreCase));

            if (aircraft is not null && aircraft.Track.Owner is not null)
            {
                aircraft.Track.HandoffPeer = entry.Target;
                aircraft.Track.HandoffInitiatedAt = scenario.ElapsedSeconds;

                EmitTerminal("System", entry.Callsign, "[AutoTrack] Delayed handoff initiated to " + TrackEngine.FormatOwner(entry.Target));
            }
            else
            {
                _logger.LogWarning(
                    "[AutoTrack] Delayed handoff for {Callsign} to {Target} dropped at t={T}s: {Reason}",
                    entry.Callsign,
                    TrackEngine.FormatOwner(entry.Target),
                    scenario.ElapsedSeconds,
                    aircraft is null ? "aircraft not found" : "aircraft is untracked (no owner to hand off from)"
                );
            }
        }
    }

    /// <summary>
    /// Auto-acknowledges pointouts addressed to simulated/unattended positions after the
    /// auto-accept delay — parity with <see cref="TickAutoAccept"/>: in real vNAS a human always
    /// answers a pointout, so one left on a virtual sector would flash at the sender forever.
    /// </summary>
    internal void TickPointoutAutoAck()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        bool soloMode = scenario.SoloTrainingMode;
        if (!soloMode && scenario.AutoAcceptDelay <= TimeSpan.Zero)
        {
            return;
        }

        var effectiveDelay = soloMode
            ? TimeSpan.FromSeconds(Math.Max(scenario.AutoAcceptDelay.TotalSeconds, SimScenarioState.SoloAutoAcceptFloorSeconds))
            : scenario.AutoAcceptDelay;

        foreach (var aircraft in World.GetSnapshot())
        {
            if (aircraft.Track.Pointout is not { IsPending: true, InitiatedAt: not null } pointout)
            {
                continue;
            }

            if (Attendance.IsTcpControlledByCrc(pointout.Recipient, scenario, ConsolidationState))
            {
                continue;
            }

            if (
                soloMode
                && scenario.StudentTcp is { } studentTcp
                && (pointout.Recipient.Subset == studentTcp.Subset)
                && string.Equals(pointout.Recipient.SectorId, studentTcp.SectorId, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (scenario.ElapsedSeconds - pointout.InitiatedAt.Value < effectiveDelay.TotalSeconds)
            {
                continue;
            }

            var result = TrackEngine.HandleAcknowledge(aircraft);
            if (result.Success)
            {
                EmitTerminal("System", aircraft.Callsign, $"[AutoAck] Pointout acknowledged by {pointout.Recipient}");
                _logger.LogInformation(
                    "Auto-acknowledged pointout: {Callsign} to {Recipient} at t={T}s",
                    aircraft.Callsign,
                    pointout.Recipient,
                    scenario.ElapsedSeconds
                );
            }
        }
    }

    internal void TickAutoAccept()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        bool soloMode = scenario.SoloTrainingMode;
        if (!soloMode && scenario.AutoAcceptDelay <= TimeSpan.Zero)
        {
            _logger.LogTrace("TickAutoAccept: auto-accept disabled (delay={Delay})", scenario.AutoAcceptDelay);
            return;
        }

        // Solo mode forces a >=3s auto-accept for non-student positions even when the operator
        // globally disabled auto-accept; non-solo play uses the operator's configured delay verbatim.
        var effectiveDelay = soloMode
            ? TimeSpan.FromSeconds(Math.Max(scenario.AutoAcceptDelay.TotalSeconds, SimScenarioState.SoloAutoAcceptFloorSeconds))
            : scenario.AutoAcceptDelay;

        var snapshot = World.GetSnapshot();
        var pendingHandoffs = snapshot.Where(a => a.Track.HandoffPeer is not null && a.Track.HandoffInitiatedAt is not null).ToList();
        if (pendingHandoffs.Count > 0)
        {
            _logger.LogTrace("TickAutoAccept: {Count} pending handoffs at t={T}s", pendingHandoffs.Count, scenario.ElapsedSeconds);
        }

        foreach (var aircraft in snapshot)
        {
            if (aircraft.Track.HandoffPeer is null || aircraft.Track.HandoffInitiatedAt is null)
            {
                continue;
            }

            if (aircraft.Track.OwnerFromLiveFeed)
            {
                // A real-world handoff mirrored from the feed: it completes when the feed says so, never by
                // the room's auto-accept (which would move the owner ahead of reality and churn every sample).
                continue;
            }

            var tcp = TrackResolver.FindTcpForOwner(aircraft.Track.HandoffPeer, scenario);
            if (tcp is not null && Attendance.IsTcpControlledByCrc(tcp, scenario, ConsolidationState))
            {
                _logger.LogTrace("TickAutoAccept: {Callsign} handoff pending to CRC-controlled position, skipping", aircraft.Callsign);
                continue;
            }

            // Solo mode: never auto-accept a handoff to the student's own position — the student
            // accepts it by hand (in non-solo an RPO does). Every other position auto-accepts below.
            if (soloMode && scenario.StudentPosition is { } student && aircraft.Track.HandoffPeer.MatchesPosition(student))
            {
                _logger.LogTrace("TickAutoAccept: {Callsign} handoff pending to student position, leaving for manual accept", aircraft.Callsign);
                continue;
            }

            var elapsed = scenario.ElapsedSeconds - aircraft.Track.HandoffInitiatedAt.Value;
            if (elapsed >= effectiveDelay.TotalSeconds)
            {
                var previousOwner = aircraft.Track.Owner;
                var newOwner = aircraft.Track.HandoffPeer;

                // Keep the previous owner's datablock as a white FDB (CRC WasPreviouslyOwned) after the
                // handoff is accepted, until that controller slews to acknowledge — instead of dropping
                // straight to an unowned green PDB. Shared with the manual ACCEPT / accept-all paths.
                TrackEngine.MarkPreviousOwnerRetained(aircraft, previousOwner, scenario);
                TrackEngine.MarkRecentHandoffAccepted(aircraft, previousOwner, wasForced: false, scenario);

                aircraft.Track.Owner = newOwner;
                aircraft.Track.HandoffPeer = null;
                aircraft.Track.HandoffInitiatedAt = null;
                aircraft.Track.HandoffRedirectedBy = null;
                // Mirror the manual ACCEPT: flag the on-handoff trigger so a queued ONHO condition fires.
                aircraft.Track.HandoffAccepted = true;

                ScratchpadRuleEngine.Apply(aircraft, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));

                var fromLabel = previousOwner is not null ? TrackEngine.FormatOwner(previousOwner) : "?";
                var toLabel = TrackEngine.FormatOwner(newOwner);
                EmitTerminal("System", aircraft.Callsign, $"[AutoAccept] Handoff accepted " + $"({fromLabel} \u2192 {toLabel})");

                _logger.LogInformation(
                    "Auto-accepted handoff: {Callsign} ({From} -> {To}) after {Elapsed}s at t={T}s",
                    aircraft.Callsign,
                    fromLabel,
                    toLabel,
                    elapsed,
                    scenario.ElapsedSeconds
                );
            }
        }
    }
}
