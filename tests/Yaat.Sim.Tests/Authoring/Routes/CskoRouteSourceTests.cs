using System.Net;
using Xunit;
using Yaat.Sim.Authoring.Routes;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="CskoRouteSource"/> asks the csko flight-plans search endpoint for the flights between two airports and
/// groups them by the route they filed. These pin the request it sends, the grouping of a real reply, the 24 h disk
/// cache and the failure it reports when csko cannot be reached.
/// </summary>
public class CskoRouteSourceTests
{
    private const string SstikIrnmn = "SSTIK5 SUSEY EBAYE BURGL IRNMN2";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Responder(request));
        }
    }

    internal static string FixtureJson() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Csko", "search-KSFO-KLAX.json"));

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string NewCacheDir() => Path.Combine(Path.GetTempPath(), "csko-" + Guid.NewGuid());

    private static (CskoRouteSource Source, FakeHandler Handler) NewSource(string cacheDir, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHandler { Responder = responder };
        return (new CskoRouteSource(new HttpClient(handler), cacheDir), handler);
    }

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FixtureReply_GroupsIntoDistinctFiledRoutes_WithFlightCounts()
    {
        string dir = NewCacheDir();
        try
        {
            (CskoRouteSource source, _) = NewSource(dir, _ => Ok(FixtureJson()));

            RouteSourceResult result = await source.FindAsync(new RouteQuery("KSFO", "KLAX"), CancellationToken.None);

            Assert.Null(result.FailureMessage);
            Assert.Equal(
                [(SstikIrnmn, 11), ("SSTIK5 SUSEY EBAYE BURGL BAYST1", 1), ("WESLA5 SUSEY EBAYE BURGL IRNMN2", 2)],
                result.Routes.Select(r => (r.RouteText, r.Count)).ToList()
            );
            Assert.All(result.Routes, r => Assert.Equal(["csko"], r.Sources));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task RequestUrl_CarriesOptionalFiltersOnlyWhenSet()
    {
        string dir = NewCacheDir();
        try
        {
            (CskoRouteSource source, FakeHandler handler) = NewSource(dir, _ => Ok("[]"));

            await source.FindAsync(new RouteQuery("KSFO", "KLAX"), CancellationToken.None);
            await source.FindAsync(new RouteQuery("KSFO", "KLAX", Fix: "SUSEY", AirlinePrefix: "UAL", AircraftType: "B738"), CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            string bare = handler.Requests[0].ToString();
            Assert.StartsWith("https://flight-plans.csko.hu/v1/flight_plan/search?", bare);
            Assert.Contains("departure=KSFO", bare);
            Assert.Contains("arrival=KLAX", bare);
            Assert.Contains("limit=500", bare);
            Assert.DoesNotContain("route=", bare);
            Assert.DoesNotContain("callsign=", bare);
            Assert.DoesNotContain("aircraft_type=", bare);

            string filtered = handler.Requests[1].ToString();
            Assert.Contains("route=SUSEY", filtered);
            Assert.Contains("callsign=UAL", filtered);
            Assert.Contains("aircraft_type=B738", filtered);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task NonConusDeparture_OmitsDepartureParameter_AndFiltersFlightsClientSide()
    {
        const string body = """
            [
              { "aircraft_id": "ACA791", "departure": "CYYZ", "arrival": "KLAX", "route_text": "DEDKI5 DUTEL LINNG4" },
              { "aircraft_id": "UAL2179", "departure": "KSFO", "arrival": "KLAX", "route_text": "SSTIK5 SUSEY EBAYE BURGL IRNMN2" }
            ]
            """;
        string dir = NewCacheDir();
        try
        {
            (CskoRouteSource source, FakeHandler handler) = NewSource(dir, _ => Ok(body));

            RouteSourceResult result = await source.FindAsync(new RouteQuery("CYYZ", "KLAX"), CancellationToken.None);

            string url = handler.Requests.Single().ToString();
            Assert.DoesNotContain("departure=", url);
            Assert.Contains("arrival=KLAX", url);
            FoundRoute route = Assert.Single(result.Routes);
            Assert.Equal("DEDKI5 DUTEL LINNG4", route.RouteText);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task SecondCallWithinTtl_MakesNoHttpRequest()
    {
        string dir = NewCacheDir();
        try
        {
            (CskoRouteSource source, FakeHandler handler) = NewSource(dir, _ => Ok(FixtureJson()));
            var query = new RouteQuery("KSFO", "KLAX");

            await source.FindAsync(query, CancellationToken.None);
            RouteSourceResult second = await source.FindAsync(query, CancellationToken.None);

            Assert.Single(handler.Requests);
            Assert.Equal(3, second.Routes.Count);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task HttpFailure_WithStaleCache_ReturnsStaleRoutesAndFailureMessage()
    {
        string dir = NewCacheDir();
        try
        {
            bool fail = false;
            (CskoRouteSource source, _) = NewSource(dir, _ => fail ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Ok(FixtureJson()));
            var query = new RouteQuery("KSFO", "KLAX");
            await source.FindAsync(query, CancellationToken.None);
            foreach (string file in Directory.GetFiles(dir))
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromDays(2));
            }

            fail = true;
            RouteSourceResult result = await source.FindAsync(query, CancellationToken.None);

            Assert.Equal("csko did not answer the route search; showing its last cached reply", result.FailureMessage);
            Assert.Equal(3, result.Routes.Count);
            Assert.Equal(SstikIrnmn, result.Routes[0].RouteText);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task NotFound_WithStaleCache_ReturnsStaleRoutesAndFailureMessage()
    {
        string dir = NewCacheDir();
        try
        {
            bool notFound = false;
            (CskoRouteSource source, _) = NewSource(dir, _ => notFound ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(FixtureJson()));
            var query = new RouteQuery("KSFO", "KLAX");
            await source.FindAsync(query, CancellationToken.None);
            foreach (string file in Directory.GetFiles(dir))
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromDays(2));
            }

            notFound = true;
            RouteSourceResult result = await source.FindAsync(query, CancellationToken.None);

            Assert.Equal("csko answered HTTP 404; showing its last cached reply", result.FailureMessage);
            Assert.Equal(3, result.Routes.Count);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task NotFound_WithNoCache_ReturnsNoRoutesAndFailureMessage()
    {
        string dir = NewCacheDir();
        try
        {
            (CskoRouteSource source, _) = NewSource(dir, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

            RouteSourceResult result = await source.FindAsync(new RouteQuery("KSFO", "KLAX"), CancellationToken.None);

            Assert.Equal("csko answered HTTP 404 for the route search", result.FailureMessage);
            Assert.Empty(result.Routes);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task UnreadableReply_IsReported_AndNotCached()
    {
        string dir = NewCacheDir();
        try
        {
            (CskoRouteSource source, FakeHandler handler) = NewSource(dir, _ => Ok("<html><body>Bad gateway</body></html>"));
            var query = new RouteQuery("KSFO", "KLAX");

            RouteSourceResult result = await source.FindAsync(query, CancellationToken.None);

            Assert.Equal("csko returned an unreadable reply", result.FailureMessage);
            Assert.Empty(result.Routes);
            Assert.Empty(Directory.GetFiles(dir));
            await source.FindAsync(query, CancellationToken.None);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task HttpFailure_WithNoCache_ReturnsNoRoutesAndFailureMessage()
    {
        string dir = NewCacheDir();
        try
        {
            (CskoRouteSource source, _) = NewSource(dir, _ => throw new HttpRequestException("connection refused"));

            RouteSourceResult result = await source.FindAsync(new RouteQuery("KSFO", "KLAX"), CancellationToken.None);

            Assert.Equal("csko did not answer the route search", result.FailureMessage);
            Assert.Empty(result.Routes);
        }
        finally
        {
            DeleteDir(dir);
        }
    }
}
