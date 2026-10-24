using Yaat.Sim.Commands;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The content a varied readback clause may never drop, add or reorder against its canonical clause. Two floors check it.
/// <para>
/// <see cref="Holds"/> compares rendered text, for the clause builders that only produce strings: the numbers and runway
/// designators (spoken numbers normalised to digits), the type keyword and altitude unit each number is spoken with, the
/// turn and altitude directions, and the hold-short and crossing instructions. It does not enforce altitude qualifiers,
/// non-numeric targets such as fix names, taxiway letters of a route, safety-critical verbs other than hold short and
/// cross, nor the readback-level rules (callsign F1, wake suffix F2, jargon F14), which sit outside one clause.
/// </para>
/// <para>
/// <see cref="HoldsTemplate"/> compares a rule's variant row with the rule's pattern by value: the same captures in the same
/// order, and the same keyword classes in the same order. The classes are the type keywords (heading, degrees, knots or
/// speed, mach, squawk, runway), the directions (left, right, climb, descend), the safety-critical verbs (hold short,
/// cross, cleared, land, takeoff, line up and wait, go around, hold position, follow or behind), and the words that change
/// a clause's meaning (expedite, expect, not, via, when able, immediately or without delay, at, the altitude qualifiers at
/// or above, at or below, cruise, VFR, except maintain and until, and the speed qualifiers or less, or greater, between,
/// maximum and minimum). Only a busy pilot may omit anything: the climb or descend verb, and the word runway on a runway
/// clearance. An expedite is the exception: in any band its readback may speak "expediting" for "expedite climb" or
/// "expedite descent", dropping the direction word after expedite, and in no band may it drop the verb before an altitude.
/// </para>
/// </summary>
public static class AviationFloor
{
    private static readonly HashSet<string> Keywords = ["heading", "degrees", "knots", "mach", "squawk", "runway"];

    /// <summary>The direction and crossing words both floors class alike.</summary>
    private static readonly Dictionary<string, string> SharedWordClasses = new(StringComparer.Ordinal)
    {
        ["left"] = "LEFT",
        ["right"] = "RIGHT",
        ["climb"] = "CLIMB",
        ["climbing"] = "CLIMB",
        ["descend"] = "DESCEND",
        ["descending"] = "DESCEND",
        ["cross"] = "CROSS",
    };

    /// <summary>The text floor's word classes: the shared words, plus "up" and "down" as bare directions.</summary>
    private static readonly Dictionary<string, string> WordClasses = new(SharedWordClasses, StringComparer.Ordinal)
    {
        ["up"] = "CLIMB",
        ["down"] = "DESCEND",
    };

    private static readonly char[] Punctuation = [',', '.', ';', ':', '!', '?', '"', '\'', '(', ')'];

    private const string Knots = "KNOTS";

    /// <summary>The template floor's word classes: the shared words, plus keywords, safety verbs and meaning-changing words.</summary>
    private static readonly Dictionary<string, string> TemplateWordClasses = new(SharedWordClasses, StringComparer.Ordinal)
    {
        ["heading"] = "HEADING",
        ["degrees"] = "DEGREES",
        ["knots"] = Knots,
        ["speed"] = Knots,
        ["mach"] = "MACH",
        ["squawk"] = "SQUAWK",
        ["runway"] = "RUNWAY",
        ["descent"] = "DESCEND",
        ["expedite"] = "EXPEDITE",
        ["expediting"] = "EXPEDITE",
        ["cruise"] = "CRUISE",
        ["vfr"] = "VFR",
        ["until"] = "UNTIL",
        ["cleared"] = "CLEARED",
        ["land"] = "LAND",
        ["landing"] = "LAND",
        ["takeoff"] = "TAKEOFF",
        ["follow"] = "FOLLOW",
        ["behind"] = "FOLLOW",
        ["expect"] = "EXPECT",
        ["not"] = "NOT",
        ["no"] = "NOT",
        ["don't"] = "NOT",
        ["via"] = "VIA",
        ["immediate"] = "IMMEDIATE",
        ["immediately"] = "IMMEDIATE",
        ["at"] = "AT",
        ["between"] = "BETWEEN",
        ["maximum"] = "MAXIMUM",
        ["minimum"] = "MINIMUM",
    };

