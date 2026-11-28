using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One point of the route being composed: the saved point, where the author placed it, and what it resolves to now.
/// </summary>
/// <param name="Point">The point as it is saved.</param>
/// <param name="Placed">Where the author placed it (a snapped fix is placed on the fix).</param>
/// <param name="AnchorPosition">An FRD point's anchor fix; null for any other point.</param>
/// <param name="Result">What resolving the point gave: its position, its nav-path text and any warning, or the failure.</param>
public sealed record RouteComposerPoint(RoutePoint Point, LatLon Placed, LatLon? AnchorPosition, RoutePointResult Result)
{
    /// <summary>Gets where its nav-path text resolves; null when it cannot be resolved.</summary>
    public LatLon? Position => Result.Position;

    /// <summary>Gets its nav-path text; null when it cannot be resolved.</summary>
    public string? Text => Result.Text;

    /// <summary>Gets what the author should know about its text; null when there is nothing.</summary>
    public string? Warning => Result.Warning;

    /// <summary>Gets a value indicating whether the point resolves more than 1 NM from where it was placed.</summary>
    public bool IsDisplaced => (Position is { } position) && (GeoMath.DistanceNm(Placed, position) > RoutePointResolver.DisplacementWarningNm);
}

/// <summary>One video map's layer in the composer: drawn while its check box is on and its map is loaded.</summary>
public sealed partial class RouteComposerMapLayer : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Initializes a new instance of the <see cref="RouteComposerMapLayer"/> class, unticked and not yet loaded.</summary>
    /// <param name="map">The map, as the ARTCC's config lists it.</param>
    public RouteComposerMapLayer(RouteComposerMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        Name = map.Name;
        MapId = map.MapId;
    }

    /// <summary>Gets the map's name.</summary>
    public string Name { get; }

    /// <summary>Gets the map's id in the ARTCC's config.</summary>
    public string MapId { get; }

    /// <summary>Gets or sets the map's lines; null until it is loaded, and kept once loaded even when unticked.</summary>
    public VideoMapData? Data { get; internal set; }
}

/// <summary>
/// The route composer: a named route of points placed on the map, saved in the per-user <see cref="RouteLibrary"/> tagged
/// with the composer's ARTCC. A click snapped to a fix adds that fix (<see cref="RoutePointResolver.PickFix"/>), any other
/// click is anchored to an FRD (<see cref="RoutePointResolver.Anchor"/>) or refused with the reason; a click inserts after
/// the selected point, or at the end when none is. Each add, move and delete is one undo step. With no navigation database
/// the status says so and nothing is added.
/// </summary>
public sealed partial class RouteComposerViewModel : ObservableObject, IDisposable
{
    /// <summary>The status while no navigation database is loaded.</summary>
    public const string NavdataLoadingText = "Navdata is loading…";

    /// <summary>The status once navdata is loaded.</summary>
    public const string ReadyText = "Click the map to add a point. Middle-drag pans, the wheel zooms.";

    /// <summary>What Save says for a blank name.</summary>
    public const string NameRequiredText = "A route needs a name.";

    /// <summary>What Save says for a colour that is not #RRGGBB.</summary>
    public const string ColorInvalidText = "The colour must be #RRGGBB.";

    /// <summary>The colour a route is drawn in when it names none.</summary>
    public const string DefaultColor = "#4FC3F7";

    private static readonly ILogger Log = AppLog.CreateLogger<RouteComposerViewModel>();

    private readonly RouteComposerContext _context;
    private readonly RouteLibrary _library;
    private readonly Func<NavigationDatabase?> _navDb;
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private ImmutableArray<RouteComposerPoint> _points = [];
    private bool _loadingRoute;
    private bool _corruptShown;
    private string? _loadedArtccId;
    private readonly CancellationTokenSource _mapLoads = new();
    private bool _disposed;

    [ObservableProperty]
    private int? _selectedIndex;

    [ObservableProperty]
    private SavedRoute? _selectedRoute;

    [ObservableProperty]
    private bool _showAllArtccs;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _color = DefaultColor;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _navPathText = "";

    [ObservableProperty]
    private string _messageText = "";

