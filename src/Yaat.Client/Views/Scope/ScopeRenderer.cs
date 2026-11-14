using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Scope;

/// <summary>Everything one scope frame reads to lay its tracks out.</summary>
/// <param name="Aircraft">The tracks to draw, already filtered for visibility.</param>
/// <param name="Selected">The selected aircraft, or null.</param>
/// <param name="Viewport">The viewport the frame is drawn in.</param>
/// <param name="EuroScopeMode">Whether the EuroScope template is in use (else the STARS template).</param>
/// <param name="Metrics">Datablock text measurements.</param>
/// <param name="Alerts">Alert overlays the user's preferences allow.</param>
/// <param name="ManualOffsets">Per-callsign block offsets from the symbol; a callsign absent here takes the default offset.</param>
public sealed record ScopeFrameInput(
    IReadOnlyList<AircraftModel> Aircraft,
    AircraftModel? Selected,
    MapViewport Viewport,
    bool EuroScopeMode,
    ScopeTextMetrics Metrics,
    ScopeAlertOptions Alerts,
    IReadOnlyDictionary<string, SKPoint> ManualOffsets
);

/// <summary>One track as the frame draws it: its symbol's screen point and its block laid out at its placed position.</summary>
/// <param name="Aircraft">The track.</param>
/// <param name="Symbol">Screen point of the position symbol.</param>
/// <param name="Block">The block's one layout, placed in screen coordinates; drawing and hit-testing both read it.</param>
/// <param name="IsSelected">Whether the track is selected.</param>
public sealed record ScopeTrack(AircraftModel Aircraft, SKPoint Symbol, ScopeBlockLayout Block, bool IsSelected);

/// <summary>A field a point landed on, and whose block it is.</summary>
public sealed record ScopeFieldHit(AircraftModel Aircraft, FieldRect Field);

/// <summary>
/// One scope frame's tracks, each block laid out once on the UI thread. The renderer draws from it and the canvas
/// hit-tests against it, so the two can never disagree about where a field is.
/// </summary>
/// <param name="TemplateId">The datablock template the frame was laid out with.</param>
/// <param name="Tracks">Tracks in draw order: the last one is drawn on top.</param>
public sealed record ScopeFrame(string TemplateId, IReadOnlyList<ScopeTrack> Tracks)
{
    /// <summary>How near a symbol a click must land to hit it, as today's radar uses.</summary>
    public const float TargetHitRadiusPx = 28f;

    /// <summary>The template the EuroScope-mode preference picks.</summary>
    public static string TemplateFor(bool euroScopeMode) => euroScopeMode ? ScopeTemplates.EuroScopeId : ScopeTemplates.StarsId;

    /// <summary>
    /// Lays every track out for this frame, reading the blink phase from <see cref="RadarBlink"/> once so the whole frame
    /// shares one phase. Each block sits at the default leader offset.
    /// </summary>
    public static ScopeFrame Build(ScopeFrameInput input)
    {
        string templateId = TemplateFor(input.EuroScopeMode);
        bool flashOn = RadarBlink.IsOn;
        var byCallsign = new Dictionary<string, AircraftModel>(StringComparer.Ordinal);
        foreach (AircraftModel ac in input.Aircraft)
        {
            byCallsign.TryAdd(ac.Callsign, ac);
        }

        var tracks = new List<ScopeTrack>(input.Aircraft.Count);
        foreach (AircraftModel ac in input.Aircraft)
        {
            bool isSelected = ReferenceEquals(ac, input.Selected);
            var presenterInput = new TrackPresenterInput(
                ac,
                templateId,
                Peer(byCallsign, ac.AtpaLeadCallsign),
                Peer(byCallsign, ac.ConflictPeerCallsign),
                flashOn,
                isSelected,
                input.Alerts
            );
            ScopeBlockLayout atOrigin = DatablockTemplate.Layout(TrackPresenter.Present(presenterInput), input.Metrics);
            bool hasManual = input.ManualOffsets.TryGetValue(ac.Callsign, out SKPoint manual);
            SKPoint offset = RadarDatablockLayout.ResolveBlockOffset(ac, false, hasManual, manual, atOrigin.Bounds, null);
            (float sx, float sy) = input.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
            tracks.Add(new ScopeTrack(ac, new SKPoint(sx, sy), atOrigin.At(sx + offset.X, sy + offset.Y), isSelected));
        }

        return new ScopeFrame(templateId, tracks);
    }

