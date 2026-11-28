using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Data.Converters;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The text of an order row's BASE strip: the base's items in the base's order, each named as the panes name it (an
/// aircraft's callsign, else a command, else its id). Bound with the row and the window's view model.
/// </summary>
public sealed class MergeBaseOrderConverter : IMultiValueConverter
{
    private static readonly JsonDocumentOptions ParseOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Gets the converter.</summary>
    public static MergeBaseOrderConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        if ((values.Count < 2) || (values[0] is not ScenarioMergeRowViewModel row) || (values[1] is not ScenarioMergeViewModel merge))
        {
            return null;
        }

        if ((row.Row.Base is not JsonArray ids) || (merge.Request.Texts.Base is not { } baseText))
        {
            return row.BaseText;
        }

        List<JsonObject> items = ListAt(JsonNode.Parse(baseText, documentOptions: ParseOptions), row.Path);
        return string.Join(" · ", ids.Select((id, i) => $"{i + 1}. {NameOf(items, id?.GetValue<string>())}"));
    }

    // The list the order row's path names ("/initializationTriggers#order"), walked from the base's root; empty when absent.
    private static List<JsonObject> ListAt(JsonNode? root, string path)
    {
        JsonNode? node = root;
        string listPath = path.EndsWith(ScenarioMerge.OrderPathSuffix, StringComparison.Ordinal)
            ? path[..^ScenarioMerge.OrderPathSuffix.Length]
            : path;
        foreach (string raw in listPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject obj => obj[segment],
                JsonArray list when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && (i < list.Count) => list[i],
                _ => null,
            };
        }

        return (node is JsonArray found) ? [.. found.OfType<JsonObject>()] : [];
    }

    private static string NameOf(List<JsonObject> items, string? id)
    {
        JsonObject? item = items.FirstOrDefault(candidate => ScenarioMergeItemKeys.IdOf(candidate) == id);
        return Text(item, "aircraftId") ?? Text(item, "command") ?? id ?? "";
    }

    private static string? Text(JsonObject? item, string key) =>
        ((item?[key] is JsonValue value) && value.TryGetValue(out string? text)) ? text : null;
}
