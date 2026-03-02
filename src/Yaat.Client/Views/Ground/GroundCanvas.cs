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
    public static readonly StyledProperty<GroundLayoutDto?> LayoutProperty =
        AvaloniaProperty.Register<GroundCanvas, GroundLayoutDto?>(nameof(Layout));

    public static readonly StyledProperty<IReadOnlyList<AircraftModel>?> AircraftProperty =
        AvaloniaProperty.Register<GroundCanvas, IReadOnlyList<AircraftModel>?>(nameof(Aircraft));

    public static readonly StyledProperty<AircraftModel?> SelectedAircraftProperty =
        AvaloniaProperty.Register<GroundCanvas, AircraftModel?>(nameof(SelectedAircraft));

    public static readonly StyledProperty<TaxiRoute?> ActiveRouteProperty =
        AvaloniaProperty.Register<GroundCanvas, TaxiRoute?>(nameof(ActiveRoute));

    public static readonly StyledProperty<TaxiRoute?> PreviewRouteProperty =
        AvaloniaProperty.Register<GroundCanvas, TaxiRoute?>(nameof(PreviewRoute));

    private readonly GroundRenderer _renderer = new();
    private int? _hoveredNodeId;
    private bool _hasFitBounds;

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

    public int? HoveredNodeId => _hoveredNodeId;

    /// <summary>Fired when a node is right-clicked. Args: nodeId, screen position.</summary>
    public event Action<int, Point>? NodeRightClicked;

    /// <summary>Fired when an aircraft is right-clicked. Args: callsign, screen position.</summary>
    public event Action<string, Point>? AircraftRightClicked;

    /// <summary>Fired when empty space is right-clicked. Args: lat, lon, screen position.</summary>
    public event Action<double, double, Point>? MapRightClicked;

    /// <summary>Fired when an aircraft is left-clicked. Args: callsign.</summary>
    public event Action<string>? AircraftLeftClicked;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LayoutProperty)
        {
            _hasFitBounds = false;
            FitToLayout();
            InvalidateVisual();
        }
        else if (change.Property == AircraftProperty
            || change.Property == SelectedAircraftProperty
            || change.Property == ActiveRouteProperty
            || change.Property == PreviewRouteProperty)
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
        TaxiRoute? PreviewRoute);

    protected override object? CreateRenderSnapshot()
    {
        return new RenderSnapshot(
            Layout,
            Aircraft ?? Array.Empty<AircraftModel>(),
            SelectedAircraft,
            _hoveredNodeId,
            ActiveRoute,
            PreviewRoute);
    }

    protected override void RenderFromSnapshot(
        SKCanvas canvas, MapViewport viewport, object? snapshot)
    {
        if (snapshot is not RenderSnapshot s)
        {
            return;
        }

        _renderer.Render(canvas, viewport, s.Layout,
            s.Aircraft, s.SelectedAircraft,
            s.HoveredNodeId, s.ActiveRoute, s.PreviewRoute);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHoveredNode(e.GetPosition(this));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed)
        {
            HandleRightClick(pos);
            e.Handled = true;
            return;
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
        }

        base.OnPointerPressed(e);
    }

    private void HandleRightClick(Point screenPos)
    {
        // Check aircraft first
        var ac = FindAircraftAtPoint(screenPos);
        if (ac is not null)
        {
            AircraftRightClicked?.Invoke(ac.Callsign, screenPos);
            return;
        }

        // Check node
        var node = FindNodeAtPoint(screenPos);
        if (node is not null)
        {
            NodeRightClicked?.Invoke(node.Id, screenPos);
            return;
        }

        // Empty map space
        var (lat, lon) = Viewport.ScreenToLatLon(
            (float)screenPos.X, (float)screenPos.Y);
        MapRightClicked?.Invoke(lat, lon, screenPos);
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
            var (sx, sy) = Viewport.LatLonToScreen(
                node.Latitude, node.Longitude);
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

    public AircraftModel? FindAircraftAtPoint(Point screenPos)
    {
        if (Aircraft is null)
        {
            return null;
        }

        const float hitRadius = 14f;
        AircraftModel? closest = null;
        float closestDist = hitRadius;

        foreach (var ac in Aircraft)
        {
            if (!ac.IsOnGround)
            {
                continue;
            }

            var (sx, sy) = Viewport.LatLonToScreen(
                ac.Latitude, ac.Longitude);
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

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;

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
    }
}
