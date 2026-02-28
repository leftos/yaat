using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

public record CommandResult(bool Success, string? Message = null);

public static class CommandDispatcher
{
    public static CommandResult DispatchCompound(
        CompoundCommand compound, AircraftState aircraft, IRunwayLookup? runways = null)
    {
        // Phase interaction: check if aircraft has active phases
        if (aircraft.Phases?.CurrentPhase is { } currentPhase)
        {
            var result = DispatchWithPhase(compound, aircraft, currentPhase, runways);
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
                if (IsTowerCommand(cmd))
                {
                    return new CommandResult(false,
                        $"{DescribeNatural(cmd)} requires an active runway assignment");
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
            var blockDesc = string.Join(", ",
                parsedBlock.Commands.Select(DescribeCommand));

            // Natural language for response message
            var blockMsg = string.Join(", ",
                parsedBlock.Commands.Select(DescribeNatural));

            if (parsedBlock.Condition is LevelCondition lv)
            {
                blockDesc = $"at {lv.Altitude}ft: {blockDesc}";
                blockMsg = $"At {lv.Altitude:N0} ft: {blockMsg}";
            }
            else if (parsedBlock.Condition is AtFixCondition at)
            {
                blockDesc = $"at {at.FixName}: {blockDesc}";
                blockMsg = $"At {at.FixName}: {blockMsg}";
            }

            var waitTime = parsedBlock.Commands.OfType<WaitCommand>().FirstOrDefault();
            var waitDist = parsedBlock.Commands.OfType<WaitDistanceCommand>().FirstOrDefault();
            bool isWait = waitTime is not null || waitDist is not null;
            var commandBlock = new CommandBlock
            {
                Trigger = ConvertCondition(parsedBlock.Condition),
                ApplyAction = BuildApplyAction(parsedBlock.Commands, aircraft),
                Description = blockDesc,
                IsWaitBlock = isWait,
                WaitRemainingSeconds = waitTime?.Seconds ?? 0,
                WaitRemainingDistanceNm = waitDist?.DistanceNm ?? 0,
            };

            foreach (var cmd in parsedBlock.Commands)
            {
                commandBlock.Commands.Add(new TrackedCommand { Type = ClassifyCommand(cmd) });
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
        ParsedCommand command, AircraftState aircraft, IRunwayLookup? runways = null)
    {
        // Clear any existing queue when a new single command is issued
        aircraft.Queue.Blocks.Clear();
        aircraft.Queue.CurrentBlockIndex = 0;

        return ApplyCommand(command, aircraft);
    }

    private static CommandResult ApplyCommand(ParsedCommand command, AircraftState aircraft)
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
                var leftHdg = NormalizeHeading(aircraft.Heading - cmd.Degrees);
                aircraft.Targets.TargetHeading = leftHdg;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
                return Ok($"Turn {cmd.Degrees} degrees left, heading {leftHdg:000}");

            case RightTurnCommand cmd:
                aircraft.Targets.NavigationRoute.Clear();
                var rightHdg = NormalizeHeading(aircraft.Heading + cmd.Degrees);
                aircraft.Targets.TargetHeading = rightHdg;
                aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
                return Ok($"Turn {cmd.Degrees} degrees right, heading {rightHdg:000}");

            case FlyPresentHeadingCommand:
                aircraft.Targets.NavigationRoute.Clear();
                aircraft.Targets.TargetHeading = NormalizeHeading(aircraft.Heading);
                aircraft.Targets.PreferredTurnDirection = null;
                return Ok("Fly present heading");

            case ClimbMaintainCommand cmd:
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                return Ok($"Climb and maintain {cmd.Altitude}");

            case DescendMaintainCommand cmd:
                aircraft.Targets.TargetAltitude = cmd.Altitude;
                return Ok($"Descend and maintain {cmd.Altitude}");

            case SpeedCommand cmd:
                aircraft.Targets.TargetSpeed = cmd.Speed == 0 ? null : cmd.Speed;
                return cmd.Speed == 0 ? Ok("Resume normal speed") : Ok($"Speed {cmd.Speed}");

            case DirectToCommand cmd:
                aircraft.Targets.NavigationRoute.Clear();
                foreach (var fix in cmd.Fixes)
                {
                    aircraft.Targets.NavigationRoute.Add(new NavigationTarget
                    {
                        Name = fix.Name,
                        Latitude = fix.Lat,
                        Longitude = fix.Lon,
                    });
                }
                var fixNames = string.Join(" ", cmd.Fixes.Select(f => f.Name));
                return Ok($"Proceed direct {fixNames}");

            case SquawkCommand cmd:
                aircraft.BeaconCode = cmd.Code;
                return Ok($"Squawk {cmd.Code:D4}");

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

            case WaitCommand cmd:
                return Ok($"Wait {cmd.Seconds} seconds");

            case WaitDistanceCommand cmd:
                return Ok($"Wait {cmd.DistanceNm} nm");

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
        CompoundCommand compound, AircraftState aircraft, Phase currentPhase,
        IRunwayLookup? runways = null)
    {
        // Extract the first command to check acceptance
        var firstCmd = compound.Blocks[0].Commands[0];
        var cmdType = ToCanonicalType(firstCmd);

        // Try tower-specific handling first (phase-interactive commands)
        var towerResult = TryApplyTowerCommand(firstCmd, aircraft, currentPhase, runways);
        if (towerResult is not null)
        {
            return towerResult;
        }

        // Check standard command acceptance against the current phase
        var acceptance = currentPhase.CanAcceptCommand(cmdType);

        if (acceptance == CommandAcceptance.Rejected)
        {
            return new CommandResult(
                false,
                $"Cannot accept {DescribeNatural(firstCmd)} during {currentPhase.Name}");
        }

        if (acceptance == CommandAcceptance.ClearsPhase)
        {
            // End the active phase properly, then exit the phase system
            var ctx = BuildMinimalContext(aircraft);
            aircraft.Phases?.Clear(ctx);
            aircraft.Phases = null;
            return null;
        }

        // Allowed but not a tower command — shouldn't normally reach here
        return null;
    }

    private static CommandResult? TryApplyTowerCommand(
        ParsedCommand command, AircraftState aircraft, Phase currentPhase,
        IRunwayLookup? runways = null)
    {
        switch (command)
        {
            case ClearedForTakeoffCommand cto:
                if (currentPhase is LinedUpAndWaitingPhase luaw)
                {
                    if (aircraft.Phases?.AssignedRunway is null)
                    {
                        return new CommandResult(false,
                            "No runway assigned — cannot clear for takeoff");
                    }

                    luaw.AssignedHeading = cto.AssignedHeading;
                    luaw.AssignedTurn = cto.Turn;
                    luaw.SatisfyClearance(ClearanceType.ClearedForTakeoff);

                    // Propagate heading/turn to TakeoffPhase
                    if (aircraft.Phases is not null)
                    {
                        foreach (var p in aircraft.Phases.Phases)
                        {
                            if (p is TakeoffPhase tkoff)
                            {
                                tkoff.SetAssignedDeparture(
                                    cto.AssignedHeading, cto.Turn);
                                break;
                            }
                        }

                        // CTOMLT/CTOMRT: establish pattern mode and append circuit
                        if (cto.TrafficPattern is { } patDir)
                        {
                            aircraft.Phases.TrafficDirection = patDir;
                            var runway = aircraft.Phases.AssignedRunway;
                            if (runway is not null)
                            {
                                var cat = AircraftCategorization.Categorize(
                                    aircraft.AircraftType);
                                var circuit = PatternBuilder.BuildCircuit(
                                    runway, cat, patDir,
                                    PatternEntryLeg.Upwind, true);
                                aircraft.Phases.Phases.AddRange(circuit);
                            }
                        }
                    }

                    var msg = $"Cleared for takeoff{RunwayLabel(aircraft)}";
                    if (cto.AssignedHeading is not null)
                    {
                        msg += $", fly heading {cto.AssignedHeading:000}";
                    }
                    if (cto.TrafficPattern is not null)
                    {
                        var dir = cto.TrafficPattern == PatternDirection.Left
                            ? "left" : "right";
                        msg += $", make {dir} traffic";
                    }
                    return Ok(msg);
                }
                return new CommandResult(false, "Aircraft is not lined up and waiting");

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
                    luawCancel.AssignedHeading = null;
                    luawCancel.AssignedTurn = null;
                    return Ok($"Takeoff clearance cancelled, hold position{RunwayLabel(aircraft)}");
                }
                if (currentPhase is TakeoffPhase && aircraft.IsOnGround)
                {
                    // Abort takeoff during ground roll
                    var ctx = BuildMinimalContext(aircraft);
                    aircraft.Phases?.Clear(ctx);
                    aircraft.Phases = null;
                    aircraft.Targets.TargetSpeed = 0;
                    return Ok("Abort takeoff, hold position");
                }
                return new CommandResult(false, "No takeoff clearance to cancel");

            case LineUpAndWaitCommand:
                return new CommandResult(false, "Aircraft position set by scenario");

            case ClearedToLandCommand:
                if (aircraft.Phases is not null)
                {
                    aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
                    aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.RunwayId;
                    aircraft.Phases.TrafficDirection = null;
                    ReplaceApproachEnding(aircraft.Phases, new LandingPhase());
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
                    var goAround = new GoAroundPhase
                    {
                        AssignedHeading = ga.AssignedHeading,
                        TargetAltitude = ga.TargetAltitude,
                    };
                    aircraft.Phases.InsertAfterCurrent(goAround);
                    var gaCtx = BuildMinimalContext(aircraft);
                    aircraft.Phases.AdvanceToNext(gaCtx);

                    var gaMsg = "Go around";
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
                return TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind,
                    runwayId: eld.RunwayId, runways: runways);

            case EnterRightDownwindCommand erd:
                return TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind,
                    runwayId: erd.RunwayId, runways: runways);

            case EnterLeftBaseCommand elb:
                return TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Base,
                    runwayId: elb.RunwayId, finalDistanceNm: elb.FinalDistanceNm, runways: runways);

            case EnterRightBaseCommand erb:
                return TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Base,
                    runwayId: erb.RunwayId, finalDistanceNm: erb.FinalDistanceNm, runways: runways);

