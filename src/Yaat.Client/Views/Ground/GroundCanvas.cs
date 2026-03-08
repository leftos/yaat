using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views.Ground;

/// <summary>
/// SkiaSharp canvas that renders airport ground layout with aircraft positions.
/// </summary>
public sealed class GroundCanvas : MapCanvasBase, IDisposable
{
    public static readonly StyledProperty<GroundLayoutDto?> LayoutProperty = AvaloniaProperty.Register<GroundCanvas, GroundLayoutDto?>(
        nameof(Layout)
    );

    public static readonly StyledProperty<IReadOnlyList<AircraftModel>?> AircraftProperty = AvaloniaProperty.Register<
        GroundCanvas,
        IReadOnlyList<AircraftModel>?
    >(nameof(Aircraft));

    public static readonly StyledProperty<AircraftModel?> SelectedAircraftProperty = AvaloniaProperty.Register<GroundCanvas, AircraftModel?>(
        nameof(SelectedAircraft)
    );

    public static readonly StyledProperty<TaxiRoute?> ActiveRouteProperty = AvaloniaProperty.Register<GroundCanvas, TaxiRoute?>(nameof(ActiveRoute));

    public static readonly StyledProperty<TaxiRoute?> PreviewRouteProperty = AvaloniaProperty.Register<GroundCanvas, TaxiRoute?>(
        nameof(PreviewRoute)
    );

    public static readonly StyledProperty<double> AirportCenterLatProperty = AvaloniaProperty.Register<GroundCanvas, double>(
        nameof(AirportCenterLat)
    );
    public static readonly StyledProperty<double> AirportCenterLonProperty = AvaloniaProperty.Register<GroundCanvas, double>(
        nameof(AirportCenterLon)
    );
    public static readonly StyledProperty<double> AirportElevationProperty = AvaloniaProperty.Register<GroundCanvas, double>(
        nameof(AirportElevation)
    );

    public static readonly StyledProperty<TaxiRoute?> DrawnRoutePreviewProperty = AvaloniaProperty.Register<GroundCanvas, TaxiRoute?>(
        nameof(DrawnRoutePreview)
    );

    public static readonly StyledProperty<bool> IsDrawingRouteProperty = AvaloniaProperty.Register<GroundCanvas, bool>(nameof(IsDrawingRoute));

    public static readonly StyledProperty<IReadOnlyList<int>?> DrawWaypointsProperty = AvaloniaProperty.Register<GroundCanvas, IReadOnlyList<int>?>(
        nameof(DrawWaypoints)
    );

    public static readonly StyledProperty<bool> ShowDebugInfoProperty = AvaloniaProperty.Register<GroundCanvas, bool>(nameof(ShowDebugInfo));

    public static readonly StyledProperty<WeatherDisplayInfo?> WeatherInfoProperty = AvaloniaProperty.Register<GroundCanvas, WeatherDisplayInfo?>(
        nameof(WeatherInfo)
    );

    private readonly GroundRenderer _renderer = new();
    private readonly Dictionary<string, SKPoint> _dataBlockOffsets = new();
    private readonly SKPaint _hitTestPaint = new() { TextSize = 12, Typeface = Services.PlatformHelper.MonospaceTypefaceBold };
    private int? _hoveredNodeId;
    private bool _hasFitBounds;
    private bool _isDraggingDataBlock;
    private string? _dragCallsign;
    private SKPoint _dragStartOffset;
    private Point _dragStartMousePos;
    private bool _dragThresholdMet;
    private readonly Dictionary<string, int> _dataBlockZOrder = new();
    private int _nextZOrder = 1;

    public GroundLayoutDto? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public IReadOnlyList<AircraftModel>? Aircraft
    {
        get => GetValue(AircraftProperty);
        set => SetValue(AircraftProperty, value);
    }

    public AircraftModel? SelectedAircraft
    {
        get => GetValue(SelectedAircraftProperty);
        set => SetValue(SelectedAircraftProperty, value);
    }

    public TaxiRoute? ActiveRoute
    {
        get => GetValue(ActiveRouteProperty);
        set => SetValue(ActiveRouteProperty, value);
    }

    public TaxiRoute? PreviewRoute
    {
        get => GetValue(PreviewRouteProperty);
        set => SetValue(PreviewRouteProperty, value);
    }

    public double AirportCenterLat
    {
        get => GetValue(AirportCenterLatProperty);
        set => SetValue(AirportCenterLatProperty, value);
    }

