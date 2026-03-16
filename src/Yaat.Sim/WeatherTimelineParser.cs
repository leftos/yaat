using System.Text.Json;

namespace Yaat.Sim;

/// <summary>
/// Discriminated result from parsing weather JSON. Exactly one of Timeline, Profile, or Error is set.
/// </summary>
public sealed class WeatherParseResult
{
    public WeatherTimeline? Timeline { get; init; }
    public WeatherProfile? Profile { get; init; }
    public string? Error { get; init; }
    public bool IsTimeline => Timeline is not null;
    public bool IsProfile => Profile is not null;
    public bool IsError => Error is not null;
}

public static class WeatherTimelineParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Parses weather JSON, auto-detecting v1 (ATCTrainer WeatherProfile) vs v2 (WeatherTimeline with periods).
    /// </summary>
    public static WeatherParseResult Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new WeatherParseResult { Error = $"Invalid JSON: {ex.Message}" };
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("periods", out var periodsEl) && periodsEl.ValueKind == JsonValueKind.Array)
            {
                return ParseV2(json, periodsEl);
            }

            return ParseV1(json);
        }
    }

    private static WeatherParseResult ParseV2(string json, JsonElement periodsEl)
    {
        if (periodsEl.GetArrayLength() == 0)
        {
            return new WeatherParseResult { Error = "V2 weather has no periods" };
        }

        WeatherTimeline? timeline;
        try
        {
            timeline = JsonSerializer.Deserialize<WeatherTimeline>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new WeatherParseResult { Error = $"Failed to deserialize v2 weather: {ex.Message}" };
        }

        if (timeline is null)
        {
            return new WeatherParseResult { Error = "Failed to deserialize v2 weather" };
        }

        // Validate periods have wind layers
        for (int i = 0; i < timeline.Periods.Count; i++)
        {
            if (timeline.Periods[i].WindLayers.Count == 0)
            {
                return new WeatherParseResult { Error = $"Period {i} has no wind layers" };
            }
        }

        // Sort periods by StartMinutes
        timeline.Periods.Sort((a, b) => a.StartMinutes.CompareTo(b.StartMinutes));

        return new WeatherParseResult { Timeline = timeline };
    }

    private static WeatherParseResult ParseV1(string json)
    {
        WeatherProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<WeatherProfile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new WeatherParseResult { Error = $"Failed to deserialize weather profile: {ex.Message}" };
        }

        if (profile is null)
        {
            return new WeatherParseResult { Error = "Failed to deserialize weather profile" };
        }

        return new WeatherParseResult { Profile = profile };
    }
}
