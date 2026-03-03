using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Yaat.Client.Views.Map;

/// <summary>
/// Abstract base for map-rendering controls. Provides pan/zoom via mouse,
/// viewport management, and batched invalidation. Subclasses implement
/// <see cref="RenderContent"/> to draw with SkiaSharp.
/// </summary>
public abstract class MapCanvasBase : Control
{
    private static readonly TimeSpan InvalidateInterval = TimeSpan.FromMilliseconds(100);

    private readonly MapViewport _viewport = new();
    private readonly DispatcherTimer _invalidateTimer;
    private bool _isPanning;
    private Point _lastPanPoint;
    private bool _isPanZoomEnabled = true;

    protected MapCanvasBase()
    {
        ClipToBounds = true;
        Focusable = true;

        _invalidateTimer = new DispatcherTimer(InvalidateInterval, DispatcherPriority.Render, OnInvalidateTick);
        _invalidateTimer.Start();
    }

    public MapViewport Viewport => _viewport;

    /// <summary>
    /// When false, mouse pan and scroll-zoom are disabled.
    /// Subclasses can still handle clicks for selection.
    /// </summary>
    public bool IsPanZoomEnabled
    {
        get => _isPanZoomEnabled;
        set => _isPanZoomEnabled = value;
    }

    /// <summary>Triggers an immediate repaint (before the next timer tick).</summary>
    public void MarkDirty()
    {
        InvalidateVisual();
    }

    /// <summary>
    /// Called on the UI thread to capture styled property values into a
    /// snapshot object. The snapshot is then passed to
    /// <see cref="RenderFromSnapshot"/> on the render thread,
    /// avoiding cross-thread access to Avalonia properties.
    /// </summary>
    protected abstract object? CreateRenderSnapshot();

    /// <summary>
    /// Called on the render thread with the snapshot from
    /// <see cref="CreateRenderSnapshot"/>. Do not access StyledProperties here.
    /// </summary>
    protected abstract void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot);

    /// <summary>Called when the viewport changes (pan/zoom). Override to react.</summary>
    protected virtual void OnViewportChanged() { }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Capture property values on the UI thread
        var snapshot = CreateRenderSnapshot();
        var viewportCopy = _viewport.Clone();
        var op = new MapDrawOperation(this, new Rect(0, 0, Bounds.Width, Bounds.Height), snapshot, viewportCopy);
        context.Custom(op);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return availableSize;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _viewport.PixelWidth = (float)Bounds.Width;
        _viewport.PixelHeight = (float)Bounds.Height;
        OnViewportChanged();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed && _isPanZoomEnabled)
        {
            _isPanning = true;
            _lastPanPoint = e.GetPosition(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isPanning)
        {
            var pos = e.GetPosition(this);
            var dx = (float)(pos.X - _lastPanPoint.X);
            var dy = (float)(pos.Y - _lastPanPoint.Y);
            _viewport.Pan(dx, dy);
            _lastPanPoint = pos;
            OnViewportChanged();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isPanning)
        {
            _isPanning = false;
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (!_isPanZoomEnabled)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.2 : 1.0 / 1.2;
        _viewport.ZoomAt((float)pos.X, (float)pos.Y, factor);
        OnViewportChanged();
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnInvalidateTick(object? sender, EventArgs e)
    {
        // Always repaint at ~10fps. Aircraft positions change via property
        // updates on items within the bound ObservableCollection, which don't
        // trigger styled-property change notifications on the canvas. Continuous
        // refresh matches real radar/ground display behavior.
        InvalidateVisual();
    }

    private sealed class MapDrawOperation : ICustomDrawOperation
    {
        private readonly MapCanvasBase _owner;
        private readonly object? _snapshot;
        private readonly MapViewport _viewport;

        public MapDrawOperation(MapCanvasBase owner, Rect bounds, object? snapshot, MapViewport viewport)
        {
            _owner = owner;
            _snapshot = snapshot;
            _viewport = viewport;
            Bounds = bounds;
        }

        public Rect Bounds { get; }

        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other) => other == this;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            _owner.RenderFromSnapshot(canvas, _viewport, _snapshot);
        }
    }
}
