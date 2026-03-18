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
            HasFlightPlan = ac.FlightPlan is not null,
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
            speed;

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
            speed = AircraftPerformance.DefaultSpeed(ac.AircraftType, category, alt, null);
        }

        var state = CreateBaseState(ac, primaryApproach);
        state.Latitude = lat;
        state.Longitude = lon;
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

        var navigationPath = ResolveVersionChanges(cond.NavigationPath ?? "", state, warnings);
        PopulateNavigationRoute(state, navigationPath, warnings);

        // Derive heading: scenario-assigned heading, or bearing to first nav route fix
        var heading = cond.Heading ?? 0.0;
        if (cond.Heading is null && state.Targets.NavigationRoute.Count > 0)
        {
            var first = state.Targets.NavigationRoute[0];
            heading = GeoMath.BearingTo(lat, lon, first.Latitude, first.Longitude);
        }
        state.Heading = heading;
        state.Track = heading;

        if (ac.OnAltitudeProfile)
        {
            ApplyAltitudeProfile(state, navigationPath, warnings);
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

    private static void PopulateNavigationRoute(AircraftState state, string? navigationPath, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath))
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;

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

    /// <summary>
    /// Detects SID/STAR version upgrades in the navigation path, emits warnings,
    /// and substitutes stale transition fixes with the geographically closest valid one.
    /// Returns the (possibly modified) navigation path. Also updates state.Route in place.
    /// </summary>
    private static string ResolveVersionChanges(string navigationPath, AircraftState state, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath))
        {
            return navigationPath;
        }

        var navDb = NavigationDatabase.Instance;
        var tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var routeTokens = state.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool modified = false;

        for (int i = 0; i < tokens.Length; i++)
        {
            var dotParts = tokens[i].Split('.');
            var rawName = dotParts[0];

            // Skip numeric tokens (altitude/speed constraints)
            if (double.TryParse(rawName, out _))
            {
                continue;
            }

            // --- SID upgrade ---
            var resolvedSidId = navDb.ResolveSidId(rawName);
            if (resolvedSidId is not null && !resolvedSidId.Equals(rawName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{state.Callsign}: SID {rawName} not found, using current version {resolvedSidId}");

                // Replace procedure name in tokens and route
                ReplaceProcedureToken(tokens, i, dotParts, resolvedSidId);
                ReplaceInRoute(routeTokens, rawName, resolvedSidId);
                modified = true;

                // Find the next non-numeric token — if it was a transition exit fix on the
                // old SID version, it may no longer exist on the new one.
                int nextIdx = FindNextNonNumericTokenIndex(tokens, i + 1);
                if (nextIdx >= 0)
                {
                    var nextFixDotParts = tokens[nextIdx].Split('.');
                    var nextFixName = nextFixDotParts[0];
                    if (!IsFixOnSid(nextFixName, resolvedSidId, state.Departure, navDb))
                    {
                        var transitions = navDb.GetSidTransitions(resolvedSidId);
                        if (transitions is not null && transitions.Count > 0)
                        {
                            var closest = FindClosestTransitionFix(nextFixName, transitions, navDb);

                            // Fallback: old fix not in navdb — use the fix after it as a
                            // geographic reference (the aircraft is heading toward that fix).
                            if (closest is null)
                            {
                                int beyondIdx = FindNextNonNumericTokenIndex(tokens, nextIdx + 1);
                                if (beyondIdx >= 0)
                                {
                                    var beyondPos = ResolveTokenPosition(tokens[beyondIdx], navDb);
                                    if (beyondPos is not null)
                                    {
                                        closest = FindClosestTransitionFixToPosition(beyondPos.Value, transitions, navDb);
                                    }
                                }
                            }

                            if (closest is not null)
                            {
                                warnings.Add($"{state.Callsign}: SID fix {nextFixName} not on {resolvedSidId}, using closest transition: {closest}");
                                var newToken = nextFixDotParts.Length > 1 ? closest + "." + nextFixDotParts[1] : closest;
                                ReplaceInRoute(routeTokens, nextFixName, closest);
                                tokens[nextIdx] = newToken;
                                modified = true;
                            }
                            else
                            {
                                warnings.Add($"{state.Callsign}: SID fix {nextFixName} not on {resolvedSidId} and no suitable replacement found");
                            }
                        }
                    }
                }

                continue;
            }

            // --- STAR upgrade ---
            var resolvedStarId = navDb.ResolveStarId(rawName);
            if (resolvedStarId is not null && !resolvedStarId.Equals(rawName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{state.Callsign}: STAR {rawName} not found, using current version {resolvedStarId}");

                // Replace procedure name in tokens and route
                ReplaceProcedureToken(tokens, i, dotParts, resolvedStarId);
                ReplaceInRoute(routeTokens, rawName, resolvedStarId);
                modified = true;

                // Find the preceding non-numeric token — if it was a transition entry fix
                // on the old STAR version, it may no longer exist on the new one.
                int prevIdx = FindPrecedingNonNumericTokenIndex(tokens, i - 1);
                if (prevIdx >= 0)
                {
                    var prevFixDotParts = tokens[prevIdx].Split('.');
                    var prevFixName = prevFixDotParts[0];
                    if (!IsFixOnStar(prevFixName, resolvedStarId, state.Destination, navDb))
                    {
                        var transitions = navDb.GetStarTransitions(resolvedStarId);
                        if (transitions is not null && transitions.Count > 0)
                        {
                            var closest = FindClosestTransitionFix(prevFixName, transitions, navDb);

                            // Fallback: old fix not in navdb — use the fix before it as a
                            // geographic reference (the aircraft is coming from that fix).
                            if (closest is null)
                            {
                                int beforeIdx = FindPrecedingNonNumericTokenIndex(tokens, prevIdx - 1);
                                if (beforeIdx >= 0)
                                {
                                    var beforePos = ResolveTokenPosition(tokens[beforeIdx], navDb);
                                    if (beforePos is not null)
                                    {
                                        closest = FindClosestTransitionFixToPosition(beforePos.Value, transitions, navDb);
                                    }
                                }
                            }

                            if (closest is not null)
                            {
                                warnings.Add(
                                    $"{state.Callsign}: STAR fix {prevFixName} not on {resolvedStarId}, using closest transition: {closest}"
                                );
                                var newToken = prevFixDotParts.Length > 1 ? closest + "." + prevFixDotParts[1] : closest;
                                ReplaceInRoute(routeTokens, prevFixName, closest);
                                tokens[prevIdx] = newToken;
                                modified = true;
                            }
                            else
                            {
                                warnings.Add($"{state.Callsign}: STAR fix {prevFixName} not on {resolvedStarId} and no suitable replacement found");
                            }
                        }
                    }
                }
            }
        }

        if (modified)
        {
            state.Route = string.Join(" ", routeTokens);
            return string.Join(" ", tokens);
        }

        return navigationPath;
    }

    private static void ReplaceProcedureToken(string[] tokens, int index, string[] dotParts, string resolvedId)
    {
        tokens[index] = dotParts.Length > 1 ? resolvedId + "." + dotParts[1] : resolvedId;
    }

    private static void ReplaceInRoute(List<string> routeTokens, string oldName, string newName)
    {
        for (int i = 0; i < routeTokens.Count; i++)
        {
            if (routeTokens[i].Split('.')[0].Equals(oldName, StringComparison.OrdinalIgnoreCase))
            {
                var parts = routeTokens[i].Split('.');
                routeTokens[i] = parts.Length > 1 ? newName + "." + parts[1] : newName;
                return;
            }
        }
    }

    /// <summary>
    /// Returns true if the fix appears anywhere on the SID: NavData body, NavData enroute
    /// transitions (names + fix lists), or CIFP runway transition legs. If true, RouteExpander
    /// will handle it correctly and no substitution is needed.
    /// </summary>
    internal static bool IsFixOnSid(string fixName, string sidId, string airportCode, NavigationDatabase navDb)
    {
        var body = navDb.GetSidBody(sidId);
        if (body is not null && body.Any(f => f.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var transitions = navDb.GetSidTransitions(sidId);
        if (
            transitions is not null
            && transitions.Any(t =>
                t.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase) || t.Fixes.Any(f => f.Equals(fixName, StringComparison.OrdinalIgnoreCase))
            )
        )
        {
            return true;
        }

        // Check CIFP runway transitions (not in NavData)
        if (!string.IsNullOrEmpty(airportCode))
        {
            var cifpSid = navDb.GetSid(airportCode, sidId);
            if (cifpSid is not null && HasFixInRunwayTransitions(fixName, cifpSid.RunwayTransitions))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the fix appears anywhere on the STAR: NavData body, NavData enroute
    /// transitions (names + fix lists), or CIFP runway transition legs. If true, RouteExpander
    /// will handle it correctly and no substitution is needed.
    /// </summary>
    internal static bool IsFixOnStar(string fixName, string starId, string airportCode, NavigationDatabase navDb)
    {
        var body = navDb.GetStarBody(starId);
        if (body is not null && body.Any(f => f.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var transitions = navDb.GetStarTransitions(starId);
        if (
            transitions is not null
            && transitions.Any(t =>
                t.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase) || t.Fixes.Any(f => f.Equals(fixName, StringComparison.OrdinalIgnoreCase))
            )
        )
        {
            return true;
        }

        // Check CIFP runway transitions (not in NavData)
        if (!string.IsNullOrEmpty(airportCode))
        {
            var cifpStar = navDb.GetStar(airportCode, starId);
            if (cifpStar is not null && HasFixInRunwayTransitions(fixName, cifpStar.RunwayTransitions))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasFixInRunwayTransitions(string fixName, IReadOnlyDictionary<string, CifpTransition> runwayTransitions)
    {
        foreach (var (_, transition) in runwayTransitions)
        {
            if (transition.Legs.Any(leg => leg.FixIdentifier.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? FindClosestTransitionFix(
        string oldFixName,
        IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> transitions,
        NavigationDatabase navDb
    )
    {
        var oldPos = navDb.GetFixPosition(oldFixName);
        if (oldPos is null)
        {
            return null;
        }

        return FindClosestTransitionFixToPosition(oldPos.Value, transitions, navDb);
    }

    internal static string? FindClosestTransitionFixToPosition(
        (double Lat, double Lon) referencePosition,
        IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> transitions,
        NavigationDatabase navDb
    )
    {
        string? closest = null;
        double minDist = double.MaxValue;

        foreach (var trans in transitions)
        {
            var pos = navDb.GetFixPosition(trans.Name);
            if (pos is null)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(referencePosition.Lat, referencePosition.Lon, pos.Value.Lat, pos.Value.Lon);
            if (dist < minDist)
            {
                minDist = dist;
                closest = trans.Name;
            }
        }

        return closest;
    }

    /// <summary>
    /// Resolves a navigation path token to a lat/lon position, trying in order:
    /// named fix, FRD (fix-radial-distance), lat/lon coordinate (e.g., "3904N/10916W").
    /// </summary>
    internal static (double Lat, double Lon)? ResolveTokenPosition(string token, NavigationDatabase navDb)
    {
        var rawName = token.Split('.')[0];

        // 1. Named fix
        var fixPos = navDb.GetFixPosition(rawName);
        if (fixPos is not null)
        {
            return fixPos;
        }

        // 2. FRD (e.g., "LNK136052")
        var frd = FrdResolver.Resolve(rawName, navDb);
        if (frd is not null)
        {
            return (frd.Latitude, frd.Longitude);
        }

        // 3. Lat/lon coordinate (e.g., "3904N/10916W")
        var coord = ParseNavPathCoordinate(rawName);
        if (coord is not null)
        {
            return coord;
        }

        return null;
    }

    /// <summary>
    /// Parses a nav path coordinate in the format "DDMMN/DDDMMW" (e.g., "3904N/10916W").
    /// Returns null if the token doesn't match this format.
    /// </summary>
    internal static (double Lat, double Lon)? ParseNavPathCoordinate(string token)
    {
        var slashIdx = token.IndexOf('/');
        if (slashIdx < 3)
        {
            return null;
        }

        var latPart = token[..slashIdx];
        var lonPart = token[(slashIdx + 1)..];

        if (latPart.Length < 3 || lonPart.Length < 4)
        {
            return null;
        }

        char latDir = latPart[^1];
        char lonDir = lonPart[^1];

        if ((latDir != 'N' && latDir != 'S') || (lonDir != 'E' && lonDir != 'W'))
        {
            return null;
        }

        if (!int.TryParse(latPart[..^1], out int latRaw) || !int.TryParse(lonPart[..^1], out int lonRaw))
        {
            return null;
        }

        double latDeg = (latRaw / 100) + ((latRaw % 100) / 60.0);
        double lonDeg = (lonRaw / 100) + ((lonRaw % 100) / 60.0);

        if (latDir == 'S')
        {
            latDeg = -latDeg;
        }

        if (lonDir == 'W')
        {
            lonDeg = -lonDeg;
        }

        return (latDeg, lonDeg);
    }

    private static int FindNextNonNumericTokenIndex(string[] tokens, int startIndex)
    {
        for (int j = startIndex; j < tokens.Length; j++)
        {
            if (!double.TryParse(tokens[j].Split('.')[0], out _))
            {
                return j;
            }
        }

        return -1;
    }

    private static int FindPrecedingNonNumericTokenIndex(string[] tokens, int startIndex)
    {
        for (int j = startIndex; j >= 0; j--)
        {
            if (!double.TryParse(tokens[j].Split('.')[0], out _))
            {
                return j;
            }
        }

        return -1;
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
