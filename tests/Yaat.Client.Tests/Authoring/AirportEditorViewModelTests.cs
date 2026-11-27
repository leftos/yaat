using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
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

    private const string EmptyMap = """{"type":"FeatureCollection","features":[]}""";

    private static readonly ImmutableArray<MapPosition> Line = [new(-122.2, 37.7), new(-122.21, 37.71)];

    private static readonly ImmutableArray<MapPosition> Point = [new(-122.2, 37.7)];

    [Fact]
    public async Task TheAutoName_OnAnEmptyMap_IsEachTypesPrefixAndOne()
    {
        string path = _folder.WriteFile("empty.geojson", EmptyMap);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(path);

        vm.AddFeature("taxiway", Line);
        vm.AddFeature("runway", Line);
        vm.AddFeature("parking", Point);
        vm.AddFeature("spot", Point);
        vm.AddFeature("helipad", Point);

        Assert.Equal(["TWY1", "RWY1", "PARK1", "SPOT1", "HELI1"], vm.Document!.Features.Select(FeatureName));
    }

    [Fact]
    public async Task TheAutoName_TakesTheLowestUnusedNumber_IgnoringCase_AndANonNumericSuffixDoesNotCount()
    {
        string numbered = _folder.WriteFile("numbered.geojson", MapOfTaxiways("TWY1", "twy3"));
        string lettered = _folder.WriteFile("lettered.geojson", MapOfTaxiways("TWYA"));
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(numbered);

        vm.AddFeature("taxiway", Line);

        Assert.Equal("TWY2", FeatureName(vm.Document!.Features[^1]));

        await vm.OpenFilePathAsync(lettered);

        vm.AddFeature("taxiway", Line);

        Assert.Equal("TWY1", FeatureName(vm.Document!.Features[^1]));
    }

    [Fact]
    public async Task TheAutoName_SkipsNoNumberForTwy0_AndCountsTwy01AsOne()
    {
        string zero = _folder.WriteFile("zero.geojson", MapOfTaxiways("TWY0"));
        string padded = _folder.WriteFile("padded.geojson", MapOfTaxiways("TWY01"));
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(zero);

        vm.AddFeature("taxiway", Line);

        Assert.Equal("TWY1", FeatureName(vm.Document!.Features[^1]));

        await vm.OpenFilePathAsync(padded);

        vm.AddFeature("taxiway", Line);

        Assert.Equal("TWY2", FeatureName(vm.Document!.Features[^1]));
    }

    [Fact]
    public async Task CommittingATaxiway_AddsItLastSelected_AndOneUndoRemovesIt_InTheSameMode()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        await vm.OpenFilePathAsync(oak);
        vm.PickMode(AirportEditorMode.DrawTaxiway);
        AirportMapDocument opened = vm.Document!;
        int count = opened.Features.Count;

        vm.AddFeature("taxiway", Line);

        Assert.Equal(count + 1, vm.Document!.Features.Count);
        MapFeature added = vm.Document.Features[count];
        Assert.Equal("taxiway", added.FeatureType);
        Assert.Equal("LineString", added.GeometryType);
        Assert.Equal<MapPosition>(Line, added.Coordinates);
        Assert.Equal("TWY1", FeatureName(added));
        Assert.Equal(count, vm.SelectedFeatureIndex);
        Assert.Null(vm.SelectedVertexIndex);
        Assert.Equal("Undo Draw taxiway", vm.UndoDescription);
        Assert.Equal(AirportEditorMode.DrawTaxiway, vm.Mode);

        vm.UndoCommand.Execute(null);

        Assert.Same(opened, vm.Document);
        Assert.Null(vm.SelectedFeatureIndex);
        Assert.Equal(AirportEditorMode.DrawTaxiway, vm.Mode);
    }

    [Fact]
    public async Task EscWithNoDrawing_ReturnsToSelect_AndPickingAModeOrOpeningAMap_CancelsTheDrawing()
    {
        string oak = _folder.WriteFile("oak.geojson", AirportEditorTestFolder.OakText);
        using var vm = new AirportEditorViewModel(_folder.Loader(), new FakeAirportEditorPrompts(), NoAirportImagery.Instance, null);
        int cancels = 0;
        vm.DrawingCancelRequested += (_, _) => cancels++;
        Assert.Equal(AirportEditorMode.Select, vm.Mode);
        Assert.False(vm.ReturnToSelect());

        vm.PickMode(AirportEditorMode.DrawRunway);

        Assert.Equal(1, cancels);
        Assert.Equal(AirportEditorMode.DrawRunway, vm.Mode);

        vm.PickMode(AirportEditorMode.DrawRunway);

        Assert.Equal(2, cancels);
        Assert.True(vm.ReturnToSelect());
        Assert.Equal(AirportEditorMode.Select, vm.Mode);

        vm.PickMode(AirportEditorMode.PlaceSpot);
        await vm.OpenFilePathAsync(oak);

        Assert.Equal(4, cancels);
        Assert.Equal(AirportEditorMode.Select, vm.Mode);
    }

    private static string? FeatureName(MapFeature feature) => feature.CloneProperties()?["name"]?.GetValue<string>();

    private static string MapOfTaxiways(params string[] names) =>
        "{\"type\":\"FeatureCollection\",\"features\":["
        + string.Join(
            ",",
            names.Select(name =>
                "{\"type\":\"Feature\",\"properties\":{\"type\":\"taxiway\",\"name\":\""
                + name
                + "\"},"
                + "\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[-122.2,37.7],[-122.21,37.71]]}}"
            )
        )
        + "]}";

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

    [Fact]
    public async Task MoveVertex_ThenUndo_RestoresTheVertex()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        int index = FirstTaxiway(opened);
        MapPosition was = opened.Features[index].Coordinates[1];
        var to = new MapPosition(was.Lon + 0.0001, was.Lat + 0.0001);
        vm.SelectedFeatureIndex = index;
        vm.SelectedVertexIndex = 1;

        vm.MoveVertex(index, 1, to);

        Assert.Equal(to, vm.Document!.Features[index].Coordinates[1]);
        Assert.Equal("Undo Move vertex", vm.UndoDescription);
        Assert.Equal(index, vm.SelectedFeatureIndex);
        Assert.Equal(1, vm.SelectedVertexIndex);

        vm.UndoCommand.Execute(null);

        Assert.Same(opened, vm.Document);
        Assert.Equal(was, vm.Document!.Features[index].Coordinates[1]);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task MoveFeature_MovesAPoint_AsOneMoveFeatureStep_AndAMoveToWhereItIsRecordsNothing()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        int parking = FirstOfType(opened, "parking");
        MapPosition was = opened.Features[parking].Coordinates[0];

        vm.MoveFeature(parking, was);

        Assert.Same(opened, vm.Document);
        Assert.False(vm.UndoCommand.CanExecute(null));

        var to = new MapPosition(was.Lon - 0.0002, was.Lat);
        vm.MoveFeature(parking, to);

        Assert.Equal(to, vm.Document!.Features[parking].Coordinates[0]);
        Assert.Equal("Undo Move feature", vm.UndoDescription);

        vm.UndoCommand.Execute(null);

        Assert.Same(opened, vm.Document);
    }

    [Fact]
    public async Task InsertVertex_OnSegmentK_PutsTheNewVertexAtKPlusOne_AtThePosition()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        int index = FirstTaxiway(opened);
        MapFeature taxiway = opened.Features[index];
        MapPosition a = taxiway.Coordinates[1];
        MapPosition b = taxiway.Coordinates[2];
        var at = new MapPosition((a.Lon + b.Lon) / 2, (a.Lat + b.Lat) / 2);

        vm.InsertVertex(index, 1, at);

        Assert.Equal(taxiway.Coordinates.Insert(2, at), vm.Document!.Features[index].Coordinates);
        Assert.Equal("Undo Insert vertex", vm.UndoDescription);

        vm.UndoCommand.Execute(null);

        Assert.Same(opened, vm.Document);
    }

    [Fact]
    public async Task SplitTaxiway_AtAnInteriorVertex_YieldsTwoTaxiwaysSharingIt_TheFirstSelected()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        int index = FirstTaxiway(opened);
        MapFeature taxiway = opened.Features[index];
        vm.SelectedFeatureIndex = index;
        vm.SelectedVertexIndex = 1;
        Assert.True(vm.CanSplitTaxiway(index, 1));

        vm.SplitTaxiway(index, 1);

        Assert.Equal(opened.Features.Count + 1, vm.Document!.Features.Count);
        MapFeature first = vm.Document!.Features[index];
        MapFeature second = vm.Document!.Features[index + 1];
        Assert.Equal([.. taxiway.Coordinates.Take(2)], first.Coordinates);
        Assert.Equal([.. taxiway.Coordinates.Skip(1)], second.Coordinates);
        Assert.Equal(first.Coordinates[^1], second.Coordinates[0]);
        Assert.Equal("taxiway", first.FeatureType);
        Assert.Equal("taxiway", second.FeatureType);
        Assert.Equal(index, vm.SelectedFeatureIndex);
        Assert.Same(first, vm.SelectedFeature);
        Assert.Equal("Undo Split taxiway", vm.UndoDescription);
    }

    [Fact]
    public async Task SplitTaxiwayAt_ASegment_IsOneUndoStep()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        int index = FirstTaxiway(opened);
        MapFeature taxiway = opened.Features[index];
        MapPosition a = taxiway.Coordinates[0];
        MapPosition b = taxiway.Coordinates[1];
        var at = new MapPosition((a.Lon + b.Lon) / 2, (a.Lat + b.Lat) / 2);
        vm.SelectedFeatureIndex = index;
        Assert.True(vm.CanSplitTaxiwayAt(index, 0, at));

        vm.SplitTaxiwayAt(index, 0, at);

        Assert.Equal(opened.Features.Count + 1, vm.Document!.Features.Count);
        Assert.Equal([a, at], vm.Document!.Features[index].Coordinates);
        Assert.Equal([at, .. taxiway.Coordinates.Skip(1)], vm.Document!.Features[index + 1].Coordinates);
        Assert.Equal(index, vm.SelectedFeatureIndex);
        Assert.Equal("Undo Split taxiway", vm.UndoDescription);

        vm.UndoCommand.Execute(null);

        Assert.Same(opened, vm.Document);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Split_IsDisabled_AtAnEndVertex_AndForANonTaxiwayLine()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        int index = FirstTaxiway(opened);
        int last = opened.Features[index].Coordinates.Length - 1;
        int runway = FirstOfType(opened, "runway");

        Assert.False(vm.CanSplitTaxiway(index, 0));
        Assert.False(vm.CanSplitTaxiway(index, last));
        Assert.False(vm.CanSplitTaxiway(runway, 1));
        Assert.False(vm.CanSplitTaxiwayAt(runway, 0, Middle(opened.Features[runway], 0)));
        Assert.True(vm.CanSplitTaxiwayAt(index, 0, Middle(opened.Features[index], 0)));

        vm.SplitTaxiway(index, 0);
        vm.SplitTaxiwayAt(runway, 0, opened.Features[runway].Coordinates[0]);

        Assert.Same(opened, vm.Document);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteVertex_IsDisabled_AtTheLinesMinimumCount_AndDeleteFeatureIsOneStep()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        int index = FirstTaxiway(vm.Document!);
        Assert.True(vm.CanDeleteVertex(index, 1));

        vm.DeleteVertex(index, 1);

        Assert.Equal("Undo Delete vertex", vm.UndoDescription);
        while (vm.Document!.Features[index].Coordinates.Length > 2)
        {
            vm.DeleteVertex(index, 0);
        }

        AirportMapDocument atMinimum = vm.Document!;
        Assert.False(vm.CanDeleteVertex(index, 0));

        vm.DeleteVertex(index, 0);

        Assert.Same(atMinimum, vm.Document);

        vm.DeleteFeature(index);

        Assert.Equal(atMinimum.Features.Count - 1, vm.Document!.Features.Count);
        Assert.Equal("Undo Delete feature", vm.UndoDescription);
    }

    [Fact]
    public async Task InsertVertex_AndSplitTaxiwayAt_RefuseAPointOnEitherEndOfTheSegment()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        AirportMapDocument opened = vm.Document!;
        int index = FirstTaxiway(opened);
        MapPosition a = opened.Features[index].Coordinates[0];
        MapPosition b = opened.Features[index].Coordinates[1];
        Assert.True(vm.CanInsertVertex(index, 0, Middle(opened.Features[index], 0)));

        Assert.False(vm.CanInsertVertex(index, 0, a));
        Assert.False(vm.CanInsertVertex(index, 0, b));
        Assert.False(vm.CanSplitTaxiwayAt(index, 0, a));
        Assert.False(vm.CanSplitTaxiwayAt(index, 0, b));

        vm.InsertVertex(index, 0, a);
        vm.InsertVertex(index, 0, b);
        vm.SplitTaxiwayAt(index, 0, a);
        vm.SplitTaxiwayAt(index, 0, b);

        Assert.Same(opened, vm.Document);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task RenamingTwy1ToA_IsOneEditNameStep_AndUndoRestoresTwy1()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        vm.AddFeature("taxiway", Line);
        int added = vm.SelectedFeatureIndex!.Value;
        Assert.Equal("TWY1", FeatureName(vm.Document!.Features[added]));
        Assert.Null(vm.SelectionRows.Single(r => r.Name == "Type").Editor);
        AirportEditorPropertyEditor name = EditorOf(vm, "name");

        name.Text = "A";
        name.Commit();

        Assert.Null(name.Error);
        Assert.Equal("A", FeatureName(vm.Document!.Features[added]));
        Assert.Equal("Undo Edit name", vm.UndoDescription);
        Assert.Equal("A", vm.SelectionRows.Single(r => r.Name == "name").Value);

        vm.UndoCommand.Execute(null);

        Assert.Equal("TWY1", FeatureName(vm.Document!.Features[added]));
    }

    [Fact]
    public async Task CancellingAnEdit_RestoresTheText_AndCommitsNothing()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        vm.AddFeature("taxiway", Line);
        AirportMapDocument before = vm.Document!;
        AirportEditorPropertyEditor name = EditorOf(vm, "name");

        name.Text = "A";
        name.Cancel();
        name.Commit();

        Assert.Equal("TWY1", name.Text);
        Assert.Same(before, vm.Document);
    }

    [Fact]
    public async Task AHeading_TakesWholeDegreesFrom0To360_AndRefusesTheRest()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        vm.AddFeature("parking", Point);
        int added = vm.SelectedFeatureIndex!.Value;
        AirportMapDocument before = vm.Document!;

        foreach (string refused in new[] { "370", "361", "-1", "90.5" })
        {
            Assert.Equal("heading is a whole number from 0 to 360", Refusal(vm, "heading", refused));
            Assert.Same(before, vm.Document);
        }

        foreach ((string typed, long stored) in new[] { ("0", 0L), ("360", 360L), ("090", 90L) })
        {
            Commit(vm, "heading", typed);
            Assert.Equal(stored, vm.Document!.Features[added].CloneProperties()!["heading"]!.GetValue<long>());
        }

        Assert.Equal("Undo Edit heading", vm.UndoDescription);
    }

    [Fact]
    public async Task AnEmptyName_IsRefused()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        vm.AddFeature("spot", Point);
        AirportMapDocument before = vm.Document!;
        AirportEditorPropertyEditor name = EditorOf(vm, "name");

        name.Text = "  ";
        name.Commit();

        Assert.Equal("A name cannot be empty", name.Error);
        Assert.Same(before, vm.Document);
    }

    [Fact]
    public async Task ARunwaysThreshold_EditsAsTwoDistances_AndAnythingElseIsRefused()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        int runway = FirstOfType(vm.Document!, "runway");
        vm.SelectedFeatureIndex = runway;
        AirportMapDocument before = vm.Document!;
        AirportEditorPropertyEditor threshold = EditorOf(vm, "threshold");

        foreach (string refused in new[] { "abc", "500", "1 - 2 - 3", "-5 - 3" })
        {
            threshold.Text = refused;
            threshold.Commit();
            Assert.Equal("threshold is two distances in feet, end 1 - end 2 (e.g. 0 - 957)", threshold.Error);
            Assert.Same(before, vm.Document);
        }

        foreach ((string typed, string written) in new[] { ("0 - 500", "0 - 500"), ("0.0000001 - 0", "0 - 0"), ("957.25 - 12", "957.25 - 12") })
        {
            Commit(vm, "threshold", typed);
            string stored = vm.Document!.Features[runway].CloneProperties()!["threshold"]!.GetValue<string>();
            Assert.Equal(written, stored);
            string[] ends = stored.Split('-', StringSplitOptions.TrimEntries);
            Assert.Equal(2, ends.Length);
            Assert.All(ends, end => Assert.True(double.TryParse(end, NumberStyles.Float, CultureInfo.InvariantCulture, out double ft) && (ft >= 0)));
        }
    }

    [Fact]
    public async Task ARunwaysOtherKeys_TakeTheirOwnForms()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        int runway = vm.Document!.Features.Select((f, i) => (f, i)).First(x => x.f.CloneProperties()?["patternAltitude"] is not null).i;
        vm.SelectedFeatureIndex = runway;
        AirportMapDocument before = vm.Document!;

        Assert.Equal("patternAltitude must be a number", Refusal(vm, "patternAltitude", "high"));
        Assert.Equal("turnoff is left or right", Refusal(vm, "turnoff", "north"));
        Assert.Same(before, vm.Document);

        Commit(vm, "patternAltitude", "1000");
        Commit(vm, "turnoff", "Left");

        JsonObject properties = vm.Document!.Features[runway].CloneProperties()!;
        Assert.Equal(1000, properties["patternAltitude"]!.GetValue<long>());
        Assert.Equal("left", properties["turnoff"]!.GetValue<string>());
        Assert.Equal(
            "noTurnoff is two lists of taxiway names ([[\"A\"], [\"B\"]])",
            AirportEditorPropertyValues.Parse("noTurnoff", "[\"A\"]", out _)
        );
        Assert.Null(AirportEditorPropertyValues.Parse("noTurnoff", """[["A"], []]""", out JsonNode? noTurnoff));
        Assert.Equal("""[["A"],[]]""", noTurnoff!.ToJsonString());
    }

    [Fact]
    public async Task AnUnchangedValue_CommitsNothing()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        vm.AddFeature("taxiway", Line);
        AirportMapDocument before = vm.Document!;
        string? undo = vm.UndoDescription;
        AirportEditorPropertyEditor name = EditorOf(vm, "name");

        name.Text = "TWY1";
        name.Commit();

        Assert.Same(before, vm.Document);
        Assert.Equal(undo, vm.UndoDescription);
        Assert.Null(name.Error);

        int calls = 0;
        var editor = new AirportEditorPropertyEditor(
            "TWY1",
            _ =>
            {
                calls++;
                return "reached";
            }
        )
        {
            Text = "TWY1",
        };
        editor.Commit();

        Assert.Equal(0, calls);
        Assert.Null(editor.Error);
    }

    [Fact]
    public async Task ACommitTypedAgainstAMapThatHasSinceChanged_IsDropped()
    {
        using AirportEditorViewModel vm = await OpenOakAsync();
        vm.AddFeature("taxiway", Line);
        int added = vm.SelectedFeatureIndex!.Value;
        AirportEditorPropertyEditor name = EditorOf(vm, "name");
        vm.AddFeature("spot", Point);
        AirportMapDocument changed = vm.Document!;

        name.Text = "A";
        name.Commit();

        Assert.Same(changed, vm.Document);
        Assert.Equal("TWY1", FeatureName(vm.Document!.Features[added]));
        Assert.Null(name.Error);
    }

    private static AirportEditorPropertyEditor EditorOf(AirportEditorViewModel vm, string key) =>
        vm.SelectionRows.Single(r => r.Name == key).Editor ?? throw new InvalidOperationException($"{key} is not editable");

    private static string? Refusal(AirportEditorViewModel vm, string key, string text)
    {
        AirportEditorPropertyEditor editor = EditorOf(vm, key);
        editor.Text = text;
        editor.Commit();
        return editor.Error;
    }

    private static void Commit(AirportEditorViewModel vm, string key, string text)
    {
        AirportEditorPropertyEditor editor = EditorOf(vm, key);
        editor.Text = text;
        editor.Commit();
        Assert.Null(editor.Error);
    }

    private static MapPosition Middle(MapFeature line, int segment)
    {
        MapPosition a = line.Coordinates[segment];
        MapPosition b = line.Coordinates[segment + 1];
        return new MapPosition((a.Lon + b.Lon) / 2, (a.Lat + b.Lat) / 2);
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

    private static int FirstOfType(AirportMapDocument document, string type) =>
        document.Features.Select((f, i) => (f, i)).First(x => x.f.FeatureType == type).i;
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
