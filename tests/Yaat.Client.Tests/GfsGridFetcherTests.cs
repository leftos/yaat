using System.Net;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Artcc;
using Yaat.Sim.Weather;

namespace Yaat.Client.Tests;

public class GfsGridFetcherTests
{
    private const string SamplePath = "TestData/gfs-zoa-sample.grib2";

    // The URL the committed sample was fetched from (Grib2ReaderTests.cs), with its XML-doc "&amp;" unescaped.
    private const string SampleUrl =
        "https://nomads.ncep.noaa.gov/cgi-bin/filter_gfs_0p25.pl?dir=%2Fgfs.20261014%2F12%2Fatmos&file=gfs.t12z.pgrb2.0p25.f000"
        + "&var_UGRD=on&var_VGRD=on&var_TMP=on&lev_1000_mb=on&lev_975_mb=on&lev_950_mb=on&lev_925_mb=on&lev_900_mb=on&lev_850_mb=on"
        + "&lev_800_mb=on&lev_750_mb=on&lev_700_mb=on&lev_650_mb=on&lev_600_mb=on&lev_550_mb=on&lev_500_mb=on&lev_450_mb=on"
        + "&lev_400_mb=on&lev_350_mb=on&lev_300_mb=on&lev_250_mb=on&lev_200_mb=on&lev_150_mb=on"
        + "&subregion=&toplat=42&leftlon=234&rightlon=243&bottomlat=34";

