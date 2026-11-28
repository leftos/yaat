using System.Globalization;
using System.Text.Json.Nodes;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The merge window's list item keys, as the engine gives them: an item's id, or <c>#n</c> for the item at index n of a list
/// matched by position (some item lacks an id, or two share one).
/// </summary>
internal static class ScenarioMergeItemKeys
{
    /// <summary>The index a positional key names.</summary>
    /// <param name="key">An item key.</param>
    /// <returns>The index of a <c>#n</c> key; null for an id key.</returns>
    public static int? Position(string key) =>
        (key.StartsWith('#') && int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) ? index : null;

    /// <summary>The item a key names in one side's list: the item at its index for a positional key, else the item with its id.</summary>
    /// <param name="items">One side's items, in its order.</param>
    /// <param name="key">An item key.</param>
    /// <returns>The item; null when the list holds none at that index or with that id.</returns>
    public static JsonObject? Find(IReadOnlyList<JsonObject> items, string key) =>
        (Position(key) is { } index) ? items.ElementAtOrDefault(index) : items.FirstOrDefault(item => IdOf(item) == key);

    /// <summary>An item's id.</summary>
    /// <param name="item">A list item.</param>
    /// <returns>Its non-empty string id; null when it has none.</returns>
    public static string? IdOf(JsonObject? item) =>
        ((item?["id"] is JsonValue value) && value.TryGetValue(out string? id) && !string.IsNullOrEmpty(id)) ? id : null;
}
