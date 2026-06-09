using System.Collections.Frozen;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;

namespace Yaat.Client.Views.Ground;

/// <summary>
/// Computed datablock geometry. Shared by renderer (draw) and canvas (hit-test).
/// </summary>
internal readonly struct DataBlockLayout
{
    private const float Pad = 3f;

    public readonly SKRect Rect;
    public readonly float TextX;
    public readonly float TextY;
    public readonly float LineHeight;
    public readonly string Line1;
    public readonly string Line2;
    public readonly string Line3;
    public readonly string Line4;

    /// <summary>Instructor note line (amber), drawn at the bottom of the block. Empty when no note.</summary>
    public readonly string Line5;

    /// <summary>Total drawn lines, including the note line.</summary>
    public readonly int LineCount;

    private DataBlockLayout(
        SKRect rect,
        float textX,
        float textY,
        float lineHeight,
        string line1,
        string line2,
        string line3,
        string line4,
        string line5,
        int lineCount
    )
    {
        Rect = rect;
        TextX = textX;
        TextY = textY;
        LineHeight = lineHeight;
        Line1 = line1;
        Line2 = line2;
        Line3 = line3;
        Line4 = line4;
        Line5 = line5;
        LineCount = lineCount;
    }

    public static DataBlockLayout Compute(AircraftModel ac, float screenX, float screenY, SKPoint offset, SKPaint textPaint, bool isAirborne)
    {
        float blockX = screenX + offset.X;
        float blockY = screenY + offset.Y;

        // Suffix '*' marks aircraft pre-armed for auto-delete on hold-short (ONHS DEL).
        string line1 = ac.AutoDeletePending ? $"{ac.Callsign}*" : ac.Callsign;
        string dest = ac.Destination.StartsWith('K') ? ac.Destination[1..] : ac.Destination;
        // CWT category prepended to the physical type as "cwt/type" (e.g. "E/B738"), mirroring the
        // radar STARS datablock. Ground stays on the physical AircraftType (tower-cab "out the window"
        // surface), unlike the radar surfaces which use DisplayAircraftType.
        string cwt = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";
        string cwtType = RadarDatablockLayout.FormatCwtType(cwt, ac.AircraftType);
        string line2 = string.IsNullOrEmpty(dest) ? cwtType : (cwtType.Length > 0 ? $"{cwtType} {dest}" : dest);
        string line3 = isAirborne ? $"{(int)(ac.Altitude / 100):D3}" : "";
        // Ground hold / auto-yield takes precedence on line4 over the SqStby transponder hint —
        // a HOLDPOSITION, GIVEWAY, or auto-detected yield is operationally more important than a
        // stale transponder indication. HoldStatusDisplay is non-empty exactly when one applies;
        // otherwise line4 reverts to SqStby.
        string line4 = !string.IsNullOrEmpty(ac.HoldStatusDisplay) ? ac.HoldStatusDisplay : (ac.TransponderMode == "Standby" ? "SqStby" : "");
        // Instructor note — always-on amber line at the bottom of the block when set.
        string line5 = ac.HasNote ? ac.Note : "";

        float w1 = textPaint.MeasureText(line1);
        float w2 = textPaint.MeasureText(line2);
        float w3 = line3.Length > 0 ? textPaint.MeasureText(line3) : 0;
        float w4 = line4.Length > 0 ? textPaint.MeasureText(line4) : 0;
        float w5 = line5.Length > 0 ? textPaint.MeasureText(line5) : 0;
        float textW = MathF.Max(MathF.Max(w1, w2), MathF.Max(MathF.Max(w3, w4), w5));
        float lineH = textPaint.TextSize + 2;
        int lineCount = 2;
        if (line3.Length > 0)
        {
            lineCount++;
        }
        if (line4.Length > 0)
        {
            lineCount++;
        }
        if (line5.Length > 0)
        {
            lineCount++;
        }

        var rect = new SKRect(blockX - Pad, blockY - textPaint.TextSize - Pad, blockX + textW + Pad, blockY + (lineCount - 1) * lineH + Pad);

        return new DataBlockLayout(rect, blockX, blockY, lineH, line1, line2, line3, line4, line5, lineCount);
    }

    public static readonly SKPoint DefaultOffset = new(30, -25);
}

/// <summary>
/// Stateless SkiaSharp renderer for the airport ground layout.
/// All SKPaint objects are pre-allocated and reused.
/// Labels are collected during geometry passes and drawn last with overlap culling.
/// </summary>
public sealed class GroundRenderer : IDisposable
{
    // Customizable colors — updated via SetColors()
    private SKColor _backgroundColor = SKColor.Parse(GroundColorScheme.DefaultBackground);
    private SKColor _runwayFillColor = SKColor.Parse(GroundColorScheme.DefaultRunwayFill);
    private SKColor _runwayOutlineColor = SKColor.Parse(GroundColorScheme.DefaultRunwayOutline);
    private SKColor _runwayColor = new(120, 120, 120);
    private SKColor _taxiwayColor = SKColor.Parse(GroundColorScheme.DefaultTaxiway);
    private SKColor _taxiLabelColor = SKColor.Parse(GroundColorScheme.DefaultTaxiLabel).WithAlpha(200);
    private SKColor _rampEdgeColor = SKColor.Parse(GroundColorScheme.DefaultRampEdge);
    private SKColor _holdShortColor = SKColor.Parse(GroundColorScheme.DefaultHoldShort).WithAlpha(180);
    private SKColor _aircraftColor = SKColor.Parse(GroundColorScheme.DefaultAircraft);
    private SKColor _datablockTextColor = SKColor.Parse(GroundColorScheme.DefaultDatablockText);

    // Non-customizable colors
    private static readonly SKColor NodeIntersection = new(80, 120, 200, 100);
    private static readonly SKColor NodeParking = new(60, 180, 80, 140);
    private static readonly SKColor NodeHelipad = new(180, 60, 220, 140);
    private static readonly SKColor NodeSpot = new(220, 160, 40, 140);
    private static readonly SKColor ActiveRouteColor = new(60, 220, 60);
    private static readonly SKColor PreviewRouteColor = new(80, 180, 255, 180);
    private static readonly SKColor DrawnRouteColor = new(0, 200, 255);
    private static readonly SKColor DrawHoverPreviewColor = new(255, 180, 50);
    private static readonly SKColor WaypointMarkerColor = new(255, 200, 0);
    private static readonly SKColor HoverRingColor = new(255, 255, 255, 160);

    // Instructor-note line color — amber/gold, matches the radar note line and stays distinct
    // from the white/cyan datablock text and green/blue ground markings.
    private static readonly SKColor NoteColor = new(255, 200, 60);

    private const double AirborneMaxAglFt = 4000;
    private const double AirborneMaxRangeNm = 10;

    private enum LabelPriority
    {
        Hovered,
        Runway,
        HoldShort,
        Taxiway,
        ParkingSpot,
    }

    private record struct LabelCandidate(string[] Lines, float X, float Y, LabelPriority Priority, SKPaint Paint, SKColor? ColorOverride);

