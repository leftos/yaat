using Microsoft.Extensions.Logging;

namespace Yaat.Client.ViewModels.Authoring;

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