    /// <summary>
    /// The topmost field under (<paramref name="x"/>, <paramref name="y"/>), or null. A flashing field keeps its reserved rect
    /// in both blink phases; the EuroScope template hit-tests it only while its text shows, as today's tag does.
    /// </summary>
    public ScopeFieldHit? FieldAt(float x, float y)
    {
        bool onlyShownText = TemplateId == ScopeTemplates.EuroScopeId;
        for (int i = Tracks.Count - 1; i >= 0; i--)
        {
            ScopeTrack track = Tracks[i];
            foreach (FieldRect field in track.Block.Fields)
            {
                if ((onlyShownText && (field.Text.Length == 0)) || !field.Rect.Contains(x, y))
                {
                    continue;
                }
                return new ScopeFieldHit(track.Aircraft, field);
            }
        }
        return null;
    }

    /// <summary>The aircraft whose block (topmost first) contains (<paramref name="x"/>, <paramref name="y"/>), or null.</summary>
    public AircraftModel? BlockAt(float x, float y)
    {
        for (int i = Tracks.Count - 1; i >= 0; i--)
        {
            if (Tracks[i].Block.Bounds.Contains(x, y))
            {
                return Tracks[i].Aircraft;
            }
        }
        return null;
    }

    /// <summary>
    /// The aircraft whose symbol is nearest (<paramref name="x"/>, <paramref name="y"/>) within <see cref="TargetHitRadiusPx"/>,
    /// or null.
    /// </summary>
    public AircraftModel? TargetAt(float x, float y)
    {
        AircraftModel? nearest = null;
        float best = TargetHitRadiusPx;
        foreach (ScopeTrack track in Tracks)
        {
            float dist = SKPoint.Distance(track.Symbol, new SKPoint(x, y));
            if (dist < best)
            {
                best = dist;
                nearest = track.Aircraft;
            }
        }
        return nearest;
    }

    private static AircraftModel? Peer(Dictionary<string, AircraftModel> byCallsign, string? callsign) =>
        string.IsNullOrEmpty(callsign) ? null : byCallsign.GetValueOrDefault(callsign);
}

/// <summary>What the render thread draws one scope frame from. Built on the UI thread; immutable.</summary>
/// <param name="Frame">The laid-out tracks.</param>
/// <param name="VideoMaps">Video maps to draw.</param>
/// <param name="BrightnessLookup">Brightness category ("A" or "B") by map id; a copy the render thread owns.</param>
/// <param name="MapBrightnessA">Brightness of A maps, 0 to 1.</param>
/// <param name="MapBrightnessB">Brightness of B maps, 0 to 1.</param>
/// <param name="FontSize">Datablock font size the frame was measured with.</param>
public sealed record ScopeRenderSnapshot(
    ScopeFrame Frame,
    IReadOnlyList<VideoMapData> VideoMaps,
    IReadOnlyDictionary<string, string> BrightnessLookup,
    float MapBrightnessA,
    float MapBrightnessB,
    float FontSize
);

/// <summary>
/// Draws a <see cref="ScopeRenderSnapshot"/> back to front: background, video maps, then each track's symbol, leader and
/// block. Every block field is drawn from its <see cref="FieldRect"/>; the presenter has already blanked a flashing
/// field's off-phase text, so nothing here flashes.
/// </summary>
public sealed class ScopeRenderer : IDisposable
{
    private const float SymbolRadius = 5f;

    private static readonly SKColor SymbolColor = new(0, 176, 255);
    private static readonly SKColor BlockColor = SKColors.White;
    private static readonly SKColor SelectedColor = SKColors.White;
    private static readonly SKColor NoteColor = new(255, 200, 60);
    private static readonly SKColor NoLandingClearanceColor = SKColors.Red;
    private static readonly SKColor ConflictAlertColor = new(255, 60, 60);
    private static readonly SKColor AtpaWarningColor = SKColors.Yellow;
    private static readonly SKColor AtpaAlertColor = new(255, 55, 0);

