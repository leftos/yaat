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
/// or above, at or below, cruise, except maintain and until, and the speed qualifiers or less, or greater, between,
/// maximum and minimum). Only a busy pilot may omit anything: the climb or descend verb, and the word runway on a runway
/// clearance.
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
        ["expedite"] = "EXPEDITE",
        ["expediting"] = "EXPEDITE",
        ["cruise"] = "CRUISE",
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

    private static readonly HashSet<string> BusyOmittableDirections = ["CLIMB", "DESCEND"];

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
    /// runway clearance (<paramref name="type"/>) the word runway unless it follows hold short or cross; it may never speak a
    /// class the canonical lacks, such as the other direction. A group-form placeholder (<c>{alt:group}</c>) counts as its
    /// capture (<c>alt</c>), since both are rendered from one value, and is refused outside the busy band.
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
        (List<string> canonicalCaptures, List<ClassEntry> canonicalClasses, _) = ReadTemplate(canonicalPattern);
        (List<string> variantCaptures, List<ClassEntry> variantClasses, bool variantSpeaksGroupForm) = ReadTemplate(
            variant.Template.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );
        if (variantSpeaksGroupForm && (band != FrequencyLoadBand.Busy))
        {
            return false;
        }

        return canonicalCaptures.SequenceEqual(variantCaptures, StringComparer.Ordinal) && ClassesHold(canonicalClasses, variantClasses, type, band);
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
        if (band != FrequencyLoadBand.Busy)
        {
            return false;
        }
        string wordClass = canonical[index].Class;
        if (BusyOmittableDirections.Contains(wordClass))
        {
            return true;
        }
        return (wordClass == "RUNWAY")
            && RunwayClearanceTypes.Contains(type)
            && ((index == 0) || (canonical[index - 1].Class is not ("HOLDSHORT" or "CROSS")));
    }

    /// <summary>Whether <paramref name="next"/> repeats <paramref name="previous"/> and so counts once with it.</summary>
    private static bool Merges(ClassEntry previous, ClassEntry next) =>
        (previous.Class == next.Class) && ((previous.Segment == next.Segment) || (next.Class == Knots));

    /// <summary>
    /// Reads a template's captures and keyword classes in order, and whether any capture carries the group-form modifier
    /// (<see cref="PilotVariant.GroupFormSuffix"/>), which is read as its plain capture.
    /// </summary>
    private static (List<string> Captures, List<ClassEntry> Classes, bool SpeaksGroupForm) ReadTemplate(IReadOnlyList<string> rawTokens)
    {
        string[] tokens = [.. rawTokens.Select(NormalizeTemplateToken).Where(token => token.Length > 0)];
        var captures = new List<string>();
        var classes = new List<ClassEntry>();
        bool speaksGroupForm = false;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            string? wordClass;
            if (token.StartsWith('{') && token.EndsWith('}'))
            {
                string name = token[1..^1];
                name = name.EndsWith("...", StringComparison.Ordinal) ? name[..^3] : name;
                if (name.EndsWith(PilotVariant.GroupFormSuffix, StringComparison.Ordinal))
                {
                    speaksGroupForm = true;
                    name = name[..^PilotVariant.GroupFormSuffix.Length];
                }

                captures.Add(name);
                continue;
            }

            if (TryMatchPhrase(tokens, i, out string? phraseClass, out int phraseLength))
            {
                wordClass = phraseClass;
                i += phraseLength - 1;
            }
            else
            {
                wordClass = TemplateWordClasses.GetValueOrDefault(token);
            }

            var entry = new ClassEntry(wordClass ?? string.Empty, captures.Count);
            if ((wordClass is not null) && ((classes.Count == 0) || !Merges(classes[^1], entry)))
            {
                classes.Add(entry);
            }
        }
        return (captures, classes, speaksGroupForm);
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