    /// <summary>
    /// Phrases read before single words. "up to" and "down to" are the only spoken forms of "up" and "down" that name a
    /// direction; "speeding up" and "slowing down" name a speed change and carry no class (null), so the "up to" or "down to"
    /// inside them is not read as a climb or descent.
    /// </summary>
    private static readonly (string[] Words, string? Class)[] TemplatePhraseClasses =
    [
        (["line", "up", "and", "wait"], "LUAW"),
        (["at", "or", "above"], "ATORABOVE"),
        (["at", "or", "below"], "ATORBELOW"),
        (["except", "maintain"], "EXCEPTMAINTAIN"),
        (["hold", "short"], "HOLDSHORT"),
        (["hold", "position"], "HOLDPOSITION"),
        (["go", "around"], "GOAROUND"),
        (["when", "able"], "WHENABLE"),
        (["without", "delay"], "IMMEDIATE"),
        (["or", "less"], "ORLESS"),
        (["or", "greater"], "ORGREATER"),
        (["or", "more"], "ORGREATER"),
        (["speeding", "up"], null),
        (["slowing", "down"], null),
        (["up", "to"], "CLIMB"),
        (["down", "to"], "DESCEND"),
    ];

    private static readonly HashSet<string> DirectionClasses = ["CLIMB", "DESCEND"];

    /// <summary>The runway clearances whose readback a busy pilot may speak without the word runway.</summary>
    private static readonly HashSet<CanonicalCommandType> RunwayClearanceTypes =
    [
        CanonicalCommandType.ClearedForTakeoff,
        CanonicalCommandType.ClearedToLand,
        CanonicalCommandType.LineUpAndWait,
        CanonicalCommandType.TouchAndGo,
        CanonicalCommandType.StopAndGo,
        CanonicalCommandType.LowApproach,
        CanonicalCommandType.ClearedForOption,
        CanonicalCommandType.ClearedApproach,
        CanonicalCommandType.ClearedApproachStraightIn,
        CanonicalCommandType.ClearedApproachForce,
        CanonicalCommandType.ClearedVisualApproach,
        CanonicalCommandType.ClearedVisualApproachForce,
        CanonicalCommandType.PositionTurnAltitudeClearance,
        CanonicalCommandType.PositionTurnAltitudeClearanceForce,
    ];

    /// <summary>
    /// Whether <paramref name="variant"/> keeps the floor of <paramref name="canonical"/>: with both texts' spoken numbers
    /// normalised to digits, the variant's anchors equal the canonical's, the same anchors in the same order, none dropped
    /// and none added; and the altitude units the two texts speak (the phrase "flight level", the words thousand and
    /// hundred), read before normalising, are the same units in the same order. The anchors are every token starting with
    /// a digit (a number, or a runway designator with its side), the keywords heading, degrees, knots, mach, squawk and
    /// runway, the direction classes left, right, climb (climb, climbing, up) and descend (descend, descending, down), the
    /// phrase "hold short" and the word "cross".
    /// </summary>
    /// <param name="canonical">The canonical clause, as the verbatim rule renders it.</param>
    /// <param name="variant">The shortened clause that would replace it.</param>
    /// <returns>True when the variant may replace the canonical clause.</returns>
    public static bool Holds(string canonical, string variant) =>
        Anchors(canonical).SequenceEqual(Anchors(variant), StringComparer.Ordinal)
        && UnitAnchors(canonical).SequenceEqual(UnitAnchors(variant), StringComparer.Ordinal);

