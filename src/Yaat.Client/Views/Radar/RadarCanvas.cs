using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Mva;

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

    public static readonly StyledProperty<bool> ShowMvaAltitudeTintProperty = AvaloniaProperty.Register<RadarCanvas, bool>(
        nameof(ShowMvaAltitudeTint)
    );

    public static readonly StyledProperty<IReadOnlyList<(string Name, double Lat, double Lon)>?> FixesProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<(string Name, double Lat, double Lon)>?
    >(nameof(Fixes));

    public static readonly StyledProperty<IReadOnlyList<(string Name, double Lat, double Lon)>?> PinnedMarkersProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<(string Name, double Lat, double Lon)>?
    >(nameof(PinnedMarkers));

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

    public static readonly StyledProperty<bool> IsPanZoomLockedProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(IsPanZoomLocked));

    public static readonly StyledProperty<bool> IsPlacingRangeRingProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(IsPlacingRangeRing));

    public static readonly StyledProperty<double> ViewRangeNmProperty = AvaloniaProperty.Register<RadarCanvas, double>(
        nameof(ViewRangeNm),
        defaultValue: 40
    );

    public static readonly StyledProperty<bool> IsAdjustingRangeRingSizeProperty = AvaloniaProperty.Register<RadarCanvas, bool>(
        nameof(IsAdjustingRangeRingSize)
    );

    public static readonly StyledProperty<bool> ShowTopDownProperty = AvaloniaProperty.Register<RadarCanvas, bool>(nameof(ShowTopDown));

    public static readonly StyledProperty<string?> GroundShownAirportIdProperty = AvaloniaProperty.Register<RadarCanvas, string?>(
        nameof(GroundShownAirportId)
    );

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

    public static readonly StyledProperty<IReadOnlyList<WeatherDisplayInfo>?> WeatherInfoProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<WeatherDisplayInfo>?
    >(nameof(WeatherInfo));

    public static readonly StyledProperty<IReadOnlyList<ShownPathEntry>?> ShownPathsProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<ShownPathEntry>?
    >(nameof(ShownPaths));

    public static readonly StyledProperty<IReadOnlyList<ShownShapeEntry>?> ShownShapesProperty = AvaloniaProperty.Register<
        RadarCanvas,
        IReadOnlyList<ShownShapeEntry>?
    >(nameof(ShownShapes));

    public static readonly StyledProperty<int> HistoryCountProperty = AvaloniaProperty.Register<RadarCanvas, int>(nameof(HistoryCount));

    public static readonly StyledProperty<DatablockDeconflictMode> DeconflictModeProperty = AvaloniaProperty.Register<
        RadarCanvas,
        DatablockDeconflictMode
    >(nameof(DeconflictMode));

    private const float DataBlockPad = 3f;
    private const double DragThresholdSq = 25.0; // 5px threshold for click vs drag
    private static readonly IReadOnlyDictionary<string, SKPoint> EmptyOffsets = new Dictionary<string, SKPoint>();

    private readonly RadarRenderer _renderer = new();
    private readonly Dictionary<string, SKPoint> _dataBlockOffsets = new();

    // Per-frame deconfliction result (callsign -> effective text-origin offset). Written on the UI
    // thread at snapshot build; read by the snapshot copy (draw) and by hit-testing. Persists across
    // frames to seed the next pass for stability.
    private readonly Dictionary<string, SKPoint> _resolvedDeconflictOffsets = new();
    private readonly Dictionary<string, SKPoint> _deconflictScratch = new();
    private readonly SKPaint _hitTestPaint = new() { TextSize = 12, Typeface = Services.PlatformHelper.MonospaceTypefaceBold };
    private bool _initialFitDone;
    private bool _suppressRangeFit;
    private bool _suppressCenterSync;
    private readonly Services.ScrollStepAccumulator _rangeRingSizeScroll = new();
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

    // True when Ctrl was held at the last pointer-move; gates the Ctrl+hover MVA tooltip overlay.
    private bool _ctrlHeldAtPointer;
    private readonly HashSet<string> _minifiedCallsigns = new();
    private readonly HashSet<string> _highlightedCallsigns = new();
    private readonly Dictionary<string, int> _dataBlockZOrder = new();
    private int _nextZOrder = 1;

    // Click-to-dismiss state for the opt-in speech bubble. On press inside a bubble, record
    // the callsign + position; on release, if the pointer barely moved, clear the bubble.
    // A real drag (pan, datablock reposition) exceeds the threshold and leaves the bubble alone.
    private string? _bubblePressCallsign;
    private Point _bubblePressPos;
    private const double BubbleClickMaxMovementSq = 25.0;

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

    public IReadOnlyList<(string Name, double Lat, double Lon)>? PinnedMarkers
    {
        get => GetValue(PinnedMarkersProperty);
        set => SetValue(PinnedMarkersProperty, value);
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

    public IReadOnlyList<WeatherDisplayInfo>? WeatherInfo
    {
        get => GetValue(WeatherInfoProperty);
        set => SetValue(WeatherInfoProperty, value);
    }

    public IReadOnlyList<ShownPathEntry>? ShownPaths
    {
        get => GetValue(ShownPathsProperty);
        set => SetValue(ShownPathsProperty, value);
    }

    public IReadOnlyList<ShownShapeEntry>? ShownShapes
    {
        get => GetValue(ShownShapesProperty);
        set => SetValue(ShownShapesProperty, value);
    }

    public int HistoryCount
    {
        get => GetValue(HistoryCountProperty);
        set => SetValue(HistoryCountProperty, value);
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

    public float HistoryBrightness
    {
        get => _renderer.HistoryBrightness;
        set
        {
            _renderer.HistoryBrightness = value;
            MarkDirty();
        }
    }

    public bool EuroScopeMode
    {
        get => _renderer.EuroScopeMode;
        set
        {
            _renderer.EuroScopeMode = value;
            MarkDirty();
        }
    }

    public bool FlashNoLandingClearance
    {
        get => _renderer.FlashNoLandingClearance;
        set
        {
            _renderer.FlashNoLandingClearance = value;
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

    public bool ShowMvaAltitudeTint
    {
        get => GetValue(ShowMvaAltitudeTintProperty);
        set => SetValue(ShowMvaAltitudeTintProperty, value);
    }

    public bool SyncStudentColors
    {
        get => _renderer.SyncStudentColors;
        set
        {
            _renderer.SyncStudentColors = value;
            MarkDirty();
        }
    }

    public bool MarkStudentLimitedDatablocks
    {
        get => _renderer.MarkStudentLimitedDatablocks;
        set
        {
            _renderer.MarkStudentLimitedDatablocks = value;
            MarkDirty();
        }
    }

    public bool CollapseStudentDatablocks
    {
        get => _renderer.CollapseStudentDatablocks;
        set
        {
            _renderer.CollapseStudentDatablocks = value;
            MarkDirty();
        }
    }

    public bool SyncStudentLeaderDirection
    {
        get => _renderer.SyncStudentLeaderDirection;
        set
        {
            _renderer.SyncStudentLeaderDirection = value;
            MarkDirty();
        }
    }

    /// <summary>Opt-in datablock deconfliction mode for this radar view. Bound from RadarViewModel.</summary>
    public DatablockDeconflictMode DeconflictMode
    {
        get => GetValue(DeconflictModeProperty);
        set => SetValue(DeconflictModeProperty, value);
    }

    /// <summary>
    /// Airport id the user's ground view is currently showing (null when none). A ground
    /// aircraft is normally hidden on the radar, but when it has an active speech bubble and its
    /// airport differs from this value, the radar surfaces it so the bubble isn't missed (#169).
    /// </summary>
    public string? GroundShownAirportId
    {
        get => GetValue(GroundShownAirportIdProperty);
        set => SetValue(GroundShownAirportIdProperty, value);
    }

    private bool _alwaysShowGroundBubblesOnRadar;

    /// <summary>
    /// When true, every ground aircraft with an active speech bubble is surfaced on the radar,
    /// even when a ground view is showing its airport (otherwise such aircraft are surfaced only
    /// when no ground view presents their airport). Driven by the matching user preference.
    /// </summary>
    public bool AlwaysShowGroundBubblesOnRadar
    {
        get => _alwaysShowGroundBubblesOnRadar;
        set
        {
            if (_alwaysShowGroundBubblesOnRadar != value)
            {
                _alwaysShowGroundBubblesOnRadar = value;
                MarkDirty();
            }
        }
    }

    public float DatablockTextSize
    {
        get => _renderer.DatablockTextSize;
        set
        {
            _renderer.DatablockTextSize = value;
            MarkDirty();
        }
    }

    public double TpaConeHalfAngleDegrees
    {
        get => _renderer.TpaConeHalfAngleDegrees;
        set
        {
            _renderer.TpaConeHalfAngleDegrees = value;
            MarkDirty();
        }
    }

    public IReadOnlyDictionary<string, EuroScopeTagResult> LastEuroScopeTags => _renderer.LastEuroScopeTags;

    public IReadOnlyDictionary<string, SKRect> LastBubbleRects => _renderer.LastBubbleRects;

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

    /// <summary>
    /// Returns the aircraft whose speech bubble contains <paramref name="screenPos"/>, or null.
    /// Used by <see cref="OnPointerPressed"/> / <see cref="OnPointerReleased"/> to implement
    /// the click-to-dismiss gesture for opt-in speech bubbles.
    /// </summary>
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

    /// <summary>True while a heading-vector interaction is in progress (cursor preview drawing).</summary>
    public bool IsHeadingModeActive => _renderer.HeadingPreview is not null;

    /// <summary>
    /// Enter EuroScope-style heading mode for the named aircraft. Cursor moves drive the preview.
    /// Two confirm paths exist: drag past <see cref="Flyouts.HeadingModeState.DragThresholdPxSq"/>
    /// then release, or release-without-drag and then left-click the map.
    /// </summary>
    public void EnterHeadingMode(string callsign, Point dragOrigin)
    {
        var (lat, lon) = Viewport.ScreenToLatLon((float)_lastPointerPos.X, (float)_lastPointerPos.Y);
        _renderer.HeadingPreview = new Flyouts.HeadingModeState
        {
            Callsign = callsign,
            CursorPos = new LatLon(lat, lon),
            DragOrigin = dragOrigin,
        };
        Cursor = new Cursor(StandardCursorType.Cross);
        Focus();
        MarkDirty();
    }

    public void ExitHeadingMode()
    {
        if (_renderer.HeadingPreview is null)
        {
            return;
        }
        _renderer.HeadingPreview = null;
        Cursor = Cursor.Default;
        MarkDirty();
    }

    private void ConfirmHeadingAt(Point pos)
    {
        var headingState = _renderer.HeadingPreview;
        if (headingState is null)
        {
            return;
        }
        var ac = Aircraft?.FirstOrDefault(a => string.Equals(a.Callsign, headingState.Callsign, StringComparison.OrdinalIgnoreCase));
        if (ac is null)
        {
            return;
        }
        var (lat, lon) = Viewport.ScreenToLatLon((float)pos.X, (float)pos.Y);
        double trueBearing = GeoMath.BearingTo(ac.Position.Lat, ac.Position.Lon, lat, lon);
        double magBearing = MagneticDeclination.TrueToMagnetic(trueBearing, ac.Position);
        int hdg = Flyouts.HeadingPreviewRenderer.SnapHeadingTo5(magBearing);
        HeadingModeConfirmed?.Invoke(ac.Callsign, hdg);
    }

    /// <summary>Raised when the user clicks the map to confirm a heading vector. Heading is in magnetic degrees [1, 360].</summary>
    public event Action<string, int>? HeadingModeConfirmed;

    public string? LocalUserInitials
    {
        get => _renderer.LocalUserInitials;
        set
        {
            _renderer.LocalUserInitials = value;
            MarkDirty();
        }
    }

    public SKColor? AssignmentTintColor
    {
        get => _renderer.AssignmentTintColor;
        set
        {
            _renderer.AssignmentTintColor = value;
            MarkDirty();
        }
    }

    public SKColor? UnassignedTintColor
    {
        get => _renderer.UnassignedTintColor;
        set
        {
            _renderer.UnassignedTintColor = value;
            MarkDirty();
        }
    }

    public SKColor? SelectedOverrideColor
    {
        get => _renderer.SelectedOverrideColor;
        set
        {
            _renderer.SelectedOverrideColor = value;
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

    /// <summary>Toggles full/mini datablock for the given callsign.</summary>
    public void ToggleMinifiedDataBlock(string callsign)
    {
        if (!_minifiedCallsigns.Remove(callsign))
        {
            _minifiedCallsigns.Add(callsign);
        }

        MarkDirty();
    }

    /// <summary>Returns true if the callsign's datablock is currently minified.</summary>
    public bool IsMinified(string callsign) => _minifiedCallsigns.Contains(callsign);

    /// <summary>
    /// Clears any manual drag offset for the callsign so its datablock returns to the student's leader
    /// direction (when leader-direction sync is on) or the default placement. Backs the radar
    /// "Reset to student position" context-menu item.
    /// </summary>
    public void ResetDataBlockOffset(string callsign)
    {
        if (_dataBlockOffsets.Remove(callsign))
        {
            MarkDirty();
        }
    }

    /// <summary>Returns true if the callsign's datablock has been manually dragged to a custom position.</summary>
    public bool HasManualDataBlockOffset(string callsign) => _dataBlockOffsets.ContainsKey(callsign);

    /// <summary>Surfaces the datablock for the given callsign to the top of the Z-order.</summary>
    public void SurfaceDataBlock(string callsign)
    {
        _dataBlockZOrder[callsign] = _nextZOrder++;
        MarkDirty();
    }

    /// <summary>Fired when an aircraft is right-clicked.</summary>
    public event Action<string, Point>? AircraftRightClicked;

    /// <summary>Fired when empty map space is right-clicked.</summary>
    public event Action<double, double, Point>? MapRightClicked;

    /// <summary>Fired when an aircraft is left-clicked.</summary>
    public event Action<string>? AircraftLeftClicked;

    /// <summary>Fired when an aircraft is Ctrl+left-clicked.</summary>
    public event Action<string>? AircraftCtrlClicked;

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

        // MVA tint is a renderer field read directly during the draw; mirror the bound value onto it.
        if (change.Property == ShowMvaAltitudeTintProperty)
        {
            _renderer.ShowMvaAltitudeTint = ShowMvaAltitudeTint;
        }

        if (
            change.Property == VideoMapsProperty
            || change.Property == AircraftProperty
            || change.Property == SelectedAircraftProperty
            || change.Property == ShowRangeRingsProperty
            || change.Property == RangeNmProperty
            || change.Property == ShowFixesProperty
            || change.Property == ShowMvaAltitudeTintProperty
            || change.Property == FixesProperty
            || change.Property == PinnedMarkersProperty
            || change.Property == RangeRingCenterLatProperty
            || change.Property == RangeRingCenterLonProperty
            || change.Property == RangeRingSizeNmProperty
            || change.Property == ShowTopDownProperty
            || change.Property == GroundShownAirportIdProperty
            || change.Property == PtlLengthMinutesProperty
            || change.Property == PtlOwnProperty
            || change.Property == PtlAllProperty
            || change.Property == ProgrammedFixNamesProperty
            || change.Property == IsDrawingRouteProperty
            || change.Property == DrawnWaypointsProperty
            || change.Property == WaypointConditionsProperty
            || change.Property == ShownPathsProperty
            || change.Property == ShownShapesProperty
            || change.Property == HistoryCountProperty
            || change.Property == DeconflictModeProperty
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
            Viewport.RotationDeg = MagneticDeclination.GetDeclination(RadarCenterLat, RadarCenterLon);

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
        IReadOnlyList<(string Name, double Lat, double Lon)>? PinnedMarkers,
        double RangeRingCenterLat,
        double RangeRingCenterLon,
        double RangeRingSizeNm,
        IReadOnlyDictionary<string, SKPoint> DataBlockOffsets,
        IReadOnlyDictionary<string, SKPoint> DeconflictOffsets,
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
        IReadOnlySet<string> MinifiedCallsigns,
        IReadOnlySet<string> HighlightedCallsigns,
        bool ShowTopDown,
        IReadOnlyList<WeatherDisplayInfo>? WeatherInfo,
        IReadOnlyList<ShownPathEntry>? ShownPaths,
        IReadOnlyList<ShownShapeEntry>? ShownShapes,
        int HistoryCount,
        (string Text, SKPoint Pos)? MvaHover
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
                drawRouteOrigin = (ac.Position.Lat, ac.Position.Lon);
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

        (string Text, SKPoint Pos)? mvaHover = null;
        if (_ctrlHeldAtPointer && IsPointerOver && Viewport.PixelWidth >= 1)
        {
            var (mvaLat, mvaLon) = Viewport.ScreenToLatLon((float)_lastPointerPos.X, (float)_lastPointerPos.Y);
            var sector = MvaDatabase.Default.FindSector(new LatLon(mvaLat, mvaLon));
            string mvaText = sector is null ? "MVA: no data" : $"MVA {sector.FloorFtMsl} ({sector.Sector})";
            mvaHover = (mvaText, new SKPoint((float)_lastPointerPos.X, (float)_lastPointerPos.Y));
        }

        var sorted = SortByZOrder(
            FilterAircraft(Aircraft, ShowTopDown, ShowSpeechBubbles, AlwaysShowGroundBubblesOnRadar, GroundShownAirportId, DateTime.UtcNow),
            _dataBlockZOrder
        );
        var deconflictOffsets = RunDeconfliction(sorted);

        return new RenderSnapshot(
            VideoMaps ?? Array.Empty<VideoMapData>(),
            _brightnessLookup,
            sorted,
            SelectedAircraft,
            ShowRangeRings,
            RangeNm,
            RadarCenterLat,
            RadarCenterLon,
            ShowFixes,
            Fixes,
            PinnedMarkers,
            RangeRingCenterLat,
            RangeRingCenterLon,
            RangeRingSizeNm,
            new Dictionary<string, SKPoint>(_dataBlockOffsets),
            deconflictOffsets,
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
            new HashSet<string>(_minifiedCallsigns),
            new HashSet<string>(_highlightedCallsigns),
            ShowTopDown,
            WeatherInfo,
            ShownPaths,
            ShownShapes,
            HistoryCount,
            mvaHover
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
            s.DeconflictOffsets,
            s.HoveredFixName,
            s.PtlLengthMinutes,
            s.PtlOwn,
            s.PtlAll,
            s.ProgrammedFixNames,
            s.PinnedMarkers,
            s.DrawnWaypoints,
            s.DrawRouteOrigin,
            s.RubberBandTarget,
            s.RubberBandLabel,
            s.WaypointConditions,
            s.MinifiedCallsigns,
            s.HighlightedCallsigns,
            s.ShowTopDown,
            s.WeatherInfo,
            s.ShownPaths,
            s.ShownShapes,
            s.HistoryCount,
            s.MvaHover
        );
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        // Heading mode: click-to-confirm path (only relevant after the user released the
        // initial button without dragging). When the button is still held from entry we let
        // OnPointerReleased handle the drag-style confirm, so we only react here if the
        // button has been released since entry.
        if (_renderer.HeadingPreview is { } headingState && !headingState.ButtonHeld)
        {
            if (props.IsLeftButtonPressed)
            {
                ConfirmHeadingAt(pos);
                ExitHeadingMode();
                e.Handled = true;
                return;
            }
            if (props.IsRightButtonPressed)
            {
                ExitHeadingMode();
                e.Handled = true;
                return;
            }
        }

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
                    e.Handled = true;
                    return;
                }
                // Fall through to base handler for pan/zoom
            }
        }

        if (props.IsMiddleButtonPressed)
        {
            var hitAc = FindDataBlockAtPoint(pos) ?? FindAircraftAtPoint(pos);
            if (hitAc is not null)
            {
                if (!_highlightedCallsigns.Remove(hitAc.Callsign))
                {
                    _highlightedCallsigns.Add(hitAc.Callsign);
                }

                MarkDirty();
                e.Handled = true;
            }

            return;
        }

        // EuroScope mode: try per-field hit test first. When a specific field is hit AND
        // somebody is subscribed to handle it, we fire the event and short-circuit the
        // normal data-block left-click flow. With no subscriber (or no field hit), fall
        // through so existing behaviour (selection, drag, context menu) keeps working.
        if (EuroScopeMode && props.IsLeftButtonPressed && !Services.PlatformHelper.HasActionModifier(e.KeyModifiers))
        {
            var (fieldAc, field) = FindTagFieldAtPoint(pos);
            if (fieldAc is not null && field != TagFieldId.None && EuroScopeFieldClicked is not null)
            {
                SurfaceDataBlock(fieldAc.Callsign);
                EuroScopeFieldClicked.Invoke(fieldAc, field, pos);
                e.Handled = true;
                return;
            }
        }

        // EuroScope mode: per-field right-click. Used today for owner-cell handoff/drop.
        // Fields without a registered handler fall through to the full aircraft right-click menu.
        if (EuroScopeMode && props.IsRightButtonPressed && EuroScopeFieldRightClicked is not null)
        {
            var (fieldAc, field) = FindTagFieldAtPoint(pos);
            if (fieldAc is not null && field != TagFieldId.None)
            {
                SurfaceDataBlock(fieldAc.Callsign);
                if (EuroScopeFieldRightClicked.Invoke(fieldAc, field, pos))
                {
                    e.Handled = true;
                    return;
                }
            }
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
                _dragStartOffset = ComputeDataBlockPlacement(dataBlockAc).Offset;
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

            // Speech-bubble click-to-dismiss: record the press but don't dismiss yet — let
            // pan/drag still initiate via base.OnPointerPressed below. Release-side checks
            // pointer movement and commits the dismiss only when the user really clicked.
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

        // Ctrl+hover surfaces the MVA at the cursor. Repaint while held (so the label follows the
        // cursor) and on the frame Ctrl is released (so the label clears).
        bool ctrlHeld = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (ctrlHeld || _ctrlHeldAtPointer)
        {
            MarkDirty();
        }
        _ctrlHeldAtPointer = ctrlHeld;

        if (_renderer.HeadingPreview is { } headingState)
        {
            var (lat, lon) = Viewport.ScreenToLatLon((float)currentPos.X, (float)currentPos.Y);
            headingState.CursorPos = new LatLon(lat, lon);
            if (headingState.ButtonHeld && !headingState.DraggedPastThreshold)
            {
                double dx = currentPos.X - headingState.DragOrigin.X;
                double dy = currentPos.Y - headingState.DragOrigin.Y;
                if ((dx * dx) + (dy * dy) > Flyouts.HeadingModeState.DragThresholdPxSq)
                {
                    headingState.DraggedPastThreshold = true;
                }
            }
            MarkDirty();
        }

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
        // Heading mode: if we entered via mouse-down and the user dragged past the threshold,
        // a release confirms the heading (drag-style EuroScope flow). If they released without
        // dragging, transition to click-to-confirm (button up but mode still active).
        if (_renderer.HeadingPreview is { } headingState && headingState.ButtonHeld && e.InitialPressMouseButton == MouseButton.Left)
        {
            if (headingState.DraggedPastThreshold)
            {
                ConfirmHeadingAt(e.GetPosition(this));
                ExitHeadingMode();
            }
            else
            {
                headingState.ButtonHeld = false;
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

        // Bubble click-to-dismiss: commit only when pointer barely moved. If the user dragged
        // (panned the map) the bubble stays — they didn't "click" it.
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

    /// <summary>
    /// EuroScope per-field hit test: returns the aircraft and field whose rect contains
    /// the cursor, or (null, None) if the cursor is not over a field. Only meaningful when
    /// <see cref="EuroScopeMode"/> is on.
    /// </summary>
    public (AircraftModel? Aircraft, TagFieldId Field) FindTagFieldAtPoint(Point screenPos)
    {
        if (!EuroScopeMode)
        {
            return (null, TagFieldId.None);
        }

        var tags = LastEuroScopeTags;
        var sorted = SortByZOrder(
            FilterAircraft(Aircraft, ShowTopDown, ShowSpeechBubbles, AlwaysShowGroundBubblesOnRadar, GroundShownAirportId, DateTime.UtcNow),
            _dataBlockZOrder
        );
        AircraftModel? bestAc = null;
        var bestField = TagFieldId.None;

        foreach (var ac in sorted)
        {
            if (!tags.TryGetValue(ac.Callsign, out var result))
            {
                continue;
            }
            if (!result.Bounds.Contains((float)screenPos.X, (float)screenPos.Y))
            {
                continue;
            }
            // Iterate fields to find the most specific hit. Last write wins for overlapping rects
            // (which shouldn't happen but defensive).
            foreach (var f in result.Fields)
            {
                if (f.Rect.Contains((float)screenPos.X, (float)screenPos.Y))
                {
                    bestAc = ac;
                    bestField = f.Field;
                }
            }
        }

        return (bestAc, bestField);
    }

    /// <summary>
    /// Raised when the user left-clicks an interactive EuroScope tag field. Subscribers
    /// dispatch the appropriate flyout/mode (see Flyouts/ folder, added in later phases).
    /// </summary>
    public event Action<AircraftModel, TagFieldId, Point>? EuroScopeFieldClicked;

    /// <summary>
    /// Raised when the user right-clicks an interactive EuroScope tag field. Returning true
    /// suppresses the fallback aircraft-level right-click menu; returning false lets the
    /// normal aircraft context menu open (e.g. for fields without a dedicated right-click action).
    /// </summary>
    public event Func<AircraftModel, TagFieldId, Point, bool>? EuroScopeFieldRightClicked;

    public AircraftModel? FindDataBlockAtPoint(Point screenPos)
    {
        if (Aircraft is null)
        {
            return null;
        }

        // Use z-order-sorted list so the topmost (last-drawn) datablock wins
        var sorted = SortByZOrder(
            FilterAircraft(Aircraft, ShowTopDown, ShowSpeechBubbles, AlwaysShowGroundBubblesOnRadar, GroundShownAirportId, DateTime.UtcNow),
            _dataBlockZOrder
        );
        AircraftModel? best = null;

        foreach (var ac in sorted)
        {
            var blockRect = ComputeDataBlockRect(ac);
            if (blockRect.Contains((float)screenPos.X, (float)screenPos.Y))
            {
                best = ac;
            }
        }

        return best;
    }

    private SKRect ComputeDataBlockRect(AircraftModel ac) => ComputeDataBlockPlacement(ac).Rect;

    /// <summary>
    /// Computes the datablock's effective placement — the text-origin offset from the symbol and the
    /// resulting bounds — using the same offset rules the renderer draws with: a manual drag offset
    /// wins, else the student's leader direction when leader sync is on, else the default upper-right
    /// offset. Hit testing uses the rect; drag start uses the offset so a leader-placed block whose
    /// position hasn't been dragged doesn't jump on the first drag.
    /// </summary>
    private (SKPoint Offset, SKRect Rect) ComputeDataBlockPlacement(AircraftModel ac)
    {
        SKPoint manualOffset = default;
        bool hasManual = _dataBlockOffsets.TryGetValue(ac.Callsign, out manualOffset);

        // EuroScope path uses the bounds the renderer cached during the last frame so
        // hit testing always matches what's actually on screen.
        if (EuroScopeMode && !_minifiedCallsigns.Contains(ac.Callsign) && LastEuroScopeTags.TryGetValue(ac.Callsign, out var esResult))
        {
            return (hasManual ? manualOffset : RadarDatablockLayout.DefaultOffset, esResult.Bounds);
        }

        var (sx, sy) = Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        var rectAtOrigin = ComputeStableRectAtOrigin(ac);
        var deconflict = DeconflictOffsetFor(ac.Callsign);
        var offset = RadarDatablockLayout.ResolveBlockOffset(ac, SyncStudentLeaderDirection, hasManual, manualOffset, rectAtOrigin, deconflict);
        var rect = new SKRect(
            rectAtOrigin.Left + sx + offset.X,
            rectAtOrigin.Top + sy + offset.Y,
            rectAtOrigin.Right + sx + offset.X,
            rectAtOrigin.Bottom + sy + offset.Y
        );
        return (offset, rect);
    }

    /// <summary>
    /// Computes the datablock bounds with the text origin at (0, 0), choosing the minified, collapsed,
    /// or full layout. The full layout reserves the handoff/warning/note slots so the rect stays stable
    /// across the 500 ms flash cadence. Shared by hit-testing and the deconfliction input assembly so
    /// both agree on the block geometry.
    /// </summary>
    private SKRect ComputeStableRectAtOrigin(AircraftModel ac)
    {
        bool isMinified = _minifiedCallsigns.Contains(ac.Callsign);
        bool collapse =
            !isMinified
            && CollapseStudentDatablocks
            && ac.StudentDatablockLevel is Yaat.Sim.StarsDatablockLevel.Limited or Yaat.Sim.StarsDatablockLevel.Partial;

        if (isMinified || collapse)
        {
            IReadOnlyList<string> lines = isMinified ? [RadarDatablockLayout.BuildMinifiedLine(ac)] : RadarDatablockLayout.BuildCollapsedLines(ac);
            return RadarDatablockLayout.ReducedRect(lines, _hitTestPaint, 0, 0);
        }

        return ComputeStableFullRectAtOrigin(ac);
    }

    private SKRect ComputeStableFullRectAtOrigin(AircraftModel ac)
    {
        // Single source of truth with the draw path: RadarDatablockLayout.Compute reserves the handoff
        // slot/width stably (and includes the assigned-to + pointout tokens), so the hit-test rect always
        // matches the drawn block — no hand-mirrored line-string re-derivation.
        string marker = MarkStudentLimitedDatablocks ? RadarDatablockLayout.StudentLevelMarker(ac.StudentDatablockLevel) : "";
        return RadarDatablockLayout.Compute(ac, 0, 0, _hitTestPaint, FlashNoLandingClearance, marker).Rect;
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
            var anchor = new SKPoint(sx, sy);
            bool hasManual = _dataBlockOffsets.TryGetValue(ac.Callsign, out var manualOffset);
            bool isPriority = ReferenceEquals(ac, SelectedAircraft);

            // EuroScope tags are pinned for v1: their per-field hit rects are cached from the draw, so
            // moving the tag would desync field hit-testing. Anchor the cached bounds as an obstacle.
            if (EuroScopeMode && !_minifiedCallsigns.Contains(ac.Callsign) && LastEuroScopeTags.TryGetValue(ac.Callsign, out var es))
            {
                var esOffset = hasManual ? manualOffset : RadarDatablockLayout.DefaultOffset;
                float ox = sx + esOffset.X;
                float oy = sy + esOffset.Y;
                var esAtOrigin = new SKRect(es.Bounds.Left - ox, es.Bounds.Top - oy, es.Bounds.Right - ox, es.Bounds.Bottom - oy);
                items.Add(
                    new DatablockDeconfliction.Item
                    {
                        Callsign = ac.Callsign,
                        Anchor = anchor,
                        RectAtOrigin = esAtOrigin,
                        PreferredOffset = esOffset,
                        IsPinned = true,
                        IsPriority = isPriority,
                    }
                );
                continue;
            }

            var rectAtOrigin = ComputeStableRectAtOrigin(ac);
            var preferred = hasManual
                ? manualOffset
                : RadarDatablockLayout.ResolveBlockOffset(ac, SyncStudentLeaderDirection, false, default, rectAtOrigin, null);
            items.Add(
                new DatablockDeconfliction.Item
                {
                    Callsign = ac.Callsign,
                    Anchor = anchor,
                    RectAtOrigin = rectAtOrigin,
                    PreferredOffset = preferred,
                    IsPinned = hasManual,
                    IsPriority = isPriority,
                }
            );
        }

        return items;
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

        foreach (
            var ac in FilterAircraft(Aircraft, ShowTopDown, ShowSpeechBubbles, AlwaysShowGroundBubblesOnRadar, GroundShownAirportId, DateTime.UtcNow)
        )
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

        Viewport.RotationDeg = MagneticDeclination.GetDeclination(RadarCenterLat, RadarCenterLon);
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
            int steps = _rangeRingSizeScroll.Accumulate(direction, ScrollSensitivity);
            for (int i = 0; i < Math.Abs(steps); i++)
            {
                RangeRingSizeNm = RadarViewModel.CycleRangeRingSize(RangeRingSizeNm, Math.Sign(steps));
            }
            MarkDirty();
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_renderer.HeadingPreview is not null && e.Key == Key.Escape)
        {
            ExitHeadingMode();
            e.Handled = true;
            return;
        }

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
    /// Selects which aircraft the radar draws. Delayed aircraft are always hidden. On-ground
    /// aircraft are hidden in the normal (non-top-down) view, except when an aircraft has an
    /// active speech bubble whose airport isn't currently shown on the user's ground view — those
    /// are surfaced so a SAY / pilot / WARN prompt for a taxiing aircraft isn't missed (#169).
    /// Pure and static so it can be unit-tested without an Avalonia control instance.
    /// </summary>
    public static IReadOnlyList<AircraftModel> FilterAircraft(
        IReadOnlyList<AircraftModel>? aircraft,
        bool showTopDown,
        bool showSpeechBubbles,
        bool alwaysShowGroundBubbles,
        string? groundShownAirportId,
        DateTime nowUtc
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

            if (
                ac.IsOnGround
                && !showTopDown
                && !ShouldSurfaceGroundBubble(ac, showSpeechBubbles, alwaysShowGroundBubbles, groundShownAirportId, nowUtc)
            )
            {
                continue;
            }

            // Airborne but the displayed altitude still rounds to 000 (below the acquisition floor):
            // withhold the target from the radar so it matches CRC STARS' coast/skip. The aircraft
            // remains on the ground view until it climbs above the floor. Top-down (ground) mode keeps
            // showing low traffic.
            if (!ac.IsOnGround && ac.BelowDisplayFloor && !showTopDown)
            {
                continue;
            }

            result.Add(ac);
        }

        return result;
    }

    /// <summary>
    /// True when a ground aircraft carries an active speech bubble the radar should surface. With
    /// <paramref name="alwaysShowGroundBubbles"/> on, any active bubble surfaces; otherwise only
    /// when no open ground view is already showing the aircraft's airport — i.e. its airport
    /// differs from <paramref name="groundShownAirportId"/> (or is unknown).
    /// </summary>
    private static bool ShouldSurfaceGroundBubble(
        AircraftModel ac,
        bool showSpeechBubbles,
        bool alwaysShowGroundBubbles,
        string? groundShownAirportId,
        DateTime nowUtc
    )
    {
        if (!showSpeechBubbles || ac.SpeechBubble is not { } bubble || bubble.ExpiresAt <= nowUtc)
        {
            return false;
        }

        if (alwaysShowGroundBubbles)
        {
            return true;
        }

        return string.IsNullOrEmpty(ac.GroundAirportId)
            || !string.Equals(ac.GroundAirportId, groundShownAirportId, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _hitTestPaint.Dispose();
    }
}
