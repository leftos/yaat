using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Data.Artcc;
using Yaat.Sim.Weather;

namespace Yaat.Client.Services;

/// <summary>
/// A NOMADS GFS 0.25° subset box: latitudes in degrees, longitudes in the grid's 0–360 convention.
/// </summary>
public readonly record struct GfsGridBox(double BottomLat, double TopLat, double LeftLon, double RightLon);

/// <summary>The box to fetch for an ARTCC, or the reason it cannot be fetched.</summary>
public sealed record GfsBoxResult(GfsGridBox? Box, string? Refusal);

/// <summary>
/// A fetched grid with its cycle and forecast hour, or the reason (one of the <see cref="GfsGridFetcher"/> constants) there is none.
/// </summary>
public sealed record GfsGridFetchResult(byte[]? Grib2, DateTime? CycleUtc, int? ForecastHour, string? FailureReason)
{
    public static GfsGridFetchResult Success(byte[] grib2, DateTime cycleUtc, int forecastHour) => new(grib2, cycleUtc, forecastHour, null);

    public static GfsGridFetchResult Failure(string reason) => new(null, null, null, reason);
}

/// <summary>
/// Fetches the GFS wind grid (UGRD, VGRD and TMP on 20 isobaric levels) over an ARTCC from the NOMADS GRIB filter, for live
/// weather to upload and name by hash.
/// </summary>
/// <param name="handler">The HTTP handler NOMADS is reached through (<see cref="HttpClientHandler"/> in production).</param>
/// <param name="requestTimeout">The bound on each GET and its body read (<see cref="DefaultRequestTimeout"/> in production).</param>
public sealed class GfsGridFetcher(HttpMessageHandler handler, TimeSpan requestTimeout)
{
    /// <summary>The production bound on each NOMADS GET and its body read.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly ILogger Log = AppLog.CreateLogger<GfsGridFetcher>();

    private readonly HttpClient _http = new(handler) { Timeout = requestTimeout };

    /// <summary>How many cycles are tried, newest first, before the grid counts as not posted.</summary>
    private const int MaxCyclesTried = 3;

    private const string HubExceptionMarker = "HubException: ";

    /// <summary>Degrees added on each side of the ARTCC boundary's box.</summary>
    public const double BoxMarginDeg = 1.5;

    public const string NoCyclePosted = "no GFS cycle posted";
    public const string RequestFailed = "NOMADS request failed";
    public const string BoxTooLarge = "ARTCC box too large";
    public const string NoBoundary = "no ARTCC boundary";
    public const string OverUploadLimit = "grid over the upload limit";

    private const string FilterUrl = "https://nomads.ncep.noaa.gov/cgi-bin/filter_gfs_0p25.pl";
    private const double GridStepDeg = 0.25;
    private const double MinBoxSideDeg = 2.0;
    private const int MessagesPerGrid = 60;
    private const int MaxForecastHour = 120;
    private const double CyclePostingDelayHours = 3.5;
    private const int CycleSpacingHours = 6;

    private static readonly int[] LevelsHpa = [1000, 975, 950, 925, 900, 850, 800, 750, 700, 650, 600, 550, 500, 450, 400, 350, 300, 250, 200, 150];

    /// <summary>The terminal warning (and log line) for a live weather load that flies the FD layers because the grid could not be had.</summary>
    public static string WarningText(string reason) => $"Live weather: GFS winds unavailable — {reason}; using FD winds aloft only.";

    /// <summary>The terminal warning (and log line) for a live weather load with neither the grid nor FD winds aloft.</summary>
    public const string FdUnavailableWarning = "Live weather: FD winds aloft unavailable; METAR surface winds only.";

    /// <summary>
    /// A grid's attribution, e.g. "NOAA GFS 12Z 16 Oct, +4 h forecast": the run's cycle hour and date in UTC, and the forecast
    /// hour the grid was fetched for.
    /// </summary>
    public static string SourceLabel(DateTime cycleUtc, int forecastHours) =>
        string.Create(CultureInfo.InvariantCulture, $"NOAA GFS {cycleUtc:HH}Z {cycleUtc:d MMM}, +{forecastHours} h forecast");

    /// <summary>The status line while the grid downloads.</summary>
    public static string FetchingStatus(DateTime cycleUtc, int forecastHour) => $"Fetching GFS winds aloft ({cycleUtc:HH}Z f{forecastHour:D3})...";

