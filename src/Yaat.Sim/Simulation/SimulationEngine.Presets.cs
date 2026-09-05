using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Scenario-authored timing: the release queue, timers, timed presets and global commands.
public sealed partial class SimulationEngine
{
    private void ProcessReleaseQueue()
    {
        var scenario = Scenario!;
        if (scenario.ReleaseQueue.Count == 0)
        {
            return;
        }

        var due = scenario.ReleaseQueue.Where(r => scenario.ElapsedSeconds >= r.FireAtSeconds).OrderBy(r => r.FireAtSeconds).ToList();
        if (due.Count == 0)
        {
            return;
        }

        scenario.ReleaseQueue.RemoveAll(r => scenario.ElapsedSeconds >= r.FireAtSeconds);

        foreach (var r in due)
        {
            var result = HeldReleaseService.Release(scenario, World, World.Rng, r.Callsign ?? r.Airport, null);
            if (result.Success)
            {
                EmitTerminal("System", r.Callsign ?? "", $"[HFR] {result.Message}");
            }
        }
    }

    /// <summary>
    /// Fires due TIMER countdowns (set via the TIMER command). Mirrors <see cref="ProcessReleaseQueue"/>:
    /// timers are gated on <see cref="SimScenarioState.ElapsedSeconds"/> so they count in sim time
    /// (paused with the sim, scaled by sim rate). On expiry each emits a green SAY-style terminal
    /// entry — the free-text message, or "timer expired" when none was given. Per-aircraft timers
    /// whose aircraft has been deleted are dropped silently so they never attribute a SAY to a gone
    /// aircraft.
    /// </summary>
    private void ProcessTimers()
    {
        var scenario = Scenario!;
        if (scenario.ActiveTimers.Count == 0)
        {
            return;
        }

        scenario.ActiveTimers.RemoveAll(t => t.Callsign is not null && World.FindAircraft(t.Callsign) is null);

        var due = scenario.ActiveTimers.Where(t => scenario.ElapsedSeconds >= t.FireAtSeconds).OrderBy(t => t.FireAtSeconds).ToList();
        if (due.Count == 0)
        {
            return;
        }

        scenario.ActiveTimers.RemoveAll(t => scenario.ElapsedSeconds >= t.FireAtSeconds);

        foreach (var t in due)
        {
            var message = string.IsNullOrWhiteSpace(t.Message) ? "timer expired" : t.Message;
            EmitTerminal("Say", t.Callsign ?? "TIMER", message);
        }
    }

    /// <summary>
    /// Auto-issues a takeoff clearance to released hold-for-release ground departures once they are
    /// holding short of their departure runway, after a short deterministic tower-readback jitter.
    /// </summary>
    internal void ProcessReleasedGroundDepartures()
    {
        var scenario = Scenario!;
        foreach (var ac in World.GetSnapshot())
        {
            if (!ac.Ground.ReleasedForDeparture)
            {
                continue;
            }

            // Only once it has reached the hold-short line of its departure runway.
            if (ac.Phases?.CurrentPhase is not HoldingShortPhase hs || hs.HoldShort.Reason != HoldShortReason.DestinationRunway)
            {
                continue;
            }

            if (scenario.ElapsedSeconds - ac.Ground.ReleasedAtSeconds < ReleaseAutoCtoJitterSeconds(ac.Callsign))
            {
                continue;
            }

            ac.Ground.ReleasedForDeparture = false;
            AutoIssueTakeoffClearance(ac);
        }
    }

    /// <summary>Deterministic 5–20 s tower-readback jitter from the callsign (FNV-1a; replay-safe, no RNG state).</summary>
    private static double ReleaseAutoCtoJitterSeconds(string callsign)
    {
        uint h = 2166136261u;
        foreach (var c in callsign)
        {
            h = (h ^ c) * 16777619u;
        }
        return HeldReleaseService.MinGroundReleaseAutoCtoJitterSeconds + (h % HeldReleaseService.GroundReleaseAutoCtoJitterRangeSeconds);
    }

