using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

public record CommandResult(bool Success, string? Message = null);

public static class CommandDispatcher
{
    public static CommandResult DispatchCompound(
        CompoundCommand compound,
        AircraftState aircraft,
        IRunwayLookup? runways,
        AirportGroundLayout? groundLayout,
        IFixLookup? fixes,
        ILogger logger
    )
    {
        // Phase interaction: check if aircraft has active phases
        if (aircraft.Phases?.CurrentPhase is { } currentPhase)
        {
            var result = DispatchWithPhase(compound, aircraft, currentPhase, runways, groundLayout, fixes, logger);
            if (result is not null)
            {
                return result;
            }
            // result is null means phases were cleared, fall through to normal dispatch
        }

        // Reject tower commands that require phase context
        foreach (var block in compound.Blocks)
        {
            foreach (var cmd in block.Commands)
            {
                if (CommandDescriber.IsTowerCommand(cmd))
                {
                    return new CommandResult(false, $"{CommandDescriber.DescribeNatural(cmd)} requires an active runway assignment");
                }

                if (CommandDescriber.IsGroundCommand(cmd))
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

            var waitTime = parsedBlock.Commands.OfType<WaitCommand>().FirstOrDefault();
            var waitDist = parsedBlock.Commands.OfType<WaitDistanceCommand>().FirstOrDefault();
            bool isWait = waitTime is not null || waitDist is not null;
            var commandBlock = new CommandBlock
            {
                Trigger = ConvertCondition(parsedBlock.Condition),
                ApplyAction = BuildApplyAction(parsedBlock.Commands, aircraft, fixes),
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
        ILogger logger
    )
    {
        // Route ground commands through DispatchCompound for phase interaction
        if (CommandDescriber.IsGroundCommand(command))
        {
            var compound = new CompoundCommand([new ParsedBlock(null, [command])]);
            return DispatchCompound(compound, aircraft, runways, groundLayout, fixes, logger);
        }

        // Clear any existing queue when a new single command is issued
        aircraft.Queue.Blocks.Clear();
        aircraft.Queue.CurrentBlockIndex = 0;

        return ApplyCommand(command, aircraft, fixes);
    }

    private static CommandResult ApplyCommand(ParsedCommand command, AircraftState aircraft, IFixLookup? fixes = null)
    {
        switch (command)
        {
            case FlyHeadingCommand cmd:
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = cmd.Heading;
                aircraft.Targets.PreferredTurnDirection = null;
                return Ok($"Fly heading {cmd.Heading:000}");

            case TurnLeftCommand cmd:
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = cmd.Heading;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
                return Ok($"Turn left heading {cmd.Heading:000}");

            case TurnRightCommand cmd:
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = cmd.Heading;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
                return Ok($"Turn right heading {cmd.Heading:000}");

            case LeftTurnCommand cmd:
                aircraft.Targets.NavigationRoute.Clear();
                var leftHdg = FlightPhysics.NormalizeHeadingInt(aircraft.Heading - cmd.Degrees);
                aircraft.Targets.TargetHeading = leftHdg;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
                return Ok($"Turn {cmd.Degrees} degrees left, heading {leftHdg:000}");

            case RightTurnCommand cmd:
                aircraft.Targets.NavigationRoute.Clear();
                var rightHdg = FlightPhysics.NormalizeHeadingInt(aircraft.Heading + cmd.Degrees);
                aircraft.Targets.TargetHeading = rightHdg;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
                return Ok($"Turn {cmd.Degrees} degrees right, heading {rightHdg:000}");

            case FlyPresentHeadingCommand:
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = FlightPhysics.NormalizeHeading(aircraft.Heading);
                aircraft.Targets.PreferredTurnDirection = null;
                return Ok("Fly present heading");

            case ClimbMaintainCommand cmd:
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                return Ok($"{AltitudeVerb(aircraft, cmd.Altitude)} {cmd.Altitude}");

            case DescendMaintainCommand cmd:
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                return Ok($"{AltitudeVerb(aircraft, cmd.Altitude)} {cmd.Altitude}");

            case SpeedCommand cmd:
                aircraft.Targets.TargetSpeed = cmd.Speed == 0 ? null : cmd.Speed;
                return cmd.Speed == 0 ? Ok("Resume normal speed") : Ok($"Speed {cmd.Speed}");

            case DirectToCommand cmd:
                aircraft.Targets.NavigationRoute.Clear();
                var resolved = cmd.Fixes.ToList();
                int originalCount = resolved.Count;
                if (fixes is not null)
                {
                    RouteChainer.AppendRouteRemainder(resolved, aircraft.Route, fixes);
                }
                foreach (var fix in resolved)
                {
                    aircraft.Targets.NavigationRoute.Add(
                        new NavigationTarget
                        {
                            Name = fix.Name,
                            Latitude = fix.Lat,
                            Longitude = fix.Lon,
                        }
                    );
                }
                var fixNames = string.Join(" ", cmd.Fixes.Select(f => f.Name));
                bool routeRejoined = resolved.Count > originalCount;
                return routeRejoined ? Ok($"Proceed direct {fixNames}, then filed route") : Ok($"Proceed direct {fixNames}");

            case SquawkCommand cmd:
                aircraft.BeaconCode = cmd.Code;
                return Ok($"Squawk {cmd.Code:D4}");

            case SquawkResetCommand:
                aircraft.BeaconCode = aircraft.AssignedBeaconCode;
                return Ok($"Squawk {aircraft.AssignedBeaconCode:D4}");

            case SquawkVfrCommand:
                aircraft.BeaconCode = 1200;
                return Ok("Squawk VFR");

            case SquawkNormalCommand:
                aircraft.TransponderMode = "C";
                return Ok("Squawk normal");

            case SquawkStandbyCommand:
                aircraft.TransponderMode = "Standby";
                return Ok("Squawk standby");

            case IdentCommand:
                aircraft.IsIdenting = true;
                return Ok("Ident");

            case RandomSquawkCommand:
                aircraft.BeaconCode = SimulationWorld.GenerateBeaconCode();
                return Ok($"Squawk {aircraft.BeaconCode:D4}");

            case WaitCommand cmd:
                return Ok($"Wait {cmd.Seconds} seconds");

            case WaitDistanceCommand cmd:
                return Ok($"Wait {cmd.DistanceNm} nm");

            case SayCommand:
                return Ok(""); // SAY is a broadcast; handled before dispatch

            case UnsupportedCommand cmd:
                return new CommandResult(false, $"Command not yet supported: {cmd.RawText}");

            default:
                return new CommandResult(false, "Unknown command");
        }
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
        ILogger logger
    )
    {
        // Extract the first command to check acceptance
        var firstCmd = compound.Blocks[0].Commands[0];
        var cmdType = CommandDescriber.ToCanonicalType(firstCmd);

        // Try tower/ground-specific handling first (phase-interactive commands)
        var towerResult = TryApplyTowerCommand(firstCmd, aircraft, currentPhase, runways, groundLayout, fixes, logger);
        if (towerResult is not null)
        {
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
            var ctx = BuildMinimalContext(aircraft, logger);
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
        ILogger logger
    )
    {
        switch (command)
        {
            case ClearedForTakeoffCommand cto:
                if (currentPhase is LinedUpAndWaitingPhase luaw)
                {
                    return DepartureClearanceHandler.TryClearedForTakeoff(cto, aircraft, luaw, fixes);
                }
                return DepartureClearanceHandler.TryDepartureClearance(
                    aircraft,
                    currentPhase,
                    ClearanceType.ClearedForTakeoff,
                    cto.Departure,
                    cto.AssignedAltitude,
                    runways,
                    fixes,
                    logger
                );

            case CancelTakeoffClearanceCommand:
                if (currentPhase is LinedUpAndWaitingPhase luawCancel)
                {
                    foreach (var req in luawCancel.Requirements)
                    {
                        if (req.Type == ClearanceType.ClearedForTakeoff)
                        {
                            req.IsSatisfied = false;
                        }
                    }
                    luawCancel.Departure = null;
                    luawCancel.AssignedAltitude = null;
                    return Ok($"Takeoff clearance cancelled, hold position{RunwayLabel(aircraft)}");
                }
                if (currentPhase is TakeoffPhase && aircraft.IsOnGround)
                {
                    // Abort takeoff during ground roll
                    var ctx = BuildMinimalContext(aircraft, logger);
                    aircraft.Phases?.Clear(ctx);
                    aircraft.Phases = null;
                    aircraft.Targets.TargetSpeed = 0;
                    return Ok("Abort takeoff, hold position");
                }
                return new CommandResult(false, "No takeoff clearance to cancel");

            case LineUpAndWaitCommand:
                return DepartureClearanceHandler.TryDepartureClearance(
                    aircraft,
                    currentPhase,
                    ClearanceType.LineUpAndWait,
                    new DefaultDeparture(),
                    null,
                    runways,
                    fixes,
                    logger
                );

            case ClearedToLandCommand ctl:
                if (aircraft.Phases is not null)
                {
                    aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
                    aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
                    aircraft.Phases.TrafficDirection = null;
                    ReplaceApproachEnding(aircraft.Phases, new LandingPhase());
                    if (ctl.NoDelete)
                    {
                        aircraft.AutoDeleteExempt = true;
                    }
                    return Ok($"Cleared to land{RunwayLabel(aircraft)}");
                }
                return new CommandResult(false, "Aircraft has no active phase sequence");

            case CancelLandingClearanceCommand:
                if (aircraft.Phases is not null && aircraft.Phases.LandingClearance is not null)
                {
                    aircraft.Phases.LandingClearance = null;
                    aircraft.Phases.ClearedRunwayId = null;
                    return Ok($"Landing clearance cancelled{RunwayLabel(aircraft)}");
                }
                return new CommandResult(false, "No landing clearance to cancel");

            case GoAroundCommand ga:
                if (aircraft.Phases is not null && !aircraft.Phases.IsComplete)
                {
                    if (ga.TrafficPattern is { } patDir)
                    {
                        aircraft.Phases.TrafficDirection = patDir;
                    }

                    bool isGaPattern = aircraft.Phases.TrafficDirection is not null;
                    var gaCtx = BuildMinimalContext(aircraft, logger);
                    int? gaTargetAlt = ga.TargetAltitude;

                    if (gaTargetAlt is null && isGaPattern)
                    {
                        double fieldElev = gaCtx.Runway?.ElevationFt ?? 0;
                        double patAgl = CategoryPerformance.PatternAltitudeAgl(gaCtx.Category);
                        gaTargetAlt = (int)(fieldElev + patAgl);
                    }

                    var goAround = new GoAroundPhase { AssignedHeading = ga.AssignedHeading, TargetAltitude = gaTargetAlt };
                    aircraft.Phases.ReplaceUpcoming([goAround]);
                    aircraft.Phases.AdvanceToNext(gaCtx);

                    var gaMsg = "Go around";
                    if (ga.TrafficPattern is PatternDirection.Left)
                    {
                        gaMsg += ", make left traffic";
                    }
                    else if (ga.TrafficPattern is PatternDirection.Right)
                    {
                        gaMsg += ", make right traffic";
                    }
                    if (ga.AssignedHeading is not null)
                    {
                        gaMsg += $", fly heading {ga.AssignedHeading:000}";
                    }
                    if (ga.TargetAltitude is not null)
                    {
                        gaMsg += $", climb to {ga.TargetAltitude:N0}";
                    }
                    return Ok(gaMsg);
                }
                return new CommandResult(false, "Go around not applicable");

            // Pattern entry commands
            case EnterLeftDownwindCommand eld:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Downwind,
                    logger,
                    runwayId: eld.RunwayId,
                    runways: runways
                );

            case EnterRightDownwindCommand erd:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Downwind,
                    logger,
                    runwayId: erd.RunwayId,
                    runways: runways
                );

            case EnterLeftBaseCommand elb:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Base,
                    logger,
                    runwayId: elb.RunwayId,
                    finalDistanceNm: elb.FinalDistanceNm,
                    runways: runways
                );

            case EnterRightBaseCommand erb:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Right,
                    PatternEntryLeg.Base,
                    logger,
                    runwayId: erb.RunwayId,
                    finalDistanceNm: erb.FinalDistanceNm,
                    runways: runways
                );

            case EnterFinalCommand ef:
                return PatternCommandHandler.TryEnterPattern(
                    aircraft,
                    PatternDirection.Left,
                    PatternEntryLeg.Final,
                    logger,
                    runwayId: ef.RunwayId,
                    runways: runways
                );

            // Pattern modification commands
            case MakeLeftTrafficCommand:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left);

            case MakeRightTrafficCommand:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right);