    private static DateTime Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static byte[] LoadSample() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, SamplePath));

    private static ArtccBoundary Zoa() =>
        ArtccBoundaryDatabase.Default.FindById("ZOA") ?? throw new InvalidOperationException("ZOA boundary missing from the bundled data");

    [Fact]
    public void BuildUrl_SampleCycleAndBox_EqualsTheSampleUrl()
    {
        var box = new GfsGridBox(BottomLat: 34, TopLat: 42, LeftLon: 234, RightLon: 243);

        string url = GfsGridFetcher.BuildUrl(Utc(2026, 10, 14, 12, 0), 0, box);

        Assert.Equal(SampleUrl, url);
    }

    [Fact]
    public void NewestCycle_OneMinuteShortOfThreeAndAHalfHours_TakesThePreviousDays18Z() =>
        Assert.Equal(Utc(2026, 10, 14, 18, 0), GfsGridFetcher.NewestCycle(Utc(2026, 10, 15, 3, 29)));

    [Fact]
    public void NewestCycle_OneMinutePastThreeAndAHalfHours_Takes00Z() =>
        Assert.Equal(Utc(2026, 10, 15, 0, 0), GfsGridFetcher.NewestCycle(Utc(2026, 10, 15, 3, 31)));

    [Fact]
    public void StepBack_From00Z_CrossesIntoThePreviousDay()
    {
        DateTime first = GfsGridFetcher.StepBack(Utc(2026, 10, 15, 0, 0));
        DateTime second = GfsGridFetcher.StepBack(first);

        Assert.Equal(Utc(2026, 10, 14, 18, 0), first);
        Assert.Equal(Utc(2026, 10, 14, 12, 0), second);
    }

    [Theory]
    [InlineData(16, 29, 4)]
    [InlineData(16, 30, 5)]
    [InlineData(16, 31, 5)]
    public void ForecastHour_RoundsToTheNearestHour(int hour, int minute, int expected) =>
        Assert.Equal(expected, GfsGridFetcher.ForecastHour(Utc(2026, 10, 14, 12, 0), Utc(2026, 10, 14, hour, minute)));

    [Fact]
    public void ForecastHour_NowBeforeTheCycle_ClampsToZero() =>
        Assert.Equal(0, GfsGridFetcher.ForecastHour(Utc(2026, 10, 14, 12, 0), Utc(2026, 10, 14, 10, 0)));

    [Fact]
    public void ForecastHour_PastOneHundredTwentyHours_ClampsTo120() =>
        Assert.Equal(120, GfsGridFetcher.ForecastHour(Utc(2026, 10, 14, 12, 0), Utc(2026, 10, 20, 0, 0)));

    [Fact]
    public void BoxFor_Zoa_IsTheBoundaryPlusMarginInZeroTo360()
    {
        ArtccBoundary zoa = Zoa();

        GfsBoxResult result = GfsGridFetcher.BoxFor(zoa.MinLat, zoa.MaxLat, zoa.MinLon, zoa.MaxLon);

        Assert.Null(result.Refusal);
        GfsGridBox box = Assert.NotNull(result.Box);
        // The boundary plus 1.5°, rounded outward to whole degrees: south and west down, north and east up.
        Assert.Equal(Math.Floor(zoa.MinLat - 1.5), box.BottomLat);
        Assert.Equal(Math.Ceiling(zoa.MaxLat + 1.5), box.TopLat);
        Assert.Equal(Math.Floor(zoa.MinLon - 1.5) + 360, box.LeftLon);
        Assert.Equal(Math.Ceiling(zoa.MaxLon + 1.5) + 360, box.RightLon);
        Assert.InRange(box.LeftLon, 0, 360);
        Assert.InRange(box.RightLon, 0, 360);
    }

    [Fact]
    public void BuildUrl_ZoaBox_SendsWholeDegreeBounds()
    {
        ArtccBoundary zoa = Zoa();
        GfsGridBox box = Assert.NotNull(GfsGridFetcher.BoxFor(zoa.MinLat, zoa.MaxLat, zoa.MinLon, zoa.MaxLon).Box);

        string url = GfsGridFetcher.BuildUrl(Utc(2026, 10, 14, 12, 0), 0, box);

        Assert.Matches(@"&subregion=&toplat=\d+&leftlon=\d+&rightlon=\d+&bottomlat=\d+$", url);
    }

    [Fact]
    public void BoxFor_LongitudeSpanOver180_IsRefusedAsTooLarge()
    {
        GfsBoxResult result = GfsGridFetcher.BoxFor(51, 60, -179.9, 179.9);

        Assert.Null(result.Box);
        Assert.Equal("ARTCC box too large", result.Refusal);
    }

    [Fact]
    public void BoxFor_EstimateAtTheCap_IsAccepted()
    {
        // 17° + 2 × 1.5° = 20° each way, already whole degrees: 81 × 81 points × 60 messages × 1.3 bytes = 511,758 bytes,
        // under 524,288.
        GfsBoxResult result = GfsGridFetcher.BoxFor(29.5, 46.5, -110.5, -93.5);

        GfsGridBox box = Assert.NotNull(result.Box);
        Assert.Equal(511_758, GfsGridFetcher.EstimatedBytes(box));
        Assert.Null(result.Refusal);
    }

    [Fact]
    public void BoxFor_EstimateOverTheCap_IsRefusedAsTooLarge()
    {
        // Latitude 28..48.25 rounds outward to 28..49, 21°: 85 × 81 points × 78 bytes = 537,030 bytes, over 524,288. The
        // unrounded 20.25° would estimate 82 × 81 × 78 = 518,076 and pass, so the rounded box is the one estimated.
        GfsBoxResult result = GfsGridFetcher.BoxFor(29.5, 46.75, -110.5, -93.5);

        Assert.Null(result.Box);
        Assert.Equal("ARTCC box too large", result.Refusal);
        Assert.Equal(512 * 1024, GfsGridFetcher.MaxWindGridUploadBytes);
    }

    // -------------------------------------------------------------------------
    // The fetch loop, against a stub NOMADS (the network boundary) serving the real sample
    // -------------------------------------------------------------------------

    // 2026-10-15 16:00Z: the newest posted cycle is 12Z (4 h old), then 06Z, then 00Z.
    private static readonly DateTime Now = Utc(2026, 10, 15, 16, 0);

    private sealed class StubNomads(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(responses[RequestedUrls.Count - 1]());
        }
    }

    // A hang guard, not the production timeout: the limit now covers the body read too, so a frozen test host must not trip it.
    // The stalled-body case passes a short one of its own.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    // A hang guard, never reached by a passing run: it turns a fetch that never ends into a failure rather than a gate kill.
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    /// <summary>A response body whose every read is the given delegate.</summary>
    private sealed class ScriptedStream(Func<CancellationToken, ValueTask<int>> read) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => read(cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            read(cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Body(Stream body) => new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage Sample() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(LoadSample()) };

    [Fact]
    public async Task FetchAsync_TwoCyclesNotPosted_StepsBackTwiceAndReturnsTheThirdCycle()
    {
        var nomads = new StubNomads(() => Status(HttpStatusCode.NotFound), () => Status(HttpStatusCode.NotFound), Sample);
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZOA", Now, CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.Equal(LoadSample(), result.Grib2);
        Assert.Equal(Utc(2026, 10, 15, 0, 0), result.CycleUtc);
        Assert.Equal(16, result.ForecastHour);
        Assert.Equal(3, nomads.RequestedUrls.Count);
        Assert.Contains("dir=%2Fgfs.20261015%2F12%2Fatmos&file=gfs.t12z.pgrb2.0p25.f004", nomads.RequestedUrls[0]);
        Assert.Contains("dir=%2Fgfs.20261015%2F06%2Fatmos&file=gfs.t06z.pgrb2.0p25.f010", nomads.RequestedUrls[1]);
        Assert.Contains("dir=%2Fgfs.20261015%2F00%2Fatmos&file=gfs.t00z.pgrb2.0p25.f016", nomads.RequestedUrls[2]);
    }

    [Fact]
    public async Task FetchAsync_ThreeCyclesNotPosted_ReportsNoCyclePosted()
    {
        var nomads = new StubNomads(
            () => Status(HttpStatusCode.NotFound),
            () => Status(HttpStatusCode.NotFound),
            () => Status(HttpStatusCode.NotFound)
        );
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZOA", Now, CancellationToken.None);

        Assert.Null(result.Grib2);
        Assert.Equal("no GFS cycle posted", result.FailureReason);
        Assert.Equal(3, nomads.RequestedUrls.Count);
    }

    [Fact]
    public async Task FetchAsync_ServerError_ReportsRequestFailedWithoutSteppingBack()
    {
        var nomads = new StubNomads(() => Status(HttpStatusCode.InternalServerError), Sample);
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZOA", Now, CancellationToken.None);

        Assert.Null(result.Grib2);
        Assert.Equal("NOMADS request failed", result.FailureReason);
        Assert.Single(nomads.RequestedUrls);
    }

    [Fact]
    public async Task FetchAsync_ResponseOverTheUploadCap_ReportsOverTheUploadLimit()
    {
        var nomads = new StubNomads(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GfsGridFetcher.MaxWindGridUploadBytes + 1]) }
        );
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZOA", Now, CancellationToken.None);

        Assert.Null(result.Grib2);
        Assert.Equal("grid over the upload limit", result.FailureReason);
        Assert.Single(nomads.RequestedUrls);
    }

    [Fact]
    public async Task FetchAsync_ResponseExactlyAtTheUploadCap_IsAccepted()
    {
        var nomads = new StubNomads(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GfsGridFetcher.MaxWindGridUploadBytes]) }
        );
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZOA", Now, CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.NotNull(result.Grib2);
        Assert.Equal(524_288, result.Grib2.Length);
    }

    [Fact]
    public async Task FetchAsync_BodyStallsAfterTheHeaders_TimesOutAsRequestFailed()
    {
        // Headers arrive, then the body never does. The stream ends its read only on cancellation, and then with the base
        // OperationCanceledException, as some transports do; the per-request timeout must reach the body read.
        var nomads = new StubNomads(() =>
            Body(
                new ScriptedStream(async token =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (TaskCanceledException)
                    {
                        throw new OperationCanceledException(token);
                    }

                    return 0;
                })
            )
        );
        var fetcher = new GfsGridFetcher(nomads, TimeSpan.FromMilliseconds(1));

        GfsGridFetchResult result = await fetcher
            .FetchAsync("ZOA", Now, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.Null(result.Grib2);
        Assert.Equal("NOMADS request failed", result.FailureReason);
        Assert.Single(nomads.RequestedUrls);
    }

    [Fact]
    public async Task FetchAsync_ConnectionResetMidBody_ReportsRequestFailed()
    {
        int reads = 0;
        var nomads = new StubNomads(() =>
            Body(
                new ScriptedStream(_ =>
                    ++reads == 1 ? ValueTask.FromResult(1_000) : ValueTask.FromException<int>(new IOException("connection reset"))
                )
            )
        );
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZOA", Now, CancellationToken.None);

        Assert.Null(result.Grib2);
        Assert.Equal("NOMADS request failed", result.FailureReason);
        Assert.Single(nomads.RequestedUrls);
    }

    [Fact]
    public async Task FetchAsync_Zan_IsRefusedAsTooLargeWithoutARequest()
    {
        // ZAN's boundary spans the antimeridian, so its widened longitude span is over 180°.
        var nomads = new StubNomads();
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZAN", Now, CancellationToken.None);

        Assert.Equal("ARTCC box too large", result.FailureReason);
        Assert.Empty(nomads.RequestedUrls);
    }

    [Fact]
    public async Task FetchAsync_UnknownArtcc_ReportsNoBoundaryWithoutARequest()
    {
        var nomads = new StubNomads();
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZZZ", Now, CancellationToken.None);

        Assert.Equal("no ARTCC boundary", result.FailureReason);
        Assert.Empty(nomads.RequestedUrls);
    }

    [Fact]
    public async Task FetchAsync_HandlerThrows_ReportsRequestFailed()
    {
        var nomads = new StubNomads(() => throw new HttpRequestException("No such host is known. (nomads.ncep.noaa.gov:443)"));
        var fetcher = new GfsGridFetcher(nomads, RequestTimeout);

        GfsGridFetchResult result = await fetcher.FetchAsync("ZOA", Now, CancellationToken.None);

        Assert.Null(result.Grib2);
        Assert.Equal("NOMADS request failed", result.FailureReason);
    }

    [Theory]
    [InlineData("An unexpected error occurred invoking 'UploadWindGrid' on the server. HubException: grid too big", "grid too big")]
    [InlineData("Not in a room", "Not in a room")]
    public void RefusalReason_TakesTheTextAfterTheLastHubExceptionMarker(string message, string expected) =>
        Assert.Equal(expected, GfsGridFetcher.RefusalReason(message));

    [Fact]
    public void Sample_DecodedField_SpansTheBoxItWasFetchedFor()
    {
        var field = GriddedWindField.FromMessages(Grib2Reader.Read(LoadSample()));

        Assert.Equal(34, field.SouthLatDeg, 6);
        Assert.Equal(42, field.NorthLatDeg, 6);
        Assert.Equal(234, field.WestLonDeg, 6);
        Assert.Equal(243, field.EastLonDeg, 6);
    }

    [Theory]
    [InlineData("SFO")]
    [InlineData("OAK")]
    [InlineData("SJC")]
    [InlineData("SMF")]
    [InlineData("RNO")]
    [InlineData("FAT")]
    public void Sample_DecodedField_CoversZoaAirport(string airportId)
    {
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();
        (double Lat, double Lon) airport =
            NavigationDatabase.Instance.GetAirportPosition(airportId) ?? throw new InvalidOperationException($"{airportId} missing from the navdata");
        double lon360 = airport.Lon < 0 ? airport.Lon + 360 : airport.Lon;

        var field = GriddedWindField.FromMessages(Grib2Reader.Read(LoadSample()));

        Assert.InRange(airport.Lat, field.SouthLatDeg, field.NorthLatDeg);
        Assert.InRange(lon360, field.WestLonDeg, field.EastLonDeg);
    }
}
