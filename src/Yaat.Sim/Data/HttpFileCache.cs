using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data;

/// <summary>
/// How <see cref="HttpFileCache"/> decides whether the cached file on disk is stale
/// relative to the origin.
/// </summary>
public enum HttpCacheFreshness
{
    /// <summary>
    /// GET the resource on every refresh and overwrite the cache only when the body changed. For
    /// origins with no usable conditional-request support: the vNAS data-api <c>/api/…</c> endpoints
    /// 405 on HEAD and send no <c>Last-Modified</c>/<c>ETag</c> (and ignore <c>If-Modified-Since</c>),
    /// so a conditional probe can never detect an update — a full GET is the only reliable refresh.
    /// Bound the fetch rate with a <c>diskTtl</c> or caller-side memoization.
    /// </summary>
    AlwaysRefetch,

    /// <summary>
    /// HEAD the resource and compare its <c>Last-Modified</c> against the cached file's mtime, GETting
    /// only when the origin copy is newer (and stamping the downloaded file's mtime with the server's
    /// <c>Last-Modified</c>). For origins that serve <c>Last-Modified</c> but ignore
    /// <c>If-Modified-Since</c>: the vNAS <c>/Files/…</c> static maps.
    /// </summary>
    HeadLastModified,
}

/// <summary>
/// Downloads a text resource to a disk cache and serves it, sharing the freshness/refresh/fallback
/// logic that several vNAS-backed caches would otherwise each reimplement (the airport ground map,
/// ARTCC config, video maps, tower-cab maps). Owns only the network↔disk step: callers layer their
/// own in-memory memoization, TTL policy, and parsing on top.
/// </summary>
public static class HttpFileCache
{
    /// <summary>
    /// Ensures <paramref name="cachePath"/> holds a reasonably fresh copy of <paramref name="url"/>
    /// and returns its text, or null when the resource is unavailable and nothing is cached.
    ///
    /// When <paramref name="diskTtl"/> is set and the cached file is younger than it, the network is
    /// skipped entirely. Otherwise <paramref name="freshness"/> decides whether to re-download. A
    /// network failure or timeout is logged and the existing on-disk copy is served; an HTTP 404
    /// leaves the cache untouched.
    /// </summary>
    public static async Task<string?> GetOrRefreshAsync(
        HttpClient http,
        string url,
        string cachePath,
        HttpCacheFreshness freshness,
        TimeSpan? diskTtl,
        ILogger log,
        CancellationToken cancellationToken = default
    )
    {
        var dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if ((diskTtl is { } ttl) && File.Exists(cachePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < ttl))
        {
            return await File.ReadAllTextAsync(cachePath, cancellationToken);
        }

        try
        {
            if (freshness == HttpCacheFreshness.HeadLastModified)
            {
                await RefreshViaHeadAsync(http, url, cachePath, log, cancellationToken);
            }
            else
            {
                await RefreshViaGetAsync(http, url, cachePath, resetTtlClock: diskTtl is not null, log, cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Failed to refresh cached file from {Url}; using cached copy if present", url);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(ex, "Timed out refreshing cached file from {Url}; using cached copy if present", url);
        }

        return File.Exists(cachePath) ? await File.ReadAllTextAsync(cachePath, cancellationToken) : null;
    }

    /// <summary>
    /// Unconditional GET with write-if-changed. A 404 leaves the cache untouched. When
    /// <paramref name="resetTtlClock"/> is set and the body is unchanged, the file's mtime is
    /// touched so a caller's disk-TTL window restarts (an unchanged refresh still counts as fresh);
    /// without a TTL the file is left alone to avoid churn.
    /// </summary>
    private static async Task RefreshViaGetAsync(
        HttpClient http,
        string url,
        string cachePath,
        bool resetTtlClock,
        ILogger log,
        CancellationToken cancellationToken
    )
    {
        using var resp = await http.GetAsync(url, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            log.LogInformation("No resource available at {Url} (HTTP 404); leaving cache untouched", url);
            return;
        }

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (File.Exists(cachePath) && string.Equals(await File.ReadAllTextAsync(cachePath, cancellationToken), body, StringComparison.Ordinal))
        {
            if (resetTtlClock)
            {
                File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
            }

            return;
        }

        await File.WriteAllTextAsync(cachePath, body, cancellationToken);
        log.LogDebug("Refreshed cached file from {Url}", url);
    }

    /// <summary>
    /// HEAD → compare <c>Last-Modified</c> to the cached mtime → GET only when the origin is newer or
    /// nothing is cached. A HEAD that fails (or omits <c>Last-Modified</c> on a live cache) keeps the
    /// cached copy rather than blindly re-downloading. The downloaded file's mtime is stamped with the
    /// server's <c>Last-Modified</c> so the next check is consistent.
    /// </summary>
    private static async Task RefreshViaHeadAsync(HttpClient http, string url, string cachePath, ILogger log, CancellationToken cancellationToken)
    {
        if (File.Exists(cachePath))
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResp = await http.SendAsync(headReq, cancellationToken);
            if (!headResp.IsSuccessStatusCode)
            {
                log.LogWarning("HEAD {Url} returned {Status}; using cached copy", url, headResp.StatusCode);
                return;
            }

            var serverLastModified = headResp.Content.Headers.LastModified?.UtcDateTime;
            if ((serverLastModified is { } sm) && (sm <= File.GetLastWriteTimeUtc(cachePath)))
            {
                return;
            }
        }

        using var getResp = await http.GetAsync(url, cancellationToken);
        getResp.EnsureSuccessStatusCode();
        var body = await getResp.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync(cachePath, body, cancellationToken);

        if (getResp.Content.Headers.LastModified?.UtcDateTime is { } stamp)
        {
            File.SetLastWriteTimeUtc(cachePath, stamp);
        }

        log.LogDebug("Downloaded cached file from {Url}", url);
    }
}
