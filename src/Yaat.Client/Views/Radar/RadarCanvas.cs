using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// SkiaSharp canvas that renders a STARS-style radar display with
/// video maps, aircraft targets, and overlays.
/// </summary>
public sealed class RadarCanvas : MapCanvasBase, IDisposable
{
    public static readonly StyledProperty<IReadOnlyList<AircraftModel>?>
        AircraftProperty = AvaloniaProperty.Register<
            RadarCanvas, IReadOnlyList<AircraftModel>?>(
            nameof(Aircraft));

    public static readonly StyledProperty<AircraftModel?>
        SelectedAircraftProperty = AvaloniaProperty.Register<
            RadarCanvas, AircraftModel?>(
            nameof(SelectedAircraft));

    public static readonly StyledProperty<IReadOnlyList<VideoMapData>?>
        VideoMapsProperty = AvaloniaProperty.Register<
            RadarCanvas, IReadOnlyList<VideoMapData>?>(
            nameof(VideoMaps));

    public static readonly StyledProperty<bool>
        ShowRangeRingsProperty = AvaloniaProperty.Register<
            RadarCanvas, bool>(
            nameof(ShowRangeRings), defaultValue: true);

    public static readonly StyledProperty<double>
        RangeNmProperty = AvaloniaProperty.Register<
            RadarCanvas, double>(
            nameof(RangeNm), defaultValue: 60);

    public static readonly StyledProperty<double>
        RadarCenterLatProperty = AvaloniaProperty.Register<
            RadarCanvas, double>(
            nameof(RadarCenterLat));

    public static readonly StyledProperty<double>
        RadarCenterLonProperty = AvaloniaProperty.Register<
            RadarCanvas, double>(
            nameof(RadarCenterLon));

    public static readonly StyledProperty<bool>
        ShowFixesProperty = AvaloniaProperty.Register<
            RadarCanvas, bool>(nameof(ShowFixes));

    public static readonly StyledProperty<
        IReadOnlyList<(string Name, double Lat, double Lon)>?>
        FixesProperty = AvaloniaProperty.Register<RadarCanvas,
            IReadOnlyList<(string Name, double Lat, double Lon)>?>(
            nameof(Fixes));

    private readonly RadarRenderer _renderer = new();
    private Dictionary<string, string> _brightnessLookup = [];

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

    /// <summary>
    /// Sets the brightness category lookup (mapId → "A"/"B").
    /// </summary>
    public void SetBrightnessLookup(
        Dictionary<string, string> lookup)
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

    protected override void OnPropertyChanged(
        AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == VideoMapsProperty
            || change.Property == AircraftProperty
            || change.Property == SelectedAircraftProperty
            || change.Property == ShowRangeRingsProperty
            || change.Property == RangeNmProperty
            || change.Property == ShowFixesProperty
            || change.Property == FixesProperty)
        {
            MarkDirty();
        }

        if (change.Property == RadarCenterLatProperty
            || change.Property == RadarCenterLonProperty
            || change.Property == RangeNmProperty)
        {
            FitToRange();
        }
    }

    protected override void RenderContent(
        SKCanvas canvas, MapViewport viewport)
    {
        _renderer.Render(
            canvas, viewport,
            VideoMaps ?? Array.Empty<VideoMapData>(),
            _brightnessLookup,
            Aircraft ?? Array.Empty<AircraftModel>(),
            SelectedAircraft,
            ShowRangeRings,
            RangeNm,
            RadarCenterLat,
            RadarCenterLon,
            ShowFixes,
            Fixes);
    }

    protected override void OnPointerPressed(
        PointerPressedEventArgs e)
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
        var ac = FindAircraftAtPoint(screenPos);
        if (ac is not null)
        {
            AircraftRightClicked?.Invoke(ac.Callsign, screenPos);
            return;
        }

        var (lat, lon) = Viewport.ScreenToLatLon(
            (float)screenPos.X, (float)screenPos.Y);
        MapRightClicked?.Invoke(lat, lon, screenPos);
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

        foreach (var ac in Aircraft)
        {
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

    /// <summary>
    /// Centers the viewport on the radar center and fits the range.
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

        // Range in degrees latitude (1 nm = 1/60 degree)
        var rangeDeg = RangeNm / 60.0;
        Viewport.FitBounds(
            RadarCenterLat - rangeDeg,
            RadarCenterLat + rangeDeg,
            RadarCenterLon - rangeDeg,
            RadarCenterLon + rangeDeg);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        FitToRange();
    }

    public void Dispose()
    {
        _renderer.Dispose();
    }
}
