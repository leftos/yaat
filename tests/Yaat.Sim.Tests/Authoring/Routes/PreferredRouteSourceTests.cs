using System.IO.Compression;
using System.Net;
using System.Text;
using Xunit;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="PreferredRouteSource"/> downloads the FAA NASR preferred-routes file once per AIRAC cycle, caches it, and
/// answers each airport pair from it. The cycle is passed in (never the clock), HTTP goes to a fake handler, and the cache
/// is a temp folder; the routes come from the committed real extract.
/// </summary>
public class PreferredRouteSourceTests
{
    private const string Cycle = "2612";
    private const string CycleUrl = "https://nfdc.faa.gov/webContent/28DaySub/extra/26_Nov_2026_PFR_CSV.zip";
    private const string UnreadableMessage = "Preferred routes file unreadable";
    private const string BurLgbA = "VNY V186 ADAMM V394 SLI";
    private const string BurLgbB = "TWINE V518 KIMMO V459 SLI";

    private static readonly RouteQuery SfoDen = new("KSFO", "KDEN", [], [], null);

    public PreferredRouteSourceTests() => Yaat.Sim.Testing.TestVnasData.EnsureAircraftDataInitialized();

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

    private static string ExtractCsv() => File.ReadAllText(PreferredRouteParserTests.ExtractPath());

