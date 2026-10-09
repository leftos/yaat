using System.Net;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;

namespace Yaat.PrecomputeCache.Tests;

/// <summary>
/// The tool's command line, run in process with the vNAS boundary stood in for by a handler that answers each request
/// from a table (anything else with one status), and navdata loading made to fail the test if a run reaches it.
/// </summary>
public class CliTests
{
    private const string OakMapUrl = "https://data-api.vnas.vatsim.net/api/training/airports/OAK/map";

    private const string SfoMapUrl = "https://data-api.vnas.vatsim.net/api/training/airports/SFO/map";

    private const string SummariesUrl = RefreshAirportsCommand.TrainingApi + "/scenario-summaries/by-artcc/";

    private const string ScenarioUrl = RefreshAirportsCommand.TrainingApi + "/scenarios/";

    [Fact]
    public async Task Cli_UnknownFlag_Exits2()
    {
        using var run = new Run(HttpStatusCode.NotFound);

        int code = await PrecomputeCacheCli.RunAsync(["--check", "--bogus"], run.Services);

        Assert.Equal(PrecomputeCacheCli.UsageError, code);
        Assert.Contains("Unknown argument '--bogus'", run.ErrorText, StringComparison.Ordinal);
        Assert.Contains("Usage: dotnet run --project tools/Yaat.PrecomputeCache", run.ErrorText, StringComparison.Ordinal);
        Assert.Empty(run.Handler.Requests);
    }

    /// <summary>
    /// vNAS answers 404 for the airport's map: the run fails, names the airport and the 404, writes no file under the root,
    /// asks vNAS for exactly that map and never loads navdata.
    /// </summary>
    [Fact]
    public async Task Cli_AirportWithNoVnasMap_Reports404AndWritesNothing()
    {
        using var run = new Run(HttpStatusCode.NotFound);

        int code = await PrecomputeCacheCli.RunAsync(["--airport", "KO32", "--root", run.Root], run.Services);

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Contains("O32: vNAS has no ground map (HTTP 404); nothing written", run.ErrorText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(run.Root) && Directory.EnumerateFileSystemEntries(run.Root).Any(), $"{run.Root} is not empty");
        Assert.Equal(["https://data-api.vnas.vatsim.net/api/training/airports/O32/map"], run.Handler.Requests);
    }

    /// <summary>
    /// The map is served, but the live NavData serial is not confirmed: vNAS's configuration cannot be read, or it names a
    /// serial other than the one loaded (a cached copy after a failed download). The run fails and writes nothing; with
    /// no live serial it never loads navdata at all.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", "The live vNAS NavData serial could not be read")]
    [InlineData(HttpStatusCode.OK, """{"navDataSerial": 200}""", "vNAS publishes NavData serial 200, but only serial 100 could be loaded")]
    public async Task Cli_OnlyCachedNavDataSerial_FailsAndWritesNothing(HttpStatusCode configStatus, string configBody, string expected)
    {
        using var run = new Run(HttpStatusCode.NotFound);
        run.Handler.Answer(OakMapUrl, HttpStatusCode.OK, """{"type": "FeatureCollection", "features": []}""");
        run.Handler.Answer(VnasConfig.Url, configStatus, configBody);
        int loads = 0;
        PrecomputeCacheServices services = run.Services with
        {
            InitializeNavigationAsync = () =>
            {
                Interlocked.Increment(ref loads);
                return Task.FromResult(100L);
            },
        };

        int code = await PrecomputeCacheCli.RunAsync(["--airport", "KOAK", "--root", run.Root], services);

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Contains(expected, run.ErrorText, StringComparison.Ordinal);
        Assert.Equal(configStatus == HttpStatusCode.OK ? 1 : 0, loads);
        Assert.False(Directory.Exists(run.Root) && Directory.EnumerateFileSystemEntries(run.Root).Any(), $"{run.Root} is not empty");
    }

