using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="HttpFileCache"/> is the shared network↔disk step behind the vNAS-backed caches. These
/// pin the two freshness strategies plus the disk-TTL skip-gate and network-failure fallback that the
/// airport-map / ARTCC-config / video-map callers each relied on before the dedup.
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

                Assert.Equal(
                    "v1",
                    await HttpFileCache.GetOrRefreshAsync(http, "http://x/y", cachePath, HttpCacheFreshness.AlwaysRefetch, null, NullLogger.Instance)
                );

                body = "v2";
                Assert.Equal(
                    "v2",
                    await HttpFileCache.GetOrRefreshAsync(http, "http://x/y", cachePath, HttpCacheFreshness.AlwaysRefetch, null, NullLogger.Instance)
                );

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

                Assert.Equal(
                    "cached",
                    await HttpFileCache.GetOrRefreshAsync(http, "http://x/y", cachePath, HttpCacheFreshness.AlwaysRefetch, null, NullLogger.Instance)
                );

                fail = true;
                Assert.Equal(
                    "cached",
                    await HttpFileCache.GetOrRefreshAsync(http, "http://x/y", cachePath, HttpCacheFreshness.AlwaysRefetch, null, NullLogger.Instance)
                );
            }
        );
    }

    [Fact]
    public async Task AlwaysRefetch_404_NoCache_ReturnsNull()
    {
        var cachePath = NewCacheFile();
        await WithCache(
            cachePath,
            async () =>
            {
                var handler = new FakeHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
                using var http = new HttpClient(handler);

                Assert.Null(
                    await HttpFileCache.GetOrRefreshAsync(http, "http://x/y", cachePath, HttpCacheFreshness.AlwaysRefetch, null, NullLogger.Instance)
                );
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

                Assert.Equal(
                    "body",
                    await HttpFileCache.GetOrRefreshAsync(http, "http://x/y", cachePath, HttpCacheFreshness.AlwaysRefetch, ttl, NullLogger.Instance)
                );
                var afterFirst = handler.GetCount;

                // Second call within the TTL window must serve disk without another GET.
                Assert.Equal(
                    "body",
                    await HttpFileCache.GetOrRefreshAsync(http, "http://x/y", cachePath, HttpCacheFreshness.AlwaysRefetch, ttl, NullLogger.Instance)
                );
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

                Assert.Equal(
                    "map-v1",
                    await HttpFileCache.GetOrRefreshAsync(
                        http,
                        "http://x/m",
                        cachePath,
                        HttpCacheFreshness.HeadLastModified,
                        null,
                        NullLogger.Instance
                    )
                );
                var getsAfterFirst = handler.GetCount;

                // Server Last-Modified is unchanged (older than our stamped mtime) → HEAD only, no re-GET.
                body = "map-v2-should-not-be-served";
                Assert.Equal(
                    "map-v1",
                    await HttpFileCache.GetOrRefreshAsync(
                        http,
                        "http://x/m",
                        cachePath,
                        HttpCacheFreshness.HeadLastModified,
                        null,
                        NullLogger.Instance
                    )
                );
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

                Assert.Equal(
                    "map-v1",
                    await HttpFileCache.GetOrRefreshAsync(
                        http,
                        "http://x/m",
                        cachePath,
                        HttpCacheFreshness.HeadLastModified,
                        null,
                        NullLogger.Instance
                    )
                );

                // Origin now advertises a newer Last-Modified → the next check must re-download.
                lastModified = DateTimeOffset.UtcNow.AddHours(1);
                body = "map-v2";
                Assert.Equal(
                    "map-v2",
                    await HttpFileCache.GetOrRefreshAsync(
                        http,
                        "http://x/m",
                        cachePath,
                        HttpCacheFreshness.HeadLastModified,
                        null,
                        NullLogger.Instance
                    )
                );
            }
        );
    }
}
