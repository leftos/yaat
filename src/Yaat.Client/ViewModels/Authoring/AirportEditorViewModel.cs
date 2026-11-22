using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly AirportMapSourceLoader _loader;
    private readonly IAirportEditorPrompts _prompts;
    private CancellationTokenSource? _openCts;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="AirportEditorViewModel"/> class.</summary>
    /// <param name="loader">Opens maps from the vNAS cache, a file or a folder.</param>
    /// <param name="prompts">The dialogs that ask for the airport, the file or the folder.</param>
    public AirportEditorViewModel(AirportMapSourceLoader loader, IAirportEditorPrompts prompts)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(prompts);
        _loader = loader;
        _prompts = prompts;
    }

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

    /// <summary>Gets the selected feature, or null when nothing is selected or the index is outside the open map.</summary>
    public MapFeature? SelectedFeature =>
        ((Document is { } document) && (SelectedFeatureIndex is int index) && (index >= 0) && (index < document.Features.Count))
            ? document.Features[index]
            : null;

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
            SelectedFeatureIndex = null;
            Document = opened.Document;
            SourceLabel = opened.SourceLabel;
        }

        StatusText = outcome.Message;
    }
}
