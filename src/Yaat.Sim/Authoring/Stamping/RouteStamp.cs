using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>
/// Writes a stamp profile and a route onto a copy of a scenario aircraft: the route with its STAR aliases, then the flight
/// plan, the starting conditions, the presets and the auto-track, and lists every change and warning.
/// </summary>
public static class RouteStamp
{
    private static readonly ILogger Log = SimLog.CreateLogger("RouteStamp");

    /// <summary>Stamps <paramref name="profile"/> and <paramref name="route"/> onto a copy of <paramref name="aircraft"/>.</summary>
    /// <remarks>
    /// The input aircraft never changes. A pool draws from the context's pool random only when it holds a value, and each new
    /// preset id is a ULID made from the context's clock and id random, so a fixed clock and seeds give the same output. A
    /// heavy-only profile skips every type whose wake class is not Heavy, Super included. An alias target missing from
    /// navdata keeps the aircraft's old route and navigation path.
    /// </remarks>
    /// <param name="aircraft">The aircraft to stamp.</param>
    /// <param name="route">The route to write.</param>
    /// <param name="profile">The profile that says what is written.</param>
    /// <param name="context">The clock, the random sources, the taken callsigns and the magnetic model date.</param>
    /// <returns>The stamped copy, its changes, its warnings, or why the profile was skipped.</returns>
    public static StampResult Apply(ScenarioAircraft aircraft, StampRoute route, StampProfile profile, StampContext context) =>
        Apply(aircraft, route, profile, context, NavigationDatabase.InstanceOrNull);

