using System.Net;
using Xunit;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="RouteFinder"/> asks every route source at once, merges the routes they return, and offers only the routes
/// <see cref="RouteExpander"/> expands to fixes the committed navdata knows. Sources are small stubs, except the last
/// case, which runs the recorded csko reply through <see cref="CskoRouteSource"/>.
/// </summary>
public class RouteFinderTests
{
    private const string SstikIrnmn = "SSTIK5 SUSEY EBAYE BURGL IRNMN2";
    private const string WeslaIrnmn = "WESLA5 SUSEY EBAYE BURGL IRNMN2";

    private static readonly RouteQuery SfoLax = new("KSFO", "KLAX");

    public RouteFinderTests() => TestVnasData.EnsureInitialized();

    private sealed class StubSource(string name, params FoundRoute[] routes) : IRouteSource
    {
        public string Name => name;

        public Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RouteSourceResult(routes, FailureMessage: null));
        }
    }

    private sealed class ThrowingSource(string name, string message) : IRouteSource
    {
        public string Name => name;

        public Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct) => throw new HttpRequestException(message);
    }

    private sealed class TimingOutSource : IRouteSource
    {
        public string Name => "slow";

        public Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
    }

    private sealed class BlockingSource : IRouteSource
    {
        public string Name => "blocking";

        public async Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new RouteSourceResult([], FailureMessage: null);
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CskoRouteSourceTests.FixtureJson()) });
    }

    private static FoundRoute Route(string text, int count, string source) => new(text, count, [source]);

    private static IEnumerable<string> UnresolvedTokens(string route)
    {
        List<string> expanded = RouteExpander.Expand(route, NavigationDatabase.Instance, includeAllTransitionsOnMismatch: false);
        return expanded.Count == 0 ? ["(empty expansion)"] : expanded.Where(t => NavigationDatabase.Instance.GetFixPosition(t) is null);
    }

    private static RouteFinder Finder(params IRouteSource[] sources) => new(sources, NavigationDatabase.Instance);

    [Fact]
    public async Task DuplicatesAcrossSources_Merge_SummingCountsAndUnioningSources()
    {
        RouteFinder finder = Finder(
            new StubSource("a", Route(SstikIrnmn, 3, "a")),
            new StubSource("b", Route("sstik5  susey EBAYE burgl IRNMN2", 2, "b"))
        );

        RouteFindResult result = await finder.FindAsync(SfoLax, CancellationToken.None);

        FoundRoute merged = Assert.Single(result.Routes);
        Assert.Equal(SstikIrnmn, merged.RouteText);
        Assert.Equal(5, merged.Count);
        Assert.Equal(["a", "b"], merged.Sources);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task AirportBookendsAndDct_DoNotMakeRoutesDistinct()
    {
        RouteFinder finder = Finder(
            new StubSource("a", Route("KSFO SSTIK5 SUSEY EBAYE BURGL IRNMN2 KLAX", 1, "a")),
            new StubSource("b", Route(SstikIrnmn, 1, "b"), Route("SSTIK5 DCT SUSEY EBAYE BURGL IRNMN2", 1, "b"))
        );

        RouteFindResult result = await finder.FindAsync(SfoLax, CancellationToken.None);

        FoundRoute merged = Assert.Single(result.Routes);
        Assert.Equal("KSFO SSTIK5 SUSEY EBAYE BURGL IRNMN2 KLAX", merged.RouteText);
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public async Task RouteWithUnknownFix_IsDropped()
    {
        RouteFinder finder = Finder(new StubSource("a", Route("SSTIK5 NOTAFX IRNMN2", 4, "a"), Route(SstikIrnmn, 1, "a")));

        RouteFindResult result = await finder.FindAsync(SfoLax, CancellationToken.None);

        FoundRoute offered = Assert.Single(result.Routes);
        Assert.Equal(SstikIrnmn, offered.RouteText);
    }

    [Fact]
    public async Task ThrowingSource_YieldsOneFailure_AndTheOtherSourcesRoutes()
    {
        RouteFinder finder = Finder(new ThrowingSource("down", "csko is down"), new StubSource("up", Route(SstikIrnmn, 1, "up")));

        RouteFindResult result = await finder.FindAsync(SfoLax, CancellationToken.None);

        RouteSourceFailure failure = Assert.Single(result.Failures);
        Assert.Equal("down", failure.Source);
        Assert.Contains("csko is down", failure.Message);
        Assert.Equal(SstikIrnmn, Assert.Single(result.Routes).RouteText);
    }

    [Fact]
    public async Task SourceOwnTimeout_IsAFailure_NotACancellation()
    {
        RouteFinder finder = Finder(new TimingOutSource(), new StubSource("up", Route(SstikIrnmn, 1, "up")));

        RouteFindResult result = await finder.FindAsync(SfoLax, CancellationToken.None);

        Assert.Equal("slow", Assert.Single(result.Failures).Source);
        Assert.Equal(SstikIrnmn, Assert.Single(result.Routes).RouteText);
    }

    [Fact]
    public async Task Cancellation_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        RouteFinder finder = Finder(new BlockingSource(), new StubSource("up", Route(SstikIrnmn, 1, "up")));

        Task<RouteFindResult> find = finder.FindAsync(SfoLax, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => find);
    }

    [Fact]
    public async Task Routes_AreOrderedByCountThenFirstSeen()
    {
        RouteFinder finder = Finder(
            new StubSource("a", Route(WeslaIrnmn, 2, "a"), Route("SSTIK5 SUSEY EBAYE BURGL BAYST1", 2, "a")),
            new StubSource("b", Route(SstikIrnmn, 5, "b"))
        );

        RouteFindResult result = await finder.FindAsync(SfoLax, CancellationToken.None);

        Assert.Equal([SstikIrnmn, WeslaIrnmn, "SSTIK5 SUSEY EBAYE BURGL BAYST1"], result.Routes.Select(r => r.RouteText).ToList());
    }

    [Fact]
    public async Task CskoFixtureRoutes_SurviveTheExpandableCheck_OnCommittedNavdata()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csko-" + Guid.NewGuid());
        try
        {
            using var source = new CskoRouteSource(new HttpClient(new FixtureHandler()), dir);
            RouteSourceResult raw = await source.FindAsync(SfoLax, CancellationToken.None);

            RouteFindResult result = await Finder(source).FindAsync(SfoLax, CancellationToken.None);

            List<string> offered = [.. result.Routes.Select(r => r.RouteText)];
            List<string> dropped = [.. raw.Routes.Select(r => r.RouteText).Except(offered)];
            Assert.True(
                dropped.Count == 0,
                "Dropped: " + string.Join("; ", dropped.Select(d => $"{d}: unresolved [{string.Join(' ', UnresolvedTokens(d))}]"))
            );
            Assert.Contains(SstikIrnmn, offered);
            Assert.Empty(result.Failures);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
