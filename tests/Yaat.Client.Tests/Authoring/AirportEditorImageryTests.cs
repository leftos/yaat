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
/// open replaced.
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

    private static TowerCabImage NewImage()
    {
        using var bitmap = new SKBitmap(4, 4);
        return new TowerCabImage(SKImage.FromBitmap(bitmap), 37.70, -122.25, 37.74, -122.19);
    }

    // Records each request and answers it when the test says.
    private sealed class FakeImagerySource : IAirportImagerySource
    {
        public List<Request> Requests { get; } = [];

        public Task<TowerCabImage?> GetAsync(string artccId, string airportId, CancellationToken ct)
        {
            var request = new Request(
                (artccId, airportId),
                new TaskCompletionSource<TowerCabImage?>(TaskCreationOptions.RunContinuationsAsynchronously),
                ct
            );
            Requests.Add(request);
            return request.Answer.Task;
        }

        public sealed record Request((string Artcc, string Airport) Key, TaskCompletionSource<TowerCabImage?> Answer, CancellationToken Token);
    }
}
