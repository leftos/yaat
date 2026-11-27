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

    [Fact]
    public async Task DeleteFeature_ThenUndo_RestoresTheDocument_AndRedoDeletesAgain()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        Assert.False(vm.IsDirty);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
        vm.SelectedFeatureIndex = 3;

        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal(AirportEditorTestFolder.OakFeatureCount - 1, vm.Document!.Features.Count);
        Assert.True(vm.IsDirty);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Undo Delete feature", vm.UndoDescription);
        Assert.Null(vm.SelectedFeatureIndex);

        vm.UndoCommand.Execute(null);

        Assert.Same(opened, vm.Document);
        Assert.False(vm.IsDirty);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.True(vm.RedoCommand.CanExecute(null));
        Assert.Null(vm.UndoDescription);
        Assert.Equal("Redo Delete feature", vm.RedoDescription);

        vm.RedoCommand.Execute(null);

        Assert.Equal(AirportEditorTestFolder.OakFeatureCount - 1, vm.Document!.Features.Count);
        Assert.DoesNotContain(opened.Features[3], vm.Document!.Features);
        Assert.True(vm.IsDirty);
        Assert.False(vm.RedoCommand.CanExecute(null));
        Assert.Null(vm.RedoDescription);
    }

    [Fact]
    public async Task DeleteVertex_OnALineAtItsMinimum_IsDisabled()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
        int index = FirstTaxiway(vm.Document!);
        while (vm.Document!.Features[index].Coordinates.Length > 2)
        {
            vm.ApplyEdit("Delete vertex", d => MapEdits.DeleteVertex(d, index, 0));
        }

        vm.SelectedFeatureIndex = index;
        vm.SelectedVertexIndex = 0;

        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
        vm.DeleteSelectedCommand.Execute(null);
        Assert.Equal(2, vm.Document!.Features[index].Coordinates.Length);
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, vm.Document!.Features.Count);

        vm.SelectedVertexIndex = null;

        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteVertex_RemovesOnlyThatVertex_AndKeepsTheFeatureSelected()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        int index = FirstTaxiway(opened);
        MapFeature taxiway = opened.Features[index];
        Assert.True(taxiway.Coordinates.Length > 2);
        vm.SelectedFeatureIndex = index;
        vm.SelectedVertexIndex = 1;
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal(taxiway.Coordinates.RemoveAt(1), vm.Document!.Features[index].Coordinates);
        Assert.Equal(opened.Features.Count, vm.Document!.Features.Count);
        for (int i = 0; i < opened.Features.Count; i++)
        {
            if (i != index)
            {
                Assert.Same(opened.Features[i], vm.Document!.Features[i]);
            }
        }

        Assert.Equal(index, vm.SelectedFeatureIndex);
        Assert.Null(vm.SelectedVertexIndex);
        Assert.Same(vm.Document!.Features[index], vm.SelectedFeature);
        Assert.Equal("Undo Delete vertex", vm.UndoDescription);
    }

    [Fact]
    public async Task Redo_OfADelete_ClearsTheSelectionOfTheDeletedFeature()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        vm.SelectedFeatureIndex = 3;
        vm.DeleteSelectedCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.SelectedFeatureIndex = 3;

        vm.RedoCommand.Execute(null);

        Assert.Null(vm.SelectedFeatureIndex);
        Assert.Null(vm.SelectedFeature);
        Assert.Empty(vm.SelectionRows);

        vm.UndoCommand.Execute(null);
        vm.SelectedFeatureIndex = 5;

        vm.RedoCommand.Execute(null);

        Assert.Equal(4, vm.SelectedFeatureIndex);
        Assert.Same(opened.Features[5], vm.SelectedFeature);
    }

    [Fact]
    public async Task Open_ClearsTheUndoStack()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(oak);
        vm.SelectedFeatureIndex = 3;
        vm.DeleteSelectedCommand.Execute(null);
        vm.SelectedFeatureIndex = 3;
        vm.DeleteSelectedCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.True(vm.RedoCommand.CanExecute(null));

        await vm.OpenFilePathAsync(oak);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
        Assert.Null(vm.UndoDescription);
        Assert.Null(vm.RedoDescription);
        Assert.False(vm.IsDirty);
        Assert.Equal(AirportEditorTestFolder.OakFeatureCount, vm.Document!.Features.Count);
    }

    [Fact]
    public async Task ApplyEdit_ReturningTheSameDocument_RecordsNothing()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        bool documentChanged = false;
        vm.PropertyChanged += (_, e) => documentChanged |= e.PropertyName == nameof(AirportEditorViewModel.Document);

        vm.ApplyEdit("Nothing", d => d);

        Assert.Same(opened, vm.Document);
        Assert.False(documentChanged);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Null(vm.UndoDescription);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Redo_OfAVertexDelete_ClearsTheVertexSelection_AndKeepsTheFeatureSelected()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        int index = FirstTaxiway(vm.Document!);
        vm.SelectedFeatureIndex = index;
        vm.SelectedVertexIndex = 1;
        vm.DeleteSelectedCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.Equal(index, vm.SelectedFeatureIndex);
        vm.SelectedVertexIndex = 1;

        vm.RedoCommand.Execute(null);

        Assert.Equal(index, vm.SelectedFeatureIndex);
        Assert.Null(vm.SelectedVertexIndex);
        Assert.Same(vm.Document!.Features[index], vm.SelectedFeature);
    }

    private async Task<AirportEditorViewModel> OpenOakAsync()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(oak);
        return vm;
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
