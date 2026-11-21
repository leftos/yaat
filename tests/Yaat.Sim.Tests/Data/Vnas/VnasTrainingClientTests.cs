using System.Net;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Authoring;

namespace Yaat.Sim.Tests.Data.Vnas;

/// <summary>
/// <see cref="VnasTrainingClient"/> reads scenario summaries and scenarios from the vNAS data-api. It only reads, so no
/// request it sends carries a token.
/// </summary>
public class VnasTrainingClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(Responder(request));
        }
    }

    private static string SummariesJson() =>
        new JsonArray(
            new JsonObject
            {
                ["id"] = TrainingFixtures.IdA,
                ["name"] = TrainingFixtures.NameOf(TrainingFixtures.IdA),
                ["primaryAirportId"] = "OAK",
                ["artccId"] = TrainingFixtures.Artcc,
                ["minimumRating"] = null,
            }
        ).ToJsonString();

    private static VnasTrainingClient ClientAnswering(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new FakeHandler { Responder = responder }));

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Summaries_NullBody_IsAFailure_NotAnEmptyList()
    {
        using VnasTrainingClient client = ClientAnswering(_ => Ok("null"));

        VnasFetchResult<IReadOnlyList<TrainingScenarioSummary>> result = await client.GetScenarioSummariesAsync("ZOA", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("null", result.Failure, StringComparison.Ordinal);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Summaries_MalformedJson_IsAFailure()
    {
        using VnasTrainingClient client = ClientAnswering(_ => Ok("{ not json"));

        VnasFetchResult<IReadOnlyList<TrainingScenarioSummary>> result = await client.GetScenarioSummariesAsync("ZOA", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("unreadable", result.Failure, StringComparison.Ordinal);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Summaries_NullElement_IsSkipped_TheRestListed()
    {
        string body = "[null," + SummariesJson()[1..];
        using VnasTrainingClient client = ClientAnswering(_ => Ok(body));

        VnasFetchResult<IReadOnlyList<TrainingScenarioSummary>> result = await client.GetScenarioSummariesAsync(
            TrainingFixtures.Artcc,
            CancellationToken.None
        );

        Assert.True(result.Succeeded);
        Assert.Equal(TrainingFixtures.IdA, Assert.Single(result.Value).Id);
    }

    [Fact]
    public async Task NetworkException_IsAFailure_NotThrown()
    {
        using VnasTrainingClient client = ClientAnswering(_ => throw new HttpRequestException("no route to host"));

        VnasFetchResult<string> result = await client.GetScenarioJsonAsync(TrainingFixtures.IdA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be reached", result.Failure, StringComparison.Ordinal);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Timeout_WhileTheCallersTokenIsLive_IsAFailure_NotThrown()
    {
        using VnasTrainingClient client = ClientAnswering(_ => throw new TaskCanceledException("timed out"));

        VnasFetchResult<string> result = await client.GetScenarioJsonAsync(TrainingFixtures.IdA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("did not answer", result.Failure, StringComparison.Ordinal);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Requests_CarryNoAuthorizationHeader()
    {
        var handler = new FakeHandler
        {
            Responder = request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.Contains("scenario-summaries", StringComparison.Ordinal)
                        ? SummariesJson()
                        : TrainingFixtures.Read(TrainingFixtures.IdA)
                ),
            },
        };
        using var client = new VnasTrainingClient(new HttpClient(handler));

        VnasFetchResult<IReadOnlyList<TrainingScenarioSummary>> summaries = await client.GetScenarioSummariesAsync(
            TrainingFixtures.Artcc,
            CancellationToken.None
        );
        VnasFetchResult<string> scenario = await client.GetScenarioJsonAsync(TrainingFixtures.IdA, CancellationToken.None);

        Assert.True(summaries.Succeeded);
        TrainingScenarioSummary summary = Assert.Single(summaries.Value!);
        Assert.Equal(TrainingFixtures.IdA, summary.Id);
        Assert.Equal("OAK", summary.PrimaryAirportId);
        Assert.True(scenario.Succeeded);
        Assert.Equal(TrainingFixtures.Read(TrainingFixtures.IdA), scenario.Value);
        Assert.Equal(
            [
                "https://data-api.vnas.vatsim.net/api/training/scenario-summaries/by-artcc/ZOA",
                $"https://data-api.vnas.vatsim.net/api/training/scenarios/{TrainingFixtures.IdA}",
            ],
            handler.Requests.Select(r => r.RequestUri!.AbsoluteUri)
        );
        Assert.All(
            handler.Requests,
            r =>
            {
                Assert.Null(r.Headers.Authorization);
                Assert.False(r.Headers.Contains("Authorization"));
            }
        );
    }
}