            case EnterFinalCommand ef:
                return TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Final,
                    runwayId: ef.RunwayId, runways: runways);

            // Pattern modification commands
            case MakeLeftTrafficCommand:
                return TryChangePatternDirection(aircraft, PatternDirection.Left);

            case MakeRightTrafficCommand:
                return TryChangePatternDirection(aircraft, PatternDirection.Right);

            case TurnCrosswindCommand:
                return TryPatternTurnTo<UpwindPhase>(aircraft, "crosswind");

            case TurnDownwindCommand:
                return TryPatternTurnTo<CrosswindPhase>(aircraft, "downwind");

            case TurnBaseCommand:
                return TryPatternTurnBase(aircraft);

            case ExtendDownwindCommand:
                return TryExtendPattern(aircraft);

            // Option approach / special ops commands
            case TouchAndGoCommand:
                return TrySetupTouchAndGo(aircraft);

            case StopAndGoCommand:
                return TrySetupStopAndGo(aircraft);

            case LowApproachCommand:
                return TrySetupLowApproach(aircraft);

            case ClearedForOptionCommand:
                return TrySetLandingClearance(aircraft, ClearanceType.ClearedForOption, $"Cleared for the option{RunwayLabel(aircraft)}");

            // Hold commands
            case HoldPresentPosition360Command hpp:
                return TryHoldPresentPosition(aircraft, hpp.Direction);

            case HoldPresentPositionHoverCommand:
                return TryHoldPresentPosition(aircraft, null);

            case HoldAtFixOrbitCommand hfix:
                return TryHoldAtFix(aircraft, hfix.FixName, hfix.Lat, hfix.Lon, hfix.Direction);

            case HoldAtFixHoverCommand hfixH:
                return TryHoldAtFix(aircraft, hfixH.FixName, hfixH.Lat, hfixH.Lon, null);

            default:
                return null;
        }
    }

    private static CommandResult TryEnterPattern(
        AircraftState aircraft, PatternDirection direction, PatternEntryLeg entryLeg,
        string? runwayId = null, double? finalDistanceNm = null,
        IRunwayLookup? runways = null)
    {
        // Resolve runway from argument if provided
        if (runwayId is not null && runways is not null)
        {
            var airportId = aircraft.Phases?.AssignedRunway?.AirportId
                ?? aircraft.Destination ?? aircraft.Departure;
            if (airportId is null)
            {
                return new CommandResult(false, "No airport context to resolve runway");
            }

            var resolved = runways.GetRunway(airportId, runwayId);
            if (resolved is null)
            {
                return new CommandResult(false, $"Runway {runwayId} not found at {airportId}");
            }

            aircraft.Phases ??= new PhaseList();
            aircraft.Phases.AssignedRunway = resolved;
        }

        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway for pattern entry");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool touchAndGo = aircraft.Phases.TrafficDirection is not null;

        // Clear current phases and build new sequence from entry leg
        var ctx = BuildMinimalContext(aircraft);
        aircraft.Phases.Clear(ctx);

        var circuitPhases = PatternBuilder.BuildCircuit(
            runway, category, direction, entryLeg, touchAndGo, finalDistanceNm);

        var phases = new PhaseList { AssignedRunway = runway };
        phases.LandingClearance = aircraft.Phases.LandingClearance;
        phases.ClearedRunwayId = aircraft.Phases.ClearedRunwayId;
        phases.TrafficDirection = aircraft.Phases.TrafficDirection;

        foreach (var phase in circuitPhases)
        {
            phases.Add(phase);
        }

        aircraft.Phases = phases;

        var dirStr = direction == PatternDirection.Left ? "left" : "right";
        var legStr = entryLeg.ToString().ToLowerInvariant();
        var distStr = finalDistanceNm is not null ? $", {finalDistanceNm:G}nm final" : "";
        return Ok($"Enter {dirStr} {legStr}{RunwayLabel(aircraft)}{distStr}");
    }

    private static CommandResult TryChangePatternDirection(
        AircraftState aircraft, PatternDirection newDirection)
    {
        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        var waypoints = PatternGeometry.Compute(runway, category, newDirection);

        // Set traffic direction — aircraft is now in pattern mode
        aircraft.Phases.TrafficDirection = newDirection;

        // Update waypoints on existing pattern phases
        bool hasPatternPhases = PatternBuilder.UpdateWaypoints(aircraft.Phases, waypoints);

        // If no pattern phases exist yet (e.g., after takeoff or go-around),
        // append a full pattern circuit after the current phase.
        if (!hasPatternPhases)
        {
            var circuit = PatternBuilder.BuildCircuit(
                runway, category, newDirection, PatternEntryLeg.Upwind, true);
            aircraft.Phases.InsertAfterCurrent(circuit);
        }
        else
        {
            // Replace any pending LandingPhase with TouchAndGoPhase
            // since the aircraft is now in pattern mode.
            // Don't override specific approach instructions (SG/LA).
            for (int i = 0; i < aircraft.Phases.Phases.Count; i++)
            {
                if (aircraft.Phases.Phases[i] is LandingPhase
                    { Status: PhaseStatus.Pending })
                {
                    aircraft.Phases.Phases[i] = new TouchAndGoPhase();
                    break;
                }
            }
        }

        var dirStr = newDirection == PatternDirection.Left ? "left" : "right";
        return Ok($"Make {dirStr} traffic{RunwayLabel(aircraft)}");
    }

    /// <summary>
    /// Replace the first pending approach-ending phase (LandingPhase,
    /// TouchAndGoPhase, StopAndGoPhase, or LowApproachPhase) with the
    /// given replacement. Returns true if a replacement was made.
    /// </summary>
    private static bool ReplaceApproachEnding(PhaseList phases, Phase replacement)
    {
        for (int i = 0; i < phases.Phases.Count; i++)
        {
            var phase = phases.Phases[i];
            if (phase.Status != PhaseStatus.Pending)
            {
                continue;
            }

            if (phase is LandingPhase or TouchAndGoPhase
                or StopAndGoPhase or LowApproachPhase)
            {
                phases.Phases[i] = replacement;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ensure the aircraft is in pattern mode. If TrafficDirection is not set,
    /// infer direction from existing pattern phases or default to Left.
    /// </summary>
    private static void EnsurePatternMode(PhaseList phases)
    {
        if (phases.TrafficDirection is not null)
        {
            return;
        }

        // Infer from existing pattern phases
        foreach (var phase in phases.Phases)
        {
            if (phase is DownwindPhase { Waypoints: not null } dw)
            {
                phases.TrafficDirection = dw.Waypoints.Direction;
                return;
            }
            if (phase is BasePhase { Waypoints: not null } bp)
            {
                phases.TrafficDirection = bp.Waypoints.Direction;
                return;
            }
        }

        phases.TrafficDirection = PatternDirection.Left;
    }

    /// <summary>
    /// Advance to the next phase when the current phase is of type T.
    /// Used for TC (skip upwind to crosswind) and TD (skip crosswind to downwind).
    /// </summary>
    private static CommandResult TryPatternTurnTo<T>(
        AircraftState aircraft, string legName) where T : Phase
    {
        if (aircraft.Phases?.CurrentPhase is T)
        {
            var ctx = BuildMinimalContext(aircraft);
            aircraft.Phases.AdvanceToNext(ctx);
            return Ok($"Turn {legName}");
        }

        return new CommandResult(false, $"Not on the leg before {legName}");
    }

    private static CommandResult TryPatternTurnBase(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is DownwindPhase)
        {
            var ctx = BuildMinimalContext(aircraft);
            aircraft.Phases.AdvanceToNext(ctx);
            return Ok("Turn base");
        }

        return new CommandResult(false, "Not on downwind");
    }

    private static CommandResult TryExtendPattern(AircraftState aircraft)
    {
        var phase = aircraft.Phases?.CurrentPhase;
        switch (phase)
        {
            case UpwindPhase p:
                p.IsExtended = true;
                return Ok("Extend upwind");
            case CrosswindPhase p:
                p.IsExtended = true;
                return Ok("Extend crosswind");
            case DownwindPhase p:
                p.IsExtended = true;
                return Ok("Extend downwind");
            case BasePhase p:
                p.IsExtended = true;
                return Ok("Extend base");
            default:
                return new CommandResult(false, "Not in the pattern");
        }
    }

    private static PhaseContext BuildMinimalContext(AircraftState aircraft)
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
        };
    }

    private static CommandResult TrySetLandingClearance(
        AircraftState aircraft, ClearanceType clearanceType, string message)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = clearanceType;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.RunwayId;
        return Ok(message);
    }

    private static CommandResult TrySetupLowApproach(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.RunwayId;
        EnsurePatternMode(aircraft.Phases);
        ReplaceApproachEnding(aircraft.Phases, new LowApproachPhase());

        return Ok($"Cleared low approach{RunwayLabel(aircraft)}");
    }

    private static CommandResult TrySetupTouchAndGo(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedTouchAndGo;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.RunwayId;
        EnsurePatternMode(aircraft.Phases);
        ReplaceApproachEnding(aircraft.Phases, new TouchAndGoPhase());

        return Ok($"Cleared touch-and-go{RunwayLabel(aircraft)}");
    }

    private static CommandResult TrySetupStopAndGo(AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedStopAndGo;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.RunwayId;
        EnsurePatternMode(aircraft.Phases);
        ReplaceApproachEnding(aircraft.Phases, new StopAndGoPhase());

        return Ok($"Cleared stop-and-go{RunwayLabel(aircraft)}");
    }

    private static CommandResult TryHoldPresentPosition(
        AircraftState aircraft, TurnDirection? orbitDirection)
    {
        var phase = new HoldPresentPositionPhase { OrbitDirection = orbitDirection };

        if (aircraft.Phases is not null)
        {
            var ctx = BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
            aircraft.Phases = new PhaseList { AssignedRunway = aircraft.Phases.AssignedRunway };
        }
        else
        {
            aircraft.Phases = new PhaseList();
        }

        aircraft.Phases.Add(phase);
        var startCtx = BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var dirStr = orbitDirection switch
        {
            TurnDirection.Left => "left 360s",
            TurnDirection.Right => "right 360s",
            _ => "hover",
        };
        return Ok($"Hold present position, {dirStr}");
    }

    private static CommandResult TryHoldAtFix(
        AircraftState aircraft, string fixName, double lat, double lon,
        TurnDirection? orbitDirection)
    {
        var phase = new HoldAtFixPhase
        {
            FixName = fixName,
            FixLat = lat,
            FixLon = lon,
            OrbitDirection = orbitDirection,
        };

        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        if (aircraft.Phases is not null)
        {
            var ctx = BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(phase);
        var startCtx = BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var dirStr = orbitDirection switch
        {
            TurnDirection.Left => "left orbits",
            TurnDirection.Right => "right orbits",
            _ => "hover",
        };
        return Ok($"Hold at {fixName}, {dirStr}");
    }

    private static CanonicalCommandType ToCanonicalType(ParsedCommand command)
    {
        return command switch
        {
            FlyHeadingCommand => CanonicalCommandType.FlyHeading,
            TurnLeftCommand => CanonicalCommandType.TurnLeft,
            TurnRightCommand => CanonicalCommandType.TurnRight,
            LeftTurnCommand => CanonicalCommandType.RelativeLeft,
            RightTurnCommand => CanonicalCommandType.RelativeRight,
            FlyPresentHeadingCommand => CanonicalCommandType.FlyPresentHeading,
            ClimbMaintainCommand => CanonicalCommandType.ClimbMaintain,
            DescendMaintainCommand => CanonicalCommandType.DescendMaintain,
            SpeedCommand => CanonicalCommandType.Speed,
            DirectToCommand => CanonicalCommandType.DirectTo,
            SquawkCommand => CanonicalCommandType.Squawk,
            IdentCommand => CanonicalCommandType.SquawkIdent,
            LineUpAndWaitCommand => CanonicalCommandType.LineUpAndWait,
            ClearedForTakeoffCommand => CanonicalCommandType.ClearedForTakeoff,
            CancelTakeoffClearanceCommand => CanonicalCommandType.CancelTakeoffClearance,
            ClearedToLandCommand => CanonicalCommandType.ClearedToLand,
            GoAroundCommand => CanonicalCommandType.GoAround,
            EnterLeftDownwindCommand => CanonicalCommandType.EnterLeftDownwind,
            EnterRightDownwindCommand => CanonicalCommandType.EnterRightDownwind,
            EnterLeftBaseCommand => CanonicalCommandType.EnterLeftBase,
            EnterRightBaseCommand => CanonicalCommandType.EnterRightBase,
            EnterFinalCommand => CanonicalCommandType.EnterFinal,
            MakeLeftTrafficCommand => CanonicalCommandType.MakeLeftTraffic,
            MakeRightTrafficCommand => CanonicalCommandType.MakeRightTraffic,
            TurnCrosswindCommand => CanonicalCommandType.TurnCrosswind,
            TurnDownwindCommand => CanonicalCommandType.TurnDownwind,
            TurnBaseCommand => CanonicalCommandType.TurnBase,
            ExtendDownwindCommand => CanonicalCommandType.ExtendDownwind,
            TouchAndGoCommand => CanonicalCommandType.TouchAndGo,
            StopAndGoCommand => CanonicalCommandType.StopAndGo,
            LowApproachCommand => CanonicalCommandType.LowApproach,
            ClearedForOptionCommand => CanonicalCommandType.ClearedForOption,
            HoldPresentPosition360Command cmd => cmd.Direction == TurnDirection.Left
                ? CanonicalCommandType.HoldPresentPosition360Left
                : CanonicalCommandType.HoldPresentPosition360Right,
            HoldPresentPositionHoverCommand => CanonicalCommandType.HoldPresentPositionHover,
            HoldAtFixOrbitCommand cmd => cmd.Direction == TurnDirection.Left
                ? CanonicalCommandType.HoldAtFixLeft
                : CanonicalCommandType.HoldAtFixRight,
            HoldAtFixHoverCommand => CanonicalCommandType.HoldAtFixHover,
            _ => CanonicalCommandType.FlyHeading, // fallback
        };
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
    private static Action<AircraftState> BuildApplyAction(List<ParsedCommand> commands, AircraftState aircraft)
    {
        // Capture the parsed commands; they'll be applied when the block activates
        var captured = commands.ToList();
        return ac =>
        {
            foreach (var cmd in captured)
            {
                ApplyCommand(cmd, ac);
            }
        };
    }

    private static TrackedCommandType ClassifyCommand(ParsedCommand command)
    {
        return command switch
        {
            FlyHeadingCommand => TrackedCommandType.Heading,
            TurnLeftCommand => TrackedCommandType.Heading,
            TurnRightCommand => TrackedCommandType.Heading,
            LeftTurnCommand => TrackedCommandType.Heading,
            RightTurnCommand => TrackedCommandType.Heading,
            FlyPresentHeadingCommand => TrackedCommandType.Immediate,
            ClimbMaintainCommand => TrackedCommandType.Altitude,
            DescendMaintainCommand => TrackedCommandType.Altitude,
            SpeedCommand => TrackedCommandType.Speed,
            DirectToCommand => TrackedCommandType.Navigation,
            WaitCommand => TrackedCommandType.Wait,
            WaitDistanceCommand => TrackedCommandType.Wait,
            _ => TrackedCommandType.Immediate,
        };
    }

    private static string DescribeCommand(ParsedCommand command)
    {
        return command switch
        {
            FlyHeadingCommand cmd => $"FH {cmd.Heading:000}",
            TurnLeftCommand cmd => $"TL {cmd.Heading:000}",
            TurnRightCommand cmd => $"TR {cmd.Heading:000}",
            LeftTurnCommand cmd => $"LT {cmd.Degrees}",
            RightTurnCommand cmd => $"RT {cmd.Degrees}",
            FlyPresentHeadingCommand => "FPH",
            ClimbMaintainCommand cmd => $"CM {cmd.Altitude}",
            DescendMaintainCommand cmd => $"DM {cmd.Altitude}",
            SpeedCommand cmd => cmd.Speed == 0 ? "Resume speed" : $"SPD {cmd.Speed}",
            DirectToCommand cmd => $"DCT {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            SquawkCommand cmd => $"SQ {cmd.Code:D4}",
            LineUpAndWaitCommand => "LUAW",
            ClearedForTakeoffCommand cto => cto.AssignedHeading is not null
                ? $"CTO {cto.AssignedHeading:000}" : "CTO",
            CancelTakeoffClearanceCommand => "CTOC",
            ClearedToLandCommand => "CTL",
            CancelLandingClearanceCommand => "CLC",
            GoAroundCommand ga => ga.AssignedHeading is not null || ga.TargetAltitude is not null
                ? $"GA {(ga.AssignedHeading?.ToString("000") ?? "RH")} {ga.TargetAltitude}"
                : "GA",
            EnterLeftDownwindCommand eld => eld.RunwayId is not null ? $"ELD {eld.RunwayId}" : "ELD",
            EnterRightDownwindCommand erd => erd.RunwayId is not null ? $"ERD {erd.RunwayId}" : "ERD",
            EnterLeftBaseCommand elb => DescribePatternBase("ELB", elb.RunwayId, elb.FinalDistanceNm),
            EnterRightBaseCommand erb => DescribePatternBase("ERB", erb.RunwayId, erb.FinalDistanceNm),
            EnterFinalCommand ef => ef.RunwayId is not null ? $"EF {ef.RunwayId}" : "EF",
            MakeLeftTrafficCommand => "MLT",
            MakeRightTrafficCommand => "MRT",
            TurnCrosswindCommand => "TC",
            TurnDownwindCommand => "TD",
            TurnBaseCommand => "TB",
            ExtendDownwindCommand => "EXT",
            TouchAndGoCommand => "TG",
            StopAndGoCommand => "SG",
            LowApproachCommand => "LA",
            ClearedForOptionCommand => "COPT",
            HoldPresentPosition360Command cmd =>
                cmd.Direction == TurnDirection.Left ? "HPPL" : "HPPR",
            HoldPresentPositionHoverCommand => "HPP",
            HoldAtFixOrbitCommand cmd =>
                $"HFIX{(cmd.Direction == TurnDirection.Left ? "L" : "R")} {cmd.FixName}",
            HoldAtFixHoverCommand cmd => $"HFIX {cmd.FixName}",
            WaitCommand cmd => $"WAIT {cmd.Seconds}",
            WaitDistanceCommand cmd => $"WAITD {cmd.DistanceNm}",
            _ => command.ToString() ?? "?",
        };
    }

    private static BlockTrigger? ConvertCondition(BlockCondition? condition)
    {
        return condition switch
        {
            LevelCondition lv => new BlockTrigger
            {
                Type = BlockTriggerType.ReachAltitude,
                Altitude = lv.Altitude,
            },
            AtFixCondition at => new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixName = at.FixName,
                FixLat = at.Lat,
                FixLon = at.Lon,
            },
            _ => null,
        };
    }

    private static int NormalizeHeading(double heading)
    {
        var normalized = ((heading % 360.0) + 360.0) % 360.0;
        return normalized < 0.5 ? 360 : (int)Math.Round(normalized);
    }

    private static string DescribeNatural(ParsedCommand command)
    {
        return command switch
        {
            FlyHeadingCommand cmd => $"Fly heading {cmd.Heading:000}",
            TurnLeftCommand cmd => $"Turn left heading {cmd.Heading:000}",
            TurnRightCommand cmd => $"Turn right heading {cmd.Heading:000}",
            LeftTurnCommand cmd => $"Turn {cmd.Degrees} degrees left",
            RightTurnCommand cmd => $"Turn {cmd.Degrees} degrees right",
            FlyPresentHeadingCommand => "Fly present heading",
            ClimbMaintainCommand cmd => $"Climb and maintain {cmd.Altitude:N0}",
            DescendMaintainCommand cmd => $"Descend and maintain {cmd.Altitude:N0}",
            SpeedCommand cmd => cmd.Speed == 0
                ? "Resume normal speed" : $"Speed {cmd.Speed} knots",
            DirectToCommand cmd =>
                $"Proceed direct {string.Join(" ", cmd.Fixes.Select(f => f.Name))}",
            SquawkCommand cmd => $"Squawk {cmd.Code:D4}",
            SquawkVfrCommand => "Squawk VFR",
            SquawkNormalCommand => "Squawk normal",
            SquawkStandbyCommand => "Squawk standby",
            IdentCommand => "Ident",
            ClearedForTakeoffCommand cto => DescribeCtoNatural(cto),
            CancelTakeoffClearanceCommand => "Cancel takeoff clearance",
            ClearedToLandCommand => "Cleared to land",
            CancelLandingClearanceCommand => "Cancel landing clearance",
            GoAroundCommand ga => DescribeGaNatural(ga),
            EnterLeftDownwindCommand eld => DescribePatternEntryNatural("left downwind", eld.RunwayId, null),
            EnterRightDownwindCommand erd => DescribePatternEntryNatural("right downwind", erd.RunwayId, null),
            EnterLeftBaseCommand elb => DescribePatternEntryNatural("left base", elb.RunwayId, elb.FinalDistanceNm),
            EnterRightBaseCommand erb => DescribePatternEntryNatural("right base", erb.RunwayId, erb.FinalDistanceNm),
            EnterFinalCommand ef => DescribePatternEntryNatural("straight-in final", ef.RunwayId, null),
            MakeLeftTrafficCommand => "Make left traffic",
            MakeRightTrafficCommand => "Make right traffic",
            TurnCrosswindCommand => "Turn crosswind",
            TurnDownwindCommand => "Turn downwind",
            TurnBaseCommand => "Turn base",
            ExtendDownwindCommand => "Extend downwind",
            TouchAndGoCommand => "Cleared touch-and-go",
            StopAndGoCommand => "Cleared stop-and-go",
            LowApproachCommand => "Cleared low approach",
            ClearedForOptionCommand => "Cleared for the option",
            HoldPresentPosition360Command cmd => cmd.Direction == TurnDirection.Left
                ? "Hold present position, left 360s"
                : "Hold present position, right 360s",
            HoldPresentPositionHoverCommand => "Hold present position",
            HoldAtFixOrbitCommand cmd =>
                $"Hold at {cmd.FixName}, {(cmd.Direction == TurnDirection.Left ? "left" : "right")} orbits",
            HoldAtFixHoverCommand cmd => $"Hold at {cmd.FixName}",
            WaitCommand cmd => $"Wait {cmd.Seconds} seconds",
            WaitDistanceCommand cmd => $"Wait {cmd.DistanceNm} nm",
            UnsupportedCommand cmd => cmd.RawText,
            _ => command.ToString() ?? "?",
        };
    }

    private static string DescribeCtoNatural(ClearedForTakeoffCommand cto)
    {
        var msg = "Cleared for takeoff";
        if (cto.AssignedHeading is not null)
        {
            msg += $", fly heading {cto.AssignedHeading:000}";
        }
        if (cto.TrafficPattern is not null)
        {
            var dir = cto.TrafficPattern == PatternDirection.Left
                ? "left" : "right";
            msg += $", make {dir} traffic";
        }
        return msg;
    }

    private static string DescribeGaNatural(GoAroundCommand ga)
    {
        var msg = "Go around";
        if (ga.AssignedHeading is not null)
        {
            msg += $", fly heading {ga.AssignedHeading:000}";
        }
        if (ga.TargetAltitude is not null)
        {
            msg += $", climb to {ga.TargetAltitude:N0}";
        }
        return msg;
    }

    private static bool IsTowerCommand(ParsedCommand command)
    {
        return command is ClearedForTakeoffCommand
            or CancelTakeoffClearanceCommand
            or LineUpAndWaitCommand
            or ClearedToLandCommand
            or CancelLandingClearanceCommand
            or GoAroundCommand
            or EnterLeftDownwindCommand
            or EnterRightDownwindCommand
            or EnterLeftBaseCommand
            or EnterRightBaseCommand
            or EnterFinalCommand
            or MakeLeftTrafficCommand
            or MakeRightTrafficCommand
            or TurnCrosswindCommand
            or TurnDownwindCommand
            or TurnBaseCommand
            or ExtendDownwindCommand
            or TouchAndGoCommand
            or StopAndGoCommand
            or LowApproachCommand
            or ClearedForOptionCommand;
    }

    private static string RunwayLabel(AircraftState aircraft)
    {
        var runway = aircraft.Phases?.AssignedRunway;
        return runway is not null ? $", Runway {runway.RunwayId}" : "";
    }

    private static string DescribePatternBase(string verb, string? runwayId, double? distNm)
    {
        var parts = new List<string> { verb };
        if (runwayId is not null)
        {
            parts.Add(runwayId);
        }

        if (distNm is not null)
        {
            parts.Add(distNm.Value.ToString("G"));
        }

        return string.Join(' ', parts);
    }

    private static string DescribePatternEntryNatural(
        string legName, string? runwayId, double? distNm)
    {
        var msg = $"Enter {legName}";
        if (runwayId is not null)
        {
            msg += $", Runway {runwayId}";
        }

        if (distNm is not null)
        {
            msg += $", {distNm:G}nm final";
        }

        return msg;
    }

    private static CommandResult Ok(string message)
    {
        return new CommandResult(true, message);
    }
}
