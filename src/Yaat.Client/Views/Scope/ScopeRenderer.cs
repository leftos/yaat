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
/// <param name="TemplateId">Id of the datablock template in use (see <see cref="ScopeTemplates"/>).</param>
/// <param name="Metrics">Datablock text measurements.</param>
/// <param name="Alerts">Alert overlays the user's preferences allow.</param>
/// <param name="Display">Instructor display hints the user's preferences allow.</param>
/// <param name="ManualOffsets">Per-callsign block offsets from the symbol; a callsign absent here takes the default offset.</param>
/// <param name="HighlightedCallsigns">Callsigns whose blocks are highlighted (middle-click).</param>
/// <param name="HoveredCallsign">The callsign of the track the pointer dwells on (<see cref="ScopeFrame.DwellAt"/>), or null.</param>
/// <param name="DefaultLevel">The block level of every track that is not dwelled.</param>
/// <param name="VciHoveredCallsign">
/// The callsign of the track whose VCI cell the pointer is on (<see cref="ScopeFrame.HoverAt"/>), or null; its VCI lights
/// without dwelling the block.
/// </param>
/// <param name="LocalDwellLockedCallsigns">
/// Callsigns the instructor dwell-locked on this scope (DataBlockViewState.DwellLockedCallsigns); null is none.
/// </param>
public sealed record ScopeFrameInput(
    IReadOnlyList<AircraftModel> Aircraft,
    AircraftModel? Selected,
    MapViewport Viewport,
    string TemplateId,
    ScopeTextMetrics Metrics,
    ScopeAlertOptions Alerts,
    ScopeDisplayOptions Display,
    IReadOnlyDictionary<string, SKPoint> ManualOffsets,
    IReadOnlySet<string> HighlightedCallsigns,
    string? HoveredCallsign = null,
    ScopeBlockLevel DefaultLevel = ScopeBlockLevel.Full,
    string? VciHoveredCallsign = null,
    IReadOnlySet<string>? LocalDwellLockedCallsigns = null
);

/// <summary>One track as the frame draws it: its symbol's screen point and its block laid out at its placed position.</summary>
/// <param name="Aircraft">The track.</param>
/// <param name="Symbol">Screen point of the position symbol.</param>
/// <param name="Block">The block's one layout, placed in screen coordinates; drawing and hit-testing both read it.</param>
/// <param name="IsSelected">Whether the track is selected.</param>
/// <param name="BlockColor">The block's colour role, as the presenter decided it.</param>
/// <param name="IsStale">Whether the track is a coasting live-traffic shadow, drawn at half alpha.</param>
/// <param name="Tint">The RPO tint that applies to the track, as the presenter decided it; it colours the symbol.</param>
/// <param name="Level">The block level the track was presented at.</param>
/// <param name="ShowsLeader">Whether the block draws its leader, as the presenter decided it.</param>
public sealed record ScopeTrack(
    AircraftModel Aircraft,
    SKPoint Symbol,
    ScopeBlockLayout Block,
    bool IsSelected,
    ScopeBlockColor BlockColor,
    bool IsStale,
    ScopeTintRole Tint,
    ScopeBlockLevel Level = ScopeBlockLevel.Full,
    bool ShowsLeader = true
)
{
    /// <summary>How far the dwell box sits outside the text bounds (CRC <c>FdbRenderObject</c>: the fields rect inflated 2 px).</summary>
    public const float DwellBoxInflatePx = 2f;

    /// <summary>Whether the block is dwelled (hovered): drawn on top, without the stale alpha, with its dwell box.</summary>
    public bool IsDwelled => Level == ScopeBlockLevel.Dwell;

    /// <summary>The text bounds inflated <see cref="DwellBoxInflatePx"/>; the hover hit-test and the emphasis both read it.</summary>
    public SKRect DwellBox
    {
        get
        {
            SKRect box = Block.TextBounds;
            box.Inflate(DwellBoxInflatePx, DwellBoxInflatePx);
            return box;
        }
    }
}

