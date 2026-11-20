using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope's map canvas. Each frame's <see cref="ScopeFrame"/> is built once on the UI thread in
/// <see cref="CreateRenderSnapshot"/>, drawn by <see cref="ScopeRenderer"/> on the render thread, and kept for hit-testing,
/// so clicks and hover read the same block layouts the user sees.
/// </summary>
public sealed partial class ScopeCanvas : MapCanvasBase, IDisposable
{
    // The zoom scale ZoomToRange and UpdateViewRangeNm convert ranges with.
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

    /// <summary>The facility whose MVA chart wins where charts overlap (the Scope map source's <c>MvaFacilityId</c>); null for none.</summary>
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
    /// The scope's DB settings: 4TH LINE, hover DWELL, ATPA DIST, ALERT CONES, MONITOR CONES, CA RING and the datablock point
    /// size. Null reads ATPA DIST and the size from <see cref="Preferences"/> and keeps line 4's fallback, hover dwell, both
    /// ATPA cone toggles and CA RING on.
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
    /// The room's ERAM CRR groups (<c>MainViewModel.CrrGroups</c>, replaced whole); read in <see cref="BuildFrame"/>, never on
    /// the render thread.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<EramCrrGroupHubDto>> CrrGroupsProperty = AvaloniaProperty.Register<
        ScopeCanvas,
        IReadOnlyList<EramCrrGroupHubDto>
    >(nameof(CrrGroups), defaultValue: []);

    /// <summary>
    /// The room's ERAM conflict pairs (<c>MainViewModel.EramConflictAlerts</c>, replaced whole); read in <see cref="BuildFrame"/>,
    /// never on the render thread.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<ConflictAlertDto>> EramConflictsProperty = AvaloniaProperty.Register<
        ScopeCanvas,
        IReadOnlyList<ConflictAlertDto>
    >(nameof(EramConflicts), defaultValue: []);

    /// <summary>The pilot lens's switches; read in <see cref="BuildFrame"/>, never on the render thread.</summary>
    public static readonly StyledProperty<ScopeLensSettings> LensProperty = AvaloniaProperty.Register<ScopeCanvas, ScopeLensSettings>(
        nameof(Lens),
        defaultValue: ScopeLensSettings.Off
    );

    /// <summary>
    /// The callsigns whose intent path is toggled on (an immutable snapshot); read in <see cref="BuildFrame"/>, never on the
    /// render thread.
    /// </summary>
    public static readonly StyledProperty<IReadOnlySet<string>> LensPathCallsignsProperty = AvaloniaProperty.Register<
        ScopeCanvas,
        IReadOnlySet<string>
    >(nameof(LensPathCallsigns), defaultValue: ScopeViewModel.NoLensPaths);

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

    /// <summary>The STARS min-sep pair and its projection (<see cref="ScopeViewModel.MinSep"/>), or null when none is shown.</summary>
    public static readonly StyledProperty<ScopeMinSep?> MinSepProperty = AvaloniaProperty.Register<ScopeCanvas, ScopeMinSep?>(nameof(MinSep));

    /// <summary>The first endpoint of a half-placed measurement; its preview draws here only when it was picked on the Scope.</summary>
    public static readonly StyledProperty<RblPendingAnchor?> MeasureAnchorProperty = AvaloniaProperty.Register<ScopeCanvas, RblPendingAnchor?>(
        nameof(MeasureAnchor)
    );

    private static readonly ILogger Log = AppLog.CreateLogger("ScopeCanvas");

    private static readonly Dictionary<string, SKPoint> NoOffsets = [];
    private static readonly HashSet<string> NoCallsigns = [];
    private static readonly Dictionary<int, SKRect> NoReadoutRects = [];

    // The last value parsed for each colour preference, and the values already warned about: the docked canvas lives for
    // the process, so neither may grow with every value the Settings live preview passes through.
    private readonly Dictionary<string, (string Value, SKColor? Color)> _parsedColors = [];
    private readonly HashSet<(string Preference, string Value)> _warnedColors = [];
    private readonly ScopeRenderer _renderer = new();

