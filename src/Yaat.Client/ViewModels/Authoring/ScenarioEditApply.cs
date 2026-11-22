using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>Writes one edit to the document as one undo step: the General tab's Apply.</summary>
/// <param name="description">The edit's name, as an Undo menu item shows it.</param>
/// <param name="change">Changes the scenario in place.</param>
public delegate void ScenarioEditApply(string description, Action<Scenario> change);

/// <summary>The General tab's id-list text fields: ids separated by commas or whitespace, shown joined with ", ".</summary>
internal static class ScenarioIdListText
{
    private static readonly char[] Separators = [',', ' ', '\t', '\r', '\n'];

    /// <summary>The ids in the text, trimmed, empties dropped, each in its own spelling and in order.</summary>
    /// <param name="text">The typed text.</param>
    /// <returns>The ids.</returns>
    public static List<string> Split(string text) =>
        [.. text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The ids as one text field shows them.</summary>
    /// <param name="ids">The ids.</param>
    /// <returns>The ids joined with ", ".</returns>
    public static string Join(IEnumerable<string> ids) => string.Join(", ", ids);
}

/// <summary>
/// Whether a General-tab row still wraps the item it was built over: an undo or redo replaces the document, and a list
/// edit moves items, so a row kept past either must not write to whatever item now sits at its index.
/// </summary>
internal static class ScenarioListRow
{
    /// <summary>Whether the list still holds the row's item at the row's index.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="list">The document's list now; null when the document holds none.</param>
    /// <param name="index">The row's index.</param>
    /// <param name="item">The item the row was built over.</param>
    /// <returns>True when the item is at that index.</returns>
    public static bool IsCurrent<T>(List<T>? list, int index, T item)
        where T : class => (list is not null) && (index >= 0) && (index < list.Count) && ReferenceEquals(list[index], item);

    /// <summary>Whether the row is stale (see <see cref="IsCurrent"/>), logging at debug when it is.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="list">The document's list now; null when the document holds none.</param>
    /// <param name="index">The row's index.</param>
    /// <param name="item">The item the row was built over.</param>
    /// <param name="log">The row's logger.</param>
    /// <returns>True when the row must not write.</returns>
    public static bool IsStale<T>(List<T>? list, int index, T item, ILogger log)
        where T : class
    {
        if (IsCurrent(list, index, item))
        {
            return false;
        }

        if (log.IsEnabled(LogLevel.Debug))
        {
            log.LogDebug("Scenario editor: a {Kind} row at {Index} no longer wraps its item; nothing written", typeof(T).Name, index);
        }

        return true;
    }
}