    /// <summary>
    /// Loading navdata throws something other than the tool's own failure: the run fails once for the whole run, with no
    /// per-airport failure lines, however many airports were waiting on it.
    /// </summary>
    [Fact]
    public async Task Cli_NavDataLoadThrows_FailsOnceForTheRun()
    {
        using var run = new Run(HttpStatusCode.NotFound);
        run.Handler.Answer(OakMapUrl, HttpStatusCode.OK, """{"type": "FeatureCollection", "features": []}""");
        run.Handler.Answer(SfoMapUrl, HttpStatusCode.OK, """{"type": "FeatureCollection", "features": []}""");
        run.Handler.Answer(VnasConfig.Url, HttpStatusCode.OK, """{"navDataSerial": 100}""");
        PrecomputeCacheServices services = run.Services with
        {
            InitializeNavigationAsync = () => throw new InvalidOperationException("No CIFP could be downloaded"),
        };

        int code = await PrecomputeCacheCli.RunAsync(["--airport", "OAK", "--airport", "SFO", "--root", run.Root], services);

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Equal(1, CountOf("No CIFP could be downloaded", run.ErrorText));
        Assert.DoesNotContain("the compute failed", run.ErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("airports failed", run.ErrorText, StringComparison.Ordinal);
    }

    /// <summary>
    /// One airport has no vNAS map and the other's map is not GeoJSON: the run works on both, one at a time, names each
    /// failure, and fails with the count.
    /// </summary>
    [Fact]
    public async Task Cli_FailedAirport_RunContinuesAndNamesEach()
    {
        using var run = new Run(HttpStatusCode.NotFound);
        run.Handler.Answer(OakMapUrl, HttpStatusCode.OK, "this is not GeoJSON");
        run.Handler.Answer(VnasConfig.Url, HttpStatusCode.OK, """{"navDataSerial": 100}""");
        PrecomputeCacheServices services = run.Services with
        {
            ArtccsDir = Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs"),
            InitializeNavigationAsync = () => Task.FromResult(100L),
        };

        int code = await PrecomputeCacheCli.RunAsync(
            ["--airport", "OAK", "--airport", "O32", "--airports-in-flight", "1", "--root", run.Root],
            services
        );

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Contains("O32: vNAS has no ground map (HTTP 404); nothing written", run.ErrorText, StringComparison.Ordinal);
        Assert.Contains("OAK: the compute failed; nothing written for it", run.ErrorText, StringComparison.Ordinal);
        Assert.Contains("2 of 2 airports failed: O32 OAK", run.ErrorText, StringComparison.Ordinal);
        Assert.Contains("https://data-api.vnas.vatsim.net/api/training/airports/O32/map", run.Handler.Requests);
        Assert.Contains(OakMapUrl, run.Handler.Requests);
    }

    /// <summary>A listed airport with no entry is a GitHub warning, not a failure.</summary>
    [Fact]
    public async Task Cli_CheckMissingEntry_WarnsAndExits0()
    {
        using var run = new Run(HttpStatusCode.NotFound);
        PrecomputeAirportList.Write(Path.Combine(run.Root, PrecomputeAirportList.FileName), ["OAK"]);

        int code = await PrecomputeCacheCli.RunAsync(["--check", "--root", run.Root], run.Services);

        Assert.Equal(PrecomputeCacheCli.Ok, code);
        Assert.Contains($"::warning::OAK: missing: no entry at {Path.Combine(run.Root, "OAK.json.br")}", run.OutText, StringComparison.Ordinal);
        Assert.Contains("1 of 1 airports checked (offline) have a missing or stale entry", run.OutText, StringComparison.Ordinal);
        Assert.Empty(run.Handler.Requests);
    }

    /// <summary>An airport list that names no airport is a broken list, so the check fails rather than pass on nothing.</summary>
    [Fact]
    public async Task Cli_CheckEmptyAirportList_Exits1()
    {
        using var run = new Run(HttpStatusCode.NotFound);
        PrecomputeAirportList.Write(Path.Combine(run.Root, PrecomputeAirportList.FileName), []);

        int code = await PrecomputeCacheCli.RunAsync(["--check", "--root", run.Root], run.Services);

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Contains("names no airport", run.ErrorText, StringComparison.Ordinal);
    }

    /// <summary>An entry file that does not decode is a GitHub error and fails the check.</summary>
    [Fact]
    public async Task Cli_CheckUnreadableEntry_ErrorsAndExits1()
    {
        using var run = new Run(HttpStatusCode.NotFound);
        Directory.CreateDirectory(run.Root);
        File.WriteAllBytes(Path.Combine(run.Root, "OAK.json.br"), [1, 2, 3]);

        int code = await PrecomputeCacheCli.RunAsync(["--check", "--airport", "KOAK", "--root", run.Root], run.Services);

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Contains("::error::OAK: unreadable", run.OutText, StringComparison.Ordinal);
        Assert.Contains("1 of 1 airports checked (offline) have an unreadable entry", run.ErrorText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scenarios name only airports vNAS has no ground map for: the refresh fails, says the list is left as it was, and
    /// leaves the existing list file byte for byte.
    /// </summary>
    [Fact]
    public async Task Cli_RefreshAirportsWithNoMappedAirport_FailsAndLeavesTheList()
    {
        using var run = new Run(HttpStatusCode.NotFound);
        run.Handler.AnswerPrefix(SummariesUrl, HttpStatusCode.OK, "[]");
        run.Handler.Answer(SummariesUrl + "ZOA", HttpStatusCode.OK, """[{"id": "s1"}]""");
        run.Handler.Answer(ScenarioUrl + "s1", HttpStatusCode.OK, """{"id": "s1", "name": "No maps", "primaryAirportId": "O32", "aircraft": []}""");
        string listPath = Path.Combine(run.Root, PrecomputeAirportList.FileName);
        PrecomputeAirportList.Write(listPath, ["OAK"]);
        byte[] before = File.ReadAllBytes(listPath);

        int code = await PrecomputeCacheCli.RunAsync(["--refresh-airports", "--root", run.Root], run.Services);

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Contains(
            "None of the 1 airports the 1 scenarios name has a vNAS ground map; the airport list is left as it was",
            run.ErrorText,
            StringComparison.Ordinal
        );
        Assert.Equal(before, File.ReadAllBytes(listPath));
        Assert.Contains("https://data-api.vnas.vatsim.net/api/training/airports/O32/map", run.Handler.Requests);
    }

    /// <summary>
    /// One scenario fetch fails while a sibling is in flight: the sibling is cancelled by the failure, and the run reports
    /// the failure alone, with no "timed out" line for the cancelled sibling.
    /// </summary>
    [Fact]
    public async Task Cli_RefreshAirportsSiblingCancelledByFailure_PrintsNoTimedOutLine()
    {
        using var run = new Run(HttpStatusCode.NotFound);
        run.Handler.AnswerPrefix(SummariesUrl, HttpStatusCode.OK, "[]");
        run.Handler.Answer(SummariesUrl + "ZOA", HttpStatusCode.OK, """[{"id": "fails"}, {"id": "hangs"}]""");
        var hangsArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Handler.Respond(
            ScenarioUrl + "hangs",
            async cancellationToken =>
            {
                hangsArrived.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("The hanging request was never cancelled");
            }
        );
        run.Handler.Respond(
            ScenarioUrl + "fails",
            async _ =>
            {
                await hangsArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
        );

        int code = await PrecomputeCacheCli.RunAsync(["--refresh-airports", "--root", run.Root], run.Services);

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Contains($"GET {ScenarioUrl}fails failed", run.ErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain("timed out", run.ErrorText, StringComparison.Ordinal);
        Assert.Contains(ScenarioUrl + "hangs", run.Handler.Requests);
    }

    /// <summary>
    /// A scenario fetch outlasts the HTTP client's timeout: the run fails with a "timed out" line naming the URL, the one
    /// case a cancelled fetch is reported as a timeout.
    /// </summary>
    [Fact]
    public async Task Cli_RefreshAirportsFetchTimesOut_ReportsTimedOut()
    {
        using var run = new Run(HttpStatusCode.NotFound);
        run.Services.Http.Timeout = TimeSpan.FromMilliseconds(200);
        run.Handler.AnswerPrefix(SummariesUrl, HttpStatusCode.OK, "[]");
        run.Handler.Answer(SummariesUrl + "ZOA", HttpStatusCode.OK, """[{"id": "slow"}]""");
        run.Handler.Respond(
            ScenarioUrl + "slow",
            async cancellationToken =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("The slow request never timed out");
            }
        );

        int code = await PrecomputeCacheCli.RunAsync(["--refresh-airports", "--root", run.Root], run.Services);

        Assert.Equal(PrecomputeCacheCli.Failed, code);
        Assert.Contains($"GET {ScenarioUrl}slow timed out", run.ErrorText, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string within) => within.Split(text, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// Answers each request from a responder or a table of URLs, then a table of URL prefixes, anything else with one
    /// status, and records the URLs asked for.
    /// </summary>
    private sealed class TableHandler(HttpStatusCode otherwise) : HttpMessageHandler
    {
        private readonly List<string> _requests = [];
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);
        private readonly List<(string Prefix, HttpStatusCode Status, string Body)> _prefixAnswers = [];
        private readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _responders = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public void Answer(string url, HttpStatusCode status, string body) => _answers[url] = (status, body);

        public void AnswerPrefix(string prefix, HttpStatusCode status, string body) => _prefixAnswers.Add((prefix, status, body));

        public void Respond(string url, Func<CancellationToken, Task<HttpResponseMessage>> responder) => _responders[url] = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            lock (_requests)
            {
                _requests.Add(url);
            }

            if (_responders.TryGetValue(url, out Func<CancellationToken, Task<HttpResponseMessage>>? responder))
            {
                return responder(cancellationToken);
            }

            (HttpStatusCode status, string body) = AnswerFor(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }

        private (HttpStatusCode Status, string Body) AnswerFor(string url)
        {
            if (_answers.TryGetValue(url, out (HttpStatusCode Status, string Body) answer))
            {
                return answer;
            }

            foreach ((string prefix, HttpStatusCode status, string body) in _prefixAnswers)
            {
                if (url.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return (status, body);
                }
            }

            return (otherwise, "");
        }
    }

    /// <summary>One run's services, temporary folders and captured output.</summary>
    private sealed class Run : IDisposable
    {
        private readonly string _scratch = Path.Combine(Path.GetTempPath(), "precompute-cli-" + Guid.NewGuid());
        private readonly StringWriter _out = new();
        private readonly StringWriter _error = new();
        private readonly HttpClient _http;

        public Run(HttpStatusCode otherwise)
        {
            Handler = new TableHandler(otherwise);
            _http = new HttpClient(Handler);
            Services = new PrecomputeCacheServices
            {
                Http = _http,
                GeoJsonCacheDir = Path.Combine(_scratch, "geojson-cache"),
                ArtccsDir = Path.Combine(_scratch, "artccs"),
                WorkingDirectory = _scratch,
                InitializeNavigationAsync = () => throw new InvalidOperationException("The run loaded navdata"),
                LoadFaaRecordsAsync = () =>
                    Task.FromResult<IReadOnlyDictionary<string, FaaAircraftRecord>>(new Dictionary<string, FaaAircraftRecord>()),
                Out = _out,
                Error = _error,
            };
        }

        public TableHandler Handler { get; }

        public PrecomputeCacheServices Services { get; }

        public string Root => Path.Combine(_scratch, "cache");

        public string OutText => _out.ToString();

        public string ErrorText => _error.ToString();

        public void Dispose()
        {
            _http.Dispose();
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
    }
}
