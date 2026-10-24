namespace Yaat.Client.ContextMenus;

/// <summary>One category's icons on one strip row.</summary>
/// <typeparam name="T">What an icon stands for.</typeparam>
/// <param name="Category">The icons' category, or null for the uncategorised group.</param>
/// <param name="Items">The icons, in list order.</param>
/// <param name="ShowsLabel">
/// Whether the category's label heads the segment: false for the uncategorised group and for a wrapped category's
/// continuation rows.
/// </param>
public sealed record QuickCommandStripSegment<T>(MenuCategory? Category, IReadOnlyList<T> Items, bool ShowsLabel);

/// <summary>One strip row: its segments left to right, at most <see cref="QuickCommandStripLayout.RowLength"/> icons in all.</summary>
/// <typeparam name="T">What an icon stands for.</typeparam>
/// <param name="Segments">The row's segments, in category order.</param>
public sealed record QuickCommandStripRow<T>(IReadOnlyList<QuickCommandStripSegment<T>> Segments);

/// <summary>
/// How the quick-command icon strip packs its icons into rows, for the menu's strip and the Settings preview alike. The
/// icons group by category (<see cref="MenuCategories.Of"/>), the categories in the order the list first names them and
/// each group in list order, with the uncategorised icons as one last group. Each group is cut into runs of
/// <see cref="RowLength"/>; a full run fills a row of its own, and a group's last, partial run joins the row before it
/// when that row is the previous group's last, partial run (or a row such runs share) and the icons fit, else it starts a
/// row the next group may join. Only neighbours share: a group never joins any row but the last. A wrapped group's later
/// runs show no label, and its last run, when partial, may share its row with the groups after it, its own part first.
/// </summary>
public static class QuickCommandStripLayout
{
    /// <summary>How many icons a strip row holds.</summary>
    public const int RowLength = 6;

    /// <summary>The rows <paramref name="entries"/> pack into; none for no entries.</summary>
    /// <typeparam name="T">What an icon stands for.</typeparam>
    /// <param name="entries">The strip's icons, in list order.</param>
    /// <param name="categoryOf">An icon's category, or null for none.</param>
    public static IReadOnlyList<QuickCommandStripRow<T>> Pack<T>(IReadOnlyList<T> entries, Func<T, MenuCategory?> categoryOf)
    {
        List<QuickCommandStripRow<T>> rows = [];
        List<QuickCommandStripSegment<T>>? open = null;
        int openCount = 0;
        foreach ((MenuCategory? category, List<T> items) in Groups(entries, categoryOf))
        {
            for (int start = 0; start < items.Count; start += RowLength)
            {
                List<T> run = items.GetRange(start, Math.Min(RowLength, items.Count - start));
                bool first = start == 0;
                var segment = new QuickCommandStripSegment<T>(category, run, ShowsLabel: first && (category is not null));
                bool shares = run.Count < RowLength;
                if (shares && (open is not null) && (openCount + run.Count <= RowLength))
                {
                    open.Add(segment);
                    openCount += run.Count;
                    continue;
                }

                List<QuickCommandStripSegment<T>> row = [segment];
                rows.Add(new QuickCommandStripRow<T>(row));
                open = shares ? row : null;
                openCount = run.Count;
            }
        }

        return rows;
    }

    /// <summary>The entries grouped by category, categories in first-named order and the uncategorised group last.</summary>
    private static List<(MenuCategory? Category, List<T> Items)> Groups<T>(IReadOnlyList<T> entries, Func<T, MenuCategory?> categoryOf)
    {
        List<(MenuCategory? Category, List<T> Items)> groups = [];
        List<T> uncategorised = [];
        foreach (T entry in entries)
        {
            if (categoryOf(entry) is not { } category)
            {
                uncategorised.Add(entry);
                continue;
            }

            int index = groups.FindIndex(group => group.Category == category);
            if (index < 0)
            {
                groups.Add((category, [entry]));
            }
            else
            {
                groups[index].Items.Add(entry);
            }
        }

        if (uncategorised.Count > 0)
        {
            groups.Add((null, uncategorised));
        }

        return groups;
    }
}