    /// <summary>
    /// Whether the spoken <paramref name="variant"/> keeps the floor of the rule pattern it varies, compared by value rather
    /// than by re-parsing rendered text. Both templates are read into their <c>{capture}</c> names in order and their keyword
    /// classes in order (the classes the class summary lists). The captures must be equal: none dropped, added or reordered.
    /// The classes must be equal, except that on a busy frequency the variant may omit the climb or descend verb, and on a
    /// runway clearance (<paramref name="type"/>) the word runway unless it follows hold short or cross; on an expedite it may
    /// omit only the direction right after expedite ("expediting" for "expedite descent"), in any band, and never the verb
    /// before an altitude; it may never speak a class the canonical lacks, such as the other direction. A group-form
    /// placeholder (<c>{alt:group}</c>) counts as its capture (<c>alt</c>), since both are rendered from one value, and is
    /// refused outside the busy band. The callsign
    /// placeholder (<c>{cs}</c>) is not a capture, may appear once and is followed by no capture and no class word, only a
    /// courtesy. A row that is a bare "wilco" (with or without the callsign) holds only on a busy frequency for resume normal
    /// speed and reduce to final approach speed. A row tagged with a speed-change condition holds only on the speed rule,
    /// outside the busy band, saying its own verb and "knots" right after the speed; an untagged row never says "slowing"
    /// or "speeding".
    /// <para>
    /// A class repeated next to itself counts once ("climb and maintain"), but the merge restarts at each capture, so the
    /// same class on either side of a value counts twice; knots is the exception, since "speed" and "knots" around one
    /// speed name one value. Repeats merge after omission too: once an omittable class is skipped, the next canonical class
    /// equal to the last one matched is satisfied by it. Optional pattern tokens (<c>to?</c>) count as spoken, as
    /// <c>PhraseologyVerbalizer.RenderPattern</c> speaks them.
    /// </para>
    /// </summary>
    /// <param name="canonicalPattern">The rule's pattern tokens, as <c>PhraseologyRule.Pattern</c> holds them.</param>
    /// <param name="type">The rule's command type, which decides whether the word runway may be omitted.</param>
    /// <param name="variant">The spoken variant row.</param>
    /// <param name="band">The frequency load band the row is listed under.</param>
    /// <returns>True when the variant may replace the canonical readback in <paramref name="band"/>.</returns>
    public static bool HoldsTemplate(IReadOnlyList<string> canonicalPattern, CanonicalCommandType type, PilotVariant variant, FrequencyLoadBand band)
    {
        ArgumentNullException.ThrowIfNull(canonicalPattern);
        ArgumentNullException.ThrowIfNull(variant);
        string[] variantTokens = variant.Template.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        (List<string> canonicalCaptures, List<ClassEntry> canonicalClasses, _, _) = ReadTemplate(canonicalPattern);
        (List<string> variantCaptures, List<ClassEntry> variantClasses, bool variantSpeaksGroupForm, _) = ReadTemplate(variantTokens);
        if (!RowShapeHolds(variant, variantTokens, variantSpeaksGroupForm, type, band))
        {
            return false;
        }

        if (variant.IsBareWilco)
        {
            return BareWilcoHolds(canonicalCaptures, type, band);
        }

        return canonicalCaptures.SequenceEqual(variantCaptures, StringComparer.Ordinal) && ClassesHold(canonicalClasses, variantClasses, type, band);
    }

    /// <summary>
    /// The checks on a row's own shape, before it is compared with the canonical: a group-form capture only in the busy band,
    /// the callsign placed at most once with only a courtesy after it, and a speed-change row only as its tag allows.
    /// </summary>
    private static bool RowShapeHolds(
        PilotVariant variant,
        string[] rawTokens,
        bool speaksGroupForm,
        CanonicalCommandType type,
        FrequencyLoadBand band
    )
    {
        string[] tokens = [.. rawTokens.Select(NormalizeTemplateToken).Where(token => token.Length > 0)];
        return (!speaksGroupForm || (band == FrequencyLoadBand.Busy))
            && CallsignPlacementHolds(tokens)
            && SpeedChangeTagsHold(variant, tokens, type, band);
    }

