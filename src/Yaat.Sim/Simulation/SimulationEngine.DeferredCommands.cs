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

// Commands held until a trigger fires: deferred dispatch and triggered track blocks.
public sealed partial class SimulationEngine
{
    private void ProcessDeferredDispatches(double deltaSeconds)
    {
        foreach (var aircraft in World.GetSnapshot())
        {
            if (aircraft.DeferredDispatches.Count == 0)
            {
                continue;
            }

            // Tick timers / evaluate conditions in insertion order and collect the deferrals that are
            // ready this sub-tick. Dispatching FIFO (rather than the old reverse walk) guarantees that
            // several commands expiring on the same sub-tick — e.g. two reaction-delayed commands the
            // order-preserving clamp parked on the same fire time — apply in the order they were issued.
            List<DeferredDispatch>? ready = null;
            foreach (var d in aircraft.DeferredDispatches)
            {
                bool isReady;
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
                    d.RemainingSeconds -= deltaSeconds;
                    isReady = d.RemainingSeconds <= 0;
                }

                if (isReady)
                {
                    (ready ??= []).Add(d);
                }
            }

            if (ready is null)
            {
                continue;
            }

            foreach (var d in ready)
            {
                aircraft.DeferredDispatches.Remove(d);
            }

            // DispatchCompound clears DeferredDispatches to supersede pending waits when a NEW command
            // is issued; a deferred RE-dispatch must not cancel its still-pending siblings (e.g. a second
            // reaction-delayed command waiting its turn). Detach the survivors across the dispatch and
            // restore them ahead of any deferral a payload itself adds, preserving issue order.
            var survivingDeferrals = new List<DeferredDispatch>(aircraft.DeferredDispatches);
            aircraft.DeferredDispatches.Clear();

            foreach (var d in ready)
            {
                // Reaction delays (the command-run delay) fire silently — the controller already saw
                // the "complying in Ns" acknowledgement when the command was issued. WAIT/BEHIND/distance
                // deferrals were explicitly requested, so they still announce themselves.
                if (!d.IsReactionDelay)
                {
                    var payloadDesc = DescribeDeferredPayload(d);
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

                var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
                var deferredCtx = new DispatchContext(
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
                    PreserveConditionals: true,
                    IsScenarioScripted: d.IsScenarioScripted
                );
                var deferredResult = CommandDispatcher.DispatchCompound(d.Payload, aircraft, deferredCtx);
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

        var commands = compound.Blocks[0].Commands;
        if (commands.Count == 0 || !commands.TrueForAll(TrackEngine.IsTrackCommand))
        {
            return false;
        }

        var scenario = Scenario!;
        foreach (var command in commands)
        {
            var result = TrackEngine.Dispatch(command, aircraft, identity: null, scenario);
            if (result is { Success: false })
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
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        foreach (var aircraft in World.GetSnapshot())
        {
            var blocks = aircraft.Queue.Blocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (!block.IsApplied || !block.HasTrackCommand || block.TrackApplied)
                {
                    continue;
                }

                // Mark before dispatching so the scan never re-fires this block, even if dispatch throws.
                block.TrackApplied = true;

                foreach (var trackCommand in ResolveTrackCommandsForBlock(block, aircraft))
                {
                    var result = TrackEngine.Dispatch(trackCommand, aircraft, identity: null, scenario);
                    if (result is { Success: false })
                    {
                        // Abort the chain remainder — the follow-on blocks were premised on this
                        // track command succeeding (e.g. "AT FIXIE HO 2B; FH 090" must not fly the
                        // heading after a failed handoff). Same contract as FlightPhysics.ApplyBlock.
                        var discarded = aircraft.Queue.DiscardChainRemainder(block);
                        var label = !string.IsNullOrEmpty(block.SourceCommandText) ? block.SourceCommandText : block.NaturalDescription;
                        var warning = $"{aircraft.Callsign} {label}: {result.Message}";
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
            return live.Where(TrackEngine.IsTrackCommand).ToList();
        }

        if (string.IsNullOrEmpty(block.SourceCommandText))
        {
            return [];
        }

        var reparsed = CommandParser.ParseCompound(block.SourceCommandText, aircraft.FlightPlan.Route);
        if (!reparsed.IsSuccess || reparsed.Value is not { } compound)
        {
            return [];
        }

        var trackBlocks = compound.Blocks.Where(b => b.Commands.Exists(TrackEngine.IsTrackCommand)).ToList();
        if (trackBlocks.Count == 1)
        {
            return trackBlocks[0].Commands.Where(TrackEngine.IsTrackCommand).ToList();
        }

        // Multiple sub-blocks share this source text — disambiguate by the block's at-fix trigger.
        if (block.Trigger is { Type: BlockTriggerType.ReachFix, FixName: { } fixName })
        {
            var match = trackBlocks.Find(b =>
                b.Condition is AtFixCondition at && string.Equals(at.FixName, fixName, StringComparison.OrdinalIgnoreCase)
            );
            if (match is not null)
            {
                return match.Commands.Where(TrackEngine.IsTrackCommand).ToList();
            }
        }

        _logger.LogDebug(
            "[TrackBlock] {Callsign}: could not disambiguate restored track block from source '{Source}'",
            aircraft.Callsign,
            block.SourceCommandText
        );
        return [];
    }

    private static string DescribeDeferredPayload(DeferredDispatch d)
    {
        var parts = new List<string>();
        foreach (var block in d.Payload.Blocks)
        {
            var cmds = string.Join(", ", block.Commands.Select(CommandDescriber.DescribeNatural));
            parts.Add(cmds);
        }

        return string.Join("; then ", parts);
    }

    private bool IsGiveWayDeferredMet(AircraftState aircraft, string targetCallsign)
    {
        var target = FindAircraft(targetCallsign);
        if (target is null || !target.IsOnGround)
        {
            return true; // Target gone or airborne — no conflict
        }

        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = targetCallsign };
        return FlightPhysics.IsGiveWayMet(aircraft, trigger, FindAircraft);
    }
}
