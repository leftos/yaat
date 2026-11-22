using System.Net;
using Xunit;
using Yaat.Sim.Authoring.Routes;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="RouteHistorySource"/> asks the connected YAAT server for the routes its SWIM route history holds. HTTP goes to
/// a stub handler, never the network; these pin the request, the rows it turns into routes and every failure it reports
/// instead of throwing.
/// </summary>
public class RouteHistorySourceTests
{
    private const string SstikIrnmn = "SSTIK5 SUSEY EBAYE BURGL IRNMN2";

    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(5);

    private static readonly RouteQuery SfoLax = new("KSFO", "KLAX", [], [], null);

    private static readonly RouteHistoryEndpoint Server = new(new Uri("http://localhost:5130"), "token-abc");

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; init; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public List<HttpRequestMessage> Requests { get; } = [];

        public TaskCompletionSource RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestReceived.TrySetResult();
            return Responder(request, cancellationToken);
        }
    }

    private static StubHandler Replying(HttpStatusCode status, string body) =>
        new() { Responder = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }) };

    private static async Task<T> Guarded<T>(Task<T> task, string what)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(HangGuard, TestContext.Current.CancellationToken));
        if (finished != task)
        {
            Assert.Fail($"Hung: {what}");
        }

        return await task;
    }

    private static async Task Guarded(Task task, string what)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(HangGuard, TestContext.Current.CancellationToken));
        if (finished != task)
        {
            Assert.Fail($"Hung: {what}");
        }

        await task;
    }

    private static Task<RouteSourceResult> Find(StubHandler handler, RouteQuery query) =>
        new RouteHistorySource(new HttpClient(handler), () => Server).FindAsync(query, CancellationToken.None);

    [Fact]
    public async Task NotConnected_GivesNoRoutes_AndSaysSo_WithNoRequest()
    {
        var handler = new StubHandler();
        var source = new RouteHistorySource(new HttpClient(handler), () => null);

        RouteSourceResult result = await source.FindAsync(SfoLax, CancellationToken.None);

        Assert.Equal("history", source.Name);
        Assert.Empty(result.Routes);
        Assert.Equal("Not connected to a YAAT server.", result.FailureMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OkWithRows_GivesRoutesWithTheirObservedSample_AndSendsTheQueryWithTheBearerToken()
    {
        const string body = $$"""
            {
              "routes": [
                { "route": "{{SstikIrnmn}}", "count": 12, "airlinePrefix": "UAL", "aircraftType": "B738" },
                { "route": "OFFSH9 SNS LYNSY ANJLL4", "count": 2, "airlinePrefix": null, "aircraftType": null }
              ],
              "message": null
            }
            """;
        StubHandler handler = Replying(HttpStatusCode.OK, body);

        RouteSourceResult result = await Find(handler, SfoLax with { Fix = "SUSEY", AirlinePrefix = "UAL", AircraftType = "B738" });

        Assert.Null(result.FailureMessage);
        Assert.Equal(
            [(SstikIrnmn, 12, new ObservedRouteSample("UAL", "B738")), ("OFFSH9 SNS LYNSY ANJLL4", 2, new ObservedRouteSample(null, null))],
            result.Routes.Select(r => (r.RouteText, r.Count, r.Observed))
        );
        Assert.All(
            result.Routes,
            r =>
            {
                Assert.Equal(["history"], r.Sources);
                Assert.Null(r.Published);
                Assert.Null(r.Preferred);
            }
        );

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:5130/api/route-history?dep=KSFO&dst=KLAX&fix=SUSEY&airline=UAL&type=B738", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("token-abc", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task OptionalFilters_AreLeftOutOfTheRequest_WhenNotSet()
    {
        StubHandler handler = Replying(HttpStatusCode.OK, """{"routes":[],"message":null}""");

        RouteSourceResult result = await Find(handler, SfoLax);

        Assert.Null(result.FailureMessage);
        Assert.Equal("http://localhost:5130/api/route-history?dep=KSFO&dst=KLAX", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task EmptyToken_SendsNoAuthorizationHeader()
    {
        StubHandler handler = Replying(HttpStatusCode.OK, """{"routes":[],"message":null}""");
        var source = new RouteHistorySource(new HttpClient(handler), () => Server with { AccessToken = "" });

        await source.FindAsync(SfoLax, CancellationToken.None);

        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Theory]
    [InlineData("http://example.test/yaat")]
    [InlineData("http://example.test/yaat/")]
    public void BuildUrl_KeepsTheServerBasePath(string serverBase)
    {
        Uri url = RouteHistorySource.BuildUrl(new Uri(serverBase), SfoLax);

        Assert.Equal("http://example.test/yaat/api/route-history?dep=KSFO&dst=KLAX", url.ToString());
    }

    [Fact]
    public async Task CallerTokenCancelledBeforeTheCall_Throws_WithNoRequest()
    {
        var handler = new StubHandler();
        var source = new RouteHistorySource(new HttpClient(handler), () => Server);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FindAsync(SfoLax, caller.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OkWithMessage_GivesTheMessageAsTheFailure()
    {
        StubHandler handler = Replying(HttpStatusCode.OK, """{"routes":[],"message":"Route history is not enabled on this server."}""");

        RouteSourceResult result = await Find(handler, SfoLax);

        Assert.Empty(result.Routes);
        Assert.Equal("Route history is not enabled on this server.", result.FailureMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task ErrorStatus_GivesNoRoutes_AndAFailureNamingTheStatus(HttpStatusCode status, string code)
    {
        StubHandler handler = Replying(status, "nope");

        RouteSourceResult result = await Find(handler, SfoLax);

        Assert.Empty(result.Routes);
        Assert.Contains(code, result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("""{"routes":"oops"}""")]
    [InlineData("""{"routes":[null]}""")]
    [InlineData("""{"routes":[{"count":1}]}""")]
    [InlineData("""{"routes":[null],"message":null}""")]
    [InlineData("""{"routes":[{"route":" ","count":1,"airlinePrefix":null,"aircraftType":null}],"message":null}""")]
    [InlineData("""{"routes":[{"route":"SUSEY","count":-1,"airlinePrefix":null,"aircraftType":null}],"message":null}""")]
    public async Task UnreadableJson_GivesNoRoutes_AndAFailure(string body)
    {
        StubHandler handler = Replying(HttpStatusCode.OK, body);

        RouteSourceResult result = await Find(handler, SfoLax);

        Assert.Empty(result.Routes);
        Assert.Equal("The YAAT server's route history reply is unreadable.", result.FailureMessage);
    }

    [Fact]
    public async Task UnreachableServer_GivesNoRoutes_AndAFailure()
    {
        var handler = new StubHandler { Responder = (_, _) => throw new HttpRequestException("connection refused") };

        RouteSourceResult result = await Find(handler, SfoLax);

        Assert.Empty(result.Routes);
        Assert.Contains("connection refused", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_GivesNoRoutes_AndAFailure_WithoutThrowing()
    {
        var handler = new StubHandler
        {
            Responder = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        using var timeout = new CancellationTokenSource();
        var source = new RouteHistorySource(new HttpClient(handler), () => Server) { CreateTimeout = () => timeout };

        Task<RouteSourceResult> find = source.FindAsync(SfoLax, CancellationToken.None);
        await Guarded(handler.RequestReceived.Task, "the request never reached the handler");
        await timeout.CancelAsync();
        RouteSourceResult result = await Guarded(find, "the timed-out search never returned");

        Assert.Empty(result.Routes);
        Assert.Equal("The YAAT server did not answer the route history search in time.", result.FailureMessage);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var handler = new StubHandler
        {
            Responder = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        using var caller = new CancellationTokenSource();
        var source = new RouteHistorySource(new HttpClient(handler), () => Server);

        Task<RouteSourceResult> find = source.FindAsync(SfoLax, caller.Token);
        await Guarded(handler.RequestReceived.Task, "the request never reached the handler");
        await caller.CancelAsync();

        await Guarded(Assert.ThrowsAnyAsync<OperationCanceledException>(() => find), "the cancelled search never returned");
    }
}
