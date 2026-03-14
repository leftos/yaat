using System.Text.Json;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;

namespace Yaat.ScenarioValidator;

public sealed class VnasClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string BaseUrl = "https://data-api.vnas.vatsim.net/api/training";
    private const string ConfigUrl = "https://configuration.vnas.vatsim.net/";

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

    public async Task<NavDataSet?> DownloadNavDataAsync()
    {
        var configJson = await Http.GetStringAsync(ConfigUrl);
        var config = JsonSerializer.Deserialize<VnasConfig>(configJson, JsonOpts);
        if (config is null || string.IsNullOrEmpty(config.NavDataUrl))
        {
            return null;
        }

        var bytes = await Http.GetByteArrayAsync(config.NavDataUrl);
        return NavDataSet.Parser.ParseFrom(bytes);
    }
}

public sealed record ScenarioSummary(string Id, string Name);