    // Held by the render thread for a whole draw and by Dispose while it sets the flag: once Dispose holds it no draw is in
    // flight, so the renderer's Skia objects are safe to free, and every later draw returns at once.
    private readonly Lock _renderLock = new();
    private readonly ScopeIntentPathCache _intentPaths = new();
    private readonly SKPaint _measurePaint = new();
    private SKFont? _measureFont;

    // Measure readouts: measured with the shared renderer's font; the spots each slot took last frame, for hysteresis. A
    // readout never nudges a block, deconflicted or not, so the nudge map stays empty.
    private readonly SKFont _rblMeasureFont = PlatformHelper.MonospaceFont(RangeBearingRenderer.LabelFontSize);
    private readonly Dictionary<int, RblReadoutSpot> _rblReadoutSpots = [];
    private readonly Dictionary<string, RblNudge> _rblNudgeOffsets = [];

    // An Alt+left drag in flight: the endpoint under the press and where the press was. A release farther than the threshold
    // from the press places the whole line; a nearer one anchors it.
    private RblEndpoint? _measureDragAnchor;
    private Point _measureDragStart;
    private const double MeasureDragThresholdSq = 25.0;
    private ScopeFrame? _lastFrame;
    private string? _hoveredCallsign;
    private string? _hoveredVciCallsign;
    private string? _hoveredQueueCallsign;
    private INotifyCollectionChanged? _watchedAircraft;
    private bool _isAttached;

    // Set by Dispose on the UI thread: a draw the render thread already has queued must not touch the Skia objects it freed.
    private volatile bool _disposed;

    // Range/zoom and centre feedback-loop guards.
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

    public IReadOnlyList<EramCrrGroupHubDto> CrrGroups
    {
        get => GetValue(CrrGroupsProperty);
        set => SetValue(CrrGroupsProperty, value);
    }

    public IReadOnlyList<ConflictAlertDto> EramConflicts
    {
        get => GetValue(EramConflictsProperty);
        set => SetValue(EramConflictsProperty, value);
    }

    public ScopeLensSettings Lens
    {
        get => GetValue(LensProperty);
        set => SetValue(LensProperty, value);
    }

