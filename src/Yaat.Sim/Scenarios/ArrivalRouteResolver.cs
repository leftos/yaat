using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Builds an aircraft's lateral navigation route from a navigation-path string and overlays a STAR's published CIFP
/// crossing restrictions: the speeds alone for a clearance with no descend via, both the altitudes and the speeds with
/// one. Shared by <see cref="ScenarioLoader"/> (scenario-defined arrivals) and
/// <see cref="AircraftGenerator"/> (the ADD-command arrival-on-STAR spawn variant).
/// </summary>
internal static class ArrivalRouteResolver
{
    /// <summary>
    /// Expands the navigation path into resolved fixes, appends any STAR runway-transition fixes,
    /// chains the remaining filed route, and populates <c>state.Targets.NavigationRoute</c>.
    /// </summary>
    internal static void PopulateNavigationRoute(AircraftState state, string? navigationPath, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath))
        {
            return;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;

        // Expand route tokens into fix names. Flight-plan context: don't fabricate transitions on mismatch.
        List<string> expanded = RouteExpander.Expand(navigationPath, navDb, includeAllTransitionsOnMismatch: false);

        // Resolve positions and build ResolvedFix list
        var resolved = new List<ResolvedFix>();
        foreach (string fixName in expanded)
        {
            if (resolved.Count > 0 && fixName.Equals(resolved[^1].Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // ResolveFixOrFrd, not GetFixPosition: a filed route routinely names points as
            // fix/radial/distance — AP/1B files military route entry and exit that way
            // ("SAT263043 IR149 LRD040028") — and a bare fix lookup drops every one of them.
            (double Lat, double Lon)? pos = navDb.ResolveFixOrFrd(fixName);
            if (pos is null)
            {
                warnings.Add($"{state.Callsign}: Could not resolve nav fix '{fixName}', skipping");
                continue;
            }

            resolved.Add(new ResolvedFix(fixName, pos.Value.Lat, pos.Value.Lon));
        }

        // Append CIFP runway transition fixes for STARs
        Dictionary<string, double> terminalCourses = AppendStarRunwayTransition(resolved, navigationPath, state.FlightPlan.Destination);

        RouteChainer.AppendRouteRemainder(resolved, state.FlightPlan.Route);

        foreach (ResolvedFix fix in resolved)
        {
            state.Targets.NavigationRoute.Add(
                new NavigationTarget
                {
                    Name = fix.Name,
                    Position = new LatLon(fix.Lat, fix.Lon),
                    TerminalCourseMagnetic = terminalCourses.TryGetValue(fix.Name, out double terminalCourse) ? terminalCourse : null,
                }
            );
        }
    }

    /// <summary>
    /// Appends CIFP runway transition fixes for the STAR found in the navigation path, joining the transition where
    /// <see cref="NavigationCommandHandler.SelectRunwayTransitionLegsToAppend"/> places it: after the last transition
    /// fix the resolved route already holds, so a fix the aircraft starts past is never re-added behind it.
    /// </summary>
    /// <returns>
    /// The transition's published terminal courses by fix name (magnetic), so a spawned route keeps the VM/FM course
    /// its last fix hands off on.
    /// </returns>
    private static Dictionary<string, double> AppendStarRunwayTransition(List<ResolvedFix> resolved, string navigationPath, string? destination)
    {
        var terminalCourses = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (destination is null)
        {
            return terminalCourses;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        string[] tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            string[] parts = token.Split('.');
            string rawName = parts[0];
            string? runwayDesignator = parts.Length > 1 ? parts[1] : null;

            string? resolvedStarId = navDb.ResolveStarId(rawName);
            if (resolvedStarId is null)
            {
                continue;
            }

            CifpStarProcedure? star = navDb.GetStar(destination, resolvedStarId);
            if (star is null)
            {
                break;
            }

            IReadOnlyList<CifpLeg>? rwTransitionLegs = FindRunwayTransition(star, runwayDesignator, destinationRunway: null);
            if (rwTransitionLegs is null)
            {
                break;
            }

            // Resolve the whole transition, not only the appended tail: the VM leg that publishes the
            // terminal course is usually the tail itself and carries no target of its own.
            foreach (NavigationTarget transitionTarget in DepartureClearanceHandler.ResolveLegsToTargets(rwTransitionLegs))
            {
                if (transitionTarget.TerminalCourseMagnetic is { } course)
                {
                    terminalCourses[transitionTarget.Name] = course;
                }
            }

            List<string> routeNames = [.. resolved.Select(r => r.Name)];
            var existingNames = new HashSet<string>(routeNames, StringComparer.OrdinalIgnoreCase);
            List<CifpLeg> legsToAppend = NavigationCommandHandler.SelectRunwayTransitionLegsToAppend(rwTransitionLegs, star, routeNames);
            List<NavigationTarget> rwTargets = DepartureClearanceHandler.ResolveLegsToTargets(legsToAppend);
            foreach (NavigationTarget target in rwTargets)
            {
                if (existingNames.Contains(target.Name))
                {
                    continue;
                }

                resolved.Add(new ResolvedFix(target.Name, target.Position.Lat, target.Position.Lon));
                existingNames.Add(target.Name);
            }

            break;
        }

        return terminalCourses;
    }

    /// <summary>
    /// When descend-via is requested, finds the STAR in the navigation path,
    /// resolves CIFP altitude/speed constraints, overlays them on route targets,
    /// and enables StarViaMode (equivalent to auto-DVIA at spawn).
    /// </summary>
    internal static void ApplyAltitudeProfile(AircraftState state, string? navigationPath, List<string> warnings)
    {
        if (ResolveStarConstraints(state, navigationPath, warnings, "descend-via constraints not applied") is not { } resolved)
        {
            return;
        }

        // Overlay constraints onto existing route targets
        Dictionary<string, NavigationTarget> constraintsByFix = resolved.ConstraintsByFix;
        List<NavigationTarget> route = state.Targets.NavigationRoute;
        for (int i = 0; i < route.Count; i++)
        {
            if (constraintsByFix.TryGetValue(route[i].Name, out NavigationTarget? constrained))
            {
                route[i] = new NavigationTarget
                {
                    Name = route[i].Name,
                    Position = route[i].Position,
                    AltitudeRestriction = constrained.AltitudeRestriction,
                    SpeedRestriction = constrained.SpeedRestriction,
                    IsFlyOver = constrained.IsFlyOver,
                    TerminalCourseMagnetic = route[i].TerminalCourseMagnetic,
                };
            }
        }

        state.Procedure.ActiveStarId = resolved.StarId;
        state.Procedure.StarViaMode = true;

        // Apply the first constrained fix's restrictions immediately so the aircraft
        // starts descending toward the first STAR constraint at spawn, not after
        // sequencing through unconstrained fixes.
        foreach (NavigationTarget target in route)
        {
            if ((target.AltitudeRestriction is not null) || (target.SpeedRestriction is not null))
            {
                FlightPhysics.ApplyFixConstraints(state, target);
                break;
            }
        }
    }

    /// <summary>
    /// Puts a STAR's published speed restrictions on the route without descend-via: a STAR clearance authorizes its
    /// published speeds with or without "descend via", while its published altitudes are not authorized until a later
    /// descend-via overlays them (7110.65 5-7-1.d NOTE; AIM 5-4-1.a.2(a) NOTE 1). An "at or above" published floor goes
    /// on the route the same way and holds until ATC deletes it, assigns a speed, vectors, clears direct or clears the
    /// approach (7110.65 5-7-1.e NOTE). The planner flies the route's speeds, so no via mode is set here.
    /// </summary>
    internal static void ApplyStarSpeedRestrictions(AircraftState state, string? navigationPath, List<string> warnings)
    {
        if (ResolveStarConstraints(state, navigationPath, warnings, "published speeds not applied") is not { } resolved)
        {
            return;
        }

        // The STAR is active without descend via, as "JARR" leaves it: a direct then truncates this route and keeps its
        // speeds, and RPS can restore a speed already crossed. Via mode stays off, so the altitudes are not authorized.
        Dictionary<string, NavigationTarget> constraintsByFix = resolved.ConstraintsByFix;
        state.Procedure.ActiveStarId = resolved.StarId;

        List<NavigationTarget> route = state.Targets.NavigationRoute;
        for (int i = 0; i < route.Count; i++)
        {
            if (constraintsByFix.TryGetValue(route[i].Name, out NavigationTarget? constrained) && (constrained.SpeedRestriction is not null))
            {
                route[i].SpeedRestriction = constrained.SpeedRestriction;
            }
        }
    }

    /// <summary>
    /// Finds the STAR named in the navigation path and resolves its published crossing restrictions (common legs + runway
    /// transition) by fix name. Returns null when the path is empty or names no STAR, when the destination is unknown,
    /// when the STAR is not in NavData, or when it resolves no legs — and warns about a procedure-shaped token that
    /// resolves as nothing else either (<see cref="WarnUnknownStarToken"/>).
    /// </summary>
    private static (string StarId, Dictionary<string, NavigationTarget> ConstraintsByFix)? ResolveStarConstraints(
        AircraftState state,
        string? navigationPath,
        List<string> warnings,
        string notApplied
    )
    {
        if (string.IsNullOrWhiteSpace(navigationPath) || string.IsNullOrEmpty(state.FlightPlan.Destination))
        {
            return null;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        string[] tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (FindStarToken(navDb, tokens) is not { } starToken)
        {
            WarnUnknownStarToken(navDb, tokens, state.Callsign, warnings, notApplied);
            return null;
        }

        CifpStarProcedure? star = navDb.GetStar(state.FlightPlan.Destination, starToken.StarId);
        if (star is null)
        {
            return null;
        }

        // Build CIFP-constrained targets: common legs + runway transition
        var orderedLegs = new List<CifpLeg>();
        orderedLegs.AddRange(star.CommonLegs);

        IReadOnlyList<CifpLeg>? rwTransitionLegs = FindRunwayTransition(star, starToken.RunwayDesignator, state.Procedure.DestinationRunway);
        if (rwTransitionLegs is not null)
        {
            orderedLegs.AddRange(rwTransitionLegs);
        }

        if (orderedLegs.Count == 0)
        {
            return null;
        }

        List<NavigationTarget> constrainedTargets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);

        // Build a lookup of constraints by fix name
        var constraintsByFix = new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (NavigationTarget target in constrainedTargets)
        {
            constraintsByFix[target.Name] = target;
        }

        return (starToken.StarId, constraintsByFix);
    }

    /// <summary>The STAR token of a navigation path: its resolved id and runway designator, or null when it names none.</summary>
    private static (string StarId, string? RunwayDesignator)? FindStarToken(NavigationDatabase navDb, string[] tokens)
    {
        foreach (string token in tokens)
        {
            string[] parts = token.Split('.');
            string? resolvedId = navDb.ResolveStarId(parts[0]);
            if (resolvedId is not null)
            {
                return (resolvedId, parts.Length > 1 ? parts[1] : null);
            }
        }

        return null;
    }

    /// <summary>
    /// Warns about the first procedure-shaped token — one with trailing version digits — that resolves as nothing a route
    /// may legitimately name: no STAR, old-version SID, airway, military route, or fix, navaid, airport or fix/radial/
    /// distance point.
    /// </summary>
    private static void WarnUnknownStarToken(NavigationDatabase navDb, string[] tokens, string callsign, List<string> warnings, string notApplied)
    {
        foreach (string token in tokens)
        {
            string rawName = token.Split('.')[0];
            if (IsKnownNonStarToken(navDb, rawName))
            {
                continue;
            }

            warnings.Add($"{callsign}: STAR {rawName} not found in NavData — {notApplied}");
            break;
        }
    }

    private static bool IsKnownNonStarToken(NavigationDatabase navDb, string rawName) =>
        (NavigationDatabase.StripTrailingDigits(rawName) == rawName)
        || (navDb.ResolveStarId(rawName) is not null)
        || (navDb.ResolveSidId(rawName) is not null)
        || navDb.IsAirway(rawName)
        || navDb.IsMilitaryRoute(rawName)
        || (navDb.ResolveFixOrFrd(rawName) is not null);

    /// <summary>
    /// Finds the best runway transition for a STAR procedure. Tries explicit designator first,
    /// falls back to DestinationRunway, then picks the first available transition.
    /// Returns the transition legs, or null if none found.
    /// </summary>
    internal static IReadOnlyList<CifpLeg>? FindRunwayTransition(CifpStarProcedure star, string? explicitDesignator, string? destinationRunway)
    {
        if (star.RunwayTransitions.Count == 0)
        {
            return null;
        }

        // Try explicit designator from the nav path token (e.g., "ALWYS3.19L" → "19L")
        IReadOnlyList<CifpLeg>? rwLegs = TryLookupRunwayTransition(star, explicitDesignator);
        if (rwLegs is not null)
        {
            return rwLegs;
        }

        // Try the aircraft's assigned destination runway
        rwLegs = TryLookupRunwayTransition(star, destinationRunway);
        if (rwLegs is not null)
        {
            return rwLegs;
        }

        // No runway known — pick the first available transition.
        // All transitions lead to the same airport; lateral differences at the STAR level are minimal.
        return star.RunwayTransitions.Values.First().Legs;
    }

    private static IReadOnlyList<CifpLeg>? TryLookupRunwayTransition(CifpStarProcedure star, string? designator)
    {
        if (designator is null)
        {
            return null;
        }

        // CIFP runway-transition keys are zero-padded ("RW01R"); pad a single-digit designator first.
        string padded = RunwayIdentifier.NormalizeDesignator(designator);
        string rwKey = "RW" + padded;
        if (star.RunwayTransitions.TryGetValue(rwKey, out CifpTransition? transition))
        {
            return transition.Legs;
        }

        // Fall back to "both" key (e.g., RW19B for RW19L/RW19R)
        string bothKey = "RW" + padded.TrimEnd('L', 'R', 'C') + "B";
        if (star.RunwayTransitions.TryGetValue(bothKey, out transition))
        {
            return transition.Legs;
        }

        return null;
    }
}