    /// <summary>Stamps as the public overload does, reading procedures and fixes from <paramref name="navDb"/>.</summary>
    /// <param name="aircraft">The aircraft to stamp.</param>
    /// <param name="route">The route to write.</param>
    /// <param name="profile">The profile that says what is written.</param>
    /// <param name="context">The clock, the random sources, the taken callsigns and the magnetic model date.</param>
    /// <param name="navDb">The navigation database; null when it is not loaded.</param>
    /// <returns>The stamped copy, its changes, its warnings, or why the profile was skipped.</returns>
    internal static StampResult Apply(
        ScenarioAircraft aircraft,
        StampRoute route,
        StampProfile profile,
        StampContext context,
        NavigationDatabase? navDb
    )
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(context);
        ScenarioAircraft copy = Copy(aircraft);
        if (SkipReason(aircraft, profile) is { } reason)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Stamp {Profile} skipped {Callsign}: {Reason}", profile.Name, aircraft.AircraftId, reason);
            }

            return new StampResult(copy, [], [], reason);
        }

        var warnings = new List<string>();
        StampPools pools = StampPicks.WithoutBlanks(profile.Pools, warnings);
        string? routeText = StampedRouteText(route, profile, navDb, warnings);
        StampPicks.ApplyIdentity(copy, pools, context, warnings);
        ApplyAirports(copy, profile);
        ApplyFlightPlan(copy, profile.FlightPlan, routeText);
        ApplyStartingConditions(copy, profile.StartingConditions, pools, routeText, context.PoolRandom);
        ApplyPresets(copy, profile.Presets, context);
        ApplyAutoTrack(copy, profile.AutoTrack, warnings);
        StampChecks.Check(copy, profile, navDb, context.MagneticModelDateUtc, warnings);
        return new StampResult(copy, StampChanges.Between(aircraft, copy), warnings, null);
    }

    private static ScenarioAircraft Copy(ScenarioAircraft aircraft) =>
        JsonSerializer.Deserialize<ScenarioAircraft>(JsonSerializer.Serialize(aircraft, ScenarioLoader.JsonOptions), ScenarioLoader.JsonOptions)
        ?? throw new JsonException("The aircraft copy is null.");

    private static string? SkipReason(ScenarioAircraft aircraft, StampProfile profile)
    {
        if (!profile.HeavyOnly)
        {
            return null;
        }

        WakeTurbulenceData.WakeClass wake = WakeTurbulenceData.WakeClassForType(
            aircraft.AircraftType,
            AircraftCategorization.Categorize(aircraft.AircraftType)
        );
        return (wake == WakeTurbulenceData.WakeClass.Heavy) ? null : $"{aircraft.AircraftType} is wake class {wake}; the profile stamps Heavy only";
    }

    // The route text the stamp writes, or null when the profile writes no route or an alias target is missing.
    private static string? StampedRouteText(StampRoute route, StampProfile profile, NavigationDatabase? navDb, List<string> warnings)
    {
        bool writesRoute = profile.FlightPlan.Apply && profile.FlightPlan.ApplyRoute;
        bool writesPath = profile.StartingConditions.Apply && profile.StartingConditions.ApplyNavigationPath;
        if (!writesRoute && !writesPath)
        {
            return null;
        }

        warnings.AddRange(route.Warnings);
        StarAliasResult aliased = StarAliasing.Substitute(route.RouteText, profile.StarAliases);
        foreach (StarAlias rejected in aliased.RejectedAliases)
        {
            warnings.Add($"STAR alias {rejected.From} to {rejected.To} was not applied: a STAR name holds a space or a '.'");
        }

        if (aliased.Replacements.Count == 0)
        {
            return aliased.RouteText;
        }

        if (navDb is null)
        {
            StampChecks.AddNavdataMissing(warnings);
            return aliased.RouteText;
        }

        return StarAliasChecks.Check(aliased, navDb, warnings);
    }

    private static void ApplyAirports(ScenarioAircraft copy, StampProfile profile)
    {
        copy.AirportId = profile.PrimaryAirport ?? copy.AirportId;
        copy.ExpectedApproach = profile.ExpectedApproach ?? copy.ExpectedApproach;
    }

    private static void ApplyFlightPlan(ScenarioAircraft copy, StampFlightPlan stamp, string? routeText)
    {
        if (!stamp.Apply)
        {
            return;
        }

        ScenarioFlightPlan plan = copy.FlightPlan ??= new ScenarioFlightPlan { AircraftType = copy.AircraftType };
        ApplyPlanFields(plan, stamp);
        if (stamp.ApplyRoute && (routeText is not null))
        {
            plan.Route = routeText;
        }
    }

    private static void ApplyPlanFields(ScenarioFlightPlan plan, StampFlightPlan stamp)
    {
        plan.Rules = stamp.Rules ?? plan.Rules;
        plan.Departure = stamp.Departure ?? plan.Departure;
        plan.Destination = stamp.Destination ?? plan.Destination;
        plan.CruiseAltitude = stamp.CruiseAltitude ?? plan.CruiseAltitude;
        plan.CruiseSpeed = stamp.CruiseSpeed ?? plan.CruiseSpeed;
        plan.Remarks = stamp.Remarks ?? plan.Remarks;
    }

    private static void ApplyStartingConditions(
        ScenarioAircraft copy,
        StampStartingConditions stamp,
        StampPools pools,
        string? routeText,
        Random random
    )
    {
        if (!stamp.Apply)
        {
            return;
        }

        StartingConditions start = copy.StartingConditions;
        ApplyStartFields(start, stamp);
        start.Runway = StampPicks.Pick(pools.Runways, random) ?? stamp.Runway ?? start.Runway;
        start.Parking = StampPicks.Pick(pools.ParkingSpots, random) ?? stamp.Parking ?? start.Parking;
        if (stamp.ApplyNavigationPath && (routeText is not null))
        {
            start.NavigationPath = routeText;
        }
    }

    private static void ApplyStartFields(StartingConditions start, StampStartingConditions stamp)
    {
        start.Type = stamp.Type ?? start.Type;
        start.Altitude = stamp.Altitude ?? start.Altitude;
        start.Speed = stamp.Speed ?? start.Speed;
        start.Heading = stamp.Heading ?? start.Heading;
    }

    private static void ApplyPresets(ScenarioAircraft copy, StampPresets stamp, StampContext context)
    {
        if (!stamp.Apply)
        {
            return;
        }

        List<PresetCommand> presets = copy.PresetCommands ??= [];
        if (stamp.Replace)
        {
            presets.Clear();
        }

        foreach (StampPresetCommand command in stamp.Commands)
        {
            presets.Add(
                new PresetCommand
                {
                    Id = ScenarioIdGenerator.Create(context.Now, context.IdRandom),
                    Command = StampTokens.Expand(command.Command, copy),
                    TimeOffset = command.TimeOffset,
                }
            );
        }
    }

    private static void ApplyAutoTrack(ScenarioAircraft copy, StampAutoTrack stamp, List<string> warnings)
    {
        if (!stamp.Apply)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(stamp.PositionId))
        {
            warnings.Add("Auto-track skipped: the profile names no position");
            return;
        }

        AutoTrackConditions conditions = copy.AutoTrackConditions ??= new AutoTrackConditions();
        conditions.PositionId = stamp.PositionId;
        conditions.HandoffDelay = stamp.HandoffDelay;
        conditions.ScratchPad = stamp.ScratchPad;
        conditions.InterimAltitude = stamp.InterimAltitude;
        conditions.ClearedAltitude = stamp.ClearedAltitude;
    }

    /// <summary>The pool picks of a stamp; an empty pool draws nothing.</summary>
    private static class StampPicks
    {
        private const int CallsignAttempts = 100;

        private static readonly ILogger Log = SimLog.CreateLogger("StampPicks");

        public static string? Pick(IReadOnlyList<string> pool, Random random) => (pool.Count == 0) ? null : pool[random.Next(pool.Count)];

        // The pools with every blank entry dropped, one warning per pool that held one.
        public static StampPools WithoutBlanks(StampPools pools, List<string> warnings) =>
            new()
            {
                AircraftTypes = WithoutBlanks(pools.AircraftTypes, "aircraft types", warnings),
                CallsignPrefixes = WithoutBlanks(pools.CallsignPrefixes, "callsign prefixes", warnings),
                Runways = WithoutBlanks(pools.Runways, "runways", warnings),
                ParkingSpots = WithoutBlanks(pools.ParkingSpots, "parking spots", warnings),
            };

        public static void ApplyIdentity(ScenarioAircraft copy, StampPools pools, StampContext context, List<string> warnings)
        {
            if (Pick(pools.AircraftTypes, context.PoolRandom) is { } type)
            {
                copy.AircraftType = WithFiledSuffix(type, copy.AircraftType);
                if (copy.FlightPlan is { } plan)
                {
                    plan.AircraftType = WithFiledSuffix(type, plan.AircraftType);
                }
            }

            if (pools.CallsignPrefixes.Count == 0)
            {
                return;
            }

            if (RandomCallsign(pools.CallsignPrefixes, context.PoolRandom, context.TakenCallsigns) is { } callsign)
            {
                copy.AircraftId = callsign;
            }
            else
            {
                warnings.Add($"No free callsign was found in {CallsignAttempts} tries; {copy.AircraftId} is kept");
            }

            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Stamp picked {Type} {Callsign}", copy.AircraftType, copy.AircraftId);
            }
        }

        private static IReadOnlyList<string> WithoutBlanks(IReadOnlyList<string> pool, string name, List<string> warnings)
        {
            if (!pool.Any(string.IsNullOrWhiteSpace))
            {
                return pool;
            }

            warnings.Add($"Pool {name} has a blank entry; skipped");
            return [.. pool.Where(entry => !string.IsNullOrWhiteSpace(entry))];
        }

        // The picked type with the equipment suffix of the type it replaces, unless the pick carries its own.
        private static string WithFiledSuffix(string type, string replaced)
        {
            FiledAircraftType pick = FlightPlanNormalization.SplitTypeAndSuffix(type);
            if ((pick.Suffix is not null) || (FlightPlanNormalization.SplitTypeAndSuffix(replaced).Suffix is not { Length: > 0 } suffix))
            {
                return type;
            }

            string elementA = ((pick.Count is null) && (pick.Indicator is null)) ? "" : $"{pick.Count}{pick.Indicator}/";
            return string.Create(CultureInfo.InvariantCulture, $"{elementA}{pick.Type}/{suffix}");
        }

        // A pool prefix and 1 to 4 digits without a leading zero, not in the taken set.
        private static string? RandomCallsign(IReadOnlyList<string> prefixes, Random random, IReadOnlySet<string> taken)
        {
            for (int attempt = 0; attempt < CallsignAttempts; attempt++)
            {
                string prefix = prefixes[random.Next(prefixes.Count)].Trim().ToUpperInvariant();
                string callsign = prefix + random.Next(1, 10_000).ToString(CultureInfo.InvariantCulture);
                if (!taken.Contains(callsign))
                {
                    return callsign;
                }
            }

            return null;
        }
    }
}

