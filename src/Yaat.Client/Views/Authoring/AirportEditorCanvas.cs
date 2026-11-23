using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.Services.Imagery;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// The Airport Editor's map: draws the open map's features through an <see cref="AirportEditorRenderer"/>, pans on a
/// middle drag (the right button is kept for its context menus), zooms with the wheel, and fits itself to the map. In
/// Select mode a left press selects the feature under it (or a vertex of the selected line), Esc clears the selection,
/// the feature under the pointer is highlighted, and a right click selects as a left press does and opens a read-only
/// menu. Nothing here edits the map.
/// </summary>
public sealed class AirportEditorCanvas : MapCanvasBase, IDisposable
{
    /// <summary>The mode the HUD shows.</summary>
    public const string ModeText = "Select";

    // A right press and release farther apart than this is a drag, which opens no menu.
    private const double ClickSlop = 4;

    private static readonly ILogger Log = AppLog.CreateLogger("AirportEditorCanvas");

    private readonly AirportEditorRenderer _renderer = new();

    // The render thread holds the lock for the whole draw, so Dispose cannot free the renderer's Skia objects under it.
    private readonly Lock _renderLock = new();

    private AirportMapDocument? _document;
    private AirportEditorScene _scene = AirportEditorScene.Empty;
    private AirportEditorHit? _selection;
    private AirportEditorHit? _hover;
    private Point? _rightPressAt;
    private TowerCabImage? _image;
    private bool _showImagery = true;
    private int _imageryBrightness = 50;
    private UsgsImageryTileCache? _tileCache;
    private bool _tilesActive;
    private string? _imageryProgressText;

    // Where the pointer last moved over the canvas, or null once it has left.
    private Point? _pointerAt;
    private ContextMenu? _activeContextMenu;
    private Func<string, Task>? _clipboardWriter;
    private bool _fitPending;
    private bool _disposed;

