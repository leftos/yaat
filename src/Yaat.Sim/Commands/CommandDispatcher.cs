using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Result of dispatching a command. <see cref="Advisory"/> carries an optional instructor-facing
/// terminal note emitted alongside the command (e.g. a procedure resolved from a retired AIRAC cycle);
/// it is surfaced via <see cref="DispatchContext.TerminalEmitter"/>, not spoken as pilot phraseology.
/// <see cref="NoDispatcherArm"/> marks the dispatcher fallback (a command that reached
/// <c>ApplyCommand</c> with no handler arm in its current context) so callers can branch on the case
/// without parsing the user-facing <see cref="Message"/>.
/// </summary>
/// <summary>
/// Outcome of a dispatch. <paramref name="EffectiveCommand"/> is set when a handler applied a rewritten
/// form of the command — a TAXI whose unreachable gate lead-out lane was dropped — so the solo pilot
/// reads back the route it will actually taxi (<see cref="Pilot.PilotResponder.BuildReadbackAsApplied"/>)
/// instead of the pavement the controller named; null when the command applied as issued.
/// </summary>
public record CommandResult(
    bool Success,
    string? Message = null,
    CanonicalCommandType? RejectedCommandType = null,
    string? Advisory = null,
    bool NoDispatcherArm = false,
    ParsedCommand? EffectiveCommand = null
);

public static class CommandDispatcher
{
    /// <summary>
    /// Sentinel returned by DispatchWithPhase to signal that phases should be cleared,
    /// but only AFTER validation succeeds. This avoids mutating PhaseList before we know
    /// the command is valid (the old approach saved a reference to the PhaseList, but
    /// Clear() mutated it in place, making restore impossible).
    /// </summary>
    private static readonly CommandResult PhaseShouldBeCleared = new(true, "__CLEAR_PHASES__");

    private static readonly ILogger Log = SimLog.CreateLogger("CommandDispatcher");

    public static CommandResult DispatchCompound(CompoundCommand compound, AircraftState aircraft, DispatchContext ctx)
    {
        if (compound.Blocks is [{ Condition: null, Commands: [AssumeCommand] }])
        {
            return LiveTraffic.LiveTrafficAssumer.Assume(aircraft, ctx);
        }

        if (aircraft.IsShadow)
        {
            return RejectShadow(aircraft);
        }

        // A successful command issued to a ground aircraft by the controller is itself evidence of
        // established controller-pilot contact (the pilot read back the clearance the controller
        // spoke). Setting this here covers every controller dispatch path — user-typed
        // (RoomEngine.SendCommandAsync) and replay (RecordingManager) — without each call site
        // re-implementing the gate. Suppresses the spurious post-takeoff airborne check-in for
        // departures the controller cleared during taxi.
        //
        // Scenario-scripted dispatch (a preset, or the automated tower's auto-CTO on
        // hold-for-release) is NOT the student establishing contact, so it must not set this — a
        // runway-spawn CTO-preset departure handed to the student via auto-track still makes its
        // post-takeoff check-in.
        var wasOnGround = aircraft.IsOnGround;
        var result = DispatchCompoundCore(compound, aircraft, ctx);
        if (result.Success && wasOnGround && !ctx.IsScenarioScripted)
        {
            aircraft.HasMadeInitialContact = true;
        }
        return result;
    }

    /// <summary>
    /// Live traffic is not controllable: the real pilot is flying it. Gated at the public entries so
    /// phase-transparent commands (SQ, ident, RTIS) cannot slip through either.
    /// </summary>
    private static CommandResult RejectShadow(AircraftState aircraft) =>
        new(false, $"ASSUME {aircraft.Callsign} first — live traffic is not controllable");

    private static CommandResult DispatchCompoundCore(CompoundCommand compound, AircraftState aircraft, DispatchContext ctx)
    {
        // Leading WAIT → deferred dispatch: extract the timer and store the remaining
        // blocks as a deferred payload. The payload dispatches fresh when the timer expires,
        // without touching phases or the command queue.
        var deferredResult = TryDeferLeadingWait(compound, aircraft, ctx);
        if (deferredResult is not null)
        {
            return deferredResult;
        }

        // GiveWay condition → deferred dispatch: the aircraft stays in its current phase
        // and the payload dispatches when the target aircraft passes.
        var gwResult = TryDeferGiveWay(compound, aircraft, ctx);
        if (gwResult is not null)
        {
            return gwResult;
        }

        // Phase-transparent commands (squawk, ident, say, RFIS/RTIS, etc.) — apply
        // directly without consulting phases, clearing the queue, or clearing
        // deferred dispatches. Fires regardless of phase state: the same `None`-
        // dimension fast path in ClearConflictingBlocks that wipes the queue when
        // phases are active also wipes it when phases are null, so the protection
        // must apply both ways. Without the unconditional check, transparent
        // commands like RTIS would wipe a queued pattern entry on an aircraft
        // that hadn't yet transitioned into a phase (see N435C in S2-OAK-5).
        if (IsAllTransparent(compound))
        {
            return ApplyTransparentCompound(compound, aircraft, ctx);
        }

        // Pattern modifiers (EXT / SA / MNA) on an aircraft with no active phase must apply directly,
        // without the All/None-dimension ClearConflictingBlocks fast path — which would otherwise wipe
        // a queued pattern entry (e.g. an ERD sitting behind DCT VPCOL) the moment the modifiers'
        // dispatcher arms make dry-run succeed. ApplyCommand pre-arms the queued entry so each modifier
        // fires when it builds its circuit. This covers both a lone modifier and a compound of only
        // modifiers (e.g. "EXT DOWNWIND; SA"): a multi-block modifier compound skips the single-block
        // form, dry-run validates only its first block, and the fast-path wipe then destroys the queued
        // entry — so the command fails ("no upcoming downwind leg to extend") having already silently
        // dropped it. The last-armed modifier wins (single PendingEntryModifier slot). With an active
        // phase the phase-gate path below already applies these in place without a queue wipe.
        if (aircraft.Phases?.CurrentPhase is null && compound.Blocks.Count > 0 && compound.Blocks.All(IsImmediatePhaseModifierBlock))
        {
            return ApplyTransparentCompound(compound, aircraft, ctx);
        }

        // Same queue-wipe footgun, for a landing/option clearance pre-issued behind a still-queued
        // pattern entry (CLAND while ERD 28R sits behind DCT VPCOL). These verbs carry CommandDimension
        // .All, so the fast path would drop the entry the clearance is meant to attach to. Scoped hard:
        // only with NO PhaseList at all, and only when an entry is actually queued — a CLAND with
        // nothing queued keeps the ordinary dry-run-guarded path and its ordinary rejection.
        if (
            aircraft.Phases is null
            && compound.Blocks.Count > 0
            && compound.Blocks.All(IsPendingLandingClearanceBlock)
            && PatternCommandHandler.HasQueuedPatternEntry(aircraft)
        )
        {
            return ApplyTransparentCompound(compound, aircraft, ctx);
        }

        // CAPP on an aircraft already established on a JFAC/JLOC lateral join authorizes the
        // glideslope descent in place — it does not tear the join down and rebuild it (which
        // would emit a spurious "… cancelled by CAPP" warning). The aircraft is already tracking
        // the localizer; CAPP just clears it to descend.
        var lateralUpgrade = TryUpgradeLateralJoinInPlace(compound, aircraft);
        if (lateralUpgrade is not null)
        {
            return lateralUpgrade;
        }

        // Capture the active phase before dispatch so post-clear logic (e.g.
        // auto-attaching the AfterRunwayCrossing trigger when CROSS clears a
        // runway hold-short) can inspect it after the phase has been cleared.
        var currentPhaseBeforeDispatch = aircraft.Phases?.CurrentPhase;

        // Same reason, for the taxiing aircraft that CROSS pre-clears ahead of: the route already
        // carries the clearance by the time the trigger is attached, so we need the before-picture to
        // tell "this CROSS armed the crossing" from "a crossing was already pending".
        bool hadPendingCrossingBeforeDispatch = HasPendingRunwayCrossing(aircraft, ctx);

        // Phase interaction: check if aircraft has active phases
        bool shouldClearPhases = false;
        if (aircraft.Phases?.CurrentPhase is { } currentPhase)
        {
            var result = DispatchWithPhase(compound, aircraft, currentPhase, ctx);
            if (ReferenceEquals(result, PhaseShouldBeCleared))
            {
                // Phases need clearing, but defer until after validation succeeds.
                // This prevents destroying phases on invalid commands.
                shouldClearPhases = true;
            }
            else if (result is not null)
            {
                // Tower command handled the first block. Enqueue remaining blocks
                // so they execute after phases complete (UpdateCommandQueue picks them
                // up once CurrentPhase becomes null) — except phase-modifier blocks
                // (SA / MNA), which we dispatch immediately via the tower path so
                // they actually arm the just-installed pattern. Otherwise they sit
                // in the queue forever (UpdateCommandQueue short-circuits while a
                // phase is active).
                if (result.Success && compound.Blocks.Count > 1)
                {
                    var phaseIncomingDims = CommandDescriber.GetCompoundDimensions(compound);
                    var phasePreserved = ClearConflictingBlocks(aircraft, phaseIncomingDims, ctx, ctx.PreserveConditionals, out var phaseDropped);
                    EmitQueueClearWarning(aircraft, phaseDropped, compound);
                    if (!ctx.PreserveConditionals)
                    {
                        aircraft.DeferredDispatches.Clear();
                    }

                    var modifierMessages = new List<string>();
                    var remainingBlocks = new List<ParsedBlock>();
                    for (int i = 1; i < compound.Blocks.Count; i++)
                    {
                        var pb = compound.Blocks[i];
                        if (IsImmediatePhaseModifierBlock(pb))
                        {
                            var modCmd = pb.Commands[0];
                            var modPhase = aircraft.Phases?.CurrentPhase ?? currentPhase;
                            var modResult = TryApplyTowerCommand(modCmd, aircraft, modPhase, ctx);
                            if (modResult is null || !modResult.Success)
                            {
                                // Couldn't apply right now — fall back to enqueueing.
                                remainingBlocks.Add(pb);
                                continue;
                            }

                            modifierMessages.Add(modResult.Message ?? CommandDescriber.DescribeNatural(modCmd));
                        }
                        else
                        {
                            remainingBlocks.Add(pb);
                        }
                    }

                    int firstRemainingIdx = aircraft.Queue.Blocks.Count;
                    var remainingMessages =
                        remainingBlocks.Count > 0
                            ? EnqueueBlocks(new CompoundCommand(remainingBlocks) { SourceText = compound.SourceText }, 0, aircraft, ctx)
                            : new List<string>();
                    AttachAfterRunwayCrossingTriggerForToweredFirstBlock(
                        compound,
                        aircraft,
                        firstRemainingIdx,
                        currentPhaseBeforeDispatch,
                        hadPendingCrossingBeforeDispatch,
                        ctx
                    );
                    aircraft.Queue.Blocks.AddRange(phasePreserved);

                    var combinedMessages = new List<string> { result.Message ?? "" };
                    combinedMessages.AddRange(modifierMessages);
                    combinedMessages.AddRange(remainingMessages);
                    if (combinedMessages.Count > 1)
                    {
                        var combined = string.Join(" ; then ", combinedMessages.Where(m => !string.IsNullOrEmpty(m)));
                        return result with { Message = combined };
                    }
                }

                return result;
            }
            // result is null means phase allowed the command, fall through to normal dispatch
        }

        // Dry-run: validate all commands on a snapshot clone before touching the
        // real aircraft. This allows compound commands like "ERD 28R, CLAND" where
        // a later command depends on state created by an earlier one.
        var dryRunError = DryRunValidate(compound, aircraft, ctx);
        if (dryRunError is not null)
        {
            return dryRunError;
        }

        // Dry-run only validates the first block, but a clearance trailing a pattern entry in the same
        // transmission is pre-issued against that entry — so its runway must agree. Check it here, before
        // anything is applied, rather than letting the leading blocks land and the trailing one fail.
        var trailingClearanceError = ValidateTrailingClearanceRunway(compound, aircraft);
        if (trailingClearanceError is not null)
        {
            return trailingClearanceError;
        }

        // Now that validation passed, clear phases if the command requires it
        if (shouldClearPhases)
        {
            var phaseCtx = BuildMinimalContext(aircraft);
            bool clearedGoAround = aircraft.Phases?.CurrentPhase is GoAroundPhase;
            string? clearedSummary = aircraft.Phases is { } pl ? PhaseClearSummary.Build(pl) : null;
            aircraft.Phases?.Clear(phaseCtx);
            aircraft.Phases = null;
            aircraft.Targets.TurnRateOverride = null;
            aircraft.Targets.HasExplicitTurnRate = false;
            aircraft.Targets.PreferredTurnDirection = null;
            AirborneFollowHelper.ClearFollowState(aircraft);
            ResumeAssignedAltitudeAfterPhaseClear(aircraft, clearedGoAround);

            if (clearedSummary is not null)
            {
                var src = compound.SourceText ?? CommandDescriber.DescribeNatural(compound.Blocks[0].Commands[0]);
                aircraft.PendingWarnings.Add($"{aircraft.Callsign} {clearedSummary} cancelled by {src}");
            }
        }

        // Conditional incoming commands are purely additive: append the triggered block
        // without disturbing existing queue blocks or pending deferred dispatches. A fresh
        // immediate command supersedes (dimension-aware clear + cancel pending WAITs); a
        // firing deferral (ctx.PreserveConditionals) supersedes conflicting *untriggered*
        // work but keeps triggered conditionals and other deferrals.
        bool conditionalIncoming = IsConditionalIncoming(compound);
        List<CommandBlock> preserved;
        if (conditionalIncoming)
        {
            preserved = [];
        }
        else
        {
            var incomingDims = CommandDescriber.GetCompoundDimensions(compound);
            preserved = ClearConflictingBlocks(aircraft, incomingDims, ctx, ctx.PreserveConditionals, out var dropped);
            EmitQueueClearWarning(aircraft, dropped, compound);
            if (!ctx.PreserveConditionals)
            {
                aircraft.DeferredDispatches.Clear();
            }
        }

        int firstNewBlockIdx = aircraft.Queue.Blocks.Count;
        var messages = EnqueueBlocks(compound, 0, aircraft, ctx);
        AttachAfterRunwayCrossingTrigger(compound, aircraft, firstNewBlockIdx, currentPhaseBeforeDispatch, hadPendingCrossingBeforeDispatch, ctx);
        aircraft.Queue.Blocks.AddRange(preserved);

        // Apply the first NEW block immediately (if no trigger).
        // After dimension-aware clearing, CurrentBlock may still point to an old applied block
        // (e.g. phases prevented the queue from advancing), so we target the first new block
        // by index rather than using CurrentBlock.
        if (firstNewBlockIdx < aircraft.Queue.Blocks.Count)
        {
            var firstNewBlock = aircraft.Queue.Blocks[firstNewBlockIdx];
            if (firstNewBlock.Trigger is null)
            {
                var applyResult = ApplyBlock(firstNewBlock, aircraft);
                if (!applyResult.Success)
                {
                    // First block failed — clear the queue and propagate the failure
                    aircraft.Queue.Blocks.Clear();
                    aircraft.Queue.CurrentBlockIndex = 0;
                    return applyResult;
                }

                // ApplyBlock may update NaturalDescription (e.g. implied CAPP resolving approach ID)
                if (messages.Count > 0)
                {
                    messages[0] = firstNewBlock.NaturalDescription;
                }

                // If the just-applied block installed pattern phases (e.g. ERD), any
                // subsequent SA/MNA/EXT/clearance blocks would otherwise sit in the queue
                // forever — UpdateCommandQueue short-circuits while a phase is active. Apply
                // them immediately via the tower path so they reach the pending leg.
                if (aircraft.Phases?.CurrentPhase is { } postApplyPhase)
                {
                    for (int bi = firstNewBlockIdx + 1; bi < aircraft.Queue.Blocks.Count; bi++)
                    {
                        var block = aircraft.Queue.Blocks[bi];
                        if (block.IsApplied || block.Trigger is not null || block.ParsedCommands is not { Count: 1 })
                        {
                            break;
                        }

                        var parsedCmd = block.ParsedCommands[0];
                        if (
                            parsedCmd is not (MakeShortApproachCommand or MakeNormalApproachCommand or ExtendPatternCommand)
                            && !IsPendingLandingClearanceCommand(parsedCmd)
                        )
                        {
                            break;
                        }

                        var modResult = TryApplyTowerCommand(parsedCmd, aircraft, postApplyPhase, ctx);
                        if (modResult is null || !modResult.Success)
                        {
                            break;
                        }

                        block.IsApplied = true;
                        if (bi - firstNewBlockIdx < messages.Count)
                        {
                            messages[bi - firstNewBlockIdx] = modResult.Message ?? block.NaturalDescription;
                        }
                    }
                }
                // A clearance in a transmission whose pattern entry is still QUEUED (e.g.
                // "DCT VPCOL; ERD 28R; CLAND") never gets its turn either: the entry installs a phase
                // the moment it fires, and the queue stops advancing from there. Pre-issue it against
                // the queued entry now. Unlike the loop above this steps over the intervening entry
                // block, which must stay queued to fire at its fix.
                else if (aircraft.Phases is null && PatternCommandHandler.HasQueuedPatternEntry(aircraft))
                {
                    for (int bi = firstNewBlockIdx + 1; bi < aircraft.Queue.Blocks.Count; bi++)
                    {
                        var block = aircraft.Queue.Blocks[bi];
                        if (
                            block.IsApplied
                            || block.Trigger is not null
                            || block.ParsedCommands is not { Count: 1 }
                            || !IsPendingLandingClearanceCommand(block.ParsedCommands[0])
                        )
                        {
                            continue;
                        }

                        var armResult = ApplyCommand(block.ParsedCommands[0], aircraft, ctx);
                        if (!armResult.Success)
                        {
                            break;
                        }

                        block.IsApplied = true;
                        if (bi - firstNewBlockIdx < messages.Count)
                        {
                            messages[bi - firstNewBlockIdx] = armResult.Message ?? block.NaturalDescription;
                        }
                    }
                }
            }
            // If there's a trigger, the physics tick will check and apply when met
        }

        var fullMessage = string.Join(" ; then ", messages);
        return new CommandResult(true, fullMessage);
    }

    /// <summary>
    /// True when the incoming compound leads with a precondition (AT / LV / ATFN / ONHO /
    /// ONHS / DistanceFinal / AtGroundEntity). Leading bare-WAIT and leading-BEHIND are
    /// already siphoned into deferred dispatches by <see cref="TryDeferLeadingWait"/> /
    /// <see cref="TryDeferGiveWay"/> before this is consulted, so a conditional incoming
    /// compound is one the controller (or a preset) wants to fire when its trigger is met.
    /// Such commands are purely additive — they never clear sibling conditionals or pending
    /// deferred dispatches; only a fresh immediate command supersedes pending work.
    /// </summary>
    private static bool IsConditionalIncoming(CompoundCommand compound) => compound.Blocks.Count > 0 && compound.Blocks[0].Condition is not null;

