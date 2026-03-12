using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Fetches all ZOA training scenarios from the vNAS data API and verifies that every
/// preset command can be parsed by CommandParser.ParseCompound. Skipped in CI (requires network).
/// </summary>
public class VnasScenarioParseTests(ITestOutputHelper output)
{
    private const string SummaryUrl = "https://data-api.vnas.vatsim.net/api/training/scenario-summaries/by-artcc/ZOA";
    private const string ScenarioUrl = "https://data-api.vnas.vatsim.net/api/training/scenarios/";

    private readonly ITestOutputHelper _output = output;

    [Fact(Skip = "Requires network — run manually")]
    public async Task AllZoaScenarios_PresetCommandsParse()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Fetch scenario summaries
        var summariesJson = await http.GetStringAsync(SummaryUrl);
        var summaries = JsonSerializer.Deserialize<List<ScenarioSummaryDto>>(summariesJson, JsonOpts);
        Assert.NotNull(summaries);
        Assert.NotEmpty(summaries);

        _output.WriteLine($"Found {summaries.Count} ZOA scenarios");

        var failures = new List<string>();
        var fixes = new PermissiveFixLookup();

        foreach (var summary in summaries)
        {
            string scenarioJson;
            try
            {
                scenarioJson = await http.GetStringAsync(ScenarioUrl + summary.Id);
            }
            catch (HttpRequestException ex)
            {
                _output.WriteLine($"  SKIP {summary.Id} ({summary.Name}): {ex.Message}");
                continue;
            }

            Scenario? scenario;
            try
            {
                scenario = JsonSerializer.Deserialize<Scenario>(scenarioJson, JsonOpts);
            }
            catch (JsonException ex)
            {
                failures.Add($"[{summary.Name}] JSON deserialize failed: {ex.Message}");
                continue;
            }

            if (scenario is null)
            {
                continue;
            }

            _output.WriteLine($"  {summary.Name} ({scenario.Aircraft.Count} aircraft)");

            foreach (var ac in scenario.Aircraft)
            {
                foreach (var preset in ac.PresetCommands)
                {
                    if (string.IsNullOrWhiteSpace(preset.Command))
                    {
                        continue;
                    }

                    var compound = CommandParser.ParseCompound(preset.Command, fixes, ac.FlightPlan?.Route);
                    if (compound is null)
                    {
                        failures.Add($"[{summary.Name}] {ac.AircraftId}: \"{preset.Command}\" → parse failed");
                    }
                }
            }
        }

        if (failures.Count > 0)
        {
            _output.WriteLine($"\n=== {failures.Count} PARSE FAILURES ===");
            foreach (var f in failures)
            {
                _output.WriteLine(f);
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} preset commands failed to parse:\n{string.Join("\n", failures)}");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private class ScenarioSummaryDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// A permissive fix lookup that returns a dummy position for any fix name.
    /// This allows parse tests to succeed even without real nav data — the goal
    /// is to verify command syntax, not fix resolution.
    /// </summary>
    private class PermissiveFixLookup : IFixLookup
    {
        public (double Lat, double Lon)? GetFixPosition(string name) => (37.6, -122.4);

        public double? GetAirportElevation(string code) => 13.0;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }
}