/// <summary>The checks on a route whose STARs aliases replaced, read from the navdata STAR bodies and transitions.</summary>
internal static class StarAliasChecks
{
    private static readonly ILogger Log = SimLog.CreateLogger("StarAliasChecks");

    /// <summary>
    /// Checks each replaced token: its STAR must be in navdata, a transition the STAR lacks is cut back to the bare STAR, and
    /// the fix before it must join the STAR at an entry, or lie on the transition the token names.
    /// </summary>
    /// <remarks>The warnings are added only when the route is written; a STAR missing from navdata adds its error alone.</remarks>
    /// <param name="aliased">The aliased route.</param>
    /// <param name="navDb">The navigation database.</param>
    /// <param name="warnings">The stamp's warnings.</param>
    /// <returns>The checked route text, or null when a STAR is missing from navdata.</returns>
    public static string? Check(StarAliasResult aliased, NavigationDatabase navDb, List<string> warnings)
    {
        string[] tokens = [.. RouteTokens.Split(aliased.RouteText)];
        var found = new List<string>();
        foreach (StarAliasReplacement replacement in aliased.Replacements)
        {
            DottedToken parts = RouteTokens.SplitDotted(tokens[replacement.TokenIndex]);
            if (navDb.ResolveStarId(parts.Head) is not { } starId)
            {
                warnings.Add($"Error: STAR {parts.Head} (alias of {replacement.Alias.From}) is not in navdata; the route keeps its old text");
                return null;
            }

            var star = new StampedStar(parts.Head, starId, KnownTransition(parts, starId, navDb, found));
            tokens[replacement.TokenIndex] = (star.Transition is null) ? star.Head : $"{star.Head}.{star.Transition}";
            if (replacement.TokenIndex > 0)
            {
                CheckJoin(RouteTokens.SplitDotted(tokens[replacement.TokenIndex - 1]).Head, star, navDb, found);
            }
        }

        if ((found.Count > 0) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("STAR alias checks raised {Count} warnings", found.Count);
        }

        warnings.AddRange(found);
        return RouteTokens.Join(tokens);
    }

