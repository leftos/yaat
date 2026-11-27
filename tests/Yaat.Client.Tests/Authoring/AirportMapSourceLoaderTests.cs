using System.Net;
using System.Text.Json;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Airport Editor opens a map from the vNAS cache (through the downloader), a file or a folder holding one combined
/// <c>.geojson</c>, and says why when it opens nothing: one outcome per case, each with its own message.
/// </summary>
public sealed class AirportMapSourceLoaderTests : IDisposable
{
    private readonly AirportEditorTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task FromTheCache_AFreshFetch_OpensTheMap()
    {
        AirportLayoutDownloader downloader = _folder.Downloader(_ => StubMapHandler.Ok(AirportEditorTestFolder.OakText));

        AirportMapOpenOutcome outcome = await new AirportMapSourceLoader(downloader).OpenFromCacheAsync("KOAK", Ct);

        AirportMapOpenOutcome.Opened opened = Assert.IsType<AirportMapOpenOutcome.Opened>(outcome);
        Assert.Equal("OAK (vNAS cache)", opened.SourceLabel);
        Assert.Equal(CachedCopyReason.None, opened.CachedCopy);
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, opened.Document.Features.Count);
        Assert.Equal($"Opened OAK (vNAS cache): {AirportEditorTestFolder.OakFeatureCount} features.", opened.Message);
    }

    [Fact]
    public async Task FromTheCache_vNasUnreachable_WithADiskCopy_OpensTheCopy_AndSaysSo()
    {
        AirportLayoutDownloader downloader = _folder.Downloader(_ => throw new HttpRequestException("offline"));
        AirportEditorTestFolder.SeedCache(downloader);

        AirportMapOpenOutcome outcome = await new AirportMapSourceLoader(downloader).OpenFromCacheAsync("OAK", Ct);

        AirportMapOpenOutcome.Opened opened = Assert.IsType<AirportMapOpenOutcome.Opened>(outcome);
        Assert.Equal(CachedCopyReason.Offline, opened.CachedCopy);
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, opened.Document.Features.Count);
        Assert.Equal("vNAS not reachable; showing the cached copy.", opened.Message);
    }

    [Fact]
    public async Task FromTheCache_AFresh404_WithADiskCopy_OpensTheCopy_AsNoLongerListed()
    {
        AirportLayoutDownloader downloader = _folder.Downloader(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        AirportEditorTestFolder.SeedCache(downloader);

        AirportMapOpenOutcome outcome = await new AirportMapSourceLoader(downloader).OpenFromCacheAsync("KOAK", Ct);

        AirportMapOpenOutcome.Opened opened = Assert.IsType<AirportMapOpenOutcome.Opened>(outcome);
        Assert.Equal(CachedCopyReason.NoLongerListed, opened.CachedCopy);
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, opened.Document.Features.Count);
        Assert.Equal("vNAS no longer lists OAK; showing the cached copy.", opened.Message);
    }

    [Fact]
    public async Task FromTheCache_A404InsideTheDownloadersMemory_WithADiskCopy_OpensTheCopy_AsNoLongerListed()
    {
        AirportLayoutDownloader downloader = _folder.Downloader(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var loader = new AirportMapSourceLoader(downloader);
        Assert.IsType<AirportMapOpenOutcome.NotFound>(await loader.OpenFromCacheAsync("KOAK", Ct));
        AirportEditorTestFolder.SeedCache(downloader);

        AirportMapOpenOutcome outcome = await loader.OpenFromCacheAsync("KOAK", Ct);

        AirportMapOpenOutcome.Opened opened = Assert.IsType<AirportMapOpenOutcome.Opened>(outcome);
        Assert.Equal(CachedCopyReason.NoLongerListed, opened.CachedCopy);
        Assert.Equal("OAK (vNAS cache)", opened.SourceLabel);
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, opened.Document.Features.Count);
    }

    [Fact]
    public async Task FromTheCache_A404_WithNoCopy_IsNotFound()
    {
        AirportLayoutDownloader downloader = _folder.Downloader(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        AirportMapOpenOutcome outcome = await new AirportMapSourceLoader(downloader).OpenFromCacheAsync("KZZZ", Ct);

        AirportMapOpenOutcome.NotFound failed = Assert.IsType<AirportMapOpenOutcome.NotFound>(outcome);
        Assert.Equal("No vNAS map for ZZZ.", failed.Message);
    }

    [Fact]
    public async Task FromTheCache_vNasUnreachable_WithNoCopy_IsUnreachable()
    {
        AirportLayoutDownloader downloader = _folder.Downloader(_ => throw new HttpRequestException("offline"));

        AirportMapOpenOutcome outcome = await new AirportMapSourceLoader(downloader).OpenFromCacheAsync("KOAK", Ct);

        AirportMapOpenOutcome.Unreachable failed = Assert.IsType<AirportMapOpenOutcome.Unreachable>(outcome);
        Assert.Equal("Couldn't reach vNAS for OAK, and no copy is cached.", failed.Message);
    }

    [Fact]
    public async Task FromTheCache_ACacheFolderThatIsAFile_IsUnreadable_NamingTheCacheFile()
    {
        AirportLayoutDownloader downloader = _folder.Downloader(_ => StubMapHandler.Ok(AirportEditorTestFolder.OakText));
        Directory.CreateDirectory(Path.GetDirectoryName(downloader.CacheDir)!);
        await File.WriteAllTextAsync(downloader.CacheDir, "a file where the cache folder goes", Ct);

        AirportMapOpenOutcome outcome = await new AirportMapSourceLoader(downloader).OpenFromCacheAsync("KOAK", Ct);

        AirportMapOpenOutcome.Unreadable failed = Assert.IsType<AirportMapOpenOutcome.Unreadable>(outcome);
        Assert.Equal(downloader.GetCachePath("OAK"), failed.Path);
        Assert.Equal($"Couldn't read {downloader.GetCachePath("OAK")}: {failed.ReaderMessage}", failed.Message);
    }

    [Theory]
    [InlineData("..\\x")]
    [InlineData("")]
    [InlineData("KOAKX")]
    [InlineData("K OA")]
    public async Task FromTheCache_TextThatIsNotAnAirportId_IsInvalidAirportId_AndFetchesNothing(string id)
    {
        int requests = 0;
        AirportLayoutDownloader downloader = _folder.Downloader(_ =>
        {
            requests++;
            return StubMapHandler.Ok(AirportEditorTestFolder.OakText);
        });

        AirportMapOpenOutcome outcome = await new AirportMapSourceLoader(downloader).OpenFromCacheAsync(id, Ct);

        AirportMapOpenOutcome.InvalidAirportId failed = Assert.IsType<AirportMapOpenOutcome.InvalidAirportId>(outcome);
        Assert.Equal($"'{id}' is not an airport id.", failed.Message);
        Assert.Equal(0, requests);
        Assert.False(Directory.Exists(downloader.CacheDir));
    }

    [Fact]
    public async Task AMissingFile_IsFileMissing()
    {
        string path = Path.Combine(_folder.Root, "nowhere.geojson");

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFileAsync(path, Ct);

        AirportMapOpenOutcome.FileMissing failed = Assert.IsType<AirportMapOpenOutcome.FileMissing>(outcome);
        Assert.Equal($"File not found: {path}", failed.Message);
    }

    [Fact]
    public async Task ANonJsonFile_IsUnreadable_WithTheReadersMessage()
    {
        string path = _folder.WriteFile("broken.geojson", "this is not JSON");

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFileAsync(path, Ct);

        AirportMapOpenOutcome.Unreadable failed = Assert.IsType<AirportMapOpenOutcome.Unreadable>(outcome);
        Assert.Equal($"Couldn't read {path}: {AirportEditorTestFolder.NotJsonReaderMessage}", failed.Message);
    }

    [Fact]
    public async Task AFeatureThatRepeatsAPropertyKey_IsUnreadable()
    {
        const string Map = """
            {"type": "FeatureCollection", "features": [
              {"type": "Feature", "properties": {"type": "parking", "name": "A1", "name": "A2"},
               "geometry": {"type": "Point", "coordinates": [-122.22, 37.72]}}
            ]}
            """;
        string path = _folder.WriteFile("repeated-key.geojson", Map);

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFileAsync(path, Ct);

        // Read rejects the repeated key before any feature is built; the expected text is built from that exception.
        JsonException repeated = Assert.Throws<JsonException>(() => AirportMapDocument.Read(Map));
        AirportMapOpenOutcome.Unreadable failed = Assert.IsType<AirportMapOpenOutcome.Unreadable>(outcome);
        Assert.Equal($"Couldn't read {path}: {repeated.Message}", failed.Message);
    }

    [Fact]
    public async Task AFeatureThatRepeatsGeometry_IsUnreadable()
    {
        const string Map = """
            {"type": "FeatureCollection", "features": [
              {"type": "Feature", "properties": {"type": "parking", "name": "A1"},
               "geometry": {"type": "Point", "coordinates": [-122.22, 37.72]},
               "geometry": {"type": "Point", "coordinates": [-122.23, 37.73]}}
            ]}
            """;
        string path = _folder.WriteFile("repeated-geometry.geojson", Map);

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFileAsync(path, Ct);

        // The repeated key is rejected while the feature object is parsed.
        JsonException repeated = Assert.Throws<JsonException>(() => AirportMapDocument.Read(Map));
        AirportMapOpenOutcome.Unreadable failed = Assert.IsType<AirportMapOpenOutcome.Unreadable>(outcome);
        Assert.Equal($"Couldn't read {path}: {repeated.Message}", failed.Message);
    }

    [Fact]
    public async Task ARepeatedRootKey_IsUnreadable()
    {
        const string Map = """
            {"type": "FeatureCollection", "features": [], "features": [
              {"type": "Feature", "properties": {"type": "parking", "name": "A1"},
               "geometry": {"type": "Point", "coordinates": [-122.22, 37.72]}}
            ]}
            """;
        string path = _folder.WriteFile("repeated-root-key.geojson", Map);

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFileAsync(path, Ct);

        JsonException repeated = Assert.Throws<JsonException>(() => AirportMapDocument.Read(Map));
        AirportMapOpenOutcome.Unreadable failed = Assert.IsType<AirportMapOpenOutcome.Unreadable>(outcome);
        Assert.Equal($"Couldn't read {path}: {repeated.Message}", failed.Message);
    }

    [Theory]
    [InlineData("PAE.geojson")]
    [InlineData("BFI.geojson")]
    [InlineData("CMH.geojson")]
    public async Task OpensEveryCommittedVnasFixture_WithALeadingZeroOrAComment(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFileAsync(path, Ct);

        AirportMapOpenOutcome.Opened opened = Assert.IsType<AirportMapOpenOutcome.Opened>(outcome);
        Assert.Equal(path, opened.SourceLabel);
    }

    [Fact]
    public async Task AFolderWithOneMap_OpensIt()
    {
        string path = _folder.WriteFile(Path.Combine("one", "oak.geojson"), AirportEditorTestFolder.OakText);

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFolderAsync(Path.GetDirectoryName(path)!, Ct);

        AirportMapOpenOutcome.Opened opened = Assert.IsType<AirportMapOpenOutcome.Opened>(outcome);
        Assert.Equal(path, opened.SourceLabel);
        Assert.Equal(CachedCopyReason.None, opened.CachedCopy);
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, opened.Document.Features.Count);
    }

    [Fact]
    public async Task AMissingFolder_IsFolderMissing()
    {
        string folder = Path.Combine(_folder.Root, "no-such-folder");

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFolderAsync(folder, Ct);

        AirportMapOpenOutcome.FolderMissing failed = Assert.IsType<AirportMapOpenOutcome.FolderMissing>(outcome);
        Assert.Equal($"Folder not found: {folder}", failed.Message);
    }

    [Fact]
    public async Task AFilePathGivenAsAFolder_IsUnreadable_NamingTheFolder()
    {
        string notAFolder = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFolderAsync(notAFolder, Ct);

        AirportMapOpenOutcome.Unreadable failed = Assert.IsType<AirportMapOpenOutcome.Unreadable>(outcome);
        Assert.Equal(notAFolder, failed.Path);
        Assert.Equal("it is a file, not a folder", failed.ReaderMessage);
        Assert.Equal($"Couldn't read {notAFolder}: it is a file, not a folder", failed.Message);
    }

    [Fact]
    public async Task AFolderWithNoMap_IsFolderNoMap()
    {
        string notAMap = _folder.WriteFile(Path.Combine("none", "readme.txt"), "no map here");
        string folder = Path.GetDirectoryName(notAMap)!;

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFolderAsync(folder, Ct);

        AirportMapOpenOutcome.FolderNoMap failed = Assert.IsType<AirportMapOpenOutcome.FolderNoMap>(outcome);
        Assert.Equal($"No .geojson file in {folder}.", failed.Message);
    }

    [Fact]
    public async Task AFolderWithSeveralMaps_IsFolderSeveral_NamingThem()
    {
        _folder.WriteFile(Path.Combine("several", "oak.geojson"), AirportEditorTestFolder.OakText);
        string second = _folder.WriteFile(Path.Combine("several", "oak-copy.geojson"), AirportEditorTestFolder.OakText);
        string folder = Path.GetDirectoryName(second)!;

        AirportMapOpenOutcome outcome = await AirportMapSourceLoader.OpenFolderAsync(folder, Ct);

        AirportMapOpenOutcome.FolderSeveral failed = Assert.IsType<AirportMapOpenOutcome.FolderSeveral>(outcome);
        Assert.Equal($"{folder} holds several .geojson files: oak-copy.geojson, oak.geojson. Open one of them.", failed.Message);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>An HTTP handler that answers every request through a function, so the downloader never reaches vNAS.</summary>
internal sealed class StubMapHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
}

