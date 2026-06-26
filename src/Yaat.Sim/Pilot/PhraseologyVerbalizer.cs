using System.Text;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
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
    /// Cached map: declaration-ordered <see cref="PhraseologyRule"/> candidates per
    /// <see cref="CanonicalCommandType"/>. Rule selection happens per-call in
    /// <see cref="PickPreferredRule"/> because the right rule depends on which args the
    /// specific command instance carries — picking "enter right downwind runway {rwy}"
    /// when the command has a runway, or "enter right downwind" when it doesn't.
    ///
    /// Rules flagged <see cref="PhraseologyRule.SttOnly"/> are excluded entirely — they exist
    /// purely as acoustic-alias safety nets for STT and must never be spoken back by the
    /// pilot AI (e.g. "descent and maintain {alt}" is a recovery alias for a Whisper
    /// mistranscription of "descend"; speaking "descent and maintain" would be wrong).
    /// </summary>
    private static readonly Dictionary<CanonicalCommandType, PhraseologyRule[]> RulesByType = BuildRulesByType();

    private static Dictionary<CanonicalCommandType, PhraseologyRule[]> BuildRulesByType()
    {
        var dict = new Dictionary<CanonicalCommandType, PhraseologyRule[]>();
        foreach (var group in PhraseologyRules.All.Where(r => !r.SttOnly).GroupBy(r => r.Type))
        {
            dict[group.Key] = group.ToArray();
        }
        return dict;
    }

    /// <summary>
    /// Among the declared rules for this canonical type, pick the richest spoken form
    /// whose <c>{captures}</c> are all satisfied by <paramref name="args"/>. Among rules
    /// with equal capture counts, pick the first declared (which encodes textbook
    /// preference). Falls back to the first-declared zero-capture form when no
    /// multi-capture variant is fully satisfied.
    /// </summary>
    private static PhraseologyRule? PickPreferredRule(PhraseologyRule[] rules, IReadOnlyDictionary<string, string> args)
    {
        PhraseologyRule? best = null;
        int bestCaptures = -1;
        foreach (var rule in rules)
        {
            int captureCount = 0;
            bool allSatisfied = true;
            foreach (var token in rule.Pattern)
            {
                var t = token.EndsWith('?') ? token[..^1] : token;
                if (!IsCapture(t))
                {
                    continue;
                }
                captureCount++;
                var name = t[1..^1];
                if (name.EndsWith("..."))
                {
                    name = name[..^3];
                }
                if (!args.ContainsKey(name) || string.IsNullOrEmpty(args[name]))
                {
                    allSatisfied = false;
                    break;
                }
            }
            if (!allSatisfied)
            {
                continue;
            }
            if (captureCount > bestCaptures)
            {
                best = rule;
                bestCaptures = captureCount;
            }
        }
        return best;
    }

    private static bool IsCapture(string token) => token.StartsWith('{') && token.EndsWith('}');

    private static readonly CaptureFormatter Spoken = new SpokenCaptureFormatter();
    private static readonly CaptureFormatter Terminal = new TerminalCaptureFormatter();

    public static string? Verbalize(ParsedCommand cmd) => Verbalize(cmd, PilotPersonality.Verbatim, FrequencyActivityLevel.Moderate);

    public static string? Verbalize(ParsedCommand cmd, PilotPersonality personality, FrequencyActivityLevel activityLevel) =>
        VerbalizeCore(cmd, personality, activityLevel, Spoken);

    /// <summary>
    /// Compact controller-readable form of the same readback — runway/taxiway identifiers
    /// (<c>8R</c>, <c>B C D</c>), digit numbers (<c>270</c>, <c>5000</c>), raw fix/callsign
    /// identifiers. Shares the rule patterns and selection with <see cref="Verbalize"/>; only
    /// the per-capture token formatting differs. Used for the terminal <c>SAY</c> echo, which is
    /// built independently from the canonical command — never by stripping the spoken string.
    /// </summary>
    public static string? VerbalizeTerminal(ParsedCommand cmd) => VerbalizeTerminal(cmd, PilotPersonality.Verbatim, FrequencyActivityLevel.Moderate);

    public static string? VerbalizeTerminal(ParsedCommand cmd, PilotPersonality personality, FrequencyActivityLevel activityLevel) =>
        VerbalizeCore(cmd, personality, activityLevel, Terminal);

    private static string? VerbalizeCore(ParsedCommand cmd, PilotPersonality personality, FrequencyActivityLevel activityLevel, CaptureFormatter fmt)
    {
        // UnsupportedCommand and similar non-canonical placeholders shouldn't crash the
        // verbalizer — they just have no pilot speech to render.
        if (cmd is UnsupportedCommand)
        {
            return null;
        }

        if (cmd is ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrAbove } atOrAbove)
        {
            return $"maintain at or above {fmt.Altitude(atOrAbove.Altitude)}";
        }

        if (cmd is ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrBelow } atOrBelow)
        {
            return $"maintain at or below {fmt.Altitude(atOrBelow.Altitude)}";
        }

        // CrossFix AtOrAbove/AtOrBelow render directly because PickPreferredRule would
        // tiebreak by declaration order and always pick the first 2-capture rule (the bare
        // "at" form), which doesn't carry the modifier in its template. Bare "at" falls
        // through to the rule-driven path below.
        if (cmd is CrossFixCommand { AltType: CrossFixAltitudeType.AtOrAbove } cfAbove)
        {
            return $"cross {fmt.Fix(cfAbove.FixName)} at or above {fmt.Altitude(cfAbove.Altitude)}";
        }

        if (cmd is CrossFixCommand { AltType: CrossFixAltitudeType.AtOrBelow } cfBelow)
        {
            return $"cross {fmt.Fix(cfBelow.FixName)} at or below {fmt.Altitude(cfBelow.Altitude)}";
        }

        // A TAXI clearance naming a runway as a path segment (taxi ALONG it) needs per-segment
        // connectors — "via" for the taxiway route, "on" for the runway (7110.65 §3-7-2.a separates
        // "VIA (route)" from "ON (runway)") — which the flat "taxi via {path}" rule template can't
        // express. Render it directly.
        if (cmd is TaxiCommand alongRwy && TaxiPathHasRunway(alongRwy))
        {
            return RenderTaxiAlongRunway(alongRwy, fmt);
        }

        var canonicalType = CommandDescriber.ToCanonicalType(cmd);
        if (!RulesByType.TryGetValue(canonicalType, out var rules))
        {
            return null;
        }

        var args = ExtractArgs(cmd, fmt);
        var rule = PickPreferredRule(rules, args);
        if (rule is null)
        {
            return null;
        }

        if (ShouldUseShortcut(personality, activityLevel) && TryRenderShortestShortcut(rule, args, out var shortcut))
        {
            return shortcut;
        }

        return RenderPattern(rule.Pattern, args);
    }

    private static bool ShouldUseShortcut(PilotPersonality personality, FrequencyActivityLevel activityLevel) =>
        personality == PilotPersonality.Varied && activityLevel is FrequencyActivityLevel.Busy or FrequencyActivityLevel.Saturated;

    private static bool TryRenderShortestShortcut(
        PhraseologyRule rule,
        IReadOnlyDictionary<string, string> args,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? shortcut
    )
    {
        shortcut = null;
        if (rule.PilotShortcuts is null || rule.PilotShortcuts.Length == 0)
        {
            return false;
        }

        var candidates = rule
            .PilotShortcuts.Select(template => RenderShortcutTemplate(template, args))
            .Where(rendered => !string.IsNullOrWhiteSpace(rendered) && !ContainsUnresolvedPlaceholder(rendered))
            .OrderBy(WordCount)
            .ThenBy(s => s.Length)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        shortcut = candidates[0];
        return true;
    }

    private static string RenderShortcutTemplate(string template, IReadOnlyDictionary<string, string> args) =>
        RenderPattern(template.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), args);

    private static bool ContainsUnresolvedPlaceholder(string text) => text.Contains('{') || text.Contains('}');

    private static int WordCount(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    /// <summary>
    /// Per-subtype extractor: pulls the command's parsed args into a capture-name → spoken-string
    /// dictionary. Capture names match the placeholders in <see cref="PhraseologyRules"/> patterns.
    /// Commands without captures return an empty dictionary; their patterns are pure literals.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExtractArgs(ParsedCommand cmd, CaptureFormatter fmt) =>
        cmd switch
        {
            // Heading
            FlyHeadingCommand h => Map("hdg", fmt.Heading(h.MagneticHeading)),
            TurnLeftCommand h => Map("hdg", fmt.Heading(h.MagneticHeading)),
            TurnRightCommand h => Map("hdg", fmt.Heading(h.MagneticHeading)),
            LeftTurnCommand r => Map("deg", fmt.Degrees(r.Degrees)),
            RightTurnCommand r => Map("deg", fmt.Degrees(r.Degrees)),

            // Altitude / speed
            ClimbMaintainCommand c => Map("alt", fmt.Altitude(c.Altitude)),
            DescendMaintainCommand c => Map("alt", fmt.Altitude(c.Altitude)),
            SpeedCommand s => Map("spd", fmt.Speed(s.Speed)),
            ExpediteCommand e => e.UntilAltitude is int alt ? Map("alt", fmt.Altitude(alt)) : Empty(),
            MachCommand m => Map("mach", fmt.Mach(m.MachNumber)),

            // Navigation
            DirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            ForceDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            AppendDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            AppendForceDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            TurnLeftDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            TurnRightDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            ExpectApproachCommand e => Map("rwy", fmt.Approach(e.ApproachId)),
            JoinFinalApproachCourseCommand jfac when jfac.ApproachId is { } id => Map("rwy", fmt.Approach(id)),
            CrossFixCommand cf => CrossFixArgs(cf, fmt),
            ClimbViaCommand cv when cv.Altitude is int alt => Map("alt", fmt.Altitude(alt)),

            // Tower
            LandAndHoldShortCommand l => Map("crossrwy", fmt.Runway(l.CrossingRunwayId)),

            // Pattern entries (runway-suffixed forms in the rules)
            EnterLeftDownwindCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterRightDownwindCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterLeftCrosswindCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterRightCrosswindCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterLeftBaseCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterRightBaseCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterFinalCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            MakeLeftTrafficCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            MakeRightTrafficCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),

            // Ground
            CrossRunwayCommand c when c.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            HoldShortCommand h => Map("holdshort", fmt.Runway(h.Target)),
            AssignRunwayCommand a => Map("rwy", fmt.Runway(a.RunwayId)),
            TaxiCommand taxi => TaxiArgs(taxi, fmt),
            ExitTaxiwayCommand e => Map("taxiway", fmt.Taxiway(e.Taxiway)),
            ExitLeftCommand e when e.Taxiway is { } t => Map("taxiway", fmt.Taxiway(t)),
            ExitRightCommand e when e.Taxiway is { } t => Map("taxiway", fmt.Taxiway(t)),
            FollowGroundCommand f => Map("name", fmt.Callsign(f.TargetCallsign)),
            FollowCommand f when f.TargetCallsign is not null => Map("name", fmt.Callsign(f.TargetCallsign)),

            // Transponder
            SquawkCommand s => Map("code", fmt.Squawk((int)s.Code)),

            // Holding
            HoldPresentPosition360Command => Empty(), // pattern uses no captures (left/right is in the rule literal)
            HoldAtFixOrbitCommand h => Map("fix", fmt.Fix(h.FixName)),
            HoldAtFixHoverCommand h => Map("fix", fmt.Fix(h.FixName)),

            // Pushback's {path...} capture isn't extracted yet, so the verbalizer falls through to the
            // rule's literal "pushback" keyword with no path filled in.
            _ => Empty(),
        };

    /// <summary>
    /// Extracts the spoken-readback captures for a TAXI clearance: the route path (with turn words),
    /// the destination runway, hold-shorts, and crossings. <see cref="PickPreferredRule"/> then selects
    /// the richest matching rule, so a path-only command reads "taxi via …" while a full clearance reads
    /// "runway … taxi via … cross runway … hold short of …". An empty path (node-refs only) yields no
    /// captures, so the command produces no readback (a draw-route debug taxi isn't voiced).
    /// </summary>
    private static IReadOnlyDictionary<string, string> TaxiArgs(TaxiCommand taxi, CaptureFormatter fmt)
    {
        string path = fmt.TaxiPath(taxi);
        if (string.IsNullOrEmpty(path))
        {
            return Empty();
        }

        var dict = new Dictionary<string, string> { ["path"] = path };
        if (taxi.DestinationRunway is { Length: > 0 } rwy)
        {
            dict["rwy"] = fmt.Runway(rwy);
        }

        if (taxi.HoldShorts is { Count: > 0 } holdShorts)
        {
            dict["holdshort"] = string.Join(" and ", holdShorts.Select(h => CommandParser.IsRunwayArg(h) ? fmt.Runway(h) : fmt.Taxiway(h)));
        }

        if (taxi.CrossRunways is { Count: > 0 } crossRunways)
        {
            dict["crossrwy"] = string.Join(" and ", crossRunways.Select(fmt.Runway));
        }

        return dict;
    }

    /// <summary>
    /// Renders a taxi path's taxiways, prefixing a hinted taxiway with its turn ("right on bravo" /
    /// "left on charlie") — the action form a pilot echoes for the controller's "make right/left turn
    /// onto &lt;taxiway&gt;" (7110.65 §3-7-2 NOTE, AIM 4-3-17). Node-reference tokens (#NNNN) are
    /// dropped. The spoken form joins with ", " ("bravo, charlie, delta"); the terminal form joins
    /// with " " ("B C D"). Per-taxiway token formatting comes from <paramref name="fmt"/>.
    /// </summary>
    private static string RenderTaxiPath(TaxiCommand taxi, CaptureFormatter fmt, string separator)
    {
        var hints = taxi.PathTurnHints;
        var parts = new List<string>(taxi.Path.Count);
        for (int i = 0; i < taxi.Path.Count; i++)
        {
            string name = taxi.Path[i];
            if (name.StartsWith('#'))
            {
                continue;
            }

            var hint = (hints is not null && i < hints.Count) ? hints[i] : null;
            parts.Add(fmt.TaxiTurn(name, hint));
        }

        return string.Join(separator, parts);
    }

    private static bool TaxiPathHasRunway(TaxiCommand taxi) => taxi.Path.Any(t => !t.StartsWith('#') && CommandParser.IsRunwayArg(t));

    /// <summary>
    /// Renders a TAXI readback whose path includes a runway segment with FAA-correct connectors
    /// (7110.65 §3-7-2.a, §3-1-3.b): the taxiway route is introduced with "via", a runway taxied along
    /// is introduced with "on" — "taxi via bravo, on runway two eight right, golf, delta" (and, runway
    /// first, "taxi on runway two eight right, golf, delta"). Trailing cross/hold-short/destination-runway
    /// captures match the rule-driven forms.
    /// </summary>
    private static string RenderTaxiAlongRunway(TaxiCommand taxi, CaptureFormatter fmt)
    {
        var hints = taxi.PathTurnHints;
        var parts = new List<string>(taxi.Path.Count);
        bool firstSegment = true;
        for (int i = 0; i < taxi.Path.Count; i++)
        {
            string name = taxi.Path[i];
            if (name.StartsWith('#'))
            {
                continue;
            }

            if (CommandParser.IsRunwayArg(name))
            {
                parts.Add(fmt.RunwaySegment(name));
            }
            else
            {
                var hint = (hints is not null && i < hints.Count) ? hints[i] : null;
                string twy = fmt.TaxiTurn(name, hint);
                parts.Add(firstSegment ? $"via {twy}" : twy);
            }

            firstSegment = false;
        }

        string body = $"taxi {string.Join(fmt.TaxiSeparator, parts)}";

        if (taxi.CrossRunways is { Count: > 0 } cross)
        {
            body += $" cross runway {string.Join(" and ", cross.Select(fmt.Runway))}";
        }

        if (taxi.HoldShorts is { Count: > 0 } holdShorts)
        {
            body +=
                $" hold short of {string.Join(" and ", holdShorts.Select(h => CommandParser.IsRunwayArg(h) ? $"runway {fmt.Runway(h)}" : fmt.Taxiway(h)))}";
        }

        if (taxi.DestinationRunway is { Length: > 0 } rwy)
        {
            body = $"runway {fmt.Runway(rwy)} {body}";
        }

        return body;
    }

    private static IReadOnlyDictionary<string, string> Map(string k, string v) => new Dictionary<string, string> { [k] = v };

    private static IReadOnlyDictionary<string, string> Empty() => new Dictionary<string, string>(0);

    private static IReadOnlyDictionary<string, string> CrossFixArgs(CrossFixCommand cf, CaptureFormatter fmt)
    {
        var dict = new Dictionary<string, string> { ["fix"] = fmt.Fix(cf.FixName), ["alt"] = fmt.Altitude(cf.Altitude) };
        if (cf.Speed is int s)
        {
            dict["speed"] = fmt.Speed(s);
        }
        return dict;
    }

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

    /// <summary>Relative-turn degrees: 45 → "forty five", 270 → "two seventy".</summary>
    public static string DegreesWords(int degrees)
    {
        if (degrees is >= 1 and <= 99)
        {
            return TwoDigitWords(degrees);
        }

        if (degrees is >= 100 and <= 360)
        {
            var hundreds = degrees / 100;
            var remainder = degrees % 100;
            var leading = SpellDigit((char)('0' + hundreds));
            return remainder switch
            {
                0 => $"{leading} hundred",
                < 10 => $"{leading} zero {SpellDigit((char)('0' + remainder))}",
                _ => $"{leading} {TwoDigitWords(remainder)}",
            };
        }

        return DigitsWords(degrees);
    }

    private static string TwoDigitWords(int value) =>
        value switch
        {
            0 => "zero",
            1 => "one",
            2 => "two",
            3 => "three",
            4 => "four",
            5 => "five",
            6 => "six",
            7 => "seven",
            8 => "eight",
            9 => "nine",
            10 => "ten",
            11 => "eleven",
            12 => "twelve",
            13 => "thirteen",
            14 => "fourteen",
            15 => "fifteen",
            16 => "sixteen",
            17 => "seventeen",
            18 => "eighteen",
            19 => "nineteen",
            < 30 => "twenty" + OnesSuffix(value - 20),
            < 40 => "thirty" + OnesSuffix(value - 30),
            < 50 => "forty" + OnesSuffix(value - 40),
            < 60 => "fifty" + OnesSuffix(value - 50),
            < 70 => "sixty" + OnesSuffix(value - 60),
            < 80 => "seventy" + OnesSuffix(value - 70),
            < 90 => "eighty" + OnesSuffix(value - 80),
            < 100 => "ninety" + OnesSuffix(value - 90),
            _ => DigitsWords(value),
        };

    private static string OnesSuffix(int value) => value == 0 ? "" : " " + TwoDigitWords(value);

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

    /// <summary>
    /// Airspeed (knots) → spoken form pilots actually use on frequency. Round speeds get the
    /// colloquial American form ("two hundred", "two fifty", "two twenty") rather than the
    /// 7110.65 §2-4-15 controller-side digit-by-digit ("two zero zero", "two five zero").
    ///
    /// Rationale: this is *pilot* readback, not controller phraseology. Per project policy
    /// (project memory <c>pilot_speech_uses_ga_colloquialisms</c>), pilot transmissions use GA
    /// colloquial forms. Whisper also handles "two hundred knots" far better than "two zero zero
    /// knots" — the ouroboros harness showed the digit-by-digit form consistently mishears as
    /// "18.20" or similar.
    ///
    /// Non-round speeds (e.g. 213) fall back to digit-by-digit; speeds outside 100–399 fall back
    /// too (real pilot speeds in that range are vanishingly rare so we don't try to be clever).
    /// </summary>
    public static string SpeedWords(int speedKnots)
    {
        if (speedKnots is < 100 or >= 1000 || speedKnots % 10 != 0)
        {
            return DigitsWords(speedKnots);
        }
        var hundreds = speedKnots / 100;
        var remainder = speedKnots % 100;
        var leading = SpellDigit((char)('0' + hundreds));
        return remainder == 0 ? $"{leading} hundred" : $"{leading} {TwoDigitWords(remainder)}";
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

    /// <summary>
    /// Spoken fix label for readbacks. Published VHF navaids use the same friendly name source as
    /// SPOS position anchors ("MOD" -> "Modesto VOR"); fixes registered in
    /// <c>FixPronunciations/*.json</c> use the first published pronunciation
    /// ("VPCOL" -> "Oakland Colliseum"); ordinary intersections keep the concise identifier form
    /// ("SUNOL" -> "sunol").
    /// </summary>
    /// <summary>
    /// Spells multiple fixes for a single DCT-style command. The phraseology pattern's
    /// <c>{fix}</c> placeholder accepts one rendered string, so multi-fix DCTs are expressed by
    /// joining each fix with <c>", then direct "</c>: e.g. <c>DCT OAK30NUM VPMID</c> becomes
    /// <c>"oak30num, then direct vpmid"</c>, which the rule pattern <c>"proceed direct to {fix}"</c>
    /// then renders as <c>"proceed direct to oak30num, then direct vpmid"</c>.
    /// </summary>
    public static string SpellFixSequence(IEnumerable<ResolvedFix> fixes) =>
        string.Join(", then direct ", fixes.Select(f => SpellFix(f.Name)).Where(s => !string.IsNullOrEmpty(s)));

    public static string SpellFix(string fix)
    {
        if (string.IsNullOrWhiteSpace(fix))
        {
            return "";
        }

        var trimmed = fix.Trim();
        if (TryGetNavigationDatabase() is { } navDb)
        {
            // Custom pronunciations win over auto-derived navaid/airport names: authors register
            // them precisely to override the default speech for visual reporting points and
            // hard-to-pronounce intersections (e.g. "VPCOL" → "Oakland Colliseum").
            var pronunciations = navDb.GetFixPronunciations(trimmed);
            if (pronunciations.Count > 0 && !string.IsNullOrWhiteSpace(pronunciations[0]))
            {
                return pronunciations[0];
            }

            // Authored display name — a custom-fix friendly name (e.g. "OAK30NUM" → "Oakland Runway
            // 30 Numbers") or a visual point defined for display only. Authors who name the fix have
            // already supplied the natural form pilots speak; no duplicate pronunciation entry needed.
            var displayName = navDb.GetFixDisplayName(trimmed);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            var navaidName = navDb.GetNavaidName(trimmed);
            if (!string.IsNullOrWhiteSpace(navaidName))
            {
                return $"{TitleCase(navaidName)} {navDb.GetNavaidType(trimmed) ?? "VOR"}";
            }

            var airportName = navDb.GetAirportName(trimmed);
            if (!string.IsNullOrWhiteSpace(airportName))
            {
                return PilotSayBuilder.FriendlyAirportName(airportName);
            }
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Renders a fix for operator-facing terminal text. When the fix has an authored display name
    /// it reads as "Name (ID)" (e.g. <c>VPCBT</c> → "Lake Chabot (VPCBT)"); otherwise the bare
    /// uppercase identifier. The display name keeps its natural case — used for pilot-readback echoes
    /// where the surrounding text is sentence-style.
    /// </summary>
    public static string FixDisplayText(string fix) => FormatFixDisplay(fix, uppercaseName: false);

    /// <summary>
    /// Like <see cref="FixDisplayText"/> but uppercases the display name ("LAKE CHABOT (VPCBT)") to
    /// match the terse all-caps fix-token style of RPO command-response messages.
    /// </summary>
    public static string FixDisplayTextUpper(string fix) => FormatFixDisplay(fix, uppercaseName: true);

    private static string FormatFixDisplay(string fix, bool uppercaseName)
    {
        if (string.IsNullOrWhiteSpace(fix))
        {
            return "";
        }

        var id = fix.Trim().ToUpperInvariant();
        var displayName = TryGetNavigationDatabase()?.GetFixDisplayName(id);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return id;
        }

        return $"{(uppercaseName ? displayName.ToUpperInvariant() : displayName)} ({id})";
    }

    /// <summary>
    /// Renders a STAR/SID identifier for pilot phraseology. Terminal keeps the raw uppercase id
    /// ("RAZRR5"); TTS spells the name then the trailing version number as a cardinal word
    /// ("RAZRR5" → "razrr five", "LAURA2" → "laura two"). Used for descend-via / climb-via
    /// initial check-ins (AIM 5-4-1.b.2 / 5-2-9.b.9).
    /// </summary>
    public static (string Terminal, string Tts) ProcedureName(string procedureId)
    {
        var id = (procedureId ?? "").Trim();
        if (id.Length == 0)
        {
            return ("", "");
        }

        var terminal = id.ToUpperInvariant();

        int split = id.Length;
        while (split > 0 && char.IsDigit(id[split - 1]))
        {
            split--;
        }
        var name = id[..split];
        var version = id[split..];
        if (name.Length == 0 || version.Length == 0)
        {
            // No alpha+digit split (all digits or all letters) — spell the whole id like a fix.
            return (terminal, SpellFix(id));
        }

        var versionWords = int.TryParse(
            version,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var v
        )
            ? AtcNumberParser.CardinalWord(v)
            : version.ToLowerInvariant();
        return (terminal, $"{SpellFix(name)} {versionWords}");
    }

    /// <summary>
    /// Spoken airport name for readbacks where the caller knows the identifier refers to an
    /// airport (boundary holds tied to a Class B/C, primary-airport position anchors). Always
    /// uses the airport-name lookup — distinct from <see cref="SpellFix"/> which prefers the
    /// VOR/navaid name when the same code is published as both. Falls back to the bare
    /// identifier (NOT NATO-spelled letters) when the lookup misses, since published
    /// identifiers like "OAK" already read as a single word.
    /// </summary>
    public static string SpellAirportName(string airportIdent)
    {
        if (string.IsNullOrWhiteSpace(airportIdent))
        {
            return "";
        }

        var trimmed = airportIdent.Trim();
        if (TryGetNavigationDatabase() is { } navDb)
        {
            // GetAirportName handles ICAO ↔ FAA key reconciliation (KOAK → OAK and back).
            var airportName = navDb.GetAirportName(trimmed);
            if (!string.IsNullOrWhiteSpace(airportName))
            {
                return PilotSayBuilder.FriendlyAirportName(airportName);
            }
        }

        return trimmed.ToUpperInvariant();
    }

    private static NavigationDatabase? TryGetNavigationDatabase()
    {
        try
        {
            return NavigationDatabase.Instance;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string TitleCase(string raw) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Trim().ToLowerInvariant());

    /// <summary>"28R" → "two eight right"; "08R" → "eight right". Letters expand; digits spell out.</summary>
    public static string SpellRunway(string runway)
    {
        if (string.IsNullOrEmpty(runway))
        {
            return "";
        }

        // Runway designators are 01–36, so a leading zero is padding (NormalizeDesignator) and is
        // not spoken — "runway eight right" / "runway nine", not "runway zero eight". Only the
        // first character can be a padding zero. Headings keep their leading zero via HeadingDigits.
        if (runway.Length > 1 && runway[0] == '0')
        {
            runway = runway[1..];
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

    // --- Compact terminal-form formatters (public: reused by PilotResponder's dual-output clauses) ---

    /// <summary>"08R" → "8R", "28L" → "28L". Drops the padding leading zero, uppercases.</summary>
    public static string CompactRunway(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }

        var s = id.Trim().ToUpperInvariant();
        if (s.Length > 1 && s[0] == '0')
        {
            s = s[1..];
        }
        return s;
    }

    /// <summary>Heading 270 → "270" (3-digit, leading zeros kept — controllers write "090").</summary>
    public static string HeadingNumber(MagneticHeading h)
    {
        int deg = ((int)Math.Round(h.Degrees) % 360 + 360) % 360;
        return deg.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Altitude → "5000" / "FL350" (mirrors the FL180+ threshold of the spoken form).</summary>
    public static string CompactAltitude(int feet)
    {
        if (feet <= 0)
        {
            return "";
        }

        if (feet >= 18000 && feet % 100 == 0)
        {
            return "FL" + (feet / 100).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        }
        return feet.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Per-capture token formatting strategy. The rule pattern (literals) is shared between the
    /// spoken (TTS) and compact terminal forms; only these token renderings differ.
    /// </summary>
    private abstract class CaptureFormatter
    {
        public abstract string Runway(string id);
        public abstract string Approach(string id);
        public abstract string Heading(MagneticHeading h);
        public abstract string Degrees(int degrees);
        public abstract string Altitude(int feet);
        public abstract string Speed(int knots);
        public abstract string Mach(double mach);
        public abstract string Fix(string fix);
        public abstract string FixSequence(IEnumerable<ResolvedFix> fixes);
        public abstract string Taxiway(string tw);
        public abstract string Callsign(string callsign);
        public abstract string Squawk(int code);
        public abstract string TaxiPath(TaxiCommand taxi);
        public abstract string TaxiTurn(string taxiway, TurnDirection? hint);

        /// <summary>A runway taxied ALONG as a path segment: "on runway two eight right" / "on 28R" (7110.65 §3-7-2.a "ON (runway)").</summary>
        public abstract string RunwaySegment(string id);

        /// <summary>Separator between taxi-path segments: comma-spoken, space-terminal.</summary>
        public abstract string TaxiSeparator { get; }
    }

    private sealed class SpokenCaptureFormatter : CaptureFormatter
    {
        public override string Runway(string id) => SpellRunway(id);

        public override string Approach(string id) => SpellApproach(id);

        public override string Heading(MagneticHeading h) => HeadingDigits(h);

        public override string Degrees(int degrees) => DegreesWords(degrees);

        public override string Altitude(int feet) => AltitudeWords(feet);

        public override string Speed(int knots) => SpeedWords(knots);

        public override string Mach(double mach) => MachWords(mach);

        public override string Fix(string fix) => SpellFix(fix);

        public override string FixSequence(IEnumerable<ResolvedFix> fixes) => SpellFixSequence(fixes);

        public override string Taxiway(string tw) => SpellTaxiway(tw);

        public override string Callsign(string callsign) => CallsignParser.IcaoToSpoken(callsign);

        public override string Squawk(int code) => DigitsWords(code, minWidth: 4);

        public override string TaxiPath(TaxiCommand taxi) => RenderTaxiPath(taxi, this, ", ");

        public override string TaxiTurn(string taxiway, TurnDirection? hint) =>
            hint switch
            {
                TurnDirection.Right => $"right on {SpellTaxiway(taxiway)}",
                TurnDirection.Left => $"left on {SpellTaxiway(taxiway)}",
                _ => SpellTaxiway(taxiway),
            };

        public override string RunwaySegment(string id) => $"on runway {SpellRunway(id)}";

        public override string TaxiSeparator => ", ";
    }

    private sealed class TerminalCaptureFormatter : CaptureFormatter
    {
        public override string Runway(string id) => CompactRunway(id);

        public override string Approach(string id) => id.Trim().ToUpperInvariant();

        public override string Heading(MagneticHeading h) => HeadingNumber(h);

        public override string Degrees(int degrees) => degrees.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string Altitude(int feet) => CompactAltitude(feet);

        public override string Speed(int knots) => knots.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string Mach(double mach) => mach.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        public override string Fix(string fix) => FixDisplayText(fix);

        public override string FixSequence(IEnumerable<ResolvedFix> fixes) =>
            string.Join(", ", fixes.Select(f => FixDisplayText(f.Name)).Where(s => s.Length > 0));

        public override string Taxiway(string tw) => tw.Trim().ToUpperInvariant();

        public override string Callsign(string callsign) => callsign.Trim().ToUpperInvariant();

        public override string Squawk(int code) => code.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

        public override string TaxiPath(TaxiCommand taxi) => RenderTaxiPath(taxi, this, " ");

        public override string TaxiTurn(string taxiway, TurnDirection? hint) =>
            hint switch
            {
                TurnDirection.Right => $"right on {Taxiway(taxiway)}",
                TurnDirection.Left => $"left on {Taxiway(taxiway)}",
                _ => Taxiway(taxiway),
            };

        public override string RunwaySegment(string id) => $"on {CompactRunway(id)}";

        public override string TaxiSeparator => " ";
    }
}
