using System.Net;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Sim;
using Directory = System.IO.Directory;

namespace Yaat.Client.Services;

/// <summary>
/// Geo-referenced tower cab background image decoded from a JPEG with EXIF GPS tags.
/// BottomLeft/TopRight define the bounding box in lat/lon. Stored as an immutable
/// <see cref="SKImage"/> so Skia can hash and cache the GPU texture across redraws —
/// drawing a mutable <see cref="SKBitmap"/> at 10 fps re-uploaded the full pixel buffer
/// each frame, which pegged a CPU core on a high-res tower-cab JPEG.
/// </summary>
public sealed record TowerCabImage(SKImage Image, double BottomLeftLat, double BottomLeftLon, double TopRightLat, double TopRightLon);

/// <summary>How much of a tower-cab image's body has downloaded.</summary>
/// <param name="BytesRead">The bytes read so far.</param>
/// <param name="TotalBytes">The body's length when the response gives it, else null.</param>
public sealed record TowerCabDownloadProgress(long BytesRead, long? TotalBytes);

// One image's download: where from, where to, what to log it as, and who hears its progress.
internal readonly record struct TowerCabImageRequest(
    string Url,
    string CachePath,
    string AirportId,
    string Resolution,
    IProgress<TowerCabDownloadProgress>? Progress
);

/// <summary>
/// Downloads, caches, and decodes tower cab background JPEG images from vNAS.
/// Uses CRC-style conditional HTTP (If-Modified-Since / HEAD check) for freshness.
/// EXIF GPS tags provide geo-referencing coordinates.
/// </summary>
public sealed class TowerCabImageService : IDisposable
{
    // Progress is reported each time this many more bytes have arrived, and once at the end.
    private const long ProgressStepBytes = 512 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger _log = AppLog.CreateLogger<TowerCabImageService>();
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly string _cacheDir;

    /// <summary>Creates the app's service: its own HTTP client, the system clock and the per-user tower-cab cache.</summary>
    public TowerCabImageService()
        : this(new HttpClientHandler(), TimeProvider.System, YaatPaths.Combine("cache", "towercab")) { }

    /// <summary>Creates a service over an HTTP handler, a clock for the download timeout and a cache folder. Test seam.</summary>
    internal TowerCabImageService(HttpMessageHandler handler, TimeProvider time, string cacheDir)
    {
        _http = new HttpClient(handler) { Timeout = RequestTimeout };
        _time = time;
        _cacheDir = cacheDir;
    }

