using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// the USGS NAIPPlus tiles once vNAS has none. The mode buttons (and Esc, T, R, P, S and H) pick what a click on the map
/// does. Closing it cancels an open, an image request and the tile fetches in flight.
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
    private readonly (ToggleButton Button, AirportEditorMode Mode)[] _modeButtons;

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
        ViewModel.DrawingCancelRequested += OnDrawingCancelRequested;
        _modeButtons =
        [
            (SelectModeButton, AirportEditorMode.Select),
            (TaxiwayModeButton, AirportEditorMode.DrawTaxiway),
            (RunwayModeButton, AirportEditorMode.DrawRunway),
            (ParkingModeButton, AirportEditorMode.PlaceParking),
            (SpotModeButton, AirportEditorMode.PlaceSpot),
            (HelipadModeButton, AirportEditorMode.PlaceHelipad),
        ];
        foreach ((ToggleButton button, _) in _modeButtons)
        {
            button.Click += OnModeButtonClick;
        }

        ApplyMode();
        Activated += OnActivated;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        PropertyRows.AddHandler(LostFocusEvent, OnPropertyBoxLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += OnClosed;
        MapCanvas.ShowImagery = ViewModel.ShowImagery;
        MapCanvas.ImageryBrightness = preferences.AirportEditorImageryBrightness;
        MapCanvas.SnapOptions = preferences.AirportEditorSnapOptions;
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
        else if (e.PropertyName == nameof(AirportEditorViewModel.Mode))
        {
            ApplyMode();
        }
        else
        {
            OnImageryPropertyChanged(e.PropertyName);
        }
    }

    private void OnImageryPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(AirportEditorViewModel.Image))
        {
            MapCanvas.Image = ViewModel.Image;
        }
        else if (propertyName == nameof(AirportEditorViewModel.ShowImagery))
        {
            MapCanvas.ShowImagery = ViewModel.ShowImagery;
            _preferences.SetAirportEditorShowImagery(ViewModel.ShowImagery);
        }
        else if (propertyName == nameof(AirportEditorViewModel.ImageryProgressText))
        {
            MapCanvas.ImageryProgressText = ViewModel.ImageryProgressText;
        }
        else if (propertyName == nameof(AirportEditorViewModel.TilesActive))
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

    // The toggle, the brightness and the snap settings are Settings fields: read again whenever the window comes forward,
    // as it does after Settings closes.
    private void OnActivated(object? sender, EventArgs e)
    {
        ViewModel.ShowImagery = _preferences.AirportEditorShowImagery;
        MapCanvas.ImageryBrightness = _preferences.AirportEditorImageryBrightness;
        MapCanvas.SnapOptions = _preferences.AirportEditorSnapOptions;
    }

    private void OnFitRequested(object? sender, EventArgs e) => MapCanvas.FitToFeatures();

    private void OnDrawingCancelRequested(object? sender, EventArgs e) => MapCanvas.CancelDrawing();

    // The canvas follows the view model's mode, and exactly the mode's button is checked (a click on the checked one
    // would otherwise uncheck it).
    private void ApplyMode()
    {
        MapCanvas.Mode = ViewModel.Mode;
        foreach ((ToggleButton button, AirportEditorMode mode) in _modeButtons)
        {
            button.IsChecked = mode == ViewModel.Mode;
        }
    }

    private void OnModeButtonClick(object? sender, RoutedEventArgs e)
    {
        foreach ((ToggleButton button, AirportEditorMode mode) in _modeButtons)
        {
            if (ReferenceEquals(button, sender))
            {
                ViewModel.PickMode(mode);
            }
        }

        ApplyMode();
    }

    // The platform's undo and redo keys (Ctrl+Z and Ctrl+Y, Cmd+Z and Cmd+Shift+Z) and Del act on the map, also when a
    // read-only property row marked them handled; an editable text field keeps its own undo, Delete and letter keys, and
    // in a property's value box Enter commits and Esc restores the value.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox { IsReadOnly: false } box)
        {
            HandlePropertyBoxKey(box, e);
            return;
        }

        if ((!e.Handled) && (HandleModeKey(e)))
        {
            e.Handled = true;
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

    private static void HandlePropertyBoxKey(TextBox box, KeyEventArgs e)
    {
        if ((box.DataContext is not AirportEditorPropertyRow { Editor: { } editor }) || (e.KeyModifiers != KeyModifiers.None))
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            editor.Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            editor.Cancel();
            e.Handled = true;
        }
    }

    // Leaving a property's value box commits what it holds; a box whose row the commit rebuilt commits nothing.
    private static void OnPropertyBoxLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is TextBox { IsReadOnly: false, DataContext: AirportEditorPropertyRow { Editor: { } editor } })
        {
            editor.Commit();
        }
    }

    // Esc, then with no modifier T, R, P, S and H pick a mode, and Enter finishes a taxiway drawing.
    private bool HandleModeKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Escape();
            return true;
        }

        if (e.KeyModifiers != KeyModifiers.None)
        {
            return false;
        }

        if (e.Key == Key.Enter)
        {
            if (ViewModel.Mode != AirportEditorMode.DrawTaxiway)
            {
                return false;
            }

            MapCanvas.FinishDrawing();
            return true;
        }

        if (ModeOfKey(e.Key) is not { } mode)
        {
            return false;
        }

        ViewModel.PickMode(mode);
        return true;
    }

    private static AirportEditorMode? ModeOfKey(Key key) =>
        key switch
        {
            Key.T => AirportEditorMode.DrawTaxiway,
            Key.R => AirportEditorMode.DrawRunway,
            Key.P => AirportEditorMode.PlaceParking,
            Key.S => AirportEditorMode.PlaceSpot,
            Key.H => AirportEditorMode.PlaceHelipad,
            _ => null,
        };

    // Esc drops a drag or a drawing in progress; with neither, a draw or place mode returns to Select, and Select clears
    // the selection.
    private void Escape()
    {
        if ((!MapCanvas.CancelGesture()) && (!ViewModel.ReturnToSelect()))
        {
            MapCanvas.ClearSelectionFromUser();
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
        ViewModel.DrawingCancelRequested -= OnDrawingCancelRequested;
        foreach ((ToggleButton button, _) in _modeButtons)
        {
            button.Click -= OnModeButtonClick;
        }

        Activated -= OnActivated;
        RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        PropertyRows.RemoveHandler(LostFocusEvent, OnPropertyBoxLostFocus);

        // The canvas first: its Dispose waits out a frame in progress, so no frame draws once the view model and the
        // cache let go of their images (which are dropped, never disposed: a published SKImage stays valid).
        MapCanvas.Dispose();
        ViewModel.Dispose();
        _tiles.Dispose();
    }
}
