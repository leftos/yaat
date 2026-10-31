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
        Set("follow", "following", "behind", "traffic", "to", "for", "from", "with", "is", "after", "sight", "turbulence", "mile", "miles", "oclock");

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

    /// <summary>Words that open a noun-led entity form; a runway, taxiway or gate form without one is bare.</summary>
    public static IReadOnlySet<string> EntityNouns { get; } = Set("runway", "taxiway", "gate", "parking", "spot");

    private static HashSet<string> Set(params string[] words) => new(words, StringComparer.Ordinal);
}
