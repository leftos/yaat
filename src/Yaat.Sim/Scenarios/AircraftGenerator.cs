using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Scenarios;

public static class AircraftGenerator
{
    private static readonly ILogger Log = SimLog.CreateLogger("AircraftGenerator");

    /// <summary>
    /// Curated pool of common-today ICAO type codes per (weight, engine) bucket. Every
    /// entry must resolve through both <see cref="AircraftProfileDatabase"/> and
    /// <see cref="AircraftCategorization"/>; <see cref="AssertEveryTypeResolves"/>
    /// enforces this at startup.
    ///
    /// The weight tiers track RECAT CWT category: <c>Small</c> = CWT I (lower small),
    /// <c>SmallPlus</c> = CWT H (upper small — business jets), <c>Large</c> = mainline
    /// narrow-bodies (CWT F) plus regional jets (CWT G), <c>Heavy</c> = widebodies. The lone
    /// carve-out is the SmallPlus turboprop pool, which keeps the CWT G commuter turboprops
    /// (AT72/DH8C/SF34) because their far lower landing energy and superior low-speed
    /// deceleration (light weight, propeller/beta drag) keep them short-field appropriate —
    /// even SF34, whose approach speed alone matches the regional jets.
    /// </summary>
    private static readonly Dictionary<(WeightClass, EngineKind), string[]> TypeTable = new()
    {
        [(WeightClass.Small, EngineKind.Piston)] = ["C172", "C182", "P28A", "SR22", "BE36", "C150", "C152"],
        [(WeightClass.Small, EngineKind.Turboprop)] = ["C208", "PC12", "BE20", "P180"],
        [(WeightClass.Small, EngineKind.Jet)] = ["C25A", "C525", "C500", "C501", "C550"],
        [(WeightClass.SmallPlus, EngineKind.Turboprop)] = ["AT72", "DH8C", "SF34", "B190", "B350"],
        [(WeightClass.SmallPlus, EngineKind.Jet)] = ["C560", "C56X", "C680", "LJ60", "LJ45"],
        [(WeightClass.Large, EngineKind.Piston)] = ["AC68", "BE60", "BE58", "C421"],
        [(WeightClass.Large, EngineKind.Jet)] = ["B737", "B738", "B739", "A319", "A320", "A321", "CRJ7", "CRJ9", "E170", "E75L", "E145", "E135"],
        [(WeightClass.Heavy, EngineKind.Jet)] = ["A332", "A333", "B763", "B764", "B772", "B788", "B744"],
    };

    private static readonly string[] Airlines = ["UAL", "AAL", "DAL", "SWA", "JBU", "ASA", "NKS", "SKW", "ENY", "RPA"];

    public static string[]? GetTypesForCombo(WeightClass weight, EngineKind engine) => TypeTable.GetValueOrDefault((weight, engine));

    public static IReadOnlyList<string> GetAirlines() => Airlines;

    /// <summary>
    /// Verify that every type listed in <see cref="TypeTable"/> resolves through both
    /// <see cref="AircraftProfileDatabase"/> and <see cref="AircraftCategorization"/>,
    /// and that every airline in <see cref="Airlines"/> has fleet data in
    /// <see cref="AirlineFleets"/>. Call once at startup AFTER the data DBs have been
    /// initialized — fails loudly rather than silently degrading to category-default
    /// performance or pairing aircraft with airlines that don't operate them.
    /// </summary>
    public static void AssertEveryTypeResolves()
    {
        var problems = new List<string>();
        foreach (var ((weight, engine), types) in TypeTable)
        {
            foreach (var type in types)
            {
                if (AircraftProfileDatabase.Get(type) is null)
                {
                    problems.Add($"{weight}+{engine}: type '{type}' has no AircraftProfileDatabase entry (and no sibling fallback)");
                }
                var cat = AircraftCategorization.Categorize(type);
                var expectedCat = engine switch
                {
                    EngineKind.Piston => AircraftCategory.Piston,
                    EngineKind.Turboprop => AircraftCategory.Turboprop,
                    EngineKind.Jet => AircraftCategory.Jet,
                    _ => AircraftCategory.Jet,
                };
                if (cat != expectedCat)
                {
                    problems.Add($"{weight}+{engine}: type '{type}' categorized as {cat}, expected {expectedCat}");
                }

                // Jet pools feed wake-separation spacing, so every jet type must resolve to a CWT
                // category — otherwise it silently degrades to the coarse weight-class default. This
                // catches non-resolvable designators (e.g. "E175" instead of the real "E75L") that the
                // profile and category checks above miss.
                if (engine == EngineKind.Jet && WakeTurbulenceData.GetCwt(type) is null)
                {
                    problems.Add($"{weight}+{engine}: jet type '{type}' has no CWT category — wake separation would fall back to the coarse default");
                }
            }
        }

        // Every curated airline must be in AirlineFleets. Without fleet data we have no
        // way to constrain type pairing, so the airline would silently land on the
        // bucket pool and we'd be back to "SWA flying an A320".
        if (AirlineFleets.AirlineCount > 0)
        {
            foreach (var airline in Airlines)
            {
                if (!AirlineFleets.TryGetAirline(airline, out _))
                {
                    problems.Add(
                        $"airline '{airline}' is in AircraftGenerator.Airlines but absent from AirlineFleets — type pairing cannot be constrained"
                    );
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("AircraftGenerator data validation failed:\n  - " + string.Join("\n  - ", problems));
        }
        Log.LogInformation(
            "AircraftGenerator type table validated: {Count} types across {Buckets} buckets, {Airlines} airlines",
            TypeTable.Sum(kv => kv.Value.Length),
            TypeTable.Count,
            Airlines.Length
        );
    }

    /// <summary>
    /// Builds the FP record for an ADD-spawned aircraft. VFR ADD = cold call: blank fields,
    /// HasFlightPlan=false. The controller files via DA / VP later. IFR ADD = filed plan with
    /// the auto-generated type so STARS/strips have a non-blank readout.
    /// </summary>
    private static AircraftFlightPlan BuildAddFlightPlan(string flightRules, string aircraftType, string departure, string destination, string route)
    {
        if (flightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase))
        {
            return new AircraftFlightPlan
            {
                HasFlightPlan = false,
                FlightRules = "VFR",
                AircraftType = "",
                EquipmentSuffix = "",
            };
        }
        return new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = flightRules,
            AircraftType = aircraftType,
            Departure = departure,
            Destination = destination,
            Route = route,
        };
    }

