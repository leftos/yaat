using System.Net;
using System.Text;
using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Data.Vnas;

/// <summary>
/// <see cref="VnasArtccList"/> ships the vNAS ARTCC ids and refreshes them on demand from data-api's <c>/api/artccs</c>,
/// whose reply is the full ARTCC payload: it is read as a stream, keeping only each ARTCC's id and name, and cached. A
/// failed refresh keeps the old cache and says why; a corrupt cache falls back to the committed list and says so.
/// </summary>
public class VnasArtccListTests : IDisposable
{
    // A real /api/artccs reply trimmed to two ARTCC objects, each kept whole.
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "VnasTraining", "artccs-ZUA-ZHN.json");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaat-artccs-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_dir, "artccs.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }

    // A stream that cannot seek, report its length or be read more than 4 KB at a time, as a network body reads.
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        private const int MaxRead = 4096;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, MaxRead));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, MaxRead)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static HttpResponseMessage RealReply() =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(File.OpenRead(FixturePath))) };

    [Fact]
    public void Load_NoCache_ReturnsTheCommittedList()
    {
        ArtccListLoad load = new VnasArtccList(CachePath).Load();

        Assert.False(load.FromCache);
        Assert.Null(load.Problem);
        Assert.Equal(VnasArtccList.Committed, load.Artccs);
        Assert.Contains(load.Artccs, a => a.Id == "ZOA");
    }

    [Fact]
    public async Task Refresh_OverTheRealReply_ReadAsAStream_WritesIdsAndNames()
    {
        var handler = new FakeHandler(_ => RealReply());
        using var http = new HttpClient(handler);
        var list = new VnasArtccList(CachePath);

        VnasFetchResult<IReadOnlyList<VnasArtcc>> result = await list.RefreshAsync(http, TestContext.Current.CancellationToken);

        VnasArtcc[] expected = [new("ZUA", "Guam CERAP"), new("ZHN", "Honolulu Control Facility")];
        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Value);
        Assert.Equal([new Uri(VnasArtccList.Url)], handler.Requests);
        Assert.True(File.Exists(CachePath));
        ArtccListLoad load = list.Load();
        Assert.True(load.FromCache);
        Assert.Null(load.Problem);
        Assert.Equal(expected, load.Artccs);
    }

    [Fact]
    public async Task Refresh_Failure_KeepsTheOldCache_AndReportsWhy()
    {
        bool fail = false;
        using var http = new HttpClient(new FakeHandler(_ => fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : RealReply()));
        var list = new VnasArtccList(CachePath);
        Assert.True((await list.RefreshAsync(http, TestContext.Current.CancellationToken)).Succeeded);
        byte[] before = File.ReadAllBytes(CachePath);
        fail = true;

        VnasFetchResult<IReadOnlyList<VnasArtcc>> result = await list.RefreshAsync(http, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("503", result.Failure, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(CachePath));
        Assert.True(list.Load().FromCache);
    }

    // The bound on a wait that must end; a hang guard only, never a timing assertion.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static HttpResponseMessage Reply(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new MemoryStream(body))) };

    // Refreshes over the real reply so a cache exists, then over the given body; asserts the second refresh left the
    // cache byte for byte and returns its result.
    private async Task<VnasFetchResult<IReadOnlyList<VnasArtcc>>> RefreshOverAGoodCache(byte[] body)
    {
        bool second = false;
        using var http = new HttpClient(new FakeHandler(_ => second ? Reply(body) : RealReply()));
        var list = new VnasArtccList(CachePath);
        Assert.True((await list.RefreshAsync(http, TestContext.Current.CancellationToken)).Succeeded);
        byte[] before = File.ReadAllBytes(CachePath);
        second = true;

        VnasFetchResult<IReadOnlyList<VnasArtcc>> result = await list.RefreshAsync(http, TestContext.Current.CancellationToken);

        Assert.Equal(before, File.ReadAllBytes(CachePath));
        return result;
    }

    private static void AssertFailed(VnasFetchResult<IReadOnlyList<VnasArtcc>> result)
    {
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public async Task Refresh_TruncatedBody_Fails_AndKeepsTheOldCache()
    {
        byte[] whole = File.ReadAllBytes(FixturePath);
        int facility = whole.AsSpan().IndexOf("\"facility\""u8);
        Assert.True(facility > 0);

        // Cut inside the "facility" property-name token of the first ARTCC.
        AssertFailed(await RefreshOverAGoodCache(whole[..(facility + 4)]));
    }

    [Fact]
    public async Task Refresh_TopLevelObject_Fails_AndKeepsTheOldCache() => AssertFailed(await RefreshOverAGoodCache("{}"u8.ToArray()));

    [Fact]
    public async Task Refresh_EmptyArray_Fails_AndKeepsTheOldCache() => AssertFailed(await RefreshOverAGoodCache("[]"u8.ToArray()));

    [Fact]
    public async Task Refresh_InvalidUtf8InAString_Fails_AndKeepsTheOldCache()
    {
        byte[] body = [.. Encoding.UTF8.GetBytes("[{\"id\":\"ZU"), 0xFF, .. Encoding.UTF8.GetBytes("A\",\"facility\":{\"name\":\"Guam CERAP\"}}]")];

        VnasFetchResult<IReadOnlyList<VnasArtcc>> result = await RefreshOverAGoodCache(body);

        AssertFailed(result);
        Assert.Contains("not valid UTF-8", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_StringLongerThanTheReadWindow_InASkippedField_ReadsTheArtccsAfterIt()
    {
        string notes = new('x', 70 * 1024);
        string body = $$$"""
            [{"id":"ZUA","notes":"{{{notes}}}","facility":{"name":"Guam CERAP"}},{"id":"ZHN","facility":{"name":"Honolulu Control Facility"}}]
            """;
        using var http = new HttpClient(new FakeHandler(_ => Reply(Encoding.UTF8.GetBytes(body))));

        VnasFetchResult<IReadOnlyList<VnasArtcc>> result = await new VnasArtccList(CachePath).RefreshAsync(
            http,
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
        Assert.Equal([new VnasArtcc("ZUA", "Guam CERAP"), new VnasArtcc("ZHN", "Honolulu Control Facility")], result.Value);
    }

    [Fact]
    public async Task Refresh_InvalidAndRepeatedIds_AreDropped_TheRestCachedAndUsable()
    {
        const string Body = """
            [{"id":"ZUA","facility":{"name":"Guam CERAP"}},{"id":"Z A","facility":{"name":"Bad"}},{"id":"zua","facility":{"name":"Again"}}]
            """;
        using var http = new HttpClient(new FakeHandler(_ => Reply(Encoding.UTF8.GetBytes(Body))));
        var list = new VnasArtccList(CachePath);

        VnasFetchResult<IReadOnlyList<VnasArtcc>> result = await list.RefreshAsync(http, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal([new VnasArtcc("ZUA", "Guam CERAP")], result.Value);
        ArtccListLoad load = list.Load();
        Assert.True(load.FromCache);
        Assert.Null(load.Problem);
        Assert.Equal(["ZUA"], load.Artccs.Select(a => a.Id));
    }

    // Waits on the request's token, so only a cancel ends it.
    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The delay only ends by a cancel.");
        }
    }

    [Fact]
    public async Task Refresh_NoAnswerInTime_IsAFailure_NotThrown()
    {
        // HttpClient's own timeout cancels the request while the caller's token stays live: the refresh's timeout branch.
        using var http = new HttpClient(new NeverAnsweringHandler()) { Timeout = TimeSpan.FromMilliseconds(50) };

        VnasFetchResult<IReadOnlyList<VnasArtcc>> result = await new VnasArtccList(CachePath)
            .RefreshAsync(http, TestContext.Current.CancellationToken)
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("did not send", result.Failure, StringComparison.Ordinal);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public void Load_CorruptCache_ReturnsTheCommittedList_AndReportsTheProblem()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, "{ not json");

        ArtccListLoad load = new VnasArtccList(CachePath).Load();

        Assert.False(load.FromCache);
        Assert.NotNull(load.Problem);
        Assert.Equal(VnasArtccList.Committed, load.Artccs);
        Assert.Equal("{ not json", File.ReadAllText(CachePath));
    }
}
