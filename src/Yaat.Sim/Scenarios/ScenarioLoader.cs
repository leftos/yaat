using System.Text.Json;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
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

    public static ScenarioLoadResult Load(string json, IFixLookup fixes, IRunwayLookup runways, IAirportGroundData? groundData, Random rng)
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
            var loaded = LoadAircraft(ac, fixes, runways, warnings, groundData, scenario.PrimaryAirportId, scenario.PrimaryApproach, rng);
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
        IFixLookup fixes,
        IRunwayLookup? runways,
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
        var fieldElevation = !string.IsNullOrEmpty(departureId) ? fixes.GetAirportElevation(departureId) ?? 0 : 0;

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
                heading = ResolveHeading(cond, lat, lon, fixes, warnings, ac.AircraftId);
                break;

            case "FixOrFrd":
                if (cond.Fix is null)
                {
                    warnings.Add($"{ac.AircraftId}: FixOrFrd type " + "but no fix provided");
                    return null;
                }
                var resolved = FrdResolver.Resolve(cond.Fix, fixes);
                if (resolved is null)
                {
                    warnings.Add($"{ac.AircraftId}: Could not " + $"resolve fix '{cond.Fix}'");
                    return null;
                }
                lat = resolved.Latitude;
                lon = resolved.Longitude;
                alt = cond.Altitude ?? fieldElevation;
                speed = cond.Altitude is null && cond.Speed is null ? 0 : cond.Speed ?? -1;
                heading = ResolveHeading(cond, lat, lon, fixes, warnings, ac.AircraftId);
                break;

            case "OnRunway":
                return LoadOnRunway(ac, fixes, runways, groundData, warnings, primaryApproach, rng);

            case "OnFinal":
                return LoadOnFinal(ac, fixes, runways, groundData, warnings, primaryApproach, rng);

            case "Parking":
                return LoadAtParking(ac, fixes, groundData, primaryAirportId, warnings, primaryApproach, rng);

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

        PopulateNavigationRoute(state, cond.NavigationPath, fixes, warnings);

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
        IFixLookup fixes,
        IRunwayLookup? runways,
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

        if (runways is null)
        {
            warnings.Add($"{ac.AircraftId}: OnRunway requires runway lookup");
            return BuildDeferredAircraft(ac, primaryApproach, "OnRunway (no runway data)");
        }

        var rwy = runways.GetRunway(airportId, runwayId);
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
        IFixLookup fixes,
        IRunwayLookup? runways,
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

        if (runways is null)
        {
            warnings.Add($"{ac.AircraftId}: OnFinal requires runway lookup");
            return BuildDeferredAircraft(ac, primaryApproach, "OnFinal (no runway data)");
        }

        var rwy = runways.GetRunway(airportId, runwayId);
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
        IFixLookup fixes,
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

        var elevation = fixes.GetAirportElevation(airportId) ?? 0;
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

    private static double ResolveHeading(StartingConditions cond, double lat, double lon, IFixLookup fixes, List<string> warnings, string callsign)
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

        var targetPos = fixes.GetFixPosition(fixName);
        if (targetPos is null)
        {
            warnings.Add($"{callsign}: Could not resolve nav " + $"waypoint '{firstWaypoint}', heading 0");
            return 0;
        }

        return GeoMath.BearingTo(lat, lon, targetPos.Value.Lat, targetPos.Value.Lon);
    }

    private static void PopulateNavigationRoute(AircraftState state, string? navigationPath, IFixLookup fixes, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath))
        {
            return;
        }

        var tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var resolved = new List<ResolvedFix>();

        foreach (var token in tokens)
        {
            var fixName = token.Split('.')[0];
            while (fixName.Length > 2 && char.IsDigit(fixName[^1]))
            {
                fixName = fixName[..^1];
            }

            var pos = fixes.GetFixPosition(fixName);
            if (pos is null)
            {
                warnings.Add($"{state.Callsign}: Could not resolve nav fix '{token}', skipping");
                continue;
            }

            resolved.Add(new ResolvedFix(fixName, pos.Value.Lat, pos.Value.Lon));
        }

        RouteChainer.AppendRouteRemainder(resolved, state.Route, fixes);

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