    private readonly SKPaint _runwayFillPaint = new()
    {
        Color = SKColor.Parse(GroundColorScheme.DefaultRunwayFill),
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _runwayOutlinePaint = new()
    {
        Color = SKColor.Parse(GroundColorScheme.DefaultRunwayOutline),
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _runwayLabelPaint = new()
    {
        Color = new SKColor(180, 180, 180),
        TextSize = 15,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypeface,
        TextAlign = SKTextAlign.Center,
    };

    private readonly SKPaint _runwayPaint = new()
    {
        Color = new SKColor(120, 120, 120),
        StrokeWidth = 6,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _runwayThresholdMarkerPaint = new()
    {
        Color = new SKColor(255, 200, 80),
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _runwayThresholdMarkerOutlinePaint = new()
    {
        Color = new SKColor(0, 0, 0, 200),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1.5f,
        IsAntialias = true,
    };

    private readonly SKPaint _taxiwayPaint = new()
    {
        Color = SKColor.Parse(GroundColorScheme.DefaultTaxiway),
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _rampEdgePaint = new()
    {
        Color = SKColor.Parse(GroundColorScheme.DefaultRampEdge),
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _taxiLabelPaint = new()
    {
        Color = SKColor.Parse(GroundColorScheme.DefaultTaxiLabel).WithAlpha(200),
        TextSize = 13,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint _activeRoutePaint = new()
    {
        Color = ActiveRouteColor,
        StrokeWidth = 4,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _previewRoutePaint = new()
    {
        Color = PreviewRouteColor,
        StrokeWidth = 5,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
        PathEffect = SKPathEffect.CreateDash([10f, 6f], 0),
    };

    private readonly SKPaint _nodePaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private readonly SKPaint _holdShortBarPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2f,
        IsAntialias = true,
    };

    private readonly SKPaint _nodeLabelPaint = new()
    {
        Color = new SKColor(200, 200, 200),
        TextSize = 12,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint _aircraftPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private readonly SKPaint _dataBlockLeaderPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _dataBlockTextPaint = new()
    {
        TextSize = 12,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypefaceBold,
    };

    // Bubble pill paints — mirror the radar palette so SAY overlays read the same on both views.
    private static readonly SKColor SpeechBubbleFillColor = new(20, 60, 50, 220);
    private static readonly SKColor SpeechBubbleBorderColor = new(80, 220, 140);

    // Amber variant for opt-in WARN-channel bubbles, matching the terminal Warning colour.
    private static readonly SKColor WarningBubbleFillColor = new(70, 50, 10, 220);
    private static readonly SKColor WarningBubbleBorderColor = new(230, 170, 40);

    private readonly SKPaint _bubbleFillPaint = new()
    {
        Color = SpeechBubbleFillColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _bubbleBorderPaint = new()
    {
        Color = SpeechBubbleBorderColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _bubbleFillPaintWarning = new()
    {
        Color = WarningBubbleFillColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _bubbleBorderPaintWarning = new()
    {
        Color = WarningBubbleBorderColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _bubbleTextPaint = new()
    {
        TextSize = 12,
        Color = SKColors.White,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypefaceBold,
    };

    /// <summary>
    /// Datablock text size in pixels. Updated from UserPreferences.GroundDatablockFontSize.
    /// </summary>
    public float DatablockTextSize
    {
        get => _dataBlockTextPaint.TextSize;
        set
        {
            _dataBlockTextPaint.TextSize = value;
            _bubbleTextPaint.TextSize = value;
        }
    }

    /// <summary>
    /// When true, aircraft with an active <see cref="AircraftModel.SpeechBubble"/> have their
    /// datablock re-rendered on top of neighboring datablocks, plus a transient bubble pill
    /// painted below it. Driven by the <c>ShowSpeechBubbles</c> user preference.
    /// </summary>
    public bool ShowSpeechBubbles { get; set; }

    /// <summary>
    /// Per-aircraft bubble rect captured during the last DrawDataBlocks pass. Populated only
    /// when <see cref="ShowSpeechBubbles"/> is on and the aircraft has an active bubble.
    /// Consumed by GroundCanvas hit-testing for the click-to-dismiss behavior.
    /// </summary>
    public IReadOnlyDictionary<string, SKRect> LastBubbleRects => _lastBubbleRects;

    private readonly Dictionary<string, SKRect> _lastBubbleRects = [];

    /// <summary>
    /// Base label text size in pixels (taxi-label baseline). Runway labels render at base+2,
    /// node labels at base-1, debug labels at base+1. Updated from UserPreferences.GroundLabelFontSize.
    /// </summary>
    public float LabelTextSize
    {
        get => _taxiLabelPaint.TextSize;
        set
        {
            _taxiLabelPaint.TextSize = value;
            _runwayLabelPaint.TextSize = value + 2;
            _nodeLabelPaint.TextSize = value - 1;
            _debugLabelPaint.TextSize = value + 1;
            _debugEdgeLabelPaint.TextSize = value;
        }
    }

    private readonly SKPaint _dataBlockBgPaint = new() { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Fill };
    private readonly SKPaint _labelBgPaint = new() { Color = new SKColor(0, 0, 0, 220), Style = SKPaintStyle.Fill };

    private readonly SKPaint _drawnRoutePaint = new()
    {
        Color = DrawnRouteColor,
        StrokeWidth = 5,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _drawHoverPreviewPaint = new()
    {
        Color = DrawHoverPreviewColor,
        StrokeWidth = 5,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
    };

    private readonly SKPaint _waypointMarkerPaint = new()
    {
        Color = WaypointMarkerColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _waypointTextPaint = new()
    {
        Color = SKColors.Black,
        TextSize = 10,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypefaceBold,
        TextAlign = SKTextAlign.Center,
    };

    private readonly SKPaint _hoverPaint = new()
    {
        Color = HoverRingColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _bgPaint = new() { Color = SKColor.Parse(GroundColorScheme.DefaultBackground), Style = SKPaintStyle.Fill };

    private readonly SKPaint _debugLabelPaint = new()
    {
        Color = new SKColor(255, 100, 255, 200),
        TextSize = 14,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint _debugEdgeLabelPaint = new()
    {
        Color = new SKColor(100, 255, 100, 180),
        TextSize = 13,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = PlatformHelper.MonospaceTypeface,
    };

    private readonly SKPaint[] _shownTaxiRoutePaints;

    private readonly List<LabelCandidate> _labelCandidates = new(256);

    /// <summary>
    /// Paints whose alpha should scale with the brightness slider.
    /// Maps each paint to its base (full-brightness) alpha value.
    /// </summary>
    private readonly FrozenDictionary<SKPaint, byte> _infrastructurePaints;

    public GroundRenderer()
    {
        _shownTaxiRoutePaints = new SKPaint[TaxiRouteColorValues.Length];
        for (int i = 0; i < TaxiRouteColorValues.Length; i++)
        {
            _shownTaxiRoutePaints[i] = new SKPaint
            {
                Color = TaxiRouteColorValues[i],
                StrokeWidth = 3,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round,
            };
        }

        _infrastructurePaints = new Dictionary<SKPaint, byte>
        {
            [_taxiwayPaint] = _taxiwayPaint.Color.Alpha,
            [_rampEdgePaint] = _rampEdgePaint.Color.Alpha,
            [_taxiLabelPaint] = _taxiLabelPaint.Color.Alpha,
            [_runwayFillPaint] = _runwayFillPaint.Color.Alpha,
            [_runwayOutlinePaint] = _runwayOutlinePaint.Color.Alpha,
            [_runwayPaint] = _runwayPaint.Color.Alpha,
            [_runwayLabelPaint] = _runwayLabelPaint.Color.Alpha,
            [_nodePaint] = _nodePaint.Color.Alpha,
            [_holdShortBarPaint] = _holdShortBarPaint.Color.Alpha,
            [_nodeLabelPaint] = _nodeLabelPaint.Color.Alpha,
        }.ToFrozenDictionary();
    }

    /// <summary>
    /// Applies a user-defined color scheme to all customizable paints.
    /// </summary>
    public void SetColors(GroundColorScheme scheme)
    {
        _backgroundColor = SKColor.Parse(scheme.Background);
        _runwayFillColor = SKColor.Parse(scheme.RunwayFill);
        _runwayOutlineColor = SKColor.Parse(scheme.RunwayOutline);
        _runwayColor = _runwayOutlineColor;
        _taxiwayColor = SKColor.Parse(scheme.Taxiway);
        _taxiLabelColor = SKColor.Parse(scheme.TaxiLabel).WithAlpha(200);
        _rampEdgeColor = SKColor.Parse(scheme.RampEdge);
        _holdShortColor = SKColor.Parse(scheme.HoldShort).WithAlpha(180);
        _aircraftColor = SKColor.Parse(scheme.Aircraft);
        _datablockTextColor = SKColor.Parse(scheme.DatablockText);

        _bgPaint.Color = _backgroundColor;
        _runwayFillPaint.Color = _runwayFillColor;
        _runwayOutlinePaint.Color = _runwayOutlineColor;
        _runwayPaint.Color = _runwayColor;
        _taxiwayPaint.Color = _taxiwayColor;
        _taxiLabelPaint.Color = _taxiLabelColor;
        _rampEdgePaint.Color = _rampEdgeColor;

        SetBrightness(scheme.Brightness / 100f);
    }

    /// <summary>
    /// Scales alpha on infrastructure paints (taxiways, runways, nodes, labels).
    /// Aircraft, datablocks, and route overlays are unaffected.
    /// </summary>
    public void SetBrightness(float brightness)
    {
        brightness = Math.Clamp(brightness, 0.1f, 1f);
        foreach (var (paint, baseAlpha) in _infrastructurePaints)
        {
            paint.Color = paint.Color.WithAlpha((byte)(baseAlpha * brightness));
        }
    }

    private static readonly SKColor[] TaxiRouteColorValues =
    [
        SKColor.Parse("#FF6B6B"),
        SKColor.Parse("#4ECDC4"),
        SKColor.Parse("#FFE66D"),
        SKColor.Parse("#A8E6CF"),
        SKColor.Parse("#FF8B94"),
        SKColor.Parse("#B088F9"),
        SKColor.Parse("#F8B500"),
        SKColor.Parse("#45B7D1"),
    ];

    public void Render(
        SKCanvas canvas,
        MapViewport vp,
        GroundLayoutDto? layout,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        int? hoveredNodeId,
        string? hoveredRunwayEnd,
        TaxiRoute? activeRoute,
        TaxiRoute? previewRoute,
        TaxiRoute? drawnRoutePreview,
        TaxiRoute? drawHoverPreview,
        IReadOnlyList<int>? drawWaypoints,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        double airportCenterLat,
        double airportCenterLon,
        double airportElevation,
        bool showDebugInfo,
        WeatherDisplayInfo? weatherInfo,
        bool showRunwayLabels,
        bool showTaxiwayLabels,
        GroundFilterMode showHoldShort,
        GroundFilterMode showParking,
        GroundFilterMode showSpot,
        IReadOnlyList<ShownTaxiRouteEntry>? shownTaxiRoutes,
        IReadOnlySet<string>? highlightedCallsigns,
        IReadOnlySet<string>? hiddenDataBlockCallsigns,
        TowerCabImage? backgroundImage,
        TowerCabMapData? towerCabMap,
        bool showSatelliteImage,
        int satelliteImageBrightness,
        bool showVideoMapOverlay,
        int videoMapOverlayBrightness,
        bool showYaatLayout,
        int yaatLayoutBrightness
    )
    {
        canvas.Clear(_backgroundColor);

        // Layer 1: Satellite background image
        if (showSatelliteImage && backgroundImage is not null)
        {
            DrawBackgroundImage(canvas, vp, backgroundImage, satelliteImageBrightness);
        }

        // Layer 2: Tower cab video map overlay
        if (showVideoMapOverlay && towerCabMap is not null)
        {
            DrawVideoMapOverlay(canvas, vp, towerCabMap, videoMapOverlayBrightness);
        }

        if (layout is null)
        {
            return;
        }

        _labelCandidates.Clear();

        // Runways render when GND or MAP is on (video map overlays may not include them)
        if (showYaatLayout || showVideoMapOverlay)
        {
            DrawRunways(
                canvas,
                vp,
                layout,
                showRunwayLabels && showYaatLayout,
                drawThresholdMarkers: showYaatLayout && selectedAircraft is not null,
                hoveredRunwayEnd
            );
        }

        // Layer 3: YAAT ground layout (conditionally rendered with brightness)
        if (showYaatLayout)
        {
            if (yaatLayoutBrightness < 100)
            {
                SetBrightness(yaatLayoutBrightness / 100f);
            }

            DrawEdges(canvas, vp, layout, showDebugInfo, showTaxiwayLabels, showParking);
            DrawActiveRoute(canvas, vp, layout, activeRoute);
            DrawPreviewRoute(canvas, vp, layout, previewRoute);
            DrawShownTaxiRoutes(canvas, vp, layout, shownTaxiRoutes);
            DrawDrawnRoute(canvas, vp, layout, drawnRoutePreview, drawWaypoints);
            DrawDrawHoverPreview(canvas, vp, layout, drawHoverPreview);
            DrawNodes(canvas, vp, layout, hoveredNodeId, showDebugInfo, showHoldShort, showParking, showSpot);
            DrawLabels(canvas, hoveredOnly: false);

            if (yaatLayoutBrightness < 100)
            {
                SetBrightness(1f);
            }
        }

        DrawAircraft(canvas, vp, aircraft, selectedAircraft, airportCenterLat, airportCenterLon, airportElevation);
        DrawDataBlocks(
            canvas,
            vp,
            aircraft,
            selectedAircraft,
            dataBlockOffsets,
            airportCenterLat,
            airportCenterLon,
            airportElevation,
            highlightedCallsigns,
            hiddenDataBlockCallsigns
        );

        if (showYaatLayout)
        {
            DrawLabels(canvas, hoveredOnly: true);
        }

        if (showDebugInfo)
        {
            DrawDebugOverlay(canvas, vp, layout);
        }

        if (weatherInfo is not null)
        {
            DrawWeatherOverlay(canvas, weatherInfo);
        }
    }

    private static void DrawBackgroundImage(SKCanvas canvas, MapViewport vp, TowerCabImage image, int brightness)
    {
        // Project all 4 corners through the viewport (handles rotation)
        var (blX, blY) = vp.LatLonToScreen(image.BottomLeftLat, image.BottomLeftLon);
        var (trX, trY) = vp.LatLonToScreen(image.TopRightLat, image.TopRightLon);
        var (brX, brY) = vp.LatLonToScreen(image.BottomLeftLat, image.TopRightLon);
        var (tlX, tlY) = vp.LatLonToScreen(image.TopRightLat, image.BottomLeftLon);

        // Source: image rectangle
        var srcRect = new SKRect(0, 0, image.Image.Width, image.Image.Height);

        // Destination: the 4 projected screen corners
        // Image top-left → screen top-left, top-right → screen top-right, etc.
        // Note: image Y=0 is top, geo top-right lat is the "top" of the image
        var matrix = ComputeBitmapTransform(srcRect, new SKPoint(tlX, tlY), new SKPoint(trX, trY), new SKPoint(brX, brY), new SKPoint(blX, blY));

        byte alpha = (byte)Math.Clamp(brightness * 255 / 100, 0, 255);
        // FilterQuality.Medium = bilinear + mipmaps in SkiaSharp 2.88. Mipmaps let Skia build
        // a downscale chain on the GPU once and reuse it; without them, sampling an 8K×8K
        // tower-cab image at typical airport zoom walked the full source per output pixel
        // and ate ~30% GPU. Combined with the immutable SKImage on TowerCabImage, the GPU
        // texture and its mip chain stay cached across redraws.
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), FilterQuality = SKFilterQuality.Medium };

        canvas.Save();
        canvas.Concat(ref matrix);
        canvas.DrawImage(image.Image, 0, 0, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Computes an affine matrix mapping 3 source points to 3 destination points.
    /// Maps: src(0,0)→dstTL, src(w,0)→dstTR, src(0,h)→dstBL.
    /// Solves the 2x3 affine system directly.
    /// </summary>
    private static SKMatrix ComputeBitmapTransform(SKRect src, SKPoint dstTL, SKPoint dstTR, SKPoint dstBR, SKPoint dstBL)
    {
        float sx0 = src.Left,
            sy0 = src.Top;
        float sx1 = src.Right,
            sy1 = src.Top;
        float sx2 = src.Left,
            sy2 = src.Bottom;

        // Solve: dst = A * src + T for affine coefficients
        // Using 3 point pairs: (sx0,sy0)→dstTL, (sx1,sy1)→dstTR, (sx2,sy2)→dstBL
        float dx1 = sx1 - sx0,
            dy1 = sy1 - sy0;
        float dx2 = sx2 - sx0,
            dy2 = sy2 - sy0;
        float det = (dx1 * dy2) - (dx2 * dy1);

        if (MathF.Abs(det) < 1e-10f)
        {
            return SKMatrix.CreateTranslation(dstTL.X, dstTL.Y);
        }

        float invDet = 1f / det;

        float ddx1 = dstTR.X - dstTL.X,
            ddy1 = dstTR.Y - dstTL.Y;
        float ddx2 = dstBL.X - dstTL.X,
            ddy2 = dstBL.Y - dstTL.Y;

        float scaleX = ((ddx1 * dy2) - (ddx2 * dy1)) * invDet;
        float skewX = ((ddx2 * dx1) - (ddx1 * dx2)) * invDet;
        float skewY = ((ddy1 * dy2) - (ddy2 * dy1)) * invDet;
        float scaleY = ((ddy2 * dx1) - (ddy1 * dx2)) * invDet;
        float transX = dstTL.X - (scaleX * sx0) - (skewX * sy0);
        float transY = dstTL.Y - (skewY * sx0) - (scaleY * sy0);

        return new SKMatrix(scaleX, skewX, transX, skewY, scaleY, transY, 0, 0, 1);
    }

    private static void DrawVideoMapOverlay(SKCanvas canvas, MapViewport vp, TowerCabMapData mapData, int brightness)
    {
        byte alpha = (byte)Math.Clamp(brightness * 255 / 100, 0, 255);

        // Draw filled polygons
        foreach (var poly in mapData.Polygons)
        {
            if (poly.Points.Count < 3)
            {
                continue;
            }

            using var path = new SKPath();
            var (firstX, firstY) = vp.LatLonToScreen(poly.Points[0].Lat, poly.Points[0].Lon);
            path.MoveTo(firstX, firstY);

            for (int i = 1; i < poly.Points.Count; i++)
            {
                var (px, py) = vp.LatLonToScreen(poly.Points[i].Lat, poly.Points[i].Lon);
                path.LineTo(px, py);
            }

            path.Close();

            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = poly.Color.WithAlpha(alpha),
                IsAntialias = true,
            };
            canvas.DrawPath(path, paint);
        }

        // Draw lines
        foreach (var line in mapData.Lines)
        {
            if (line.Points.Count < 2)
            {
                continue;
            }

            using var path = new SKPath();
            var (firstX, firstY) = vp.LatLonToScreen(line.Points[0].Lat, line.Points[0].Lon);
            path.MoveTo(firstX, firstY);

            for (int i = 1; i < line.Points.Count; i++)
            {
                var (px, py) = vp.LatLonToScreen(line.Points[i].Lat, line.Points[i].Lon);
                path.LineTo(px, py);
            }

            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = line.Color.WithAlpha(alpha),
                StrokeWidth = line.Thickness,
                IsAntialias = true,
            };
            canvas.DrawPath(path, paint);
        }
    }

    private static void DrawWeatherOverlay(SKCanvas canvas, WeatherDisplayInfo info)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(0xCC, 0xCC, 0xCC), // light gray
            TextSize = 14,
            Typeface = PlatformHelper.MonospaceTypeface,
            IsAntialias = true,
        };

        canvas.DrawText(info.ToDisplayString(), 10, 20, paint);
    }

    private void DrawDebugOverlay(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout)
    {
        var nodeScreenPos = new Dictionary<int, (float X, float Y)>(layout.Nodes.Count);
        foreach (var node in layout.Nodes)
        {
            nodeScreenPos[node.Id] = vp.LatLonToScreen(node.Latitude, node.Longitude);
        }

        foreach (var edge in layout.Edges)
        {
            if (!nodeScreenPos.TryGetValue(edge.FromNodeId, out var from) || !nodeScreenPos.TryGetValue(edge.ToNodeId, out var to))
            {
                continue;
            }

            var mx = (from.X + to.X) / 2f;
            var my = (from.Y + to.Y) / 2f;
            string debugLabel = $"{edge.TaxiwayName} {edge.FromNodeId}-{edge.ToNodeId}";
            canvas.DrawText(debugLabel, mx + 2, my + 4, _debugEdgeLabelPaint);
        }

        if (layout.Arcs is not null)
        {
            foreach (var arc in layout.Arcs)
            {
                if (!nodeScreenPos.TryGetValue(arc.FromNodeId, out var from) || !nodeScreenPos.TryGetValue(arc.ToNodeId, out var to))
                {
                    continue;
                }

                var mx = (from.X + to.X) / 2f;
                var my = (from.Y + to.Y) / 2f;
                string arcName = arc.TaxiwayNames.Length == 1 ? arc.TaxiwayNames[0] : string.Join(" · ", arc.TaxiwayNames);
                string debugLabel = $"⌒{arcName} {arc.FromNodeId}-{arc.ToNodeId}";
                canvas.DrawText(debugLabel, mx + 2, my + 4, _debugEdgeLabelPaint);
            }
        }

        foreach (var node in layout.Nodes)
        {
            var (sx, sy) = nodeScreenPos[node.Id];
            string debugLabel = node.Name is not null ? $"{node.Id} {node.Name} ({node.Type})" : $"{node.Id} ({node.Type})";
            canvas.DrawText(debugLabel, sx + 5, sy - 3, _debugLabelPaint);
        }
    }

    private void DrawRunways(
        SKCanvas canvas,
        MapViewport vp,
        GroundLayoutDto layout,
        bool showLabels,
        bool drawThresholdMarkers,
        string? hoveredRunwayEnd
    )
    {
        if (layout.Runways is null)
        {
            return;
        }

        foreach (var rwy in layout.Runways)
        {
            if (rwy.Coordinates.Count < 2)
            {
                continue;
            }

            var first = rwy.Coordinates[0];
            var last = rwy.Coordinates[^1];
            if (first.Length < 2 || last.Length < 2)
            {
                continue;
            }

            double heading = GeoMath.BearingTo(first[0], first[1], last[0], last[1]);
            double halfWidthNm = (rwy.WidthFt / 2.0) / GeoMath.FeetPerNm;

            // Perpendicular angle (heading + 90)
            double perpRad = (heading + 90.0) * Math.PI / 180.0;
            double dLat = halfWidthNm / 60.0 * Math.Cos(perpRad);
            double dLon = halfWidthNm / 60.0 * Math.Sin(perpRad) / Math.Cos(first[0] * Math.PI / 180.0);

            // Build the 4 corners of the runway rectangle
            var (x1, y1) = vp.LatLonToScreen(first[0] + dLat, first[1] + dLon);
            var (x2, y2) = vp.LatLonToScreen(first[0] - dLat, first[1] - dLon);
            var (x3, y3) = vp.LatLonToScreen(last[0] - dLat, last[1] - dLon);
            var (x4, y4) = vp.LatLonToScreen(last[0] + dLat, last[1] + dLon);

            using var path = new SKPath();
            path.MoveTo(x1, y1);
            path.LineTo(x2, y2);
            path.LineTo(x3, y3);
            path.LineTo(x4, y4);
            path.Close();

            canvas.DrawPath(path, _runwayFillPaint);
            canvas.DrawPath(path, _runwayOutlinePaint);

            // Draw centerline dashed
            var (cx1, cy1) = vp.LatLonToScreen(first[0], first[1]);
            var (cx2, cy2) = vp.LatLonToScreen(last[0], last[1]);
            canvas.DrawLine(cx1, cy1, cx2, cy2, _runwayPaint);

            // Runway label at midpoint
            double midLat = (first[0] + last[0]) / 2.0;
            double midLon = (first[1] + last[1]) / 2.0;
            var (mx, my) = vp.LatLonToScreen(midLat, midLon);

            if (showLabels)
            {
                string label = RunwayIdentifier.ToDisplayDesignator(rwy.Name.Replace(" - ", "/"));
                _labelCandidates.Add(new LabelCandidate([label], mx, my + 4, LabelPriority.Runway, _runwayLabelPaint, null));
            }

            if (drawThresholdMarkers)
            {
                const float markerRadius = 5f;
                canvas.DrawCircle(cx1, cy1, markerRadius, _runwayThresholdMarkerPaint);
                canvas.DrawCircle(cx1, cy1, markerRadius, _runwayThresholdMarkerOutlinePaint);
                canvas.DrawCircle(cx2, cy2, markerRadius, _runwayThresholdMarkerPaint);
                canvas.DrawCircle(cx2, cy2, markerRadius, _runwayThresholdMarkerOutlinePaint);

                // Hover label: when the user is hovering one of the threshold
                // markers, show "RWY {end}" next to it so they know what they'll
                // be clearing the aircraft into. Mirrors the hold-short hover
                // label rendered in DrawNodes.
                if (hoveredRunwayEnd is not null)
                {
                    var ids = RunwayIdentifier.Parse(rwy.Name);
                    if (string.Equals(ids.End1, hoveredRunwayEnd, StringComparison.OrdinalIgnoreCase))
                    {
                        _labelCandidates.Add(
                            new LabelCandidate(
                                [$"RWY {RunwayIdentifier.ToDisplayDesignator(ids.End1)}"],
                                cx1 + 10,
                                cy1 - 12,
                                LabelPriority.Hovered,
                                _nodeLabelPaint,
                                new SKColor(255, 255, 255)
                            )
                        );
                    }
                    else if (string.Equals(ids.End2, hoveredRunwayEnd, StringComparison.OrdinalIgnoreCase))
                    {
                        _labelCandidates.Add(
                            new LabelCandidate(
                                [$"RWY {RunwayIdentifier.ToDisplayDesignator(ids.End2)}"],
                                cx2 + 10,
                                cy2 - 12,
                                LabelPriority.Hovered,
                                _nodeLabelPaint,
                                new SKColor(255, 255, 255)
                            )
                        );
                    }
                }
            }
        }
    }

    private void DrawEdges(
        SKCanvas canvas,
        MapViewport vp,
        GroundLayoutDto layout,
        bool showDebugInfo,
        bool showTaxiwayLabels,
        GroundFilterMode showParking
    )
    {
        var nodeScreenPos = new Dictionary<int, (float X, float Y)>(layout.Nodes.Count);
        var nodeLatLon = new Dictionary<int, LatLon>(layout.Nodes.Count);
        foreach (var node in layout.Nodes)
        {
            nodeScreenPos[node.Id] = vp.LatLonToScreen(node.Latitude, node.Longitude);
            nodeLatLon[node.Id] = new LatLon(node.Latitude, node.Longitude);
        }

        // Track placed taxiway label positions for deduplication
        var taxiLabelPositions = new Dictionary<string, List<(float X, float Y)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in layout.Edges)
        {
            if (!nodeScreenPos.TryGetValue(edge.FromNodeId, out var from) || !nodeScreenPos.TryGetValue(edge.ToNodeId, out var to))
            {
                continue;
            }

            bool isRunway = edge.IsRunway;
            bool isRamp = edge.IsRamp;

            // RAMP edges follow the parking filter
            if (isRamp && showParking == GroundFilterMode.Off)
            {
                continue;
            }

            SKPaint paint;
            if (isRunway)
            {
                paint = _runwayPaint;
            }
            else if (isRamp)
            {
                _rampEdgePaint.Color =
                    showParking == GroundFilterMode.IconsOnly ? _rampEdgeColor.WithAlpha((byte)(_rampEdgeColor.Alpha / 2)) : _rampEdgeColor;
                paint = _rampEdgePaint;
            }
            else
            {
                paint = _taxiwayPaint;
            }

            if (edge.IntermediatePoints is { Count: > 0 })
            {
                using var path = new SKPath();
                path.MoveTo(from.X, from.Y);
                foreach (var pt in edge.IntermediatePoints)
                {
                    if (pt.Length >= 2)
                    {
                        var (sx, sy) = vp.LatLonToScreen(pt[0], pt[1]);
                        path.LineTo(sx, sy);
                    }
                }

                path.LineTo(to.X, to.Y);
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawLine(from.X, from.Y, to.X, to.Y, paint);
            }

            if (!showDebugInfo && !isRunway && !isRamp && showTaxiwayLabels)
            {
                var mx = (from.X + to.X) / 2f;
                var my = (from.Y + to.Y) / 2f;

                // Skip if too close to an existing label for the same taxiway
                if (IsTaxiLabelTooClose(taxiLabelPositions, edge.TaxiwayName, mx, my))
                {
                    continue;
                }

                if (!taxiLabelPositions.TryGetValue(edge.TaxiwayName, out var positions))
                {
                    positions = [];
                    taxiLabelPositions[edge.TaxiwayName] = positions;
                }

                positions.Add((mx, my));
                _labelCandidates.Add(new LabelCandidate([edge.TaxiwayName], mx + 3, my - 3, LabelPriority.Taxiway, _taxiLabelPaint, null));
            }
        }

        // Draw arcs (bezier curves from fillet generation)
        if (layout.Arcs is not null)
        {
            foreach (var arcDto in layout.Arcs)
            {
                if (
                    !nodeScreenPos.TryGetValue(arcDto.FromNodeId, out var from)
                    || !nodeScreenPos.TryGetValue(arcDto.ToNodeId, out var to)
                    || !nodeLatLon.TryGetValue(arcDto.FromNodeId, out var fromLL)
                    || !nodeLatLon.TryGetValue(arcDto.ToNodeId, out var toLL)
                )
                {
                    continue;
                }

                // Hairpin fillets no fixed-wing aircraft can taxi (turn exceeds the most permissive
                // fixed-wing heading-change limit) are never routed by the pathfinder; drawing them
                // clutters the view with taxi lines aircraft never use.
                if (arcDto.TurnAngleDeg > CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Piston))
                {
                    continue;
                }

                // Corner arcs a blocked turn suppresses are never routed and have no painted line at the
                // apex (the controller uses the connector instead) — omit them from the view.
                if (arcDto.HiddenInGroundView)
                {
                    continue;
                }

                var bezier = new CubicBezier(fromLL.Lat, fromLL.Lon, arcDto.P1Lat, arcDto.P1Lon, arcDto.P2Lat, arcDto.P2Lon, toLL.Lat, toLL.Lon);

                const int steps = 16;
                using var path = new SKPath();
                path.MoveTo(from.X, from.Y);
                for (int s = 1; s < steps; s++)
                {
                    double t = (double)s / steps;
                    var (lat, lon) = bezier.Evaluate(t);
                    var (sx, sy) = vp.LatLonToScreen(lat, lon);
                    path.LineTo(sx, sy);
                }

                path.LineTo(to.X, to.Y);
                canvas.DrawPath(path, _taxiwayPaint);
            }
        }
    }

    private static bool IsTaxiLabelTooClose(Dictionary<string, List<(float X, float Y)>> positions, string name, float x, float y)
    {
        const float minDistSq = 100f * 100f;

        if (!positions.TryGetValue(name, out var existing))
        {
            return false;
        }

        foreach (var (ex, ey) in existing)
        {
            float dx = x - ex;
            float dy = y - ey;
            if (dx * dx + dy * dy < minDistSq)
            {
                return true;
            }
        }

        return false;
    }

    private static void DrawArcSegment(SKCanvas canvas, MapViewport vp, GroundArc arc, (float X, float Y) from, (float X, float Y) to, SKPaint paint)
    {
        var bezier = arc.ToBezier();
        const int steps = 16;
        using var path = new SKPath();
        path.MoveTo(from.X, from.Y);

        for (int s = 1; s < steps; s++)
        {
            double t = (double)s / steps;
            var (lat, lon) = bezier.Evaluate(t);
            var (sx, sy) = vp.LatLonToScreen(lat, lon);
            path.LineTo(sx, sy);
        }

        path.LineTo(to.X, to.Y);
        canvas.DrawPath(path, paint);
    }

    private void DrawActiveRoute(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? route)
    {
        DrawRoute(canvas, vp, layout, route, _activeRoutePaint);
    }

    private void DrawPreviewRoute(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? route)
    {
        DrawRoute(canvas, vp, layout, route, _previewRoutePaint);
    }

    private void DrawShownTaxiRoutes(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, IReadOnlyList<ShownTaxiRouteEntry>? entries)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            int colorIdx = Array.IndexOf(TaxiRouteColorValues, entry.Color);
            if (colorIdx < 0)
            {
                colorIdx = 0;
            }

            DrawRoute(canvas, vp, layout, entry.Route, _shownTaxiRoutePaints[colorIdx]);
        }
    }

    private void DrawDrawHoverPreview(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? hoverRoute)
    {
        DrawRoute(canvas, vp, layout, hoverRoute, _drawHoverPreviewPaint);
    }

    private void DrawDrawnRoute(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? drawnRoute, IReadOnlyList<int>? waypoints)
    {
        DrawRoute(canvas, vp, layout, drawnRoute, _drawnRoutePaint);

        if (waypoints is null || waypoints.Count == 0 || layout.Nodes.Count == 0)
        {
            return;
        }

        var nodePositions = new Dictionary<int, (float X, float Y)>();
        foreach (var node in layout.Nodes)
        {
            nodePositions[node.Id] = vp.LatLonToScreen(node.Latitude, node.Longitude);
        }

        for (int i = 0; i < waypoints.Count; i++)
        {
            if (!nodePositions.TryGetValue(waypoints[i], out var pos))
            {
                continue;
            }

            canvas.DrawCircle(pos.X, pos.Y, 8f, _waypointMarkerPaint);
            canvas.DrawText($"{i + 1}", pos.X, pos.Y + _waypointTextPaint.TextSize / 3f, _waypointTextPaint);
        }
    }

    private static void DrawRoute(SKCanvas canvas, MapViewport vp, GroundLayoutDto layout, TaxiRoute? route, SKPaint paint)
    {
        if (route is null)
        {
            return;
        }

        var nodeScreenPos = new Dictionary<int, (float X, float Y)>();
        foreach (var node in layout.Nodes)
        {
            nodeScreenPos[node.Id] = vp.LatLonToScreen(node.Latitude, node.Longitude);
        }

        foreach (var seg in route.Segments)
        {
            if (!nodeScreenPos.TryGetValue(seg.FromNodeId, out var from) || !nodeScreenPos.TryGetValue(seg.ToNodeId, out var to))
            {
                continue;
            }

            if (seg.Edge.Edge is GroundArc arc)
            {
                DrawArcSegment(canvas, vp, arc, from, to, paint);
            }
            else if (seg.Edge.Edge is GroundEdge straight && straight.IntermediatePoints.Count > 0)
            {
                using var path = new SKPath();
                path.MoveTo(from.X, from.Y);
                foreach (var (lat, lon) in straight.IntermediatePoints)
                {
                    var (sx, sy) = vp.LatLonToScreen(lat, lon);
                    path.LineTo(sx, sy);
                }

                path.LineTo(to.X, to.Y);
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawLine(from.X, from.Y, to.X, to.Y, paint);
            }
        }
    }

    private void DrawNodes(
        SKCanvas canvas,
        MapViewport vp,
        GroundLayoutDto layout,
        int? hoveredNodeId,
        bool showDebugInfo,
        GroundFilterMode showHoldShort,
        GroundFilterMode showParking,
        GroundFilterMode showSpot
    )
    {
        // Pre-build node→taxiway screen angle map for hold short bar rendering
        var holdShortAngles = new Dictionary<int, float>();
        if (showHoldShort != GroundFilterMode.Off)
        {
            foreach (var edge in layout.Edges)
            {
                ComputeHoldShortAngle(holdShortAngles, vp, layout, edge, edge.FromNodeId);
                ComputeHoldShortAngle(holdShortAngles, vp, layout, edge, edge.ToNodeId);
            }
        }

        // Pre-build node→connected taxiway names for hover tooltips
        var nodeEdgeNames = new Dictionary<int, List<string>>();
        foreach (var edge in layout.Edges)
        {
            if (edge.IsRunway || edge.IsRamp)
            {
                continue;
            }

            AddEdgeName(nodeEdgeNames, edge.FromNodeId, edge.TaxiwayName);
            AddEdgeName(nodeEdgeNames, edge.ToNodeId, edge.TaxiwayName);
        }

        if (layout.Arcs is not null)
        {
            foreach (var arc in layout.Arcs)
            {
                foreach (string name in arc.TaxiwayNames)
                {
                    if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddEdgeName(nodeEdgeNames, arc.FromNodeId, name);
                    AddEdgeName(nodeEdgeNames, arc.ToNodeId, name);
                }
            }
        }

        var pxPerFt = (float)(vp.Zoom * 5000.0 / FeetPerDegLat);

        foreach (var node in layout.Nodes)
        {
            var (sx, sy) = vp.LatLonToScreen(node.Latitude, node.Longitude);
            bool isHovered = hoveredNodeId == node.Id;

            if (node.Type == "RunwayHoldShort")
            {
                bool drawIcon = showHoldShort != GroundFilterMode.Off || isHovered;
                if (drawIcon)
                {
                    DrawHoldShortBar(canvas, sx, sy, holdShortAngles.GetValueOrDefault(node.Id, 0f), pxPerFt);
                }

                if (node.RunwayId is not null)
                {
                    if (isHovered)
                    {
                        var twyNames = ResolveNearbyTaxiwayNames(node.Id, layout, nodeEdgeNames);
                        string hsLabel = $"HS {RunwayIdentifier.ToDisplayDesignator(node.RunwayId)}";
                        string[] lines = twyNames.Count > 0 ? [hsLabel, string.Join("/", twyNames)] : [hsLabel];
                        _labelCandidates.Add(
                            new LabelCandidate(lines, sx + 12, sy - 14, LabelPriority.Hovered, _nodeLabelPaint, new SKColor(255, 255, 255))
                        );
                    }
                    else if (showHoldShort == GroundFilterMode.LabelsAndIcons)
                    {
                        _labelCandidates.Add(
                            new LabelCandidate(
                                [$"HS {RunwayIdentifier.ToDisplayDesignator(node.RunwayId)}"],
                                sx + 5,
                                sy - 3,
                                LabelPriority.HoldShort,
                                _nodeLabelPaint,
                                null
                            )
                        );
                    }
                }
            }
            else if (node.Type is "Parking" or "Helipad" or "Spot")
            {
                var mode = node.Type == "Spot" ? showSpot : showParking;
                bool drawIcon = mode != GroundFilterMode.Off || isHovered;

                if (drawIcon)
                {
                    _nodePaint.Color = node.Type switch
                    {
                        "Parking" => NodeParking,
                        "Helipad" => NodeHelipad,
                        _ => NodeSpot,
                    };

                    float radius = node.Type switch
                    {
                        "Parking" => 4f,
                        "Helipad" => 5f,
                        _ => 2.5f,
                    };

                    canvas.DrawCircle(sx, sy, radius, _nodePaint);

                    if (node.Type == "Helipad")
                    {
                        _nodeLabelPaint.Color = NodeHelipad;
                        canvas.DrawText("H", sx - 3, sy + 3, _nodeLabelPaint);
                    }
                }

                if (!showDebugInfo && node.Name is not null)
                {
                    if (isHovered)
                    {
                        var lines = BuildHoverLines(node.Name, node.Id, nodeEdgeNames);
                        _labelCandidates.Add(
                            new LabelCandidate(lines, sx + 12, sy - 14, LabelPriority.Hovered, _nodeLabelPaint, new SKColor(255, 255, 255))
                        );
                    }
                    else if (mode == GroundFilterMode.LabelsAndIcons)
                    {
                        _labelCandidates.Add(new LabelCandidate([node.Name], sx + 5, sy - 3, LabelPriority.ParkingSpot, _nodeLabelPaint, null));
                    }
                }
            }
            else
            {
                // Intersection nodes — always drawn
                _nodePaint.Color = NodeIntersection;
                canvas.DrawCircle(sx, sy, 2.5f, _nodePaint);

                if (isHovered && nodeEdgeNames.TryGetValue(node.Id, out var twyNames) && twyNames.Count > 0)
                {
                    _labelCandidates.Add(
                        new LabelCandidate(
                            [string.Join("/", twyNames)],
                            sx + 12,
                            sy - 14,
                            LabelPriority.Hovered,
                            _nodeLabelPaint,
                            new SKColor(255, 255, 255)
                        )
                    );
                }
            }

            if (isHovered)
            {
                canvas.DrawCircle(sx, sy, 8f, _hoverPaint);
            }
        }
    }

    private static void AddEdgeName(Dictionary<int, List<string>> map, int nodeId, string name)
    {
        if (!map.TryGetValue(nodeId, out var list))
        {
            list = [];
            map[nodeId] = list;
        }

        if (!list.Contains(name))
        {
            list.Add(name);
        }
    }

    /// <summary>
    /// For hold-short nodes, finds taxiway names by looking at direct edges first,
    /// then one hop through RAMP edges to reach named taxiways.
    /// </summary>
    private static List<string> ResolveNearbyTaxiwayNames(int nodeId, GroundLayoutDto layout, Dictionary<int, List<string>> nodeEdgeNames)
    {
        // Direct taxiway edges on this node
        if (nodeEdgeNames.TryGetValue(nodeId, out var direct) && direct.Count > 0)
        {
            return direct;
        }

        // One hop: find neighbors via any edge or arc, then check their taxiway names
        var result = new List<string>();
        foreach (var edge in layout.Edges)
        {
            int neighborId;
            if (edge.FromNodeId == nodeId)
            {
                neighborId = edge.ToNodeId;
            }
            else if (edge.ToNodeId == nodeId)
            {
                neighborId = edge.FromNodeId;
            }
            else
            {
                continue;
            }

            AddNeighborNames(result, neighborId, nodeEdgeNames);
        }

        if (layout.Arcs is not null)
        {
            foreach (var arc in layout.Arcs)
            {
                int neighborId;
                if (arc.FromNodeId == nodeId)
                {
                    neighborId = arc.ToNodeId;
                }
                else if (arc.ToNodeId == nodeId)
                {
                    neighborId = arc.FromNodeId;
                }
                else
                {
                    continue;
                }

                AddNeighborNames(result, neighborId, nodeEdgeNames);
            }
        }

        return result;
    }

    private static void AddNeighborNames(List<string> result, int neighborId, Dictionary<int, List<string>> nodeEdgeNames)
    {
        if (nodeEdgeNames.TryGetValue(neighborId, out var neighborNames))
        {
            foreach (var name in neighborNames)
            {
                if (!result.Contains(name))
                {
                    result.Add(name);
                }
            }
        }
    }

    private static string[] BuildHoverLines(string primaryLabel, int nodeId, Dictionary<int, List<string>> nodeEdgeNames)
    {
        if (!nodeEdgeNames.TryGetValue(nodeId, out var twyNames) || twyNames.Count == 0)
        {
            return [primaryLabel];
        }

        return [primaryLabel, string.Join("/", twyNames)];
    }

    /// <summary>
    /// Computes the screen-space angle (radians) of the taxiway edge at a hold-short node.
    /// Uses screen coordinates so rotation is handled correctly.
    /// </summary>
    private static void ComputeHoldShortAngle(Dictionary<int, float> angles, MapViewport vp, GroundLayoutDto layout, GroundEdgeDto edge, int nodeId)
    {
        if (angles.ContainsKey(nodeId))
        {
            return;
        }

        // Only use non-runway edges so the bar is perpendicular to the taxiway, not the runway
        if (edge.IsRunway)
        {
            return;
        }

        var node = layout.Nodes.Find(n => n.Id == nodeId);
        if (node is null || node.Type != "RunwayHoldShort")
        {
            return;
        }

        int otherId = edge.FromNodeId == nodeId ? edge.ToNodeId : edge.FromNodeId;
        var other = layout.Nodes.Find(n => n.Id == otherId);
        if (other is null)
        {
            return;
        }

        var (nx, ny) = vp.LatLonToScreen(node.Latitude, node.Longitude);
        var (ox, oy) = vp.LatLonToScreen(other.Latitude, other.Longitude);
        float angle = MathF.Atan2(oy - ny, ox - nx);
        angles[nodeId] = angle;
    }

    /// <summary>Real-world half-width of the hold short bar in feet (~60 ft total, typical taxiway width).</summary>
    private const float HoldShortHalfWidthFt = 30f;

    /// <summary>Minimum half-length in pixels so bars remain visible when zoomed out.</summary>
    private const float MinHoldShortHalfPx = 5f;

    /// <summary>Minimum stroke width in pixels for hold short bars.</summary>
    private const float MinHoldShortStrokePx = 1.5f;

    /// <summary>Hold short bar stroke width in feet (≈1 ft painted line).</summary>
    private const float HoldShortStrokeWidthFt = 1.5f;

    private void DrawHoldShortBar(SKCanvas canvas, float sx, float sy, float taxiwayAngleRad, float pxPerFt)
    {
        float halfLen = MathF.Max(HoldShortHalfWidthFt * pxPerFt, MinHoldShortHalfPx);
        // Perpendicular to the taxiway direction
        float perpAngle = taxiwayAngleRad + MathF.PI / 2f;
        float dx = halfLen * MathF.Cos(perpAngle);
        float dy = halfLen * MathF.Sin(perpAngle);

        _holdShortBarPaint.Color = _holdShortColor;
        _holdShortBarPaint.StrokeWidth = MathF.Max(HoldShortStrokeWidthFt * pxPerFt, MinHoldShortStrokePx);
        canvas.DrawLine(sx - dx, sy - dy, sx + dx, sy + dy, _holdShortBarPaint);
    }

    /// <summary>Minimum triangle half-length in pixels (ensures visibility when zoomed out).</summary>
    private const float MinAircraftPx = 8f;

    /// <summary>
    /// The user-selected aircraft can have a slight scale-up for visual emphasis.
    /// Disabled for now.
    /// </summary>
    private const float SelectedScaleFactor = 1.0f;

    /// <summary>Fallback dimensions when FAA ACD data is unavailable (small GA aircraft).</summary>
    private const float FallbackLengthFt = 60f;
    private const float FallbackWingspanFt = 50f;

    /// <summary>Feet per degree of latitude (constant).</summary>
    private const double FeetPerDegLat = 364_567.2;

    private void DrawAircraft(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        double airportCenterLat,
        double airportCenterLon,
        double airportElevation
    )
    {
        // Pixels per foot at current zoom (latitude direction, no cosine correction needed for small areas)
        var pxPerFt = (float)(vp.Zoom * 5000.0 / FeetPerDegLat);

        foreach (var ac in aircraft)
        {
            if (!ac.IsOnGround && !IsAirborneVisible(ac, airportCenterLat, airportCenterLon, airportElevation))
            {
                continue;
            }

            var (sx, sy) = vp.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
            bool isSelected = ac == selectedAircraft;
            bool isAirborne = !ac.IsOnGround;

            _aircraftPaint.Color = _aircraftColor;

            var (lengthPx, widthPx) = ComputeAircraftPixelSize(ac.AircraftType, pxPerFt);
            if (isSelected)
            {
                lengthPx *= SelectedScaleFactor;
                widthPx *= SelectedScaleFactor;
            }

            bool isHeli = AircraftCategorization.Categorize(ac.AircraftType) == AircraftCategory.Helicopter;
            float headingDeg = (float)(ac.Heading.Degrees - vp.RotationDeg);

            if (isHeli)
            {
                float rotorPx = ComputeRotorPixelRadius(ac.AircraftType, pxPerFt);
                DrawHelicopterSilhouette(canvas, sx, sy, headingDeg, lengthPx, widthPx, rotorPx, _aircraftPaint);
            }
            else
            {
                DrawFixedWingSilhouette(canvas, sx, sy, headingDeg, lengthPx, widthPx, _aircraftPaint);
            }
        }
    }

    /// <summary>
    /// Returns (halfLengthPx, halfWidthPx) for the aircraft silhouette.
    /// Uses FAA ACD dimensions when available, otherwise category fallbacks.
    /// Clamps to <see cref="MinAircraftPx"/> so aircraft remain visible when zoomed out.
    /// </summary>
    private static (float HalfLengthPx, float HalfWidthPx) ComputeAircraftPixelSize(string? aircraftType, float pxPerFt)
    {
        float lengthFt = FallbackLengthFt;
        float wingspanFt = FallbackWingspanFt;

        var record = FaaAircraftDatabase.Get(aircraftType);
        if (record is not null)
        {
            if (record.LengthFt is { } len)
            {
                lengthFt = (float)len;
            }

            if (record.WingspanFt is { } ws)
            {
                wingspanFt = (float)ws;
            }
        }

        float halfLenPx = MathF.Max(lengthFt * 0.5f * pxPerFt, MinAircraftPx);
        float halfWsPx = MathF.Max(wingspanFt * 0.5f * pxPerFt, MinAircraftPx * (wingspanFt / lengthFt));
        return (halfLenPx, halfWsPx);
    }

    /// <summary>Fallback rotor diameter when FAA ACD data is unavailable (medium helicopter).</summary>
    private const float FallbackRotorDiameterFt = 40f;

    private static float ComputeRotorPixelRadius(string? aircraftType, float pxPerFt)
    {
        float diameterFt = FallbackRotorDiameterFt;
        var record = FaaAircraftDatabase.Get(aircraftType);
        if (record?.RotorDiameterFt is { } rd)
        {
            diameterFt = (float)rd;
        }

        return MathF.Max(diameterFt * 0.5f * pxPerFt, MinAircraftPx);
    }

    /// <summary>
    /// Draws a top-down fixed-wing aircraft silhouette centered at (cx, cy).
    /// The silhouette spans exactly nose-to-tail = 2 * halfLength and wingtip-to-wingtip = 2 * halfWidth.
    /// Fuselage, swept wings, and horizontal stabilizer are drawn as a single filled path.
    /// </summary>
    private static void DrawFixedWingSilhouette(
        SKCanvas canvas,
        float cx,
        float cy,
        float headingDeg,
        float halfLength,
        float halfWidth,
        SKPaint paint
    )
    {
        // All coordinates in normalized space: X = along heading (-1 = tail, +1 = nose), Y = lateral (-1 = left, +1 = right).
        // Scaled by halfLength (X) and halfWidth (Y), then rotated and translated.

        // Fuselage width as fraction of wingspan
        float fuseW = 0.08f;

        // --- Build the silhouette path in normalized coords ---
        // We trace clockwise starting from the nose.
        // Nose tip
        SKPoint[] pts =
        [
            // Nose cone
            new(1.0f, 0f),
            // Fuselage widens
            new(0.7f, fuseW),
            // Wing leading edge root
            new(0.25f, fuseW),
            // Wing tip leading edge (swept back)
            new(0.05f, 1.0f),
            // Wing tip trailing edge
            new(-0.15f, 0.85f),
            // Wing trailing edge root
            new(-0.15f, fuseW),
            // Fuselage continues aft
            new(-0.6f, fuseW),
            // Horizontal stabilizer tip leading edge
            new(-0.7f, 0.4f),
            // Horizontal stabilizer tip trailing edge
            new(-0.9f, 0.35f),
            // Tail tip
            new(-0.95f, fuseW),
            // Tail end center
            new(-1.0f, 0f),
            // Mirror: tail tip (left side)
            new(-0.95f, -fuseW),
            // Horizontal stabilizer tip trailing edge (left)
            new(-0.9f, -0.35f),
            // Horizontal stabilizer tip leading edge (left)
            new(-0.7f, -0.4f),
            // Fuselage continues aft (left)
            new(-0.6f, -fuseW),
            // Wing trailing edge root (left)
            new(-0.15f, -fuseW),
            // Wing tip trailing edge (left)
            new(-0.15f, -0.85f),
            // Wing tip leading edge (left)
            new(0.05f, -1.0f),
            // Wing leading edge root (left)
            new(0.25f, -fuseW),
            // Fuselage narrows toward nose (left)
            new(0.7f, -fuseW),
        ];

        DrawRotatedSilhouette(canvas, cx, cy, headingDeg, halfLength, halfWidth, pts, paint);
    }

    /// <summary>
    /// Draws a top-down helicopter silhouette centered at (cx, cy).
    /// Shows fuselage (oval body), tail boom, tail rotor disc, and main rotor disc.
    /// HalfLength/halfWidth define the fuselage; rotorRadius defines the main rotor disc.
    /// </summary>
    private static void DrawHelicopterSilhouette(
        SKCanvas canvas,
        float cx,
        float cy,
        float headingDeg,
        float halfLength,
        float halfWidth,
        float rotorRadius,
        SKPaint paint
    )
    {
        float rad = (headingDeg - 90) * MathF.PI / 180f;
        float cosH = MathF.Cos(rad);
        float sinH = MathF.Sin(rad);
        float cosP = MathF.Cos(rad + MathF.PI / 2f);
        float sinP = MathF.Sin(rad + MathF.PI / 2f);

        // --- Fuselage body (teardrop shape) ---
        // Wider at front, tapers to tail boom
        float fuseW = 0.25f;
        SKPoint[] bodyPts =
        [
            new(0.5f, 0f),
            new(0.3f, fuseW),
            new(-0.1f, fuseW),
            new(-0.3f, 0.08f),
            // Tail boom
            new(-0.9f, 0.03f),
            new(-1.0f, 0f),
            new(-0.9f, -0.03f),
            // Back to body
            new(-0.3f, -0.08f),
            new(-0.1f, -fuseW),
            new(0.3f, -fuseW),
        ];

        DrawRotatedSilhouette(canvas, cx, cy, headingDeg, halfLength, halfWidth, bodyPts, paint);

        // --- Main rotor disc (circle centered slightly forward of body center) ---
        float rotorCenterOffset = halfLength * 0.1f;
        float rotorCx = cx + cosH * rotorCenterOffset;
        float rotorCy = cy + sinH * rotorCenterOffset;
        float effectiveRotorR = MathF.Max(rotorRadius, MinAircraftPx);

        using var rotorPaint = paint.Clone();
        rotorPaint.Style = SKPaintStyle.Stroke;
        rotorPaint.StrokeWidth = MathF.Max(1f, halfLength * 0.03f);
        canvas.DrawCircle(rotorCx, rotorCy, effectiveRotorR, rotorPaint);

        // Rotor cross lines
        canvas.DrawLine(
            rotorCx + cosH * effectiveRotorR,
            rotorCy + sinH * effectiveRotorR,
            rotorCx - cosH * effectiveRotorR,
            rotorCy - sinH * effectiveRotorR,
            rotorPaint
        );
        canvas.DrawLine(
            rotorCx + cosP * effectiveRotorR,
            rotorCy + sinP * effectiveRotorR,
            rotorCx - cosP * effectiveRotorR,
            rotorCy - sinP * effectiveRotorR,
            rotorPaint
        );

        // --- Tail rotor disc (small circle at tail boom end) ---
        float tailRotorOffset = halfLength * 0.95f;
        float tailRotorCx = cx - cosH * tailRotorOffset;
        float tailRotorCy = cy - sinH * tailRotorOffset;
        float tailRotorR = MathF.Max(halfLength * 0.12f, 2f);
        canvas.DrawCircle(tailRotorCx, tailRotorCy, tailRotorR, rotorPaint);
    }

    /// <summary>
    /// Transforms normalized silhouette points (X along heading, Y lateral) to screen space
    /// and draws them as a filled polygon.
    /// </summary>
    private static void DrawRotatedSilhouette(
        SKCanvas canvas,
        float cx,
        float cy,
        float headingDeg,
        float halfLength,
        float halfWidth,
        SKPoint[] normalizedPts,
        SKPaint paint
    )
    {
        float rad = (headingDeg - 90) * MathF.PI / 180f;
        float cosH = MathF.Cos(rad);
        float sinH = MathF.Sin(rad);
        float cosP = MathF.Cos(rad + MathF.PI / 2f);
        float sinP = MathF.Sin(rad + MathF.PI / 2f);

        using var path = new SKPath();
        for (int i = 0; i < normalizedPts.Length; i++)
        {
            float ax = normalizedPts[i].X * halfLength;
            float ay = normalizedPts[i].Y * halfWidth;
            float screenX = cx + cosH * ax + cosP * ay;
            float screenY = cy + sinH * ax + sinP * ay;

            if (i == 0)
            {
                path.MoveTo(screenX, screenY);
            }
            else
            {
                path.LineTo(screenX, screenY);
            }
        }

        path.Close();
        canvas.DrawPath(path, paint);
    }

    private void DrawDataBlocks(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        double airportCenterLat,
        double airportCenterLon,
        double airportElevation,
        IReadOnlySet<string>? highlightedCallsigns,
        IReadOnlySet<string>? hiddenDataBlockCallsigns
    )
    {
        // Two-pass render so bubbled aircraft's datablock + bubble paint above neighbors.
        // List allocated only when bubbles are enabled.
        _lastBubbleRects.Clear();
        List<AircraftModel>? deferred = null;
        var now = ShowSpeechBubbles ? DateTime.UtcNow : default;

        foreach (var ac in aircraft)
        {
            if (ShowSpeechBubbles && IsBubbleActive(ac.SpeechBubble, now))
            {
                deferred ??= new List<AircraftModel>();
                deferred.Add(ac);
                continue;
            }
            DrawOneDataBlock(
                canvas,
                vp,
                ac,
                selectedAircraft,
                dataBlockOffsets,
                airportCenterLat,
                airportCenterLon,
                airportElevation,
                highlightedCallsigns,
                hiddenDataBlockCallsigns,
                drawBubble: false
            );
        }

        if (deferred is not null)
        {
            foreach (var ac in deferred)
            {
                DrawOneDataBlock(
                    canvas,
                    vp,
                    ac,
                    selectedAircraft,
                    dataBlockOffsets,
                    airportCenterLat,
                    airportCenterLon,
                    airportElevation,
                    highlightedCallsigns,
                    hiddenDataBlockCallsigns,
                    drawBubble: true
                );
            }
        }
    }

    private static bool IsBubbleActive(AircraftSpeechBubble? bubble, DateTime now) => bubble is not null && bubble.ExpiresAt > now;

    private void DrawOneDataBlock(
        SKCanvas canvas,
        MapViewport vp,
        AircraftModel ac,
        AircraftModel? selectedAircraft,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        double airportCenterLat,
        double airportCenterLon,
        double airportElevation,
        IReadOnlySet<string>? highlightedCallsigns,
        IReadOnlySet<string>? hiddenDataBlockCallsigns,
        bool drawBubble
    )
    {
        bool isAirborne = !ac.IsOnGround;
        if (isAirborne && !IsAirborneVisible(ac, airportCenterLat, airportCenterLon, airportElevation))
        {
            return;
        }

        bool isSelected = ac == selectedAircraft;
        bool isHidden = hiddenDataBlockCallsigns is not null && hiddenDataBlockCallsigns.Contains(ac.Callsign);
        if (isHidden && !isSelected)
        {
            return;
        }

        var (sx, sy) = vp.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

        SKPoint offset = DataBlockLayout.DefaultOffset;
        if (dataBlockOffsets is not null && dataBlockOffsets.TryGetValue(ac.Callsign, out var customOffset))
        {
            offset = customOffset;
        }

        var layout = DataBlockLayout.Compute(ac, sx, sy, offset, _dataBlockTextPaint, isAirborne);

        bool isHighlighted = highlightedCallsigns is not null && highlightedCallsigns.Contains(ac.Callsign);
        var dbColor =
            isHighlighted ? SKColors.Cyan
            : isSelected ? _aircraftColor
            : _datablockTextColor;

        _dataBlockTextPaint.Color = dbColor;
        canvas.DrawRect(layout.Rect, _dataBlockBgPaint);

        if (isSelected)
        {
            _dataBlockLeaderPaint.Color = _aircraftColor;
            canvas.DrawRect(layout.Rect, _dataBlockLeaderPaint);
        }

        var leaderEnd = ClampToBlockEdge(sx, sy, layout.Rect);
        _dataBlockLeaderPaint.Color = dbColor;
        canvas.DrawLine(sx, sy, leaderEnd.X, leaderEnd.Y, _dataBlockLeaderPaint);

        canvas.DrawText(layout.Line1, layout.TextX, layout.TextY, _dataBlockTextPaint);
        canvas.DrawText(layout.Line2, layout.TextX, layout.TextY + layout.LineHeight, _dataBlockTextPaint);
        int row = 2;
        if (layout.Line3.Length > 0)
        {
            canvas.DrawText(layout.Line3, layout.TextX, layout.TextY + layout.LineHeight * row, _dataBlockTextPaint);
            row++;
        }

        if (layout.Line4.Length > 0)
        {
            canvas.DrawText(layout.Line4, layout.TextX, layout.TextY + layout.LineHeight * row, _dataBlockTextPaint);
        }

        // Instructor note: always the bottom line of the block, drawn in amber.
        if (layout.Line5.Length > 0)
        {
            _dataBlockTextPaint.Color = NoteColor;
            canvas.DrawText(layout.Line5, layout.TextX, layout.TextY + layout.LineHeight * (layout.LineCount - 1), _dataBlockTextPaint);
            _dataBlockTextPaint.Color = dbColor;
        }

        if (drawBubble && ac.SpeechBubble is { } bubble)
        {
            var bubbleRect = DrawSpeechBubble(canvas, layout.Rect, bubble.Text, bubble.Severity);
            if (bubbleRect is { } r)
            {
                _lastBubbleRects[ac.Callsign] = r;
            }
        }
    }

    private const int SpeechBubbleMaxLineChars = 36;
    private const int SpeechBubbleMaxLines = 3;

    private SKRect? DrawSpeechBubble(SKCanvas canvas, SKRect anchor, string text, SpeechBubbleSeverity severity)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var lines = WrapBubbleText(text);
        if (lines.Count == 0)
        {
            return null;
        }

        float lineH = _bubbleTextPaint.TextSize + 2;
        float maxLineWidth = 0;
        foreach (var line in lines)
        {
            float w = _bubbleTextPaint.MeasureText(line);
            if (w > maxLineWidth)
            {
                maxLineWidth = w;
            }
        }

        const float pad = 4f;
        const float gap = 4f;
        float left = anchor.Left;
        float top = anchor.Bottom + gap;
        float right = left + maxLineWidth + 2 * pad;
        float bottom = top + lines.Count * lineH + 2 * pad - 2;
        var rect = new SKRect(left, top, right, bottom);

        var fillPaint = severity == SpeechBubbleSeverity.Warning ? _bubbleFillPaintWarning : _bubbleFillPaint;
        var borderPaint = severity == SpeechBubbleSeverity.Warning ? _bubbleBorderPaintWarning : _bubbleBorderPaint;
        canvas.DrawRoundRect(rect, 3f, 3f, fillPaint);
        canvas.DrawRoundRect(rect, 3f, 3f, borderPaint);

        float textX = left + pad;
        float baseline = top + pad + _bubbleTextPaint.TextSize;
        for (int i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], textX, baseline + i * lineH, _bubbleTextPaint);
        }

        return rect;
    }

    private static List<string> WrapBubbleText(string text)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (word.Length >= SpeechBubbleMaxLineChars)
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    if (lines.Count >= SpeechBubbleMaxLines)
                    {
                        break;
                    }
                }
                lines.Add(word[..Math.Min(SpeechBubbleMaxLineChars, word.Length)]);
                if (lines.Count >= SpeechBubbleMaxLines)
                {
                    break;
                }
                continue;
            }

            int prospective = current.Length + (current.Length > 0 ? 1 : 0) + word.Length;
            if (prospective > SpeechBubbleMaxLineChars)
            {
                lines.Add(current.ToString());
                current.Clear();
                if (lines.Count >= SpeechBubbleMaxLines)
                {
                    break;
                }
            }
            if (current.Length > 0)
            {
                current.Append(' ');
            }
            current.Append(word);
        }
        if (current.Length > 0 && lines.Count < SpeechBubbleMaxLines)
        {
            lines.Add(current.ToString());
        }

        if (lines.Count == SpeechBubbleMaxLines)
        {
            int totalUsed = lines.Sum(l => l.Length) + (lines.Count - 1);
            if (totalUsed < text.Length)
            {
                var last = lines[^1];
                if (last.Length >= SpeechBubbleMaxLineChars - 1)
                {
                    last = last[..(SpeechBubbleMaxLineChars - 1)];
                }
                lines[^1] = last + "…";
            }
        }

        return lines;
    }

    private static bool IsAirborneVisible(AircraftModel ac, double airportCenterLat, double airportCenterLon, double airportElevation)
    {
        double agl = ac.Altitude - airportElevation;
        if (agl > AirborneMaxAglFt)
        {
            return false;
        }

        double dist = GeoMath.DistanceNm(ac.Position, new LatLon(airportCenterLat, airportCenterLon));
        return dist <= AirborneMaxRangeNm;
    }

    private static SKPoint ClampToBlockEdge(float pointX, float pointY, SKRect rect)
    {
        if (rect.Contains(pointX, pointY))
        {
            return new SKPoint(pointX, pointY);
        }

        return new SKPoint(Math.Clamp(pointX, rect.Left, rect.Right), Math.Clamp(pointY, rect.Top, rect.Bottom));
    }

    private void DrawLabels(SKCanvas canvas, bool hoveredOnly)
    {
        _labelCandidates.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        var placedRects = new List<SKRect>(_labelCandidates.Count);

        foreach (var label in _labelCandidates)
        {
            bool isHovered = label.Priority == LabelPriority.Hovered;
            if (hoveredOnly != isHovered)
            {
                continue;
            }

            var paint = label.Paint;
            float textHeight = paint.TextSize;
            float lineSpacing = textHeight + 2;
            int lineCount = label.Lines.Length;

            float maxWidth = 0;
            foreach (var line in label.Lines)
            {
                float w = paint.MeasureText(line);
                if (w > maxWidth)
                {
                    maxWidth = w;
                }
            }

            float left = paint.TextAlign == SKTextAlign.Center ? label.X - maxWidth / 2f - 2 : label.X - 2;
            float totalHeight = textHeight + (lineCount - 1) * lineSpacing;
            var rect = new SKRect(left, label.Y - textHeight - 1, left + maxWidth + 4, label.Y + (totalHeight - textHeight) + 1);

            bool overlaps = false;
            foreach (var placed in placedRects)
            {
                if (rect.IntersectsWith(placed))
                {
                    overlaps = true;
                    break;
                }
            }

            if (overlaps)
            {
                continue;
            }

            placedRects.Add(rect);

            canvas.DrawRect(rect, _labelBgPaint);

            if (label.ColorOverride is { } color)
            {
                paint.Color = color;
            }

            float y = label.Y;
            foreach (var line in label.Lines)
            {
                canvas.DrawText(line, label.X, y, paint);
                y += lineSpacing;
            }
        }
    }

    public void Dispose()
    {
        foreach (var paint in _shownTaxiRoutePaints)
        {
            paint.Dispose();
        }

        _runwayFillPaint.Dispose();
        _runwayOutlinePaint.Dispose();
        _runwayLabelPaint.Dispose();
        _runwayPaint.Dispose();
        _taxiwayPaint.Dispose();
        _rampEdgePaint.Dispose();
        _taxiLabelPaint.Dispose();
        _activeRoutePaint.Dispose();
        _previewRoutePaint.Dispose();
        _drawnRoutePaint.Dispose();
        _drawHoverPreviewPaint.Dispose();
        _waypointMarkerPaint.Dispose();
        _waypointTextPaint.Dispose();
        _nodePaint.Dispose();
        _holdShortBarPaint.Dispose();
        _nodeLabelPaint.Dispose();
        _aircraftPaint.Dispose();
        _hoverPaint.Dispose();
        _dataBlockLeaderPaint.Dispose();
        _dataBlockTextPaint.Dispose();
        _dataBlockBgPaint.Dispose();
        _labelBgPaint.Dispose();
        _bgPaint.Dispose();
        _debugLabelPaint.Dispose();
        _debugEdgeLabelPaint.Dispose();
        _bubbleFillPaint.Dispose();
        _bubbleBorderPaint.Dispose();
        _bubbleFillPaintWarning.Dispose();
        _bubbleBorderPaintWarning.Dispose();
        _bubbleTextPaint.Dispose();
    }
}
