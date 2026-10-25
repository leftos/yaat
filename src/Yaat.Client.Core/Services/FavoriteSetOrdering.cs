namespace Yaat.Client.Services;

/// <summary>
/// One row of the bar's Sets flyout, in bar order: a slot (Global, the active airport's set, the
/// active scenario's set) or a named set, loaded or not.
/// </summary>
/// <param name="Entry">The order entry: a slot token of <see cref="FavoriteSetOrdering"/> or a named set's id.</param>
/// <param name="Label">The row text.</param>
/// <param name="IsSlot">True for the Global, airport and scenario rows, which have no load toggle.</param>
/// <param name="IsLoaded">True when the row's favorites show on the bar (always true for a slot row).</param>
/// <param name="CanMoveUp">False on the top visible row.</param>
/// <param name="CanMoveDown">False on the bottom visible row.</param>
public sealed record FavoriteSetRow(string Entry, string Label, bool IsSlot, bool IsLoaded, bool CanMoveUp, bool CanMoveDown);

/// <summary>
/// The bar order of the favorite sets: one list of the slot tokens <see cref="GlobalSlot"/>,
/// <see cref="AirportSlot"/> and <see cref="ScenarioSlot"/> beside named-set ids, loaded or not, so an
/// unloaded set keeps its place. The stored order is never trusted; every reader normalizes it.
/// </summary>
public static class FavoriteSetOrdering
{
    public const string GlobalSlot = "global";
    public const string AirportSlot = "airport";
    public const string ScenarioSlot = "scenario";

    private static readonly string[] Slots = [GlobalSlot, AirportSlot, ScenarioSlot];

    /// <summary>Whether the order entry is a slot token rather than a named set's id (case-insensitive, as in <see cref="Normalize"/>).</summary>
    public static bool IsSlot(string entry) => Slots.Contains(entry, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The full bar order: the three slot tokens plus every id in <paramref name="namedSetIds"/>, each once.
    /// Tokens missing from <paramref name="order"/> go to the front (Global, airport, scenario); named sets
    /// missing from it go to the end, the loaded ones first in <paramref name="loadedSetIds"/> order, then the
    /// unloaded ones in <paramref name="namedSetIds"/> order. Unknown ids and duplicates (case-insensitive) are skipped.
    /// </summary>
    /// <param name="order">The stored order.</param>
    /// <param name="loadedSetIds">Ids of the loaded named sets, in load order.</param>
    /// <param name="namedSetIds">The store's named sets, in its OrderedSets order.</param>
    /// <returns>The normalized order, with each token and id in its canonical spelling.</returns>
    public static List<string> Normalize(IReadOnlyList<string> order, IReadOnlyList<string> loadedSetIds, IReadOnlyList<string> namedSetIds)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in Slots.Concat(namedSetIds))
        {
            known.TryAdd(id, id);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = new List<string>();
        foreach (string entry in order)
        {
            if ((entry is not null) && known.TryGetValue(entry, out string? canonical) && seen.Add(canonical))
            {
                listed.Add(canonical);
            }
        }

        List<string> result = [.. Slots.Where(slot => !seen.Contains(slot)), .. listed];
        seen.UnionWith(result);

        foreach (string id in loadedSetIds.Concat(namedSetIds))
        {
            if ((id is not null) && known.TryGetValue(id, out string? canonical) && seen.Add(canonical))
            {
                result.Add(canonical);
            }
        }

        return result;
    }
}