/// <summary>What the pointer hovers: the track whose block it dwells, or the track whose VCI it previews; at most one is set.</summary>
/// <param name="Dwelled">The aircraft whose block the pointer dwells on, or null.</param>
/// <param name="VciPreviewed">The aircraft whose VCI cell the pointer is on, or null.</param>
public readonly record struct ScopeHoverHit(AircraftModel? Dwelled, AircraftModel? VciPreviewed);

/// <summary>A field a point landed on, and whose block it is.</summary>
public sealed record ScopeFieldHit(AircraftModel Aircraft, FieldRect Field);

/// <summary>
/// One scope frame's tracks, each block laid out once on the UI thread. The renderer draws from it and the canvas
/// hit-tests against it, so the two can never disagree about where a field is.
/// </summary>
/// <param name="TemplateId">The scope's picked template id (a template id, or <see cref="ScopeTemplates.MixedId"/>, resolved per track).</param>
/// <param name="Tracks">Tracks in draw order: the last one is drawn on top.</param>
public sealed record ScopeFrame(string TemplateId, IReadOnlyList<ScopeTrack> Tracks)
{
    /// <summary>How near a symbol a click must land to hit it, as today's radar uses.</summary>
    public const float TargetHitRadiusPx = 28f;

    /// <summary>How near a symbol the pointer must be to dwell its track (CRC <c>ViewDataBlocks.GetDataBlockByPosition</c>).</summary>
    public const float DwellSymbolRadiusPx = 8f;

    /// <summary>How near the MVA floor an altitude reads as at it, as today's radar uses.</summary>
    private const int MvaAtBandFt = 100;

    /// <summary>
    /// Lays every track out for this frame, reading the blink phase from <see cref="RadarBlink"/> once so the whole frame
    /// shares one phase. Each block sits at the default leader offset; dwelled tracks come last, so they draw on top and
    /// hit-test first: the dwell-locked ones, then the hovered one.
    /// </summary>
    public static ScopeFrame Build(ScopeFrameInput input)
    {
        bool flashOn = RadarBlink.IsOn;
        var byCallsign = new Dictionary<string, AircraftModel>(StringComparer.Ordinal);
        foreach (AircraftModel ac in input.Aircraft)
        {
            byCallsign.TryAdd(ac.Callsign, ac);
        }

        var tracks = new List<ScopeTrack>(input.Aircraft.Count);
        var locked = new List<ScopeTrack>();
        var hovered = new List<ScopeTrack>();
        foreach (AircraftModel ac in input.Aircraft)
        {
            ScopeTrack track = BuildTrack(input, byCallsign, ac, flashOn);
            if (!track.IsDwelled)
            {
                tracks.Add(track);
            }
            else
            {
                (IsHovered(ac, input) ? hovered : locked).Add(track);
            }
        }

        tracks.AddRange(locked);
        tracks.AddRange(hovered);
        return new ScopeFrame(input.TemplateId, tracks);
    }

    /// <summary>
    /// The one place a track's block level is chosen: <see cref="ScopeBlockLevel.Dwell"/> while the pointer dwells on it, or
    /// while it is drawn with the ERAM template and dwell-locked, by the instructor on this scope
    /// (<see cref="ScopeFrameInput.LocalDwellLockedCallsigns"/>) or by the student's CRC for the viewer's ERAM sector; else
    /// the frame's default.
    /// </summary>
    private static ScopeBlockLevel LevelFor(AircraftModel ac, string templateId, ScopeFrameInput input)
    {
        bool locked =
            (templateId == ScopeTemplates.EramId)
            && ((input.LocalDwellLockedCallsigns?.Contains(ac.Callsign) ?? false) || EramDwellLock.IsDwellLocked(ac, input.Display.Viewer));
        return (IsHovered(ac, input) || locked) ? ScopeBlockLevel.Dwell : input.DefaultLevel;
    }

