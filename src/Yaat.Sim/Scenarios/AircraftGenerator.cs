using Yaat.Sim.Data;

namespace Yaat.Sim.Scenarios;

public static class AircraftGenerator
{
    private static readonly Dictionary<(WeightClass, EngineKind), string[]> TypeTable = new()
    {
        [(WeightClass.Small, EngineKind.Piston)] = ["C172", "C182", "PA28", "SR22"],
        [(WeightClass.Small, EngineKind.Turboprop)] = ["C208", "PC12", "BE20"],
        [(WeightClass.Large, EngineKind.Turboprop)] = ["DH8D", "AT76", "AT72"],
        [(WeightClass.Large, EngineKind.Jet)] = ["B738", "A320", "E170", "E175", "CRJ9"],
        [(WeightClass.Heavy, EngineKind.Jet)] = ["B77L", "B772", "A332", "B789", "B744", "A359"],
    };

    private static readonly string[] Airlines =
        ["UAL", "AAL", "DAL", "SWA", "JBU", "ASA", "NKS", "SKW", "ENY", "RPA"];

    public static string[]? GetTypesForCombo(WeightClass weight, EngineKind engine)
        => TypeTable.GetValueOrDefault((weight, engine));

    public static IReadOnlyList<string> GetAirlines() => Airlines;

    public static (AircraftState? State, string? Error) Generate(
        SpawnRequest request,
        string? primaryAirportId,
        IFixLookup fixes,
        IRunwayLookup runways,
        IReadOnlyCollection<AircraftState> existingAircraft)
    {
        var aircraftType = ResolveType(request);
        if (aircraftType is null)
        {
            return (null, $"No aircraft types defined for {request.Weight}+{request.Engine}");
        }

        var callsign = GenerateCallsign(request, existingAircraft);
        var category = AircraftCategorization.Categorize(aircraftType);
        var beaconCode = SimulationWorld.GenerateBeaconCode();
        var transponderMode = request.Rules == FlightRulesKind.Vfr ? "A" : "C";
        var flightRules = request.Rules == FlightRulesKind.Ifr ? "IFR" : "VFR";

        switch (request.PositionType)
        {
            case SpawnPositionType.Bearing:
                return GenerateBearing(
                    request, primaryAirportId, fixes, callsign,
                    aircraftType, category, beaconCode, transponderMode, flightRules);

            case SpawnPositionType.Runway:
                return GenerateOnRunway(
                    request, primaryAirportId, runways, callsign,
                    aircraftType, beaconCode, transponderMode, flightRules);

            case SpawnPositionType.OnFinal:
                return GenerateOnFinal(
                    request, primaryAirportId, runways, callsign,
                    aircraftType, category, beaconCode, transponderMode, flightRules);

            case SpawnPositionType.AtFix:
                return GenerateAtFix(
                    request, primaryAirportId, fixes, callsign,
                    aircraftType, category, beaconCode, transponderMode, flightRules);

            default:
                return (null, $"Unknown position type: {request.PositionType}");
        }
    }

    private static (AircraftState? State, string? Error) GenerateBearing(
        SpawnRequest request, string? primaryAirportId, IFixLookup fixes,
        string callsign, string aircraftType, AircraftCategory category,
        uint beaconCode, string transponderMode, string flightRules)
    {
        if (string.IsNullOrEmpty(primaryAirportId))
        {
            return (null, "Bearing position requires a primary airport in the scenario");
        }

        var airportPos = fixes.GetFixPosition(primaryAirportId);
        if (airportPos is null)
        {
            return (null, $"Could not find primary airport '{primaryAirportId}' in navdata");
        }

        var (lat, lon) = ComputePosition(
            airportPos.Value.Lat, airportPos.Value.Lon,
            request.Bearing, request.DistanceNm);

        // Heading toward the airport
        var heading = ComputeBearing(lat, lon, airportPos.Value.Lat, airportPos.Value.Lon);
        var speed = CategoryPerformance.DefaultSpeed(category, request.Altitude);

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Latitude = lat,
            Longitude = lon,
            Heading = heading,
            Altitude = request.Altitude,
            GroundSpeed = speed,
            BeaconCode = beaconCode,
            TransponderMode = transponderMode,
            FlightRules = flightRules,
        };

