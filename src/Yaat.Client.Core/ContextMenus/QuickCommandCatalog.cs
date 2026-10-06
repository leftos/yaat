namespace Yaat.Client.ContextMenus;

/// <summary>A catalog action the Quick Commands editor can add to a situation's list.</summary>
/// <param name="Id">The catalog action's stable identifier (see <see cref="MenuIds"/>).</param>
/// <param name="Family">The identifier's prefix before its first dot ("tower", "ground", "altitude", ...), which groups the picker.</param>
/// <param name="Label">The text the catalog action shows.</param>
/// <param name="DefaultFlightRules">The flight rules the action is offered under when a list entry sets none of its own.</param>
/// <param name="Glyph">The icon the action shows in the quick-command strip, or null when it lists as text.</param>
public sealed record QuickCommandCatalogItem(string Id, string Family, string Label, MenuFlightRules DefaultFlightRules, QuickCommandGlyph? Glyph);

/// <summary>
/// The catalog actions a quick-command list may name: every <see cref="MenuCatalog.All"/> action an aircraft's own menu
/// offers, without the ones that need something a quick list cannot give them: a right-clicked point (<c>point.</c>), a
/// delayed spawn (<c>spawn.</c>), a second aircraft (<c>relative.</c>, <c>ground.relative-</c>), the aircraft list's
/// selection (<see cref="MenuIds.LiveTrafficAssumeSelected"/>), and the menu's own free-text and favorites items
/// (<see cref="MenuIds.AircraftCommand"/>, <see cref="MenuIds.AircraftNote"/>, <see cref="MenuIds.FavoritesMenu"/>).
/// </summary>
public static class QuickCommandCatalog
{
    private static readonly string[] ExcludedPrefixes = ["point.", "spawn.", "relative.", "ground.relative-"];

    private static readonly HashSet<string> ExcludedIds = new(StringComparer.Ordinal)
    {
        MenuIds.AircraftCommand,
        MenuIds.AircraftNote,
        MenuIds.FavoritesMenu,
        MenuIds.LiveTrafficAssumeSelected,
    };

    /// <summary>Every eligible catalog action, in catalog order.</summary>
    public static IReadOnlyList<QuickCommandCatalogItem> Eligible { get; } =
    [.. MenuCatalog.All.Where(entry => IsEligibleId(entry.Id)).Select(ToItem)];

    private static readonly Dictionary<string, QuickCommandCatalogItem> ById = Eligible.ToDictionary(item => item.Id, StringComparer.Ordinal);

    /// <summary>True when <paramref name="catalogId"/> names an eligible catalog action.</summary>
    public static bool IsEligible(string catalogId) => ById.ContainsKey(catalogId);

    /// <summary>The eligible action <paramref name="catalogId"/> names, or null when it names none.</summary>
    public static QuickCommandCatalogItem? Find(string catalogId) => ById.GetValueOrDefault(catalogId);

    /// <summary>The family of <paramref name="catalogId"/>: its prefix before the first dot, or the whole id when it has none.</summary>
    public static string FamilyOf(string catalogId)
    {
        int dot = catalogId.IndexOf('.', StringComparison.Ordinal);
        return (dot < 0) ? catalogId : catalogId[..dot];
    }

    private static bool IsEligibleId(string id) =>
        !ExcludedIds.Contains(id) && !ExcludedPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal));

    private static QuickCommandCatalogItem ToItem(MenuCatalogEntry entry) =>
        new(entry.Id, FamilyOf(entry.Id), entry.Label, entry.DefaultFlightRules, QuickCommandGlyphs.For(entry.Id));
}