            case TurnCrosswindCommand:
                return PatternCommandHandler.TryPatternTurnTo<UpwindPhase>(aircraft, "crosswind", logger);

            case TurnDownwindCommand:
                return PatternCommandHandler.TryPatternTurnTo<CrosswindPhase>(aircraft, "downwind", logger);

            case TurnBaseCommand:
                return PatternCommandHandler.TryPatternTurnBase(aircraft, logger);

            case ExtendDownwindCommand:
                return PatternCommandHandler.TryExtendPattern(aircraft);

            case MakeShortApproachCommand:
                return PatternCommandHandler.TryMakeShortApproach(aircraft, logger);

            case MakeLeft360Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Left, 360, logger);
            case MakeRight360Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Right, 360, logger);
            case MakeLeft270Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Left, 270, logger);
            case MakeRight270Command:
                return PatternCommandHandler.TryMakeTurn(aircraft, TurnDirection.Right, 270, logger);

            case CircleAirportCommand:
                return PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Left);

            case SequenceCommand seq:
                aircraft.SequenceNumber = seq.Number;
                aircraft.FollowTarget = seq.FollowCallsign;
                var seqMsg = seq.FollowCallsign is not null
                    ? $"Number {seq.Number}, follow {seq.FollowCallsign}"
                    : $"Number {seq.Number} in sequence";
                return Ok(seqMsg);

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
                return PatternCommandHandler.TryHoldPresentPosition(aircraft, hpp.Direction, logger);

            case HoldPresentPositionHoverCommand:
                return PatternCommandHandler.TryHoldPresentPosition(aircraft, null, logger);

            case HoldAtFixOrbitCommand hfix:
                return PatternCommandHandler.TryHoldAtFix(aircraft, hfix.FixName, hfix.Lat, hfix.Lon, hfix.Direction, logger);

            case HoldAtFixHoverCommand hfixH:
                return PatternCommandHandler.TryHoldAtFix(aircraft, hfixH.FixName, hfixH.Lat, hfixH.Lon, null, logger);

            // Ground commands
            case PushbackCommand push:
                return GroundCommandHandler.TryPushback(aircraft, push, groundLayout, logger);

            case TaxiCommand taxi:
                return GroundCommandHandler.TryTaxi(aircraft, taxi, groundLayout, runways, logger);

            case HoldPositionCommand:
                return GroundCommandHandler.TryHoldPosition(aircraft);

            case ResumeCommand:
                return GroundCommandHandler.TryResumeTaxi(aircraft);

            case CrossRunwayCommand cross:
                return GroundCommandHandler.TryCrossRunway(aircraft, cross);

            case HoldShortCommand hs:
                return GroundCommandHandler.TryHoldShort(aircraft, hs, groundLayout, logger);

            case FollowCommand follow:
                return GroundCommandHandler.TryFollow(aircraft, follow, groundLayout, logger);

            case ExitLeftCommand el:
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Left }, el.NoDelete);

            case ExitRightCommand er:
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Right }, er.NoDelete);

            case ExitTaxiwayCommand et:
                return GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Taxiway = et.Taxiway }, et.NoDelete);

            default:
                return null;
        }
    }

    /// <summary>
    /// Replace the first pending approach-ending phase (LandingPhase,
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

            if (phase is LandingPhase or TouchAndGoPhase or StopAndGoPhase or LowApproachPhase)
            {
                phases.Phases[i] = replacement;
                return true;
            }
        }

        return false;
    }

    internal static PhaseContext BuildMinimalContext(AircraftState aircraft, ILogger logger, AirportGroundLayout? groundLayout = null)
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
            Logger = logger,
        };
    }

    internal static RunwayInfo? ResolveRunway(AircraftState aircraft, string runwayId, IRunwayLookup? runways)
    {
        if (runways is null)
        {
            return null;
        }

        var airportId = aircraft.Departure ?? aircraft.Destination;
        if (airportId is null)
        {
            return null;
        }

        // Hold-short runway IDs can be combined (e.g., "28R/10L").
        // Try each end until one resolves.
        var parsed = RunwayIdentifier.Parse(runwayId);
        var info = runways.GetRunway(airportId, parsed.End1);
        if (info is not null)
        {
            return info;
        }

        return runways.GetRunway(airportId, parsed.End2);
    }

    internal static string RunwayLabel(AircraftState aircraft)
    {
        var runway = aircraft.Phases?.AssignedRunway;
        return runway is not null ? $", Runway {runway.Designator}" : "";
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
    private static Action<AircraftState> BuildApplyAction(List<ParsedCommand> commands, AircraftState aircraft, IFixLookup? fixes = null)
    {
        // Capture the parsed commands; they'll be applied when the block activates
        var captured = commands.ToList();
        return ac =>
        {
            foreach (var cmd in captured)
            {
                ApplyCommand(cmd, ac, fixes);
            }
        };
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
