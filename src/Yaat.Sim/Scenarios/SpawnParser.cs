using Yaat.Sim.Commands;

namespace Yaat.Sim.Scenarios;

public static class SpawnParser
{
    public static (SpawnRequest? Request, string? Error) Parse(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4)
        {
            return (null, "Not enough arguments. Usage: ADD {rules} {weight} {engine} {position...}");
        }

        if (!TryParseRules(tokens[0], out var rules))
        {
            return (null, $"Invalid flight rules '{tokens[0]}'. Use I/IFR or V/VFR");
        }

        if (!TryParseWeight(tokens[1], out var weight))
        {
            return (null, $"Invalid weight class '{tokens[1]}'. Use S (small), S+ (smallplus), L (large), or H (heavy)");
        }

        if (!TryParseEngine(tokens[2], out var engine))
        {
            return (null, $"Invalid engine type '{tokens[2]}'. Use P (piston), T (turboprop), J (jet), or H (helicopter)");
        }

        var comboError = ValidateCombo(weight, engine);
        if (comboError is not null)
        {
            return (null, comboError);
        }

        // Arrival-on-STAR: the first position token (always index 3 — the trailing-override scan
        // never consumes it) is a dotted WAYPOINT.STAR[.RUNWAY] route. Dispatch here, before the
        // generic type/airline scan, so the variant owns its full trailing parse (SP### speed,
        // LVL, airport) order-independently. Bearing tokens start '-', fix/parking start '@'.
        if (!tokens[3].StartsWith('-') && !tokens[3].StartsWith('@') && tokens[3].Contains('.'))
        {
            return ParseOnStarVariant(tokens[3..], rules, weight, engine);
        }

        // Parse optional trailing overrides (type like B738, airline like *UAL). Stop at index 4 so the
        // first position token (index 3) is never consumed — e.g. a runway like "28R" looks like an
        // aircraft type (3-4 chars, letter + digit) but is the position, not a type override.
        string? explicitType = null;
        string? explicitAirline = null;
        int positionEndIndex = tokens.Length;

        for (int i = tokens.Length - 1; i >= 4; i--)
        {
            if (tokens[i].StartsWith('*') && tokens[i].Length > 1)
            {
                explicitAirline = tokens[i][1..].ToUpperInvariant();
                positionEndIndex = i;
            }
            else if (IsLikelyAircraftType(tokens[i]))
            {
                explicitType = tokens[i].ToUpperInvariant();
                positionEndIndex = i;
            }
            else
            {
                break;
            }
        }

        var posTokens = tokens[3..positionEndIndex];
        if (posTokens.Length == 0)
        {
            return (null, "Missing position arguments after engine type");
        }

        // Determine position variant
        if (posTokens[0].StartsWith('-'))
        {
            return ParseBearingVariant(posTokens, rules, weight, engine, explicitType, explicitAirline);
        }

        if (posTokens[0].StartsWith('@'))
        {
            // Disambiguate on token count: "@name" alone = parking (ground level), "@name {alt}" = at-fix.
            // Counting rather than sniffing posTokens[1] for a number keeps AGL altitudes ("KOAK+010")
            // on the at-fix path instead of silently falling through to parking.
            if (posTokens.Length == 1)
            {
                return ParseParkingVariant(posTokens, rules, weight, engine, explicitType, explicitAirline);
            }

            return ParseFixVariant(posTokens, rules, weight, engine, explicitType, explicitAirline);
        }

