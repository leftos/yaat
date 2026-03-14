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

    public static ScenarioLoadResult Load(string json, NavigationDatabase navDb, IAirportGroundData? groundData, Random rng)
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
            var loaded = LoadAircraft(ac, navDb, warnings, groundData, scenario.PrimaryAirportId, scenario.PrimaryApproach, rng);
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
        NavigationDatabase navDb,
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
                heading = ResolveHeading(cond, lat, lon, navDb, warnings, ac.AircraftId);
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
                heading = ResolveHeading(cond, lat, lon, navDb, warnings, ac.AircraftId);
                break;

            case "OnRunway":
                return LoadOnRunway(ac, navDb, groundData, warnings, primaryApproach, rng);

            case "OnFinal":
                return LoadOnFinal(ac, navDb, groundData, warnings, primaryApproach, rng);

            case "Parking":
                return LoadAtParking(ac, navDb, groundData, primaryAirportId, warnings, primaryApproach, rng);

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

        PopulateNavigationRoute(state, cond.NavigationPath, navDb, warnings);

        if (ac.OnAltitudeProfile)
        {
            ApplyAltitudeProfile(state, cond.NavigationPath, navDb, warnings);
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
        NavigationDatabase navDb,
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

        var rwy = navDb.GetRunway(airportId, runwayId);
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
        NavigationDatabase navDb,
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

        var rwy = navDb.GetRunway(airportId, runwayId);
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
        NavigationDatabase navDb,
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

        var elevation = navDb.GetAirportElevation(airportId) ?? 0;
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

    private static double ResolveHeading(
        StartingConditions cond,
        double lat,
        double lon,
        NavigationDatabase navDb,
        List<string> warnings,
        string callsign
    )
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

        var targetPos = navDb.GetFixPosition(fixName);
        if (targetPos is null)
        {
            warnings.Add($"{callsign}: Could not resolve nav " + $"waypoint '{firstWaypoint}', heading 0");
            return 0;
        }

        return GeoMath.BearingTo(lat, lon, targetPos.Value.Lat, targetPos.Value.Lon);
    }

    private static void PopulateNavigationRoute(AircraftState state, string? navigationPath, NavigationDatabase navDb, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath))
        {
            return;
        }

        var tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var resolved = new List<ResolvedFix>();

        for (int tokenIdx = 0; tokenIdx < tokens.Length; tokenIdx++)
        {
            var token = tokens[tokenIdx];

            // Split runway suffix (e.g., "EMZOH4.30" → name="EMZOH4", runway="30")
            var parts = token.Split('.');
            var rawName = parts[0];
            string? runwayDesignator = parts.Length > 1 ? parts[1] : null;

            // Check SID first (e.g., "CNDEL5") — expand body + match transition to next token
            var sidBody = navDb.GetSidBody(rawName);
            if (sidBody is not null && sidBody.Count > 0)
            {
                string? nextToken = tokenIdx + 1 < tokens.Length ? tokens[tokenIdx + 1].Split('.')[0] : null;
                ExpandSidBody(resolved, rawName, sidBody, nextToken, navDb, warnings, state.Callsign);
                continue;
            }

            // Check if this token is a STAR reference (e.g., "EMZOH4")
            var starBody = navDb.GetStarBody(rawName);
            if (starBody is not null && starBody.Count > 0)
            {
                ExpandStarBody(resolved, rawName, starBody, runwayDesignator, state.Destination, navDb, warnings, state.Callsign);
                continue;
            }

            // Regular fix: strip trailing digits (e.g., procedure version numbers)
            var fixName = rawName;
            while (fixName.Length > 2 && char.IsDigit(fixName[^1]))
            {
                fixName = fixName[..^1];
            }

            if (resolved.Count > 0 && fixName.Equals(resolved[^1].Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pos = navDb.GetFixPosition(fixName);
            if (pos is null)
            {
                warnings.Add($"{state.Callsign}: Could not resolve nav fix '{token}', skipping");
                continue;
            }

            resolved.Add(new ResolvedFix(fixName, pos.Value.Lat, pos.Value.Lon));
        }

        RouteChainer.AppendRouteRemainder(resolved, state.Route, navDb);

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

    /// <summary>
    /// When onAltitudeProfile is true, finds the STAR in the navigation path,
    /// resolves CIFP altitude/speed constraints, overlays them on route targets,
    /// and enables StarViaMode (equivalent to auto-DVIA at spawn).
    /// </summary>
    private static void ApplyAltitudeProfile(AircraftState state, string? navigationPath, NavigationDatabase navDb, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath) || string.IsNullOrEmpty(state.Destination))
        {
            return;
        }

        // Find the STAR token in the navigation path
        var tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? starId = null;
        string? runwayDesignator = null;

        foreach (var token in tokens)
        {
            var parts = token.Split('.');
            var rawName = parts[0];
            var starBody = navDb.GetStarBody(rawName);
            if (starBody is not null && starBody.Count > 0)
            {
                starId = rawName;
                runwayDesignator = parts.Length > 1 ? parts[1] : null;
                break;
            }
        }

        if (starId is null)
        {
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

        if (runwayDesignator is not null)
        {
            var rwKey = "RW" + runwayDesignator;
            if (!star.RunwayTransitions.TryGetValue(rwKey, out var rwTransition))
            {
                var bothKey = "RW" + runwayDesignator.TrimEnd('L', 'R', 'C') + "B";
                star.RunwayTransitions.TryGetValue(bothKey, out rwTransition);
            }

            if (rwTransition is not null)
            {
                orderedLegs.AddRange(rwTransition.Legs);
            }
        }

        if (orderedLegs.Count == 0)
        {
            return;
        }

        var constrainedTargets = DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs, navDb);

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
    /// Expands a SID body into resolved fixes, then appends transition fixes
    /// if the next route token matches a transition endpoint.
    /// </summary>
    private static void ExpandSidBody(
        List<ResolvedFix> resolved,
        string sidId,
        IReadOnlyList<string> sidBody,
        string? nextToken,
        NavigationDatabase navDb,
        List<string> warnings,
        string callsign
    )
    {
        foreach (var fixName in sidBody)
        {
            AddResolvedFix(resolved, fixName, navDb, warnings, callsign, sidId);
        }

        if (nextToken is null)
        {
            return;
        }

        // Strip trailing digits from next token to get the fix name for matching
        var nextFixName = nextToken;
        while (nextFixName.Length > 2 && char.IsDigit(nextFixName[^1]))
        {
            nextFixName = nextFixName[..^1];
        }

        var transitions = navDb.GetSidTransitions(sidId);
        if (transitions is null)
        {
            return;
        }

        // Find the transition whose endpoint matches the next route token
        foreach (var trans in transitions)
        {
            if (!trans.Name.Equals(nextFixName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Append transition fixes (skipping any that overlap with the end of body)
            foreach (var fixName in trans.Fixes)
            {
                AddResolvedFix(resolved, fixName, navDb, warnings, callsign, sidId);
            }

            break;
        }
    }

    /// <summary>
    /// Expands a STAR into resolved fixes, starting from the last already-resolved fix.
    /// Uses NavData for the common body and CIFP (if available) for runway transitions.
    /// </summary>
    private static void ExpandStarBody(
        List<ResolvedFix> resolved,
        string starId,
        IReadOnlyList<string> starBody,
        string? runwayDesignator,
        string? destination,
        NavigationDatabase navDb,
        List<string> warnings,
        string callsign
    )
    {
        int startIdx = 0;

        // If we have a preceding fix, find it in the STAR body to determine the join point
        if (resolved.Count > 0)
        {
            var lastFix = resolved[^1].Name;
            for (int i = 0; i < starBody.Count; i++)
            {
                if (starBody[i].Equals(lastFix, StringComparison.OrdinalIgnoreCase))
                {
                    startIdx = i + 1;
                    break;
                }
            }

            // Also check STAR transitions for the join fix
            if (startIdx == 0)
            {
                var transitions = navDb.GetStarTransitions(starId);
                if (transitions is not null)
                {
                    foreach (var trans in transitions)
                    {
                        int transIdx = -1;
                        for (int i = 0; i < trans.Fixes.Count; i++)
                        {
                            if (trans.Fixes[i].Equals(lastFix, StringComparison.OrdinalIgnoreCase))
                            {
                                transIdx = i;
                                break;
                            }
                        }

                        if (transIdx >= 0)
                        {
                            for (int i = transIdx + 1; i < trans.Fixes.Count; i++)
                            {
                                AddResolvedFix(resolved, trans.Fixes[i], navDb, warnings, callsign, starId);
                            }

                            startIdx = 0;
                            break;
                        }
                    }
                }
            }
        }

        // Append STAR body fixes from the start index
        for (int i = startIdx; i < starBody.Count; i++)
        {
            AddResolvedFix(resolved, starBody[i], navDb, warnings, callsign, starId);
        }

        // Append runway transition legs from CIFP if available
        if (runwayDesignator is not null && destination is not null)
        {
            var star = navDb.GetStar(destination, starId);
            if (star is not null)
            {
                var rwKey = "RW" + runwayDesignator;
                if (!star.RunwayTransitions.TryGetValue(rwKey, out var rwTransition))
                {
                    var bothKey = "RW" + runwayDesignator.TrimEnd('L', 'R', 'C') + "B";
                    star.RunwayTransitions.TryGetValue(bothKey, out rwTransition);
                }

                if (rwTransition is not null)
                {
                    var rwTargets = Commands.DepartureClearanceHandler.ResolveLegsToTargets(rwTransition.Legs, navDb);
                    foreach (var target in rwTargets)
                    {
                        if (resolved.Count > 0 && target.Name.Equals(resolved[^1].Name, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        resolved.Add(new ResolvedFix(target.Name, target.Latitude, target.Longitude));
                    }
                }
            }
        }
    }

    private static void AddResolvedFix(
        List<ResolvedFix> resolved,
        string fixName,
        NavigationDatabase navDb,
        List<string> warnings,
        string callsign,
        string starId
    )
    {
        if (resolved.Count > 0 && fixName.Equals(resolved[^1].Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            warnings.Add($"{callsign}: Could not resolve STAR {starId} fix '{fixName}', skipping");
            return;
        }

        resolved.Add(new ResolvedFix(fixName, pos.Value.Lat, pos.Value.Lon));
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
