namespace Yaat.Sim.Speech.Context;

/// <summary>The phraseology word lists the context decoder's addressing and matching rules read.</summary>
internal static class ContextPhraseWords
{
    /// <summary>Words a prefixless callsign may directly follow.</summary>
    public static IReadOnlySet<string> Openers { get; } =
        Set(
            "tower",
            "ground",
            "approach",
            "departure",
            "center",
            "ramp",
            "clearance",
            "radio",
            "roger",
            "okay",
            "ok",
            "morning",
            "afternoon",
            "evening",
            "hello",
            "thanks",
            "sorry",
            "correction",
            "and"
        );

    /// <summary>Facility words, which name a facility and are never a near miss of another word.</summary>
    public static IReadOnlySet<string> FacilityWords { get; } = Set("tower", "ground", "approach", "departure", "center");

    /// <summary>Words that tell two otherwise equal runways or approaches apart, so they must match exactly.</summary>
    public static IReadOnlySet<string> DiscriminatorWords { get; } = Set("left", "right", "center", "ils", "localizer", "vor", "rnav");

    /// <summary>Words a bare runway form may directly follow.</summary>
    public static IReadOnlySet<string> BareRunwayLeadIns { get; } =
        Set("land", "takeoff", "cross", "crossing", "short", "of", "taxi", "for", "exit", "wait", "depart", "departure");

    /// <summary>Words a bare taxiway or gate form, or the chain of them it ends, may follow.</summary>
    public static IReadOnlySet<string> BareSurfaceLeadIns { get; } =
        Set("via", "taxi", "at", "on", "cross", "short", "of", "then", "passing", "off", "left", "right", "behind", "join", "ahead", "to", "gate");

    /// <summary>Words after a number that make it a value, so a callsign may end right before that number.</summary>
    public static IReadOnlySet<string> NumberValueFollowers { get; } =
        Set("mile", "miles", "thousand", "hundred", "knots", "degrees", "left", "right", "center", "oclock");

    /// <summary>Words after a number that make it a value, so a prefixless callsign cannot be that number.</summary>
    public static IReadOnlySet<string> PrefixlessValueFollowers { get; } = Set(["point", .. NumberValueFollowers]);

    /// <summary>Words that open a second callsign in one transmission.</summary>
    public static IReadOnlySet<string> SecondCallsignWords { get; } = Set("correction", "break", "and");

    /// <summary>Words before a callsign that make its aircraft traffic, not the addressee.</summary>
    public static IReadOnlySet<string> TrafficLeadIns { get; } =
        Set(
            "follow",
            "following",
            "behind",
            "traffic",
            "to",
            "for",
            "from",
            "with",
            "is",
            "after",
            "sight",
            "turbulence",
            "mile",
            "miles",
            "oclock",
            "report"
        );

    /// <summary>Articles that may stand between a traffic lead-in and the callsign it introduces.</summary>
    public static IReadOnlySet<string> Articles { get; } = Set("the", "a", "an");

    /// <summary>Words between an addressee and a correction that describe traffic, so the correction may amend the description.</summary>
    public static IReadOnlySet<string> TrafficDescriptionWords { get; } = Set("follow", "following", "behind", "traffic");

    /// <summary>Words that introduce a value, so the digits after them, or after the word that follows them, name no aircraft.</summary>
    public static IReadOnlySet<string> CorrectionValueWords { get; } = Set("contact", "altimeter", "wind", "frequency");

    /// <summary>Words after a number that make it a distance, time, speed or bearing rather than a runway.</summary>
    public static IReadOnlySet<string> RunwayValueFollowers { get; } =
        Set("mile", "miles", "minute", "minutes", "second", "seconds", "feet", "nautical", "dme", "knots", "degrees", "oclock");

    /// <summary>The side words of a parallel runway.</summary>
    public static IReadOnlySet<string> RunwaySides { get; } = Set("left", "right", "center");

    /// <summary>Verbs that hand off to a facility, so the facility word after them is not an opener.</summary>
    public static IReadOnlySet<string> FrequencyVerbs { get; } = Set("contact", "monitor");

    /// <summary>Words that may open a transmission's opener run, at token 0 only.</summary>
    public static IReadOnlySet<string> OpenerLeads { get; } = Set("and", "okay", "ok", "alright", "roger");

    /// <summary>The function words of a facility's name ("oakland tower", "san francisco clearance delivery"), or a facility alone.</summary>
    public static IReadOnlySet<string> FacilityFunctionWords { get; } =
        Set("tower", "ground", "approach", "departure", "center", "clearance", "delivery", "ramp", "radio", "unicom");

    /// <summary>One-word greetings.</summary>
    public static IReadOnlySet<string> Greetings { get; } = Set("morning", "afternoon", "evening", "hello", "hi", "hey");

    /// <summary>The words after "good" that make a two-word greeting.</summary>
    public static IReadOnlySet<string> GoodGreetings { get; } = Set("morning", "afternoon", "evening", "day");

    /// <summary>Words that hand off to a frequency, so an opener run that reaches one is a frequency change, not a check-in.</summary>
    public static IReadOnlySet<string> FrequencyChangeWords { get; } = Set("contact", "monitor", "change", "switch", "switching");

    /// <summary>
    /// The phrases a closing tail after a trailing callsign is made of, as letter-only words; "g'day" reads as "gday". Farewells
    /// are convention, the acknowledgements are AIM 4-2-3c.
    /// </summary>
    public static IReadOnlyList<string[]> ClosingPhrases { get; } =
    [
        ["good", "day"],
        ["good", "night"],
        ["gday"],
        ["have", "a", "good", "one"],
        ["have", "a", "good", "day"],
        ["have", "a", "good", "night"],
        ["have", "a", "good", "evening"],
        ["have", "a", "nice", "day"],
        ["have", "a", "great", "day"],
        ["thanks"],
        ["thank", "you"],
        ["thanks", "a", "lot"],
        ["thanks", "much"],
        ["cheers"],
        ["appreciate", "it"],
        ["so", "long"],
        ["see", "ya"],
        ["see", "you"],
        ["take", "care"],
        ["bye"],
        ["later"],
        ["roger"],
        ["wilco"],
        ["affirmative"],
        ["over"],
    ];

    /// <summary>The words before "traffic" that make it the object of a sighting report ("looking for that traffic").</summary>
    public static IReadOnlySet<string> SightingArticles { get; } = Set("that", "the");

    /// <summary>The objects before "in sight" in a sighting report ("got him in sight").</summary>
    public static IReadOnlySet<string> SightingObjects { get; } = Set("traffic", "it", "him", "her", "them");

    /// <summary>Words shortly before a sighting pattern that make the callsign after it traffic, not the speaker.</summary>
    public static IReadOnlySet<string> SightingBlockers { get; } =
        Set(
            "follow",
            "following",
            "behind",
            "number",
            "caution",
            "wake",
            "turbulence",
            "your",
            "report",
            "from",
            "is",
            "was",
            "thats",
            "its",
            "theres",
            "be",
            "thatll"
        );

    /// <summary>The blockers of an "in sight" pattern, which add the controller's "has you in sight" (7110.65 7-2-1a.2(d)).</summary>
    public static IReadOnlySet<string> InSightBlockers { get; } = Set([.. SightingBlockers, "has", "you"]);

    /// <summary>Words that open a noun-led entity form; a runway, taxiway or gate form without one is bare.</summary>
    public static IReadOnlySet<string> EntityNouns { get; } = Set("runway", "taxiway", "gate", "parking", "spot");

    private static HashSet<string> Set(params string[] words) => new(words, StringComparer.Ordinal);
}
