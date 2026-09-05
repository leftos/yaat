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

// Applying a recorded command during replay, and the setting/generator/weather appliers it routes to.
public sealed partial class SimulationEngine
{
    // Public for tests (replay-determinism of the command-run delay); production drives this through
    // the Replay / FastForwardTo entry points.
    public void ReplayCommand(RecordedCommand cmd)
    {
        // Track and AS-prefix commands run before the aircraft-exists guard so per-connection
        // active-position state still updates when the addressed aircraft hasn't spawned yet
        // (e.g. auto-accept sims firing for delayed-spawn aircraft). The applier safely no-ops
        // any per-aircraft mutation when aircraft is null.
        var asPrefixCheck = TrackResolver.ExtractAsPrefix(cmd.Command);

        // A flight-plan command (typed DA/FP/RMK, or the CRC "AS {tcp} DA ..." echo) changed only the flight
        // plan live, through the server's flight-plan arm, and that change rides in the RecordedAmendFlightPlan
        // recorded alongside it. Skip it before the AS-prefix and dispatcher paths below: either would treat
        // it as a manoeuvring command and clear the phase the aircraft was flying.
        if (RecordedCommandClassifier.Classify(asPrefixCheck.Remainder).Kind == RecordedCommandKind.FlightPlan)
        {
            _logger.LogDebug(
                "Replay: skipping flight-plan command {Cmd} for {Callsign} (state arrives via the recorded amendment)",
                cmd.Command,
                cmd.Callsign
            );
            return;
        }

        var firstParse = CommandParser.Parse(asPrefixCheck.Remainder);
        if (
            firstParse.IsSuccess
            && firstParse.Value is not null
            && (TrackEngine.IsTrackCommand(firstParse.Value) || asPrefixCheck.AsOverrideTcp is not null)
        )
        {
            _replayTrackApplier.Apply(cmd.Command, FindAircraft(cmd.Callsign), cmd.ConnectionId, Scenario);
            return;
        }

        var (kind, _, parsed) = RecordedCommandClassifier.Classify(cmd.Command);

        switch (kind)
        {
            case RecordedCommandKind.SayOrShow:
                return;

            case RecordedCommandKind.Delete:
                // Before aircraft-exists guard: target may be in the delayed queue only.
                DeleteAircraft(cmd.Callsign);
                return;

            case RecordedCommandKind.SpawnNow:
                // Before aircraft-exists guard: a manual spawn pulls the aircraft FROM the delayed
                // queue, so it is intentionally not active yet. Gating it behind FindAircraft would
                // silently drop every recorded manual spawn on replay (and snapshot regeneration).
                HandleSpawnNow(cmd.Callsign);
                return;

            case RecordedCommandKind.SpawnDelay:
                // Before aircraft-exists guard: re-times a still-queued delayed spawn.
                HandleSpawnDelay(cmd.Callsign, ((SpawnDelayCommand)parsed!).Seconds);
                return;

            case RecordedCommandKind.Timer:
                if (Scenario is not null && parsed is TimerCommand timerCmd)
                {
                    TimerCommandReplayer.Apply(timerCmd, Scenario, World, cmd.Callsign);
                }

                return;

            case RecordedCommandKind.HoldForRelease:
                if (Scenario is not null && parsed is HoldForReleaseCommand hfr)
                {
                    HeldReleaseService.Arm(Scenario, World, hfr.Airport);
                }

                return;

            case RecordedCommandKind.DisarmHoldForRelease:
                if (Scenario is not null && parsed is DisarmHoldForReleaseCommand hfrOff)
                {
                    HeldReleaseService.Disarm(Scenario, World, hfrOff.Airport);
                }

                return;

            case RecordedCommandKind.ReleaseDeparture:
                if (Scenario is not null && parsed is ReleaseDepartureCommand rel)
                {
                    HeldReleaseService.ReplayRelease(Scenario, World, rel.Target, rel.IntervalSeconds, cmd.SpawnJitterSeconds);
                }

                return;

            case RecordedCommandKind.Coordination:
            case RecordedCommandKind.GlobalCoordination:
                // RD/RDH/RDR/RDACK/RDAUTO mutate state owned by yaat-server only.
                _logger.LogDebug("Replay: skipping coordination command {Cmd} for {Callsign} (no Sim-side handler)", cmd.Command, cmd.Callsign);
                return;

            case RecordedCommandKind.GhostTrack:
            case RecordedCommandKind.Strip:
            case RecordedCommandKind.TrackOwnership:
            case RecordedCommandKind.Consolidate:
            case RecordedCommandKind.Deconsolidate:
            case RecordedCommandKind.AcceptAllHandoffs:
            case RecordedCommandKind.InitiateHandoffAll:
                // Server-only handlers; Sim has no state to mutate.
                return;
        }

        var aircraft = FindAircraft(cmd.Callsign);
        if (aircraft is null)
        {
            return;
        }

        switch (kind)
        {
            case RecordedCommandKind.DeleteQueued:
                ReplayDeleteQueued(aircraft, ((DeleteQueuedCommand)parsed!).BlockNumber);
                return;

            case RecordedCommandKind.SquawkAll:
                HandleGlobalSquawkCommand(parsed!);
                return;

            case RecordedCommandKind.Note:
                aircraft.Note = AircraftState.TruncateNote(((NoteCommand)parsed!).Text);
                return;
        }

        var replayResult = CommandParser.ParseCompound(cmd.Command, aircraft.FlightPlan.Route);
        if (!replayResult.IsSuccess)
        {
            _logger.LogDebug(
                "[Replay] {Callsign}: recorded command '{Command}' failed to parse on replay — {Reason}",
                cmd.Callsign,
                cmd.Command,
                replayResult.Reason
            );
            return;
        }

        // Recorded command-run delay: reproduce the exact delay sampled at the live run rather than
        // re-rolling (a re-roll would draw from a divergent RNG state and break determinism). The
        // deferral fires through ProcessDeferredDispatches during replay ticking, exactly as it did live.
        // The recorded connection id says who issued the command; an AI-controller command replays with the same
        // origin it had live (no student contact, no evaluator scoring).
        var origin = AiConnectionId.OriginOf(cmd.ConnectionId);
        bool human = origin == DispatchOrigin.Human;
        bool answering = Scenario?.PilotContacts.AnyAnswering ?? false;

        if (cmd.ReactionDelaySeconds is double recordedReactionSeconds)
        {
            aircraft.DeferredDispatches.Add(
                new DeferredDispatch(recordedReactionSeconds, replayResult.Value!)
                {
                    SourceText = cmd.Command,
                    IsReactionDelay = true,
                    IsScenarioScripted = !human,
                }
            );
            // Mirror the live SendCommand path: a deferred command still counts as accepted, so it
            // establishes two-way comms at issue time (not when the deferral later fires). Without this
            // a replayed/reconstructed vector never clears the Class B/C boundary-hold gate.
            ApplyReplayPostDispatch(aircraft, replayResult.Value!, human, answering);
            return;
        }

        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var replayCtx = new DispatchContext(
            groundLayout,
            World.Rng,
            World.Weather,
            FindAircraft,
            () => World.GetSnapshot(),
            Scenario?.ValidateDctFixes ?? true,
            Scenario?.AutoCrossRunway ?? false,
            Scenario?.SoloTrainingMode ?? false,
            Scenario?.RpoShowPilotSpeech ?? false,
            AddTerminalEntry,
            Scenario?.ArtccConfig,
            Scenario?.ElapsedSeconds ?? 0,
            PreserveConditionals: false,
            IsScenarioScripted: !human
        );
        var replayDispatchResult = CommandDispatcher.DispatchCompound(replayResult.Value!, aircraft, replayCtx);
        if (replayDispatchResult.Success)
        {
            // Mirror the live SendCommand path so a replayed/reconstructed instruction establishes the
            // two-way comms that clears the Class B/C boundary-hold gate.
            ApplyReplayPostDispatch(aircraft, replayResult.Value!, human, answering);
        }
        else
        {
            // Debug, not Warning: recordings faithfully replay commands that were rejected during the
            // live session too (e.g. TDLSS to a parked aircraft), so a rejection here is usually
            // expected, not a divergence. Enable this category at Debug to surface a command that
            // stopped taking effect because the replay layout drifted from the captured one.
            _logger.LogDebug(
                "[Replay] {Callsign}: recorded command '{Command}' was rejected on replay — {Message}",
                aircraft.Callsign,
                cmd.Command,
                replayDispatchResult.Message
            );
        }
    }

