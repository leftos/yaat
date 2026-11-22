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
public sealed partial class AirportEditorViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger("AirportEditorViewModel");

    private readonly AirportMapSourceLoader _loader;
    private readonly IAirportEditorPrompts _prompts;
    private readonly IAirportImagerySource _imagery;
    private readonly string? _preferredArtccId;
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

    /// <summary>Whether imagery is shown; while off, no image is asked for.</summary>
    [ObservableProperty]
    private bool _showImagery = true;

    /// <summary>Gets the latest image request, completed once its image is applied or dropped. Test seam.</summary>
    internal Task ImageryLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The open map, or null before the first successful open: one immutable value, replaced whole.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFeature))]
    private AirportMapDocument? _document;

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

    partial void OnDocumentChanged(AirportMapDocument? value) => RebuildSelectionRows();

    // Turned on, the open airport's image is asked for if it has not arrived; turned off, a request in flight is dropped.
    partial void OnShowImageryChanged(bool value)
    {
        if (!value)
        {
            _imageryCts?.Cancel();
            _imageryCts = null;
        }
        else if (Image is null)
        {
            StartImageryRequest();
        }
    }

    // Another feature's vertices are not this one's, so a change of feature drops the vertex.
    partial void OnSelectedFeatureIndexChanged(int? value)
    {
        SelectedVertexIndex = null;
        RebuildSelectionRows();
    }

    partial void OnSelectedVertexIndexChanged(int? value) => RebuildSelectionRows();

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
            Document = opened.Document;
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
        StartImageryRequest();
    }

    // A newer request cancels the older one, whose late image is then dropped; nothing is asked while imagery is off or
    // the map has no airport or ARTCC.
    private void StartImageryRequest()
    {
        _imageryCts?.Cancel();
        _imageryCts = null;
        if ((_disposed) || (!ShowImagery) || (AirportId is not { } airportId) || (_imageryArtccId is not { } artccId))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _imageryCts = cts;
        ImageryLoad = LoadImageryAsync(artccId, airportId, cts);
    }

    private async Task LoadImageryAsync(string artccId, string airportId, CancellationTokenSource cts)
    {
        try
        {
            TowerCabImage? image = await _imagery.GetAsync(artccId, airportId, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                Image = image;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer open, the toggle or the window's close dropped it.
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
    }
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
