using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;
using Yaat.Client.Services.Imagery;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Tools › Airport Editor: a non-modal window over an <see cref="AirportEditorViewModel"/> with the commands that open a
/// map from the vNAS cache, a file or a folder in a toolbar, the map on an <see cref="AirportEditorCanvas"/>, and the
/// status line along the bottom. It answers the view model's questions itself: the app's file and folder pickers and a
/// small prompt for the airport. The toolbar's Imagery toggle shows the airport's vNAS tower-cab image under the map, or
/// the USGS NAIPPlus tiles once vNAS has none. Closing it cancels an open, an image request and the tile fetches in flight.
/// </summary>
public partial class AirportEditorWindow : Window, IAirportEditorPrompts
{
    /// <summary>The key the window's position and size are kept under.</summary>
    public const string GeometryKey = "AirportEditor";

    private const string BaseTitle = "Airport Editor";

    private static readonly KeyGesture FallbackUndo = new(Key.Z, KeyModifiers.Control);
    private static readonly KeyGesture CtrlY = new(Key.Y, KeyModifiers.Control);

    // One downloader for every Airport Editor the app opens, never disposed: its HttpClient lives as long as the process,
    // so closing a window cannot dispose it under an open still in flight.
    private static readonly Lazy<AirportLayoutDownloader> SharedDownloader = new(() => new AirportLayoutDownloader());

    private static readonly IReadOnlyList<FilePickerFilter> MapFilter = [new FilePickerFilter("Airport map", ["*.geojson", "*.json"])];

    private readonly WindowGeometryHelper _geometry;
    private readonly IFilePickerService _filePicker;
    private readonly UserPreferences _preferences;

    private readonly UsgsImageryTileCache _tiles;

    // Parameterless ctor for the Avalonia designer / XamlLoader.
    public AirportEditorWindow()
        : this(
            new UserPreferences(),
            prompts => new AirportEditorViewModel(new AirportMapSourceLoader(SharedDownloader.Value), prompts, NoAirportImagery.Instance, null),
            UsgsImageryTileCache.CreateForApp
        ) { }

    /// <summary>Creates the window over the app's vNAS cache, drawing each airport's vNAS tower-cab image under its map.</summary>
    /// <param name="preferences">Keeps the window's position and size, the imagery toggle and its brightness.</param>
    /// <param name="towerCabImagery">
    /// The app's one tower-cab image source, shared by every editor window so a request in flight survives a close.
    /// </param>
    public AirportEditorWindow(UserPreferences preferences, TowerCabAirportImagerySource towerCabImagery)
        : this(
            preferences,
            prompts => new AirportEditorViewModel(new AirportMapSourceLoader(SharedDownloader.Value), prompts, towerCabImagery, preferences.ArtccId),
            UsgsImageryTileCache.CreateForApp
        ) { }