    /// <summary>
    /// Downloads (if needed) and decodes a tower cab background image.
    /// Returns null if the image is unavailable or has no EXIF GPS data.
    /// </summary>
    /// <param name="towerCabImagesBaseUrl">The vNAS tower-cab images' base URL.</param>
    /// <param name="artccId">The ARTCC whose folder holds the image.</param>
    /// <param name="airportId">The airport's id.</param>
    /// <param name="highRes">True for the high-res image.</param>
    /// <param name="progress">Told how much of the image's body has arrived while it downloads (never on a cache hit), or null.</param>
    public async Task<TowerCabImage?> GetImageAsync(
        string towerCabImagesBaseUrl,
        string artccId,
        string airportId,
        bool highRes,
        IProgress<TowerCabDownloadProgress>? progress
    )
    {
        string resolution = highRes ? "High" : "Low";
        string fileName = $"{airportId}-{resolution}Res.jpg";
        string artccCacheDir = Path.Combine(_cacheDir, artccId);
        Directory.CreateDirectory(artccCacheDir);
        string cachePath = Path.Combine(artccCacheDir, fileName);

        string url = $"{towerCabImagesBaseUrl}/{artccId}/{fileName}";

        await EnsureFreshAsync(new TowerCabImageRequest(url, cachePath, airportId, resolution, progress));

        if (!File.Exists(cachePath))
        {
            return null;
        }

        return DecodeImage(cachePath, airportId);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, IProgress<TowerCabDownloadProgress>? progress, CancellationToken ct)
    {
        long? total = content.Headers.ContentLength;
        await using Stream body = await content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = total is > 0 and <= int.MaxValue ? new MemoryStream((int)total.Value) : new MemoryStream();
        byte[] chunk = new byte[81920];
        long read = 0;
        long reportedAt = 0;
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, n);
            read += n;
            if (read - reportedAt >= ProgressStepBytes)
            {
                reportedAt = read;
                progress?.Report(new TowerCabDownloadProgress(read, total));
            }
        }

        if (read != reportedAt)
        {
            progress?.Report(new TowerCabDownloadProgress(read, total));
        }

        return buffer.ToArray();
    }

    private async Task EnsureFreshAsync(TowerCabImageRequest request)
    {
        (string url, string cachePath, string airportId, string resolution, _) = request;
        // Freshness check: the vNAS data-api (Cloudflare origin) returns 200 OK on HEAD requests
        // regardless of If-Modified-Since, so we compare Last-Modified against the cached file's
        // mtime ourselves instead of relying on 304 responses.
        try
        {
            if (File.Exists(cachePath))
            {
                var fileInfo = new FileInfo(cachePath);
                using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                using HttpResponseMessage headResp = await _http.SendAsync(headReq);

                if (headResp.StatusCode == HttpStatusCode.NotFound)
                {
                    if (_log.IsEnabled(LogLevel.Debug))
                    {
                        _log.LogDebug("Tower cab image {AirportId} {Res}Res not found on server", airportId, resolution);
                    }

                    return;
                }

                if (headResp.IsSuccessStatusCode)
                {
                    DateTime? serverLastModified = headResp.Content.Headers.LastModified?.UtcDateTime;
                    if ((serverLastModified is { } sm) && (sm <= fileInfo.LastWriteTimeUtc))
                    {
                        if (_log.IsEnabled(LogLevel.Debug))
                        {
                            _log.LogDebug("Tower cab image {AirportId} {Res}Res is up to date", airportId, resolution);
                        }

                        return;
                    }

                    if (_log.IsEnabled(LogLevel.Debug))
                    {
                        _log.LogDebug("Tower cab image {AirportId} {Res}Res has been updated, re-downloading", airportId, resolution);
                    }

                    await DownloadAsync(request, serverLastModified);
                    return;
                }

                _log.LogWarning(
                    "HEAD for tower cab image {AirportId} {Res}Res returned {Status}; using cached copy",
                    airportId,
                    resolution,
                    headResp.StatusCode
                );
                return;
            }

            await DownloadAsync(request, serverLastModified: null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to check/download tower cab image {AirportId} {Res}Res", airportId, resolution);
        }
    }

    private async Task DownloadAsync(TowerCabImageRequest request, DateTime? serverLastModified)
    {
        (string url, string cachePath, string airportId, string resolution, _) = request;
        if (_log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation("Downloading tower cab image {AirportId} {Res}Res from {Url}", airportId, resolution, url);
        }

        // ResponseHeadersRead ends the client's own timeout at the headers, so the body is timed here: a stalled body
        // throws, and the caller's failure path keeps the cached copy.
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug("Tower cab image {AirportId} {Res}Res not found (404)", airportId, resolution);
            }

            return;
        }

        response.EnsureSuccessStatusCode();
        byte[] bytes = await ReadBodyAsync(response.Content, request.Progress, timeout.Token);
        await File.WriteAllBytesAsync(cachePath, bytes);

        DateTime? stamp = serverLastModified ?? response.Content.Headers.LastModified?.UtcDateTime;
        if (stamp is { } s)
        {
            File.SetLastWriteTimeUtc(cachePath, s);
        }

        LogCached(airportId, resolution, bytes);
    }

    /// <summary>Logs at Information that the airport's tower cab image at the resolution is cached, with its size.</summary>
    private void LogCached(string airportId, string resolution, byte[] bytes)
    {
        if (_log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation("Cached tower cab image {AirportId} {Res}Res ({Size:N0} bytes)", airportId, resolution, bytes.Length);
        }
    }

    private TowerCabImage? DecodeImage(string path, string airportId)
    {
        try
        {
            (double BLLat, double BLLon, double TRLat, double TRLon)? bounds = ExtractGpsBounds(path);
            if (bounds is null)
            {
                _log.LogWarning("Tower cab image {AirportId} has no EXIF GPS data", airportId);
                return null;
            }

            using var decoded = SKBitmap.Decode(path);
            if (decoded is null)
            {
                _log.LogWarning("Failed to decode tower cab image {AirportId}", airportId);
                return null;
            }

            // Cap the longest side so the GPU texture fits in Skia's default GrContext
            // resource cache (~96 MB). vNAS HighRes images are 9984×9984 (~380 MB RGBA +
            // mips); without this cap the texture is evicted between frames, forcing a
            // re-upload from CPU memory every redraw. 4096 px across a typical airport
            // (~5 km) is ~1.2 m/pixel, plenty for ground-view rendering.
            const int MaxDimension = 4096;
            SKBitmap working;
            if (decoded.Width > MaxDimension || decoded.Height > MaxDimension)
            {
                float scale = MaxDimension / (float)Math.Max(decoded.Width, decoded.Height);
                int newW = (int)Math.Round(decoded.Width * scale);
                int newH = (int)Math.Round(decoded.Height * scale);
                var info = new SKImageInfo(newW, newH, decoded.ColorType, decoded.AlphaType);
                // Mitchell cubic: the quality-appropriate kernel for a one-shot downscale of a
                // photographic tower-cab background, where this runs once per image at load.
                working = decoded.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (working is null)
                {
                    _log.LogWarning("Failed to downscale tower cab image {AirportId}", airportId);
                    return null;
                }

                if (_log.IsEnabled(LogLevel.Debug))
                {
                    _log.LogDebug(
                        "Downscaled tower cab image {AirportId} from {OldW}x{OldH} to {NewW}x{NewH}",
                        airportId,
                        decoded.Width,
                        decoded.Height,
                        newW,
                        newH
                    );
                }
            }
            else
            {
                working = decoded;
            }

            // SKImage.FromBitmap snapshots the pixels into an immutable image whose
            // GPU texture Skia caches in the GrContext resource cache by stable id.
            var image = SKImage.FromBitmap(working);
            if (!ReferenceEquals(working, decoded))
            {
                working.Dispose();
            }

            if (image is null)
            {
                _log.LogWarning("Failed to wrap tower cab image {AirportId} as SKImage", airportId);
                return null;
            }

            (double blLat, double blLon, double trLat, double trLon) = bounds.Value;

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "Tower cab image {AirportId}: {W}x{H}, bounds ({BLLat:F6},{BLLon:F6})-({TRLat:F6},{TRLon:F6})",
                    airportId,
                    image.Width,
                    image.Height,
                    blLat,
                    blLon,
                    trLat,
                    trLon
                );
            }

            return new TowerCabImage(image, blLat, blLon, trLat, trLon);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to decode tower cab image {AirportId}", airportId);
            return null;
        }
    }

    /// <summary>
    /// Extracts geo-referencing bounds from EXIF GPS tags.
    /// CRC convention: GPSLatitude/Longitude = bottom-left, GPSDestLatitude/DestLongitude = top-right.
    /// </summary>
    private static (double BLLat, double BLLon, double TRLat, double TRLon)? ExtractGpsBounds(string path)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(path);

        GpsDirectory? gpsDir = directories.OfType<GpsDirectory>().FirstOrDefault();
        if (gpsDir is null)
        {
            return null;
        }

        // Bottom-left: standard GPS position
        GeoLocation? blGeo = gpsDir.GetGeoLocation();
        if (blGeo is null)
        {
            return null;
        }

        // Top-right: GPS destination (CRC convention)
        Rational[]? trLatRationals = gpsDir.GetRationalArray(GpsDirectory.TagDestLatitude);
        string? trLatRef = gpsDir.GetString(GpsDirectory.TagDestLatitudeRef);
        Rational[]? trLonRationals = gpsDir.GetRationalArray(GpsDirectory.TagDestLongitude);
        string? trLonRef = gpsDir.GetString(GpsDirectory.TagDestLongitudeRef);

        if (trLatRationals is null || trLonRationals is null || trLatRef is null || trLonRef is null)
        {
            return null;
        }

        double trLat = RationalsToDegrees(trLatRationals);
        if (trLatRef == "S")
        {
            trLat = -trLat;
        }

        double trLon = RationalsToDegrees(trLonRationals);
        if (trLonRef == "W")
        {
            trLon = -trLon;
        }

        GeoLocation bl = blGeo.Value;
        return (bl.Latitude, bl.Longitude, trLat, trLon);
    }

    private static double RationalsToDegrees(MetadataExtractor.Rational[] rationals)
    {
        double degrees = rationals.Length > 0 ? rationals[0].ToDouble() : 0;
        double minutes = rationals.Length > 1 ? rationals[1].ToDouble() / 60.0 : 0;
        double seconds = rationals.Length > 2 ? rationals[2].ToDouble() / 3600.0 : 0;

        if (double.IsNaN(degrees))
        {
            degrees = 0;
        }

        if (double.IsNaN(minutes))
        {
            minutes = 0;
        }

        if (double.IsNaN(seconds))
        {
            seconds = 0;
        }

        return degrees + minutes + seconds;
    }

    public void Dispose() => _http.Dispose();
}