    public static (AircraftState? State, string? Error) Generate(
        SpawnRequest request,
        string? primaryAirportId,
        IReadOnlyCollection<AircraftState> existingAircraft,
        AirportGroundLayout? groundLayout,
        Random rng
    )
    {
        var result = GenerateCore(request, primaryAirportId, existingAircraft, groundLayout, rng);
        if (result.State is null)
        {
            Log.LogWarning(
                "[ArrivalGen] spawn FAILED ({Weight}/{Engine} {Position} rwy={Runway}): {Error}",
                request.Weight,
                request.Engine,
                request.PositionType,
                request.RunwayId ?? "-",
                result.Error ?? "(unknown)"
            );
        }
        else
        {
            var s = result.State;
            var cat = AircraftCategorization.Categorize(s.AircraftType);
            Log.LogInformation(
                "[ArrivalGen] spawned {Callsign} type={Type} cat={Cat} (req={Weight}/{Engine}) on {Position} rwy={Runway} alt={Alt:F0}ft ias={Ias:F0}kts",
                s.Callsign,
                s.AircraftType,
                cat,
                request.Weight,
                request.Engine,
                request.PositionType,
                request.RunwayId ?? "-",
                s.Altitude,
                s.IndicatedAirspeed
            );
        }
        return result;
    }