    public IReadOnlySet<string> LensPathCallsigns
    {
        get => GetValue(LensPathCallsignsProperty);
        set => SetValue(LensPathCallsignsProperty, value);
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

    public ScopeMinSep? MinSep
    {
        get => GetValue(MinSepProperty);
        set => SetValue(MinSepProperty, value);
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
    public ScopeDataBlockViewState? DataBlockState { get; set; }

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
    /// block whose action is not <see cref="ScopeFieldAction.None"/>, or on an HSF indicator or VCI cell, with the callsign, the
    /// action, the click point and the keyboard modifiers held at the press.
    /// </summary>
    public event Action<string, ScopeFieldAction, Point, KeyModifiers>? FieldClicked;

    /// <summary>
    /// Fired when an aircraft's symbol or block, or an ERAM HSF indicator or VCI cell, is middle-clicked, with the callsign and
    /// what the press landed on (<see cref="ScopeMiddleHit"/>).
    /// </summary>
    public event Action<string, ScopeMiddleHit>? AircraftMiddleClicked;

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

    /// <summary>
    /// Asked at a plain left press while the measuring tool is armed: true leaves the press to the entry's fill
    /// (<see cref="FillsOnLeftClick"/>) instead of picking an endpoint, as a STARS <c>*T</c> entry in the preview takes it as its
    /// slew whatever armed the tool. Null never does.
    /// </summary>
    internal Func<bool>? EntryTakesMeasurePress { get; set; }

    /// <summary>
    /// Fired for a measurement endpoint the pointer picked: a left press while the tool is armed, or an Alt+left click released
    /// within the drag threshold (which anchors the line). An aircraft under the pointer latches; else the map point.
    /// </summary>
    public event Action<RblEndpoint>? MeasurePointPicked;

    /// <summary>Fired when an Alt+left drag is released past the drag threshold, with the press's and the release's endpoints.</summary>
    public event Action<RblEndpoint, RblEndpoint>? MeasureDragCompleted;

    /// <summary>Fired for a right press while the measuring tool is armed: the tool is to be cancelled.</summary>
    public event Action? MeasureCancelled;

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
    /// changed since. A disposed canvas builds none: a hit test then sees an empty frame.
    /// </summary>
    internal ScopeFrame CurrentFrame =>
        _disposed ? new ScopeFrame(TemplateId, []) : (_lastFrame ??= BuildFrame(BuildPalette(), stepDeconfliction: false));

    /// <summary>The frame the last render snapshot drew, or null before the first. Test seam.</summary>
    internal ScopeFrame? LastRenderedFrame { get; private set; }

    /// <summary>Drops the cached hit-test frame, as a hover change does, so the next <see cref="CurrentFrame"/> builds one. Test seam.</summary>
    internal void DropCachedFrame() => _lastFrame = null;

    /// <summary>The callsign of the track the pointer dwells on, or null. Test seam.</summary>
    internal string? HoveredCallsign => _hoveredCallsign;

    /// <summary>The callsign of the track whose VCI cell the pointer previews, or null. Test seam.</summary>
    internal string? HoveredVciCallsign => _hoveredVciCallsign;

    /// <summary>The callsign of the track whose queued-commands badge the pointer is on, or null. Test seam.</summary>
    internal string? HoveredQueueCallsign => _hoveredQueueCallsign;

    /// <summary>How many frames the pointer-move handler has built for its dwell hit-test. Test seam.</summary>
    internal int HoverFramesBuilt { get; private set; }

    /// <summary>The traffic collection the canvas is listening to, or null. Test seam.</summary>
    internal INotifyCollectionChanged? WatchedAircraft => _watchedAircraft;

    /// <summary>Whether the canvas has been disposed. Test seam.</summary>
    internal bool IsDisposed => _disposed;

    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        DisposeFeatures();
        _renderer.Dispose();
        _measurePaint.Dispose();
        _measureFont?.Dispose();
        _rblMeasureFont.Dispose();
    }

    protected override object? CreateRenderSnapshot()
    {
        if (_disposed)
        {
            return null;
        }

        ScopePalette palette = BuildPalette();
        ScopeFrame frame = BuildFrame(palette, stepDeconfliction: true);
        _lastFrame = frame;
        LastRenderedFrame = frame;
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
            PlaceRblReadouts(frame, lines, pending),
            MinSep,
            Overlays: CollectOverlays(frame)
        );
    }

    /// <summary>
    /// Resolves this frame's measurements: the lines taken on the Scope, and the half-placed one (an Alt+left drag in flight,
    /// else an anchor picked on the Scope) when a cursor position is known.
    /// </summary>
    private (IReadOnlyList<ResolvedRbl> Lines, ResolvedRbl? Pending) ResolveMeasurements()
    {
        // A canvas with no Scope view model owns no line.
        var owner = DataContext as ScopeViewModel;
        IReadOnlyList<RangeBearingLine>? placed = (owner is not null) ? RangeBearingLines : null;
        // A half-placed anchor picked on another surface, another Scope window's included, previews there, not here.
        RblEndpoint? anchor = _measureDragAnchor ?? OwnMeasureAnchor()?.Endpoint;
        if ((placed is not { Count: > 0 }) && (anchor is null))
        {
            return ([], null);
        }

        RblTrackLookup lookup = BuildMeasureLookup();
        List<ResolvedRbl> lines = placed is { Count: > 0 }
            ? RangeBearingLineResolver.Resolve(placed, lookup, ScopeViewModel.MeasureUnits, ScopeViewModel.MeasureView, owner)
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
    private Dictionary<int, SKRect> PlaceRblReadouts(ScopeFrame frame, IReadOnlyList<ResolvedRbl> lines, ResolvedRbl? pending)
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
    /// What a readout avoids on the Scope: every block of <paramref name="frame"/> where it was placed, deconflicted or not,
    /// fixed (a readout never nudges one), and every symbol.
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
    internal ScopeRenderSnapshot RenderSnapshotForTesting() =>
        TryRenderSnapshotForTesting() ?? throw new InvalidOperationException("The canvas is disposed: use TryRenderSnapshotForTesting");

    /// <summary>The snapshot the next frame would hand the render thread, or null once the canvas is disposed. Test seam.</summary>
    internal ScopeRenderSnapshot? TryRenderSnapshotForTesting() => (ScopeRenderSnapshot?)CreateRenderSnapshot();

    protected override void RenderFromSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot) =>
        DrawScopeSnapshot(canvas, viewport, snapshot);