    private static bool IsHovered(AircraftModel ac, ScopeFrameInput input) =>
        string.Equals(ac.Callsign, input.HoveredCallsign, StringComparison.Ordinal);

    private static ScopeTrack BuildTrack(ScopeFrameInput input, Dictionary<string, AircraftModel> byCallsign, AircraftModel ac, bool flashOn)
    {
        bool isSelected = ReferenceEquals(ac, input.Selected);
        string templateId = ScopeOwnerKind.Resolve(input.TemplateId, ac);
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
            input.HighlightedCallsigns.Contains(ac.Callsign),
            LevelFor(ac, templateId, input),
            VciPreview: string.Equals(ac.Callsign, input.VciHoveredCallsign, StringComparison.Ordinal)
        );
        TrackPresentation presentation = TrackPresenter.Present(presenterInput);
        ScopeBlockLayout atOrigin = DatablockTemplate.Layout(presentation, input.Metrics);
        bool hasManual = input.ManualOffsets.TryGetValue(ac.Callsign, out SKPoint manual);
        SKPoint offset = RadarDatablockLayout.ResolveBlockOffset(ac, false, hasManual, manual, atOrigin.Bounds, null);
        (float sx, float sy) = input.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        ScopeBlockLayout placed = atOrigin.At(sx + offset.X, sy + offset.Y);
        if (!EramDwellLock.HasEramSector(input.Display.Viewer))
        {
            // With no viewer ERAM sector the VCI can never light, so its cell takes no hover.
            placed = placed with
            {
                VciCell = null,
            };
        }
        return new ScopeTrack(
            ac,
            new SKPoint(sx, sy),
            placed,
            isSelected,
            presentation.BlockColor,
            ac.LiveTrafficStale,
            presentation.Tint,
            presentation.Level,
            presentation.ShowsLeader
        );
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

    /// <summary>The aircraft the pointer at (<paramref name="x"/>, <paramref name="y"/>) dwells on, or null (<see cref="HoverAt"/>).</summary>
    public AircraftModel? DwellAt(float x, float y) => HoverAt(x, y).Dwelled;

    /// <summary>
    /// What the pointer at (<paramref name="x"/>, <paramref name="y"/>) hovers, by CRC's pick precedence, each pass topmost block
    /// first (<c>ViewDataBlocks.HandleMouseMove</c>): within <see cref="DwellSymbolRadiusPx"/> of a symbol dwells its block;
    /// else inside a block's VCI cell, inflated as CRC's <c>OnFrequencyIndicatorBounds</c>, previews its VCI; else inside a
    /// block's <see cref="ScopeTrack.DwellBox"/> dwells it. Separate from the click rule (<see cref="BlockAt"/>,
    /// <see cref="TargetAt"/>).
    /// </summary>
    public ScopeHoverHit HoverAt(float x, float y)
    {
        var point = new SKPoint(x, y);
        if (Topmost(t => SKPoint.Distance(t.Symbol, point) <= DwellSymbolRadiusPx) is { } symbol)
        {
            return new ScopeHoverHit(symbol, null);
        }
        if (Topmost(t => (t.Block.VciCell is { } cell) && VciHoverBox(cell).Contains(x, y)) is { } vci)
        {
            return new ScopeHoverHit(null, vci);
        }
        return new ScopeHoverHit(Topmost(t => t.DwellBox.Contains(x, y)), null);
    }

    /// <summary>The VCI cell's hover box: CRC's <c>OnFrequencyIndicatorBounds</c>, 5 px out left and down, 3 px up and right.</summary>
    private static SKRect VciHoverBox(SKRect cell) => new(cell.Left - 5f, cell.Top - 3f, cell.Right + 3f, cell.Bottom + 5f);

