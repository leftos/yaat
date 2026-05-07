using System.Text;
using Yaat.Sim.Commands;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Renders an accepted <see cref="ParsedCommand"/> back into spoken-English ATC phraseology
/// by inverting the same <see cref="PhraseologyRule"/>s that <see cref="PhraseologyMapper"/>
/// uses to parse controller speech. One source of truth — adding a recognition rule on the
/// input side gives us the readback for free.
///
/// Pipeline:
/// <list type="number">
///   <item><description>Look up the preferred rule for the command's canonical type. First-declared wins
///     (textbook readback before terser variants — pilot shortcuts are opt-in via
///     <c>PilotPersonality.Varied</c> when populated).</description></item>
///   <item><description>Extract the command's capture arguments via a subtype switch. Each capture name
///     (<c>{alt}</c>, <c>{hdg}</c>, etc.) gets formatted in its spoken form for that capture
///     class — altitudes via <see cref="AtcNumberParser.AltitudeToWords"/>, headings as
///     three individual digits, runways as "two eight right", and so on.</description></item>
///   <item><description>Render the pattern: drop optional-token markers (<c>?</c>) but keep their content,
///     substitute <c>{capture}</c> placeholders, lowercase joined-by-space.</description></item>
/// </list>
///
/// Returns <see langword="null"/> when no rule maps the canonical type — admin/diagnostic
/// verbs (WARP, PAUSE, TRACK, strip ops) intentionally have no PhraseologyRule and therefore
/// produce no readback.
/// </summary>
public static class PhraseologyVerbalizer
{
    /// <summary>
    /// Cached map: preferred <see cref="PhraseologyRule"/> per <see cref="CanonicalCommandType"/>.
    /// Among rules sharing a canonical type, the verbalizer prefers (1) the fewest
    /// <c>{capture}</c> placeholders, then (2) the earliest declared. Declaration order in
    /// <see cref="PhraseologyRules"/> encodes pilot textbook preference — the first form is the
    /// canonical readback, later forms are recognition-only variants. This picks "line up and
    /// wait" over "line up and wait runway {rwy}" (fewer captures), and "maintain {spd} knots"
    /// over "reduce speed to {spd}" (same capture count, first declared).
    /// </summary>
    private static readonly Dictionary<CanonicalCommandType, PhraseologyRule> PreferredRule = BuildPreferredRule();

    private static Dictionary<CanonicalCommandType, PhraseologyRule> BuildPreferredRule()
    {
        var dict = new Dictionary<CanonicalCommandType, PhraseologyRule>();
        var indexed = PhraseologyRules.All.Select((rule, idx) => (rule, idx));
        foreach (var group in indexed.GroupBy(t => t.rule.Type))
        {
            var preferred = group.OrderBy(t => t.rule.Pattern.Count(IsCapture)).ThenBy(t => t.idx).First().rule;
            dict[group.Key] = preferred;
        }

        return dict;
    }

    private static bool IsCapture(string token) => token.StartsWith('{') && token.EndsWith('}');

    public static string? Verbalize(ParsedCommand cmd)
    {
        // UnsupportedCommand and similar non-canonical placeholders shouldn't crash the
        // verbalizer — they just have no pilot speech to render.
        if (cmd is UnsupportedCommand)
        {
            return null;
        }

        var canonicalType = CommandDescriber.ToCanonicalType(cmd);
        if (!PreferredRule.TryGetValue(canonicalType, out var rule))
        {
            return null;
        }

        var args = ExtractArgs(cmd);
        return RenderPattern(rule.Pattern, args);
    }

