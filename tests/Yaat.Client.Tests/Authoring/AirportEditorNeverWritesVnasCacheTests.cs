using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Airport Editor never writes <c>cache/airports/{FAA}.geojson</c>: the downloader's own fetch is the one write, and
/// selecting, opening a file or a folder (the cache's own file and folder included) and closing leave the cache file's
/// bytes and last-write time as that fetch left them.
/// </summary>
public sealed class AirportEditorNeverWritesVnasCacheTests : IDisposable
{
    private readonly AirportEditorTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task OpeningSelectingAndClosing_LeaveTheCacheFileAsTheDownloadersFetchLeftIt()
    {
        AirportLayoutDownloader downloader = _folder.Downloader(_ => StubMapHandler.Ok(AirportEditorTestFolder.OakText));
        var prompts = new FakeAirportEditorPrompts { AirportId = "KOAK" };
        var vm = new AirportEditorViewModel(new AirportMapSourceLoader(downloader), prompts);
        string cachePath = downloader.GetCachePath("KOAK");

        await vm.OpenFromCacheCommand.ExecuteAsync(null);
        Assert.Equal("OAK (vNAS cache)", vm.SourceLabel);
        byte[] bytes = await File.ReadAllBytesAsync(cachePath, TestContext.Current.CancellationToken);
        DateTime lastWrite = File.GetLastWriteTimeUtc(cachePath);
        string[] cacheFiles = Directory.GetFileSystemEntries(downloader.CacheDir);

        vm.SelectedFeatureIndex = 0;
        Assert.Same(vm.Document!.Features[0], vm.SelectedFeature);

        string elsewhere = _folder.WriteFile(Path.Combine("elsewhere", "oak.geojson"), AirportEditorTestFolder.OakText);
        prompts.File = elsewhere;
        await vm.OpenFileCommand.ExecuteAsync(null);
        Assert.Equal(elsewhere, vm.SourceLabel);

        prompts.File = cachePath;
        await vm.OpenFileCommand.ExecuteAsync(null);
        Assert.Equal(cachePath, vm.SourceLabel);
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, vm.Document!.Features.Count);

        prompts.Folder = downloader.CacheDir;
        await vm.OpenFolderCommand.ExecuteAsync(null);
        Assert.Equal(cachePath, vm.SourceLabel);

        vm.Dispose();

        Assert.Equal(bytes, await File.ReadAllBytesAsync(cachePath, TestContext.Current.CancellationToken));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(cachePath));
        Assert.Equal(cacheFiles, Directory.GetFileSystemEntries(downloader.CacheDir));
    }
}
