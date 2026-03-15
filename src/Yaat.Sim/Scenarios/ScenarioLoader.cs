using System.Text.Json;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Scenarios;

public class ScenarioLoadResult
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? PrimaryAirportId { get; init; }
    public string? ArtccId { get; init; }
    public string? StudentPositionId { get; init; }
    public List<ScenarioAtc> AtcEntries { get; init; } = [];
    public List<LoadedAircraft> ImmediateAircraft { get; init; } = [];
    public List<LoadedAircraft> DelayedAircraft { get; init; } = [];
    public List<LoadedAircraft> DeferredAircraft { get; init; } = [];
    public List<InitializationTrigger> Triggers { get; init; } = [];
    public List<ScenarioGeneratorConfig> Generators { get; init; } = [];
    public string? AutoDeleteMode { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public class LoadedAircraft
{
    public required AircraftState State { get; init; }
    public int SpawnDelaySeconds { get; init; }
    public string? DeferralReason { get; init; }
    public List<PresetCommand> PresetCommands { get; init; } = [];
    public AutoTrackConditions? AutoTrackConditions { get; init; }
    public List<string> AutoTrackMessages { get; } = [];
}

public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static ScenarioLoadResult Load(string json, IAirportGroundData? groundData, Random rng)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(json, JsonOptions);

        if (scenario is null)
        {
            return new ScenarioLoadResult { Warnings = ["Failed to deserialize scenario JSON"] };
        }

        var warnings = new List<string>();
        var immediate = new List<LoadedAircraft>();
        var delayed = new List<LoadedAircraft>();
        var deferred = new List<LoadedAircraft>();

        foreach (var ac in scenario.Aircraft)
        {
            var loaded = LoadAircraft(ac, warnings, groundData, scenario.PrimaryAirportId, scenario.PrimaryApproach, rng);
            if (loaded is null)
            {
                continue;
            }

            if (loaded.DeferralReason is not null)
            {
                deferred.Add(loaded);
            }
            else if (loaded.SpawnDelaySeconds > 0)
            {
                delayed.Add(loaded);
            }
            else
            {
                immediate.Add(loaded);
            }
        }

        return new ScenarioLoadResult
        {
            Id = scenario.Id,
            Name = scenario.Name,
            PrimaryAirportId = scenario.PrimaryAirportId,
            ArtccId = scenario.ArtccId,
            StudentPositionId = scenario.StudentPositionId,
            AtcEntries = scenario.Atc,
            ImmediateAircraft = immediate,
            DelayedAircraft = delayed,
            DeferredAircraft = deferred,
            Triggers = scenario.InitializationTriggers,
            Generators = scenario.AircraftGenerators,
            AutoDeleteMode = scenario.AutoDeleteMode,
            Warnings = warnings,
        };
    }

    private static AircraftState CreateBaseState(ScenarioAircraft ac, string? primaryApproach = null)
    {
        var fpType = ac.FlightPlan?.AircraftType;
        var equipType = !string.IsNullOrEmpty(fpType) ? fpType : ac.AircraftType;

        return new AircraftState
        {
            Callsign = ac.AircraftId,
            AircraftType = equipType,
            TransponderMode = ac.TransponderMode,
            FlightRules = ac.FlightPlan?.Rules ?? "IFR",
            CruiseAltitude = ac.FlightPlan?.CruiseAltitude ?? 0,
            CruiseSpeed = ac.FlightPlan?.CruiseSpeed ?? 0,
            Departure = ac.FlightPlan?.Departure ?? "",
            Destination = ac.FlightPlan?.Destination ?? "",
            Route = ac.FlightPlan?.Route ?? "",
            Remarks = ac.FlightPlan?.Remarks ?? "",
            EquipmentSuffix = ExtractSuffix(equipType),
            ExpectedApproach = ac.ExpectedApproach ?? primaryApproach,
        };
    }

    private static LoadedAircraft? LoadAircraft(
        ScenarioAircraft ac,
        List<string> warnings,
        IAirportGroundData? groundData,
        string? primaryAirportId,
        string? primaryApproach,
        Random rng
    )
    {
        var cond = ac.StartingConditions;
        double lat,
            lon,
            alt,
            speed,
            heading;

        var navDb = NavigationDatabase.Instance;
        var departureId = ac.FlightPlan?.Departure;
        var fieldElevation = !string.IsNullOrEmpty(departureId) ? navDb.GetAirportElevation(departureId) ?? 0 : 0;

        switch (cond.Type)
        {
            case "Coordinates":
                if (cond.Coordinates is null)
                {
                    warnings.Add($"{ac.AircraftId}: Coordinates " + "type but no coordinates provided");
                    return null;
                }
                lat = cond.Coordinates.Lat;
                lon = cond.Coordinates.Lon;
                alt = cond.Altitude ?? fieldElevation;
                speed = cond.Altitude is null && cond.Speed is null ? 0 : cond.Speed ?? -1;
                heading = ResolveHeading(cond, lat, lon, warnings, ac.AircraftId);
                break;

            case "FixOrFrd":
                if (cond.Fix is null)
                {
                    warnings.Add($"{ac.AircraftId}: FixOrFrd type " + "but no fix provided");
                    return null;
                }
                var resolved = FrdResolver.Resolve(cond.Fix, navDb);
                if (resolved is null)
                {
                    warnings.Add($"{ac.AircraftId}: Could not " + $"resolve fix '{cond.Fix}'");
                    return null;
                }
                lat = resolved.Latitude;
                lon = resolved.Longitude;
                alt = cond.Altitude ?? fieldElevation;
                speed = cond.Altitude is null && cond.Speed is null ? 0 : cond.Speed ?? -1;
                heading = ResolveHeading(cond, lat, lon, warnings, ac.AircraftId);
                break;

            case "OnRunway":
                return LoadOnRunway(ac, groundData, warnings, primaryApproach, rng);

            case "OnFinal":
                return LoadOnFinal(ac, groundData, warnings, primaryApproach, rng);

            case "Parking":
                return LoadAtParking(ac, groundData, primaryAirportId, warnings, primaryApproach, rng);

            default:
                warnings.Add($"{ac.AircraftId}: Unknown starting " + $"condition type '{cond.Type}'");
                return null;
        }

        var category = AircraftCategorization.Categorize(ac.AircraftType);

        if (speed < 0)
        {
            speed = CategoryPerformance.DefaultSpeed(category, alt);
        }

        var state = CreateBaseState(ac, primaryApproach);
        state.Latitude = lat;
        state.Longitude = lon;
        state.Heading = heading;
        state.Track = heading;
        state.Altitude = alt;
        state.IndicatedAirspeed = speed;
        var code = SimulationWorld.GenerateBeaconCode(rng);
        state.AssignedBeaconCode = code;
        state.BeaconCode = code;

        // Ground detection: near field elevation + zero speed = on the ground
        var agl = alt - fieldElevation;
        if (speed <= 0 && agl < 200)
        {
            state.IsOnGround = true;
            var phases = new PhaseList();
            phases.Add(new AtParkingPhase());
            state.Phases = phases;

            // Resolve ground layout from departure (on-ground aircraft) or destination
            var groundAirportId = ac.FlightPlan?.Departure ?? ac.FlightPlan?.Destination;
            state.GroundLayout = !string.IsNullOrEmpty(groundAirportId) ? groundData?.GetLayout(groundAirportId) : null;
        }

        PopulateNavigationRoute(state, cond.NavigationPath, warnings);

        if (ac.OnAltitudeProfile)
        {
            ApplyAltitudeProfile(state, cond.NavigationPath, warnings);
        }

        return new LoadedAircraft
        {
            State = state,
            SpawnDelaySeconds = ac.SpawnDelay,
            PresetCommands = ac.PresetCommands,
            AutoTrackConditions = ac.AutoTrackConditions,
        };
    }

    private static LoadedAircraft? LoadOnRunway(
        ScenarioAircraft ac,
        IAirportGroundData? groundData,
        List<string> warnings,
        string? primaryApproach,
        Random rng
    )
    {
        var runwayId = ac.StartingConditions.Runway;
        var airportId = ac.AirportId ?? ac.FlightPlan?.Departure ?? "";

        if (string.IsNullOrEmpty(runwayId) || string.IsNullOrEmpty(airportId))
        {
            warnings.Add($"{ac.AircraftId}: OnRunway requires runway and airport ID");
            return BuildDeferredAircraft(ac, primaryApproach, "OnRunway (missing runway/airport)");
        }

        var rwy = NavigationDatabase.Instance.GetRunway(airportId, runwayId);
        if (rwy is null)
        {
            warnings.Add($"{ac.AircraftId}: Could not find runway {runwayId} at {airportId}");
            return BuildDeferredAircraft(ac, primaryApproach, $"OnRunway ({airportId}/{runwayId} not found)");
        }

        var rwyCategory = AircraftCategorization.Categorize(ac.AircraftType);
        var init = AircraftInitializer.InitializeOnRunway(rwy, rwyCategory);

        var state = CreateBaseState(ac, primaryApproach);
        state.Latitude = init.Latitude;
        state.Longitude = init.Longitude;
        state.Heading = init.Heading;
        state.Track = init.Heading;
        state.Altitude = init.Altitude;
        state.IndicatedAirspeed = init.Speed;
        state.IsOnGround = init.IsOnGround;
        var rwyCode = SimulationWorld.GenerateBeaconCode(rng);
        state.AssignedBeaconCode = rwyCode;
        state.BeaconCode = rwyCode;
        state.Phases = init.Phases;
        state.GroundLayout = groundData?.GetLayout(airportId);

        return new LoadedAircraft
        {
            State = state,
            SpawnDelaySeconds = ac.SpawnDelay,
            PresetCommands = ac.PresetCommands,
            AutoTrackConditions = ac.AutoTrackConditions,
        };
    }

    private static LoadedAircraft? LoadOnFinal(
        ScenarioAircraft ac,
        IAirportGroundData? groundData,
        List<string> warnings,
        string? primaryApproach,
        Random rng
    )
    {
        var runwayId = ac.StartingConditions.Runway;
        var airportId = ac.AirportId ?? ac.FlightPlan?.Departure ?? "";

        if (string.IsNullOrEmpty(runwayId) || string.IsNullOrEmpty(airportId))
        {
            warnings.Add($"{ac.AircraftId}: OnFinal requires runway and airport ID");
            return BuildDeferredAircraft(ac, primaryApproach, "OnFinal (missing runway/airport)");
        }

        var rwy = NavigationDatabase.Instance.GetRunway(airportId, runwayId);
        if (rwy is null)
        {
            warnings.Add($"{ac.AircraftId}: Could not find runway {runwayId} at {airportId}");
            return BuildDeferredAircraft(ac, primaryApproach, $"OnFinal ({airportId}/{runwayId} not found)");
        }

        var category = AircraftCategorization.Categorize(ac.AircraftType);
        var init = AircraftInitializer.InitializeOnFinal(
            rwy,
            category,
            ac.StartingConditions.Altitude,
            ac.StartingConditions.Speed,
            ac.StartingConditions.DistanceFromRunway,
            ac.AircraftType
        );

        var state = CreateBaseState(ac, primaryApproach);
        state.Latitude = init.Latitude;
        state.Longitude = init.Longitude;
        state.Heading = init.Heading;
        state.Track = init.Heading;
        state.Altitude = init.Altitude;
        state.IndicatedAirspeed = init.Speed;
        state.IsOnGround = init.IsOnGround;
        var finalCode = SimulationWorld.GenerateBeaconCode(rng);
        state.AssignedBeaconCode = finalCode;
        state.BeaconCode = finalCode;
        state.Phases = init.Phases;

        // Arriving aircraft: use destination airport layout for runway exit after landing
        var destId = ac.FlightPlan?.Destination;
        state.GroundLayout = !string.IsNullOrEmpty(destId) ? groundData?.GetLayout(destId) : null;

        return new LoadedAircraft
        {
            State = state,
            SpawnDelaySeconds = ac.SpawnDelay,
            PresetCommands = ac.PresetCommands,
            AutoTrackConditions = ac.AutoTrackConditions,
        };
    }

    private static LoadedAircraft? LoadAtParking(
        ScenarioAircraft ac,
        IAirportGroundData? groundData,
        string? primaryAirportId,
        List<string> warnings,
        string? primaryApproach,
        Random rng
    )
    {
        var cond = ac.StartingConditions;
        var airportId = ac.AirportId ?? primaryAirportId ?? ac.FlightPlan?.Departure ?? "";

        if (string.IsNullOrEmpty(airportId))
        {
            warnings.Add($"{ac.AircraftId}: Parking requires airport ID");
            return BuildDeferredAircraft(ac, primaryApproach, "Parking (missing airport)");
        }

        if (groundData is null)
        {
            warnings.Add($"{ac.AircraftId}: Parking requires airport ground data");
            return BuildDeferredAircraft(ac, primaryApproach, "Parking (no ground data)");
        }

        var layout = groundData.GetLayout(airportId);
        if (layout is null)
        {
            warnings.Add($"{ac.AircraftId}: No ground layout for {airportId}");
            return BuildDeferredAircraft(ac, primaryApproach, $"Parking ({airportId} has no ground data)");
        }

        var parkingName = cond.Parking;
        if (string.IsNullOrEmpty(parkingName))
        {
            warnings.Add($"{ac.AircraftId}: Parking type but no parking name");
            return BuildDeferredAircraft(ac, primaryApproach, "Parking (missing parking name)");
        }

        // Search parking first, then helipads and other spots
        var node = layout.FindParkingByName(parkingName) ?? layout.FindSpotByName(parkingName);
        if (node is null)
        {
            warnings.Add($"{ac.AircraftId}: Parking '{parkingName}' not found at {airportId}");
            return BuildDeferredAircraft(ac, primaryApproach, $"Parking ({parkingName} not found)");
        }

        var elevation = NavigationDatabase.Instance.GetAirportElevation(airportId) ?? 0;
        var init = AircraftInitializer.InitializeAtParking(node, elevation);

        var state = CreateBaseState(ac, primaryApproach);
        state.Latitude = init.Latitude;
        state.Longitude = init.Longitude;
        state.Heading = init.Heading;
        state.Track = init.Heading;
        state.Altitude = init.Altitude;
        state.IndicatedAirspeed = init.Speed;
        state.IsOnGround = init.IsOnGround;
        var parkCode = SimulationWorld.GenerateBeaconCode(rng);
        state.AssignedBeaconCode = parkCode;
        state.BeaconCode = parkCode;
        state.Phases = init.Phases;
        state.AutoDeleteExempt = true;
        state.GroundLayout = layout;

        return new LoadedAircraft
        {
            State = state,
            SpawnDelaySeconds = ac.SpawnDelay,
            PresetCommands = ac.PresetCommands,
            AutoTrackConditions = ac.AutoTrackConditions,
        };
    }

    private static LoadedAircraft BuildDeferredAircraft(ScenarioAircraft ac, string? primaryApproach, string reason)
    {
        return new LoadedAircraft
        {
            State = CreateBaseState(ac, primaryApproach),
            DeferralReason = reason,
            PresetCommands = ac.PresetCommands,
        };
    }

    private static double ResolveHeading(StartingConditions cond, double lat, double lon, List<string> warnings, string callsign)
    {
        if (cond.Heading is not null)
        {
            return cond.Heading.Value;
        }

        if (cond.NavigationPath is null or "")
        {
            return 0;
        }

        var firstWaypoint = cond.NavigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (firstWaypoint is null)
        {
            return 0;
        }

        // Strip SID/STAR suffixes like "ALTTA9.29R"
        var fixName = firstWaypoint.Split('.')[0];

        // Strip trailing digits that might be a SID number
        // (e.g., "ALTTA9" → "ALTTA")
        while (fixName.Length > 2 && char.IsDigit(fixName[^1]))
        {
            fixName = fixName[..^1];
        }

        var targetPos = NavigationDatabase.Instance.GetFixPosition(fixName);
        if (targetPos is null)
        {
            warnings.Add($"{callsign}: Could not resolve nav " + $"waypoint '{firstWaypoint}', heading 0");
            return 0;
        }

        return GeoMath.BearingTo(lat, lon, targetPos.Value.Lat, targetPos.Value.Lon);
    }

    private static void PopulateNavigationRoute(AircraftState state, string? navigationPath, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath))
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;

        // Emit version-change warnings by comparing input tokens against resolved SID/STAR IDs
        EmitVersionChangeWarnings(navigationPath, warnings, state.Callsign);

        // Expand route tokens into fix names
        var expanded = RouteExpander.Expand(navigationPath);

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
        AppendStarRunwayTransition(resolved, navigationPath, state.Destination);

        RouteChainer.AppendRouteRemainder(resolved, state.Route);

        foreach (var fix in resolved)
        {
            state.Targets.NavigationRoute.Add(
                new NavigationTarget
                {
                    Name = fix.Name,
                    Latitude = fix.Lat,
                    Longitude = fix.Lon,
                }
            );
        }
    }

    private static void EmitVersionChangeWarnings(string navigationPath, List<string> warnings, string callsign)
    {
        var navDb = NavigationDatabase.Instance;
        var tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var rawName = token.Split('.')[0];

            var resolvedSidId = navDb.ResolveSidId(rawName);
            if (resolvedSidId is not null && !resolvedSidId.Equals(rawName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{callsign}: SID {rawName} not found, using current version {resolvedSidId}");
                continue;
            }

            var resolvedStarId = navDb.ResolveStarId(rawName);
            if (resolvedStarId is not null && !resolvedStarId.Equals(rawName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{callsign}: STAR {rawName} not found, using current version {resolvedStarId}");
            }
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

                resolved.Add(new ResolvedFix(target.Name, target.Latitude, target.Longitude));
                existingNames.Add(target.Name);
            }

            break;
        }
    }

    /// <summary>
    /// When onAltitudeProfile is true, finds the STAR in the navigation path,
    /// resolves CIFP altitude/speed constraints, overlays them on route targets,
    /// and enables StarViaMode (equivalent to auto-DVIA at spawn).
    /// </summary>
    private static void ApplyAltitudeProfile(AircraftState state, string? navigationPath, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath) || string.IsNullOrEmpty(state.Destination))
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
                if (!resolvedId.Equals(rawName, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"{state.Callsign}: STAR {rawName} not found, using current version {resolvedId}");
                }

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

        var star = navDb.GetStar(state.Destination, starId);
        if (star is null)
        {
            return;
        }

        // Build CIFP-constrained targets: common legs + runway transition
        var orderedLegs = new List<CifpLeg>();
        orderedLegs.AddRange(star.CommonLegs);

        var rwTransitionLegs = FindRunwayTransition(star, runwayDesignator, state.DestinationRunway);
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
                    Latitude = route[i].Latitude,
                    Longitude = route[i].Longitude,
                    AltitudeRestriction = constrained.AltitudeRestriction,
                    SpeedRestriction = constrained.SpeedRestriction,
                    IsFlyOver = constrained.IsFlyOver,
                };
            }
        }

        state.ActiveStarId = starId;
        state.StarViaMode = true;

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
    private static IReadOnlyList<CifpLeg>? FindRunwayTransition(CifpStarProcedure star, string? explicitDesignator, string? destinationRunway)
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

    private static string ExtractSuffix(string equipType)
    {
        if (equipType.Contains('/'))
        {
            var parts = equipType.Split('/');
            return parts[^1];
        }
        return "A";
    }
}
