using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Bookmarks;

/// <summary>
/// The mutating <c>BM</c> verbs over the engine: <c>ADD</c> (authored by the issuing controller's initials and stamped
/// at the scenario's current second), <c>REN</c>, <c>DEL</c> and <c>DEL ALL</c>, through the same
/// <see cref="SimulationEngine.AddBookmark"/> / <see cref="SimulationEngine.RenameBookmark"/> /
/// <see cref="SimulationEngine.DeleteBookmark"/> / <see cref="SimulationEngine.DeleteAllBookmarks"/> bodies the room's
/// bookmark RPCs call, so both paths mark the set dirty alike and the host broadcasts once. The message is the
/// terminal response. The remaining actions (<c>LIST</c>, <c>GO</c>, <c>NEXT</c>, <c>PREV</c>) are the client's.
///
/// <para>
/// What the bodies touched reaches the host as <see cref="Spine.IStateChangeConsumer.OnBookmarksChanged"/>.
/// </para>
/// </summary>
public static class BookmarkCommandHandler
{
    public static CommandResult Handle(SimulationEngine engine, BookmarkCommand command, string initials)
    {
        if (engine.Scenario is not { } scenario)
        {
            return new CommandResult(false, "No active scenario");
        }

        return command.Action switch
        {
            BookmarkAction.Add => HandleAdd(engine, scenario, command, initials),
            BookmarkAction.Rename => HandleRename(engine, command),
            BookmarkAction.Delete => HandleDelete(engine, command),
            BookmarkAction.DeleteAll => HandleDeleteAll(engine),
            _ => new CommandResult(false, $"BM {command.Action} is handled by the client, not the server"),
        };
    }

    private static CommandResult HandleAdd(SimulationEngine engine, SimScenarioState scenario, BookmarkCommand command, string initials)
    {
        var added = engine.AddBookmark(scenario.ElapsedSeconds, command.Name, initials);
        return new CommandResult(
            added.Success,
            added.Success
                ? $"Bookmark {added.Message} added at {FormatBookmarkTime(scenario.ElapsedSeconds)}"
                : added.Message ?? "Add bookmark failed"
        );
    }

    private static CommandResult HandleRename(SimulationEngine engine, BookmarkCommand command)
    {
        var renamed = engine.RenameBookmark(command.Id!, command.Name);
        var message = renamed.Success
            ? (command.Name is null ? $"Bookmark {command.Id} name cleared" : $"Bookmark {command.Id} renamed to \"{command.Name}\"")
            : renamed.Message ?? "Rename bookmark failed";
        return new CommandResult(renamed.Success, message);
    }

    private static CommandResult HandleDelete(SimulationEngine engine, BookmarkCommand command)
    {
        var deleted = engine.DeleteBookmark(command.Id!);
        return new CommandResult(deleted.Success, deleted.Success ? $"Bookmark {command.Id} deleted" : deleted.Message ?? "Delete bookmark failed");
    }

    private static CommandResult HandleDeleteAll(SimulationEngine engine)
    {
        var removed = engine.DeleteAllBookmarks();
        return new CommandResult(removed > 0, removed > 0 ? $"Deleted {removed} bookmark(s)" : "No bookmarks");
    }

    private static string FormatBookmarkTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