    /// <summary>Raised on the UI thread when a click, a right click or Esc changes <see cref="Selection"/>.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Gets or sets the map drawn; set on the UI thread. A plain property, read into each frame's snapshot.</summary>
    public AirportMapDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value))
            {
                return;
            }

            _document = value;
            _scene = AirportEditorScene.Build(value);
            _hover = null;
            MarkDirty();
        }
    }

    /// <summary>
    /// Gets or sets the selected feature and, for a line, its selected vertex, or null when nothing is selected; set on
    /// the UI thread. Setting it raises no <see cref="SelectionChanged"/>: that is for the user's own clicks.
    /// </summary>
    public AirportEditorHit? Selection
    {
        get => _selection;
        set
        {
            if (_selection == value)
            {
                return;
            }

            _selection = value;
            MarkDirty();
        }
    }

    /// <summary>
    /// Gets or sets the tower-cab image drawn under the map, or null for none; set on the UI thread. The canvas never
    /// disposes it: a frame on the render thread may still be drawing it.
    /// </summary>
    public TowerCabImage? Image
    {
        get => _image;
        set
        {
            if (!ReferenceEquals(_image, value))
            {
                _image = value;
                MarkDirty();
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether <see cref="Image"/> is drawn; set on the UI thread.</summary>
    public bool ShowImagery
    {
        get => _showImagery;
        set
        {
            if (_showImagery != value)
            {
                _showImagery = value;
                MarkDirty();
            }
        }
    }

    /// <summary>Gets or sets the image's opacity, 0 to 100; set on the UI thread.</summary>
    public int ImageryBrightness
    {
        get => _imageryBrightness;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (_imageryBrightness != clamped)
            {
                _imageryBrightness = clamped;
                MarkDirty();
            }
        }
    }

    /// <summary>
    /// Gets or sets the USGS tile cache the frames ask for tiles, or null for none; set on the UI thread. The window owns
    /// it and disposes it after the canvas.
    /// </summary>
    public UsgsImageryTileCache? TileCache
    {
        get => _tileCache;
        set
        {
            if (!ReferenceEquals(_tileCache, value))
            {
                _tileCache = value;
                MarkDirty();
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether the USGS tiles are drawn (the view model's TilesActive); UI thread.</summary>
    public bool TilesActive
    {
        get => _tilesActive;
        set
        {
            if (_tilesActive != value)
            {
                _tilesActive = value;
                MarkDirty();
            }
        }
    }

    /// <summary>Gets or sets the vNAS download's progress line, or null while nothing downloads; set on the UI thread.</summary>
    public string? ImageryProgressText
    {
        get => _imageryProgressText;
        set
        {
            if (_imageryProgressText != value)
            {
                _imageryProgressText = value;
                MarkDirty();
            }
        }
    }

    /// <summary>Gets what the pointer is over, or null when nothing is. Test seam.</summary>
    internal AirportEditorHit? Hover => _hover;

    /// <summary>Gets a value indicating whether the canvas has been disposed. Test seam.</summary>
    internal bool IsDisposed
    {
        get
        {
            lock (_renderLock)
            {
                return _disposed;
            }
        }
    }

    /// <summary>Gets the context menu open now, or null when none is. Test seam.</summary>
    internal ContextMenu? ActiveContextMenu => _activeContextMenu;

    /// <summary>
    /// Gets or sets the writer Copy coordinates hands its text to: the TopLevel clipboard's, until a test replaces it.
    /// Test seam.
    /// </summary>
    internal Func<string, Task> ClipboardWriter
    {
        get => _clipboardWriter ??= WriteClipboardAsync;
        set => _clipboardWriter = value;
    }

    /// <inheritdoc />
    protected override MouseButton PanButton => MouseButton.Middle;

    /// <summary>
    /// Fits the view to the box around every feature of the open map, with the viewport's own margin; a canvas not yet
    /// laid out fits once it has a size. Does nothing when no feature has a position.
    /// </summary>
    public void FitToFeatures()
    {
        if (_scene.Bounds is not { } bounds)
        {
            return;
        }

        if ((Viewport.PixelWidth <= 0) || (Viewport.PixelHeight <= 0))
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;
        Viewport.FitBounds(bounds.MinLat, bounds.MaxLat, bounds.MinLon, bounds.MaxLon);
        OnViewportChanged();
        MarkDirty();
    }

    /// <summary>
    /// Centres the view on the box around a drawn feature's positions; a line is also zoomed so the box fills the view
    /// with the margin <see cref="FitToFeatures"/> keeps, a point keeps the zoom. Does nothing for a feature not drawn.
    /// </summary>
    /// <param name="featureIndex">The feature's index in the map.</param>
    public void ZoomToFeature(int featureIndex)
    {
        AirportEditorShape? shape = _scene.Shapes.FirstOrDefault(s => s.FeatureIndex == featureIndex);
        if ((shape is null) || (AirportEditorBounds.Of(shape.Positions) is not { } box))
        {
            return;
        }

        if (AirportEditorHitTest.IsLine(shape.Kind))
        {
            Viewport.FitBounds(box.MinLat, box.MaxLat, box.MinLon, box.MaxLon);
        }
        else
        {
            Viewport.CenterLat = (box.MinLat + box.MaxLat) / 2;
            Viewport.CenterLon = (box.MinLon + box.MaxLon) / 2;
        }

        OnViewportChanged();
        MarkDirty();
    }

    /// <summary>Frees the renderer's Skia objects; the canvas draws nothing afterwards.</summary>
    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderer.Dispose();
        }
    }

    /// <summary>Draws the current frame into a bitmap the canvas's size. Test seam.</summary>
    /// <returns>The frame; the caller disposes it.</returns>
    internal SKBitmap RenderForTesting()
    {
        var bitmap = new SKBitmap(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas, Viewport.Clone(), CreateRenderSnapshot());
        canvas.Flush();
        return bitmap;
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fitPending)
        {
            FitToFeatures();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        MouseButton button = e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton();
        if (button == MouseButton.Left)
        {
            CloseContextMenu();
            Focus();
            SelectAt(e.GetPosition(this));
            e.Handled = true;
        }
        else if (button == MouseButton.Right)
        {
            Focus();
            _rightPressAt = e.GetPosition(this);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if ((e.InitialPressMouseButton != MouseButton.Right) || (_rightPressAt is not { } pressedAt))
        {
            return;
        }

        _rightPressAt = null;
        Vector moved = e.GetPosition(this) - pressedAt;
        if ((Math.Abs(moved.X) <= ClickSlop) && (Math.Abs(moved.Y) <= ClickSlop))
        {
            ShowContextMenu(BuildContextMenu(pressedAt));
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Handled)
        {
            return;
        }

        Point at = e.GetPosition(this);
        _pointerAt = at;
        SetHover(AirportEditorHitTest.Find(_scene, Viewport, (float)at.X, (float)at.Y, _selection?.FeatureIndex));
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerAt = null;
        SetHover(null);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((!e.Handled) && (e.Key == Key.Escape))
        {
            SetSelectionFromUser(null);
            e.Handled = true;
        }
    }

    /// <summary>Builds the frame the next render would draw, asking the tile cache as a render does. Test seam.</summary>
    /// <returns>The frame, or null once the canvas is disposed.</returns>
    internal AirportEditorRenderSnapshot? SnapshotForTesting() => CreateRenderSnapshot() as AirportEditorRenderSnapshot;

    /// <inheritdoc />
    protected override object? CreateRenderSnapshot()
    {
        if (_disposed)
        {
            return null;
        }

        (ImmutableArray<ImageryTile> tiles, string? status) = BuildImagery();
        return new AirportEditorRenderSnapshot(_scene, ModeText, _selection?.FeatureIndex, _selection?.VertexIndex, _hover)
        {
            Image = _image,
            ShowImagery = _showImagery,
            ImageryBrightness = _imageryBrightness,
            Tiles = tiles,
            ShowCredit = !tiles.IsEmpty,
            StatusLine = status,
        };
    }

    /// <inheritdoc />
    protected override void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot) => Draw(canvas, viewport, snapshot);

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    // The feature under the point, with the current selection's vertices first; a miss clears the selection.
    private void SelectAt(Point at) =>
        SetSelectionFromUser(AirportEditorHitTest.Find(_scene, Viewport, (float)at.X, (float)at.Y, _selection?.FeatureIndex));

    private void SetSelectionFromUser(AirportEditorHit? selection)
    {
        if (_selection == selection)
        {
            return;
        }

        Selection = selection;
        SelectionChanged?.Invoke(this, EventArgs.Empty);

        // The hover depends on the selection (a selected line's vertices are hit first), so it is found again where the
        // pointer last was rather than waiting for it to move.
        if (_pointerAt is { } at)
        {
            SetHover(AirportEditorHitTest.Find(_scene, Viewport, (float)at.X, (float)at.Y, _selection?.FeatureIndex));
        }
    }

    private void SetHover(AirportEditorHit? hover)
    {
        if (_hover == hover)
        {
            return;
        }

        _hover = hover;
        MarkDirty();
    }

    // Selects as a left press at the point does, then offers the read-only items for what is there.
    private ContextMenu BuildContextMenu(Point at)
    {
        SelectAt(at);
        (double lat, double lon) = Viewport.ScreenToLatLon((float)at.X, (float)at.Y);
        string coordinates = string.Create(CultureInfo.InvariantCulture, $"{lat:F6}, {lon:F6}");
        var menu = new ContextMenu();
        if (_selection is { } hit)
        {
            menu.Items.Add(
                CreateMenuItem(
                    "Zoom to feature",
                    () =>
                    {
                        ZoomToFeature(hit.FeatureIndex);
                        return Task.CompletedTask;
                    }
                )
            );
        }

        menu.Items.Add(CreateMenuItem("Copy coordinates", () => CopyCoordinatesAsync(coordinates)));
        menu.Items.Add(new Separator());
        menu.Items.Add(
            CreateMenuItem(
                "Fit to airport",
                () =>
                {
                    FitToFeatures();
                    return Task.CompletedTask;
                }
            )
        );
        return menu;
    }

    private void ShowContextMenu(ContextMenu menu)
    {
        CloseContextMenu();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (_activeContextMenu == menu)
            {
                _activeContextMenu = null;
            }
        };
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(this);
    }

    private void CloseContextMenu()
    {
        _activeContextMenu?.Close();
        _activeContextMenu = null;
    }

    private Task WriteClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning("Copy coordinates failed: the canvas has no clipboard");
            }

            return Task.CompletedTask;
        }

        return clipboard.SetTextAsync(text);
    }

    // The clipboard can fail (lost, locked, or a platform that refuses it); the item's async click handler lets nothing escape.
    private async Task CopyCoordinatesAsync(string coordinates)
    {
        try
        {
            await ClipboardWriter(coordinates);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Copy coordinates failed");
        }
    }

    // On the UI thread: the tiles the view wants at its zoom, from the cache (which starts fetching the missing ones), and
    // the status line. No tile is asked while imagery is off, the tiles are inactive or a tower-cab image is shown.
    private (ImmutableArray<ImageryTile> Tiles, string? Status) BuildImagery()
    {
        if (!_showImagery)
        {
            return ([], null);
        }

        bool active = (_tilesActive) && (_tileCache is not null) && (_image is null);
        double lat = Viewport.CenterLat;
        int? zoom = active ? ImageryZoom.Choose(lat, Viewport.Zoom) : null;
        bool covered = (active) && UsgsCoverage.Contains(lat, Viewport.CenterLon);
        if ((zoom is not int z) || (!covered) || (_tileCache is not { } cache))
        {
            return ([], AirportEditorImageryStatus.Line(_imageryProgressText, active, zoom, covered, ImageryTileState.AllShown));
        }

        IReadOnlyList<TileKey> wanted = WantedTiles(z);
        ImmutableArray<ImageryTile> tiles = [.. cache.GetVisible(wanted)];
        return (tiles, AirportEditorImageryStatus.Line(_imageryProgressText, active, zoom, covered, cache.State(wanted)));
    }

    // The tiles covering the box around the view's four corners (a rotated view's box holds all of it).
    private IReadOnlyList<TileKey> WantedTiles(int z)
    {
        float w = Viewport.PixelWidth;
        float h = Viewport.PixelHeight;
        (double Lat, double Lon)[] corners =
        [
            Viewport.ScreenToLatLon(0, 0),
            Viewport.ScreenToLatLon(w, 0),
            Viewport.ScreenToLatLon(0, h),
            Viewport.ScreenToLatLon(w, h),
        ];
        return WebMercatorTile.TilesCovering(corners.Min(c => c.Lat), corners.Min(c => c.Lon), corners.Max(c => c.Lat), corners.Max(c => c.Lon), z);
    }

    private void Draw(SKCanvas canvas, MapViewport viewport, object? snapshot)
    {
        if (snapshot is not AirportEditorRenderSnapshot frame)
        {
            return;
        }

        lock (_renderLock)
        {
            if (!_disposed)
            {
                _renderer.Render(canvas, viewport, frame);
            }
        }
    }
}

