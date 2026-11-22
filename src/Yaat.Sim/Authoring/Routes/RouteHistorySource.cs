using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// Where the connected YAAT server answers route history searches, and the token it accepts.
/// </summary>
/// <param name="ServerBase">The server's base URL, e.g. <c>http://localhost:5130/</c>.</param>
/// <param name="AccessToken">The signed-in user's bearer token for that server.</param>
public sealed record RouteHistoryEndpoint(Uri ServerBase, string AccessToken);

/// <summary>
/// Routes observed on the SWIM feed, from the connected YAAT server's route history
/// (<c>GET {server}api/route-history</c>), which runs the search on its <see cref="RouteHistoryIndex"/>. Each route carries
/// the most-flown airline prefix and aircraft type the server saw with it. With no server connected, a reply other than
/// HTTP 200, no answer within 30 seconds or an unreadable reply, the source answers no routes and a failure message; a
/// message in the server's reply (e.g. route history not enabled) is the failure message too. A reply missing a field, or
/// holding a null row, a blank route or a negative count, is unreadable.
/// </summary>
/// <param name="http">The client to call the server with; the source does not dispose it.</param>
/// <param name="endpoint">The connected server, or null when none is connected; read on every search.</param>
public sealed class RouteHistorySource(HttpClient http, Func<RouteHistoryEndpoint?> endpoint) : IRouteSource
{
    /// <summary>The source's <see cref="Name"/>, also the source name of every route <see cref="RouteHistoryIndex.Query"/> gives.</summary>
    public const string SourceName = "history";

    private const string NotConnectedMessage = "Not connected to a YAAT server.";
    private const string TimedOutMessage = "The YAAT server did not answer the route history search in time.";
    private const string UnreadableMessage = "The YAAT server's route history reply is unreadable.";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly ILogger Log = SimLog.CreateLogger<RouteHistorySource>();

    private static readonly JsonSerializerOptions ReplyOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private readonly HttpClient _http = http;
    private readonly Func<RouteHistoryEndpoint?> _endpoint = endpoint;

    /// <inheritdoc />
    public string Name => SourceName;

    /// <summary>Gets the timeout each search runs under; a seam for tests, which cancel it themselves.</summary>
    internal Func<CancellationTokenSource> CreateTimeout { get; init; } = () => new CancellationTokenSource(RequestTimeout);

    /// <inheritdoc />
    public async Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_endpoint() is not { } server)
        {
            return new RouteSourceResult([], NotConnectedMessage);
        }

        using CancellationTokenSource timeout = CreateTimeout();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(server.ServerBase, query));
        if (!string.IsNullOrWhiteSpace(server.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.AccessToken);
        }

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, linked.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new RouteSourceResult([], $"The YAAT server answered HTTP {(int)response.StatusCode} to the route history search.");
            }

            return ReadReply(await response.Content.ReadAsStringAsync(linked.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning("The route history search timed out");
            }

            return new RouteSourceResult([], TimedOutMessage);
        }
        catch (HttpRequestException ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "The route history search failed");
            }

            return new RouteSourceResult([], $"The YAAT server could not be reached for the route history search: {ex.Message}");
        }
    }

    /// <summary>
    /// The search URL: <c>api/route-history</c> under <paramref name="serverBase"/> (as a folder, so a base path is kept),
    /// with dep and dst, and fix, airline and type only when the query sets them.
    /// </summary>
    internal static Uri BuildUrl(Uri serverBase, RouteQuery query)
    {
        string root = serverBase.AbsoluteUri.EndsWith('/') ? serverBase.AbsoluteUri : serverBase.AbsoluteUri + "/";
        StringBuilder url = new StringBuilder(root).Append("api/route-history?dep=").Append(Uri.EscapeDataString(query.Departure));
        url.Append("&dst=").Append(Uri.EscapeDataString(query.Destination));
        AppendOptional(url, "fix", query.Fix);
        AppendOptional(url, "airline", query.AirlinePrefix);
        AppendOptional(url, "type", query.AircraftType);
        return new Uri(url.ToString());
    }

    private static void AppendOptional(StringBuilder url, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            url.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value.Trim()));
        }
    }

    private static RouteSourceResult ReadReply(string body)
    {
        RouteHistoryResponse? reply;
        try
        {
            reply = JsonSerializer.Deserialize<RouteHistoryResponse>(body, ReplyOptions);
        }
        catch (JsonException ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "The route history reply is not readable JSON");
            }

            return new RouteSourceResult([], UnreadableMessage);
        }

        if ((reply?.Routes is null) || reply.Routes.Any(row => (row is null) || string.IsNullOrWhiteSpace(row.Route) || (row.Count < 0)))
        {
            return new RouteSourceResult([], UnreadableMessage);
        }

        List<FoundRoute> routes =
        [
            .. reply.Routes.Select(row => new FoundRoute(
                row.Route,
                row.Count,
                [SourceName],
                Published: null,
                Preferred: null,
                new ObservedRouteSample(row.AirlinePrefix, row.AircraftType)
            )),
        ];
        return new RouteSourceResult(routes, reply.Message);
    }
}
