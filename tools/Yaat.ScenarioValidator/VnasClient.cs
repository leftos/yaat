using System.Text.Json;

namespace Yaat.ScenarioValidator;

public sealed class VnasClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string BaseUrl = "https://data-api.vnas.vatsim.net/api/training";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<ScenarioSummary>> GetScenarioSummariesAsync(string artccId)
    {
        var url = $"{BaseUrl}/scenario-summaries/by-artcc/{artccId}";
        var json = await Http.GetStringAsync(url);
        return JsonSerializer.Deserialize<List<ScenarioSummary>>(json, JsonOpts) ?? [];
    }

    public async Task<string?> GetScenarioJsonAsync(string scenarioId)
    {
        try
        {
            var url = $"{BaseUrl}/scenarios/{scenarioId}";
            return await Http.GetStringAsync(url);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}

public sealed record ScenarioSummary(string Id, string Name);
