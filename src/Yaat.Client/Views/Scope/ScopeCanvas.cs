using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope's map canvas. Each frame's <see cref="ScopeFrame"/> is built once on the UI thread in
/// <see cref="CreateRenderSnapshot"/>, drawn by <see cref="ScopeRenderer"/> on the render thread, and kept for hit-testing,
/// so clicks and hover read the same block layouts the user sees.
/// </summary>
public sealed class ScopeCanvas : MapCanvasBase, IDisposable
{
    // Copied from RadarCanvas (ZoomToRange / UpdateViewRangeNm).
    private const double DefaultPixelsPerDeg = 5000.0;

    public static readonly StyledProperty<IReadOnlyList<AircraftModel>?> AircraftProperty = AvaloniaProperty.Register<
        ScopeCanvas,
        IReadOnlyList<AircraftModel>?
    >(nameof(Aircraft));

    public static readonly StyledProperty<AircraftModel?> SelectedAircraftProperty = AvaloniaProperty.Register<ScopeCanvas, AircraftModel?>(
        nameof(SelectedAircraft)
    );

    public static readonly StyledProperty<double> CenterLatProperty = AvaloniaProperty.Register<ScopeCanvas, double>(nameof(CenterLat));

    public static readonly StyledProperty<double> CenterLonProperty = AvaloniaProperty.Register<ScopeCanvas, double>(nameof(CenterLon));

    public static readonly StyledProperty<double> RangeNmProperty = AvaloniaProperty.Register<ScopeCanvas, double>(
        nameof(RangeNm),
        defaultValue: ScopeViewModel.DefaultRangeNm
    );

    /// <summary>Whether altitudes are tinted against the MVA floor at each track's position.</summary>
    public static readonly StyledProperty<bool> ShowMvaAltitudeTintProperty = AvaloniaProperty.Register<ScopeCanvas, bool>(
        nameof(ShowMvaAltitudeTint)
    );

    /// <summary>The facility whose MVA chart wins where charts overlap (the Radar's <c>MvaFacilityId</c>); null for none.</summary>
    public static readonly StyledProperty<string?> MvaFacilityIdProperty = AvaloniaProperty.Register<ScopeCanvas, string?>(nameof(MvaFacilityId));

    /// <summary>Id of the datablock template the scope draws (see <see cref="ScopeTemplates"/>).</summary>
    public static readonly StyledProperty<string> TemplateIdProperty = AvaloniaProperty.Register<ScopeCanvas, string>(
        nameof(TemplateId),
        defaultValue: ScopeTemplates.StarsId
    );

    /// <summary>The position the scope is viewed from; read in <see cref="BuildFrame"/>, never on the render thread.</summary>
    public static readonly StyledProperty<ScopeViewer> ViewerProperty = AvaloniaProperty.Register<ScopeCanvas, ScopeViewer>(
        nameof(Viewer),
        defaultValue: ScopeViewer.None
    );

    /// <summary>The scope's BRITE groups; a change redraws the frame without rebuilding its layout.</summary>
    public static readonly StyledProperty<ScopeBrightness> BrightnessProperty = AvaloniaProperty.Register<ScopeCanvas, ScopeBrightness>(
        nameof(Brightness),
        defaultValue: ScopeBrightness.Default
    );

    /// <summary>
    /// The scope's DB settings: 4TH LINE, hover DWELL, ATPA DIST and the datablock point size. Null reads ATPA and the size from
    /// <see cref="Preferences"/> and keeps line 4's fallback and hover dwell on.
    /// </summary>
    public static readonly StyledProperty<ScopeDbSettings?> DbSettingsProperty = AvaloniaProperty.Register<ScopeCanvas, ScopeDbSettings?>(
        nameof(DbSettings)
    );

    /// <summary>
    /// The zoom band's level for others' tracks (<see cref="ScopeProfile.OthersFull"/> or <see cref="ScopeProfile.OthersPartial"/>);
    /// read in <see cref="BuildFrame"/>, never on the render thread.
    /// </summary>
    public static readonly StyledProperty<string> OthersLevelProperty = AvaloniaProperty.Register<ScopeCanvas, string>(
        nameof(OthersLevel),
        defaultValue: ScopeProfile.OthersFull
    );

    /// <summary>The vector length in effect, in minutes; read in <see cref="BuildFrame"/>, never on the render thread.</summary>
    public static readonly StyledProperty<double> VectorMinutesProperty = AvaloniaProperty.Register<ScopeCanvas, double>(nameof(VectorMinutes));

    /// <summary>The tracks vectors draw on (OWN or ALL); read in <see cref="BuildFrame"/>, never on the render thread.</summary>
    public static readonly StyledProperty<ScopeVectorOwnership> VectorOwnershipProperty = AvaloniaProperty.Register<
        ScopeCanvas,
        ScopeVectorOwnership
    >(nameof(VectorOwnership), defaultValue: ScopeVectorOwnership.Own);