    private readonly VideoMapRenderer _videoMapRenderer = new();

    private readonly SKPaint _symbolPaint = new()
    {
        StrokeWidth = 1.5f,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _leaderPaint = new()
    {
        Color = BlockColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _selectedBorderPaint = new()
    {
        Color = SelectedColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _textPaint = new() { IsAntialias = true };

    // Stroke paints drawn in the radar's line style (no AA, device-pixel width) each frame; each carries its base width in DIPs.
    private readonly (SKPaint Paint, float BaseWidth)[] _strokePaints;

    private SKFont? _font;

    public ScopeRenderer()
    {
        _strokePaints = [(_symbolPaint, 1.5f), (_leaderPaint, 2f), (_selectedBorderPaint, 1f)];
    }

    /// <summary>Draws <paramref name="snapshot"/> into <paramref name="canvas"/>.</summary>
    public void Render(SKCanvas canvas, MapViewport vp, ScopeRenderSnapshot snapshot)
    {
        RadarLineStyle.Apply(_strokePaints, RadarLineStyle.GetScale(canvas));
        canvas.Clear(SKColors.Black);
        _videoMapRenderer.BrightnessA = snapshot.MapBrightnessA;
        _videoMapRenderer.BrightnessB = snapshot.MapBrightnessB;
        _videoMapRenderer.Render(canvas, vp, snapshot.VideoMaps, snapshot.BrightnessLookup);

        SKFont font = FontFor(snapshot.FontSize);
        foreach (ScopeTrack track in snapshot.Frame.Tracks)
        {
            DrawTrack(canvas, track, font);
        }
    }

    public void Dispose()
    {
        _videoMapRenderer.Dispose();
        _symbolPaint.Dispose();
        _leaderPaint.Dispose();
        _selectedBorderPaint.Dispose();
        _textPaint.Dispose();
        _font?.Dispose();
    }

    private SKFont FontFor(float size)
    {
        if ((_font is null) || (Math.Abs(_font.Size - size) > 0.01f))
        {
            _font?.Dispose();
            _font = PlatformHelper.MonospaceFontBold(size);
        }
        return _font;
    }

    private void DrawTrack(SKCanvas canvas, ScopeTrack track, SKFont font)
    {
        _symbolPaint.Color = track.IsSelected ? SelectedColor : SymbolColor;
        canvas.DrawCircle(track.Symbol.X, track.Symbol.Y, SymbolRadius, _symbolPaint);

        SKRect bounds = track.Block.Bounds;
        if (track.IsSelected)
        {
            canvas.DrawRect(bounds, _selectedBorderPaint);
        }

        SKPoint leaderEnd = ClampToBlockEdge(track.Symbol, bounds);
        canvas.DrawLine(track.Symbol, leaderEnd, _leaderPaint);

        foreach (FieldRect field in track.Block.Fields)
        {
            if (field.Text.Length == 0)
            {
                continue;
            }
            _textPaint.Color = ColorFor(field.Token);
            // A field rect spans the font's ascent budget above its baseline, so the baseline is the rect's bottom.
            canvas.DrawText(field.Text, field.Rect.Left, field.Rect.Bottom, SKTextAlign.Left, font, _textPaint);
        }
    }

    private static SKColor ColorFor(ScopeColorToken token) =>
        token switch
        {
            ScopeColorToken.Note => NoteColor,
            ScopeColorToken.NoLandingClearance => NoLandingClearanceColor,
            ScopeColorToken.ConflictAlert => ConflictAlertColor,
            ScopeColorToken.AtpaWarning => AtpaWarningColor,
            ScopeColorToken.AtpaAlert => AtpaAlertColor,
            ScopeColorToken.Selected => SelectedColor,
            _ => BlockColor,
        };

    /// <summary>The point on <paramref name="rect"/>'s edge nearest <paramref name="point"/>, or the point itself when inside.</summary>
    private static SKPoint ClampToBlockEdge(SKPoint point, SKRect rect) =>
        rect.Contains(point.X, point.Y) ? point : new SKPoint(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom));
}
