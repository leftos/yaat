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
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope's map canvas. Each frame's <see cref="ScopeFrame"/> is built once on the UI thread in
/// <see cref="CreateRenderSnapshot"/>, drawn by <see cref="ScopeRenderer"/> on the render thread, and kept for hit-testing,
/// so a click reads the same block layouts the user sees.
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

    private static readonly Dictionary<string, SKPoint> NoOffsets = [];

    private readonly ScopeRenderer _renderer = new();
    private readonly SKPaint _measurePaint = new();
    private SKFont? _measureFont;
    private ScopeFrame? _lastFrame;
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

    /// <summary>The user's preferences: the template choice, the datablock font size and the alert overlays. Null draws STARS at 12.</summary>
    public UserPreferences? Preferences { get; set; }

    /// <summary>The scope's datablock state; each block is placed from its manual offsets. Null places every block at the default offset.</summary>
    public RadarDataBlockViewState? DataBlockState { get; set; }

    /// <summary>Video maps to draw.</summary>
    public IReadOnlyList<VideoMapData> VideoMaps { get; set; } = [];

    /// <summary>Brightness category ("A" or "B") by video map id.</summary>
    public IReadOnlyDictionary<string, string> BrightnessLookup { get; set; } = new Dictionary<string, string>();

    /// <summary>Brightness of A maps, 0 to 1.</summary>
    public float MapBrightnessA { get; set; } = 1.0f;

    /// <summary>Brightness of B maps, 0 to 1.</summary>
    public float MapBrightnessB { get; set; } = 0.6f;

    /// <summary>Fired when an aircraft's symbol or block is left-clicked.</summary>
    public event Action<string>? AircraftLeftClicked;

    /// <summary>Fired when an aircraft's symbol or block is right-clicked, with the click point.</summary>
    public event Action<string, Point>? AircraftRightClicked;

    /// <summary>
    /// The frame clicks are hit-tested against: the last one drawn, or a fresh one when the traffic or the viewport has
    /// changed since.
    /// </summary>
    internal ScopeFrame CurrentFrame => _lastFrame ??= BuildFrame();

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
        ScopeFrame frame = BuildFrame();
        _lastFrame = frame;
        return new ScopeRenderSnapshot(
            frame,
            VideoMaps,
            // The view model's own copy, never changed after it is set, so the render thread reads it as is.
            BrightnessLookup,
            MapBrightnessA,
            MapBrightnessB,
            DatablockFontSize()
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
        else if (change.Property == SelectedAircraftProperty)
        {
            _lastFrame = null;
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
                    AircraftLeftClicked?.Invoke(hit.Callsign);
                }
                e.Handled = true;
                return;
            }
        }

        base.OnPointerPressed(e);
    }

    private ScopeFrame BuildFrame()
    {
        UserPreferences? prefs = Preferences;
        IReadOnlyList<AircraftModel> visible = RadarCanvas.FilterAircraft(Aircraft, false, false, false, null, DateTime.UtcNow);
        var alerts = new ScopeAlertOptions(prefs?.ShowAtpa ?? false, prefs?.ShowConflictAlerts ?? false, prefs?.FlashNoLandingClearance ?? true);
        var display = new ScopeDisplayOptions(prefs?.MarkStudentLimitedDatablocks ?? true, prefs?.ShowTypeMismatchHints ?? true);
        var metrics = ScopeTextMetrics.FromStyle(new TextStyle(MeasureFont(DatablockFontSize()), _measurePaint));
        IReadOnlyDictionary<string, SKPoint> offsets = DataBlockState?.ManualOffsets ?? NoOffsets;
        bool euroScope = prefs?.EuroScopeMode ?? false;
        return ScopeFrame.Build(new ScopeFrameInput(visible, SelectedAircraft, Viewport, euroScope, metrics, alerts, display, offsets));
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

    private float DatablockFontSize() => Preferences?.RadarDatablockFontSize ?? 12;

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
