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
public record CommandResult(
    bool Success,
    string? Message = null,
    CanonicalCommandType? RejectedCommandType = null,
    string? Advisory = null,
    bool NoDispatcherArm = false
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
                    AttachAfterRunwayCrossingTriggerForToweredFirstBlock(compound, aircraft, firstRemainingIdx, currentPhaseBeforeDispatch);
                    aircraft.Queue.Blocks.AddRange(phasePreserved);

                    var combinedMessages = new List<string> { result.Message ?? "" };
                    combinedMessages.AddRange(modifierMessages);
                    combinedMessages.AddRange(remainingMessages);
                    if (combinedMessages.Count > 1)
                    {
                        var combined = string.Join(" ; then ", combinedMessages.Where(m => !string.IsNullOrEmpty(m)));
                        return new CommandResult(true, combined);
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
        AttachAfterRunwayCrossingTrigger(compound, aircraft, firstNewBlockIdx, currentPhaseBeforeDispatch);
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
                // subsequent SA/MNA/EXT blocks would otherwise sit in the queue forever
                // — UpdateCommandQueue short-circuits while a phase is active. Apply
                // them immediately via the tower path so they arm the pending leg.
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
                        if (parsedCmd is not (MakeShortApproachCommand or MakeNormalApproachCommand or ExtendPatternCommand))
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
        // Route ground commands through DispatchCompound for phase interaction
        if (CommandDescriber.IsGroundCommand(command))
        {
            var compound = new CompoundCommand([new ParsedBlock(null, [command])]);
            return DispatchCompound(compound, aircraft, ctx);
        }

        // Phase-transparent commands: apply without clearing queue or phases
        if ((aircraft.Phases?.CurrentPhase is not null) && CommandDescriber.IsPhaseTransparent(CommandDescriber.ToCanonicalType(command)))
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

    private static bool RequiresVfr(ParsedCommand command) =>
        command
            is EnterLeftDownwindCommand
                or EnterRightDownwindCommand
                or EnterLeftCrosswindCommand
                or EnterRightCrosswindCommand
                or EnterLeftBaseCommand
                or EnterRightBaseCommand
                or EnterFinalCommand
                or MakeLeftTrafficCommand
                or MakeRightTrafficCommand
                or TurnCrosswindCommand
                or TurnDownwindCommand
                or TurnBaseCommand
                or ExtendPatternCommand
                or MakeShortApproachCommand
                or MakeNormalApproachCommand
                or MakeLeft360Command
                or MakeRight360Command
                or MakeLeft270Command
                or MakeRight270Command
                or CircleAirportCommand
                or PatternSizeCommand
                or MakeLeftSTurnsCommand
                or MakeRightSTurnsCommand
                or OffsetLeftPatternCommand
                or OffsetRightPatternCommand
                or Plan270Command
                or Cancel270Command
                or TouchAndGoCommand
                or StopAndGoCommand
                or LowApproachCommand
                or ClearedForOptionCommand
                or HoldPresentPosition360Command
                or HoldPresentPositionHoverCommand
                or HoldAtFixOrbitCommand
                or HoldAtFixHoverCommand;

    private static readonly CommandResult VfrRequiredResult = new(false, "Command requires VFR aircraft. Use CIFR to cancel IFR flight plan");

    private static readonly CommandResult IfrCtoVfrModifierResult = new(
        false,
        "IFR aircraft can only receive a bare CTO (follow SID), CTO with an assigned heading, or CTO RH (runway heading); pattern/relative modifiers (MRC, ML*, OC, DCT, MLT, MRT, ...) require VFR"
    );

    /// <summary>
    /// IFR departure clearances accept only <see cref="DefaultDeparture"/> (follow SID),
    /// <see cref="FlyHeadingDeparture"/> (assigned numeric heading), or
    /// <see cref="RunwayHeadingDeparture"/> (CTO RH — hold runway heading and await vectors;
    /// routinely issued to IFR departures). Pattern-relative modifiers (MRC, ML*, OC, DCT, MLT/MRT)
    /// are VFR-only and must be rejected so the aircraft doesn't peel off toward the pattern
    /// immediately at liftoff. Returns null if the command is allowed for this aircraft.
    /// </summary>
    private static CommandResult? CheckIfrDepartureCompatibility(ParsedCommand command, AircraftState aircraft)
    {
        if (aircraft.FlightPlan.IsVfr)
        {
            return null;
        }

        DepartureInstruction? departure = command switch
        {
            ClearedForTakeoffCommand cto => cto.Departure,
            ClearedTakeoffPresentCommand ctopp => ctopp.Departure,
            _ => null,
        };

        if (departure is null or DefaultDeparture or FlyHeadingDeparture or RunwayHeadingDeparture or PresentPositionHoverDeparture)
        {
            return null;
        }

        return IfrCtoVfrModifierResult;
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
    /// Instructor advisory text for a procedure resolved from a cached prior AIRAC cycle because its coded
    /// legs are absent from the current FAA CIFP — the procedure may be retired, or still charted but missing
    /// from the CIFP dataset. Returns null when the procedure came from the current cycle.
    /// </summary>
    internal static string? PriorCycleProcedureAdvisory(string kind, string procedureId, string? resolvedFromCycleId)
    {
        if (resolvedFromCycleId is not { } cycle)
        {
            return null;
        }

        return $"{procedureId} ({kind}) resolved from a prior AIRAC cycle ({cycle}) — its coded data is absent from the current FAA CIFP. Verify against current charts and vector as needed.";
    }

    private static CommandResult ApplyCommandCore(ParsedCommand command, AircraftState aircraft, DispatchContext ctx)
    {
        if (RequiresVfr(command) && !aircraft.FlightPlan.IsVfr)
        {
            return VfrRequiredResult;
        }

        if (CheckIfrDepartureCompatibility(command, aircraft) is { } ifrReject)
        {
            return ifrReject;
        }

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
                int removed = aircraft.Queue.Blocks.RemoveAll(b => b.Trigger?.Type == BlockTriggerType.EnteringHoldingAfterExit);
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
                return PatternCommandHandler.TryClearedToLand(ctl, aircraft);
            case ForceLandingCommand flc:
                return PatternCommandHandler.TryForceLanding(flc, aircraft, ctx);
            case LandAndHoldShortCommand lahso:
                return PatternCommandHandler.TryLandAndHoldShort(lahso, aircraft, aircraft.Ground.Layout);
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

            case FollowCommand follow:
                return TryAirborneFollow(aircraft, follow, ctx);

            // --- Flight plan ---
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
        // Dry-run uses a deterministic RNG and disables DCT-fix validation, auto-cross-runway
        // side effects, and terminal emission. The clone is discarded; emitting SAY broadcasts
        // here would surface phantom pilot transmissions before the trigger actually fires.
        var dryCtx = ctx with
        {
            Rng = new Random(0),
            ValidateDctFixes = false,
            AutoCrossRunway = false,
            TerminalEmitter = null,
        };

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
        Phase? phaseBeforeDispatch
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

        if (!WillProduceRunwayCrossing(phaseBeforeDispatch))
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
        Phase? phaseBeforeDispatch
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

        if (!WillProduceRunwayCrossing(phaseBeforeDispatch))
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
    /// True when the aircraft was holding short of a runway (either implicit
    /// <see cref="HoldShortReason.RunwayCrossing"/> or explicit-but-runway-named
    /// <see cref="HoldShortReason.ExplicitHoldShort"/>) immediately before
    /// dispatch. A <c>CROSS</c> against such a hold-short will produce a
    /// <see cref="Yaat.Sim.Phases.Ground.CrossingRunwayPhase"/>.
    /// </summary>
    private static bool WillProduceRunwayCrossing(Phase? phase)
    {
        if (phase is not HoldingShortPhase hp)
        {
            return false;
        }

        if (hp.HoldShort.Reason == HoldShortReason.DestinationRunway)
        {
            return false;
        }

        return hp.HoldShort.TargetName is { Length: > 0 } target && target.Length > 0 && char.IsAsciiDigit(target[0]);
    }

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
        var payloadBlocks = new List<ParsedBlock>();

        // Sibling commands in the first block (everything except the WAIT)
        var siblings = firstBlock.Commands.Where(c => c != waitCmd && c != waitDistCmd).ToList();
        if (siblings.Count > 0)
        {
            payloadBlocks.Add(new ParsedBlock(firstBlock.Condition, siblings));
        }

        // Subsequent blocks
        for (int i = 1; i < compound.Blocks.Count; i++)
        {
            payloadBlocks.Add(compound.Blocks[i]);
        }

        // Bare WAIT with no payload — standalone wait, let queue handle it
        if (payloadBlocks.Count == 0)
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
        var payloadBlocks = new List<ParsedBlock>();
        payloadBlocks.Add(new ParsedBlock(null, compound.Blocks[0].Commands));
        for (int i = 1; i < compound.Blocks.Count; i++)
        {
            payloadBlocks.Add(compound.Blocks[i]);
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

            return messages.Count <= 1 ? towerResult : new CommandResult(true, string.Join(", ", messages));
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
        if (RequiresVfr(command) && !aircraft.FlightPlan.IsVfr)
        {
            return VfrRequiredResult;
        }

        // Hold-for-release runway-entry gate: a held departure may not enter the runway (LUAW) or
        // take off (CTO/CTOPP) until released. It stays holding short. Cleared by REL/CTOA.
        if (aircraft.Ground.HeldForRelease && command is ClearedForTakeoffCommand or ClearedTakeoffPresentCommand or LineUpAndWaitCommand)
        {
            return new CommandResult(
                false,
                $"{aircraft.Callsign} is held for release at {aircraft.FlightPlan.Departure} — REL {aircraft.Callsign} first"
            );
        }

        if (CheckIfrDepartureCompatibility(command, aircraft) is { } ifrReject)
        {
            return ifrReject;
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
                return PatternCommandHandler.TryClearedToLand(ctl, aircraft);

            case ForceLandingCommand flc:
                return PatternCommandHandler.TryForceLanding(flc, aircraft, ctx);

            case LandAndHoldShortCommand lahso:
                return PatternCommandHandler.TryLandAndHoldShort(lahso, aircraft, groundLayout);

            case CancelLandingClearanceCommand:
                return PatternCommandHandler.TryCancelLandingClearance(aircraft);

            case GoAroundCommand ga:
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
                return PatternCommandHandler.TrySetupTouchAndGo(aircraft, tg.TrafficPattern);
            case StopAndGoCommand sg:
                return PatternCommandHandler.TrySetupStopAndGo(aircraft, sg.TrafficPattern);
            case LowApproachCommand la:
                return PatternCommandHandler.TrySetupLowApproach(aircraft, la.TrafficPattern);
            case ClearedForOptionCommand opt:
                return PatternCommandHandler.TrySetupClearedForOption(aircraft, opt.TrafficPattern);

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
                var preClear = GroundCommandHandler.TryPreClearRouteCrossings(aircraft, hsResume.CrossRunways);
                if (!preClear.Success)
                {
                    return preClear;
                }
                var addHs = GroundCommandHandler.TryAddExplicitHoldShorts(aircraft, groundLayout, hsResume.HoldShorts);
                if (!addHs.Success)
                {
                    return addHs;
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
                var preClear = GroundCommandHandler.TryPreClearRouteCrossings(aircraft, groundResume.CrossRunways);
                if (!preClear.Success)
                {
                    return preClear;
                }
                var addHs = GroundCommandHandler.TryAddExplicitHoldShorts(aircraft, groundLayout, groundResume.HoldShorts);
                if (!addHs.Success)
                {
                    return addHs;
                }
                var resumeResult = GroundCommandHandler.TryResumeTaxi(aircraft);
                if (!resumeResult.Success)
                {
                    return resumeResult;
                }
                return Ok(CommandDescriber.DescribeNatural(groundResume));
            }
            case CrossRunwayCommand cross:
                return GroundCommandHandler.TryCrossRunway(aircraft, cross);
            case HoldShortCommand hs:
                return GroundCommandHandler.TryHoldShort(aircraft, hs, groundLayout);
            case AssignRunwayCommand assignRwy:
                return GroundCommandHandler.TryAssignRunway(aircraft, assignRwy.RunwayId);
            case FollowGroundCommand followG:
                return GroundCommandHandler.TryFollow(aircraft, followG, groundLayout);
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
        };
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

        // Find which command indices to keep
        var keepIndices = new List<int>();
        for (int i = 0; i < block.Commands.Count; i++)
        {
            if ((CommandDescriber.GetDimension(block.Commands[i].Type) & conflictingDims) == 0)
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

        // Runtime state the surviving commands cannot describe: a partially-elapsed wait, and the guard that
        // stops an already-dispatched track command from firing twice. Both belong to the block being replaced.
        rebuilt.WaitRemainingSeconds = block.WaitRemainingSeconds;
        rebuilt.WaitRemainingDistanceNm = block.WaitRemainingDistanceNm;
        rebuilt.TrackApplied = block.TrackApplied;

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
        // FOLLOW is VFR-only — IFR traffic uses CVA FOLLOW for visual separation.
        if (!aircraft.FlightPlan.IsVfr)
        {
            return new CommandResult(false, "FOLLOW only available for VFR aircraft");
        }

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
        bool followerOnPatternLeg = current is PatternEntryPhase or UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase or FinalApproachPhase;
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

        // If the follower is already in VfrFollowPhase, retarget in place.
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
}
