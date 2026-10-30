using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The corpus download: a good file lands and verifies, a failed or short download leaves no
/// <c>.partial</c> behind, and a cached file is trusted only when its size and hash both match.
/// </summary>
public sealed class CorpusWerDownloadTests : IDisposable
{
    private static readonly byte[] Payload = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))];

    private static readonly CorpusDownload Download = new(
        "https://example.invalid/corpus.parquet",
        Payload.Length,
        Convert.ToHexStringLower(SHA256.HashData(Payload))
    );

    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-wer-download-").FullName;

    private string CacheDir => Path.Combine(_dir, "nested");

    private string CachePath => Path.Combine(CacheDir, "test.parquet");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_Good_Download_Lands_And_Verifies()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Payload);
        using var http = new HttpClient(handler);

        string path = await UwbAtccCorpus.EnsureDownloadedAsync(http, Download, CachePath, TestContext.Current.CancellationToken);

        Assert.Equal(CachePath, path);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
        Assert.False(File.Exists(CachePath + ".partial"));
    }

    [Fact]
    public async Task An_Http_Error_Throws_And_Leaves_No_Partial()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, []));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            UwbAtccCorpus.EnsureDownloadedAsync(http, Download, CachePath, TestContext.Current.CancellationToken)
        );

        Assert.False(File.Exists(CachePath + ".partial"));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task A_Short_Download_Throws_And_Leaves_No_Partial()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, Payload[..100]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UwbAtccCorpus.EnsureDownloadedAsync(http, Download, CachePath, TestContext.Current.CancellationToken)
        );

        Assert.False(File.Exists(CachePath + ".partial"));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task A_Cached_File_With_The_Wrong_Hash_Is_Deleted()
    {
        byte[] wrong = new byte[Payload.Length];
        WriteCached(wrong);
        var handler = new StubHandler(HttpStatusCode.OK, Payload);
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UwbAtccCorpus.EnsureDownloadedAsync(http, Download, CachePath, TestContext.Current.CancellationToken)
        );

        Assert.False(File.Exists(CachePath));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_Cached_File_With_The_Wrong_Size_Is_Downloaded_Again()
    {
        WriteCached(Payload[..10]);
        var handler = new StubHandler(HttpStatusCode.OK, Payload);
        using var http = new HttpClient(handler);

        await UwbAtccCorpus.EnsureDownloadedAsync(http, Download, CachePath, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(CachePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_Good_Cached_File_Makes_No_Request()
    {
        WriteCached(Payload);
        var handler = new StubHandler(HttpStatusCode.OK, Payload);
        using var http = new HttpClient(handler);

        string path = await UwbAtccCorpus.EnsureDownloadedAsync(http, Download, CachePath, TestContext.Current.CancellationToken);

        Assert.Equal(CachePath, path);
        Assert.Equal(0, handler.Calls);
    }

    private void WriteCached(byte[] bytes)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllBytes(CachePath, bytes);
    }

    /// <summary>Answers every request with one status and body, counting the requests.</summary>
    private sealed class StubHandler(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }
}
