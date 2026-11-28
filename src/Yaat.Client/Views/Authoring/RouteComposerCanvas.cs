using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Authoring;

/// <summary>What the render thread draws one route composer frame from. It has no imagery: the composer draws video maps only.</summary>
/// <param name="Scene">The route points (with anchors and ghosts) and the drawn fixes.</param>
/// <param name="Selected">The selected point's index, or null.</param>
/// <param name="Hover">What the pointer is over, or null.</param>
/// <param name="Maps">The enabled video maps, in the order their check boxes list them.</param>
/// <param name="RouteColor">The colour the route is drawn in.</param>
public sealed record RouteComposerRenderSnapshot(
    RouteComposerScene Scene,
    int? Selected,
    RouteComposerHit? Hover,
    ImmutableArray<VideoMapData> Maps,
    SKColor RouteColor
);

/// <summary>
/// The route composer's map: the enabled video maps, the fixes near the view (while it spans 60 NM or less), and the route.
/// A left click adds a point after the selected one, snapped to a drawn fix within 10 px; a left drag on a point moves it
/// and re-resolves it on release; a right click on a point opens Delete point and Insert after; Delete removes the selected
/// point; Ctrl+Z and Ctrl+Y undo and redo; the middle button pans and the wheel zooms. Every frame draws only from its
/// snapshot, and the draw and the hit test read the same <see cref="RouteComposerScene.Layout"/>.
/// </summary>
public sealed class RouteComposerCanvas : MapCanvasBase, IDisposable
{
    private const float PointRadius = 3f;
    private const float SelectedRadius = 7f;
    private const float HoverRadius = 6f;
    private const float FixSize = 4f;
    private const float LabelGap = 6f;
    private const double ClickSlop = 3.0;

    private static readonly SKColor DefaultRouteColor = SKColor.Parse(RouteComposerViewModel.DefaultColor);

    // Shared by every canvas and never freed, so no paint can outlive it.
    private static readonly SKPathEffect AnchorDash = SKPathEffect.CreateDash([4f, 3f], 0f);

    private static readonly IReadOnlyDictionary<string, string> NoBrightness = new Dictionary<string, string>();

    // The render thread holds the lock for the whole draw, so Dispose cannot free the paints under it.
    private readonly Lock _renderLock = new();
    private readonly VideoMapRenderer _maps = new();
    private readonly SKPaint _routePaint = Stroke(DefaultRouteColor, 2f);
    private readonly SKPaint _pointPaint = Fill(DefaultRouteColor);
    private readonly SKPaint _ghostPaint = Stroke(DefaultRouteColor, 1.5f);
    private readonly SKPaint _anchorPaint = Dashed(new SKColor(0xB0, 0xBE, 0xC5), 1f);
    private readonly SKPaint _fixPaint = Stroke(new SKColor(0x9E, 0x9E, 0x9E), 1f);
    private readonly SKPaint _fixLabelPaint = Fill(new SKColor(0xBD, 0xBD, 0xBD));
    private readonly SKPaint _hoverPaint = Stroke(SKColors.White, 1.5f);
    private readonly SKPaint _selectedPaint = Stroke(new SKColor(0xFF, 0xC1, 0x07), 2f);
    private readonly SKFont _font = new(SKTypeface.Default, 11);

    private RouteComposerViewModel? _viewModel;
    private ImmutableArray<RouteComposerFix> _fixes = [];
    private RouteComposerHit? _hover;
    private PointDrag? _drag;
    private (int Index, Point At)? _rightPress;
    private ContextMenu? _activeContextMenu;
    private bool _disposed;

    /// <summary>Gets or sets the composer the canvas draws and edits; set on the UI thread.</summary>
    public RouteComposerViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel?.PropertyChanged -= OnViewModelChanged;

            _viewModel = value;
            _viewModel?.PropertyChanged += OnViewModelChanged;

