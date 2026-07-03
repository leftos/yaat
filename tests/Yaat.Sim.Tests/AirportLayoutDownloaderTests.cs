using System.Net;
using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

/// <summary>
/// The vNAS training-airports origin (Cloudflare) hard-405s HEAD requests and sends no
/// Last-Modified/ETag on GET, so the old HEAD-based freshness probe never fired and a cached
/// airport map was frozen forever after the first download (the SFO A/B1 stale-layout bug).
/// The downloader must refresh via an unconditional GET and only keep the cache on network
/// failure.
/// </summary>
public class AirportLayoutDownloaderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; }

        public int GetCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                GetCount++;
            }

            return Task.FromResult(Responder(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string NewCacheDir() => Path.Combine(Path.GetTempPath(), "ald-" + Guid.NewGuid());

    [Fact]
    public async Task GetGeoJson_RefreshesCache_EvenWhenHeadReturns405()
    {
        string cacheDir = NewCacheDir();
        string body = "v1";
        var handler = new FakeHandler
        {
            Responder = req => req.Method == HttpMethod.Head ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) : Ok(body),
        };

        try
        {
            using var dl = new AirportLayoutDownloader(new HttpClient(handler), cacheDir);

            Assert.Equal("v1", await dl.GetGeoJsonAsync("KSFO"));

            body = "v2";
            // Must pick up the server-side change despite HEAD 405 and no Last-Modified/ETag.
            Assert.Equal("v2", await dl.GetGeoJsonAsync("KSFO"));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetGeoJson_NetworkFailure_FallsBackToCachedCopy()
    {
        string cacheDir = NewCacheDir();
        bool fail = false;
        var handler = new FakeHandler
        {
            Responder = req =>
            {
                if (fail)
                {
                    throw new HttpRequestException("offline");
                }

                return Ok("cached-body");
            },
        };

        try
        {
            using var dl = new AirportLayoutDownloader(new HttpClient(handler), cacheDir);
            Assert.Equal("cached-body", await dl.GetGeoJsonAsync("KSFO"));

            fail = true;
            // The refresh GET throws, but the on-disk copy must still be served.
            Assert.Equal("cached-body", await dl.GetGeoJsonAsync("KSFO"));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetGeoJson_404_NoCache_ReturnsNull()
    {
        string cacheDir = NewCacheDir();
        var handler = new FakeHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };

        try
        {
            using var dl = new AirportLayoutDownloader(new HttpClient(handler), cacheDir);
            Assert.Null(await dl.GetGeoJsonAsync("KZZZ"));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
    }
}