    /// <summary>
    /// CAPP issued to an aircraft already established on a JFAC/JLOC lateral join authorizes the
    /// glideslope descent on the SAME approach in place — flipping <c>LateralInterceptOnly</c> off
    /// rather than tearing the join down and rebuilding it (which would emit a spurious
    /// "… cancelled by CAPP" warning). Returns the clearance result when it handled the upgrade,
    /// or <c>null</c> to fall through to normal CAPP dispatch (forced CAPPF, a different approach,
    /// AT/DCT/maintain-altitude forms, or any aircraft not on a lateral join).
    /// </summary>
    private static CommandResult? TryUpgradeLateralJoinInPlace(CompoundCommand compound, AircraftState aircraft)
    {
        if (compound.Blocks.Count != 1)
        {
            return null;
        }

        var block = compound.Blocks[0];
        if (block.Condition is not null || block.Commands.Count != 1 || block.Commands[0] is not ClearedApproachCommand capp)
        {
            return null;
        }

        // Only a plain, immediate, non-forced CAPP upgrades in place. Forced (CAPPF), AT/DCT
        // fixes, and a maintain-until-altitude form rebuild the approach via the full handler.
        if (capp.Force || (capp.AtFix is not null) || (capp.DctFix is not null) || (capp.CrossFixAltitude is not null))
        {
            return null;
        }

        var clearance = aircraft.Phases?.ActiveApproach;
        if (clearance is null || !clearance.LateralInterceptOnly)
        {
            return null;
        }

        // A CAPP naming a different approach than the one being joined must re-clear properly.
        if (capp.ApproachId is not null)
        {
            string airport = capp.AirportCode ?? ResolveAirport(aircraft);
            string? resolvedId = NavigationDatabase.Instance.ResolveApproachId(airport, capp.ApproachId);
            if (resolvedId is null || !resolvedId.Equals(clearance.ApproachId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        // Authorize the descent on the join already in progress. The glideslope still gates on the
        // aircraft's lateral establishment (5°/0.15nm) — a relaxed join is not a PTACF forced
        // intercept, so ForcedInterceptCapture stays false and the gate is not bypassed even from a
        // steep cut. Cancel speed adjustments per 7110.65 §5-7-1 (approach clearances cancel
        // previously assigned speeds).
        clearance.LateralInterceptOnly = false;
        aircraft.Targets.TargetSpeed = null;

        return Ok($"Cleared {clearance.ApproachId} approach, runway {RunwayIdentifier.ToDisplayDesignator(clearance.RunwayId)}");
    }

    /// <summary>
    /// True for the one phase-transparent command that still owns a control axis: <c>EXP &lt;alt&gt;</c>
    /// assigns an altitude, so it has to supersede conflicting queued vertical blocks rather than
    /// take the queue-preserving fast path. Everything else on the transparent list is either
    /// genuinely dimensionless (squawk, RFIS, bare EXP) or a pattern modifier — and those classify
    /// as <see cref="CommandDimension.All"/>, so clearing on their behalf would wipe the queued
    /// pattern entry they exist to modify.
    /// </summary>
    private static bool NeedsVerticalSupersede(ParsedCommand cmd) => cmd is ExpediteCommand { Altitude: not null };

    private static bool IsAllTransparent(CompoundCommand compound)
    {
        foreach (var block in compound.Blocks)
        {
            if (block.Condition is not null)
            {
                return false;
            }

            foreach (var cmd in block.Commands)
            {
                if (cmd is UnsupportedCommand)
                {
                    return false;
                }

                if (!CommandDescriber.IsPhaseTransparent(CommandDescriber.ToCanonicalType(cmd)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static CommandResult ApplyTransparentCompound(CompoundCommand compound, AircraftState aircraft, DispatchContext ctx)
    {
        // Transparent commands intentionally bypass DCT-fix validation — preserve that
        // by overriding the flag on a per-call basis.
        var transparentCtx = ctx with
        {
            ValidateDctFixes = false,
        };
        var messages = new List<string>();
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                if (NeedsVerticalSupersede(cmd))
                {
                    var preserved = ClearConflictingBlocks(aircraft, CommandDimension.Vertical, ctx, preserveTriggeredBlocks: false, out var dropped);
                    EmitQueueClearWarning(aircraft, dropped, compound);
                    aircraft.Queue.Blocks.AddRange(preserved);
                }

                var result = ApplyCommand(cmd, aircraft, transparentCtx);
                if (!result.Success)
                {
                    return WithRejectedCommand(result, cmd);
                }

                if (!string.IsNullOrEmpty(result.Message))
                {
                    messages.Add(result.Message);
                }
            }
        }

        return new CommandResult(true, string.Join(", ", messages));
    }

    public static CommandResult Dispatch(ParsedCommand command, AircraftState aircraft, DispatchContext ctx)
    {
        if (command is AssumeCommand)
        {
            return LiveTraffic.LiveTrafficAssumer.Assume(aircraft, ctx);
        }

        if (aircraft.IsShadow)
        {
            return RejectShadow(aircraft);
        }

        // Route ground commands through DispatchCompound for phase interaction
        if (CommandDescriber.IsGroundCommand(command))
        {
            var compound = new CompoundCommand([new ParsedBlock(null, [command])]);
            return DispatchCompound(compound, aircraft, ctx);
        }

        // Phase-transparent commands: apply without clearing queue or phases. EXP <alt> is
        // transparent to the phase system but not to the queue — it assigns an altitude, so
        // it must still supersede conflicting vertical blocks below.
        if (
            (aircraft.Phases?.CurrentPhase is not null)
            && CommandDescriber.IsPhaseTransparent(CommandDescriber.ToCanonicalType(command))
            && !NeedsVerticalSupersede(command)
        )
        {
            return ApplyCommand(command, aircraft, ctx);
        }

        // Selectively clear queue: remove only blocks whose dimensions conflict. This single-
        // command path is always a fresh immediate command (a precondition is a block-level
        // attribute, absent here), so it supersedes — preserveTriggeredBlocks stays false.
        var singleDims = CommandDescriber.GetCommandDimension(command);
        var singlePreserved = ClearConflictingBlocks(aircraft, singleDims, ctx, preserveTriggeredBlocks: false, out var singleDropped);
        EmitQueueClearWarning(aircraft, singleDropped, new CompoundCommand([new ParsedBlock(null, [command])]));
        aircraft.Queue.Blocks.AddRange(singlePreserved);

        bool hadProcedure = aircraft.Procedure.ActiveSidId is not null || aircraft.Procedure.ActiveStarId is not null;
        bool hadViaMode = aircraft.Procedure.SidViaMode || aircraft.Procedure.StarViaMode;
        var result = ApplyCommand(command, aircraft, ctx);
        if (!result.Success)
        {
            return WithRejectedCommand(result, command);
        }

        CheckVectoringWarning(aircraft, [command], hadProcedure, hadViaMode);
        return result;
    }

    private static CommandResult ApplyCommand(ParsedCommand command, AircraftState aircraft, DispatchContext ctx)
    {
        var result = ApplyCommandCore(command, aircraft, ctx);
        EmitProcedureAdvisory(result, aircraft, ctx);
        return result;
    }

    /// <summary>
    /// Surfaces an instructor-facing advisory (e.g. a procedure resolved from a retired AIRAC cycle) on the
    /// terminal via <see cref="DispatchContext.TerminalEmitter"/>. A no-op during dry-run validation, whose
    /// context nulls the emitter — so it never double-fires when a command is validated then applied.
    /// </summary>
    private static void EmitProcedureAdvisory(CommandResult? result, AircraftState aircraft, DispatchContext ctx)
    {
        if (result is { Advisory: { Length: > 0 } advisory })
        {
            ctx.TerminalEmitter?.Invoke(new TerminalEntry("Warning", aircraft.Callsign, advisory));
        }
    }

    /// <summary>
    /// Instructor advisory text for a procedure whose coded legs did not come from the current FAA CIFP —
    /// either recovered from a cached prior AIRAC cycle, or supplied by an ARTCC CIFP fragment. The
    /// procedure may be retired, or still charted but missing from the CIFP dataset. Returns null when the
    /// procedure came from the current cycle.
    /// </summary>
    internal static string? ProcedureSourceAdvisory(string kind, string procedureId, ProcedureSource? source)
    {
        if (source is null)
        {
            return null;
        }

        return source.Kind switch
        {
            ProcedureSourceKind.PriorCycle =>
                $"{procedureId} ({kind}) resolved from a prior AIRAC cycle ({source.Label}) — its coded data is absent from the current FAA CIFP. Verify against current charts and vector as needed.",
            ProcedureSourceKind.ArtccCustom =>
                $"{procedureId} ({kind}) resolved from FAA CIFP data archived by {source.Label} — its coded data is absent from the current FAA CIFP. Verify against current charts and vector as needed.",
            _ => null,
        };
    }

    private static CommandResult ApplyCommandCore(ParsedCommand command, AircraftState aircraft, DispatchContext ctx)
    {
        var rng = ctx.Rng;
        var validateDctFixes = ctx.ValidateDctFixes;

        switch (command)
        {
            // --- Heading ---
            case FlyHeadingCommand cmd:
                return FlightCommandHandler.ApplyHeading(cmd, aircraft);
            case TurnLeftCommand cmd:
                return FlightCommandHandler.ApplyTurnLeft(cmd, aircraft);
            case TurnRightCommand cmd:
                return FlightCommandHandler.ApplyTurnRight(cmd, aircraft);
            case LeftTurnCommand cmd:
                return FlightCommandHandler.ApplyLeftTurn(cmd, aircraft);
            case RightTurnCommand cmd:
                return FlightCommandHandler.ApplyRightTurn(cmd, aircraft);
            case FlyPresentHeadingCommand:
                return FlightCommandHandler.ApplyFlyPresentHeading(aircraft);
            case ForceHeadingCommand cmd:
                return FlightCommandHandler.ApplyForceHeading(cmd, aircraft);

            // --- Altitude ---
            case ClimbMaintainCommand cmd:
                return FlightCommandHandler.ApplyClimbMaintain(cmd, aircraft);
            case DescendMaintainCommand cmd:
                return FlightCommandHandler.ApplyDescendMaintain(cmd, aircraft);
            case ForceAltitudeCommand cmd:
                return FlightCommandHandler.ApplyForceAltitude(cmd, aircraft);

            // --- Speed ---
            case SpeedCommand cmd:
                return FlightCommandHandler.ApplySpeed(cmd, aircraft);
            case ResumeNormalSpeedCommand:
                return FlightCommandHandler.ApplyResumeNormalSpeed(aircraft);
            case ReduceToFinalApproachSpeedCommand:
                return FlightCommandHandler.ApplyReduceToFinalApproachSpeed(aircraft);
            case DeleteSpeedRestrictionsCommand:
                return FlightCommandHandler.ApplyDeleteSpeedRestrictions(aircraft);
            case ExpediteCommand cmd:
                return FlightCommandHandler.ApplyExpedite(cmd, aircraft);
            case NormalRateCommand:
                return FlightCommandHandler.ApplyNormalRate(aircraft);
            case MachCommand cmd:
                return FlightCommandHandler.ApplyMach(cmd, aircraft);
            case ForceSpeedCommand cmd:
                return FlightCommandHandler.ApplyForceSpeed(cmd, aircraft);

            // --- Squawk ---
            case SquawkCommand cmd:
                return FlightCommandHandler.ApplySquawk(cmd, aircraft);
            case SquawkResetCommand:
                return FlightCommandHandler.ApplySquawkReset(aircraft);
            case SquawkVfrCommand:
                return FlightCommandHandler.ApplySquawkVfr(aircraft);
            case SquawkNormalCommand:
                return FlightCommandHandler.ApplySquawkNormal(aircraft);
            case SquawkStandbyCommand:
                return FlightCommandHandler.ApplySquawkStandby(aircraft);
            case IdentCommand:
                return FlightCommandHandler.ApplyIdent(aircraft);
            case RandomSquawkCommand:
                return FlightCommandHandler.ApplyRandomSquawk(aircraft, rng);

            // --- Direct-to ---
            // Every direct-to shape is checked in one place: each of the seven has its own handler,
            // and a guard added to only some of them is the kind of half-wiring that reads as
            // working. AP/1B routes are one-way and reversals are prohibited.
            case ParsedCommand
                when DirectToFixes(command) is { } directFixes
                    && FlightCommandHandler.RejectBackwardsMilitaryRouteDirect(directFixes, aircraft) is { } rejection:
                return rejection;

            case DirectToCommand cmd:
                return FlightCommandHandler.ApplyDirectTo(cmd, aircraft, validateDctFixes);
            case ForceDirectToCommand cmd:
                return FlightCommandHandler.ApplyForceDirectTo(cmd, aircraft);
            case ConstrainedForceDirectToCommand cmd:
                return FlightCommandHandler.ApplyConstrainedForceDirectTo(cmd, aircraft);
            case AppendDirectToCommand cmd:
                return FlightCommandHandler.ApplyAppendDirectTo(cmd, aircraft, validateDctFixes);
            case AppendForceDirectToCommand cmd:
                return FlightCommandHandler.ApplyAppendForceDirectTo(cmd, aircraft);
            case TurnLeftDirectToCommand cmd:
                return FlightCommandHandler.ApplyTurnDirectTo(cmd.Fixes, cmd.SkippedFixes, aircraft, validateDctFixes, TurnDirection.Left);
            case TurnRightDirectToCommand cmd:
                return FlightCommandHandler.ApplyTurnDirectTo(cmd.Fixes, cmd.SkippedFixes, aircraft, validateDctFixes, TurnDirection.Right);

            // --- Warp ---
            case WarpCommand cmd:
                return FlightCommandHandler.ApplyWarp(cmd, aircraft);
            case WarpGroundCommand cmd:
                return FlightCommandHandler.ApplyWarpGround(cmd, aircraft);

            // --- Turn rate ---
            case SetTurnRateCommand cmd:
                return FlightCommandHandler.ApplySetTurnRate(cmd, aircraft);
            case ClearTurnRateCommand:
                return FlightCommandHandler.ApplyClearTurnRate(aircraft);

            // --- Misc ---
            case DeleteCommand:
                aircraft.Ground.PendingAutoDelete = true;
                return Ok($"{aircraft.Callsign} marked for delete");
            case CancelAutoDeleteCommand:
            {
                int removed = RemoveQueuedDeleteBlocks(aircraft);
                aircraft.Ground.AutoDeleteExempt = true;
                aircraft.Ground.PendingAutoDelete = false;
                string msg =
                    removed > 0
                        ? $"Auto-delete cancelled ({removed} pending {(removed == 1 ? "block" : "blocks")} cleared); aircraft will remain on the scope"
                        : "Auto-delete cancelled; aircraft will remain on the scope";
                return Ok(msg);
            }
            case WaitCommand cmd:
                return Ok($"Wait {cmd.Seconds} seconds");
            case WaitDistanceCommand cmd:
                return Ok($"Wait {cmd.DistanceNm} nm");
            case SayCommand sayCmd:
                ctx.TerminalEmitter?.Invoke(new TerminalEntry("Say", aircraft.Callsign, sayCmd.Text));
                return Ok("");
            case ReportCommand reportCmd:
                return NavigationCommandHandler.DispatchReport(reportCmd, aircraft, ctx);
            case SaySpeedCommand:
                ctx.TerminalEmitter?.Invoke(new TerminalEntry("SaySpeed", aircraft.Callsign, PilotSayBuilder.BuildSpeed(aircraft)));
                return Ok("");
            case SayMachCommand:
                ctx.TerminalEmitter?.Invoke(new TerminalEntry("SayMach", aircraft.Callsign, PilotSayBuilder.BuildMach(aircraft)));
                return Ok("");
            case SayAltitudeCommand:
                ctx.TerminalEmitter?.Invoke(new TerminalEntry("SayAltitude", aircraft.Callsign, PilotSayBuilder.BuildAltitude(aircraft)));
                return Ok("");
            case SayHeadingCommand:
                ctx.TerminalEmitter?.Invoke(new TerminalEntry("SayHeading", aircraft.Callsign, PilotSayBuilder.BuildHeading(aircraft)));
                return Ok("");
            case SayPositionCommand:
                ctx.TerminalEmitter?.Invoke(new TerminalEntry("SayPosition", aircraft.Callsign, PilotSayBuilder.BuildPosition(aircraft)));
                return Ok("");
            case SayExitFixEstimateCommand:
                ctx.TerminalEmitter?.Invoke(
                    new TerminalEntry("SayExitFixEstimate", aircraft.Callsign, PilotSayBuilder.BuildExitFixEstimate(aircraft))
                );
                return Ok("");
            case SayExpectedApproachCommand:
                ctx.TerminalEmitter?.Invoke(
                    new TerminalEntry("SayExpectedApproach", aircraft.Callsign, PilotSayBuilder.BuildExpectedApproach(aircraft))
                );
                return Ok("");

            // --- Contact / Frequency change (M10.1.4) ---
            case ContactCommand contactCmd:
                return ContactCommandHandler.HandleContact(contactCmd, aircraft, ctx);
            case FrequencyChangeApprovedCommand:
                return ContactCommandHandler.HandleFrequencyChangeApproved(aircraft, ctx);
            case ClearedBravoAirspaceCommand:
                aircraft.IsClearedIntoBravo = true;
                return Ok("Cleared into Bravo airspace");
            case AcknowledgePilotContactCommand:
                aircraft.HasControllerAcknowledgedInitialContact = true;
                return Ok("Radio contact acknowledged");

            // --- Navigation commands ---
            case JoinRadialOutboundCommand cmd:
                return NavigationCommandHandler.DispatchJrado(cmd, aircraft);
            case JoinRadialInboundCommand cmd:
                return NavigationCommandHandler.DispatchJradi(cmd, aircraft);
            case DepartFixCommand cmd:
                return NavigationCommandHandler.DispatchDepartFix(cmd, aircraft);
            case CrossFixCommand cmd:
                return NavigationCommandHandler.DispatchCrossFix(cmd, aircraft);
            case ClimbViaCommand cmd:
                return NavigationCommandHandler.DispatchClimbVia(cmd, aircraft);
            case DescendViaCommand cmd:
                return NavigationCommandHandler.DispatchDescendVia(cmd, aircraft);

            case ExpectApproachCommand eapp:
            {
                var eappResolved = ApproachCommandHandler.ResolveApproach(eapp.ApproachId, eapp.AirportCode, aircraft);
                if (!eappResolved.Success)
                {
                    return new CommandResult(false, eappResolved.Error);
                }
                var (eappProc, eappRunway, _) = eappResolved;
                aircraft.Approach.Expected = eappProc.ApproachId;
                // Telling a pilot to expect "ILS 30" implies the arrival runway is 30. Set
                // DestinationRunway so the active STAR can load its runway transition (and
                // anything else that keys off the assigned runway) without a separate RWY.
                aircraft.Procedure.DestinationRunway = eappRunway.Designator;
                // If a STAR is already active, extend the live NavigationRoute with the
                // runway transition for the new runway — otherwise the published vector
                // segment off the STAR's final fix never enters the route until CAPP.
                NavigationCommandHandler.ExtendActiveStarWithRunwayTransition(aircraft, eappRunway.Designator);
                return Ok($"Expecting {eappProc.ApproachId} approach");
            }

            case ListApproachesCommand cmd:
                return NavigationCommandHandler.DispatchListApproaches(cmd, aircraft);
            case JoinStarCommand cmd:
                return NavigationCommandHandler.DispatchJarr(cmd, aircraft);
            case JoinAirwayCommand cmd:
                return NavigationCommandHandler.DispatchJawy(cmd, aircraft);
            case ClearedIntoMilitaryRouteCommand cmd:
                return MilitaryRouteCommandHandler.DispatchClearedInto(cmd, aircraft, ctx);
            case MaintainMilitaryRouteAltitudesCommand:
                return MilitaryRouteCommandHandler.DispatchMaintainRouteAltitudes(aircraft);
            case ClearedOutOfMilitaryRouteCommand cmd:
                return MilitaryRouteCommandHandler.DispatchClearedOutOf(cmd, aircraft);
            case ClearedToConductRefuelingCommand cmd:
                return MilitaryRouteCommandHandler.DispatchClearedToConductRefueling(cmd, aircraft);
            case HoldingPatternCommand cmd:
                return NavigationCommandHandler.DispatchHoldingPattern(cmd, aircraft);
            case JoinFinalApproachCourseCommand cmd:
                return NavigationCommandHandler.DispatchJfac(cmd, aircraft);

            // --- Approach commands ---
            case ClearedApproachCommand cmd:
                return ApproachCommandHandler.TryClearedApproach(cmd, aircraft);
            case JoinApproachCommand cmd:
                return ApproachCommandHandler.TryJoinApproach(cmd.ApproachId, cmd.AirportCode, cmd.Force, straightIn: false, aircraft);
            case ClearedApproachStraightInCommand cmd:
                return ApproachCommandHandler.TryJoinApproach(cmd.ApproachId, cmd.AirportCode, force: false, straightIn: true, aircraft);
            case JoinApproachStraightInCommand cmd:
                return ApproachCommandHandler.TryJoinApproach(cmd.ApproachId, cmd.AirportCode, force: false, straightIn: true, aircraft);
            case PositionTurnAltitudeClearanceCommand cmd:
                return ApproachCommandHandler.TryPtac(cmd, aircraft);
            case ClearedVisualApproachCommand cmd:
                return ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft, ctx);
            case ReportFieldInSightCommand:
                return NavigationCommandHandler.DispatchReportFieldInSight(aircraft, ctx);
            case ReportFieldAdvisoryCommand cmd:
                return NavigationCommandHandler.DispatchReportFieldAdvisory(cmd, aircraft, ctx);
            case ReportFieldInSightForcedCommand:
                return NavigationCommandHandler.DispatchReportFieldInSightForced(aircraft, ctx);
            case ReportTrafficInSightCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficInSight(aircraft, cmd.TargetCallsign, ctx);
            case ReportTrafficAdvisoryCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficAdvisory(cmd, aircraft, ctx);
            case ReportTrafficRelativeCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficRelative(cmd, aircraft, ctx);
            case ReportTrafficPatternCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficPattern(cmd, aircraft, ctx);
            case ReportTrafficLandmarkCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficLandmark(cmd, aircraft, ctx);
            case ReportTrafficInSightForcedCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficInSightForced(aircraft, cmd.TargetCallsign, ctx);
            case SafetyAlertCommand cmd:
                return NavigationCommandHandler.DispatchSafetyAlert(cmd, aircraft, ctx);
            case WakeAdvisoryCommand:
                return Ok("Caution wake turbulence");

            // --- Pattern entry commands ---
            case EnterLeftDownwindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Downwind,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case EnterRightDownwindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Downwind,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case EnterLeftCrosswindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Crosswind,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case EnterRightCrosswindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Crosswind,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case EnterLeftBaseCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Base,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: cmd.FinalDistanceNm,
                    groundLayout: ctx.GroundLayout
                );
            case EnterRightBaseCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Base,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: cmd.FinalDistanceNm,
                    groundLayout: ctx.GroundLayout
                );
            case EnterFinalCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    // EF has no L/R in its verb — let TryEnterPattern infer from runway
                    // (28R parallel to 28L → Right, single runway → Left).
                    requestedDirection: null,
                    PatternEntryLeg.Final,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case PatternSizeCommand cmd:
                return PatternCommandHandler.TrySetPatternSize(aircraft, cmd.SizeNm, ctx.GroundLayout);
            case Plan270Command:
                return PatternCommandHandler.TryPlan270(aircraft);

            // Helicopter commands
            case ClearedTakeoffPresentCommand ctopp:
                return DepartureClearanceHandler.TryClearedTakeoffPresent(ctopp, aircraft, aircraft.Ground.Layout);
            case AirTaxiCommand atxi:
                return GroundCommandHandler.TryAirTaxi(aircraft, atxi.Destination, aircraft.Ground.Layout);
            case LandCommand land:
                return GroundCommandHandler.TryLand(aircraft, land, aircraft.Ground.Layout);

            // Hold commands (orbit/hover)
            case HoldPresentPosition360Command hpp:
                return PatternCommandHandler.TryHoldPresentPosition(aircraft, hpp.Direction);
            case HoldPresentPositionHoverCommand:
                return PatternCommandHandler.TryHoldPresentPosition(aircraft, null);
            case HoldAtFixOrbitCommand hfix:
                return PatternCommandHandler.TryHoldAtFix(aircraft, hfix.FixName, hfix.Lat, hfix.Lon, hfix.Direction);
            case HoldAtFixHoverCommand hfixH:
                return PatternCommandHandler.TryHoldAtFix(aircraft, hfixH.FixName, hfixH.Lat, hfixH.Lon, null);

            // --- Tower commands (also dispatched via TryApplyTowerCommand in the phase path) ---
            case ClearedToLandCommand ctl:
                return PatternCommandHandler.TryClearedToLand(ctl, aircraft, ctx);
            case ForceLandingCommand flc:
                return PatternCommandHandler.TryForceLanding(flc, aircraft, ctx);
            case LandAndHoldShortCommand lahso:
                return PatternCommandHandler.TryLandAndHoldShort(lahso, aircraft, aircraft.Ground.Layout, ctx);
            case CancelLandingClearanceCommand:
                return PatternCommandHandler.TryCancelLandingClearance(aircraft);
            case GoAroundCommand ga:
                return PatternCommandHandler.TryGoAround(ga, aircraft, ctx.GroundLayout);
            case MakeLeftTrafficCommand mlt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, mlt.RunwayId, mlt.Altitude, ctx.GroundLayout);
            case MakeRightTrafficCommand mrt:
                return PatternCommandHandler.TryChangePatternDirection(
                    aircraft,
                    PatternDirection.Right,
                    mrt.RunwayId,
                    mrt.Altitude,
                    ctx.GroundLayout
                );
            case MakeLeft360Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Left, 360);
            case MakeRight360Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Right, 360);
            case MakeLeft270Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Left, 270);
            case MakeRight270Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Right, 270);