    /// <summary>Initializes a new instance of the <see cref="RouteComposerViewModel"/> class with an empty route.</summary>
    /// <param name="context">The ARTCC, fix resolution and maps the composer opens over.</param>
    /// <param name="library">The per-user route library.</param>
    /// <param name="navDb">
    /// Reads the navigation database (<see cref="NavigationDatabase.InstanceOrNull"/>), null while it loads; read at every use.
    /// </param>
    public RouteComposerViewModel(RouteComposerContext context, RouteLibrary library, Func<NavigationDatabase?> navDb)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(navDb);
        _context = context;
        _library = library;
        _navDb = navDb;
        _statusText = (navDb() is null) ? NavdataLoadingText : ReadyText;
        RefreshRoutes();
    }

    /// <summary>Raised when a route from the library has been loaded into the composer.</summary>
    public event EventHandler? RouteLoaded;

    /// <summary>Gets the ARTCC the composer composes in.</summary>
    public string ArtccId => _context.ArtccId;

    /// <summary>Gets the window title.</summary>
    public string Title => $"Route composer — {ArtccId}";

    /// <summary>Gets the navigation database points resolve on now, or null while it loads.</summary>
    public NavigationDatabase? NavDb => _navDb();

    /// <summary>Gets the route's points in flying order.</summary>
    public IReadOnlyList<RouteComposerPoint> Points => _points;

    /// <summary>Gets the id of the library route being edited, or null for a new route.</summary>
    public string? LoadedRouteId { get; private set; }

    /// <summary>Gets the library's routes: the composer's ARTCC only unless <see cref="ShowAllArtccs"/> is on.</summary>
    public ObservableCollection<SavedRoute> Routes { get; } = [];

    /// <summary>Gets the ARTCC's video map layers, once <see cref="LoadMapsAsync"/> has listed them; each loads when it is ticked.</summary>
    public ObservableCollection<RouteComposerMapLayer> Maps { get; } = [];

    /// <summary>Gets the load of the layer ticked last, for tests to await; completed when no layer is loading.</summary>
    internal Task MapLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Adds a point after the selected one (at the end when none is) and selects it.</summary>
    /// <param name="click">Where the author clicked.</param>
    /// <param name="snapFix">The ident of the drawn fix the click snapped to, or null for none.</param>
    /// <returns>True when a point was added; false when navdata is missing or the click cannot be written.</returns>
    public bool AddAt(LatLon click, string? snapFix)
    {
        if (ResolveClick(click, snapFix) is not { } point)
        {
            return false;
        }

        int index = ((SelectedIndex is int selected) && (selected < _points.Length)) ? selected + 1 : _points.Length;
        Apply(_points.Insert(index, point), index);
        return true;
    }

    /// <summary>Moves a point and re-resolves it there, as a click would; a point that cannot be written stays put.</summary>
    /// <param name="index">The point's index.</param>
    /// <param name="to">Where it was dropped.</param>
    /// <param name="snapFix">The ident of the drawn fix the drop snapped to, or null for none.</param>
    /// <returns>True when the point moved.</returns>
    public bool MovePoint(int index, LatLon to, string? snapFix)
    {
        if ((index < 0) || (index >= _points.Length) || (ResolveClick(to, snapFix) is not { } point))
        {
            return false;
        }

        Apply(_points.SetItem(index, point), index);
        return true;
    }

    /// <summary>Deletes a point; nothing is selected afterwards.</summary>
    /// <param name="index">The point's index.</param>
    /// <returns>True when a point was deleted.</returns>
    public bool DeletePoint(int index)
    {
        if ((index < 0) || (index >= _points.Length))
        {
            return false;
        }

        Apply(_points.RemoveAt(index), null);
        return true;
    }

    /// <summary>
    /// Lists the ARTCC's video maps into <see cref="Maps"/> as layers, none downloaded, loaded or drawn until it is ticked;
    /// a failure is shown in the message line.
    /// </summary>
    /// <param name="ct">Cancels the listing.</param>
    /// <returns>The listing.</returns>
    public async Task LoadMapsAsync(CancellationToken ct)
    {
        Func<CancellationToken, Task<IReadOnlyList<RouteComposerMap>>> load = _context.LoadMaps;
        IReadOnlyList<RouteComposerMap> maps;
        try
        {
            maps = await Task.Run(() => load(ct), ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Route composer: listing the video maps of {ArtccId} failed", ArtccId);
            MessageText = $"Could not load the video maps of {ArtccId}: {ex.Message}";
            return;
        }

        Maps.Clear();
        foreach (RouteComposerMap map in maps)
        {
            var layer = new RouteComposerMapLayer(map);
            layer.PropertyChanged += OnLayerChanged;
            Maps.Add(layer);
        }
    }

    /// <summary>Stops the map loads still in flight; the composer's window calls it as it closes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mapLoads.Cancel();
        _mapLoads.Dispose();
    }

    // Ticking a layer draws it at once when its map is loaded, and loads it first when it is not; a loaded layer is kept.
    private void OnLayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RouteComposerMapLayer layer)
        {
            return;
        }

        OnPropertyChanged(nameof(Maps));
        bool ticking =
            (!_disposed)
            && (e.PropertyName == nameof(RouteComposerMapLayer.IsEnabled))
            && (layer.IsEnabled)
            && (layer.Data is null)
            && (!layer.IsLoading);
        if (ticking)
        {
            MapLoadTask = LoadLayerAsync(layer);
        }
    }

    // One map's load off the UI thread: a map that cannot be fetched unties the layer and says why.
    private async Task LoadLayerAsync(RouteComposerMapLayer layer)
    {
        layer.IsLoading = true;
        VideoMapData? data;
        try
        {
            CancellationToken ct = _mapLoads.Token;
            data = await Task.Run(() => _context.LoadMap(layer.MapId, ct), ct);
        }
        catch (OperationCanceledException) when (_mapLoads.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Route composer: loading the video map {MapId} failed", layer.MapId);
            MessageText = $"Could not load the video map {layer.Name}: {ex.Message}";
            layer.IsEnabled = false;
            return;
        }
        finally
        {
            layer.IsLoading = false;
        }

        if (_disposed)
        {
            return;
        }

        if (data is null)
        {
            MessageText = $"The video map {layer.Name} is not cached; it needs one online open.";
            layer.IsEnabled = false;
            return;
        }

        layer.Data = data;
        OnPropertyChanged(nameof(Maps));
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedIndex is int index)
        {
            DeletePoint(index);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Step(_undo, _redo);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Step(_redo, _undo);

    private bool CanUndo() => _undo.Count > 0;

    private bool CanRedo() => _redo.Count > 0;

    [RelayCommand]
    private void New()
    {
        LoadedRouteId = null;
        _loadedArtccId = null;
        Name = "";
        Color = DefaultColor;
        SetRoute([], null);
        SelectWithoutLoading(null);
    }

    [RelayCommand]
    private void Save()
    {
        string name = Name.Trim();
        string color = Color.Trim();
        if (name.Length == 0)
        {
            MessageText = NameRequiredText;
            return;
        }

        if (!ColorPattern().IsMatch(color))
        {
            MessageText = ColorInvalidText;
            return;
        }

        string id = LoadedRouteId ?? SavedRoute.NewId();
        string artccId = _loadedArtccId ?? ArtccId;
        if (
            !TryLibrary(
                "save",
                name,
                () => _library.Save(new SavedRoute(id, name, color.ToUpperInvariant(), artccId, [.. _points.Select(p => p.Point)]))
            )
        )
        {
            return;
        }

        LoadedRouteId = id;
        _loadedArtccId = artccId;
        Name = name;
        RefreshRoutes();
        SelectWithoutLoading(Routes.FirstOrDefault(r => r.Id == id));
        RoutePointResult path = RoutePointResolver.NavPathText([.. _points.Select(p => p.Point)], _context.Fixes, NavDb);
        MessageText = (path.Failure is { } failure) ? $"Saved {name}; {failure}" : $"Saved {name}.";
    }

    [RelayCommand]
    private void Delete()
    {
        if ((LoadedRouteId is { } id) && TryLibrary("delete", Name.Trim(), () => _library.Delete(id)))
        {
            New();
            RefreshRoutes();
        }
    }

    partial void OnShowAllArtccsChanged(bool value) => RefreshRoutes();

    partial void OnSelectedRouteChanged(SavedRoute? value)
    {
        if (_loadingRoute || (value is null))
        {
            return;
        }

        LoadedRouteId = value.Id;
        _loadedArtccId = value.ArtccId;
        Name = value.Name;
        Color = value.Color ?? DefaultColor;
        SetRoute([.. value.Points.Select(FromSaved)], null);
        RouteLoaded?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshRoutes()
    {
        RouteLibraryListing listing = _library.List(ShowAllArtccs ? null : ArtccId);
        ReportCorrupt(listing.CorruptFiles);
        string? selectedId = SelectedRoute?.Id;
        _loadingRoute = true;
        try
        {
            Routes.Clear();
            foreach (SavedRoute route in listing.Routes)
            {
                Routes.Add(route);
            }

            SelectedRoute = Routes.FirstOrDefault(r => r.Id == selectedId);
        }
        finally
        {
            _loadingRoute = false;
        }
    }

    // A library write that fails (a locked file, a folder it may not write) leaves the composer as it was and says why.
    private bool TryLibrary(string verb, string name, Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Route composer: could not {Verb} route {Name}", verb, name);
            MessageText = $"Couldn't {verb} {name}: {ex.Message}";
            return false;
        }
    }

    private void ReportCorrupt(IReadOnlyList<string> corruptFiles)
    {
        if (_corruptShown || (corruptFiles.Count == 0))
        {
            return;
        }

        _corruptShown = true;
        string names = string.Join(", ", corruptFiles);
        Log.LogWarning("Route composer: could not read route files {Files}", names);
        MessageText = $"Couldn't read route files: {names}";
    }

    private void SelectWithoutLoading(SavedRoute? route)
    {
        _loadingRoute = true;
        try
        {
            SelectedRoute = route;
        }
        finally
        {
            _loadingRoute = false;
        }
    }

    // A snapped click picks the fix's instance nearest the click and is placed on it; any other click is anchored.
    private RouteComposerPoint? ResolveClick(LatLon click, string? snapFix)
    {
        if (ReadNavDb() is not { } navDb)
        {
            return null;
        }

        RoutePointResult result =
            (snapFix is null) ? RoutePointResolver.Anchor(click, navDb) : RoutePointResolver.PickFix(snapFix, click, _context.Fixes, navDb);
        if (Written(result) is not { } written)
        {
            return null;
        }

        RoutePoint point = (snapFix is null) ? PointOf(written.Text) : RoutePoint.AtFix(written.Text);
        LatLon placed = (snapFix is null) ? click : written.Position;
        return new RouteComposerPoint(point, placed, AnchorOf(point, navDb), result);
    }

    // The navigation database now, with the status line saying whether it has loaded.
    private NavigationDatabase? ReadNavDb()
    {
        NavigationDatabase? navDb = NavDb;
        StatusText = (navDb is null) ? NavdataLoadingText : ReadyText;
        return navDb;
    }

    // The text and position of a result that can be written; a failure goes to the message line.
    private (string Text, LatLon Position)? Written(RoutePointResult result)
    {
        if ((result.Failure is null) && (result.Text is { } text) && (result.Position is { } position))
        {
            return (text, position);
        }

        MessageText = result.Failure ?? "This point cannot be written.";
        return null;
    }

    // Anchor writes an FRD, or a bare fix when the click sits on one.
    private static RoutePoint PointOf(string text)
    {
        if (FrdResolver.ParseFrd(text) is { Radial: int radial, Distance: int distance } frd)
        {
            return RoutePoint.AtFrd(frd.Fix, (radial == 0) ? 360 : radial, distance);
        }

        return RoutePoint.AtFix(text);
    }

    private LatLon? AnchorOf(RoutePoint point, NavigationDatabase navDb)
    {
        if ((point.Kind != RoutePointKind.Frd) || (point.Fix is null))
        {
            return null;
        }

        return (navDb.GetFixPosition(point.Fix, _context.Fixes) is { } p) ? new LatLon(p.Lat, p.Lon) : null;
    }

    // A saved point is placed where it resolves, or at its own coordinate; one that cannot be resolved is kept undrawn.
    private RouteComposerPoint FromSaved(RoutePoint point)
    {
        NavigationDatabase? navDb = ReadNavDb();
        RoutePointResult result = RoutePointResolver.Resolve(point, _context.Fixes, navDb);
        LatLon? own = ((point.Lat is { } lat) && (point.Lon is { } lon)) ? new LatLon(lat, lon) : null;
        LatLon placed = own ?? result.Position ?? default;
        LatLon? anchor = (navDb is not null) ? AnchorOf(point, navDb) : null;
        return new RouteComposerPoint(point, placed, anchor, result);
    }

    private void Apply(ImmutableArray<RouteComposerPoint> points, int? selected)
    {
        _undo.Push(new Edit(_points, SelectedIndex));
        _redo.Clear();
        SetPoints(points, selected);
    }

    private void Step(Stack<Edit> from, Stack<Edit> to)
    {
        if (from.TryPop(out Edit? edit))
        {
            to.Push(new Edit(_points, SelectedIndex));
            SetPoints(edit.Points, edit.Selected);
        }
    }

    // A whole new route (a load or New) starts a fresh undo history.
    private void SetRoute(ImmutableArray<RouteComposerPoint> points, int? selected)
    {
        _undo.Clear();
        _redo.Clear();
        SetPoints(points, selected);
    }

    private void SetPoints(ImmutableArray<RouteComposerPoint> points, int? selected)
    {
        _points = points;
        SelectedIndex = selected;
        NavigationDatabase? navDb = NavDb;
        RoutePointResult path = RoutePointResolver.NavPathText([.. points.Select(p => p.Point)], _context.Fixes, navDb);
        NavPathText = path.Text ?? "";
        MessageText = (navDb is null) ? "" : (path.Failure ?? Warnings(points, path.Warning));
        OnPropertyChanged(nameof(Points));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // Each point's own warning by 1-based number, then the path's. A coordinate point's warning is already in the path's,
    // which anchors it again.
    private static string Warnings(ImmutableArray<RouteComposerPoint> points, string? pathWarning)
    {
        List<string> warnings = [];
        for (int i = 0; i < points.Length; i++)
        {
            if ((points[i].Point.Kind != RoutePointKind.LatLon) && (points[i].Warning is { } warning))
            {
                warnings.Add($"Point {i + 1}: {warning}");
            }
        }

        if (pathWarning is not null)
        {
            warnings.Add(pathWarning);
        }

        return string.Join("; ", warnings);
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    private sealed record Edit(ImmutableArray<RouteComposerPoint> Points, int? Selected);
}