    /// <summary>
    /// The instructions a busy pilot may read back with a bare "wilco": no number to verify (7110.65 2-4-3 NOTE 1.a). Delete
    /// speed restrictions is not one: it keeps its words (7110.65 5-7-4.a NOTE against 5-7-4.d).
    /// </summary>
    private static readonly HashSet<CanonicalCommandType> WilcoTypes =
    [
        CanonicalCommandType.ResumeNormalSpeed,
        CanonicalCommandType.ReduceToFinalApproachSpeed,
    ];

    /// <summary>Whether a bare "wilco" row may stand for the canonical: busy band, a wilco instruction, no value to read back.</summary>
    private static bool BareWilcoHolds(List<string> canonicalCaptures, CanonicalCommandType type, FrequencyLoadBand band) =>
        (band == FrequencyLoadBand.Busy) && WilcoTypes.Contains(type) && (canonicalCaptures.Count == 0);

    /// <summary>
    /// Whether the row places the callsign at most once, and speaks no capture and no keyword-class word after it: what follows
    /// the callsign is a courtesy ("thanks", "good day"), never part of the readback the controller checks.
    /// </summary>
    private static bool CallsignPlacementHolds(string[] tokens)
    {
        int callsignAt = Array.IndexOf(tokens, PilotVariant.CallsignPlaceholder);
        if (callsignAt < 0)
        {
            return true;
        }

        string[] after = tokens[(callsignAt + 1)..];
        (List<string> captures, List<ClassEntry> classes, _, int callsigns) = ReadTemplate(after);
        return (callsigns == 0) && (captures.Count == 0) && (classes.Count == 0);
    }

    /// <summary>
    /// Whether the row's speed-change wording matches its tag (<see cref="PilotVariant.SpeedChangeTags"/>). An untagged row
    /// never says "slowing" or "speeding". A tagged row sits on the speed rule only, never in the busy band, says its own verb
    /// ("slowing", "speeding up", "maintaining") and not the others, and says "knots" right after the speed.
    /// </summary>
    private static bool SpeedChangeTagsHold(PilotVariant variant, string[] tokens, CanonicalCommandType type, FrequencyLoadBand band)
    {
        bool slowing = tokens.Contains("slowing");
        bool speeding = tokens.Contains("speeding");
        PilotVariantTags tags = variant.Tags & PilotVariant.SpeedChangeTags;
        if (tags == PilotVariantTags.None)
        {
            return !slowing && !speeding;
        }

        return (type == CanonicalCommandType.Speed)
            && (band != FrequencyLoadBand.Busy)
            && SpeedChangeVerbMatches(tags, tokens, slowing, speeding)
            && KnotsFollowSpeed(tokens);
    }

    private static bool SpeedChangeVerbMatches(PilotVariantTags tags, string[] tokens, bool slowing, bool speeding) =>
        tags switch
        {
            PilotVariantTags.SlowingOnly => slowing && !speeding,
            PilotVariantTags.SpeedingOnly => !slowing && ContainsWordPair(tokens, "speeding", "up"),
            PilotVariantTags.MaintainingOnly => !slowing && !speeding && tokens.Contains("maintaining"),
            _ => false,
        };

    private static bool ContainsWordPair(string[] tokens, string first, string second) =>
        Enumerable.Range(0, Math.Max(0, tokens.Length - 1)).Any(i => (tokens[i] == first) && (tokens[i + 1] == second));

    private static bool KnotsFollowSpeed(string[] tokens)
    {
        int speedAt = Array.IndexOf(tokens, "{spd}");
        return (speedAt >= 0) && (speedAt + 1 < tokens.Length) && (tokens[speedAt + 1] == "knots");
    }