    /// <summary>
    /// The server's refusal text from a client-side HubException message: the text after the last "HubException: " SignalR puts
    /// before it, or the whole message without that marker.
    /// </summary>
    public static string RefusalReason(string hubExceptionMessage)
    {
        int marker = hubExceptionMessage.LastIndexOf(HubExceptionMarker, StringComparison.Ordinal);
        return marker < 0 ? hubExceptionMessage : hubExceptionMessage[(marker + HubExceptionMarker.Length)..];
    }

    /// <summary>
    /// Fetches the grid over <paramref name="artccId"/>'s boundary box for the newest posted cycle and the forecast hour nearest to
    /// <paramref name="nowUtc"/>. A cycle not posted yet (HTTP 404) steps back 6 h, three cycles at most; any other failure ends the
    /// fetch. Cancelling <paramref name="cancellationToken"/> stops the GET in flight and ends the fetch as
    /// <see cref="RequestFailed"/>.
    /// </summary>
    public async Task<GfsGridFetchResult> FetchAsync(string artccId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArtccBoundary? boundary = ArtccBoundaryDatabase.Default.FindById(artccId);
        if (boundary is null)
        {
            return GfsGridFetchResult.Failure(NoBoundary);
        }

        GfsBoxResult boxResult = BoxFor(boundary.MinLat, boundary.MaxLat, boundary.MinLon, boundary.MaxLon);
        if (boxResult.Box is not { } box)
        {
            if (boxResult.Refusal is null)
            {
                throw new InvalidOperationException($"GFS box for {artccId} refused without a reason");
            }

            return GfsGridFetchResult.Failure(boxResult.Refusal);
        }

        DateTime cycle = NewestCycle(nowUtc);
        for (int attempt = 0; attempt < MaxCyclesTried; attempt++, cycle = StepBack(cycle))
        {
            int forecastHour = ForecastHour(cycle, nowUtc);
            string url = BuildUrl(cycle, forecastHour, box);
            GfsGridFetchResult? result = await TryCycleAsync(url, cycle, forecastHour, cancellationToken);
            if (result is not null)
            {
                return result;
            }

            Log.LogInformation(
                "GFS cycle {Cycle:yyyy-MM-dd HH}Z f{ForecastHour:D3} not posted for {Artcc}: {Url}",
                cycle,
                forecastHour,
                artccId,
                url
            );
        }

        return GfsGridFetchResult.Failure(NoCyclePosted);
    }

    /// <summary>
    /// One GET and its body read, bounded together by the request timeout (with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// the client's own timeout stops at the headers); null when the cycle is not posted (HTTP 404).
    /// </summary>
    private async Task<GfsGridFetchResult?> TryCycleAsync(string url, DateTime cycle, int forecastHour, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_http.Timeout);
        try
        {
            return await GetCycleAsync(url, cycle, forecastHour, attempt.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // A cancellation is the request timeout, or the caller stopping the fetch; both end it like any failed request.
            string cause = cancellationToken.IsCancellationRequested ? "cancelled by the caller" : "failed";
            Log.LogWarning(ex, "GFS grid request {Cause}: {Url}", cause, url);
            return GfsGridFetchResult.Failure(RequestFailed);
        }
    }

    private async Task<GfsGridFetchResult?> GetCycleAsync(string url, DateTime cycle, int forecastHour, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            Log.LogWarning("GFS grid request failed with HTTP {Status}: {Url}", (int)response.StatusCode, url);
            return GfsGridFetchResult.Failure(RequestFailed);
        }

        byte[]? grib2 = await ReadWithinCapAsync(response.Content, cancellationToken);
        if (grib2 is null)
        {
            Log.LogWarning("GFS grid response passed the {Cap}-byte upload limit; reading stopped: {Url}", WindGridRegistry.MaxGridBytes, url);
            return GfsGridFetchResult.Failure(OverUploadLimit);
        }

