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
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Commands held until a trigger fires: deferred dispatch and triggered track blocks.
public sealed partial class SimulationEngine
{
    private void ProcessDeferredDispatches(double deltaSeconds)
    {
        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            if (aircraft.DeferredDispatches.Count == 0)
            {
                continue;
            }

            // A scenario-scripted deferral that would end the running tug move waits for the push to
            // finish (see EndsPushback). Captured once, because nothing in this scan starts, advances
            // or clears a phase — the payloads dispatch only after the scan.
            var activePushback = aircraft.Phases?.CurrentPhase as PushbackPhase;

            // Tick timers / evaluate conditions in insertion order and collect the deferrals that are ready
            // this sub-tick. They dispatch in the order their timers expired, with this insertion order as the
            // tiebreak (see the release ordering below), so several commands expiring on the same sub-tick —
            // e.g. two reaction-delayed commands the order-preserving clamp parked on the same fire time —
            // apply in the order they were issued.
            var readyNow = new List<(DeferredDispatch Dispatch, bool JustExpired)>();
            foreach (DeferredDispatch d in aircraft.DeferredDispatches)
            {
                bool isReady;
                bool justExpired = false;
                if (d.GiveWayTarget is not null)
                {
                    isReady = IsGiveWayDeferredMet(aircraft, d.GiveWayTarget);
                    if (isReady && aircraft.Ground.Hold is { Kind: HoldKind.GiveWay })
                    {
                        // Condition met — clear any active GIVEWAY hold so the payload can dispatch
                        // cleanly. HoldPosition holds are NOT cleared (a controller's explicit HOLD
                        // should not be overridden by a deferred BEHIND condition firing).
                        aircraft.Ground.Hold = null;
                    }
                }
                else if (d.IsDistanceBased)
                {
                    d.RemainingDistanceNm -= aircraft.GroundSpeed * deltaSeconds / 3600.0;
                    isReady = d.RemainingDistanceNm <= 0;
                }
                else
                {
                    // Left to run negative: the countdown of an expired command kept waiting is what says
                    // when its timer expired relative to the others (see the release ordering below), and it
                    // is what the snapshot carries, so a restore keeps the order. The conditional list clamps
                    // it for display.
                    double before = d.RemainingSeconds;
                    d.RemainingSeconds = before - deltaSeconds;
                    isReady = d.RemainingSeconds <= 0;
                    justExpired = before > 0;
                }

                if (isReady)
                {
                    readyNow.Add((d, justExpired));
                }
            }

            // Scripted means the scenario wrote the WAIT, not the instructor: a scenario's own taxi
            // clearance must not take the tug off mid-push (the aircraft would start taxiing tail-first
            // from the middle of the alley). It fires on the first sub-tick whose current phase is no
            // longer a pushback — completed, aborted or cancelled. Two rules hold the rest:
            // (1) a scripted command that would end the tow is held, whatever else is pending;
            // (2) once one is held, every other scripted command on the aircraft that the pushback would
            //     refuse is held too, wherever it sits in the list — a refused command dispatched mid-tow
            //     is lost, and the deferral list is not in time order (time-offset presets are queued and
            //     fired in reverse), so the refused command may well sit ahead of the one holding the queue.
            // A lone scripted refusal, with nothing held ahead of it, keeps its own timing.
            bool holdsPushback =
                (activePushback is not null)
                && readyNow.Exists(entry => entry.Dispatch.IsScenarioScripted && EndsPushback(activePushback, entry.Dispatch));

            List<DeferredDispatch>? ready = null;
            foreach ((DeferredDispatch d, bool justExpired) in readyNow)
            {
                bool waitsForPushback =
                    (activePushback is not null)
                    && d.IsScenarioScripted
                    && (EndsPushback(activePushback, d) || (holdsPushback && PushbackRejects(activePushback, d)));
                if (waitsForPushback)
                {
                    if (justExpired)
                    {
                        string command = DescribeDeferredPayload(d);
                        _logger.LogDebug("[Deferred] {Callsign}: {Command} held until pushback completes", aircraft.Callsign, command);
                        EmitTerminal("System", aircraft.Callsign, $"{command} held until pushback completes");
                    }

                    continue;
                }

                (ready ??= []).Add(d);
            }

            if (ready is null)
            {
                continue;
            }

            // Release in the order the timers expired: the countdown runs negative while a command waits, so
            // ascending RemainingSeconds is ascending expiry time. OrderBy is stable, so two commands that
            // expired on the same sub-tick keep their insertion order.
            if (ready.Count > 1)
            {
                ready = [.. ready.OrderBy(d => d.RemainingSeconds)];
            }

            foreach (DeferredDispatch d in ready)
            {
                aircraft.DeferredDispatches.Remove(d);
            }

            // DispatchCompound clears DeferredDispatches to supersede pending waits when a NEW command
            // is issued; a deferred RE-dispatch must not cancel its still-pending siblings (e.g. a second
            // reaction-delayed command waiting its turn). Detach the survivors across the dispatch and
            // restore them ahead of any deferral a payload itself adds, preserving issue order.
            var survivingDeferrals = new List<DeferredDispatch>(aircraft.DeferredDispatches);
            aircraft.DeferredDispatches.Clear();

            foreach (DeferredDispatch d in ready)
            {
                // Reaction delays (the command-run delay) fire silently — the controller already saw
                // the "complying in Ns" acknowledgement when the command was issued. WAIT/BEHIND/distance
                // deferrals were explicitly requested, so they still announce themselves.
                if (!d.IsReactionDelay)
                {
                    string payloadDesc = DescribeDeferredPayload(d);
                    string conditionDesc;
                    if (d.GiveWayTarget is not null)
                    {
                        conditionDesc = $"Give-way cleared ({d.GiveWayTarget})";
                    }
                    else if (d.IsDistanceBased)
                    {
                        conditionDesc = "Distance reached";
                    }
                    else
                    {
                        conditionDesc = "WAIT expired";
                    }

                    _logger.LogInformation("[Deferred] {Callsign}: {Condition} → {Payload}", aircraft.Callsign, conditionDesc, payloadDesc);
                    EmitTerminal("System", aircraft.Callsign, $"[Deferred] {conditionDesc} → {payloadDesc}");
                }

                // A pure-track deferred payload (e.g. WAIT 5 SP1 …) has no ApplyCommand arm; route it to
                // the track engine directly, mirroring DispatchSinglePreset. Strip payloads stay on
                // DispatchCompound below — the ApplyCommand strip arm queues them for the host to apply.
                if (TryDispatchImmediateTrackPreset(d.Payload, aircraft))
                {
                    continue;
                }

                AirportGroundLayout? groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
                var deferredCtx = new DispatchContext
                {
                    GroundLayout = groundLayout,
                    Rng = World.Rng,
                    Weather = World.Weather,
                    FindAircraft = FindAircraft,
                    ListAircraft = () => World.GetSnapshot(),
                    ValidateDctFixes = Scenario?.ValidateDctFixes ?? true,
                    AutoCrossRunway = Scenario?.AutoCrossRunway ?? false,
                    SoloTrainingMode = Scenario?.SoloTrainingMode ?? false,
                    RpoShowPilotSpeech = Scenario?.RpoShowPilotSpeech ?? false,
                    TerminalEmitter = AddTerminalEntry,
                    ArtccConfig = Scenario?.ArtccConfig,
                    ScenarioElapsedSeconds = Scenario?.ElapsedSeconds ?? 0,
                    SessionStartUtc = Scenario?.SessionStartUtc ?? SimScenarioState.ProcessDayUtc,
                    PreserveConditionals = true,
                    IsScenarioScripted = d.IsScenarioScripted,
                    FacilityHint = Scenario?.StudentPosition?.FacilityId,
                };
                CommandResult deferredResult = CommandDispatcher.DispatchCompound(d.Payload, aircraft, deferredCtx);
                if (!deferredResult.Success)
                {
                    // A deferred/preset command that fails when it finally fires (e.g. a DVIA whose STAR
                    // never activated) used to vanish silently after the optimistic line above — surface it.
                    _logger.LogWarning("[Deferred] {Callsign}: dispatch failed — {Message}", aircraft.Callsign, deferredResult.Message);
                    EmitTerminal("Warning", aircraft.Callsign, $"[Deferred] could not apply: {deferredResult.Message}");
                }
            }

            if (survivingDeferrals.Count > 0)
            {
                aircraft.DeferredDispatches.InsertRange(0, survivingDeferrals);
            }
        }
    }

    /// <summary>
    /// Routes an immediate (unconditional) preset that is purely track commands straight to the track
    /// engine. Such presets never reach <see cref="CommandDispatcher.EnqueueBlocks"/> — the leading block
    /// applies inline through <see cref="CommandDispatcher.ApplyCommand"/>, which has no track-command arm
    /// (the no-dispatcher-arm default). Conditional or mixed compounds return false and fall
    /// through to the normal dispatcher, where <see cref="ProcessTriggeredTrackBlocks"/> handles any
    /// triggered track commands.
    /// </summary>
    private bool TryDispatchImmediateTrackPreset(CompoundCommand compound, AircraftState aircraft)
    {
        if (compound.Blocks.Count != 1 || compound.Blocks[0].Condition is not null)
        {
            return false;
        }

        List<ParsedCommand> commands = compound.Blocks[0].Commands;
        if (commands.Count == 0 || !commands.TrueForAll(TrackEngine.IsTrackCommand))
        {
            return false;
        }

        SimScenarioState scenario = Scenario!;
        var track = new TrackDispatchContext(Identity: null, scenario, Redirect: null, ConflictAlerts);
        foreach (ParsedCommand command in commands)
        {
            // A verb the track table has no arm for returns null; treated as a refusal so it warns instead of
            // silently applying nothing.
            CommandResult result = TrackEngine.Dispatch(command, aircraft, track) ?? ActionRefusals.HostOnly(command);
            if (!result.Success)
            {
                aircraft.PendingWarnings.Add($"{aircraft.Callsign}: {result.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// Dispatches track commands (HO/TRACK/DROP/…) carried by triggered command-queue blocks. Track
    /// commands have no arm in <see cref="CommandDispatcher.ApplyCommand"/>; they must reach
    /// <see cref="TrackEngine.Dispatch"/>, which needs the live <see cref="Scenario"/> and ARTCC config.
    /// Runs inside <see cref="TickPhysics"/> (shared by the standalone sim/replay and the server tick) so
    /// the routing fires regardless of host. The block's own <c>ApplyAction</c> deliberately omits track
    /// commands (see <see cref="CommandDispatcher.EnqueueBlocks"/>), so this is the single place they
    /// execute. <see cref="CommandBlock.TrackApplied"/> guards against the per-sub-tick scan re-firing,
    /// and survives snapshot restore.
    /// </summary>
    public void ProcessTriggeredTrackBlocks()
    {
        SimScenarioState? scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        var track = new TrackDispatchContext(Identity: null, scenario, Redirect: null, ConflictAlerts);
        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            List<CommandBlock> blocks = aircraft.Queue.Blocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                CommandBlock block = blocks[i];
                if (!block.IsApplied || !block.HasTrackCommand || block.TrackApplied)
                {
                    continue;
                }

                // Mark before dispatching so the scan never re-fires this block, even if dispatch throws.
                block.TrackApplied = true;

                foreach (ParsedCommand trackCommand in ResolveTrackCommandsForBlock(block, aircraft))
                {
                    // Null is "no arm in the track table" — a refusal like any other, never a silent success.
                    CommandResult result = TrackEngine.Dispatch(trackCommand, aircraft, track) ?? ActionRefusals.HostOnly(trackCommand);
                    if (!result.Success)
                    {
                        // Abort the chain remainder — the follow-on blocks were premised on this
                        // track command succeeding (e.g. "AT FIXIE HO 2B; FH 090" must not fly the
                        // heading after a failed handoff). Same contract as FlightPhysics.ApplyBlock.
                        List<string> discarded = aircraft.Queue.DiscardChainRemainder(block);
                        string label = !string.IsNullOrEmpty(block.SourceCommandText) ? block.SourceCommandText : block.NaturalDescription;
                        string warning = $"{aircraft.Callsign} {label}: {result.Message}";
                        if (discarded.Count > 0)
                        {
                            warning += $" — rest of transmission discarded: {string.Join("; ", discarded)}";
                        }
                        aircraft.PendingWarnings.Add(warning);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves the parsed track commands for a triggered block. Prefers the live
    /// <see cref="CommandBlock.ParsedCommands"/>; when those are absent (the block was restored from a
    /// snapshot, which does not serialize parsed commands) it re-parses
    /// <see cref="CommandBlock.SourceCommandText"/> and recovers the track commands from the matching
    /// sub-block.
    /// </summary>
    private List<ParsedCommand> ResolveTrackCommandsForBlock(CommandBlock block, AircraftState aircraft)
    {
        if (block.ParsedCommands is { } live)
        {
            return [.. live.Where(TrackEngine.IsTrackCommand)];
        }

        if (string.IsNullOrEmpty(block.SourceCommandText))
        {
            return [];
        }

        ParseResult<CompoundCommand> reparsed = CommandParser.ParseCompound(block.SourceCommandText, aircraft.FlightPlan.Route);
        if (!reparsed.IsSuccess || reparsed.Value is not { } compound)
        {
            return [];
        }

        var trackBlocks = compound.Blocks.Where(b => b.Commands.Exists(TrackEngine.IsTrackCommand)).ToList();
        if (trackBlocks.Count == 1)
        {
            return [.. trackBlocks[0].Commands.Where(TrackEngine.IsTrackCommand)];
        }

        // Multiple sub-blocks share this source text — disambiguate by the block's at-fix trigger.
        if (block.Trigger is { Type: BlockTriggerType.ReachFix, FixName: { } fixName })
        {
            ParsedBlock? match = trackBlocks.Find(b =>
                b.Condition is AtFixCondition at && string.Equals(at.FixName, fixName, StringComparison.OrdinalIgnoreCase)
            );
            if (match is not null)
            {
                return [.. match.Commands.Where(TrackEngine.IsTrackCommand)];
            }
        }

        _logger.LogDebug(
            "[TrackBlock] {Callsign}: could not disambiguate restored track block from source '{Source}'",
            aircraft.Callsign,
            block.SourceCommandText
        );
        return [];
    }

    /// <summary>
    /// Whether a deferred payload would end the running tug move, judged by the classification the phase
    /// itself applies to an incoming command (<see cref="PushbackPhase.CanAcceptCommand"/>): a TAXI /
    /// TAXIAUTO / AIRTAXI / LAND / DEL / PUSHM clears the phase and ends the tow, an ALLOWED verb
    /// (HOLD / RES / a redirecting PUSH) or a REJECTED one (a squawk, a strip op) leaves it running.
    /// Never hard-code the clearing verbs — the phase owns that list.
    ///
    /// <para>Any command of the payload counts, whichever block it sits in: a chain led by a
    /// transparent block peels that block off and re-dispatches the remainder through the phase gate,
    /// so the clearing verb reaches the phase even when it is not the first command.</para>
    /// </summary>
    private static bool EndsPushback(PushbackPhase pushback, DeferredDispatch d) =>
        CommandsOf(d).Any(cmd => AcceptanceOf(pushback, cmd)?.ClearsThePhase == true);

    /// <summary>
    /// Whether the pushback would refuse a deferred payload: it carries a command that reaches the phase gate
    /// (<see cref="CommandDispatcher.ReachesPhaseGate"/>) and the gate answers REJECTED there. Dispatching one
    /// mid-tow loses it — the dispatcher surfaces the refusal and the command is gone (an <c>HS</c> at that
    /// moment has no taxi route to bind to).
    /// </summary>
    private static bool PushbackRejects(PushbackPhase pushback, DeferredDispatch d) =>
        CommandsOf(d).Any(cmd => CommandDispatcher.ReachesPhaseGate(cmd) && (AcceptanceOf(pushback, cmd)?.IsRejected == true));

    /// <summary>Every command of a deferred payload, in block order.</summary>
    private static IEnumerable<ParsedCommand> CommandsOf(DeferredDispatch d) => d.Payload.Blocks.SelectMany(block => block.Commands);

    /// <summary>
    /// The phase's verdict on one command, or null for a verb that never reaches the gate — an unsupported
    /// command, which <see cref="CommandDescriber.ToCanonicalType"/> throws on rather than mapping.
    /// </summary>
    private static CommandAcceptance? AcceptanceOf(PushbackPhase pushback, ParsedCommand cmd) =>
        cmd is UnsupportedCommand ? null : pushback.CanAcceptCommand(CommandDescriber.ToCanonicalType(cmd));

    private static string DescribeDeferredPayload(DeferredDispatch d)
    {
        var parts = new List<string>();
        foreach (ParsedBlock block in d.Payload.Blocks)
        {
            string cmds = string.Join(", ", block.Commands.Select(CommandDescriber.DescribeNatural));
            parts.Add(cmds);
        }

        return string.Join("; then ", parts);
    }

    private bool IsGiveWayDeferredMet(AircraftState aircraft, string targetCallsign)
    {
        AircraftState? target = FindAircraft(targetCallsign);
        if (target is null || !target.IsOnGround)
        {
            return true; // Target gone or airborne — no conflict
        }

        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = targetCallsign };
        return FlightPhysics.IsGiveWayMet(aircraft, trigger, FindAircraft);
    }
}
