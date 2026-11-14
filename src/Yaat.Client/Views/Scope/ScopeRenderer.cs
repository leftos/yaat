using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Views.Scope;

/// <summary>Everything one scope frame reads to lay its tracks out.</summary>
/// <param name="Aircraft">The tracks to draw, already filtered for visibility.</param>
/// <param name="Selected">The selected aircraft, or null.</param>
/// <param name="Viewport">The viewport the frame is drawn in.</param>
/// <param name="EuroScopeMode">Whether the EuroScope template is in use (else the STARS template).</param>
/// <param name="Metrics">Datablock text measurements.</param>
/// <param name="Alerts">Alert overlays the user's preferences allow.</param>
/// <param name="Display">Instructor display hints the user's preferences allow.</param>
/// <param name="ManualOffsets">Per-callsign block offsets from the symbol; a callsign absent here takes the default offset.</param>
/// <param name="HighlightedCallsigns">Callsigns whose blocks are highlighted (middle-click).</param>
public sealed record ScopeFrameInput(
    IReadOnlyList<AircraftModel> Aircraft,
    AircraftModel? Selected,
    MapViewport Viewport,
    bool EuroScopeMode,
    ScopeTextMetrics Metrics,
    ScopeAlertOptions Alerts,
    ScopeDisplayOptions Display,
    IReadOnlyDictionary<string, SKPoint> ManualOffsets,
    IReadOnlySet<string> HighlightedCallsigns
);

