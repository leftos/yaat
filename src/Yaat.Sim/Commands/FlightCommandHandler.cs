using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Commands;

internal static class FlightCommandHandler
{
    internal static CommandResult ApplyHeading(FlyHeadingCommand cmd, AircraftState aircraft)
    {
        var prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;
        return CommandDispatcher.Ok($"Fly heading {cmd.MagneticHeading.Degrees:000}{prev}");
    }

    internal static CommandResult ApplyTurnLeft(TurnLeftCommand cmd, AircraftState aircraft)
    {
        var prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
        return CommandDispatcher.Ok($"Turn left heading {cmd.MagneticHeading.Degrees:000}{prev}");
    }

    internal static CommandResult ApplyTurnRight(TurnRightCommand cmd, AircraftState aircraft)
    {
        var prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
        return CommandDispatcher.Ok($"Turn right heading {cmd.MagneticHeading.Degrees:000}{prev}");
    }

    internal static CommandResult ApplyLeftTurn(LeftTurnCommand cmd, AircraftState aircraft)
    {
        var prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading - cmd.Degrees;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading - cmd.Degrees;
        aircraft.Targets.PreferredTurnDirection = TurnDirection.Left;
        int leftHdg = (aircraft.MagneticHeading - cmd.Degrees).ToDisplayInt();
        return CommandDispatcher.Ok($"Turn {cmd.Degrees} degrees left, heading {leftHdg:000}{prev}");
    }

    internal static CommandResult ApplyRightTurn(RightTurnCommand cmd, AircraftState aircraft)
    {
        var prev = PreviousLateralGuidance(aircraft);
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading + cmd.Degrees;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading + cmd.Degrees;
        aircraft.Targets.PreferredTurnDirection = TurnDirection.Right;
        int rightHdg = (aircraft.MagneticHeading + cmd.Degrees).ToDisplayInt();
        return CommandDispatcher.Ok($"Turn {cmd.Degrees} degrees right, heading {rightHdg:000}{prev}");
    }

    internal static CommandResult ApplyFlyPresentHeading(AircraftState aircraft)
    {
        var prev = PreviousLateralGuidance(aircraft);
        int hdg = aircraft.MagneticHeading.ToDisplayInt();
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;
        return CommandDispatcher.Ok($"Fly present heading {hdg:000}{prev}");
    }

    internal static CommandResult ApplyForceHeading(ForceHeadingCommand cmd, AircraftState aircraft)
    {
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.TrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.TrueTrack = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;
        return CommandDispatcher.Ok($"Force heading {cmd.MagneticHeading.Degrees:000}");
    }

    internal static CommandResult ApplyClimbMaintain(ClimbMaintainCommand cmd, AircraftState aircraft)
    {
        var prev = PreviousAltitude(aircraft, cmd.Altitude);
        aircraft.SidViaMode = false;
        aircraft.SidViaCeiling = null;
        aircraft.IsExpediting = false;
        aircraft.Targets.TargetAltitude = cmd.Altitude;
        aircraft.Targets.AssignedAltitude = cmd.Altitude;
        aircraft.Targets.HasExplicitSpeedCommand = false;
        return CommandDispatcher.Ok($"{AltitudeVerb(aircraft, cmd.Altitude)} {cmd.Altitude}{prev}");
    }

    internal static CommandResult ApplyDescendMaintain(DescendMaintainCommand cmd, AircraftState aircraft)
    {
        var prev = PreviousAltitude(aircraft, cmd.Altitude);
        aircraft.StarViaMode = false;
        aircraft.StarViaFloor = null;
        aircraft.IsExpediting = false;
        aircraft.Targets.TargetAltitude = cmd.Altitude;
        aircraft.Targets.AssignedAltitude = cmd.Altitude;
        aircraft.Targets.HasExplicitSpeedCommand = false;
        return CommandDispatcher.Ok($"{AltitudeVerb(aircraft, cmd.Altitude)} {cmd.Altitude}{prev}");
    }

