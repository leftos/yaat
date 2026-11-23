using System.Net;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// YAAT sends nothing to vNAS: Export JSON, Export clean JSON and Export all unexported write only the picked files and
/// the exported copies' sidecars. The mirror of the server scenarios, the rest of the store and the vNAS cache folder
/// keep their bytes and last-write times, and no export downloads or sends an HTTP request.
/// </summary>
public sealed class ExportNeverWritesVnasCacheTests : IDisposable
{
    private const string Zoa = ScenarioEditorFixture.Zoa;

    private readonly ScenarioEditorFixture _fx = new();
    private readonly string _work = Path.Combine(Path.GetTempPath(), "yaat-export-guard-tests", Guid.NewGuid().ToString("N"));
    private ScenarioBrowserViewModel? _browser;

    public void Dispose()
    {
        _fx.Dispose();
        _browser?.Dispose();
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    [Fact]
    public async Task Exports_WriteOnlyThePickedFilesAndTheSidecars_NoDownloadNoRequest()
    {
        _fx.Store.ApplyServerBody(Zoa, "S1", ScenarioEditorFixture.ScenarioJson("S1", "Sierra"));
        _fx.Store.CreateWorkingCopy(Zoa, "S1");
        _fx.Store.SaveWorkingCopy(Zoa, "S1", ScenarioEditorFixture.ScenarioJson("S1", "Sierra edited"));
        _fx.AddCopy("L1", "Lima");
        _fx.AddCopy("L2", "Mike");
        int downloads = 0;
        var handler = new CountingHandler();
        ScenarioBrowserServices services = _fx.Services() with
        {
            Download = (artcc, _, _) =>
            {
                Interlocked.Increment(ref downloads);
                return Task.FromResult(new DownloadReport(artcc, null, [], [], [], [], [], [], []));
            },
            Configs = new ArtccConfigSource(new HttpClient(handler), Path.Combine(_work, "artcc"), () => DateTime.UtcNow),
        };
        _browser = new ScenarioBrowserViewModel(services);
        _fx.NewEditor(_browser, services);
        await _fx.Ui.Run(_browser.InitializeAsync());
        await _fx.Ui.Run(_browser.ResyncTask);
        int downloadsBefore = downloads;
        string storeRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_fx.Store.GetWorkingCopyPath(Zoa, "L1"))!)!)!;
        string cache = Path.Combine(YaatPaths.AppDataRoot, "cache");
        Dictionary<string, (byte[] Bytes, DateTime Written)> storeBefore = Snapshot(storeRoot);
        Dictionary<string, (byte[] Bytes, DateTime Written)> cacheBefore = Snapshot(cache);
        string exports = Path.Combine(_work, "exports");
        Directory.CreateDirectory(exports);

        Select("S1");
        _fx.Prompts.ExportPath = Path.Combine(exports, "sierra.json");
        await _fx.Ui.Run(_browser.ExportJsonCommand.ExecuteAsync(null));
        Select("L1");
        _fx.Prompts.ExportPath = Path.Combine(exports, "lima.json");
        await _fx.Ui.Run(_browser.ExportCleanJsonCommand.ExecuteAsync(null));
        string folder = Path.Combine(exports, "all");
        Directory.CreateDirectory(folder);
        _fx.Prompts.ExportFolder = folder;
        await _fx.Ui.Run(_browser.ExportAllUnexportedCommand.ExecuteAsync(null));

        Assert.Equal(["lima.json", "sierra.json"], Directory.GetFiles(exports).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["Mike-L2.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
        string[] exported = ["S1", "L1", "L2"];
        string[] sidecars = [.. exported.Select(id => Path.ChangeExtension(_fx.Store.GetWorkingCopyPath(Zoa, id), ".meta.json"))];
        Dictionary<string, (byte[] Bytes, DateTime Written)> storeAfter = Snapshot(storeRoot);
        Assert.All(sidecars, s => Assert.True(storeAfter.ContainsKey(s), s + " was not written"));
        Assert.Equal(Unchanged(storeBefore, sidecars), Unchanged(storeAfter, sidecars));
        Assert.Equal(cacheBefore.Keys.Order(StringComparer.Ordinal), Snapshot(cache).Keys.Order(StringComparer.Ordinal));
        Assert.All(cacheBefore, e => Assert.Equal(e.Value.Written, File.GetLastWriteTimeUtc(e.Key)));
        Assert.Equal(downloadsBefore, downloads);
        Assert.Equal(0, handler.Requests);
        Assert.Empty(_browser.Notices);
    }

    private void Select(string id)
    {
        _browser!.SelectedTab = ScenarioBrowserTab.Local;
        _browser.SelectedLocalRow = _browser.LocalRows.Single(r => r.Id == id);
    }

    // Every file but the sidecars, by path, with its bytes as text and its last-write time.
    private static List<string> Unchanged(Dictionary<string, (byte[] Bytes, DateTime Written)> files, string[] sidecars) =>
        [
            .. files
                .Where(f => !sidecars.Contains(f.Key, StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => $"{f.Key}|{Convert.ToBase64String(f.Value.Bytes)}|{f.Value.Written:O}"),
        ];

    private static Dictionary<string, (byte[] Bytes, DateTime Written)> Snapshot(string root) =>
        Directory.Exists(root)
            ? Directory
                .GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, (byte[] Bytes, DateTime Written)>(StringComparer.OrdinalIgnoreCase);

    // Counts every request and fails it, as the network being down.
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
