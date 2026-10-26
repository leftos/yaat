using System.Net;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The tower-cab map downloader is the single download path for a vNAS ARTCC video map: it requests
/// <c>{base}/{ARTCC}/{id}.geojson</c>, caches the body at the path the client's ground view has always
/// used, falls back to the cached copy on a network failure, and serves the cache without a request when
/// the base URL is empty (the vNAS config fetch failed).
/// </summary>
public class TowerCabMapDownloaderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }

        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(Responder(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string NewCacheRoot() => Path.Combine(Path.GetTempPath(), "tcm-" + Guid.NewGuid());

    private static void DeleteCacheRoot(string cacheRoot)
    {
        if (Directory.Exists(cacheRoot))
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://maps.test/files")]
    [InlineData("https://maps.test/files/")]
    public async Task FetchAsync_RequestsTheArtccMapUrl_AndCachesTheBody(string baseUrl)
    {
        string cacheRoot = NewCacheRoot();
        var handler = new StubHandler { Responder = _ => Ok("{\"type\":\"FeatureCollection\",\"features\":[]}") };

        try
        {
            using var downloader = new TowerCabMapDownloader(new HttpClient(handler), cacheRoot);

            HttpCacheResult result = await downloader.FetchAsync(baseUrl, "ZOA", "oak", TestContext.Current.CancellationToken);

            Assert.Equal("{\"type\":\"FeatureCollection\",\"features\":[]}", result.Content);
            Assert.False(result.RefreshFailed);
            Assert.Single(handler.RequestedUrls);
            Assert.Equal("https://maps.test/files/ZOA/oak.geojson", handler.RequestedUrls[0]);
            Assert.Equal(Path.Combine(cacheRoot, "ZOA", "oak.geojson"), downloader.GetCachePath("ZOA", "oak"));
            Assert.Equal(
                "{\"type\":\"FeatureCollection\",\"features\":[]}",
                await File.ReadAllTextAsync(downloader.GetCachePath("ZOA", "oak"), TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            DeleteCacheRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task FetchAsync_NetworkFailure_ServesTheCachedBody()
    {
        string cacheRoot = NewCacheRoot();
        bool offline = false;
        var handler = new StubHandler
        {
            Responder = _ =>
            {
                if (offline)
                {
                    throw new HttpRequestException("offline");
                }

                return Ok("map-v1");
            },
        };

        try
        {
            using var downloader = new TowerCabMapDownloader(new HttpClient(handler), cacheRoot);

            Assert.Equal("map-v1", (await downloader.FetchAsync("https://maps.test", "ZOA", "oak", TestContext.Current.CancellationToken)).Content);

            offline = true;
            HttpCacheResult result = await downloader.FetchAsync("https://maps.test", "ZOA", "oak", TestContext.Current.CancellationToken);

            Assert.Equal("map-v1", result.Content);
            Assert.True(result.RefreshFailed);
            Assert.False(result.NotFound);
        }
        finally
        {
            DeleteCacheRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task FetchAsync_EmptyBaseUrl_ServesTheCachedBodyWithoutARequest()
    {
        string cacheRoot = NewCacheRoot();

        try
        {
            string cachePath = Path.Combine(cacheRoot, "ZOA", "oak.geojson");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, "cached-map", TestContext.Current.CancellationToken);

            var handler = new StubHandler { Responder = _ => throw new InvalidOperationException("no request expected") };
            using var downloader = new TowerCabMapDownloader(new HttpClient(handler), cacheRoot);

            HttpCacheResult result = await downloader.FetchAsync("", "ZOA", "oak", TestContext.Current.CancellationToken);

            Assert.Equal("cached-map", result.Content);
            Assert.True(result.RefreshFailed);
            Assert.False(result.NotFound);
            Assert.Empty(handler.RequestedUrls);
        }
        finally
        {
            DeleteCacheRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task FetchAsync_EmptyBaseUrlAndNoCachedFile_ReturnsNoContentWithoutARequest()
    {
        string cacheRoot = NewCacheRoot();

        try
        {
            var handler = new StubHandler { Responder = _ => throw new InvalidOperationException("no request expected") };
            using var downloader = new TowerCabMapDownloader(new HttpClient(handler), cacheRoot);

            HttpCacheResult result = await downloader.FetchAsync("", "ZOA", "oak", TestContext.Current.CancellationToken);

            Assert.Null(result.Content);
            Assert.True(result.RefreshFailed);
            Assert.False(result.NotFound);
            Assert.Empty(handler.RequestedUrls);
        }
        finally
        {
            DeleteCacheRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task Dispose_LeavesAPassedClientUsable()
    {
        string cacheRoot = NewCacheRoot();

        try
        {
            var handler = new StubHandler { Responder = _ => Ok("map") };
            using var client = new HttpClient(handler);
            var downloader = new TowerCabMapDownloader(client, cacheRoot);

            downloader.Dispose();

            HttpCacheResult result = await downloader.FetchAsync("https://maps.test", "ZOA", "oak", TestContext.Current.CancellationToken);
            Assert.Equal("map", result.Content);
        }
        finally
        {
            DeleteCacheRoot(cacheRoot);
        }
    }

    [Fact]
    public async Task Dispose_DisposesAnOwnedClient()
    {
        string cacheRoot = NewCacheRoot();

        try
        {
            var downloader = new TowerCabMapDownloader(http: null, cacheRoot: cacheRoot);
            downloader.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                downloader.FetchAsync("https://maps.test", "ZOA", "oak", TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            DeleteCacheRoot(cacheRoot);
        }
    }
}