            RefreshFixes();
        }
    }

    /// <summary>Gets the context menu open now, or null when none is. Test seam.</summary>
    internal ContextMenu? ActiveContextMenu => _activeContextMenu;

    /// <inheritdoc />
    protected override MouseButton PanButton => MouseButton.Middle;

    /// <summary>Centres the view on a position with the view's wider side spanning the given NM.</summary>
    /// <param name="center">The new centre.</param>
    /// <param name="spanNm">How far the wider side spans.</param>
    public void CenterOn(LatLon center, double spanNm)
    {
        Viewport.CenterLat = center.Lat;
        Viewport.CenterLon = center.Lon;
        double span = RouteComposerScene.SpanNm(Viewport);
        if ((span > 0.0) && (spanNm > 0.0))
        {
            Viewport.Zoom *= span / spanNm;
        }

        OnViewportChanged();
        MarkDirty();
    }

    /// <summary>Fits the view to the route's points, else to the enabled maps; does nothing when there are neither.</summary>
    public void FitToContent()
    {
        List<(double Lat, double Lon)> positions = [.. ContentPositions()];
        if (positions.Count == 0)
        {
            return;
        }

        double minLat = positions.Min(p => p.Lat);
        double maxLat = positions.Max(p => p.Lat);
        double minLon = positions.Min(p => p.Lon);
        double maxLon = positions.Max(p => p.Lon);
        if ((maxLat - minLat < 1e-6) && (maxLon - minLon < 1e-6))
        {
            CenterOn(new LatLon(minLat, minLon), 20.0);
            return;
        }

        Viewport.FitBounds(minLat, maxLat, minLon, maxLon);
        OnViewportChanged();
        MarkDirty();
    }

    /// <summary>Frees the paints and the map renderer; the canvas draws nothing afterwards.</summary>
    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ViewModel = null;
            foreach (
                SKPaint paint in (SKPaint[])
                    [_routePaint, _pointPaint, _ghostPaint, _anchorPaint, _fixPaint, _fixLabelPaint, _hoverPaint, _selectedPaint]
            )
            {
                paint.Dispose();
            }

            _font.Dispose();
            _maps.Dispose();
        }
    }

    /// <summary>Builds the frame the next render would draw. Test seam.</summary>
    /// <returns>The frame, or null once the canvas is disposed.</returns>
    internal RouteComposerRenderSnapshot? SnapshotForTesting() => CreateRenderSnapshot() as RouteComposerRenderSnapshot;

    /// <summary>Draws a given frame into a bitmap the canvas's size, from that frame alone. Test seam.</summary>
    /// <param name="snapshot">The frame to draw.</param>
    /// <returns>The frame; the caller disposes it.</returns>
    internal SKBitmap RenderForTesting(object? snapshot)
    {
        var bitmap = new SKBitmap(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas, Viewport.Clone(), snapshot);
        canvas.Flush();
        return bitmap;
    }

    /// <inheritdoc />
    protected override void OnViewportChanged() => RefreshFixes();

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        MouseButton button = e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton();
        if ((button is not (MouseButton.Left or MouseButton.Right)) || (_viewModel is not { } vm))
        {
            return;
        }

        Focus();
        e.Handled = true;
        Point at = e.GetPosition(this);
        RouteComposerHit? hit = RouteComposerHitTest.Find(CurrentScene(), Viewport, (float)at.X, (float)at.Y);
        if (button == MouseButton.Right)
        {
            _rightPress = (hit?.PointIndex is int pressed) ? (pressed, at) : null;
            return;
        }

        CloseContextMenu();
        if (hit?.PointIndex is int index)
        {
            _drag = new PointDrag(index, at, false, ToLatLon(at));
            return;
        }

        vm.AddAt(ToLatLon(at), hit?.Fix?.Name);
        MarkDirty();
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
        if (_drag is { } drag)
        {
            Vector moved = at - drag.Start;
            bool moving = drag.Moving || (Math.Abs(moved.X) > ClickSlop) || (Math.Abs(moved.Y) > ClickSlop);
            _drag = drag with { Moving = moving, At = ToLatLon(at) };
            MarkDirty();
            e.Handled = true;
            return;
        }

        SetHover(RouteComposerHitTest.Find(CurrentScene(), Viewport, (float)at.X, (float)at.Y));
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Point at = e.GetPosition(this);
        if ((e.InitialPressMouseButton == MouseButton.Left) && (_drag is { } drag))
        {
            _drag = null;
            EndDrag(drag, at);
            e.Handled = true;
        }
        else if ((e.InitialPressMouseButton == MouseButton.Right) && (_rightPress is { } press))
        {
            _rightPress = null;
            Vector moved = at - press.At;
            if ((Math.Abs(moved.X) <= ClickSlop) && (Math.Abs(moved.Y) <= ClickSlop))
            {
                ShowContextMenu(BuildContextMenu(press.Index));
                e.Handled = true;
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        // A drag whose capture went without its release (another window took the pointer) is dropped, unapplied.
        _drag = null;
        MarkDirty();
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_viewModel is not { } vm)
        {
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.Delete => vm.DeleteSelectedCommand,
            Key.Z when ctrl => vm.UndoCommand,
            Key.Y when ctrl => vm.RedoCommand,
            _ => null,
        };
        if (command is not null)
        {
            command.Execute(null);
            e.Handled = true;
            MarkDirty();
        }
    }

    /// <inheritdoc />
    protected override object? CreateRenderSnapshot()
    {
        if (_disposed)
        {
            return null;
        }

        RouteComposerViewModel? vm = _viewModel;
        ImmutableArray<VideoMapData> maps = (vm is null) ? [] : [.. vm.Maps.Where(m => m.IsEnabled).Select(m => m.Data)];
        SKColor color = ((vm is not null) && SKColor.TryParse(vm.Color, out SKColor parsed)) ? parsed : DefaultRouteColor;
        return new RouteComposerRenderSnapshot(CurrentScene(), vm?.SelectedIndex, _hover, maps, color);
    }

    /// <inheritdoc />
    protected override void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot) => Draw(canvas, viewport, snapshot);

    private static SKPaint Stroke(SKColor color, float width) =>
        new()
        {
            Color = color,
            StrokeWidth = width,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };

    private static SKPaint Dashed(SKColor color, float width)
    {
        SKPaint paint = Stroke(color, width);
        paint.StrokeCap = SKStrokeCap.Butt;
        paint.PathEffect = AnchorDash;
        return paint;
    }

    private static SKPaint Fill(SKColor color) =>
        new()
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

    private LatLon ToLatLon(Point at)
    {
        (double lat, double lon) = Viewport.ScreenToLatLon((float)at.X, (float)at.Y);
        return new LatLon(lat, lon);
    }

    private RouteComposerScene CurrentScene()
    {
        var scene = RouteComposerScene.Build(_viewModel?.Points ?? [], _fixes);
        return (_drag is { Moving: true } drag) ? scene.WithPointAt(drag.Index, drag.At) : scene;
    }

    private IEnumerable<(double Lat, double Lon)> ContentPositions()
    {
        if (_viewModel is not { } vm)
        {
            return [];
        }

        List<(double, double)> points = [.. vm.Points.Where(p => p.Position is not null).Select(p => (p.Position!.Value.Lat, p.Position.Value.Lon))];
        return (points.Count > 0) ? points : vm.Maps.Where(m => m.IsEnabled).SelectMany(m => m.Data.Lines).SelectMany(l => l.Points);
    }

    private void RefreshFixes()
    {
        _fixes = RouteComposerScene.FixesInView(_viewModel?.NavDb, Viewport);
        MarkDirty();
    }

    // Navdata seen for the first time (the status turns ready) brings the fixes in view without a pan or zoom.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RouteComposerViewModel.StatusText))
        {
            RefreshFixes();
            return;
        }

        MarkDirty();
    }

    private void SetHover(RouteComposerHit? hit)
    {
        if (!Equals(_hover, hit))
        {
            _hover = hit;
            MarkDirty();
        }
    }

    // A drag that moved re-resolves the point where it was dropped, snapped to a drawn fix there; one that did not selects it.
    private void EndDrag(PointDrag drag, Point at)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (drag.Moving)
        {
            RouteComposerFix? fix = RouteComposerHitTest.FindFix(CurrentScene(), Viewport, (float)at.X, (float)at.Y);
            vm.MovePoint(drag.Index, ToLatLon(at), fix?.Name);
        }
        else
        {
            vm.SelectedIndex = drag.Index;
        }

        MarkDirty();
    }

    private ContextMenu BuildContextMenu(int index)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Delete point", () => _viewModel?.DeletePoint(index)));
        menu.Items.Add(
            MenuItemFor(
                "Insert after",
                () =>
                {
                    if (_viewModel is { } vm)
                    {
                        vm.SelectedIndex = index;
                    }
                }
            )
        );
        return menu;
    }

    private MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            action();
            MarkDirty();
        };
        return item;
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

    private void Draw(SKCanvas canvas, MapViewport viewport, object? snapshot)
    {
        if (snapshot is not RouteComposerRenderSnapshot frame)
        {
            return;
        }

        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            canvas.Clear(new SKColor(0x12, 0x14, 0x18));
            _maps.Render(canvas, viewport, frame.Maps, NoBrightness);
            RouteComposerLayout layout = frame.Scene.Layout(viewport);
            DrawFixes(canvas, layout);
            DrawRoute(canvas, layout, frame.RouteColor);
            DrawRings(canvas, layout, frame);
        }
    }

    private void DrawFixes(SKCanvas canvas, RouteComposerLayout layout)
    {
        foreach (RouteComposerScreenFix fix in layout.Fixes)
        {
            using var path = new SKPath();
            path.MoveTo(fix.At.X, fix.At.Y - FixSize);
            path.LineTo(fix.At.X + FixSize, fix.At.Y + FixSize);
            path.LineTo(fix.At.X - FixSize, fix.At.Y + FixSize);
            path.Close();
            canvas.DrawPath(path, _fixPaint);
            canvas.DrawText(fix.Fix.Name, fix.At.X + LabelGap, fix.At.Y - LabelGap, SKTextAlign.Left, _font, _fixLabelPaint);
        }
    }

    private void DrawRoute(SKCanvas canvas, RouteComposerLayout layout, SKColor color)
    {
        _routePaint.Color = color;
        _pointPaint.Color = color;
        _ghostPaint.Color = color;
        for (int i = 1; i < layout.Points.Length; i++)
        {
            canvas.DrawLine(layout.Points[i - 1].At, layout.Points[i].At, _routePaint);
        }

        foreach (RouteComposerScreenPoint point in layout.Points)
        {
            if (point.Anchor is { } anchor)
            {
                canvas.DrawLine(anchor, point.At, _anchorPaint);
            }

            if (point.Ghost is { } ghost)
            {
                canvas.DrawCircle(ghost, PointRadius, _ghostPaint);
            }

            canvas.DrawCircle(point.At, PointRadius, _pointPaint);
        }
    }

    private void DrawRings(SKCanvas canvas, RouteComposerLayout layout, RouteComposerRenderSnapshot frame)
    {
        if ((frame.Selected is int selected) && (layout.Points.FirstOrDefault(p => p.Index == selected) is { } point))
        {
            canvas.DrawCircle(point.At, SelectedRadius, _selectedPaint);
        }

        SKPoint? hovered = frame.Hover switch
        {
            { PointIndex: int index } => layout.Points.FirstOrDefault(p => p.Index == index)?.At,
            { Fix: { } fix } => layout.Fixes.FirstOrDefault(f => f.Fix == fix)?.At,
            _ => null,
        };
        if (hovered is { } at)
        {
            canvas.DrawCircle(at, HoverRadius, _hoverPaint);
        }
    }

    // A left press on a point: whether it has moved past the click slop yet, and where the pointer is now.
    private sealed record PointDrag(int Index, Point Start, bool Moving, LatLon At);
}