/// <summary>
/// A per-test folder under the test process's <c>YAAT_APPDATA_DIR</c>, holding the test's own airport cache and files;
/// it disposes the downloaders and HTTP clients it made.
/// </summary>
internal sealed class AirportEditorTestFolder : IDisposable
{
    /// <summary>The committed <c>oak.geojson</c>'s feature count.</summary>
    public const int OakFeatureCount = 222;

    /// <summary>The reader's own message for the text <c>this is not JSON</c>, taken from the reader.</summary>
    public static string NotJsonReaderMessage => Assert.ThrowsAny<JsonException>(() => AirportMapDocument.Read("this is not JSON")).Message;

    private static readonly Lazy<string> Oak = new(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson")));

    private readonly List<IDisposable> _owned = [];

    public AirportEditorTestFolder()
    {
        Root = Path.Combine(YaatPaths.AppDataRoot, "airport-editor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>The committed real KOAK map's text.</summary>
    public static string OakText => Oak.Value;

    public string Root { get; }

    public string CacheDir => Path.Combine(Root, "cache", "airports");

    /// <summary>A downloader over this folder's cache whose every request <paramref name="respond"/> answers.</summary>
    public AirportLayoutDownloader Downloader(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        GatedDownloader(request => Task.FromResult(respond(request)));

    /// <summary>A downloader over this folder's cache whose every request <paramref name="respond"/> answers, when it likes.</summary>
    public AirportLayoutDownloader GatedDownloader(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var http = new HttpClient(new StubMapHandler(respond));
        _owned.Add(http);
        var downloader = new AirportLayoutDownloader(http, CacheDir);
        _owned.Add(downloader);
        return downloader;
    }

    /// <summary>A loader whose cache opens would fail as offline; for file and folder opens.</summary>
    public AirportMapSourceLoader Loader() => new(Downloader(_ => throw new HttpRequestException("unused")));

    /// <summary>Writes the committed KOAK map to the downloader's cache path for OAK.</summary>
    public static void SeedCache(AirportLayoutDownloader downloader)
    {
        Directory.CreateDirectory(downloader.CacheDir);
        File.WriteAllText(downloader.GetCachePath("OAK"), OakText);
    }

    public string WriteFile(string relativePath, string text)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
        {
            owned.Dispose();
        }

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