    private static HttpResponseMessage ZipReply(string csv)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("PFR_BASE.csv").Open(), Encoding.ASCII);
            writer.Write(csv);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(buffer.ToArray()) };
    }

    private static HttpResponseMessage Refused(HttpRequestMessage request) => throw new HttpRequestException("connection refused");

    private static string NewCacheDir() => Path.Combine(Path.GetTempPath(), "prefroutes-" + Guid.NewGuid());

    private static void Cache(string dir, string cycleId, string csv)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, cycleId + ".csv"), csv);
    }

    private static (PreferredRouteSource Source, FakeHandler Handler) NewSource(string dir, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHandler { Responder = responder };
        return (new PreferredRouteSource(new HttpClient(handler), dir, NavigationDatabase.Instance, () => Cycle), handler);
    }

    private static async Task<(RouteSourceResult Result, FakeHandler Handler)> FindIn(
        string dir,
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        RouteQuery query
    )
    {
        (PreferredRouteSource source, FakeHandler handler) = NewSource(dir, responder);
        using (source)
        {
            return (await source.FindAsync(query, CancellationToken.None), handler);
        }
    }

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentCycleCached_AnswersFromDisk_WithNoHttpRequest()
    {
        string dir = NewCacheDir();
        try
        {
            Cache(dir, Cycle, ExtractCsv());

            (RouteSourceResult result, FakeHandler handler) = await FindIn(dir, Refused, SfoDen);

            Assert.Empty(handler.Requests);
            Assert.Null(result.FailureMessage);
            Assert.Equal(
                ["TIPRE Q126 INSLO LBERT LONGZ4", "ORRCA Q120 GALLI BAM J154 TCH KAMPR LONGZ4"],
                result.Routes.Select(r => r.RouteText).ToList()
            );
            Assert.All(
                result.Routes,
                r =>
                {
                    Assert.Equal(0, r.Count);
                    Assert.Equal(["preferred"], r.Sources);
                    Assert.Null(r.Published);
                }
            );
            Assert.Equal(new PreferredRouteInfo("H", null, null, null, "1400-0000", null, null), result.Routes[0].Preferred);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Download_FetchesTheCycleFile_AndCachesItForTheNextSource()
    {
        string dir = NewCacheDir();
        try
        {
            (RouteSourceResult first, FakeHandler handler) = await FindIn(dir, _ => ZipReply(ExtractCsv()), SfoDen);
            (RouteSourceResult second, FakeHandler again) = await FindIn(dir, Refused, SfoDen);

            Assert.Equal(CycleUrl, Assert.Single(handler.Requests).ToString());
            Assert.Null(first.FailureMessage);
            Assert.Equal(2, first.Routes.Count);
            Assert.True(File.Exists(Path.Combine(dir, Cycle + ".csv")));
            Assert.Empty(again.Requests);
            Assert.Equal(first.Routes.Select(r => r.RouteText), second.Routes.Select(r => r.RouteText));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task DownloadFails_PreviousCycleCached_ServesItWithTheStaleMessage()
    {
        string dir = NewCacheDir();
        try
        {
            Cache(dir, "2611", ExtractCsv());

            (RouteSourceResult result, _) = await FindIn(dir, Refused, SfoDen);

            Assert.Equal("Preferred routes from cycle 2611; the current file could not be downloaded.", result.FailureMessage);
            Assert.Equal(2, result.Routes.Count);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task DownloadFails_OnlyACycleThreeBackCached_IsUnavailable()
    {
        string dir = NewCacheDir();
        try
        {
            Cache(dir, "2609", ExtractCsv());

            (RouteSourceResult result, _) = await FindIn(dir, Refused, SfoDen);

            Assert.StartsWith("Preferred routes unavailable: ", result.FailureMessage);
            Assert.Empty(result.Routes);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task DownloadFails_NothingCached_IsUnavailable()
    {
        string dir = NewCacheDir();
        try
        {
            (RouteSourceResult result, _) = await FindIn(dir, Refused, SfoDen);

            Assert.Equal("Preferred routes unavailable: connection refused", result.FailureMessage);
            Assert.Empty(result.Routes);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task NotFound_IsUnavailable()
    {
        string dir = NewCacheDir();
        try
        {
            (RouteSourceResult result, _) = await FindIn(dir, _ => new HttpResponseMessage(HttpStatusCode.NotFound), SfoDen);

            Assert.StartsWith("Preferred routes unavailable: ", result.FailureMessage);
            Assert.Contains("404", result.FailureMessage);
            Assert.Empty(result.Routes);
            Assert.False(File.Exists(Path.Combine(dir, Cycle + ".csv")));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task EveryRowMalformed_DeletesTheCachedFile_AndReportsUnreadable()
    {
        string dir = NewCacheDir();
        try
        {
            Cache(dir, Cycle, "<html><body>\nService unavailable\n</body></html>\n");

            (RouteSourceResult result, _) = await FindIn(dir, Refused, SfoDen);

            Assert.Equal(UnreadableMessage, result.FailureMessage);
            Assert.Empty(result.Routes);
            Assert.False(File.Exists(Path.Combine(dir, Cycle + ".csv")));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task ReversePair_GetsNoRoutes()
    {
        string dir = NewCacheDir();
        try
        {
            Cache(dir, Cycle, ExtractCsv());

            (RouteSourceResult result, _) = await FindIn(dir, Refused, new RouteQuery("KDEN", "KSFO", [], [], null));

            Assert.Empty(result.Routes);
            Assert.Null(result.FailureMessage);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    /// <summary>
    /// SFO-SMF rows are coded "J30"/"J50" (jets only). BUR-LGB rows are, in order, "PQ50" (route A), "JM90" (route B), "M50"
    /// (A) and "J50" (A), so a turboprop gets B only through M. ACY-IAD has a "NON-JETS ONLY" and a "JETS ONLY" row with
    /// plain-number altitudes. ABE-ACY's "5000" row has no code and no AIRCRAFT text: the AIM default, no jets.
    /// </summary>
    [Theory]
    [InlineData("KSFO", "KSMF", "B738", "FEVTA FEVTA1|CIITY BCEEE FEVTA FEVTA1|CIITY ALTAM")]
    [InlineData("KSFO", "KSMF", "C172", "")]
    [InlineData("KSFO", "KSMF", null, "FEVTA FEVTA1|CIITY BCEEE FEVTA FEVTA1|CIITY ALTAM")]
    [InlineData("KSFO", "KSMF", "ZZZZ", "FEVTA FEVTA1|CIITY BCEEE FEVTA FEVTA1|CIITY ALTAM")]
    [InlineData("KBUR", "KLGB", "DH8D", BurLgbA + "|" + BurLgbB)]
    [InlineData("KBUR", "KLGB", "B738", BurLgbB + "|" + BurLgbA)]
    [InlineData("KBUR", "KLGB", "C172", BurLgbA)]
    [InlineData("KACY", "KIAD", "B738", "LEEAH T315 TAPPA BRV V286 CSN")]
    [InlineData("KACY", "KIAD", "C172", "LEEAH T358 OBWON T356 DROSA WOOLY MRB")]
    [InlineData("KABE", "KACY", "B738", "")]
    [InlineData("KABE", "KACY", "C172", "FJC ARD CYN")]
    public async Task TecRoutes_AreOfferedByTheRowsOwnAircraftClass(string departure, string destination, string? aircraftType, string expected)
    {
        string dir = NewCacheDir();
        try
        {
            Cache(dir, Cycle, ExtractCsv());

            (RouteSourceResult result, _) = await FindIn(
                dir,
                Refused,
                new RouteQuery(departure, destination, [], [], null, AircraftType: aircraftType)
            );

            Assert.Equal(expected, string.Join('|', result.Routes.Select(r => r.RouteText)));
            Assert.All(result.Routes, r => Assert.Equal("TEC", r.Preferred?.Type));
            Assert.Null(result.FailureMessage);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task FailedDownload_IsRememberedForTheLifeOfTheSource()
    {
        string dir = NewCacheDir();
        try
        {
            (PreferredRouteSource source, FakeHandler handler) = NewSource(dir, Refused);
            using (source)
            {
                RouteSourceResult first = await source.FindAsync(SfoDen, CancellationToken.None);
                RouteSourceResult second = await source.FindAsync(SfoDen, CancellationToken.None);

                Assert.Single(handler.Requests);
                Assert.Equal("Preferred routes unavailable: connection refused", first.FailureMessage);
                Assert.Equal(first.FailureMessage, second.FailureMessage);
            }
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task CacheWriteFails_IsUnavailable_WithTheSaveReason()
    {
        string notAFolder = Path.Combine(Path.GetTempPath(), "prefroutes-file-" + Guid.NewGuid());
        File.WriteAllText(notAFolder, "a file where the cache folder should be");
        try
        {
            (RouteSourceResult result, _) = await FindIn(notAFolder, _ => ZipReply(ExtractCsv()), SfoDen);

            Assert.StartsWith("Preferred routes unavailable: couldn't save the download: ", result.FailureMessage);
            Assert.Empty(result.Routes);
        }
        finally
        {
            File.Delete(notAFolder);
        }
    }

    [Fact]
    public async Task OkReplyThatIsNotAZip_IsUnavailable()
    {
        string dir = NewCacheDir();
        try
        {
            static HttpResponseMessage Html(HttpRequestMessage request) =>
                new(HttpStatusCode.OK) { Content = new StringContent("<html><body>Site maintenance</body></html>") };

            (RouteSourceResult result, _) = await FindIn(dir, Html, SfoDen);

            Assert.Equal("Preferred routes unavailable: the FAA download is not a zip file", result.FailureMessage);
            Assert.Empty(result.Routes);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task UnreadableStaleFile_IsDeleted_AndTheNextOlderCycleServed()
    {
        string dir = NewCacheDir();
        try
        {
            Cache(dir, "2611", "<html><body>\nService unavailable\n</body></html>\n");
            Cache(dir, "2610", ExtractCsv());

            (RouteSourceResult result, _) = await FindIn(dir, Refused, SfoDen);

            Assert.Equal("Preferred routes from cycle 2610; the current file could not be downloaded.", result.FailureMessage);
            Assert.Equal(2, result.Routes.Count);
            Assert.False(File.Exists(Path.Combine(dir, "2611.csv")));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task UnreadableStaleFile_WithNoOlderCycle_KeepsTheDownloadReason()
    {
        string dir = NewCacheDir();
        try
        {
            Cache(dir, "2611", "<html><body>\nService unavailable\n</body></html>\n");

            (RouteSourceResult result, _) = await FindIn(dir, Refused, SfoDen);

            Assert.Equal("Preferred routes unavailable: connection refused", result.FailureMessage);
            Assert.Empty(result.Routes);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(null, false)]
    [InlineData("0", false)]
    public void SkipVariableValue_TurnsDownloadsOffOnlyForOneOrTrue(string? value, bool skipped) =>
        Assert.Equal(skipped, PreferredRouteSource.IsDownloadSkipped(value));

    [Fact]
    public async Task NonFaaEndpoint_GetsNoRoutes_NoFailure_AndNoDownload()
    {
        string dir = NewCacheDir();
        try
        {
            (RouteSourceResult result, FakeHandler handler) = await FindIn(dir, Refused, new RouteQuery("EGLL", "KSFO", [], [], null));

            Assert.Empty(result.Routes);
            Assert.Null(result.FailureMessage);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            DeleteDir(dir);
        }
    }
}
