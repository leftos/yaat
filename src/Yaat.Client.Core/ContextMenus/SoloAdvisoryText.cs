using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>The four spoken forms of a solo traffic advisory (<c>RTIS</c>), told apart by the call's first word.</summary>
public enum AdvisoryForm
{
    /// <summary>Radar clock and miles: <c>2 4 W SR22 035</c>.</summary>
    Radar,

    /// <summary>Relative to the nose: <c>NR 2 C172</c>.</summary>
    Nose,

    /// <summary>A pattern leg: <c>BASE L 2 28L C152</c>, <c>FINAL 2 28R C172</c>.</summary>
    Pattern,

    /// <summary>Over a reporting point: <c>OVER VPCOL SR22</c>.</summary>
    Over,
}

/// <summary>One part of a spoken traffic advisory, each named by its kind word (<see cref="SoloAdvisoryText.KindWord"/>).</summary>
public enum AdvisoryPart
{
    Clock,
    Miles,
    Direction,
    Type,
    Altitude,
    Position,
    Leg,
    Side,
    Runway,
    Fix,
}

/// <summary>
/// What the solo advisory box holds, read word by word: the form its first word picks and each part a word fills in that form's
/// order, a word that does not fit its slot leaving the part unset; or, for a first word that starts no form, the text the list
/// is filtered by; or nothing typed.
/// </summary>
/// <param name="Words">The typed words, upper-cased.</param>
/// <param name="Form">The form the first word picks; null with nothing typed or a filter.</param>
/// <param name="Parts">The parts the words fill, each as typed (upper-cased).</param>
/// <param name="FilterText">The words the list is filtered by when the first word starts no form; else null.</param>
public sealed record AdvisoryReading(
    IReadOnlyList<string> Words,
    AdvisoryForm? Form,
    IReadOnlyDictionary<AdvisoryPart, string> Parts,
    string? FilterText
)
{
    /// <summary>Whether nothing is typed.</summary>
    public bool IsEmpty => Words.Count == 0;

    /// <summary>The form's parts in the order they are said, the optional altitude last; empty without a form.</summary>
    public IReadOnlyList<AdvisoryPart> Order => (Form is { } form) ? SoloAdvisoryText.OrderOf(form, IsFinal) : [];

    /// <summary>The parts the form needs that are still unset, in order.</summary>
    public IReadOnlyList<AdvisoryPart> Missing => [.. Order.Where(part => !SoloAdvisoryText.IsOptional(part) && !Parts.ContainsKey(part))];

    /// <summary>The first part still unset, the optional altitude included; null once every part is set, or without a form.</summary>
    public AdvisoryPart? Next => Order.Where(part => !Parts.ContainsKey(part)).Select(part => (AdvisoryPart?)part).FirstOrDefault();

    /// <summary>Whether the call names a form and every part it needs.</summary>
    public bool IsComplete => (Form is not null) && (Missing.Count == 0);

    /// <summary>Whether a pattern call names the final leg, which has no side.</summary>
    public bool IsFinal =>
        (Form == AdvisoryForm.Pattern)
        && Parts.TryGetValue(AdvisoryPart.Leg, out string? leg)
        && (SoloAdvisoryText.LegOf(leg) == PatternEntryLeg.Final);
}

/// <summary>
/// The solo advisory box's reading of what the student types (<see cref="Read"/>), the words for each part, the command preview
/// and the command Enter sends. A complete call is decided by the sim's own parser (<see cref="Parse"/>).
/// </summary>
public static class SoloAdvisoryText
{
    /// <summary>The eight directions of flight, clockwise from north.</summary>
    public static IReadOnlyList<string> Directions { get; } = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>The eight positions off the nose, clockwise from the nose.</summary>
    public static IReadOnlyList<string> Positions { get; } = ["NOSE", "NR", "R", "RR", "TAIL", "LR", "L", "NL"];

    /// <summary>The pattern legs' short words, in the order they are flown.</summary>
    public static IReadOnlyList<string> LegWords { get; } = ["UW", "XW", "DW", "BASE", "FINAL"];