            // Pattern modifiers (also dispatched via TryApplyTowerCommand in the phase path).
            // Present here so they can arm a pending pattern entry when no phase is active yet —
            // e.g. EXT DOWNWIND / SA / MNA issued while ERD sits queued behind DCT VPCOL.
            case ExtendPatternCommand ext:
                return PatternCommandHandler.TryExtendPattern(aircraft, ext.Leg, ctx.GroundLayout);
            case MakeShortApproachCommand:
                return PatternCommandHandler.TryMakeShortApproach(aircraft);
            case MakeNormalApproachCommand:
                return PatternCommandHandler.TryMakeNormalApproach(aircraft);

            // Option clearances (also dispatched via TryApplyTowerCommand in the phase path). Present
            // here for the same reason as the modifiers above: with no phase they must reach their
            // handler so it can pre-issue the clearance against a queued pattern entry. Without these
            // arms they fall to the default NoDispatcherArm rejection instead.
            case TouchAndGoCommand tg:
                return PatternCommandHandler.TrySetupTouchAndGo(aircraft, tg.TrafficPattern, ctx);
            case StopAndGoCommand sg:
                return PatternCommandHandler.TrySetupStopAndGo(aircraft, sg.TrafficPattern, ctx);
            case LowApproachCommand la:
                return PatternCommandHandler.TrySetupLowApproach(aircraft, la.TrafficPattern, ctx);
            case ClearedForOptionCommand opt:
                return PatternCommandHandler.TrySetupClearedForOption(aircraft, opt.TrafficPattern, ctx);

            case FollowCommand follow:
                return TryAirborneFollow(aircraft, follow, ctx);

            // --- Flight plan ---
            // Scenario presets and conditional forms (`AT 5000 APT KOAK`) reach this switch via the
            // queued-block apply path — yaat-server's interactive intercept only covers the bare,
            // unconditioned command. The server's flight-plan change tracker picks the mutation up
            // for CRC/training broadcasts on the next tick.
            case ChangeDestinationCommand dest:
                return FlightPlanCommandHandler.TryChangeDestination(aircraft, dest.Airport);

            case CancelIfrCommand:
                if (aircraft.FlightPlan.IsVfr)
                {
                    return new CommandResult(false, "Aircraft is already VFR");
                }
                aircraft.FlightPlan.FlightRules = "VFR";
                aircraft.FlightPlan.Altitude = PlannedAltitude.Vfr(null);
                return Ok("IFR cancelled, aircraft is now VFR");

            case UnsupportedCommand cmd:
                return new CommandResult(false, $"Command not yet supported: {cmd.RawText}");

            case var strip when TrackEngine.IsStripCommand(strip):
                // Strip state is host-owned (yaat-server's TrainingRoom.StripState) — the Sim has no
                // strip handler. Queue preset/deferred/triggered strip commands for the host to drain
                // (TickProcessor.ProcessDeferredStripDispatches → StripCommandHandler) rather than
                // letting them fall to the no-dispatcher-arm default below.
                aircraft.PendingStripDispatches.Add(strip);
                return Ok(CommandDescriber.DescribeNatural(strip));

