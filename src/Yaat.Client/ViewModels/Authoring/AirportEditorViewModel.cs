using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The Airport Editor's state: the open airport map, where it came from, the status line and the selected feature.
/// </summary>
/// <remarks>
/// The map is one immutable <see cref="AirportMapDocument"/> replaced whole, so an undo can hold earlier values. An open
/// that fails keeps the map that was open and shows why in <see cref="StatusText"/>. The view model only reads maps: it
/// never writes a file, and the vNAS cache is refreshed only by the downloader behind <see cref="AirportMapSourceLoader"/>.
/// </remarks>
public sealed partial class AirportEditorViewModel : ObservableObject, IAirportEditorEditTarget, IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger("AirportEditorViewModel");

    // MapFeature.MinLineVertices, which is internal to Yaat.Sim: a line keeps at least two positions.
    private const int MinLineVertices = 2;

    private readonly AirportMapSourceLoader _loader;
    private readonly IAirportEditorPrompts _prompts;
    private readonly IAirportImagerySource _imagery;
    private readonly string? _preferredArtccId;
    private readonly UndoStack<AirportMapDocument> _undo = new();
    private AirportMapDocument? _openedDocument;
    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _imageryCts;
    private string? _imageryArtccId;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="AirportEditorViewModel"/> class.</summary>
    /// <param name="loader">Opens maps from the vNAS cache, a file or a folder.</param>
    /// <param name="prompts">The dialogs that ask for the airport, the file or the folder.</param>
    /// <param name="imagery">Gives the tower-cab image drawn under an open airport's map.</param>
    /// <param name="preferredArtccId">The ARTCC asked when no ARTCC boundary holds the map's centre, or null for none.</param>
    public AirportEditorViewModel(
        AirportMapSourceLoader loader,
        IAirportEditorPrompts prompts,
        IAirportImagerySource imagery,
        string? preferredArtccId
    )
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(imagery);
        _loader = loader;
        _prompts = prompts;
        _imagery = imagery;
        _preferredArtccId = preferredArtccId;
    }

    /// <summary>
    /// The open map's airport (FAA id), from the vNAS cache or the file stem (<see cref="AirportEditorAirportId"/>), or
    /// null when there is none and so no tower-cab image.
    /// </summary>
    [ObservableProperty]
    private string? _airportId;

    /// <summary>The open airport's tower-cab image, or null while none has arrived or there is none.</summary>
    [ObservableProperty]
    private TowerCabImage? _image;

    /// <summary>Whether imagery is shown; while off, no image is asked for and no tile is fetched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TilesActive))]
    private bool _showImagery = true;

    /// <summary>
    /// What vNAS answered for the open map's tower-cab image; the map's open resets it, and a request the toggle cancelled
    /// leaves it <see cref="AirportEditorVnasAnswer.Pending"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TilesActive))]
    private AirportEditorVnasAnswer _vnasAnswer = AirportEditorVnasAnswer.Unaskable;

    /// <summary>
    /// The vNAS download's progress line (<c>Downloading vNAS imagery: 40%</c>, or <c>… 12 MB</c> when the length is not
    /// given), or null when nothing is downloading.
    /// </summary>
    [ObservableProperty]
    private string? _imageryProgressText;

    /// <summary>Gets the latest image request, completed once its image is applied or dropped. Test seam.</summary>
    internal Task ImageryLoad { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Gets how a download's progress reaches the UI thread, or null for a <see cref="Progress{T}"/> made on the thread
    /// that asks (the UI thread). Test seam.
    /// </summary>
    internal Action<Action>? ProgressPost { get; init; }

    /// <summary>The open map, or null before the first successful open: one immutable value, replaced whole.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFeature))]
    [NotifyPropertyChangedFor(nameof(TilesActive))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private AirportMapDocument? _document;

    /// <summary>Gets whether the open map differs from the one last opened.</summary>
    public bool IsDirty => (Document is not null) && (!ReferenceEquals(Document, _openedDocument));

    /// <summary>Gets the Undo button's tooltip, <c>Undo {edit}</c>, or null when there is nothing to undo.</summary>
    public string? UndoDescription => _undo.NextUndoDescription is { } edit ? $"Undo {edit}" : null;

    /// <summary>Gets the Redo button's tooltip, <c>Redo {edit}</c>, or null when there is nothing to redo.</summary>
    public string? RedoDescription => _undo.NextRedoDescription is { } edit ? $"Redo {edit}" : null;

    /// <summary>
    /// Gets a value indicating whether the USGS tiles are drawn and fetched: imagery on, a map open, and vNAS either cannot
    /// be asked or answered with no image.
    /// </summary>
    public bool TilesActive =>
        (ShowImagery) && (Document is not null) && (VnasAnswer is AirportEditorVnasAnswer.Unaskable or AirportEditorVnasAnswer.None);

    /// <summary>Where the open map came from: <c>{FAA} (vNAS cache)</c> or the file's path; empty before the first open.</summary>
    [ObservableProperty]
    private string _sourceLabel = "";

    /// <summary>The status line: what the last open did, or why it opened nothing.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>The index of the selected feature in the open map's features, or null when nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFeature))]
    private int? _selectedFeatureIndex;

    /// <summary>
    /// The index of the selected vertex in the selected feature's positions, or null when none is; choosing another
    /// feature or opening a map clears it.
    /// </summary>
    [ObservableProperty]
    private int? _selectedVertexIndex;

    /// <summary>
    /// Raised on the UI thread after each successful open, once <see cref="Document"/> holds the new map, so the view fits
    /// itself to it; a later change of the document that is not an open does not raise it.
    /// </summary>
    public event EventHandler? FitRequested;

    /// <summary>Gets the selected feature, or null when nothing is selected or the index is outside the open map.</summary>
    public MapFeature? SelectedFeature =>
        ((Document is { } document) && (SelectedFeatureIndex is int index) && (index >= 0) && (index < document.Features.Count))
            ? document.Features[index]
            : null;

    /// <summary>
    /// Gets the property panel's rows for the selection, read-only: empty when nothing is selected, else the feature's
    /// number, type, geometry and position count, the selected vertex's number and position, then each of its properties
    /// in ordinal key order. Rebuilt whenever the selection or the map changes.
    /// </summary>
    public IReadOnlyList<AirportEditorPropertyRow> SelectionRows { get; private set; } = [];

    /// <summary>Opens an airport's vNAS map from the cache.</summary>
    /// <param name="airportId">The airport's FAA or ICAO id.</param>
    /// <returns>A task that completes when the open has finished.</returns>
    public Task OpenAirportAsync(string airportId) => RunOpenAsync(ct => _loader.OpenFromCacheAsync(airportId, ct));

    /// <summary>Opens an airport-map file.</summary>
    /// <param name="path">The file's path.</param>
    /// <returns>A task that completes when the open has finished.</returns>
    public Task OpenFilePathAsync(string path) => RunOpenAsync(ct => AirportMapSourceLoader.OpenFileAsync(path, ct));

    /// <summary>Opens the one combined <c>.geojson</c> a folder holds.</summary>
    /// <param name="folder">The folder's path.</param>
    /// <returns>A task that completes when the open has finished.</returns>
    public Task OpenFolderPathAsync(string folder) => RunOpenAsync(ct => AirportMapSourceLoader.OpenFolderAsync(folder, ct));

    /// <summary>
    /// Applies one edit to the open map as one undo step, keeping the selection where its feature and vertex still exist.
    /// Does nothing when no map is open or the edit returns the document it was given.
    /// </summary>
    /// <param name="description">The edit's name, as the Undo tooltip shows it (<c>Delete vertex</c>).</param>
    /// <param name="edit">Builds the new document from the open one.</param>
    public void ApplyEdit(string description, Func<AirportMapDocument, AirportMapDocument> edit)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(edit);
        if (Document is not { } before)
        {
            return;
        }

        AirportMapDocument after = edit(before);
        if (ReferenceEquals(after, before))
        {
            return;
        }

        _undo.Record(description, before);
        ReplaceDocument(after);
    }

    /// <summary>Moves one vertex of a line as one <c>Move vertex</c> step; a move to where it already is records nothing.</summary>
    /// <param name="featureIndex">The line's index in the open map's features.</param>
    /// <param name="vertexIndex">The vertex's index in the line's positions.</param>
    /// <param name="to">The vertex's new position.</param>
    public void MoveVertex(int featureIndex, int vertexIndex, MapPosition to) => Move("Move vertex", featureIndex, vertexIndex, to);

    /// <summary>Moves a point feature as one <c>Move feature</c> step; a move to where it already is records nothing.</summary>
    /// <param name="featureIndex">The point's index in the open map's features.</param>
    /// <param name="to">The point's new position.</param>
    public void MoveFeature(int featureIndex, MapPosition to) => Move("Move feature", featureIndex, 0, to);

    /// <summary>
    /// Inserts a vertex into a line's segment as one <c>Insert vertex</c> step: on segment k (from vertex k to vertex k + 1)
    /// the new vertex becomes vertex k + 1. Does nothing where <see cref="CanInsertVertex"/> is false.
    /// </summary>
    /// <param name="featureIndex">The line's index in the open map's features.</param>
    /// <param name="segmentIndex">The segment's index, its first vertex's index.</param>
    /// <param name="at">The new vertex's position.</param>
    public void InsertVertex(int featureIndex, int segmentIndex, MapPosition at)
    {
        if (CanInsertVertex(featureIndex, segmentIndex, at))
        {
            ApplyEdit("Insert vertex", d => MapEdits.InsertVertex(d, featureIndex, segmentIndex + 1, at));
        }
    }

    /// <summary>
    /// Whether a vertex can be inserted on a line's segment at a position: the line has the segment and the position is on
    /// neither of its ends, which would repeat a vertex.
    /// </summary>
    /// <param name="featureIndex">The line's index in the open map's features.</param>
    /// <param name="segmentIndex">The segment's index, its first vertex's index.</param>
    /// <param name="at">The new vertex's position.</param>
    /// <returns>True for a segment of a line and a position on neither of its ends.</returns>
    public bool CanInsertVertex(int featureIndex, int segmentIndex, MapPosition at) =>
        (IsSegmentOf(featureIndex, segmentIndex))
        && (Document!.Features[featureIndex].Coordinates[segmentIndex] != at)
        && (Document.Features[featureIndex].Coordinates[segmentIndex + 1] != at);

    /// <summary>Whether a line's vertex can be deleted: the line keeps at least its minimum two positions once it has gone.</summary>
    /// <param name="featureIndex">The line's index in the open map's features.</param>
    /// <param name="vertexIndex">The vertex's index in the line's positions.</param>
    /// <returns>True when the vertex exists and the line has more than two positions.</returns>
    public bool CanDeleteVertex(int featureIndex, int vertexIndex) =>
        (LineAt(featureIndex) is { } line)
        && (vertexIndex >= 0)
        && (vertexIndex < line.Coordinates.Length)
        && (line.Coordinates.Length > MinLineVertices);

    /// <summary>Deletes a line's vertex as one <c>Delete vertex</c> step; does nothing where <see cref="CanDeleteVertex"/> is false.</summary>
    /// <param name="featureIndex">The line's index in the open map's features.</param>
    /// <param name="vertexIndex">The vertex's index in the line's positions.</param>
    public void DeleteVertex(int featureIndex, int vertexIndex)
    {
        if (CanDeleteVertex(featureIndex, vertexIndex))
        {
            ApplyEdit("Delete vertex", d => MapEdits.DeleteVertex(d, featureIndex, vertexIndex));
        }
    }

    /// <summary>Deletes a feature as one <c>Delete feature</c> step; does nothing for a feature the map does not have.</summary>
    /// <param name="featureIndex">The feature's index in the open map's features.</param>
    public void DeleteFeature(int featureIndex)
    {
        if (FeatureAt(featureIndex) is not null)
        {
            ApplyEdit("Delete feature", d => MapEdits.DeleteFeature(d, featureIndex));
        }
    }

    /// <summary>Whether a taxiway can be split at a vertex: the feature is a taxiway line and the vertex is interior.</summary>
    /// <param name="featureIndex">The taxiway's index in the open map's features.</param>
    /// <param name="vertexIndex">The vertex's index in the taxiway's positions.</param>
    /// <returns>True for an interior vertex of a taxiway line.</returns>
    public bool CanSplitTaxiway(int featureIndex, int vertexIndex) =>
        (TaxiwayAt(featureIndex) is { } taxiway) && (vertexIndex >= 1) && (vertexIndex <= taxiway.Coordinates.Length - 2);

    /// <summary>
    /// Whether a taxiway can be split on a segment at a position: the feature is a taxiway line that has the segment, and the
    /// position is on neither of its ends.
    /// </summary>
    /// <param name="featureIndex">The taxiway's index in the open map's features.</param>
    /// <param name="segmentIndex">The segment's index, its first vertex's index.</param>
    /// <param name="at">The position of the vertex the halves would share.</param>
    /// <returns>True for a segment of a taxiway line and a position on neither of its ends.</returns>
    public bool CanSplitTaxiwayAt(int featureIndex, int segmentIndex, MapPosition at) =>
        (TaxiwayAt(featureIndex) is not null) && (CanInsertVertex(featureIndex, segmentIndex, at));

    /// <summary>
    /// Splits a taxiway at an interior vertex into two that share it, as one <c>Split taxiway</c> step, leaving the first half
    /// selected; does nothing where <see cref="CanSplitTaxiway"/> is false.
    /// </summary>
    /// <param name="featureIndex">The taxiway's index in the open map's features.</param>
    /// <param name="vertexIndex">The interior vertex to split at.</param>
    public void SplitTaxiway(int featureIndex, int vertexIndex)
    {
        if (CanSplitTaxiway(featureIndex, vertexIndex))
        {
            ApplySplit(featureIndex, d => MapEdits.SplitTaxiway(d, featureIndex, vertexIndex).Document);
        }
    }

    /// <summary>
    /// Inserts a vertex on a taxiway's segment and splits the taxiway there, as one <c>Split taxiway</c> step, leaving the first
    /// half selected; does nothing where <see cref="CanSplitTaxiwayAt"/> is false.
    /// </summary>
    /// <param name="featureIndex">The taxiway's index in the open map's features.</param>
    /// <param name="segmentIndex">The segment's index, its first vertex's index.</param>
    /// <param name="at">The position of the vertex the halves share.</param>
    public void SplitTaxiwayAt(int featureIndex, int segmentIndex, MapPosition at)
    {
        if (CanSplitTaxiwayAt(featureIndex, segmentIndex, at))
        {
            int vertex = segmentIndex + 1;
            ApplySplit(featureIndex, d => MapEdits.SplitTaxiway(MapEdits.InsertVertex(d, featureIndex, vertex, at), featureIndex, vertex).Document);
        }
    }

    /// <summary>Cancels an open in flight; a later open does nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Cancelled, not disposed: the open in flight disposes its own source once it has finished.
        _disposed = true;
        _openCts?.Cancel();
        _openCts = null;
        _imageryCts?.Cancel();
        _imageryCts = null;
    }

    partial void OnDocumentChanged(AirportMapDocument? value)
    {
        RebuildSelectionRows();
        RefreshEditState();
    }

    // The undo and redo state follows the stack, and Delete follows the selection and the map.
    private void RefreshEditState()
    {
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    // Turned on, vNAS is asked again only when it has not answered (the toggle cancelled the request); turned off, a
    // request in flight is dropped with its progress line.
    partial void OnShowImageryChanged(bool value)
    {
        if (!value)
        {
            _imageryCts?.Cancel();
            _imageryCts = null;
            ImageryProgressText = null;
        }
        else if (VnasAnswer == AirportEditorVnasAnswer.Pending)
        {
            StartImageryRequest();
        }
    }

    // Another feature's vertices are not this one's, so a change of feature drops the vertex.
    partial void OnSelectedFeatureIndexChanged(int? value)
    {
        SelectedVertexIndex = null;
        RebuildSelectionRows();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVertexIndexChanged(int? value)
    {
        RebuildSelectionRows();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _undo.CanUndo;

    private bool CanRedo() => _undo.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if ((Document is { } current) && (_undo.Undo(current) is { } before))
        {
            ReplaceDocument(before);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if ((Document is { } current) && (_undo.Redo(current) is { } after))
        {
            ReplaceDocument(after);
        }
    }

    // A selected feature can go; a selected vertex only from a line that keeps its minimum once it has gone.
    private bool CanDeleteSelected()
    {
        if (SelectedFeature is not { } feature)
        {
            return false;
        }

        if (SelectedVertexIndex is not int vertex)
        {
            return true;
        }

        return (string.Equals(feature.GeometryType, "LineString", StringComparison.Ordinal))
            && (vertex >= 0)
            && (vertex < feature.Coordinates.Length)
            && (feature.Coordinates.Length > MinLineVertices);
    }

    // The selected vertex, or else the selected feature; the deleted vertex leaves its feature selected.
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if ((!CanDeleteSelected()) || (SelectedFeatureIndex is not int feature))
        {
            return;
        }

        if (SelectedVertexIndex is int vertex)
        {
            ApplyEdit("Delete vertex", d => MapEdits.DeleteVertex(d, feature, vertex));
            SelectedVertexIndex = null;
        }
        else
        {
            ApplyEdit("Delete feature", d => MapEdits.DeleteFeature(d, feature));
        }
    }

    // A move of a feature's vertex (vertex 0 of a point), recorded only when the position changes.
    private void Move(string description, int featureIndex, int vertexIndex, MapPosition to)
    {
        if ((FeatureAt(featureIndex) is not { GeometryType: not null } feature) || (vertexIndex < 0) || (vertexIndex >= feature.Coordinates.Length))
        {
            return;
        }

        if (feature.Coordinates[vertexIndex] != to)
        {
            ApplyEdit(description, d => MapEdits.MoveVertex(d, featureIndex, vertexIndex, to));
        }
    }

    // The split's halves are new features, so the selection repair cannot find the old one: the first half is selected.
    private void ApplySplit(int featureIndex, Func<AirportMapDocument, AirportMapDocument> split)
    {
        AirportMapDocument? before = Document;
        ApplyEdit("Split taxiway", split);
        if (!ReferenceEquals(before, Document))
        {
            SelectedFeatureIndex = featureIndex;
        }
    }

    private MapFeature? FeatureAt(int featureIndex) =>
        ((Document is { } document) && (featureIndex >= 0) && (featureIndex < document.Features.Count)) ? document.Features[featureIndex] : null;

    private MapFeature? LineAt(int featureIndex) =>
        ((FeatureAt(featureIndex) is { } feature) && (string.Equals(feature.GeometryType, "LineString", StringComparison.Ordinal))) ? feature : null;

    private MapFeature? TaxiwayAt(int featureIndex) =>
        ((LineAt(featureIndex) is { } line) && (string.Equals(line.FeatureType, "taxiway", StringComparison.Ordinal))) ? line : null;

    private bool IsSegmentOf(int featureIndex, int segmentIndex) =>
        (LineAt(featureIndex) is { } line) && (segmentIndex >= 0) && (segmentIndex < line.Coordinates.Length - 1);

    // An edit, an undo or a redo: the selection stays on its feature (and vertex) when they still exist, else it clears.
    private void ReplaceDocument(AirportMapDocument next)
    {
        AirportMapDocument? previous = Document;
        MapFeature? selected = SelectedFeature;
        int? featureIndex = SelectedFeatureIndex;
        int? vertexIndex = SelectedVertexIndex;
        Document = next;
        if (KeptFeatureIndex(previous, next, featureIndex) is not int kept)
        {
            SelectedVertexIndex = null;
            SelectedFeatureIndex = null;
            return;
        }

        SelectedFeatureIndex = kept;
        MapFeature now = next.Features[kept];
        bool sameVertices = (ReferenceEquals(now, selected)) || (now.Coordinates.Length == selected?.Coordinates.Length);
        SelectedVertexIndex = ((sameVertices) && (vertexIndex is int v) && (v < now.Coordinates.Length)) ? v : null;
    }

    // With the feature count unchanged the edit kept every feature in place; otherwise the selected feature is looked up
    // by reference, so a delete before it moves the index and its own delete clears it.
    private static int? KeptFeatureIndex(AirportMapDocument? previous, AirportMapDocument next, int? featureIndex)
    {
        if ((previous is null) || (featureIndex is not int index) || (index < 0) || (index >= previous.Features.Count))
        {
            return null;
        }

        if (previous.Features.Count == next.Features.Count)
        {
            return index;
        }

        int found = next.Features.IndexOf(previous.Features[index], 0, next.Features.Count, ReferenceEqualityComparer.Instance);
        return (found >= 0) ? found : null;
    }

    private void RebuildSelectionRows()
    {
        SelectionRows =
            (SelectedFeature is { } feature) && (SelectedFeatureIndex is int index) && (Document is { } doc)
                ? AirportEditorPropertyRows.Build(feature, index, doc.Features.Count, SelectedVertexIndex)
                : [];
        OnPropertyChanged(nameof(SelectionRows));
    }

    [RelayCommand]
    private async Task OpenFromCacheAsync()
    {
        string? airportId = await _prompts.AskAirportIdAsync();
        if (!string.IsNullOrWhiteSpace(airportId))
        {
            await OpenAirportAsync(airportId);
        }
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        string? path = await _prompts.PickMapFileAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenFilePathAsync(path);
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        string? folder = await _prompts.PickMapFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await OpenFolderPathAsync(folder);
        }
    }

    // A new open cancels the one in flight, whose outcome is then dropped, so the last open asked for is the one shown.
    private async Task RunOpenAsync(Func<CancellationToken, Task<AirportMapOpenOutcome>> open)
    {
        if (_disposed)
        {
            return;
        }

        // The replaced open's source is only cancelled here; that open disposes it once its own await has finished, so
        // no token is read after its source is disposed.
        _openCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _openCts = cts;
        AirportMapOpenOutcome outcome;
        try
        {
            outcome = await open(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            Interlocked.CompareExchange(ref _openCts, null, cts);
        }

        if (!cts.IsCancellationRequested)
        {
            Apply(outcome);
        }
    }

    private void Apply(AirportMapOpenOutcome outcome)
    {
        if (outcome is AirportMapOpenOutcome.Opened opened)
        {
            SelectedVertexIndex = null;
            SelectedFeatureIndex = null;
            _undo.Clear();
            _openedDocument = opened.Document;
            Document = opened.Document;
            RefreshEditState();
            SourceLabel = opened.SourceLabel;
            SetImageryTarget(opened);
        }

        StatusText = outcome.Message;
        if (outcome is AirportMapOpenOutcome.Opened)
        {
            FitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // The new map's airport and ARTCC; the last map's image is another airport's, so it goes (not disposed: a render
    // snapshot may still hold it) and the new one is asked for.
    private void SetImageryTarget(AirportMapOpenOutcome.Opened opened)
    {
        var bounds = AirportEditorBounds.Of(opened.Document.Features.SelectMany(f => f.Coordinates));
        LatLon? centre = bounds is null ? null : new LatLon((bounds.MinLat + bounds.MaxLat) / 2, (bounds.MinLon + bounds.MaxLon) / 2);
        AirportId = AirportEditorAirportId.Infer(opened.FaaCode, opened.SourceLabel, centre);
        _imageryArtccId = centre is { } at ? AirportEditorArtccResolver.Resolve(at, _preferredArtccId) : null;
        Image = null;
        ImageryProgressText = null;
        bool askable = (AirportId is not null) && (_imageryArtccId is not null);
        VnasAnswer = askable ? AirportEditorVnasAnswer.Pending : AirportEditorVnasAnswer.Unaskable;
        StartImageryRequest();
    }

    // A newer request cancels the older one, whose late image is then dropped; nothing is asked while imagery is off, vNAS
    // has answered, or the map has no airport or ARTCC.
    private void StartImageryRequest()
    {
        _imageryCts?.Cancel();
        _imageryCts = null;
        if ((_disposed) || (!ShowImagery) || (VnasAnswer != AirportEditorVnasAnswer.Pending))
        {
            return;
        }

        if ((AirportId is not { } airportId) || (_imageryArtccId is not { } artccId))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _imageryCts = cts;
        void onProgress(TowerCabDownloadProgress p) => OnImageryProgress(cts, p);
        IProgress<TowerCabDownloadProgress> progress = ProgressPost is { } post
            ? new PostedProgress(post, onProgress)
            : new Progress<TowerCabDownloadProgress>(onProgress);
        ImageryLoad = LoadImageryAsync(artccId, airportId, cts, progress);
    }

    // Only the current request's progress shows; a report posted after its answer or its cancel is dropped.
    private void OnImageryProgress(CancellationTokenSource cts, TowerCabDownloadProgress progress)
    {
        if ((ReferenceEquals(_imageryCts, cts)) && (!cts.IsCancellationRequested))
        {
            ImageryProgressText = AirportEditorImageryProgress.Text(progress);
        }
    }

    // The answer, unless a newer open, the toggle or the window's close dropped the request; a failure counts as None.
    private async Task LoadImageryAsync(string artccId, string airportId, CancellationTokenSource cts, IProgress<TowerCabDownloadProgress> progress)
    {
        TowerCabImage? image = null;
        try
        {
            image = await _imagery.GetAsync(artccId, airportId, progress, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Failed to load the tower-cab image for {AirportId} ({ArtccId})", airportId, artccId);
            }
        }
        finally
        {
            // Not disposed: a source with no timer holds nothing, and a later Cancel on a disposed one would throw.
            Interlocked.CompareExchange(ref _imageryCts, null, cts);
        }

        if (!cts.IsCancellationRequested)
        {
            Image = image;
            ImageryProgressText = null;
            VnasAnswer = image is null ? AirportEditorVnasAnswer.None : AirportEditorVnasAnswer.Image;
        }
    }

    // Runs each report through the post seam.
    private sealed class PostedProgress(Action<Action> post, Action<TowerCabDownloadProgress> handler) : IProgress<TowerCabDownloadProgress>
    {
        public void Report(TowerCabDownloadProgress value) => post(() => handler(value));
    }
}

/// <summary>What vNAS answered for an Airport Editor map's tower-cab image.</summary>
public enum AirportEditorVnasAnswer
{
    /// <summary>No request can be made: the map has no airport id or no ARTCC.</summary>
    Unaskable,

    /// <summary>Asked (or to be asked once imagery is on), no answer yet.</summary>
    Pending,

    /// <summary>vNAS has an image for the airport.</summary>
    Image,

    /// <summary>vNAS has no image for the airport, or the request failed.</summary>
    None,
}

/// <summary>The Airport Editor's vNAS download progress line.</summary>
internal static class AirportEditorImageryProgress
{
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>The line for a progress report: a percentage when the length is known, else whole megabytes.</summary>
    /// <param name="progress">The report.</param>
    /// <returns>The line.</returns>
    public static string Text(TowerCabDownloadProgress progress) =>
        progress.TotalBytes is long total and > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Downloading vNAS imagery: {Math.Min(100, progress.BytesRead * 100 / total)}%")
            : string.Create(CultureInfo.InvariantCulture, $"Downloading vNAS imagery: {progress.BytesRead / BytesPerMegabyte} MB");
}

/// <summary>One row of the Airport Editor's property panel.</summary>
/// <param name="Name">What the row shows: a fixed heading or a property's key.</param>
/// <param name="Value">The row's value as text.</param>
public sealed record AirportEditorPropertyRow(string Name, string Value);

/// <summary>Builds the Airport Editor's property panel rows for a selected feature.</summary>
internal static class AirportEditorPropertyRows
{
    /// <summary>Builds the rows of a selected feature.</summary>
    /// <param name="feature">The selected feature.</param>
    /// <param name="index">Its index in the map's features.</param>
    /// <param name="count">How many features the map has.</param>
    /// <param name="vertex">The selected vertex's index, or null for none; one outside the positions adds no rows.</param>
    /// <returns>The rows, in panel order.</returns>
    public static IReadOnlyList<AirportEditorPropertyRow> Build(MapFeature feature, int index, int count, int? vertex)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        int positions = feature.Coordinates.Length;
        List<AirportEditorPropertyRow> rows =
        [
            new("Feature", string.Create(inv, $"{index + 1} of {count}")),
            new("Type", feature.FeatureType ?? "(none)"),
            new("Geometry", feature.GeometryType ?? "(none)"),
        ];
        if (positions > 0)
        {
            rows.Add(new("Positions", positions.ToString(inv)));
        }

        if ((vertex is int v) && (v >= 0) && (v < positions))
        {
            MapPosition at = feature.Coordinates[v];
            rows.Add(new("Vertex", string.Create(inv, $"{v + 1} of {positions}")));
            rows.Add(new("Position", string.Create(inv, $"{at.Lat:F7}, {at.Lon:F7}")));
        }

        if (feature.CloneProperties() is { } properties)
        {
            foreach (KeyValuePair<string, JsonNode?> property in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                rows.Add(new(property.Key, TextOf(property.Value)));
            }
        }

        return rows;
    }

    // A JSON string as itself, a null as "null", anything else as its JSON text.
    private static string TextOf(JsonNode? node) =>
        node switch
        {
            null => "null",
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            _ => node.ToJsonString(),
        };
}
