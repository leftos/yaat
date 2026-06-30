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
    public bool HasParkingSpawns { get; init; }
    public bool HasArrivalGenerators { get; init; }
    public string? AutoDeleteMode { get; init; }
    public string? MinimumRating { get; init; }
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
            HasParkingSpawns = scenario.Aircraft.Any(ac =>
                string.Equals(ac.StartingConditions.Type, "Parking", StringComparison.OrdinalIgnoreCase) && !HasTaxiPreset(ac.PresetCommands)
            ),
            HasArrivalGenerators = scenario.AircraftGenerators.Count > 0,
            AutoDeleteMode = scenario.AutoDeleteMode,
            MinimumRating = scenario.MinimumRating,
            Warnings = warnings,
        };
    }

    internal static AircraftState CreateBaseState(ScenarioAircraft ac, string? primaryAirportId, string? primaryApproach)
    {
        // Top-level wins for the actual physical type. Filed FP type is opt-in: only set when
        // the scenario explicitly populates FlightPlan.aircraftType. EquipmentSuffix derives
        // from the filed string when present, else from the actual type so legacy scenarios
        // without a filed type still surface a sensible suffix on strips. Cold calls (no
        // scenario flightPlan block) get a blank suffix — controllers file via DA / VP.
        var hasFiledFp = ac.FlightPlan is not null;
        var actualType = ac.AircraftType;
        var filedType = ac.FlightPlan?.AircraftType ?? "";
        var suffixSource = !string.IsNullOrEmpty(filedType) ? filedType : actualType;
        var equipmentSuffix = hasFiledFp ? ExtractSuffix(suffixSource) : "";

        // primaryApproach is only intended for the scenario's primary airport.
        // Aircraft destined elsewhere must not inherit it — even if the same approach ID
        // exists at their destination, it was chosen for the primary airport's routes.
        string? effectiveApproach = ac.ExpectedApproach;
        if (effectiveApproach is null && primaryApproach is not null)
        {
            var dest = ac.FlightPlan?.Destination;
            bool destMatchesPrimary =
                primaryAirportId is null
                || string.IsNullOrEmpty(dest)
                || NormalizeAirportCode(dest).Equals(NormalizeAirportCode(primaryAirportId), StringComparison.OrdinalIgnoreCase);
            if (destMatchesPrimary)
            {
                effectiveApproach = primaryApproach;
            }
        }

        return new AircraftState
        {
            Callsign = ac.AircraftId,
            AircraftType = actualType,
            AirportId = ac.AirportId ?? primaryAirportId ?? "",
            Transponder = new AircraftTransponder { Mode = ac.TransponderMode },
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = InferFlightRules(ac.FlightPlan),
                AircraftType = filedType,
                CruiseAltitude = ac.FlightPlan?.CruiseAltitude ?? 0,
                CruiseSpeed = ac.FlightPlan?.CruiseSpeed ?? 0,
                Departure = ac.FlightPlan?.Departure ?? "",
                Destination = ac.FlightPlan?.Destination ?? "",
                Route = ac.FlightPlan?.Route ?? "",
                Remarks = ac.FlightPlan?.Remarks ?? "",
                EquipmentSuffix = equipmentSuffix,
                HasFlightPlan = hasFiledFp,
            },
            Approach = new AircraftApproachState { Expected = effectiveApproach },
        };
    }

    public static string InferFlightRules(ScenarioFlightPlan? fp)
    {
        if (fp?.Rules is not null)
        {
            return fp.Rules;
        }

        if ((fp is null) || (fp.CruiseAltitude <= 0))
        {
            return "VFR";
        }

        return "IFR";
    }

    // Aircraft with a filed scenario flight plan get a discrete beacon code at
    // spawn. Cold-call aircraft (no flightplan field in the scenario JSON) get
    // no AssignedCode and squawk 1200 (FAA VFR conspicuity code) — controllers
    // file via DA / VP later, which assigns the discrete code.
    private static void AssignSpawnBeacon(AircraftState state, ScenarioAircraft ac, Random rng)
    {
        if (ac.FlightPlan is not null)
        {
            var code = SimulationWorld.GenerateBeaconCode(rng);
            state.Transponder.AssignedCode = code;
            state.Transponder.Code = code;
        }
        else
        {
            state.Transponder.Code = 1200;
        }
    }

    private static string NormalizeAirportCode(string code)
    {
        return (code.StartsWith('K') && code.Length == 4) ? code[1..] : code;
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
                lat = resolved.Value.Lat;
                lon = resolved.Value.Lon;
                alt = cond.Altitude ?? fieldElevation;
                speed = cond.Altitude is null && cond.Speed is null ? 0 : cond.Speed ?? -1;
                break;

            case "OnRunway":
                return LoadOnRunway(ac, groundData, warnings, primaryAirportId, primaryApproach, rng);

            case "OnFinal":
                return LoadOnFinal(ac, groundData, warnings, primaryAirportId, primaryApproach, rng);

            case "Parking":
                return LoadAtParking(ac, groundData, primaryAirportId, warnings, primaryApproach, rng);

            default:
                warnings.Add($"{ac.AircraftId}: Unknown starting " + $"condition type '{cond.Type}'");
                return null;
        }

        var category = AircraftCategorization.Categorize(ac.AircraftType);

        // Ground detection runs against the *unresolved* speed sentinel, before the cruise-speed
        // default. `speed` is 0 (altitude and speed both omitted) or -1 (altitude authored at field
        // elevation, speed omitted) — both mean "no positive authored speed", i.e. a ground spawn.
        // Resolving DefaultSpeed first would turn the -1 sentinel into a positive cruise speed and
        // make the gate fail, spawning a departure that sits at field elevation airborne.
        var agl = alt - fieldElevation;
        var onGround = speed <= 0 && agl < 200;
        if (onGround)
        {
            speed = 0;
        }
        else if (speed < 0)
        {
            speed = AircraftPerformance.DefaultSpeed(ac.AircraftType, category, alt, null);
        }

        var state = CreateBaseState(ac, primaryAirportId, primaryApproach);
        state.Position = new LatLon(lat, lon);
        state.Altitude = alt;
        state.IndicatedAirspeed = speed;
        AssignSpawnBeacon(state, ac, rng);

        if (onGround)
        {
            state.IsOnGround = true;
            var phases = new PhaseList();
            phases.Add(new AtParkingPhase());
            state.Phases = phases;

            // Resolve ground layout from departure (on-ground aircraft) or destination, mirror the
            // Parking path: exempt from Parked auto-delete and flag scripted-departure when a TAXI
            // preset drives the ground sequence.
            var groundAirportId = ac.FlightPlan?.Departure ?? ac.FlightPlan?.Destination;
            state.Ground.Layout = !string.IsNullOrEmpty(groundAirportId) ? groundData?.GetLayout(groundAirportId) : null;
            state.Ground.AutoDeleteExempt = true;
            state.Ground.IsScriptedDeparture = HasTaxiPreset(ac.PresetCommands);
        }

        var navigationPath = ResolveVersionChanges(cond.NavigationPath ?? "", state, warnings);
        ArrivalRouteResolver.PopulateNavigationRoute(state, navigationPath, warnings);

        // Derive heading: scenario-assigned heading (magnetic) or bearing to first nav route fix (already true)
        if (cond.Heading is not null)
        {
            // Scenario headings are in the magnetic (controller/pilot) reference frame
            double trueHeadingDeg = MagneticDeclination.MagneticToTrue(cond.Heading.Value, lat, lon);
            state.TrueHeading = new TrueHeading(trueHeadingDeg);
            state.TrueTrack = state.TrueHeading;
        }
        else if (state.Targets.NavigationRoute.Count > 0)
        {
            // BearingTo returns a true bearing
            var first = state.Targets.NavigationRoute[0];
            double bearingDeg = GeoMath.BearingTo(new LatLon(lat, lon), first.Position);
            state.TrueHeading = new TrueHeading(bearingDeg);
            state.TrueTrack = state.TrueHeading;
        }
        else
        {
            state.TrueHeading = new TrueHeading(0.0);
            state.TrueTrack = state.TrueHeading;
        }

        if (ac.OnAltitudeProfile)
        {
            ArrivalRouteResolver.ApplyAltitudeProfile(state, navigationPath, warnings);
        }

        // Snap "Coordinates" / "FixOrFrd" ground spawns onto the nearest taxi
        // edge and rotate the heading to match. Coord spawns are how
        // scenarios express "ready to taxi from this point" (parking spawns
        // use the "Parking" type and are already on a graph node); without
        // the snap, the aircraft ends up a few feet off every taxiway and
        // any TAXI preset has to cut diagonally across terrain to acquire
        // the first segment. After snap, the TAXI command plans from an
        // on-edge, aligned pose. Runs AFTER heading derivation so the
        // scenario's intended heading is used as the "which edge direction"
        // tiebreaker.
        if (state.IsOnGround && state.Ground.Layout is not null)
        {
            GroundSpawnSnap.Apply(state, state.Ground.Layout);
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
        string? primaryAirportId,
        string? primaryApproach,
        Random rng
    )
    {
        var runwayId = ac.StartingConditions.Runway;
        var airportId = ac.AirportId ?? ac.FlightPlan?.Departure ?? "";

        if (string.IsNullOrEmpty(runwayId) || string.IsNullOrEmpty(airportId))
        {
            warnings.Add($"{ac.AircraftId}: OnRunway requires runway and airport ID");
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, "OnRunway (missing runway/airport)");
        }

        var rwy = NavigationDatabase.Instance.GetRunway(airportId, runwayId);
        if (rwy is null)
        {
            warnings.Add($"{ac.AircraftId}: Could not find runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} at {airportId}");
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, $"OnRunway ({airportId}/{runwayId} not found)");
        }

        var rwyCategory = AircraftCategorization.Categorize(ac.AircraftType);
        var init = AircraftInitializer.InitializeOnRunway(rwy, rwyCategory);

        var state = CreateBaseState(ac, primaryAirportId, primaryApproach);
        state.Position = init.Position;
        state.TrueHeading = init.TrueHeading;
        state.TrueTrack = init.TrueHeading;
        state.Altitude = init.Altitude;
        state.IndicatedAirspeed = init.Speed;
        state.IsOnGround = init.IsOnGround;
        AssignSpawnBeacon(state, ac, rng);
        state.Phases = init.Phases;
        state.Ground.Layout = groundData?.GetLayout(airportId);

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
        string? primaryAirportId,
        string? primaryApproach,
        Random rng
    )
    {
        var runwayId = ac.StartingConditions.Runway;
        var airportId = ac.AirportId ?? ac.FlightPlan?.Departure ?? "";

        if (string.IsNullOrEmpty(runwayId) || string.IsNullOrEmpty(airportId))
        {
            warnings.Add($"{ac.AircraftId}: OnFinal requires runway and airport ID");
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, "OnFinal (missing runway/airport)");
        }

        var rwy = NavigationDatabase.Instance.GetRunway(airportId, runwayId);
        if (rwy is null)
        {
            warnings.Add($"{ac.AircraftId}: Could not find runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} at {airportId}");
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, $"OnFinal ({airportId}/{runwayId} not found)");
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

        var state = CreateBaseState(ac, primaryAirportId, primaryApproach);
        state.Position = init.Position;
        state.TrueHeading = init.TrueHeading;
        state.TrueTrack = init.TrueHeading;
        state.Altitude = init.Altitude;
        state.IndicatedAirspeed = init.Speed;
        state.IsOnGround = init.IsOnGround;
        AssignSpawnBeacon(state, ac, rng);
        state.Phases = init.Phases;

        // Arriving aircraft: use destination airport layout for runway exit after landing
        var destId = ac.FlightPlan?.Destination;
        state.Ground.Layout = !string.IsNullOrEmpty(destId) ? groundData?.GetLayout(destId) : null;

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
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, "Parking (missing airport)");
        }

        if (groundData is null)
        {
            warnings.Add($"{ac.AircraftId}: Parking requires airport ground data");
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, "Parking (no ground data)");
        }

        var layout = groundData.GetLayout(airportId);
        if (layout is null)
        {
            warnings.Add($"{ac.AircraftId}: No ground layout for {airportId}");
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, $"Parking ({airportId} has no ground data)");
        }

        var parkingName = cond.Parking;
        if (string.IsNullOrEmpty(parkingName))
        {
            warnings.Add($"{ac.AircraftId}: Parking type but no parking name");
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, "Parking (missing parking name)");
        }

        // Search parking first, then helipads and other spots
        var node = layout.FindParkingByName(parkingName) ?? layout.FindSpotByName(parkingName);
        if (node is null)
        {
            warnings.Add($"{ac.AircraftId}: Parking '{parkingName}' not found at {airportId}");
            return BuildDeferredAircraft(ac, primaryAirportId, primaryApproach, $"Parking ({parkingName} not found)");
        }

        var elevation = NavigationDatabase.Instance.GetAirportElevation(airportId) ?? 0;
        var init = AircraftInitializer.InitializeAtParking(node, elevation);

        var state = CreateBaseState(ac, primaryAirportId, primaryApproach);
        state.Position = init.Position;
        state.TrueHeading = init.TrueHeading;
        state.TrueTrack = init.TrueHeading;
        state.Altitude = init.Altitude;
        state.IndicatedAirspeed = init.Speed;
        state.IsOnGround = init.IsOnGround;
        AssignSpawnBeacon(state, ac, rng);
        state.Phases = init.Phases;
        state.Ground.AutoDeleteExempt = true;
        state.Ground.Layout = layout;
        state.Ground.IsScriptedDeparture = HasTaxiPreset(ac.PresetCommands);

        return new LoadedAircraft
        {
            State = state,
            SpawnDelaySeconds = ac.SpawnDelay,
            PresetCommands = ac.PresetCommands,
            AutoTrackConditions = ac.AutoTrackConditions,
        };
    }

    /// <summary>
    /// True when any preset command on this aircraft is a TAXI command. Scenario authors
    /// who script TAXI on a parking aircraft are taking over the ground sequence — the
    /// autonomous solo-training ready-to-taxi call-up should not fire on top of it, and
    /// the aircraft should not count toward the "has parking call-up source" gate that
    /// shows the pacing slider.
    /// </summary>
    public static bool HasTaxiPreset(IEnumerable<PresetCommand> presets)
    {
        foreach (var preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Command))
            {
                continue;
            }

            var firstToken = preset.Command.AsSpan().Trim();
            int spaceIdx = firstToken.IndexOf(' ');
            var verb = (spaceIdx < 0 ? firstToken : firstToken[..spaceIdx]).ToString();
            if (CommandRegistry.IsAliasFor(CanonicalCommandType.Taxi, verb))
            {
                return true;
            }
        }

        return false;
    }

    private static LoadedAircraft BuildDeferredAircraft(ScenarioAircraft ac, string? primaryAirportId, string? primaryApproach, string reason)
    {
        return new LoadedAircraft
        {
            State = CreateBaseState(ac, primaryAirportId, primaryApproach),
            DeferralReason = reason,
            PresetCommands = ac.PresetCommands,
        };
    }

    /// <summary>
    /// Detects SID/STAR version upgrades in the navigation path, emits warnings,
    /// and substitutes stale transition fixes with the geographically closest valid one.
    /// Returns the (possibly modified) navigation path. Also updates state.FlightPlan.Route in place.
    /// </summary>
    private static string ResolveVersionChanges(string navigationPath, AircraftState state, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(navigationPath))
        {
            return navigationPath;
        }

        var navDb = NavigationDatabase.Instance;
        var tokens = navigationPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var routeTokens = state.FlightPlan.Route.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
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
                    if (!IsFixOnSid(nextFixName, resolvedSidId, state.FlightPlan.Departure, navDb))
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
                    if (!IsFixOnStar(prevFixName, resolvedStarId, state.FlightPlan.Destination, navDb))
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
            state.FlightPlan.Route = string.Join(" ", routeTokens);
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
            return (frd.Value.Lat, frd.Value.Lon);
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