    /// <summary>Draws one snapshot as the render thread does, answering whether it drew. Test seam.</summary>
    internal bool DrawSnapshotForTesting(SKCanvas canvas, MapViewport viewport, object? snapshot) => DrawScopeSnapshot(canvas, viewport, snapshot);

    // The render thread holds the lock for the whole draw, so Dispose cannot free the renderer's Skia objects underneath it.
    private bool DrawScopeSnapshot(SKCanvas canvas, MapViewport viewport, object? snapshot)
    {
        lock (_renderLock)
        {
            if (_disposed || snapshot is not ScopeRenderSnapshot s)
            {
                return false;
            }

            _renderer.Render(canvas, viewport, s);
            return true;
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
    /// Whether <paramref name="property"/> sets the vector, history, ring, PTL, pilot lens, CRR or top-down layer (its TDM-only
    /// maps among them), which the frame lays out.
    /// </summary>
    private static bool IsLayerProperty(AvaloniaProperty property) =>
        (property == VectorMinutesProperty)
        || (property == CrrGroupsProperty)
        || (property == EramConflictsProperty)
        || (property == VectorOwnershipProperty)
        || (property == HistoryCountProperty)
        || (property == LocalRingsProperty)
        || (property == LocalPtlCallsignsProperty)
        || (property == LensProperty)
        || (property == LensPathCallsignsProperty)
        || (property == ShowTopDownProperty)
        || (property == TdmOnlyMapIdsProperty);

    /// <summary>Whether <paramref name="property"/> sets the measuring tool's or the min-sep pair's state, which draws over the frame.</summary>
    private static bool IsMeasureProperty(AvaloniaProperty property) =>
        (property == IsMeasuringProperty)
        || (property == RangeBearingLinesProperty)
        || (property == MeasureAnchorProperty)
        || (property == MinSepProperty);

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
        if (!IsAltLeftPress(props, e.KeyModifiers))
        {
            // An Alt-drag whose release never came (its capture went elsewhere) ends at the next other press.
            CancelMeasureDrag();
        }

        if (RunPressLadder(new ScopePress(e, props, pos)))
        {
            e.Handled = true;
            return;
        }

        base.OnPointerPressed(e);
    }

    // The measuring tool's presses, ahead of the fill, the Rings tool and the aircraft rungs: Alt+left starts a drag whether or
    // not the tool is armed; while it is armed a left press picks an endpoint and a right press cancels it (no menu, no pan),
    // unless another surface holds the anchor or the entry takes the left press (EntryTakesMeasurePress). False for any other
    // press.
    // Each rung reads the button this press went down with (PointerUpdateKind), so a second button pressed while the left is
    // held neither re-anchors nor picks.
    private bool TryMeasurePress(PointerPointProperties props, KeyModifiers modifiers, Point pos)
    {
        if (IsAltLeftPress(props, modifiers))
        {
            _measureDragAnchor = MeasureEndpointAt(pos);
            _measureDragStart = pos;
            MeasureCursor = pos;
            return true;
        }

        if (!IsMeasuring)
        {
            return false;
        }

        if (props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            if (EntryTakesMeasurePress?.Invoke() == true)
            {
                return false;
            }

            MeasurePointPicked?.Invoke(MeasureEndpointAt(pos));
            return true;
        }

        if ((props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed) && IsMeasuringHere)
        {
            MeasureCancelled?.Invoke();
            return true;
        }

        return false;
    }

    // The armed tool is this Scope's to cancel: no anchor anywhere, or the anchor this canvas's Scope picked. With an anchor
    // another surface holds, a right press is not the tool's (it opens the Scope's menu and the anchor stays), and the draw-route
    // rungs take their presses as with the tool disarmed.
    private bool IsMeasuringHere => IsMeasuring && ((MeasureAnchor is null) || (OwnMeasureAnchor() is not null));

    // A press that went down with the left button while Alt is held: it starts a measurement drag.
    private static bool IsAltLeftPress(PointerPointProperties props, KeyModifiers modifiers) =>
        (props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed) && modifiers.HasFlag(KeyModifiers.Alt);

    /// <summary>
    /// Drops an Alt+left drag in flight, so its release places nothing and its line stops drawing: Esc's step for it, a lost
    /// capture and any press other than Alt+left. Returns whether one was in flight.
    /// </summary>
    internal bool CancelMeasureDrag()
    {
        if (_measureDragAnchor is null)
        {
            return false;
        }

        _measureDragAnchor = null;
        MarkDirty();
        return true;
    }

    /// <summary>An Alt+left drag whose capture went elsewhere (Alt-Tab mid-drag) never gets its release: it ends here.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        CancelMeasureDrag();
        base.OnPointerCaptureLost(e);
    }

