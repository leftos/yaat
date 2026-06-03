using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class NavigationCommandHandler
{
    internal static CommandResult DispatchJrado(JoinRadialOutboundCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): fly present heading
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on radial intercept, fly outbound heading (radial is magnetic)
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
                var magneticHdg = new MagneticHeading(cmd.Radial);
                ac.Targets.NavigationRoute.Clear();
                ac.Targets.TargetTrueHeading = magneticHdg.ToTrue(ac.Declination);
                ac.Targets.AssignedMagneticHeading = magneticHdg;
                ac.Targets.PreferredTurnDirection = null;
                return new CommandResult(true);
            },
            Description = $"at {cmd.FixName} R{cmd.Radial:D3}: FH {cmd.Radial:D3}",
            NaturalDescription = $"On {cmd.FixName} {cmd.Radial:D3} radial: fly heading {cmd.Radial:D3}",
        };
        interceptBlock.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Heading });
        aircraft.Queue.Blocks.Add(interceptBlock);

        return CommandDispatcher.Ok($"Fly present heading, intercept {cmd.FixName} {cmd.Radial:D3} radial outbound");
    }

    internal static CommandResult DispatchJradi(JoinRadialInboundCommand cmd, AircraftState aircraft)
    {
        // Block 0 (immediate): fly present heading
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
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
                ac.Targets.AssignedMagneticHeading = null;
                ac.Targets.NavigationRoute.Clear();
                ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = cmd.FixName, Position = new LatLon(cmd.FixLat, cmd.FixLon) });
                return new CommandResult(true);
            },
            Description = $"at {cmd.FixName} R{cmd.Radial:D3}: DCT {cmd.FixName}",
            NaturalDescription = $"On {cmd.FixName} {cmd.Radial:D3} radial: proceed inbound to {cmd.FixName}",
        };
        interceptBlock.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Navigation });
        aircraft.Queue.Blocks.Add(interceptBlock);

        return CommandDispatcher.Ok($"Fly present heading, intercept {cmd.FixName} {cmd.Radial:D3} radial inbound");
    }

    internal static CommandResult DispatchDepartFix(DepartFixCommand cmd, AircraftState aircraft)
    {
        // Preserve any crossing restriction already assigned to the depart fix (e.g. a
        // preceding `CFIX CASST 6000 210`). "Depart FIX heading" proceeds direct to the fix
        // and then turns — it must not discard a cross-at altitude/speed the controller set
        // on that same fix, otherwise the aircraft sails over it at default cruise (issue #184).
        var existing = aircraft.Targets.NavigationRoute.Find(f => f.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase));

        // Block 0 (immediate): navigate to fix (navigation phase — clear assigned heading)
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = cmd.FixName,
                Position = new LatLon(cmd.FixLat, cmd.FixLon),
                AltitudeRestriction = existing?.AltitudeRestriction,
                SpeedRestriction = existing?.SpeedRestriction,
                RevertAltitude = existing?.RevertAltitude,
                RevertAssignedAltitude = existing?.RevertAssignedAltitude,
                RevertSpeed = existing?.RevertSpeed,
                RevertAssignedSpeed = existing?.RevertAssignedSpeed,
            }
        );

        // Block 1: on reaching fix, fly heading (controller heading)
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
                // A crossing-restriction speed assigned at this fix is an ATC-assigned speed,
                // not a published one — it persists through the depart vector until an approach
                // or via clearance (7110.65 5-7-1.h.4 / NOTE after h.5). Publish it as a ceiling
                // so the aircraft does not accelerate back to default cruise after the turn.
                var departFix = ac.Targets.NavigationRoute.Find(f => f.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase));
                if (departFix?.SpeedRestriction is { } crossingSpeed && !ac.Targets.HasExplicitSpeedCommand)
                {
                    ac.Targets.SpeedCeiling = crossingSpeed.SpeedKts;
                }

                ac.Targets.NavigationRoute.Clear();
                ac.Targets.TargetTrueHeading = cmd.MagneticHeading.ToTrue(ac.Declination);
                ac.Targets.AssignedMagneticHeading = cmd.MagneticHeading;
                ac.Targets.PreferredTurnDirection = null;
                return new CommandResult(true);
            },
            Description = $"at {cmd.FixName}: FH {cmd.MagneticHeading.ToDisplayInt():000}",
            NaturalDescription = $"At {cmd.FixName}: fly heading {cmd.MagneticHeading.ToDisplayInt():000}",
        };
        departBlock.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Heading });
        aircraft.Queue.Blocks.Add(departBlock);

        return CommandDispatcher.Ok($"Proceed direct {cmd.FixName}, depart heading {cmd.MagneticHeading.ToDisplayInt():000}");
    }

    internal static CommandResult DispatchCrossFix(CrossFixCommand cmd, AircraftState aircraft)
    {
        // Capture current altitude/speed for revert after fix passage
        double? previousAlt = aircraft.Targets.TargetAltitude;
        double? previousAssignedAlt = aircraft.Targets.AssignedAltitude;
        double? previousSpeed = cmd.Speed is not null ? aircraft.Targets.TargetSpeed : null;
        double? previousAssignedSpeed = cmd.Speed is not null ? aircraft.Targets.AssignedSpeed : null;

        // Preserve route fixes that come after the cross fix so the aircraft
        // continues on its route after reaching the fix (issue #70).
        var remainingRoute = new List<NavigationTarget>();
        bool foundCfix = false;
        foreach (var target in aircraft.Targets.NavigationRoute)
        {
            if (foundCfix)
            {
                remainingRoute.Add(target);
            }
            else if (target.Name.Equals(cmd.FixName, StringComparison.OrdinalIgnoreCase))
            {
                foundCfix = true;
            }
        }

        // Map CrossFixAltitudeType to CifpAltitudeRestrictionType
        var restrictionType = cmd.AltType switch
        {
            CrossFixAltitudeType.AtOrAbove => CifpAltitudeRestrictionType.AtOrAbove,
            CrossFixAltitudeType.AtOrBelow => CifpAltitudeRestrictionType.AtOrBelow,
            _ => CifpAltitudeRestrictionType.At,
        };

        var cfixTarget = new NavigationTarget
        {
            Name = cmd.FixName,
            Position = new LatLon(cmd.FixLat, cmd.FixLon),
            AltitudeRestriction = new CifpAltitudeRestriction(restrictionType, cmd.Altitude),
            SpeedRestriction = cmd.Speed is { } spd ? new CifpSpeedRestriction(spd, true) : null,
            RevertAltitude = previousAlt,
            RevertAssignedAltitude = previousAssignedAlt,
            RevertSpeed = previousSpeed,
            RevertAssignedSpeed = previousAssignedSpeed,
        };

        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.NavigationRoute.Add(cfixTarget);
        aircraft.Targets.NavigationRoute.AddRange(remainingRoute);

        // Let the planner handle altitude on the next tick — don't set TargetAltitude directly.
        // But do set AssignedAltitude for the datablock display.
        aircraft.Targets.AssignedAltitude = cmd.Altitude;

        if (cmd.Speed is { } spdVal)
        {
            aircraft.Targets.AssignedSpeed = spdVal;

            // For acceleration, set TargetSpeed immediately so the aircraft starts
            // accelerating toward the CFIX speed right away. For deceleration,
            // the look-ahead planner (UpdateSpeedPlanning) handles timing.
            if (spdVal > aircraft.IndicatedAirspeed)
            {
                aircraft.Targets.TargetSpeed = spdVal;
            }
        }

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
        return CommandDispatcher.Ok(cfixMsg);
    }

    internal static CommandResult DispatchListApproaches(ListApproachesCommand cmd, AircraftState aircraft)
    {
        var navDb = NavigationDatabase.Instance;
        string airport;
        if (cmd.AirportCode is not null)
        {
            if (!navDb.TryResolveAirport(cmd.AirportCode, out var canonical))
            {
                return new CommandResult(false, $"Unknown airport {cmd.AirportCode.Trim().ToUpperInvariant()}");
            }
            airport = canonical;
        }
        else
        {
            airport = aircraft.FlightPlan.Destination;
        }

        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "No airport specified and no destination in flight plan");
        }

        var approaches = navDb.GetApproaches(airport);
        if (approaches.Count == 0)
        {
            return CommandDispatcher.Ok($"No approaches found for {airport.ToUpperInvariant()}");
        }

        var grouped = approaches.GroupBy(a => a.Runway ?? "").OrderBy(g => g.Key);

        var parts = grouped.Select(g =>
        {
            var items = string.Join(", ", g.Select(a => FormatApproachDisplay(a)));
            return g.Key.Length > 0 ? $"RWY {g.Key}: {items}" : items;
        });

        return CommandDispatcher.Ok($"{airport.ToUpperInvariant()} approaches: {string.Join(" | ", parts)}");
    }

    private static string FormatApproachDisplay(CifpApproachProcedure approach)
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

    internal static CommandResult DispatchJarr(JoinStarCommand cmd, AircraftState aircraft)
    {
        var navDb = NavigationDatabase.Instance;

        // Try CIFP STAR first for constrained navigation targets
        var cifpResult = TryResolveStarFromCifp(cmd, aircraft);
        if (cifpResult is not null)
        {
            aircraft.Targets.NavigationRoute.Clear();
            aircraft.Targets.AssignedMagneticHeading = null;
            foreach (var target in cifpResult)
            {
                aircraft.Targets.NavigationRoute.Add(target);
            }

            aircraft.Procedure.ActiveStarId = cmd.StarId;
            aircraft.Procedure.StarViaMode = false; // STAR via mode OFF by default

            var cifpFixList = string.Join(" ", cifpResult.Select(t => t.Name));
            return CommandDispatcher.Ok($"Join STAR {cmd.StarId}: {cifpFixList}");
        }

        // Fallback to NavData body fixes (lateral path only, no constraints)
        var starBody = navDb.GetStarBody(cmd.StarId);
        if (starBody is null || starBody.Count == 0)
        {
            return new CommandResult(false, $"Unknown STAR: {cmd.StarId}");
        }

        List<string> routeFixes;

        if (cmd.Transition is not null)
        {
            var transitions = navDb.GetStarTransitions(cmd.StarId);
            var match = transitions?.FirstOrDefault(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
            if (match is not null && match.Value.Fixes is not null)
            {
                routeFixes = [.. match.Value.Fixes, .. starBody];
            }
            else
            {
                // No transition matched — try joining at an intermediate fix in the body
                int fixIdx = starBody.ToList().FindIndex(f => f.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
                if (fixIdx >= 0)
                {
                    routeFixes = starBody.Skip(fixIdx).ToList();
                }
                else
                {
                    return new CommandResult(false, $"Unknown transition or fix '{cmd.Transition}' for STAR {cmd.StarId}");
                }
            }
        }
        else
        {
            routeFixes = FindStarFixesAhead(aircraft, starBody);
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
        aircraft.Targets.AssignedMagneticHeading = null;
        foreach (var fixName in deduped)
        {
            var pos = navDb.GetFixPosition(fixName);
            if (pos is not null)
            {
                aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fixName, Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
            }
        }

        if (aircraft.Targets.NavigationRoute.Count == 0)
        {
            return new CommandResult(false, $"Could not resolve fixes for STAR {cmd.StarId}");
        }

        // Set STAR state even for NavData fallback (allows DVIA later)
        aircraft.Procedure.ActiveStarId = cmd.StarId;
        aircraft.Procedure.StarViaMode = false;

        var fixListStr = string.Join(" ", deduped);
        return CommandDispatcher.Ok($"Join STAR {cmd.StarId}: {fixListStr}");
    }

    /// <summary>
    /// Attempts to resolve a STAR from CIFP data with altitude/speed constraints.
    /// Builds ordered leg sequence: enroute transition → common → runway transition.
    /// Returns null if CIFP data is unavailable or STAR cannot be resolved.
    /// </summary>
    private static List<NavigationTarget>? TryResolveStarFromCifp(JoinStarCommand cmd, AircraftState aircraft)
    {
        if (aircraft.FlightPlan.Destination is null)
        {
            return null;
        }

        var navDb = NavigationDatabase.Instance;
        var star = navDb.GetStar(aircraft.FlightPlan.Destination, cmd.StarId);
        if (star is null)
        {
            return null;
        }

        // Build ordered leg sequence: enroute transition → common → runway transition
        var orderedLegs = new List<CifpLeg>();

        // Enroute transition (if specified)
        bool transitionMatched = false;
        if (cmd.Transition is not null && star.EnrouteTransitions.TryGetValue(cmd.Transition, out var enTransition))
        {
            orderedLegs.AddRange(enTransition.Legs);
            transitionMatched = true;
        }

        orderedLegs.AddRange(star.CommonLegs);

        // Runway transition: prefer the live AssignedRunway, fall back to the procedure's
        // DestinationRunway. EAPP sets DestinationRunway without touching AssignedRunway, so
        // a JARR issued after EAPP can still pick up the right transition.
        string? rwDesignator = aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Procedure.DestinationRunway;
        if (!string.IsNullOrEmpty(rwDesignator))
        {
            var rwTransition = LookupRunwayTransition(star.RunwayTransitions, rwDesignator);
            if (rwTransition is not null)
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        if (orderedLegs.Count == 0)
        {
            return null;
        }

        // Convert legs to NavigationTargets with constraints
        var targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);

        // If transition was specified but didn't match, try joining at an intermediate fix
        if (cmd.Transition is not null && !transitionMatched)
        {
            int fixIdx = targets.FindIndex(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
            if (fixIdx >= 0)
            {
                return targets.GetRange(fixIdx, targets.Count - fixIdx);
            }

            // Check each enroute transition for the fix
            foreach (var (_, trans) in star.EnrouteTransitions)
            {
                var transTargets = DepartureClearanceHandler.ResolveLegsToTargets(trans.Legs);
                int transFixIdx = transTargets.FindIndex(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
                if (transFixIdx >= 0)
                {
                    var result = transTargets.GetRange(transFixIdx, transTargets.Count - transFixIdx);
                    result.AddRange(targets);
                    return result;
                }
            }

            // Fix not found in CIFP data — fall through to NavData
            return null;
        }

        // Filter to fixes ahead of aircraft (same logic as NavData fallback)
        if (cmd.Transition is null && targets.Count > 1)
        {
            targets = FindTargetsAhead(aircraft, targets);
        }

        return targets.Count > 0 ? targets : null;
    }

    /// <summary>
    /// Resolves a CIFP STAR/SID runway-transition entry by runway designator. Handles the
    /// "B" wildcard convention (e.g. <c>RW01B</c> covers both L and R parallels).
    /// </summary>
    internal static CifpTransition? LookupRunwayTransition(IReadOnlyDictionary<string, CifpTransition> transitions, string runwayDesignator)
    {
        var rwKey = "RW" + runwayDesignator;
        if (transitions.TryGetValue(rwKey, out var rwTransition))
        {
            return rwTransition;
        }
        var bothKey = "RW" + runwayDesignator.TrimEnd('L', 'R', 'C') + "B";
        return transitions.TryGetValue(bothKey, out var bothTransition) ? bothTransition : null;
    }

    /// <summary>
    /// Fix names that appear only on runway transitions other than <paramref name="selectedTransition"/>.
    /// </summary>
    internal static HashSet<string> CollectExclusiveRunwayTransitionFixes(CifpStarProcedure star, CifpTransition selectedTransition)
    {
        var selectedFixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leg in selectedTransition.Legs)
        {
            if (!string.IsNullOrEmpty(leg.FixIdentifier))
            {
                selectedFixes.Add(leg.FixIdentifier);
            }
        }

        var exclusive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var otherTransition in star.RunwayTransitions.Values)
        {
            if (ReferenceEquals(otherTransition, selectedTransition))
            {
                continue;
            }

            foreach (var leg in otherTransition.Legs)
            {
                if (!string.IsNullOrEmpty(leg.FixIdentifier) && !selectedFixes.Contains(leg.FixIdentifier))
                {
                    exclusive.Add(leg.FixIdentifier);
                }
            }
        }

        return exclusive;
    }

    /// <summary>
    /// Removes nav-route targets that belong exclusively to non-selected STAR runway transitions.
    /// </summary>
    internal static void RemoveStaleStarRunwayTransitionFixes(AircraftState aircraft, CifpStarProcedure star, CifpTransition selectedTransition)
    {
        var stale = CollectExclusiveRunwayTransitionFixes(star, selectedTransition);
        if (stale.Count > 0)
        {
            aircraft.Targets.NavigationRoute.RemoveAll(t => stale.Contains(t.Name));
        }
    }

    /// <summary>
    /// Sets <see cref="AircraftProcedure.DestinationRunway"/> and extends the active STAR route
    /// for that runway (scrubbing fixes from other runway transitions first).
    /// </summary>
    internal static void SyncDestinationRunwayWithActiveStar(AircraftState aircraft, string runwayDesignator)
    {
        aircraft.Procedure.DestinationRunway = runwayDesignator;
        ExtendActiveStarWithRunwayTransition(aircraft, runwayDesignator);
    }

    /// <summary>
    /// Extends an aircraft's active NavigationRoute with the runway-transition fixes for the
    /// given <paramref name="runwayDesignator"/> on its active STAR. No-op when the aircraft
    /// has no active STAR, no destination airport, or the STAR has no transition for the
    /// runway. Skips fixes already present in the route to avoid duplicates when the route
    /// already contains some or all of the transition.
    /// </summary>
    internal static void ExtendActiveStarWithRunwayTransition(AircraftState aircraft, string runwayDesignator)
    {
        if (string.IsNullOrEmpty(aircraft.Procedure.ActiveStarId) || string.IsNullOrEmpty(aircraft.FlightPlan.Destination))
        {
            return;
        }

        var star = NavigationDatabase.Instance.GetStar(aircraft.FlightPlan.Destination, aircraft.Procedure.ActiveStarId);
        if (star is null)
        {
            return;
        }

        var transition = LookupRunwayTransition(star.RunwayTransitions, runwayDesignator);
        if (transition is null || transition.Legs.Count == 0)
        {
            return;
        }

        RemoveStaleStarRunwayTransitionFixes(aircraft, star, transition);

        var present = new HashSet<string>(aircraft.Targets.NavigationRoute.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var newLegs = transition.Legs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier) && !present.Contains(l.FixIdentifier)).ToList();
        if (newLegs.Count == 0)
        {
            return;
        }

        var newTargets = DepartureClearanceHandler.ResolveLegsToTargets(newLegs);
        foreach (var t in newTargets)
        {
            aircraft.Targets.NavigationRoute.Add(t);
        }
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
            double bearing = GeoMath.BearingTo(aircraft.Position, targets[i].Position);
            double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, targets[i].Position);
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
    private static List<string> FindStarFixesAhead(AircraftState aircraft, IReadOnlyList<string> bodyFixes)
    {
        var navDb = NavigationDatabase.Instance;
        int bestIdx = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < bodyFixes.Count; i++)
        {
            var pos = navDb.GetFixPosition(bodyFixes[i]);
            if (pos is null)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
            double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            if (angleDiff > 90)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(pos.Value.Lat, pos.Value.Lon));
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

    internal static CommandResult DispatchJawy(JoinAirwayCommand cmd, AircraftState aircraft)
    {
        var navDb = NavigationDatabase.Instance;
        var airwayFixes = navDb.GetAirwayFixes(cmd.AirwayId);
        if (airwayFixes is null || airwayFixes.Count == 0)
        {
            return new CommandResult(false, $"Unknown airway: {cmd.AirwayId}");
        }

        // Find the bracketing segment: the fix behind and fix ahead of the aircraft
        var (behindIdx, aheadIdx) = FindBracketingSegment(aircraft, airwayFixes);
        if (aheadIdx < 0)
        {
            return new CommandResult(false, $"No navigable segment found on {cmd.AirwayId}");
        }

        // Resolve ahead fix position (guaranteed non-null since FindBracketingSegment validated it)
        var aheadPos = navDb.GetFixPosition(airwayFixes[aheadIdx])!.Value;

        // Determine the segment course to intercept
        double segmentCourse;
        double interceptFixLat;
        double interceptFixLon;
        string interceptFixName;

        if (behindIdx >= 0)
        {
            var behindPos = navDb.GetFixPosition(airwayFixes[behindIdx])!.Value;
            segmentCourse = GeoMath.BearingTo(behindPos.Lat, behindPos.Lon, aheadPos.Lat, aheadPos.Lon);
            // Use behind fix as the radial origin — aircraft intercepts the radial FROM behind fix TO ahead fix
            interceptFixLat = behindPos.Lat;
            interceptFixLon = behindPos.Lon;
            interceptFixName = airwayFixes[behindIdx];
        }
        else
        {
            // No fix behind — aircraft is before the first fix. Direct to first fix.
            segmentCourse = GeoMath.BearingTo(aircraft.Position, new LatLon(aheadPos.Lat, aheadPos.Lon));
            interceptFixLat = aheadPos.Lat;
            interceptFixLon = aheadPos.Lon;
            interceptFixName = airwayFixes[aheadIdx];
        }

        int segmentRadial = (int)Math.Round(segmentCourse) % 360;

        // Determine direction of travel along the airway (forward or reverse)
        bool reversed = behindIdx >= 0 && behindIdx > aheadIdx;

        // Build remaining fix list from ahead fix onward (in the direction of travel)
        var remainingFixes = new List<string>();
        if (!reversed)
        {
            for (int i = aheadIdx; i < airwayFixes.Count; i++)
            {
                remainingFixes.Add(airwayFixes[i]);
            }
        }
        else
        {
            for (int i = aheadIdx; i >= 0; i--)
            {
                remainingFixes.Add(airwayFixes[i]);
            }
        }

        // Build NavigationTargets for the remaining fixes
        var navTargets = new List<NavigationTarget>();
        foreach (var fixName in remainingFixes)
        {
            var pos = navDb.GetFixPosition(fixName);
            if (pos is not null)
            {
                navTargets.Add(new NavigationTarget { Name = fixName, Position = new LatLon(pos.Value.Lat, pos.Value.Lon) });
            }
        }

        if (navTargets.Count == 0)
        {
            return new CommandResult(false, $"Could not resolve fixes on {cmd.AirwayId}");
        }

        // Block 0 (immediate): fly present heading (to allow intercept)
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = aircraft.TrueHeading;
        aircraft.Targets.AssignedMagneticHeading = aircraft.MagneticHeading;
        aircraft.Targets.PreferredTurnDirection = null;

        // Block 1: on segment intercept, navigate the airway fix sequence
        var interceptBlock = new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.InterceptRadial,
                FixName = interceptFixName,
                FixLat = interceptFixLat,
                FixLon = interceptFixLon,
                Radial = segmentRadial,
            },
            ApplyAction = ac =>
            {
                ac.Targets.AssignedMagneticHeading = null;
                ac.Targets.NavigationRoute.Clear();
                foreach (var target in navTargets)
                {
                    ac.Targets.NavigationRoute.Add(target);
                }
                return new CommandResult(true);
            },
            Description = $"intercept {cmd.AirwayId}: DCT {string.Join(" ", remainingFixes)}",
            NaturalDescription = $"On {cmd.AirwayId}: proceed via {string.Join(" ", remainingFixes)}",
        };
        interceptBlock.Commands.Add(new TrackedCommand { Type = TrackedCommandType.Navigation });
        aircraft.Queue.Blocks.Add(interceptBlock);

        var fixListStr = string.Join(" ", remainingFixes);
        return CommandDispatcher.Ok($"Fly present heading, intercept {cmd.AirwayId}: {fixListStr}");
    }

    /// <summary>
    /// Finds the bracketing segment on an airway — the fix behind and fix ahead of the aircraft.
    /// Returns (behindIdx, aheadIdx). behindIdx may be -1 if the aircraft is before the first fix.
    /// aheadIdx will be -1 if no valid segment is found.
    /// The method considers both forward and reverse traversal of the airway to determine the
    /// direction of travel that best matches the aircraft's heading.
    /// </summary>
    private static (int BehindIdx, int AheadIdx) FindBracketingSegment(AircraftState aircraft, IReadOnlyList<string> airwayFixes)
    {
        var navDb = NavigationDatabase.Instance;
        // Resolve positions for all fixes
        var positions = new (double Lat, double Lon)?[airwayFixes.Count];
        for (int i = 0; i < airwayFixes.Count; i++)
        {
            positions[i] = navDb.GetFixPosition(airwayFixes[i]);
        }

        // Find the closest fix ahead (within ±90° of heading) and closest fix behind
        int bestAheadIdx = -1;
        double bestAheadDist = double.MaxValue;
        int bestBehindIdx = -1;
        double bestBehindDist = double.MaxValue;

        for (int i = 0; i < airwayFixes.Count; i++)
        {
            if (positions[i] is null)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(aircraft.Position, new LatLon(positions[i]!.Value.Lat, positions[i]!.Value.Lon));
            double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, new LatLon(positions[i]!.Value.Lat, positions[i]!.Value.Lon));

            if (angleDiff <= 90)
            {
                if (dist < bestAheadDist)
                {
                    bestAheadDist = dist;
                    bestAheadIdx = i;
                }
            }
            else
            {
                if (dist < bestBehindDist)
                {
                    bestBehindDist = dist;
                    bestBehindIdx = i;
                }
            }
        }

        if (bestAheadIdx < 0)
        {
            return (-1, -1);
        }

        // Verify the behind and ahead fixes form an adjacent segment on the airway
        // (they should be consecutive in forward or reverse order)
        if (bestBehindIdx >= 0)
        {
            int diff = bestAheadIdx - bestBehindIdx;
            if (diff != 1 && diff != -1)
            {
                // Not adjacent — use the fix adjacent to the ahead fix in the correct direction
                int candidateBefore = bestAheadIdx - 1;
                int candidateAfter = bestAheadIdx + 1;

                // Pick the adjacent fix that is behind the aircraft
                bestBehindIdx = -1;
                if (candidateBefore >= 0 && positions[candidateBefore] is not null)
                {
                    double bearing = GeoMath.BearingTo(
                        aircraft.Position,
                        new LatLon(positions[candidateBefore]!.Value.Lat, positions[candidateBefore]!.Value.Lon)
                    );
                    double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
                    if (angleDiff > 180)
                    {
                        angleDiff = 360 - angleDiff;
                    }

                    if (angleDiff > 90)
                    {
                        bestBehindIdx = candidateBefore;
                    }
                }

                if (bestBehindIdx < 0 && candidateAfter < airwayFixes.Count && positions[candidateAfter] is not null)
                {
                    double bearing = GeoMath.BearingTo(
                        aircraft.Position,
                        new LatLon(positions[candidateAfter]!.Value.Lat, positions[candidateAfter]!.Value.Lon)
                    );
                    double angleDiff = ((bearing - aircraft.TrueHeading.Degrees) % 360 + 360) % 360;
                    if (angleDiff > 180)
                    {
                        angleDiff = 360 - angleDiff;
                    }

                    if (angleDiff > 90)
                    {
                        bestBehindIdx = candidateAfter;
                    }
                }
            }
        }

        return (bestBehindIdx, bestAheadIdx);
    }

    internal static CommandResult DispatchHoldingPattern(HoldingPatternCommand cmd, AircraftState aircraft)
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
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Phases = runway is not null ? new PhaseList { AssignedRunway = runway } : new PhaseList();
        aircraft.Phases.Add(phase);

        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var dirStr = cmd.Direction == TurnDirection.Left ? "left" : "right";
        var legStr = cmd.IsMinuteBased ? $"{cmd.LegLength}min" : $"{cmd.LegLength}nm";
        return CommandDispatcher.Ok($"Hold at {cmd.FixName}, {cmd.InboundCourse:D3} inbound, {dirStr} turns, {legStr} legs");
    }

    internal static CommandResult DispatchJfac(JoinFinalApproachCourseCommand cmd, AircraftState aircraft)
    {
        var navDb = NavigationDatabase.Instance;
        string airport = CommandDispatcher.ResolveAirport(aircraft);
        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for approach");
        }

        // Auto-resolve: when no approach ID given, try ExpectedApproach first, then assigned runway
        var approachId = cmd.ApproachId;
        if (approachId is null)
        {
            approachId = aircraft.Approach.Expected ?? aircraft.Procedure.DestinationRunway ?? aircraft.Procedure.DepartureRunway;
            if (approachId is null)
            {
                return new CommandResult(false, "No approach ID and no runway assigned — cannot auto-resolve");
            }
        }

        string? resolvedId = navDb.ResolveApproachId(airport, approachId);
        if (resolvedId is null)
        {
            return new CommandResult(false, $"Unknown approach: {approachId} at {airport}");
        }

        var procedure = navDb.GetApproach(airport, resolvedId);
        if (procedure?.Runway is null)
        {
            return new CommandResult(false, $"No runway for approach {resolvedId}");
        }

        var runway = navDb.GetRunway(airport, procedure.Runway);
        if (runway is null)
        {
            return new CommandResult(false, $"Unknown runway {procedure.Runway} at {airport}");
        }

        // Ensure the runway designator matches the approach runway
        var approachRunway = runway.Designator.Equals(procedure.Runway, StringComparison.OrdinalIgnoreCase)
            ? runway
            : runway.ForApproach(procedure.Runway);

        var facResult = FinalApproachCourseExtractor.Extract(procedure, approachRunway, navDb);
        TrueHeading finalCourse = facResult.Course;

        // JFAC/JLOC is a lateral "join the localizer" vector, not an approach clearance: it does
        // NOT cancel a previously assigned speed (7110.65 §5-7-4 / §5-7-1.h.4 only on approach
        // clearance, resume-normal, or descend-via). Keep the assigned speed and any STAR
        // crossing-speed ceiling — the aircraft holds them through the intercept until CAPP.

        // Clear assigned heading — approach takes over steering
        aircraft.Targets.AssignedMagneticHeading = null;
        aircraft.Targets.NavigationRoute.Clear();

        // Clear existing phases
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        ApproachCommandHandler.ClearPendingApproach(aircraft);

        // Build phase sequence: InterceptCourse → FinalApproach → Landing
        var interceptPhase = new InterceptCoursePhase
        {
            FinalApproachCourse = finalCourse,
            ThresholdLat = approachRunway.ThresholdLatitude,
            ThresholdLon = approachRunway.ThresholdLongitude,
            ApproachId = resolvedId,
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
            FinalApproachAnchorLat = facResult.AnchorLat,
            FinalApproachAnchorLon = facResult.AnchorLon,
            Procedure = procedure,
            MapAltitudeFt = ApproachCommandHandler.ExtractMapAltitude(procedure),
            MapDistanceNm = ApproachCommandHandler.ExtractMapDistance(procedure, approachRunway),
            // Lateral intercept only: hold altitude on the final approach course until CAPP.
            LateralInterceptOnly = true,
        };

        aircraft.Phases = new PhaseList { AssignedRunway = approachRunway, ActiveApproach = clearance };
        aircraft.Procedure.DestinationRunway = approachRunway.Designator;

        aircraft.Phases.Add(interceptPhase);
        aircraft.Phases.Add(finalPhase);
        aircraft.Phases.Add(landingPhase);

        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        return CommandDispatcher.Ok($"Join final approach course, {resolvedId}, runway {procedure.Runway}");
    }

    internal static CommandResult DispatchClimbVia(ClimbViaCommand cmd, AircraftState aircraft)
    {
        if (aircraft.Procedure.ActiveSidId is null)
        {
            return new CommandResult(false, "No active SID — climb via requires an active SID");
        }

        aircraft.Procedure.SidViaMode = true;
        aircraft.Procedure.SidViaCeiling = cmd.Altitude;
        aircraft.Procedure.SpeedRestrictionsDeleted = false;
        // Reset procedural speed memory; ApplyFirstConstrainedFix will repopulate
        // it from the next constrained fix in the route.
        aircraft.Procedure.LastProcedureSpeedKts = null;
        ApplyFirstConstrainedFix(aircraft);

        if (cmd.Altitude is not null)
        {
            return CommandDispatcher.Ok($"Climb via SID, except maintain {cmd.Altitude:N0}");
        }

        return CommandDispatcher.Ok("Climb via SID");
    }

    internal static CommandResult DispatchDescendVia(DescendViaCommand cmd, AircraftState aircraft)
    {
        if (aircraft.Procedure.ActiveStarId is null)
        {
            return new CommandResult(false, "No active STAR — descend via requires an active STAR");
        }

        aircraft.Procedure.StarViaMode = true;
        aircraft.Procedure.StarViaFloor = cmd.Altitude;
        aircraft.Procedure.SpeedRestrictionsDeleted = false;
        // Reset procedural speed memory; ApplyFirstConstrainedFix will repopulate
        // it from the next constrained fix in the route.
        aircraft.Procedure.LastProcedureSpeedKts = null;
        ApplyFirstConstrainedFix(aircraft);

        // DVIA SPD <speed> <fix>: inject a speed restriction at the specified fix in the nav route
        if (cmd.Speed is { } speed && cmd.SpeedFixName is not null && cmd.SpeedFixLat is not null && cmd.SpeedFixLon is not null)
        {
            var route = aircraft.Targets.NavigationRoute;
            bool found = false;
            for (int i = 0; i < route.Count; i++)
            {
                if (string.Equals(route[i].Name, cmd.SpeedFixName, StringComparison.OrdinalIgnoreCase))
                {
                    route[i] = new NavigationTarget
                    {
                        Name = route[i].Name,
                        Position = route[i].Position,
                        AltitudeRestriction = route[i].AltitudeRestriction,
                        SpeedRestriction = new CifpSpeedRestriction(speed, true),
                        IsFlyOver = route[i].IsFlyOver,
                    };
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return new CommandResult(false, $"Fix {cmd.SpeedFixName} not found in current route");
            }

            return CommandDispatcher.Ok($"Descend via STAR, {speed} knots at {cmd.SpeedFixName}");
        }

        if (cmd.Altitude is not null)
        {
            return CommandDispatcher.Ok($"Descend via STAR, except maintain {cmd.Altitude:N0}");
        }

        return CommandDispatcher.Ok("Descend via STAR");
    }

    /// <summary>
    /// Applies constraints from the first fix in the nav route that has an altitude
    /// or speed restriction. Called immediately when CVIA/DVIA is issued so the
    /// aircraft starts working toward the first constraint right away, rather than
    /// waiting to sequence through unconstrained fixes.
    /// </summary>
    private static void ApplyFirstConstrainedFix(AircraftState aircraft)
    {
        foreach (var target in aircraft.Targets.NavigationRoute)
        {
            if (target.AltitudeRestriction is not null || target.SpeedRestriction is not null)
            {
                FlightPhysics.ApplyFixConstraints(aircraft, target);
                break;
            }
        }
    }

    internal static CommandResult DispatchReportFieldInSight(AircraftState aircraft, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return new CommandResult(false, "Use RFIS <clock> <miles> in solo training");
        }

        return DispatchReportFieldInSightCore(aircraft, ctx);
    }

    private static CommandResult DispatchReportFieldInSightCore(AircraftState aircraft, DispatchContext ctx)
    {
        // Fast path: if the tick processor has already confirmed acquisition on a
        // visual approach, just echo the in-sight response.
        if (aircraft.Approach.HasReportedFieldInSight)
        {
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildFieldInSight(aircraft),
                FormatFieldInSightNotification()
            );
            return CommandDispatcher.Ok("Field in sight");
        }

        var destination = aircraft.FlightPlan.Destination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return new CommandResult(false, "Unable, no arrival airport assigned");
        }

        var navDb = NavigationDatabase.Instance;
        if (navDb.GetFixPosition(destination) is null || navDb.GetAirportElevation(destination) is null)
        {
            return new CommandResult(false, $"Unable, {destination} not in nav database");
        }

        var metar = ctx.Weather?.GetWeatherForAirport(destination);
        var result =
            VisualAcquisition.TryAcquireAirport(aircraft, ctx.Weather)
            ?? throw new InvalidOperationException($"Destination {destination} pre-validated but TryAcquireAirport returned null");

        if (result.Acquired)
        {
            // Setting the flag here unblocks the CVA FOLLOW gate and lets the tick
            // processor take over maintained-contact tracking once visual clearance
            // becomes active. First-check acquisition supersedes any in-flight
            // "looking" state.
            aircraft.Approach.HasReportedFieldInSight = true;
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildFieldInSight(aircraft),
                FormatFieldInSightNotification()
            );
            aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
            return CommandDispatcher.Ok("Field in sight");
        }

        // Soft-fail: pilot acknowledges the request but can't see the field yet.
        // Record the reason in a pilot readback and keep looking each tick via
        // PilotObservationUpdater. A new RFIS replaces the prior observation —
        // the latest request always wins.
        aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
        aircraft.PendingObservations.Add(new FieldAcquisitionObservation());
        if (ctx.SoloTrainingMode)
        {
            Pilot.PilotResponder.QueueSoloPilotReadback(
                aircraft,
                FormatFieldLookingNotification(result, destination),
                Pilot.PilotResponder.SourceSayReadback
            );
        }
        else
        {
            aircraft.PendingPilotReadbacks.Add(FormatFieldLookingNotification(result, destination));
        }
        return CommandDispatcher.Ok($"Looking for the field — {FormatFieldFailureHint(result, metar, destination)}");
    }

    internal static CommandResult DispatchReportFieldAdvisory(ReportFieldAdvisoryCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        var acquisition = DispatchReportFieldInSightCore(aircraft, ctx);
        return acquisition.Success ? CommandDispatcher.Ok(CommandDescriber.FormatFieldAdvisoryPhrase(cmd.Details)) : acquisition;
    }

    internal static CommandResult DispatchReportTrafficInSight(AircraftState aircraft, string? targetCallsign, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return new CommandResult(false, "Use RTIS <clock> <miles> <direction> <type> <altitude> in solo training");
        }

        // Fast path: if the tick processor has already confirmed acquisition on an
        // active FOLLOW, just echo the in-sight response. Still update the stored
        // callsign if a new one is supplied so a later bare FOLLOW targets the
        // most recently reported traffic.
        if (aircraft.Approach.HasReportedTrafficInSight)
        {
            if (!string.IsNullOrWhiteSpace(targetCallsign))
            {
                aircraft.Approach.LastReportedTrafficCallsign = targetCallsign.ToUpperInvariant();
            }
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildTrafficInSight(aircraft, targetCallsign),
                FormatTrafficInSightNotification(targetCallsign)
            );
            return CommandDispatcher.Ok("Traffic in sight");
        }

        if (string.IsNullOrWhiteSpace(targetCallsign))
        {
            return new CommandResult(false, "Unable, no traffic specified");
        }

        var target = ctx.FindAircraft?.Invoke(targetCallsign);
        if (target is null)
        {
            return new CommandResult(false, $"Negative contact, {targetCallsign} not on this frequency");
        }

        return DispatchReportTrafficInSightForTarget(aircraft, targetCallsign, target, ctx);
    }

    private static CommandResult DispatchReportTrafficInSightForTarget(
        AircraftState aircraft,
        string targetCallsign,
        AircraftState target,
        DispatchContext ctx
    )
    {
        var result = VisualAcquisition.TryAcquireTraffic(aircraft, target, ctx.Weather);

        if (result.Acquired)
        {
            aircraft.Approach.HasReportedTrafficInSight = true;
            aircraft.Approach.LastReportedTrafficCallsign = targetCallsign.ToUpperInvariant();
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildTrafficInSight(aircraft, targetCallsign),
                FormatTrafficInSightNotification(targetCallsign)
            );
            // First-check acquisition supersedes any in-flight "looking" state.
            aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
            return CommandDispatcher.Ok("Traffic in sight");
        }

        // Soft-fail: pilot acknowledges the request but can't see the traffic yet.
        // Record the reason in a pilot readback and keep looking each tick via
        // PilotObservationUpdater. A new RTIS (same or different callsign) replaces
        // the prior observation — the latest request always wins.
        var targetCallsignUpper = targetCallsign.ToUpperInvariant();
        aircraft.PendingObservations.RemoveAll(o => o is TrafficAcquisitionObservation);
        aircraft.PendingObservations.Add(new TrafficAcquisitionObservation(targetCallsignUpper));
        if (ctx.SoloTrainingMode)
        {
            Pilot.PilotResponder.QueueSoloPilotReadback(
                aircraft,
                FormatTrafficLookingNotification(result, target),
                Pilot.PilotResponder.SourceSayReadback
            );
        }
        else
        {
            aircraft.PendingPilotReadbacks.Add(FormatTrafficLookingNotification(result, target));
        }
        return CommandDispatcher.Ok($"Looking for traffic — {FormatTrafficFailureHint(result, target)}");
    }

    internal static CommandResult DispatchReportTrafficAdvisory(ReportTrafficAdvisoryCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        var target = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(aircraft, cmd.Details, ctx.ListAircraft?.Invoke(), out string error);
        if (target is null)
        {
            return new CommandResult(false, error);
        }

        var acquisition = DispatchReportTrafficInSightForTarget(aircraft, target.Callsign, target, ctx);
        return acquisition.Success ? CommandDispatcher.Ok(CommandDescriber.FormatTrafficAdvisoryPhrase(cmd.Details)) : acquisition;
    }

    internal static CommandResult DispatchSafetyAlert(SafetyAlertCommand cmd, AircraftState aircraft, DispatchContext ctx)
    {
        var target = TrafficAdvisoryMatcher.ResolveSafetyAlertTarget(aircraft, cmd.Details, ctx.ListAircraft?.Invoke(), out string error);
        if (target is null)
        {
            return new CommandResult(false, error);
        }

        return CommandDispatcher.Ok(CommandDescriber.FormatSafetyAlertPhrase(cmd.Details));
    }

    /// <summary>
    /// Pilot readback when the traffic has been acquired. The terminal entry already shows
    /// the speaking aircraft in its callsign column, so the readback drops the leading
    /// ownship callsign and uses the GA-pilot colloquial "Have &lt;target&gt; in sight" form
    /// (AIM 5-5-10 / 5-5-11).
    /// </summary>
    internal static string FormatTrafficInSightNotification(string? targetCallsign) =>
        string.IsNullOrWhiteSpace(targetCallsign) ? "Traffic in sight" : $"Have {targetCallsign} in sight";

    /// <summary>
    /// Pilot readback when RTIS can't be satisfied on the first check — the
    /// pilot acknowledges the request and commits to keep looking. Reviewed with
    /// aviation-sim-expert: pilot phraseology only ("negative contact",
    /// "looking"), no simulator-internal diagnostics ("outside forward
    /// hemisphere", detection-range numbers). Layer details are paraphrased as
    /// "clouds between us". "Unable" is reserved for refused clearances
    /// (7110.65 2-4-20) and is NOT used here. See AIM 4-4-14 (visual separation)
    /// and AIM 5-5-10 (pilot traffic advisories).
    /// </summary>
    private static string FormatTrafficLookingNotification(VisualAcquisitionResult r, AircraftState target) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.MixedCeiling => $"Negative contact, {target.Callsign}, clouds between us, looking",
            VisualAcquisitionFailure.OccludedByBank => $"Negative contact, {target.Callsign}, in the turn, looking",
            _ => $"Negative contact, {target.Callsign}, looking",
        };

    /// <summary>
    /// RPO-facing diagnostic naming the specific reason the pilot could not
    /// visually acquire the target. Surfaced through the command result so the
    /// instructor/RPO can decide whether to relay it to the student. Distinct
    /// from <see cref="FormatTrafficLookingNotification"/>, which stays in
    /// pilot phraseology and must avoid sim-internal diagnostics.
    /// </summary>
    private static string FormatTrafficFailureHint(VisualAcquisitionResult r, AircraftState target) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.MixedCeiling => $"cloud layer {FormatLayer(r.BindingLayer)} between aircraft",
            VisualAcquisitionFailure.OccludedByBank => "target on high-wing side during bank (occluded)",
            VisualAcquisitionFailure.BehindOwnship => "target behind ownship (outside forward hemisphere)",
            VisualAcquisitionFailure.OutOfRange => $"target {r.DistanceNm:F1} nm away, max visual {r.MaxRangeNm:F1} nm ({target.AircraftType})",
            _ => $"not acquired ({r.Reason})",
        };

    /// <summary>
    /// Pilot readback when the field has been acquired. Routed through
    /// <see cref="AircraftState.PendingPilotReadbacks"/> so the RPO sees the resolution
    /// on the SAY channel — "field in sight" gates the visual approach clearance.
    /// The terminal entry already shows the speaking aircraft in its callsign column,
    /// so the readback drops the leading ownship callsign and uses the GA-pilot
    /// colloquial "Have the field in sight" form.
    /// </summary>
    internal static string FormatFieldInSightNotification() => "Have the field in sight";

    /// <summary>
    /// Pilot readback when RFIS can't be satisfied on the first check — the
    /// pilot acknowledges the request and commits to keep looking. Mirrors
    /// the RTIS soft-fail readback (see <see cref="FormatTrafficLookingNotification"/>):
    /// pilot phraseology only, no simulator-internal diagnostics. "Unable" is
    /// reserved for refused clearances (7110.65 2-4-20) and is NOT used here.
    /// Reviewed with aviation-sim-expert:
    /// - "On top" only fits <see cref="VisualAcquisitionFailure.AboveCeiling"/>
    ///   (the AIM 4-4-8 / 5-5-3 sense of "above an obscuring layer"). For
    ///   <see cref="VisualAcquisitionFailure.InClassA"/> the issue is altitude,
    ///   not a deck, so the readback collapses to the default — the controller
    ///   already knows why at FL180+.
    /// - "Field's behind us" is a real-world idiom for
    ///   <see cref="VisualAcquisitionFailure.BehindOwnship"/> and is actionable
    ///   for the controller (cue to offer a vector).
    /// - <see cref="VisualAcquisitionFailure.OppositeSideOfRunway"/> stays in
    ///   the default arm; runway side isn't standard pilot phraseology in this
    ///   reply.
    /// See AIM 5-4-23 (visual approach) and AIM 4-1-15 / 5-5-8 ("negative
    /// contact" usage).
    /// </summary>
    private static string FormatFieldLookingNotification(VisualAcquisitionResult r, string airportId) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.OccludedByBank => $"Negative contact, {airportId}, in the turn, looking",
            VisualAcquisitionFailure.AboveCeiling => $"Negative contact, {airportId}, on top, looking",
            VisualAcquisitionFailure.BehindOwnship => $"Negative contact, {airportId}, field's behind us, looking",
            _ => $"Negative contact, {airportId}, looking",
        };

    // Phraseology borrowed from AIM §4-1-15 and §5-5-8 (traffic advisories use
    // "negative contact" when the pilot cannot visually acquire a target) and
    // 7110.65 §7-2-1 (visual separation boundary at FL180).

    /// <summary>
    /// RPO-facing diagnostic naming the specific reason the pilot could not
    /// visually acquire the field. Surfaced through the command result so the
    /// instructor/RPO can decide whether to relay it to the student. Distinct
    /// from <see cref="FormatFieldLookingNotification"/>, which stays in
    /// pilot phraseology and must avoid sim-internal diagnostics.
    /// </summary>
    private static string FormatFieldFailureHint(VisualAcquisitionResult r, MetarParser.ParsedMetar? metar, string airportId) =>
        r.Reason switch
        {
            VisualAcquisitionFailure.InClassA => "ownship in Class Alpha",
            VisualAcquisitionFailure.AboveCeiling => $"{airportId} below {FormatLayer(r.BindingLayer)} (cloud deck blocking ground view)",
            VisualAcquisitionFailure.BehindOwnship => $"{airportId} behind ownship (outside forward hemisphere)",
            VisualAcquisitionFailure.OccludedByBank => $"{airportId} on high-wing side during bank (occluded)",
            VisualAcquisitionFailure.OutOfRange =>
                $"{airportId} {r.DistanceNm:F1} nm away, max visual {r.MaxRangeNm:F1} nm{VisibilityQualifier(metar)}",
            VisualAcquisitionFailure.OppositeSideOfRunway => $"{airportId} on opposite side of runway",
            _ => $"not acquired ({r.Reason})",
        };

    /// <summary>
    /// Formats a cloud layer as the 6-character METAR-style code controllers and
    /// pilots actually read out loud, e.g. BKN070 or OVC200. Falls back to a
    /// generic "cloud layer" if no binding layer was recorded on the result.
    /// </summary>
    private static string FormatLayer(MetarParser.CloudLayer? layer)
    {
        if (layer is null)
        {
            return "cloud layer";
        }
        string prefix = layer.Cover switch
        {
            MetarParser.CloudCover.Few => "FEW",
            MetarParser.CloudCover.Scattered => "SCT",
            MetarParser.CloudCover.Broken => "BKN",
            MetarParser.CloudCover.Overcast => "OVC",
            _ => "CLD",
        };
        int hundreds = layer.BaseFeetAgl / 100;
        return $"{prefix}{hundreds:D3}";
    }

    /// <summary>
    /// Clarifies the max-range qualifier for field acquisition: airport detection
    /// is capped at 12 nm normally but drops with low visibility. If visibility is
    /// the binding constraint, mention it; otherwise report the hard cap.
    /// </summary>
    private static string VisibilityQualifier(MetarParser.ParsedMetar? metar)
    {
        if (metar?.VisibilityStatuteMiles is double visSm && visSm * 0.869 < 12.0)
        {
            return $" — visibility {visSm:F0} SM";
        }
        return string.Empty;
    }

    internal static CommandResult DispatchReportFieldInSightForced(AircraftState aircraft, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return new CommandResult(false, "RFISF is RPO-only; use RFIS <clock> <miles> in solo training");
        }

        aircraft.Approach.HasReportedFieldInSight = true;
        Pilot.PilotResponder.RouteRpoSayReadback(
            aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Pilot.PilotResponder.BuildFieldInSight(aircraft),
            FormatFieldInSightNotification()
        );
        aircraft.PendingObservations.RemoveAll(o => o is FieldAcquisitionObservation);
        return CommandDispatcher.Ok("Field in sight (forced)");
    }

    internal static CommandResult DispatchReportTrafficInSightForced(AircraftState aircraft, string? targetCallsign, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return new CommandResult(false, "RTISF is RPO-only; use RTIS <clock> <miles> <direction> <type> <altitude> in solo training");
        }

        aircraft.Approach.HasReportedTrafficInSight = true;
        if (!string.IsNullOrWhiteSpace(targetCallsign))
        {
            aircraft.Approach.LastReportedTrafficCallsign = targetCallsign.ToUpperInvariant();
        }
        Pilot.PilotResponder.RouteRpoSayReadback(
            aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Pilot.PilotResponder.BuildTrafficInSight(aircraft, targetCallsign),
            FormatTrafficInSightNotification(targetCallsign)
        );
        return CommandDispatcher.Ok("Traffic in sight (forced)");
    }
}
