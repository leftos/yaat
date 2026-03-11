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
            return (null, $"Invalid weight class '{tokens[1]}'. Use S (small), L (large), or H (heavy)");
        }

        if (!TryParseEngine(tokens[2], out var engine))
        {
            return (null, $"Invalid engine type '{tokens[2]}'. Use P (piston), T (turboprop), or J (jet)");
        }

        var comboError = ValidateCombo(weight, engine);
        if (comboError is not null)
        {
            return (null, comboError);
        }

        // Parse optional trailing overrides (type like B738, airline like *UAL)
        string? explicitType = null;
        string? explicitAirline = null;
        int positionEndIndex = tokens.Length;

        for (int i = tokens.Length - 1; i >= 3; i--)
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
            // Disambiguate: @name {number} = at-fix (altitude), @name alone or @name {non-number} = parking
            if (posTokens.Length >= 2 && double.TryParse(posTokens[1], out _))
            {
                return ParseFixVariant(posTokens, rules, weight, engine, explicitType, explicitAirline);
            }

            return ParseParkingVariant(posTokens, rules, weight, engine, explicitType, explicitAirline);
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

        if (!double.TryParse(posTokens[0][1..], out var bearing) || bearing < 0 || bearing > 360)
        {
            return (null, $"Invalid bearing '{posTokens[0]}'. Use -{{0-360}}");
        }

        if (!double.TryParse(posTokens[1], out var dist) || dist <= 0)
        {
            return (null, $"Invalid distance '{posTokens[1]}'. Must be a positive number (nm)");
        }

        if (!double.TryParse(posTokens[2], out var alt) || alt < 0)
        {
            return (null, $"Invalid altitude '{posTokens[2]}'. Must be a non-negative number (feet)");
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
                Altitude = alt,
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

        var fixId = posTokens[0][1..]; // strip '@' prefix
        if (string.IsNullOrEmpty(fixId))
        {
            return (null, "Missing fix name after '@'");
        }

        if (!double.TryParse(posTokens[1], out var alt) || alt < 0)
        {
            return (null, $"Invalid altitude '{posTokens[1]}'. Must be a non-negative number (feet)");
        }

        return (
            new SpawnRequest
            {
                Rules = rules,
                Weight = weight,
                Engine = engine,
                PositionType = SpawnPositionType.AtFix,
                FixId = fixId.ToUpperInvariant(),
                Altitude = alt,
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
            // Lined up on runway
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
            // On final at distance
            if (!double.TryParse(posTokens[1], out var finalDist) || finalDist <= 0)
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

        return (null, "Too many position arguments for runway variant");
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
        return null;
    }

    private static bool IsLikelyAircraftType(string token)
    {
        if (token.Length < 3 || token.Length > 4)
        {
            return false;
        }

        // ICAO type designators are 2-4 alphanumeric characters,
        // containing at least one letter and one digit
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
        return hasLetter && hasDigit;
    }
}
