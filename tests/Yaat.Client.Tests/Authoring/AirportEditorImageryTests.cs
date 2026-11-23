using System.Net;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Airport Editor's airport id for the tower-cab image: a vNAS-cache open keeps its FAA code; a file open takes its
/// stem when it is 3 or 4 letters or digits naming an airport within 10 nm of the map's centre, else none. The view model
/// asks its imagery source for (ARTCC, airport) on each open, exposes the image that arrives, and drops a request a newer
/// open replaced. The vNAS answer is Unaskable, Pending, Image or None, and USGS tiles are active only for Unaskable or
/// None with imagery on; the download's progress is the status text until the answer comes; one in-flight vNAS request
/// per airport is shared by every waiter.
/// </summary>
public sealed class AirportEditorImageryTests : IDisposable
{
    private static readonly LatLon Koak = new(37.7213, -122.2208);

    private readonly AirportEditorTestFolder _folder = new();

    public AirportEditorImageryTests() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("oak.geojson", "OAK")]
    [InlineData("KOAK.geojson", "OAK")]
    [InlineData("lax.geojson", null)]
    [InlineData("oakxx.geojson", null)]
    [InlineData("oa.geojson", null)]
    [InlineData("o-k.geojson", null)]
    public void AFileOpen_TakesTheStem_OnlyForANearbyAirport(string file, string? expected) =>
        Assert.Equal(expected, AirportEditorAirportId.Infer(null, Path.Combine("maps", file), Koak));

    [Fact]
    public void ACacheOpen_KeepsItsFaaCode_AndAMapWithNoPositionsHasNoStemAirport()
    {
        Assert.Equal("OAK", AirportEditorAirportId.Infer("OAK", "OAK (vNAS cache)", Koak));
        Assert.Null(AirportEditorAirportId.Infer(null, "oak.geojson", null));
    }

    [Fact]
    public async Task ACacheOpen_KeepsOak_AndAsksTheSourceForZoaOak()
    {
        var source = new FakeImagerySource();
        AirportLayoutDownloader downloader = _folder.GatedDownloader(_ => Task.FromResult(StubMapHandler.Ok(AirportEditorTestFolder.OakText)));
        using var vm = new AirportEditorViewModel(new AirportMapSourceLoader(downloader), new FakeAirportEditorPrompts(), source, null);

        await vm.OpenAirportAsync("KOAK");

        Assert.Equal("OAK", vm.AirportId);
        Assert.Equal(("ZOA", "OAK"), Assert.Single(source.Requests).Key);
    }

    [Fact]
    public async Task AFileOpenOfOak_AsksTheSourceForZoaOak_AndExposesTheImage()
    {
        var source = new FakeImagerySource();
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, "ZLA");

        await vm.OpenFilePathAsync(oak);

        Assert.Equal("OAK", vm.AirportId);
        FakeImagerySource.Request request = Assert.Single(source.Requests);
        Assert.Equal(("ZOA", "OAK"), request.Key);
        Assert.Null(vm.Image);

        TowerCabImage image = NewImage();
        request.Answer.SetResult(image);
        await vm.ImageryLoad;

        Assert.Same(image, vm.Image);
    }

    [Fact]
    public async Task ASecondOpen_CancelsTheFirstRequest_AndDropsItsLateImage()
    {
        var source = new FakeImagerySource();
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);
        await vm.OpenFilePathAsync(oak);
        Task firstLoad = vm.ImageryLoad;

        await vm.OpenFilePathAsync(oak);
        Assert.Equal(2, source.Requests.Count);
        Assert.True(source.Requests[0].Token.IsCancellationRequested);
        Assert.False(source.Requests[1].Token.IsCancellationRequested);

        source.Requests[0].Answer.SetResult(NewImage());
        await firstLoad;
        Assert.Null(vm.Image);

        TowerCabImage second = NewImage();
        source.Requests[1].Answer.SetResult(second);
        await vm.ImageryLoad;
        Assert.Same(second, vm.Image);
    }

    [Fact]
    public async Task TurningImageryOff_CancelsTheRequestInFlight_AndDropsItsLateImage()
    {
        var source = new FakeImagerySource();
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);
        await vm.OpenFilePathAsync(oak);
        FakeImagerySource.Request request = Assert.Single(source.Requests);
        Task load = vm.ImageryLoad;

        vm.ShowImagery = false;

        Assert.True(request.Token.IsCancellationRequested);
        request.Answer.SetResult(NewImage());
        await load;
        Assert.Null(vm.Image);
        Assert.Single(source.Requests);
    }

    [Fact]
    public async Task WithImageryOff_NothingIsAsked_UntilItIsTurnedOn()
    {
        var source = new FakeImagerySource();
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null) { ShowImagery = false };

        await vm.OpenFilePathAsync(oak);
        Assert.Empty(source.Requests);

        vm.ShowImagery = true;

        Assert.Equal(("ZOA", "OAK"), Assert.Single(source.Requests).Key);
    }

    [Fact]
    public async Task AFileOfNoNearbyAirport_AsksForNothing()
    {
        var source = new FakeImagerySource();
        string lax = _folder.WriteFile("lax.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, "ZOA");

        await vm.OpenFilePathAsync(lax);

        Assert.Null(vm.AirportId);
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task WhilePending_NoTiles_AndANoneAnswer_TurnsTilesOn()
    {
        var source = new FakeImagerySource();
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);

        await vm.OpenFilePathAsync(oak);
        Assert.Equal(AirportEditorVnasAnswer.Pending, vm.VnasAnswer);
        Assert.False(vm.TilesActive);

        Assert.Single(source.Requests).Answer.SetResult(null);
        await vm.ImageryLoad;

        Assert.Equal(AirportEditorVnasAnswer.None, vm.VnasAnswer);
        Assert.True(vm.TilesActive);
    }

    [Fact]
    public async Task AFileOpenWithNoAirport_IsUnaskable_AndHasTiles()
    {
        var source = new FakeImagerySource();
        string lax = _folder.WriteFile("lax.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, "ZOA");

        await vm.OpenFilePathAsync(lax);

        Assert.Equal(AirportEditorVnasAnswer.Unaskable, vm.VnasAnswer);
        Assert.True(vm.TilesActive);
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task AnImageAnswer_HasNoTiles_AndAFailedRequestCountsAsNone()
    {
        var source = new FakeImagerySource();
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);
        await vm.OpenFilePathAsync(oak);

        source.Requests[0].Answer.SetResult(NewImage());
        await vm.ImageryLoad;
        Assert.Equal(AirportEditorVnasAnswer.Image, vm.VnasAnswer);
        Assert.False(vm.TilesActive);

        await vm.OpenFilePathAsync(oak);
        source.Requests[1].Answer.SetException(new IOException("disk"));
        await vm.ImageryLoad;
        Assert.Equal(AirportEditorVnasAnswer.None, vm.VnasAnswer);
        Assert.True(vm.TilesActive);
    }

    [Fact]
    public async Task ALateImage_ForTheNextMap_ReplacesTheTiles()
    {
        var source = new FakeImagerySource();
        string lax = _folder.WriteFile("lax.geojson", AirportEditorTestFolder.OakText);
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);
        await vm.OpenFilePathAsync(lax);
        Assert.True(vm.TilesActive);

        await vm.OpenFilePathAsync(oak);
        Assert.False(vm.TilesActive);
        TowerCabImage image = NewImage();
        Assert.Single(source.Requests).Answer.SetResult(image);
        await vm.ImageryLoad;

        Assert.Same(image, vm.Image);
        Assert.Equal(AirportEditorVnasAnswer.Image, vm.VnasAnswer);
        Assert.False(vm.TilesActive);
    }

    [Fact]
    public async Task TurningImageryBackOn_AsksAgain_OnlyWhenTheEarlierRequestWasCancelled()
    {
        var source = new FakeImagerySource();
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);
        await vm.OpenFilePathAsync(oak);

        vm.ShowImagery = false;
        Assert.Equal(AirportEditorVnasAnswer.Pending, vm.VnasAnswer);
        vm.ShowImagery = true;
        Assert.Equal(2, source.Requests.Count);

        source.Requests[1].Answer.SetResult(null);
        await vm.ImageryLoad;
        vm.ShowImagery = false;
        Assert.False(vm.TilesActive);
        vm.ShowImagery = true;

        Assert.Equal(2, source.Requests.Count);
        Assert.True(vm.TilesActive);
    }

    [Fact]
    public async Task DownloadProgress_ReachesTheStatusText_AndClearsWhenTheAnswerComes()
    {
        var source = new FakeImagerySource();
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null)
        {
            ProgressPost = action => action(),
        };
        await vm.OpenFilePathAsync(oak);
        FakeImagerySource.Request request = Assert.Single(source.Requests);

        Assert.NotNull(request.Progress);
        IProgress<TowerCabDownloadProgress> progress = request.Progress;
        progress.Report(new TowerCabDownloadProgress(50, 200));
        Assert.Equal("Downloading vNAS imagery: 25%", vm.ImageryProgressText);
        progress.Report(new TowerCabDownloadProgress((3 * 1024 * 1024) + 5, null));
        Assert.Equal("Downloading vNAS imagery: 3 MB", vm.ImageryProgressText);

        request.Answer.SetResult(null);
        await vm.ImageryLoad;
        Assert.Null(vm.ImageryProgressText);
    }

    [Fact]
    public async Task ASecondRequestForTheSameAirport_SharesTheFirst_AndEveryWaiterGetsTheProgress()
    {
        int calls = 0;
        var fetched = new TaskCompletionSource<IProgress<TowerCabDownloadProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<TowerCabImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TowerCabAirportImagerySource(
            () => "https://example.invalid/towercab",
            (_, _, _, progress) =>
            {
                Interlocked.Increment(ref calls);
                fetched.TrySetResult(progress);
                return answer.Task;
            }
        );
        var first = new ListProgress();
        var second = new ListProgress();
        using var cancelFirst = new CancellationTokenSource();
        CancellationToken ct = TestContext.Current.CancellationToken;

        Task<TowerCabImage?> a = source.GetAsync("ZOA", "OAK", first, cancelFirst.Token);
        Task<TowerCabImage?> b = source.GetAsync("ZOA", "OAK", second, ct);
        IProgress<TowerCabDownloadProgress> shared = await fetched.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        shared.Report(new TowerCabDownloadProgress(10, 100));
        await cancelFirst.CancelAsync();
        TowerCabImage image = NewImage();
        answer.SetResult(image);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.Same(image, await b);
        Assert.Equal(1, calls);
        Assert.Equal([new TowerCabDownloadProgress(10, 100)], first.Reports);
        Assert.Equal([new TowerCabDownloadProgress(10, 100)], second.Reports);
    }

    [Fact]
    public async Task ACacheOpenOutsideEveryArtcc_HasAnAirportButIsUnaskable_AndHasTiles()
    {
        const string GulfOfGuinea = """
            {"type":"FeatureCollection","features":[{"type":"Feature",
            "geometry":{"type":"Point","coordinates":[0.5,0.5]},"properties":{"type":"parking","name":"P1"}}]}
            """;
        var source = new FakeImagerySource();
        AirportLayoutDownloader downloader = _folder.GatedDownloader(_ => Task.FromResult(StubMapHandler.Ok(GulfOfGuinea)));
        using var vm = new AirportEditorViewModel(new AirportMapSourceLoader(downloader), new FakeAirportEditorPrompts(), source, null);

        await vm.OpenAirportAsync("KOAK");

        Assert.Equal("OAK", vm.AirportId);
        Assert.Equal(AirportEditorVnasAnswer.Unaskable, vm.VnasAnswer);
        Assert.True(vm.TilesActive);
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task TwoViewModelsOnTheAppsSource_MakeOneFetchForOneAirport()
    {
        int calls = 0;
        var answer = new TaskCompletionSource<TowerCabImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TowerCabAirportImagerySource(
            () => "https://example.invalid/towercab",
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                fetched.TrySetResult();
                return answer.Task;
            }
        );
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var first = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);
        await first.OpenFilePathAsync(oak);
        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The first window closes mid-download and a second one opens the same airport.
        first.Dispose();
        using var second = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);
        await second.OpenFilePathAsync(oak);
        TowerCabImage image = NewImage();
        answer.SetResult(image);
        await second.ImageryLoad;

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Same(image, second.Image);
    }

    // The body stalls after the headers; the service's own timeout (on its clock) ends the download and vNAS answers None.
    [Fact]
    public async Task AStalledImageBody_TimesOut_AndTheAnswerIsNone()
    {
        var time = new FakeTimeProvider();
        var body = new StalledStream();
        var handler = new StubMapHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        string cacheDir = Path.Combine(Path.GetTempPath(), "yaat-towercab-" + Guid.NewGuid().ToString("N"));
        using var service = new TowerCabImageService(handler, time, cacheDir);
        var source = new TowerCabAirportImagerySource(
            () => "https://example.invalid/towercab",
            (baseUrl, artcc, airport, progress) => service.GetImageAsync(baseUrl, artcc, airport, highRes: true, progress)
        );
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), source, null);
        try
        {
            await vm.OpenFilePathAsync(oak);
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(AirportEditorVnasAnswer.Pending, vm.VnasAnswer);

            time.Advance(TimeSpan.FromSeconds(61));
            await vm.ImageryLoad.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(AirportEditorVnasAnswer.None, vm.VnasAnswer);
            Assert.Null(vm.Image);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
            }
        }
    }

    private static TowerCabImage NewImage()
    {
        using var bitmap = new SKBitmap(4, 4);
        return new TowerCabImage(SKImage.FromBitmap(bitmap), 37.70, -122.25, 37.74, -122.19);
    }

    // Records each request and answers it when the test says.
    private sealed class FakeImagerySource : IAirportImagerySource
    {
        public List<Request> Requests { get; } = [];

        public Task<TowerCabImage?> GetAsync(string artccId, string airportId, IProgress<TowerCabDownloadProgress>? progress, CancellationToken ct)
        {
            var request = new Request(
                (artccId, airportId),
                new TaskCompletionSource<TowerCabImage?>(TaskCreationOptions.RunContinuationsAsynchronously),
                ct
            )
            {
                Progress = progress,
            };
            Requests.Add(request);
            return request.Answer.Task;
        }

        public sealed record Request((string Artcc, string Airport) Key, TaskCompletionSource<TowerCabImage?> Answer, CancellationToken Token)
        {
            public IProgress<TowerCabDownloadProgress>? Progress { get; init; }
        }
    }

    // A response body that never yields a byte: each read waits until its token is cancelled.
    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Keeps every report, synchronously.
    private sealed class ListProgress : IProgress<TowerCabDownloadProgress>
    {
        private readonly Lock _lock = new();
        private readonly List<TowerCabDownloadProgress> _reports = [];

        public List<TowerCabDownloadProgress> Reports
        {
            get
            {
                lock (_lock)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(TowerCabDownloadProgress value)
        {
            lock (_lock)
            {
                _reports.Add(value);
            }
        }
    }
}
