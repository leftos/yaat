using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// SkiaSharp canvas that renders a STARS-style radar display with
/// video maps, aircraft targets, and overlays.
/// </summary>
public sealed class RadarCanvas : MapCanvasBase, IDisposable
{
    public static readonly StyledProperty<IReadOnlyList<AircraftModel>?> AircraftProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<AircraftModel>?
    >(nameof(Aircraft));

    public static readonly StyledProperty<AircraftModel?> SelectedAircraftProperty = AvaloniaProperty.Register<RadarCanvas, AircraftModel?>(
        nameof(SelectedAircraft)
    );

    public static readonly StyledProperty<IReadOnlyList<VideoMapData>?> VideoMapsProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<VideoMapData>?
    >(nameof(VideoMaps));

    public static readonly StyledProperty<bool> ShowRangeRingsProperty = AvaloniaProperty.Register<RadarCanvas, bool>(
        nameof(ShowRangeRings),
        defaultValue: true
    );

    public static readonly StyledProperty<double> RangeNmProperty = AvaloniaProperty.Register<RadarCanvas, double>(nameof(RangeNm), defaultValue: 40);

    public static readonly StyledProperty<double> RadarCenterLatProperty = AvaloniaProperty.Register<RadarCanvas, double>(nameof(RadarCenterLat));

    public static readonly StyledProperty<double> RadarCenterLonProperty = AvaloniaProperty.Register<RadarCanvas, double>(nameof(RadarCenterLon));