    /// <summary>
    /// Per-subtype extractor: pulls the command's parsed args into a capture-name → spoken-string
    /// dictionary. Capture names match the placeholders in <see cref="PhraseologyRules"/> patterns.
    /// Commands without captures return an empty dictionary; their patterns are pure literals.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExtractArgs(ParsedCommand cmd) =>
        cmd switch
        {
            // Heading
            FlyHeadingCommand h => Map("hdg", HeadingDigits(h.MagneticHeading)),
            TurnLeftCommand h => Map("hdg", HeadingDigits(h.MagneticHeading)),
            TurnRightCommand h => Map("hdg", HeadingDigits(h.MagneticHeading)),
            LeftTurnCommand r => Map("deg", DegreesWords(r.Degrees)),
            RightTurnCommand r => Map("deg", DegreesWords(r.Degrees)),

            // Altitude / speed
            ClimbMaintainCommand c => Map("alt", AltitudeWords(c.Altitude)),
            DescendMaintainCommand c => Map("alt", AltitudeWords(c.Altitude)),
            SpeedCommand s => Map("spd", DigitsWords(s.Speed)),
            ExpediteCommand e => e.UntilAltitude is int alt ? Map("alt", AltitudeWords(alt)) : Empty(),
            MachCommand m => Map("mach", MachWords(m.MachNumber)),

            // Navigation
            DirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFix(d.Fixes[0].Name)),
            ForceDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFix(d.Fixes[0].Name)),
            AppendDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFix(d.Fixes[0].Name)),
            AppendForceDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFix(d.Fixes[0].Name)),
            TurnLeftDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFix(d.Fixes[0].Name)),
            TurnRightDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFix(d.Fixes[0].Name)),
            ExpectApproachCommand e => Map("rwy", SpellApproach(e.ApproachId)),
            JoinFinalApproachCourseCommand jfac when jfac.ApproachId is { } id => Map("rwy", SpellApproach(id)),

            // Tower
            LandAndHoldShortCommand l => Map("crossrwy", SpellRunway(l.CrossingRunwayId)),

            // Pattern entries (runway-suffixed forms in the rules)
            EnterLeftDownwindCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            EnterRightDownwindCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            EnterLeftCrosswindCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            EnterRightCrosswindCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            EnterLeftBaseCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            EnterRightBaseCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            EnterFinalCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            MakeLeftTrafficCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            MakeRightTrafficCommand p when p.RunwayId is { } r => Map("rwy", SpellRunway(r)),

            // Ground
            CrossRunwayCommand c => Map("rwy", SpellRunway(c.RunwayId)),
            HoldShortCommand h => Map("holdshort", SpellRunway(h.Target)),
            AssignRunwayCommand a => Map("rwy", SpellRunway(a.RunwayId)),
            ExitTaxiwayCommand e => Map("taxiway", SpellTaxiway(e.Taxiway)),
            ExitLeftCommand e when e.Taxiway is { } t => Map("taxiway", SpellTaxiway(t)),
            ExitRightCommand e when e.Taxiway is { } t => Map("taxiway", SpellTaxiway(t)),
            FollowGroundCommand f => Map("name", CallsignParser.IcaoToSpoken(f.TargetCallsign)),
            FollowCommand f when f.TargetCallsign is not null => Map("name", CallsignParser.IcaoToSpoken(f.TargetCallsign)),

            // Transponder
            SquawkCommand s => Map("code", DigitsWords((int)s.Code, minWidth: 4)),

            // Holding
            HoldPresentPosition360Command => Empty(), // pattern uses no captures (left/right is in the rule literal)
            HoldAtFixOrbitCommand h => Map("fix", SpellFix(h.FixName)),
            HoldAtFixHoverCommand h => Map("fix", SpellFix(h.FixName)),

            // Pushback / Taxi: the variadic {path...} capture isn't extracted yet, so the
            // verbalizer falls through to the rule's literal "taxi" / "pushback" keywords with
            // no path-list filled in.
            _ => Empty(),
        };

    private static IReadOnlyDictionary<string, string> Map(string k, string v) => new Dictionary<string, string> { [k] = v };

    private static IReadOnlyDictionary<string, string> Empty() => new Dictionary<string, string>(0);

    /// <summary>
    /// Renders a <see cref="PhraseologyRule.Pattern"/> as a spoken English string.
    /// Drops trailing <c>?</c> on optional tokens but keeps the literal content.
    /// Substitutes <c>{name}</c> placeholders from <paramref name="args"/>; unknown placeholders
    /// are emitted verbatim (with braces) so the gap is visible during development.
    /// </summary>
    public static string RenderPattern(string[] pattern, IReadOnlyDictionary<string, string> args)
    {
        var sb = new StringBuilder();
        foreach (var rawToken in pattern)
        {
            var t = rawToken.EndsWith('?') ? rawToken[..^1] : rawToken;

            if (t.StartsWith('{') && t.EndsWith('}'))
            {
                var name = t[1..^1];
                if (name.EndsWith("..."))
                {
                    name = name[..^3];
                }

                var value = args.TryGetValue(name, out var v) ? v : t;
                Append(sb, value);
            }
            else
            {
                Append(sb, t);
            }
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return;
        }

        if (sb.Length > 0)
        {
            sb.Append(' ');
        }

        sb.Append(s);
    }

    // --- Spoken-form formatters ---

    /// <summary>Heading 270 → "two seven zero" (always 3 digits, ATC convention).</summary>
    public static string HeadingDigits(MagneticHeading h)
    {
        int deg = ((int)Math.Round(h.Degrees) % 360 + 360) % 360;
        return string.Join(' ', deg.ToString("D3").Select(SpellDigit));
    }

    /// <summary>Relative-turn degrees: 30 → "thirty", 90 → "ninety". Whole-tens preferred.</summary>
    public static string DegreesWords(int degrees) =>
        degrees switch
        {
            10 => "ten",
            20 => "twenty",
            30 => "thirty",
            40 => "forty",
            50 => "fifty",
            60 => "sixty",
            70 => "seventy",
            80 => "eighty",
            90 => "ninety",
            _ => DigitsWords(degrees),
        };

    /// <summary>Altitude → "five thousand" / "flight level three three zero" (delegates to <see cref="AtcNumberParser"/>).</summary>
    public static string AltitudeWords(int feet) => AtcNumberParser.AltitudeToWords(feet);

    /// <summary>Generic positive integer → digit-by-digit ("250" → "two five zero").</summary>
    public static string DigitsWords(int value, int minWidth = 0)
    {
        if (value < 0)
        {
            return value.ToString();
        }

        var s = minWidth > 0 ? value.ToString($"D{minWidth}") : value.ToString();
        return string.Join(' ', s.Select(SpellDigit));
    }

    public static string MachWords(double mach)
    {
        // 0.78 → "point seven eight"; 1.5 → "one point five".
        var integerPart = (int)Math.Floor(mach);
        var fractional = mach - integerPart;
        var fractionalDigits = ((int)Math.Round(fractional * 100)).ToString("D2");
        var sb = new StringBuilder();
        if (integerPart > 0)
        {
            sb.Append(SpellDigit((char)('0' + integerPart)));
            sb.Append(" point ");
        }
        else
        {
            sb.Append("point ");
        }

        sb.Append(string.Join(' ', fractionalDigits.Select(SpellDigit)));
        return sb.ToString();
    }

    /// <summary>
    /// Spoken radio frequency per FAA 7110.65 §2-4-16: separate digits before and after the decimal,
    /// "point" at the decimal. Truncates after the second fractional digit; drops trailing zeros so
    /// 121.5 MHz reads "one two one point five" (not "...point five zero"). Whole numbers like
    /// 369.0 read "three six nine point zero" (single zero after point).
    /// </summary>
    public static string FrequencyToWords(double mhz)
    {
        var integerPart = (int)Math.Floor(mhz);
        var fractional = mhz - integerPart;
        // 7110.65 §2-4-16: omit digits after the second decimal — truncate, don't round.
        // The 1e-9 nudge absorbs binary-fp drift (e.g. 119.6 → 119.60000000000001) so
        // 119.6 reads "point six" instead of "point five nine".
        var hundredths = (int)Math.Floor(fractional * 100 + 1e-9);
        var sb = new StringBuilder();
        sb.Append(string.Join(' ', integerPart.ToString().Select(SpellDigit)));
        sb.Append(" point ");
        if (hundredths == 0)
        {
            sb.Append("zero");
        }
        else if (hundredths % 10 == 0)
        {
            // Single non-zero tenths digit (e.g. 121.5 → "five", not "five zero")
            sb.Append(SpellDigit((char)('0' + hundredths / 10)));
        }
        else
        {
            sb.Append(string.Join(' ', hundredths.ToString("D2").Select(SpellDigit)));
        }

        return sb.ToString();
    }

    private static string SpellDigit(char c) =>
        c switch
        {
            '0' => "zero",
            '1' => "one",
            '2' => "two",
            '3' => "three",
            '4' => "four",
            '5' => "five",
            '6' => "six",
            '7' => "seven",
            '8' => "eight",
            '9' => "nine",
            _ => c.ToString(),
        };

    /// <summary>Lowercase the fix name. Real fix-name pronunciation (DUMBA, SUNOL) needs a phoneme dictionary.</summary>
    public static string SpellFix(string fix) => string.IsNullOrEmpty(fix) ? "" : fix.ToLowerInvariant();

    /// <summary>"28R" → "two eight right". Letters expand; digits spell out.</summary>
    public static string SpellRunway(string runway)
    {
        if (string.IsNullOrEmpty(runway))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var c in runway)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            switch (c)
            {
                case 'L':
                case 'l':
                    sb.Append("left");
                    break;
                case 'R':
                case 'r':
                    sb.Append("right");
                    break;
                case 'C':
                case 'c':
                    sb.Append("center");
                    break;
                default:
                    sb.Append(char.IsDigit(c) ? SpellDigit(c) : char.ToLowerInvariant(c).ToString());
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Taxiway "B6" → "bravo six"; "AA" → "alpha alpha".</summary>
    public static string SpellTaxiway(string tw)
    {
        if (string.IsNullOrEmpty(tw))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var c in tw)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(char.IsDigit(c) ? SpellDigit(c) : NatoPhoneticAlphabet.SpellChar(c));
        }

        return sb.ToString();
    }

    /// <summary>Approach name like "ILS28R" → "I L S two eight right". Letters via NATO; L/R/C as words.</summary>
    public static string SpellApproach(string approachId)
    {
        if (string.IsNullOrEmpty(approachId))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var c in approachId)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            switch (c)
            {
                case 'L':
                case 'l':
                    sb.Append("left");
                    break;
                case 'R':
                case 'r':
                    sb.Append("right");
                    break;
                case 'C':
                case 'c':
                    sb.Append("center");
                    break;
                default:
                    sb.Append(char.IsDigit(c) ? SpellDigit(c) : NatoPhoneticAlphabet.SpellChar(c));
                    break;
            }
        }

        return sb.ToString();
    }
}