    internal static CommandResult ApplyForceAltitude(ForceAltitudeCommand cmd, AircraftState aircraft)
    {
        aircraft.SidViaMode = false;
        aircraft.SidViaCeiling = null;
        aircraft.StarViaMode = false;
        aircraft.StarViaFloor = null;
        aircraft.Altitude = cmd.Altitude;
        aircraft.VerticalSpeed = 0;
        aircraft.Targets.TargetAltitude = cmd.Altitude;
        aircraft.Targets.AssignedAltitude = cmd.Altitude;
        return CommandDispatcher.Ok($"Force altitude {cmd.Altitude:N0}");
    }

    internal static CommandResult ApplySpeed(SpeedCommand cmd, AircraftState aircraft)
    {
        // Reject speed commands inside 5nm final per §5-7-1.a.2.d
        if (!aircraft.IsOnGround && aircraft.Phases?.AssignedRunway is { } spdRwy)
        {
            double spdDist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, spdRwy.ThresholdLatitude, spdRwy.ThresholdLongitude);
            if (spdDist <= 5.0)
            {
                return new CommandResult(false, "Cannot assign speed inside 5nm final [7110.65 §5-7-1.a.2.d]");
            }
        }

        // Any SPD command clears DSR flag and Mach hold
        aircraft.SpeedRestrictionsDeleted = false;
        aircraft.Targets.TargetMach = null;

        aircraft.Targets.HasExplicitSpeedCommand = true;

        aircraft.Targets.AssignedSpeed = cmd.Speed;

        switch (cmd.Modifier)
        {
            case SpeedModifier.Floor:
                aircraft.Targets.SpeedFloor = cmd.Speed;
                aircraft.Targets.SpeedCeiling = null;
                aircraft.Targets.TargetSpeed = null;
                break;
            case SpeedModifier.Ceiling:
                aircraft.Targets.SpeedCeiling = cmd.Speed;
                aircraft.Targets.SpeedFloor = null;
                aircraft.Targets.TargetSpeed = null;
                break;
            default:
                aircraft.Targets.TargetSpeed = cmd.Speed;
                aircraft.Targets.SpeedFloor = null;
                aircraft.Targets.SpeedCeiling = null;
                break;
        }

        // Helicopter min radar speed warning per §5-7-3.e.5
        var spdCat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if (spdCat == AircraftCategory.Helicopter && cmd.Speed > 0 && cmd.Speed < 60)
        {
            aircraft.PendingWarnings.Add($"Speed {cmd.Speed} below helicopter minimum 60 KIAS [7110.65 §5-7-3.e.5]");
        }