    public double AirportCenterLon
    {
        get => GetValue(AirportCenterLonProperty);
        set => SetValue(AirportCenterLonProperty, value);
    }

    public double AirportElevation
    {
        get => GetValue(AirportElevationProperty);
        set => SetValue(AirportElevationProperty, value);
    }

    public bool ShowDebugInfo
    {
        get => GetValue(ShowDebugInfoProperty);
        set => SetValue(ShowDebugInfoProperty, value);
    }

    public WeatherDisplayInfo? WeatherInfo
    {
        get => GetValue(WeatherInfoProperty);
        set => SetValue(WeatherInfoProperty, value);
    }

    public TaxiRoute? DrawnRoutePreview
    {
        get => GetValue(DrawnRoutePreviewProperty);
        set => SetValue(DrawnRoutePreviewProperty, value);
    }

    public bool IsDrawingRoute
    {
        get => GetValue(IsDrawingRouteProperty);
        set => SetValue(IsDrawingRouteProperty, value);
    }

    public IReadOnlyList<int>? DrawWaypoints
    {
        get => GetValue(DrawWaypointsProperty);
        set => SetValue(DrawWaypointsProperty, value);
    }

    public int? HoveredNodeId => _hoveredNodeId;

    /// <summary>Surfaces the datablock for the given callsign to the top of the Z-order.</summary>
    public void SurfaceDataBlock(string callsign)
    {
        _dataBlockZOrder[callsign] = _nextZOrder++;
        MarkDirty();
    }

    /// <summary>Fired when a node is right-clicked. Args: nodeId, screen position.</summary>
    public event Action<int, Point>? NodeRightClicked;

    /// <summary>Fired when an aircraft is right-clicked. Args: callsign, screen position.</summary>
    public event Action<string, Point>? AircraftRightClicked;

    /// <summary>Fired when an aircraft is left-clicked. Args: callsign.</summary>
    public event Action<string>? AircraftLeftClicked;

    /// <summary>Fired when an aircraft is Ctrl+left-clicked. Args: callsign.</summary>
    public event Action<string>? AircraftCtrlClicked;

    /// <summary>Fired when empty space is left-clicked (deselect).</summary>
    public event Action? EmptySpaceClicked;

    /// <summary>Fired when a node is left-clicked during draw mode.</summary>
    public event Action<int>? DrawNodeClicked;

