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

public record CommandResult(bool Success, string? Message = null, CanonicalCommandType? RejectedCommandType = null);

public static class CommandDispatcher
{
    /// <summary>
    /// Sentinel returned by DispatchWithPhase to signal that phases should be cleared,
    /// but only AFTER validation succeeds. This avoids mutating PhaseList before we know
    /// the command is valid (the old approach saved a reference to the PhaseList, but
    /// Clear() mutated it in place, making restore impossible).
    /// </summary>
    private static readonly CommandResult PhaseShouldBeCleared = new(true, "__CLEAR_PHASES__");

    /// <summary>
    /// Sentinel marker for the "no dispatcher arm" message. Embedded in the user-visible
    /// failure text so callers can detect the case via a substring check without using a
    /// private side-channel. The full message also includes the command type and natural
    /// description so the user (or maintainer reading a bug report) can identify the gap.
    /// </summary>
    private const string NoDispatcherArmMarker = "__NO_DISPATCHER_ARM__";

    private static readonly ILogger Log = SimLog.CreateLogger("CommandDispatcher");

    public static CommandResult DispatchCompound(CompoundCommand compound, AircraftState aircraft, DispatchContext ctx)
    {
        // A successful command issued to a ground aircraft is itself evidence of
        // established controller-pilot contact (the pilot read back the clearance
        // the controller spoke). Setting this here covers every dispatch path —
        // user-typed (RoomEngine.SendCommandAsync) and replay (RecordingManager) —
        // without each call site re-implementing the gate. Suppresses the spurious
        // post-takeoff airborne check-in for departures cleared during taxi.
        var wasOnGround = aircraft.IsOnGround;
        var result = DispatchCompoundCore(compound, aircraft, ctx);
        if (result.Success && wasOnGround)
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
        var deferredResult = TryDeferLeadingWait(compound, aircraft);
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
                    var phasePreserved = ClearConflictingBlocks(aircraft, phaseIncomingDims, ctx, out var phaseDropped);
                    EmitQueueClearWarning(aircraft, phaseDropped, compound);
                    aircraft.DeferredDispatches.Clear();

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

                    var remainingMessages =
                        remainingBlocks.Count > 0
                            ? EnqueueBlocks(new CompoundCommand(remainingBlocks) { SourceText = compound.SourceText }, 0, aircraft, ctx)
                            : new List<string>();
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
            string? clearedSummary = aircraft.Phases is { } pl ? PhaseClearSummary.Build(pl) : null;
            aircraft.Phases?.Clear(phaseCtx);
            aircraft.Phases = null;
            aircraft.Targets.TurnRateOverride = null;
            aircraft.Targets.HasExplicitTurnRate = false;
            AirborneFollowHelper.ClearFollowState(aircraft);

            if (clearedSummary is not null)
            {
                var src = compound.SourceText ?? CommandDescriber.DescribeNatural(compound.Blocks[0].Commands[0]);
                aircraft.PendingWarnings.Add($"{aircraft.Callsign} {clearedSummary} cancelled by {src}");
            }
        }

        // Selectively clear queue: remove only blocks whose dimensions conflict with the
        // incoming command. Non-conflicting pending blocks are preserved and re-appended
        // after the new blocks.
        var incomingDims = CommandDescriber.GetCompoundDimensions(compound);
        var preserved = ClearConflictingBlocks(aircraft, incomingDims, ctx, out var dropped);
        EmitQueueClearWarning(aircraft, dropped, compound);
        aircraft.DeferredDispatches.Clear();

        int firstNewBlockIdx = aircraft.Queue.Blocks.Count;
        var messages = EnqueueBlocks(compound, 0, aircraft, ctx);
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
                // subsequent SA/MNA blocks would otherwise sit in the queue forever
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
                        if (parsedCmd is not (MakeShortApproachCommand or MakeNormalApproachCommand))
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