/// <summary>The Airport Editor's imagery status line: one line saying why imagery is missing, or what is downloading.</summary>
internal static class AirportEditorImageryStatus
{
    /// <summary>The line while the view is zoomed out past the tiles' lowest zoom.</summary>
    public const string ZoomIn = "Zoom in to see imagery";

    /// <summary>The line while the view's centre is outside the USGS coverage.</summary>
    public const string OutsideUs = "No imagery outside the US";

    /// <summary>The line while a wanted tile failed for a network reason and has no disk copy.</summary>
    public const string Offline = "Imagery offline: showing cached tiles only";

    /// <summary>The first line that applies: the vNAS download, then, with tiles active, zoomed out, outside the US, offline.</summary>
    /// <param name="progress">The vNAS download's progress line, or null.</param>
    /// <param name="tilesActive">Whether the USGS tiles are active.</param>
    /// <param name="zoom">The tile zoom for the view, or null when it is zoomed out too far.</param>
    /// <param name="covered">Whether the view's centre is inside the USGS coverage.</param>
    /// <param name="state">The wanted tiles' state.</param>
    /// <returns>The line, or null for none.</returns>
    public static string? Line(string? progress, bool tilesActive, int? zoom, bool covered, ImageryTileState state)
    {
        if (progress is not null)
        {
            return progress;
        }

        if (!tilesActive)
        {
            return null;
        }

        if (zoom is null)
        {
            return ZoomIn;
        }

        if (!covered)
        {
            return OutsideUs;
        }

        return state == ImageryTileState.Offline ? Offline : null;
    }
}
