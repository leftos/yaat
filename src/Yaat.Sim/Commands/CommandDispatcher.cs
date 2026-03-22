using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

public record CommandResult(bool Success, string? Message = null);

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

    public static CommandResult DispatchCompound(
        CompoundCommand compound,
        AircraftState aircraft,
        AirportGroundLayout? groundLayout,
        Random rng,
        bool validateDctFixes,
        bool autoCrossRunway = false
    )
    {
        // Leading WAIT → deferred dispatch: extract the timer and store the remaining
        // blocks as a deferred payload. The payload dispatches fresh when the timer expires,
        // without touching phases or the command queue.
        var deferredResult = TryDeferLeadingWait(compound, aircraft);
        if (deferredResult is not null)
        {
            return deferredResult;
        }

        // Phase-transparent commands (squawk, ident, say, etc.) — apply directly
        // without consulting phases, clearing the queue, or clearing deferred dispatches.
        if (aircraft.Phases?.CurrentPhase is not null && IsAllTransparent(compound))
        {
            return ApplyTransparentCompound(compound, aircraft, rng);
        }

        // Phase interaction: check if aircraft has active phases
        bool shouldClearPhases = false;
        if (aircraft.Phases?.CurrentPhase is { } currentPhase)
        {
            var result = DispatchWithPhase(compound, aircraft, currentPhase, groundLayout, autoCrossRunway);
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
                // up once CurrentPhase becomes null).
                if (result.Success && compound.Blocks.Count > 1)
                {
                    aircraft.Queue.Blocks.Clear();
                    aircraft.Queue.CurrentBlockIndex = 0;
                    aircraft.DeferredDispatches.Clear();

                    var remainingMessages = EnqueueBlocks(compound, 1, aircraft, rng, validateDctFixes);
                    if (remainingMessages.Count > 0)
                    {
                        var combined = result.Message + "; then " + string.Join("; then ", remainingMessages);
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
        var dryRunError = DryRunValidate(compound, aircraft, groundLayout);
        if (dryRunError is not null)
        {
            return dryRunError;
        }

        // Now that validation passed, clear phases if the command requires it
        if (shouldClearPhases)
        {
            var ctx = BuildMinimalContext(aircraft);
            aircraft.Phases?.Clear(ctx);
            aircraft.Phases = null;
            aircraft.Targets.TurnRateOverride = null;
        }

        // Clear any existing queue and pending deferred dispatches
        aircraft.Queue.Blocks.Clear();
        aircraft.Queue.CurrentBlockIndex = 0;
        aircraft.DeferredDispatches.Clear();

        var messages = EnqueueBlocks(compound, 0, aircraft, rng, validateDctFixes);

        // Apply the first block immediately (if no trigger or trigger already met)
        var firstBlock = aircraft.Queue.CurrentBlock;
        if (firstBlock is not null)
        {
            if (firstBlock.Trigger is null)
            {
                var applyResult = ApplyBlock(firstBlock, aircraft);
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
                    messages[0] = firstBlock.NaturalDescription;
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

    private static CommandResult ApplyTransparentCompound(CompoundCommand compound, AircraftState aircraft, Random rng)
    {
        var messages = new List<string>();
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                var result = ApplyCommand(cmd, aircraft, rng, false);
                if (!result.Success)
                {
                    return result;
                }

                if (result.Message is not null)
                {
                    messages.Add(result.Message);
                }
            }
        }

        return new CommandResult(true, string.Join(", ", messages));
    }

    public static CommandResult Dispatch(
        ParsedCommand command,
        AircraftState aircraft,
        AirportGroundLayout? groundLayout,
        Random rng,
        bool validateDctFixes,
        bool autoCrossRunway = false
    )
    {
        // Route ground commands through DispatchCompound for phase interaction
        if (CommandDescriber.IsGroundCommand(command))
        {
            var compound = new CompoundCommand([new ParsedBlock(null, [command])]);
            return DispatchCompound(compound, aircraft, groundLayout, rng, validateDctFixes, autoCrossRunway);
        }

        // Phase-transparent commands: apply without clearing queue or phases
        if ((aircraft.Phases?.CurrentPhase is not null) && CommandDescriber.IsPhaseTransparent(CommandDescriber.ToCanonicalType(command)))
        {
            return ApplyCommand(command, aircraft, rng, validateDctFixes);
        }

        // Clear any existing queue when a new single command is issued
        aircraft.Queue.Blocks.Clear();
        aircraft.Queue.CurrentBlockIndex = 0;

        bool hadProcedure = aircraft.ActiveSidId is not null || aircraft.ActiveStarId is not null;
        bool hadViaMode = aircraft.SidViaMode || aircraft.StarViaMode;
        var result = ApplyCommand(command, aircraft, rng, validateDctFixes);
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
                or ExtendDownwindCommand
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

    private static CommandResult ApplyCommand(ParsedCommand command, AircraftState aircraft, Random rng, bool validateDctFixes)
    {
        if (RequiresVfr(command) && !aircraft.IsVfr)
        {
            return VfrRequiredResult;
        }

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

            // --- Misc ---
            case WaitCommand cmd:
                return Ok($"Wait {cmd.Seconds} seconds");
            case WaitDistanceCommand cmd:
                return Ok($"Wait {cmd.DistanceNm} nm");
            case SayCommand:
                return Ok(""); // SAY is a broadcast; handled before dispatch
            case SaySpeedCommand:
                return Ok(""); // SSPD is a broadcast; handled before dispatch
            case SayMachCommand:
                return Ok(""); // SMACH is a broadcast; handled before dispatch

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
                aircraft.ExpectedApproach = eappProc.ApproachId;
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
                return NavigationCommandHandler.DispatchReportFieldInSight(aircraft);
            case ReportFieldInSightForcedCommand:
                return NavigationCommandHandler.DispatchReportFieldInSightForced(aircraft);
            case ReportTrafficInSightCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficInSight(aircraft, cmd.TargetCallsign);
            case ReportTrafficInSightForcedCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficInSightForced(aircraft, cmd.TargetCallsign);

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
            case ClearedTakeoffPresentCommand:
                return DepartureClearanceHandler.TryClearedTakeoffPresent(aircraft, aircraft.GroundLayout);
            case AirTaxiCommand atxi:
                return GroundCommandHandler.TryAirTaxi(aircraft, atxi.Destination, aircraft.GroundLayout);
            case LandCommand land:
                return GroundCommandHandler.TryLand(aircraft, land, aircraft.GroundLayout);

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
                return PatternCommandHandler.TryLandAndHoldShort(lahso, aircraft, aircraft.GroundLayout);
            case CancelLandingClearanceCommand:
                return PatternCommandHandler.TryCancelLandingClearance(aircraft);
            case GoAroundCommand ga:
                return PatternCommandHandler.TryGoAround(ga, aircraft);
            case MakeLeftTrafficCommand mlt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, mlt.RunwayId);
            case MakeRightTrafficCommand mrt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, mrt.RunwayId);
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
                if (aircraft.IsVfr)
                {
                    return new CommandResult(false, "Aircraft is already VFR");
                }
                aircraft.FlightRules = "VFR";
                aircraft.CruiseAltitude = 0;
                return Ok("IFR cancelled, aircraft is now VFR");

            case UnsupportedCommand cmd:
                return new CommandResult(false, $"Command not yet supported: {cmd.RawText}");

            default:
                return new CommandResult(false, "Unknown command");
        }
    }

    /// <summary>
    /// Validates all commands in a compound by applying them sequentially on a
    /// snapshot clone of the aircraft. Returns the first error, or null if all
    /// commands are valid. The real aircraft is never mutated.
    /// </summary>
    private static CommandResult? DryRunValidate(CompoundCommand compound, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        var clone = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout);
        var dryRng = new Random(0);

        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                var result = DryRunApplyCommand(cmd, clone, groundLayout, dryRng);
                if (!result.Success)
                {
                    return result;
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
    private static CommandResult DryRunApplyCommand(ParsedCommand cmd, AircraftState clone, AirportGroundLayout? groundLayout, Random rng)
    {
        // Try the tower-command path first if phases are active — it handles
        // CTO, CLAND, LUAW, go-around, pattern turns, etc.
        var currentPhase = clone.Phases?.CurrentPhase;
        if (currentPhase is not null)
        {
            var towerResult = TryApplyTowerCommand(cmd, clone, currentPhase, groundLayout);
            if (towerResult is not null)
            {
                return towerResult;
            }
        }

        // Then try ApplyCommand — handles flight, nav, pattern entry, etc.
        var result = ApplyCommand(cmd, clone, rng, false);
        if (result.Message != "Unknown command")
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
    /// Handles command dispatch when aircraft has an active phase.
    /// Returns a result if the command was handled (accepted or rejected),
    /// or null if phases were cleared and normal dispatch should proceed.
    /// </summary>
    private static CommandResult? DispatchWithPhase(
        CompoundCommand compound,
        AircraftState aircraft,
        Phase currentPhase,
        AirportGroundLayout? groundLayout,
        bool autoCrossRunway = false
    )
    {
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

        // Try tower/ground-specific handling first (phase-interactive commands)
        var towerResult = TryApplyTowerCommand(firstCmd, aircraft, currentPhase, groundLayout, autoCrossRunway);
        if (towerResult is not null)
        {
            if (towerResult.Success)
            {
                // Dispatch remaining parallel commands in the same block (e.g. CROSS after TAXI)
                var block = compound.Blocks[0];
                for (int i = 1; i < block.Commands.Count; i++)
                {
                    TryApplyTowerCommand(block.Commands[i], aircraft, aircraft.Phases?.CurrentPhase ?? currentPhase, groundLayout, autoCrossRunway);
                }
            }

            return towerResult;
        }

        // Check standard command acceptance against the current phase
        var acceptance = currentPhase.CanAcceptCommand(cmdType);

        if (acceptance == CommandAcceptance.Rejected)
        {
            return new CommandResult(false, $"Cannot accept {CommandDescriber.DescribeNatural(firstCmd)} during {currentPhase.Name}");
        }

        if (acceptance == CommandAcceptance.ClearsPhase)
        {
            // Don't clear phases yet — return a sentinel so DispatchCompound can validate
            // the command first. If validation fails, phases stay intact.
            return PhaseShouldBeCleared;
        }

        // Allowed but not a tower command — shouldn't normally reach here
        return null;
    }

    private static CommandResult? TryApplyTowerCommand(
        ParsedCommand command,
        AircraftState aircraft,
        Phase currentPhase,
        AirportGroundLayout? groundLayout,
        bool autoCrossRunway = false
    )
    {
        if (RequiresVfr(command) && !aircraft.IsVfr)
        {
            return VfrRequiredResult;
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
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, mlt.RunwayId);
            case MakeRightTrafficCommand mrt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, mrt.RunwayId);
            case TurnCrosswindCommand:
                return PatternCommandHandler.TryPatternTurnTo<UpwindPhase>(aircraft, "crosswind");
            case TurnDownwindCommand:
                return PatternCommandHandler.TryPatternTurnTo<CrosswindPhase>(aircraft, "downwind");
            case TurnBaseCommand:
                return PatternCommandHandler.TryPatternTurnBase(aircraft);
            case ExtendDownwindCommand:
                return PatternCommandHandler.TryExtendPattern(aircraft);
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
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, null);
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
                aircraft.IsHeld = true;
                return Ok("Hold position");
            case ResumeCommand when currentPhase is AirTaxiPhase:
                aircraft.IsHeld = false;
                return Ok("Resume taxi");
            case ResumeCommand when currentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.ExplicitHoldShort } holdShort:
                holdShort.SatisfyClearance(ClearanceType.RunwayCrossing);
                return Ok("Resume taxi");

            // Helicopter commands
            case AirTaxiCommand atxi:
                return GroundCommandHandler.TryAirTaxi(aircraft, atxi.Destination, groundLayout);
            case LandCommand land:
                return GroundCommandHandler.TryLand(aircraft, land, groundLayout);

            case ClearedTakeoffPresentCommand:
                return DepartureClearanceHandler.TryClearedTakeoffPresent(aircraft, groundLayout);

            // Ground commands
            case PushbackCommand push:
                return GroundCommandHandler.TryPushback(aircraft, push, groundLayout);
            case TaxiCommand taxi:
                return GroundCommandHandler.TryTaxi(aircraft, taxi, groundLayout, autoCrossRunway);
            case HoldPositionCommand:
                return GroundCommandHandler.TryHoldPosition(aircraft);
            case ResumeCommand:
                return GroundCommandHandler.TryResumeTaxi(aircraft);
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
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Left }, el.NoDelete);
            case ExitRightCommand er:
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Right }, er.NoDelete);
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
        if (!string.IsNullOrWhiteSpace(aircraft.Destination))
        {
            string dest = aircraft.Destination;
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
            !string.IsNullOrEmpty(aircraft.Departure) ? aircraft.Departure
            : !string.IsNullOrEmpty(aircraft.Destination) ? aircraft.Destination
            : aircraft.GroundLayout?.AirportId;

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
                if (string.Equals(edge.TaxiwayName, taxiway1, StringComparison.OrdinalIgnoreCase))
                {
                    hasTwy1 = true;
                }

                if (string.Equals(edge.TaxiwayName, taxiway2, StringComparison.OrdinalIgnoreCase))
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
    private static List<string> EnqueueBlocks(CompoundCommand compound, int startIndex, AircraftState aircraft, Random rng, bool validateDctFixes)
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
            var commandBlock = new CommandBlock
            {
                Trigger = ConvertCondition(parsedBlock.Condition),
                ApplyAction = BuildApplyAction(parsedBlock.Commands, rng, validateDctFixes),
                Description = blockDesc,
                NaturalDescription = blockMsg,
                IsWaitBlock = isWait,
                WaitRemainingSeconds = waitTime?.Seconds ?? 0,
                WaitRemainingDistanceNm = waitDist?.DistanceNm ?? 0,
                SourceCommandText = compound.SourceText,
            };

            foreach (var cmd in parsedBlock.Commands)
            {
                commandBlock.Commands.Add(new TrackedCommand { Type = CommandDescriber.ClassifyCommand(cmd) });
            }

            aircraft.Queue.Blocks.Add(commandBlock);
            messages.Add(blockMsg);
        }

        return messages;
    }

    /// <summary>
    /// Builds a deferred action that applies all commands in a block to the aircraft.
    /// This is stored on the CommandBlock and executed when the block becomes active.
    /// </summary>
    private static Func<AircraftState, CommandResult> BuildApplyAction(List<ParsedCommand> commands, Random rng, bool validateDctFixes)
    {
        // Capture the parsed commands; they'll be applied when the block activates
        var captured = commands.ToList();
        return ac =>
        {
            bool hadProcedure = ac.ActiveSidId is not null || ac.ActiveStarId is not null;
            bool hadViaMode = ac.SidViaMode || ac.StarViaMode;
            var messages = new List<string>();

            foreach (var cmd in captured)
            {
                var result = ApplyCommand(cmd, ac, rng, validateDctFixes);
                if (!result.Success)
                {
                    return result;
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
        bool procedureCleared = aircraft.ActiveSidId is null && aircraft.ActiveStarId is null;

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
        if (hadViaMode && !aircraft.SidViaMode && !aircraft.StarViaMode)
        {
            bool hasViaCmd = commands.Any(c => c is ClimbViaCommand or DescendViaCommand);
            if (!hasAltCmd && !hasViaCmd)
            {
                aircraft.PendingWarnings.Add("Vectored off procedure without an altitude assignment");
                FlightCommandHandler.LevelOff(aircraft);
            }
        }
    }

    private static BlockTrigger? ConvertCondition(BlockCondition? condition)
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
            GiveWayCondition gw => new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = gw.TargetCallsign },
            DistanceFinalCondition df => new BlockTrigger { Type = BlockTriggerType.DistanceFinal, DistanceFinalNm = df.DistanceNm },
            OnHandoffCondition => new BlockTrigger { Type = BlockTriggerType.OnHandoff },
            _ => null,
        };
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

    private static CommandResult TryAirborneFollow(AircraftState aircraft, FollowCommand follow)
    {
        if (aircraft.Phases is null || aircraft.Phases.CurrentPhase is null)
        {
            return new CommandResult(false, "No active approach or pattern");
        }

        aircraft.FollowingCallsign = follow.TargetCallsign;
        return Ok($"Follow {follow.TargetCallsign}");
    }
}
