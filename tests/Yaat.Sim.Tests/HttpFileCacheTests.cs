using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="HttpFileCache"/> is the shared network↔disk step behind the vNAS-backed caches. These
/// pin the two freshness strategies plus the disk-TTL skip-gate and network-failure fallback that the
/// airport-map / ARTCC-config / video-map callers each relied on before the dedup, and the
/// <see cref="HttpCacheResult.NotFound"/> flag callers use for negative caching.
/// </summary>
public class HttpFileCacheTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }

        public int GetCount { get; private set; }
        public int HeadCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                GetCount++;
            }
            else if (request.Method == HttpMethod.Head)
            {
                HeadCount++;
            }

            return Task.FromResult(Responder(request));
        }
    }

    private static HttpResponseMessage Ok(string body, DateTimeOffset? lastModified = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        if (lastModified is { } lm)
        {
            resp.Content.Headers.LastModified = lm;
        }

        return resp;
    }

    private static string NewCacheFile() => Path.Combine(Path.GetTempPath(), "hfc-" + Guid.NewGuid(), "cache.txt");

    private static Task<HttpCacheResult> Fetch(HttpClient http, string cachePath, HttpCacheFreshness freshness, TimeSpan? ttl = null) =>
        HttpFileCache.GetOrRefreshAsync(http, "http://x/y", cachePath, freshness, ttl, NullLogger.Instance);

    private static async Task WithCache(string cachePath, Func<Task> body)
    {
        try
        {
            await body();
        }
        finally
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AlwaysRefetch_PicksUpContentChange_WithoutHead()
    {
        var cachePath = NewCacheFile();
        await WithCache(
            cachePath,
            async () =>
            {
                var body = "v1";
                var handler = new FakeHandler { Responder = _ => Ok(body) };
                using var http = new HttpClient(handler);

                Assert.Equal("v1", (await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch)).Content);

                body = "v2";
                Assert.Equal("v2", (await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch)).Content);

                Assert.Equal(0, handler.HeadCount);
            }
        );
    }

    [Fact]
    public async Task AlwaysRefetch_NetworkFailure_FallsBackToCachedCopy()
    {
        var cachePath = NewCacheFile();
        await WithCache(
            cachePath,
            async () =>
            {
                var fail = false;
                var handler = new FakeHandler { Responder = _ => fail ? throw new HttpRequestException("offline") : Ok("cached") };
                using var http = new HttpClient(handler);

                Assert.Equal("cached", (await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch)).Content);

                fail = true;
                var result = await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch);
                Assert.Equal("cached", result.Content);
                // A network failure is not a 404 — callers must not negative-cache it.
                Assert.False(result.NotFound);
            }
        );
    }

    [Fact]
    public async Task AlwaysRefetch_404_NoCache_ReturnsNull_AndReportsNotFound()
    {
        var cachePath = NewCacheFile();
        await WithCache(
            cachePath,
            async () =>
            {
                var handler = new FakeHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
                using var http = new HttpClient(handler);

                var result = await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch);
                Assert.Null(result.Content);
                Assert.True(result.NotFound);
            }
        );
    }

    [Fact]
    public async Task AlwaysRefetch_404_WithCache_ServesCache_AndReportsNotFound()
    {
        var cachePath = NewCacheFile();
        await WithCache(
            cachePath,
            async () =>
            {
                var notFound = false;
                var handler = new FakeHandler { Responder = _ => notFound ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok("cached") };
                using var http = new HttpClient(handler);

                Assert.Equal("cached", (await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch)).Content);

                // Origin starts 404ing (map unpublished): the cache is kept and served, and the 404
                // is still reported so callers can decide their own policy.
                notFound = true;
                var result = await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch);
                Assert.Equal("cached", result.Content);
                Assert.True(result.NotFound);
            }
        );
    }

    [Fact]
    public async Task DiskTtl_SkipsNetwork_WhileFresh()
    {
        var cachePath = NewCacheFile();
        await WithCache(
            cachePath,
            async () =>
            {
                var handler = new FakeHandler { Responder = _ => Ok("body") };
                using var http = new HttpClient(handler);
                var ttl = TimeSpan.FromHours(6);

                Assert.Equal("body", (await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch, ttl)).Content);
                var afterFirst = handler.GetCount;

                // Second call within the TTL window must serve disk without another GET.
                Assert.Equal("body", (await Fetch(http, cachePath, HttpCacheFreshness.AlwaysRefetch, ttl)).Content);
                Assert.Equal(afterFirst, handler.GetCount);
            }
        );
    }

    [Fact]
    public async Task HeadLastModified_ServesCache_WhenServerNotNewer()
    {
        var cachePath = NewCacheFile();
        await WithCache(
            cachePath,
            async () =>
            {
                var lastModified = DateTimeOffset.UtcNow.AddDays(-1);
                var body = "map-v1";
                var handler = new FakeHandler
                {
                    Responder = req => req.Method == HttpMethod.Head ? Ok(string.Empty, lastModified) : Ok(body, lastModified),
                };
                using var http = new HttpClient(handler);

                Assert.Equal("map-v1", (await Fetch(http, cachePath, HttpCacheFreshness.HeadLastModified)).Content);
                var getsAfterFirst = handler.GetCount;

                // Server Last-Modified is unchanged (older than our stamped mtime) → HEAD only, no re-GET.
                body = "map-v2-should-not-be-served";
                Assert.Equal("map-v1", (await Fetch(http, cachePath, HttpCacheFreshness.HeadLastModified)).Content);
                Assert.Equal(getsAfterFirst, handler.GetCount);
                Assert.True(handler.HeadCount >= 1);
            }
        );
    }

    [Fact]
    public async Task HeadLastModified_ReDownloads_WhenServerNewer()
    {
        var cachePath = NewCacheFile();
        await WithCache(
            cachePath,
            async () =>
            {
                var lastModified = DateTimeOffset.UtcNow.AddDays(-2);
                var body = "map-v1";
                var handler = new FakeHandler
                {
                    Responder = req => req.Method == HttpMethod.Head ? Ok(string.Empty, lastModified) : Ok(body, lastModified),
                };
                using var http = new HttpClient(handler);

                Assert.Equal("map-v1", (await Fetch(http, cachePath, HttpCacheFreshness.HeadLastModified)).Content);

                // Origin now advertises a newer Last-Modified → the next check must re-download.
                lastModified = DateTimeOffset.UtcNow.AddHours(1);
                body = "map-v2";
                Assert.Equal("map-v2", (await Fetch(http, cachePath, HttpCacheFreshness.HeadLastModified)).Content);
            }
        );
    }
}