    public static readonly StyledProperty<bool> ShowFixesProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(ShowFixes));

    public static readonly StyledProperty<IReadOnlyList<(string Name, double Lat, double Lon)>?> FixesProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<(string Name, double Lat, double Lon)>?
    >(nameof(Fixes));

    public static readonly StyledProperty<double> RangeRingCenterLatProperty = AvaloniaProperty.Register<RadarCanvas, double>(
        nameof(RangeRingCenterLat)
    );

    public static readonly StyledProperty<double> RangeRingCenterLonProperty = AvaloniaProperty.Register<RadarCanvas, double>(
        nameof(RangeRingCenterLon)
    );

    public static readonly StyledProperty<double> RangeRingSizeNmProperty = AvaloniaProperty.Register<RadarCanvas, double>(
        nameof(RangeRingSizeNm),
        defaultValue: 5
    );

    public static readonly StyledProperty<bool> IsPanZoomLockedProperty = AvaloniaProperty.Register<RadarCanvas, bool>(
        nameof(IsPanZoomLocked),
        defaultValue: true
    );

    public static readonly StyledProperty<bool> IsPlacingRangeRingProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(IsPlacingRangeRing));

    public static readonly StyledProperty<double> ViewRangeNmProperty = AvaloniaProperty.Register<RadarCanvas, double>(
        nameof(ViewRangeNm),
        defaultValue: 40
    );

    public static readonly StyledProperty<bool> IsAdjustingRangeRingSizeProperty = AvaloniaProperty.Register<RadarCanvas, bool>(
        nameof(IsAdjustingRangeRingSize)
    );

    public static readonly StyledProperty<bool> ShowTopDownProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(ShowTopDown));

    public static readonly StyledProperty<double> PtlLengthMinutesProperty = AvaloniaProperty.Register<RadarCanvas, double>(nameof(PtlLengthMinutes));

    public static readonly StyledProperty<bool> PtlOwnProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(PtlOwn));

    public static readonly StyledProperty<bool> PtlAllProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(PtlAll));

    public static readonly StyledProperty<IReadOnlySet<string>?> ProgrammedFixNamesProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlySet<string>?
    >(nameof(ProgrammedFixNames));

    public static readonly StyledProperty<bool> IsDrawingRouteProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(IsDrawingRoute));

    public static readonly StyledProperty<IReadOnlyList<DrawnWaypoint>?> DrawnWaypointsProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<DrawnWaypoint>?
    >(nameof(DrawnWaypoints));

    public static readonly StyledProperty<IReadOnlyDictionary<int, WaypointCondition>?> WaypointConditionsProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyDictionary<int, WaypointCondition>?
    >(nameof(WaypointConditions));

    private static readonly SKPoint DefaultDataBlockOffset = new(28, -28);
    private const float DataBlockPad = 3f;
    private const double DragThresholdSq = 25.0; // 5px threshold for click vs drag

    private readonly RadarRenderer _renderer = new();
    private readonly Dictionary<string, SKPoint> _dataBlockOffsets = new();
    private readonly SKPaint _hitTestPaint = new() { TextSize = 12, Typeface = SKTypeface.FromFamilyName("Consolas") };
    private bool _initialFitDone;
    private bool _suppressRangeFit;
    private bool _suppressCenterSync;
    private bool _rightButtonDown;
    private bool _rightDragStarted;
    private Point _rightPressPos;
    private Dictionary<string, string> _brightnessLookup = [];
    private bool _isDraggingDataBlock;
    private string? _dragCallsign;
    private SKPoint _dragStartOffset;
    private Point _dragStartMousePos;
    private bool _dragThresholdMet;
    private Point _lastPointerPos;
    private readonly HashSet<string> _minifiedCallsigns = new();

    public RadarCanvas()
    {
        // IsPanZoomLocked defaults to true, so match the base class state.
        // OnPropertyChanged won't fire when binding sets true→true.
        IsPanZoomEnabled = false;
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

    public IReadOnlyList<VideoMapData>? VideoMaps
    {
        get => GetValue(VideoMapsProperty);
        set => SetValue(VideoMapsProperty, value);
    }

    public bool ShowRangeRings
    {
        get => GetValue(ShowRangeRingsProperty);
        set => SetValue(ShowRangeRingsProperty, value);
    }

    public double RangeNm
    {
        get => GetValue(RangeNmProperty);
        set => SetValue(RangeNmProperty, value);
    }

    public double RadarCenterLat
    {
        get => GetValue(RadarCenterLatProperty);
        set => SetValue(RadarCenterLatProperty, value);
    }

    public double RadarCenterLon
    {
        get => GetValue(RadarCenterLonProperty);
        set => SetValue(RadarCenterLonProperty, value);
    }

    public bool ShowFixes
    {
        get => GetValue(ShowFixesProperty);
        set => SetValue(ShowFixesProperty, value);
    }

    public IReadOnlyList<(string Name, double Lat, double Lon)>? Fixes
    {
        get => GetValue(FixesProperty);
        set => SetValue(FixesProperty, value);
    }

    public double RangeRingCenterLat
    {
        get => GetValue(RangeRingCenterLatProperty);
        set => SetValue(RangeRingCenterLatProperty, value);
    }

    public double RangeRingCenterLon
    {
        get => GetValue(RangeRingCenterLonProperty);
        set => SetValue(RangeRingCenterLonProperty, value);
    }

    public double RangeRingSizeNm
    {
        get => GetValue(RangeRingSizeNmProperty);
        set => SetValue(RangeRingSizeNmProperty, value);
    }

    public bool IsPanZoomLocked
    {
        get => GetValue(IsPanZoomLockedProperty);
        set => SetValue(IsPanZoomLockedProperty, value);
    }

    public bool IsPlacingRangeRing
    {
        get => GetValue(IsPlacingRangeRingProperty);
        set => SetValue(IsPlacingRangeRingProperty, value);
    }

    public double ViewRangeNm
    {
        get => GetValue(ViewRangeNmProperty);
        private set => SetValue(ViewRangeNmProperty, value);
    }

    public bool IsAdjustingRangeRingSize
    {
        get => GetValue(IsAdjustingRangeRingSizeProperty);
        set => SetValue(IsAdjustingRangeRingSizeProperty, value);
    }

    public bool ShowTopDown
    {
        get => GetValue(ShowTopDownProperty);
        set => SetValue(ShowTopDownProperty, value);
    }

    public double PtlLengthMinutes
    {
        get => GetValue(PtlLengthMinutesProperty);
        set => SetValue(PtlLengthMinutesProperty, value);
    }

    public bool PtlOwn
    {
        get => GetValue(PtlOwnProperty);
        set => SetValue(PtlOwnProperty, value);
    }

    public bool PtlAll
    {
        get => GetValue(PtlAllProperty);
        set => SetValue(PtlAllProperty, value);
    }

    public IReadOnlySet<string>? ProgrammedFixNames
    {
        get => GetValue(ProgrammedFixNamesProperty);
        set => SetValue(ProgrammedFixNamesProperty, value);
    }

    public bool IsDrawingRoute
    {
        get => GetValue(IsDrawingRouteProperty);
        set => SetValue(IsDrawingRouteProperty, value);
    }

    public IReadOnlyList<DrawnWaypoint>? DrawnWaypoints
    {
        get => GetValue(DrawnWaypointsProperty);
        set => SetValue(DrawnWaypointsProperty, value);
    }

    public IReadOnlyDictionary<int, WaypointCondition>? WaypointConditions
    {
        get => GetValue(WaypointConditionsProperty);
        set => SetValue(WaypointConditionsProperty, value);
    }

    public float BrightnessA
    {
        get => _renderer.BrightnessA;
        set
        {
            _renderer.BrightnessA = value;
            MarkDirty();
        }
    }

    public float BrightnessB
    {
        get => _renderer.BrightnessB;
        set
        {
            _renderer.BrightnessB = value;
            MarkDirty();
        }
    }

    public float RangeRingBrightness
    {
        get => _renderer.RangeRingBrightness;
        set
        {
            _renderer.RangeRingBrightness = value;
            MarkDirty();
        }
    }

    /// <summary>
    /// Sets the brightness category lookup (mapId → "A"/"B").
    /// </summary>
    public void SetBrightnessLookup(Dictionary<string, string> lookup)
    {
        _brightnessLookup = lookup;
        MarkDirty();
    }

    /// <summary>Fired when an aircraft is right-clicked.</summary>
    public event Action<string, Point>? AircraftRightClicked;

    /// <summary>Fired when empty map space is right-clicked.</summary>
    public event Action<double, double, Point>? MapRightClicked;

    /// <summary>Fired when an aircraft is left-clicked.</summary>
    public event Action<string>? AircraftLeftClicked;

    /// <summary>Fired when empty map space is left-clicked (deselect).</summary>
    public event Action? EmptySpaceClicked;

    /// <summary>Fired when a route waypoint is placed during draw mode.</summary>
    public event Action<double, double>? RoutePointPlaced;

    /// <summary>Fired to undo the last route waypoint.</summary>
    public event Action? RoutePointUndo;

    /// <summary>Fired to confirm the drawn route.</summary>
    public event Action? RouteConfirmed;

    /// <summary>Fired to cancel route drawing.</summary>
    public event Action? RouteCancelled;

    /// <summary>Fired when right-clicking a drawn waypoint during route drawing.</summary>
    public event Action<int, Point>? RouteWaypointRightClicked;

    /// <summary>Fired when middle-clicking a waypoint to set a condition.</summary>
    public event Action<int, Point>? RoutePointConditionRequested;

    /// <summary>Fired when a range ring is placed via click.</summary>
    public event Action<double, double>? RangeRingPlaced;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (
            change.Property == VideoMapsProperty
            || change.Property == AircraftProperty
            || change.Property == SelectedAircraftProperty
            || change.Property == ShowRangeRingsProperty
            || change.Property == RangeNmProperty
            || change.Property == ShowFixesProperty
            || change.Property == FixesProperty
            || change.Property == RangeRingCenterLatProperty
            || change.Property == RangeRingCenterLonProperty
            || change.Property == RangeRingSizeNmProperty
            || change.Property == ShowTopDownProperty
            || change.Property == PtlLengthMinutesProperty
            || change.Property == PtlOwnProperty
            || change.Property == PtlAllProperty
            || change.Property == ProgrammedFixNamesProperty
            || change.Property == IsDrawingRouteProperty
            || change.Property == DrawnWaypointsProperty
            || change.Property == WaypointConditionsProperty
        )
        {
            MarkDirty();
        }

        // Sync center from binding → viewport. On initial load, also zoom to range.
        // Don't mark as done unless the viewport has pixel dimensions;
        // otherwise OnSizeChanged will handle it when the tab becomes visible.
        if (
            (change.Property == RadarCenterLatProperty || change.Property == RadarCenterLonProperty)
            && !_suppressCenterSync
            && RadarCenterLat != 0
            && RadarCenterLon != 0
        )
        {
            if (Viewport.PixelWidth >= 1 && Viewport.PixelHeight >= 1)
            {
                _suppressCenterSync = true;
                Viewport.CenterLat = RadarCenterLat;
                Viewport.CenterLon = RadarCenterLon;
                if (!_initialFitDone)
                {
                    _initialFitDone = true;
                    ZoomToRange();
                }
                _suppressCenterSync = false;
            }
        }

        // RANGE spinner drives viewport zoom
        if (change.Property == RangeNmProperty && _initialFitDone && !_suppressRangeFit)
        {
            ZoomToRange();
        }

        if (change.Property == IsPanZoomLockedProperty)
        {
            IsPanZoomEnabled = !IsPanZoomLocked;
        }
    }

    private sealed record RenderSnapshot(
        IReadOnlyList<VideoMapData> VideoMaps,
        Dictionary<string, string> BrightnessLookup,
        IReadOnlyList<AircraftModel> Aircraft,
        AircraftModel? SelectedAircraft,
        bool ShowRangeRings,
        double RangeNm,
        double RadarCenterLat,
        double RadarCenterLon,
        bool ShowFixes,
        IReadOnlyList<(string Name, double Lat, double Lon)>? Fixes,
        double RangeRingCenterLat,
        double RangeRingCenterLon,
        double RangeRingSizeNm,
        IReadOnlyDictionary<string, SKPoint> DataBlockOffsets,
        string? HoveredFixName,
        double PtlLengthMinutes,
        bool PtlOwn,
        bool PtlAll,
        IReadOnlySet<string>? ProgrammedFixNames,
        IReadOnlyList<DrawnWaypoint>? DrawnWaypoints,
        (double Lat, double Lon)? DrawRouteOrigin,
        (double Lat, double Lon)? RubberBandTarget,
        string? RubberBandLabel,
        IReadOnlyDictionary<int, WaypointCondition>? WaypointConditions,
        IReadOnlySet<string> MinifiedCallsigns
    );

    protected override object? CreateRenderSnapshot()
    {
        string? hoveredFix = null;
        if (ShowFixes && Fixes is not null)
        {
            hoveredFix = FindHoveredFixName(Fixes, _lastPointerPos);
        }

        IReadOnlyList<DrawnWaypoint>? drawRouteWaypoints = null;
        (double Lat, double Lon)? drawRouteCursorLatLon = null;
        string? drawRouteCursorLabel = null;
        (double Lat, double Lon)? drawRouteOrigin = null;
        if (IsDrawingRoute)
        {
            if (SelectedAircraft is { } ac)
            {
                drawRouteOrigin = (ac.Latitude, ac.Longitude);
            }

            if (DrawnWaypoints is { Count: > 0 })
            {
                drawRouteWaypoints = DrawnWaypoints;
            }

            var cursorLatLon = Viewport.ScreenToLatLon((float)_lastPointerPos.X, (float)_lastPointerPos.Y);
            drawRouteCursorLatLon = cursorLatLon;
            if (Fixes is not null)
            {
                drawRouteCursorLabel = FrdResolver.ToFrd(cursorLatLon.Lat, cursorLatLon.Lon, Fixes);
            }
        }

        return new RenderSnapshot(
            VideoMaps ?? Array.Empty<VideoMapData>(),
            _brightnessLookup,
            FilterAircraft(Aircraft, ShowTopDown),
            SelectedAircraft,
            ShowRangeRings,
            RangeNm,
            RadarCenterLat,
            RadarCenterLon,
            ShowFixes,
            Fixes,
            RangeRingCenterLat,
            RangeRingCenterLon,
            RangeRingSizeNm,
            new Dictionary<string, SKPoint>(_dataBlockOffsets),
            hoveredFix,
            PtlLengthMinutes,
            PtlOwn,
            PtlAll,
            ProgrammedFixNames,
            drawRouteWaypoints,
            drawRouteOrigin,
            drawRouteCursorLatLon,
            drawRouteCursorLabel,
            IsDrawingRoute ? WaypointConditions : null,
            new HashSet<string>(_minifiedCallsigns)
        );
    }

    private string? FindHoveredFixName(IReadOnlyList<(string Name, double Lat, double Lon)> fixes, Point mousePos)
    {
        const float hitRadius = 20f;
        string? bestName = null;
        float bestDist = hitRadius;

        foreach (var fix in fixes)
        {
            var (sx, sy) = Viewport.LatLonToScreen(fix.Lat, fix.Lon);
            var dx = (float)mousePos.X - sx;
            var dy = (float)mousePos.Y - sy;
            var dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = fix.Name;
            }
        }

        return bestName;
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
            s.VideoMaps,
            s.BrightnessLookup,
            s.Aircraft,
            s.SelectedAircraft,
            s.ShowRangeRings,
            s.RangeNm,
            s.RadarCenterLat,
            s.RadarCenterLon,
            s.ShowFixes,
            s.Fixes,
            s.RangeRingCenterLat,
            s.RangeRingCenterLon,
            s.RangeRingSizeNm,
            s.DataBlockOffsets,
            s.HoveredFixName,
            s.PtlLengthMinutes,
            s.PtlOwn,
            s.PtlAll,
            s.ProgrammedFixNames,
            s.DrawnWaypoints,
            s.DrawRouteOrigin,
            s.RubberBandTarget,
            s.RubberBandLabel,
            s.WaypointConditions,
            s.MinifiedCallsigns
        );
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (IsDrawingRoute)
        {
            if (props.IsLeftButtonPressed)
            {
                // Snap check: if clicking near an existing waypoint, ignore (no-op)
                var nearIdx = FindNearestDrawnWaypointIndex(pos);
                if (nearIdx >= 0)
                {
                    e.Handled = true;
                    return;
                }

                var (lat, lon) = Viewport.ScreenToLatLon((float)pos.X, (float)pos.Y);
                RoutePointPlaced?.Invoke(lat, lon);
                e.Handled = true;
                return;
            }

            if (props.IsMiddleButtonPressed)
            {
                var wpIdx = FindNearestDrawnWaypointIndex(pos);
                if (wpIdx >= 0)
                {
                    RoutePointConditionRequested?.Invoke(wpIdx, pos);
                    e.Handled = true;
                }

                return;
            }

            if (props.IsRightButtonPressed)
            {
                var wpIdx = FindNearestDrawnWaypointIndex(pos);
                if (wpIdx >= 0)
                {
                    RouteWaypointRightClicked?.Invoke(wpIdx, pos);
                }
                else
                {
                    RoutePointUndo?.Invoke();
                }

                e.Handled = true;
                return;
            }

            return;
        }

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
            var ac = FindAircraftAtPoint(pos);
            if (ac is not null)
            {
                AircraftRightClicked?.Invoke(ac.Callsign, pos);
                e.Handled = true;
                return;
            }

            _rightButtonDown = true;
            _rightDragStarted = false;
            _rightPressPos = pos;

            if (IsPanZoomEnabled)
            {
                base.OnPointerPressed(e);
            }

            return;
        }

        if (props.IsLeftButtonPressed)
        {
            if (IsPlacingRangeRing)
            {
                var (lat, lon) = Viewport.ScreenToLatLon((float)pos.X, (float)pos.Y);
                RangeRingPlaced?.Invoke(lat, lon);
                IsPlacingRangeRing = false;
                e.Handled = true;
                return;
            }

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

        var currentPos = e.GetPosition(this);
        _lastPointerPos = currentPos;

        if (ShowFixes || IsDrawingRoute)
        {
            MarkDirty();
        }

        if (_rightButtonDown && !_rightDragStarted)
        {
            var dx = currentPos.X - _rightPressPos.X;
            var dy = currentPos.Y - _rightPressPos.Y;
            if (dx * dx + dy * dy > DragThresholdSq)
            {
                _rightDragStarted = true;
            }
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isDraggingDataBlock)
        {
            _isDraggingDataBlock = false;

            if (!_dragThresholdMet && _dragCallsign is not null)
            {
                if (!_minifiedCallsigns.Remove(_dragCallsign))
                {
                    _minifiedCallsigns.Add(_dragCallsign);
                }

                InvalidateVisual();
            }

            _dragCallsign = null;
            e.Handled = true;
            return;
        }

        if (_rightButtonDown && e.InitialPressMouseButton == MouseButton.Right)
        {
            _rightButtonDown = false;

            if (!_rightDragStarted)
            {
                // Quick right-click (no drag) — show map context menu
                var pos = e.GetPosition(this);
                var (lat, lon) = Viewport.ScreenToLatLon((float)pos.X, (float)pos.Y);
                MapRightClicked?.Invoke(lat, lon, pos);
            }
        }

        base.OnPointerReleased(e);
    }

    public AircraftModel? FindDataBlockAtPoint(Point screenPos)
    {
        if (Aircraft is null)
        {
            return null;
        }

        foreach (var ac in FilterAircraft(Aircraft, ShowTopDown))
        {
            var blockRect = ComputeDataBlockRect(ac);
            if (blockRect.Contains((float)screenPos.X, (float)screenPos.Y))
            {
                return ac;
            }
        }

        return null;
    }

    private SKRect ComputeDataBlockRect(AircraftModel ac)
    {
        var (sx, sy) = Viewport.LatLonToScreen(ac.Latitude, ac.Longitude);

        SKPoint offset = DefaultDataBlockOffset;
        if (_dataBlockOffsets.TryGetValue(ac.Callsign, out var customOffset))
        {
            offset = customOffset;
        }

        float blockX = sx + offset.X;
        float blockY = sy + offset.Y;
        float lineH = _hitTestPaint.TextSize + 2;
        bool isMinified = _minifiedCallsigns.Contains(ac.Callsign);

        if (isMinified)
        {
            var altHundreds = ((int)ac.Altitude / 100).ToString("D3");
            var cwt = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";
            string miniLine = cwt.Length > 0 ? $"{altHundreds} {cwt}" : altHundreds;
            float miniW = _hitTestPaint.MeasureText(miniLine);
            return new SKRect(
                blockX - DataBlockPad,
                blockY - _hitTestPaint.TextSize - DataBlockPad,
                blockX + miniW + DataBlockPad,
                blockY + DataBlockPad
            );
        }

        string line1 = ac.Callsign;
        var altH = ((int)ac.Altitude / 100).ToString("D3");
        var spdTens = ((int)ac.GroundSpeed / 10).ToString("D2");
        var cwtCode = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";
        string line2 = cwtCode.Length > 0 ? $"{altH} {spdTens} {cwtCode}" : $"{altH} {spdTens}";

        float w1 = _hitTestPaint.MeasureText(line1);
        float w2 = _hitTestPaint.MeasureText(line2);
        float textW = MathF.Max(w1, w2);
        int lineCount = 2;

        if (!string.IsNullOrEmpty(ac.OwnerDisplay))
        {
            float w3 = _hitTestPaint.MeasureText(ac.OwnerDisplay);
            textW = MathF.Max(textW, w3);
            lineCount = 3;
        }

        return new SKRect(
            blockX - DataBlockPad,
            blockY - _hitTestPaint.TextSize - DataBlockPad,
            blockX + textW + DataBlockPad,
            blockY + (lineCount - 1) * lineH + DataBlockPad
        );
    }

    public AircraftModel? FindAircraftAtPoint(Point screenPos)
    {
        if (Aircraft is null)
        {
            return null;
        }

        const float hitRadius = 16f;
        AircraftModel? closest = null;
        float closestDist = hitRadius;

        foreach (var ac in FilterAircraft(Aircraft, ShowTopDown))
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

    /// <summary>
    /// Centers the viewport on the radar center and zooms to match RangeNm.
    /// Called when the canvas first gets pixel dimensions (deferred initial fit).
    /// </summary>
    public void FitToRange()
    {
        if (Viewport.PixelWidth < 1 || Viewport.PixelHeight < 1)
        {
            return;
        }

        if (RadarCenterLat == 0 && RadarCenterLon == 0)
        {
            return;
        }

        Viewport.CenterLat = RadarCenterLat;
        Viewport.CenterLon = RadarCenterLon;
        ZoomToRange();
    }

    /// <summary>
    /// Adjusts viewport zoom to match RangeNm without changing the center.
    /// </summary>
    private void ZoomToRange()
    {
        if (Viewport.PixelWidth < 1 || Viewport.PixelHeight < 1)
        {
            return;
        }

        const double defaultPixelsPerDeg = 5000.0;
        var maxPixels = Math.Max(Viewport.PixelWidth, Viewport.PixelHeight);
        var targetZoom = maxPixels * 60.0 / (defaultPixelsPerDeg * RangeNm);
        Viewport.Zoom = Math.Clamp(targetZoom, 0.02, 10000.0);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_initialFitDone)
        {
            UpdateViewRangeNm();
        }
        else if (RadarCenterLat != 0 || RadarCenterLon != 0)
        {
            _initialFitDone = true;
            FitToRange();
        }
    }

    protected override void OnViewportChanged()
    {
        UpdateViewRangeNm();

        if (_initialFitDone)
        {
            // Sync RangeNm back from viewport zoom (suppress re-fit to avoid feedback loop)
            var rounded = Math.Max(1, (int)Math.Round(ViewRangeNm));
            if (Math.Abs(rounded - RangeNm) >= 1)
            {
                _suppressRangeFit = true;
                RangeNm = rounded;
                _suppressRangeFit = false;
            }

            // Sync center back so VM persists the actual panned position
            if (!_suppressCenterSync)
            {
                _suppressCenterSync = true;
                SyncCenterFromViewport();
                _suppressCenterSync = false;
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (IsAdjustingRangeRingSize)
        {
            var direction = e.Delta.Y > 0 ? 1 : -1;
            RangeRingSizeNm = RadarViewModel.CycleRangeRingSize(RangeRingSizeNm, direction);
            MarkDirty();
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsDrawingRoute)
        {
            if (e.Key == Key.Enter)
            {
                RouteConfirmed?.Invoke();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                RouteCancelled?.Invoke();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Back)
            {
                RoutePointUndo?.Invoke();
                e.Handled = true;
                return;
            }
        }

        if (IsAdjustingRangeRingSize && (e.Key == Key.Enter || e.Key == Key.Escape))
        {
            IsAdjustingRangeRingSize = false;
            e.Handled = true;
            return;
        }

        if (IsPlacingRangeRing && e.Key == Key.Escape)
        {
            IsPlacingRangeRing = false;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void SyncCenterFromViewport()
    {
        RadarCenterLat = Viewport.CenterLat;
        RadarCenterLon = Viewport.CenterLon;
    }

    private void UpdateViewRangeNm()
    {
        if (Viewport.PixelWidth < 1 || Viewport.Zoom < 1e-10)
        {
            return;
        }

        const double defaultPixelsPerDeg = 5000.0;
        var maxPixels = Math.Max(Viewport.PixelWidth, Viewport.PixelHeight);
        var rangeNm = maxPixels * 60.0 / (defaultPixelsPerDeg * Viewport.Zoom);
        ViewRangeNm = rangeNm;
    }

    private int FindNearestDrawnWaypointIndex(Point screenPos)
    {
        if (DrawnWaypoints is null)
        {
            return -1;
        }

        const float hitRadius = 20f;
        int bestIdx = -1;
        float bestDist = hitRadius;

        for (int i = 0; i < DrawnWaypoints.Count; i++)
        {
            var wp = DrawnWaypoints[i];
            var (sx, sy) = Viewport.LatLonToScreen(wp.Lat, wp.Lon);
            var dx = (float)screenPos.X - sx;
            var dy = (float)screenPos.Y - sy;
            var dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private static IReadOnlyList<AircraftModel> FilterAircraft(IReadOnlyList<AircraftModel>? aircraft, bool showTopDown)
    {
        if (aircraft is null || aircraft.Count == 0)
        {
            return Array.Empty<AircraftModel>();
        }

        var result = new List<AircraftModel>(aircraft.Count);
        foreach (var ac in aircraft)
        {
            if (ac.IsDelayed)
            {
                continue;
            }

            if (ac.IsOnGround && !showTopDown)
            {
                continue;
            }

            result.Add(ac);
        }

        return result;
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _hitTestPaint.Dispose();
    }
}
