using System.Text.Json;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Weather;

/// <summary>The GFS grids a recording's weather text names, so a writer bundles each one once.</summary>
public static class WindGridHashes
{
    /// <summary>
    /// The distinct <see cref="WeatherProfile.WindGridHash"/> values the initial weather, every recorded weather change and
    /// every snapshot's weather name, in order of first appearance. A v2 timeline, a weather clear and text that is not a
    /// JSON object name none.
    /// </summary>
    /// <param name="initialWeatherJson">The weather the recording starts on, or null without one.</param>
    /// <param name="actions">The recording's action log; only its <see cref="RecordedWeatherChange"/> entries are read.</param>
    /// <param name="snapshotWeatherJsons">The <c>WeatherJson</c> of each snapshot the recording carries (null where one has none).</param>
    public static IReadOnlyList<string> Referenced(
        string? initialWeatherJson,
        IEnumerable<RecordedAction> actions,
        IEnumerable<string?> snapshotWeatherJsons
    )
    {
        var hashes = new List<string>();
        AddHashOf(hashes, initialWeatherJson);
        foreach (RecordedWeatherChange change in actions.OfType<RecordedWeatherChange>())
        {
            AddHashOf(hashes, change.WeatherJson);
        }

        foreach (string? snapshotWeatherJson in snapshotWeatherJsons)
        {
            AddHashOf(hashes, snapshotWeatherJson);
        }

        return hashes;
    }

    private static void AddHashOf(List<string> hashes, string? weatherJson)
    {
        if ((HashIn(weatherJson) is { } hash) && !hashes.Contains(hash))
        {
            hashes.Add(hash);
        }
    }

    /// <summary>
    /// The grid hash a v1 profile's JSON carries, read as <see cref="WeatherTimelineParser"/> reads it: v2 detected by
    /// <see cref="WeatherTimelineParser.IsTimelineRoot"/> (a timeline carries no grid), the hash's name matched
    /// case-insensitively as the profile deserializer matches it.
    /// </summary>
    private static string? HashIn(string? weatherJson)
    {
        if (weatherJson is null)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(weatherJson);
        }
        catch (JsonException)
        {
            // Text that does not parse is refused by every weather loader, so it flies no grid and names none.
            return null;
        }

        using (document)
        {
            return document.RootElement.ValueKind is JsonValueKind.Object ? HashInProfile(document.RootElement) : null;
        }
    }

    private static string? HashInProfile(JsonElement root)
    {
        if (WeatherTimelineParser.IsTimelineRoot(root, out _))
        {
            return null;
        }

        string? hash = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.Equals("windGridHash", StringComparison.OrdinalIgnoreCase))
            {
                hash = property.Value.ValueKind is JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return hash;
    }
}
