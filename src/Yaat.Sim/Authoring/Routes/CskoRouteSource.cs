using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// Routes observed on VATSIM, from the csko flight-plans search endpoint
/// (<c>https://flight-plans.csko.hu/v1/flight_plan/search</c>): the flights between two airports, grouped by the route
/// they filed, each route's count the number of flights. Replies are cached on disk under <c>cache/csko/</c> for 24 hours,
/// one file per request URL; when csko cannot be reached the cached reply is served and the failure still reported.
/// </summary>
/// <param name="http">The client to call csko with, or null for one of its own (20 s timeout, its own User-Agent).</param>
/// <param name="cacheDir">Where replies are cached, or null for <c>cache/csko</c> under the YAAT app-data folder.</param>
public sealed partial class CskoRouteSource(HttpClient? http, string? cacheDir) : IRouteSource, IDisposable
{
    private const string SearchUrl = "https://flight-plans.csko.hu/v1/flight_plan/search";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly ILogger Log = SimLog.CreateLogger<CskoRouteSource>();

    private readonly HttpClient _http = http ?? CreateClient();
    private readonly bool _ownsHttp = http is null;
    private readonly string _cacheDir = cacheDir ?? YaatPaths.Combine("cache", "csko");

    /// <summary>
    /// Initializes a new instance of the <see cref="CskoRouteSource"/> class with its own HTTP client and the user cache.
    /// </summary>
    public CskoRouteSource()
        : this(http: null, cacheDir: null) { }

    /// <inheritdoc />
    public string Name => "csko";

    /// <inheritdoc />
    public async Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
    {
        string url = BuildUrl(query);
        string cachePath = Path.Combine(_cacheDir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32] + ".json");
        HttpCacheResult reply = await HttpFileCache.GetOrRefreshAsync(_http, url, cachePath, HttpCacheFreshness.AlwaysRefetch, CacheTtl, Log, ct);

        string? failure = FailureMessage(reply);
        if (reply.Content is null)
        {
            return new RouteSourceResult([], failure);
        }

        try
        {
            return new RouteSourceResult(GroupRoutes(reply.Content, query), failure);
        }
        catch (JsonException ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "csko reply for {Url} is not readable JSON; discarding the cached copy", url);
            }

            File.Delete(cachePath);
            return new RouteSourceResult([], "csko returned an unreadable reply");
        }
    }

    /// <summary>Disposes the HTTP client when this source created it.</summary>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// The search request for <paramref name="query"/>. A departure outside the contiguous US (not starting with "K") is
    /// left out of the request, because csko truncates those flights' route texts under a departure filter; the
    /// flights are filtered by departure after the reply instead.
    /// </summary>
    internal static string BuildUrl(RouteQuery query)
    {
        StringBuilder url = new StringBuilder(SearchUrl).Append('?');
        if (IsConus(query.Departure))
        {
            url.Append("departure=").Append(Uri.EscapeDataString(query.Departure)).Append('&');
        }

        url.Append("arrival=").Append(Uri.EscapeDataString(query.Destination)).Append("&limit=500");
        AppendOptional(url, "route", query.Fix);
        AppendOptional(url, "callsign", query.AirlinePrefix);
        AppendOptional(url, "aircraft_type", query.AircraftType);
        return url.ToString();
    }

    private static void AppendOptional(StringBuilder url, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            url.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    private static bool IsConus(string airport) => airport.StartsWith('K') || airport.StartsWith('k');

    private static string? FailureMessage(HttpCacheResult reply)
    {
        if (reply.RefreshFailed)
        {
            return reply.Content is null
                ? "csko did not answer the route search"
                : "csko did not answer the route search; showing its last cached reply";
        }

        if (reply.NotFound)
        {
            return reply.Content is null ? "csko answered HTTP 404 for the route search" : "csko answered HTTP 404; showing its last cached reply";
        }

        return null;
    }

    private static List<FoundRoute> GroupRoutes(string json, RouteQuery query)
    {
        List<CskoFlight> flights = JsonSerializer.Deserialize<List<CskoFlight>>(json) ?? [];
        bool filterDeparture = !IsConus(query.Departure);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (CskoFlight flight in flights)
        {
            if (filterDeparture && !string.Equals(flight.Departure, query.Departure, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? route = EffectiveRoute(flight);
            if (route is null)
            {
                continue;
            }

            if (!counts.TryGetValue(route, out int count))
            {
                order.Add(route);
            }

            counts[route] = count + 1;
        }

        return [.. order.Select(route => new FoundRoute(route, counts[route], ["csko"], Published: null, Preferred: null, Observed: null))];
    }

    /// <summary>
    /// The route a flight filed: its <c>route_text</c>, else its dotted <c>last_route_text</c> turned into a spaced
    /// route (leading departure, trailing arrival and "/HHMM" dropped), unless that holds "./.", an amendment from a
    /// present position rather than a departure route. Null when the flight has neither.
    /// </summary>
    internal static string? EffectiveRoute(CskoFlight flight)
    {
        if (!string.IsNullOrWhiteSpace(flight.RouteText))
        {
            return flight.RouteText.Trim();
        }

        string? last = flight.LastRouteText;
        if (string.IsNullOrWhiteSpace(last) || last.Contains("./.", StringComparison.Ordinal))
        {
            return null;
        }

        List<string> tokens = [.. EtaSuffix().Replace(last.Trim(), "").Split('.', StringSplitOptions.RemoveEmptyEntries)];
        RouteFinder.TrimAirports(tokens, flight.Departure, flight.Arrival);

        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("yaat-route-finder");
        return http;
    }

    [GeneratedRegex(@"/\d{4}$")]
    private static partial Regex EtaSuffix();

    /// <summary>One flight in a csko search reply; only the fields the route source reads.</summary>
    internal sealed record CskoFlight(
        [property: JsonPropertyName("departure")] string? Departure,
        [property: JsonPropertyName("arrival")] string? Arrival,
        [property: JsonPropertyName("route_text")] string? RouteText,
        [property: JsonPropertyName("last_route_text")] string? LastRouteText
    );
}