    /// <summary>The history dots in effect per track; read in <see cref="BuildFrame"/>, never on the render thread.</summary>
    public static readonly StyledProperty<int> HistoryCountProperty = AvaloniaProperty.Register<ScopeCanvas, int>(nameof(HistoryCount));

    /// <summary>
    /// The rings the instructor placed on this scope, by callsign (an immutable snapshot); read in <see cref="BuildFrame"/>,
    /// never on the render thread.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyDictionary<string, ScopeTrackRings>> LocalRingsProperty = AvaloniaProperty.Register<
        ScopeCanvas,
        IReadOnlyDictionary<string, ScopeTrackRings>
    >(nameof(LocalRings), defaultValue: ScopeViewModel.NoRings);

    /// <summary>
    /// The callsigns with a PTL on this scope (an immutable snapshot); read in <see cref="BuildFrame"/>, never on the render
    /// thread.
    /// </summary>
    public static readonly StyledProperty<IReadOnlySet<string>> LocalPtlCallsignsProperty = AvaloniaProperty.Register<
        ScopeCanvas,
        IReadOnlySet<string>
    >(nameof(LocalPtlCallsigns), defaultValue: ScopeViewModel.NoPtl);

    /// <summary>
    /// Whether top-down mode is on, drawing ground targets (with their ground-target block), airborne ones below the display
    /// floor and the TDM-only maps (<see cref="TdmOnlyMapIds"/>); read on the UI thread, never on the render thread.
    /// </summary>
    public static readonly StyledProperty<bool> ShowTopDownProperty = AvaloniaProperty.Register<ScopeCanvas, bool>(nameof(ShowTopDown));

    /// <summary>
    /// The ids of the video maps that are TDM-only (<c>ScopeViewModel.TdmOnlyMapIds</c>): <see cref="CreateRenderSnapshot"/>
    /// leaves them out while <see cref="ShowTopDown"/> is off; read on the UI thread, never on the render thread.
    /// </summary>
    public static readonly StyledProperty<IReadOnlySet<string>> TdmOnlyMapIdsProperty = AvaloniaProperty.Register<ScopeCanvas, IReadOnlySet<string>>(
        nameof(TdmOnlyMapIds),
        defaultValue: new HashSet<string>(StringComparer.Ordinal)
    );

    /// <summary>Whether the distance measuring tool is armed (it is global: armed on one surface, armed on all).</summary>
    public static readonly StyledProperty<bool> IsMeasuringProperty = AvaloniaProperty.Register<ScopeCanvas, bool>(nameof(IsMeasuring));

    /// <summary>Every placed measurement; only those taken on the Scope draw here.</summary>
    public static readonly StyledProperty<IReadOnlyList<RangeBearingLine>?> RangeBearingLinesProperty = AvaloniaProperty.Register<
        ScopeCanvas,
        IReadOnlyList<RangeBearingLine>?
    >(nameof(RangeBearingLines));

    /// <summary>The first endpoint of a half-placed measurement; its preview draws here only when it was picked on the Scope.</summary>
    public static readonly StyledProperty<RblPendingAnchor?> MeasureAnchorProperty = AvaloniaProperty.Register<ScopeCanvas, RblPendingAnchor?>(
        nameof(MeasureAnchor)
    );

    private static readonly Dictionary<string, SKPoint> NoOffsets = [];
    private static readonly HashSet<string> NoCallsigns = [];
    private static readonly Dictionary<int, SKRect> NoReadoutRects = [];

    private readonly ScopeRenderer _renderer = new();
    private readonly SKPaint _measurePaint = new();
    private SKFont? _measureFont;

    // Measure readouts: measured with the shared renderer's font; the spots each slot took last frame, for hysteresis. The
    // Scope has no datablock deconfliction, so no readout nudges a block and the nudge map stays empty.
    private readonly SKFont _rblMeasureFont = PlatformHelper.MonospaceFont(RangeBearingRenderer.LabelFontSize);
    private readonly Dictionary<int, RblReadoutSpot> _rblReadoutSpots = [];
    private readonly Dictionary<string, RblNudge> _rblNudgeOffsets = [];
    private ScopeFrame? _lastFrame;
    private string? _hoveredCallsign;
    private string? _hoveredVciCallsign;
    private INotifyCollectionChanged? _watchedAircraft;
    private bool _isAttached;

    // Range/zoom and centre feedback-loop guards, copied from RadarCanvas (docs/radar-rendering.md).
    private bool _initialFitDone;
    private bool _suppressRangeFit;
    private bool _suppressCenterSync;

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

    public bool ShowMvaAltitudeTint
    {
        get => GetValue(ShowMvaAltitudeTintProperty);
        set => SetValue(ShowMvaAltitudeTintProperty, value);
    }