    /// <summary>Fired when a node is right-clicked or double-clicked during draw mode (finish).</summary>
    public event Action<int, Point>? DrawNodeFinished;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LayoutProperty)
        {
            _hasFitBounds = false;
            _dataBlockOffsets.Clear();
            FitToLayout();
            InvalidateVisual();
        }
        else if (
            change.Property == AircraftProperty
            || change.Property == SelectedAircraftProperty
            || change.Property == ActiveRouteProperty
            || change.Property == PreviewRouteProperty
            || change.Property == DrawnRoutePreviewProperty
            || change.Property == DrawWaypointsProperty
            || change.Property == ShowDebugInfoProperty
        )
        {
            MarkDirty();
        }
        else if (change.Property == IsDrawingRouteProperty)
        {
            Cursor = IsDrawingRoute ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
            MarkDirty();
        }
    }

    private sealed record RenderSnapshot(
        GroundLayoutDto? Layout,
        IReadOnlyList<AircraftModel> Aircraft,
        AircraftModel? SelectedAircraft,
        int? HoveredNodeId,
        TaxiRoute? ActiveRoute,
        TaxiRoute? PreviewRoute,
        TaxiRoute? DrawnRoutePreview,
        IReadOnlyList<int>? DrawWaypoints,
        bool IsDrawingRoute,
        IReadOnlyDictionary<string, SKPoint> DataBlockOffsets,
        double AirportCenterLat,
        double AirportCenterLon,
        double AirportElevation,
        bool ShowDebugInfo,
        WeatherDisplayInfo? WeatherInfo
    );

    protected override object? CreateRenderSnapshot()
    {
        return new RenderSnapshot(
            Layout,
            SortByZOrder(FilterActiveAircraft(Aircraft), _dataBlockZOrder),
            SelectedAircraft,
            _hoveredNodeId,
            ActiveRoute,
            PreviewRoute,
            DrawnRoutePreview,
            DrawWaypoints,
            IsDrawingRoute,
            new Dictionary<string, SKPoint>(_dataBlockOffsets),
            AirportCenterLat,
            AirportCenterLon,
            AirportElevation,
            ShowDebugInfo,
            WeatherInfo
        );
    }

    protected override void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot)
    {
        if (snapshot is not RenderSnapshot s)
        {
            return;
        }

        _renderer.Render(
            canvas,
            viewport,
            s.Layout,
            s.Aircraft,
            s.SelectedAircraft,
            s.HoveredNodeId,
            s.ActiveRoute,
            s.PreviewRoute,
            s.DrawnRoutePreview,
            s.DrawWaypoints,
            s.DataBlockOffsets,
            s.AirportCenterLat,
            s.AirportCenterLon,
            s.AirportElevation,
            s.ShowDebugInfo,
            s.WeatherInfo
        );
    }

    private static IReadOnlyList<AircraftModel> SortByZOrder(IReadOnlyList<AircraftModel> aircraft, Dictionary<string, int> zOrder)
    {
        if (zOrder.Count == 0)
        {
            return aircraft;
        }

        var sorted = new List<AircraftModel>(aircraft);
        sorted.Sort(
            (a, b) =>
            {
                zOrder.TryGetValue(a.Callsign, out var za);
                zOrder.TryGetValue(b.Callsign, out var zb);
                return za.CompareTo(zb);
            }
        );
        return sorted;
    }

    private static IReadOnlyList<AircraftModel> FilterActiveAircraft(IReadOnlyList<AircraftModel>? aircraft)
    {
        if (aircraft is null || aircraft.Count == 0)
        {
            return Array.Empty<AircraftModel>();
        }

        var result = new List<AircraftModel>(aircraft.Count);
        foreach (var ac in aircraft)
        {
            if (!ac.IsDelayed)
            {
                result.Add(ac);
            }
        }
        return result;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isDraggingDataBlock)
        {
            var pos = e.GetPosition(this);
            var dx = (float)(pos.X - _dragStartMousePos.X);
            var dy = (float)(pos.Y - _dragStartMousePos.Y);

            if (!_dragThresholdMet && dx * dx + dy * dy > 16)
            {
                _dragThresholdMet = true;
            }

            if (_dragThresholdMet && _dragCallsign is not null)
            {
                _dataBlockOffsets[_dragCallsign] = new SKPoint(_dragStartOffset.X + dx, _dragStartOffset.Y + dy);
                MarkDirty();
            }

            e.Handled = true;
            return;
        }

        base.OnPointerMoved(e);
        UpdateHoveredNode(e.GetPosition(this));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        var dataBlockAc = FindDataBlockAtPoint(pos);
        if (dataBlockAc is not null)
        {
            SurfaceDataBlock(dataBlockAc.Callsign);

            if (props.IsRightButtonPressed)
            {
                AircraftRightClicked?.Invoke(dataBlockAc.Callsign, pos);
                e.Handled = true;
                return;
            }

            if (props.IsLeftButtonPressed)
            {
                if (Services.PlatformHelper.HasActionModifier(e.KeyModifiers))
                {
                    AircraftCtrlClicked?.Invoke(dataBlockAc.Callsign);
                }
                else
                {
                    AircraftLeftClicked?.Invoke(dataBlockAc.Callsign);
                }

                _isDraggingDataBlock = true;
                _dragCallsign = dataBlockAc.Callsign;
                _dragStartOffset = _dataBlockOffsets.TryGetValue(dataBlockAc.Callsign, out var off) ? off : DataBlockLayout.DefaultOffset;
                _dragStartMousePos = pos;
                _dragThresholdMet = false;
                e.Handled = true;
                return;
            }
        }

        if (IsDrawingRoute)
        {
            if (props.IsLeftButtonPressed)
            {
                var node = FindNodeAtPoint(pos);
                if (node is not null)
                {
                    DrawNodeClicked?.Invoke(node.Id);
                    e.Handled = true;
                    return;
                }
            }
            else if (props.IsRightButtonPressed)
            {
                var node = FindNodeAtPoint(pos);
                if (node is not null)
                {
                    DrawNodeFinished?.Invoke(node.Id, pos);
                    e.Handled = true;
                    return;
                }
            }

            base.OnPointerPressed(e);
            return;
        }

        if (props.IsRightButtonPressed)
        {
            if (HandleRightClick(pos))
            {
                e.Handled = true;
                return;
            }
        }

        if (props.IsLeftButtonPressed)
        {
            var ac = FindAircraftAtPoint(pos);
            if (ac is not null)
            {
                SurfaceDataBlock(ac.Callsign);
                if (Services.PlatformHelper.HasActionModifier(e.KeyModifiers))
                {
                    AircraftCtrlClicked?.Invoke(ac.Callsign);
                }
                else
                {
                    AircraftLeftClicked?.Invoke(ac.Callsign);
                }
                e.Handled = true;
                return;
            }

            EmptySpaceClicked?.Invoke();
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isDraggingDataBlock)
        {
            _isDraggingDataBlock = false;
            _dragCallsign = null;
            e.Handled = true;
            return;
        }

        base.OnPointerReleased(e);
    }

    /// <summary>Returns true if a context menu target was hit.</summary>
    private bool HandleRightClick(Point screenPos)
    {
        var ac = FindAircraftAtPoint(screenPos);
        if (ac is not null)
        {
            AircraftRightClicked?.Invoke(ac.Callsign, screenPos);
            return true;
        }

        var node = FindNodeAtPoint(screenPos);
        if (node is not null)
        {
            NodeRightClicked?.Invoke(node.Id, screenPos);
            return true;
        }

        return false;
    }

    public GroundNodeDto? FindNodeAtPoint(Point screenPos)
    {
        if (Layout is null)
        {
            return null;
        }

        const float hitRadius = 20f;
        GroundNodeDto? closest = null;
        float closestDist = hitRadius;

        foreach (var node in Layout.Nodes)
        {
            var (sx, sy) = Viewport.LatLonToScreen(node.Latitude, node.Longitude);
            var dx = (float)screenPos.X - sx;
            var dy = (float)screenPos.Y - sy;
            var dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist < closestDist)
            {
                closestDist = dist;
                closest = node;
            }
        }

        return closest;
    }

    public AircraftModel? FindDataBlockAtPoint(Point screenPos)
    {
        if (Aircraft is null)
        {
            return null;
        }

        // Use z-order-sorted list so the topmost (last-drawn) datablock wins
        var sorted = SortByZOrder(FilterActiveAircraft(Aircraft), _dataBlockZOrder);
        AircraftModel? best = null;

        foreach (var ac in sorted)
        {
            var (sx, sy) = Viewport.LatLonToScreen(ac.Latitude, ac.Longitude);

            SKPoint offset = DataBlockLayout.DefaultOffset;
            if (_dataBlockOffsets.TryGetValue(ac.Callsign, out var customOffset))
            {
                offset = customOffset;
            }

            var layout = DataBlockLayout.Compute(ac, sx, sy, offset, _hitTestPaint, isAirborne: false);
            if (layout.Rect.Contains((float)screenPos.X, (float)screenPos.Y))
            {
                best = ac;
            }
        }

        return best;
    }

    public AircraftModel? FindAircraftAtPoint(Point screenPos)
    {
        if (Aircraft is null)
        {
            return null;
        }

        const float hitRadius = 28f;
        AircraftModel? closest = null;
        float closestDist = hitRadius;

        foreach (var ac in FilterActiveAircraft(Aircraft))
        {
            var (sx, sy) = Viewport.LatLonToScreen(ac.Latitude, ac.Longitude);
            var dx = (float)screenPos.X - sx;
            var dy = (float)screenPos.Y - sy;
            var dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist < closestDist)
            {
                closestDist = dist;
                closest = ac;
            }
        }

        return closest;
    }

    private void UpdateHoveredNode(Point screenPos)
    {
        var node = FindNodeAtPoint(screenPos);
        var newId = node?.Id;
        if (newId != _hoveredNodeId)
        {
            _hoveredNodeId = newId;
            MarkDirty();
        }
    }

    private void FitToLayout()
    {
        if (_hasFitBounds || Layout is null || Layout.Nodes.Count == 0)
        {
            return;
        }

        if (Viewport.PixelWidth < 1 || Viewport.PixelHeight < 1)
        {
            return;
        }

        double minLat = double.MaxValue,
            maxLat = double.MinValue;
        double minLon = double.MaxValue,
            maxLon = double.MinValue;

        foreach (var node in Layout.Nodes)
        {
            minLat = Math.Min(minLat, node.Latitude);
            maxLat = Math.Max(maxLat, node.Latitude);
            minLon = Math.Min(minLon, node.Longitude);
            maxLon = Math.Max(maxLon, node.Longitude);
        }

        Viewport.FitBounds(minLat, maxLat, minLon, maxLon);
        _hasFitBounds = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (IsPanZoomEnabled && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var delta = e.Delta.Y > 0 ? 1.0 : -1.0;
            Viewport.RotationDeg = (Viewport.RotationDeg + delta) % 360.0;
            OnViewportChanged();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_hasFitBounds)
        {
            FitToLayout();
        }
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _hitTestPaint.Dispose();
    }
}