    private void AutoIssueTakeoffClearance(AircraftState aircraft)
    {
        var parsed = CommandParser.ParseCompound("CTO", aircraft.FlightPlan.Route);
        if (!parsed.IsSuccess)
        {
            _logger.LogWarning("Auto-CTO parse failed for released departure {Callsign}", aircraft.Callsign);
            return;
        }

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var ctx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            () => World.GetSnapshot(),
            Scenario!.ValidateDctFixes,
            Scenario!.AutoCrossRunway,
            Scenario!.SoloTrainingMode,
            Scenario!.RpoShowPilotSpeech,
            AddTerminalEntry,
            Scenario!.ArtccConfig,
            Scenario!.ElapsedSeconds,
            PreserveConditionals: false,
            // The takeoff clearance is issued by the automated tower, not by the student (who only
            // lifted the hold-for-release). It is not the student establishing two-way comms, so it
            // must not mark initial contact — the departure still checks in after takeoff.
            IsScenarioScripted: true
        );
        CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, ctx);
        // The pilot's "ready for departure" call is answered even though the automated tower issued the
        // clearance — without this the request stays open and the pilot re-announces it every 120 s from
        // the air. No frequency-gate release / read-back / evaluator scoring: the student didn't speak.
        PilotRequestTracker.ApplyControllerResponse(aircraft, parsed.Value!, Scenario!.ElapsedSeconds);
        EmitTerminal("System", aircraft.Callsign, "[HFR] Released — cleared for takeoff");
    }

    private void ProcessTimedPresets()
    {
        var scenario = Scenario!;
        if (scenario.PresetQueue.Count == 0)
        {
            return;
        }

        List<AircraftState>? snapshot = null;

        for (int i = scenario.PresetQueue.Count - 1; i >= 0; i--)
        {
            var preset = scenario.PresetQueue[i];
            if (scenario.ElapsedSeconds < preset.FireAtSeconds)
            {
                continue;
            }

            scenario.PresetQueue.RemoveAt(i);
            snapshot ??= World.GetSnapshot();

            var aircraft = snapshot.FirstOrDefault(a => a.Callsign.Equals(preset.Callsign, StringComparison.OrdinalIgnoreCase));
            if (aircraft is null)
            {
                continue;
            }

            var timedResult = CommandParser.ParseCompound(preset.Command, aircraft.FlightPlan.Route);
            if (!timedResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Timed preset parse failed for {Callsign}: \"{Command}\" — {Reason}",
                    preset.Callsign,
                    preset.Command,
                    timedResult.Reason
                );
                EmitTerminal("Warning", preset.Callsign, $"[Preset] Unparseable: {preset.Command}");
                continue;
            }

            var compound = timedResult.Value!;

            if (TryDispatchImmediateTrackPreset(compound, aircraft))
            {
                EmitTerminal("System", preset.Callsign, $"[Preset] {preset.Command}");
                continue;
            }

            var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
            var presetCtx = new DispatchContext(
                groundLayout,
                World.Rng,
                World.Weather,
                FindAircraft,
                () => World.GetSnapshot(),
                scenario.ValidateDctFixes,
                scenario.AutoCrossRunway,
                scenario.SoloTrainingMode,
                scenario.RpoShowPilotSpeech,
                AddTerminalEntry,
                scenario.ArtccConfig,
                scenario.ElapsedSeconds,
                PreserveConditionals: false,
                IsScenarioScripted: true
            );
            var routeBeforeTimed = aircraft.Ground.AssignedTaxiRoute;
            var timedOutcome = CommandDispatcher.DispatchCompound(compound, aircraft, presetCtx);
            // A scripted clearance still answers whatever the pilot last asked for, so the pending
            // request closes and stops following up. Scripted commands emit no read-back and are not
            // scored — the student didn't issue them.
            PilotRequestTracker.ApplyControllerResponse(aircraft, compound, scenario.ElapsedSeconds);

            EmitTerminal("System", preset.Callsign, $"[Preset] {preset.Command}");
            ReportPresetOutcome(aircraft, preset.Command, timedOutcome, routeBeforeTimed);
        }
    }

    private void ProcessTriggers()
    {
        var scenario = Scenario!;
        for (int i = scenario.TriggerQueue.Count - 1; i >= 0; i--)
        {
            var trigger = scenario.TriggerQueue[i];
            if (scenario.ElapsedSeconds >= trigger.FireAtSeconds)
            {
                scenario.TriggerQueue.RemoveAt(i);
                ExecuteGlobalCommand(trigger.Command);
            }
        }
    }

    private void ExecuteGlobalCommand(string command)
    {
        var globalResult = CommandParser.Parse(command);
        if (!globalResult.IsSuccess)
        {
            _logger.LogWarning("Unknown trigger command: {Cmd} — {Reason}", command, globalResult.Reason);
            return;
        }

        var parsed = globalResult.Value!;
        if (parsed is SquawkAllCommand or SquawkNormalAllCommand or SquawkStandbyAllCommand)
        {
            var result = SquawkAll(parsed);
            EmitTerminal("System", "", $"[Trigger] {result.Message}");
        }
    }

    /// <summary>
    /// <c>SQALL</c> / <c>SNALL</c> / <c>SSALL</c>: every aircraft squawks its assigned code, mode C, or standby. The one
    /// body for the router's arm, the live server and the scenario triggers.
    /// </summary>
    public CommandResult SquawkAll(ParsedCommand command)
    {
        var count = 0;
        foreach (var ac in World.GetSnapshot())
        {
            switch (command)
            {
                case SquawkAllCommand:
                    ac.Transponder.Code = ac.Transponder.AssignedCode;
                    break;
                case SquawkNormalAllCommand:
                    ac.Transponder.Mode = "C";
                    break;
                case SquawkStandbyAllCommand:
                    ac.Transponder.Mode = "Standby";
                    break;
            }

            count++;
        }

        var verb = command switch
        {
            SquawkAllCommand => "SQALL",
            SquawkNormalAllCommand => "SNALL",
            SquawkStandbyAllCommand => "SSALL",
            _ => "?",
        };

        return new CommandResult(true, $"{verb}: {count} aircraft updated");
    }

    /// <summary>
    /// Optional callback invoked per aircraft before its presets are dispatched.
    /// Tests can use this to replace, modify, or clear presets for specific aircraft.
    /// </summary>
    public Action<LoadedAircraft>? PresetOverride { get; set; }

    private void DispatchSinglePreset(string command, AircraftState aircraft)
    {
        var presetResult = CommandParser.ParseCompound(command, aircraft.FlightPlan.Route);
        if (!presetResult.IsSuccess)
        {
            _logger.LogWarning("Preset parse failed for {Callsign}: \"{Command}\" — {Reason}", aircraft.Callsign, command, presetResult.Reason);
            EmitTerminal("Warning", aircraft.Callsign, $"[Preset] Unparseable: {command}");
            return;
        }

        var compound = presetResult.Value!;

        if (TryDispatchImmediateTrackPreset(compound, aircraft))
        {
            EmitTerminal("System", aircraft.Callsign, $"[Preset] {command}");
            return;
        }

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var singlePresetCtx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            () => World.GetSnapshot(),
            Scenario!.ValidateDctFixes,
            Scenario!.AutoCrossRunway,
            Scenario!.SoloTrainingMode,
            Scenario!.RpoShowPilotSpeech,
            AddTerminalEntry,
            Scenario!.ArtccConfig,
            Scenario!.ElapsedSeconds,
            PreserveConditionals: false,
            IsScenarioScripted: true
        );
        var routeBefore = aircraft.Ground.AssignedTaxiRoute;
        var presetOutcome = CommandDispatcher.DispatchCompound(compound, aircraft, singlePresetCtx);

        EmitTerminal("System", aircraft.Callsign, $"[Preset] {command}");
        ReportPresetOutcome(aircraft, command, presetOutcome, routeBefore);
    }

    /// <summary>
    /// Tells the instructor what a scripted preset actually did. A preset that fails when it fires — a TAXI
    /// whose route cannot be resolved from the gate, a DVIA with no STAR — used to leave only a server-log
    /// line behind the optimistic "[Preset] …" echo, so the instructor saw an aircraft that never moved and
    /// no reason (issue #396). A TAXI that succeeded with route advisories (a dropped unreachable lead-out
    /// lane, an unhonored turn hint) echoes each advisory as its own warning line — the response message a
    /// controller-issued TAXI carries them in is never shown for a scripted one. Only a route this dispatch
    /// installed is echoed (<paramref name="routeBefore"/> is the route object from before the dispatch): a
    /// deferred "WAIT 5 TAXI …" returns success immediately with the old route still assigned.
    /// </summary>
    private void ReportPresetOutcome(AircraftState aircraft, string command, CommandResult outcome, TaxiRoute? routeBefore)
    {
        if (!outcome.Success)
        {
            _logger.LogWarning("[Preset] {Callsign}: \"{Command}\" could not apply — {Message}", aircraft.Callsign, command, outcome.Message);
            EmitTerminal("Warning", aircraft.Callsign, $"[Preset] could not apply: {outcome.Message}");
            return;
        }

        if (aircraft.Ground.AssignedTaxiRoute is not { Warnings.Count: > 0 } route || ReferenceEquals(route, routeBefore))
        {
            return;
        }

        foreach (string warning in route.Warnings)
        {
            EmitTerminal("Warning", aircraft.Callsign, $"[Preset] {warning}");
        }
    }

    public void DispatchPresetCommands(LoadedAircraft loaded)
    {
        var scenario = Scenario!;

        PresetOverride?.Invoke(loaded);

        // Backstop for filed flight plans missing a destination: fall back to the
        // scenario's primary airport so arrivals show up in STARS arrival lists.
        // Skipped for cold-call aircraft (HasFlightPlan == false) — those must
        // remain destination-less until a controller files via DA / VP.
        if (
            loaded.State.FlightPlan.HasFlightPlan
            && string.IsNullOrWhiteSpace(loaded.State.FlightPlan.Destination)
            && !string.IsNullOrWhiteSpace(scenario.PrimaryAirportId)
        )
        {
            loaded.State.FlightPlan.Destination = scenario.PrimaryAirportId;
        }

        // Separate immediate presets from delayed ones.
        var immediatePresets = new List<string>();
        foreach (var preset in loaded.PresetCommands)
        {
            if (preset.TimeOffset > 0)
            {
                scenario.PresetQueue.Add(
                    new ScheduledPreset
                    {
                        Callsign = loaded.State.Callsign,
                        Command = preset.Command,
                        FireAtSeconds = scenario.ElapsedSeconds + preset.TimeOffset,
                    }
                );
            }
            else
            {
                immediatePresets.Add(preset.Command);
            }
        }

        // CFIX is additive — it stamps the named route fix in place — so multiple CFIX presets
        // can be dispatched independently and all their crossing restrictions land at spawn.
        // Compose into a single sequential compound only when a CFIX is followed by a non-CFIX
        // command (e.g. "CFIX ...; CAPP"): that later command must wait until the crossing fix
        // is reached, otherwise it would rebuild the route and lose the CFIX restrictions.
        bool allCfix = immediatePresets.All(p => p.TrimStart().StartsWith("CFIX ", StringComparison.OrdinalIgnoreCase));
        if (!allCfix && immediatePresets.Count >= 2 && immediatePresets[0].TrimStart().StartsWith("CFIX ", StringComparison.OrdinalIgnoreCase))
        {
            var composed = string.Join("; ", immediatePresets);
            DispatchSinglePreset(composed, loaded.State);
            return;
        }

        foreach (var cmd in immediatePresets)
        {
            DispatchSinglePreset(cmd, loaded.State);
        }
    }

    // --- Replay helpers ---
}
