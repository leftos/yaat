using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation;

// The timeline-bookmark half of the engine: the four mutating bodies, and the dirty flag the drain turns into the
// host's one broadcast. The verb bodies live in Simulation/Bookmarks/BookmarkCommandHandler.cs; the room's bookmark
// RPCs — the client's bookmark buttons, which place a bookmark at a time of their choosing rather than at the current
// second — call the bodies here directly. Bookmarks are timeline-global metadata rather than per-tick simulation
// state, deliberately outside the snapshot (see SimScenarioState.Bookmarks), and the BM verb is never recorded: a
// rewind carries the list over verbatim instead of replaying the adds.
public sealed partial class SimulationEngine
{
    /// <summary>Cap on the number of shared timeline bookmarks per scenario.</summary>
    private const int MaxBookmarks = 500;

    /// <summary>
    /// True when a bookmark body has changed the set since the last drain. Payload-less like the coordination flag:
    /// the host re-sends the whole list, which is what the wire carries anyway.
    /// </summary>
    internal bool BookmarksChanged { get; private set; }

    /// <summary>Marks the bookmark set dirty; the next drain hands the host one <c>OnBookmarksChanged</c>.</summary>
    internal void MarkBookmarksChanged() => BookmarksChanged = true;

    /// <summary>
    /// Takes the flag and clears it, for the paths that mutate the set outside both the action router's drain and the
    /// post-physics one: the room's bookmark RPCs.
    /// </summary>
    internal bool DrainBookmarksChanged()
    {
        var changed = BookmarksChanged;
        BookmarksChanged = false;
        return changed;
    }

    /// <summary>
    /// Adds a shared timeline bookmark. The id is minted here; <paramref name="initials"/> records who created it.
    /// The message carries the new id.
    /// </summary>
    public CommandResult AddBookmark(double timeSeconds, string? name, string? initials)
    {
        if (Scenario is not { } scenario)
        {
            return new CommandResult(false, "No active scenario");
        }

        if (scenario.Bookmarks.Count >= MaxBookmarks)
        {
            return new CommandResult(false, $"Bookmark limit reached ({MaxBookmarks})");
        }

        var id = $"bm-{scenario.NextBookmarkId++}";
        scenario.Bookmarks.Add(new TimelineBookmark(id, timeSeconds, NormalizeBookmarkText(name), NormalizeBookmarkText(initials)));
        MarkBookmarksChanged();
        return new CommandResult(true, id);
    }

    /// <summary>Renames a shared timeline bookmark (any RPO may rename any bookmark).</summary>
    public CommandResult RenameBookmark(string id, string? name)
    {
        if (Scenario is not { } scenario)
        {
            return new CommandResult(false, "No active scenario");
        }

        var index = scenario.Bookmarks.FindIndex(b => b.Id == id);
        if (index < 0)
        {
            return new CommandResult(false, $"No bookmark {id}");
        }

        scenario.Bookmarks[index] = scenario.Bookmarks[index] with { Name = NormalizeBookmarkText(name) };
        MarkBookmarksChanged();
        return new CommandResult(true, id);
    }

    /// <summary>Deletes a shared timeline bookmark (any RPO may delete any bookmark).</summary>
    public CommandResult DeleteBookmark(string id)
    {
        if (Scenario is not { } scenario)
        {
            return new CommandResult(false, "No active scenario");
        }

        if (scenario.Bookmarks.RemoveAll(b => b.Id == id) == 0)
        {
            return new CommandResult(false, $"No bookmark {id}");
        }

        MarkBookmarksChanged();
        return new CommandResult(true, id);
    }

    /// <summary>Deletes every shared timeline bookmark. Returns how many were removed.</summary>
    public int DeleteAllBookmarks()
    {
        if ((Scenario is not { } scenario) || (scenario.Bookmarks.Count == 0))
        {
            return 0;
        }

        var removed = scenario.Bookmarks.Count;
        scenario.Bookmarks.Clear();
        MarkBookmarksChanged();
        return removed;
    }

    private static string? NormalizeBookmarkText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