        // Selectively clear queue: remove only blocks whose dimensions conflict
        var singleDims = CommandDescriber.GetCommandDimension(command);
        var singlePreserved = ClearConflictingBlocks(aircraft, singleDims, ctx, out var singleDropped);
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
        "IFR aircraft can only receive a bare CTO (follow SID) or CTO with an assigned heading; pattern/relative modifiers (MRC, ML*, RH, OC, DCT, MLT, MRT, ...) require VFR"
    );

    /// <summary>
    /// IFR departure clearances accept only <see cref="DefaultDeparture"/> (follow SID) or
    /// <see cref="FlyHeadingDeparture"/> (assigned numeric heading). Pattern-relative
    /// modifiers (MRC, ML*, RH, OC, DCT, MLT/MRT) are VFR-only and must be rejected so
    /// the aircraft doesn't peel off runway heading immediately at liftoff. Returns null
    /// if the command is allowed for this aircraft.
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

        if (departure is null or DefaultDeparture or FlyHeadingDeparture)
        {
            return null;
        }

        return IfrCtoVfrModifierResult;
    }

    private static CommandResult ApplyCommand(ParsedCommand command, AircraftState aircraft, DispatchContext ctx)
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
            case WaitCommand cmd:
                return Ok($"Wait {cmd.Seconds} seconds");
            case WaitDistanceCommand cmd:
                return Ok($"Wait {cmd.DistanceNm} nm");
            case SayCommand sayCmd:
                ctx.TerminalEmitter?.Invoke(new TerminalEntry("Say", aircraft.Callsign, sayCmd.Text));
                return Ok("");
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
                var (eappProc, _, _) = eappResolved;
                aircraft.Approach.Expected = eappProc.ApproachId;
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
                return ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft);
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
                    finalDistanceNm: null
                );
            case EnterRightDownwindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Downwind,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null
                );
            case EnterLeftCrosswindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Crosswind,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null
                );
            case EnterRightCrosswindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Crosswind,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null
                );
            case EnterLeftBaseCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Base,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: cmd.FinalDistanceNm
                );
            case EnterRightBaseCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Base,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: cmd.FinalDistanceNm
                );
            case EnterFinalCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Final,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: null
                );
            case PatternSizeCommand cmd:
                return PatternCommandHandler.TrySetPatternSize(aircraft, cmd.SizeNm);
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
            case LandAndHoldShortCommand lahso:
                return PatternCommandHandler.TryLandAndHoldShort(lahso, aircraft, aircraft.Ground.Layout);
            case CancelLandingClearanceCommand:
                return PatternCommandHandler.TryCancelLandingClearance(aircraft);
            case GoAroundCommand ga:
                return PatternCommandHandler.TryGoAround(ga, aircraft);
            case MakeLeftTrafficCommand mlt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, mlt.RunwayId, mlt.Altitude);
            case MakeRightTrafficCommand mrt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, mrt.RunwayId, mrt.Altitude);
            case MakeLeft360Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Left, 360);
            case MakeRight360Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Right, 360);
            case MakeLeft270Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Left, 270);
            case MakeRight270Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Right, 270);

            case FollowCommand follow:
                return TryAirborneFollow(aircraft, follow);

            // --- Flight plan ---
            case CancelIfrCommand:
                if (aircraft.FlightPlan.IsVfr)
                {
                    return new CommandResult(false, "Aircraft is already VFR");
                }
                aircraft.FlightPlan.FlightRules = "VFR";
                aircraft.FlightPlan.CruiseAltitude = 0;
                return Ok("IFR cancelled, aircraft is now VFR");

            case UnsupportedCommand cmd:
                return new CommandResult(false, $"Command not yet supported: {cmd.RawText}");

            default:
                return new CommandResult(
                    false,
                    $"{NoDispatcherArmMarker} no dispatcher arm for {command.GetType().Name} ({CommandDescriber.DescribeNatural(command)})"
                );
        }
    }

    /// <summary>
    /// Validates all commands in a compound by applying them sequentially on a
    /// snapshot clone of the aircraft. Returns the first error, or null if all
    /// commands are valid. The real aircraft is never mutated.
    /// </summary>
    private static CommandResult? DryRunValidate(CompoundCommand compound, AircraftState aircraft, DispatchContext ctx)
    {
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

        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                var result = DryRunApplyCommand(cmd, clone, dryCtx);
                if (!result.Success)
                {
                    return WithRejectedCommand(result, cmd);
                }
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
        if (result.Message is null || !result.Message.StartsWith(NoDispatcherArmMarker, StringComparison.Ordinal))
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
    /// first block (e.g. <c>ERD 28R</c>), can be applied immediately rather than
    /// enqueued. These commands modify the just-installed pattern phases (arming a
    /// pending downwind for short approach, etc.) and would otherwise sit in the
    /// command queue forever — <see cref="FlightPhysics"/>.UpdateCommandQueue
    /// short-circuits while any phase is active.
    /// </summary>
    private static bool IsImmediatePhaseModifierBlock(ParsedBlock block)
    {
        if (block.Condition is not null || block.Commands.Count != 1)
        {
            return false;
        }

        return block.Commands[0] is MakeShortApproachCommand or MakeNormalApproachCommand;
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
    private static CommandResult? TryDeferLeadingWait(CompoundCommand compound, AircraftState aircraft)
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
            deferred = new DeferredDispatch(waitCmd.Seconds, payload) { SourceText = compound.SourceText };
            timerDesc = $"{waitCmd.Seconds}s";
        }
        else
        {
            deferred = new DeferredDispatch(payload, waitDistCmd!.DistanceNm) { SourceText = compound.SourceText };
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
        aircraft.DeferredDispatches.Add(new DeferredDispatch(payload, gw.TargetCallsign) { SourceText = compound.SourceText });
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

        // Extract the first command to check acceptance
        var firstCmd = compound.Blocks[0].Commands[0];

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

            // Dispatch remaining parallel commands in the same block (e.g. CLAND after EF 28L,
            // or CROSS after TAXI). Collect every per-command message so the RPO sees the full
            // outcome — without this, CLAND's "Cleared to land 28L" would be silently dropped
            // and the user would think only the EF took effect.
            var messages = new List<string>();
            if (!string.IsNullOrEmpty(towerResult.Message))
            {
                messages.Add(towerResult.Message);
            }
            var block = compound.Blocks[0];
            for (int i = 1; i < block.Commands.Count; i++)
            {
                var subResult = TryApplyTowerCommand(block.Commands[i], aircraft, aircraft.Phases?.CurrentPhase ?? currentPhase, ctx);
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
                    return WithRejectedCommand(new CommandResult(false, combinedFail), block.Commands[i]);
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

        // Allowed but not a tower command — shouldn't normally reach here
        return null;
    }

    private static bool IsPhaseTransparentCommand(CanonicalCommandType cmd) =>
        cmd switch
        {
            CanonicalCommandType.ReportFieldInSight => true,
            CanonicalCommandType.ReportFieldInSightForced => true,
            CanonicalCommandType.ReportTrafficInSight => true,
            CanonicalCommandType.ReportTrafficInSightForced => true,
            CanonicalCommandType.SafetyAlert => true,
            CanonicalCommandType.WakeAdvisory => true,
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
        var groundLayout = ctx.GroundLayout;
        var autoCrossRunway = ctx.AutoCrossRunway;
        if (RequiresVfr(command) && !aircraft.FlightPlan.IsVfr)
        {
            return VfrRequiredResult;
        }

        if (CheckIfrDepartureCompatibility(command, aircraft) is { } ifrReject)
        {
            return ifrReject;
        }

        switch (command)
        {
            case ClearedForTakeoffCommand cto:
                if (currentPhase is LinedUpAndWaitingPhase luaw)
                {
                    return DepartureClearanceHandler.TryClearedForTakeoff(cto, aircraft, luaw);
                }
                return DepartureClearanceHandler.TryDepartureClearance(
                    aircraft,
                    currentPhase,
                    ClearanceType.ClearedForTakeoff,
                    cto.Departure,
                    cto.AssignedAltitude,
                    Log
                );

            case CancelTakeoffClearanceCommand:
                return DepartureClearanceHandler.TryCancelTakeoff(aircraft, currentPhase);

            case LineUpAndWaitCommand:
                return DepartureClearanceHandler.TryDepartureClearance(
                    aircraft,
                    currentPhase,
                    ClearanceType.LineUpAndWait,
                    new DefaultDeparture(),
                    null,
                    Log
                );

            case ClearedToLandCommand ctl:
                return PatternCommandHandler.TryClearedToLand(ctl, aircraft);

            case LandAndHoldShortCommand lahso:
                return PatternCommandHandler.TryLandAndHoldShort(lahso, aircraft, groundLayout);

            case CancelLandingClearanceCommand:
                return PatternCommandHandler.TryCancelLandingClearance(aircraft);

            case GoAroundCommand ga:
                return PatternCommandHandler.TryGoAround(ga, aircraft);

            // Pattern entry commands
            case EnterLeftDownwindCommand eld:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Downwind,
                    runwayId: eld.RunwayId,
                    finalDistanceNm: null
                );
            case EnterRightDownwindCommand erd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Downwind,
                    runwayId: erd.RunwayId,
                    finalDistanceNm: null
                );
            case EnterLeftCrosswindCommand elc:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Crosswind,
                    runwayId: elc.RunwayId,
                    finalDistanceNm: null
                );
            case EnterRightCrosswindCommand erc:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Crosswind,
                    runwayId: erc.RunwayId,
                    finalDistanceNm: null
                );
            case EnterLeftBaseCommand elb:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Base,
                    runwayId: elb.RunwayId,
                    finalDistanceNm: elb.FinalDistanceNm
                );
            case EnterRightBaseCommand erb:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Base,
                    runwayId: erb.RunwayId,
                    finalDistanceNm: erb.FinalDistanceNm
                );
            case EnterFinalCommand ef:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Final,
                    runwayId: ef.RunwayId,
                    finalDistanceNm: null
                );

            // Pattern modification commands
            case MakeLeftTrafficCommand mlt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, mlt.RunwayId, mlt.Altitude);
            case MakeRightTrafficCommand mrt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, mrt.RunwayId, mrt.Altitude);
            case TurnCrosswindCommand:
                return PatternCommandHandler.TryPatternTurnTo<UpwindPhase>(aircraft, "crosswind");
            case TurnDownwindCommand:
                return PatternCommandHandler.TryPatternTurnTo<CrosswindPhase>(aircraft, "downwind");
            case TurnBaseCommand:
                return PatternCommandHandler.TryPatternTurnBase(aircraft);
            case ExtendPatternCommand ext:
                return PatternCommandHandler.TryExtendPattern(aircraft, ext.Leg);
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
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, null, null);
            case PatternSizeCommand ps:
                return PatternCommandHandler.TrySetPatternSize(aircraft, ps.SizeNm);
            case MakeNormalApproachCommand:
                return PatternCommandHandler.TryMakeNormalApproach(aircraft);
            case Cancel270Command:
                return PatternCommandHandler.TryCancel270(aircraft);
            case MakeLeftSTurnsCommand mls:
                return PatternCommandHandler.TryMakeSTurns(aircraft, TurnDirection.Left, mls.Count);
            case MakeRightSTurnsCommand mrs:
                return PatternCommandHandler.TryMakeSTurns(aircraft, TurnDirection.Right, mrs.Count);
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

            // Hold/resume during air taxi (airborne, so ground handler's IsOnGround check would reject)
            case HoldPositionCommand when currentPhase is AirTaxiPhase:
                aircraft.Ground.Hold = HoldDirective.HoldPosition;
                return Ok("Hold position");
            case ResumeCommand airTaxiResume when currentPhase is AirTaxiPhase:
            {
                var preClear = GroundCommandHandler.TryPreClearRouteCrossings(aircraft, airTaxiResume.CrossRunways);
                if (!preClear.Success)
                {
                    return preClear;
                }
                aircraft.Ground.Hold = null;
                return Ok("Resume taxi");
            }
            case ResumeCommand hsResume
                when currentPhase
                    is HoldingShortPhase { HoldShort.Reason: HoldShortReason.ExplicitHoldShort or HoldShortReason.RunwayCrossing } holdShort:
            {
                var preClear = GroundCommandHandler.TryPreClearRouteCrossings(aircraft, hsResume.CrossRunways);
                if (!preClear.Success)
                {
                    return preClear;
                }
                holdShort.SatisfyClearance(ClearanceType.RunwayCrossing);
                return Ok("Resume taxi");
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
                return GroundCommandHandler.TryResumeTaxi(aircraft);
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
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Left, Taxiway = el.Taxiway }, el.NoDelete);
            case ExitRightCommand er:
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Right, Taxiway = er.Taxiway }, er.NoDelete);
            case ExitTaxiwayCommand et:
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Taxiway = et.Taxiway }, et.NoDelete);

            case BreakConflictCommand:
                return GroundCommandHandler.TryBreakConflict(aircraft);
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
            FieldElevation = runway?.ElevationFt ?? 0,
            GroundLayout = groundLayout,
            Logger = Log,
        };
    }

    internal static RunwayInfo? ResolveRunway(AircraftState aircraft, string runwayId)
    {
        var navDb = NavigationDatabase.Instance;

        // Treat empty strings (VFR local traffic with no flight plan) the same as null.
        // Fall back to the ground layout airport ID so CTO works for aircraft with no departure/destination.
        var airportId =
            !string.IsNullOrEmpty(aircraft.FlightPlan.Departure) ? aircraft.FlightPlan.Departure
            : !string.IsNullOrEmpty(aircraft.FlightPlan.Destination) ? aircraft.FlightPlan.Destination
            : aircraft.Ground.Layout?.AirportId;

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
        return runway is not null ? $", Runway {runway.Designator}" : "";
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
    /// reported, since the queued instruction survived in modified form.
    /// </summary>
    private static List<CommandBlock> ClearConflictingBlocks(
        AircraftState aircraft,
        CommandDimension incomingDimensions,
        DispatchContext ctx,
        out List<string> droppedDescriptions
    )
    {
        var queue = aircraft.Queue;
        droppedDescriptions = [];

        // Fast path: All/None → clear everything (original behavior)
        if ((incomingDimensions & CommandDimension.All) == CommandDimension.All || incomingDimensions == CommandDimension.None)
        {
            int fastStart = queue.CurrentBlockIndex + (queue.CurrentBlock is { IsApplied: true } ? 1 : 0);
            for (int i = fastStart; i < queue.Blocks.Count; i++)
            {
                droppedDescriptions.Add(DescribeQueueBlock(queue.Blocks[i]));
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
            var split = SplitBlockNonConflicting(block, incomingDimensions, ctx);
            if (split is null)
            {
                droppedDescriptions.Add(DescribeQueueBlock(block));
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

        // Rebuild a new block with only the non-conflicting commands
        var keptParsed = keepIndices.Select(i => block.ParsedCommands[i]).ToList();
        var keptTracked = keepIndices.Select(i => new TrackedCommand { Type = block.Commands[i].Type }).ToList();
        var keptDims = CommandDimension.None;
        foreach (var idx in keepIndices)
        {
            keptDims |= CommandDescriber.GetCommandDimension(block.ParsedCommands[idx]);
        }

        var desc = string.Join(", ", keptParsed.Select(CommandDescriber.DescribeCommand));
        var natural = string.Join(", ", keptParsed.Select(CommandDescriber.DescribeNatural));

        return new CommandBlock
        {
            Trigger = block.Trigger,
            ApplyAction = BuildApplyAction(keptParsed, ctx),
            ParsedCommands = keptParsed,
            Commands = keptTracked,
            Dimensions = keptDims,
            Description = desc,
            NaturalDescription = natural,
            IsWaitBlock = block.IsWaitBlock,
            WaitRemainingSeconds = block.WaitRemainingSeconds,
            WaitRemainingDistanceNm = block.WaitRemainingDistanceNm,
            SourceCommandText = block.SourceCommandText,
        };
    }

    private static List<string> EnqueueBlocks(CompoundCommand compound, int startIndex, AircraftState aircraft, DispatchContext ctx)
    {
        var messages = new List<string>();

        for (int i = startIndex; i < compound.Blocks.Count; i++)
        {
            var parsedBlock = compound.Blocks[i];

            var blockDesc = string.Join(", ", parsedBlock.Commands.Select(CommandDescriber.DescribeCommand));
            var blockMsg = string.Join(", ", parsedBlock.Commands.Select(CommandDescriber.DescribeNatural));

            if (parsedBlock.Condition is LevelCondition lv)
            {
                blockDesc = $"at {lv.Altitude}ft: {blockDesc}";
                blockMsg = $"At {lv.Altitude:N0} ft: {blockMsg}";
            }
            else if (parsedBlock.Condition is AtFixCondition at)
            {
                var atLabel = FormatAtLabel(at);
                blockDesc = $"at {atLabel}: {blockDesc}";
                blockMsg = $"At {atLabel}: {blockMsg}";
            }
            else if (parsedBlock.Condition is AtGroundEntityCondition ge)
            {
                var geLabel = FormatGroundLabel(ge);
                blockDesc = $"at {geLabel}: {blockDesc}";
                blockMsg = $"At {geLabel}: {blockMsg}";
            }
            else if (parsedBlock.Condition is GiveWayCondition gw)
            {
                blockDesc = $"giveway {gw.TargetCallsign}: {blockDesc}";
                blockMsg = $"After {gw.TargetCallsign} passes: {blockMsg}";
            }
            else if (parsedBlock.Condition is DistanceFinalCondition df)
            {
                blockDesc = $"at {df.DistanceNm}nm final: {blockDesc}";
                blockMsg = $"At {df.DistanceNm}nm final: {blockMsg}";
            }

            var waitTime = parsedBlock.Commands.OfType<WaitCommand>().FirstOrDefault();
            var waitDist = parsedBlock.Commands.OfType<WaitDistanceCommand>().FirstOrDefault();
            bool isWait = waitTime is not null || waitDist is not null;
            var trigger = ConvertCondition(parsedBlock.Condition, aircraft, ctx);
            if (trigger is null && parsedBlock.Condition is AtGroundEntityCondition unresolved)
            {
                aircraft.PendingWarnings.Add($"AT ground entity not found: {FormatGroundLabel(unresolved)}");
                continue;
            }
            var commandBlock = new CommandBlock
            {
                Trigger = trigger,
                ApplyAction = BuildApplyAction(parsedBlock.Commands, ctx),
                ParsedCommands = parsedBlock.Commands.ToList(),
                Description = blockDesc,
                NaturalDescription = blockMsg,
                IsWaitBlock = isWait,
                WaitRemainingSeconds = waitTime?.Seconds ?? 0,
                WaitRemainingDistanceNm = waitDist?.DistanceNm ?? 0,
                SourceCommandText = compound.SourceText,
            };

            var blockDims = CommandDimension.None;
            foreach (var cmd in parsedBlock.Commands)
            {
                commandBlock.Commands.Add(new TrackedCommand { Type = CommandDescriber.ClassifyCommand(cmd) });
                blockDims |= CommandDescriber.GetCommandDimension(cmd);
            }

            commandBlock.Dimensions = blockDims;

            aircraft.Queue.Blocks.Add(commandBlock);
            messages.Add(blockMsg);
        }

        return messages;
    }

    /// <summary>
    /// Builds a deferred action that applies all commands in a block to the aircraft.
    /// This is stored on the CommandBlock and executed when the block becomes active.
    /// Captures the dispatch context by reference so triggered commands see the same
    /// weather, ground layout, and aircraft lookup as the original dispatch.
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
                var result = ApplyCommand(cmd, ac, ctx);
                if (!result.Success)
                {
                    return WithRejectedCommand(result, cmd);
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

        if (result.Message?.Contains(NoDispatcherArmMarker, StringComparison.Ordinal) == true)
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

    private static CommandResult TryAirborneFollow(AircraftState aircraft, FollowCommand follow)
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

        // RTIS gate: a pilot cannot follow traffic they haven't visually acquired.
        // Matches CVA FOLLOW behavior — controllers can force this with RTISF.
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

        // If the follower is already in a pattern phase, just update the target —
        // AirborneFollowHelper handles spacing on every pattern leg. Rebuilding
        // through VfrFollowPhase here would route the follower back through
        // PatternEntry for the same runway it's already flying — wasteful and
        // confusing. Also clear any prior EXT (extended leg) on Upwind/Crosswind/
        // Downwind: FOLLOW supersedes the controller's hold-and-call-the-next-leg
        // instruction since the pilot now has explicit traffic to sequence behind.
        var current = aircraft.Phases?.CurrentPhase;
        if (current is PatternEntryPhase or UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase or FinalApproachPhase)
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
            AirborneFollowHelper.ResetRunawayTracking(aircraft);
            return Ok($"Follow {target}");
        }

        // If the follower is already in VfrFollowPhase, retarget in place.
        if (current is VfrFollowPhase vfp)
        {
            vfp.UpdateTarget(target);
            aircraft.Approach.FollowingCallsign = target;
            AirborneFollowHelper.ResetRunawayTracking(aircraft);
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
        AirborneFollowHelper.ResetRunawayTracking(aircraft);
        return Ok($"Follow {target}");
    }
}