    public string? MvaFacilityId
    {
        get => GetValue(MvaFacilityIdProperty);
        set => SetValue(MvaFacilityIdProperty, value);
    }

    public string TemplateId
    {
        get => GetValue(TemplateIdProperty);
        set => SetValue(TemplateIdProperty, value);
    }

    public ScopeViewer Viewer
    {
        get => GetValue(ViewerProperty);
        set => SetValue(ViewerProperty, value);
    }

    public ScopeBrightness Brightness
    {
        get => GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, value);
    }

    public ScopeDbSettings? DbSettings
    {
        get => GetValue(DbSettingsProperty);
        set => SetValue(DbSettingsProperty, value);
    }

    public double CenterLat
    {
        get => GetValue(CenterLatProperty);
        set => SetValue(CenterLatProperty, value);
    }

    public double CenterLon
    {
        get => GetValue(CenterLonProperty);
        set => SetValue(CenterLonProperty, value);
    }

    public double RangeNm
    {
        get => GetValue(RangeNmProperty);
        set => SetValue(RangeNmProperty, value);
    }

    public string OthersLevel
    {
        get => GetValue(OthersLevelProperty);
        set => SetValue(OthersLevelProperty, value);
    }

    public double VectorMinutes
    {
        get => GetValue(VectorMinutesProperty);
        set => SetValue(VectorMinutesProperty, value);
    }

    public ScopeVectorOwnership VectorOwnership
    {
        get => GetValue(VectorOwnershipProperty);
        set => SetValue(VectorOwnershipProperty, value);
    }

    public int HistoryCount
    {
        get => GetValue(HistoryCountProperty);
        set => SetValue(HistoryCountProperty, value);
    }

    public IReadOnlyDictionary<string, ScopeTrackRings> LocalRings
    {
        get => GetValue(LocalRingsProperty);
        set => SetValue(LocalRingsProperty, value);
    }

    public IReadOnlySet<string> LocalPtlCallsigns
    {
        get => GetValue(LocalPtlCallsignsProperty);
        set => SetValue(LocalPtlCallsignsProperty, value);
    }

    public bool ShowTopDown
    {
        get => GetValue(ShowTopDownProperty);
        set => SetValue(ShowTopDownProperty, value);
    }

    public IReadOnlySet<string> TdmOnlyMapIds
    {
        get => GetValue(TdmOnlyMapIdsProperty);
        set => SetValue(TdmOnlyMapIdsProperty, value);
    }

    public bool IsMeasuring
    {
        get => GetValue(IsMeasuringProperty);
        set => SetValue(IsMeasuringProperty, value);
    }

    public IReadOnlyList<RangeBearingLine>? RangeBearingLines
    {
        get => GetValue(RangeBearingLinesProperty);
        set => SetValue(RangeBearingLinesProperty, value);
    }

    public RblPendingAnchor? MeasureAnchor
    {
        get => GetValue(MeasureAnchorProperty);
        set => SetValue(MeasureAnchorProperty, value);
    }

    /// <summary>
    /// The pointer position the half-placed measurement's preview runs to, in canvas coordinates; null while no position is
    /// known, which draws no preview.
    /// </summary>
    internal Point? MeasureCursor { get; set; }

    /// <summary>
    /// The user's preferences: the datablock font size and the alert overlays (the template comes from <see cref="TemplateId"/>).
    /// Null draws at 12.
    /// </summary>
    public UserPreferences? Preferences { get; set; }

    /// <summary>The scope's datablock state; each block is placed from its manual offsets. Null places every block at the default offset.</summary>
    public RadarDataBlockViewState? DataBlockState { get; set; }

    /// <summary>Video maps to draw.</summary>
    public IReadOnlyList<VideoMapData> VideoMaps { get; set; } = [];

    /// <summary>Brightness category ("A" or "B") by video map id.</summary>
    public IReadOnlyDictionary<string, string> BrightnessLookup { get; set; } = new Dictionary<string, string>();

    /// <summary>Fired when an aircraft's symbol or block is left-clicked.</summary>
    public event Action<string>? AircraftLeftClicked;

    /// <summary>Fired when an aircraft's symbol or block is right-clicked, with the click point.</summary>
    public event Action<string, Point>? AircraftRightClicked;

    /// <summary>
    /// Fired, before <see cref="AircraftLeftClicked"/>, when the callsign field (Field A) of a block drawn with the ERAM template
    /// is left-clicked: the user asks to toggle the instructor's local dwell lock of the block, on this scope only.
    /// </summary>
    public event Action<string>? AircraftDwellLockToggleRequested;

    /// <summary>
    /// Fired in place of <see cref="AircraftLeftClicked"/> when the left click lands on a field of the clicked aircraft's own
    /// block whose action is not <see cref="ScopeFieldAction.None"/>, with the callsign, the action and the click point.
    /// </summary>
    public event Action<string, ScopeFieldAction, Point>? FieldClicked;

    /// <summary>
    /// Fired when an aircraft's symbol or block is middle-clicked, with the callsign and whether the click is on the callsign field
    /// (Field A) of its block drawn with the ERAM template.
    /// </summary>
    public event Action<string, bool>? AircraftMiddleClicked;

    /// <summary>Fired when the map, where no block or target is, is middle-clicked, with the point under the pointer.</summary>
    public event Action<LatLon>? MapMiddleClicked;

    /// <summary>
    /// Fired in place of every other left-click event when <see cref="FillsOnLeftClick"/> answers true and the click lands on an
    /// aircraft's symbol or block: the track is to be filled into the entry.
    /// </summary>
    public event Action<string>? AircraftFillRequested;

    /// <summary>
    /// Fired when <see cref="FillsOnLeftClick"/> answers true and the left click lands on the map, with the point under the
    /// pointer: the point is to be filled into the entry.
    /// </summary>
    public event Action<LatLon>? MapFillRequested;

    /// <summary>
    /// Asked at each left press with the callsign of the aircraft under it (null on the map): true makes the press fill the entry
    /// (<see cref="AircraftFillRequested"/>, <see cref="MapFillRequested"/>) instead of selecting, opening a field menu, toggling
    /// a dwell lock. Null never fills.
    /// </summary>
    internal Func<string?, bool>? FillsOnLeftClick { get; set; }

    /// <summary>Fired for text typed while the canvas has the focus; a handler that takes it marks it handled.</summary>
    public event EventHandler<TextInputEventArgs>? EntryTextInput;

    /// <summary>Fired for a key pressed while the canvas has the focus; a handler that takes it marks it handled.</summary>
    public event EventHandler<KeyEventArgs>? EntryKeyDown;

    protected override void OnTextInput(TextInputEventArgs e)
    {
        EntryTextInput?.Invoke(this, e);
        base.OnTextInput(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        EntryKeyDown?.Invoke(this, e);
        base.OnKeyDown(e);
    }

    /// <summary>
    /// The frame clicks and hover are hit-tested against: the last one drawn, or a fresh one when the traffic or the viewport has
    /// changed since.
    /// </summary>
    internal ScopeFrame CurrentFrame => _lastFrame ??= BuildFrame(BuildPalette());

    /// <summary>The callsign of the track the pointer dwells on, or null. Test seam.</summary>
    internal string? HoveredCallsign => _hoveredCallsign;

    /// <summary>The callsign of the track whose VCI cell the pointer previews, or null. Test seam.</summary>
    internal string? HoveredVciCallsign => _hoveredVciCallsign;

    /// <summary>How many frames the pointer-move handler has built for its dwell hit-test. Test seam.</summary>
    internal int HoverFramesBuilt { get; private set; }

    /// <summary>The traffic collection the canvas is listening to, or null. Test seam.</summary>
    internal INotifyCollectionChanged? WatchedAircraft => _watchedAircraft;

    public void Dispose()
    {
        _renderer.Dispose();
        _measurePaint.Dispose();
        _measureFont?.Dispose();
        _rblMeasureFont.Dispose();
    }

    protected override object? CreateRenderSnapshot()
    {
        ScopePalette palette = BuildPalette();
        ScopeFrame frame = BuildFrame(palette);
        _lastFrame = frame;
        (IReadOnlyList<ResolvedRbl> lines, ResolvedRbl? pending) = ResolveMeasurements();
        return new ScopeRenderSnapshot(
            frame,
            DrawnVideoMaps(),
            // The view model's own copy, never changed after it is set, so the render thread reads it as is.
            BrightnessLookup,
            Brightness,
            DatablockFontSize(),
            palette,
            lines,
            pending,
            PlaceRblReadouts(frame, lines, pending)
        );
    }

    /// <summary>
    /// Resolves this frame's measurements: the lines taken on the Scope, and the half-placed one when its anchor was picked
    /// on the Scope and a cursor position is known.
    /// </summary>
    private (IReadOnlyList<ResolvedRbl> Lines, ResolvedRbl? Pending) ResolveMeasurements()
    {
        IReadOnlyList<RangeBearingLine>? placed = RangeBearingLines;
        // A half-placed anchor picked on another surface previews there, not here.
        RblEndpoint? anchor = MeasureAnchor is { View: ScopeViewModel.MeasureView } pending ? pending.Endpoint : null;
        if ((placed is not { Count: > 0 }) && (anchor is null))
        {
            return ([], null);
        }

        RblTrackLookup lookup = BuildMeasureLookup();
        List<ResolvedRbl> lines = placed is { Count: > 0 }
            ? RangeBearingLineResolver.Resolve(placed, lookup, ScopeViewModel.MeasureUnits, ScopeViewModel.MeasureView)
            : [];
        if ((anchor is null) || (MeasureCursor is not { } cursor))
        {
            return (lines, null);
        }

        (double lat, double lon) = Viewport.ScreenToLatLon((float)cursor.X, (float)cursor.Y);
        return (lines, RangeBearingLineResolver.ResolvePending(anchor, new LatLon(lat, lon), lookup, ScopeViewModel.MeasureUnits));
    }

    /// <summary>Resolves a latched endpoint against the traffic this canvas draws.</summary>
    private RblTrackLookup BuildMeasureLookup()
    {
        IReadOnlyList<AircraftModel>? aircraft = Aircraft;
        return RangeBearingViewState.TrackLookup(callsign => aircraft?.FirstOrDefault(a => a.Callsign == callsign));
    }

    /// <summary>
    /// Places this frame's readouts clear of the Scope's blocks, symbols and each other (<see cref="RblReadoutPlacement"/>),
    /// keeping each slot's spot for the next frame. Returns each readout's rect by slot.
    /// </summary>
    private IReadOnlyDictionary<int, SKRect> PlaceRblReadouts(ScopeFrame frame, IReadOnlyList<ResolvedRbl> lines, ResolvedRbl? pending)
    {
        List<RblReadout> readouts = RangeBearingRenderer.BuildReadouts(lines, pending, Viewport, _rblMeasureFont);
        if (readouts.Count == 0)
        {
            _rblReadoutSpots.Clear();
            _rblNudgeOffsets.Clear();
            return NoReadoutRects;
        }

        return RblReadoutPlacement.PlaceAll(readouts, BuildRblObstacles(frame, Viewport), _rblReadoutSpots, _rblNudgeOffsets);
    }

    /// <summary>
    /// What a readout avoids on the Scope: every block of <paramref name="frame"/>, fixed (the Scope deconflicts no block, so a
    /// readout never nudges one), and every symbol.
    /// </summary>
    internal static RblObstacles BuildRblObstacles(ScopeFrame frame, MapViewport viewport)
    {
        var blocks = new List<SKRect>(frame.Tracks.Count);
        var symbols = new List<SKPoint>(frame.Tracks.Count);
        foreach (ScopeTrack track in frame.Tracks)
        {
            blocks.Add(track.Block.Bounds);
            symbols.Add(track.Symbol);
        }

        return new RblObstacles
        {
            FixedBlocks = blocks,
            PlacedThisFrame = [],
            MovableBlocks = [],
            SymbolCentres = symbols,
            SymbolHalfSide = ScopeFrame.TargetHitRadiusPx,
            MaxLeaderLength = 0,
            ViewSize = new SKSize(viewport.PixelWidth, viewport.PixelHeight),
        };
    }

    /// <summary>
    /// The maps this frame draws: <see cref="VideoMaps"/>, less the TDM-only ones (<see cref="TdmOnlyMapIds"/>) while
    /// <see cref="ShowTopDown"/> is off.
    /// </summary>
    private IReadOnlyList<VideoMapData> DrawnVideoMaps()
    {
        IReadOnlySet<string> tdmOnly = TdmOnlyMapIds;
        if (ShowTopDown || (tdmOnly.Count == 0))
        {
            return VideoMaps;
        }
        return [.. VideoMaps.Where(m => !tdmOnly.Contains(m.MapId))];
    }

    /// <summary>The snapshot the next frame would hand the render thread. Test seam.</summary>
    internal ScopeRenderSnapshot RenderSnapshotForTesting() => (ScopeRenderSnapshot)CreateRenderSnapshot()!;

    protected override void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot)
    {
        if (snapshot is ScopeRenderSnapshot s)
        {
            _renderer.Render(canvas, viewport, s);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AircraftProperty)
        {
            OnAircraftChanged();
        }
        else if (
            (change.Property == SelectedAircraftProperty)
            || (change.Property == ShowMvaAltitudeTintProperty)
            || (change.Property == MvaFacilityIdProperty)
            || (change.Property == TemplateIdProperty)
            || (change.Property == ViewerProperty)
            || (change.Property == OthersLevelProperty)
            || IsLayerProperty(change.Property)
        )
        {
            _lastFrame = null;
            MarkDirty();
        }
        else if (change.Property == DbSettingsProperty)
        {
            if (DbSettings is { HoverDwell: false })
            {
                // Hover dwell off releases the hovered block; a locked block holds at Dwell through the frame.
                _hoveredCallsign = null;
            }

            _lastFrame = null;
            MarkDirty();
        }
        else if ((change.Property == BrightnessProperty) || IsMeasureProperty(change.Property))
        {
            // Colours or measure lines only: the kept frame's layout still holds, so hit-testing keeps it.
            MarkDirty();
        }
        else if ((change.Property == CenterLatProperty) || (change.Property == CenterLonProperty))
        {
            SyncCenterIntoViewport();
        }
        else if ((change.Property == RangeNmProperty) && _initialFitDone && !_suppressRangeFit)
        {
            ZoomToRange();
        }
    }

    /// <summary>
    /// Whether <paramref name="property"/> sets the vector, history, ring, PTL or top-down layer (its TDM-only maps among
    /// them), which the frame lays out.
    /// </summary>
    private static bool IsLayerProperty(AvaloniaProperty property) =>
        (property == VectorMinutesProperty)
        || (property == VectorOwnershipProperty)
        || (property == HistoryCountProperty)
        || (property == LocalRingsProperty)
        || (property == LocalPtlCallsignsProperty)
        || (property == ShowTopDownProperty)
        || (property == TdmOnlyMapIdsProperty);

    /// <summary>Whether <paramref name="property"/> sets the measuring tool's state, which draws over the frame.</summary>
    private static bool IsMeasureProperty(AvaloniaProperty property) =>
        (property == IsMeasuringProperty) || (property == RangeBearingLinesProperty) || (property == MeasureAnchorProperty);

    /// <summary>
    /// Watches the traffic list only while in the visual tree, so a closed scope never stays rooted by the main view
    /// model's collection. An added or removed aircraft drops the kept frame, so the next click sees it.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        WatchAircraft(Aircraft);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        WatchAircraft(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_initialFitDone && ((CenterLat != 0) || (CenterLon != 0)))
        {
            _initialFitDone = true;
            _suppressCenterSync = true;
            ApplyCenter();
            _suppressCenterSync = false;
            ZoomToRange();
        }
    }

    protected override void OnViewportChanged()
    {
        _lastFrame = null;
        if (!_initialFitDone || (Viewport.PixelWidth < 1) || (Viewport.Zoom < 1e-10))
        {
            return;
        }

        float maxPixels = Math.Max(Viewport.PixelWidth, Viewport.PixelHeight);
        int rounded = Math.Max(1, (int)Math.Round(maxPixels * 60.0 / (DefaultPixelsPerDeg * Viewport.Zoom)));
        if (Math.Abs(rounded - RangeNm) >= 1)
        {
            _suppressRangeFit = true;
            RangeNm = rounded;
            _suppressRangeFit = false;
        }

        if (!_suppressCenterSync)
        {
            _suppressCenterSync = true;
            CenterLat = Viewport.CenterLat;
            CenterLon = Viewport.CenterLon;
            _suppressCenterSync = false;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Any press gives the canvas the keyboard, so typing goes to the Scope's entry.
        Focus();
        Point pos = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed)
        {
            OnMiddlePressed(pos);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && TryFillPress(pos))
        {
            e.Handled = true;
            return;
        }

        if ((props.IsLeftButtonPressed || props.IsRightButtonPressed) && PressAircraft(props.IsRightButtonPressed, pos))
        {
            e.Handled = true;
            return;
        }

        base.OnPointerPressed(e);
    }

    // A left or right press on an aircraft: the right opens its menu, the left selects it (OnLeftPressed). False off any aircraft.
    private bool PressAircraft(bool right, Point pos)
    {
        ScopeFrame frame = CurrentFrame;
        if (HitAt(frame, pos) is not { } hit)
        {
            return false;
        }

        if (right)
        {
            AircraftRightClicked?.Invoke(hit.Callsign, pos);
        }
        else
        {
            OnLeftPressed(frame, hit, pos);
        }

        return true;
    }

    // A middle press: on an aircraft, with whether it is on its ERAM Field A; else on the map. The frame is rebuilt, as the
    // press may toggle a highlight it draws.
    private void OnMiddlePressed(Point pos)
    {
        ScopeFrame frame = CurrentFrame;
        if (HitAt(frame, pos) is { } hit)
        {
            AircraftMiddleClicked?.Invoke(hit.Callsign, IsEramCallsignOf(frame, hit, (float)pos.X, (float)pos.Y));
        }
        else
        {
            MapMiddleClicked?.Invoke(PointAt(pos));
        }

        _lastFrame = null;
        MarkDirty();
    }

    // A left press the entry takes (FillsOnLeftClick): an aircraft asks for its track, the map for its point. False when the
    // entry does not take it, so the press goes on as without one.
    private bool TryFillPress(Point pos)
    {
        AircraftModel? hit = HitAt(CurrentFrame, pos);
        if (FillsOnLeftClick?.Invoke(hit?.Callsign) != true)
        {
            return false;
        }

        if (hit is not null)
        {
            AircraftFillRequested?.Invoke(hit.Callsign);
        }
        else
        {
            MapFillRequested?.Invoke(PointAt(pos));
        }

        return true;
    }

    // The aircraft whose block, else whose target, is under the point.
    private static AircraftModel? HitAt(ScopeFrame frame, Point pos) =>
        frame.BlockAt((float)pos.X, (float)pos.Y) ?? frame.TargetAt((float)pos.X, (float)pos.Y);

    private LatLon PointAt(Point pos)
    {
        (double lat, double lon) = Viewport.ScreenToLatLon((float)pos.X, (float)pos.Y);
        return new LatLon(lat, lon);
    }

    // A left press on an aircraft: an ERAM Field A asks for the dwell-lock toggle first; then a field with an action fires
    // FieldClicked, anything else AircraftLeftClicked.
    private void OnLeftPressed(ScopeFrame frame, AircraftModel hit, Point pos)
    {
        if (IsEramCallsignOf(frame, hit, (float)pos.X, (float)pos.Y))
        {
            AircraftDwellLockToggleRequested?.Invoke(hit.Callsign);
            // The lock is drawn from the frame, so the next one reads the toggled state.
            _lastFrame = null;
            MarkDirty();
        }

        ScopeFieldAction action = FieldActionOf(frame, hit, (float)pos.X, (float)pos.Y);
        if (action != ScopeFieldAction.None)
        {
            FieldClicked?.Invoke(hit.Callsign, action, pos);
        }
        else
        {
            AircraftLeftClicked?.Invoke(hit.Callsign);
        }
    }

    /// <summary>
    /// The action of the field under the point when it is on <paramref name="selected"/>'s block, else
    /// <see cref="ScopeFieldAction.None"/>: a lower block's field seen through the top one opens nothing.
    /// </summary>
    private static ScopeFieldAction FieldActionOf(ScopeFrame frame, AircraftModel selected, float x, float y) =>
        ((frame.FieldAt(x, y) is { } field) && ReferenceEquals(field.Aircraft, selected)) ? field.Field.Action : ScopeFieldAction.None;

    /// <summary>
    /// Whether the point is on the callsign field (Field A) of <paramref name="selected"/>'s block, drawn with the ERAM template:
    /// the toggle goes only to the aircraft the click selects, never to a lower block's callsign seen through the top one.
    /// </summary>
    private static bool IsEramCallsignOf(ScopeFrame frame, AircraftModel selected, float x, float y) =>
        (frame.FieldAt(x, y) is { Field.Slot: ScopeFieldSlot.Callsign } field)
        && ReferenceEquals(field.Aircraft, selected)
        && (ScopeOwnerKind.Resolve(frame.TemplateId, selected) == ScopeTemplates.EramId);

    /// <summary>
    /// Dwells the track under the pointer at once, as CRC does, or previews the VCI whose cell it is on, hit-testing the drawn
    /// frame; the frame is rebuilt only when either hovered callsign changes. Panning stays with the base canvas.
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Handled)
        {
            // A pan drag: the map moves with the pointer, so the dwell holds; the next free move re-tests it.
            return;
        }

        Point pos = e.GetPosition(this);
        HoverFramesBuilt += _lastFrame is null ? 1 : 0;
        ScopeHoverHit hit = CurrentFrame.HoverAt((float)pos.X, (float)pos.Y);
        Hover(hit.Dwelled?.Callsign, hit.VciPreviewed?.Callsign);
    }

    /// <summary>Releases the dwell and the VCI preview when the pointer leaves the canvas.</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(null, null);
    }

    private void Hover(string? hitCallsign, string? vciCallsign)
    {
        // With hover dwell off the pointer dwells nothing; the VCI preview is no dwell and stays.
        string? callsign = DbSettings is { HoverDwell: false } ? null : hitCallsign;
        if (
            string.Equals(callsign, _hoveredCallsign, StringComparison.Ordinal)
            && string.Equals(vciCallsign, _hoveredVciCallsign, StringComparison.Ordinal)
        )
        {
            return;
        }

        _hoveredCallsign = callsign;
        _hoveredVciCallsign = vciCallsign;
        _lastFrame = null;
        MarkDirty();
    }

    private ScopeFrame BuildFrame(ScopePalette palette)
    {
        IReadOnlyList<AircraftModel> visible = RadarCanvas.FilterAircraft(Aircraft, ShowTopDown, false, false, null, DateTime.UtcNow);
        ScopeAlertOptions alerts = BuildAlertOptions();
        ScopeDisplayOptions display = BuildDisplayOptions(palette);
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(MeasureFont(DatablockFontSize()), _measurePaint));
        IReadOnlyDictionary<string, SKPoint> offsets = DataBlockState?.ManualOffsets ?? NoOffsets;
        IReadOnlySet<string> highlighted = DataBlockState?.HighlightedCallsigns ?? NoCallsigns;
        return ScopeFrame.Build(
            new ScopeFrameInput(
                visible,
                SelectedAircraft,
                Viewport,
                TemplateId,
                metrics,
                alerts,
                display,
                offsets,
                highlighted,
                MvaFacilityId,
                OthersLevel,
                VectorMinutes,
                VectorOwnership,
                HistoryCount,
                LocalRings,
                _hoveredCallsign,
                VciHoveredCallsign: _hoveredVciCallsign,
                LocalDwellLockedCallsigns: DataBlockState?.DwellLockedCallsigns ?? NoCallsigns,
                ShowTopDown: ShowTopDown,
                LocalPtlCallsigns: LocalPtlCallsigns
            )
        );
    }

    /// <summary>The alert overlays: ATPA from the DB settings (else the preferences), the others from the preferences.</summary>
    private ScopeAlertOptions BuildAlertOptions()
    {
        UserPreferences? prefs = Preferences;
        bool atpa = DbSettings?.Atpa ?? prefs?.ShowAtpa ?? false;
        return new ScopeAlertOptions(atpa, prefs?.ShowConflictAlerts ?? false, prefs?.FlashNoLandingClearance ?? true);
    }

    /// <summary>The display hints from the preferences; line 4's type and destination fallback from the DB settings' 4TH LINE.</summary>
    private ScopeDisplayOptions BuildDisplayOptions(ScopePalette palette)
    {
        UserPreferences? prefs = Preferences;
        bool line4 = DbSettings?.Line4 ?? true;
        return new ScopeDisplayOptions(
            prefs?.MarkStudentLimitedDatablocks ?? true,
            prefs?.ShowTypeMismatchHints ?? true,
            ShowMvaAltitudeTint,
            prefs?.SyncStudentDatablockColors ?? true,
            prefs?.UserInitials,
            palette.AssignedToMe is not null,
            palette.AssignedToOther is not null,
            Viewer,
            EramLine4Type: line4,
            EramLine4Destination: line4
        );
    }

    /// <summary>The user's tints and selected-symbol colour, as today's radar reads them from the preferences.</summary>
    private ScopePalette BuildPalette()
    {
        UserPreferences? prefs = Preferences;
        if (prefs is null)
        {
            return new ScopePalette(null, null, new SKColor(255, 255, 255));
        }

        SKColor? assignedToMe = prefs.AssignmentTintEnabled ? TryParseHex(prefs.AssignmentTintColor) : null;
        SKColor? assignedToOther = prefs.UnassignedTintEnabled ? TryParseHex(prefs.UnassignedTintColor) : null;
        return new ScopePalette(assignedToMe, assignedToOther, TryParseHex(prefs.SelectedColor) ?? new SKColor(255, 255, 255));
    }

    private static SKColor? TryParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        if (SKColor.TryParse(hex, out SKColor color))
        {
            return color;
        }

        return null;
    }

    private void OnAircraftChanged()
    {
        if (_isAttached)
        {
            WatchAircraft(Aircraft);
        }

        _lastFrame = null;
        MarkDirty();
    }

    private void WatchAircraft(IReadOnlyList<AircraftModel>? aircraft)
    {
        _watchedAircraft?.CollectionChanged -= OnAircraftCollectionChanged;

        _watchedAircraft = aircraft as INotifyCollectionChanged;
        _watchedAircraft?.CollectionChanged += OnAircraftCollectionChanged;
    }

    private void OnAircraftCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => _lastFrame = null;

    // The DB settings' CHAR, else the preference; one size for the measure font and the drawn font.
    private float DatablockFontSize() => DbSettings?.CharSize ?? Preferences?.RadarDatablockFontSize ?? 12;

    private SKFont MeasureFont(float size)
    {
        if ((_measureFont is null) || (Math.Abs(_measureFont.Size - size) > 0.01f))
        {
            _measureFont?.Dispose();
            _measureFont = PlatformHelper.MonospaceFontBold(size);
        }
        return _measureFont;
    }

    private void SyncCenterIntoViewport()
    {
        if (_suppressCenterSync || ((CenterLat == 0) && (CenterLon == 0)) || (Viewport.PixelWidth < 1) || (Viewport.PixelHeight < 1))
        {
            return;
        }

        _suppressCenterSync = true;
        ApplyCenter();
        if (!_initialFitDone)
        {
            _initialFitDone = true;
            ZoomToRange();
        }
        _suppressCenterSync = false;
        _lastFrame = null;
        MarkDirty();
    }

    private void ApplyCenter()
    {
        Viewport.RotationDeg = MagneticDeclination.GetDeclination(CenterLat, CenterLon);
        Viewport.CenterLat = CenterLat;
        Viewport.CenterLon = CenterLon;
    }

    private void ZoomToRange()
    {
        if ((Viewport.PixelWidth < 1) || (Viewport.PixelHeight < 1) || (RangeNm <= 0))
        {
            return;
        }

        float maxPixels = Math.Max(Viewport.PixelWidth, Viewport.PixelHeight);
        Viewport.Zoom = Math.Clamp(maxPixels * 60.0 / (DefaultPixelsPerDeg * RangeNm), 0.02, 10000.0);
        _lastFrame = null;
        InvalidateVisual();
    }
}