    /// <summary>Creates the window.</summary>
    /// <param name="preferences">Keeps the window's position and size, the imagery toggle and its brightness.</param>
    /// <param name="createViewModel">Builds the view model over the window's own prompts.</param>
    /// <param name="createTileCache">
    /// Builds the USGS tile cache over the callback that repaints the canvas when a tile arrives; the window owns the cache
    /// and disposes it on close.
    /// </param>
    public AirportEditorWindow(
        UserPreferences preferences,
        Func<IAirportEditorPrompts, AirportEditorViewModel> createViewModel,
        Func<Action, UsgsImageryTileCache> createTileCache
    )
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(createViewModel);
        ArgumentNullException.ThrowIfNull(createTileCache);
        InitializeComponent();
        _preferences = preferences;
        _filePicker = FilePickerFactory.Create(this);
        ViewModel = createViewModel(this);
        ViewModel.ShowImagery = preferences.AirportEditorShowImagery;
        DataContext = ViewModel;
        _geometry = new WindowGeometryHelper(this, preferences, GeometryKey, 1100, 700);
        _geometry.Restore();
        _tiles = createTileCache(MapCanvas.MarkDirty);
        MapCanvas.TileCache = _tiles;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FitRequested += OnFitRequested;
        MapCanvas.SelectionChanged += OnCanvasSelectionChanged;
        MapCanvas.EditTarget = ViewModel;
        Activated += OnActivated;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += OnClosed;
        MapCanvas.ShowImagery = ViewModel.ShowImagery;
        MapCanvas.ImageryBrightness = preferences.AirportEditorImageryBrightness;
        MapCanvas.Image = ViewModel.Image;
        MapCanvas.ImageryProgressText = ViewModel.ImageryProgressText;
        ApplyTilesActive();

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
        else if (e.PropertyName == nameof(AirportEditorViewModel.ImageryProgressText))
        {
            MapCanvas.ImageryProgressText = ViewModel.ImageryProgressText;
        }
        else if (e.PropertyName == nameof(AirportEditorViewModel.TilesActive))
        {
            ApplyTilesActive();
        }
    }

    // The one place the tile cache is turned on or off: off cancels its fetches, so nothing is fetched while vNAS is
    // pending or has an image, or while imagery is off.
    private void ApplyTilesActive()
    {
        bool active = ViewModel.TilesActive;
        _tiles.SetActive(active);
        MapCanvas.TilesActive = active;
    }

    // The toggle and the brightness are Settings fields: read again whenever the window comes forward, as it does after
    // Settings closes.
    private void OnActivated(object? sender, EventArgs e)
    {
        ViewModel.ShowImagery = _preferences.AirportEditorShowImagery;
        MapCanvas.ImageryBrightness = _preferences.AirportEditorImageryBrightness;
    }

    private void OnFitRequested(object? sender, EventArgs e) => MapCanvas.FitToFeatures();

    // The platform's undo and redo keys (Ctrl+Z and Ctrl+Y, Cmd+Z and Cmd+Shift+Z) and Del act on the map, also when a
    // read-only property row marked them handled; an editable text field keeps its own undo and Delete.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox { IsReadOnly: false })
        {
            return;
        }

        IRelayCommand? command = CommandFor(e);
        if (command is null)
        {
            return;
        }

        e.Handled = true;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    // The canvas's click selected something (or nothing): the view model holds the selection the panel shows.
    private void OnCanvasSelectionChanged(object? sender, EventArgs e)
    {
        AirportEditorHit? selection = MapCanvas.Selection;
        ViewModel.SelectedFeatureIndex = selection?.FeatureIndex;
        ViewModel.SelectedVertexIndex = selection?.VertexIndex;
    }

    // Ctrl+Y redoes off macOS whether or not the platform lists it; without platform settings Ctrl+Z undoes.
    private IRelayCommand? CommandFor(KeyEventArgs e)
    {
        PlatformHotkeyConfiguration? hotkeys = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if ((hotkeys?.Undo ?? [FallbackUndo]).Exists(g => g.Matches(e)))
        {
            return ViewModel.UndoCommand;
        }

        if (((hotkeys?.Redo ?? []).Exists(g => g.Matches(e))) || ((!PlatformHelper.IsMacOS) && (CtrlY.Matches(e))))
        {
            return ViewModel.RedoCommand;
        }

        return ((e.Key == Key.Delete) && (e.KeyModifiers == KeyModifiers.None)) ? ViewModel.DeleteSelectedCommand : null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.FitRequested -= OnFitRequested;
        MapCanvas.SelectionChanged -= OnCanvasSelectionChanged;
        MapCanvas.EditTarget = null;
        Activated -= OnActivated;
        RemoveHandler(KeyDownEvent, OnWindowKeyDown);

        // The canvas first: its Dispose waits out a frame in progress, so no frame draws once the view model and the
        // cache let go of their images (which are dropped, never disposed: a published SKImage stays valid).
        MapCanvas.Dispose();
        ViewModel.Dispose();
        _tiles.Dispose();
    }
}