    /// <summary>
    /// The replay half of <see cref="ApplyPostDispatch"/>: contact registration and evaluator scoring for a human
    /// command, pending-request resolution whenever someone answers pilots. Read-backs are deliberately not re-fired
    /// on replay.
    /// </summary>
    private void ApplyReplayPostDispatch(AircraftState aircraft, CompoundCommand compound, bool human, bool answering)
    {
        double elapsedSeconds = Scenario?.ElapsedSeconds ?? 0;
        if (human)
        {
            Pilot.PilotInitialContactEligibility.RegisterControllerContact(aircraft, Scenario, compound);
        }

        if (human && Scenario?.SoloTrainingMode == true)
        {
            SoloTrainingEvaluator.RecordControllerCommand(aircraft, compound, elapsedSeconds, World.GetSnapshot());
        }

        if (answering)
        {
            PilotRequestTracker.ApplyControllerResponse(aircraft, compound, elapsedSeconds);
        }
    }

    private void HandleSpawnNow(string callsign)
    {
        var scenario = Scenario!;
        var entry = scenario.DelayedQueue.FirstOrDefault(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        scenario.DelayedQueue.Remove(entry);
        entry.Aircraft.State.SpawnedAtSeconds = scenario.ElapsedSeconds;
        World.AddAircraft(entry.Aircraft.State);
        DispatchPresetCommands(entry.Aircraft);
    }

    private void HandleSpawnDelay(string callsign, int seconds)
    {
        var scenario = Scenario!;
        var entry = scenario.DelayedQueue.FirstOrDefault(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        entry.SpawnAtSeconds = (int)scenario.ElapsedSeconds + seconds;
    }

    private void ApplySettingChange(RecordedSettingChange setting)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        // Mirrors yaat-server's SimControlService recorders. Every setting the
        // server records mid-session must round-trip through replay so bundle
        // playback (and snapshot regeneration at export time) matches what the
        // user actually saw live.
        switch (setting.Setting)
        {
            case "AutoClearedToLand":
                if (bool.TryParse(setting.Value, out var ctl))
                {
                    scenario.AutoClearedToLand = ctl;
                }
                break;
            case "AutoCrossRunway":
                if (bool.TryParse(setting.Value, out var acr))
                {
                    scenario.AutoCrossRunway = acr;
                    World.ApplyAutoCrossToActiveTaxiRoutes(acr);
                }
                break;
            case "AutoPullUpToParallel":
                // Only affects future landing exits — no active-route walk needed.
                if (bool.TryParse(setting.Value, out var apup))
                {
                    scenario.AutoPullUpToParallel = apup;
                }
                break;
            case "AutoGoAroundOnOccupiedRunway":
                if (bool.TryParse(setting.Value, out var agor))
                {
                    scenario.AutoGoAroundOnOccupiedRunway = agor;
                }
                break;
            case "AutoRejectTakeoffOnOccupiedRunway":
                if (bool.TryParse(setting.Value, out var arto))
                {
                    scenario.AutoRejectTakeoffOnOccupiedRunway = arto;
                }
                break;
            case "LiveTrafficEnabled":
                if (bool.TryParse(setting.Value, out var live))
                {
                    scenario.LiveTrafficEnabled = live;
                }
                break;
            case "LiveTrafficCeilingFt":
                if (int.TryParse(setting.Value, out var ceiling))
                {
                    scenario.LiveTrafficCeilingFt = ceiling;
                }
                break;
            case "LiveTrafficFilter":
                scenario.LiveTrafficFilter = setting.Value ?? "";
                break;
            case "LiveTrafficFeedTimeUtc":
                // Diagnostic: where the room stood in the feed. Replay is driven by the recorded samples themselves.
                break;
            case "AutoAcceptDelay":
                if (int.TryParse(setting.Value, out var seconds))
                {
                    scenario.AutoAcceptDelay = seconds < 0 ? TimeSpan.FromSeconds(-1) : TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60));
                }
                break;
            case "CommandRunDelayMinSeconds":
                if (int.TryParse(setting.Value, out var crdMin))
                {
                    scenario.CommandRunDelayMinSeconds = Math.Clamp(crdMin, 0, 60);
                }
                break;
            case "CommandRunDelayMaxSeconds":
                if (int.TryParse(setting.Value, out var crdMax))
                {
                    scenario.CommandRunDelayMaxSeconds = Math.Clamp(crdMax, 0, 60);
                }
                break;
            case "AutoDeleteMode":
                // Server writes ClientAutoDeleteOverride, not ScenarioAutoDeleteMode.
                // Null/empty string is a valid value: it means "clear the override and
                // fall back to the scenario default".
                scenario.ClientAutoDeleteOverride = string.IsNullOrEmpty(setting.Value) ? null : setting.Value;
                break;
            case "ValidateDctFixes":
                if (bool.TryParse(setting.Value, out var validate))
                {
                    scenario.ValidateDctFixes = validate;
                }
                break;
            case "SoloTrainingMode":
                if (bool.TryParse(setting.Value, out var soloTrainingMode))
                {
                    scenario.SoloTrainingMode = soloTrainingMode;
                }
                break;
            case "SoloParkingInitialCallupRatePercent":
                if (int.TryParse(setting.Value, out var parkingRate))
                {
                    ApplySoloPacingRates(
                        parkingRate,
                        scenario.SoloArrivalGeneratorRatePercent,
                        scenario.SoloGoAroundProbabilityPercent,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "SoloArrivalGeneratorRatePercent":
                if (int.TryParse(setting.Value, out var arrivalRate))
                {
                    ApplySoloPacingRates(
                        scenario.SoloParkingInitialCallupRatePercent,
                        arrivalRate,
                        scenario.SoloGoAroundProbabilityPercent,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "SoloGoAroundProbabilityPercent":
                if (int.TryParse(setting.Value, out var goAroundPct))
                {
                    ApplySoloPacingRates(
                        scenario.SoloParkingInitialCallupRatePercent,
                        scenario.SoloArrivalGeneratorRatePercent,
                        goAroundPct,
                        rescheduleFromNow: setting.ElapsedSeconds > 0
                    );
                }
                break;
            case "RpoShowPilotSpeech":
                if (bool.TryParse(setting.Value, out var rpoShowPilotSpeech))
                {
                    scenario.RpoShowPilotSpeech = rpoShowPilotSpeech;
                }
                break;
        }
    }

    /// <summary>
    /// Replaces every generator on the live scenario from a <see cref="GeneratorsPayload"/> JSON document.
    /// A generator whose id survives the edit keeps its spawn cadence and activation, so toggling one row
    /// does not re-phase the rest of the traffic; a newly added generator starts one interval from now.
    /// Already-spawned aircraft keep flying. The swap is best-effort per generator: entries with
    /// unresolvable runways are dropped and reported in the returned warnings.
    /// </summary>
    public List<string> ApplyGeneratorsJson(string generatorsJson)
    {
        var warnings = new List<string>();
        var scenario = Scenario;
        if (scenario is null)
        {
            warnings.Add("No active scenario");
            return warnings;
        }

        GeneratorsPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GeneratorsPayload>(generatorsJson);
        }
        catch (JsonException ex)
        {
            warnings.Add($"Invalid generators JSON: {ex.Message}");
            return warnings;
        }

        if (payload is null)
        {
            warnings.Add("Generators JSON deserialized to null");
            return warnings;
        }

        var priorCadence = scenario
            .Generators.Cast<IGeneratorRuntimeState>()
            .Concat(scenario.VfrArrivalGenerators)
            .Concat(scenario.OverflightGenerators)
            .GroupBy(g => g.ConfigBase.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.First().NextSpawnSeconds, g.First().WasActive), StringComparer.Ordinal);

        var navDb = NavigationDatabase.Instance;
        var airportId = scenario.PrimaryAirportId ?? "";

        var newArrivals = new List<GeneratorState>();
        foreach (var cfg in payload.AircraftGenerators)
        {
            var runwayId = cfg.Runway ?? "";
            var runway = navDb.GetRunway(airportId, runwayId);
            if (runway is null)
            {
                warnings.Add($"Generator '{cfg.Id}': runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} not found at {airportId}");
                continue;
            }

            var (next, wasActive) = ResumeCadence(cfg, scaledByArrivalRate: true);
            newArrivals.Add(
                new GeneratorState
                {
                    Config = cfg,
                    Runway = runway,
                    NextSpawnSeconds = next,
                    WasActive = wasActive,
                }
            );
        }

        var newVfrArrivals = new List<VfrArrivalGeneratorState>();
        foreach (var cfg in payload.VfrArrivalGenerators)
        {
            var (next, wasActive) = ResumeCadence(cfg, scaledByArrivalRate: true);
            newVfrArrivals.Add(
                new VfrArrivalGeneratorState
                {
                    Config = cfg,
                    NextSpawnSeconds = next,
                    WasActive = wasActive,
                }
            );
        }

        var newOverflights = new List<OverflightGeneratorState>();
        foreach (var cfg in payload.OverflightGenerators)
        {
            var (next, wasActive) = ResumeCadence(cfg, scaledByArrivalRate: false);
            newOverflights.Add(
                new OverflightGeneratorState
                {
                    Config = cfg,
                    NextSpawnSeconds = next,
                    WasActive = wasActive,
                }
            );
        }

        scenario.Generators.Clear();
        scenario.Generators.AddRange(newArrivals);
        scenario.VfrArrivalGenerators.Clear();
        scenario.VfrArrivalGenerators.AddRange(newVfrArrivals);
        scenario.OverflightGenerators.Clear();
        scenario.OverflightGenerators.AddRange(newOverflights);
        return warnings;

        (double NextSpawnSeconds, bool WasActive) ResumeCadence(IGeneratorConfig cfg, bool scaledByArrivalRate)
        {
            if (priorCadence.TryGetValue(cfg.Id, out var prior))
            {
                return prior;
            }

            var interval = scaledByArrivalRate
                ? ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(cfg.IntervalTime, scenario.SoloArrivalGeneratorRatePercent)
                : cfg.IntervalTime;
            return (scenario.ElapsedSeconds + interval, false);
        }
    }

    internal void ApplyWeatherJson(string weatherJson)
    {
        var parseResult = WeatherTimelineParser.Parse(weatherJson);
        if (parseResult.IsTimeline)
        {
            if (Scenario is not null)
            {
                Scenario.WeatherTimeline = parseResult.Timeline;
                Scenario.WeatherSourceJson = weatherJson;
            }
            World.Weather = parseResult.Timeline!.GetWeatherAt(Scenario?.ElapsedSeconds ?? 0);
        }
        else if (parseResult.IsProfile)
        {
            if (Scenario is not null)
            {
                Scenario.WeatherTimeline = null;
                Scenario.WeatherSourceJson = weatherJson;
            }
            World.Weather = parseResult.Profile;
        }
    }

    private static void ReplayDeleteQueued(AircraftState aircraft, int? blockNumber)
    {
        // Mirror the live DELAT/DELCOND handler exactly (queue blocks + deferred dispatches)
        // so replay reproduces deletions deterministically.
        ConditionalList.Delete(aircraft, blockNumber);
    }
}
