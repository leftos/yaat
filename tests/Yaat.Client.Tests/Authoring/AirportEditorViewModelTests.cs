using System.Globalization;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Airport Editor's view model keeps the open map when an open fails, shows why, and selects a raw feature by index.
/// </summary>
public sealed class AirportEditorViewModelTests : IDisposable
{
    private readonly AirportEditorTestFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task AFailedOpen_KeepsThePreviousDocument_AndShowsItsMessage()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        string broken = _folder.WriteFile("broken.geojson", "this is not JSON");
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(oak);
        AirportMapDocument? document = vm.Document;
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, document?.Features.Count);
        Assert.Equal($"Opened {oak}: {AirportEditorTestFolder.OakFeatureCount} features.", vm.StatusText);

        await vm.OpenFilePathAsync(broken);

        Assert.Same(document, vm.Document);
        Assert.Equal(oak, vm.SourceLabel);
        Assert.Equal($"Couldn't read {broken}: {AirportEditorTestFolder.NotJsonReaderMessage}", vm.StatusText);

        string missing = Path.Combine(_folder.Root, "missing.geojson");
        await vm.OpenFilePathAsync(missing);

        Assert.Same(document, vm.Document);
        Assert.Equal($"File not found: {missing}", vm.StatusText);
    }

    [Fact]
    public async Task Selecting_AFeature_ExposesIt_AndOpeningAnotherMapClearsTheSelection()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        string other = _folder.WriteFile(Path.Combine("other", "oak.geojson"), AirportEditorTestFolder.OakText);
        var prompts = new FakeAirportEditorPrompts { File = oak, Folder = Path.GetDirectoryName(other) };
        using var vm = new AirportEditorViewModel(_folder.Loader(), prompts, NoAirportImagery.Instance, null);
        await vm.OpenFileCommand.ExecuteAsync(null);
        Assert.Null(vm.SelectedFeature);

        vm.SelectedFeatureIndex = 3;

        Assert.Same(vm.Document!.Features[3], vm.SelectedFeature);

        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(other, vm.SourceLabel);
        Assert.Null(vm.SelectedFeatureIndex);
        Assert.Null(vm.SelectedFeature);
    }

    [Fact]
    public async Task SelectionRows_AreEmpty_WithNothingSelected()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        Assert.Empty(vm.SelectionRows);

        await vm.OpenFilePathAsync(oak);

        Assert.Empty(vm.SelectionRows);
    }

    [Fact]
    public async Task SelectionRows_ForATaxiway_ListItsFeatureTypeGeometryAndPositions_ThenItsPropertiesInKeyOrder()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(oak);
        int index = FirstTaxiway(vm.Document!);
        MapFeature taxiway = vm.Document!.Features[index];

        vm.SelectedFeatureIndex = index;

        AirportEditorPropertyRow[] expected =
        [
            new("Feature", $"{index + 1} of {AirportEditorTestFolder.OakFeatureCount}"),
            new("Type", "taxiway"),
            new("Geometry", "LineString"),
            new("Positions", taxiway.Coordinates.Length.ToString(CultureInfo.InvariantCulture)),
            new("circular", "false"),
            new("name", "W"),
            new("type", "taxiway"),
        ];
        Assert.Equal(expected, vm.SelectionRows);
    }

    [Fact]
    public async Task SelectionRows_ForASelectedVertex_AddItsNumberAndPosition_AfterThePositionsCount()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(oak);
        int index = FirstTaxiway(vm.Document!);
        MapFeature taxiway = vm.Document!.Features[index];
        MapPosition vertex = taxiway.Coordinates[1];

        vm.SelectedFeatureIndex = index;
        vm.SelectedVertexIndex = 1;

        Assert.Equal("Positions", vm.SelectionRows[3].Name);
        Assert.Equal(new AirportEditorPropertyRow("Vertex", $"2 of {taxiway.Coordinates.Length}"), vm.SelectionRows[4]);
        string position = string.Create(CultureInfo.InvariantCulture, $"{vertex.Lat:F7}, {vertex.Lon:F7}");
        Assert.Equal(new AirportEditorPropertyRow("Position", position), vm.SelectionRows[5]);
        Assert.Equal("circular", vm.SelectionRows[6].Name);
    }

    [Fact]
    public async Task TheSelectedVertex_IsClearedByAnotherFeature_AndByANewDocument_ButNotByTheSameFeatureAgain()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        string other = _folder.WriteFile(Path.Combine("other", "oak.geojson"), AirportEditorTestFolder.OakText);
        var prompts = new FakeAirportEditorPrompts { File = oak, Folder = Path.GetDirectoryName(other) };
        using var vm = new AirportEditorViewModel(_folder.Loader(), prompts, NoAirportImagery.Instance, null);
        await vm.OpenFileCommand.ExecuteAsync(null);
        int index = FirstTaxiway(vm.Document!);
        vm.SelectedFeatureIndex = index;
        vm.SelectedVertexIndex = 1;

        vm.SelectedFeatureIndex = index;
        Assert.Equal(1, vm.SelectedVertexIndex);

        vm.SelectedFeatureIndex = index + 1;
        Assert.Null(vm.SelectedVertexIndex);

        vm.SelectedFeatureIndex = index;
        vm.SelectedVertexIndex = 1;
        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedFeatureIndex);
        Assert.Null(vm.SelectedVertexIndex);
        Assert.Empty(vm.SelectionRows);
    }

    [Fact]
    public async Task ASecondOpen_CancelsTheFirst_AndDropsItsOutcome()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AirportLayoutDownloader downloader = _folder.GatedDownloader(_ =>
        {
            reached.TrySetResult();
            return gate.Task;
        });
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(
            new AirportMapSourceLoader(downloader),
            new FakeAirportEditorPrompts(),
            NoAirportImagery.Instance,
            null
        );

        Task first = vm.OpenAirportAsync("KOAK");
        await reached.Task;
        await vm.OpenFilePathAsync(oak);
        AirportMapDocument? second = vm.Document;
        gate.SetResult(StubMapHandler.Ok(AirportEditorTestFolder.OakText));
        await first;

        Assert.Same(second, vm.Document);
        Assert.Equal(oak, vm.SourceLabel);
        Assert.Equal($"Opened {oak}: {AirportEditorTestFolder.OakFeatureCount} features.", vm.StatusText);
    }

    [Fact]
    public async Task Dispose_DropsAnOpenInFlight()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AirportLayoutDownloader downloader = _folder.GatedDownloader(_ =>
        {
            reached.TrySetResult();
            return gate.Task;
        });
        var vm = new AirportEditorViewModel(new AirportMapSourceLoader(downloader), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);

        Task open = vm.OpenAirportAsync("KOAK");
        await reached.Task;
        vm.Dispose();
        gate.SetResult(StubMapHandler.Ok(AirportEditorTestFolder.OakText));
        await open;

        Assert.Null(vm.Document);
        Assert.Equal("", vm.SourceLabel);
        Assert.Equal("", vm.StatusText);
    }

    private static int FirstTaxiway(AirportMapDocument document) =>
        document.Features.Select((f, i) => (f, i)).First(x => x.f.FeatureType == "taxiway").i;
}

/// <summary>Answers the Airport Editor's prompts with fixed values; null is a cancel.</summary>
internal sealed class FakeAirportEditorPrompts : IAirportEditorPrompts
{
    public string? AirportId { get; set; }

    public string? File { get; set; }

    public string? Folder { get; set; }

    public Task<string?> AskAirportIdAsync() => Task.FromResult(AirportId);

    public Task<string?> PickMapFileAsync() => Task.FromResult(File);

    public Task<string?> PickMapFolderAsync() => Task.FromResult(Folder);
}