    private static (AircraftState? State, string? Error) GenerateCore(
        SpawnRequest request,
        string? primaryAirportId,
        IReadOnlyCollection<AircraftState> existingAircraft,
        AirportGroundLayout? groundLayout,
        Random rng
    )
    {
        var r = rng;

        // For IFR aircraft we pick the airline first so that the type can be
        // constrained to that airline's actual fleet (e.g. SWA never gets paired
        // with an A320). VFR uses N-numbers and has no airline.
        string? airline = null;
        if (request.Rules == FlightRulesKind.Ifr)
        {
            airline =
                request.ExplicitAirline
                ?? PickCompatibleAirportAirline(request.PreferredAirlineAirportId, request.Weight, request.Engine, r)
                ?? PickCompatibleAirline(request.Weight, request.Engine, r);
        }

        var aircraftType = ResolveType(request, airline, r);
        if (aircraftType is null)
        {
            return (null, $"No aircraft types defined for {request.Weight}+{request.Engine}");
        }

        var callsign = GenerateCallsign(request, airline, existingAircraft, r);
        var category = AircraftCategorization.Categorize(aircraftType);
        // Airborne and on-runway spawns squawk Mode C (real-world airborne traffic, including
        // VFR/1200, is altitude-reporting; runway spawns are about to take off with the
        // transponder already on). Parking spawns sit on Standby until the pilot powers up
        // the transponder for taxi.
        var transponderMode = request.PositionType == SpawnPositionType.Parking ? "Standby" : "C";
        var flightRules = request.Rules == FlightRulesKind.Ifr ? "IFR" : "VFR";

        // VFR ADD spawns are cold calls: no AssignedCode, squawking 1200 (FAA
        // VFR conspicuity code). Controller files later via DA / VP, which
        // assigns a discrete beacon. IFR ADD spawns get a discrete code at
        // spawn so they're trackable immediately.
        uint assignedCode;
        uint activeCode;
        if (request.Rules == FlightRulesKind.Vfr)
        {
            assignedCode = 0;
            activeCode = 1200;
        }
        else
        {
            var code = SimulationWorld.GenerateBeaconCode(r);
            assignedCode = code;
            activeCode = code;
        }

        switch (request.PositionType)
        {
            case SpawnPositionType.Bearing:
                return GenerateBearing(
                    request,
                    primaryAirportId,
                    callsign,
                    aircraftType,
                    category,
                    assignedCode,
                    activeCode,
                    transponderMode,
                    flightRules
                );

            case SpawnPositionType.Runway:
                return GenerateOnRunway(request, primaryAirportId, callsign, aircraftType, assignedCode, activeCode, transponderMode, flightRules);

            case SpawnPositionType.OnFinal:
                return GenerateOnFinal(
                    request,
                    primaryAirportId,
                    callsign,
                    aircraftType,
                    category,
                    assignedCode,
                    activeCode,
                    transponderMode,
                    flightRules
                );

            case SpawnPositionType.AtFix:
                return GenerateAtFix(
                    request,
                    primaryAirportId,
                    callsign,
                    aircraftType,
                    category,
                    assignedCode,
                    activeCode,
                    transponderMode,
                    flightRules
                );

            case SpawnPositionType.Parking:
                return GenerateAtParking(
                    request,
                    primaryAirportId,
                    groundLayout,
                    callsign,
                    aircraftType,
                    assignedCode,
                    activeCode,
                    transponderMode,
                    flightRules
                );

            case SpawnPositionType.OnStar:
                return GenerateOnStar(
                    request,
                    primaryAirportId,
                    callsign,
                    aircraftType,
                    category,
                    assignedCode,
                    activeCode,
                    transponderMode,
                    flightRules
                );

            default:
                return (null, $"Unknown position type: {request.PositionType}");
        }
    }

    private static (AircraftState? State, string? Error) GenerateBearing(
        SpawnRequest request,
        string? primaryAirportId,
        string callsign,
        string aircraftType,
        AircraftCategory category,
        uint assignedCode,
        uint activeCode,
        string transponderMode,
        string flightRules
    )
    {
        if (string.IsNullOrEmpty(primaryAirportId))
        {
            return (null, "Bearing position requires a primary airport in the scenario");
        }

        var airportPos = NavigationDatabase.Instance.GetFixPosition(primaryAirportId);
        if (airportPos is null)
        {
            return (null, $"Could not find primary airport '{primaryAirportId}' in navdata");
        }

        var (lat, lon) = ComputePosition(airportPos.Value.Lat, airportPos.Value.Lon, request.Bearing, request.DistanceNm);

        // Heading toward the airport
        TrueHeading trueHeading = new(ComputeBearing(lat, lon, airportPos.Value.Lat, airportPos.Value.Lon));
        var speed = AircraftPerformance.DefaultSpeed(aircraftType, category, request.Altitude, null);

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            AirportId = primaryAirportId ?? "",
            Position = new LatLon(lat, lon),
            TrueHeading = trueHeading,
            TrueTrack = trueHeading,
            Altitude = request.Altitude,
            IndicatedAirspeed = speed,
            Transponder = new AircraftTransponder
            {
                Mode = transponderMode,
                AssignedCode = assignedCode,
                Code = activeCode,
            },
            FlightPlan = BuildAddFlightPlan(flightRules, aircraftType, departure: "", destination: "", route: ""),
        };

