using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Tools › Airport Editor: a non-modal window over an <see cref="AirportEditorViewModel"/> with the commands that open a
/// map from the vNAS cache, a file or a folder in a toolbar, the map on an <see cref="AirportEditorCanvas"/>, and the
/// status line along the bottom. It answers the view model's questions itself: the app's file and folder pickers and a
/// small prompt for the airport. The toolbar's Imagery toggle shows the airport's vNAS tower-cab image under the map.
/// Closing it cancels an open and an image request in flight.
/// </summary>
public partial class AirportEditorWindow : Window, IAirportEditorPrompts
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "AirportEditor";

    private const string BaseTitle = "Airport Editor";

    // One downloader for every Airport Editor the app opens, never disposed: its HttpClient lives as long as the process,
    // so closing a window cannot dispose it under an open still in flight.
    private static readonly Lazy<AirportLayoutDownloader> SharedDownloader = new(() => new AirportLayoutDownloader());

    private static readonly IReadOnlyList<FilePickerFilter> MapFilter = [new FilePickerFilter("Airport map", ["*.geojson", "*.json"])];

    private readonly WindowGeometryHelper _geometry;
    private readonly IFilePickerService _filePicker;
    private readonly UserPreferences _preferences;

    // Parameterless ctor for the Avalonia designer / XamlLoader.
    public AirportEditorWindow()
        : this(
            new UserPreferences(),
            prompts => new AirportEditorViewModel(new AirportMapSourceLoader(SharedDownloader.Value), prompts, NoAirportImagery.Instance, null)
        ) { }

    /// <summary>Creates the window over the app's vNAS cache, drawing each airport's vNAS tower-cab image under its map.</summary>
    /// <param name="preferences">Keeps the window's position and size, the imagery toggle and its brightness.</param>
    /// <param name="towerCabImages">The app's tower-cab image service (one HTTP client, one disk cache).</param>
    /// <param name="vnasConfig">The app's vNAS config, which gives the tower-cab images' base URL.</param>
    public AirportEditorWindow(UserPreferences preferences, TowerCabImageService towerCabImages, VnasConfigService vnasConfig)
        : this(
            preferences,
            prompts => new AirportEditorViewModel(
                new AirportMapSourceLoader(SharedDownloader.Value),
                prompts,
                new TowerCabAirportImagerySource(towerCabImages, vnasConfig),
                preferences.ArtccId
            )
        ) { }

    /// <summary>Creates the window.</summary>
    /// <param name="preferences">Keeps the window's position and size, the imagery toggle and its brightness.</param>
    /// <param name="createViewModel">Builds the view model over the window's own prompts.</param>
    public AirportEditorWindow(UserPreferences preferences, Func<IAirportEditorPrompts, AirportEditorViewModel> createViewModel)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(createViewModel);
        InitializeComponent();
        _preferences = preferences;
        _filePicker = FilePickerFactory.Create(this);
        ViewModel = createViewModel(this);
        ViewModel.ShowImagery = preferences.AirportEditorShowImagery;
        DataContext = ViewModel;
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 1100, 700);
        _geometry.Restore();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FitRequested += OnFitRequested;
        MapCanvas.SelectionChanged += OnCanvasSelectionChanged;
        Activated += OnActivated;
        Closed += OnClosed;
        MapCanvas.ShowImagery = ViewModel.ShowImagery;
        MapCanvas.ImageryBrightness = preferences.AirportEditorImageryBrightness;
        MapCanvas.Image = ViewModel.Image;

        // A view model handed in with a map already open (createViewModel may open one) shows it, fitted once the canvas
        // has a size; a fresh one has none, so this does nothing.
        MapCanvas.Document = ViewModel.Document;
        MapCanvas.FitToFeatures();
    }

    /// <summary>Gets the view model the window shows.</summary>
    internal AirportEditorViewModel ViewModel { get; }

    /// <inheritdoc />
    public async Task<string?> AskAirportIdAsync()
    {
        var input = new TextBox { PlaceholderText = "FAA or ICAO id, e.g. OAK", MinWidth = 220 };
        var ok = new Button { Content = "Open", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window
        {
            Title = "Open from vNAS cache",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Airport" },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 6,
                        Children = { ok, cancel },
                    },
                },
            },
        };
        ok.Click += (_, _) => DialogPresenter.Close(dialog, input.Text);
        cancel.Click += (_, _) => DialogPresenter.Close(dialog, null);
        dialog.Opened += (_, _) => input.Focus();
        string? answer = await DialogPresenter.ShowModalAsync<string?>(dialog, this);
        return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
    }

    /// <inheritdoc />
    public Task<string?> PickMapFileAsync() => _filePicker.OpenFileAsync(new OpenFileOptions("Open airport map", MapFilter));

    /// <inheritdoc />
    public Task<string?> PickMapFolderAsync() => _filePicker.OpenFolderAsync(new OpenFolderOptions("Open airport map folder"));

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AirportEditorViewModel.Document))
        {
            MapCanvas.Document = ViewModel.Document;
        }
        else if (e.PropertyName is nameof(AirportEditorViewModel.SelectedFeatureIndex) or nameof(AirportEditorViewModel.SelectedVertexIndex))
        {
            MapCanvas.Selection = ViewModel.SelectedFeatureIndex is int feature ? new AirportEditorHit(feature, ViewModel.SelectedVertexIndex) : null;
        }
        else if (e.PropertyName == nameof(AirportEditorViewModel.SourceLabel))
        {
            _geometry.SetBaseTitle(ViewModel.SourceLabel.Length > 0 ? $"{BaseTitle} — {ViewModel.SourceLabel}" : BaseTitle);
        }
        else if (e.PropertyName == nameof(AirportEditorViewModel.Image))
        {
            MapCanvas.Image = ViewModel.Image;
        }
        else if (e.PropertyName == nameof(AirportEditorViewModel.ShowImagery))
        {
            MapCanvas.ShowImagery = ViewModel.ShowImagery;
            _preferences.SetAirportEditorShowImagery(ViewModel.ShowImagery);
        }
    }

    // The toggle and the brightness are Settings fields: read again whenever the window comes forward, as it does after
    // Settings closes.
    private void OnActivated(object? sender, EventArgs e)
    {
        ViewModel.ShowImagery = _preferences.AirportEditorShowImagery;
        MapCanvas.ImageryBrightness = _preferences.AirportEditorImageryBrightness;
    }

    private void OnFitRequested(object? sender, EventArgs e) => MapCanvas.FitToFeatures();

    // The canvas's click selected something (or nothing): the view model holds the selection the panel shows.
    private void OnCanvasSelectionChanged(object? sender, EventArgs e)
    {
        AirportEditorHit? selection = MapCanvas.Selection;
        ViewModel.SelectedFeatureIndex = selection?.FeatureIndex;
        ViewModel.SelectedVertexIndex = selection?.VertexIndex;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.FitRequested -= OnFitRequested;
        MapCanvas.SelectionChanged -= OnCanvasSelectionChanged;
        Activated -= OnActivated;
        ViewModel.Dispose();
        MapCanvas.Dispose();
    }
}
