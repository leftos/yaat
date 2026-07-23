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

/// <summary>
/// Downloads, caches, and decodes tower cab background JPEG images from vNAS.
/// Uses CRC-style conditional HTTP (If-Modified-Since / HEAD check) for freshness.
/// EXIF GPS tags provide geo-referencing coordinates.
/// </summary>
public sealed class TowerCabImageService : IDisposable
{
    private static readonly string CacheDir = YaatPaths.Combine("cache", "towercab");

    private readonly ILogger _log = AppLog.CreateLogger<TowerCabImageService>();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Downloads (if needed) and decodes a tower cab background image.
    /// Returns null if the image is unavailable or has no EXIF GPS data.
    /// </summary>
    public async Task<TowerCabImage?> GetImageAsync(string towerCabImagesBaseUrl, string artccId, string airportId, bool highRes)
    {
        var resolution = highRes ? "High" : "Low";
        var fileName = $"{airportId}-{resolution}Res.jpg";
        var artccCacheDir = Path.Combine(CacheDir, artccId);
        Directory.CreateDirectory(artccCacheDir);
        var cachePath = Path.Combine(artccCacheDir, fileName);

        var url = $"{towerCabImagesBaseUrl}/{artccId}/{fileName}";

        await EnsureFreshAsync(url, cachePath, airportId, resolution);

        if (!File.Exists(cachePath))
        {
            return null;
        }

        return DecodeImage(cachePath, airportId);
    }

    private async Task EnsureFreshAsync(string url, string cachePath, string airportId, string resolution)
    {
        // Freshness check: the vNAS data-api (Cloudflare origin) returns 200 OK on HEAD requests
        // regardless of If-Modified-Since, so we compare Last-Modified against the cached file's
        // mtime ourselves instead of relying on 304 responses.
        try
        {
            if (File.Exists(cachePath))
            {
                var fileInfo = new FileInfo(cachePath);
                using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                using var headResp = await _http.SendAsync(headReq);

                if (headResp.StatusCode == HttpStatusCode.NotFound)
                {
                    _log.LogDebug("Tower cab image {AirportId} {Res}Res not found on server", airportId, resolution);
                    return;
                }

                if (headResp.IsSuccessStatusCode)
                {
                    var serverLastModified = headResp.Content.Headers.LastModified?.UtcDateTime;
                    if ((serverLastModified is { } sm) && (sm <= fileInfo.LastWriteTimeUtc))
                    {
                        _log.LogDebug("Tower cab image {AirportId} {Res}Res is up to date", airportId, resolution);
                        return;
                    }

                    _log.LogDebug("Tower cab image {AirportId} {Res}Res has been updated, re-downloading", airportId, resolution);
                    await DownloadAsync(url, cachePath, airportId, resolution, serverLastModified);
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

            await DownloadAsync(url, cachePath, airportId, resolution, serverLastModified: null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to check/download tower cab image {AirportId} {Res}Res", airportId, resolution);
        }
    }

    private async Task DownloadAsync(string url, string cachePath, string airportId, string resolution, DateTime? serverLastModified)
    {
        _log.LogInformation("Downloading tower cab image {AirportId} {Res}Res from {Url}", airportId, resolution, url);

        using var response = await _http.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _log.LogDebug("Tower cab image {AirportId} {Res}Res not found (404)", airportId, resolution);
            return;
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(cachePath, bytes);

        var stamp = serverLastModified ?? response.Content.Headers.LastModified?.UtcDateTime;
        if (stamp is { } s)
        {
            File.SetLastWriteTimeUtc(cachePath, s);
        }

        _log.LogInformation("Cached tower cab image {AirportId} {Res}Res ({Size:N0} bytes)", airportId, resolution, bytes.Length);
    }

    private TowerCabImage? DecodeImage(string path, string airportId)
    {
        try
        {
            var bounds = ExtractGpsBounds(path);
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

                _log.LogDebug(
                    "Downscaled tower cab image {AirportId} from {OldW}x{OldH} to {NewW}x{NewH}",
                    airportId,
                    decoded.Width,
                    decoded.Height,
                    newW,
                    newH
                );
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

            var (blLat, blLon, trLat, trLon) = bounds.Value;

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
        var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(path);

        var gpsDir = directories.OfType<GpsDirectory>().FirstOrDefault();
        if (gpsDir is null)
        {
            return null;
        }

        // Bottom-left: standard GPS position
        var blGeo = gpsDir.GetGeoLocation();
        if (blGeo is null)
        {
            return null;
        }

        // Top-right: GPS destination (CRC convention)
        var trLatRationals = gpsDir.GetRationalArray(GpsDirectory.TagDestLatitude);
        var trLatRef = gpsDir.GetString(GpsDirectory.TagDestLatitudeRef);
        var trLonRationals = gpsDir.GetRationalArray(GpsDirectory.TagDestLongitude);
        var trLonRef = gpsDir.GetString(GpsDirectory.TagDestLongitudeRef);

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

        var bl = blGeo.Value;
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

    public void Dispose()
    {
        _http.Dispose();
    }
}
