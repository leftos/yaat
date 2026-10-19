namespace Yaat.Sim.Simulation.Strips;

/// <summary>
/// One rack entry as <see cref="StripRackSort"/> sees it: the strip's id, its type and its first field (a full strip's
/// callsign, a half strip's box 1, a separator's label).
/// </summary>
public readonly record struct StripRackSortEntry(string Id, int Type, string FirstField)
{
    /// <summary>The entry for a strip with the given fields; an empty field list reads as an empty first field.</summary>
    public static StripRackSortEntry Of(string id, int type, IReadOnlyList<string> fieldValues) =>
        new(id, type, fieldValues.Count > 0 ? fieldValues[0] ?? "" : "");
}

/// <summary>
/// The order an auto-sorted rack keeps. Read top to bottom, each run of strips between two separators (or between a
/// separator and a rack end) holds tail registrations first, then airline callsigns, each A→Z, ordinal and
/// case-insensitive, ties broken on the strip id; blank strips and strips whose first field is empty sit at the bottom
/// of their run in the order they already had. Separators keep their slots. Pure, so the Sim and the client's
/// optimistic drop share it.
/// </summary>
public static class StripRackSort
{
    /// <summary>
    /// Sorts a rack given in storage order (index 0 is the visual bottom) and returns the ids in storage order.
    /// </summary>
    public static List<string> Sort(IReadOnlyList<StripRackSortEntry> rack)
    {
        var sorted = new List<string>(rack.Count);
        var segment = new List<StripRackSortEntry>();
        foreach (StripRackSortEntry entry in rack)
        {
            if (StripMutations.IsSeparator(entry.Type))
            {
                AppendSortedSegment(segment, sorted);
                segment.Clear();
                sorted.Add(entry.Id);
            }
            else
            {
                segment.Add(entry);
            }
        }

        AppendSortedSegment(segment, sorted);
        return sorted;
    }

    /// <summary>Whether a callsign is airline-shaped: exactly three letters, then a digit, after trimming. Anything else is a tail.</summary>
    public static bool IsAirlineShaped(string callsign)
    {
        string key = callsign.Trim();
        return (key.Length >= 4)
            && char.IsAsciiLetter(key[0])
            && char.IsAsciiLetter(key[1])
            && char.IsAsciiLetter(key[2])
            && char.IsAsciiDigit(key[3]);
    }

    /// <summary>
    /// Appends one run in storage order: the keyless entries first (the visual bottom) in their existing order, then the
    /// keyed ones bottom-up, which is the top-down order reversed.
    /// </summary>
    private static void AppendSortedSegment(List<StripRackSortEntry> segment, List<string> sorted)
    {
        var keyed = new List<StripRackSortEntry>(segment.Count);
        foreach (StripRackSortEntry entry in segment)
        {
            if (IsKeyless(entry))
            {
                sorted.Add(entry.Id);
            }
            else
            {
                keyed.Add(entry);
            }
        }

        keyed.Sort(CompareTopDown);
        for (int i = keyed.Count - 1; i >= 0; i--)
        {
            sorted.Add(keyed[i].Id);
        }
    }

    private static bool IsKeyless(StripRackSortEntry entry) =>
        (entry.Type == StripMutations.BlankStripType) || string.IsNullOrWhiteSpace(entry.FirstField);

    /// <summary>Negative when <paramref name="a"/> reads above <paramref name="b"/>.</summary>
    private static int CompareTopDown(StripRackSortEntry a, StripRackSortEntry b)
    {
        int group = IsAirlineShaped(a.FirstField).CompareTo(IsAirlineShaped(b.FirstField));
        if (group != 0)
        {
            return group;
        }

        int key = StringComparer.OrdinalIgnoreCase.Compare(a.FirstField.Trim(), b.FirstField.Trim());
        return key != 0 ? key : string.CompareOrdinal(a.Id, b.Id);
    }
}
