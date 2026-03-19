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
        if (tokens.Length == 0)
        {
            return new ClearedForTakeoffCommand(new DefaultDeparture());
        }

        var mod = tokens[0].ToUpperInvariant();
        var secondToken = tokens.Length > 1 ? tokens[1] : null;

        // Special case: DCT requires a fix name (and optional altitude after)
        if (mod == "DCT")
        {
            return ParseCtoDct(tokens);
        }

        // Try to parse as a named modifier (MRC, MRD, RH, OC, H270, etc.)
        var departure = ParseCtoModifier(mod);
        if (departure is not null)
        {
            // For closed traffic, second token is a pattern runway ID (e.g., "CTO MRT 28R")
            if (departure is ClosedTrafficDeparture ct && secondToken is not null)
            {
                return new ClearedForTakeoffCommand(ct with { RunwayId = secondToken.ToUpperInvariant() });
            }

            int? alt = secondToken is not null ? AltitudeResolver.Resolve(secondToken) : null;
            return new ClearedForTakeoffCommand(departure, alt);
        }

        // Bare number: first number is heading (1-360), second is altitude
        if (int.TryParse(mod, out var bareNum) && bareNum >= 1 && bareNum <= 360)
        {
            int? alt = secondToken is not null ? AltitudeResolver.Resolve(secondToken) : null;
            return new ClearedForTakeoffCommand(new FlyHeadingDeparture(new MagneticHeading(bareNum), null), alt);
        }

        // Unrecognized — treat as default
        return new ClearedForTakeoffCommand(new DefaultDeparture());
    }

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
                "T" => new ClosedTrafficDeparture(PatternDirection.Right),
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
                "T" => new ClosedTrafficDeparture(PatternDirection.Left),
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
            return new ClosedTrafficDeparture(PatternDirection.Right);
        }
        if (mod is "MLT")
        {
            return new ClosedTrafficDeparture(PatternDirection.Left);
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
    /// Parses CTO DCT {fix} [altitude].
    /// </summary>
    internal static ParsedCommand ParseCtoDct(string[] tokens)
    {
        // tokens[0] = "DCT", tokens[1] = fix name, tokens[2] = optional altitude
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
            pos = (frd.Latitude, frd.Longitude);
        }

        int? alt = tokens.Length > 2 ? AltitudeResolver.Resolve(tokens[2]) : null;
        return new ClearedForTakeoffCommand(new DirectFixDeparture(fixName, pos.Value.Lat, pos.Value.Lon), alt);
    }

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