    // The token's transition when the STAR publishes it; null for a bare token or, with a warning, an unknown transition.
    private static string? KnownTransition(DottedToken parts, string starId, NavigationDatabase navDb, List<string> warnings)
    {
        if (parts.Rest.Length <= 1)
        {
            return null;
        }

        string transition = parts.Rest[1..];
        if ((navDb.GetStarTransitions(starId) ?? []).Any(t => Same(t.Name, transition)))
        {
            return transition;
        }

        warnings.Add($"STAR {parts.Head} has no {transition} transition; the route files the bare {parts.Head}");
        return null;
    }

    private static void CheckJoin(string fix, StampedStar star, NavigationDatabase navDb, List<string> warnings)
    {
        IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> transitions = navDb.GetStarTransitions(star.Id) ?? [];
        IReadOnlyList<string> body = navDb.GetStarBody(star.Id) ?? [];
        if (star.Transition is { } named)
        {
            if (!IsOnTransition(fix, named, transitions))
            {
                warnings.Add($"{fix} before STAR {star.Head} is not on its {named} transition");
            }

            return;
        }

        if (IsEntry(fix, body, transitions))
        {
            return;
        }

        bool published = body.Contains(fix, StringComparer.OrdinalIgnoreCase) || transitions.Any(t => HasFix(t.Fixes, fix));
        warnings.Add(
            published ? $"{fix} joins STAR {star.Head} mid-procedure" : $"{fix} before STAR {star.Head} is not one of its entry or transition fixes"
        );
    }

    // Whether the fix is the named transition's name or one of its fixes.
    private static bool IsOnTransition(string fix, string named, IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> transitions) =>
        transitions.Any(t => Same(t.Name, named) && (Same(t.Name, fix) || HasFix(t.Fixes, fix)));

    private static bool HasFix(IReadOnlyList<string> fixes, string fix) => fixes.Contains(fix, StringComparer.OrdinalIgnoreCase);

