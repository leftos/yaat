using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

// ReSharper disable MemberCanBePrivate.Global — Avalonia styled properties must be public

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

    public static readonly StyledProperty<TaxiRoute?> DrawHoverPreviewProperty = AvaloniaProperty.Register<GroundCanvas, TaxiRoute?>(
        nameof(DrawHoverPreview)
    );

    public static readonly StyledProperty<IReadOnlyList<ShownTaxiRouteEntry>?> ShownTaxiRoutesProperty = AvaloniaProperty.Register<
        GroundCanvas,
        IReadOnlyList<ShownTaxiRouteEntry>?
    >(nameof(ShownTaxiRoutes));

    public static readonly StyledProperty<bool> ShowDebugInfoProperty = AvaloniaProperty.Register<GroundCanvas, bool>(nameof(ShowDebugInfo));

    public static readonly StyledProperty<WeatherDisplayInfo?> WeatherInfoProperty = AvaloniaProperty.Register<GroundCanvas, WeatherDisplayInfo?>(
        nameof(WeatherInfo)
    );

    public static readonly StyledProperty<bool> ShowRunwayLabelsProperty = AvaloniaProperty.Register<GroundCanvas, bool>(
        nameof(ShowRunwayLabels),
        defaultValue: true
    );

    public static readonly StyledProperty<bool> ShowTaxiwayLabelsProperty = AvaloniaProperty.Register<GroundCanvas, bool>(
        nameof(ShowTaxiwayLabels),
        defaultValue: true
    );

    public static readonly StyledProperty<GroundFilterMode> ShowHoldShortProperty = AvaloniaProperty.Register<GroundCanvas, GroundFilterMode>(
        nameof(ShowHoldShort),
        defaultValue: GroundFilterMode.LabelsAndIcons
    );

    public static readonly StyledProperty<GroundFilterMode> ShowParkingProperty = AvaloniaProperty.Register<GroundCanvas, GroundFilterMode>(
        nameof(ShowParking),
        defaultValue: GroundFilterMode.LabelsAndIcons
    );

    public static readonly StyledProperty<GroundFilterMode> ShowSpotProperty = AvaloniaProperty.Register<GroundCanvas, GroundFilterMode>(
        nameof(ShowSpot),
        defaultValue: GroundFilterMode.LabelsAndIcons
    );

    public static readonly StyledProperty<GroundColorScheme> ColorSchemeProperty = AvaloniaProperty.Register<GroundCanvas, GroundColorScheme>(
        nameof(ColorScheme),
        defaultValue: GroundColorScheme.Default
    );

    public static readonly StyledProperty<bool> IsPanZoomLockedProperty = AvaloniaProperty.Register<GroundCanvas, bool>(nameof(IsPanZoomLocked));

    public static readonly StyledProperty<TowerCabImage?> BackgroundImageProperty = AvaloniaProperty.Register<GroundCanvas, TowerCabImage?>(
        nameof(BackgroundImage)
    );

    public static readonly StyledProperty<TowerCabMapData?> TowerCabMapProperty = AvaloniaProperty.Register<GroundCanvas, TowerCabMapData?>(
        nameof(TowerCabMap)
    );

    public static readonly StyledProperty<bool> ShowSatelliteImageProperty = AvaloniaProperty.Register<GroundCanvas, bool>(
        nameof(ShowSatelliteImage)
    );

    public static readonly StyledProperty<int> SatelliteImageBrightnessProperty = AvaloniaProperty.Register<GroundCanvas, int>(
        nameof(SatelliteImageBrightness),
        defaultValue: 50
    );

    public static readonly StyledProperty<bool> ShowVideoMapOverlayProperty = AvaloniaProperty.Register<GroundCanvas, bool>(
        nameof(ShowVideoMapOverlay)
    );

    public static readonly StyledProperty<int> VideoMapOverlayBrightnessProperty = AvaloniaProperty.Register<GroundCanvas, int>(
        nameof(VideoMapOverlayBrightness),
        defaultValue: 70
    );

    public static readonly StyledProperty<bool> ShowYaatLayoutProperty = AvaloniaProperty.Register<GroundCanvas, bool>(
        nameof(ShowYaatLayout),
        defaultValue: true
    );

    public static readonly StyledProperty<int> YaatLayoutBrightnessProperty = AvaloniaProperty.Register<GroundCanvas, int>(
        nameof(YaatLayoutBrightness),
        defaultValue: 100
    );

    public static readonly StyledProperty<double> ViewCenterLatProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(ViewCenterLat));
    public static readonly StyledProperty<double> ViewCenterLonProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(ViewCenterLon));
    public static readonly StyledProperty<double> ViewZoomProperty = AvaloniaProperty.Register<GroundCanvas, double>(
        nameof(ViewZoom),
        defaultValue: 1.0
    );
    public static readonly StyledProperty<double> ViewRotationProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(ViewRotation));

    public static readonly StyledProperty<bool> HasSavedViewProperty = AvaloniaProperty.Register<GroundCanvas, bool>(nameof(HasSavedView));

    private readonly GroundRenderer _renderer = new();
    private readonly Dictionary<string, SKPoint> _dataBlockOffsets = new();
    private readonly SKPaint _hitTestPaint = new() { TextSize = 12, Typeface = PlatformHelper.MonospaceTypefaceBold };

    public float DatablockTextSize
    {
        get => _renderer.DatablockTextSize;
        set
        {
            _renderer.DatablockTextSize = value;
            MarkDirty();
        }
    }

    public float LabelTextSize
    {
        get => _renderer.LabelTextSize;
        set
        {
            _renderer.LabelTextSize = value;
            MarkDirty();
        }
    }

    public bool ShowSpeechBubbles
    {
        get => _renderer.ShowSpeechBubbles;
        set
        {
            _renderer.ShowSpeechBubbles = value;
            MarkDirty();
        }
    }

    public IReadOnlyDictionary<string, SKRect> LastBubbleRects => _renderer.LastBubbleRects;

    public AircraftModel? FindBubbleAircraftAtPoint(Point screenPos)
    {
        if (Aircraft is null || LastBubbleRects.Count == 0)
        {
            return null;
        }

        foreach (var ac in Aircraft)
        {
            if (LastBubbleRects.TryGetValue(ac.Callsign, out var rect) && rect.Contains((float)screenPos.X, (float)screenPos.Y))
            {
                return ac;
            }
        }

        return null;
    }

    private void DismissSpeechBubble(string callsign)
    {
        if (Aircraft is null)
        {
            return;
        }
        foreach (var ac in Aircraft)
        {
            if (ac.Callsign == callsign && ac.SpeechBubble is not null)
            {
                ac.SpeechBubble = null;
                MarkDirty();
                return;
            }
        }
    }

    private int? _hoveredNodeId;
    private string? _hoveredRunwayEnd;
    private bool _initialFitDone;
    private bool _suppressViewSync;
    private bool _isDraggingDataBlock;
    private string? _dragCallsign;
    private SKPoint _dragStartOffset;
    private Point _dragStartMousePos;
    private bool _dragThresholdMet;
    private readonly Dictionary<string, int> _dataBlockZOrder = new();
    private int _nextZOrder = 1;
    private readonly HashSet<string> _highlightedCallsigns = [];
    private readonly HashSet<string> _hiddenDataBlockCallsigns = [];
    private readonly HashSet<string> _shownDataBlockCallsigns = [];
    private bool _startWithAllHidden;

    // Click-to-dismiss state for opt-in speech bubbles. See RadarCanvas for the same pattern.
    private string? _bubblePressCallsign;
    private Point _bubblePressPos;
    private const double BubbleClickMaxMovementSq = 25.0;

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

    public bool ShowRunwayLabels
    {
        get => GetValue(ShowRunwayLabelsProperty);
        set => SetValue(ShowRunwayLabelsProperty, value);
    }

    public bool ShowTaxiwayLabels
    {
        get => GetValue(ShowTaxiwayLabelsProperty);
        set => SetValue(ShowTaxiwayLabelsProperty, value);
    }

    public GroundFilterMode ShowHoldShort
    {
        get => GetValue(ShowHoldShortProperty);
        set => SetValue(ShowHoldShortProperty, value);
    }

    public GroundFilterMode ShowParking
    {
        get => GetValue(ShowParkingProperty);
        set => SetValue(ShowParkingProperty, value);
    }

    public GroundFilterMode ShowSpot
    {
        get => GetValue(ShowSpotProperty);
        set => SetValue(ShowSpotProperty, value);
    }

    public GroundColorScheme ColorScheme
    {
        get => GetValue(ColorSchemeProperty);
        set => SetValue(ColorSchemeProperty, value);
    }

    public TowerCabImage? BackgroundImage
    {
        get => GetValue(BackgroundImageProperty);
        set => SetValue(BackgroundImageProperty, value);
    }

    public TowerCabMapData? TowerCabMap
    {
        get => GetValue(TowerCabMapProperty);
        set => SetValue(TowerCabMapProperty, value);
    }

    public bool ShowSatelliteImage
    {
        get => GetValue(ShowSatelliteImageProperty);
        set => SetValue(ShowSatelliteImageProperty, value);
    }

    public int SatelliteImageBrightness
    {
        get => GetValue(SatelliteImageBrightnessProperty);
        set => SetValue(SatelliteImageBrightnessProperty, value);
    }

    public bool ShowVideoMapOverlay
    {
        get => GetValue(ShowVideoMapOverlayProperty);
        set => SetValue(ShowVideoMapOverlayProperty, value);
    }

    public int VideoMapOverlayBrightness
    {
        get => GetValue(VideoMapOverlayBrightnessProperty);
        set => SetValue(VideoMapOverlayBrightnessProperty, value);
    }

    public bool ShowYaatLayout
    {
        get => GetValue(ShowYaatLayoutProperty);
        set => SetValue(ShowYaatLayoutProperty, value);
    }

    public int YaatLayoutBrightness
    {
        get => GetValue(YaatLayoutBrightnessProperty);
        set => SetValue(YaatLayoutBrightnessProperty, value);
    }

    public bool IsPanZoomLocked
    {
        get => GetValue(IsPanZoomLockedProperty);
        set => SetValue(IsPanZoomLockedProperty, value);
    }

    public double ViewCenterLat
    {
        get => GetValue(ViewCenterLatProperty);
        set => SetValue(ViewCenterLatProperty, value);
    }

    public double ViewCenterLon
    {
        get => GetValue(ViewCenterLonProperty);
        set => SetValue(ViewCenterLonProperty, value);
    }

    public double ViewZoom
    {
        get => GetValue(ViewZoomProperty);
        set => SetValue(ViewZoomProperty, value);
    }

    public double ViewRotation
    {
        get => GetValue(ViewRotationProperty);
        set => SetValue(ViewRotationProperty, value);
    }

    public bool HasSavedView
    {
        get => GetValue(HasSavedViewProperty);
        set => SetValue(HasSavedViewProperty, value);
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

    public TaxiRoute? DrawHoverPreview
    {
        get => GetValue(DrawHoverPreviewProperty);
        set => SetValue(DrawHoverPreviewProperty, value);
    }

    public IReadOnlyList<ShownTaxiRouteEntry>? ShownTaxiRoutes
    {
        get => GetValue(ShownTaxiRoutesProperty);
        set => SetValue(ShownTaxiRoutesProperty, value);
    }

    public int? HoveredNodeId => _hoveredNodeId;

    /// <summary>Surfaces the datablock for the given callsign to the top of the Z-order.</summary>
    public void SurfaceDataBlock(string callsign)
    {
        _dataBlockZOrder[callsign] = _nextZOrder++;
        MarkDirty();
    }

    /// <summary>Returns true if the datablock for the given callsign is currently hidden.</summary>
    public bool IsDataBlockHidden(string callsign)
    {
        return _startWithAllHidden ? !_shownDataBlockCallsigns.Contains(callsign) : _hiddenDataBlockCallsigns.Contains(callsign);
    }

    /// <summary>Toggles the hidden state of the datablock for the given callsign.</summary>
    public void ToggleHiddenDataBlock(string callsign)
    {
        if (_startWithAllHidden)
        {
            if (!_shownDataBlockCallsigns.Remove(callsign))
            {
                _shownDataBlockCallsigns.Add(callsign);
            }
        }
        else
        {
            if (!_hiddenDataBlockCallsigns.Remove(callsign))
            {
                _hiddenDataBlockCallsigns.Add(callsign);
            }
        }

        MarkDirty();
    }

    /// <summary>Sets whether all datablocks start hidden (inverts the hide/show logic).</summary>
    public void SetStartWithAllHidden(bool hidden)
    {
        _startWithAllHidden = hidden;
        _hiddenDataBlockCallsigns.Clear();
        _shownDataBlockCallsigns.Clear();
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

    /// <summary>
    /// Fired when a runway-threshold marker is left-clicked while an aircraft is selected.
    /// Args: runway-end designator (e.g. <c>"28L"</c>), screen position of the click.
    /// </summary>
    public event Action<string, Point>? RunwayThresholdClicked;

    /// <summary>
    /// Fired when a runway-threshold marker is right-clicked while an aircraft is selected.
    /// Args: runway-end designator (e.g. <c>"28L"</c>), screen position of the click.
    /// </summary>
    public event Action<string, Point>? RunwayThresholdRightClicked;

    /// <summary>Fired when a node is left-clicked during draw mode.</summary>
    public event Action<int>? DrawNodeClicked;

    /// <summary>Fired when a node is right-clicked or double-clicked during draw mode (finish).</summary>
    public event Action<int, Point>? DrawNodeFinished;

    /// <summary>Fired when the hovered node changes during draw mode. Args: nodeId (null if no node).</summary>
    public event Action<int?>? DrawNodeHovered;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LayoutProperty)
        {
            _initialFitDone = false;
            _dataBlockOffsets.Clear();
            _highlightedCallsigns.Clear();
            _hiddenDataBlockCallsigns.Clear();
            _shownDataBlockCallsigns.Clear();
            TryInitialView();
            InvalidateVisual();
        }
        else if (
            change.Property == AircraftProperty
            || change.Property == SelectedAircraftProperty
            || change.Property == ActiveRouteProperty
            || change.Property == PreviewRouteProperty
            || change.Property == DrawnRoutePreviewProperty
            || change.Property == DrawHoverPreviewProperty
            || change.Property == DrawWaypointsProperty
            || change.Property == ShownTaxiRoutesProperty
            || change.Property == ShowDebugInfoProperty
            || change.Property == ShowRunwayLabelsProperty
            || change.Property == ShowTaxiwayLabelsProperty
            || change.Property == ShowHoldShortProperty
            || change.Property == ShowParkingProperty
            || change.Property == ShowSpotProperty
            || change.Property == BackgroundImageProperty
            || change.Property == TowerCabMapProperty
            || change.Property == ShowSatelliteImageProperty
            || change.Property == SatelliteImageBrightnessProperty
            || change.Property == ShowVideoMapOverlayProperty
            || change.Property == VideoMapOverlayBrightnessProperty
            || change.Property == ShowYaatLayoutProperty
            || change.Property == YaatLayoutBrightnessProperty
        )
        {
            MarkDirty();
        }
        else if (change.Property == ColorSchemeProperty)
        {
            _renderer.SetColors(ColorScheme);
            MarkDirty();
        }
        else if (change.Property == IsPanZoomLockedProperty)
        {
            IsPanZoomEnabled = !IsPanZoomLocked;
        }
        else if (
            !_suppressViewSync
            && _initialFitDone
            && (
                change.Property == ViewCenterLatProperty
                || change.Property == ViewCenterLonProperty
                || change.Property == ViewZoomProperty
                || change.Property == ViewRotationProperty
            )
        )
        {
            ApplyViewToViewport();
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
        string? HoveredRunwayEnd,
        TaxiRoute? ActiveRoute,
        TaxiRoute? PreviewRoute,
        TaxiRoute? DrawnRoutePreview,
        TaxiRoute? DrawHoverPreview,
        IReadOnlyList<int>? DrawWaypoints,
        bool IsDrawingRoute,
        IReadOnlyDictionary<string, SKPoint> DataBlockOffsets,
        double AirportCenterLat,
        double AirportCenterLon,
        double AirportElevation,
        bool ShowDebugInfo,
        WeatherDisplayInfo? WeatherInfo,
        bool ShowRunwayLabels,
        bool ShowTaxiwayLabels,
        GroundFilterMode ShowHoldShort,
        GroundFilterMode ShowParking,
        GroundFilterMode ShowSpot,
        IReadOnlyList<ShownTaxiRouteEntry>? ShownTaxiRoutes,
        IReadOnlySet<string> HighlightedCallsigns,
        IReadOnlySet<string> HiddenDataBlockCallsigns,
        TowerCabImage? BackgroundImage,
        TowerCabMapData? TowerCabMap,
        bool ShowSatelliteImage,
        int SatelliteImageBrightness,
        bool ShowVideoMapOverlay,
        int VideoMapOverlayBrightness,
        bool ShowYaatLayout,
        int YaatLayoutBrightness
    );

    protected override object? CreateRenderSnapshot()
    {
        var aircraft = SortByZOrder(FilterActiveAircraft(Aircraft), _dataBlockZOrder);

        var hiddenDbs = new HashSet<string>();
        if (_startWithAllHidden)
        {
            foreach (var ac in aircraft)
            {
                if (!_shownDataBlockCallsigns.Contains(ac.Callsign))
                {
                    hiddenDbs.Add(ac.Callsign);
                }
            }
        }
        else
        {
            foreach (var cs in _hiddenDataBlockCallsigns)
            {
                hiddenDbs.Add(cs);
            }
        }

        return new RenderSnapshot(
            Layout,
            aircraft,
            SelectedAircraft,
            _hoveredNodeId,
            _hoveredRunwayEnd,
            ActiveRoute,
            PreviewRoute,
            DrawnRoutePreview,
            DrawHoverPreview,
            DrawWaypoints,
            IsDrawingRoute,
            new Dictionary<string, SKPoint>(_dataBlockOffsets),
            AirportCenterLat,
            AirportCenterLon,
            AirportElevation,
            ShowDebugInfo,
            WeatherInfo,
            ShowRunwayLabels,
            ShowTaxiwayLabels,
            ShowHoldShort,
            ShowParking,
            ShowSpot,
            ShownTaxiRoutes,
            new HashSet<string>(_highlightedCallsigns),
            hiddenDbs,
            BackgroundImage,
            TowerCabMap,
            ShowSatelliteImage,
            SatelliteImageBrightness,
            ShowVideoMapOverlay,
            VideoMapOverlayBrightness,
            ShowYaatLayout,
            YaatLayoutBrightness
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
            s.HoveredRunwayEnd,
            s.ActiveRoute,
            s.PreviewRoute,
            s.DrawnRoutePreview,
            s.DrawHoverPreview,
            s.DrawWaypoints,
            s.DataBlockOffsets,
            s.AirportCenterLat,
            s.AirportCenterLon,
            s.AirportElevation,
            s.ShowDebugInfo,
            s.WeatherInfo,
            s.ShowRunwayLabels,
            s.ShowTaxiwayLabels,
            s.ShowHoldShort,
            s.ShowParking,
            s.ShowSpot,
            s.ShownTaxiRoutes,
            s.HighlightedCallsigns,
            s.HiddenDataBlockCallsigns,
            s.BackgroundImage,
            s.TowerCabMap,
            s.ShowSatelliteImage,
            s.SatelliteImageBrightness,
            s.ShowVideoMapOverlay,
            s.VideoMapOverlayBrightness,
            s.ShowYaatLayout,
            s.YaatLayoutBrightness
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

        if (props.IsMiddleButtonPressed)
        {
            var hitAc = FindDataBlockAtPoint(pos) ?? FindAircraftAtPoint(pos);
            if (hitAc is not null)
            {
                if (!_highlightedCallsigns.Remove(hitAc.Callsign))
                {
                    _highlightedCallsigns.Add(hitAc.Callsign);
                }

                if (IsDataBlockHidden(hitAc.Callsign))
                {
                    ToggleHiddenDataBlock(hitAc.Callsign);
                }

                MarkDirty();
                e.Handled = true;
            }

            return;
        }

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
                if (PlatformHelper.HasActionModifier(e.KeyModifiers))
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
                if (PlatformHelper.HasActionModifier(e.KeyModifiers))
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

            if (SelectedAircraft is not null)
            {
                var threshold = FindRunwayThresholdAtPoint(pos);
                if (threshold is { } hit)
                {
                    RunwayThresholdClicked?.Invoke(hit.RunwayEnd, pos);
                    e.Handled = true;
                    return;
                }
            }

            // Speech-bubble click-to-dismiss: record the press but let pan still initiate.
            // Release-side checks pointer movement and only dismisses on a genuine click.
            var bubbleAc = FindBubbleAircraftAtPoint(pos);
            if (bubbleAc is not null)
            {
                _bubblePressCallsign = bubbleAc.Callsign;
                _bubblePressPos = pos;
            }
            else
            {
                EmptySpaceClicked?.Invoke();
            }
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

        if (_bubblePressCallsign is not null && e.InitialPressMouseButton == MouseButton.Left)
        {
            var releasePos = e.GetPosition(this);
            var dx = releasePos.X - _bubblePressPos.X;
            var dy = releasePos.Y - _bubblePressPos.Y;
            if (dx * dx + dy * dy <= BubbleClickMaxMovementSq)
            {
                DismissSpeechBubble(_bubblePressCallsign);
                e.Handled = true;
            }
            _bubblePressCallsign = null;
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

        // Runway thresholds: mirror the left-click menu so the user gets the
        // same Taxi/Takeoff options regardless of which mouse button they used.
        if (SelectedAircraft is not null)
        {
            var threshold = FindRunwayThresholdAtPoint(screenPos);
            if (threshold is { } hit)
            {
                RunwayThresholdRightClicked?.Invoke(hit.RunwayEnd, screenPos);
                return true;
            }
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

    /// <summary>
    /// Hit-tests the runway-threshold markers (one per runway end). Returns the
    /// closest threshold within hit radius, with its end designator
    /// (e.g. <c>"28L"</c>) and the lat/lon of the threshold point.
    /// Slightly tighter radius than <see cref="FindNodeAtPoint"/> so the marker
    /// doesn't steal clicks from nearby hold-short nodes.
    /// </summary>
    public (string RunwayEnd, LatLon Position)? FindRunwayThresholdAtPoint(Point screenPos)
    {
        if (Layout?.Runways is not { } runways)
        {
            return null;
        }

        const float hitRadius = 18f;
        (string RunwayEnd, LatLon Position)? best = null;
        float bestDist = hitRadius;

        foreach (var rwy in runways)
        {
            if (rwy.Coordinates.Count < 2)
            {
                continue;
            }

            var ids = RunwayIdentifier.Parse(rwy.Name);
            (string End, double Lat, double Lon)[] thresholds =
            [
                (ids.End1, rwy.Coordinates[0][0], rwy.Coordinates[0][1]),
                (ids.End2, rwy.Coordinates[^1][0], rwy.Coordinates[^1][1]),
            ];

            foreach (var (end, lat, lon) in thresholds)
            {
                var (sx, sy) = Viewport.LatLonToScreen(lat, lon);
                var dx = (float)screenPos.X - sx;
                var dy = (float)screenPos.Y - sy;
                var dist = MathF.Sqrt(dx * dx + dy * dy);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = (end, new LatLon(lat, lon));
                }
            }
        }

        return best;
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
            var (sx, sy) = Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

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
            var (sx, sy) = Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
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

            if (IsDrawingRoute)
            {
                DrawNodeHovered?.Invoke(newId);
            }
        }

        // Runway thresholds and runway hold-shorts are clickable destinations
        // when an aircraft is selected — surface a Hand cursor so the user
        // sees they're click targets without needing to read the menu first.
        var runwayEnd = SelectedAircraft is not null ? FindRunwayThresholdAtPoint(screenPos)?.RunwayEnd : null;
        if (runwayEnd != _hoveredRunwayEnd)
        {
            _hoveredRunwayEnd = runwayEnd;
            MarkDirty();
        }

        UpdateCursor(node);
    }

    private void UpdateCursor(GroundNodeDto? hoveredNode)
    {
        if (IsDrawingRoute)
        {
            return;
        }

        bool isClickableTaxiTarget =
            SelectedAircraft is not null
            && (
                _hoveredRunwayEnd is not null
                || (hoveredNode is not null && hoveredNode.Type is "RunwayHoldShort" or "Parking" or "Helipad" or "Spot")
            );

        var desired = isClickableTaxiTarget ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (Cursor != desired)
        {
            Cursor = desired;
        }
    }

    private void ApplyViewToViewport()
    {
        Viewport.CenterLat = ViewCenterLat;
        Viewport.CenterLon = ViewCenterLon;
        Viewport.Zoom = ViewZoom;
        Viewport.RotationDeg = ViewRotation;
        _initialFitDone = true;
        InvalidateVisual();
    }

    public void ResetView()
    {
        _initialFitDone = false;
        FitToLayout();
    }

    public void ResetViewIncludingRotation()
    {
        _initialFitDone = false;
        Viewport.RotationDeg = 0;
        FitToLayout();
    }

    private void TryInitialView()
    {
        if (_initialFitDone)
        {
            return;
        }

        if (Layout is null || Layout.Nodes.Count == 0)
        {
            return;
        }

        if (Viewport.PixelWidth < 1 || Viewport.PixelHeight < 1)
        {
            return;
        }

        if (HasSavedView)
        {
            ApplyViewToViewport();
        }
        else
        {
            FitToLayout();
        }
    }

    private void FitToLayout()
    {
        if (_initialFitDone || Layout is null || Layout.Nodes.Count == 0)
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

        var savedRotation = Viewport.RotationDeg;
        Viewport.FitBounds(minLat, maxLat, minLon, maxLon);
        Viewport.RotationDeg = savedRotation;
        _initialFitDone = true;
        OnViewportChanged();
    }

    protected override void OnViewportChanged()
    {
        // Before the viewport has been initialised (by FitToLayout or ApplyViewToViewport),
        // its CenterLat/Lon/Zoom are still defaults (0,0,1.0). Syncing those back to the
        // bound styled properties would clobber the saved-view values that the viewmodel
        // already pushed in but the canvas hasn't applied yet.
        if (!_initialFitDone)
        {
            return;
        }

        _suppressViewSync = true;
        ViewCenterLat = Viewport.CenterLat;
        ViewCenterLon = Viewport.CenterLon;
        ViewZoom = Viewport.Zoom;
        ViewRotation = Viewport.RotationDeg;
        _suppressViewSync = false;
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
        if (!_initialFitDone)
        {
            TryInitialView();
        }
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _hitTestPaint.Dispose();
    }
}
