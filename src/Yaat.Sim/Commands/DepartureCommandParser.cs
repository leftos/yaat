using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using PR = Yaat.Sim.Commands.ParseResult<Yaat.Sim.Commands.ParsedCommand>;

namespace Yaat.Sim.Commands;

internal static class DepartureCommandParser
{
    /// <summary>
    /// Parses the unified CTO modifier grammar.
    /// CTO [modifier [altitude]] | CTO [heading [altitude]] | CTO [altitude]
    /// When no lateral modifier is present, a bare number is heading (1-360),
    /// and the second number is altitude.
    /// </summary>
    internal static ParsedCommand ParseCtoArg(string? arg)
    {
        if (arg is null)
        {
            return new ClearedForTakeoffCommand(new DefaultDeparture());
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool cautionWakeTurbulence = TryStripCautionWakeTurbulence(ref tokens);
        if (tokens.Length == 0)
        {
            return new ClearedForTakeoffCommand(new DefaultDeparture()) { CautionWakeTurbulence = cautionWakeTurbulence };
        }

        var mod = tokens[0].ToUpperInvariant();
        var secondToken = tokens.Length > 1 ? tokens[1] : null;

        // Special case: TLDCT/TRDCT/DCT require a fix name (and optional altitude after)
        if (mod == "TLDCT")
        {
            return ApplyCautionWakeTurbulence(ParseCtoDct(tokens, TurnDirection.Left), cautionWakeTurbulence);
        }

        if (mod == "TRDCT")
        {
            return ApplyCautionWakeTurbulence(ParseCtoDct(tokens, TurnDirection.Right), cautionWakeTurbulence);
        }

        if (mod == "DCT")
        {
            return ApplyCautionWakeTurbulence(ParseCtoDct(tokens, null), cautionWakeTurbulence);
        }

        // Try to parse as a named modifier (MRC, MRD, RH, OC, H270, etc.)
        var departure = ParseCtoModifier(mod);
        if (departure is not null)
        {
            // For closed traffic: CTO MLT [runway] [altitude]
            if (departure is ClosedTrafficDeparture ct)
            {
                return ApplyCautionWakeTurbulence(ParseCtoClosedTraffic(ct, tokens), cautionWakeTurbulence);
            }

            int? alt = secondToken is not null ? AltitudeResolver.Resolve(secondToken) : null;
            return new ClearedForTakeoffCommand(departure, alt) { CautionWakeTurbulence = cautionWakeTurbulence };
        }

        // Bare number: first number is heading (1-360), second is altitude
        if (int.TryParse(mod, out var bareNum) && bareNum >= 1 && bareNum <= 360)
        {
            int? alt = secondToken is not null ? AltitudeResolver.Resolve(secondToken) : null;
            return new ClearedForTakeoffCommand(new FlyHeadingDeparture(new MagneticHeading(bareNum), null), alt)
            {
                CautionWakeTurbulence = cautionWakeTurbulence,
            };
        }

        // Unrecognized — treat as default
        return new ClearedForTakeoffCommand(new DefaultDeparture()) { CautionWakeTurbulence = cautionWakeTurbulence };
    }

    private static bool TryStripCautionWakeTurbulence(ref string[] tokens)
    {
        if (tokens.Length == 0 || !tokens[^1].Equals("CWT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        tokens = tokens[..^1];
        return true;
    }

    private static ParsedCommand ApplyCautionWakeTurbulence(ParsedCommand command, bool cautionWakeTurbulence) =>
        command is ClearedForTakeoffCommand cto ? cto with { CautionWakeTurbulence = cautionWakeTurbulence } : command;

    /// <summary>
    /// Parses a single CTO modifier token into a DepartureInstruction,
    /// or returns null if the token is not a recognized modifier.
    /// </summary>
    internal static DepartureInstruction? ParseCtoModifier(string mod)
    {
        // Relative turns: MRC (90R), MRD (180R), MR{N}, MLC, MLD, ML{N}
        if (mod.StartsWith("MR", StringComparison.Ordinal) && mod.Length > 2)
        {
            var suffix = mod[2..];
            return suffix switch
            {
                "C" => new RelativeTurnDeparture(90, TurnDirection.Right),
                "D" => new RelativeTurnDeparture(180, TurnDirection.Right),
                "H" => new RunwayHeadingDeparture(),
                "T" => new ClosedTrafficDeparture(PatternDirection.Right, null, null),
                _ when int.TryParse(suffix, out var deg) && deg >= 1 && deg <= 359 => new RelativeTurnDeparture(deg, TurnDirection.Right),
                _ => null,
            };
        }

        if (mod.StartsWith("ML", StringComparison.Ordinal) && mod.Length > 2)
        {
            var suffix = mod[2..];
            return suffix switch
            {
                "C" => new RelativeTurnDeparture(90, TurnDirection.Left),
                "D" => new RelativeTurnDeparture(180, TurnDirection.Left),
                "T" => new ClosedTrafficDeparture(PatternDirection.Left, null, null),
                _ when int.TryParse(suffix, out var deg) && deg >= 1 && deg <= 359 => new RelativeTurnDeparture(deg, TurnDirection.Left),
                _ => null,
            };
        }

        // Runway heading aliases
        if (mod is "MRH" or "MSO" or "RH")
        {
            return new RunwayHeadingDeparture();
        }

        // Pattern traffic aliases
        if (mod is "MRT")
        {
            return new ClosedTrafficDeparture(PatternDirection.Right, null, null);
        }
        if (mod is "MLT")
        {
            return new ClosedTrafficDeparture(PatternDirection.Left, null, null);
        }

        // On course
        if (mod is "OC")
        {
            return new OnCourseDeparture();
        }

        // Fly heading: H{N}, RH{N}, LH{N}, RT{N}, LT{N}
        if (mod.StartsWith("H", StringComparison.Ordinal) && mod.Length > 1 && int.TryParse(mod[1..], out var hHdg) && hHdg >= 1 && hHdg <= 360)
        {
            return new FlyHeadingDeparture(new MagneticHeading(hHdg), null);
        }

        // RH{digits} — must have digits to distinguish from bare RH (runway heading)
        if (mod.StartsWith("RH", StringComparison.Ordinal) && mod.Length > 2 && int.TryParse(mod[2..], out var rhHdg) && rhHdg >= 1 && rhHdg <= 360)
        {
            return new FlyHeadingDeparture(new MagneticHeading(rhHdg), TurnDirection.Right);
        }

        if (mod.StartsWith("LH", StringComparison.Ordinal) && mod.Length > 2 && int.TryParse(mod[2..], out var lhHdg) && lhHdg >= 1 && lhHdg <= 360)
        {
            return new FlyHeadingDeparture(new MagneticHeading(lhHdg), TurnDirection.Left);
        }

        if (mod.StartsWith("RT", StringComparison.Ordinal) && mod.Length > 2 && int.TryParse(mod[2..], out var rtHdg) && rtHdg >= 1 && rtHdg <= 360)
        {
            return new FlyHeadingDeparture(new MagneticHeading(rtHdg), TurnDirection.Right);
        }

        if (mod.StartsWith("LT", StringComparison.Ordinal) && mod.Length > 2 && int.TryParse(mod[2..], out var ltHdg) && ltHdg >= 1 && ltHdg <= 360)
        {
            return new FlyHeadingDeparture(new MagneticHeading(ltHdg), TurnDirection.Left);
        }

        return null;
    }

    /// <summary>
    /// Parses CTO closed traffic with optional runway and altitude.
    /// Forms: CTO MLT, CTO MLT 28R, CTO MLT 15, CTO MLT 28R 15.
    /// </summary>
    private static ParsedCommand ParseCtoClosedTraffic(ClosedTrafficDeparture ct, string[] tokens)
    {
        // tokens[0] = "MLT"/"MRT", rest are runway and/or altitude
        string? runwayId = null;
        int? patternAlt = null;

        for (int i = 1; i < tokens.Length; i++)
        {
            if (runwayId is null && CommandParser.IsRunwayDesignator(tokens[i]))
            {
                runwayId = tokens[i].ToUpperInvariant();
            }
            else
            {
                var resolved = AltitudeResolver.Resolve(tokens[i]);
                if (resolved is not null)
                {
                    patternAlt = resolved;
                }
                else
                {
                    runwayId = tokens[i].ToUpperInvariant();
                }
            }
        }

        return new ClearedForTakeoffCommand(ct with { RunwayId = runwayId, PatternAltitude = patternAlt });
    }

    /// <summary>
    /// Parses CTO DCT/TLDCT/TRDCT {fix} [altitude].
    /// </summary>
    internal static ParsedCommand ParseCtoDct(string[] tokens, TurnDirection? direction)
    {
        // tokens[0] = "DCT"/"TLDCT"/"TRDCT", tokens[1] = fix name, tokens[2] = optional altitude
        if (tokens.Length < 2)
        {
            return new ClearedForTakeoffCommand(new DefaultDeparture());
        }

        var navDb = NavigationDatabase.Instance;
        var fixName = tokens[1].ToUpperInvariant();
        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            var frd = FrdResolver.Resolve(fixName, navDb);
            if (frd is null)
            {
                return new ClearedForTakeoffCommand(new DefaultDeparture());
            }
            pos = (frd.Value.Lat, frd.Value.Lon);
        }

        int? alt = tokens.Length > 2 ? AltitudeResolver.Resolve(tokens[2]) : null;
        return new ClearedForTakeoffCommand(new DirectFixDeparture(fixName, pos.Value.Lat, pos.Value.Lon, direction), alt);
    }

    /// <summary>
    /// Parses CTOPP modifier grammar — same shape as CTO but rejects runway-only forms
    /// (RH/MRH/MSO/MLT/MRT and relative turns) since vertical liftoff has no runway.
    /// Supported: bare, heading, LT/RT heading, OC, DCT/TLDCT/TRDCT fix, all with optional altitude.
    /// </summary>
    internal static PR ParseCtoppArg(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new ClearedTakeoffPresentCommand(new DefaultDeparture()));
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Ok(new ClearedTakeoffPresentCommand(new DefaultDeparture()));
        }

        var mod = tokens[0].ToUpperInvariant();
        var secondToken = tokens.Length > 1 ? tokens[1] : null;

        if (mod is "TLDCT")
        {
            return ToCtopp(ParseCtoDct(tokens, TurnDirection.Left));
        }
        if (mod is "TRDCT")
        {
            return ToCtopp(ParseCtoDct(tokens, TurnDirection.Right));
        }
        if (mod is "DCT")
        {
            return ToCtopp(ParseCtoDct(tokens, null));
        }

        var departure = ParseCtoModifier(mod);

        if (departure is RunwayHeadingDeparture or ClosedTrafficDeparture or RelativeTurnDeparture)
        {
            return PR.Fail($"CTOPP does not accept '{mod}' — runway-relative modifiers are not valid for vertical liftoff");
        }

        if (departure is OnCourseDeparture)
        {
            int? alt = secondToken is not null ? AltitudeResolver.Resolve(secondToken) : null;
            return PR.Ok(new ClearedTakeoffPresentCommand(departure, alt));
        }

        if (departure is FlyHeadingDeparture fh)
        {
            int? alt = secondToken is not null ? AltitudeResolver.Resolve(secondToken) : null;
            return PR.Ok(new ClearedTakeoffPresentCommand(fh, alt));
        }

        if (int.TryParse(mod, out var bareNum) && bareNum >= 1 && bareNum <= 360)
        {
            int? alt = secondToken is not null ? AltitudeResolver.Resolve(secondToken) : null;
            return PR.Ok(new ClearedTakeoffPresentCommand(new FlyHeadingDeparture(new MagneticHeading(bareNum), null), alt));
        }

        return PR.Fail($"CTOPP does not understand '{arg}'");
    }

