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
    internal static PR ParseCtoArg(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new ClearedForTakeoffCommand(new DefaultDeparture()));
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (cautionWakeTurbulence, immediate) = StripTowerModifiers(ref tokens);
        if (tokens.Length == 0)
        {
            return PR.Ok(
                new ClearedForTakeoffCommand(new DefaultDeparture()) { CautionWakeTurbulence = cautionWakeTurbulence, Immediate = immediate }
            );
        }

        var mod = tokens[0].ToUpperInvariant();

        // Special case: TLDCT/TRDCT/DCT require a fix name (and optional altitude after)
        if (mod is "TLDCT" or "TRDCT" or "DCT")
        {
            var direction = mod switch
            {
                "TLDCT" => (TurnDirection?)TurnDirection.Left,
                "TRDCT" => TurnDirection.Right,
                _ => null,
            };
            return WithModifiers(ParseCtoDct(tokens, direction), cautionWakeTurbulence, immediate);
        }

        // Try to parse as a named modifier (MRC, MRD, RH, OC, H270, etc.)
        var departure = ParseCtoModifier(mod);

        // For closed traffic: CTO MLT [runway] [altitude]
        if (departure is ClosedTrafficDeparture ct)
        {
            return WithModifiers(ParseCtoClosedTraffic(ct, tokens), cautionWakeTurbulence, immediate);
        }

        if (departure is not null)
        {
            if (!TryResolveTrailingAltitude(tokens, consumed: 1, "CTO", out var alt, out var error))
            {
                return PR.Fail(error);
            }

            return PR.Ok(new ClearedForTakeoffCommand(departure, alt) { CautionWakeTurbulence = cautionWakeTurbulence, Immediate = immediate });
        }

        // Bare number: first number is heading (1-360), second is altitude
        if (int.TryParse(mod, out var bareNum) && bareNum >= 1 && bareNum <= 360)
        {
            if (!TryResolveTrailingAltitude(tokens, consumed: 1, "CTO", out var alt, out var error))
            {
                return PR.Fail(error);
            }

            return PR.Ok(
                new ClearedForTakeoffCommand(new FlyHeadingDeparture(new MagneticHeading(bareNum), null), alt)
                {
                    CautionWakeTurbulence = cautionWakeTurbulence,
                    Immediate = immediate,
                }
            );
        }

        return PR.Fail($"CTO does not understand '{arg}'");
    }

    /// <summary>
    /// Parses the LUAW grammar: an optional trailing IMM/WD/ND modifier
    /// ("line up and wait, without delay"). Any other trailing token is rejected.
    /// </summary>
    internal static PR ParseLuawArg(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new LineUpAndWaitCommand());
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool withoutDelay = TryStripImmediate(ref tokens);
        if (tokens.Length > 0)
        {
            return PR.Fail($"LUAW does not understand '{string.Join(' ', tokens)}'");
        }

        return PR.Ok(new LineUpAndWaitCommand { WithoutDelay = withoutDelay });
    }

    /// <summary>
    /// Strips trailing CWT and IMM/WD/ND suffixes from a CTO token list in any order,
    /// returning whether each was present.
    /// </summary>
    private static (bool CautionWakeTurbulence, bool Immediate) StripTowerModifiers(ref string[] tokens)
    {
        bool cautionWakeTurbulence = false;
        bool immediate = false;
        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            if (TryStripCautionWakeTurbulence(ref tokens))
            {
                cautionWakeTurbulence = true;
                stripped = true;
            }
            if (TryStripImmediate(ref tokens))
            {
                immediate = true;
                stripped = true;
            }
        }
        return (cautionWakeTurbulence, immediate);
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

    /// <summary>
    /// Strips a trailing "immediate"/"without delay" modifier — IMM, WD, or ND
    /// (interchangeable aliases) — returning whether one was present.
    /// </summary>
    private static bool TryStripImmediate(ref string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return false;
        }

        var last = tokens[^1];
        bool match =
            last.Equals("IMM", StringComparison.OrdinalIgnoreCase)
            || last.Equals("WD", StringComparison.OrdinalIgnoreCase)
            || last.Equals("ND", StringComparison.OrdinalIgnoreCase);
        if (!match)
        {
            return false;
        }

        tokens = tokens[..^1];
        return true;
    }

    /// <summary>
    /// Applies the CWT advisory and/or the immediate modifier to a successful
    /// <see cref="ClearedForTakeoffCommand"/> result; failures and other command
    /// types pass through unchanged.
    /// </summary>
    private static PR WithModifiers(PR result, bool cautionWakeTurbulence, bool immediate)
    {
        if (result.Value is not ClearedForTakeoffCommand cto)
        {
            return result;
        }

        return PR.Ok(cto with { CautionWakeTurbulence = cto.CautionWakeTurbulence || cautionWakeTurbulence, Immediate = cto.Immediate || immediate });
    }

    /// <summary>
    /// Validates the optional trailing altitude for a CTO/CTOPP form. After the leading
    /// modifier token(s), there may be either nothing or exactly one valid altitude token.
    /// Fails on an unparseable altitude or any extra tokens so malformed input is rejected
    /// rather than silently ignored.
    /// </summary>
    private static bool TryResolveTrailingAltitude(
        string[] tokens,
        int consumed,
        string verb,
        out int? altitude,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error
    )
    {
        altitude = null;
        error = null;

        if (tokens.Length <= consumed)
        {
            return true;
        }

        if (tokens.Length > consumed + 1)
        {
            error = $"{verb} does not understand extra arguments: '{string.Join(' ', tokens[(consumed + 1)..])}'";
            return false;
        }

        var altToken = tokens[consumed];
        var resolved = AltitudeResolver.Resolve(altToken);
        if (resolved is null)
        {
            error = $"{verb} does not understand '{altToken}' — expected an altitude";
            return false;
        }

        altitude = resolved;
        return true;
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
                "C" => new PatternExitDeparture(PatternEntryLeg.Crosswind, PatternDirection.Right),
                "D" => new PatternExitDeparture(PatternEntryLeg.Downwind, PatternDirection.Right),
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
                "C" => new PatternExitDeparture(PatternEntryLeg.Crosswind, PatternDirection.Left),
                "D" => new PatternExitDeparture(PatternEntryLeg.Downwind, PatternDirection.Left),
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
    private static PR ParseCtoClosedTraffic(ClosedTrafficDeparture ct, string[] tokens)
    {
        // tokens[0] = "MLT"/"MRT", rest are runway and/or altitude
        string? runwayId = null;
        int? patternAlt = null;

        for (int i = 1; i < tokens.Length; i++)
        {
            if (runwayId is null && CommandParser.IsRunwayDesignator(tokens[i]))
            {
                runwayId = tokens[i].ToUpperInvariant();
                continue;
            }

            var resolved = AltitudeResolver.Resolve(tokens[i]);
            if (resolved is not null && patternAlt is null)
            {
                patternAlt = resolved;
                continue;
            }

            return PR.Fail($"CTO {tokens[0].ToUpperInvariant()} does not understand '{tokens[i]}'");
        }

        return PR.Ok(new ClearedForTakeoffCommand(ct with { RunwayId = runwayId, PatternAltitude = patternAlt }));
    }

    /// <summary>
    /// Parses CTO DCT/TLDCT/TRDCT {fix} [altitude].
    /// </summary>
    internal static PR ParseCtoDct(string[] tokens, TurnDirection? direction)
    {
        // tokens[0] = "DCT"/"TLDCT"/"TRDCT", tokens[1] = fix name, tokens[2] = optional altitude
        var keyword = tokens[0].ToUpperInvariant();
        if (tokens.Length < 2)
        {
            return PR.Fail($"{keyword} requires a fix name");
        }

        var navDb = NavigationDatabase.Instance;
        var fixName = tokens[1].ToUpperInvariant();
        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            var frd = FrdResolver.Resolve(fixName, navDb);
            if (frd is null)
            {
                return PR.Fail($"{keyword} does not understand '{fixName}' — unknown fix");
            }
            pos = (frd.Value.Lat, frd.Value.Lon);
        }

        if (!TryResolveTrailingAltitude(tokens, consumed: 2, keyword, out var alt, out var error))
        {
            return PR.Fail(error);
        }

        return PR.Ok(new ClearedForTakeoffCommand(new DirectFixDeparture(fixName, pos.Value.Lat, pos.Value.Lon, direction), alt));
    }

    /// <summary>
    /// Parses CTOPP modifier grammar — same shape as CTO but rejects runway-only forms
    /// (RH/MRH/MSO/MLT/MRT and relative turns) since vertical liftoff has no runway.
    /// Bare CTOPP and CTOPP +AGL hold position (vertical liftoff to a hover). All other
    /// forms (heading, LT/RT heading, OC, DCT/TLDCT/TRDCT fix, optional altitude) depart.
    /// </summary>
    internal static PR ParseCtoppArg(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new ClearedTakeoffPresentCommand(new PresentPositionHoverDeparture()));
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Ok(new ClearedTakeoffPresentCommand(new PresentPositionHoverDeparture()));
        }

        var mod = tokens[0].ToUpperInvariant();

        // Present-position AGL hover: CTOPP +001 / +002 (no airport — relative to present position).
        if (mod.StartsWith('+'))
        {
            var hoverAgl = ParsePresentPositionAgl(mod);
            if (hoverAgl is null)
            {
                return PR.Fail($"CTOPP does not understand altitude '{mod}' — use +0XX feet AGL (e.g. +001 = 100 ft)");
            }

            if (tokens.Length > 1)
            {
                return PR.Fail($"CTOPP does not understand extra arguments: '{string.Join(' ', tokens[1..])}'");
            }

            return PR.Ok(new ClearedTakeoffPresentCommand(new PresentPositionHoverDeparture(hoverAgl.Value)));
        }

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

        if (departure is RunwayHeadingDeparture or ClosedTrafficDeparture or RelativeTurnDeparture or PatternExitDeparture)
        {
            return PR.Fail($"CTOPP does not accept '{mod}' — runway-relative modifiers are not valid for vertical liftoff");
        }

        if (departure is OnCourseDeparture)
        {
            if (!TryResolveTrailingAltitude(tokens, consumed: 1, "CTOPP", out var alt, out var error))
            {
                return PR.Fail(error);
            }

            return PR.Ok(new ClearedTakeoffPresentCommand(departure, alt));
        }

        if (departure is FlyHeadingDeparture fh)
        {
            if (!TryResolveTrailingAltitude(tokens, consumed: 1, "CTOPP", out var alt, out var error))
            {
                return PR.Fail(error);
            }

            return PR.Ok(new ClearedTakeoffPresentCommand(fh, alt));
        }

        if (int.TryParse(mod, out var bareNum) && bareNum >= 1 && bareNum <= 360)
        {
            if (!TryResolveTrailingAltitude(tokens, consumed: 1, "CTOPP", out var alt, out var error))
            {
                return PR.Fail(error);
            }

            return PR.Ok(new ClearedTakeoffPresentCommand(new FlyHeadingDeparture(new MagneticHeading(bareNum), null), alt));
        }

        return PR.Fail($"CTOPP does not understand '{arg}'");
    }

    private static PR ToCtopp(PR result)
    {
        if (result.Value is not ClearedForTakeoffCommand c)
        {
            return result;
        }

        return PR.Ok(new ClearedTakeoffPresentCommand(c.Departure, c.AssignedAltitude));
    }

    /// <summary>
    /// Parses a present-position AGL token "+0XX" into feet AGL (no airport).
    /// Digits are interpreted like <see cref="AltitudeResolver"/>: under 1000 means hundreds
    /// (+001 = 100 ft, +010 = 1000 ft); 1000+ is taken literally. Returns null if malformed.
    /// </summary>
    private static int? ParsePresentPositionAgl(string token)
    {
        if (!token.StartsWith('+') || token.Length < 2)
        {
            return null;
        }

        if (!int.TryParse(token[1..], out var digits) || digits <= 0)
        {
            return null;
        }

        return digits < 1000 ? digits * 100 : digits;
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
