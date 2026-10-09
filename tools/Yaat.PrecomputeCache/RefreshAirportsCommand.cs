using System.Collections.Concurrent;
using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Scenarios;

namespace Yaat.PrecomputeCache;

/// <summary>
/// <c>--refresh-airports</c>: fetches every ARTCC's training scenarios from the vNAS data API, collects the airports they
/// use (<see cref="ScenarioAirportCollector"/>), keeps those vNAS has a ground map for, and rewrites the airport list. Any
/// failed fetch leaves the list as it was, so a network fault never drops an airport from it.
/// </summary>
public static class RefreshAirportsCommand
{
    /// <summary>The vNAS training data API.</summary>
    public const string TrainingApi = "https://data-api.vnas.vatsim.net/api/training";

    /// <summary>Requests in flight at once, as the server repo's scenario download uses.</summary>
    private const int MaxConcurrentRequests = 8;

    /// <summary>The ARTCCs whose scenarios are read, as yaat-server's <c>tools/validate-all-scenarios.py</c> lists them.</summary>
    private static readonly string[] Artccs =
    [
        "ZAB",
        "ZAN",
        "ZAU",
        "ZBW",
        "ZDC",
        "ZDV",
        "ZFW",
        "ZHN",
        "ZHU",
        "ZID",
        "ZJX",
        "ZKC",
        "ZLA",
        "ZLC",
        "ZMA",
        "ZME",
        "ZMP",
        "ZNY",
        "ZOA",
        "ZOB",
        "ZSE",
        "ZSU",
        "ZTL",
    ];

    /// <summary>Runs the refresh into <paramref name="root"/>'s airport list.</summary>
    /// <param name="root">The cache folder.</param>
    /// <param name="services">The network and output.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string root, PrecomputeCacheServices services)
    {
        List<Scenario> scenarios = await FetchScenariosAsync(services);
        IReadOnlyList<string> named = ScenarioAirportCollector.AirportsOf(scenarios);
        (List<string> mapped, List<string> skipped) = await SplitByMapAsync(named, services);
        if (mapped.Count == 0)
        {
            throw new ToolFailureException(
                $"None of the {named.Count} airports the {scenarios.Count} scenarios name has a vNAS ground map; the airport list is left as it was"
            );
        }

        string path = Path.Combine(root, PrecomputeAirportList.FileName);
        PrecomputeAirportList.Write(path, mapped);

        int withFlightPlans = ScenarioAirportCollector
            .Normalize(named.Concat(scenarios.SelectMany(s => s.Aircraft).SelectMany(a => FlightPlanAirports(a.FlightPlan))))
            .Count;
        await services.Out.WriteLineAsync($"{scenarios.Count} scenarios across {Artccs.Length} ARTCCs name {named.Count} airports.");
        await services.Out.WriteLineAsync($"Wrote {path}: {mapped.Count} airports with a vNAS ground map.");
        await services.Out.WriteLineAsync($"Skipped {skipped.Count} airports with no vNAS ground map (HTTP 404): {string.Join(' ', skipped)}");
        await services.Out.WriteLineAsync(
            $"For information: counting flight-plan departures and destinations too would name {withFlightPlans} airports "
                + "(map availability not probed)."
        );
        return PrecomputeCacheCli.Ok;
    }

    private static IEnumerable<string?> FlightPlanAirports(ScenarioFlightPlan? plan) => plan is null ? [] : [plan.Departure, plan.Destination];

    private static async Task<List<Scenario>> FetchScenariosAsync(PrecomputeCacheServices services)
    {
        var ids = new List<string>();
        foreach (string artcc in Artccs)
        {
            string summaries = await GetStringAsync(services.Http, $"{TrainingApi}/scenario-summaries/by-artcc/{artcc}", CancellationToken.None);
            using var document = JsonDocument.Parse(summaries);
            ids.AddRange(document.RootElement.EnumerateArray().Select(s => s.GetProperty("id").GetString()).OfType<string>());
        }

        var scenarios = new ConcurrentDictionary<string, Scenario>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            ids.Distinct(StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentRequests },
            async (id, token) => scenarios[id] = ParseScenario(id, await GetStringAsync(services.Http, $"{TrainingApi}/scenarios/{id}", token))
        );
        return [.. scenarios.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Value)];
    }

    private static Scenario ParseScenario(string id, string json)
    {
        try
        {
            return ScenarioAirportCollector.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new ToolFailureException($"Scenario {id} from vNAS is not a readable scenario ({ex.Message}); the airport list is left as it was");
        }
    }

    /// <summary>The airports vNAS has a ground map for, and those it answers 404 for; any other outcome fails the refresh.</summary>
    private static async Task<(List<string> Mapped, List<string> Skipped)> SplitByMapAsync(
        IReadOnlyList<string> airports,
        PrecomputeCacheServices services
    )
    {
        using var downloader = new AirportLayoutDownloader(services.Http, services.GeoJsonCacheDir);
        var outcomes = new ConcurrentDictionary<string, HttpCacheResult>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            airports,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentRequests },
            async (airport, token) => outcomes[airport] = await downloader.FetchGeoJsonAsync(airport, token)
        );

        List<string> unreachable = [.. airports.Where(a => PrecomputeCacheCli.MapUnreachable(outcomes[a]))];
        if (unreachable.Count > 0)
        {
            throw new ToolFailureException(
                $"vNAS could not be reached for the ground maps of {string.Join(' ', unreachable)}; the airport list is left as it was"
            );
        }

        return ([.. airports.Where(a => !outcomes[a].NotFound)], [.. airports.Where(a => outcomes[a].NotFound)]);
    }

    private static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            return await http.GetStringAsync(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ToolFailureException($"GET {url} failed ({ex.Message}); the airport list is left as it was");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new ToolFailureException($"GET {url} timed out ({ex.Message}); the airport list is left as it was");
        }
    }
}
