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
    private static readonly ILogger Log = SimLog.CreateLogger("CommandDispatcher");

    public static CommandResult DispatchCompound(
        CompoundCommand compound,
        AircraftState aircraft,
        IRunwayLookup? runways,
        AirportGroundLayout? groundLayout,
        IFixLookup? fixes,
        Random rng,
        IApproachLookup? approachLookup,
        IProcedureLookup? procedureLookup,
        bool validateDctFixes,
        bool autoCrossRunway = false
    )
    {
        // Leading WAIT → deferred dispatch: extract the timer and store the remaining
        // blocks as a deferred payload. The payload dispatches fresh when the timer expires,
        // without touching phases or the command queue.
        var deferredResult = TryDeferLeadingWait(
            compound,
            aircraft,
            runways,
            groundLayout,
            fixes,
            rng,
            approachLookup,
            procedureLookup,
            validateDctFixes,
            autoCrossRunway
        );
        if (deferredResult is not null)
        {
            return deferredResult;
        }

        // Phase interaction: check if aircraft has active phases
        if (aircraft.Phases?.CurrentPhase is { } currentPhase)
        {
            var result = DispatchWithPhase(compound, aircraft, currentPhase, runways, groundLayout, fixes, rng, procedureLookup, autoCrossRunway);
            if (result is not null)
            {
                return result;
            }
            // result is null means phases were cleared, fall through to normal dispatch
        }

        // Reject tower commands that require phase context
        // (pattern entry commands with an explicit runway can self-resolve)
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                if (CommandDescriber.IsTowerCommand(cmd) && !IsPatternEntryWithRunway(cmd))
                {
                    return new CommandResult(false, $"{CommandDescriber.DescribeNatural(cmd)} requires an active runway assignment");
                }

                if (CommandDescriber.IsGroundCommand(cmd) && !aircraft.IsOnGround)
                {
                    return new CommandResult(false, $"{CommandDescriber.DescribeNatural(cmd)} requires the aircraft to be on the ground");
                }
            }
        }

        // Clear any existing queue
        aircraft.Queue.Blocks.Clear();
        aircraft.Queue.CurrentBlockIndex = 0;

        var messages = new List<string>();

        for (int i = 0; i < compound.Blocks.Count; i++)
        {
            var parsedBlock = compound.Blocks[i];

            // Terse description for block tracking
            var blockDesc = string.Join(", ", parsedBlock.Commands.Select(CommandDescriber.DescribeCommand));

            // Natural language for response message
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
                ApplyAction = BuildApplyAction(
                    parsedBlock.Commands,
                    aircraft,
                    rng,
                    fixes,
                    approachLookup,
                    runways,
                    procedureLookup,
                    validateDctFixes
                ),
                Description = blockDesc,
                NaturalDescription = blockMsg,
                IsWaitBlock = isWait,
                WaitRemainingSeconds = waitTime?.Seconds ?? 0,
                WaitRemainingDistanceNm = waitDist?.DistanceNm ?? 0,
            };

            foreach (var cmd in parsedBlock.Commands)
            {
                commandBlock.Commands.Add(new TrackedCommand { Type = CommandDescriber.ClassifyCommand(cmd) });
            }

            aircraft.Queue.Blocks.Add(commandBlock);
            messages.Add(blockMsg);
        }

        // Apply the first block immediately (if no trigger or trigger already met)
        var firstBlock = aircraft.Queue.CurrentBlock;
        if (firstBlock is not null)
        {
            if (firstBlock.Trigger is null)
            {
                ApplyBlock(firstBlock, aircraft);
            }
            // If there's a trigger, the physics tick will check and apply when met
        }

        var fullMessage = string.Join(" ; ", messages);
        return new CommandResult(true, fullMessage);
    }

    public static CommandResult Dispatch(
        ParsedCommand command,
        AircraftState aircraft,
        IRunwayLookup? runways,
        AirportGroundLayout? groundLayout,
        IFixLookup? fixes,
        Random rng,
        IApproachLookup? approachLookup,
        IProcedureLookup? procedureLookup,
        bool validateDctFixes,
        bool autoCrossRunway = false
    )
    {
        // Route ground commands through DispatchCompound for phase interaction
        if (CommandDescriber.IsGroundCommand(command))
        {
            var compound = new CompoundCommand([new ParsedBlock(null, [command])]);
            return DispatchCompound(
                compound,
                aircraft,
                runways,
                groundLayout,
                fixes,
                rng,
                approachLookup,
                procedureLookup,
                validateDctFixes,
                autoCrossRunway
            );
        }

        // Clear any existing queue when a new single command is issued
        aircraft.Queue.Blocks.Clear();
        aircraft.Queue.CurrentBlockIndex = 0;

        bool hadProcedure = aircraft.ActiveSidId is not null || aircraft.ActiveStarId is not null;
        var result = ApplyCommand(command, aircraft, rng, fixes, approachLookup, runways, procedureLookup, validateDctFixes);
        CheckVectoringWarning(aircraft, [command], hadProcedure);
        return result;
    }

    private static CommandResult ApplyCommand(
        ParsedCommand command,
        AircraftState aircraft,
        Random rng,
        IFixLookup? fixes,
        IApproachLookup? approachLookup,
        IRunwayLookup? runways,
        IProcedureLookup? procedureLookup,
        bool validateDctFixes
    )
    {
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
                return FlightCommandHandler.ApplyDirectTo(cmd, aircraft, fixes, approachLookup, validateDctFixes);
            case ForceDirectToCommand cmd:
                return FlightCommandHandler.ApplyForceDirectTo(cmd, aircraft, fixes);
            case AppendDirectToCommand cmd:
                return FlightCommandHandler.ApplyAppendDirectTo(cmd, aircraft, fixes, approachLookup, validateDctFixes);
            case AppendForceDirectToCommand cmd:
                return FlightCommandHandler.ApplyAppendForceDirectTo(cmd, aircraft, fixes);

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
                var eappResolved = ApproachCommandHandler.ResolveApproach(eapp.ApproachId, eapp.AirportCode, aircraft, approachLookup, runways);
                if (!eappResolved.Success)
                {
                    return new CommandResult(false, eappResolved.Error);
                }
                var (eappProc, _, _) = eappResolved;
                aircraft.ExpectedApproach = eappProc.ApproachId;
                return Ok($"Expecting {eappProc.ApproachId} approach");
            }

            case ListApproachesCommand cmd:
                return NavigationCommandHandler.DispatchListApproaches(cmd, aircraft, approachLookup);
            case JoinStarCommand cmd:
                return NavigationCommandHandler.DispatchJarr(cmd, aircraft, fixes, procedureLookup);
            case JoinAirwayCommand cmd:
                return NavigationCommandHandler.DispatchJawy(cmd, aircraft, fixes);
            case HoldingPatternCommand cmd:
                return NavigationCommandHandler.DispatchHoldingPattern(cmd, aircraft);
            case JoinFinalApproachCourseCommand cmd:
                return NavigationCommandHandler.DispatchJfac(cmd, aircraft, approachLookup, runways);

            // --- Approach commands ---
            case ClearedApproachCommand cmd:
                return ApproachCommandHandler.TryClearedApproach(cmd, aircraft, approachLookup, runways, fixes);
            case JoinApproachCommand cmd:
                return ApproachCommandHandler.TryJoinApproach(
                    cmd.ApproachId,
                    cmd.AirportCode,
                    cmd.Force,
                    straightIn: false,
                    aircraft,
                    approachLookup,
                    runways,
                    fixes
                );
            case ClearedApproachStraightInCommand cmd:
                return ApproachCommandHandler.TryJoinApproach(
                    cmd.ApproachId,
                    cmd.AirportCode,
                    force: false,
                    straightIn: true,
                    aircraft,
                    approachLookup,
                    runways,
                    fixes
                );
            case JoinApproachStraightInCommand cmd:
                return ApproachCommandHandler.TryJoinApproach(
                    cmd.ApproachId,
                    cmd.AirportCode,
                    force: false,
                    straightIn: true,
                    aircraft,
                    approachLookup,
                    runways,
                    fixes
                );
            case PositionTurnAltitudeClearanceCommand cmd:
                return ApproachCommandHandler.TryPtac(cmd, aircraft, approachLookup, runways, fixes);
            case ClearedVisualApproachCommand cmd:
                return ApproachCommandHandler.TryClearedVisualApproach(cmd, aircraft, runways);
            case ReportFieldInSightCommand:
                return NavigationCommandHandler.DispatchReportFieldInSight(aircraft);
            case ReportTrafficInSightCommand cmd:
                return NavigationCommandHandler.DispatchReportTrafficInSight(aircraft, cmd.TargetCallsign);

            // --- Pattern entry commands ---
            case EnterLeftDownwindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Downwind,
                    runwayId: cmd.RunwayId,
                    runways: runways
                );
            case EnterRightDownwindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Downwind,
                    runwayId: cmd.RunwayId,
                    runways: runways
                );
            case EnterLeftCrosswindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Crosswind,
                    runwayId: cmd.RunwayId,
                    runways: runways
                );
            case EnterRightCrosswindCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Crosswind,
                    runwayId: cmd.RunwayId,
                    runways: runways
                );
            case EnterLeftBaseCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Base,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: cmd.FinalDistanceNm,
                    runways: runways
                );
            case EnterRightBaseCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Base,
                    runwayId: cmd.RunwayId,
                    finalDistanceNm: cmd.FinalDistanceNm,
                    runways: runways
                );
            case EnterFinalCommand cmd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Final,
                    runwayId: cmd.RunwayId,
                    runways: runways
                );
            case PatternSizeCommand cmd:
                return PatternCommandHandler.TrySetPatternSize(aircraft, cmd.SizeNm);
            case Plan270Command:
                return PatternCommandHandler.TryPlan270(aircraft);

            case UnsupportedCommand cmd:
                return new CommandResult(false, $"Command not yet supported: {cmd.RawText}");

            default:
                return new CommandResult(false, "Unknown command");
        }
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
    private static CommandResult? TryDeferLeadingWait(
        CompoundCommand compound,
        AircraftState aircraft,
        IRunwayLookup? runways,
        AirportGroundLayout? groundLayout,
        IFixLookup? fixes,
        Random rng,
        IApproachLookup? approachLookup,
        IProcedureLookup? procedureLookup,
        bool validateDctFixes,
        bool autoCrossRunway
    )
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
        var siblings = firstBlock.Commands.Where(c => c != (ParsedCommand?)waitCmd && c != (ParsedCommand?)waitDistCmd).ToList();
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
        var payloadDesc = string.Join(" ; ", payloadBlocks.Select(b => string.Join(", ", b.Commands.Select(CommandDescriber.DescribeNatural))));

        DeferredDispatch deferred;
        string timerDesc;
        if (waitCmd is not null)
        {
            deferred = new DeferredDispatch(waitCmd.Seconds, payload);
            timerDesc = $"{waitCmd.Seconds}s";
        }
        else
        {
            deferred = new DeferredDispatch(payload, waitDistCmd!.DistanceNm);
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
        IRunwayLookup? runways,
        AirportGroundLayout? groundLayout,
        IFixLookup? fixes,
        Random rng,
        IProcedureLookup? procedureLookup,
        bool autoCrossRunway = false
    )
    {
        // Extract the first command to check acceptance
        var firstCmd = compound.Blocks[0].Commands[0];
        var cmdType = CommandDescriber.ToCanonicalType(firstCmd);

        // Try tower/ground-specific handling first (phase-interactive commands)
        var towerResult = TryApplyTowerCommand(firstCmd, aircraft, currentPhase, runways, groundLayout, fixes, procedureLookup, autoCrossRunway);
        if (towerResult is not null)
        {
            if (towerResult.Success)
            {
                // Dispatch remaining parallel commands in the same block (e.g. CROSS after TAXI)
                var block = compound.Blocks[0];
                for (int i = 1; i < block.Commands.Count; i++)
                {
                    TryApplyTowerCommand(
                        block.Commands[i],
                        aircraft,
                        aircraft.Phases?.CurrentPhase ?? currentPhase,
                        runways,
                        groundLayout,
                        fixes,
                        procedureLookup,
                        autoCrossRunway
                    );
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
            // End the active phase properly, then exit the phase system
            var ctx = BuildMinimalContext(aircraft);
            aircraft.Phases?.Clear(ctx);
            aircraft.Phases = null;
            aircraft.Targets.TurnRateOverride = null;
            return null;
        }

        // Allowed but not a tower command — shouldn't normally reach here
        return null;
    }

    private static CommandResult? TryApplyTowerCommand(
        ParsedCommand command,
        AircraftState aircraft,
        Phase currentPhase,
        IRunwayLookup? runways,
        AirportGroundLayout? groundLayout,
        IFixLookup? fixes,
        IProcedureLookup? procedureLookup,
        bool autoCrossRunway = false
    )
    {
        switch (command)
        {
            case ClearedForTakeoffCommand cto:
                if (currentPhase is LinedUpAndWaitingPhase luaw)
                {
                    return DepartureClearanceHandler.TryClearedForTakeoff(cto, aircraft, luaw, fixes, procedureLookup, runways);
                }
                return DepartureClearanceHandler.TryDepartureClearance(
                    aircraft,
                    currentPhase,
                    ClearanceType.ClearedForTakeoff,
                    cto.Departure,
                    cto.AssignedAltitude,
                    runways,
                    fixes,
                    Log,
                    procedureLookup
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
                    runways,
                    fixes,
                    Log,
                    procedureLookup
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
                    runways: runways
                );
            case EnterRightDownwindCommand erd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Downwind,
                    runwayId: erd.RunwayId,
                    runways: runways
                );
            case EnterLeftCrosswindCommand elc:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Crosswind,
                    runwayId: elc.RunwayId,
                    runways: runways
                );
            case EnterRightCrosswindCommand erc:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Crosswind,
                    runwayId: erc.RunwayId,
                    runways: runways
                );
            case EnterLeftBaseCommand elb:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Base,
                    runwayId: elb.RunwayId,
                    finalDistanceNm: elb.FinalDistanceNm,
                    runways: runways
                );
            case EnterRightBaseCommand erb:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Base,
                    runwayId: erb.RunwayId,
                    finalDistanceNm: erb.FinalDistanceNm,
                    runways: runways
                );
            case EnterFinalCommand ef:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Final,
                    runwayId: ef.RunwayId,
                    runways: runways
                );

            // Pattern modification commands
            case MakeLeftTrafficCommand mlt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left, mlt.RunwayId, runways);
            case MakeRightTrafficCommand mrt:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, mrt.RunwayId, runways);
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
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left);
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

            case SequenceCommand seq:
                return PatternCommandHandler.TrySetSequence(seq, aircraft);

            // Option approach / special ops commands
            case TouchAndGoCommand:
                return PatternCommandHandler.TrySetupTouchAndGo(aircraft);
            case StopAndGoCommand:
                return PatternCommandHandler.TrySetupStopAndGo(aircraft);
            case LowApproachCommand:
                return PatternCommandHandler.TrySetupLowApproach(aircraft);
            case ClearedForOptionCommand:
                return PatternCommandHandler.TrySetLandingClearance(
                    aircraft,
                    ClearanceType.ClearedForOption,
                    $"Cleared for the option{RunwayLabel(aircraft)}"
                );

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
                return GroundCommandHandler.TryTaxi(aircraft, taxi, groundLayout, runways, autoCrossRunway);
            case HoldPositionCommand:
                return GroundCommandHandler.TryHoldPosition(aircraft);
            case ResumeCommand:
                return GroundCommandHandler.TryResumeTaxi(aircraft);
            case CrossRunwayCommand cross:
                return GroundCommandHandler.TryCrossRunway(aircraft, cross);
            case HoldShortCommand hs:
                return GroundCommandHandler.TryHoldShort(aircraft, hs, groundLayout);
            case AssignRunwayCommand assignRwy:
                return GroundCommandHandler.TryAssignRunway(aircraft, assignRwy.RunwayId, runways);
            case FollowCommand follow:
                return GroundCommandHandler.TryFollow(aircraft, follow, groundLayout);
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

    internal static RunwayInfo? ResolveRunway(AircraftState aircraft, string runwayId, IRunwayLookup? runways)
    {
        if (runways is null)
        {
            return null;
        }

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
        var result = runways.GetRunway(airportId, parsed.End1) ?? runways.GetRunway(airportId, parsed.End2);
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

    private static void ClearActiveProcedure(AircraftState aircraft)
    {
        aircraft.ActiveSidId = null;
        aircraft.ActiveStarId = null;
        aircraft.SidViaMode = false;
        aircraft.StarViaMode = false;
        aircraft.SidViaCeiling = null;
        aircraft.StarViaFloor = null;
        aircraft.DepartureRunway = null;
        aircraft.DestinationRunway = null;
    }

    private static string AltitudeVerb(AircraftState aircraft, int targetAltitude)
    {
        return aircraft.Altitude > targetAltitude ? "Descend and maintain" : "Climb and maintain";
    }

    internal static CommandResult Ok(string message)
    {
        return new CommandResult(true, message);
    }

    private static void ApplyBlock(CommandBlock block, AircraftState aircraft)
    {
        block.IsApplied = true;
        block.ApplyAction?.Invoke(aircraft);

        foreach (var cmd in block.Commands)
        {
            if (cmd.Type == TrackedCommandType.Immediate)
            {
                cmd.IsComplete = true;
            }
        }
    }

    /// <summary>
    /// Builds a deferred action that applies all commands in a block to the aircraft.
    /// This is stored on the CommandBlock and executed when the block becomes active.
    /// </summary>
    private static Action<AircraftState> BuildApplyAction(
        List<ParsedCommand> commands,
        AircraftState aircraft,
        Random rng,
        IFixLookup? fixes,
        IApproachLookup? approachLookup,
        IRunwayLookup? runways,
        IProcedureLookup? procedureLookup,
        bool validateDctFixes
    )
    {
        // Capture the parsed commands; they'll be applied when the block activates
        var captured = commands.ToList();
        return ac =>
        {
            bool hadProcedure = ac.ActiveSidId is not null || ac.ActiveStarId is not null;

            foreach (var cmd in captured)
            {
                ApplyCommand(
                    cmd,
                    ac,
                    rng,
                    fixes,
                    approachLookup,
                    runways: runways,
                    procedureLookup: procedureLookup,
                    validateDctFixes: validateDctFixes
                );
            }

            CheckVectoringWarning(ac, captured, hadProcedure);
        };
    }

    /// <summary>
    /// Warns when an aircraft is vectored off a procedure (SID/STAR) without both
    /// a heading and an altitude assignment in the same block.
    /// </summary>
    private static void CheckVectoringWarning(AircraftState aircraft, List<ParsedCommand> commands, bool hadProcedure)
    {
        if (!hadProcedure)
        {
            return;
        }

        // Procedure was cleared if all SID/STAR identifiers are now null
        if (aircraft.ActiveSidId is not null || aircraft.ActiveStarId is not null)
        {
            return;
        }

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
        );

        if (!hasHeadingCmd)
        {
            return;
        }

        bool hasAltCmd = commands.Any(c => c is ClimbMaintainCommand or DescendMaintainCommand);
        if (!hasAltCmd)
        {
            aircraft.PendingWarnings.Add("Vectored off procedure without an altitude assignment");
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
            _ => null,
        };
    }

    private static BlockTrigger ConvertFrdCondition(AtFixCondition at, int radial, int dist)
    {
        var (targetLat, targetLon) = GeoMath.ProjectPoint(at.Lat, at.Lon, radial, dist);
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
}
