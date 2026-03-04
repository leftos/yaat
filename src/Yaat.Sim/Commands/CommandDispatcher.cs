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
    public static CommandResult DispatchCompound(
        CompoundCommand compound,
        AircraftState aircraft,
        IRunwayLookup? runways,
        AirportGroundLayout? groundLayout,
        IFixLookup? fixes,
        ILogger logger,
        IApproachLookup? approachLookup = null,
        IProcedureLookup? procedureLookup = null,
        bool validateDctFixes = true,
        bool autoCrossRunway = false
    )
    {
        // Phase interaction: check if aircraft has active phases
        if (aircraft.Phases?.CurrentPhase is { } currentPhase)
        {
            var result = DispatchWithPhase(compound, aircraft, currentPhase, runways, groundLayout, fixes, logger, procedureLookup, autoCrossRunway);
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
                ApplyAction = BuildApplyAction(parsedBlock.Commands, aircraft, fixes, approachLookup, runways, procedureLookup, validateDctFixes),
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
        ILogger logger,
        IApproachLookup? approachLookup = null,
        IProcedureLookup? procedureLookup = null,
        bool validateDctFixes = true,
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
                logger,
                approachLookup,
                procedureLookup,
                autoCrossRunway: autoCrossRunway
            );
        }

        // Clear any existing queue when a new single command is issued
        aircraft.Queue.Blocks.Clear();
        aircraft.Queue.CurrentBlockIndex = 0;

        bool hadProcedure = aircraft.ActiveSidId is not null || aircraft.ActiveStarId is not null;
        var result = ApplyCommand(command, aircraft, fixes, approachLookup, logger, runways, procedureLookup, validateDctFixes);
        CheckVectoringWarning(aircraft, [command], hadProcedure);
        return result;
    }

    private static CommandResult ApplyCommand(
        ParsedCommand command,
        AircraftState aircraft,
        IFixLookup? fixes = null,
        IApproachLookup? approachLookup = null,
        ILogger? logger = null,
        IRunwayLookup? runways = null,
        IProcedureLookup? procedureLookup = null,
        bool validateDctFixes = true
    )
    {
        switch (command)
        {
            case FlyHeadingCommand cmd:
                ClearActiveProcedure(aircraft);
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = cmd.Heading;
                aircraft.Targets.PreferredTurnDirection = null;
                return Ok($"Fly heading {cmd.Heading:000}");

            case TurnLeftCommand cmd:
                ClearActiveProcedure(aircraft);
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = cmd.Heading;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
                return Ok($"Turn left heading {cmd.Heading:000}");

            case TurnRightCommand cmd:
                ClearActiveProcedure(aircraft);
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = cmd.Heading;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
                return Ok($"Turn right heading {cmd.Heading:000}");

            case LeftTurnCommand cmd:
                ClearActiveProcedure(aircraft);
                aircraft.Targets.NavigationRoute.Clear();
                var leftHdg = FlightPhysics.NormalizeHeadingInt(aircraft.Heading - cmd.Degrees);
                aircraft.Targets.TargetHeading = leftHdg;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
                return Ok($"Turn {cmd.Degrees} degrees left, heading {leftHdg:000}");

            case RightTurnCommand cmd:
                ClearActiveProcedure(aircraft);
                aircraft.Targets.NavigationRoute.Clear();
                var rightHdg = FlightPhysics.NormalizeHeadingInt(aircraft.Heading + cmd.Degrees);
                aircraft.Targets.TargetHeading = rightHdg;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
                return Ok($"Turn {cmd.Degrees} degrees right, heading {rightHdg:000}");

            case FlyPresentHeadingCommand:
                ClearActiveProcedure(aircraft);
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = FlightPhysics.NormalizeHeading(aircraft.Heading);
                aircraft.Targets.PreferredTurnDirection = null;
                return Ok("Fly present heading");

            case ClimbMaintainCommand cmd:
                aircraft.SidViaMode = false;
                aircraft.SidViaCeiling = null;
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                return Ok($"{AltitudeVerb(aircraft, cmd.Altitude)} {cmd.Altitude}");

            case DescendMaintainCommand cmd:
                aircraft.StarViaMode = false;
                aircraft.StarViaFloor = null;
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                return Ok($"{AltitudeVerb(aircraft, cmd.Altitude)} {cmd.Altitude}");

            case SpeedCommand cmd:
            {
                aircraft.Targets.TargetSpeed = cmd.Speed == 0 ? null : cmd.Speed;
                if (cmd.Speed == 0)
                {
                    return Ok("Resume normal speed");
                }

                // Helicopter min radar speed warning per §5-7-3.e.5
                var spdCat = AircraftCategorization.Categorize(aircraft.AircraftType);
                if (spdCat == AircraftCategory.Helicopter && cmd.Speed > 0 && cmd.Speed < 60)
                {
                    aircraft.PendingWarnings.Add($"Speed {cmd.Speed} below helicopter minimum 60 KIAS [7110.65 §5-7-3.e.5]");
                }

                return Ok($"Speed {cmd.Speed}");
            }

            case DirectToCommand cmd:
            {
                if (validateDctFixes)
                {
                    var programmed = aircraft.GetProgrammedFixes(approachLookup);
                    if (programmed.Count > 0)
                    {
                        var unprogrammed = cmd.Fixes.Where(f => !programmed.Contains(f.Name)).ToList();
                        if (unprogrammed.Count > 0)
                        {
                            var names = string.Join(", ", unprogrammed.Select(f => f.Name));
                            return new CommandResult(false, $"Fix {names} not programmed — use DCTF to override");
                        }
                    }
                }

                ClearActiveProcedure(aircraft);
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
            }

            case ForceDirectToCommand fCmd:
            {
                ClearActiveProcedure(aircraft);
                aircraft.Targets.NavigationRoute.Clear();
                var fResolved = fCmd.Fixes.ToList();
                int fOriginalCount = fResolved.Count;
                if (fixes is not null)
                {
                    RouteChainer.AppendRouteRemainder(fResolved, aircraft.Route, fixes);
                }
                foreach (var fix in fResolved)
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
                var fFixNames = string.Join(" ", fCmd.Fixes.Select(f => f.Name));
                bool fRouteRejoined = fResolved.Count > fOriginalCount;
                return fRouteRejoined ? Ok($"Proceed direct {fFixNames}, then filed route") : Ok($"Proceed direct {fFixNames}");
            }

            case AppendDirectToCommand adct:
            {
                if (validateDctFixes)
                {
                    var adctProgrammed = aircraft.GetProgrammedFixes(approachLookup);
                    if (adctProgrammed.Count > 0)
                    {
                        var adctUnprogrammed = adct.Fixes.Where(f => !adctProgrammed.Contains(f.Name)).ToList();
                        if (adctUnprogrammed.Count > 0)
                        {
                            var adctBadNames = string.Join(", ", adctUnprogrammed.Select(f => f.Name));
                            return new CommandResult(false, $"Fix {adctBadNames} not programmed — use DCTF to override");
                        }
                    }
                }

                var adctResolved = adct.Fixes.ToList();
                int adctOriginal = adctResolved.Count;
                if (fixes is not null)
                {
                    RouteChainer.AppendRouteRemainder(adctResolved, aircraft.Route, fixes);
                }
                if (aircraft.Targets.NavigationRoute.Count == 0)
                {
                    foreach (var fix in adctResolved)
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
                    var adctNames = string.Join(" ", adct.Fixes.Select(f => f.Name));
                    return adctResolved.Count > adctOriginal
                        ? Ok($"Proceed direct {adctNames}, then filed route")
                        : Ok($"Proceed direct {adctNames}");
                }
                else
                {
                    foreach (var fix in adctResolved)
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
                    var adctAppended = string.Join(" ", adct.Fixes.Select(f => f.Name));
                    return adctResolved.Count > adctOriginal
                        ? Ok($"Then direct {adctAppended}, then filed route")
                        : Ok($"Then direct {adctAppended}");
                }
            }

            case AppendForceDirectToCommand adctf:
            {
                var adctfResolved = adctf.Fixes.ToList();
                int adctfOriginal = adctfResolved.Count;
                if (fixes is not null)
                {
                    RouteChainer.AppendRouteRemainder(adctfResolved, aircraft.Route, fixes);
                }
                if (aircraft.Targets.NavigationRoute.Count == 0)
                {
                    foreach (var fix in adctfResolved)
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
                    var adctfNames = string.Join(" ", adctf.Fixes.Select(f => f.Name));
                    return adctfResolved.Count > adctfOriginal
                        ? Ok($"Proceed direct {adctfNames}, then filed route")
                        : Ok($"Proceed direct {adctfNames}");
                }
                else
                {
                    foreach (var fix in adctfResolved)
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
                    var adctfAppended = string.Join(" ", adctf.Fixes.Select(f => f.Name));
                    return adctfResolved.Count > adctfOriginal
                        ? Ok($"Then direct {adctfAppended}, then filed route")
                        : Ok($"Then direct {adctfAppended}");
                }
            }

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

            // --- Approach/navigation commands (Chunks 5, 6, 7) ---

            case JoinRadialOutboundCommand jrado:
                return DispatchJrado(jrado, aircraft);

            case JoinRadialInboundCommand jradi:
                return DispatchJradi(jradi, aircraft);

            case DepartFixCommand depart:
                return DispatchDepartFix(depart, aircraft);

            case CrossFixCommand cfix:
                return DispatchCrossFix(cfix, aircraft);

            case ClimbViaCommand cvia:
                return DispatchClimbVia(cvia, aircraft);

            case DescendViaCommand dvia:
                return DispatchDescendVia(dvia, aircraft);

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

            case ListApproachesCommand apps:
                return DispatchListApproaches(apps, aircraft, approachLookup);

            case JoinStarCommand jarr:
                return DispatchJarr(jarr, aircraft, fixes, procedureLookup);

            case HoldingPatternCommand hold:
                return DispatchHoldingPattern(hold, aircraft, logger);

            case JoinFinalApproachCourseCommand jfac:
                return DispatchJfac(jfac, aircraft, approachLookup, runways, logger);

            case ClearedApproachCommand capp:
                return ApproachCommandHandler.TryClearedApproach(capp, aircraft, approachLookup, runways, fixes, logger);

            case JoinApproachCommand japp:
                return ApproachCommandHandler.TryJoinApproach(
                    japp.ApproachId,
                    japp.AirportCode,
                    japp.Force,
                    straightIn: false,
                    aircraft,
                    approachLookup,
                    runways,
                    fixes,
                    logger
                );

            case ClearedApproachStraightInCommand cappsi:
                return ApproachCommandHandler.TryJoinApproach(
                    cappsi.ApproachId,
                    cappsi.AirportCode,
                    force: false,
                    straightIn: true,
                    aircraft,
                    approachLookup,
                    runways,
                    fixes,
                    logger
                );

            case JoinApproachStraightInCommand jappsi:
                return ApproachCommandHandler.TryJoinApproach(
                    jappsi.ApproachId,
                    jappsi.AirportCode,
                    force: false,
                    straightIn: true,
                    aircraft,
                    approachLookup,
                    runways,
                    fixes,
                    logger
                );

            case PositionTurnAltitudeClearanceCommand ptac:
                return ApproachCommandHandler.TryPtac(ptac, aircraft, approachLookup, runways, logger);

            case ClearedVisualApproachCommand cva:
                return ApproachCommandHandler.TryClearedVisualApproach(cva, aircraft, runways, logger);

            case ReportFieldInSightCommand:
                return DispatchReportFieldInSight(aircraft);

            case ReportTrafficInSightCommand rtis:
                return DispatchReportTrafficInSight(aircraft, rtis.TargetCallsign);

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
        ILogger logger,
        IProcedureLookup? procedureLookup = null,
        bool autoCrossRunway = false
    )
    {
        // Extract the first command to check acceptance
        var firstCmd = compound.Blocks[0].Commands[0];
        var cmdType = CommandDescriber.ToCanonicalType(firstCmd);

        // Try tower/ground-specific handling first (phase-interactive commands)
        var towerResult = TryApplyTowerCommand(
            firstCmd,
            aircraft,
            currentPhase,
            runways,
            groundLayout,
            fixes,
            logger,
            procedureLookup,
            autoCrossRunway
        );
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
        ILogger logger,
        IProcedureLookup? procedureLookup = null,
        bool autoCrossRunway = false
    )
    {
        switch (command)
        {
            case ClearedForTakeoffCommand cto:
                if (currentPhase is LinedUpAndWaitingPhase luaw)
                {
                    return DepartureClearanceHandler.TryClearedForTakeoff(cto, aircraft, luaw, fixes, procedureLookup);
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
                    var isHeliCtl = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
                    Phase landingCtl = isHeliCtl ? new HelicopterLandingPhase() : new LandingPhase();
                    ReplaceApproachEnding(aircraft.Phases, landingCtl);
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

            // Hold/resume during air taxi (airborne, so ground handler's IsOnGround check would reject)
            case HoldPositionCommand when currentPhase is AirTaxiPhase:
                aircraft.IsHeld = true;
                return Ok("Hold position");

            case ResumeCommand when currentPhase is AirTaxiPhase:
                aircraft.IsHeld = false;
                return Ok("Resume taxi");

            // Helicopter commands
            case AirTaxiCommand atxi:
                return GroundCommandHandler.TryAirTaxi(aircraft, atxi.Destination, groundLayout, logger);

            case LandCommand land:
                return GroundCommandHandler.TryLand(aircraft, land, groundLayout, logger);

            case ClearedTakeoffPresentCommand:
            {
                var ctoppCat = AircraftCategorization.Categorize(aircraft.AircraftType);
                if (ctoppCat != AircraftCategory.Helicopter)
                {
                    return new CommandResult(false, "CTOPP is only valid for helicopters");
                }

                if (!aircraft.IsOnGround)
                {
                    return new CommandResult(false, "CTOPP requires the aircraft to be on the ground");
                }

                // Clear existing phases and set up vertical takeoff
                var ctoppCtx = BuildMinimalContext(aircraft, logger, groundLayout);
                if (aircraft.Phases is not null)
                {
                    aircraft.Phases.Clear(ctoppCtx);
                }

                aircraft.IsHeld = false;
                aircraft.Phases = new PhaseList();
                aircraft.Phases.Add(new Phases.Tower.HelicopterTakeoffPhase());
                aircraft.Phases.Add(new Phases.Tower.InitialClimbPhase { IsVfr = aircraft.IsVfr, CruiseAltitude = aircraft.CruiseAltitude });

                // Field elevation = current altitude (on ground)
                ctoppCtx = new PhaseContext
                {
                    Aircraft = aircraft,
                    Targets = aircraft.Targets,
                    Category = ctoppCat,
                    DeltaSeconds = 0,
                    Runway = null,
                    FieldElevation = aircraft.Altitude,
                    GroundLayout = groundLayout,
                    Logger = logger,
                };
                aircraft.Phases.Start(ctoppCtx);

                return Ok("Cleared for takeoff, present position");
            }

            // Ground commands
            case PushbackCommand push:
                return GroundCommandHandler.TryPushback(aircraft, push, groundLayout, logger);

            case TaxiCommand taxi:
                return GroundCommandHandler.TryTaxi(aircraft, taxi, groundLayout, runways, logger, autoCrossRunway);

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

    // --- Multi-block navigation command dispatchers ---

    private static CommandResult DispatchJrado(JoinRadialOutboundCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): fly present heading
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetHeading = FlightPhysics.NormalizeHeading(aircraft.Heading);
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on radial intercept, fly outbound heading
        var interceptBlock = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
                Radial = cmd.Radial,
            },
            ApplyAction = ac =>
            {
                ac.Targets.NavigationRoute.Clear();
                ac.Targets.TargetHeading = cmd.Radial;
                ac.Targets.PreferredTurnDirection = null;
            },
            Description = $"at {cmd.FixName} R{cmd.Radial:D3}: FH {cmd.Radial:D3}",
            NaturalDescription = $"On {cmd.FixName} {cmd.Radial:D3} radial: fly heading {cmd.Radial:D3}",
        };
        interceptBlock.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Heading });
        aircraft.Queue.Blocks.Add(interceptBlock);

        return Ok($"Fly present heading, intercept {cmd.FixName} {cmd.Radial:D3} radial outbound");
    }

    private static CommandResult DispatchJradi(JoinRadialInboundCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): fly present heading
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetHeading = FlightPhysics.NormalizeHeading(aircraft.Heading);
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on radial intercept, navigate inbound to fix
        var interceptBlock = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
                Radial = cmd.Radial,
            },
            ApplyAction = ac =>
            {
                ac.Targets.NavigationRoute.Clear();
                ac.Targets.NavigationRoute.Add(
                    new NavigationTarget
                    {
                        Name = cmd.FixName,
                        Latitude = cmd.FixLat,
                        Longitude = cmd.FixLon,
                    }
                );
            },
            Description = $"at {cmd.FixName} R{cmd.Radial:D3}: DCT {cmd.FixName}",
            NaturalDescription = $"On {cmd.FixName} {cmd.Radial:D3} radial: proceed inbound to {cmd.FixName}",
        };
        interceptBlock.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Navigation });
        aircraft.Queue.Blocks.Add(interceptBlock);

        return Ok($"Fly present heading, intercept {cmd.FixName} {cmd.Radial:D3} radial inbound");
    }

    private static CommandResult DispatchDepartFix(DepartFixCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): navigate to fix
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = cmd.FixName,
                Latitude = cmd.FixLat,
                Longitude = cmd.FixLon,
            }
        );

        // Block 1: on reaching fix, fly heading
        var departBlock = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
            },
            ApplyAction = ac =>
            {
                ac.Targets.NavigationRoute.Clear();
                ac.Targets.TargetHeading = cmd.Heading;
                ac.Targets.PreferredTurnDirection = null;
            },
            Description = $"at {cmd.FixName}: FH {cmd.Heading:D3}",
            NaturalDescription = $"At {cmd.FixName}: fly heading {cmd.Heading:D3}",
        };
        departBlock.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Heading });
        aircraft.Queue.Blocks.Add(departBlock);

        return Ok($"Proceed direct {cmd.FixName}, depart heading {cmd.Heading:D3}");
    }

    private static CommandResult DispatchCrossFix(CrossFixCommand cmd, AircraftState aircraft)
    {
        // Capture current altitude for revert after fix passage
        double? previousAlt = aircraft.Targets.TargetAltitude;

        // Block 0 (immediate): navigate to fix + set crossing altitude
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = cmd.FixName,
                Latitude = cmd.FixLat,
                Longitude = cmd.FixLon,
            }
        );

        switch (cmd.AltType)
        {
            case CrossFixAltitudeType.At:
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                break;
            case CrossFixAltitudeType.AtOrAbove when aircraft.Altitude < cmd.Altitude:
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                break;
            case CrossFixAltitudeType.AtOrBelow when aircraft.Altitude > cmd.Altitude:
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                break;
        }

        if (cmd.Speed is not null)
        {
            aircraft.Targets.TargetSpeed = cmd.Speed;
        }

        // Block 1: on reaching fix, revert to previous altitude target
        var revertBlock = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixName = cmd.FixName,
                FixLat = cmd.FixLat,
                FixLon = cmd.FixLon,
            },
            ApplyAction = ac =>
            {
                if (previousAlt is not null)
                {
                    ac.Targets.TargetAltitude = previousAlt;
                }
            },
            Description = $"at {cmd.FixName}: revert altitude",
            NaturalDescription = $"At {cmd.FixName}: resume assigned altitude",
        };
        revertBlock.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Immediate });
        aircraft.Queue.Blocks.Add(revertBlock);

        var altTypeStr = cmd.AltType switch
        {
            CrossFixAltitudeType.AtOrAbove => "at or above",
            CrossFixAltitudeType.AtOrBelow => "at or below",
            _ => "at",
        };
        var cfixMsg = $"Cross {cmd.FixName} {altTypeStr} {cmd.Altitude:N0}";
        if (cmd.Speed is not null)
        {
            cfixMsg += $", speed {cmd.Speed}";
        }
        return Ok(cfixMsg);
    }

    private static CommandResult DispatchListApproaches(ListApproachesCommand cmd, AircraftState aircraft, IApproachLookup? approachLookup)
    {
        if (approachLookup is null)
        {
            return new CommandResult(false, "Approach data not available");
        }

        string airport = cmd.AirportCode ?? aircraft.Destination ?? "";
        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "No airport specified and no destination in flight plan");
        }

        var approaches = approachLookup.GetApproaches(airport);
        if (approaches.Count == 0)
        {
            return Ok($"No approaches found for {airport.ToUpperInvariant()}");
        }

        var grouped = approaches.GroupBy(a => a.Runway ?? "").OrderBy(g => g.Key);

        var parts = grouped.Select(g =>
        {
            var items = string.Join(", ", g.Select(a => FormatApproachDisplay(a)));
            return g.Key.Length > 0 ? $"RWY {g.Key}: {items}" : items;
        });

        return Ok($"{airport.ToUpperInvariant()} approaches: {string.Join(" | ", parts)}");
    }

    private static string FormatApproachDisplay(Data.Vnas.CifpApproachProcedure approach)
    {
        string typeName = approach.ApproachTypeName;
        int parenIdx = typeName.IndexOf('(');
        if (parenIdx >= 0)
        {
            typeName = typeName[..parenIdx];
        }

        string rwy = approach.Runway ?? "";

        // Extract variant: anything in ApproachId after type code + runway
        string variant = "";
        if (approach.ApproachId.Length > 1 + rwy.Length)
        {
            variant = approach.ApproachId[(1 + rwy.Length)..];
        }

        return $"{typeName}{rwy}{variant}";
    }

    private static CommandResult DispatchJarr(
        JoinStarCommand cmd,
        AircraftState aircraft,
        IFixLookup? fixes,
        IProcedureLookup? procedureLookup = null
    )
    {
        if (fixes is null)
        {
            return new CommandResult(false, "Fix database not available");
        }

        // Try CIFP STAR first for constrained navigation targets
        var cifpResult = TryResolveStarFromCifp(cmd, aircraft, fixes, procedureLookup);
        if (cifpResult is not null)
        {
            aircraft.Targets.NavigationRoute.Clear();
            foreach (var target in cifpResult)
            {
                aircraft.Targets.NavigationRoute.Add(target);
            }

            aircraft.ActiveStarId = cmd.StarId;
            aircraft.StarViaMode = false; // STAR via mode OFF by default

            var cifpFixList = string.Join(" ", cifpResult.Select(t => t.Name));
            return Ok($"Join STAR {cmd.StarId}: {cifpFixList}");
        }

        // Fallback to NavData body fixes (lateral path only, no constraints)
        var starBody = fixes.GetStarBody(cmd.StarId);
        if (starBody is null || starBody.Count == 0)
        {
            return new CommandResult(false, $"Unknown STAR: {cmd.StarId}");
        }

        List<string> routeFixes;

        if (cmd.Transition is not null)
        {
            var transitions = fixes.GetStarTransitions(cmd.StarId);
            var match = transitions?.FirstOrDefault(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
            if (match is null || match.Value.Fixes is null)
            {
                return new CommandResult(false, $"Unknown transition '{cmd.Transition}' for STAR {cmd.StarId}");
            }

            routeFixes = [.. match.Value.Fixes, .. starBody];
        }
        else
        {
            routeFixes = FindStarFixesAhead(aircraft, starBody, fixes);
        }

        if (routeFixes.Count == 0)
        {
            return new CommandResult(false, $"No navigable fixes found for STAR {cmd.StarId}");
        }

        // Deduplicate adjacent identical fix names
        var deduped = new List<string>(routeFixes.Count);
        foreach (var name in routeFixes)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                deduped.Add(name);
            }
        }

        aircraft.Targets.NavigationRoute.Clear();
        foreach (var fixName in deduped)
        {
            var pos = fixes.GetFixPosition(fixName);
            if (pos is not null)
            {
                aircraft.Targets.NavigationRoute.Add(
                    new NavigationTarget
                    {
                        Name = fixName,
                        Latitude = pos.Value.Lat,
                        Longitude = pos.Value.Lon,
                    }
                );
            }
        }

        if (aircraft.Targets.NavigationRoute.Count == 0)
        {
            return new CommandResult(false, $"Could not resolve fixes for STAR {cmd.StarId}");
        }

        // Set STAR state even for NavData fallback (allows DVIA later)
        aircraft.ActiveStarId = cmd.StarId;
        aircraft.StarViaMode = false;

        var fixListStr = string.Join(" ", deduped);
        return Ok($"Join STAR {cmd.StarId}: {fixListStr}");
    }

    /// <summary>
    /// Attempts to resolve a STAR from CIFP data with altitude/speed constraints.
    /// Builds ordered leg sequence: enroute transition → common → runway transition.
    /// Returns null if CIFP data is unavailable or STAR cannot be resolved.
    /// </summary>
    private static List<NavigationTarget>? TryResolveStarFromCifp(
        JoinStarCommand cmd,
        AircraftState aircraft,
        IFixLookup fixes,
        IProcedureLookup? procedures
    )
    {
        if (procedures is null || aircraft.Destination is null)
        {
            return null;
        }

        var star = procedures.GetStar(aircraft.Destination, cmd.StarId);
        if (star is null)
        {
            return null;
        }

        // Build ordered leg sequence: enroute transition → common → runway transition
        var orderedLegs = new List<CifpLeg>();

        // Enroute transition (if specified)
        if (cmd.Transition is not null && star.EnrouteTransitions.TryGetValue(cmd.Transition, out var enTransition))
        {
            orderedLegs.AddRange(enTransition.Legs);
        }

        orderedLegs.AddRange(star.CommonLegs);

        // Runway transition (if assigned runway available)
        if (aircraft.Phases?.AssignedRunway is { } rwy)
        {
            var rwKey = "RW" + rwy.Designator;
            if (star.RunwayTransitions.TryGetValue(rwKey, out var rwTransition))
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        if (orderedLegs.Count == 0)
        {
            return null;
        }

        // Convert legs to NavigationTargets with constraints
        var targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs, fixes);

        // Filter to fixes ahead of aircraft (same logic as NavData fallback)
        if (cmd.Transition is null && targets.Count > 1)
        {
            targets = FindTargetsAhead(aircraft, targets);
        }

        return targets.Count > 0 ? targets : null;
    }

    /// <summary>
    /// Filters NavigationTargets to those ahead of the aircraft (within ±90° of heading),
    /// starting from the nearest such target.
    /// </summary>
    private static List<NavigationTarget> FindTargetsAhead(AircraftState aircraft, List<NavigationTarget> targets)
    {
        int bestIdx = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < targets.Count; i++)
        {
            double bearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, targets[i].Latitude, targets[i].Longitude);
            double angleDiff = ((bearing - aircraft.Heading) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, targets[i].Latitude, targets[i].Longitude);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
        {
            return targets;
        }

        return targets.GetRange(bestIdx, targets.Count - bestIdx);
    }

    /// <summary>
    /// Find the subset of STAR body fixes ahead of the aircraft (within ±90° of heading),
    /// starting from the nearest such fix. Prevents U-turns to fixes behind the aircraft.
    /// </summary>
    private static List<string> FindStarFixesAhead(AircraftState aircraft, IReadOnlyList<string> bodyFixes, IFixLookup fixes)
    {
        int bestIdx = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < bodyFixes.Count; i++)
        {
            var pos = fixes.GetFixPosition(bodyFixes[i]);
            if (pos is null)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, pos.Value.Lat, pos.Value.Lon);
            double angleDiff = ((bearing - aircraft.Heading) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, pos.Value.Lat, pos.Value.Lon);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
        {
            // No fixes ahead — use first fix as fallback
            return [.. bodyFixes];
        }

        return bodyFixes.Skip(bestIdx).ToList();
    }

    private static CommandResult DispatchHoldingPattern(HoldingPatternCommand cmd, AircraftState aircraft, ILogger? logger)
    {
        var phase = new HoldingPatternPhase
        {
            FixName = cmd.FixName,
            FixLat = cmd.FixLat,
            FixLon = cmd.FixLon,
            InboundCourse = cmd.InboundCourse,
            LegLength = cmd.LegLength,
            IsMinuteBased = cmd.IsMinuteBased,
            Direction = cmd.Direction,
            Entry = cmd.Entry,
        };

        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        if (aircraft.Phases is not null && logger is not null)
        {
            var ctx = BuildMinimalContext(aircraft, logger);
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Phases = runway is not null ? new PhaseList { AssignedRunway = runway } : new PhaseList();
        aircraft.Phases.Add(phase);

        if (logger is not null)
        {
            var startCtx = BuildMinimalContext(aircraft, logger);
            aircraft.Phases.Start(startCtx);
        }

        var dirStr = cmd.Direction == TurnDirection.Left ? "left" : "right";
        var legStr = cmd.IsMinuteBased ? $"{cmd.LegLength}min" : $"{cmd.LegLength}nm";
        return Ok($"Hold at {cmd.FixName}, {cmd.InboundCourse:D3} inbound, {dirStr} turns, {legStr} legs");
    }

    private static CommandResult DispatchJfac(
        JoinFinalApproachCourseCommand cmd,
        AircraftState aircraft,
        IApproachLookup? approachLookup,
        IRunwayLookup? runways,
        ILogger? logger
    )
    {
        if (approachLookup is null)
        {
            return new CommandResult(false, "Approach data not available");
        }

        string airport = ResolveAirport(aircraft);
        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for approach");
        }

        string? resolvedId = approachLookup.ResolveApproachId(airport, cmd.ApproachId);
        if (resolvedId is null)
        {
            return new CommandResult(false, $"Unknown approach: {cmd.ApproachId} at {airport}");
        }

        var procedure = approachLookup.GetApproach(airport, resolvedId);
        if (procedure?.Runway is null)
        {
            return new CommandResult(false, $"No runway for approach {resolvedId}");
        }

        if (runways is null)
        {
            return new CommandResult(false, "Runway data not available");
        }

        var runway = runways.GetRunway(airport, procedure.Runway);
        if (runway is null)
        {
            return new CommandResult(false, $"Unknown runway {procedure.Runway} at {airport}");
        }

        // Ensure the runway designator matches the approach runway
        var approachRunway = runway.Designator.Equals(procedure.Runway, StringComparison.OrdinalIgnoreCase)
            ? runway
            : runway.ForApproach(procedure.Runway);

        double finalCourse = approachRunway.TrueHeading;

        // Cancel existing speed restrictions per 7110.65 §5-7-1.a.4
        aircraft.Targets.TargetSpeed = null;

        // Clear existing phases
        if (aircraft.Phases is not null && logger is not null)
        {
            var ctx = BuildMinimalContext(aircraft, logger);
            aircraft.Phases.Clear(ctx);
        }

        // Build phase sequence: InterceptCourse → FinalApproach → Landing
        var interceptPhase = new InterceptCoursePhase
        {
            FinalApproachCourse = finalCourse,
            ThresholdLat = approachRunway.ThresholdLatitude,
            ThresholdLon = approachRunway.ThresholdLongitude,
        };

        var finalPhase = new FinalApproachPhase();
        var isHeliApch = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        Phase landingPhase = isHeliApch ? new HelicopterLandingPhase() : new LandingPhase();

        var clearance = new ApproachClearance
        {
            ApproachId = resolvedId,
            AirportCode = airport,
            RunwayId = procedure.Runway,
            FinalApproachCourse = finalCourse,
            Procedure = procedure,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };

        aircraft.Phases.Add(interceptPhase);
        aircraft.Phases.Add(finalPhase);
        aircraft.Phases.Add(landingPhase);

        if (logger is not null)
        {
            var startCtx = BuildMinimalContext(aircraft, logger);
            aircraft.Phases.Start(startCtx);
        }

        return Ok($"Join final approach course, {resolvedId}, runway {procedure.Runway}");
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

    private static CommandResult DispatchClimbVia(ClimbViaCommand cmd, AircraftState aircraft)
    {
        if (aircraft.ActiveSidId is null)
        {
            return new CommandResult(false, "No active SID — climb via requires an active SID");
        }

        aircraft.SidViaMode = true;
        aircraft.SidViaCeiling = cmd.Altitude;

        if (cmd.Altitude is not null)
        {
            return Ok($"Climb via SID, except maintain {cmd.Altitude:N0}");
        }

        return Ok("Climb via SID");
    }

    private static CommandResult DispatchDescendVia(DescendViaCommand cmd, AircraftState aircraft)
    {
        if (aircraft.ActiveStarId is null)
        {
            return new CommandResult(false, "No active STAR — descend via requires an active STAR");
        }

        aircraft.StarViaMode = true;
        aircraft.StarViaFloor = cmd.Altitude;

        if (cmd.Altitude is not null)
        {
            return Ok($"Descend via STAR, except maintain {cmd.Altitude:N0}");
        }

        return Ok("Descend via STAR");
    }

    private static void ClearActiveProcedure(AircraftState aircraft)
    {
        aircraft.ActiveSidId = null;
        aircraft.ActiveStarId = null;
        aircraft.SidViaMode = false;
        aircraft.StarViaMode = false;
        aircraft.SidViaCeiling = null;
        aircraft.StarViaFloor = null;
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
        IFixLookup? fixes = null,
        IApproachLookup? approachLookup = null,
        IRunwayLookup? runways = null,
        IProcedureLookup? procedureLookup = null,
        bool validateDctFixes = true
    )
    {
        // Capture the parsed commands; they'll be applied when the block activates
        var captured = commands.ToList();
        return ac =>
        {
            bool hadProcedure = ac.ActiveSidId is not null || ac.ActiveStarId is not null;

            foreach (var cmd in captured)
            {
                ApplyCommand(cmd, ac, fixes, approachLookup, runways: runways, procedureLookup: procedureLookup, validateDctFixes: validateDctFixes);
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

    private static CommandResult DispatchReportFieldInSight(AircraftState aircraft)
    {
        if (aircraft.HasReportedFieldInSight)
        {
            aircraft.PendingNotifications.Add($"{aircraft.Callsign} has the field in sight");
            return Ok("Field in sight");
        }

        return new CommandResult(false, "Unable, field not in sight");
    }

    private static CommandResult DispatchReportTrafficInSight(AircraftState aircraft, string? targetCallsign)
    {
        if (aircraft.HasReportedTrafficInSight)
        {
            var msg = targetCallsign is not null
                ? $"{aircraft.Callsign} has the traffic in sight ({targetCallsign})"
                : $"{aircraft.Callsign} has the traffic in sight";
            aircraft.PendingNotifications.Add(msg);
            return Ok("Traffic in sight");
        }

        return new CommandResult(false, "Unable, traffic not in sight");
    }
}
