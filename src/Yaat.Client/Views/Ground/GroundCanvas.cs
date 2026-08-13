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

    public static readonly StyledProperty<TaxiRoute?> HoverTaxiRouteProperty = AvaloniaProperty.Register<GroundCanvas, TaxiRoute?>(
        nameof(HoverTaxiRoute)
    );

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

    public static readonly StyledProperty<bool> IsMeasuringProperty = AvaloniaProperty.Register<GroundCanvas, bool>(nameof(IsMeasuring));

    public static readonly StyledProperty<IReadOnlyList<RangeBearingLine>?> RangeBearingLinesProperty = AvaloniaProperty.Register<
        GroundCanvas,
        IReadOnlyList<RangeBearingLine>?
    >(nameof(RangeBearingLines));

    public static readonly StyledProperty<RblPendingAnchor?> MeasureAnchorProperty = AvaloniaProperty.Register<GroundCanvas, RblPendingAnchor?>(
        nameof(MeasureAnchor)
    );

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

    public static readonly StyledProperty<bool> ShowAdwMarkingsProperty = AvaloniaProperty.Register<GroundCanvas, bool>(
        nameof(ShowAdwMarkings),
        defaultValue: true
    );

    public static readonly StyledProperty<double> ViewCenterLatProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(ViewCenterLat));
    public static readonly StyledProperty<double> ViewCenterLonProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(ViewCenterLon));
    public static readonly StyledProperty<double> ViewZoomProperty = AvaloniaProperty.Register<GroundCanvas, double>(
        nameof(ViewZoom),
        defaultValue: 1.0
    );
    public static readonly StyledProperty<double> ViewRotationProperty = AvaloniaProperty.Register<GroundCanvas, double>(nameof(ViewRotation));

    public static readonly StyledProperty<bool> HasSavedViewProperty = AvaloniaProperty.Register<GroundCanvas, bool>(nameof(HasSavedView));

    public static readonly StyledProperty<DatablockDeconflictMode> DeconflictModeProperty = AvaloniaProperty.Register<
        GroundCanvas,
        DatablockDeconflictMode
    >(nameof(DeconflictMode));

    public static readonly StyledProperty<GroundDataBlockViewState?> DataBlockStateProperty = AvaloniaProperty.Register<
        GroundCanvas,
        GroundDataBlockViewState?
    >(nameof(DataBlockState));

    private static readonly IReadOnlyDictionary<string, SKPoint> EmptyOffsets = new Dictionary<string, SKPoint>();

    private readonly GroundRenderer _renderer = new();

    // Unbound fallback (bare-canvas tests, detached windows). The bound view-model state is the
    // session-persistent store; the binding may churn to null on tab detach, so reads always go
    // through State and nothing is ever cleared from a property change.
    private readonly GroundDataBlockViewState _localDataBlockState = new();
    private GroundDataBlockViewState State => DataBlockState ?? _localDataBlockState;

    // Per-frame deconfliction result (callsign -> effective text-origin offset). Written on the UI
    // thread at snapshot build; read by the snapshot copy (draw) and by hit-testing; persists across
    // frames to seed the next pass for stability.
    private readonly Dictionary<string, SKPoint> _resolvedDeconflictOffsets = new();
    private readonly Dictionary<string, SKPoint> _deconflictScratch = new();
    private readonly SKPaint _hitTestPaint = new();
    private readonly SKFont _hitTestFont = PlatformHelper.MonospaceFontBold(12);

    /// <summary>
    /// Measuring pair for the hit-test path. Must stay metric-identical to the renderer's ground
    /// datablock style, or clicks miss the block.
    /// </summary>
    private TextStyle HitTestStyle => new(_hitTestFont, _hitTestPaint);

    public float DatablockTextSize
    {
        get => _renderer.DatablockTextSize;
        set
        {
            _renderer.DatablockTextSize = value;
            // Keep the hit-test font in step with the draw font, or datablock clicks/drags miss
            // whenever the user picks a non-default ground datablock size.
            _hitTestFont.Size = value;
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
    private string? _hoveredAircraftCallsign;
    private bool _initialFitDone;
    private bool _suppressViewSync;
    private bool _isDraggingDataBlock;
    private string? _dragCallsign;
    private SKPoint _dragStartOffset;
    private Point _dragStartMousePos;
    private bool _dragThresholdMet;

    // Click-to-dismiss state for opt-in speech bubbles. See RadarCanvas for the same pattern.
    private string? _bubblePressCallsign;
    private Point _bubblePressPos;
    private const double BubbleClickMaxMovementSq = 25.0;

    // Alt+left-drag distance measuring: the anchor picked on press, and where the press landed so a
    // release can tell a drag from a click.
    private RblEndpoint? _measureDragAnchor;
    private Point _measureDragStart;
    private Point _measurePointerPos;
    private const double MeasureDragThresholdSq = 25.0;

    // Right-button click-vs-drag tracking. A right press starts a pan immediately and only opens a
    // context menu on release if the pointer never moved past the threshold, so both gestures share
    // the button.
    private readonly RightClickGesture _rightClick = new();

    // Pixel radius for deciding a right-click is pointing at an already-drawn measurement.
    private const float MeasurePickRadiusPx = 8f;

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

    public TaxiRoute? HoverTaxiRoute
    {
        get => GetValue(HoverTaxiRouteProperty);
        set => SetValue(HoverTaxiRouteProperty, value);
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

    /// <summary>Opt-in datablock deconfliction mode for this ground view. Bound from GroundViewModel.</summary>
    public DatablockDeconflictMode DeconflictMode
    {
        get => GetValue(DeconflictModeProperty);
        set => SetValue(DeconflictModeProperty, value);
    }

    /// <summary>Session-persistent datablock state shared with the owning view-model (see <see cref="GroundDataBlockViewState"/>).</summary>
    public GroundDataBlockViewState? DataBlockState
    {
        get => GetValue(DataBlockStateProperty);
        set => SetValue(DataBlockStateProperty, value);
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

    /// <summary>Whether the airport's Arrival/Departure Window reference marks are drawn.</summary>
    public bool ShowAdwMarkings
    {
        get => GetValue(ShowAdwMarkingsProperty);
        set => SetValue(ShowAdwMarkingsProperty, value);
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

    /// <summary>True while the distance measuring tool is intercepting clicks to pick endpoints.</summary>
    public bool IsMeasuring
    {
        get => GetValue(IsMeasuringProperty);
        set => SetValue(IsMeasuringProperty, value);
    }

    /// <summary>All placed measurements; only those created in the ground view render here.</summary>
    public IReadOnlyList<RangeBearingLine>? RangeBearingLines
    {
        get => GetValue(RangeBearingLinesProperty);
        set => SetValue(RangeBearingLinesProperty, value);
    }

    /// <summary>
    /// First endpoint of a half-placed measurement, for the rubber-band preview. Only shown here when it
    /// was picked in the ground view.
    /// </summary>
    public RblPendingAnchor? MeasureAnchor
    {
        get => GetValue(MeasureAnchorProperty);
        set => SetValue(MeasureAnchorProperty, value);
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
        State.SurfaceDataBlock(callsign);
        MarkDirty();
    }

    /// <summary>Returns true if the datablock for the given callsign is currently hidden.</summary>
    public bool IsDataBlockHidden(string callsign) => State.IsDataBlockHidden(callsign);

    /// <summary>Toggles the hidden state of the datablock for the given callsign.</summary>
    public void ToggleHiddenDataBlock(string callsign)
    {
        State.ToggleHiddenDataBlock(callsign);
        MarkDirty();
    }

    /// <summary>Sets whether all datablocks start hidden (inverts the hide/show logic).</summary>
    public void SetStartWithAllHidden(bool hidden)
    {
        if (State.SetStartWithAllHidden(hidden))
        {
            MarkDirty();
        }
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

    /// <summary>Fired when the measuring tool picks an endpoint — first click anchors, second completes.</summary>
    public event Action<RblEndpoint>? MeasurePointPicked;

    /// <summary>Fired when an Alt-drag measurement is released, supplying both endpoints at once.</summary>
    public event Action<RblEndpoint, RblEndpoint>? MeasureDragCompleted;

    /// <summary>Fired when the user cancels a half-placed measurement (Escape or right-click).</summary>
    public event Action? MeasureCancelled;

    /// <summary>
    /// Fired when the aircraft under the cursor changes (null when none). Drives the transient
    /// hover taxi-route preview. Not raised while drawing a route.
    /// </summary>
    public event Action<string?>? HoveredAircraftChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LayoutProperty)
        {
            // Per-callsign datablock state deliberately survives this: the Layout binding re-fires
            // on tab detach/reattach, and the view-model owns the real lifecycle clears.
            _initialFitDone = false;
            TryInitialView();
            InvalidateVisual();
        }
        else if (
            change.Property == DataBlockStateProperty
            || change.Property == AircraftProperty
            || change.Property == SelectedAircraftProperty
            || change.Property == HoverTaxiRouteProperty
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
            || change.Property == DeconflictModeProperty
            || change.Property == BackgroundImageProperty
            || change.Property == TowerCabMapProperty
            || change.Property == ShowSatelliteImageProperty
            || change.Property == SatelliteImageBrightnessProperty
            || change.Property == ShowVideoMapOverlayProperty
            || change.Property == VideoMapOverlayBrightnessProperty
            || change.Property == ShowYaatLayoutProperty
            || change.Property == YaatLayoutBrightnessProperty
            || change.Property == ShowAdwMarkingsProperty
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
        TaxiRoute? HoverTaxiRoute,
        TaxiRoute? PreviewRoute,
        TaxiRoute? DrawnRoutePreview,
        TaxiRoute? DrawHoverPreview,
        IReadOnlyList<int>? DrawWaypoints,
        bool IsDrawingRoute,
        IReadOnlyDictionary<string, SKPoint> DataBlockOffsets,
        IReadOnlyDictionary<string, SKPoint> DeconflictOffsets,
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
        int YaatLayoutBrightness,
        bool ShowAdwMarkings,
        IReadOnlyList<ResolvedRbl>? RangeBearingLines,
        ResolvedRbl? PendingRangeBearingLine
    );

    protected override object? CreateRenderSnapshot()
    {
        var state = State;
        var aircraft = SortByZOrder(VisibleAircraft(), state.DataBlockZOrder);
        var deconflictOffsets = RunDeconfliction(aircraft);

        var hiddenDbs = new HashSet<string>();
        if (state.StartWithAllHidden)
        {
            foreach (var ac in aircraft)
            {
                if (!state.ShownDataBlockCallsigns.Contains(ac.Callsign))
                {
                    hiddenDbs.Add(ac.Callsign);
                }
            }
        }
        else
        {
            foreach (var cs in state.HiddenDataBlockCallsigns)
            {
                hiddenDbs.Add(cs);
            }
        }

        List<ResolvedRbl>? measurements = null;
        ResolvedRbl? pendingMeasurement = null;
        var placedMeasurements = RangeBearingLines;
        // A half-placed anchor picked in the other view previews there, not here.
        var measureAnchor = _measureDragAnchor ?? (MeasureAnchor is { View: RblView.Ground } pending ? pending.Endpoint : (RblEndpoint?)null);
        if (placedMeasurements is { Count: > 0 } || measureAnchor is not null)
        {
            var lookup = BuildMeasureLookup();
            if (placedMeasurements is { Count: > 0 })
            {
                measurements = RangeBearingLineResolver.Resolve(
                    placedMeasurements,
                    lookup,
                    GroundViewModel.MeasureUnits,
                    GroundViewModel.MeasureView
                );
            }

            if (measureAnchor is not null)
            {
                var cursor = Viewport.ScreenToLatLon((float)_measurePointerPos.X, (float)_measurePointerPos.Y);
                pendingMeasurement = RangeBearingLineResolver.ResolvePending(
                    measureAnchor,
                    new LatLon(cursor.Lat, cursor.Lon),
                    lookup,
                    GroundViewModel.MeasureUnits
                );
            }
        }

        return new RenderSnapshot(
            Layout,
            aircraft,
            SelectedAircraft,
            _hoveredNodeId,
            _hoveredRunwayEnd,
            HoverTaxiRoute,
            PreviewRoute,
            DrawnRoutePreview,
            DrawHoverPreview,
            DrawWaypoints,
            IsDrawingRoute,
            new Dictionary<string, SKPoint>(state.ManualOffsets),
            deconflictOffsets,
            ShowDebugInfo,
            WeatherInfo,
            ShowRunwayLabels,
            ShowTaxiwayLabels,
            ShowHoldShort,
            ShowParking,
            ShowSpot,
            ShownTaxiRoutes,
            new HashSet<string>(state.HighlightedCallsigns),
            hiddenDbs,
            BackgroundImage,
            TowerCabMap,
            ShowSatelliteImage,
            SatelliteImageBrightness,
            ShowVideoMapOverlay,
            VideoMapOverlayBrightness,
            ShowYaatLayout,
            YaatLayoutBrightness,
            ShowAdwMarkings,
            measurements,
            pendingMeasurement
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
            s.HoverTaxiRoute,
            s.PreviewRoute,
            s.DrawnRoutePreview,
            s.DrawHoverPreview,
            s.DrawWaypoints,
            s.DataBlockOffsets,
            s.DeconflictOffsets,
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
            s.YaatLayoutBrightness,
            s.ShowAdwMarkings
        );

        // Drawn last so a measurement stays readable over aircraft symbols, datablocks, and the surface.
        _renderer.DrawRangeBearingLines(canvas, viewport, s.RangeBearingLines, s.PendingRangeBearingLine);
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

    /// <summary>
    /// The aircraft the Tower Cab view currently shows — the membership filter applied with this
    /// canvas's airport geometry and weather (cloud ceiling) — shared by render and both hit-testers.
    /// </summary>
    private IReadOnlyList<AircraftModel> VisibleAircraft() =>
        FilterActiveAircraft(Aircraft, AirportCenterLat, AirportCenterLon, AirportElevation, GroundRenderer.ResolveAirborneMaxAglFt(WeatherInfo));

    /// <summary>
    /// Aircraft eligible for the Ground (Tower Cab) display, the single chokepoint for both render and
    /// the hit-testers. The view is an out-the-window picture: it shows real aircraft on the surface
    /// and within visual range of the field — within 10 nm laterally and the cloud ceiling / 6,000 ft
    /// AGL vertically (<see cref="GroundRenderer.IsAirborneVisible"/>). On-ground aircraft are always
    /// kept; an airborne aircraft is kept only while inside that bound. The only membership exclusion is
    /// a pure phantom — a CRC <c>DA</c>/<c>VP</c> data block typed for a callsign with no real aircraft
    /// body (<c>IsUnsupported &amp;&amp; !IsGhostOverlay</c>) — because there is no aircraft in space to
    /// see. A ghost overlay (<c>IsGhostOverlay</c>) is attached to a real scenario aircraft, so it is
    /// treated like any other aircraft (which also means it never flickers off the view at rotation).
    /// Delayed-spawn aircraft are hidden until they appear.
    /// </summary>
    private static IReadOnlyList<AircraftModel> FilterActiveAircraft(
        IReadOnlyList<AircraftModel>? aircraft,
        double airportCenterLat,
        double airportCenterLon,
        double airportElevation,
        double airborneMaxAglFt
    )
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

            if (ac.IsUnsupported && !ac.IsGhostOverlay)
            {
                continue;
            }

            if (!ac.IsOnGround && !GroundRenderer.IsAirborneVisible(ac, airportCenterLat, airportCenterLon, airportElevation, airborneMaxAglFt))
            {
                continue;
            }

            result.Add(ac);
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
                State.ManualOffsets[_dragCallsign] = new SKPoint(_dragStartOffset.X + dx, _dragStartOffset.Y + dy);
                MarkDirty();
            }

            e.Handled = true;
            return;
        }

        base.OnPointerMoved(e);
        var hoverPos = e.GetPosition(this);
        _measurePointerPos = hoverPos;
        UpdateHoveredNode(hoverPos);
        UpdateHoveredAircraft(hoverPos);

        // Past the threshold this right-button press is a pan, not a click — suppress the menu on release.
        _rightClick.Move(hoverPos);

        // Keep the half-placed measurement's rubber band glued to the cursor.
        if (MeasureAnchor is not null || _measureDragAnchor is not null)
        {
            MarkDirty();
        }
    }

    /// <summary>
    /// Resolves measurement endpoints against the aircraft this canvas is showing, so a latched endpoint
    /// tracks its aircraft. Built on the UI thread; the resolved list is immutable.
    /// </summary>
    private RblTrackLookup BuildMeasureLookup()
    {
        var aircraft = Aircraft;
        return callsign =>
        {
            if (aircraft is null)
            {
                return null;
            }

            foreach (var ac in aircraft)
            {
                if (string.Equals(ac.Callsign, callsign, StringComparison.Ordinal))
                {
                    return new RblTrack(ac.Position, ac.GroundSpeed);
                }
            }

            return null;
        };
    }

    /// <summary>
    /// Turns a clicked point into a measurement endpoint: an aircraft under the cursor becomes a latched
    /// endpoint that travels with it, anything else becomes a fixed point on the surface.
    /// </summary>
    /// <remarks>
    /// Unlike the taxi-route tools, this deliberately does not snap to the ground graph — measuring the
    /// gap between a wingtip and a hold bar means picking the exact spot the cursor is over.
    /// </remarks>
    public RblEndpoint MeasureEndpointAt(Point pos)
    {
        var aircraft = FindAircraftAtPoint(pos) ?? FindDataBlockAtPoint(pos);
        if (aircraft is not null)
        {
            return RblEndpoint.OnAircraft(aircraft.Callsign);
        }

        var (lat, lon) = Viewport.ScreenToLatLon((float)pos.X, (float)pos.Y);
        return RblEndpoint.AtPoint(new LatLon(lat, lon), "");
    }

    /// <summary>
    /// Slot number of the measurement drawn nearest <paramref name="pos" />, or null when none is within
    /// picking distance. Used to offer "remove" on the right-click menu.
    /// </summary>
    public int? MeasurementSlotAt(Point pos)
    {
        if (RangeBearingLines is not { Count: > 0 } lines)
        {
            return null;
        }

        var resolved = RangeBearingLineResolver.Resolve(lines, BuildMeasureLookup(), GroundViewModel.MeasureUnits, GroundViewModel.MeasureView);
        return RangeBearingHitTest.NearestSlot(resolved, Viewport, (float)pos.X, (float)pos.Y, MeasurePickRadiusPx);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoveredAircraftCallsign(null);
    }

    /// <summary>
    /// Hit-tests the aircraft under the cursor (datablock first, then position symbol) and raises
    /// <see cref="HoveredAircraftChanged"/> when it changes. Suppressed while drawing a route.
    /// </summary>
    private void UpdateHoveredAircraft(Point screenPos)
    {
        var ac = IsDrawingRoute ? null : (FindDataBlockAtPoint(screenPos) ?? FindAircraftAtPoint(screenPos));
        SetHoveredAircraftCallsign(ac?.Callsign);
    }

    private void SetHoveredAircraftCallsign(string? callsign)
    {
        if (string.Equals(_hoveredAircraftCallsign, callsign, StringComparison.Ordinal))
        {
            return;
        }

        _hoveredAircraftCallsign = callsign;
        HoveredAircraftChanged?.Invoke(callsign);
        MarkDirty();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        // Distance measuring. Alt+left-drag measures without arming the tool first; once armed (toolbar
        // button, hotkey, context menu, or .rbl) plain left-clicks pick the endpoints. Both are exclusive
        // modes, so they sit above the datablock, route-drawing, and aircraft rungs.
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            _measureDragAnchor = MeasureEndpointAt(pos);
            _measureDragStart = pos;
            e.Handled = true;
            return;
        }

        if (IsMeasuring && props.IsLeftButtonPressed)
        {
            MeasurePointPicked?.Invoke(MeasureEndpointAt(pos));
            e.Handled = true;
            return;
        }

        // Right button: record the press and let panning start, but decide nothing yet. Which of the two
        // gestures this is — a click that opens a menu, or a drag that pans — is only known on release,
        // once we can see whether the pointer moved. Firing a menu here would mean a right-drag that
        // happens to start on a datablock, aircraft, or node never pans.
        if (props.IsRightButtonPressed)
        {
            _rightClick.Press(pos);

            if (IsPanZoomEnabled)
            {
                base.OnPointerPressed(e);
            }

            return;
        }

        if (props.IsMiddleButtonPressed)
        {
            var hitAc = FindDataBlockAtPoint(pos) ?? FindAircraftAtPoint(pos);
            if (hitAc is not null)
            {
                State.ToggleHighlight(hitAc.Callsign);

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
                _dragStartOffset = State.ManualOffsets.TryGetValue(dataBlockAc.Callsign, out var off) ? off : DataBlockLayout.DefaultOffset;
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

            base.OnPointerPressed(e);
            return;
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
        // A right press that never became a drag is a click; a drag was a pan and owes no menu.
        if (e.InitialPressMouseButton == MouseButton.Right && _rightClick.Release() is { } rightClickPos)
        {
            HandleRightClick(rightClickPos);
        }

        if (_measureDragAnchor is { } measureAnchor && e.InitialPressMouseButton == MouseButton.Left)
        {
            var measureReleasePos = e.GetPosition(this);
            _measureDragAnchor = null;

            var mdx = measureReleasePos.X - _measureDragStart.X;
            var mdy = measureReleasePos.Y - _measureDragStart.Y;
            if ((mdx * mdx) + (mdy * mdy) > MeasureDragThresholdSq)
            {
                MeasureDragCompleted?.Invoke(measureAnchor, MeasureEndpointAt(measureReleasePos));
            }
            else
            {
                // Alt+click without dragging anchors the measurement and leaves the tool armed, so the
                // second endpoint can be picked with an ordinary click.
                MeasurePointPicked?.Invoke(measureAnchor);
            }

            e.Handled = true;
            return;
        }

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
    /// <summary>
    /// Resolves a right-click — one that stayed put rather than becoming a pan — to whatever it landed on.
    /// The single place every ground right-click menu is decided; called from
    /// <see cref="OnPointerReleased" />, never on press.
    /// </summary>
    private bool HandleRightClick(Point screenPos)
    {
        if (IsMeasuring)
        {
            MeasureCancelled?.Invoke();
            return true;
        }

        if (IsDrawingRoute)
        {
            // Right-click finishes the drawn route at the clicked node; anywhere else it does nothing,
            // so the gesture stays free for panning while the route is being laid out.
            var drawNode = FindNodeAtPoint(screenPos);
            if (drawNode is not null)
            {
                DrawNodeFinished?.Invoke(drawNode.Id, screenPos);
                return true;
            }

            return false;
        }

        var dataBlockAc = FindDataBlockAtPoint(screenPos);
        if (dataBlockAc is not null)
        {
            SurfaceDataBlock(dataBlockAc.Callsign);
            AircraftRightClicked?.Invoke(dataBlockAc.Callsign, screenPos);
            return true;
        }

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
        // same Taxi/Takeoff options regardless of which mouse button they used. Needs a selection —
        // the items it offers are taxi/takeoff clearances for the selected aircraft.
        if (SelectedAircraft is not null)
        {
            var threshold = FindRunwayThresholdAtPoint(screenPos);
            if (threshold is { } hit)
            {
                RunwayThresholdRightClicked?.Invoke(hit.RunwayEnd, screenPos);
                return true;
            }
        }

        // Fallback: snap a right-click anywhere to the nearest ground node so the node menu is always
        // reachable, not only within the node hit radius. With an aircraft selected that menu carries the
        // taxi-route and "Warp here" items, letting the controller drop a stuck aircraft onto an open
        // stretch of runway/taxiway that has no graph node under the cursor; with nothing selected it
        // still carries the measuring-tool items. Safe to run unconditionally now that a right *drag*
        // pans instead of opening a menu.
        var nearest = FindNearestNode(screenPos);
        if (nearest is not null)
        {
            NodeRightClicked?.Invoke(nearest.Id, screenPos);
            return true;
        }

        return false;
    }

    public GroundNodeDto? FindNodeAtPoint(Point screenPos)
    {
        const float hitRadius = 20f;
        var nearest = FindNearestNode(screenPos, out float dist);
        return dist <= hitRadius ? nearest : null;
    }

    /// <summary>The ground node closest to <paramref name="screenPos"/> regardless of distance, or null if no layout is loaded.</summary>
    public GroundNodeDto? FindNearestNode(Point screenPos) => FindNearestNode(screenPos, out _);

    private GroundNodeDto? FindNearestNode(Point screenPos, out float distance)
    {
        distance = float.MaxValue;
        if (Layout is null)
        {
            return null;
        }

        GroundNodeDto? closest = null;
        foreach (var node in Layout.Nodes)
        {
            var (sx, sy) = Viewport.LatLonToScreen(node.Latitude, node.Longitude);
            var dx = (float)screenPos.X - sx;
            var dy = (float)screenPos.Y - sy;
            var dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist < distance)
            {
                distance = dist;
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
        var sorted = SortByZOrder(VisibleAircraft(), State.DataBlockZOrder);
        AircraftModel? best = null;

        foreach (var ac in sorted)
        {
            var (sx, sy) = Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

            SKPoint offset = DataBlockLayout.DefaultOffset;
            if (State.ManualOffsets.TryGetValue(ac.Callsign, out var customOffset))
            {
                offset = customOffset;
            }
            else if (DeconflictOffsetFor(ac.Callsign) is { } resolvedOffset)
            {
                offset = resolvedOffset;
            }

            // Match the draw path's airborne flag (GroundRenderer.DrawOneDataBlock) so an airborne
            // aircraft's altitude line is included in the hit rect — otherwise its block is one line
            // shorter than drawn and clicks near the bottom miss.
            var layout = DataBlockLayout.Compute(ac, sx, sy, offset, HitTestStyle, isAirborne: !ac.IsOnGround);
            if (layout.Rect.Contains((float)screenPos.X, (float)screenPos.Y))
            {
                best = ac;
            }
        }

        return best;
    }

    /// <summary>The deconfliction-resolved offset for a callsign, or null when deconfliction is off or absent.</summary>
    private SKPoint? DeconflictOffsetFor(string callsign) =>
        DeconflictMode != DatablockDeconflictMode.Off && _resolvedDeconflictOffsets.TryGetValue(callsign, out var off) ? off : null;

    /// <summary>
    /// Runs the deconfliction pass for the current frame and returns an immutable copy for the snapshot.
    /// Updates <see cref="_resolvedDeconflictOffsets"/> in place so the next frame and the UI-thread
    /// hit-test path read the same result. A no-op (empty) when the mode is Off.
    /// </summary>
    private IReadOnlyDictionary<string, SKPoint> RunDeconfliction(IReadOnlyList<AircraftModel> sorted)
    {
        if (DeconflictMode == DatablockDeconflictMode.Off || Viewport.PixelWidth < 1 || Viewport.PixelHeight < 1)
        {
            _resolvedDeconflictOffsets.Clear();
            return EmptyOffsets;
        }

        var items = BuildDeconflictItems(sorted);
        var bounds = new SKRect(0, 0, Viewport.PixelWidth, Viewport.PixelHeight);
        DatablockDeconfliction.Resolve(
            DeconflictMode,
            items,
            DatablockDeconfliction.Options.Default(bounds),
            _resolvedDeconflictOffsets,
            _deconflictScratch
        );

        _resolvedDeconflictOffsets.Clear();
        foreach (var kvp in _deconflictScratch)
        {
            _resolvedDeconflictOffsets[kvp.Key] = kvp.Value;
        }

        return new Dictionary<string, SKPoint>(_resolvedDeconflictOffsets);
    }

    private List<DatablockDeconfliction.Item> BuildDeconflictItems(IReadOnlyList<AircraftModel> sorted)
    {
        var items = new List<DatablockDeconfliction.Item>(sorted.Count);
        foreach (var ac in sorted)
        {
            var (sx, sy) = Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
            bool hasManual = State.ManualOffsets.TryGetValue(ac.Callsign, out var manualOffset);
            var rectAtOrigin = DataBlockLayout.Compute(ac, 0, 0, SKPoint.Empty, HitTestStyle, isAirborne: !ac.IsOnGround).Rect;
            items.Add(
                new DatablockDeconfliction.Item
                {
                    Callsign = ac.Callsign,
                    Anchor = new SKPoint(sx, sy),
                    RectAtOrigin = rectAtOrigin,
                    PreferredOffset = hasManual ? manualOffset : DataBlockLayout.DefaultOffset,
                    IsPinned = hasManual,
                    IsPriority = ReferenceEquals(ac, SelectedAircraft),
                }
            );
        }

        return items;
    }

    /// <summary>
    /// Clears any manual drag offset for the callsign so its datablock returns to the default placement
    /// (or rejoins automatic deconfliction when a mode is active). Backs the ground "Reset datablock
    /// position" context-menu item.
    /// </summary>
    public void ResetDataBlockOffset(string callsign)
    {
        if (State.ManualOffsets.Remove(callsign))
        {
            MarkDirty();
        }
    }

    /// <summary>Returns true if the callsign's datablock has been manually dragged to a custom position.</summary>
    public bool HasManualDataBlockOffset(string callsign) => State.ManualOffsets.ContainsKey(callsign);

    public AircraftModel? FindAircraftAtPoint(Point screenPos)
    {
        if (Aircraft is null)
        {
            return null;
        }

        const float hitRadius = 28f;
        AircraftModel? closest = null;
        float closestDist = hitRadius;

        foreach (var ac in VisibleAircraft())
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
            var delta = (e.Delta.Y > 0 ? 1.0 : -1.0) * ScrollSensitivity;
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
        _hitTestFont.Dispose();
    }
}
