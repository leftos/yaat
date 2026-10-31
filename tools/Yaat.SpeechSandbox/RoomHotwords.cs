using Yaat.Sim.Speech.Context;

namespace Yaat.SpeechSandbox;

/// <summary>The hotword phrases a room's spoken forms give a recogniser.</summary>
public static class RoomHotwords
{
    private static readonly HashSet<string> NounWords = new(StringComparer.Ordinal)
    {
        "runway",
        "taxiway",
        "gate",
        "parking",
        "spot",
        "approach",
        "departure",
        "arrival",
    };

    /// <summary>
    /// The room's spoken forms as hotword phrases: every aircraft's callsign forms, then every aircraft's own entities' forms
    /// (both in room order), then the airport entities' forms. A form is kept only when it has a word other than a noun such as
    /// "runway" or "taxiway", so a bare number or NATO spelling with its noun is never boosted. Deduplicated ignoring case with the
    /// first kept, at most <paramref name="cap"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cap"/> is below 1.</exception>
    public static IReadOnlyList<string> Select(TransmissionContext context, int cap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        IEnumerable<ContextEntity> entities = context
            .Aircraft.Select(aircraft => aircraft.Callsign)
            .Concat(context.Aircraft.SelectMany(aircraft => aircraft.OwnEntities))
            .Concat(context.AirportEntities);
        return
        [
            .. entities
                .SelectMany(entity => entity.Forms)
                .Select(form => form.Text)
                .Where(HasNamingWord)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(cap),
        ];
    }

    private static bool HasNamingWord(string text) =>
        SpokenTokenizer.Tokenize(text).Any(token => (token.Kind == TokenKind.Word) && !NounWords.Contains(token.Text));
}

/// <summary>How many gold slots of one kind a context can reach, and how many it reaches with the speaker's own words.</summary>
/// <param name="Kind">The slot kind.</param>
/// <param name="Slots">The gold slots of that kind.</param>
/// <param name="Reachable">Those whose entity the context holds.</param>
/// <param name="FormMatched">Those reachable whose entity has a form that tokenizes as the slot's spoken words do.</param>
public sealed record CorpusSlotCoverage(EntityKind Kind, int Slots, int Reachable, int FormMatched);

/// <summary>Counts how much of a clip's gold a context covers.</summary>
public static class RoomCoverage
{
    /// <summary>
    /// One row per entity kind, in enum order. A slot is reachable when the context holds an entity of its kind and value (ignoring
    /// case) among the aircraft callsigns, their own entities and the airport entities, and form-matched when one of that entity's
    /// forms tokenizes to the same text as the slot's spoken words.
    /// </summary>
    public static IReadOnlyList<CorpusSlotCoverage> Count(IReadOnlyList<CorpusSlot> gold, TransmissionContext context)
    {
        ContextEntity[] entities =
        [
            .. context
                .Aircraft.Select(aircraft => aircraft.Callsign)
                .Concat(context.Aircraft.SelectMany(aircraft => aircraft.OwnEntities))
                .Concat(context.AirportEntities),
        ];
        return [.. Enum.GetValues<EntityKind>().Select(kind => Row(kind, gold, entities))];
    }

    private static CorpusSlotCoverage Row(EntityKind kind, IReadOnlyList<CorpusSlot> gold, ContextEntity[] entities)
    {
        int slots = 0;
        int reachable = 0;
        int formMatched = 0;
        foreach (CorpusSlot slot in gold.Where(slot => slot.Kind == kind))
        {
            slots++;
            ContextEntity[] matches = [.. entities.Where(entity => Names(entity, slot))];
            if (matches.Length == 0)
            {
                continue;
            }
            reachable++;
            string spoken = TokenText(slot.Spoken);
            if (matches.Any(entity => entity.Forms.Any(form => TokenText(form.Text) == spoken)))
            {
                formMatched++;
            }
        }
        return new CorpusSlotCoverage(kind, slots, reachable, formMatched);
    }

    private static bool Names(ContextEntity entity, CorpusSlot slot) =>
        (entity.Kind == slot.Kind) && string.Equals(entity.Value, slot.Value, StringComparison.OrdinalIgnoreCase);

    private static string TokenText(string text) => string.Join(" ", SpokenTokenizer.Tokenize(text).Select(token => token.Text));
}