    // A measurement endpoint at the point: the aircraft whose block, else whose target, is under it latches; else the map
    // point, unlabelled (the Scope names no FRD).
    private RblEndpoint MeasureEndpointAt(Point pos) =>
        HitAt(CurrentFrame, pos) is { } hit ? RblEndpoint.OnAircraft(hit.Callsign) : RblEndpoint.AtPoint(PointAt(pos), "");

    /// <summary>
    /// Ends an Alt+left drag: a release past the drag threshold places the whole line, a nearer one anchors it and leaves the
    /// tool armed for a plain click to complete. Any release records the cursor the half-placed line runs to.
    /// </summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Point pos = e.GetPosition(this);
        TrackMeasureCursor(pos);
        if (RunReleaseLadder(new ScopeRelease(e, pos)))
        {
            e.Handled = true;
            return;
        }

        if ((_measureDragAnchor is not { } anchor) || (e.InitialPressMouseButton != MouseButton.Left))
        {
            base.OnPointerReleased(e);
            return;
        }

        _measureDragAnchor = null;
        double dx = pos.X - _measureDragStart.X;
        double dy = pos.Y - _measureDragStart.Y;
        if (((dx * dx) + (dy * dy)) > MeasureDragThresholdSq)
        {
            MeasureDragCompleted?.Invoke(anchor, MeasureEndpointAt(pos));
        }
        else
        {
            MeasurePointPicked?.Invoke(anchor);
        }