    /// <summary>A keyword class and the number of captures read before it.</summary>
    private readonly record struct ClassEntry(string Class, int Segment);

    /// <summary>
    /// Whether <paramref name="variant"/> is <paramref name="canonical"/> with only omittable classes left out: every other
    /// class matched in order, and nothing the canonical does not have.
    /// </summary>
    private static bool ClassesHold(List<ClassEntry> canonical, List<ClassEntry> variant, CanonicalCommandType type, FrequencyLoadBand band)
    {
        int matched = 0;
        ClassEntry? lastMatched = null;
        bool omittedSinceMatch = false;
        for (int i = 0; i < canonical.Count; i++)
        {
            ClassEntry entry = canonical[i];
            if ((matched < variant.Count) && (variant[matched].Class == entry.Class))
            {
                matched++;
                lastMatched = entry;
                omittedSinceMatch = false;
            }
            else if (IsOmittable(canonical, i, type, band))
            {
                omittedSinceMatch = true;
            }
            else if (!(omittedSinceMatch && (lastMatched is { } last) && Merges(last, entry)))
            {
                return false;
            }
        }
        return matched == variant.Count;
    }

    private static bool IsOmittable(List<ClassEntry> canonical, int index, CanonicalCommandType type, FrequencyLoadBand band)
    {
        if (type == CanonicalCommandType.Expedite)
        {
            return IsExpeditedDirection(canonical, index);
        }

        if (band != FrequencyLoadBand.Busy)
        {
            return false;
        }
        string wordClass = canonical[index].Class;
        if (DirectionClasses.Contains(wordClass))
        {
            return true;
        }
        return (wordClass == "RUNWAY")
            && RunwayClearanceTypes.Contains(type)
            && ((index == 0) || (canonical[index - 1].Class is not ("HOLDSHORT" or "CROSS")));
    }

    /// <summary>
    /// Whether the class at <paramref name="index"/> is the direction word right after expedite ("expedite climb"), which
    /// "expediting" carries; the verb before an altitude ("climb and maintain") is not.
    /// </summary>
    private static bool IsExpeditedDirection(List<ClassEntry> canonical, int index) =>
        DirectionClasses.Contains(canonical[index].Class) && (index > 0) && (canonical[index - 1].Class == "EXPEDITE");

    /// <summary>Whether <paramref name="next"/> repeats <paramref name="previous"/> and so counts once with it.</summary>
    private static bool Merges(ClassEntry previous, ClassEntry next) =>
        (previous.Class == next.Class) && ((previous.Segment == next.Segment) || (next.Class == Knots));

    /// <summary>
    /// Reads a template's captures and keyword classes in order, whether any capture carries the group-form modifier
    /// (<see cref="PilotVariant.GroupFormSuffix"/>), which is read as its plain capture, and how many times it places the
    /// callsign (<see cref="PilotVariant.CallsignPlaceholder"/>), which is not a capture of the canonical.
    /// </summary>
    private static (List<string> Captures, List<ClassEntry> Classes, bool SpeaksGroupForm, int Callsigns) ReadTemplate(
        IReadOnlyList<string> rawTokens
    )
    {
        string[] tokens = [.. rawTokens.Select(NormalizeTemplateToken).Where(token => token.Length > 0)];
        var captures = new List<string>();
        var classes = new List<ClassEntry>();
        bool speaksGroupForm = false;
        int callsigns = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token == PilotVariant.CallsignPlaceholder)
            {
                callsigns++;
                continue;
            }

            if (ReadCapture(token, ref speaksGroupForm) is { } capture)
            {
                captures.Add(capture);
                continue;
            }

