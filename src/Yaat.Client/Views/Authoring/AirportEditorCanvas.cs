using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;

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

    /// <summary>Gets what the pointer is over, or null when nothing is. Test seam.</summary>
    internal AirportEditorHit? Hover => _hover;

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

    /// <inheritdoc />
    protected override object? CreateRenderSnapshot() =>
        _disposed ? null : new AirportEditorRenderSnapshot(_scene, ModeText, _selection?.FeatureIndex, _selection?.VertexIndex, _hover);

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