        return cmd.Modifier switch
        {
            SpeedModifier.Floor => CommandDispatcher.Ok($"Maintain {cmd.Speed} knots or greater"),
            SpeedModifier.Ceiling => CommandDispatcher.Ok($"Do not exceed {cmd.Speed} knots"),
            _ => CommandDispatcher.Ok($"Speed {cmd.Speed}"),
        };
    }

    internal static CommandResult ApplyResumeNormalSpeed(AircraftState aircraft)
    {
        aircraft.Targets.TargetSpeed = null;
        aircraft.Targets.AssignedSpeed = null;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.HasExplicitSpeedCommand = false;
        return CommandDispatcher.Ok("Resume normal speed");
    }

    internal static CommandResult ApplyReduceToFinalApproachSpeed(AircraftState aircraft)
    {
        var rfasCat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double approachSpeed = AircraftPerformance.ApproachSpeed(aircraft.AircraftType, rfasCat);
        aircraft.Targets.TargetSpeed = approachSpeed;
        aircraft.Targets.AssignedSpeed = approachSpeed;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.TargetMach = null;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        return CommandDispatcher.Ok($"Reduce to final approach speed ({approachSpeed:F0} kts)");
    }

    internal static CommandResult ApplyDeleteSpeedRestrictions(AircraftState aircraft)
    {
        aircraft.Targets.TargetSpeed = null;
        aircraft.Targets.AssignedSpeed = null;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.TargetMach = null;
        aircraft.SpeedRestrictionsDeleted = true;
        return CommandDispatcher.Ok("Speed restrictions deleted");
    }

    internal static CommandResult ApplyExpedite(ExpediteCommand cmd, AircraftState aircraft)
    {
        if (aircraft.Targets.TargetAltitude is null)
        {
            return new CommandResult(false, "Expedite requires an active altitude assignment");
        }

        aircraft.IsExpediting = true;

        if (cmd.UntilAltitude is not null)
        {
            // Add a queued block that clears expedite at the specified altitude
            aircraft.Queue.Blocks.Add(
                new CommandBlock
                {
                    Trigger = new BlockTrigger { Type = BlockTriggerType.ReachAltitude, Altitude = cmd.UntilAltitude.Value },
                    ApplyAction = ac =>
                    {
                        ac.IsExpediting = false;
                        ac.Targets.DesiredVerticalRate = null;
                        return new CommandResult(true);
                    },
                    Description = $"NORM at {cmd.UntilAltitude}",
                    NaturalDescription = $"Resume normal rate at {cmd.UntilAltitude:N0}",
                    Commands = { new TrackedCommand { Type = TrackedCommandType.Immediate } },
                }
            );
            return CommandDispatcher.Ok($"Expedite climb/descent through {cmd.UntilAltitude:N0}");
        }

        return CommandDispatcher.Ok("Expedite climb/descent");
    }

    internal static CommandResult ApplyNormalRate(AircraftState aircraft)
    {
        aircraft.IsExpediting = false;
        aircraft.Targets.DesiredVerticalRate = null;
        return CommandDispatcher.Ok("Resume normal rate");
    }

    internal static CommandResult ApplyMach(MachCommand cmd, AircraftState aircraft)
    {
        aircraft.Targets.TargetMach = cmd.MachNumber;
        aircraft.Targets.TargetSpeed = null;
        aircraft.Targets.AssignedSpeed = null;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        return CommandDispatcher.Ok($"Maintain Mach {cmd.MachNumber:F2}");
    }

    internal static CommandResult ApplyForceSpeed(ForceSpeedCommand cmd, AircraftState aircraft)
    {
        aircraft.IndicatedAirspeed = cmd.Speed;
        aircraft.Targets.TargetSpeed = cmd.Speed;
        aircraft.Targets.AssignedSpeed = cmd.Speed;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.SpeedRestrictionsDeleted = false;
        return CommandDispatcher.Ok($"Force speed {cmd.Speed}");
    }

    internal static CommandResult ApplySquawk(SquawkCommand cmd, AircraftState aircraft)
    {
        aircraft.BeaconCode = cmd.Code;
        return CommandDispatcher.Ok($"Squawk {cmd.Code:D4}");
    }

    internal static CommandResult ApplySquawkReset(AircraftState aircraft)
    {
        aircraft.BeaconCode = aircraft.AssignedBeaconCode;
        return CommandDispatcher.Ok($"Squawk {aircraft.AssignedBeaconCode:D4}");
    }

    internal static CommandResult ApplySquawkVfr(AircraftState aircraft)
    {
        aircraft.BeaconCode = 1200;
        return CommandDispatcher.Ok("Squawk VFR");
    }

    internal static CommandResult ApplySquawkNormal(AircraftState aircraft)
    {
        aircraft.TransponderMode = "C";
        return CommandDispatcher.Ok("Squawk normal");
    }

    internal static CommandResult ApplySquawkStandby(AircraftState aircraft)
    {
        aircraft.TransponderMode = "Standby";
        return CommandDispatcher.Ok("Squawk standby");
    }

    internal static CommandResult ApplyIdent(AircraftState aircraft)
    {
        aircraft.IsIdenting = true;
        return CommandDispatcher.Ok("Ident");
    }

    internal static CommandResult ApplyRandomSquawk(AircraftState aircraft, Random rng)
    {
        aircraft.BeaconCode = SimulationWorld.GenerateBeaconCode(rng);
        return CommandDispatcher.Ok($"Squawk {aircraft.BeaconCode:D4}");
    }

    internal static CommandResult ApplyDirectTo(DirectToCommand cmd, AircraftState aircraft, bool validateDctFixes)
    {
        if (validateDctFixes)
        {
            var programmed = aircraft.GetProgrammedFixes();
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

        var fixNames = string.Join(" ", cmd.Fixes.Select(f => f.Name));

        if (cmd.Fixes.Count == 1 && TryPreserveProcedure(aircraft, cmd.Fixes[0].Name))
        {
            aircraft.Targets.AssignedMagneticHeading = null;
            return CommandDispatcher.Ok($"Proceed direct {fixNames}");
        }

        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.AssignedMagneticHeading = null;
        var resolved = cmd.Fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.Route);
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
        bool routeRejoined = resolved.Count > originalCount;
        return routeRejoined
            ? CommandDispatcher.Ok($"Proceed direct {fixNames}, then filed route")
            : CommandDispatcher.Ok($"Proceed direct {fixNames}");
    }

    internal static CommandResult ApplyTurnDirectTo(
        List<ResolvedFix> fixes,
        List<string> skippedFixes,
        AircraftState aircraft,
        bool validateDctFixes,
        TurnDirection direction
    )
    {
        if (validateDctFixes)
        {
            var programmed = aircraft.GetProgrammedFixes();
            if (programmed.Count > 0)
            {
                var unprogrammed = fixes.Where(f => !programmed.Contains(f.Name)).ToList();
                if (unprogrammed.Count > 0)
                {
                    var names = string.Join(", ", unprogrammed.Select(f => f.Name));
                    return new CommandResult(false, $"Fix {names} not programmed — use DCTF to override");
                }
            }
        }

        var dirLabel = direction == TurnDirection.Left ? "Turn left, direct" : "Turn right, direct";
        var fixNames = string.Join(" ", fixes.Select(f => f.Name));

        if (fixes.Count == 1 && TryPreserveProcedure(aircraft, fixes[0].Name))
        {
            aircraft.Targets.AssignedMagneticHeading = null;
            aircraft.Targets.PreferredTurnDirection = direction;
            return CommandDispatcher.Ok($"{dirLabel} {fixNames}");
        }

        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Targets.PreferredTurnDirection = direction;
        var resolved = fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.Route);
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
        bool routeRejoined = resolved.Count > originalCount;
        return routeRejoined ? CommandDispatcher.Ok($"{dirLabel} {fixNames}, then filed route") : CommandDispatcher.Ok($"{dirLabel} {fixNames}");
    }

    internal static CommandResult ApplyForceDirectTo(ForceDirectToCommand cmd, AircraftState aircraft)
    {
        var fixNames = string.Join(" ", cmd.Fixes.Select(f => f.Name));

        if (cmd.Fixes.Count == 1 && TryPreserveProcedure(aircraft, cmd.Fixes[0].Name))
        {
            aircraft.Targets.AssignedMagneticHeading = null;
            return CommandDispatcher.Ok($"Proceed direct {fixNames}");
        }

        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.AssignedMagneticHeading = null;
        var resolved = cmd.Fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.Route);
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
        bool routeRejoined = resolved.Count > originalCount;
        return routeRejoined
            ? CommandDispatcher.Ok($"Proceed direct {fixNames}, then filed route")
            : CommandDispatcher.Ok($"Proceed direct {fixNames}");
    }

    internal static CommandResult ApplyConstrainedForceDirectTo(ConstrainedForceDirectToCommand cmd, AircraftState aircraft)
    {
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.AssignedMagneticHeading = null;

        // Capture current altitude/speed for revert on the last constrained fix
        double? previousAlt = aircraft.Targets.TargetAltitude;
        double? previousAssignedAlt = aircraft.Targets.AssignedAltitude;

        // Find the last index that has an altitude constraint (for revert)
        int lastConstrainedIdx = -1;
        foreach (var (idx, _) in cmd.AltitudeConstraints)
        {
            if (idx > lastConstrainedIdx)
            {
                lastConstrainedIdx = idx;
            }
        }

        for (int i = 0; i < cmd.Fixes.Count; i++)
        {
            var fix = cmd.Fixes[i];
            CifpAltitudeRestriction? altRestriction = null;
            CifpSpeedRestriction? speedRestriction = null;
            double? revertAlt = null;
            double? revertAssignedAlt = null;
            double? revertSpeed = null;
            double? revertAssignedSpeed = null;

            if (cmd.AltitudeConstraints.TryGetValue(i, out var alt))
            {
                var restrictionType = alt.AltType switch
                {
                    CrossFixAltitudeType.AtOrAbove => CifpAltitudeRestrictionType.AtOrAbove,
                    CrossFixAltitudeType.AtOrBelow => CifpAltitudeRestrictionType.AtOrBelow,
                    _ => CifpAltitudeRestrictionType.At,
                };
                altRestriction = new CifpAltitudeRestriction(restrictionType, alt.AltitudeFt);

                // Only the last constrained fix gets revert
                if (i == lastConstrainedIdx)
                {
                    revertAlt = previousAlt;
                    revertAssignedAlt = previousAssignedAlt;
                }
            }

            if (cmd.SpeedConstraints is not null && cmd.SpeedConstraints.TryGetValue(i, out var spd))
            {
                speedRestriction = new CifpSpeedRestriction(spd, true);
                if (i == lastConstrainedIdx)
                {
                    revertSpeed = aircraft.Targets.TargetSpeed;
                    revertAssignedSpeed = aircraft.Targets.AssignedSpeed;
                }
            }

            aircraft.Targets.NavigationRoute.Add(
                new NavigationTarget
                {
                    Name = fix.Name,
                    Latitude = fix.Lat,
                    Longitude = fix.Lon,
                    AltitudeRestriction = altRestriction,
                    SpeedRestriction = speedRestriction,
                    RevertAltitude = revertAlt,
                    RevertAssignedAltitude = revertAssignedAlt,
                    RevertSpeed = revertSpeed,
                    RevertAssignedSpeed = revertAssignedSpeed,
                }
            );
        }

        var fixNames = string.Join(" ", cmd.Fixes.Select(f => f.Name));
        return CommandDispatcher.Ok($"Proceed direct {fixNames}");
    }

    internal static CommandResult ApplyAppendDirectTo(AppendDirectToCommand cmd, AircraftState aircraft, bool validateDctFixes)
    {
        if (validateDctFixes)
        {
            var programmed = aircraft.GetProgrammedFixes();
            if (programmed.Count > 0)
            {
                var unprogrammed = cmd.Fixes.Where(f => !programmed.Contains(f.Name)).ToList();
                if (unprogrammed.Count > 0)
                {
                    var badNames = string.Join(", ", unprogrammed.Select(f => f.Name));
                    return new CommandResult(false, $"Fix {badNames} not programmed — use DCTF to override");
                }
            }
        }

        var resolved = cmd.Fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.Route);
        if (aircraft.Targets.NavigationRoute.Count == 0)
        {
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
            var names = string.Join(" ", cmd.Fixes.Select(f => f.Name));
            return resolved.Count > originalCount
                ? CommandDispatcher.Ok($"Proceed direct {names}, then filed route")
                : CommandDispatcher.Ok($"Proceed direct {names}");
        }
        else
        {
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
            var appended = string.Join(" ", cmd.Fixes.Select(f => f.Name));
            return resolved.Count > originalCount
                ? CommandDispatcher.Ok($"Then direct {appended}, then filed route")
                : CommandDispatcher.Ok($"Then direct {appended}");
        }
    }

    internal static CommandResult ApplyAppendForceDirectTo(AppendForceDirectToCommand cmd, AircraftState aircraft)
    {
        var resolved = cmd.Fixes.ToList();
        int originalCount = resolved.Count;
        RouteChainer.AppendRouteRemainder(resolved, aircraft.Route);
        if (aircraft.Targets.NavigationRoute.Count == 0)
        {
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
            var names = string.Join(" ", cmd.Fixes.Select(f => f.Name));
            return resolved.Count > originalCount
                ? CommandDispatcher.Ok($"Proceed direct {names}, then filed route")
                : CommandDispatcher.Ok($"Proceed direct {names}");
        }
        else
        {
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
            var appended = string.Join(" ", cmd.Fixes.Select(f => f.Name));
            return resolved.Count > originalCount
                ? CommandDispatcher.Ok($"Then direct {appended}, then filed route")
                : CommandDispatcher.Ok($"Then direct {appended}");
        }
    }

    internal static CommandResult ApplyWarp(WarpCommand cmd, AircraftState aircraft)
    {
        ClearActiveProcedure(aircraft);
        aircraft.Targets.NavigationRoute.Clear();
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
            aircraft.Phases = null;
        }
        aircraft.AssignedTaxiRoute = null;
        aircraft.Targets.TurnRateOverride = null;
        aircraft.Latitude = cmd.Latitude;
        aircraft.Longitude = cmd.Longitude;
        aircraft.TrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.TrueTrack = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Altitude = cmd.Altitude;
        aircraft.VerticalSpeed = 0;
        aircraft.IndicatedAirspeed = cmd.Speed;
        aircraft.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;
        aircraft.Targets.TargetAltitude = cmd.Altitude;
        aircraft.Targets.AssignedAltitude = cmd.Altitude;
        aircraft.Targets.TargetSpeed = cmd.Speed;
        aircraft.Targets.AssignedSpeed = cmd.Speed;
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;
        aircraft.IsOnGround = false;
        return CommandDispatcher.Ok(
            $"Warped to {cmd.PositionLabel}, heading {cmd.MagneticHeading.Degrees:000}, {cmd.Altitude:N0} ft, {cmd.Speed} kts"
        );
    }

    internal static CommandResult ApplyWarpGround(WarpGroundCommand cmd, AircraftState aircraft)
    {
        var layout = aircraft.GroundLayout;
        if (layout is null)
        {
            return new CommandResult(false, "No airport layout loaded for this aircraft");
        }

        GroundNode? node;
        string description;

        if (cmd.NodeId is int nodeId)
        {
            if (!layout.Nodes.TryGetValue(nodeId, out node))
            {
                return new CommandResult(false, $"Node #{nodeId} not found in airport layout");
            }
            description = $"node #{nodeId}" + (node.Name is not null ? $" ({node.Name})" : "");
        }
        else if (cmd.ParkingName is string parkingName)
        {
            node = layout.FindSpotByName(parkingName);
            if (node is null)
            {
                return new CommandResult(false, $"Parking/spot '{parkingName}' not found in airport layout");
            }
            description = $"parking {parkingName}";
        }
        else
        {
            node = CommandDispatcher.FindTaxiwayIntersection(layout, cmd.Taxiway1, cmd.Taxiway2);
            if (node is null)
            {
                return new CommandResult(false, $"No intersection found between {cmd.Taxiway1} and {cmd.Taxiway2}");
            }
            description = $"{cmd.Taxiway1}/{cmd.Taxiway2} intersection";
        }

        // Clear stale state from prior operations
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
            aircraft.Phases = null;
        }
        aircraft.AssignedTaxiRoute = null;
        aircraft.Queue.Blocks.Clear();
        aircraft.IsHeld = false;
        aircraft.Targets.TurnRateOverride = null;

        // Place on ground at the target node with best heading alignment
        aircraft.Latitude = node.Latitude;
        aircraft.Longitude = node.Longitude;
        aircraft.IndicatedAirspeed = 0;
        aircraft.IsOnGround = true;
        aircraft.Targets.TargetSpeed = 0;

        TrueHeading bestHeading = PickBestEdgeHeading(layout, node, aircraft.TrueHeading);
        aircraft.TrueHeading = bestHeading;
        aircraft.TrueTrack = bestHeading;

        // Install ground-idle phase so subsequent commands (TAXI, LUAW, etc.) have phase context
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));

        return CommandDispatcher.Ok($"Warped to {description}");
    }

    internal static void LevelOff(AircraftState aircraft)
    {
        aircraft.Targets.TargetAltitude = null;
        aircraft.Targets.DesiredVerticalRate = null;
        aircraft.IsExpediting = false;
    }

    internal static bool TryPreserveProcedure(AircraftState aircraft, string firstFixName)
    {
        if (aircraft.ActiveSidId is null && aircraft.ActiveStarId is null)
        {
            return false;
        }

        var route = aircraft.Targets.NavigationRoute;
        int matchIndex = -1;
        for (int i = 0; i < route.Count; i++)
        {
            if (string.Equals(route[i].Name, firstFixName, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        if (matchIndex < 0)
        {
            return false;
        }

        // Truncate: remove fixes before the matched one
        if (matchIndex > 0)
        {
            route.RemoveRange(0, matchIndex);
        }

        // Disable via-mode but keep procedure ID
        aircraft.SidViaMode = false;
        aircraft.StarViaMode = false;
        aircraft.SidViaCeiling = null;
        aircraft.StarViaFloor = null;

        return true;
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

    private static string PreviousAltitude(AircraftState aircraft, int newAltitude)
    {
        if (aircraft.Targets.AssignedAltitude is { } prev && (int)prev != newAltitude)
        {
            return $" (was {(int)prev})";
        }

        return "";
    }

    private static string PreviousLateralGuidance(AircraftState aircraft)
    {
        if (aircraft.Targets.AssignedMagneticHeading is { } prevHdg)
        {
            return $" (was heading {prevHdg.Degrees:000})";
        }

        if (aircraft.Targets.NavigationRoute.Count > 0)
        {
            return $" (was DCT {aircraft.Targets.NavigationRoute[0].Name})";
        }

        if (aircraft.ActiveSidId is { } sid)
        {
            return $" (was SID {sid})";
        }

        if (aircraft.ActiveStarId is { } star)
        {
            return $" (was STAR {star})";
        }

        return "";
    }

    private static TrueHeading PickBestEdgeHeading(AirportGroundLayout layout, GroundNode node, TrueHeading currentHeading)
    {
        TrueHeading best = currentHeading;
        double bestDelta = 360;

        foreach (var edge in node.Edges)
        {
            var bearing = new TrueHeading(EdgeBearing(layout, node, edge));
            double delta = currentHeading.AbsAngleTo(bearing);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = bearing;
            }
        }

        return best;
    }

    private static double EdgeBearing(AirportGroundLayout layout, GroundNode node, GroundEdge edge)
    {
        if (edge.FromNodeId == node.Id && edge.IntermediatePoints.Count > 0)
        {
            var pt = edge.IntermediatePoints[0];
            return GeoMath.BearingTo(node.Latitude, node.Longitude, pt.Lat, pt.Lon);
        }

        if (edge.ToNodeId == node.Id && edge.IntermediatePoints.Count > 0)
        {
            var pt = edge.IntermediatePoints[^1];
            return GeoMath.BearingTo(node.Latitude, node.Longitude, pt.Lat, pt.Lon);
        }

        int otherId = edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId;
        if (layout.Nodes.TryGetValue(otherId, out var otherNode))
        {
            return GeoMath.BearingTo(node.Latitude, node.Longitude, otherNode.Latitude, otherNode.Longitude);
        }

        return 0;
    }
}