        return (state, null);
    }

    private static (AircraftState? State, string? Error) GenerateAtFix(
        SpawnRequest request, string? primaryAirportId, IFixLookup fixes,
        string callsign, string aircraftType, AircraftCategory category,
        uint beaconCode, string transponderMode, string flightRules)
    {
        var resolved = FrdResolver.Resolve(request.FixId, fixes);
        if (resolved is null)
        {
            return (null, $"Could not resolve fix or FRD '{request.FixId}'");
        }

        // If primary airport is known, head toward it; otherwise head north
        double heading = 0;
        if (!string.IsNullOrEmpty(primaryAirportId))
        {
            var airportPos = fixes.GetFixPosition(primaryAirportId);
            if (airportPos is not null)
            {
                heading = ComputeBearing(
                    resolved.Latitude, resolved.Longitude,
                    airportPos.Value.Lat, airportPos.Value.Lon);
            }
        }

        var speed = CategoryPerformance.DefaultSpeed(category, request.Altitude);

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Latitude = resolved.Latitude,
            Longitude = resolved.Longitude,
            Heading = heading,
            Altitude = request.Altitude,
            GroundSpeed = speed,
            BeaconCode = beaconCode,
            TransponderMode = transponderMode,
            FlightRules = flightRules,
        };

        return (state, null);
    }

    private static (AircraftState? State, string? Error) GenerateOnRunway(
        SpawnRequest request, string? primaryAirportId, IRunwayLookup runways,
        string callsign, string aircraftType,
        uint beaconCode, string transponderMode, string flightRules)
    {
        var airportId = primaryAirportId ?? "";
        if (string.IsNullOrEmpty(airportId))
        {
            return (null, "Runway position requires a primary airport in the scenario");
        }

        var rwy = runways.GetRunway(airportId, request.RunwayId);
        if (rwy is null)
        {
            return (null, $"Could not find runway {request.RunwayId} at {airportId}");
        }

        var init = AircraftInitializer.InitializeOnRunway(rwy);

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Latitude = init.Latitude,
            Longitude = init.Longitude,
            Heading = init.Heading,
            Altitude = init.Altitude,
            GroundSpeed = init.Speed,
            IsOnGround = init.IsOnGround,
            BeaconCode = beaconCode,
            TransponderMode = transponderMode,
            FlightRules = flightRules,
            Phases = init.Phases,
        };

        return (state, null);
    }

    private static (AircraftState? State, string? Error) GenerateOnFinal(
        SpawnRequest request, string? primaryAirportId, IRunwayLookup runways,
        string callsign, string aircraftType, AircraftCategory category,
        uint beaconCode, string transponderMode, string flightRules)
    {
        var airportId = primaryAirportId ?? "";
        if (string.IsNullOrEmpty(airportId))
        {
            return (null, "Final position requires a primary airport in the scenario");
        }

        var rwy = runways.GetRunway(airportId, request.RunwayId);
        if (rwy is null)
        {
            return (null, $"Could not find runway {request.RunwayId} at {airportId}");
        }

        // Convert distance to altitude for AircraftInitializer (3° glide slope ~ 300ft/nm)
        double altFromDist = rwy.ElevationFt + (request.FinalDistanceNm * 300.0);

        var init = AircraftInitializer.InitializeOnFinal(rwy, category, altFromDist);

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Latitude = init.Latitude,
            Longitude = init.Longitude,
            Heading = init.Heading,
            Altitude = init.Altitude,
            GroundSpeed = init.Speed,
            IsOnGround = init.IsOnGround,
            BeaconCode = beaconCode,
            TransponderMode = transponderMode,
            FlightRules = flightRules,
            Phases = init.Phases,
        };

        return (state, null);
    }

    private static string? ResolveType(SpawnRequest request)
    {
        if (request.ExplicitType is not null)
        {
            return request.ExplicitType;
        }

        if (!TypeTable.TryGetValue((request.Weight, request.Engine), out var types))
        {
            return null;
        }

        return types[Random.Shared.Next(types.Length)];
    }

    private static string GenerateCallsign(
        SpawnRequest request,
        IReadOnlyCollection<AircraftState> existingAircraft)
    {
        var existing = new HashSet<string>(
            existingAircraft.Select(a => a.Callsign),
            StringComparer.OrdinalIgnoreCase);

        if (request.Rules == FlightRulesKind.Vfr)
        {
            return GenerateNNumber(existing);
        }

        var airline = request.ExplicitAirline ?? Airlines[Random.Shared.Next(Airlines.Length)];
        return GenerateAirlineCallsign(airline, existing);
    }

    private static string GenerateAirlineCallsign(string airline, HashSet<string> existing)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            int digits = Random.Shared.Next(3, 5); // 3 or 4 digits
            var number = Random.Shared.Next(
                (int)Math.Pow(10, digits - 1),
                (int)Math.Pow(10, digits));
            var callsign = $"{airline}{number}";
            if (!existing.Contains(callsign))
            {
                return callsign;
            }
        }

        return $"{airline}{Random.Shared.Next(10000, 99999)}";
    }

    private static string GenerateNNumber(HashSet<string> existing)
    {
        const string chars = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var suffix = new char[4];
            // First char after N must be a digit (1-9)
            suffix[0] = (char)('1' + Random.Shared.Next(9));
            for (int i = 1; i < 4; i++)
            {
                suffix[i] = chars[Random.Shared.Next(chars.Length)];
            }
            var callsign = $"N{new string(suffix)}";
            if (!existing.Contains(callsign))
            {
                return callsign;
            }
        }

        return $"N{Random.Shared.Next(10000, 99999)}";
    }

    private static (double Lat, double Lon) ComputePosition(
        double originLat, double originLon,
        double bearingDeg, double distanceNm)
    {
        double bearingRad = bearingDeg * Math.PI / 180.0;
        double latRad = originLat * Math.PI / 180.0;
        const double nmPerDegLat = 60.0;

        double lat = originLat + (distanceNm * Math.Cos(bearingRad) / nmPerDegLat);
        double lon = originLon + (distanceNm * Math.Sin(bearingRad) / (nmPerDegLat * Math.Cos(latRad)));

        return (lat, lon);
    }

    private static double ComputeBearing(
        double lat1, double lon1,
        double lat2, double lon2)
    {
        double lat1R = lat1 * Math.PI / 180.0;
        double lat2R = lat2 * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;

        double y = Math.Sin(dLon) * Math.Cos(lat2R);
        double x = Math.Cos(lat1R) * Math.Sin(lat2R)
            - Math.Sin(lat1R) * Math.Cos(lat2R) * Math.Cos(dLon);

        double bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return ((bearing % 360.0) + 360.0) % 360.0;
    }
}