/// <summary>One track as the frame draws it: its symbol's screen point and its block laid out at its placed position.</summary>
/// <param name="Aircraft">The track.</param>
/// <param name="Symbol">Screen point of the position symbol.</param>
/// <param name="Block">The block's one layout, placed in screen coordinates; drawing and hit-testing both read it.</param>
/// <param name="IsSelected">Whether the track is selected.</param>
/// <param name="BlockColor">The block's colour role, as the presenter decided it.</param>
/// <param name="IsStale">Whether the track is a coasting live-traffic shadow, drawn at half alpha.</param>
/// <param name="Tint">The RPO tint that applies to the track, as the presenter decided it; it colours the symbol.</param>
public sealed record ScopeTrack(
    AircraftModel Aircraft,
    SKPoint Symbol,
    ScopeBlockLayout Block,
    bool IsSelected,
    ScopeBlockColor BlockColor,
    bool IsStale,
    ScopeTintRole Tint
);

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

    /// <summary>How near the MVA floor an altitude reads as at it, as today's radar uses.</summary>
    private const int MvaAtBandFt = 100;

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
                input.Alerts,
                input.Display,
                MvaRelationFor(ac, input.Display.ShowMvaAltitudeTint),
                input.HighlightedCallsigns.Contains(ac.Callsign)
            );
            TrackPresentation presentation = TrackPresenter.Present(presenterInput);
            ScopeBlockLayout atOrigin = DatablockTemplate.Layout(presentation, input.Metrics);
            bool hasManual = input.ManualOffsets.TryGetValue(ac.Callsign, out SKPoint manual);
            SKPoint offset = RadarDatablockLayout.ResolveBlockOffset(ac, false, hasManual, manual, atOrigin.Bounds, null);
            (float sx, float sy) = input.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
            ScopeBlockLayout placed = atOrigin.At(sx + offset.X, sy + offset.Y);
            tracks.Add(new ScopeTrack(ac, new SKPoint(sx, sy), placed, isSelected, presentation.BlockColor, ac.LiveTrafficStale, presentation.Tint));
        }

        return new ScopeFrame(templateId, tracks);
    }

    /// <summary>
    /// The altitude's relation to the MVA floor at <paramref name="ac"/>'s position, or null when the tint does not apply:
    /// it is off, the aircraft is on the ground, established on an approach, or VFR (MSAW-inhibited), as today's radar rules.
    /// </summary>
    internal static MvaRelation? MvaRelationFor(AircraftModel ac, bool showTint)
    {
        if (!showTint || ac.IsOnGround || ac.IsEstablishedOnApproach || ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return MvaDatabase.Default.Classify(ac.Position, ac.Altitude, MvaAtBandFt).Relation;
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
/// <param name="Palette">The user's block colours, read from the preferences on the UI thread.</param>
public sealed record ScopeRenderSnapshot(
    ScopeFrame Frame,
    IReadOnlyList<VideoMapData> VideoMaps,
    IReadOnlyDictionary<string, string> BrightnessLookup,
    float MapBrightnessA,
    float MapBrightnessB,
    float FontSize,
    ScopePalette Palette
);

/// <summary>The user-chosen colours a frame draws with; the render thread reads no preferences.</summary>
/// <param name="AssignedToMe">The assignment tint, or null when it is off or its colour does not parse.</param>
/// <param name="AssignedToOther">The unassigned tint, or null when it is off or its colour does not parse.</param>
/// <param name="SelectedSymbol">The selected track's symbol colour.</param>
public sealed record ScopePalette(SKColor? AssignedToMe, SKColor? AssignedToOther, SKColor SelectedSymbol);

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
    private static readonly SKColor SelectedBorderColor = SKColors.White;
    private static readonly SKColor HighlightColor = SKColors.Cyan;

    // The student's STARS block colours, as today's radar mirrors them from CRC (DisplayElementTracks).
    private static readonly SKColor StarsOwnedColor = SKColors.White;
    private static readonly SKColor StarsUnownedColor = new(50, 205, 50);
    private static readonly SKColor StarsPointoutColor = SKColors.Yellow;
    private static readonly SKColor StarsHighlightColor = SKColors.Cyan;
    private static readonly SKColor NoteColor = new(255, 200, 60);
    private static readonly SKColor NoLandingClearanceColor = SKColors.Red;
    private static readonly SKColor ConflictAlertColor = new(255, 60, 60);
    private static readonly SKColor AtpaWarningColor = SKColors.Yellow;
    private static readonly SKColor AtpaAlertColor = new(255, 55, 0);
    private static readonly SKColor TypeMismatchColor = new(0xFF, 0xB0, 0x30);
    private static readonly SKColor MvaBelowColor = new(255, 90, 90);
    private static readonly SKColor MvaAtColor = new(255, 200, 60);

    // The squawk pulse's off-phase brightness, as today's radar (CRC ApplyColorBrightness(color, 25)).
    private const float DimTailBrightness = 0.25f;

    // A coasting live-traffic shadow's alpha, as today's radar.
    private const byte StaleAlpha = 128;

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
        Color = SelectedBorderColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _textPaint = new() { IsAntialias = true };

    // Antialiased like today's radar strike paint, and outside the radar line style: its width comes from the font each draw.
    private readonly SKPaint _strikethroughPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };

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
            DrawTrack(canvas, track, font, snapshot.Palette);
        }
    }

    public void Dispose()
    {
        _videoMapRenderer.Dispose();
        _symbolPaint.Dispose();
        _leaderPaint.Dispose();
        _selectedBorderPaint.Dispose();
        _textPaint.Dispose();
        _strikethroughPaint.Dispose();
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

    private void DrawTrack(SKCanvas canvas, ScopeTrack track, SKFont font, ScopePalette palette)
    {
        _symbolPaint.Color = SymbolColorOf(track, palette);
        canvas.DrawCircle(track.Symbol.X, track.Symbol.Y, SymbolRadius, _symbolPaint);

        SKRect bounds = track.Block.Bounds;
        if (track.IsSelected)
        {
            canvas.DrawRect(bounds, _selectedBorderPaint);
        }

        SKColor blockColor = BlockColorOf(track, palette);
        SKPoint leaderEnd = ClampToBlockEdge(track.Symbol, bounds);
        _leaderPaint.Color = blockColor;
        canvas.DrawLine(track.Symbol, leaderEnd, _leaderPaint);

        foreach (FieldRect field in track.Block.Fields)
        {
            if (field.Text.Length > 0)
            {
                DrawField(canvas, field, font, blockColor);
            }
        }
    }

    private void DrawField(SKCanvas canvas, FieldRect field, SKFont font, SKColor blockColor)
    {
        SKColor color = ColorFor(field.Token, blockColor);
        // A field rect spans the font's ascent budget above its baseline, so the baseline is the rect's bottom.
        float baseline = field.Rect.Bottom;
        if (field.Style.HasFlag(ScopeFieldStyle.DimTail))
        {
            DrawDimTail(canvas, field.Text, field.Rect.Left, baseline, font, color);
        }
        else
        {
            _textPaint.Color = color;
            canvas.DrawText(field.Text, field.Rect.Left, baseline, SKTextAlign.Left, font, _textPaint);
        }

        if (field.Style.HasFlag(ScopeFieldStyle.Strikethrough))
        {
            DrawStrikethrough(canvas, field, font, color);
        }
    }

    /// <summary>
    /// Draws the text before the first space at <paramref name="color"/>, then the rest (its leading space included) at a
    /// quarter of its brightness, placed as today's <c>TargetRenderer.DrawSquawkMismatchLine</c> places its head and tail.
    /// </summary>
    private void DrawDimTail(SKCanvas canvas, string text, float x, float baseline, SKFont font, SKColor color)
    {
        int space = text.IndexOf(' ');
        string head = space < 0 ? text : text[..space];
        _textPaint.Color = color;
        canvas.DrawText(head, x, baseline, SKTextAlign.Left, font, _textPaint);
        if (space < 0)
        {
            return;
        }

        float headWidth = font.MeasureText(head);
        _textPaint.Color = Dim(color, DimTailBrightness);
        canvas.DrawText(text[space..], x + headWidth, baseline, SKTextAlign.Left, font, _textPaint);
    }

    /// <summary>A line across <paramref name="field"/> at the font's strikeout position, as today's radar strikes ModeC.</summary>
    private void DrawStrikethrough(SKCanvas canvas, FieldRect field, SKFont font, SKColor color)
    {
        SKFontMetrics metrics = font.Metrics;
        // StrikeoutPosition is negative (above the baseline); a font that reports none falls back to a third of its size.
        float strikeOffset = metrics.StrikeoutPosition.GetValueOrDefault();
        if (strikeOffset == 0)
        {
            strikeOffset = -font.Size / 3f;
        }
        float strikeY = field.Rect.Bottom + strikeOffset;
        _strikethroughPaint.Color = color;
        _strikethroughPaint.StrokeWidth = MathF.Max(1f, metrics.StrikeoutThickness.GetValueOrDefault());
        canvas.DrawLine(field.Rect.Left, strikeY, field.Rect.Right, strikeY, _strikethroughPaint);
    }

    /// <summary>Scales <paramref name="color"/>'s RGB toward black by <paramref name="factor"/>, keeping its alpha.</summary>
    private static SKColor Dim(SKColor color, float factor) =>
        new((byte)(color.Red * factor), (byte)(color.Green * factor), (byte)(color.Blue * factor), color.Alpha);

    /// <summary>
    /// The colour of <paramref name="track"/>'s leader and of every <see cref="ScopeColorToken.Default"/> field, at the stale
    /// alpha when it coasts.
    /// </summary>
    internal static SKColor BlockColorOf(ScopeTrack track, ScopePalette palette) => Stale(track, ResolveBlockColor(track.BlockColor, palette));

    /// <summary>
    /// The colour of <paramref name="track"/>'s symbol: the selected colour when selected, else its tint, else the symbol blue,
    /// at the stale alpha when it coasts. The highlight and student colours leave the symbol alone, as on today's radar.
    /// </summary>
    internal static SKColor SymbolColorOf(ScopeTrack track, ScopePalette palette)
    {
        SKColor? tint = track.Tint switch
        {
            ScopeTintRole.AssignedToMe => palette.AssignedToMe,
            ScopeTintRole.AssignedToOther => palette.AssignedToOther,
            _ => null,
        };
        return Stale(track, track.IsSelected ? palette.SelectedSymbol : (tint ?? SymbolColor));
    }

    /// <summary>The colour <paramref name="token"/> draws in; <see cref="ScopeColorToken.Default"/> takes <paramref name="block"/>.</summary>
    internal static SKColor ColorFor(ScopeColorToken token, SKColor block) =>
        token switch
        {
            ScopeColorToken.TypeMismatch => TypeMismatchColor,
            ScopeColorToken.MvaBelow => MvaBelowColor,
            ScopeColorToken.MvaAt => MvaAtColor,
            ScopeColorToken.Note => NoteColor,
            ScopeColorToken.NoLandingClearance => NoLandingClearanceColor,
            ScopeColorToken.ConflictAlert => ConflictAlertColor,
            ScopeColorToken.AtpaWarning => AtpaWarningColor,
            ScopeColorToken.AtpaAlert => AtpaAlertColor,
            _ => block,
        };

    private static SKColor ResolveBlockColor(ScopeBlockColor role, ScopePalette palette) =>
        role switch
        {
            ScopeBlockColor.StudentOwned => StarsOwnedColor,
            ScopeBlockColor.StudentUnowned => StarsUnownedColor,
            ScopeBlockColor.StudentPointout => StarsPointoutColor,
            ScopeBlockColor.StudentHighlighted => StarsHighlightColor,
            ScopeBlockColor.Highlighted => HighlightColor,
            ScopeBlockColor.AssignedToMe => palette.AssignedToMe ?? BlockColor,
            ScopeBlockColor.AssignedToOther => palette.AssignedToOther ?? BlockColor,
            _ => BlockColor,
        };

    private static SKColor Stale(ScopeTrack track, SKColor color) => track.IsStale ? color.WithAlpha(StaleAlpha) : color;

    /// <summary>The point on <paramref name="rect"/>'s edge nearest <paramref name="point"/>, or the point itself when inside.</summary>
    private static SKPoint ClampToBlockEdge(SKPoint point, SKRect rect) =>
        rect.Contains(point.X, point.Y) ? point : new SKPoint(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom));
}
