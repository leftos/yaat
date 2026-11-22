using System.Net;
using System.Text;
using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Data.Vnas;

/// <summary>
/// <see cref="ArtccConfigSource"/> reads one ARTCC's vNAS config from data-api's <c>/api/artccs/{ID}</c> into
/// <c>{cacheRoot}/{ID}.json</c>, the file the client's ArtccAirportResolver also writes. A copy inside the TTL is read
/// without a download, a failed refresh serves the copy on disk and says so, and no outcome throws but a cancel.
/// </summary>
public class ArtccConfigSourceTests : IDisposable
{
    // A position of ZOA's own facility in the committed snapshot: OAK_14_CTR, Sector 14.
    private const string ZoaSector14Id = "01GEAT1S60WNVZ3VQXP7TSQF84";

    private const string SnapshotMissing = "the committed ZOA snapshot is not in the test output";

    private static readonly string ZoaSnapshotPath = Path.Combine(AppContext.BaseDirectory, "TestData", "artcc-zoa-snapshot.json");

    // A fixed clock in the past: the shared HttpFileCache counts its own TTL on the wall clock, so the files these tests
    // stamp relative to it are never in the future.
    private static readonly DateTime Now = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // The cache root sits one level inside the test's own folder, so a file written beside the root is seen too.
    private readonly string _base = Path.Combine(Path.GetTempPath(), "yaat-artcc-config-" + Guid.NewGuid().ToString("N"));

    private string Root => Path.Combine(_base, "artcc");