    // A clean join: a transition's name or first fix, or the body's first fix.
    private static bool IsEntry(string fix, IReadOnlyList<string> body, IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)> transitions) =>
        ((body.Count > 0) && Same(body[0], fix)) || transitions.Any(t => Same(t.Name, fix) || ((t.Fixes.Count > 0) && Same(t.Fixes[0], fix)));

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // A STAR token after its alias: the name the route files, its navdata id, and the transition it keeps.
    private readonly record struct StampedStar(string Head, string Id, string? Transition);
}

/// <summary>The warnings on a stamped aircraft: procedures at their airports, and cruise and starting altitudes.</summary>
internal static class StampChecks
{
    private const int DirectionRuleFloorFt = 3000;
    private const int ClassAFloorFt = 18000;
    private const int RvsmTopFt = 41000;
    private const string NavdataMissing = "Navdata is not loaded; SID and STAR checks skipped";

    private static readonly ILogger Log = SimLog.CreateLogger("StampChecks");

    /// <summary>Adds every warning the stamped aircraft raises.</summary>
    /// <param name="copy">The stamped aircraft.</param>
    /// <param name="profile">The profile it was stamped with.</param>
    /// <param name="navDb">The navigation database; null when it is not loaded, which skips the procedure and course checks.</param>
    /// <param name="magneticModelDateUtc">The date the magnetic model is evaluated at for the course.</param>
    /// <param name="warnings">The stamp's warnings.</param>
    public static void Check(
        ScenarioAircraft copy,
        StampProfile profile,
        NavigationDatabase? navDb,
        DateTime magneticModelDateUtc,
        List<string> warnings
    )
    {
        if (copy.FlightPlan is not { } plan)
        {
            return;
        }

        if (profile.FlightPlan.Apply)
        {
            if (navDb is null)
            {
                AddNavdataMissing(warnings);
            }
            else
            {
                CheckProcedures(plan, profile.FlightPlan, navDb, warnings);
            }
        }

        if (plan.CruiseAltitude <= 0)
        {
            return;
        }

        CheckClassA(plan, warnings);
        if ((navDb is not null) && (MagneticCourse(plan.Route, navDb, magneticModelDateUtc) is { } course))
        {
            CheckDirectionOfFlight(plan, course, warnings);
        }

        CheckCeiling(copy, plan, warnings);
        CheckAirborneStart(copy.StartingConditions, plan, warnings);
    }

    /// <summary>True when an IFR cruise altitude fits the direction-of-flight rule for a magnetic course (7110.65 §4-5-2; 14 CFR 91.179).</summary>
    /// <param name="altitudeFt">The cruise altitude in feet.</param>
    /// <param name="magneticCourse">The magnetic course in degrees, read to the nearest whole degree.</param>
    /// <returns>Whether the altitude fits.</returns>
    public static bool IfrAltitudeFits(int altitudeFt, double magneticCourse)
    {
        bool east = Eastbound(magneticCourse);
        if (altitudeFt > RvsmTopFt)
        {
            int first = east ? 45000 : 43000;
            return (altitudeFt >= first) && (((altitudeFt - first) % 4000) == 0);
        }

        return ((altitudeFt % 1000) == 0) && (IsOddThousand(altitudeFt) == east);
    }

    /// <summary>True when a VFR cruise altitude fits the 14 CFR 91.159 rule (odd or even thousands plus 500) for a magnetic course.</summary>
    /// <param name="altitudeFt">The cruise altitude in feet.</param>
    /// <param name="magneticCourse">The magnetic course in degrees, read to the nearest whole degree.</param>
    /// <returns>Whether the altitude fits.</returns>
    public static bool VfrAltitudeFits(int altitudeFt, double magneticCourse) =>
        ((altitudeFt % 1000) == 500) && (IsOddThousand(altitudeFt - 500) == Eastbound(magneticCourse));

    private static bool Eastbound(double magneticCourse) =>
        FiledCruiseAltitude.IsEastboundMagneticCourse(MagneticDeclination.NormalizeDeg(magneticCourse));

    private static bool IsOddThousand(int altitudeFt) => ((altitudeFt / 1000) % 2) == 1;