            string? wordClass = ReadWordClass(tokens, ref i);
            if ((wordClass is not null) && ((classes.Count == 0) || !Merges(classes[^1], new ClassEntry(wordClass, captures.Count))))
            {
                classes.Add(new ClassEntry(wordClass, captures.Count));
            }
        }
        return (captures, classes, speaksGroupForm, callsigns);
    }

    /// <summary>
    /// The capture name of a <c>{capture}</c> token (a repeat suffix <c>...</c> dropped, the group-form modifier read as its
    /// plain capture and noted in <paramref name="speaksGroupForm"/>); null for any other token.
    /// </summary>
    private static string? ReadCapture(string token, ref bool speaksGroupForm)
    {
        if (!token.StartsWith('{') || !token.EndsWith('}'))
        {
            return null;
        }

        string name = token[1..^1];
        name = name.EndsWith("...", StringComparison.Ordinal) ? name[..^3] : name;
        if (name.EndsWith(PilotVariant.GroupFormSuffix, StringComparison.Ordinal))
        {
            speaksGroupForm = true;
            name = name[..^PilotVariant.GroupFormSuffix.Length];
        }

        return name;
    }

    /// <summary>
    /// The keyword class of the phrase or word at <paramref name="index"/>, moving <paramref name="index"/> to the phrase's last
    /// word; null for a word or phrase that carries no class.
    /// </summary>
    private static string? ReadWordClass(string[] tokens, ref int index)
    {
        if (TryMatchPhrase(tokens, index, out string? phraseClass, out int phraseLength))
        {
            index += phraseLength - 1;
            return phraseClass;
        }

        return TemplateWordClasses.GetValueOrDefault(tokens[index]);
    }

    private static string NormalizeTemplateToken(string rawToken)
    {
        string token = rawToken.Trim().ToLowerInvariant();
        token = token.EndsWith('?') ? token[..^1] : token;
        return token.Trim(Punctuation);
    }

    /// <summary>
    /// Matches a phrase of <see cref="TemplatePhraseClasses"/> at <paramref name="start"/>; its class is null for a phrase
    /// that carries none.
    /// </summary>
    private static bool TryMatchPhrase(string[] tokens, int start, out string? phraseClass, out int length)
    {
        foreach ((string[] words, string? wordClass) in TemplatePhraseClasses)
        {
            if ((start + words.Length <= tokens.Length) && tokens.AsSpan(start, words.Length).SequenceEqual(words))
            {
                phraseClass = wordClass;
                length = words.Length;
                return true;
            }
        }
        phraseClass = null;
        length = 0;
        return false;
    }

    private static List<string> Anchors(string text)
    {
        string[] tokens = AtcNumberParser.NormalizeDigits(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var anchors = new List<string>();
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if ((token == "hold") && (i + 1 < tokens.Length) && (tokens[i + 1] == "short"))
            {
                anchors.Add("HOLDSHORT");
                i++;
            }
            else if (char.IsAsciiDigit(token[0]) || Keywords.Contains(token))
            {
                anchors.Add(token);
            }
            else if (WordClasses.TryGetValue(token, out string? wordClass))
            {
                anchors.Add(wordClass);
            }
        }
        return anchors;
    }

    /// <summary>
    /// The altitude units of <paramref name="text"/> in order, read from its raw words because normalising folds them into
    /// the number: "flight level" → FL, thousand → THOUSAND, hundred → HUNDRED.
    /// </summary>
    private static List<string> UnitAnchors(string text)
    {
        string[] words =
        [
            .. text.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => word.Trim(Punctuation))
                .Where(word => word.Length > 0),
        ];
        var units = new List<string>();
        for (int i = 0; i < words.Length; i++)
        {
            if ((words[i] == "flight") && (i + 1 < words.Length) && (words[i + 1] == "level"))
            {
                units.Add("FL");
                i++;
            }
            else if (words[i] == "thousand")
            {
                units.Add("THOUSAND");
            }
            else if (words[i] == "hundred")
            {
                units.Add("HUNDRED");
            }
        }
        return units;
    }
}
