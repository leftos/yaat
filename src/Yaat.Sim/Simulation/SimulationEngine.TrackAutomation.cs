using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

// The track-automation spine steps: the delayed-handoff queue in pre-physics, auto-accept, point-out
// auto-acknowledge and the two autotrack passes in post-physics, plus the autotrack conditions a scenario or
// generator aircraft carries. They decide from engine state alone — the scenario's queue and delay, its ATC
// roster and ARTCC config, the recorded CRC attendance and the consolidation hierarchy — so every run kind
// reaches the same verdict.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Applies a scenario or generator aircraft's auto-track conditions: the airport-based owner from the ATC
    /// roster, the scratchpad rules, the configured owner and scratchpad, the delayed handoff to the student and
    /// the ERAM datablock altitudes. The <c>[AutoTrack] …</c> lines go into
    /// <see cref="LoadedAircraft.AutoTrackMessages"/>, which the spawn paths echo to the terminal.
    /// </summary>
    public void ApplyAutoTrackConditions(LoadedAircraft loaded)
    {
        var scenario = Scenario!;
        var messages = loaded.AutoTrackMessages;

        if (
            loaded.State.Track.Owner is null
            && !FieldElevationResolver.IsBelowDisplayFloor(loaded.State, NavigationDatabase.Instance)
            && !string.IsNullOrEmpty(loaded.State.FlightPlan.Departure)
        )
        {
            foreach (var atcPos in scenario.AtcPositions)
            {
                if (atcPos.Source.AutoTrackAirportIds.Count == 0)
                {
                    continue;
                }

                var dep = loaded.State.FlightPlan.Departure;
                string? matchedAirportId = null;
                foreach (var airportId in atcPos.Source.AutoTrackAirportIds)
                {
                    if (NavigationDatabase.Instance.AirportIdsMatchResolved(dep, airportId))
                    {
                        loaded.State.Track.Owner = atcPos.Owner;
                        matchedAirportId = airportId;
                        break;
                    }
                }

                if (loaded.State.Track.Owner is not null)
                {
                    messages.Add(
                        $"[AutoTrack] Owned by " + $"{TrackEngine.FormatOwner(atcPos.Owner)} " + $"(autoTrackAirportIds: {matchedAirportId})"
                    );
                    break;
                }
            }
        }

        ScratchpadRuleEngine.Apply(loaded.State, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));

        var autoTrack = loaded.AutoTrackConditions;
        if (autoTrack is null)
        {
            return;
        }

        // Prefer the resolved ATC roster: it is ARTCC-aware, so a position in a *neighboring*
        // ARTCC ("ZLC starts with the track") resolves against its own facility tree. Falling
        // back to the scenario's own ARTCC covers positions not listed in the roster (e.g.
        // generator autoTrackConfiguration in scenarios with an empty atc array).
        var owner =
            scenario.AtcPositions.FirstOrDefault(p => p.Source.PositionId == autoTrack.PositionId)?.Owner
            ?? scenario.ArtccConfig?.ResolvePosition(autoTrack.PositionId);

        if (owner is not null)
        {
            loaded.State.Track.Owner = owner;
            messages.Add($"[AutoTrack] Owned by " + $"{TrackEngine.FormatOwner(owner)} " + "(autoTrackConditions)");
        }
        else
        {
            _logger.LogWarning(
                "[AutoTrack] {Callsign}: could not resolve autoTrackConditions position {PositionId} "
                    + "(not in the scenario ATC roster or {ArtccId}); aircraft spawns untracked",
                loaded.State.Callsign,
                autoTrack.PositionId,
                scenario.ArtccId ?? ""
            );
        }

        if (autoTrack.HandoffDelay is not null && scenario.StudentPosition is not null)
        {
            var targetLabel = TrackEngine.FormatOwner(scenario.StudentPosition);
            // Always queue autotrack handoffs — don't set HandoffPeer immediately.
            // TickDelayedHandoffs will fire them once the target position is online.
            var spawnAt = loaded.SpawnDelaySeconds;
            var fireAt = autoTrack.HandoffDelay == 0 ? Math.Max((int)scenario.ElapsedSeconds, spawnAt) : spawnAt + autoTrack.HandoffDelay.Value;
            scenario.DelayedHandoffQueue.Add(
                new DelayedHandoff
                {
                    Callsign = loaded.State.Callsign,
                    Target = scenario.StudentPosition,
                    FireAtSeconds = fireAt,
                }
            );
            messages.Add(
                $"[AutoTrack] Handoff to {targetLabel} queued (fireAt={fireAt}s, spawnDelay={spawnAt}s, handoffDelay={autoTrack.HandoffDelay}s)"
            );
        }

        if (!string.IsNullOrEmpty(autoTrack.ScratchPad))
        {
            // ATCTrainer convention: '+' prefix means SP2, bare value means SP1.
            // Supports space-separated values, e.g. "FOO +RGT" → SP1=FOO, SP2=RGT.
            foreach (var token in autoTrack.ScratchPad.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('+'))
                {
                    var sp2Value = token[1..];
                    loaded.State.Stars.Scratchpad2 = sp2Value;
                    messages.Add($"[AutoTrack] SP2 set: {sp2Value}");
                }
                else
                {
                    loaded.State.Stars.Scratchpad1 = token;
                    messages.Add($"[AutoTrack] SP1 set: {token}");
                }
            }
        }

        // Auto-track interim/cleared altitudes are inherited DATABLOCK-DISPLAY state -- they must never
        // change what the aircraft physically flies (no Targets.AssignedAltitude write here). They are
        // wired to the ERAM datablock only: interim -> Eram.InterimAltitude, cleared ->
        // Eram.ControllerEnteredAltitude (both hundreds of feet), which keeps the two values distinct in
        // ERAM field B. The STARS datablock is deliberately NOT populated from these fields: whether and
        // how non-ERAM (STARS) scenarios use interim/cleared altitudes is still being confirmed with
        // VATUSA staff, so we leave Stars.TemporaryAltitude untouched until that guidance lands.
        var interimHundreds = ResolveDatablockAltitudeHundreds(autoTrack.InterimAltitude);
        var clearedHundreds = ResolveDatablockAltitudeHundreds(autoTrack.ClearedAltitude);
        if (interimHundreds is not null)
        {
            loaded.State.Eram.InterimAltitude = interimHundreds;
        }
        if (clearedHundreds is not null)
        {
            loaded.State.Eram.ControllerEnteredAltitude = clearedHundreds;
        }
        if ((interimHundreds ?? clearedHundreds) is { } shown)
        {
            messages.Add($"[AutoTrack] ERAM datablock altitude set: {shown}");
        }
    }

    /// <summary>
    /// Resolves a vNAS auto-track datablock altitude (a STARS hundreds-of-feet string, optionally with a
    /// leading qualifier letter such as "P040") to hundreds of feet -- the unit the ERAM/STARS altitude
    /// fields use. A single leading non-digit qualifier is stripped before parsing. Returns null when the
    /// value is empty or unparseable.
    /// </summary>
    private static int? ResolveDatablockAltitudeHundreds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var token = raw.Trim();
        if (!char.IsDigit(token[0]))
        {
            token = token[1..];
        }

        var feet = AltitudeResolver.Resolve(token);
        return feet is null ? null : feet.Value / 100;
    }

    /// <summary>
    /// Auto-track aircraft once they first appear on STARS — i.e. after they climb through the
    /// acquisition floor (field elevation + 100 ft AGL), not the instant their wheels leave the
    /// ground. Owning a track that isn't yet displayed would hand a still-on-the-surface departure
    /// to a radar controller before it exists on the scope.
    /// </summary>
    internal void TickDeferredAutoTrack()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        var snapshot = World.GetSnapshot();

        foreach (var ac in snapshot)
        {
            if (
                ac.Track.Owner is not null
                || FieldElevationResolver.IsBelowDisplayFloor(ac, NavigationDatabase.Instance)
                || string.IsNullOrEmpty(ac.FlightPlan.Departure)
            )
            {
                continue;
            }

            var dep = ac.FlightPlan.Departure;
            foreach (var atcPos in scenario.AtcPositions)
            {
                if (atcPos.Source.AutoTrackAirportIds.Count == 0)
                {
                    continue;
                }

                string? matchedAirportId = null;
                foreach (var airportId in atcPos.Source.AutoTrackAirportIds)
                {
                    if (NavigationDatabase.Instance.AirportIdsMatchResolved(dep, airportId))
                    {
                        ac.Track.Owner = atcPos.Owner;
                        matchedAirportId = airportId;
                        break;
                    }
                }

                if (ac.Track.Owner is not null)
                {
                    ScratchpadRuleEngine.Apply(ac, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));
                    _logger.LogInformation(
                        "[DeferredAutoTrack] {Callsign} appeared on STARS — owned by {Owner} (departure {Dep}, matched {Airport})",
                        ac.Callsign,
                        TrackEngine.FormatOwner(atcPos.Owner),
                        dep,
                        matchedAirportId
                    );
                    EmitTerminal(
                        "System",
                        ac.Callsign,
                        $"[AutoTrack] On STARS — owned by {TrackEngine.FormatOwner(atcPos.Owner)} (autoTrackAirportIds: {matchedAirportId})"
                    );
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Auto-track aircraft to the TCP that created their flight plan via VP/DA, once the pilot
    /// is squawking the assigned beacon code. Mirrors STARS flight-plan-to-track correlation:
    /// when an FP's beacon code matches the aircraft's Mode-3 transponder, the data block flips
    /// to the creating facility. Skips aircraft that already have a track owner (no overwrite).
    /// </summary>
    internal void TickFlightPlanCreatorAutoTrack()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        var snapshot = World.GetSnapshot();

        foreach (var ac in snapshot)
        {
            if (ac.Track.Owner is not null)
            {
                continue;
            }

            var creator = ac.FlightPlan.CreatedByOwner;
            if (creator is null)
            {
                continue;
            }

            if (ac.Transponder.AssignedCode == 0 || ac.Transponder.Code != ac.Transponder.AssignedCode)
            {
                continue;
            }

            ac.Track.Owner = creator;
            ScratchpadRuleEngine.Apply(ac, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));

            _logger.LogInformation(
                "[FlightPlanCreatorAutoTrack] {Callsign} squawking assigned code {Code:D4} — owned by {Owner} (FP creator)",
                ac.Callsign,
                ac.Transponder.Code,
                TrackEngine.FormatOwner(creator)
            );
            EmitTerminal("System", ac.Callsign, $"[AutoTrack] Squawking assigned code — owned by {TrackEngine.FormatOwner(creator)} (FP creator)");
        }
    }

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
