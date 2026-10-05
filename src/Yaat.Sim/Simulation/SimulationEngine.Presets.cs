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
        SimScenarioState scenario = Scenario!;
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

        foreach (ScheduledRelease? r in due)
        {
            HeldReleaseResult result = HeldReleaseService.Release(scenario, World, World.Rng, r.Callsign ?? r.Airport, null);
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
        SimScenarioState scenario = Scenario!;
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

        foreach (ActiveTimer? t in due)
        {
            string message = string.IsNullOrWhiteSpace(t.Message) ? "timer expired" : t.Message;
            EmitTerminal("Say", t.Callsign ?? "TIMER", message);
        }
    }

    /// <summary>
    /// Auto-issues a takeoff clearance to released hold-for-release ground departures once they are
    /// holding short of their departure runway, after a short deterministic tower-readback jitter. Solo rooms only: in an
    /// RPO room a released departure is released and nothing more, and leaves on the RPO's takeoff clearance or its preset.
    /// </summary>
    internal void ProcessReleasedGroundDepartures()
    {
        SimScenarioState scenario = Scenario!;
        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (!ac.Ground.ReleasedForDeparture)
            {
                continue;
            }

            if (!scenario.SoloTrainingMode)
            {
                ac.Ground.ReleasedForDeparture = false;
                _logger.LogDebug("{Callsign} released in an RPO room: no automatic takeoff clearance", ac.Callsign);
                continue;
            }

            // A takeoff clearance it already has (a held preset CTO that fired on the release) launched it: the release
            // has nothing left to clear.
            if ((ac.Phases?.DepartureClearance is not null) || !ac.IsOnGround)
            {
                ac.Ground.ReleasedForDeparture = false;
                continue;
            }

            // Only once it has reached the hold-short line of its departure runway, or is lined up on it as a runway spawn
            // that asked the radar student for its release. Any other released departure the controller lines up waits
            // for the controller's own takeoff clearance.
            if (!IsAtItsDepartureRunway(ac))
            {
                continue;
            }

            if (scenario.ElapsedSeconds - ac.Ground.ReleasedAtSeconds < ReleaseAutoCtoJitterSeconds(ac.Callsign))
            {
                continue;
            }

            ac.Ground.ReleasedForDeparture = false;
            AutoIssueTakeoffClearance(ac, "[HFR] Released — cleared for takeoff");
        }
    }

    private static bool IsAtItsDepartureRunway(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase switch
        {
            HoldingShortPhase hs => hs.HoldShort.Reason == HoldShortReason.DestinationRunway,
            LinedUpAndWaitingPhase => HasItsRelease(aircraft),
            _ => false,
        };

    /// <summary>
    /// A runway spawn that asked the radar student for its release (<see cref="RunwaySpawnCall.TryRequestRelease"/>) and had
    /// its <see cref="PilotPendingRequestKind.Release"/> request answered by <c>REL</c> or <c>HFROFF</c>, or one released
    /// through the hold-for-release spawn gate (<see cref="AircraftGroundOps.ReleasedAtSpawnGate"/>), which departs the same way.
    /// </summary>
    private static bool HasItsRelease(AircraftState aircraft) =>
        (aircraft.Ground.InitialCallup == InitialCallupPlan.RunwayNoPreset)
        && (
            aircraft.Ground.ReleasedAtSpawnGate
            || (aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Release, ResponseState: PilotPendingRequestResponseState.Satisfied })
        );

    /// <summary>
    /// Clears a runway spawn with no preset for takeoff at its lined-up call point when the student works a radar position
    /// and either its field has a tower or it flies VFR (<see cref="RunwaySpawnCall"/>), in a solo room only: the simulated tower sends it off,
    /// or a VFR pilot at an untowered field departs on its own, scripted, so the existing airborne check-in calls the
    /// student. An IFR spawn at an untowered field released through the hold-for-release spawn gate needs no release
    /// request: lined up in a solo room, it starts the released departure's clock (<see cref="ProcessReleasedGroundDepartures"/>)
    /// at once (<see cref="StartSpawnGateRelease"/>). Closes the call-up decision of every such spawn that has left its
    /// lined-up position or whose student does not work a radar position, so <see cref="IsRunwaySpawnFieldTowered"/> stops
    /// evaluating it.
    /// </summary>
    internal void ProcessRunwaySpawnAutoTakeoffs()
    {
        SimScenarioState scenario = Scenario!;
        bool radarStudent = RunwaySpawnCall.IsRadarStudent(scenario.StudentPositionType);
        foreach (AircraftState ac in World.GetSnapshot())
        {
            if ((ac.Ground.InitialCallup != InitialCallupPlan.RunwayNoPreset) || ac.Ground.InitialCallupDecisionProcessed)
            {
                continue;
            }

            if (ShouldCloseDecision(ac, radarStudent))
            {
                ac.Ground.InitialCallupDecisionProcessed = true;
                continue;
            }

            if (IsUntoweredIfrReleasedAtSpawnGate(ac))
            {
                StartSpawnGateRelease(ac, scenario);
                continue;
            }

            TryAutoTakeoffAtCallPoint(ac, scenario);
        }
    }

    /// <summary>
    /// Whether an open runway-spawn call-up decision closes with nothing to do: the student works no radar position, or the
    /// spawn is no longer lined up.
    /// </summary>
    private static bool ShouldCloseDecision(AircraftState aircraft, bool radarStudent) =>
        !radarStudent || (aircraft.Phases?.CurrentPhase is not LinedUpAndWaitingPhase);

    private bool IsUntoweredIfrReleasedAtSpawnGate(AircraftState aircraft) =>
        aircraft.Ground.ReleasedAtSpawnGate && !aircraft.FlightPlan.IsVfr && !IsRunwaySpawnFieldTowered(aircraft);

    /// <summary>
    /// At its lined-up call point, a runway spawn at a towered field, or a VFR one at an untowered field, closes its
    /// call-up decision and, in a solo room, is cleared for takeoff by the simulated tower or departs on its own.
    /// </summary>
    private void TryAutoTakeoffAtCallPoint(AircraftState aircraft, SimScenarioState scenario)
    {
        if (!RunwaySpawnCall.IsAtAutoTakeoffPoint(aircraft, scenario.StudentPositionType))
        {
            return;
        }

        bool towered = IsRunwaySpawnFieldTowered(aircraft);
        if (!towered && !aircraft.FlightPlan.IsVfr)
        {
            return;
        }

        // The takeoff clearance takes the lined-up call's place: the phase must not make it this tick. In an RPO room
        // nothing departs on its own (the simulated tower and the VFR self-departure are solo-room stand-ins), so the
        // decision closes with no call and no clearance, and the spawn waits lined up for the RPO's takeoff clearance.
        aircraft.Ground.InitialCallupDecisionProcessed = true;
        aircraft.HasAnnouncedLinedUpReady = true;
        if (!scenario.SoloTrainingMode)
        {
            _logger.LogDebug("{Callsign} lined up in an RPO room: call-up closed, waiting for the RPO's takeoff clearance", aircraft.Callsign);
            return;
        }

        AutoIssueTakeoffClearance(
            aircraft,
            towered ? "[Auto] Towered field — cleared for takeoff by the simulated tower" : "[Auto] Untowered field — VFR departure on its own"
        );
    }

    /// <summary>
    /// Whether a <see cref="InitialCallupPlan.RunwayNoPreset"/> runway spawn whose call is still open, lined up under a radar
    /// student, sits at a towered field (<see cref="AiPositionResolver.IsTowered"/>); false for every other aircraft, so the
    /// config is read only for them.
    /// </summary>
    private bool IsRunwaySpawnFieldTowered(AircraftState aircraft) =>
        (aircraft.Ground.InitialCallup == InitialCallupPlan.RunwayNoPreset)
        && !aircraft.Ground.InitialCallupDecisionProcessed
        && (aircraft.Phases?.CurrentPhase is LinedUpAndWaitingPhase)
        && RunwaySpawnCall.IsRadarStudent(Scenario?.StudentPositionType)
        && (Scenario?.ArtccConfig is { } config)
        && (RunwaySpawnCall.RunwayOf(aircraft) is { } runway)
        && AiPositionResolver.IsTowered(config, runway.AirportId);

    /// <summary>
    /// A lined-up IFR runway spawn at an untowered field released through the hold-for-release spawn gate makes no release
    /// request and no lined-up call. In a solo room it departs as one that asked and got <c>REL</c> now would, on the
    /// line-up-and-wait auto-clearance after the released departure's tower jitter; in an RPO room nothing departs on its
    /// own, so it waits lined up for the RPO's takeoff clearance.
    /// </summary>
    private void StartSpawnGateRelease(AircraftState aircraft, SimScenarioState scenario)
    {
        aircraft.Ground.InitialCallupDecisionProcessed = true;
        aircraft.HasAnnouncedLinedUpReady = true;
        if (!scenario.SoloTrainingMode)
        {
            _logger.LogDebug("{Callsign} released at the spawn gate in an RPO room: waiting for the RPO's takeoff clearance", aircraft.Callsign);
            return;
        }

        aircraft.Ground.ReleasedForDeparture = true;
        aircraft.Ground.ReleasedAtSeconds = scenario.ElapsedSeconds;
        _logger.LogDebug("{Callsign} released at the spawn gate: the released departure's takeoff clock starts", aircraft.Callsign);
    }

    /// <summary>
    /// Deterministic 5–20 s tower-readback jitter from the callsign (<see cref="DeterministicHash"/> with no salt;
    /// replay-safe, no RNG state).
    /// </summary>
    internal static double ReleaseAutoCtoJitterSeconds(string callsign) =>
        HeldReleaseService.MinGroundReleaseAutoCtoJitterSeconds
        + (DeterministicHash.Fnv1a("", callsign) % HeldReleaseService.GroundReleaseAutoCtoJitterRangeSeconds);

    private void AutoIssueTakeoffClearance(AircraftState aircraft, string systemNote)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("CTO", aircraft.FlightPlan.Route);
        if (!parsed.IsSuccess)
        {
            _logger.LogWarning("Auto-CTO parse failed for released departure {Callsign}", aircraft.Callsign);
            return;
        }

        AirportGroundLayout? groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var ctx = new DispatchContext
        {
            GroundLayout = groundLayout,
            Rng = World.Rng,
            Weather = World.Weather,
            FindAircraft = FindAircraft,
            ListAircraft = () => World.GetSnapshot(),
            ValidateDctFixes = Scenario!.ValidateDctFixes,
            AutoCrossRunway = Scenario!.AutoCrossRunway,
            SoloTrainingMode = Scenario!.SoloTrainingMode,
            SoloRpoCommandsAllowed = Scenario!.SoloRpoCommandsAllowed,
            RpoShowPilotSpeech = Scenario!.RpoShowPilotSpeech,
            TerminalEmitter = AddTerminalEntry,
            ArtccConfig = Scenario!.ArtccConfig,
            ScenarioElapsedSeconds = Scenario!.ElapsedSeconds,
            SessionStartUtc = Scenario!.SessionStartUtc,
            PreserveConditionals = false,
            // The takeoff clearance is issued by the automated tower, not by the student (who only
            // lifted the hold-for-release). It is not the student establishing two-way comms, so it
            // must not mark initial contact — the departure still checks in after takeoff.
            IsScenarioScripted = true,
            FacilityHint = Scenario?.StudentPosition?.FacilityId,
        };
        CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, ctx);
        // The pilot's "ready for departure" call is answered even though the automated tower issued the
        // clearance — without this the request stays open and the pilot re-announces it every 120 s from
        // the air. No frequency-gate release / read-back / evaluator scoring: the student didn't speak.
        PilotRequestTracker.ApplyControllerResponse(aircraft, parsed.Value!, Scenario!.ElapsedSeconds);
        EmitTerminal("System", aircraft.Callsign, systemNote);
    }

    /// <summary>
    /// A due timed preset whose first block (what the dispatcher applies now) would clear a departure held for release onto
    /// its runway (<see cref="HeldReleaseService.IsRunwayEntryCommand"/>, the commands the hold-for-release gate refuses)
    /// stays queued while the hold lasts and fires once the departure is released (REL or HFROFF), rather than being refused
    /// and dropped. A later block waits for the release in the aircraft's command queue (<c>FlightPhysics.WaitsForRelease</c>),
    /// so a preset that taxis first dispatches on time.
    /// </summary>
    private static bool IsHeldUntilRelease(AircraftState aircraft, ParseResult<CompoundCommand> parsed) =>
        aircraft.Ground.HeldForRelease
        && (parsed is { IsSuccess: true, Value.Blocks: [ParsedBlock first, ..] })
        && first.Commands.Any(HeldReleaseService.IsRunwayEntryCommand);

    /// <summary>
    /// Dispatches every due timed preset in fire-time order; presets due at the same time keep the reverse queue order the
    /// timed-preset walk has always used (a later-listed preset first). Once an aircraft has a preset held until its release
    /// (<see cref="IsHeldUntilRelease"/>), every later-due preset for it is held too, so none jumps ahead; on the release
    /// they dispatch in that same order.
    /// </summary>
    private void ProcessTimedPresets()
    {
        SimScenarioState scenario = Scenario!;
        List<ScheduledPreset> due =
        [
            .. scenario
                .PresetQueue.Select((preset, index) => (Preset: preset, Index: index))
                .Where(entry => scenario.ElapsedSeconds >= entry.Preset.FireAtSeconds)
                .OrderBy(entry => entry.Preset.FireAtSeconds)
                .ThenByDescending(entry => entry.Index)
                .Select(entry => entry.Preset),
        ];
        if (due.Count == 0)
        {
            return;
        }

        List<AircraftState> snapshot = World.GetSnapshot();
        var holding = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ScheduledPreset preset in due)
        {
            AircraftState? aircraft = snapshot.FirstOrDefault(a => a.Callsign.Equals(preset.Callsign, StringComparison.OrdinalIgnoreCase));
            if (aircraft is null)
            {
                scenario.PresetQueue.Remove(preset);
                continue;
            }

            if (holding.Contains(preset.Callsign))
            {
                continue;
            }

            ParseResult<CompoundCommand> timedResult = CommandParser.ParseCompound(preset.Command, aircraft.FlightPlan.Route);
            if (IsHeldUntilRelease(aircraft, timedResult))
            {
                holding.Add(preset.Callsign);
                continue;
            }

            scenario.PresetQueue.Remove(preset);
            DispatchTimedPreset(preset, aircraft, timedResult);
        }
    }

    private void DispatchTimedPreset(ScheduledPreset preset, AircraftState aircraft, ParseResult<CompoundCommand> timedResult)
    {
        if (!timedResult.IsSuccess)
        {
            _logger.LogWarning(
                "Timed preset parse failed for {Callsign}: \"{Command}\" — {Reason}",
                preset.Callsign,
                preset.Command,
                timedResult.Reason
            );
            EmitTerminal("Warning", preset.Callsign, $"[Preset] Unparseable: {preset.Command}");
            return;
        }

        CompoundCommand compound = timedResult.Value!;

        if (TryDispatchImmediateTrackPreset(compound, aircraft))
        {
            EmitTerminal("System", preset.Callsign, $"[Preset] {preset.Command}");
            return;
        }

        TaxiRoute? routeBeforeTimed = aircraft.Ground.AssignedTaxiRoute;
        CommandResult timedOutcome = CommandDispatcher.DispatchCompound(compound, aircraft, BuildTimedPresetContext(aircraft));
        // A scripted clearance still answers whatever the pilot last asked for, so the pending
        // request closes and stops following up. Scripted commands emit no read-back and are not
        // scored — the student didn't issue them.
        PilotRequestTracker.ApplyControllerResponse(aircraft, compound, Scenario!.ElapsedSeconds);

        EmitTerminal("System", preset.Callsign, $"[Preset] {preset.Command}");
        ReportPresetOutcome(aircraft, preset.Command, timedOutcome, routeBeforeTimed);
    }

    private DispatchContext BuildTimedPresetContext(AircraftState aircraft)
    {
        SimScenarioState scenario = Scenario!;
        return new DispatchContext
        {
            GroundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft),
            Rng = World.Rng,
            Weather = World.Weather,
            FindAircraft = FindAircraft,
            ListAircraft = () => World.GetSnapshot(),
            ValidateDctFixes = scenario.ValidateDctFixes,
            AutoCrossRunway = scenario.AutoCrossRunway,
            SoloTrainingMode = scenario.SoloTrainingMode,
            SoloRpoCommandsAllowed = scenario.SoloRpoCommandsAllowed,
            RpoShowPilotSpeech = scenario.RpoShowPilotSpeech,
            TerminalEmitter = AddTerminalEntry,
            ArtccConfig = scenario.ArtccConfig,
            ScenarioElapsedSeconds = scenario.ElapsedSeconds,
            SessionStartUtc = scenario.SessionStartUtc,
            PreserveConditionals = false,
            IsScenarioScripted = true,
            FacilityHint = scenario.StudentPosition?.FacilityId,
        };
    }

    private void ProcessTriggers()
    {
        SimScenarioState scenario = Scenario!;
        for (int i = scenario.TriggerQueue.Count - 1; i >= 0; i--)
        {
            ScheduledTrigger trigger = scenario.TriggerQueue[i];
            if (scenario.ElapsedSeconds >= trigger.FireAtSeconds)
            {
                scenario.TriggerQueue.RemoveAt(i);
                ExecuteGlobalCommand(trigger.Command);
            }
        }
    }

    private void ExecuteGlobalCommand(string command)
    {
        ParseResult<ParsedCommand> globalResult = CommandParser.Parse(command);
        if (!globalResult.IsSuccess)
        {
            _logger.LogWarning("Unknown trigger command: {Cmd} — {Reason}", command, globalResult.Reason);
            return;
        }

        ParsedCommand parsed = globalResult.Value!;
        if (parsed is SquawkAllCommand or SquawkNormalAllCommand or SquawkStandbyAllCommand)
        {
            CommandResult result = SquawkAll(parsed);
            EmitTerminal("System", "", $"[Trigger] {result.Message}");
        }
    }

    /// <summary>
    /// <c>SQALL</c> / <c>SNALL</c> / <c>SSALL</c>: every aircraft squawks its assigned code, mode C, or standby. The one
    /// body for the router's arm, the live server and the scenario triggers.
    /// </summary>
    public CommandResult SquawkAll(ParsedCommand command)
    {
        int count = 0;
        foreach (AircraftState ac in World.GetSnapshot())
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

        string verb = command switch
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
        ParseResult<CompoundCommand> presetResult = CommandParser.ParseCompound(command, aircraft.FlightPlan.Route);
        if (!presetResult.IsSuccess)
        {
            _logger.LogWarning("Preset parse failed for {Callsign}: \"{Command}\" — {Reason}", aircraft.Callsign, command, presetResult.Reason);
            EmitTerminal("Warning", aircraft.Callsign, $"[Preset] Unparseable: {command}");
            return;
        }

        CompoundCommand compound = presetResult.Value!;

        if (TryDispatchImmediateTrackPreset(compound, aircraft))
        {
            EmitTerminal("System", aircraft.Callsign, $"[Preset] {command}");
            return;
        }

        AirportGroundLayout? groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var singlePresetCtx = new DispatchContext
        {
            GroundLayout = groundLayout,
            Rng = World.Rng,
            Weather = World.Weather,
            FindAircraft = FindAircraft,
            ListAircraft = () => World.GetSnapshot(),
            ValidateDctFixes = Scenario!.ValidateDctFixes,
            AutoCrossRunway = Scenario!.AutoCrossRunway,
            SoloTrainingMode = Scenario!.SoloTrainingMode,
            SoloRpoCommandsAllowed = Scenario!.SoloRpoCommandsAllowed,
            RpoShowPilotSpeech = Scenario!.RpoShowPilotSpeech,
            TerminalEmitter = AddTerminalEntry,
            ArtccConfig = Scenario!.ArtccConfig,
            ScenarioElapsedSeconds = Scenario!.ElapsedSeconds,
            SessionStartUtc = Scenario!.SessionStartUtc,
            PreserveConditionals = false,
            IsScenarioScripted = true,
            FacilityHint = Scenario?.StudentPosition?.FacilityId,
        };
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;
        CommandResult presetOutcome = CommandDispatcher.DispatchCompound(compound, aircraft, singlePresetCtx);

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
        SimScenarioState scenario = Scenario!;

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
        foreach (PresetCommand preset in loaded.PresetCommands)
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
            string composed = string.Join("; ", immediatePresets);
            DispatchSinglePreset(composed, loaded.State);
            return;
        }

        foreach (string cmd in immediatePresets)
        {
            DispatchSinglePreset(cmd, loaded.State);
        }
    }

    // --- Replay helpers ---
}