        MarkDirty();
        e.Handled = true;
    }

    // Records where the half-placed line runs to, redrawing while one is in flight on the Scope.
    private void TrackMeasureCursor(Point pos)
    {
        MeasureCursor = pos;
        if ((_measureDragAnchor is not null) || (OwnMeasureAnchor() is not null))
        {
            MarkDirty();
        }
    }

    // The half-placed anchor when this canvas's Scope picked it (the Scope view, and its ScopeViewModel DataContext), else
    // null; a canvas with no ScopeViewModel owns none.
    private RblPendingAnchor? OwnMeasureAnchor() =>
        ((MeasureAnchor is { } pending) && (DataContext is ScopeViewModel owner) && pending.BelongsTo(ScopeViewModel.MeasureView, owner))
            ? pending
            : null;

    // A left or right press on an aircraft: the right opens its menu, the left selects it (OnLeftPressed), or on a VCI cell, lit
    // or not, runs its toggle, or that of an HSF indicator over it in the same tier or above (ScopeFrame.IndicatorPickAt, which
    // leaves a plain track's cell under a dwell-locked block to the block). False off any aircraft. A left press carries its
    // modifiers to FieldClicked.
    private bool PressAircraft(bool right, Point pos, KeyModifiers modifiers)
    {
        ScopeFrame frame = CurrentFrame;
        if (!right && (frame.IndicatorPickAt((float)pos.X, (float)pos.Y) is { } pick))
        {
            FieldClicked?.Invoke(pick.Aircraft.Callsign, pick.Action, pos, modifiers);
            return true;
        }

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
            OnLeftPressed(frame, hit, pos, modifiers);
        }

        return true;
    }

    // A middle press: on an HSF indicator or VCI cell (ScopeFrame.IndicatorPickAt, consulted first as the left press does), else
    // on an aircraft, with whether it is on its ERAM Field A and the action of its own field under the press; else on the map.
    // The frame is rebuilt, as the press may toggle a highlight it draws.
    private void OnMiddlePressed(Point pos)
    {
        ScopeFrame frame = CurrentFrame;
        float x = (float)pos.X;
        float y = (float)pos.Y;
        if (frame.IndicatorPickAt(x, y) is { } pick)
        {
            AircraftMiddleClicked?.Invoke(pick.Aircraft.Callsign, new ScopeMiddleHit(false, pick.Action));
        }
        else if (HitAt(frame, pos) is { } hit)
        {
            AircraftMiddleClicked?.Invoke(hit.Callsign, new ScopeMiddleHit(IsEramCallsignOf(frame, hit, x, y), FieldActionOf(frame, hit, x, y)));
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

    /// <summary>The bounds of <paramref name="callsign"/>'s block in the current frame, in canvas coordinates; null when none draws.</summary>
    internal Rect? BlockBoundsOf(string callsign) =>
        (CurrentFrame.Tracks.FirstOrDefault(t => t.Aircraft.Callsign == callsign)?.Block.Bounds is { } b)
            ? new Rect(b.Left, b.Top, b.Width, b.Height)
            : null;

    // The aircraft whose block, else whose target, is under the point.
    private static AircraftModel? HitAt(ScopeFrame frame, Point pos) =>
        frame.BlockAt((float)pos.X, (float)pos.Y) ?? frame.TargetAt((float)pos.X, (float)pos.Y);

    private LatLon PointAt(Point pos)
    {
        (double lat, double lon) = Viewport.ScreenToLatLon((float)pos.X, (float)pos.Y);
        return new LatLon(lat, lon);
    }

    // A left press on an aircraft: an ERAM Field A asks for the dwell-lock toggle first; then a field with an action fires
    // FieldClicked, with the press's modifiers, anything else AircraftLeftClicked.
    private void OnLeftPressed(ScopeFrame frame, AircraftModel hit, Point pos, KeyModifiers modifiers)
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
            FieldClicked?.Invoke(hit.Callsign, action, pos, modifiers);
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
    /// Dwells the track under the pointer at once, as CRC does, previews the VCI whose cell it is on, or shows the pending lines
    /// of the queued-commands badge it is on, hit-testing the drawn frame; the frame is rebuilt only when a hovered callsign
    /// changes. Panning stays with the base canvas. Every move records the cursor a half-placed measurement runs to.
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point pos = e.GetPosition(this);
        TrackMeasureCursor(pos);
        RunMoveHooks(new ScopeMove(e, pos));
        if (e.Handled)
        {
            // A pan drag: the map moves with the pointer, so the dwell holds; the next free move re-tests it.
            return;
        }

        HoverFramesBuilt += _lastFrame is null ? 1 : 0;
        ScopeHoverHit hit = CurrentFrame.HoverAt((float)pos.X, (float)pos.Y);
        Hover(hit.Dwelled?.Callsign, hit.VciPreviewed?.Callsign, hit.QueueHovered?.Callsign);
    }

    /// <summary>Releases the dwell, the VCI preview and the queued-commands lines when the pointer leaves the canvas.</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(null, null, null);
    }

    private void Hover(string? hitCallsign, string? vciCallsign, string? queueCallsign)
    {
        // With hover dwell off the pointer dwells nothing; the VCI preview and the queued-commands lines are no dwell and stay.
        string? callsign = DbSettings is { HoverDwell: false } ? null : hitCallsign;
        if (
            string.Equals(callsign, _hoveredCallsign, StringComparison.Ordinal)
            && string.Equals(vciCallsign, _hoveredVciCallsign, StringComparison.Ordinal)
            && string.Equals(queueCallsign, _hoveredQueueCallsign, StringComparison.Ordinal)
        )
        {
            return;
        }

        _hoveredCallsign = callsign;
        _hoveredVciCallsign = vciCallsign;
        _hoveredQueueCallsign = queueCallsign;
        _lastFrame = null;
        MarkDirty();
    }

    /// <summary>
    /// Lays this frame's tracks out; only the render's build (<paramref name="stepDeconfliction"/>) steps Free-form, so a
    /// hit-test's build finds each block where the last render drew it.
    /// </summary>
    private ScopeFrame BuildFrame(ScopePalette palette, bool stepDeconfliction)
    {
        IReadOnlyList<AircraftModel> visible = FilterVisible();
        ScopeAlertOptions alerts = BuildAlertOptions();
        ScopeDisplayOptions display = BuildDisplayOptions(palette);
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(MeasureFont(DatablockFontSize()), _measurePaint));
        IReadOnlyDictionary<string, SKPoint> offsets = DataBlockState?.ManualOffsets ?? NoOffsets;
        IReadOnlySet<string> highlighted = DataBlockState?.HighlightedCallsigns ?? NoCallsigns;
        return OrderBubbledLast(
            BuildDeconflictedFrame(
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
                    _intentPaths,
                    _hoveredCallsign,
                    VciHoveredCallsign: _hoveredVciCallsign,
                    LocalDwellLockedCallsigns: DataBlockState?.DwellLockedCallsigns ?? NoCallsigns,
                    ShowTopDown: ShowTopDown,
                    LocalPtlCallsigns: LocalPtlCallsigns,
                    LensPath: Lens.DrawsPath,
                    LensPathCallsigns: LensPathCallsigns,
                    // Only while the lens draws a path, and null until the navigation data loads.
                    NavDb: Lens.DrawsPath ? NavigationDatabase.InstanceOrNull : null,
                    LensQueue: Lens.DrawsQueue,
                    QueueHoveredCallsign: _hoveredQueueCallsign,
                    LensCheckIn: Lens.DrawsCheckIn,
                    CrrGroups: CrrGroups,
                    HiddenCallsigns: DataBlockState?.HiddenCallsigns,
                    SyncLeaderDirection: Preferences?.SyncStudentLeaderDirection ?? false,
                    EramConflicts: EramConflicts
                ),
                stepDeconfliction
            )
        );
    }

    /// <summary>
    /// The alert overlays: ATPA DIST from the DB settings (else the preferences), the two ATPA cone toggles and CA RING from the
    /// DB settings (on without them), the others from the preferences.
    /// </summary>
    private ScopeAlertOptions BuildAlertOptions()
    {
        UserPreferences? prefs = Preferences;
        bool atpa = DbSettings?.Atpa ?? prefs?.ShowAtpa ?? false;
        return new ScopeAlertOptions(
            atpa,
            DbSettings?.AtpaAlertCones ?? true,
            DbSettings?.AtpaMonitorCones ?? true,
            prefs?.ShowConflictAlerts ?? true,
            prefs?.FlashNoLandingClearance ?? true,
            DbSettings?.EramCaRing ?? true
        );
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

    /// <summary>The user's tints and selected-symbol colour, read from the preferences.</summary>
    private ScopePalette BuildPalette()
    {
        UserPreferences? prefs = Preferences;
        if (prefs is null)
        {
            return new ScopePalette(null, null, new SKColor(255, 255, 255));
        }

        SKColor? assignedToMe = prefs.AssignmentTintEnabled
            ? ParsePreferenceColor(nameof(UserPreferences.AssignmentTintColor), prefs.AssignmentTintColor)
            : null;
        SKColor? assignedToOther = prefs.UnassignedTintEnabled
            ? ParsePreferenceColor(nameof(UserPreferences.UnassignedTintColor), prefs.UnassignedTintColor)
            : null;
        SKColor selected = ParsePreferenceColor(nameof(UserPreferences.SelectedColor), prefs.SelectedColor) ?? new SKColor(255, 255, 255);
        return new ScopePalette(assignedToMe, assignedToOther, selected);
    }

    /// <summary>
    /// One colour preference's parsed value: the last value per preference is kept, so a frame re-parses nothing unless the
    /// value changes. A value that does not parse warns once per distinct value; a blank one is "no colour picked" and stays
    /// quiet.
    /// </summary>
    private SKColor? ParsePreferenceColor(string preference, string value)
    {
        if (_parsedColors.TryGetValue(preference, out (string Value, SKColor? Color) parsed) && (parsed.Value == value))
        {
            return parsed.Color;
        }

        SKColor? color = TryParseHex(value);
        _parsedColors[preference] = (value, color);
        if ((color is null) && !string.IsNullOrWhiteSpace(value) && _warnedColors.Add((preference, value)))
        {
            Log.LogWarning("Scope: {Pref} colour {Value} does not parse; using the default", preference, value);
        }

        return color;
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
