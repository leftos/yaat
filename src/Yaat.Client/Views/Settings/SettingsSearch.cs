using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Settings;

/// <summary>
/// One searchable thing in the Settings window: a bound control, found on screen by its label, or a link
/// button that opens the section a shared setting lives in.
/// </summary>
public sealed class SettingsSearchEntry
{
    /// <summary>The section the control or link sits in.</summary>
    public required SettingsSectionId Section { get; init; }

    /// <summary>
    /// What identifies the control: the view-model path it binds to, or the <c>x:Name</c> of a button the window wires
    /// up in code, or <c>Keybind.&lt;id&gt;</c> for a Keys-section row generated from the keybind descriptors. Null for a link.
    /// </summary>
    public required string? Key { get; init; }

    /// <summary>The label text exactly as the section shows it: a TextBlock's text, or a CheckBox's or Button's content.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The heading text the control sits under (a card heading, a checkbox that guards it, or an Expander's header).
    /// The control is the first with <see cref="Label"/> after that heading, which tells apart a label that repeats.
    /// </summary>
    public required string? Within { get; init; }

    /// <summary>For a link, the section it opens; null for a control.</summary>
    public required SettingsSectionId? LinkTarget { get; init; }

    /// <summary>Other words a user may search for this entry by.</summary>
    public required IReadOnlyList<string> Aliases { get; init; }

    public bool IsLink => LinkTarget is not null;
}

/// <summary>What a Settings search query matched: the entries in sidebar order and how many fell in each section.</summary>
public sealed class SettingsSearchResult
{
    private static readonly Dictionary<SettingsSectionId, int> NoCounts = [];

    private SettingsSearchResult(string query, bool isFiltered, IReadOnlyList<SettingsSearchEntry> matches)
    {
        Query = query;
        IsFiltered = isFiltered;
        Matches = matches;
        CountsBySection = isFiltered ? matches.GroupBy(m => m.Section).ToDictionary(g => g.Key, g => g.Count()) : NoCounts;
    }

    /// <summary>The query as typed.</summary>
    public string Query { get; }

    /// <summary>False when the query is empty or whitespace: every section shows and nothing is highlighted.</summary>
    public bool IsFiltered { get; }

    /// <summary>The matching entries, by section in sidebar order, then in the order they appear in their section.</summary>
    public IReadOnlyList<SettingsSearchEntry> Matches { get; }

    /// <summary>The number of matching entries in each section that has any.</summary>
    public IReadOnlyDictionary<SettingsSectionId, int> CountsBySection { get; }

    public static SettingsSearchResult Filtered(string query, IReadOnlyList<SettingsSearchEntry> matches) => new(query, isFiltered: true, matches);

    public static SettingsSearchResult Unfiltered(string query) => new(query, isFiltered: false, []);

    public int CountFor(SettingsSectionId id) => CountsBySection.GetValueOrDefault(id);

    /// <summary>The first match in <paramref name="id"/>, the row a filtered section highlights.</summary>
    public SettingsSearchEntry? FirstMatchIn(SettingsSectionId id) => Matches.FirstOrDefault(m => m.Section == id);

    /// <summary>
    /// The sidebar for this result: every row when unfiltered; otherwise each section with a match, carrying its count,
    /// under its group header, and a header only when one of its sections shows.
    /// </summary>
    public IReadOnlyList<SettingsNavItem> NavItems()
    {
        if (!IsFiltered)
        {
            return SettingsNavigation.Items;
        }

        var items = new List<SettingsNavItem>();
        SettingsNavItem? pendingHeader = null;
        foreach (SettingsNavItem item in SettingsNavigation.Items)
        {
            if (item.Id is not { } id)
            {
                pendingHeader = item;
                continue;
            }

            int count = CountFor(id);
            if (count == 0)
            {
                continue;
            }

            if (pendingHeader is not null)
            {
                items.Add(pendingHeader);
                pendingHeader = null;
            }

            items.Add(item with { MatchCount = count });
        }

        return items;
    }
}

/// <summary>
/// Matches a Settings search query against the catalog. The query splits on whitespace; an entry matches when every
/// word is found, ignoring case, in its label, its heading, its section's title or one of its aliases.
/// </summary>
public static class SettingsSearch
{
    public static SettingsSearchResult Run(string query, IReadOnlyList<SettingsSearchEntry> entries)
    {
        string[] tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return SettingsSearchResult.Unfiltered(query);
        }

        var sidebarOrder = SettingsNavigation
            .Items.Select(item => item.Id)
            .OfType<SettingsSectionId>()
            .Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index);

        List<SettingsSearchEntry> matches = [.. entries.Where(entry => Matches(entry, tokens)).OrderBy(entry => sidebarOrder[entry.Section])];
        return SettingsSearchResult.Filtered(query, matches);
    }

    private static bool Matches(SettingsSearchEntry entry, string[] tokens)
    {
        string sectionTitle = SettingsNavigation.ItemFor(entry.Section).Title;
        return tokens.All(token =>
            Contains(entry.Label, token)
            || ((entry.Within is { } within) && Contains(within, token))
            || Contains(sectionTitle, token)
            || entry.Aliases.Any(alias => Contains(alias, token))
        );
    }

    private static bool Contains(string text, string token) => text.Contains(token, StringComparison.OrdinalIgnoreCase);
}
