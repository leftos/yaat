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

    public static string? Verbalize(ParsedCommand cmd) => Verbalize(cmd, PilotPersonality.Verbatim, FrequencyActivityLevel.Moderate);

    public static string? Verbalize(ParsedCommand cmd, PilotPersonality personality, FrequencyActivityLevel activityLevel)
    {
        // UnsupportedCommand and similar non-canonical placeholders shouldn't crash the
        // verbalizer — they just have no pilot speech to render.
        if (cmd is UnsupportedCommand)
        {
            return null;
        }

        if (cmd is ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrAbove } atOrAbove)
        {
            return $"maintain at or above {AltitudeWords(atOrAbove.Altitude)}";
        }

        if (cmd is ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrBelow } atOrBelow)
        {
            return $"maintain at or below {AltitudeWords(atOrBelow.Altitude)}";
        }

        // CrossFix AtOrAbove/AtOrBelow render directly because PickPreferredRule would
        // tiebreak by declaration order and always pick the first 2-capture rule (the bare
        // "at" form), which doesn't carry the modifier in its template. Bare "at" falls
        // through to the rule-driven path below.
        if (cmd is CrossFixCommand { AltType: CrossFixAltitudeType.AtOrAbove } cfAbove)
        {
            return $"cross {SpellFix(cfAbove.FixName)} at or above {AltitudeWords(cfAbove.Altitude)}";
        }

        if (cmd is CrossFixCommand { AltType: CrossFixAltitudeType.AtOrBelow } cfBelow)
        {
            return $"cross {SpellFix(cfBelow.FixName)} at or below {AltitudeWords(cfBelow.Altitude)}";
        }

        var canonicalType = CommandDescriber.ToCanonicalType(cmd);
        if (!RulesByType.TryGetValue(canonicalType, out var rules))
        {
            return null;
        }

        var args = ExtractArgs(cmd);
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
            SpeedCommand s => Map("spd", SpeedWords(s.Speed)),
            ExpediteCommand e => e.UntilAltitude is int alt ? Map("alt", AltitudeWords(alt)) : Empty(),
            MachCommand m => Map("mach", MachWords(m.MachNumber)),

            // Navigation
            DirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFixSequence(d.Fixes)),
            ForceDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFixSequence(d.Fixes)),
            AppendDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFixSequence(d.Fixes)),
            AppendForceDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFixSequence(d.Fixes)),
            TurnLeftDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFixSequence(d.Fixes)),
            TurnRightDirectToCommand d when d.Fixes.Count > 0 => Map("fix", SpellFixSequence(d.Fixes)),
            ExpectApproachCommand e => Map("rwy", SpellApproach(e.ApproachId)),
            JoinFinalApproachCourseCommand jfac when jfac.ApproachId is { } id => Map("rwy", SpellApproach(id)),
            CrossFixCommand cf => CrossFixArgs(cf),
            ClimbViaCommand cv when cv.Altitude is int alt => Map("alt", AltitudeWords(alt)),

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
            CrossRunwayCommand c when c.RunwayId is { } r => Map("rwy", SpellRunway(r)),
            HoldShortCommand h => Map("holdshort", SpellRunway(h.Target)),
            AssignRunwayCommand a => Map("rwy", SpellRunway(a.RunwayId)),
            TaxiCommand taxi => TaxiArgs(taxi),
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
    private static IReadOnlyDictionary<string, string> TaxiArgs(TaxiCommand taxi)
    {
        string path = SpellTaxiPath(taxi);
        if (string.IsNullOrEmpty(path))
        {
            return Empty();
        }

        var dict = new Dictionary<string, string> { ["path"] = path };
        if (taxi.DestinationRunway is { Length: > 0 } rwy)
        {
            dict["rwy"] = SpellRunway(rwy);
        }

        if (taxi.HoldShorts is { Count: > 0 } holdShorts)
        {
            dict["holdshort"] = string.Join(" and ", holdShorts.Select(h => CommandParser.IsRunwayArg(h) ? SpellRunway(h) : SpellTaxiway(h)));
        }

        if (taxi.CrossRunways is { Count: > 0 } crossRunways)
        {
            dict["crossrwy"] = string.Join(" and ", crossRunways.Select(SpellRunway));
        }

        return dict;
    }

    /// <summary>
    /// Renders a taxi path as comma-separated spoken taxiways, prefixing a hinted taxiway with its turn
    /// ("right on bravo" / "left on charlie") — the action form a pilot echoes for the controller's
    /// "make right/left turn onto &lt;taxiway&gt;" (7110.65 §3-7-2 NOTE, AIM 4-3-17). Node-reference
    /// tokens (#NNNN) are dropped — they have no spoken form.
    /// </summary>
    private static string SpellTaxiPath(TaxiCommand taxi)
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

            string spelled = SpellTaxiway(name);
            var hint = (hints is not null && i < hints.Count) ? hints[i] : null;
            parts.Add(
                hint switch
                {
                    TurnDirection.Right => $"right on {spelled}",
                    TurnDirection.Left => $"left on {spelled}",
                    _ => spelled,
                }
            );
        }

        return string.Join(", ", parts);
    }

    private static IReadOnlyDictionary<string, string> Map(string k, string v) => new Dictionary<string, string> { [k] = v };

    private static IReadOnlyDictionary<string, string> Empty() => new Dictionary<string, string>(0);

    private static IReadOnlyDictionary<string, string> CrossFixArgs(CrossFixCommand cf)
    {
        var dict = new Dictionary<string, string> { ["fix"] = SpellFix(cf.FixName), ["alt"] = AltitudeWords(cf.Altitude) };
        if (cf.Speed is int s)
        {
            dict["speed"] = SpeedWords(s);
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

            // Custom-fix friendly name (e.g. "OAK30NUM" → "Oakland Runway 30 Numbers"). Authors
            // who name their custom fix have already supplied the natural form pilots speak;
            // there's no need to require a duplicate entry in FixPronunciations.
            var customName = navDb.GetCustomFixName(trimmed);
            if (!string.IsNullOrWhiteSpace(customName))
            {
                return customName;
            }

            var navaidName = navDb.GetNavaidName(trimmed);
            if (!string.IsNullOrWhiteSpace(navaidName))
            {
                return $"{TitleCase(navaidName)} VOR";
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