    private string ZoaCachePath => Path.Combine(Root, "ZOA.json");

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ColdFetch_WritesTheUpperCaseCacheFile_AndReturnsTheTypedConfig()
    {
        Assert.SkipUnless(File.Exists(ZoaSnapshotPath), SnapshotMissing);
        var handler = new FakeArtccHandler(_ => Ok(File.ReadAllText(ZoaSnapshotPath)));

        ArtccConfigLoad load = await NewSource(handler).LoadAsync("ZOA", TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.Fresh, load.Status);
        Assert.Equal("ZOA", load.ArtccId);
        Assert.NotNull(load.Config);
        Assert.Equal("ZOA", load.Config.Facility.Id);
        Assert.Equal("OAK_14_CTR", load.Config.FindPosition(ZoaSector14Id).Position?.Callsign);
        Assert.True(File.Exists(ZoaCachePath));
        Assert.Equal(new Uri("https://data-api.vnas.vatsim.net/api/artccs/ZOA"), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task FileTheAirportResolverWrote_InsideTheTtl_IsReadWithNoHttpCall()
    {
        Assert.SkipUnless(File.Exists(ZoaSnapshotPath), SnapshotMissing);

        // ArtccAirportResolver writes the raw data-api body to cache/artcc/{id}.json.
        WriteCache(File.ReadAllText(ZoaSnapshotPath), Now - TimeSpan.FromHours(1));
        var handler = new FakeArtccHandler(_ => throw new HttpRequestException("no network in this test"));

        ArtccConfigLoad load = await NewSource(handler).LoadAsync("ZOA", TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.Fresh, load.Status);
        Assert.Equal("ZOA", load.Config?.Facility.Id);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StaleFile_NetworkFails_ServesTheCachedConfigFlaggedWithItsAge()
    {
        Assert.SkipUnless(File.Exists(ZoaSnapshotPath), SnapshotMissing);
        DateTime written = Now - TimeSpan.FromHours(7);
        WriteCache(File.ReadAllText(ZoaSnapshotPath), written);
        var handler = new FakeArtccHandler(_ => throw new HttpRequestException("offline"));

        ArtccConfigLoad load = await NewSource(handler).LoadAsync("ZOA", TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.FromCache, load.Status);
        Assert.Equal("ZOA", load.Config?.Facility.Id);
        Assert.Equal(written, load.CachedCopyUtc);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NoCache_NetworkFails_IsUnreachableWithNoConfig_AndDoesNotThrow()
    {
        var handler = new FakeArtccHandler(_ => throw new HttpRequestException("offline"));
        ArtccConfigSource source = NewSource(handler);

        ArtccConfigLoad load = await source.LoadAsync("ZOA", TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.Unreachable, load.Status);
        Assert.Null(load.Config);
        Assert.NotNull(load.Problem);
        Assert.Null(await source.TryGetCachedAsync("ZOA", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NotFound_WithACachedCopy_ServesItAsStale_AndWithoutOneIsNotFound()
    {
        Assert.SkipUnless(File.Exists(ZoaSnapshotPath), SnapshotMissing);
        var handler = new FakeArtccHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        ArtccConfigSource source = NewSource(handler);

        ArtccConfigLoad none = await source.LoadAsync("ZOA", TestContext.Current.CancellationToken);
        Assert.Equal(ArtccConfigStatus.NotFound, none.Status);
        Assert.Null(none.Config);

        DateTime written = Now - TimeSpan.FromHours(7);
        WriteCache(File.ReadAllText(ZoaSnapshotPath), written);
        ArtccConfigLoad cached = await source.LoadAsync("ZOA", TestContext.Current.CancellationToken);
        Assert.Equal(ArtccConfigStatus.FromCache, cached.Status);
        Assert.Equal("ZOA", cached.Config?.Facility.Id);
        Assert.Equal(written, cached.CachedCopyUtc);
        Assert.Contains("404", cached.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptCache_NetworkFails_IsUnparseable_AndTheFileIsKept()
    {
        const string Corrupt = "{\"id\":\"ZOA\",\"facility\":";
        WriteCache(Corrupt, Now - TimeSpan.FromHours(1));
        var handler = new FakeArtccHandler(_ => throw new HttpRequestException("offline"));

        ArtccConfigLoad load = await NewSource(handler).LoadAsync("ZOA", TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.Unparseable, load.Status);
        Assert.Null(load.Config);
        Assert.NotNull(load.Problem);
        Assert.Single(handler.Requests);
        Assert.Equal(Corrupt, File.ReadAllText(ZoaCachePath));
    }

    [Fact]
    public async Task CorruptCache_NetworkWorks_IsRepairedToFresh()
    {
        Assert.SkipUnless(File.Exists(ZoaSnapshotPath), SnapshotMissing);
        WriteCache("{\"id\":\"ZOA\",\"facility\":", Now - TimeSpan.FromHours(1));
        string snapshot = File.ReadAllText(ZoaSnapshotPath);
        var handler = new FakeArtccHandler(_ => Ok(snapshot));

        ArtccConfigLoad load = await NewSource(handler).LoadAsync("ZOA", TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.Fresh, load.Status);
        Assert.Equal("ZOA", load.Config?.Facility.Id);
        Assert.Equal(snapshot, File.ReadAllText(ZoaCachePath));
    }

    [Fact]
    public async Task StaleGoodFile_A200WithAnHtmlBody_KeepsTheOldConfigOnDisk()
    {
        Assert.SkipUnless(File.Exists(ZoaSnapshotPath), SnapshotMissing);
        string snapshot = File.ReadAllText(ZoaSnapshotPath);
        DateTime written = Now - TimeSpan.FromHours(7);
        WriteCache(snapshot, written);
        var handler = new FakeArtccHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Service Unavailable</body></html>", Encoding.UTF8, "text/html"),
        });
        ArtccConfigSource source = NewSource(handler);

        ArtccConfigLoad load = await source.LoadAsync("ZOA", TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.FromCache, load.Status);
        Assert.Equal("OAK_14_CTR", load.Config?.FindPosition(ZoaSector14Id).Position?.Callsign);
        Assert.Equal(written, load.CachedCopyUtc);
        Assert.NotNull(load.Problem);
        Assert.Equal(snapshot, File.ReadAllText(ZoaCachePath));
        Assert.Equal("ZOA", (await NewSource(handler).TryGetCachedAsync("ZOA", TestContext.Current.CancellationToken))?.Facility.Id);
    }

    [Fact]
    public async Task LowerCaseId_UsesTheSameFileAndUrlAsUpperCase()
    {
        Assert.SkipUnless(File.Exists(ZoaSnapshotPath), SnapshotMissing);
        var handler = new FakeArtccHandler(_ => Ok(File.ReadAllText(ZoaSnapshotPath)));

        ArtccConfigLoad load = await NewSource(handler).LoadAsync(" zoa ", TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.Fresh, load.Status);
        Assert.Equal("ZOA", load.ArtccId);
        Assert.Equal(new Uri("https://data-api.vnas.vatsim.net/api/artccs/ZOA"), Assert.Single(handler.Requests));
        Assert.Equal("ZOA.json", Path.GetFileName(Assert.Single(Directory.GetFiles(Root))));
    }

    [Theory]
    [InlineData("../ZOA")]
    [InlineData("ZOA/..")]
    [InlineData("Z\\OA")]
    [InlineData("ZO A")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ZOA.json")]
    public async Task AnIdThatIsNotPlainLettersAndDigits_IsNotFound_WithNoRequestAndNoFile(string artccId)
    {
        var handler = new FakeArtccHandler(_ => Ok("{}"));

        ArtccConfigLoad load = await NewSource(handler).LoadAsync(artccId, TestContext.Current.CancellationToken);

        Assert.Equal(ArtccConfigStatus.NotFound, load.Status);
        Assert.Null(load.Config);
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(_base) && Directory.EnumerateFileSystemEntries(_base, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task ACancelledLoad_Throws()
    {
        var handler = new FakeArtccHandler(_ => Ok("{}"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewSource(handler).LoadAsync("ZOA", cancelled.Token));
    }

    internal static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private ArtccConfigSource NewSource(FakeArtccHandler handler) => new(new HttpClient(handler), Root, () => Now);

    private void WriteCache(string body, DateTime writtenUtc)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(ZoaCachePath, body);
        File.SetLastWriteTimeUtc(ZoaCachePath, writtenUtc);
    }
}

/// <summary>An HTTP handler answering every request with <paramref name="responder"/> and recording the URLs asked for.</summary>
/// <param name="responder">Answers a request, or throws to fail it as the network would.</param>
internal sealed class FakeArtccHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly List<Uri> _requests = [];

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _requests.Add(request.RequestUri!);
        }

        return Task.FromResult(responder(request));
    }
}
