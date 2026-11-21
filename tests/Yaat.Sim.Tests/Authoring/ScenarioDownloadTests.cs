using System.Net;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioDownloader"/> downloads every scenario of an ARTCC into the <see cref="LocalScenarioStore"/>, at
/// most four at once. A failed scenario is listed by id and reason while the rest still land, and a failed list call
/// leaves the disk as it was and says so rather than reporting an empty ARTCC.
/// </summary>
public class ScenarioDownloadTests
{
    private const string Zoa = TrainingFixtures.Artcc;
    private const string IdA = TrainingFixtures.IdA;
    private const string IdB = TrainingFixtures.IdB;

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, Task<HttpResponseMessage>> Responder { get; set; }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }

            return Responder(request);
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static bool IsList(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.Contains("/scenario-summaries/by-artcc/", StringComparison.Ordinal);

    private static string ScenarioIdOf(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);

    private static string Summaries(params string[] ids) =>
        new JsonArray([
            .. ids.Select(id => new JsonObject
            {
                ["id"] = id,
                ["name"] = "Summary " + id,
                ["primaryAirportId"] = "OAK",
                ["artccId"] = Zoa,
                ["minimumRating"] = null,
            }),
        ]).ToJsonString();

    private static async Task InRoot(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder, Func<string, ScenarioDownloader, Task> body)
    {
        string root = TrainingFixtures.NewRoot();
        try
        {
            var handler = new FakeHandler { Responder = responder };
            using var client = new VnasTrainingClient(new HttpClient(handler));
            await body(root, new ScenarioDownloader(client, new LocalScenarioStore(root)));
        }
        finally
        {
            TrainingFixtures.DeleteDir(root);
        }
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory
            .GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Convert.ToBase64String(File.ReadAllBytes(path)), StringComparer.Ordinal);

    [Fact]
    public Task BulkDownload_OneFetchFails_IsListedFailed_TheOthersLand() =>
        InRoot(
            request =>
                Task.FromResult(
                    IsList(request) ? Ok(Summaries(IdA, IdB))
                    : ScenarioIdOf(request) == IdB ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Ok(TrainingFixtures.Read(IdA))
                ),
            async (root, downloader) =>
            {
                DownloadReport report = await downloader.DownloadArtccAsync(Zoa, progress: null, CancellationToken.None);

                Assert.False(report.Offline);
                Assert.Equal([IdA], report.Downloaded);
                DownloadFailure failure = Assert.Single(report.Failed);
                Assert.Equal(IdB, failure.Id);
                Assert.Equal("Summary " + IdB, failure.Name);
                Assert.Contains("500", failure.Reason, StringComparison.Ordinal);
                Assert.True(File.Exists(TrainingFixtures.MirrorPath(root, IdA)));
                Assert.False(File.Exists(TrainingFixtures.MirrorPath(root, IdB)));
            }
        );

    [Fact]
    public Task BulkDownload_ListFails_ChangesNothingOnDisk_ReportsOffline() =>
        InRoot(
            request => Task.FromResult(IsList(request) ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(TrainingFixtures.Read(IdA))),
            async (root, downloader) =>
            {
                new LocalScenarioStore(root).ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                Dictionary<string, string> before = Snapshot(root);

                DownloadReport report = await downloader.DownloadArtccAsync(Zoa, progress: null, CancellationToken.None);

                Assert.True(report.Offline);
                Assert.Contains("503", report.ListFailure, StringComparison.Ordinal);
                Assert.Empty(report.Downloaded);
                Assert.Empty(report.Missing);
                Assert.Equal(before, Snapshot(root));
                Assert.False(Assert.Single(new LocalScenarioStore(root).Load(Zoa).Mirrors).Mirror.GoneFromServer);
            }
        );

    [Fact]
    public Task BulkDownload_BodyNotAScenario_IsNotWritten_FailedNotAScenario()
    {
        JsonObject noName = JsonNode.Parse(TrainingFixtures.Read(IdB))!.AsObject();
        noName.Remove("name");
        return InRoot(
            request =>
                Task.FromResult(
                    IsList(request) ? Ok(Summaries(IdA, IdB))
                    : ScenarioIdOf(request) == IdA ? Ok("{}")
                    : Ok(noName.ToJsonString())
                ),
            async (root, downloader) =>
            {
                DownloadReport report = await downloader.DownloadArtccAsync(Zoa, progress: null, CancellationToken.None);

                Assert.Empty(report.Downloaded);
                Assert.Equal([IdA, IdB], report.Failed.Select(f => f.Id));
                Assert.All(report.Failed, f => Assert.StartsWith("not a scenario", f.Reason, StringComparison.Ordinal));
                Assert.False(File.Exists(TrainingFixtures.MirrorPath(root, IdA)));
                Assert.False(File.Exists(TrainingFixtures.MirrorPath(root, IdB)));
            }
        );
    }

    [Fact]
    public Task BulkDownload_InvalidId_IsRejected_NothingWrittenOutsideTheFolder() =>
        InRoot(
            request => Task.FromResult(IsList(request) ? Ok(Summaries("../x", IdA)) : Ok(TrainingFixtures.WithId(IdA, ScenarioIdOf(request)))),
            async (root, downloader) =>
            {
                DownloadReport report = await downloader.DownloadArtccAsync(Zoa, progress: null, CancellationToken.None);

                DownloadFailure failure = Assert.Single(report.Failed);
                Assert.Equal("../x", failure.Id);
                Assert.Equal("invalid id", failure.Reason);
                Assert.Equal([IdA], report.Downloaded);
                Assert.False(File.Exists(Path.Combine(root, Zoa, "x.json")));
                Assert.False(File.Exists(Path.Combine(root, "x.json")));
                Assert.Equal([TrainingFixtures.MirrorPath(root, IdA)], Directory.GetFiles(Path.Combine(root, Zoa, "server")));
            }
        );

    [Fact]
    public async Task BulkDownload_Twice_EachIdLandsInTheListOfWhatChanged()
    {
        string idC = IdA + "-C";
        string idD = IdA + "-D";
        string idE = IdA + "-E";
        string bodyC = TrainingFixtures.WithId(IdA, idC);
        string bodyD = TrainingFixtures.WithId(IdA, idD);
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IdA] = TrainingFixtures.Read(IdA),
            [IdB] = TrainingFixtures.Read(IdB),
            [idC] = bodyC,
            [idD] = bodyD,
        };
        string[] listed = [IdA, IdB, idC, idD];

        await InRoot(
            request => Task.FromResult(IsList(request) ? Ok(Summaries(listed)) : Ok(bodies[ScenarioIdOf(request)])),
            async (root, downloader) =>
            {
                DownloadReport first = await downloader.DownloadArtccAsync(Zoa, progress: null, CancellationToken.None);
                new LocalScenarioStore(root).CreateWorkingCopy(Zoa, IdA);
                listed = [IdA, idC, idD, idE];
                bodies[IdA] = TrainingFixtures.Changed(IdA);
                bodies[idC] = TrainingFixtures.SuffixName(bodyC, " (revised on vNAS)");
                bodies[idD] = TrainingFixtures.Reorder(bodyD);
                bodies[idE] = TrainingFixtures.WithId(IdA, idE);

                DownloadReport second = await downloader.DownloadArtccAsync(Zoa, progress: null, CancellationToken.None);

                Assert.Equal([.. new[] { IdA, IdB, idC, idD }.Order(StringComparer.Ordinal)], first.Downloaded);
                Assert.Equal([idE], second.Downloaded);
                Assert.Equal([idC], second.Updated);
                Assert.Equal([idD], second.Unchanged);
                Assert.Equal([IdA], second.ServerChanged);
                Assert.Equal([IdB], second.Missing);
                Assert.Empty(second.Failed);
            }
        );
    }

    [Fact]
    public Task BulkDownload_IdListedTwice_IsFetchedOnce_AndLandsInOneList()
    {
        int fetches = 0;
        return InRoot(
            request =>
            {
                if (IsList(request))
                {
                    return Task.FromResult(Ok(Summaries(IdA, IdA)));
                }

                Interlocked.Increment(ref fetches);
                return Task.FromResult(Ok(TrainingFixtures.Read(IdA)));
            },
            async (_, downloader) =>
            {
                DownloadReport report = await downloader.DownloadArtccAsync(Zoa, progress: null, CancellationToken.None);

                Assert.Equal(1, Volatile.Read(ref fetches));
                Assert.Equal([IdA], report.Downloaded);
                Assert.Empty(report.Unchanged);
                Assert.Empty(report.Failed);
            }
        );
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource cts) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => cts.Cancel();
    }

    [Fact]
    public async Task BulkDownload_CancelledAfterTheFirstScenario_Throws_AndKeepsWhatLanded()
    {
        using var cts = new CancellationTokenSource();

        async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            if (IsList(request))
            {
                return Ok(Summaries(IdA, IdB));
            }

            if (ScenarioIdOf(request) == IdA)
            {
                return Ok(TrainingFixtures.Read(IdA));
            }

            // IdB never answers on its own: it ends only when the download is cancelled.
            await Task.Delay(Timeout.Infinite, cts.Token);
            return Ok(TrainingFixtures.Read(IdB));
        }

        await InRoot(
            Respond,
            async (root, downloader) =>
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    downloader
                        .DownloadArtccAsync(Zoa, new CancelOnFirstReport(cts), cts.Token)
                        .WaitAsync(HangGuard, TestContext.Current.CancellationToken)
                );

                Assert.True(File.Exists(TrainingFixtures.MirrorPath(root, IdA)));
                Assert.Contains(new LocalScenarioStore(root).Load(Zoa).Mirrors, m => m.Mirror.Id == IdA);
                Assert.False(File.Exists(TrainingFixtures.MirrorPath(root, IdB)));
            }
        );
    }

    [Fact]
    public async Task BulkDownload_IsBoundedParallel()
    {
        string[] ids = [.. Enumerable.Range(1, 6).Select(i => $"{IdA}-{i}")];
        int inFlight = 0;
        int maxInFlight = 0;
        int arrived = 0;
        var fourArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            if (IsList(request))
            {
                return Ok(Summaries(ids));
            }

            int now = Interlocked.Increment(ref inFlight);
            int seen;
            do
            {
                seen = Volatile.Read(ref maxInFlight);
            } while ((now > seen) && (Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen));

            int count = Interlocked.Increment(ref arrived);
            if (count == 4)
            {
                fourArrived.TrySetResult();
            }

            if (count >= 6)
            {
                release.TrySetResult();
            }

            await release.Task;
            Interlocked.Decrement(ref inFlight);
            return Ok(TrainingFixtures.WithId(IdA, ScenarioIdOf(request)));
        }

        await InRoot(
            Respond,
            async (_, downloader) =>
            {
                Task<DownloadReport> run = downloader.DownloadArtccAsync(Zoa, progress: null, CancellationToken.None);
                await fourArrived.Task.WaitAsync(HangGuard);
                release.TrySetResult();
                DownloadReport report = await run.WaitAsync(HangGuard);

                // Exactly 4 is certain under a correct bound; a bound above 4 is caught only if its 5th worker arrives first.
                Assert.Equal(4, Volatile.Read(ref maxInFlight));
                Assert.Equal(ids, report.Downloaded);
                Assert.Empty(report.Failed);
            }
        );
    }
}
