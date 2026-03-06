using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Fetches training scenarios, weather profiles, and airport summaries from the vNAS data API.
/// </summary>
public sealed class TrainingDataService
{
    private static readonly ILogger Log = AppLog.CreateLogger<TrainingDataService>();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const string BaseUrl = "https://data-api.vnas.vatsim.net/api/training";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<ScenarioSummaryDto>> GetScenarioSummariesAsync(string artccId)
    {
        try
        {
            var url = $"{BaseUrl}/scenario-summaries/by-artcc/{artccId}";
            var json = await Http.GetStringAsync(url);
            return JsonSerializer.Deserialize<List<ScenarioSummaryDto>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to fetch scenario summaries for {ArtccId}", artccId);
            return [];
        }
    }

    public async Task<string?> GetScenarioJsonAsync(string scenarioId)
    {
        try
        {
            var url = $"{BaseUrl}/scenarios/{scenarioId}";
            return await Http.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to fetch scenario {ScenarioId}", scenarioId);
            return null;
        }
    }

    public async Task<List<WeatherProfileDto>> GetWeatherProfilesAsync(string artccId)
    {
        try
        {
            var url = $"{BaseUrl}/weather/by-artcc/{artccId}";
            var json = await Http.GetStringAsync(url);
            return JsonSerializer.Deserialize<List<WeatherProfileDto>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to fetch weather profiles for {ArtccId}", artccId);
            return [];
        }
    }

    public async Task<string?> GetWeatherJsonAsync(string weatherId)
    {
        try
        {
            var url = $"{BaseUrl}/weather/{weatherId}";
            return await Http.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to fetch weather {WeatherId}", weatherId);
            return null;
        }
    }
}

public sealed record ScenarioSummaryDto(string Id, string Name, string ArtccId, string? PrimaryAirportId, string? MinimumRating);

public sealed record WeatherProfileDto(
    string Id,
    string Name,
    string ArtccId,
    string? Precipitation,
    List<WeatherProfileLayerDto> WindLayers,
    List<string>? Metars
);

public sealed record WeatherProfileLayerDto(string Id, int Altitude, int Direction, int Speed);
