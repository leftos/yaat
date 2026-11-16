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

    private static readonly Dictionary<string, SKPoint> NoOffsets = [];
    private static readonly HashSet<string> NoCallsigns = [];

    private readonly ScopeRenderer _renderer = new();
    private readonly SKPaint _measurePaint = new();
    private SKFont? _measureFont;
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
    }

    protected override object? CreateRenderSnapshot()
    {
        ScopePalette palette = BuildPalette();
        ScopeFrame frame = BuildFrame(palette);
        _lastFrame = frame;
        return new ScopeRenderSnapshot(
            frame,
            VideoMaps,
            // The view model's own copy, never changed after it is set, so the render thread reads it as is.
            BrightnessLookup,
            Brightness,
            DatablockFontSize(),
            palette
        );
    }

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
        else if (change.Property == BrightnessProperty)
        {
            // Colours only: the kept frame's layout still holds, so hit-testing keeps it.
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
        Point pos = e.GetPosition(this);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
        {
            ScopeFrame frame = CurrentFrame;
            AircraftModel? hit = frame.BlockAt((float)pos.X, (float)pos.Y) ?? frame.TargetAt((float)pos.X, (float)pos.Y);
            if (hit is not null)
            {
                if (props.IsRightButtonPressed)
                {
                    AircraftRightClicked?.Invoke(hit.Callsign, pos);
                }
                else
                {
                    if (IsEramCallsignOf(frame, hit, (float)pos.X, (float)pos.Y))
                    {
                        AircraftDwellLockToggleRequested?.Invoke(hit.Callsign);
                        // The lock is drawn from the frame, so the next one reads the toggled state.
                        _lastFrame = null;
                        MarkDirty();
                    }
                    AircraftLeftClicked?.Invoke(hit.Callsign);
                }
                e.Handled = true;
                return;
            }
        }

        base.OnPointerPressed(e);
    }

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
        IReadOnlyList<AircraftModel> visible = RadarCanvas.FilterAircraft(Aircraft, false, false, false, null, DateTime.UtcNow);
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
                _hoveredCallsign,
                VciHoveredCallsign: _hoveredVciCallsign,
                LocalDwellLockedCallsigns: DataBlockState?.DwellLockedCallsigns ?? NoCallsigns
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