        Log.LogInformation(
            "Fetched GFS grid: {Bytes} bytes, cycle {Cycle:yyyy-MM-dd HH}Z f{ForecastHour:D3}: {Url}",
            grib2.Length,
            cycle,
            forecastHour,
            url
        );
        return GfsGridFetchResult.Success(grib2, cycle, forecastHour);
    }

    /// <summary>The response body, or null as soon as it passes <see cref="WindGridRegistry.MaxGridBytes"/>.</summary>
    private static async Task<byte[]?> ReadWithinCapAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream body = await content.ReadAsStreamAsync(cancellationToken);
        using var grib2 = new MemoryStream();
        byte[] chunk = new byte[81_920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if ((grib2.Length + read) > WindGridRegistry.MaxGridBytes)
            {
                return null;
            }

            grib2.Write(chunk, 0, read);
        }

        return grib2.ToArray();
    }

    /// <summary>The NOMADS filter URL for one cycle, forecast hour and box.</summary>
    public static string BuildUrl(DateTime cycleUtc, int forecastHour, GfsGridBox box)
    {
        var url = new StringBuilder(FilterUrl);
        url.Append(CultureInfo.InvariantCulture, $"?dir=%2Fgfs.{cycleUtc:yyyyMMdd}%2F{cycleUtc:HH}%2Fatmos");
        url.Append(CultureInfo.InvariantCulture, $"&file=gfs.t{cycleUtc:HH}z.pgrb2.0p25.f{forecastHour:D3}");
        url.Append("&var_UGRD=on&var_VGRD=on&var_TMP=on");
        foreach (int level in LevelsHpa)
        {
            url.Append(CultureInfo.InvariantCulture, $"&lev_{level}_mb=on");
        }

        url.Append(
            CultureInfo.InvariantCulture,
            $"&subregion=&toplat={box.TopLat}&leftlon={box.LeftLon}&rightlon={box.RightLon}&bottomlat={box.BottomLat}"
        );
        return url.ToString();
    }

    /// <summary>The latest 00/06/12/18Z cycle at least 3.5 hours before <paramref name="nowUtc"/>.</summary>
    public static DateTime NewestCycle(DateTime nowUtc)
    {
        DateTime posted = nowUtc.AddHours(-CyclePostingDelayHours);
        return new DateTime(posted.Year, posted.Month, posted.Day, posted.Hour / CycleSpacingHours * CycleSpacingHours, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>The cycle before <paramref name="cycleUtc"/>.</summary>
    public static DateTime StepBack(DateTime cycleUtc) => cycleUtc.AddHours(-CycleSpacingHours);

    /// <summary>The forecast hour nearest to <paramref name="nowUtc"/> from <paramref name="cycleUtc"/>, clamped to 0..120.</summary>
    public static int ForecastHour(DateTime cycleUtc, DateTime nowUtc)
    {
        double hours = Math.Round((nowUtc - cycleUtc).TotalHours, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(hours, 0, MaxForecastHour);
    }

    /// <summary>
    /// The box over an ARTCC boundary's extremes: <see cref="BoxMarginDeg"/> on each side, at least 2° × 2°, rounded outward to
    /// whole degrees (south and west down, north and east up), longitudes in 0–360. Refused as <see cref="BoxTooLarge"/> when it
    /// spans more than 180° of longitude or its estimated size (<see cref="EstimatedBytes"/>) exceeds
    /// <see cref="WindGridRegistry.MaxGridBytes"/>.
    /// </summary>
    public static GfsBoxResult BoxFor(double minLat, double maxLat, double minLon, double maxLon)
    {
        (double bottom, double top) = WidenToWholeDegrees(minLat, maxLat);
        (double left, double right) = WidenToWholeDegrees(minLon, maxLon);
        if ((right - left) > 180)
        {
            return new GfsBoxResult(null, BoxTooLarge);
        }

        var box = new GfsGridBox(bottom, top, ToGfsLon(left), ToGfsLon(right));
        return EstimatedBytes(box) > WindGridRegistry.MaxGridBytes ? new GfsBoxResult(null, BoxTooLarge) : new GfsBoxResult(box, null);
    }

    /// <summary>
    /// The estimated GRIB2 size of a box: its 0.25° grid points, edges included, × 60 messages × 1.3 bytes (the ratio measured on
    /// the ZOA sample).
    /// </summary>
    public static long EstimatedBytes(GfsGridBox box)
    {
        long rows = PointsAlong(box.TopLat - box.BottomLat);
        long columns = PointsAlong(box.RightLon - box.LeftLon);
        return rows * columns * MessagesPerGrid * 13 / 10;
    }

    private static long PointsAlong(double spanDeg) => (long)Math.Ceiling(spanDeg / GridStepDeg) + 1;

    private static (double Low, double High) WidenToWholeDegrees(double low, double high)
    {
        double widenedLow = low - BoxMarginDeg;
        double widenedHigh = high + BoxMarginDeg;
        double shortfall = MinBoxSideDeg - (widenedHigh - widenedLow);
        if (shortfall > 0)
        {
            widenedLow -= shortfall / 2;
            widenedHigh += shortfall / 2;
        }

        return (Math.Floor(widenedLow), Math.Ceiling(widenedHigh));
    }

    private static double ToGfsLon(double lon) => lon < 0 ? lon + 360 : lon;
}
