using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Builds an aircraft's lateral navigation route from a navigation-path string and, optionally,
/// overlays a STAR's CIFP altitude/speed crossing restrictions and enables descend-via mode.
/// Shared by <see cref="ScenarioLoader"/> (scenario-defined arrivals) and
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

        var navDb = NavigationDatabase.Instance;

        // Expand route tokens into fix names. Flight-plan context: don't fabricate transitions on mismatch.
        var expanded = RouteExpander.Expand(navigationPath, navDb, includeAllTransitionsOnMismatch: false);

        // Resolve positions and build ResolvedFix list
        var resolved = new List<ResolvedFix>();
        foreach (var fixName in expanded)
        {
            if (resolved.Count > 0 && fixName.Equals(resolved[^1].Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pos = navDb.GetFixPosition(fixName);
            if (pos is null)
            {
                warnings.Add($"{state.Callsign}: Could not resolve nav fix '{fixName}', skipping");
                continue;
            }

            resolved.Add(new ResolvedFix(fixName, pos.Value.Lat, pos.Value.Lon));
        }

        // Append CIFP runway transition fixes for STARs
        AppendStarRunwayTransition(resolved, navigationPath, state.FlightPlan.Destination);

        RouteChainer.AppendRouteRemainder(resolved, state.FlightPlan.Route);

        foreach (var fix in resolved)
        {
            state.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
        }
    }

    /// <summary>
    /// Appends CIFP runway transition fixes for the STAR found in the navigation path.
    /// Only adds fixes not already present in the resolved list.
    /// </summary>
    private static void AppendStarRunwayTransition(List<ResolvedFix> resolved, string navigationPath, string? destination)
    {
        if (destination is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var parts = token.Split('.');
            var rawName = parts[0];
            string? runwayDesignator = parts.Length > 1 ? parts[1] : null;

            var resolvedStarId = navDb.ResolveStarId(rawName);
            if (resolvedStarId is null)
            {
                continue;
            }

            var star = navDb.GetStar(destination, resolvedStarId);
            if (star is null)
            {
                break;
            }

            var rwTransitionLegs = FindRunwayTransition(star, runwayDesignator, destinationRunway: null);
            if (rwTransitionLegs is null)
            {
                break;
            }

            var existingNames = new HashSet<string>(resolved.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var rwTargets = DepartureClearanceHandler.ResolveLegsToTargets(rwTransitionLegs);
            foreach (var target in rwTargets)
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
    }

    /// <summary>
    /// When descend-via is requested, finds the STAR in the navigation path,
    /// resolves CIFP altitude/speed constraints, overlays them on route targets,
    /// and enables StarViaMode (equivalent to auto-DVIA at spawn).
    /// </summary>
    internal static void ApplyAltitudeProfile(AircraftState state, string? navigationPath, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath) || string.IsNullOrEmpty(state.FlightPlan.Destination))
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;

        // Find the STAR token in the navigation path
        var tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? starId = null;
        string? runwayDesignator = null;

        foreach (var token in tokens)
        {
            var parts = token.Split('.');
            var rawName = parts[0];
            var resolvedId = navDb.ResolveStarId(rawName);
            if (resolvedId is not null)
            {
                starId = resolvedId;
                runwayDesignator = parts.Length > 1 ? parts[1] : null;
                break;
            }
        }

        if (starId is null)
        {
            // Check if any token looks like a procedure name (has trailing digits) but wasn't found
            foreach (var token in tokens)
            {
                var rawName = token.Split('.')[0];
                string baseName = NavigationDatabase.StripTrailingDigits(rawName);
                if (baseName != rawName)
                {
                    warnings.Add($"{state.Callsign}: STAR {rawName} not found in NavData — descend-via constraints not applied");
                    break;
                }
            }

            return;
        }

        var star = navDb.GetStar(state.FlightPlan.Destination, starId);
        if (star is null)
        {
            return;
        }

        // Build CIFP-constrained targets: common legs + runway transition
        var orderedLegs = new List<CifpLeg>();
        orderedLegs.AddRange(star.CommonLegs);

        var rwTransitionLegs = FindRunwayTransition(star, runwayDesignator, state.Procedure.DestinationRunway);
        if (rwTransitionLegs is not null)
        {
            orderedLegs.AddRange(rwTransitionLegs);
        }

        if (orderedLegs.Count == 0)
        {
            return;
        }

        var constrainedTargets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs);

        // Build a lookup of constraints by fix name
        var constraintsByFix = new Dictionary<string, NavigationTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in constrainedTargets)
        {
            constraintsByFix[target.Name] = target;
        }

        // Overlay constraints onto existing route targets
        var route = state.Targets.NavigationRoute;
        for (int i = 0; i < route.Count; i++)
        {
            if (constraintsByFix.TryGetValue(route[i].Name, out var constrained))
            {
                route[i] = new NavigationTarget
                {
                    Name = route[i].Name,
                    Position = route[i].Position,
                    AltitudeRestriction = constrained.AltitudeRestriction,
                    SpeedRestriction = constrained.SpeedRestriction,
                    IsFlyOver = constrained.IsFlyOver,
                };
            }
        }

        state.Procedure.ActiveStarId = starId;
        state.Procedure.StarViaMode = true;

        // Apply the first constrained fix's restrictions immediately so the aircraft
        // starts descending toward the first STAR constraint at spawn, not after
        // sequencing through unconstrained fixes.
        foreach (var target in route)
        {
            if (target.AltitudeRestriction is not null || target.SpeedRestriction is not null)
            {
                FlightPhysics.ApplyFixConstraints(state, target);
                break;
            }
        }
    }

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
        var rwLegs = TryLookupRunwayTransition(star, explicitDesignator);
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

        var rwKey = "RW" + designator;
        if (star.RunwayTransitions.TryGetValue(rwKey, out var transition))
        {
            return transition.Legs;
        }

        // Fall back to "both" key (e.g., RW19B for RW19L/RW19R)
        var bothKey = "RW" + designator.TrimEnd('L', 'R', 'C') + "B";
        if (star.RunwayTransitions.TryGetValue(bothKey, out transition))
        {
            return transition.Legs;
        }

        return null;
    }
}