            default:
                // No handler arm for this command in the current context. Keep the command type in the
                // log for bug triage, but give the user a plain, actionable message. The most common
                // trigger is a ground command (TAXI/PUSH/…) sent to an airborne aircraft.
                Log.LogWarning(
                    "No dispatcher arm for {CommandType} ({Description}) on {Callsign}",
                    command.GetType().Name,
                    CommandDescriber.DescribeNatural(command),
                    aircraft.Callsign
                );
                var fallbackMessage =
                    CommandDescriber.IsGroundCommand(command) && !aircraft.IsOnGround
                        ? $"{CommandDescriber.DescribeNatural(command)} requires the aircraft to be on the ground"
                        : $"Unable to {CommandDescriber.DescribeNatural(command)}";
                return new CommandResult(false, fallbackMessage, NoDispatcherArm: true);
        }
    }

    /// <summary>
    /// Strips every queued block that carries a <see cref="DeleteCommand"/> and returns how many were
    /// removed. Backs <c>NODEL</c>: a pending delete must die whatever armed it — <c>ONHS DEL</c>,
    /// <c>CROSS 28R; DEL</c>, <c>AT FIXIE DEL</c>. Leaving one behind is not a cosmetic miss: when it
    /// eventually fires it raises <see cref="AircraftGroundOps.PendingAutoDelete"/>, which deliberately
    /// bypasses the <see cref="AircraftGroundOps.AutoDeleteExempt"/> flag NODEL just set, so the
    /// aircraft would disappear despite the cancel. Blocks removed from before the cursor pull
    /// <see cref="CommandQueue.CurrentBlockIndex"/> back with them so it keeps pointing at the same
    /// logical block.
    /// </summary>
    private static int RemoveQueuedDeleteBlocks(AircraftState aircraft)
    {
        var queue = aircraft.Queue;
        int removed = 0;
        int removedBeforeCursor = 0;

        for (int i = queue.Blocks.Count - 1; i >= 0; i--)
        {
            if (!queue.Blocks[i].HasDeleteCommand)
            {
                continue;
            }

            queue.Blocks.RemoveAt(i);
            removed++;
            if (i < queue.CurrentBlockIndex)
            {
                removedBeforeCursor++;
            }
        }

        queue.CurrentBlockIndex = Math.Max(0, queue.CurrentBlockIndex - removedBeforeCursor);
        return removed;
    }

    /// <summary>
    /// Validates the immediately-applied commands in a compound by running them
    /// on a snapshot clone of the aircraft. Only the first block is dry-run,
    /// and only when it has no condition — every other block is deferred:
    /// either it has an explicit AT/LV/etc. trigger, or it sits in the queue
    /// behind the previous block's tracked commands and only fires once the
    /// aircraft sequences past them. By that time the aircraft is in a
    /// different state, so dry-running deferred handlers against current
    /// state produces false rejections (e.g. <c>DCT VPCBT; ERB 28R</c> would
    /// reject "too close for base" at present position even though ERB would
    /// fire at VPCBT well outside the base-entry floor).
    ///
    /// Syntax/parse-level errors still bubble up — the parser rejects unknown
    /// verbs and malformed args before <see cref="DispatchCompound"/> is
    /// called. Handler-level failures on deferred blocks surface as
    /// <see cref="AircraftState.PendingWarnings"/> entries at the trigger
    /// fire moment (see <see cref="FlightPhysics.ApplyBlock"/>).
    /// </summary>
    private static CommandResult? DryRunValidate(CompoundCommand compound, AircraftState aircraft, DispatchContext ctx)
    {
        if (compound.Blocks.Count == 0 || compound.Blocks[0].Condition is not null)
        {
            // First block is conditional → entire compound is deferred behind a
            // trigger. Cannot meaningfully evaluate handler feasibility against
            // current state; fire-time evaluation owns this.
            return null;
        }

        var clone = AircraftState.FromSnapshot(aircraft.ToSnapshot(), ctx.GroundLayout);

        // ApproachClearance.Procedure is not serialized, so the clone's active approach comes back without it and
        // GetProgrammedFixes — which reads it — would report a smaller programmed set than the real aircraft. Re-attach
        // the real procedure so DCT-fix validation below sees the true set and cannot reject a direct-to onto a fix of
        // the approach the aircraft is already flying.
        if (clone.Phases?.ActiveApproach is { Procedure: null } cloneApproach)
        {
            cloneApproach.Procedure = aircraft.Phases?.ActiveApproach?.Procedure;
        }

        // Dry-run uses a deterministic RNG and suppresses auto-cross-runway side effects and terminal emission.
        // The clone is discarded; emitting SAY broadcasts here would surface phantom pilot transmissions before the
        // trigger actually fires. DCT-fix validation stays ENABLED: the real path clears conflicting queue blocks and
        // every deferred dispatch before applying the first block, so a rejection that reaches ApplyBlock destroys
        // unrelated pending work on its way out. Catching it here keeps the contract that a rejected command leaves
        // state unchanged.
        var dryCtx = ctx with
        {
            Rng = new Random(0),
            AutoCrossRunway = false,
            TerminalEmitter = null,
        };

        // Model the real dispatch path's pre-application queue clear on the clone so a block whose
        // application reads the queue (a pattern modifier scanning for a queued entry) is validated
        // against the same post-clear queue it will actually see. Without this, a modifier-led compound
        // (e.g. "EXT DOWNWIND; CLAND") passes dry-run against the intact clone queue but fails on the
        // real aircraft after ClearConflictingBlocks wipes the queued entry — a silent-wipe-then-fail.
        // A conditional first block already returned above, so the real path always clears here
        // (non-conditional-incoming); mirror its dims and preserve flag.
        ClearConflictingBlocks(clone, CommandDescriber.GetCompoundDimensions(compound), ctx, ctx.PreserveConditionals, out _);

        var firstBlock = compound.Blocks[0];
        foreach (var cmd in firstBlock.Commands)
        {
            var result = DryRunApplyCommand(cmd, clone, dryCtx);
            if (!result.Success)
            {
                return WithRejectedCommand(result, cmd);
            }
        }

        return null;
    }

    /// <summary>
    /// Applies a single command during dry-run validation. Handles both normal
    /// commands (via ApplyCommand) and tower-only commands that are normally
    /// dispatched through TryApplyTowerCommand.
    /// </summary>
    private static CommandResult DryRunApplyCommand(ParsedCommand cmd, AircraftState clone, DispatchContext ctx)
    {
        // Try the tower-command path first if phases are active — it handles
        // CTO, CLAND, LUAW, go-around, pattern turns, etc.
        var currentPhase = clone.Phases?.CurrentPhase;
        if (currentPhase is not null)
        {
            var towerResult = TryApplyTowerCommand(cmd, clone, currentPhase, ctx);
            if (towerResult is not null)
            {
                return towerResult;
            }
        }

        // Then try ApplyCommand — handles flight, nav, pattern entry, etc.
        var result = ApplyCommand(cmd, clone, ctx);
        if (!result.NoDispatcherArm)
        {
            return result;
        }

        // Tower command without phases — give a descriptive error.
        if (CommandDescriber.IsTowerCommand(cmd))
        {
            return new CommandResult(false, $"{CommandDescriber.DescribeNatural(cmd)} requires an active runway assignment");
        }

        // Commands not handled at the Sim level (e.g. DEL, server-side commands)
        // cannot be validated here — assume valid.
        return new CommandResult(true, "");
    }

    /// <summary>
    /// True when a parsed block in a compound, occurring after a tower-handled
    /// first block (e.g. <c>ERD 28R</c> or <c>COPT</c>), can be applied immediately
    /// rather than enqueued. These commands modify the just-installed or pending
    /// pattern phases (arming a pending downwind for short approach, extending the
    /// current or upcoming upwind, etc.) and would otherwise sit in the command
    /// queue forever — <see cref="FlightPhysics"/>.UpdateCommandQueue short-
    /// circuits while any phase is active, so the chained modifier would never
    /// run before its target moment.
    /// </summary>
    private static bool IsImmediatePhaseModifierBlock(ParsedBlock block)
    {
        if (block.Condition is not null || block.Commands.Count != 1)
        {
            return false;
        }

        return block.Commands[0] is MakeShortApproachCommand or MakeNormalApproachCommand or ExtendPatternCommand;
    }

    /// <summary>
    /// True when a block is a single, unconditional landing/option clearance (CLAND/TG/SG/LA/COPT).
    /// These are tower commands, so <see cref="CommandDescriber.GetCommandDimension"/> reports
    /// <see cref="CommandDimension.All"/> and the All/None fast path in
    /// <see cref="ClearConflictingBlocks"/> wipes the whole queue — destroying the very queued pattern
    /// entry the clearance is meant to pre-arm. (Today a no-phase CLAND survives only because
    /// DryRunValidate rejects it before dispatch reaches the wipe.) Deliberately distinct from
    /// <see cref="IsImmediatePhaseModifierBlock"/>, which is about modifier blocks that follow a
    /// tower-handled first block and whose commands carry the <c>None</c> dimension instead.
    /// </summary>
    private static bool IsPendingLandingClearanceBlock(ParsedBlock block)
    {
        if (block.Condition is not null || block.Commands.Count != 1)
        {
            return false;
        }

        return IsPendingLandingClearanceCommand(block.Commands[0]);
    }

    /// <summary>The landing/option clearance verbs that can be pre-issued against a queued pattern entry.</summary>
    private static bool IsPendingLandingClearanceCommand(ParsedCommand command) =>
        command is ClearedToLandCommand or TouchAndGoCommand or StopAndGoCommand or LowApproachCommand or ClearedForOptionCommand;

    /// <summary>
    /// A clearance that trails a pattern entry in the same transmission (<c>DCT VPCOL; ERD 28R; CLAND</c>)
    /// is pre-issued against that entry, so a runway it names must agree with the entry's (7110.65
    /// §3-10-5). Checked before anything is applied so a contradiction rejects the whole compound rather
    /// than landing the leading blocks and failing on the trailing one. Returns null when there is no
    /// such pairing or the two agree.
    /// </summary>
    private static CommandResult? ValidateTrailingClearanceRunway(CompoundCommand compound, AircraftState aircraft)
    {
        string? entryRunwayId = null;
        foreach (var block in compound.Blocks)
        {
            if (block.Commands.Count == 1 && PatternCommandHandler.IsPatternEntryCommand(block.Commands[0]))
            {
                entryRunwayId = PatternCommandHandler.PatternEntryRunwayOf(block.Commands[0]) is { } raw
                    ? RunwayIdentifier.NormalizeDesignator(raw)
                    : null;
                continue;
            }

            if (entryRunwayId is null || !IsPendingLandingClearanceBlock(block))
            {
                continue;
            }

            if (
                block.Commands[0] is ClearedToLandCommand { RunwayId: { } requestedRaw }
                && !string.Equals(RunwayIdentifier.NormalizeDesignator(requestedRaw), entryRunwayId, StringComparison.OrdinalIgnoreCase)
            )
            {
                return new CommandResult(
                    false,
                    $"Cannot clear for runway {RunwayIdentifier.ToDisplayDesignator(requestedRaw)} — {aircraft.Callsign} is queued to enter the pattern for runway {RunwayIdentifier.ToDisplayDesignator(entryRunwayId)}"
                );
            }
        }

        return null;
    }

    /// <summary>
    /// When a compound starts with <c>CROSS</c> and that CROSS clears (or is
    /// about to clear) a runway hold-short, retroactively tag the subsequent
    /// untriggered blocks with <see cref="BlockTriggerType.AfterRunwayCrossing"/>
    /// so they fire only after the aircraft has rolled past the far-side
    /// runway hold bars (i.e. <see cref="Yaat.Sim.Phases.Ground.CrossingRunwayPhase"/>
    /// has run and completed). Without this, an untriggered <c>HOLD</c> block
    /// would sit in the queue forever (UpdateCommandQueue short-circuits while
    /// any phase is active and the post-CROSS phase chain auto-appends a
    /// TaxiingPhase without an intervening null gap).
    /// </summary>
    private static void AttachAfterRunwayCrossingTrigger(
        CompoundCommand compound,
        AircraftState aircraft,
        int firstNewBlockIdx,
        Phase? phaseBeforeDispatch,
        bool hadPendingCrossingBeforeDispatch,
        DispatchContext ctx
    )
    {
        if (compound.Blocks.Count <= 1)
        {
            return;
        }

        if (compound.Blocks[0].Commands.Count == 0 || compound.Blocks[0].Commands[0] is not CrossRunwayCommand)
        {
            return;
        }

        if (!WillProduceRunwayCrossing(aircraft, phaseBeforeDispatch, hadPendingCrossingBeforeDispatch, ctx))
        {
            return;
        }

        var trigger = new BlockTrigger { Type = BlockTriggerType.AfterRunwayCrossing };
        for (int i = firstNewBlockIdx + 1; i < aircraft.Queue.Blocks.Count; i++)
        {
            var block = aircraft.Queue.Blocks[i];
            if (block.Trigger is not null)
            {
                // User provided an explicit trigger (LV / AT / ATFN / …) — respect it.
                continue;
            }

            block.Trigger = trigger;
        }
    }

    /// <summary>
    /// Variant of <see cref="AttachAfterRunwayCrossingTrigger"/> for the
    /// tower-handled-first-block branch in <see cref="DispatchCompound"/>: there
    /// the original compound's first block (CROSS) was already applied via
    /// <see cref="TryApplyTowerCommand"/> and never made it into the queue —
    /// only the remaining blocks reached <see cref="EnqueueBlocks"/>. We still
    /// need to tag them with the post-crossing trigger when the original-first
    /// block was a runway-crossing CROSS.
    /// </summary>
    private static void AttachAfterRunwayCrossingTriggerForToweredFirstBlock(
        CompoundCommand originalCompound,
        AircraftState aircraft,
        int firstRemainingIdx,
        Phase? phaseBeforeDispatch,
        bool hadPendingCrossingBeforeDispatch,
        DispatchContext ctx
    )
    {
        if (originalCompound.Blocks.Count == 0 || originalCompound.Blocks[0].Commands.Count == 0)
        {
            return;
        }

        if (originalCompound.Blocks[0].Commands[0] is not CrossRunwayCommand)
        {
            return;
        }

        if (!WillProduceRunwayCrossing(aircraft, phaseBeforeDispatch, hadPendingCrossingBeforeDispatch, ctx))
        {
            return;
        }

        var trigger = new BlockTrigger { Type = BlockTriggerType.AfterRunwayCrossing };
        for (int i = firstRemainingIdx; i < aircraft.Queue.Blocks.Count; i++)
        {
            var block = aircraft.Queue.Blocks[i];
            if (block.Trigger is not null)
            {
                continue;
            }
            block.Trigger = trigger;
        }
    }

    /// <summary>
    /// True when the just-dispatched <c>CROSS</c> puts a
    /// <see cref="Yaat.Sim.Phases.Ground.CrossingRunwayPhase"/> in the aircraft's future, so the
    /// blocks chained behind it should wait for that crossing. Two shapes qualify:
    ///
    /// <list type="bullet">
    /// <item>the aircraft was stopped at a runway hold-short (implicit
    /// <see cref="HoldShortReason.RunwayCrossing"/> or an explicit-but-runway-named
    /// <see cref="HoldShortReason.ExplicitHoldShort"/>) and the CROSS satisfied it. A
    /// <see cref="HoldShortReason.DestinationRunway"/> hold counts only once the taxi route has
    /// completed at it — that is exactly when CROSS undesignates the runway and taxis across to the far
    /// side instead of rejecting in favour of LUAW/CTO (see <c>GroundCommandHandler.TryCrossSingleRunway</c>);</item>
    /// <item>the aircraft was still taxiing and the CROSS pre-cleared a runway crossing further along
    /// the route — <see cref="TaxiingPhase"/> drives straight into a
    /// <see cref="Yaat.Sim.Phases.Ground.CrossingRunwayPhase"/> when it reaches an already-cleared
    /// crossing. <paramref name="hadPendingCrossingBeforeDispatch"/> is what makes this arm specific to
    /// the CROSS just issued: without it, an unrelated crossing already cleared by AutoCross would
    /// defer the chained blocks of (say) a <c>CROSS B; …</c> across a taxiway.</item>
    /// </list>
    ///
    /// Never guess here. A trigger attached to a crossing that never happens strands the block: an
    /// unapplied <see cref="BlockTriggerType.AfterRunwayCrossing"/> block waits forever, and so does an
    /// untriggered one (ground aircraft end in terminal phases, and
    /// <see cref="FlightPhysics.UpdateCommandQueue"/> skips untriggered blocks while a phase is active).
    /// </summary>
    private static bool WillProduceRunwayCrossing(AircraftState aircraft, Phase? phase, bool hadPendingCrossingBeforeDispatch, DispatchContext ctx)
    {
        if (phase is HoldingShortPhase hp)
        {
            if (hp.HoldShort.TargetName is not { Length: > 0 } target || !char.IsAsciiDigit(target[0]))
            {
                return false;
            }

            return hp.HoldShort.Reason != HoldShortReason.DestinationRunway || aircraft.Ground.AssignedTaxiRoute is { IsComplete: true };
        }

        return !hadPendingCrossingBeforeDispatch && HasPendingRunwayCrossing(aircraft, ctx);
    }

    /// <summary>
    /// Whether the aircraft's taxi route still has a cleared runway crossing ahead of its cursor.
    /// Delegates to <see cref="TaxiingPhase.HasPendingClearedRunwayCrossing"/> so the dispatcher and the
    /// phase agree on what counts as a crossing.
    /// </summary>
    private static bool HasPendingRunwayCrossing(AircraftState aircraft, DispatchContext ctx) =>
        TaxiingPhase.HasPendingClearedRunwayCrossing(aircraft.Ground.AssignedTaxiRoute, aircraft.Ground.Layout ?? ctx.GroundLayout);

    private static bool IsPatternEntryWithRunway(ParsedCommand cmd)
    {
        return cmd switch
        {
            EnterLeftDownwindCommand { RunwayId: not null } => true,
            EnterRightDownwindCommand { RunwayId: not null } => true,
            EnterLeftCrosswindCommand { RunwayId: not null } => true,
            EnterRightCrosswindCommand { RunwayId: not null } => true,
            EnterLeftBaseCommand { RunwayId: not null } => true,
            EnterRightBaseCommand { RunwayId: not null } => true,
            EnterFinalCommand { RunwayId: not null } => true,
            MakeLeftTrafficCommand { RunwayId: not null } => true,
            MakeRightTrafficCommand { RunwayId: not null } => true,
            _ => false,
        };
    }

    /// <summary>
    /// If the first block is a bare (unconditioned) WAIT, extract it as a deferred dispatch.
    /// The remaining blocks become the payload, validated now but dispatched later when the
    /// timer expires. Returns null if the compound doesn't start with a bare WAIT.
    /// </summary>
    private static CommandResult? TryDeferLeadingWait(CompoundCommand compound, AircraftState aircraft, DispatchContext ctx)
    {
        var firstBlock = compound.Blocks[0];
        if (firstBlock.Condition is not null)
        {
            return null;
        }

        // Find a WAIT command in the first block (could be sole command or parallel with others)
        WaitCommand? waitCmd = null;
        WaitDistanceCommand? waitDistCmd = null;
        foreach (var cmd in firstBlock.Commands)
        {
            if (cmd is WaitCommand w)
            {
                waitCmd = w;
                break;
            }

            if (cmd is WaitDistanceCommand wd)
            {
                waitDistCmd = wd;
                break;
            }
        }

        if (waitCmd is null && waitDistCmd is null)
        {
            return null;
        }

        // Build payload: sibling commands from the same block (minus WAIT) + subsequent blocks.
        // "WAIT 10, FH 270" → payload is [FH 270]; "WAIT 10; FH 270" → payload is [FH 270].
        var payloadBlocks = StripDeferralGateBlocks(compound);

        // Bare WAIT with no payload — standalone wait, let queue handle it
        if (payloadBlocks is null)
        {
            return null;
        }

        var payload = new CompoundCommand(payloadBlocks);

        // Validate the payload commands now so the user gets immediate feedback
        foreach (var block in payloadBlocks)
        {
            foreach (var cmd in block.Commands)
            {
                if (CommandDescriber.IsGroundCommand(cmd) && !aircraft.IsOnGround)
                {
                    return new CommandResult(false, $"{CommandDescriber.DescribeNatural(cmd)} requires the aircraft to be on the ground");
                }
            }
        }

        // Build a description of the deferred payload
        var payloadDesc = string.Join(" ; then ", payloadBlocks.Select(b => string.Join(", ", b.Commands.Select(CommandDescriber.DescribeNatural))));

        DeferredDispatch deferred;
        string timerDesc;
        if (waitCmd is not null)
        {
            deferred = new DeferredDispatch(waitCmd.Seconds, payload)
            {
                SourceText = compound.SourceText,
                IsScenarioScripted = ctx.IsScenarioScripted,
            };
            timerDesc = $"{waitCmd.Seconds}s";
        }
        else
        {
            deferred = new DeferredDispatch(payload, waitDistCmd!.DistanceNm)
            {
                SourceText = compound.SourceText,
                IsScenarioScripted = ctx.IsScenarioScripted,
            };
            timerDesc = $"{waitDistCmd.DistanceNm}nm";
        }

        aircraft.DeferredDispatches.Add(deferred);
        return new CommandResult(true, $"Will execute in {timerDesc}: {payloadDesc}");
    }

    /// <summary>
    /// If the first block has a GiveWay condition, defer the entire compound as a
    /// give-way-gated deferred dispatch. The aircraft stays in its current phase
    /// (e.g. AtParkingPhase) and the payload dispatches fresh when the target passes.
    /// Returns null if the compound doesn't start with a GiveWay condition.
    ///
    /// When <paramref name="ctx"/>.FindAircraft is wired (production), an unresolved
    /// target callsign is hard-rejected so typos don't silently fire the deferred
    /// payload via the "target gone → MET" shortcut in IsGiveWayMet.
    /// </summary>
    /// <summary>
    /// Builds the payload blocks a deferred dispatch runs when its gate fires, by stripping that gate off
    /// <paramref name="compound"/>: a <c>WAIT</c>/<c>WAITD</c> command out of the first block's commands, or a
    /// give-way condition off the first block. Returns null when there is no gate to strip, or when stripping leaves
    /// nothing to run (a bare <c>WAIT</c>).
    ///
    /// Shared by the two dispatch paths that create deferrals and by <see cref="DeferredDispatch.FromSnapshot"/>,
    /// which re-parses the stored source text — gate still attached — and has to strip it exactly the same way. One
    /// implementation is the point: while these were separate, a restored <c>WAIT</c> re-deferred itself and restarted
    /// its full countdown, and a restored <c>BEHIND</c> could be rejected outright and drop its clearance.
    /// </summary>
    internal static List<ParsedBlock>? StripDeferralGateBlocks(CompoundCommand compound)
    {
        if (compound.Blocks.Count == 0)
        {
            return null;
        }

        var firstBlock = compound.Blocks[0];
        var payloadBlocks = new List<ParsedBlock>();

        if (firstBlock.Condition is GiveWayCondition)
        {
            payloadBlocks.Add(new ParsedBlock(null, firstBlock.Commands));
        }
        else
        {
            // Drop the first WAIT/WAITD instance only, mirroring the dispatch path — a second one in the same block
            // stays part of the payload.
            ParsedCommand? gate = firstBlock.Commands.FirstOrDefault(c => c is WaitCommand or WaitDistanceCommand);
            if (gate is null)
            {
                return null;
            }

            var siblings = firstBlock.Commands.Where(c => c != gate).ToList();
            if (siblings.Count > 0)
            {
                payloadBlocks.Add(new ParsedBlock(firstBlock.Condition, siblings));
            }
        }

        for (int i = 1; i < compound.Blocks.Count; i++)
        {
            payloadBlocks.Add(compound.Blocks[i]);
        }

        return payloadBlocks.Count > 0 ? payloadBlocks : null;
    }

    private static CommandResult? TryDeferGiveWay(CompoundCommand compound, AircraftState aircraft, DispatchContext ctx)
    {
        if (compound.Blocks[0].Condition is not GiveWayCondition gw)
        {
            return null;
        }

        if (ctx.FindAircraft is { } findAircraft && findAircraft(gw.TargetCallsign) is null)
        {
            return new CommandResult(false, $"BEHIND target {gw.TargetCallsign} not found");
        }

        // Strip the condition from the first block; keep the commands and subsequent blocks
        var payloadBlocks = StripDeferralGateBlocks(compound);
        if (payloadBlocks is null)
        {
            return null;
        }

        var payload = new CompoundCommand(payloadBlocks) { SourceText = compound.SourceText };

        var payloadDesc = string.Join(" ; then ", payloadBlocks.Select(b => string.Join(", ", b.Commands.Select(CommandDescriber.DescribeNatural))));

        // The deferred-dispatch carries its own GiveWayTarget gate; no need to mirror
        // it onto aircraft.Ground.Hold during the wait — the aircraft remains under its
        // prior phase control until the condition fires and the payload dispatches.
        aircraft.DeferredDispatches.Add(
            new DeferredDispatch(payload, gw.TargetCallsign) { SourceText = compound.SourceText, IsScenarioScripted = ctx.IsScenarioScripted }
        );
        return new CommandResult(true, $"After {gw.TargetCallsign} passes: {payloadDesc}");
    }

    /// <summary>
    /// Handles command dispatch when aircraft has an active phase.
    /// Returns a result if the command was handled (accepted or rejected),
    /// or null if phases were cleared and normal dispatch should proceed.
    /// </summary>
    private static CommandResult? DispatchWithPhase(CompoundCommand compound, AircraftState aircraft, Phase currentPhase, DispatchContext ctx)
    {
        // Conditional leading blocks (AT FIX, LV altitude, distance-final, on-handoff,
        // ground-entity) defer to the queue's trigger machinery — the wrapped command
        // hasn't actually fired yet, so the active phase must not be torn down based on
        // what the deferred block would do. WAIT / GiveWay are short-circuited earlier
        // via TryDeferLeadingWait / TryDeferGiveWay; this guard covers the other
        // condition types that share the queue/trigger path. Returning null routes the
        // compound through DispatchCompound's normal DryRunValidate + EnqueueBlocks
        // path, where the block gets a BlockTrigger and waits for the trigger to fire.
        if (compound.Blocks[0].Condition is not null)
        {
            return null;
        }

        // Within a parallel block the phase-interactive command drives the phase gate; its
        // phase-transparent siblings (squawk / ident / say / …) are metadata setters that every
        // phase tolerates. Gating on a leading transparent command wrongly rejects the whole block:
        // "SQ, SQNORM, PUSH" at parking loses the IsAllTransparent fast path (PUSH is interactive),
        // and AtParkingPhase.CanAcceptCommand rejects Squawk — even though each command succeeds
        // when issued on its own.
        var gateBlock = compound.Blocks[0];
        int driverIdx = FindPhaseGateDriverIndex(gateBlock);
        var firstCmd = gateBlock.Commands[driverIdx];

        // Bail out immediately for unsupported commands — they must never interact
        // with phases (the old default fallback in ToCanonicalType mapped them to
        // FlyHeading, which triggered ClearsPhase and destroyed pattern state).
        if (firstCmd is UnsupportedCommand unsupported)
        {
            return new CommandResult(false, $"Command not yet supported: {unsupported.RawText}");
        }

        var cmdType = CommandDescriber.ToCanonicalType(firstCmd);

        // Phase-transparent commands: pure status-flag setters (RFIS/RTIS and their forced
        // variants) with no navigation/altitude/speed effect. They must never clear a phase.
        // Returning null routes them through normal dispatch (NavigationCommandHandler).
        if (IsPhaseTransparentCommand(cmdType))
        {
            return null;
        }

        // Sim-control bypass: destructive teleports (WARP/WARPG) that wipe phase/queue/route
        // state inside the handler. The phase gate would otherwise reject them in any phase
        // whose CanAcceptCommand switch doesn't whitelist them — and there's nothing for the
        // gate to protect, since the handler clears everything before applying the warp.
        if (IsSimControlBypass(cmdType))
        {
            return null;
        }

        // Try tower/ground-specific handling first (phase-interactive commands)
        var towerResult = TryApplyTowerCommand(firstCmd, aircraft, currentPhase, ctx);
        if (towerResult is not null)
        {
            if (!towerResult.Success)
            {
                return WithRejectedCommand(towerResult, firstCmd);
            }

            // Dispatch the other parallel commands in the same block (e.g. CLAND after EF 28L,
            // or CROSS after TAXI). Collect every per-command message so the RPO sees the full
            // outcome — without this, CLAND's "Cleared to land 28L" would be silently dropped
            // and the user would think only the EF took effect.
            var messages = new List<string>();
            if (!string.IsNullOrEmpty(towerResult.Message))
            {
                messages.Add(towerResult.Message);
            }
            for (int i = 0; i < gateBlock.Commands.Count; i++)
            {
                if (i == driverIdx)
                {
                    continue;
                }

                var sibling = gateBlock.Commands[i];
                var subResult = ApplyParallelSibling(sibling, aircraft, currentPhase, ctx);
                if (subResult is null)
                {
                    continue;
                }
                if (!subResult.Success)
                {
                    // Subsequent failure on a partially-applied compound: surface it so the RPO
                    // knows the second clause didn't take effect (e.g. EF succeeds but CLAND
                    // fails because the new phase rejects it).
                    var combinedFail =
                        messages.Count > 0
                            ? $"{string.Join(", ", messages)}; but {subResult.Message}"
                            : subResult.Message ?? "Subsequent command failed";
                    return WithRejectedCommand(new CommandResult(false, combinedFail), sibling);
                }
                if (!string.IsNullOrEmpty(subResult.Message))
                {
                    messages.Add(subResult.Message);
                }
            }

            return messages.Count <= 1 ? towerResult : towerResult with { Message = string.Join(", ", messages) };
        }

        // Check standard command acceptance against the current phase
        var acceptance = currentPhase.CanAcceptCommand(cmdType);

        if (acceptance.IsRejected)
        {
            var reason = acceptance.Reason ?? $"Cannot accept {CommandDescriber.DescribeNatural(firstCmd)} during {currentPhase.Name}";
            return WithRejectedCommand(new CommandResult(false, reason), firstCmd);
        }

        if (acceptance.ClearsThePhase)
        {
            // Don't clear phases yet — return a sentinel so DispatchCompound can validate
            // the command first. If validation fails, phases stay intact.
            return PhaseShouldBeCleared;
        }

        // Allowed but not a tower command — phase notification is deferred to
        // <see cref="BuildApplyAction"/> after a successful apply so a later
        // validation/apply failure does not release internal state (e.g. RV SID hold).
        return null;
    }

    /// <summary>
    /// True when a parsed command is phase-transparent — a pure transponder/metadata setter
    /// (squawk, ident, say, scratchpad, …) that no phase needs to gate. Guards
    /// <see cref="UnsupportedCommand"/>, which <see cref="CommandDescriber.ToCanonicalType"/> throws on.
    /// </summary>
    private static bool IsTransparentCommand(ParsedCommand cmd) =>
        cmd is not UnsupportedCommand && CommandDescriber.IsPhaseTransparent(CommandDescriber.ToCanonicalType(cmd));

    /// <summary>
    /// Index of the command in a parallel block that is checked against the active phase's
    /// <see cref="Phase.CanAcceptCommand"/> — the first phase-interactive (non-transparent) command.
    /// Transparent siblings must not drive the gate: a block reaches <see cref="DispatchWithPhase"/>
    /// only because it holds at least one non-transparent command, so gating on a leading transparent
    /// one makes every phase that doesn't whitelist it (e.g. <c>AtParkingPhase</c> vs <c>Squawk</c>)
    /// reject the whole block. Falls back to 0 for an all-transparent block — unreachable in practice,
    /// since <see cref="IsAllTransparent"/> claims those first.
    /// </summary>
    private static int FindPhaseGateDriverIndex(ParsedBlock block)
    {
        for (int i = 0; i < block.Commands.Count; i++)
        {
            if (!IsTransparentCommand(block.Commands[i]))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// Applies one non-driver command of a parallel block after the driver was applied via the tower
    /// path. Transparent siblings never reach a tower handler, so route them through
    /// <see cref="ApplyCommand"/> — otherwise <c>PUSH, SQ 0233</c> silently drops the squawk. Returns
    /// null when the sibling has no handler in this context, preserving the skip-and-continue
    /// behaviour for non-tower, non-transparent commands.
    /// </summary>
    private static CommandResult? ApplyParallelSibling(ParsedCommand sibling, AircraftState aircraft, Phase currentPhase, DispatchContext ctx)
    {
        if (IsTransparentCommand(sibling))
        {
            // Mirror ApplyTransparentCompound: transparent commands bypass DCT-fix validation.
            return ApplyCommand(sibling, aircraft, ctx with { ValidateDctFixes = false });
        }

        return TryApplyTowerCommand(sibling, aircraft, aircraft.Phases?.CurrentPhase ?? currentPhase, ctx);
    }

    /// <summary>
    /// Notifies the active phase that a command was accepted without clearing it.
    /// Used on immediate dispatch (<see cref="DispatchWithPhase"/>) and when a queued
    /// block fires (<see cref="BuildApplyAction"/>).
    ///
    /// The Unsupported / phase-transparent / sim-control-bypass guards below are
    /// load-bearing for the <see cref="BuildApplyAction"/> path — queued blocks reach
    /// this helper without the pre-filtering that <see cref="DispatchWithPhase"/>
    /// applies earlier (<see cref="UnsupportedCommand"/> reject at the top, then
    /// <see cref="IsPhaseTransparentCommand"/> and <see cref="IsSimControlBypass"/>
    /// short-circuits). For the immediate-dispatch caller they are redundant but
    /// harmless; do not remove them without also collapsing the BuildApplyAction
    /// invocation back into its own filter.
    /// </summary>
    private static void NotifyPhaseCommandAccepted(AircraftState aircraft, ParsedCommand cmd, Phase currentPhase, DispatchContext ctx)
    {
        if (cmd is UnsupportedCommand)
        {
            return;
        }

        var cmdType = CommandDescriber.ToCanonicalType(cmd);
        if (IsPhaseTransparentCommand(cmdType) || IsSimControlBypass(cmdType))
        {
            return;
        }

        var acceptance = currentPhase.CanAcceptCommand(cmdType);
        if (acceptance.IsRejected || acceptance.ClearsThePhase)
        {
            return;
        }

        currentPhase.OnCommandAccepted(cmdType, BuildMinimalContext(aircraft, ctx.GroundLayout));
    }

    private static bool IsPhaseTransparentCommand(CanonicalCommandType cmd) =>
        cmd switch
        {
            CanonicalCommandType.ReportFieldInSight => true,
            CanonicalCommandType.ReportFieldInSightForced => true,
            CanonicalCommandType.ReportTrafficInSight => true,
            CanonicalCommandType.ReportTrafficInSightForced => true,
            CanonicalCommandType.Report => true,
            CanonicalCommandType.SafetyAlert => true,
            CanonicalCommandType.WakeAdvisory => true,
            // NODEL is a pure controller bookkeeping toggle (flips AutoDeleteExempt /
            // strips queued ONHS DEL blocks); it has no nav/altitude/speed effect and
            // is meaningful in every phase, so bypass the phase gate.
            CanonicalCommandType.CancelAutoDelete => true,
            _ => false,
        };

    private static bool IsSimControlBypass(CanonicalCommandType cmd) =>
        cmd switch
        {
            CanonicalCommandType.Warp => true,
            CanonicalCommandType.WarpGround => true,
            _ => false,
        };

    private static CommandResult? TryApplyTowerCommand(ParsedCommand command, AircraftState aircraft, Phase currentPhase, DispatchContext ctx)
    {
        var result = TryApplyTowerCommandCore(command, aircraft, currentPhase, ctx);
        EmitProcedureAdvisory(result, aircraft, ctx);
        return result;
    }

    private static CommandResult? TryApplyTowerCommandCore(ParsedCommand command, AircraftState aircraft, Phase currentPhase, DispatchContext ctx)
    {
        var groundLayout = ctx.GroundLayout;
        var autoCrossRunway = ctx.AutoCrossRunway;

        // Hold-for-release runway-entry gate: a held departure may not enter the runway (LUAW) or
        // take off (CTO/CTOPP) until released. It stays holding short. Cleared by REL/CTOA.
        if (aircraft.Ground.HeldForRelease && command is ClearedForTakeoffCommand or ClearedTakeoffPresentCommand or LineUpAndWaitCommand)
        {
            return new CommandResult(
                false,
                $"{aircraft.Callsign} is held for release at {aircraft.FlightPlan.Departure} — REL {aircraft.Callsign} first"
            );
        }

        // Cache the SID's published initial-altitude cap so an IFR departure with no commanded
        // altitude holds it through the initial climb (issue #187). Resolved here where the ARTCC
        // TDLS config is in scope; consumed later by InitialClimbPhase.ResolveTargetAltitude.
        if (command is ClearedForTakeoffCommand or LineUpAndWaitCommand or ClearedTakeoffPresentCommand)
        {
            DepartureClearanceHandler.StoreSidInitialAltitude(aircraft, ctx.ArtccConfig);
        }

        switch (command)
        {
            case ClearedForTakeoffCommand cto:
            {
                var ctoResult = currentPhase is LinedUpAndWaitingPhase luaw
                    ? DepartureClearanceHandler.TryClearedForTakeoff(cto, aircraft, luaw)
                    : DepartureClearanceHandler.TryDepartureClearance(
                        aircraft,
                        currentPhase,
                        ClearanceType.ClearedForTakeoff,
                        cto.Departure,
                        cto.AssignedAltitude,
                        ctx,
                        Log
                    );
                // "Cleared for immediate takeoff" — brisk lineup taxi (+ rolling takeoff via the
                // existing rolling/upgrade machinery). Latest clearance's modifier wins.
                if (ctoResult.Success)
                {
                    aircraft.Ground.IsExpeditingLineup = cto.Immediate;
                }
                return ctoResult;
            }

            case CancelTakeoffClearanceCommand:
                // Cancelling the takeoff clearance moots any pending expedite intent.
                aircraft.Ground.IsExpeditingLineup = false;
                return DepartureClearanceHandler.TryCancelTakeoff(aircraft, currentPhase);

            case LineUpAndWaitCommand luawCmd:
            {
                var luawResult = DepartureClearanceHandler.TryDepartureClearance(
                    aircraft,
                    currentPhase,
                    ClearanceType.LineUpAndWait,
                    new DefaultDeparture(),
                    null,
                    ctx,
                    Log
                );
                // "Line up and wait, without delay" — brisk lineup taxi; still stops at the centerline.
                if (luawResult.Success)
                {
                    aircraft.Ground.IsExpeditingLineup = luawCmd.WithoutDelay;
                }
                return luawResult;
            }

            case ClearedToLandCommand ctl:
                return PatternCommandHandler.TryClearedToLand(ctl, aircraft, ctx);

            case ForceLandingCommand flc:
                return PatternCommandHandler.TryForceLanding(flc, aircraft, ctx);

            case LandAndHoldShortCommand lahso:
                return PatternCommandHandler.TryLandAndHoldShort(lahso, aircraft, groundLayout, ctx);

            case CancelLandingClearanceCommand:
                return PatternCommandHandler.TryCancelLandingClearance(aircraft);

            case GoAroundCommand ga:
                // Tower commands deliberately run before the phase acceptance gate so a clearance still applies
                // during phases that would otherwise be cleared by it. That bypass also swallowed the phases which
                // explicitly REJECT a go-around — the post-touchdown energy gate, a helicopter departure, an
                // already-rejected landing — leaving those safety gates unreachable in production. Honour an explicit
                // GA rejection here; every other tower command keeps its bypass.
                if (currentPhase.CanAcceptCommand(CanonicalCommandType.GoAround) is { IsRejected: true } gaGate)
                {
                    return new CommandResult(false, gaGate.Reason ?? "unable to go around from the current phase");
                }

                return PatternCommandHandler.TryGoAround(ga, aircraft, ctx.GroundLayout);

            // Pattern entry commands
            case EnterLeftDownwindCommand eld:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Downwind,
                    runwayId: eld.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case EnterRightDownwindCommand erd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Downwind,
                    runwayId: erd.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case EnterLeftCrosswindCommand elc:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Crosswind,
                    runwayId: elc.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case EnterRightCrosswindCommand erc:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Crosswind,
                    runwayId: erc.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
            case EnterLeftBaseCommand elb:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Base,
                    runwayId: elb.RunwayId,
                    finalDistanceNm: elb.FinalDistanceNm,
                    groundLayout: ctx.GroundLayout
                );
            case EnterRightBaseCommand erb:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Base,
                    runwayId: erb.RunwayId,
                    finalDistanceNm: erb.FinalDistanceNm,
                    groundLayout: ctx.GroundLayout
                );
            case EnterFinalCommand ef:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    // EF has no L/R in its verb — let TryEnterPattern infer from runway
                    // (28R parallel to 28L → Right, single runway → Left).
                    requestedDirection: null,
                    PatternEntryLeg.Final,
                    runwayId: ef.RunwayId,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );

            // Pattern modification commands
            case MakeLeftTrafficCommand mlt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, mlt.RunwayId, mlt.Altitude, ctx.GroundLayout);
            case MakeRightTrafficCommand mrt:
                return PatternCommandHandler.TryChangePatternDirection(
                    aircraft,
                    PatternDirection.Right,
                    mrt.RunwayId,
                    mrt.Altitude,
                    ctx.GroundLayout
                );
            case TurnCrosswindCommand:
                return PatternCommandHandler.TryPatternTurnTo<UpwindPhase>(aircraft, "crosswind");
            case TurnDownwindCommand:
                return PatternCommandHandler.TryPatternTurnTo<CrosswindPhase>(aircraft, "downwind");
            case TurnBaseCommand:
                return PatternCommandHandler.TryPatternTurnBase(aircraft);
            case ExtendPatternCommand ext:
                return PatternCommandHandler.TryExtendPattern(aircraft, ext.Leg, ctx.GroundLayout);
            case MakeShortApproachCommand:
                return PatternCommandHandler.TryMakeShortApproach(aircraft);
            case MakeLeft360Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Left, 360);
            case MakeRight360Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Right, 360);
            case MakeLeft270Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Left, 270);
            case MakeRight270Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Right, 270);
            case CircleAirportCommand:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, null, null, ctx.GroundLayout);
            case PatternSizeCommand ps:
                return PatternCommandHandler.TrySetPatternSize(aircraft, ps.SizeNm, ctx.GroundLayout);
            case MakeNormalApproachCommand:
                return PatternCommandHandler.TryMakeNormalApproach(aircraft);
            case Cancel270Command:
                return PatternCommandHandler.TryCancel270(aircraft);
            case MakeLeftSTurnsCommand mls:
                return PatternCommandHandler.TryMakeSTurns(aircraft, TurnDirection.Left, mls.Count);
            case MakeRightSTurnsCommand mrs:
                return PatternCommandHandler.TryMakeSTurns(aircraft, TurnDirection.Right, mrs.Count);
            case OffsetLeftPatternCommand ofl:
                return PatternCommandHandler.TryOffsetPattern(aircraft, TurnDirection.Left, ofl.OffsetNm);
            case OffsetRightPatternCommand ofr:
                return PatternCommandHandler.TryOffsetPattern(aircraft, TurnDirection.Right, ofr.OffsetNm);
            case Plan270Command:
                return PatternCommandHandler.TryPlan270(aircraft);

            // Option approach / special ops commands
            case TouchAndGoCommand tg:
                return PatternCommandHandler.TrySetupTouchAndGo(aircraft, tg.TrafficPattern, ctx);
            case StopAndGoCommand sg:
                return PatternCommandHandler.TrySetupStopAndGo(aircraft, sg.TrafficPattern, ctx);
            case LowApproachCommand la:
                return PatternCommandHandler.TrySetupLowApproach(aircraft, la.TrafficPattern, ctx);
            case ClearedForOptionCommand opt:
                return PatternCommandHandler.TrySetupClearedForOption(aircraft, opt.TrafficPattern, ctx);

            // Hold commands
            case HoldPresentPosition360Command hpp:
                return PatternCommandHandler.TryHoldPresentPosition(aircraft, hpp.Direction);
            case HoldPresentPositionHoverCommand:
                return PatternCommandHandler.TryHoldPresentPosition(aircraft, null);
            case HoldAtFixOrbitCommand hfix:
                return PatternCommandHandler.TryHoldAtFix(aircraft, hfix.FixName, hfix.Lat, hfix.Lon, hfix.Direction);
            case HoldAtFixHoverCommand hfixH:
                return PatternCommandHandler.TryHoldAtFix(aircraft, hfixH.FixName, hfixH.Lat, hfixH.Lon, null);

            // A helicopter air-taxiing or relocating is held with HPP (hover present position),
            // which routes through the hold-command cases above into a VfrHold hover; to continue
            // the relocation the controller re-issues ATXI/LAND @spot. The ground HOLD/RES verbs
            // don't apply to an airborne heli — they fall through to TryHoldPosition/TryResumeTaxi,
            // which reject with an on-the-ground message.
            case ResumeCommand hsResume
                when currentPhase
                    is HoldingShortPhase { HoldShort.Reason: HoldShortReason.ExplicitHoldShort or HoldShortReason.RunwayCrossing } holdShort:
            {
                var applied = GroundCommandHandler.TryApplyRouteCrossingsAndHoldShorts(
                    aircraft,
                    groundLayout,
                    hsResume.CrossRunways,
                    hsResume.HoldShorts
                );
                if (!applied.Success)
                {
                    return applied;
                }
                holdShort.SatisfyClearance(ClearanceType.RunwayCrossing);
                return Ok(CommandDescriber.DescribeNatural(hsResume));
            }

            // Helicopter commands
            case AirTaxiCommand atxi:
                return GroundCommandHandler.TryAirTaxi(aircraft, atxi.Destination, groundLayout);
            case LandCommand land:
                return GroundCommandHandler.TryLand(aircraft, land, groundLayout);

            case ClearedTakeoffPresentCommand ctopp:
                return DepartureClearanceHandler.TryClearedTakeoffPresent(ctopp, aircraft, groundLayout);

            // Ground commands
            case PushbackCommand push:
                return GroundCommandHandler.TryPushback(aircraft, push, groundLayout);
            case TaxiCommand taxi:
                return GroundCommandHandler.TryTaxi(aircraft, taxi, groundLayout, autoCrossRunway);
            case TaxiAutoCommand autoTaxi:
                return GroundCommandHandler.TryTaxiAuto(aircraft, autoTaxi, groundLayout, autoCrossRunway);
            case HoldPositionCommand:
                return GroundCommandHandler.TryHoldPosition(aircraft);
            case ResumeCommand groundResume when currentPhase is not HoldingShortPhase:
            {
                var applied = GroundCommandHandler.TryApplyRouteCrossingsAndHoldShorts(
                    aircraft,
                    groundLayout,
                    groundResume.CrossRunways,
                    groundResume.HoldShorts
                );
                if (!applied.Success)
                {
                    return applied;
                }
                var resumeResult = GroundCommandHandler.TryResumeTaxi(aircraft);
                if (!resumeResult.Success)
                {
                    return resumeResult;
                }
                return Ok(CommandDescriber.DescribeNatural(groundResume));
            }
            case CrossRunwayCommand cross:
                return GroundCommandHandler.TryCrossRunway(aircraft, cross, groundLayout);
            case HoldShortCommand hs:
                return GroundCommandHandler.TryHoldShort(aircraft, hs, groundLayout);
            case AssignRunwayCommand assignRwy:
                return GroundCommandHandler.TryAssignRunway(aircraft, assignRwy.RunwayId);
            case FollowGroundCommand followG:
                return GroundCommandHandler.TryFollow(aircraft, followG, groundLayout, ctx.FindAircraft);
            case GiveWayCommand gw:
                return GroundCommandHandler.TryGiveWay(aircraft, gw.TargetCallsign);
            case ExitLeftCommand el:
                return GroundCommandHandler.TryExitCommand(
                    aircraft,
                    new ExitPreference { Side = ExitSide.Left, Taxiway = el.Taxiway },
                    el.NoDelete,
                    el.Expedite
                );
            case ExitRightCommand er:
                return GroundCommandHandler.TryExitCommand(
                    aircraft,
                    new ExitPreference { Side = ExitSide.Right, Taxiway = er.Taxiway },
                    er.NoDelete,
                    er.Expedite
                );
            case ExitTaxiwayCommand et:
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Taxiway = et.Taxiway }, et.NoDelete, et.Expedite);

            case BreakConflictCommand:
                return GroundCommandHandler.TryBreakConflict(aircraft);
            case ClearRunwayCommand:
                return GroundCommandHandler.TryClearRunway(aircraft, groundLayout);
            case GoCommand:
                return GroundCommandHandler.TryGo(aircraft);

            // TAXIALL is dispatched at the engine level, not per-aircraft
            case TaxiAllCommand:
                return new CommandResult(false, "TAXIALL must be dispatched at the engine level");

            default:
                return null;
        }
    }

    internal static string ResolveAirport(AircraftState aircraft)
    {
        // Try destination airport from flight plan
        if (!string.IsNullOrWhiteSpace(aircraft.FlightPlan.Destination))
        {
            string dest = aircraft.FlightPlan.Destination;
            return dest.StartsWith('K') && dest.Length == 4 ? dest[1..] : dest;
        }

        // Try assigned runway's airport
        if (aircraft.Phases?.AssignedRunway is { } rwy)
        {
            string apt = rwy.AirportId;
            return apt.StartsWith('K') && apt.Length == 4 ? apt[1..] : apt;
        }

        return "";
    }

    /// <summary>
    /// Replace the first pending approach-ending phase (LandingPhase, HelicopterLandingPhase,
    /// TouchAndGoPhase, StopAndGoPhase, or LowApproachPhase) with the
    /// given replacement. Returns true if a replacement was made.
    /// </summary>
    internal static bool ReplaceApproachEnding(PhaseList phases, Phase replacement)
    {
        for (int i = 0; i < phases.Phases.Count; i++)
        {
            var phase = phases.Phases[i];
            if (phase.Status != PhaseStatus.Pending)
            {
                continue;
            }

            if (phase is LandingPhase or HelicopterLandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase)
            {
                phases.Phases[i] = replacement;
                return true;
            }
        }

        return false;
    }

    internal static PhaseContext BuildMinimalContext(AircraftState aircraft, AirportGroundLayout? groundLayout = null)
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        var runway = aircraft.Phases?.AssignedRunway;
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = cat,
            DeltaSeconds = 0,
            Runway = runway,
            FieldElevation = runway?.ElevationFt ?? ResolveFieldElevation(aircraft, groundLayout),
            GroundLayout = groundLayout,
            Logger = Log,
        };
    }

    // Climb margin for re-arming the altitude target after a phase clear; mirrors the FlightPhysics
    // altitude snap so a target the aircraft has effectively reached is not re-armed.
    private const double PhaseClearClimbMarginFt = 10.0;

    /// <summary>
    /// A phase-clearing command (e.g. <c>FH</c> issued during a climb phase) is a lateral instruction;
    /// it does not cancel the aircraft's altitude clearance. The cleared phase may have been driving the
    /// climb through an internal target (<c>TakeoffPhase</c> climbs to ~400 ft AGL before handing off,
    /// <c>InitialClimbPhase</c> climbs to the assigned altitude), which would otherwise leave the aircraft
    /// levelling off there. A go-around or missed-approach climb is exempt entirely (see <paramref name="clearedGoAround"/>):
    /// <c>GoAroundPhase</c> climbs to the published missed-approach altitude — the aircraft's real clearance — while
    /// <c>AssignedAltitude</c> still holds the stale approach clearance, so re-arming would either lower the climb (MAP
    /// above the approach clearance) or overshoot it (MAP below); the MAP target is left untouched. Otherwise re-arm the
    /// climb to the last assigned altitude only when the cleared phase was actively climbing (its managed target above
    /// the current altitude), the phase target is at or below the assigned altitude (a guard that never lowers the
    /// target), and the assigned altitude is still above the aircraft. Descents and level-offs are left
    /// untouched — once an aircraft leaves an altitude
    /// it does not climb back without a new clearance (FAA last-assigned-altitude doctrine), so an aircraft
    /// vectored off a descent/approach below its last assigned altitude must hold present altitude, not
    /// climb back up. A command that carries its own altitude applies after this and wins.
    /// </summary>
    internal static void ResumeAssignedAltitudeAfterPhaseClear(AircraftState aircraft, bool clearedGoAround)
    {
        if (aircraft.IsOnGround)
        {
            return;
        }

        // A go-around / missed-approach climb owns its altitude target: GoAroundPhase climbs to the
        // published missed-approach altitude (the real clearance), while AssignedAltitude still holds
        // the stale approach clearance. Re-arming to it would lower the climb (MAP above the approach
        // clearance) or overshoot the MAP (MAP below) — both wrong. Leave the MAP target untouched.
        if (clearedGoAround)
        {
            return;
        }

        if (aircraft.Targets.AssignedAltitude is not { } assigned)
        {
            return;
        }

        if (aircraft.Targets.TargetAltitude is not { } phaseTarget)
        {
            return;
        }

        // The phase was climbing only if its managed target was above the current altitude; this
        // excludes descents/approaches (target at or below current), where re-arming the assigned
        // altitude would command an un-cleared climb back up. The phase-target-at-or-below-assigned
        // guard keeps this from ever lowering a climb target (go-around MAP climbs are handled above).
        bool phaseWasClimbing = phaseTarget > aircraft.Altitude + PhaseClearClimbMarginFt;
        if (phaseWasClimbing && (phaseTarget <= assigned) && (assigned > aircraft.Altitude + PhaseClearClimbMarginFt))
        {
            aircraft.Targets.TargetAltitude = assigned;
        }
    }

    /// <summary>
    /// Field elevation (ft MSL) for an aircraft without an assigned runway — parked, taxiing, or a
    /// helicopter air-taxi / relocation with no runway. Resolves the operating airport's elevation
    /// rather than defaulting to 0 MSL, so a heli air-taxiing to a helipad descends to field level
    /// (at a non-sea-level airport, nowhere near 0). Prefers <see cref="AircraftState.AirportId"/>
    /// (the stable scenario-set operational airport), then the ground-layout airport, then the
    /// flight-plan departure/destination.
    /// </summary>
    internal static double ResolveFieldElevation(AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        var navDb = NavigationDatabase.Instance;
        if (aircraft.AirportId is { Length: > 0 } operatingAirport && navDb.GetAirportElevation(operatingAirport) is { } opElev)
        {
            return opElev;
        }
        if (groundLayout?.AirportId is { Length: > 0 } layoutAirport && navDb.GetAirportElevation(layoutAirport) is { } layoutElev)
        {
            return layoutElev;
        }
        if (aircraft.FlightPlan.Departure is { Length: > 0 } departure && navDb.GetAirportElevation(departure) is { } depElev)
        {
            return depElev;
        }
        if (aircraft.FlightPlan.Destination is { Length: > 0 } destination && navDb.GetAirportElevation(destination) is { } destElev)
        {
            return destElev;
        }
        return 0;
    }

    internal static RunwayInfo? ResolveRunway(AircraftState aircraft, string runwayId)
    {
        var navDb = NavigationDatabase.Instance;

        // An aircraft physically on the ground departs/taxis on the airport its wheels are on —
        // never on a filed destination. Prefer the physical/operational airport (mirrors
        // SimulationEngine.ResolveGroundLayout) before the flight-plan fields, so a VFR plan filed
        // with only a destination (e.g. KAPC while parked at OAK) does not send the runway lookup to
        // the wrong airport and reject CTO/RWY/TAXI-to-runway. Empty strings are treated as null.
        var airportId =
            aircraft.Phases?.AssignedRunway?.AirportId is { Length: > 0 } assignedApt ? assignedApt
            : aircraft.AirportId is { Length: > 0 } operatingApt ? operatingApt
            : aircraft.Ground.Layout?.AirportId is { Length: > 0 } layoutApt ? layoutApt
            : aircraft.FlightPlan.Departure is { Length: > 0 } dep ? dep
            : aircraft.FlightPlan.Destination is { Length: > 0 } dest ? dest
            : null;

        if (airportId is null)
        {
            return null;
        }

        // Hold-short runway IDs can be combined (e.g., "28R/10L").
        // Try each end until one resolves.
        var parsed = RunwayIdentifier.Parse(runwayId);
        var result = navDb.GetRunway(airportId, parsed.End1) ?? navDb.GetRunway(airportId, parsed.End2);
        if (result is null)
        {
            Log.LogWarning(
                "Runway lookup failed for {Aircraft}: runway '{RunwayId}' not found at {Airport} (tried '{End1}' and '{End2}')",
                aircraft.Callsign,
                runwayId,
                airportId,
                parsed.End1,
                parsed.End2
            );
        }

        return result;
    }

    internal static string RunwayLabel(AircraftState aircraft)
    {
        var runway = aircraft.Phases?.AssignedRunway;
        return runway is not null ? $", Runway {RunwayIdentifier.ToDisplayDesignator(runway.Designator)}" : "";
    }

    internal static GroundNode? FindTaxiwayIntersection(AirportGroundLayout layout, string taxiway1, string taxiway2)
    {
        foreach (var node in layout.Nodes.Values)
        {
            bool hasTwy1 = false;
            bool hasTwy2 = false;
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway(taxiway1))
                {
                    hasTwy1 = true;
                }

                if (edge.MatchesTaxiway(taxiway2))
                {
                    hasTwy2 = true;
                }

                if (hasTwy1 && hasTwy2)
                {
                    return node;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The fixes a direct-to style command routes to, or null when the command is not one.
    /// Every shape that sends an aircraft direct to a fix belongs here, so a single guard covers
    /// all of them.
    /// </summary>
    private static IReadOnlyList<ResolvedFix>? DirectToFixes(ParsedCommand command) =>
        command switch
        {
            DirectToCommand c => c.Fixes,
            ForceDirectToCommand c => c.Fixes,
            ConstrainedForceDirectToCommand c => c.Fixes,
            AppendDirectToCommand c => c.Fixes,
            AppendForceDirectToCommand c => c.Fixes,
            TurnLeftDirectToCommand c => c.Fixes,
            TurnRightDirectToCommand c => c.Fixes,
            _ => null,
        };

    internal static CommandResult Ok(string message)
    {
        return new CommandResult(true, message);
    }

    private static CommandResult ApplyBlock(CommandBlock block, AircraftState aircraft)
    {
        block.IsApplied = true;
        var result = block.ApplyAction?.Invoke(aircraft);

        if (result is not null && !result.Success)
        {
            return result;
        }

        if (result?.Message is not null)
        {
            block.NaturalDescription = result.Message;
        }

        foreach (var cmd in block.Commands)
        {
            if (cmd.Type == TrackedCommandType.Immediate)
            {
                cmd.IsComplete = true;
            }
        }

        return result ?? new CommandResult(true);
    }

    /// <summary>
    /// Builds CommandBlocks from parsed blocks starting at <paramref name="startIndex"/>
    /// and appends them to the aircraft's command queue. Returns natural-language messages
    /// for each enqueued block.
    /// </summary>
    /// <summary>
    /// Removes pending queue blocks whose dimensions conflict with the incoming command,
    /// preserving non-conflicting blocks. For the current applied block, marks conflicting
    /// tracked commands as complete (superseded). Returns the preserved blocks (removed
    /// from the queue) so the caller can re-append them after enqueueing new blocks.
    /// <paramref name="droppedDescriptions"/> receives a description of every pending block
    /// that was lost outright (full conflict or fast-path wipe), so callers can warn the
    /// RPO. Partial splits — where some commands in a block were preserved — are NOT
    /// reported, since the queued instruction survived in modified form. Already-applied
    /// blocks are likewise NOT reported: their effect already took hold (e.g. a chain of
    /// CFIX crossing restrictions stamped on the route, or an earlier DM/SPEED), so
    /// superseding them is not a loss the RPO needs to re-issue.
    /// </summary>
    private static List<CommandBlock> ClearConflictingBlocks(
        AircraftState aircraft,
        CommandDimension incomingDimensions,
        DispatchContext ctx,
        bool preserveTriggeredBlocks,
        out List<string> droppedDescriptions
    )
    {
        var queue = aircraft.Queue;
        droppedDescriptions = [];

        // Fast path: All/None → clear everything (original behavior). Skipped when
        // preserving triggered blocks (a firing deferral must keep pending conditionals)
        // so the per-block loop below can spare them.
        if (
            !preserveTriggeredBlocks
            && ((incomingDimensions & CommandDimension.All) == CommandDimension.All || incomingDimensions == CommandDimension.None)
        )
        {
            int fastStart = queue.CurrentBlockIndex + (queue.CurrentBlock is { IsApplied: true } ? 1 : 0);
            for (int i = fastStart; i < queue.Blocks.Count; i++)
            {
                if (!queue.Blocks[i].IsApplied)
                {
                    droppedDescriptions.Add(DescribeQueueBlock(queue.Blocks[i]));
                }
            }
            queue.Blocks.Clear();
            queue.CurrentBlockIndex = 0;
            return [];
        }

        // Mark conflicting tracked commands in the current applied block as complete (superseded)
        var current = queue.CurrentBlock;
        if (current is { IsApplied: true })
        {
            foreach (var cmd in current.Commands)
            {
                if (!cmd.IsComplete && (CommandDescriber.GetDimension(cmd.Type) & incomingDimensions) != 0)
                {
                    cmd.IsComplete = true;
                }
            }
        }

        // Partition pending blocks into preserved vs removed
        int pendingStart = queue.CurrentBlockIndex + (current is { IsApplied: true } ? 1 : 0);
        var preserved = new List<CommandBlock>();

        for (int i = pendingStart; i < queue.Blocks.Count; i++)
        {
            var block = queue.Blocks[i];

            // A firing deferral preserves pending conditionals verbatim — only fresh
            // immediate commands supersede triggered blocks.
            if (preserveTriggeredBlocks && block.Trigger is not null)
            {
                preserved.Add(block);
                continue;
            }

            var split = SplitBlockNonConflicting(block, incomingDimensions, ctx);
            if (split is null)
            {
                // Already-applied blocks have delivered their effect; superseding them is not a
                // loss (e.g. a chain of CFIX restrictions already stamped on the route), so drop
                // them silently. Only not-yet-applied queued work is reported as lost.
                if (!block.IsApplied)
                {
                    droppedDescriptions.Add(DescribeQueueBlock(block));
                }
            }
            else
            {
                preserved.Add(split);
            }
        }

        // Remove all pending blocks from the queue
        if (pendingStart < queue.Blocks.Count)
        {
            queue.Blocks.RemoveRange(pendingStart, queue.Blocks.Count - pendingStart);
        }

        return preserved;
    }

    private static string DescribeQueueBlock(CommandBlock block) =>
        !string.IsNullOrEmpty(block.Description) ? block.Description : block.NaturalDescription;

    /// <summary>
    /// Append a "queue cleared" warning to <paramref name="aircraft"/>'s PendingWarnings
    /// when the dispatcher silently dropped one or more queued blocks. The warning lists
    /// what was lost so an RPO can re-issue any instructions that mattered.
    ///
    /// Suppresses dropped blocks whose description equals one of the blocks in the incoming
    /// compound — those will be re-enqueued by the same dispatch and aren't actually lost.
    /// This makes re-sending an identical compound silent rather than emitting a spurious
    /// "lost: …" warning that names exactly the blocks the user just re-issued.
    /// </summary>
    private static void EmitQueueClearWarning(AircraftState aircraft, IReadOnlyList<string> dropped, CompoundCommand compound)
    {
        if (dropped.Count == 0)
        {
            return;
        }

        var incoming = ComputeIncomingBlockDescriptions(compound);
        var trulyLost = dropped.Where(d => !incoming.Contains(d)).ToList();
        if (trulyLost.Count == 0)
        {
            return;
        }

        var src = compound.SourceText ?? CommandDescriber.DescribeNatural(compound.Blocks[0].Commands[0]);
        var lost = string.Join(", ", trulyLost);
        aircraft.PendingWarnings.Add($"{aircraft.Callsign} queue cleared by {src} (lost: {lost})");
    }

    /// <summary>
    /// Mirrors the block-description format produced by <see cref="EnqueueBlocks"/> so the
    /// "queue cleared" warning can suppress entries that the same dispatch is about to
    /// re-enqueue. Keep this in sync with the <c>blockDesc</c> construction there.
    /// </summary>
    private static HashSet<string> ComputeIncomingBlockDescriptions(CompoundCommand compound)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pb in compound.Blocks)
        {
            var blockDesc = string.Join(", ", pb.Commands.Select(CommandDescriber.DescribeCommand));
            blockDesc = pb.Condition switch
            {
                LevelCondition lv => $"at {lv.Altitude}ft: {blockDesc}",
                AtFixCondition at => $"at {FormatAtLabel(at)}: {blockDesc}",
                AtGroundEntityCondition ge => $"at {FormatGroundLabel(ge)}: {blockDesc}",
                GiveWayCondition gw => $"giveway {gw.TargetCallsign}: {blockDesc}",
                DistanceFinalCondition df => $"at {df.DistanceNm}nm final: {blockDesc}",
                _ => blockDesc,
            };
            set.Add(blockDesc);
        }
        return set;
    }

    /// <summary>
    /// Returns a version of the block with only the non-conflicting commands, or null
    /// if all commands conflict. If no commands conflict, returns the original block.
    /// For partial conflicts, rebuilds the block from the remaining ParsedCommands.
    /// </summary>
    /// <summary>
    /// The condition label a queued block's descriptions are prefixed with — <c>("at OAK: ", "At OAK: ")</c> for
    /// <c>AT OAK …</c>, both empty for an unconditional block. Carried as one value so <see cref="CreateBlock"/> stays
    /// inside the positional-parameter budget.
    /// </summary>
    private readonly record struct BlockLabels(string DescriptionPrefix, string NaturalPrefix);

    /// <summary>
    /// Maps a block's parsed condition to the label its descriptions are prefixed with. The sole source of those
    /// prefixes: <see cref="CreateBlock"/> stores them on the block so a supersede-split can re-apply them verbatim
    /// rather than re-deriving them from the (lossy) <see cref="BlockTrigger"/>.
    /// </summary>
    private static BlockLabels BuildConditionLabels(BlockCondition? condition)
    {
        switch (condition)
        {
            case LevelCondition lv:
                return new BlockLabels($"at {lv.Altitude}ft: ", $"At {lv.Altitude:N0} ft: ");
            case AtFixCondition at:
            {
                var atLabel = FormatAtLabel(at);
                return new BlockLabels($"at {atLabel}: ", $"At {atLabel}: ");
            }
            case AtGroundEntityCondition ge:
            {
                var geLabel = FormatGroundLabel(ge);
                return new BlockLabels($"at {geLabel}: ", $"At {geLabel}: ");
            }
            case GiveWayCondition gw:
                return new BlockLabels($"giveway {gw.TargetCallsign}: ", $"After {gw.TargetCallsign} passes: ");
            case DistanceFinalCondition df:
                return new BlockLabels($"at {df.DistanceNm}nm final: ", $"At {df.DistanceNm}nm final: ");
            case OnHoldShortCondition:
                return new BlockLabels("on hold-short: ", "Once holding short: ");
            case OnHandoffCondition:
                return new BlockLabels("on handoff: ", "On handoff: ");
            default:
                return new BlockLabels("", "");
        }
    }

    /// <summary>
    /// The single construction point for queued <see cref="CommandBlock"/>s. Everything derivable from the block's
    /// parsed commands — <see cref="CommandBlock.Commands"/>, <see cref="CommandBlock.Dimensions"/>,
    /// <see cref="CommandBlock.HasTrackCommand"/>, <see cref="CommandBlock.IsWaitBlock"/>, and the command list behind
    /// <see cref="CommandBlock.ApplyAction"/> — is derived here, so the enqueue path and the supersede-split path
    /// cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Track commands (HO/TRACK/DROP/…) have no arm in <see cref="ApplyCommand"/>. They stay in
    /// <see cref="CommandBlock.ParsedCommands"/>, where <c>SimulationEngine.ProcessTriggeredTrackBlocks</c> reads them
    /// at trigger-fire time, but are kept out of the <see cref="CommandBlock.ApplyAction"/> so a triggered block never
    /// reaches <see cref="ApplyCommand"/>'s no-dispatcher-arm default.
    ///
    /// A caller rebuilding an existing block must copy that block's live wait countdown and
    /// <see cref="CommandBlock.TrackApplied"/> guard across afterwards: both are per-block runtime state and cannot be
    /// derived from the commands.
    /// </remarks>
    private static CommandBlock CreateBlock(
        List<ParsedCommand> parsedCommands,
        BlockTrigger? trigger,
        BlockLabels labels,
        string? sourceCommandText,
        DispatchContext ctx
    )
    {
        bool hasTrackCommand = parsedCommands.Exists(TrackEngine.IsTrackCommand);
        var applyCommands = hasTrackCommand ? parsedCommands.Where(c => !TrackEngine.IsTrackCommand(c)).ToList() : parsedCommands;

        var tracked = new List<TrackedCommand>(parsedCommands.Count);
        var dimensions = CommandDimension.None;
        foreach (var cmd in parsedCommands)
        {
            tracked.Add(new TrackedCommand { Type = CommandDescriber.ClassifyCommand(cmd) });
            dimensions |= CommandDescriber.GetCommandDimension(cmd);
        }

        // Sum all leading waits — `AT A WAIT 5 WAIT 10 <cmd>` merges two WaitCommands into one block.
        double waitSeconds = parsedCommands.OfType<WaitCommand>().Sum(w => w.Seconds);
        double waitDistanceNm = parsedCommands.OfType<WaitDistanceCommand>().Sum(w => w.DistanceNm);
        bool hasWait = parsedCommands.Exists(c => c is WaitCommand or WaitDistanceCommand);

        var description = labels.DescriptionPrefix + string.Join(", ", parsedCommands.Select(CommandDescriber.DescribeCommand));
        var naturalDescription = labels.NaturalPrefix + string.Join(", ", parsedCommands.Select(CommandDescriber.DescribeNatural));

        return new CommandBlock
        {
            Trigger = trigger,
            ApplyAction = BuildApplyAction(applyCommands, ctx),
            ParsedCommands = [.. parsedCommands],
            Commands = tracked,
            Dimensions = dimensions,
            Description = description,
            NaturalDescription = naturalDescription,
            DescriptionPrefix = labels.DescriptionPrefix,
            NaturalDescriptionPrefix = labels.NaturalPrefix,
            IsWaitBlock = hasWait,
            WaitRemainingSeconds = waitSeconds,
            WaitRemainingDistanceNm = waitDistanceNm,
            SourceCommandText = sourceCommandText,
            HasTrackCommand = hasTrackCommand,
            HasDeleteCommand = parsedCommands.Exists(c => c is DeleteCommand),
        };
    }

    /// <summary>
    /// Rebuilds the non-serialized halves of a queued block after a snapshot restore:
    /// <see cref="CommandBlock.ParsedCommands"/> and <see cref="CommandBlock.ApplyAction"/>. Both are
    /// closures/objects that only exist in the dispatch that created the block, so a restored block used
    /// to reach its turn, mark itself applied, and silently do nothing — the queued instruction vanished
    /// across every rewind and bug-bundle replay.
    ///
    /// Recovery re-parses <see cref="CommandBlock.SourceCommandText"/> (the same durable text the track
    /// path and the pattern pre-arm already recover from) and picks this block's sub-block by matching
    /// the serialized <see cref="CommandBlock.Description"/> against each candidate's regenerated
    /// description — longest match wins, so a candidate that is a suffix of another can't shadow it.
    /// Returns false when the text no longer parses or no candidate matches (the caller warns).
    /// </summary>
    internal static bool RehydrateRestoredBlock(CommandBlock block, AircraftState aircraft, DispatchContext ctx)
    {
        if (string.IsNullOrEmpty(block.SourceCommandText))
        {
            return false;
        }

        var reparsed = CommandParser.ParseCompound(block.SourceCommandText, aircraft.FlightPlan.Route);
        if (!reparsed.IsSuccess || reparsed.Value is not { } compound)
        {
            return false;
        }

        List<ParsedCommand>? matched = null;
        int matchedLength = -1;
        foreach (var candidate in compound.Blocks)
        {
            var candidateDescription = string.Join(", ", candidate.Commands.Select(CommandDescriber.DescribeCommand));
            if (candidateDescription.Length > matchedLength && block.Description.EndsWith(candidateDescription, StringComparison.Ordinal))
            {
                matched = [.. candidate.Commands];
                matchedLength = candidateDescription.Length;
            }
        }

        if (matched is null)
        {
            return false;
        }

        // Mirror CreateBlock: track commands stay out of the ApplyAction (they have no ApplyCommand arm
        // and are dispatched by SimulationEngine.ProcessTriggeredTrackBlocks) but stay in ParsedCommands.
        var applyCommands = matched.Where(c => !TrackEngine.IsTrackCommand(c)).ToList();
        block.ParsedCommands = matched;
        block.ApplyAction = BuildApplyAction(applyCommands, ctx);
        return true;
    }

    private static CommandBlock? SplitBlockNonConflicting(CommandBlock block, CommandDimension conflictingDims, DispatchContext ctx)
    {
        // If the block has no dimensional overlap at all, keep it entirely
        if ((block.Dimensions & conflictingDims) == 0)
        {
            return block;
        }

        // If we can't split (no ParsedCommands stored), the whole block conflicts
        if (block.ParsedCommands is null || block.ParsedCommands.Count != block.Commands.Count)
        {
            return null;
        }

        // Find which command indices to keep. This reads GetQueuedCommandDimension — what the command occupies while
        // it waits — not GetDimension(TrackedCommandType), which reports None for every verb that classifies as
        // Immediate. A block whose aggregate Dimensions say "conflict" must not then keep every one of its commands:
        // that is how a queued pattern entry used to survive the vector that replaced its lateral plan, and fire the
        // moment the superseded DCT ahead of it was marked complete.
        var keepIndices = new List<int>();
        for (int i = 0; i < block.Commands.Count; i++)
        {
            if ((CommandDescriber.GetQueuedCommandDimension(block.ParsedCommands[i]) & conflictingDims) == 0)
            {
                keepIndices.Add(i);
            }
        }

        if (keepIndices.Count == 0)
        {
            return null;
        }

        if (keepIndices.Count == block.Commands.Count)
        {
            return block;
        }

        // Rebuild a new block with only the non-conflicting commands, keeping the condition label ("at OAK: ") that
        // tells the controller when the survivors will fire.
        var keptParsed = keepIndices.Select(i => block.ParsedCommands[i]).ToList();
        var labels = new BlockLabels(block.DescriptionPrefix, block.NaturalDescriptionPrefix);

        var rebuilt = CreateBlock(keptParsed, block.Trigger, labels, block.SourceCommandText, ctx);

        // Runtime state the surviving commands cannot describe: a partially-elapsed wait, the guard that stops an
        // already-dispatched track command from firing twice, and how far the block's trigger has progressed. All of
        // it belongs to the block being replaced, and CreateBlock returns it defaulted.
        //
        // The trigger flags matter most: TriggerMet is a latch because IsTriggerMet goes false again once the aircraft
        // flies past the fix, so a rebuilt block that forgets it re-arms against a condition that has already
        // happened — it never completes and pins the queue behind it, or re-applies its commands if the trigger can
        // still evaluate true.
        rebuilt.WaitRemainingSeconds = block.WaitRemainingSeconds;
        rebuilt.WaitRemainingDistanceNm = block.WaitRemainingDistanceNm;
        rebuilt.TrackApplied = block.TrackApplied;
        rebuilt.IsApplied = block.IsApplied;
        rebuilt.TriggerMet = block.TriggerMet;
        rebuilt.TriggerCrossingObserved = block.TriggerCrossingObserved;
        rebuilt.TriggerMissed = block.TriggerMissed;
        rebuilt.TriggerClosestApproach = block.TriggerClosestApproach;

        return rebuilt;
    }

    private static List<string> EnqueueBlocks(CompoundCommand compound, int startIndex, AircraftState aircraft, DispatchContext ctx)
    {
        var messages = new List<string>();

        for (int i = startIndex; i < compound.Blocks.Count; i++)
        {
            var parsedBlock = compound.Blocks[i];

            var trigger = ConvertCondition(parsedBlock.Condition, aircraft, ctx);
            if (trigger is null && parsedBlock.Condition is AtGroundEntityCondition unresolved)
            {
                aircraft.PendingWarnings.Add($"AT ground entity not found: {FormatGroundLabel(unresolved)}");
                continue;
            }

            var labels = BuildConditionLabels(parsedBlock.Condition);
            var commandBlock = CreateBlock([.. parsedBlock.Commands], trigger, labels, compound.SourceText, ctx);

            aircraft.Queue.Blocks.Add(commandBlock);
            messages.Add(commandBlock.NaturalDescription);
        }

        return messages;
    }

    /// <summary>
    /// Builds a deferred action that applies all commands in a block to the aircraft.
    /// This is stored on the CommandBlock and executed when the block becomes active.
    /// Captures the dispatch context by reference so triggered commands see the same
    /// weather, ground layout, and aircraft lookup as the original dispatch.
    ///
    /// When a phase is active at apply time, tower-only verbs (CTO/LUAW/TAXI/CROSS
    /// etc.) are routed through <see cref="TryApplyTowerCommand"/> first, mirroring
    /// the user-typed dispatch path. Without this, queued tower verbs that re-fire
    /// after a phase transition (e.g. <c>TAXI ... ; CTO MRT</c> firing CTO when
    /// the aircraft reaches the hold-short) would hit the <see cref="ApplyCommand"/>
    /// fallback, which has no arm for those verbs and returns a
    /// <see cref="CommandResult.NoDispatcherArm"/> result.
    /// </summary>
    internal static Func<AircraftState, CommandResult> BuildApplyAction(List<ParsedCommand> commands, DispatchContext ctx)
    {
        // Capture the parsed commands; they'll be applied when the block activates
        var captured = commands.ToList();
        return ac =>
        {
            bool hadProcedure = ac.Procedure.ActiveSidId is not null || ac.Procedure.ActiveStarId is not null;
            bool hadViaMode = ac.Procedure.SidViaMode || ac.Procedure.StarViaMode;
            var messages = new List<string>();

            foreach (var cmd in captured)
            {
                CommandResult? result = null;

                if (ac.Phases?.CurrentPhase is { } currentPhase)
                {
                    var towerResult = TryApplyTowerCommand(cmd, ac, currentPhase, ctx);
                    if (towerResult is not null)
                    {
                        if (ReferenceEquals(towerResult, PhaseShouldBeCleared))
                        {
                            // Mirror the phase-clear sequence DispatchCompoundCore performs
                            // once validation succeeds. We are already past validation here
                            // (the block was enqueued via the same dispatcher).
                            var phaseCtx = BuildMinimalContext(ac);
                            string? clearedSummary = ac.Phases is { } pl ? PhaseClearSummary.Build(pl) : null;
                            ac.Phases?.Clear(phaseCtx);
                            ac.Phases = null;
                            ac.Targets.TurnRateOverride = null;
                            ac.Targets.HasExplicitTurnRate = false;
                            ac.Targets.PreferredTurnDirection = null;
                            AirborneFollowHelper.ClearFollowState(ac);
                            ResumeAssignedAltitudeAfterPhaseClear(ac, currentPhase is GoAroundPhase);

                            if (clearedSummary is not null)
                            {
                                ac.PendingWarnings.Add($"{ac.Callsign} {clearedSummary} cancelled by {CommandDescriber.DescribeNatural(cmd)}");
                            }

                            // Now apply the tower command against the cleared phase state.
                            result = ApplyCommand(cmd, ac, ctx);
                        }
                        else
                        {
                            result = towerResult;
                        }
                    }
                }

                result ??= ApplyCommand(cmd, ac, ctx);

                if (!result.Success)
                {
                    return WithRejectedCommand(result, cmd);
                }

                // Release phase-internal state only after the command actually applied
                // (e.g. RV SID heading hold on a successful DCT during InitialClimb).
                if (ac.Phases?.CurrentPhase is { } phaseForNotify)
                {
                    NotifyPhaseCommandAccepted(ac, cmd, phaseForNotify, ctx);
                }

                if (result.Message is not null)
                {
                    messages.Add(result.Message);
                }
            }

            CheckVectoringWarning(ac, captured, hadProcedure, hadViaMode);
            var msg = messages.Count > 0 ? string.Join(", ", messages) : null;
            return new CommandResult(true, msg);
        };
    }

    private static CommandResult WithRejectedCommand(CommandResult result, ParsedCommand command)
    {
        if (result.Success || result.RejectedCommandType is not null || command is UnsupportedCommand)
        {
            return result;
        }

        if (result.NoDispatcherArm)
        {
            return result;
        }

        return result with
        {
            RejectedCommandType = CommandDescriber.ToCanonicalType(command),
        };
    }

    /// <summary>
    /// Warns and levels off when an aircraft is vectored off a procedure (SID/STAR)
    /// without an altitude assignment in the same block. Handles two cases:
    /// 1. Procedure fully cleared (heading/DCT off-procedure without altitude)
    /// 2. Procedure preserved but via-mode disabled (DCT on-procedure without altitude/DVIA/CVIA)
    /// </summary>
    private static void CheckVectoringWarning(AircraftState aircraft, List<ParsedCommand> commands, bool hadProcedure, bool hadViaMode)
    {
        if (!hadProcedure)
        {
            return;
        }

        bool hasAltCmd = commands.Any(c => c is ClimbMaintainCommand or DescendMaintainCommand);
        bool procedureCleared = aircraft.Procedure.ActiveSidId is null && aircraft.Procedure.ActiveStarId is null;

        if (procedureCleared)
        {
            bool hasHeadingCmd = commands.Any(c =>
                c
                    is FlyHeadingCommand
                        or TurnLeftCommand
                        or TurnRightCommand
                        or LeftTurnCommand
                        or RightTurnCommand
                        or FlyPresentHeadingCommand
                        or DirectToCommand
                        or ForceDirectToCommand
                        or ConstrainedForceDirectToCommand
                        or TurnLeftDirectToCommand
                        or TurnRightDirectToCommand
            );

            if (hasHeadingCmd && !hasAltCmd)
            {
                aircraft.PendingWarnings.Add("Vectored off procedure without an altitude assignment");
                FlightCommandHandler.LevelOff(aircraft);
            }

            return;
        }

        // Procedure preserved (DCT to on-procedure fix) but via-mode was disabled
        if (hadViaMode && !aircraft.Procedure.SidViaMode && !aircraft.Procedure.StarViaMode)
        {
            bool hasViaCmd = commands.Any(c => c is ClimbViaCommand or DescendViaCommand);
            if (!hasAltCmd && !hasViaCmd)
            {
                aircraft.PendingWarnings.Add("Vectored off procedure without an altitude assignment");
                FlightCommandHandler.LevelOff(aircraft);
            }
        }
    }

    private static BlockTrigger? ConvertCondition(BlockCondition? condition, AircraftState aircraft, DispatchContext ctx)
    {
        return condition switch
        {
            LevelCondition lv => new BlockTrigger { Type = BlockTriggerType.ReachAltitude, Altitude = lv.Altitude },
            AtFixCondition { Radial: { } radial, Distance: { } dist } at => ConvertFrdCondition(at, radial, dist),
            AtFixCondition { Radial: { } radial } at => new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = at.FixName,
                FixLat = at.Lat,
                FixLon = at.Lon,
                Radial = radial,
            },
            AtFixCondition at => new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixName = at.FixName,
                FixLat = at.Lat,
                FixLon = at.Lon,
            },
            AtGroundEntityCondition ge => ConvertGroundEntityCondition(ge, aircraft, ctx),
            GiveWayCondition gw => new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = gw.TargetCallsign },
            DistanceFinalCondition df => new BlockTrigger { Type = BlockTriggerType.DistanceFinal, DistanceFinalNm = df.DistanceNm },
            OnHandoffCondition => new BlockTrigger { Type = BlockTriggerType.OnHandoff },
            OnHoldShortCondition => new BlockTrigger { Type = BlockTriggerType.EnteringHoldingAfterExit },
            _ => null,
        };
    }

    private static BlockTrigger? ConvertGroundEntityCondition(AtGroundEntityCondition ge, AircraftState aircraft, DispatchContext ctx)
    {
        var layout = aircraft.Ground.Layout ?? ctx.GroundLayout;
        if (layout is null)
        {
            return null;
        }

        switch (ge.Kind)
        {
            case GroundEntityKind.Spot:
            {
                var node = layout.FindSpotNodeByName(ge.Token) ?? layout.FindSpotByName(ge.Token);
                if (node is null)
                {
                    return null;
                }
                return new BlockTrigger
                {
                    Type = BlockTriggerType.AtGroundEntity,
                    GroundKind = ge.Kind,
                    GroundNodeId = node.Id,
                    FixLat = node.Position.Lat,
                    FixLon = node.Position.Lon,
                    GroundEntityToken = ge.Token,
                };
            }
            case GroundEntityKind.Parking:
            {
                var node = layout.FindParkingByName(ge.Token);
                if (node is null)
                {
                    return null;
                }
                return new BlockTrigger
                {
                    Type = BlockTriggerType.AtGroundEntity,
                    GroundKind = ge.Kind,
                    GroundNodeId = node.Id,
                    FixLat = node.Position.Lat,
                    FixLon = node.Position.Lon,
                    GroundEntityToken = ge.Token,
                };
            }
            case GroundEntityKind.Intersection:
            {
                if (ge.SecondTaxiway is null)
                {
                    return null;
                }
                var node = layout.FindIntersectionNode(ge.Token, ge.SecondTaxiway, aircraft.Position);
                if (node is null)
                {
                    return null;
                }
                return new BlockTrigger
                {
                    Type = BlockTriggerType.AtGroundEntity,
                    GroundKind = ge.Kind,
                    GroundNodeId = node.Id,
                    FixLat = node.Position.Lat,
                    FixLon = node.Position.Lon,
                    GroundTaxiwayName = ge.Token,
                    GroundEntityToken = $"{ge.Token}/{ge.SecondTaxiway}",
                };
            }
            case GroundEntityKind.Taxiway:
            {
                if (layout.GetNodesOnTaxiway(ge.Token).Count == 0)
                {
                    return null;
                }
                return new BlockTrigger
                {
                    Type = BlockTriggerType.AtGroundEntity,
                    GroundKind = ge.Kind,
                    GroundTaxiwayName = ge.Token,
                    GroundEntityToken = ge.Token,
                };
            }
            default:
                return null;
        }
    }

    private static BlockTrigger ConvertFrdCondition(AtFixCondition at, int radial, int dist)
    {
        var (targetLat, targetLon) = GeoMath.ProjectPointRaw(at.Lat, at.Lon, radial, dist);
        return new BlockTrigger
        {
            Type = BlockTriggerType.ReachFrdPoint,
            FixName = at.FixName,
            FixLat = at.Lat,
            FixLon = at.Lon,
            Radial = radial,
            DistanceNm = dist,
            TargetLat = targetLat,
            TargetLon = targetLon,
        };
    }

    private static string FormatAtLabel(AtFixCondition at)
    {
        if (at.Radial is { } radial && at.Distance is { } dist)
        {
            return $"{at.FixName} R{radial:D3} D{dist:D3}";
        }

        if (at.Radial is { } r)
        {
            return $"{at.FixName} R{r:D3}";
        }

        return at.FixName;
    }

    private static string FormatGroundLabel(AtGroundEntityCondition ge) =>
        ge.Kind switch
        {
            GroundEntityKind.Taxiway => $"taxi {ge.Token}",
            GroundEntityKind.Spot => $"spot {ge.Token}",
            GroundEntityKind.Parking => $"parking {ge.Token}",
            GroundEntityKind.Intersection => $"intersection {ge.Token}/{ge.SecondTaxiway}",
            _ => ge.Token,
        };

    private static CommandResult TryAirborneFollow(AircraftState aircraft, FollowCommand follow, DispatchContext ctx)
    {
        if (aircraft.IsOnGround)
        {
            return new CommandResult(false, "FOLLOW requires the aircraft to be airborne");
        }

        // Forced FOLLOW (FOLLOWF): the RPO folds the RTISF into the follow clearance so
        // traffic-in-sight need not be reported first. RPO-only, like RTISF — a solo student
        // must acquire the traffic with RTIS before following.
        if (follow.Force)
        {
            if (ctx.SoloTrainingMode)
            {
                return new CommandResult(false, "FOLLOWF is RPO-only; use RTIS/RTISF in solo training");
            }
            aircraft.Approach.HasReportedTrafficInSight = true;
            if (!string.IsNullOrWhiteSpace(follow.TargetCallsign))
            {
                aircraft.Approach.LastReportedTrafficCallsign = follow.TargetCallsign.ToUpperInvariant();
            }
            else if (aircraft.PendingObservations.OfType<TrafficAcquisitionObservation>().FirstOrDefault() is { } pending)
            {
                // Bare FOLLOWF folds in a still-pending RTIS: the traffic the RPO called out but the
                // pilot hasn't visually acquired yet lives only in PendingObservations
                // (LastReportedTrafficCallsign isn't set until acquisition succeeds). FOLLOWF
                // supersedes that look-for-traffic, so consume and clear the observation.
                aircraft.Approach.LastReportedTrafficCallsign = pending.TargetCallsign.ToUpperInvariant();
                aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
            }
        }

        // RTIS gate: a pilot cannot follow traffic they haven't visually acquired.
        // Matches CVA FOLLOW behavior — controllers can force this with RTISF (or FOLLOWF).
        if (!aircraft.Approach.HasReportedTrafficInSight)
        {
            return new CommandResult(false, "Traffic not in sight — issue RTIS first");
        }

        // Bare FOLLOW (no explicit callsign) defaults to the most recently reported
        // traffic. Explicit callsign always wins. If neither is available, reject.
        // Message mirrors the "Unable, no traffic specified" wording used by RTIS.
        var target = follow.TargetCallsign ?? aircraft.Approach.LastReportedTrafficCallsign;
        if (string.IsNullOrEmpty(target))
        {
            return new CommandResult(false, "Unable, say traffic callsign");
        }

        var leadAircraft = ctx.FindAircraft?.Invoke(target);

        // Visual separation — and therefore FOLLOW — is not authorized behind a super
        // (7110.65 §7-2-1; AIM §5-5-11.2.5). Reject when the lead resolves to a super.
        if (
            leadAircraft is { } lead
            && WakeTurbulenceData.WakeClassForType(lead.AircraftType, AircraftCategorization.Categorize(lead.AircraftType))
                == WakeTurbulenceData.WakeClass.Super
        )
        {
            return new CommandResult(false, $"Unable, visual separation not authorized behind super {target}");
        }

        // If the follower is already in a pattern phase to the SAME runway the lead is using,
        // just update the target — AirborneFollowHelper handles spacing on every pattern leg.
        // Rebuilding through VfrFollowPhase here would route the follower back through
        // PatternEntry for the same runway it's already flying — wasteful and confusing. Also
        // clear any prior EXT (extended leg) on Upwind/Crosswind/Downwind: FOLLOW supersedes
        // the controller's hold-and-call-the-next-leg instruction since the pilot now has
        // explicit traffic to sequence behind.
        //
        // When the lead is landing a DIFFERENT runway, in-trail sequencing against the
        // follower's own pattern is meaningless — fall through to the VfrFollowPhase install
        // below, whose auto-join (TryJoinLeadPattern / TryJoinLeadFinal) re-sequences the
        // follower onto the lead's runway with proper in-trail spacing and intercept gates.
        var current = aircraft.Phases?.CurrentPhase;
        bool followerOnPatternLeg =
            current
            is PatternEntryPhase
                or MidfieldCrossingPhase
                or TeardropReentryPhase
                or UpwindPhase
                or CrosswindPhase
                or DownwindPhase
                or BasePhase
                or FinalApproachPhase;
        bool crossRunway = followerOnPatternLeg && IsLeadOnDifferentRunway(aircraft, leadAircraft);

        // A cross-runway re-sequence needs room to maneuver. From Base or FinalApproach the
        // follower is already low and close in, and swinging it onto a (typically closely
        // spaced) parallel from there flies a low crossing of the original runway's final
        // approach course — AIM §4-3-3 FIG 4-3-3 note 7 (do not continue on a track that
        // penetrates the parallel runway's final) and §4-3-5 (no unexpected pattern
        // maneuvers). Refuse; the controller re-sequences explicitly (ERB/ELB), vectors, or
        // sends it around. Re-sequencing from upwind/crosswind/downwind/entry is fine.
        if (crossRunway && (current is BasePhase or FinalApproachPhase))
        {
            string ownRunway = aircraft.Phases!.AssignedRunway!.Designator;
            return new CommandResult(false, $"Unable, established for runway {ownRunway} — vector or go around to follow {target}");
        }

        if (followerOnPatternLeg && !crossRunway)
        {
            switch (current)
            {
                case UpwindPhase uw when uw.IsExtended:
                    uw.IsExtended = false;
                    break;
                case CrosswindPhase cw when cw.IsExtended:
                    cw.IsExtended = false;
                    break;
                case DownwindPhase dw when dw.IsExtended:
                    dw.IsExtended = false;
                    break;
            }
            aircraft.Approach.FollowingCallsign = target;
            return Ok($"Follow {target}");
        }

        // Pattern-aware install (issue #352): when the lead is established toward a known
        // runway (entry, pattern leg, final, landing), FOLLOW is a runway-sequencing
        // instruction — the correct maneuver is to fly that runway's pattern behind the
        // lead (continue the downwind, extend, turn base behind), which free pursuit cannot
        // express: ComputeFreePursuitHeading immediately parallels the lead's track, and
        // from a downwind-shaped geometry that is an about-face onto a parallel offset
        // track. Build the pattern entry to the lead's runway instead — on the runway's
        // established circuit side (ChooseFollowJoinDirection), with the published
        // midfield-crossing entry when the follower is on the wrong side — and let the
        // Downwind/AirborneFollowHelper sequencing holds do the spacing. Free pursuit
        // remains for genuinely free-flight leads (no runway to sequence onto).
        // For a lead landing a DIFFERENT runway this models an implied runway change:
        // 7110.65 §3-8-1's codified phraseology for traffic on another runway is a
        // traffic advisory ("TRAFFIC ... LANDING RUNWAY (number)"), not FOLLOW — the
        // re-sequence is a deliberate trainer affordance (the controller's intent is the
        // lead's runway), kept per maintainer decision.
        if (leadAircraft is { IsOnGround: false } establishedLead && IsEstablishedTowardRunway(establishedLead))
        {
            var leadRunway = establishedLead.Phases!.AssignedRunway!;

            // Exception: the lead is on final and the follower is already positioned to join
            // that final directly — inbound at a workable angle, near the approach course,
            // with no parallel runway's final in between. There the in-trail join
            // (VfrFollowPhase → TryJoinLeadFinal) is the right shape; a full downwind
            // circuit would loop an aircraft that is effectively number two on the approach.
            bool leadOnFinal = establishedLead.Phases.CurrentPhase is FinalApproachPhase or LandingPhase;
            if (!(leadOnFinal && CanJoinLeadFinalDirectly(aircraft, leadRunway)))
            {
                var joinDirection = ChooseFollowJoinDirection(aircraft, establishedLead, leadRunway);
                var entryResult = PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    joinDirection,
                    PatternEntryLeg.Downwind,
                    runwayId: leadRunway.Designator,
                    finalDistanceNm: null,
                    groundLayout: ctx.GroundLayout
                );
                if (entryResult.Success)
                {
                    aircraft.Approach.FollowingCallsign = target;
                    return Ok($"Follow {target}");
                }
                // The entry could not be built (no airport context / runway lookup failure) —
                // fall through to the free-pursuit install rather than dropping the follow.
                // Invariant this relies on: with (Downwind, finalDistanceNm: null) every
                // TryEnterPattern reject path returns BEFORE mutating the phase chain, so
                // `current` (captured above) is still valid below. The reject paths that DO
                // leave AssignedRunway repointed without a rebuilt chain are all gated on
                // Final/Base entries, which this call site never requests.
            }
        }

        // If the follower is already in VfrFollowPhase, retarget in place. Reached only for
        // free-flight leads — a lead established toward a runway re-sequences via the
        // pattern install above.
        if (current is VfrFollowPhase vfp)
        {
            vfp.UpdateTarget(target);
            aircraft.Approach.FollowingCallsign = target;
            return Ok($"Follow {target}");
        }

        // Otherwise install a fresh VfrFollowPhase, replacing any existing phases.
        // Build a new PhaseList (mirrors ApproachCommandHandler.TryClearedVisualApproach)
        // so we don't inherit stale phase indices from the old list.
        if (aircraft.Phases is { } existing)
        {
            var clearCtx = BuildMinimalContext(aircraft, groundLayout: null);
            existing.Clear(clearCtx);
        }
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Phases.Add(new VfrFollowPhase(target));
        var startCtx = BuildMinimalContext(aircraft, groundLayout: null);
        aircraft.Phases.Start(startCtx);
        aircraft.Approach.FollowingCallsign = target;
        return Ok($"Follow {target}");
    }

    /// <summary>
    /// True when both the follower and the lead have a known assigned runway and those
    /// runways differ. FOLLOW is an in-trail sequencing instruction, and in-trail only has
    /// meaning on a shared runway — a follower told to follow traffic landing the parallel
    /// must be re-sequenced onto that runway (the controller's intent) rather than left on
    /// its own pattern, where none of the spacing or leg-hold logic engages (all of it is
    /// gated on a matching runway). Returns false whenever either runway is unknown, so the
    /// cheap in-place retarget stays the default.
    /// </summary>
    private static bool IsLeadOnDifferentRunway(AircraftState follower, AircraftState? lead)
    {
        string? followerRunway = follower.Phases?.AssignedRunway?.Designator;
        string? leadRunway = lead?.Phases?.AssignedRunway?.Designator;
        if ((followerRunway is null) || (leadRunway is null))
        {
            return false;
        }

        return !string.Equals(followerRunway, leadRunway, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the lead has an assigned runway and is on a phase that flows toward it —
    /// a pattern entry/leg, final, or the landing itself. A FOLLOW on such a lead is a
    /// sequencing instruction for that runway, so the follower joins its pattern rather
    /// than free-pursuing the lead's present track.
    /// </summary>
    private static bool IsEstablishedTowardRunway(AircraftState lead) =>
        (lead.Phases?.AssignedRunway is not null)
        && lead.Phases.CurrentPhase
            is PatternEntryPhase
                or MidfieldCrossingPhase
                or TeardropReentryPhase
                or UpwindPhase
                or CrosswindPhase
                or DownwindPhase
                or BasePhase
                or FinalApproachPhase
                or LandingPhase
                or TouchAndGoPhase;

    /// <summary>
    /// Maximum cross-track from the lead's final approach course at which a follower still
    /// counts as positioned for a direct in-trail final join (rather than a pattern join).
    /// Deliberately wider than <see cref="VfrFollowPhase.MaxFinalJoinCrossTrackNm"/> — the
    /// follower converges under free pursuit before the join gates commit the turn.
    /// </summary>
    private const double FollowDirectFinalJoinMaxCrossTrackNm = 2.5;

    /// <summary>
    /// Minimum distance up the final approach course for a direct in-trail join. Below it
    /// the follower is abeam the field (a downwind-shaped position, issue #352) — even if it
    /// is laterally near the extended centerline band, "beside the runway" is pattern
    /// territory, not approach-corridor territory.
    /// </summary>
    private const double FollowDirectFinalJoinMinAlongFinalNm = 1.5;

    /// <summary>
    /// True when the follower is positioned in <paramref name="runway"/>'s final approach
    /// corridor — genuinely out on the arrival side, laterally near the course (within a 45°
    /// cone), and not separated from it by a parallel runway's final. The test is position
    /// only: the instantaneous track is unreliable (the follower may be mid-turn when the
    /// FOLLOW arrives), and free pursuit turns a corridor-positioned follower into trail
    /// without a pattern-shaped maneuver.
    /// </summary>
    private static bool CanJoinLeadFinalDirectly(AircraftState follower, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double alongFinalNm = GeoMath.AlongTrackDistanceNm(follower.Position, threshold, runway.TrueHeading.ToReciprocal());
        double crossTrackNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(follower.Position, threshold, runway.TrueHeading));
        return (alongFinalNm >= FollowDirectFinalJoinMinAlongFinalNm)
            && (crossTrackNm <= FollowDirectFinalJoinMaxCrossTrackNm)
            && (crossTrackNm <= alongFinalNm)
            && !VfrFollowPhase.JoinCapturePathCrossesParallelFinal(follower.Position, runway);
    }

    /// <summary>
    /// Cross-track band around the extended centerline within which the follower has no
    /// meaningful "own side" for the last-resort side fallback.
    /// </summary>
    private const double FollowJoinSideEpsilonNm = 0.25;

    /// <summary>
    /// Pattern side for a follow-driven pattern join: the lead's own circuit direction
    /// first, then the runway's natural direction (parallel-runway inference — 28R with
    /// 28L present flies right traffic), then the side the follower happens to occupy,
    /// then the FAA default Left (AIM §4-3-3). The runway's established circuit must win:
    /// joining on whatever side the follower momentarily occupies can build an opposing
    /// circuit for the same runway, and on close parallels it descends a base leg across
    /// the neighboring runway's final approach course (AIM §4-3-3 FIG 4-3-3 note 7). A
    /// follower left on the wrong side for the chosen circuit takes the published
    /// midfield-crossing entry at pattern altitude (AIM §4-3-3.1.b) via
    /// <see cref="PatternCommandHandler.TryEnterPattern"/>'s wrong-side path — crossing
    /// the field at TPA is the maneuver the AIM prescribes for exactly this geometry.
    /// </summary>
    internal static PatternDirection ChooseFollowJoinDirection(AircraftState follower, AircraftState lead, RunwayInfo runway)
    {
        if (lead.Phases?.TrafficDirection is { } leadDirection)
        {
            return leadDirection;
        }
        if (GoAroundHelper.InferDefaultPatternDirection(runway) is { } naturalDirection)
        {
            return naturalDirection;
        }
        TrueHeading rightSideHeading = runway.TrueHeading + 90.0;
        double sideOffsetNm = GeoMath.AlongTrackDistanceNm(
            follower.Position,
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            rightSideHeading
        );
        if (sideOffsetNm > FollowJoinSideEpsilonNm)
        {
            return PatternDirection.Right;
        }
        if (sideOffsetNm < -FollowJoinSideEpsilonNm)
        {
            return PatternDirection.Left;
        }
        return PatternDirection.Left;
    }
}
