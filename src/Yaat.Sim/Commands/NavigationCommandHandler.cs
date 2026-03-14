using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class NavigationCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("NavigationCommandHandler");

    internal static CommandResult DispatchJrado(JoinRadialOutboundCommand cmd, AircraftState aircraft)
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

        return CommandDispatcher.Ok($"Fly present heading, intercept {cmd.FixName} {cmd.Radial:D3} radial outbound");
    }

    internal static CommandResult DispatchJradi(JoinRadialInboundCommand cmd, AircraftState aircraft)
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

        return CommandDispatcher.Ok($"Fly present heading, intercept {cmd.FixName} {cmd.Radial:D3} radial inbound");
    }

    internal static CommandResult DispatchDepartFix(DepartFixCommand cmd, AircraftState aircraft)
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

        return CommandDispatcher.Ok($"Proceed direct {cmd.FixName}, depart heading {cmd.Heading:D3}");
    }

    internal static CommandResult DispatchCrossFix(CrossFixCommand cmd, AircraftState aircraft)
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
        return CommandDispatcher.Ok(cfixMsg);
    }

    internal static CommandResult DispatchListApproaches(ListApproachesCommand cmd, AircraftState aircraft, IApproachLookup? approachLookup)
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

    internal static CommandResult DispatchJarr(
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
            return CommandDispatcher.Ok($"Join STAR {cmd.StarId}: {cifpFixList}");
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
        return CommandDispatcher.Ok($"Join STAR {cmd.StarId}: {fixListStr}");
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
        bool transitionMatched = false;
        if (cmd.Transition is not null && star.EnrouteTransitions.TryGetValue(cmd.Transition, out var enTransition))
        {
            orderedLegs.AddRange(enTransition.Legs);
            transitionMatched = true;
        }

        orderedLegs.AddRange(star.CommonLegs);

        // Runway transition (if assigned runway available)
        if (aircraft.Phases?.AssignedRunway is { } rwy)
        {
            var rwKey = "RW" + rwy.Designator;
            if (!star.RunwayTransitions.TryGetValue(rwKey, out var rwTransition))
            {
                // CIFP "B" suffix means both L/R share the same transition (e.g. "RW01B")
                var bothKey = "RW" + rwy.Designator.TrimEnd('L', 'R', 'C') + "B";
                star.RunwayTransitions.TryGetValue(bothKey, out rwTransition);
            }

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
        var targets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs, fixes);

        // If transition was specified but didn't match, try joining at an intermediate fix
        if (cmd.Transition is not null && !transitionMatched)
        {
            int fixIdx = targets.FindIndex(t => t.Name.Equals(cmd.Transition, StringComparison.OrdinalIgnoreCase));
            if (fixIdx >= 0)
            {
                return targets.GetRange(fixIdx, targets.Count - fixIdx);
            }

            // Check each enroute transition for the fix
            foreach (var (transName, trans) in star.EnrouteTransitions)
            {
                var transTargets = DepartureClearanceHandler.ResolveLegsToTargets(trans.Legs, fixes);
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

    internal static CommandResult DispatchJawy(JoinAirwayCommand cmd, AircraftState aircraft, IFixLookup? fixes)
    {
        if (fixes is null)
        {
            return new CommandResult(false, "Fix database not available");
        }

        var airwayFixes = fixes.GetAirwayFixes(cmd.AirwayId);
        if (airwayFixes is null || airwayFixes.Count == 0)
        {
            return new CommandResult(false, $"Unknown airway: {cmd.AirwayId}");
        }

        // Find the bracketing segment: the fix behind and fix ahead of the aircraft
        var (behindIdx, aheadIdx) = FindBracketingSegment(aircraft, airwayFixes, fixes);
        if (aheadIdx < 0)
        {
            return new CommandResult(false, $"No navigable segment found on {cmd.AirwayId}");
        }

        // Resolve ahead fix position (guaranteed non-null since FindBracketingSegment validated it)
        var aheadPos = fixes.GetFixPosition(airwayFixes[aheadIdx])!.Value;

        // Determine the segment course to intercept
        double segmentCourse;
        double interceptFixLat;
        double interceptFixLon;
        string interceptFixName;

        if (behindIdx >= 0)
        {
            var behindPos = fixes.GetFixPosition(airwayFixes[behindIdx])!.Value;
            segmentCourse = GeoMath.BearingTo(behindPos.Lat, behindPos.Lon, aheadPos.Lat, aheadPos.Lon);
            // Use behind fix as the radial origin — aircraft intercepts the radial FROM behind fix TO ahead fix
            interceptFixLat = behindPos.Lat;
            interceptFixLon = behindPos.Lon;
            interceptFixName = airwayFixes[behindIdx];
        }
        else
        {
            // No fix behind — aircraft is before the first fix. Direct to first fix.
            segmentCourse = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, aheadPos.Lat, aheadPos.Lon);
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
            var pos = fixes.GetFixPosition(fixName);
            if (pos is not null)
            {
                navTargets.Add(
                    new NavigationTarget
                    {
                        Name = fixName,
                        Latitude = pos.Value.Lat,
                        Longitude = pos.Value.Lon,
                    }
                );
            }
        }

        if (navTargets.Count == 0)
        {
            return new CommandResult(false, $"Could not resolve fixes on {cmd.AirwayId}");
        }

        // Block 0 (immediate): fly present heading (to allow intercept)
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetHeading = FlightPhysics.NormalizeHeading(aircraft.Heading);
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
                ac.Targets.NavigationRoute.Clear();
                foreach (var target in navTargets)
                {
                    ac.Targets.NavigationRoute.Add(target);
                }
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
    private static (int BehindIdx, int AheadIdx) FindBracketingSegment(AircraftState aircraft, IReadOnlyList<string> airwayFixes, IFixLookup fixes)
    {
        // Resolve positions for all fixes
        var positions = new (double Lat, double Lon)?[airwayFixes.Count];
        for (int i = 0; i < airwayFixes.Count; i++)
        {
            positions[i] = fixes.GetFixPosition(airwayFixes[i]);
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

            double bearing = GeoMath.BearingTo(aircraft.Latitude, aircraft.Longitude, positions[i]!.Value.Lat, positions[i]!.Value.Lon);
            double angleDiff = ((bearing - aircraft.Heading) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            double dist = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, positions[i]!.Value.Lat, positions[i]!.Value.Lon);

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
                        aircraft.Latitude,
                        aircraft.Longitude,
                        positions[candidateBefore]!.Value.Lat,
                        positions[candidateBefore]!.Value.Lon
                    );
                    double angleDiff = ((bearing - aircraft.Heading) % 360 + 360) % 360;
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
                        aircraft.Latitude,
                        aircraft.Longitude,
                        positions[candidateAfter]!.Value.Lat,
                        positions[candidateAfter]!.Value.Lon
                    );
                    double angleDiff = ((bearing - aircraft.Heading) % 360 + 360) % 360;
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

    internal static CommandResult DispatchJfac(
        JoinFinalApproachCourseCommand cmd,
        AircraftState aircraft,
        IApproachLookup? approachLookup,
        IRunwayLookup? runways
    )
    {
        if (approachLookup is null)
        {
            return new CommandResult(false, "Approach data not available");
        }

        string airport = CommandDispatcher.ResolveAirport(aircraft);
        if (string.IsNullOrEmpty(airport))
        {
            return new CommandResult(false, "Cannot determine airport for approach");
        }

        // Auto-resolve: when no approach ID given, try ExpectedApproach first, then assigned runway
        var approachId = cmd.ApproachId;
        if (approachId is null)
        {
            approachId = aircraft.ExpectedApproach ?? aircraft.DestinationRunway ?? aircraft.DepartureRunway;
            if (approachId is null)
            {
                return new CommandResult(false, "No approach ID and no runway assigned — cannot auto-resolve");
            }
        }

        string? resolvedId = approachLookup.ResolveApproachId(airport, approachId);
        if (resolvedId is null)
        {
            return new CommandResult(false, $"Unknown approach: {approachId} at {airport}");
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
        aircraft.Targets.SpeedFloor = null;
        aircraft.Targets.SpeedCeiling = null;

        // Clear existing phases
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
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
        aircraft.DestinationRunway = approachRunway.Designator;

        aircraft.Phases.Add(interceptPhase);
        aircraft.Phases.Add(finalPhase);
        aircraft.Phases.Add(landingPhase);

        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        return CommandDispatcher.Ok($"Join final approach course, {resolvedId}, runway {procedure.Runway}");
    }

    internal static CommandResult DispatchClimbVia(ClimbViaCommand cmd, AircraftState aircraft)
    {
        if (aircraft.ActiveSidId is null)
        {
            return new CommandResult(false, "No active SID — climb via requires an active SID");
        }

        aircraft.SidViaMode = true;
        aircraft.SidViaCeiling = cmd.Altitude;
        aircraft.SpeedRestrictionsDeleted = false;
        ApplyFirstConstrainedFix(aircraft);

        if (cmd.Altitude is not null)
        {
            return CommandDispatcher.Ok($"Climb via SID, except maintain {cmd.Altitude:N0}");
        }

        return CommandDispatcher.Ok("Climb via SID");
    }

    internal static CommandResult DispatchDescendVia(DescendViaCommand cmd, AircraftState aircraft)
    {
        if (aircraft.ActiveStarId is null)
        {
            return new CommandResult(false, "No active STAR — descend via requires an active STAR");
        }

        aircraft.StarViaMode = true;
        aircraft.StarViaFloor = cmd.Altitude;
        aircraft.SpeedRestrictionsDeleted = false;
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
                        Latitude = route[i].Latitude,
                        Longitude = route[i].Longitude,
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

    internal static CommandResult DispatchReportFieldInSight(AircraftState aircraft)
    {
        if (aircraft.HasReportedFieldInSight)
        {
            aircraft.PendingNotifications.Add($"{aircraft.Callsign} has the field in sight");
            return CommandDispatcher.Ok("Field in sight");
        }

        return new CommandResult(false, "Unable, field not in sight");
    }

    internal static CommandResult DispatchReportTrafficInSight(AircraftState aircraft, string? targetCallsign)
    {
        if (aircraft.HasReportedTrafficInSight)
        {
            var msg = targetCallsign is not null
                ? $"{aircraft.Callsign} has the traffic in sight ({targetCallsign})"
                : $"{aircraft.Callsign} has the traffic in sight";
            aircraft.PendingNotifications.Add(msg);
            return CommandDispatcher.Ok("Traffic in sight");
        }

        return new CommandResult(false, "Unable, traffic not in sight");
    }
}