    private static PR ToCtopp(ParsedCommand cto) =>
        cto is ClearedForTakeoffCommand c ? PR.Ok(new ClearedTakeoffPresentCommand(c.Departure, c.AssignedAltitude)) : PR.Ok(cto);

    /// <summary>
    /// Disambiguates a single argument as runway or no-arg for ELD/ERD/EF.
    /// </summary>
    internal static PR ParsePatternRunwayEntry(string? arg, Func<string?, ParsedCommand> factory)
    {
        if (arg is null)
        {
            return PR.Ok(factory(null));
        }

        var token = arg.Trim();
        if (token.Length == 0)
        {
            return PR.Ok(factory(null));
        }

        // Treat the argument as a runway identifier
        return PR.Ok(factory(token.ToUpperInvariant()));
    }

    /// <summary>
    /// Disambiguates arguments for ELB/ERB: [runway] [distance].
    /// Single arg: runway if contains L/C/R suffix or is exactly 2 digits,
    /// distance if numeric with optional decimal. Two args: runway then distance.
    /// </summary>
    internal static PR ParsePatternBaseEntry(string? arg, bool right = false)
    {
        if (arg is null)
        {
            return PR.Ok(right ? new EnterRightBaseCommand() : new EnterLeftBaseCommand());
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 1)
        {
            var token = tokens[0].Trim();
            if (CommandParser.IsRunwayArg(token))
            {
                return PR.Ok(right ? new EnterRightBaseCommand(token.ToUpperInvariant()) : new EnterLeftBaseCommand(token.ToUpperInvariant()));
            }

            if (double.TryParse(token, out var dist) && dist > 0)
            {
                return PR.Ok(right ? new EnterRightBaseCommand(FinalDistanceNm: dist) : new EnterLeftBaseCommand(FinalDistanceNm: dist));
            }

            return PR.Fail($"invalid base entry arg '{arg}'");
        }

        if (tokens.Length == 2)
        {
            var rwy = tokens[0].Trim().ToUpperInvariant();
            if (!double.TryParse(tokens[1].Trim(), out var dist2) || dist2 <= 0)
            {
                return PR.Fail($"invalid base entry distance '{tokens[1]}'");
            }

            return PR.Ok(right ? new EnterRightBaseCommand(rwy, dist2) : new EnterLeftBaseCommand(rwy, dist2));
        }

        return PR.Fail($"invalid base entry args '{arg}'");
    }
}