    private static bool IsVfr(ScenarioFlightPlan plan) => string.Equals(plan.Rules, "VFR", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds the one line that says navdata is missing, unless the stamp already holds it.</summary>
    /// <param name="warnings">The stamp's warnings.</param>
    public static void AddNavdataMissing(List<string> warnings)
    {
        if (!warnings.Contains(NavdataMissing))
        {
            warnings.Add(NavdataMissing);
        }
    }

    private static void CheckProcedures(ScenarioFlightPlan plan, StampFlightPlan stamp, NavigationDatabase navDb, List<string> warnings)
    {
        foreach (string token in RouteTokens.Split(plan.Route))
        {
            string head = RouteTokens.SplitDotted(token).Head.ToUpperInvariant();
            if ((stamp.Departure is { } departure) && (navDb.GetSidBody(head) is not null) && (navDb.GetSid(departure, head) is null))
            {
                warnings.Add($"SID {head} is not a procedure of the departure airport {departure}");
            }

            if ((stamp.Destination is { } destination) && (navDb.GetStarBody(head) is not null) && (navDb.GetStar(destination, head) is null))
            {
                warnings.Add($"STAR {head} is not a procedure of the destination airport {destination}");
            }
        }
    }

    // The magnetic course from the first to the last fix of the expanded route, or null with fewer than two placed fixes.
    private static double? MagneticCourse(string route, NavigationDatabase navDb, DateTime magneticModelDateUtc)
    {
        (double Lat, double Lon)[] positions =
        [
            .. RouteExpander
                .Expand(route, navDb, includeAllTransitionsOnMismatch: false)
                .Select(navDb.GetFixPosition)
                .OfType<(double Lat, double Lon)>(),
        ];
        if ((positions.Length < 2) || (positions[0] == positions[^1]))
        {
            return null;
        }

        (double lat, double lon) = positions[0];
        double trueCourse = GeoMath.BearingTo(lat, lon, positions[^1].Lat, positions[^1].Lon);
        double course = MagneticDeclination.TrueToMagnetic(trueCourse, lat, lon, magneticModelDateUtc);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Stamped route {Route} flies magnetic course {Course:0.0}", route, course);
        }

        return course;
    }

    private static void CheckClassA(ScenarioFlightPlan plan, List<string> warnings)
    {
        if (IsVfr(plan) && (plan.CruiseAltitude >= ClassAFloorFt))
        {
            warnings.Add($"VFR cruise {plan.CruiseAltitude} ft is in Class A (14 CFR 91.135)");
        }
    }

    private static void CheckDirectionOfFlight(ScenarioFlightPlan plan, double course, List<string> warnings)
    {
        int altitude = plan.CruiseAltitude;
        string courseText = Math.Round(MagneticDeclination.NormalizeDeg(course)).ToString("000", CultureInfo.InvariantCulture);
        bool vfr = IsVfr(plan);
        if (vfr && (altitude > DirectionRuleFloorFt) && (altitude < ClassAFloorFt) && !VfrAltitudeFits(altitude, course))
        {
            warnings.Add($"VFR cruise {altitude} ft does not fit magnetic course {courseText} (14 CFR 91.159)");
        }
        else if (!vfr && (altitude >= DirectionRuleFloorFt) && !IfrAltitudeFits(altitude, course))
        {
            warnings.Add($"IFR cruise {altitude} ft does not fit magnetic course {courseText} (7110.65 §4-5-2; 14 CFR 91.179)");
        }
    }

    private static void CheckCeiling(ScenarioAircraft copy, ScenarioFlightPlan plan, List<string> warnings)
    {
        if ((AircraftPerformance.Ceiling(copy.AircraftType) is { } ceiling) && (plan.CruiseAltitude > ceiling))
        {
            warnings.Add($"Cruise {plan.CruiseAltitude} ft is above the {copy.AircraftType} ceiling of {ceiling:0} ft");
        }
    }

    private static void CheckAirborneStart(StartingConditions start, ScenarioFlightPlan plan, List<string> warnings)
    {
        bool airborne = start.Type is StartingConditionKinds.Coordinates or StartingConditionKinds.FixOrFrd or StartingConditionKinds.OnFinal;
        if (airborne && (start.Altitude is { } altitude) && (altitude > plan.CruiseAltitude))
        {
            warnings.Add($"The airborne start at {altitude:0} ft is above the filed cruise of {plan.CruiseAltitude} ft");
        }
    }
}