    private AircraftModel? Topmost(Func<ScopeTrack, bool> hit)
    {
        for (int i = Tracks.Count - 1; i >= 0; i--)
        {
            if (hit(Tracks[i]))
            {
                return Tracks[i].Aircraft;
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
/// <param name="Brightness">The scope's BRITE groups: the maps by category, the symbols, the blocks, line 4, the alerts and DWELL.</param>
/// <param name="FontSize">Datablock font size the frame was measured with.</param>
/// <param name="Palette">The user's block colours, read from the preferences on the UI thread.</param>
public sealed record ScopeRenderSnapshot(
    ScopeFrame Frame,
    IReadOnlyList<VideoMapData> VideoMaps,
    IReadOnlyDictionary<string, string> BrightnessLookup,
    ScopeBrightness Brightness,
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

    /// <summary>The VCI's three arc radii as fractions of the largest.</summary>
    private static readonly float[] VciArcScales = [1f / 3f, 2f / 3f, 1f];

    /// <summary>The ≠ bars' heights above the baseline as fractions of the cap height, the upper first.</summary>
    private static readonly float[] NotEqualBarHeights = [0.62f, 0.30f];
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

    // CRC's dwell emphasis box: a 1 px outline in the block's own colour (FdbRenderObject, StyleManager.FdbEmphasisBox).
    private readonly SKPaint _dwellBoxPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _textPaint = new() { IsAntialias = true };

    // Antialiased like today's radar strike paint, and outside the radar line style: its width comes from the font each draw.
    private readonly SKPaint _strikethroughPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };

    // The ERAM climb/descend arrow, drawn as a path in its monospace cell; its width comes from the font each draw.
    private readonly SKPaint _arrowPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };

    // Stroke paints drawn in the radar's line style (no AA, device-pixel width) each frame; each carries its base width in DIPs.
    private readonly (SKPaint Paint, float BaseWidth)[] _strokePaints;

    private SKFont? _font;

    public ScopeRenderer()
    {
        _strokePaints = [(_symbolPaint, 1.5f), (_leaderPaint, 2f), (_selectedBorderPaint, 1f), (_dwellBoxPaint, 1f)];
    }

    /// <summary>Draws <paramref name="snapshot"/> into <paramref name="canvas"/>.</summary>
    public void Render(SKCanvas canvas, MapViewport vp, ScopeRenderSnapshot snapshot)
    {
        RadarLineStyle.Apply(_strokePaints, RadarLineStyle.GetScale(canvas));
        canvas.Clear(SKColors.Black);
        _videoMapRenderer.BrightnessA = snapshot.Brightness.MapA / 100f;
        _videoMapRenderer.BrightnessB = snapshot.Brightness.MapB / 100f;
        _videoMapRenderer.Render(canvas, vp, snapshot.VideoMaps, snapshot.BrightnessLookup);

        SKFont font = FontFor(snapshot.FontSize);
        foreach (ScopeTrack track in snapshot.Frame.Tracks)
        {
            DrawTrack(canvas, track, font, snapshot.Palette, snapshot.Brightness);
        }
    }

    public void Dispose()
    {
        _videoMapRenderer.Dispose();
        _symbolPaint.Dispose();
        _leaderPaint.Dispose();
        _selectedBorderPaint.Dispose();
        _dwellBoxPaint.Dispose();
        _textPaint.Dispose();
        _strikethroughPaint.Dispose();
        _arrowPaint.Dispose();
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

    /// <summary>
    /// Draws <paramref name="track"/>: its symbol at TARGETS, its leader, dwell box and fields at their groups (see
    /// <see cref="FieldPercent"/>), each lifted by DWELL when the track is dwelled; the selection border never dims.
    /// </summary>
    private void DrawTrack(SKCanvas canvas, ScopeTrack track, SKFont font, ScopePalette palette, ScopeBrightness brightness)
    {
        _symbolPaint.Color = ScopeBrightness.Apply(SymbolColorOf(track, palette), Lifted(track, brightness.Targets, brightness.Dwell));
        canvas.DrawCircle(track.Symbol.X, track.Symbol.Y, SymbolRadius, _symbolPaint);

        if (track.IsSelected)
        {
            canvas.DrawRect(SelectionBorder(track), _selectedBorderPaint);
        }

        SKColor rawBlockColor = BlockColorOf(track, palette);
        SKColor blockColor = ScopeBrightness.Apply(rawBlockColor, Lifted(track, brightness.Fdb, brightness.Dwell));
        if (track.IsDwelled)
        {
            _dwellBoxPaint.Color = blockColor;
            canvas.DrawRect(track.DwellBox, _dwellBoxPaint);
        }

        if (track.ShowsLeader)
        {
            _leaderPaint.Color = blockColor;
            canvas.DrawLine(track.Symbol, LeaderEnd(track), _leaderPaint);
        }

        foreach (FieldRect field in track.Block.Fields)
        {
            if (field.Text.Length > 0)
            {
                int percent = Lifted(track, FieldPercent(field, brightness), brightness.Dwell);
                DrawField(canvas, field, font, ScopeBrightness.Apply(ColorFor(field.Token, rawBlockColor), percent));
            }
        }
    }

    /// <summary>
    /// <paramref name="percent"/>, lifted by <paramref name="dwell"/> when <paramref name="track"/> is dwelled.
    /// </summary>
    private static int Lifted(ScopeTrack track, int percent, int dwell) => track.IsDwelled ? ScopeBrightness.Lift(percent, dwell) : percent;

    /// <summary>
    /// The group percent <paramref name="field"/> draws at before the dwell lift: the ERAM line 4 at FDB plus LINE 4, clamped
    /// to 0 to 100; an alert token at ALERTS; any other field at FDB.
    /// </summary>
    internal static int FieldPercent(FieldRect field, ScopeBrightness brightness)
    {
        if (field.Slot == ScopeFieldSlot.Line4Notes)
        {
            return Math.Clamp(brightness.Fdb + brightness.Line4, 0, 100);
        }

        return IsAlertToken(field.Token) ? brightness.Alerts : brightness.Fdb;
    }

    /// <summary>Whether <paramref name="token"/> is one the ALERTS group dims: conflict, no landing clearance, ATPA.</summary>
    private static bool IsAlertToken(ScopeColorToken token) =>
        token is ScopeColorToken.ConflictAlert or ScopeColorToken.NoLandingClearance or ScopeColorToken.AtpaWarning or ScopeColorToken.AtpaAlert;

    /// <summary>
    /// Draws <paramref name="field"/> in <paramref name="color"/>, its token's colour at its brightness; the squawk-mismatch
    /// pulse's off phase dims its tail, dwelled or not.
    /// </summary>
    private void DrawField(SKCanvas canvas, FieldRect field, SKFont font, SKColor color)
    {
        if (field.Style.HasFlag(ScopeFieldStyle.DimTail))
        {
            DrawDimTail(canvas, field, font, color, Dim(color, DimTailBrightness));
        }
        else
        {
            _textPaint.Color = color;
            // A field rect spans the font's ascent budget above its baseline, so the baseline is the rect's bottom.
            canvas.DrawText(field.Text, field.Rect.Left, field.Rect.Bottom, SKTextAlign.Left, font, _textPaint);
        }

        if (field.Style.HasFlag(ScopeFieldStyle.Strikethrough))
        {
            DrawStrikethrough(canvas, field, font, color);
        }

        if ((field.Style & (ScopeFieldStyle.ArrowUp | ScopeFieldStyle.ArrowDown)) != 0)
        {
            bool up = field.Style.HasFlag(ScopeFieldStyle.ArrowUp);
            DrawCellPath(canvas, field, font, color, cell => ArrowPath(field, font, cell, up));
        }

        if (field.Style.HasFlag(ScopeFieldStyle.HsfIndicator))
        {
            DrawCellPath(canvas, field, font, color, cell => HsfPath(field, font, cell));
        }

        if (field.Style.HasFlag(ScopeFieldStyle.Vci))
        {
            DrawCellPath(canvas, field, font, color, cell => VciPath(field, font, cell));
        }

        if (field.Style.HasFlag(ScopeFieldStyle.AltitudeMismatch))
        {
            DrawCellPath(canvas, field, font, color, cell => NotEqualPath(field, font, cell));
        }
    }

    /// <summary>
    /// Strokes the glyph <paramref name="buildPath"/> builds in the monospace cell <see cref="TrackPresenter.EramArrowCell"/>
    /// holds in <paramref name="field"/>'s text (the arrow, the HSF indicator, the VCI); nothing when the text holds no cell.
    /// </summary>
    private void DrawCellPath(SKCanvas canvas, FieldRect field, SKFont font, SKColor color, Func<int, SKPath> buildPath)
    {
        int cell = field.Text.IndexOf(TrackPresenter.EramArrowCell, StringComparison.Ordinal);
        if (cell < 0)
        {
            return;
        }

        using SKPath path = buildPath(cell);
        _arrowPaint.Color = color;
        _arrowPaint.StrokeWidth = MathF.Max(1f, font.Size / 10f);
        canvas.DrawPath(path, _arrowPaint);
    }

    /// <summary>
    /// Where cell <paramref name="cell"/> of <paramref name="field"/> sits: its left edge, its width and the font's cap height
    /// (seven tenths of the size when the font reports none).
    /// </summary>
    private static (float Left, float Width, float CapHeight) CellOf(FieldRect field, SKFont font, int cell) =>
        (
            field.Rect.Left + font.MeasureText(field.Text[..cell]),
            font.MeasureText(TrackPresenter.EramArrowCell),
            font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : font.Size * 0.7f
        );

    /// <summary>
    /// The VCI path in cell <paramref name="cell"/> of <paramref name="field"/>: three concentric quarter arcs about the cell's
    /// bottom-left corner, opening up and right, the largest as wide as the cell or as tall as the cap height.
    /// </summary>
    internal static SKPath VciPath(FieldRect field, SKFont font, int cell)
    {
        (float left, float width, float capHeight) = CellOf(field, font, cell);
        float radius = MathF.Min(width, capHeight) * 0.9f;
        float cx = left + (width * 0.05f);
        float cy = field.Rect.Bottom;

        var path = new SKPath();
        foreach (float scale in VciArcScales)
        {
            float r = radius * scale;
            path.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), -90f, 90f);
        }
        return path;
    }

    /// <summary>
    /// The ↴ path in cell <paramref name="cell"/> of <paramref name="field"/>: a stroke rightwards a quarter of the cap height
    /// below the cap line, then down to the baseline, with a downward head at its foot.
    /// </summary>
    internal static SKPath HsfPath(FieldRect field, SKFont font, int cell)
    {
        (float left, float width, float capHeight) = CellOf(field, font, cell);
        float bottom = field.Rect.Bottom;
        float y = bottom - (capHeight * 0.75f);
        float x = left + (width * 0.65f);
        float head = width * 0.3f;

        var path = new SKPath();
        path.MoveTo(left + (width * 0.1f), y);
        path.LineTo(x, y);
        path.LineTo(x, bottom);
        path.MoveTo(x - head, bottom - head);
        path.LineTo(x, bottom);
        path.LineTo(x + head, bottom - head);
        return path;
    }

    /// <summary>
    /// The ≠ path in cell <paramref name="cell"/> of <paramref name="field"/>: two bars across 0.12 to 0.88 of the cell at 0.62
    /// and 0.30 of the cap height above the baseline, the upper first, then a diagonal from 0.72 of the cell at 0.95 of the cap
    /// height down to 0.28 of the cell a twentieth of the cap height below the baseline.
    /// </summary>
    internal static SKPath NotEqualPath(FieldRect field, SKFont font, int cell)
    {
        (float left, float width, float capHeight) = CellOf(field, font, cell);
        float baseline = field.Rect.Bottom;
        float barLeft = left + (width * 0.12f);
        float barRight = left + (width * 0.88f);

        var path = new SKPath();
        foreach (float height in NotEqualBarHeights)
        {
            float y = baseline - (capHeight * height);
            path.MoveTo(barLeft, y);
            path.LineTo(barRight, y);
        }
        path.MoveTo(left + (width * 0.72f), baseline - (capHeight * 0.95f));
        path.LineTo(left + (width * 0.28f), baseline + (capHeight * 0.05f));
        return path;
    }

    /// <summary>
    /// The ERAM climb or descend arrow's path in cell <paramref name="cell"/> of <paramref name="field"/>: a shaft from the
    /// baseline to the cap height with a head at the pointed end.
    /// </summary>
    internal static SKPath ArrowPath(FieldRect field, SKFont font, int cell, bool up)
    {
        (float left, float width, float capHeight) = CellOf(field, font, cell);
        float bottom = field.Rect.Bottom;
        float top = bottom - capHeight;
        float x = left + (width / 2f);
        float head = width * 0.35f;
        float tip = up ? top : bottom;
        float barb = up ? top + head : bottom - head;

        var path = new SKPath();
        path.MoveTo(x, bottom);
        path.LineTo(x, top);
        path.MoveTo(x - head, barb);
        path.LineTo(x, tip);
        path.LineTo(x + head, barb);
        return path;
    }

    /// <summary>
    /// Draws the text before the first space at <paramref name="color"/>, then the rest (its leading space included) at
    /// <paramref name="tailColor"/>, placed as today's <c>TargetRenderer.DrawSquawkMismatchLine</c> places its head and tail.
    /// </summary>
    private void DrawDimTail(SKCanvas canvas, FieldRect field, SKFont font, SKColor color, SKColor tailColor)
    {
        string text = field.Text;
        float x = field.Rect.Left;
        float baseline = field.Rect.Bottom;
        int space = text.IndexOf(' ');
        string head = space < 0 ? text : text[..space];
        _textPaint.Color = color;
        canvas.DrawText(head, x, baseline, SKTextAlign.Left, font, _textPaint);
        if (space < 0)
        {
            return;
        }

        float headWidth = font.MeasureText(head);
        _textPaint.Color = tailColor;
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
    /// The colour of <paramref name="track"/>'s leader, dwell box and every <see cref="ScopeColorToken.Default"/> field, at the
    /// stale alpha when it coasts, unless it is dwelled: a dwelled block skips the stale alpha.
    /// </summary>
    internal static SKColor BlockColorOf(ScopeTrack track, ScopePalette palette)
    {
        SKColor color = ResolveBlockColor(track.BlockColor, palette);
        return track.IsDwelled ? color : Stale(track, color);
    }

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

    /// <summary>
    /// Where <paramref name="track"/>'s leader meets its block: the nearest point of the text bounds, so column 0 never moves it
    /// (CRC attaches the leader to the fields box).
    /// </summary>
    internal static SKPoint LeaderEnd(ScopeTrack track) => ClampToBlockEdge(track.Symbol, track.Block.TextBounds);

    /// <summary>The rect a selected track's border is drawn on: its text block, so column 0 never moves it.</summary>
    internal static SKRect SelectionBorder(ScopeTrack track) => track.Block.TextBounds;

    /// <summary>The point on <paramref name="rect"/>'s edge nearest <paramref name="point"/>, or the point itself when inside.</summary>
    private static SKPoint ClampToBlockEdge(SKPoint point, SKRect rect) =>
        rect.Contains(point.X, point.Y) ? point : new SKPoint(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom));
}