    /// <summary>The verb every solo traffic advisory is sent with.</summary>
    public const string Verb = "RTIS";

    /// <summary>Reads <paramref name="typed"/> into its form and parts, its filter text, or nothing.</summary>
    public static AdvisoryReading Read(string typed)
    {
        string[] words = [.. typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => word.ToUpperInvariant())];
        if (words.Length == 0)
        {
            return new AdvisoryReading(words, null, new Dictionary<AdvisoryPart, string>(), null);
        }

        AdvisoryForm? form = FormOf(words[0]);
        if (form is null)
        {
            return new AdvisoryReading(words, null, new Dictionary<AdvisoryPart, string>(), string.Join(' ', words));
        }

        int lead = LeadWords(form.Value);
        IReadOnlyList<AdvisoryPart> slots = SlotsOf(form.Value, words);
        var parts = new Dictionary<AdvisoryPart, string>();
        for (int i = 0; i < Math.Min(slots.Count, words.Length - lead); i++)
        {
            if (Fits(slots[i], words[i + lead]))
            {
                parts[slots[i]] = words[i + lead];
            }
        }

        return new AdvisoryReading(words, form, parts, null);
    }

    /// <summary>
    /// The form <paramref name="first"/> starts: a number the radar form, a position the nose form, a leg the pattern form,
    /// <c>OVER</c> the over form.
    /// </summary>
    public static AdvisoryForm? FormOf(string first) =>
        first switch
        {
            _ when first.All(char.IsDigit) => AdvisoryForm.Radar,
            "OVER" => AdvisoryForm.Over,
            _ when LegOf(first) is not null => AdvisoryForm.Pattern,
            _ when Positions.Contains(first) => AdvisoryForm.Nose,
            _ => null,
        };

    /// <summary>The pattern leg <paramref name="word"/> names, as the sim's parser reads it; null for none.</summary>
    public static PatternEntryLeg? LegOf(string word) =>
        word switch
        {
            "UPWIND" or "UW" => PatternEntryLeg.Upwind,
            "CROSSWIND" or "CW" or "XW" => PatternEntryLeg.Crosswind,
            "DOWNWIND" or "DW" => PatternEntryLeg.Downwind,
            "BASE" => PatternEntryLeg.Base,
            "FINAL" => PatternEntryLeg.Final,
            _ => null,
        };

    /// <summary>The short word for <paramref name="leg"/>: <c>UW</c>, <c>XW</c>, <c>DW</c>, <c>BASE</c>, <c>FINAL</c>.</summary>
    public static string LegWord(PatternEntryLeg leg) => LegWords[(int)leg];

    /// <summary>
    /// <paramref name="form"/>'s parts in the order they are said: clock, miles, direction, type, altitude; position, miles,
    /// type; leg, side (not on <paramref name="isFinal"/>), miles, runway, type; reporting point, type.
    /// </summary>
    public static IReadOnlyList<AdvisoryPart> OrderOf(AdvisoryForm form, bool isFinal) =>
        form switch
        {
            AdvisoryForm.Radar => [AdvisoryPart.Clock, AdvisoryPart.Miles, AdvisoryPart.Direction, AdvisoryPart.Type, AdvisoryPart.Altitude],
            AdvisoryForm.Nose => [AdvisoryPart.Position, AdvisoryPart.Miles, AdvisoryPart.Type],
            AdvisoryForm.Pattern when isFinal => [AdvisoryPart.Leg, AdvisoryPart.Miles, AdvisoryPart.Runway, AdvisoryPart.Type],
            AdvisoryForm.Pattern => [AdvisoryPart.Leg, AdvisoryPart.Side, AdvisoryPart.Miles, AdvisoryPart.Runway, AdvisoryPart.Type],
            _ => [AdvisoryPart.Fix, AdvisoryPart.Type],
        };

    /// <summary>Whether <paramref name="part"/> may be left out: only the radar form's altitude (7110.65 2-1-21.a.5, "if known").</summary>
    public static bool IsOptional(AdvisoryPart part) => part == AdvisoryPart.Altitude;

    /// <summary>The word the box and the check line name <paramref name="part"/> by.</summary>
    public static string KindWord(AdvisoryPart part) =>
        part switch
        {
            AdvisoryPart.Clock => "clock",
            AdvisoryPart.Miles => "miles",
            AdvisoryPart.Direction => "direction",
            AdvisoryPart.Type => "type",
            AdvisoryPart.Altitude => "altitude",
            AdvisoryPart.Position => "position off the nose",
            AdvisoryPart.Leg => "leg",
            AdvisoryPart.Side => "side",
            AdvisoryPart.Runway => "runway",
            _ => "reporting point",
        };

    /// <summary>A part left unset as the preview and the check line show it: <c>‹direction›</c>.</summary>
    public static string Gap(AdvisoryPart part) => $"‹{KindWord(part)}›";

    /// <summary>
    /// The command column beside the box: the verb and the call's parts in order, an unset part as its gap
    /// (<c>RTIS 2 4 ‹direction› ‹type›</c>), the altitude only when typed; <c>RTIS ···</c> with nothing typed or a filter.
    /// </summary>
    public static string Preview(AdvisoryReading reading)
    {
        if (reading.Form is null)
        {
            return $"{Verb} {TakeoffFlyout.Unfilled}";
        }

        IEnumerable<string> parts = reading
            .Order.Where(part => !IsOptional(part) || reading.Parts.ContainsKey(part))
            .Select(part => reading.Parts.TryGetValue(part, out string? word) ? word : Gap(part));
        string lead = (reading.Form == AdvisoryForm.Over) ? "OVER " : "";
        return $"{Verb} {lead}{string.Join(' ', parts)}";
    }

    /// <summary>The command Enter sends: the verb and every typed word, upper-cased and single-spaced.</summary>
    public static string Command(AdvisoryReading reading) => $"{Verb} {string.Join(' ', reading.Words)}";

    /// <summary>
    /// The sim's own reading of the call (<see cref="CommandParser.Parse"/>), which decides whether a complete call is one the
    /// sim takes.
    /// </summary>
    public static ParseResult<ParsedCommand> Parse(AdvisoryReading reading) => CommandParser.Parse(Command(reading));

    /// <summary>
    /// The parts of <paramref name="reading"/>'s form its words have not reached yet, in the order they are said: the parts a
    /// builder may still hold for the call. Empty without a form.
    /// </summary>
    public static IReadOnlyList<AdvisoryPart> Unreached(AdvisoryReading reading) =>
        (reading.Form is { } form) ? [.. SlotsOf(form, reading.Words).Skip(reading.Words.Count - LeadWords(form))] : [];

    /// <summary>How many words open <paramref name="form"/> without filling a part: the over form's <c>OVER</c>.</summary>
    private static int LeadWords(AdvisoryForm form) => (form == AdvisoryForm.Over) ? 1 : 0;

    /// <summary>The parts the words after the lead fill, in order; a pattern call's first word says whether it has a side.</summary>
    private static IReadOnlyList<AdvisoryPart> SlotsOf(AdvisoryForm form, IReadOnlyList<string> words) =>
        OrderOf(form, (form == AdvisoryForm.Pattern) && (LegOf(words[0]) == PatternEntryLeg.Final));

    private static bool Fits(AdvisoryPart part, string word) =>
        part switch
        {
            AdvisoryPart.Clock => int.TryParse(word, out int clock) && (clock is >= 1 and <= 12),
            AdvisoryPart.Miles => int.TryParse(word, out int miles) && (miles > 0),
            AdvisoryPart.Direction => Directions.Contains(word),
            AdvisoryPart.Altitude => AltitudeResolver.Resolve(word) is not null,
            AdvisoryPart.Position => Positions.Contains(word),
            AdvisoryPart.Leg => LegOf(word) is not null,
            AdvisoryPart.Side => word is "L" or "R",
            AdvisoryPart.Runway => RunwayIdentifier.NormalizeDesignator(word).Length > 0,
            _ => word.Length > 0,
        };
}