        return ParseRunwayVariant(posTokens, rules, weight, engine, explicitType, explicitAirline);
    }

    private static (SpawnRequest? Request, string? Error) ParseBearingVariant(
        string[] posTokens,
        FlightRulesKind rules,
        WeightClass weight,
        EngineKind engine,
        string? explicitType,
        string? explicitAirline
    )
    {
        if (posTokens.Length < 3)
        {
            return (null, "Bearing variant requires: -{bearing} {distance} {altitude}");
        }

        if (posTokens.Length > 3)
        {
            return (null, "Too many position arguments for bearing variant");
        }

        if (!double.TryParse(posTokens[0][1..], out var bearing) || bearing < 0 || bearing > 360)
        {
            return (null, $"Invalid bearing '{posTokens[0]}'. Use -{{0-360}}");
        }

        if (!double.TryParse(posTokens[1], out var dist) || dist <= 0)
        {
            return (null, $"Invalid distance '{posTokens[1]}'. Must be a positive number (nm)");
        }

        var alt = AltitudeResolver.Resolve(posTokens[2]);
        if (alt is null)
        {
            return (null, $"Invalid altitude '{posTokens[2]}'. Use hundreds of feet ('035'), full feet ('3500'), or AGL ('KOAK+010')");
        }

        return (
            new SpawnRequest
            {
                Rules = rules,
                Weight = weight,
                Engine = engine,
                PositionType = SpawnPositionType.Bearing,
                Bearing = bearing,
                DistanceNm = dist,
                Altitude = alt.Value,
                ExplicitType = explicitType,
                ExplicitAirline = explicitAirline,
            },
            null
        );
    }

    private static (SpawnRequest? Request, string? Error) ParseFixVariant(
        string[] posTokens,
        FlightRulesKind rules,
        WeightClass weight,
        EngineKind engine,
        string? explicitType,
        string? explicitAirline
    )
    {
        if (posTokens.Length < 2)
        {
            return (null, "Fix variant requires: @{fix_or_FRD} {altitude}");
        }

        if (posTokens.Length > 2)
        {
            return (null, "Too many position arguments for fix variant");
        }

        var fixId = posTokens[0][1..]; // strip '@' prefix
        if (string.IsNullOrEmpty(fixId))
        {
            return (null, "Missing fix name after '@'");
        }

        var alt = AltitudeResolver.Resolve(posTokens[1]);
        if (alt is null)
        {
            return (null, $"Invalid altitude '{posTokens[1]}'. Use hundreds of feet ('035'), full feet ('3500'), or AGL ('KOAK+010')");
        }

        return (
            new SpawnRequest
            {
                Rules = rules,
                Weight = weight,
                Engine = engine,
                PositionType = SpawnPositionType.AtFix,
                FixId = fixId.ToUpperInvariant(),
                Altitude = alt.Value,
                ExplicitType = explicitType,
                ExplicitAirline = explicitAirline,
            },
            null
        );
    }

    private static (SpawnRequest? Request, string? Error) ParseParkingVariant(
        string[] posTokens,
        FlightRulesKind rules,
        WeightClass weight,
        EngineKind engine,
        string? explicitType,
        string? explicitAirline
    )
    {
        var parkingName = posTokens[0][1..]; // strip '@' prefix
        if (string.IsNullOrEmpty(parkingName))
        {
            return (null, "Missing parking/helipad name after '@'");
        }

        return (
            new SpawnRequest
            {
                Rules = rules,
                Weight = weight,
                Engine = engine,
                PositionType = SpawnPositionType.Parking,
                ParkingName = parkingName.ToUpperInvariant(),
                ExplicitType = explicitType,
                ExplicitAirline = explicitAirline,
            },
            null
        );
    }

    private static (SpawnRequest? Request, string? Error) ParseRunwayVariant(
        string[] posTokens,
        FlightRulesKind rules,
        WeightClass weight,
        EngineKind engine,
        string? explicitType,
        string? explicitAirline
    )
    {
        var runwayId = posTokens[0].ToUpperInvariant();

        if (posTokens.Length == 1)
        {
            // Lined up on runway (no filed route)
            return (
                new SpawnRequest
                {
                    Rules = rules,
                    Weight = weight,
                    Engine = engine,
                    PositionType = SpawnPositionType.Runway,
                    RunwayId = runwayId,
                    ExplicitType = explicitType,
                    ExplicitAirline = explicitAirline,
                },
                null
            );
        }

        if (posTokens.Length == 2)
        {
            // A numeric second token is an on-final distance; a non-numeric token is a dot-joined
            // departure route (e.g. "NIMI6.OAK.SAU"), spawning a departure lined up on the runway.
            if (double.TryParse(posTokens[1], out var finalDist))
            {
                if (finalDist <= 0)
                {
                    return (null, $"Invalid final distance '{posTokens[1]}'. Must be a positive number (nm)");
                }

                return (
                    new SpawnRequest
                    {
                        Rules = rules,
                        Weight = weight,
                        Engine = engine,
                        PositionType = SpawnPositionType.OnFinal,
                        RunwayId = runwayId,
                        FinalDistanceNm = finalDist,
                        ExplicitType = explicitType,
                        ExplicitAirline = explicitAirline,
                    },
                    null
                );
            }

            return (
                new SpawnRequest
                {
                    Rules = rules,
                    Weight = weight,
                    Engine = engine,
                    PositionType = SpawnPositionType.Runway,
                    RunwayId = runwayId,
                    Route = posTokens[1].ToUpperInvariant().Replace('.', ' '),
                    ExplicitType = explicitType,
                    ExplicitAirline = explicitAirline,
                },
                null
            );
        }

        return (null, "Too many position arguments for runway variant");
    }

    private static (SpawnRequest? Request, string? Error) ParseOnStarVariant(
        string[] posTokens,
        FlightRulesKind rules,
        WeightClass weight,
        EngineKind engine
    )
    {
        if (rules != FlightRulesKind.Ifr)
        {
            return (null, "Arrival-on-STAR spawn requires IFR (descend-via and STARs are IFR procedures)");
        }

        var routeParts = posTokens[0].Split('.');
        if (
            routeParts.Length is < 2 or > 3
            || string.IsNullOrEmpty(routeParts[0])
            || string.IsNullOrEmpty(routeParts[1])
            || (routeParts.Length == 3 && string.IsNullOrEmpty(routeParts[2]))
        )
        {
            return (null, $"Invalid arrival route '{posTokens[0]}'. Use WAYPOINT.STAR[.RUNWAY] (e.g. TBARR.TBARR4.34R)");
        }

        var entryFix = routeParts[0].ToUpperInvariant();
        var starId = routeParts[1].ToUpperInvariant();
        string? runway = routeParts.Length == 3 ? routeParts[2].ToUpperInvariant() : null;

        bool descendVia = true;
        double? altitude = null;
        double? speed = null;
        string? airport = null;
        string? explicitType = null;
        string? explicitAirline = null;

        for (int i = 1; i < posTokens.Length; i++)
        {
            var token = posTokens[i];
            var upper = token.ToUpperInvariant();

            if (upper == "LVL")
            {
                descendVia = false;
            }
            else if (upper.StartsWith("SP") && upper.Length > 2 && upper[2..].All(char.IsDigit))
            {
                if (speed is not null)
                {
                    return (null, $"Duplicate speed override '{token}'");
                }

                speed = int.Parse(upper[2..]);
            }
            else if (token.StartsWith('*') && token.Length > 1)
            {
                explicitAirline = token[1..].ToUpperInvariant();
            }
            else if (token.All(char.IsDigit))
            {
                if (altitude is not null)
                {
                    return (null, $"Unexpected numeric token '{token}' (use SP### for a speed override)");
                }

                var resolved = AltitudeResolver.Resolve(token);
                if (resolved is null)
                {
                    return (null, $"Invalid altitude '{token}'");
                }

                altitude = resolved.Value;
            }
            else if (IsLikelyAircraftType(token))
            {
                explicitType = upper;
            }
            else
            {
                if (airport is not null)
                {
                    return (null, $"Unexpected token '{token}'");
                }

                airport = upper;
            }
        }

        return (
            new SpawnRequest
            {
                Rules = rules,
                Weight = weight,
                Engine = engine,
                PositionType = SpawnPositionType.OnStar,
                StarEntryFix = entryFix,
                StarId = starId,
                StarRunway = runway,
                DescendVia = descendVia,
                StarAltitude = altitude,
                StarSpeedKts = speed,
                DestinationAirportId = airport,
                ExplicitType = explicitType,
                ExplicitAirline = explicitAirline,
            },
            null
        );
    }

    private static bool TryParseRules(string token, out FlightRulesKind rules)
    {
        rules = default;
        var upper = token.ToUpperInvariant();
        if (upper is "I" or "IFR")
        {
            rules = FlightRulesKind.Ifr;
            return true;
        }
        if (upper is "V" or "VFR")
        {
            rules = FlightRulesKind.Vfr;
            return true;
        }
        return false;
    }

    private static bool TryParseWeight(string token, out WeightClass weight)
    {
        weight = default;
        var upper = token.ToUpperInvariant();
        if (upper == "S")
        {
            weight = WeightClass.Small;
            return true;
        }
        if (upper == "S+")
        {
            weight = WeightClass.SmallPlus;
            return true;
        }
        if (upper == "L")
        {
            weight = WeightClass.Large;
            return true;
        }
        if (upper == "H")
        {
            weight = WeightClass.Heavy;
            return true;
        }
        return false;
    }

    private static bool TryParseEngine(string token, out EngineKind engine)
    {
        engine = default;
        var upper = token.ToUpperInvariant();
        if (upper == "P")
        {
            engine = EngineKind.Piston;
            return true;
        }
        if (upper == "T")
        {
            engine = EngineKind.Turboprop;
            return true;
        }
        if (upper == "J")
        {
            engine = EngineKind.Jet;
            return true;
        }
        if (upper == "H")
        {
            engine = EngineKind.Helicopter;
            return true;
        }
        return false;
    }

    private static string? ValidateCombo(WeightClass weight, EngineKind engine)
    {
        if (weight == WeightClass.Heavy && engine == EngineKind.Piston)
        {
            return "Invalid combo: Heavy + Piston doesn't exist";
        }
        if (weight == WeightClass.Heavy && engine == EngineKind.Turboprop)
        {
            return "Invalid combo: Heavy + Turboprop doesn't exist";
        }
        if (weight == WeightClass.Small && engine == EngineKind.Jet)
        {
            return "Invalid combo: Small + Jet doesn't exist";
        }
        if (weight == WeightClass.SmallPlus && engine == EngineKind.Piston)
        {
            return "Invalid combo: SmallPlus + Piston doesn't exist";
        }
        return null;
    }

    private static bool IsLikelyAircraftType(string token)
    {
        if (token.Length < 3 || token.Length > 4)
        {
            return false;
        }

        // ICAO type designators are 3-4 alphanumeric characters with at least one letter.
        bool hasLetter = false;
        bool hasDigit = false;
        foreach (char c in token)
        {
            if (char.IsLetter(c))
            {
                hasLetter = true;
            }
            else if (char.IsDigit(c))
            {
                hasDigit = true;
            }
            else
            {
                return false;
            }
        }

        if (!hasLetter)
        {
            return false;
        }

        // A letter+digit designator (B738, R22, H60) is unambiguous — treat it as a type without a
        // data lookup so this works before the specs DB is loaded. An all-letter code (PUMA, GAZL) is
        // a type only when the specs lookup confirms it, so airport ICAOs and fix names (KOAK, TBARR)
        // on a STAR arrival aren't misread as aircraft types.
        if (hasDigit)
        {
            return true;
        }

        return AircraftCategorization.IsKnownType(token);
    }
}