        return (state, null);
    }

    private static (AircraftState? State, string? Error) GenerateAtFix(
        SpawnRequest request,
        string? primaryAirportId,
        string callsign,
        string aircraftType,
        AircraftCategory category,
        uint assignedCode,
        uint activeCode,
        string transponderMode,
        string flightRules
    )
    {
        var navDb = NavigationDatabase.Instance;
        var resolved = FrdResolver.Resolve(request.FixId, navDb);
        if (resolved is null)
        {
            return (null, $"Could not resolve fix or FRD '{request.FixId}'");
        }

        // If primary airport is known, head toward it; otherwise head north
        TrueHeading trueHeading = new(0);
        if (!string.IsNullOrEmpty(primaryAirportId))
        {
            var airportPos = navDb.GetFixPosition(primaryAirportId);
            if (airportPos is not null)
            {
                trueHeading = new TrueHeading(ComputeBearing(resolved.Value.Lat, resolved.Value.Lon, airportPos.Value.Lat, airportPos.Value.Lon));
            }
        }

        var speed = AircraftPerformance.DefaultSpeed(aircraftType, category, request.Altitude, null);

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            AirportId = primaryAirportId ?? "",
            Position = resolved.Value,
            TrueHeading = trueHeading,
            TrueTrack = trueHeading,
            Altitude = request.Altitude,
            IndicatedAirspeed = speed,
            Transponder = new AircraftTransponder
            {
                Mode = transponderMode,
                AssignedCode = assignedCode,
                Code = activeCode,
            },
            FlightPlan = BuildAddFlightPlan(flightRules, aircraftType, departure: "", destination: "", route: ""),
        };

        return (state, null);
    }

    private static (AircraftState? State, string? Error) GenerateOnStar(
        SpawnRequest request,
        string? primaryAirportId,
        string callsign,
        string aircraftType,
        AircraftCategory category,
        uint assignedCode,
        uint activeCode,
        string transponderMode,
        string flightRules
    )
    {
        var navDb = NavigationDatabase.Instance;

        var airport = !string.IsNullOrEmpty(request.DestinationAirportId) ? request.DestinationAirportId : primaryAirportId;
        if (string.IsNullOrEmpty(airport))
        {
            return (null, "Arrival spawn needs a destination airport (none specified and no primary scenario airport)");
        }

        var starId = navDb.ResolveCommandStarId(airport, request.StarId);
        var star = navDb.GetStar(airport, starId);
        if (star is null)
        {
            return (null, $"STAR '{request.StarId}' not found for {airport}");
        }

        var entryPos = FrdResolver.Resolve(request.StarEntryFix, navDb);
        if (entryPos is null)
        {
            return (null, $"Could not resolve arrival entry waypoint '{request.StarEntryFix}'");
        }

        // Synthetic navigation path in the "<entry> <STAR>[.<runway>]" form ArrivalRouteResolver expects
        // (the dotted runway drives runway-transition resolution). The filed Route, by contrast, carries
        // the BARE versioned STAR token so a later DVIA's TryActivateFiledStar (which calls ResolveStarId
        // per space-separated token, without splitting on '.') can recognize it.
        var navPath = request.StarRunway is not null ? $"{request.StarEntryFix} {starId}.{request.StarRunway}" : $"{request.StarEntryFix} {starId}";
        var filedRoute = $"{request.StarEntryFix} {starId}";

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            AirportId = airport,
            Position = entryPos.Value,
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = false,
            Transponder = new AircraftTransponder
            {
                Mode = transponderMode,
                AssignedCode = assignedCode,
                Code = activeCode,
            },
            FlightPlan = BuildAddFlightPlan(flightRules, aircraftType, departure: "", destination: airport, route: filedRoute),
        };

        // The runway-transition designator drives both the lateral route build and the descend-via profile.
        state.Procedure.DestinationRunway = request.StarRunway;

        var warnings = new List<string>();
        ArrivalRouteResolver.PopulateNavigationRoute(state, navPath, warnings);

        // Resolve the STAR's published altitude/speed crossings once (common legs + runway transition).
        var (altByFix, speedByFix) = ResolveStarRestrictions(star, request.StarRunway, state.Procedure.DestinationRunway);

        // Current altitude: explicit if supplied, else a computed establishment altitude from the STAR.
        state.Altitude = request.StarAltitude ?? ComputeDefaultEstablishmentAltitude(state, altByFix, airport);

        // Speed: explicit override, else the STAR's published mandatory speed at the entry/join fix
        // (AIM 5-4-1.a.1 — applies regardless of descend-via), else the per-category default.
        double? joinFixSpeed = speedByFix.TryGetValue(request.StarEntryFix, out var sk) ? sk : null;
        state.IndicatedAirspeed =
            request.StarSpeedKts ?? joinFixSpeed ?? AircraftPerformance.DefaultSpeed(aircraftType, category, state.Altitude, null);

        // Descend-via overlays CIFP crossing restrictions and enables StarViaMode (auto-DVIA at spawn).
        // Level spawns keep a constraint-free lateral route so the aircraft holds altitude until DVIA;
        // the STAR stays in the filed route so a later DVIA reactivates the descent profile.
        if (request.DescendVia)
        {
            ArrivalRouteResolver.ApplyAltitudeProfile(state, navPath, warnings);
        }

        var heading = ComputeOnStarHeading(state, entryPos.Value, airport, navDb);
        state.TrueHeading = heading;
        state.TrueTrack = heading;

        foreach (var warning in warnings)
        {
            Log.LogWarning("[ArrivalGen] {Callsign} OnStar {Star}: {Warning}", callsign, starId, warning);
        }

        return (state, null);
    }

    /// <summary>
    /// Resolves a STAR's published altitude and speed crossing restrictions (common legs + runway
    /// transition), keyed by fix name, for deriving spawn altitude/speed.
    /// </summary>
    private static (Dictionary<string, int> AltByFix, Dictionary<string, int> SpeedByFix) ResolveStarRestrictions(
        CifpStarProcedure star,
        string? runway,
        string? destinationRunway
    )
    {
        var orderedLegs = new List<CifpLeg>(star.CommonLegs);
        var rwLegs = ArrivalRouteResolver.FindRunwayTransition(star, runway, destinationRunway);
        if (rwLegs is not null)
        {
            orderedLegs.AddRange(rwLegs);
        }

        var altByFix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var speedByFix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in DepartureClearanceHandler.ResolveLegsToTargets(orderedLegs))
        {
            if (target.AltitudeRestriction is { } ar && !altByFix.ContainsKey(target.Name))
            {
                altByFix[target.Name] = ar.Altitude1Ft;
            }

            if (target.SpeedRestriction is { } sr && !speedByFix.ContainsKey(target.Name))
            {
                speedByFix[target.Name] = sr.SpeedKts;
            }
        }

        return (altByFix, speedByFix);
    }

    /// <summary>
    /// Estimates a realistic "established on the STAR" altitude when none was given. Uses the
    /// published crossing at the entry fix (authoritative — returned as-is); failing that, projects
    /// up from the first downstream crossing along a ~3:1 gradient; failing that (unconstrained STAR),
    /// estimates from the flying distance to the destination airport. The derived estimates are
    /// clamped to a sane band; all results snap to a 1000-ft step.
    /// </summary>
    private static double ComputeDefaultEstablishmentAltitude(AircraftState state, IReadOnlyDictionary<string, int> altByFix, string airport)
    {
        const double GradientFtPerNm = 300.0; // ~3:1; AIM 5-4-2.b descent window is 250–350 ft/nm
        const double FloorFt = 4000.0;
        const double CapFt = 24000.0; // typical top-of-descent ceiling (FL240) for the derived estimate

        var route = state.Targets.NavigationRoute;

        // 1) Established exactly at the entry fix's published crossing — authoritative, not capped.
        if (route.Count > 0 && altByFix.TryGetValue(route[0].Name, out var entryCrossing))
        {
            return Math.Round(entryCrossing / 1000.0) * 1000.0;
        }

        // 2) Project up from the first downstream crossing along a 3:1 gradient.
        double cumulativeNm = 0;
        double? derived = null;
        for (int i = 1; i < route.Count; i++)
        {
            cumulativeNm += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
            if (altByFix.TryGetValue(route[i].Name, out var crossing))
            {
                derived = crossing + (cumulativeNm * GradientFtPerNm);
                break;
            }
        }

        double result;
        if (derived is { } d)
        {
            result = d;
        }
        else
        {
            // 3) Unconstrained STAR — estimate from the flying distance to the destination airport.
            var airportPos = NavigationDatabase.Instance.GetFixPosition(airport);
            double distNm =
                airportPos is not null && route.Count > 0
                    ? GeoMath.DistanceNm(route[0].Position, new LatLon(airportPos.Value.Lat, airportPos.Value.Lon))
                    : 30.0;
            result = distNm * GradientFtPerNm;
        }

        result = Math.Clamp(result, FloorFt, CapFt);
        return Math.Round(result / 1000.0) * 1000.0;
    }

    /// <summary>True heading toward the first downstream route fix (the entry fix coincides with the spawn point).</summary>
    private static TrueHeading ComputeOnStarHeading(AircraftState state, LatLon spawnPos, string airport, NavigationDatabase navDb)
    {
        foreach (var fix in state.Targets.NavigationRoute)
        {
            if (GeoMath.DistanceNm(spawnPos, fix.Position) > 0.1)
            {
                return new TrueHeading(ComputeBearing(spawnPos.Lat, spawnPos.Lon, fix.Position.Lat, fix.Position.Lon));
            }
        }

        var airportPos = navDb.GetFixPosition(airport);
        if (airportPos is not null)
        {
            return new TrueHeading(ComputeBearing(spawnPos.Lat, spawnPos.Lon, airportPos.Value.Lat, airportPos.Value.Lon));
        }

        return new TrueHeading(0);
    }

    private static (AircraftState? State, string? Error) GenerateOnRunway(
        SpawnRequest request,
        string? primaryAirportId,
        string callsign,
        string aircraftType,
        uint assignedCode,
        uint activeCode,
        string transponderMode,
        string flightRules
    )
    {
        var airportId = primaryAirportId ?? "";
        if (string.IsNullOrEmpty(airportId))
        {
            return (null, "Runway position requires a primary airport in the scenario");
        }

        var rwy = NavigationDatabase.Instance.GetRunway(airportId, request.RunwayId);
        if (rwy is null)
        {
            return (null, $"Could not find runway {RunwayIdentifier.ToDisplayDesignator(request.RunwayId)} at {airportId}");
        }

        var category = AircraftCategorization.Categorize(aircraftType);
        var init = AircraftInitializer.InitializeOnRunway(rwy, category);

        // Departure airport (canonical ICAO) for IFR so a filed SID route resolves against it.
        var departure = "";
        if (request.Rules == FlightRulesKind.Ifr)
        {
            departure = NavigationDatabase.Instance.TryResolveAirport(airportId, out var canonical) ? canonical : airportId;
        }

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            AirportId = primaryAirportId ?? "",
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            TrueTrack = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = init.IsOnGround,
            Transponder = new AircraftTransponder
            {
                Mode = transponderMode,
                AssignedCode = assignedCode,
                Code = activeCode,
            },
            FlightPlan = BuildAddFlightPlan(flightRules, aircraftType, departure: departure, destination: "", route: request.Route),
            Phases = init.Phases,
        };

        return (state, null);
    }

    private static (AircraftState? State, string? Error) GenerateOnFinal(
        SpawnRequest request,
        string? primaryAirportId,
        string callsign,
        string aircraftType,
        AircraftCategory category,
        uint assignedCode,
        uint activeCode,
        string transponderMode,
        string flightRules
    )
    {
        var airportId = primaryAirportId ?? "";
        if (string.IsNullOrEmpty(airportId))
        {
            return (null, "Final position requires a primary airport in the scenario");
        }

        var rwy = NavigationDatabase.Instance.GetRunway(airportId, request.RunwayId);
        if (rwy is null)
        {
            return (null, $"Could not find runway {RunwayIdentifier.ToDisplayDesignator(request.RunwayId)} at {airportId}");
        }

        double gsAngle = Phases.GlideSlopeGeometry.AngleForCategory(category);
        double altFromDist = rwy.ElevationFt + (request.FinalDistanceNm * Phases.GlideSlopeGeometry.FeetPerNm(gsAngle));

        var init = AircraftInitializer.InitializeOnFinal(rwy, category, altFromDist, aircraftType: aircraftType);

        var destination = "";
        if (request.Rules == FlightRulesKind.Ifr)
        {
            destination = NavigationDatabase.Instance.TryResolveAirport(airportId, out var canonical) ? canonical : airportId;
        }

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            AirportId = primaryAirportId ?? "",
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            TrueTrack = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = init.IsOnGround,
            Transponder = new AircraftTransponder
            {
                Mode = transponderMode,
                AssignedCode = assignedCode,
                Code = activeCode,
            },
            FlightPlan = BuildAddFlightPlan(flightRules, aircraftType, departure: "", destination: destination, route: ""),
            Phases = init.Phases,
        };

        return (state, null);
    }

    private static (AircraftState? State, string? Error) GenerateAtParking(
        SpawnRequest request,
        string? primaryAirportId,
        AirportGroundLayout? groundLayout,
        string callsign,
        string aircraftType,
        uint assignedCode,
        uint activeCode,
        string transponderMode,
        string flightRules
    )
    {
        if (groundLayout is null)
        {
            return (null, "Parking position requires airport ground data");
        }

        // Search parking first, then helipads/spots
        var node = groundLayout.FindParkingByName(request.ParkingName) ?? groundLayout.FindSpotByName(request.ParkingName);
        if (node is null)
        {
            return (null, $"Parking/helipad '{request.ParkingName}' not found");
        }

        var elevation = NavigationDatabase.Instance.GetAirportElevation(primaryAirportId ?? "") ?? 0;
        var init = AircraftInitializer.InitializeAtParking(node, elevation);

        var state = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            AirportId = primaryAirportId ?? "",
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            TrueTrack = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = init.IsOnGround,
            Ground = new AircraftGroundOps { ParkingSpot = request.ParkingName, AutoDeleteExempt = true },
            Transponder = new AircraftTransponder
            {
                Mode = transponderMode,
                AssignedCode = assignedCode,
                Code = activeCode,
            },
            FlightPlan = BuildAddFlightPlan(flightRules, aircraftType, departure: "", destination: "", route: ""),
            Phases = init.Phases,
        };

        return (state, null);
    }

    /// <summary>
    /// Pick an aircraft type for the requested bucket, optionally constrained to types
    /// the given airline actually operates (per <see cref="AirlineFleets"/>). Walks a
    /// fallback chain (see <see cref="EnumerateBucketFallbackChain"/>) so empty buckets
    /// — e.g. <c>Heavy+Piston</c> — degrade to the nearest neighbour rather than failing
    /// the spawn. Same engine wins over same size: a request for a piston aircraft will
    /// always resolve to a piston type as long as any piston bucket has entries.
    ///
    /// If the airline operates no type in the chosen bucket, the airline filter is
    /// dropped on a second pass — the spawn falls back to an N-number callsign rather
    /// than dropping the aircraft entirely.
    /// </summary>
    private static string? ResolveType(SpawnRequest request, string? airline, Random rng)
    {
        if (request.ExplicitType is not null)
        {
            return request.ExplicitType;
        }

        var requested = (request.Weight, request.Engine);

        // Exact-bucket path preserves the original airline-overlap behaviour:
        // when the bucket exists we either pick an airline-compatible type, or
        // (if the airline has no overlap) log a warning and use the bucket pool.
        // We only descend into the fallback chain when the exact bucket is empty
        // — that way airline-vs-bucket mismatches don't silently swap engine type.
        if (TypeTable.TryGetValue(requested, out var exactBucket))
        {
            if (airline is null || !AirlineFleets.TryGetTypes(airline, out var fleetTypes))
            {
                return exactBucket[rng.Next(exactBucket.Length)];
            }

            var compatible = exactBucket.Where(t => fleetTypes.ContainsKey(t)).ToArray();
            if (compatible.Length > 0)
            {
                return compatible[rng.Next(compatible.Length)];
            }

            Log.LogWarning(
                "[ArrivalGen] {Airline} fleet has no overlap with TypeTable[{Weight}+{Engine}]={Types}; using bucket pool",
                airline,
                request.Weight,
                request.Engine,
                string.Join(",", exactBucket)
            );
            return exactBucket[rng.Next(exactBucket.Length)];
        }

        // Exact bucket is empty — walk the fallback chain. Engine wins over size,
        // so a Heavy+Piston request resolves to Large+Piston before any non-piston
        // option. The airline filter is dropped here because the bucket has already
        // diverged from the request; keeping it could pair e.g. SWA with a non-SWA
        // type just because SWA happens to have overlap in a different bucket.
        foreach (var bucket in EnumerateBucketFallbackChain(request.Weight, request.Engine))
        {
            if (!TypeTable.TryGetValue(bucket, out var bucketTypes))
            {
                continue;
            }
            LogBucketFallback(requested, bucket);
            return bucketTypes[rng.Next(bucketTypes.Length)];
        }

        return null;
    }

    private static void LogBucketFallback((WeightClass Weight, EngineKind Engine) requested, (WeightClass Weight, EngineKind Engine) resolved)
    {
        if (requested.Weight == resolved.Weight && requested.Engine == resolved.Engine)
        {
            return;
        }
        Log.LogInformation(
            "[ArrivalGen] bucket fallback: requested {ReqWeight}/{ReqEngine} resolved via {ResWeight}/{ResEngine}",
            requested.Weight,
            requested.Engine,
            resolved.Weight,
            resolved.Engine
        );
    }

    /// <summary>
    /// Yield bucket keys in priority order for fallback resolution:
    /// (1) exact <c>(weight, engine)</c>, (2) same engine with other weights nearest
    /// first, (3) same weight with other engines, (4) any remaining combinations.
    /// Engine takes priority over size so a request for piston traffic still resolves
    /// to a piston type when the exact size bucket is empty.
    /// </summary>
    internal static IEnumerable<(WeightClass Weight, EngineKind Engine)> EnumerateBucketFallbackChain(WeightClass weight, EngineKind engine)
    {
        yield return (weight, engine);

        foreach (var w in OrderByDistance(weight))
        {
            if (w != weight)
            {
                yield return (w, engine);
            }
        }

        foreach (var e in Enum.GetValues<EngineKind>())
        {
            if (e != engine)
            {
                yield return (weight, e);
            }
        }

        foreach (var w in Enum.GetValues<WeightClass>())
        {
            if (w == weight)
            {
                continue;
            }
            foreach (var e in Enum.GetValues<EngineKind>())
            {
                if (e == engine)
                {
                    continue;
                }
                yield return (w, e);
            }
        }
    }

    private static IEnumerable<WeightClass> OrderByDistance(WeightClass center)
    {
        return Enum.GetValues<WeightClass>().OrderBy(w => Math.Abs((int)w - (int)center));
    }

    private static string GenerateCallsign(SpawnRequest request, string? airline, IReadOnlyCollection<AircraftState> existingAircraft, Random rng)
    {
        var existing = new HashSet<string>(existingAircraft.Select(a => a.Callsign), StringComparer.OrdinalIgnoreCase);

        if (request.Rules == FlightRulesKind.Vfr || airline is null)
        {
            return GenerateNNumber(existing, rng);
        }
        return GenerateAirlineCallsign(airline, existing, rng);
    }

    /// <summary>
    /// Pick an airline that actually operates at least one aircraft type in
    /// <c>TypeTable[(weight, engine)]</c> per <see cref="AirlineFleets"/>. Returns null
    /// when no curated airline matches (e.g. a turboprop bucket where every airline in
    /// <see cref="Airlines"/> has divested turboprops) — caller should fall back to a
    /// VFR N-number callsign in that case.
    /// </summary>
    private static string? PickCompatibleAirline(WeightClass weight, EngineKind engine, Random rng)
    {
        if (!TypeTable.TryGetValue((weight, engine), out var bucketTypes))
        {
            return null;
        }

        var compatible = Airlines.Where(a => AirlineFleets.TryGetTypes(a, out var fleet) && bucketTypes.Any(t => fleet.ContainsKey(t))).ToArray();

        return compatible.Length > 0 ? compatible[rng.Next(compatible.Length)] : null;
    }

    private static string? PickCompatibleAirportAirline(string? airportId, WeightClass weight, EngineKind engine, Random rng)
    {
        if (!TypeTable.TryGetValue((weight, engine), out var bucketTypes))
        {
            return null;
        }

        if (airportId is null || !AirportAirlines.TryGetAirlinesForAirport(airportId, out var airportAirlines))
        {
            return null;
        }

        var compatible = airportAirlines
            .Where(a => AirlineFleets.TryGetTypes(a.Icao, out var fleet) && bucketTypes.Any(t => fleet.ContainsKey(t)))
            .ToArray();
        if (compatible.Length == 0)
        {
            return null;
        }

        // Weight linearly by real arrival count so the generated carrier mix matches each airport's actual
        // traffic share -- a dominant carrier (e.g. Southwest at Oakland) shows up proportionally often and
        // the long tail of carriers with a handful of yearly arrivals stays rare. (A square-root weighting
        // over-represented that tail by ~100x.)
        var totalWeight = compatible.Sum(a => (double)Math.Max(1, a.Arrivals));
        var pick = rng.NextDouble() * totalWeight;
        foreach (var entry in compatible)
        {
            pick -= Math.Max(1, entry.Arrivals);
            if (pick <= 0)
            {
                return entry.Icao;
            }
        }

        return compatible[^1].Icao;
    }

    private static string GenerateAirlineCallsign(string airline, HashSet<string> existing, Random rng)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            int digits = rng.Next(3, 5); // 3 or 4 digits
            var number = rng.Next((int)Math.Pow(10, digits - 1), (int)Math.Pow(10, digits));
            var callsign = $"{airline}{number}";
            if (!existing.Contains(callsign))
            {
                return callsign;
            }
        }

        return $"{airline}{rng.Next(10000, 99999)}";
    }

    private static string GenerateNNumber(HashSet<string> existing, Random rng)
    {
        // FAA N-number format: N followed by 5 alphanumeric characters. First char
        // must be a digit 1-9 (no leading zero). Up to 2 letters allowed, at the
        // end. I and O are excluded to avoid confusion with 1 and 0.
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "0123456789";
        for (int attempt = 0; attempt < 100; attempt++)
        {
            int letterCount = rng.Next(0, 3); // 0, 1, or 2 trailing letters
            int digitCount = 5 - letterCount;
            var suffix = new char[5];
            suffix[0] = (char)('1' + rng.Next(9));
            for (int i = 1; i < digitCount; i++)
            {
                suffix[i] = digits[rng.Next(digits.Length)];
            }
            for (int i = digitCount; i < 5; i++)
            {
                suffix[i] = letters[rng.Next(letters.Length)];
            }
            var callsign = $"N{new string(suffix)}";
            if (!existing.Contains(callsign))
            {
                return callsign;
            }
        }

        return $"N{rng.Next(10000, 100000)}";
    }

    private static (double Lat, double Lon) ComputePosition(double originLat, double originLon, double bearingDeg, double distanceNm)
    {
        double bearingRad = bearingDeg * Math.PI / 180.0;
        double latRad = originLat * Math.PI / 180.0;
        const double nmPerDegLat = 60.0;

        double lat = originLat + (distanceNm * Math.Cos(bearingRad) / nmPerDegLat);
        double lon = originLon + (distanceNm * Math.Sin(bearingRad) / (nmPerDegLat * Math.Cos(latRad)));

        return (lat, lon);
    }

    private static double ComputeBearing(double lat1, double lon1, double lat2, double lon2)
    {
        double lat1R = lat1 * Math.PI / 180.0;
        double lat2R = lat2 * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;

        double y = Math.Sin(dLon) * Math.Cos(lat2R);
        double x = Math.Cos(lat1R) * Math.Sin(lat2R) - Math.Sin(lat1R) * Math.Cos(lat2R) * Math.Cos(dLon);

        double bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return ((bearing % 360.0) + 360.0) % 360.0;
    }
}
