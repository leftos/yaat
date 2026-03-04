using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
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

    public static readonly StyledProperty<double> AirportCenterLatProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(AirportCenterLat));
    public static readonly StyledProperty<double> AirportCenterLonProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(AirportCenterLon));
    public static readonly StyledProperty<double> AirportElevationProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(AirportElevation));

    private static readonly SKPoint DefaultDataBlockOffset = new(30, -25);
    private const float DataBlockPad = 3f;

    private readonly GroundRenderer _renderer = new();
    private readonly Dictionary<string, SKPoint> _dataBlockOffsets = new();
    private readonly SKPaint _hitTestPaint = new() { TextSize = 12, Typeface = SKTypeface.FromFamilyName("Consolas") };
    private int? _hoveredNodeId;
    private bool _hasFitBounds;
    private bool _isDraggingDataBlock;
    private string? _dragCallsign;
    private SKPoint _dragStartOffset;
    private Point _dragStartMousePos;
    private bool _dragThresholdMet;

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

    public int? HoveredNodeId => _hoveredNodeId;

    /// <summary>Fired when a node is right-clicked. Args: nodeId, screen position.</summary>
    public event Action<int, Point>? NodeRightClicked;

    /// <summary>Fired when an aircraft is right-clicked. Args: callsign, screen position.</summary>
    public event Action<string, Point>? AircraftRightClicked;

    /// <summary>Fired when an aircraft is left-clicked. Args: callsign.</summary>
    public event Action<string>? AircraftLeftClicked;

    /// <summary>Fired when empty space is left-clicked (deselect).</summary>
    public event Action? EmptySpaceClicked;

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
        )
        {
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
        IReadOnlyDictionary<string, SKPoint> DataBlockOffsets,
        double AirportCenterLat,
        double AirportCenterLon,
        double AirportElevation
    );

    protected override object? CreateRenderSnapshot()
    {
        return new RenderSnapshot(
            Layout,
            Aircraft ?? Array.Empty<AircraftModel>(),
            SelectedAircraft,
            _hoveredNodeId,
            ActiveRoute,
            PreviewRoute,
            new Dictionary<string, SKPoint>(_dataBlockOffsets),
            AirportCenterLat,
            AirportCenterLon,
            AirportElevation
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
            s.DataBlockOffsets,
            s.AirportCenterLat,
            s.AirportCenterLon,
            s.AirportElevation
        );
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
            if (props.IsRightButtonPressed)
            {
                AircraftRightClicked?.Invoke(dataBlockAc.Callsign, pos);
                e.Handled = true;
                return;
            }

            if (props.IsLeftButtonPressed)
            {
                _isDraggingDataBlock = true;
                _dragCallsign = dataBlockAc.Callsign;
                _dragStartOffset = _dataBlockOffsets.TryGetValue(dataBlockAc.Callsign, out var off) ? off : DefaultDataBlockOffset;
                _dragStartMousePos = pos;
                _dragThresholdMet = false;
                e.Handled = true;
                return;
            }
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
                AircraftLeftClicked?.Invoke(ac.Callsign);
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

            if (!_dragThresholdMet && _dragCallsign is not null)
            {
                AircraftLeftClicked?.Invoke(_dragCallsign);
            }

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

        const float hitRadius = 12f;
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

        foreach (var ac in Aircraft)
        {
            if (!ac.IsOnGround)
            {
                continue;
            }

            var (sx, sy) = Viewport.LatLonToScreen(ac.Latitude, ac.Longitude);

            SKPoint offset = DefaultDataBlockOffset;
            if (_dataBlockOffsets.TryGetValue(ac.Callsign, out var customOffset))
            {
                offset = customOffset;
            }

            float blockX = sx + offset.X;
            float blockY = sy + offset.Y;

            string line1 = ac.Callsign;
            string line2 = ac.AircraftType ?? "";

            float w1 = _hitTestPaint.MeasureText(line1);
            float w2 = _hitTestPaint.MeasureText(line2);
            float textW = MathF.Max(w1, w2);
            float lineH = _hitTestPaint.TextSize + 2;

            var blockRect = new SKRect(
                blockX - DataBlockPad,
                blockY - _hitTestPaint.TextSize - DataBlockPad,
                blockX + textW + DataBlockPad,
                blockY + lineH + DataBlockPad
            );

            if (blockRect.Contains((float)screenPos.X, (float)screenPos.Y))
            {
                return ac;
            }
        }

        return null;
    }

    public AircraftModel? FindAircraftAtPoint(Point screenPos)
    {
        if (Aircraft is null)
        {
            return null;
        }

        const float hitRadius = 19f;
        AircraftModel? closest = null;
        float closestDist = hitRadius;

        foreach (var ac in Aircraft)
        {
            if (!ac.IsOnGround)
            {
                continue;
            }

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
