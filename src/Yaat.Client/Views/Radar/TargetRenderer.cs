using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Renders aircraft targets with STARS-style position symbols,
/// leader lines, and data blocks.
/// </summary>
public sealed class TargetRenderer : IDisposable
{
    private static readonly SKColor SymbolColor = new(0, 176, 255);
    private static readonly SKColor DataBlockColor = SKColors.White;
    private static readonly SKColor SelectedColor = new(255, 255, 255);
    private static readonly SKColor HistoryColor = new(0, 176, 255);
    private static readonly SKColor GroundColor = new(0, 200, 0);

    // STARS datablock palette mirrored from CRC (DisplayElementTracks): owned=white, unowned=LimeGreen,
    // pointout=yellow, highlighted=cyan. Applied to the datablock (and its leader) when the student
    // STARS color sync is enabled, reflecting how the student's scope colors the track.
    private static readonly SKColor StarsOwnedColor = SKColors.White;
    private static readonly SKColor StarsUnownedColor = new(50, 205, 50);
    private static readonly SKColor StarsPointoutColor = SKColors.Yellow;
    private static readonly SKColor StarsHighlightColor = SKColors.Cyan;

    // Instructor-note line color — amber/gold, distinct from white datablock text, red
    // NoLndgClnc, green ground/SAY, and cyan highlight.
    private static readonly SKColor NoteColor = new(255, 200, 60);

    // MVA datablock altitude tint: red when below the charted sector floor, amber within the "at" band.
    private static readonly SKColor MvaBelowColor = new(255, 90, 90);
    private static readonly SKColor MvaAtColor = new(255, 200, 60);
    private const int MvaAtBandFt = 100;

    // Filed-vs-actual aircraft type hint: a deeper amber than the MVA "at" tint, which sits on the
    // altitude token at the other end of the same line.
    private static readonly SKColor TypeMismatchColor = new(0xFF, 0xB0, 0x30);

    // STARS TPA graphics color (CRC's TCW value, DisplayElementTracks: Color.FromArgb(90, 180, 255)) —
    // used for the instructor J-Ring / Cone overlay so it reads like the TPA graphics a controller sees.
    private static readonly SKColor TpaColor = new(90, 180, 255);

    private const int TpaJRing = 1;
    private const int TpaCone = 2;

    // Automatic ATPA cone / in-trail line, escalating off the TPA blue: yellow once the pair is inside
    // the warning band, red-orange once it is in alert — CRC's STARS ATPA cone palette.
    private static readonly SKColor AtpaWarningColor = SKColors.Yellow;
    private static readonly SKColor AtpaAlertColor = new(255, 55, 0);

    // Conflict alert: red, matching the NoLndgClnc warning line, and deliberately distinct from the
    // blue TPA overlay so an automatic alert never reads as an instructor-placed graphic.
    private static readonly SKColor ConflictAlertColor = new(255, 60, 60);

    /// <summary>
    /// Conflict ring radius in nautical miles — the terminal CA detector's lateral trigger
    /// (<c>ConflictAlertDetector.HorizontalNm</c>), not a separation minimum. Keep the two in step.
    /// </summary>
    private const double ConflictRingRadiusNm = 3.0;

    // Bubble pill colors — subtle teal-tinted background with a SAY-green border so the
    // bubble reads as a pilot-transmission overlay without competing with the datablock.
    private static readonly SKColor SpeechBubbleFillColor = new(20, 60, 50, 220);
    private static readonly SKColor SpeechBubbleBorderColor = new(80, 220, 140);
    private static readonly SKColor SpeechBubbleTextColor = SKColors.White;

    // Amber variant for opt-in WARN-channel bubbles, matching the terminal Warning colour so a
    // warning reads distinctly from a green pilot/SAY transmission.
    private static readonly SKColor WarningBubbleFillColor = new(70, 50, 10, 220);
    private static readonly SKColor WarningBubbleBorderColor = new(230, 170, 40);

    private readonly SKPaint _symbolPaint = new()
    {
        StrokeWidth = 1.5f,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    // Live-traffic shadows: the same outline circle drawn dashed, so a target the sim does not own reads as
    // "not mine" at a glance without changing its color or datablock.
    private readonly SKPaint _shadowSymbolPaint = new()
    {
        StrokeWidth = 1.5f,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash([2.5f, 2.5f], 0),
    };

    // Drawn thicker than the 1px PTL so the line to the data block stays
    // distinguishable from the predicted-track vector when both emanate from the symbol.
    private readonly SKPaint _leaderPaint = new()
    {
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _dataBlockPaint = new() { IsAntialias = true };

    private readonly SKFont _dataBlockFont = Services.PlatformHelper.MonospaceFontBold(12);

    /// <summary>Font + paint pair for datablock text. Shared with the hit-test path via the layout helpers.</summary>
    private TextStyle DataBlockStyle => new(_dataBlockFont, _dataBlockPaint);

    private readonly SKPaint _historyPaint = new()
    {
        Color = HistoryColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _selectedBorderPaint = new()
    {
        Color = SelectedColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _ptlPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _strikethroughPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _tpaPaint = new()
    {
        Color = TpaColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _conflictRingPaint = new()
    {
        Color = ConflictAlertColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _tpaTextPaint = new() { Color = TpaColor, IsAntialias = true };

    private readonly SKFont _tpaTextFont = Services.PlatformHelper.MonospaceFontBold(11);

    private readonly SKPaint _tpaLabelBackingPaint = new()
    {
        Color = SKColors.Black,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

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

    private readonly SKPaint _bubbleTextPaint = new() { Color = SpeechBubbleTextColor, IsAntialias = true };

    private readonly SKFont _bubbleTextFont = Services.PlatformHelper.MonospaceFontBold(12);

    /// <summary>
    /// Half-angle (degrees) of the instructor TPA Cone overlay. CRC draws the manual TPA cone as a
    /// razor-thin ±2° needle along the ground track; this is exposed so it can be widened for
    /// legibility via the <c>TpaConeHalfAngleDegrees</c> user preference. Default 2° (CRC-exact).
    /// </summary>
    public double TpaConeHalfAngleDegrees { get; set; } = 2.0;

    /// <summary>Local user's initials for assignment tint matching.</summary>
    public string? LocalUserInitials { get; set; }

    /// <summary>When non-null, aircraft assigned to LocalUserInitials use this color.</summary>
    public SKColor? AssignmentTintColor { get; set; }

    /// <summary>When non-null, aircraft NOT assigned to LocalUserInitials use this color.</summary>
    public SKColor? UnassignedTintColor { get; set; }

    /// <summary>When non-null, overrides the hardcoded white for selected aircraft.</summary>
    public SKColor? SelectedOverrideColor { get; set; }

    /// <summary>Brightness factor (0-1) for history trail dots. Driven by BRITE HST control.</summary>
    public float HistoryBrightness { get; set; } = 1.0f;

    /// <summary>When true, render aircraft tags using the EuroScope-style layout instead of STARS.</summary>
    public bool EuroScopeMode { get; set; }

    /// <summary>
    /// When true, render a flashing red "NoLndgClnc" line on the datablock for aircraft whose
    /// <see cref="AircraftModel.NoLandingClearanceWarningActive"/> flag is set. Default true;
    /// driven by the <c>FlashNoLandingClearance</c> user preference.
    /// </summary>
    public bool FlashNoLandingClearance { get; set; } = true;

    /// <summary>
    /// When true, render a flashing red "CA"/"MCI" datablock field and a 3 nm ring around both members
    /// of each active conflict pair (see <see cref="AircraftModel.ConflictPeerCallsign"/>). Default
    /// false (opt-in); driven by the <c>ShowConflictAlerts</c> user preference.
    /// </summary>
    public bool ShowConflictAlerts { get; set; }

    /// <summary>
    /// When true, tint the datablock's type token amber for an aircraft whose physical type differs from
    /// its filed flight-plan type (see <see cref="AircraftModel.HasFiledTypeMismatch"/>). Instructor-only
    /// hint — the datablock itself shows the filed type, as the student's STARS does. Default false here;
    /// driven by the <c>ShowTypeMismatchHints</c> user preference, which defaults on.
    /// </summary>
    public bool ShowTypeMismatchHints { get; set; }

    /// <summary>
    /// When true, render the automatic ATPA cone and the in-trail distance datablock line for each
    /// aircraft trailing another in an ATPA pairing (see <see cref="AircraftModel.AtpaLeadCallsign"/>).
    /// Default false (opt-in); driven by the <c>ShowAtpa</c> user preference. Distinct from the
    /// instructor's manual <c>JRING</c>/<c>CONE</c> overlay.
    /// </summary>
    public bool ShowAtpa { get; set; }

    /// <summary>
    /// Callsign → model index for the aircraft in the current render pass, used to resolve a
    /// conflicting aircraft's peer for the separation readout and a trailing aircraft's ATPA lead for
    /// the in-trail readout. Populated only while <see cref="ShowConflictAlerts"/> or
    /// <see cref="ShowAtpa"/> is on.
    /// </summary>
    private readonly Dictionary<string, AircraftModel> _callsignIndex = [];

    /// <summary>
    /// Resolves a paired aircraft by callsign from the current render pass, or null when the callsign
    /// is unset or the aircraft isn't on the scope.
    /// </summary>
    private AircraftModel? ResolvePeer(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return null;
        }

        return _callsignIndex.GetValueOrDefault(callsign);
    }

    /// <summary>
    /// Resolves the other member of <paramref name="ac"/>'s conflict pair, or null when conflict alerts
    /// are off or the peer isn't on the scope.
    /// </summary>
    private AircraftModel? ResolveConflictPeer(AircraftModel ac) => ShowConflictAlerts ? ResolvePeer(ac.ConflictPeerCallsign) : null;

    /// <summary>
    /// Resolves the aircraft ahead of <paramref name="ac"/> in its ATPA pairing, or null when the ATPA
    /// overlay is off, this aircraft isn't the trailing member, or the lead isn't on the scope.
    /// </summary>
    private AircraftModel? ResolveAtpaLead(AircraftModel ac) => ShowAtpa ? ResolvePeer(ac.AtpaLeadCallsign) : null;

    /// <summary>Datablock text size in pixels. Updated from UserPreferences via RadarView.SyncAssignmentTint.</summary>
    public float DatablockTextSize
    {
        get => _dataBlockFont.Size;
        set
        {
            _dataBlockFont.Size = value;
            _bubbleTextFont.Size = value;
        }
    }

    /// <summary>
    /// When true, aircraft with an active <see cref="AircraftModel.SpeechBubble"/> render in a
    /// deferred top-layer pass so their datablock and bubble pill aren't obscured by neighboring
    /// aircraft. Driven by the <c>ShowSpeechBubbles</c> user preference. Default false (opt-in).
    /// </summary>
    public bool ShowSpeechBubbles { get; set; }

    /// <summary>
    /// Tints the datablock altitude field by the aircraft's relationship to the charted MVA floor
    /// (red below, amber within ±100 ft). Driven by the bound <c>RadarViewModel.ShowMvaHints</c> (seeded
    /// per scenario position type, toggled by the DCB MVA button). Defaults off to match the RadarCanvas
    /// StyledProperty default — a true default would tint before the first change event fires.
    /// </summary>
    public bool ShowMvaAltitudeTint { get; set; }

    /// <summary>
    /// When true, color each datablock to match the student's STARS scope (white = owned by student,
    /// green = owned by another, yellow = pointout, cyan = highlighted) from
    /// <see cref="AircraftModel.StudentDatablockColor"/>. Default true (opt-out); driven by the
    /// <c>SyncStudentDatablockColors</c> user preference.
    /// </summary>
    public bool SyncStudentColors { get; set; } = true;

    /// <summary>
    /// When true, append "(LDB)"/"(PDB)" to the callsign line when the student sees a limited /
    /// partial datablock, while the instructor keeps the full block. Default true (opt-out); driven
    /// by <c>MarkStudentLimitedDatablocks</c>. Suppressed when <see cref="CollapseStudentDatablocks"/>
    /// renders the reduced block instead.
    /// </summary>
    public bool MarkStudentLimitedDatablocks { get; set; } = true;

    /// <summary>
    /// When true, render the reduced datablock the student actually sees (limited / partial) instead
    /// of the instructor's full block. Default false (opt-in); driven by <c>CollapseStudentDatablocks</c>.
    /// </summary>
    public bool CollapseStudentDatablocks { get; set; }

    /// <summary>
    /// When true, orient each datablock's leader line in the direction the student set in STARS,
    /// unless the block has a manual drag offset. Default false (opt-in); driven by
    /// <c>SyncStudentLeaderDirection</c>.
    /// </summary>
    public bool SyncStudentLeaderDirection { get; set; }

    /// <summary>
    /// Per-aircraft layout result captured during the last Render(). Populated only when
    /// <see cref="EuroScopeMode"/> is on. Consumed by RadarCanvas hit-testing.
    /// </summary>
    public IReadOnlyDictionary<string, EuroScopeTagResult> LastEuroScopeTags => _lastEuroScopeTags;

    private readonly Dictionary<string, EuroScopeTagResult> _lastEuroScopeTags = [];

    /// <summary>
    /// Per-aircraft bubble rect captured during the last Render(). Populated only when
    /// <see cref="ShowSpeechBubbles"/> is on and the aircraft has an active bubble. Consumed
    /// by RadarCanvas hit-testing for the click-to-dismiss behavior.
    /// </summary>
    public IReadOnlyDictionary<string, SKRect> LastBubbleRects => _lastBubbleRects;

    private readonly Dictionary<string, SKRect> _lastBubbleRects = [];

    private const float SymbolSize = 5f;

    // Scope stroke paints rendered CRC-style (no AA, device-pixel width) by RadarLineStyle.Apply each
    // frame; the tuple carries each paint's base width in DIPs.
    private readonly (SKPaint Paint, float BaseWidth)[] _strokePaints;

    public TargetRenderer()
    {
        _strokePaints =
        [
            (_symbolPaint, 1.5f),
            (_leaderPaint, 2f),
            (_selectedBorderPaint, 1f),
            (_ptlPaint, 1f),
            (_tpaPaint, 1f),
            (_conflictRingPaint, 1f),
        ];
    }

    public void Render(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        IReadOnlyDictionary<string, SKPoint>? deconflictOffsets,
        double ptlLengthMinutes = 0,
        bool ptlOwn = false,
        bool ptlAll = false,
        IReadOnlySet<string>? minifiedCallsigns = null,
        IReadOnlySet<string>? highlightedCallsigns = null,
        bool showTopDown = false,
        int historyCount = 0
    )
    {
        RadarLineStyle.Apply(_strokePaints, RadarLineStyle.GetScale(canvas));

        // Draw history trails first (behind position symbols)
        if (historyCount > 0)
        {
            DrawHistoryTrails(canvas, vp, aircraft, historyCount);
        }

        _lastEuroScopeTags.Clear();
        _lastBubbleRects.Clear();

        // Index this pass's aircraft by callsign so a conflicting aircraft can resolve its peer — and a
        // trailing aircraft its ATPA lead — for the live separation readouts. Rebuilt per pass, since
        // the list is a fresh snapshot each frame.
        _callsignIndex.Clear();
        if (ShowConflictAlerts || ShowAtpa)
        {
            foreach (AircraftModel ac in aircraft)
            {
                _callsignIndex[ac.Callsign] = ac;
            }
        }

        // Two-pass render: aircraft with an active speech bubble are deferred to pass 2 so
        // their symbol, datablock, and bubble all paint on top of neighboring aircraft.
        // Without this, an overlapping datablock from a non-bubbled aircraft would obscure
        // the bubble we're trying to surface. List is allocated only when speech bubbles
        // are enabled; otherwise the per-aircraft check is a single property read.
        List<AircraftModel>? deferred = null;
        DateTime now = ShowSpeechBubbles ? DateTime.UtcNow : default;

        foreach (AircraftModel ac in aircraft)
        {
            if (ShowSpeechBubbles && IsBubbleActive(ac.SpeechBubble, now))
            {
                deferred ??= [];
                deferred.Add(ac);
                continue;
            }
            DrawOneAircraft(
                canvas,
                vp,
                ac,
                selectedAircraft,
                dataBlockOffsets,
                deconflictOffsets,
                minifiedCallsigns,
                highlightedCallsigns,
                showTopDown,
                ptlLengthMinutes,
                ptlOwn,
                ptlAll,
                drawBubble: false
            );
        }

        if (deferred is not null)
        {
            foreach (AircraftModel ac in deferred)
            {
                DrawOneAircraft(
                    canvas,
                    vp,
                    ac,
                    selectedAircraft,
                    dataBlockOffsets,
                    deconflictOffsets,
                    minifiedCallsigns,
                    highlightedCallsigns,
                    showTopDown,
                    ptlLengthMinutes,
                    ptlOwn,
                    ptlAll,
                    drawBubble: true
                );
            }
        }
    }

    private static bool IsBubbleActive(AircraftSpeechBubble? bubble, DateTime now) => bubble is not null && bubble.ExpiresAt > now;

    private void DrawOneAircraft(
        SKCanvas canvas,
        MapViewport vp,
        AircraftModel ac,
        AircraftModel? selectedAircraft,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        IReadOnlyDictionary<string, SKPoint>? deconflictOffsets,
        IReadOnlySet<string>? minifiedCallsigns,
        IReadOnlySet<string>? highlightedCallsigns,
        bool showTopDown,
        double ptlLengthMinutes,
        bool ptlOwn,
        bool ptlAll,
        bool drawBubble
    )
    {
        (float sx, float sy) = vp.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

        bool isSelected = ac == selectedAircraft;
        bool isOnGround = showTopDown && (int)(ac.Altitude / 100) < 1;
        SKColor? tintColor = null;
        if (LocalUserInitials is not null)
        {
            bool isAssignedToMe = string.Equals(ac.AssignedTo, LocalUserInitials, StringComparison.OrdinalIgnoreCase);
            if (isAssignedToMe && AssignmentTintColor is { } assignedTint)
            {
                tintColor = assignedTint;
            }
            else if (!isAssignedToMe && UnassignedTintColor is { } unassignedTint)
            {
                tintColor = unassignedTint;
            }
        }

        bool isHighlighted = highlightedCallsigns is not null && highlightedCallsigns.Contains(ac.Callsign);
        // Student STARS color (when sync is on) sits below an explicit RPO tint but above the ground /
        // white defaults, so the datablock reflects how the student's scope colors the track.
        SKColor? studentColor = SyncStudentColors ? StudentDatablockColorFor(ac) : null;
        (SKColor symbolColor, SKColor dbColor) = ResolveTargetColors(
            new TargetColorInputs(
                isSelected,
                isHighlighted,
                isOnGround,
                tintColor,
                studentColor,
                SelectedOverrideColor ?? SelectedColor,
                ac.LiveTrafficStale
            )
        );
        bool isMinified = minifiedCallsigns is not null && minifiedCallsigns.Contains(ac.Callsign);

        if (ptlLengthMinutes > 0 && ShouldShowPtl(ac, ptlOwn, ptlAll))
        {
            DrawPtlLine(canvas, vp, sx, sy, ac, ptlLengthMinutes);
        }

        AircraftModel? atpaLead = ResolveAtpaLead(ac);

        if (ac.TpaType != 0 && ac.TpaSize > 0)
        {
            DrawTpaGraphic(canvas, vp, sx, sy, ac, suppressManualCone: atpaLead is not null);
        }

        if (atpaLead is not null)
        {
            DrawAtpaCone(canvas, vp, sx, sy, ac, atpaLead);
        }

        if (ShowConflictAlerts && !string.IsNullOrEmpty(ac.ConflictPeerCallsign))
        {
            DrawConflictRing(canvas, vp, ac);
        }

        DrawPositionSymbol(canvas, sx, sy, symbolColor, ac.IsLiveTraffic);
        SKRect blockRect = DrawLeaderAndDataBlock(canvas, sx, sy, ac, dbColor, dataBlockOffsets, deconflictOffsets, isMinified, isSelected);

        if (drawBubble && ac.SpeechBubble is { } bubble)
        {
            SKRect? bubbleRect = DrawSpeechBubble(canvas, blockRect, bubble.Text, bubble.Severity);
            if (bubbleRect is { } r)
            {
                _lastBubbleRects[ac.Callsign] = r;
            }
        }
    }

    /// <summary>Inputs to <see cref="ResolveTargetColors"/> — the per-aircraft state that drives the
    /// position-symbol and datablock colors on a single render pass.</summary>
    internal readonly record struct TargetColorInputs(
        bool IsSelected,
        bool IsHighlighted,
        bool IsOnGround,
        SKColor? TintColor,
        SKColor? StudentColor,
        SKColor SelectedColor,
        bool IsStale
    );

    /// <summary>Alpha applied to a coasting live-traffic shadow's symbol and datablock (feed sweeps missed, position dead-reckoned).</summary>
    internal const byte StaleAlpha = 128;

    /// <summary>
    /// Resolves the position-symbol and datablock (text + leader) colors for one target. Selection
    /// brightens the position symbol to <see cref="TargetColorInputs.SelectedColor"/> but leaves the
    /// datablock text and leader at their unselected color — the white rectangular border is the
    /// datablock's selection cue. An RPO tint outranks the student-scope color, which outranks the
    /// ground/white defaults; a middle-click highlight overrides the datablock color entirely. A stale
    /// (coasting) live-traffic shadow keeps whatever color it resolved to at <see cref="StaleAlpha"/>.
    /// </summary>
    internal static (SKColor Symbol, SKColor DataBlock) ResolveTargetColors(TargetColorInputs i)
    {
        SKColor baseSymbolColor = i.TintColor ?? (i.IsOnGround ? GroundColor : SymbolColor);
        SKColor baseDbColor = i.TintColor ?? i.StudentColor ?? (i.IsOnGround ? GroundColor : DataBlockColor);
        SKColor symbolColor = i.IsSelected ? i.SelectedColor : baseSymbolColor;
        SKColor dbColor = i.IsHighlighted ? SKColors.Cyan : baseDbColor;
        return i.IsStale ? (symbolColor.WithAlpha(StaleAlpha), dbColor.WithAlpha(StaleAlpha)) : (symbolColor, dbColor);
    }

    private static SKColor? StudentDatablockColorFor(AircraftModel ac) =>
        ac.StudentDatablockColor switch
        {
            StarsDatablockColor.Owned => StarsOwnedColor,
            StarsDatablockColor.Unowned => StarsUnownedColor,
            StarsDatablockColor.Pointout => StarsPointoutColor,
            StarsDatablockColor.Highlighted => StarsHighlightColor,
            _ => null,
        };

    private void DrawPtlLine(SKCanvas canvas, MapViewport vp, float sx, float sy, AircraftModel ac, double minutes)
    {
        if (ac.GroundSpeed < 1)
        {
            return;
        }

        double distNm = ac.GroundSpeed * minutes / 60.0;
        (double endLat, double endLon) = GeoMath.ProjectPoint(ac.Position, ac.Heading, distNm);
        (float ex, float ey) = vp.LatLonToScreen(endLat, endLon);

        _ptlPaint.Color = SKColors.White;
        canvas.DrawLine(sx, sy, ex, ey, _ptlPaint);
    }

    /// <summary>
    /// Draws the 3 nm conflict ring around a target in an active conflict pair. Both members carry
    /// <see cref="AircraftModel.ConflictPeerCallsign"/>, so both get a ring: a conflict is a property of
    /// the pair and 7110.65 §2-1-6 makes safety-alert recognition a per-aircraft duty regardless of who
    /// owns the track. Read it as "the peer's symbol inside your ring is the threshold violation" — the
    /// rings themselves overlap from 6 nm apart, so their overlap is not the alert condition.
    /// <para>
    /// The radius is the detector's lateral trigger, <b>not</b> an applicable separation minimum (those
    /// vary — 3 / 5 / 2.5 nm under §5-5-4 depending on sensor mode and position). It answers "why did
    /// this fire", so it must track the detector rather than the airspace.
    /// </para>
    /// Geo-anchored like the TPA ring — vertices are projected, not pixel-scaled — so the radius stays a
    /// true 3 nm at any zoom.
    /// </summary>
    private void DrawConflictRing(SKCanvas canvas, MapViewport vp, AircraftModel ac)
    {
        using var path = new SKPath();
        for (int deg = 0; deg <= 360; deg += 5)
        {
            (double lat, double lon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(deg), ConflictRingRadiusNm);
            (float px, float py) = vp.LatLonToScreen(lat, lon);
            if (deg == 0)
            {
                path.MoveTo(px, py);
            }
            else
            {
                path.LineTo(px, py);
            }
        }

        path.Close();
        canvas.DrawPath(path, _conflictRingPaint);
    }

    /// <summary>
    /// Draws the instructor TPA overlay (emulating STARS *J/*P) on YAAT's own radar. A J-Ring is a
    /// circle of radius <see cref="AircraftModel.TpaSize"/> nm centered on the target; a Cone is a thin
    /// wedge projecting from the target along its track for <see cref="AircraftModel.TpaSize"/> nm. Both
    /// are geo-anchored (ring/wedge vertices are projected, not pixel-scaled) so they stay accurate at
    /// any zoom, and carry a numeric size label like CRC's optional TPA size display.
    /// <para>
    /// <paramref name="suppressManualCone"/> is set while an automatic ATPA cone is drawn on the same
    /// track: CRC draws the manual cone only while no ATPA cone is present, but draws the J-Ring
    /// unconditionally, so a J-Ring survives the supersession and a manual cone does not.
    /// </para>
    /// </summary>
    private void DrawTpaGraphic(SKCanvas canvas, MapViewport vp, float sx, float sy, AircraftModel ac, bool suppressManualCone)
    {
        if (ac.TpaType == TpaJRing)
        {
            DrawTpaJRing(canvas, vp, ac);
        }
        else if ((ac.TpaType == TpaCone) && !suppressManualCone)
        {
            DrawTpaCone(canvas, vp, sx, sy, ac);
        }
    }

    /// <summary>
    /// The colour of an ATPA cone and its in-trail distance line for a pairing's advisory state:
    /// Monitor keeps the STARS TPA blue, Warning goes yellow, Alert goes red-orange (CRC's STARS ATPA
    /// palette). A Monitor datablock line uses the block's own colour instead — see
    /// <c>DrawLeaderAndDataBlock</c>.
    /// </summary>
    internal static SKColor AtpaConeColorFor(AtpaConeState state) =>
        state switch
        {
            AtpaConeState.Warning => AtpaWarningColor,
            AtpaConeState.Alert => AtpaAlertColor,
            _ => TpaColor,
        };

    /// <summary>
    /// Draws the automatic ATPA cone: a wedge from the trailing aircraft along the true bearing to the
    /// aircraft ahead, as long as the pairing's required in-trail separation and as wide as the manual
    /// TPA cone (±<see cref="TpaConeHalfAngleDegrees"/>), labelled with that required separation at its
    /// midpoint. Geo-anchored like the manual overlay, and coloured by the pairing's cone state so a
    /// warning/alert reads at a glance. Distinct from the instructor's manual <c>CONE</c> (issue #189).
    /// </summary>
    private void DrawAtpaCone(SKCanvas canvas, MapViewport vp, float sx, float sy, AircraftModel ac, AircraftModel lead)
    {
        double lengthNm = ac.AtpaAllowedSeparationNm;
        if (lengthNm <= 0)
        {
            return;
        }

        // GeoMath.ProjectPoint takes true headings; the viewport applies the display rotation.
        double axis = GeoMath.BearingTo(ac.Position, lead.Position);
        (double leftLat, double leftLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis + TpaConeHalfAngleDegrees), lengthNm);
        (double rightLat, double rightLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis - TpaConeHalfAngleDegrees), lengthNm);
        (float lx, float ly) = vp.LatLonToScreen(leftLat, leftLon);
        (float rx, float ry) = vp.LatLonToScreen(rightLat, rightLon);

        SKColor coneColor = AtpaConeColorFor(ac.AtpaConeState);
        using var path = new SKPath();
        path.MoveTo(sx, sy);
        path.LineTo(lx, ly);
        path.LineTo(rx, ry);
        path.Close();
        _tpaPaint.Color = coneColor;
        canvas.DrawPath(path, _tpaPaint);
        _tpaPaint.Color = TpaColor;

        (double midLat, double midLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis), lengthNm / 2.0);
        (float mx, float my) = vp.LatLonToScreen(midLat, midLon);
        _tpaTextPaint.Color = coneColor;
        DrawTpaSizeLabel(canvas, mx, my, lengthNm);
        _tpaTextPaint.Color = TpaColor;
    }

    private void DrawTpaJRing(SKCanvas canvas, MapViewport vp, AircraftModel ac)
    {
        using var path = new SKPath();
        for (int deg = 0; deg <= 360; deg += 5)
        {
            (double lat, double lon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(deg), ac.TpaSize);
            (float px, float py) = vp.LatLonToScreen(lat, lon);
            if (deg == 0)
            {
                path.MoveTo(px, py);
            }
            else
            {
                path.LineTo(px, py);
            }
        }

        path.Close();
        canvas.DrawPath(path, _tpaPaint);

        // Size label at the south edge of the ring (clear of the typical NE datablock).
        (double lblLat, double lblLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(180), ac.TpaSize);
        (float lx, float ly) = vp.LatLonToScreen(lblLat, lblLon);
        DrawTpaSizeLabel(canvas, lx, ly, ac.TpaSize);
    }

    private void DrawTpaCone(SKCanvas canvas, MapViewport vp, float sx, float sy, AircraftModel ac)
    {
        // CRC projects the cone along ground track; YAAT draws its leader line along Heading, so the
        // cone shares that axis to stay visually consistent with the rest of the YAAT target.
        double axis = ac.Heading.Degrees;
        (double leftLat, double leftLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis + TpaConeHalfAngleDegrees), ac.TpaSize);
        (double rightLat, double rightLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis - TpaConeHalfAngleDegrees), ac.TpaSize);
        (float lx, float ly) = vp.LatLonToScreen(leftLat, leftLon);
        (float rx, float ry) = vp.LatLonToScreen(rightLat, rightLon);

        using var path = new SKPath();
        path.MoveTo(sx, sy);
        path.LineTo(lx, ly);
        path.LineTo(rx, ry);
        path.Close();
        canvas.DrawPath(path, _tpaPaint);

        // Size label at the cone's midpoint along the track axis (mirrors CRC).
        (double midLat, double midLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis), ac.TpaSize / 2.0);
        (float mx, float my) = vp.LatLonToScreen(midLat, midLon);
        DrawTpaSizeLabel(canvas, mx, my, ac.TpaSize);
    }

    private void DrawTpaSizeLabel(SKCanvas canvas, float centerX, float centerY, double sizeNm)
    {
        string text = sizeNm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        float width = _tpaTextFont.MeasureText(text);
        float height = _tpaTextFont.Size;
        float baselineX = centerX - (width / 2f);
        float baselineY = centerY + (height / 2f);

        // Black backing rect keeps the value legible over targets / video maps (mirrors CRC's RenderQuad).
        canvas.DrawRect(baselineX - 2f, baselineY - height - 1f, width + 4f, height + 4f, _tpaLabelBackingPaint);
        canvas.DrawText(text, baselineX, baselineY, SKTextAlign.Left, _tpaTextFont, _tpaTextPaint);
    }

    private static bool ShouldShowPtl(AircraftModel ac, bool ptlOwn, bool ptlAll)
    {
        if (ptlAll)
        {
            return true;
        }

        return ptlOwn && !string.IsNullOrEmpty(ac.Owner);
    }

    private void DrawPositionSymbol(SKCanvas canvas, float cx, float cy, SKColor color, bool isLiveTraffic)
    {
        SKPaint paint = isLiveTraffic ? _shadowSymbolPaint : _symbolPaint;
        paint.Color = color;
        canvas.DrawCircle(cx, cy, SymbolSize, paint);
    }

    /// <summary>
    /// The MVA altitude-tint color for an aircraft, or null when the altitude field should render in the
    /// normal block color (tint disabled, on the ground, MSAW-inhibited, above the floor, or outside MVA
    /// coverage). VFR is MSAW-inhibited by default (7110.65 §5-14-7). Aircraft established on an approach
    /// are also inhibited: on final the procedure owns obstacle clearance (or, on a visual, the pilot has
    /// the runway), so the MVA no longer applies (server-computed
    /// <see cref="AircraftModel.IsEstablishedOnApproach"/>; AIM 4-1-16.a.1).
    /// </summary>
    private SKColor? ResolveMvaAltitudeTint(AircraftModel ac)
    {
        if (!ShowMvaAltitudeTint || ac.IsOnGround || ac.IsEstablishedOnApproach || ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        (MvaRelation relation, MvaSector? _) = MvaDatabase.Default.Classify(ac.Position, ac.Altitude, MvaAtBandFt);
        return relation switch
        {
            MvaRelation.Below => MvaBelowColor,
            MvaRelation.At => MvaAtColor,
            _ => null,
        };
    }

    /// <summary>
    /// Amber tint for the datablock type token when the aircraft is physically a different type than its
    /// flight plan files, or null when the hint is off or the types agree.
    /// </summary>
    private SKColor? ResolveTypeMismatchTint(AircraftModel ac) => (ShowTypeMismatchHints && ac.HasFiledTypeMismatch) ? TypeMismatchColor : null;

    /// <summary>
    /// Draws a datablock line, tinting the leading altitude token (everything before the first space)
    /// when <paramref name="altTint"/> is set, and the <paramref name="typeSpan"/> token in the middle of
    /// the line when <paramref name="typeTint"/> is set (the filed-vs-actual type hint). When
    /// <paramref name="identing"/>, the line's trailing IDENT token flashes on the 500 ms cycle: it is
    /// skipped entirely on the off-phase, which shifts nothing because it is the last token and its width
    /// is already reserved in the measured line.
    /// </summary>
    private void DrawDatablockLine(
        SKCanvas canvas,
        string line,
        float x,
        float baseline,
        SKColor? altTint,
        (int Start, int Length) typeSpan,
        SKColor? typeTint,
        bool identing
    )
    {
        if (line.Length == 0)
        {
            return;
        }

        SKColor blockColor = _dataBlockPaint.Color;
        int firstSpace = line.IndexOf(' ');
        // A tinted line with no space is all altitude token, so the tint covers the whole line.
        int headLength = altTint is null ? 0 : (firstSpace >= 0 ? firstSpace : line.Length);
        int tailStart = identing ? line.LastIndexOf(' ') + 1 : line.Length;
        if (tailStart <= headLength)
        {
            tailStart = line.Length;
        }

        // The type token sits between the altitude head and the IDENT tail, so tinting it splits the
        // body into three spans; without a tint the body stays one span and the drawing is unchanged.
        int typeStart = typeSpan.Start;
        int typeEnd = typeSpan.Start + typeSpan.Length;
        bool tintType = (typeTint is not null) && (typeSpan.Length > 0) && (typeStart >= headLength) && (typeEnd <= tailStart);

        float cursor = DrawSpan(canvas, line[..headLength], x, baseline, altTint ?? blockColor);
        if (tintType)
        {
            cursor = DrawSpan(canvas, line[headLength..typeStart], cursor, baseline, blockColor);
            cursor = DrawSpan(canvas, line[typeStart..typeEnd], cursor, baseline, typeTint!.Value);
            cursor = DrawSpan(canvas, line[typeEnd..tailStart], cursor, baseline, blockColor);
        }
        else
        {
            cursor = DrawSpan(canvas, line[headLength..tailStart], cursor, baseline, blockColor);
        }

        bool blinkOff = Environment.TickCount64 / 500 % 2 != 0;
        if (!blinkOff)
        {
            DrawSpan(canvas, line[tailStart..], cursor, baseline, blockColor);
        }
        _dataBlockPaint.Color = blockColor;
    }

    /// <summary>Draws one colored span of a datablock line and returns the x cursor past it.</summary>
    private float DrawSpan(SKCanvas canvas, string text, float x, float baseline, SKColor color)
    {
        if (text.Length == 0)
        {
            return x;
        }

        _dataBlockPaint.Color = color;
        canvas.DrawText(text, x, baseline, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
        return x + _dataBlockFont.MeasureText(text);
    }

    private void DrawSquawkMismatchLine(SKCanvas canvas, string line, float x, float baseline, SKColor color) =>
        DrawSquawkMismatchLine(canvas, line, x, baseline, _dataBlockFont, _dataBlockPaint, color);

    /// <summary>
    /// Draws the beacon-code mismatch line <c>"{reported} {assigned}"</c>: the reported code solid in the
    /// block color, then the assigned code dim-pulsing to its right on the 500 ms off-phase — emulating
    /// CRC STARS (reported solid, assigned blinking via <c>ApplyColorBrightness(color, 25)</c>). Shared
    /// with the ground datablock so both views give the identical indication; <paramref name="paint"/>
    /// is left in <paramref name="color"/> on return.
    /// </summary>
    internal static void DrawSquawkMismatchLine(SKCanvas canvas, string line, float x, float baseline, SKFont font, SKPaint paint, SKColor color)
    {
        int space = line.IndexOf(' ');
        string reported = space < 0 ? line : line[..space];
        paint.Color = color;
        canvas.DrawText(reported, x, baseline, SKTextAlign.Left, font, paint);
        if (space < 0)
        {
            return;
        }

        float reportedWidth = font.MeasureText(reported);
        bool blinkOff = Environment.TickCount64 / 500 % 2 != 0;
        paint.Color = blinkOff ? DimColor(color, 0.25f) : color;
        canvas.DrawText(line[space..], x + reportedWidth, baseline, SKTextAlign.Left, font, paint);
        paint.Color = color;
    }

    /// <summary>Scales a color's RGB toward black by <paramref name="factor"/> (alpha preserved), matching
    /// CRC's <c>RenderUtils.ApplyColorBrightness</c> used for the dim-pulse of a blinking datablock token.</summary>
    private static SKColor DimColor(SKColor color, float factor) =>
        new((byte)(color.Red * factor), (byte)(color.Green * factor), (byte)(color.Blue * factor), color.Alpha);

    private SKRect DrawLeaderAndDataBlock(
        SKCanvas canvas,
        float cx,
        float cy,
        AircraftModel ac,
        SKColor color,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        IReadOnlyDictionary<string, SKPoint>? deconflictOffsets,
        bool isMinified,
        bool isSelected
    )
    {
        _dataBlockPaint.Color = color;

        SKPoint manualOffset = default;
        bool hasManualOffset = dataBlockOffsets is not null && dataBlockOffsets.TryGetValue(ac.Callsign, out manualOffset);
        SKPoint? deconflictOffset =
            deconflictOffsets is not null && deconflictOffsets.TryGetValue(ac.Callsign, out SKPoint resolvedOffset) ? resolvedOffset : null;

        if (EuroScopeMode && !isMinified)
        {
            // EuroScope tags keep manual/default placement; student-scope sync targets STARS tags only.
            SKPoint esOffset = hasManualOffset ? manualOffset : RadarDatablockLayout.DefaultOffset;
            return DrawEuroScopeBlock(canvas, cx, cy, cx + esOffset.X, cy + esOffset.Y, ac, color, isSelected);
        }

        // Collapse to the reduced datablock the student actually sees (LDB/PDB) when enabled and the
        // student does not see a full block. The instructor's manual minify toggle takes precedence.
        bool collapse =
            !isMinified && CollapseStudentDatablocks && ac.StudentDatablockLevel is StarsDatablockLevel.Limited or StarsDatablockLevel.Partial;

        if (isMinified || collapse)
        {
            IReadOnlyList<string> lines = isMinified ? [RadarDatablockLayout.BuildMinifiedLine(ac)] : RadarDatablockLayout.BuildCollapsedLines(ac);
            return DrawReducedBlock(canvas, cx, cy, ac, color, lines, ac.IsIdenting, isSelected, hasManualOffset, manualOffset, deconflictOffset);
        }

        // Full STARS block, optionally annotated with the student's (LDB)/(PDB) marker.
        string marker = MarkStudentLimitedDatablocks ? RadarDatablockLayout.StudentLevelMarker(ac.StudentDatablockLevel) : "";
        var overlays = new DatablockOverlays(ShowConflictAlerts, ResolveConflictPeer(ac), ShowAtpa, ResolveAtpaLead(ac));
        SKRect rectAtOrigin = RadarDatablockLayout
            .Compute(ac, new DatablockPlacement(0, 0, DataBlockStyle, FlashNoLandingClearance), overlays, marker)
            .Rect;
        SKPoint offset = RadarDatablockLayout.ResolveBlockOffset(
            ac,
            SyncStudentLeaderDirection,
            hasManualOffset,
            manualOffset,
            rectAtOrigin,
            deconflictOffset
        );
        float blockX = cx + offset.X;
        float blockY = cy + offset.Y;

        var layout = RadarDatablockLayout.Compute(
            ac,
            new DatablockPlacement(blockX, blockY, DataBlockStyle, FlashNoLandingClearance),
            overlays,
            marker
        );

        if (isSelected)
        {
            canvas.DrawRect(layout.Rect, _selectedBorderPaint);
        }

        SKPoint leaderEndStars = ClampToBlockEdge(cx, cy, layout.Rect);
        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, leaderEndStars.X, leaderEndStars.Y, _leaderPaint);

        canvas.DrawText(layout.Line1, layout.TextX, layout.TextY, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
        DrawDatablockLine(
            canvas,
            layout.Line2,
            layout.TextX,
            layout.TextY + layout.LineHeight,
            ResolveMvaAltitudeTint(ac),
            (layout.TypeTokenStart, layout.TypeTokenLength),
            ResolveTypeMismatchTint(ac),
            layout.IdentActive
        );

        int row = 2;
        if (layout.SquawkLine.Length > 0)
        {
            DrawSquawkMismatchLine(canvas, layout.SquawkLine, layout.TextX, layout.TextY + row * layout.LineHeight, color);
            row++;
        }

        if (layout.ReserveOwnerSlot)
        {
            // The slot is reserved even when the handoff is flashing blank, so advance the row
            // unconditionally — otherwise ModeC and the warning line would jump up during the off-phase.
            if (layout.Line3.Length > 0)
            {
                canvas.DrawText(
                    layout.Line3,
                    layout.TextX,
                    layout.TextY + row * layout.LineHeight,
                    SKTextAlign.Left,
                    _dataBlockFont,
                    _dataBlockPaint
                );
            }
            row++;
        }

        if (layout.AtpaLine.Length > 0)
        {
            // Steady (never flashed): the in-trail distance is a continuous readout, and a Monitor-state
            // pairing is routine enough to stay in the block's own colour.
            SKColor prev = _dataBlockPaint.Color;
            if (ac.AtpaConeState != AtpaConeState.Monitor)
            {
                _dataBlockPaint.Color = AtpaConeColorFor(ac.AtpaConeState);
            }
            canvas.DrawText(layout.AtpaLine, layout.TextX, layout.TextY + row * layout.LineHeight, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
            _dataBlockPaint.Color = prev;
            row++;
        }

        if (layout.Line4.Length > 0)
        {
            float modeCBaseline = layout.TextY + row * layout.LineHeight;
            canvas.DrawText(layout.Line4, layout.TextX, modeCBaseline, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
            DrawStrikethrough(canvas, layout.TextX, modeCBaseline, layout.Line4, DataBlockStyle, color);
            row++;
        }

        if (layout.ReserveWarningSlot)
        {
            // Reserved slot: advance the row even while the warning flashes blank, so the conflict
            // field below doesn't jump up during the off-phase.
            if (layout.Line5.Length > 0)
            {
                SKColor prev = _dataBlockPaint.Color;
                _dataBlockPaint.Color = SKColors.Red;
                canvas.DrawText(
                    layout.Line5,
                    layout.TextX,
                    layout.TextY + row * layout.LineHeight,
                    SKTextAlign.Left,
                    _dataBlockFont,
                    _dataBlockPaint
                );
                _dataBlockPaint.Color = prev;
            }
            row++;
        }

        if (layout.ReserveConflictSlot && layout.ConflictLine.Length > 0)
        {
            SKColor prev = _dataBlockPaint.Color;
            _dataBlockPaint.Color = ConflictAlertColor;
            canvas.DrawText(
                layout.ConflictLine,
                layout.TextX,
                layout.TextY + row * layout.LineHeight,
                SKTextAlign.Left,
                _dataBlockFont,
                _dataBlockPaint
            );
            _dataBlockPaint.Color = prev;
        }

        // Instructor note: always the bottom line of the block, drawn in amber.
        if (layout.Line6.Length > 0)
        {
            SKColor prev = _dataBlockPaint.Color;
            _dataBlockPaint.Color = NoteColor;
            canvas.DrawText(
                layout.Line6,
                layout.TextX,
                layout.TextY + (layout.LineCount - 1) * layout.LineHeight,
                SKTextAlign.Left,
                _dataBlockFont,
                _dataBlockPaint
            );
            _dataBlockPaint.Color = prev;
        }

        return layout.Rect;
    }

    private SKRect DrawReducedBlock(
        SKCanvas canvas,
        float cx,
        float cy,
        AircraftModel ac,
        SKColor color,
        IReadOnlyList<string> lines,
        bool identOnFirstLine,
        bool isSelected,
        bool hasManualOffset,
        SKPoint manualOffset,
        SKPoint? deconflictOffset
    )
    {
        SKRect rectAtOrigin = RadarDatablockLayout.ReducedRect(lines, DataBlockStyle, 0, 0);
        SKPoint offset = RadarDatablockLayout.ResolveBlockOffset(
            ac,
            SyncStudentLeaderDirection,
            hasManualOffset,
            manualOffset,
            rectAtOrigin,
            deconflictOffset
        );
        float blockX = cx + offset.X;
        float blockY = cy + offset.Y;
        SKRect rect = RadarDatablockLayout.ReducedRect(lines, DataBlockStyle, blockX, blockY);

        if (isSelected)
        {
            canvas.DrawRect(rect, _selectedBorderPaint);
        }

        SKPoint leaderEnd = ClampToBlockEdge(cx, cy, rect);
        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, leaderEnd.X, leaderEnd.Y, _leaderPaint);

        float lineH = DataBlockStyle.LineHeight;
        for (int i = 0; i < lines.Count; i++)
        {
            // Every reduced format carries the ident on its first line; flash that token only.
            DrawDatablockLine(
                canvas,
                lines[i],
                blockX,
                blockY + (i * lineH),
                altTint: null,
                typeSpan: (-1, 0),
                typeTint: null,
                identing: identOnFirstLine && i == 0
            );
        }

        return rect;
    }

    private void DrawStrikethrough(SKCanvas canvas, float textX, float textBaseline, string text, TextStyle style, SKColor color)
    {
        SKFontMetrics metrics = style.Font.Metrics;
        // StrikeoutPosition is negative (above baseline). Some fonts report null/0 — fall back to one-third of cap height.
        float strikeOffset = metrics.StrikeoutPosition.GetValueOrDefault();
        if (strikeOffset == 0)
        {
            strikeOffset = -style.Size / 3f;
        }
        float strikeY = textBaseline + strikeOffset;
        _strikethroughPaint.Color = color;
        _strikethroughPaint.StrokeWidth = MathF.Max(1f, metrics.StrikeoutThickness.GetValueOrDefault());
        float w = style.Measure(text);
        canvas.DrawLine(textX, strikeY, textX + w, strikeY, _strikethroughPaint);
    }

    private SKRect DrawEuroScopeBlock(
        SKCanvas canvas,
        float cx,
        float cy,
        float blockX,
        float blockY,
        AircraftModel ac,
        SKColor color,
        bool isSelected
    )
    {
        EuroScopeTagResult result = EuroScopeTagLayout.Layout(
            ac,
            blockX,
            blockY,
            DataBlockStyle,
            FlashNoLandingClearance,
            ShowConflictAlerts,
            ResolveConflictPeer(ac)
        );
        _lastEuroScopeTags[ac.Callsign] = result;
        SKColor? mvaTint = ResolveMvaAltitudeTint(ac);

        if (isSelected)
        {
            canvas.DrawRect(result.Bounds, _selectedBorderPaint);
        }

        SKPoint leaderEnd = ClampToBlockEdge(cx, cy, result.Bounds);
        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, leaderEnd.X, leaderEnd.Y, _leaderPaint);

        foreach (TagFieldRect f in result.Fields)
        {
            if (f.Field == TagFieldId.NoLandingClearance)
            {
                SKColor prev = _dataBlockPaint.Color;
                _dataBlockPaint.Color = SKColors.Red;
                canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
                _dataBlockPaint.Color = prev;
                continue;
            }

            if (f.Field == TagFieldId.ConflictAlert)
            {
                SKColor prev = _dataBlockPaint.Color;
                _dataBlockPaint.Color = ConflictAlertColor;
                canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
                _dataBlockPaint.Color = prev;
                continue;
            }

            if (f.Field == TagFieldId.Note)
            {
                SKColor prev = _dataBlockPaint.Color;
                _dataBlockPaint.Color = NoteColor;
                canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
                _dataBlockPaint.Color = prev;
                continue;
            }

            if (f.Field == TagFieldId.CurrentAltitude && mvaTint is { } altTint)
            {
                SKColor prev = _dataBlockPaint.Color;
                _dataBlockPaint.Color = altTint;
                canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
                _dataBlockPaint.Color = prev;
                continue;
            }

            if (f.Field == TagFieldId.Squawk)
            {
                DrawSquawkMismatchLine(canvas, f.Text, f.Rect.Left, f.Rect.Bottom, color);
                continue;
            }

            if (f.Field == TagFieldId.Ident)
            {
                // Flashes on the 500 ms cycle. The field rect is emitted either way, so the tag bounds
                // and the click target stay put while the text blinks.
                if (Environment.TickCount64 / 500 % 2 == 0)
                {
                    canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
                }
                continue;
            }

            // Anchor at baseline (= rect.Bottom) so the text aligns with how STARS draws elsewhere.
            canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, SKTextAlign.Left, _dataBlockFont, _dataBlockPaint);
            if (f.Field == TagFieldId.ModeC)
            {
                DrawStrikethrough(canvas, f.Rect.Left, f.Rect.Bottom, f.Text, DataBlockStyle, color);
            }
        }

        return result.Bounds;
    }

    /// <summary>
    /// Draws a transient speech-bubble pill below the aircraft's datablock. Text is word-wrapped
    /// at <see cref="SpeechBubbleMaxLineChars"/>, capped at <see cref="SpeechBubbleMaxLines"/>
    /// with a trailing ellipsis when truncated. Same monospace face as the datablock so the two
    /// read as a single visual stack. Returns the painted rect so the canvas can hit-test
    /// clicks for the click-to-dismiss behavior, or null when nothing was drawn.
    /// </summary>
    private SKRect? DrawSpeechBubble(SKCanvas canvas, SKRect anchor, string text, SpeechBubbleSeverity severity)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        List<string> lines = WrapBubbleText(text);
        if (lines.Count == 0)
        {
            return null;
        }

        float lineH = _bubbleTextFont.Size + 2;
        float maxLineWidth = 0;
        foreach (string line in lines)
        {
            float w = _bubbleTextFont.MeasureText(line);
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

        SKPaint fillPaint = severity == SpeechBubbleSeverity.Warning ? _bubbleFillPaintWarning : _bubbleFillPaint;
        SKPaint borderPaint = severity == SpeechBubbleSeverity.Warning ? _bubbleBorderPaintWarning : _bubbleBorderPaint;
        canvas.DrawRoundRect(rect, 3f, 3f, fillPaint);
        canvas.DrawRoundRect(rect, 3f, 3f, borderPaint);

        float textX = left + pad;
        float baseline = top + pad + _bubbleTextFont.Size;
        for (int i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], textX, baseline + i * lineH, SKTextAlign.Left, _bubbleTextFont, _bubbleTextPaint);
        }

        return rect;
    }

    private const int SpeechBubbleMaxLineChars = 36;
    private const int SpeechBubbleMaxLines = 3;

    private static List<string> WrapBubbleText(string text)
    {
        var lines = new List<string>();
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();
        foreach (string word in words)
        {
            // Word longer than the line width on its own: hard-break it.
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

        // If we ran out of room mid-text, mark the last line with an ellipsis.
        if (lines.Count == SpeechBubbleMaxLines)
        {
            int totalUsed = lines.Sum(l => l.Length) + (lines.Count - 1);
            if (totalUsed < text.Length)
            {
                string last = lines[^1];
                if (last.Length >= SpeechBubbleMaxLineChars - 1)
                {
                    last = last[..(SpeechBubbleMaxLineChars - 1)];
                }
                lines[^1] = last + "…";
            }
        }

        return lines;
    }

    private static SKPoint ClampToBlockEdge(float pointX, float pointY, SKRect rect)
    {
        if (rect.Contains(pointX, pointY))
        {
            return new SKPoint(pointX, pointY);
        }

        return new SKPoint(Math.Clamp(pointX, rect.Left, rect.Right), Math.Clamp(pointY, rect.Top, rect.Bottom));
    }

    private void DrawHistoryTrails(SKCanvas canvas, MapViewport vp, IReadOnlyList<AircraftModel> aircraft, int historyCount)
    {
        float baseAlpha = 255 * HistoryBrightness;

        foreach (AircraftModel ac in aircraft)
        {
            if (ac.PositionHistory is not { Count: > 0 })
            {
                continue;
            }

            IReadOnlyList<double[]> dots = ac.PositionHistory;
            int start = Math.Max(0, dots.Count - historyCount);
            int visibleCount = dots.Count - start;
            for (int i = start; i < dots.Count; i++)
            {
                int dotIndex = i - start;
                byte alpha = (byte)(baseAlpha * (dotIndex + 1) / visibleCount);
                _historyPaint.Color = HistoryColor.WithAlpha(alpha);
                double[] dot = dots[i];
                (float hx, float hy) = vp.LatLonToScreen(dot[0], dot[1]);
                canvas.DrawCircle(hx, hy, SymbolSize / 2f, _historyPaint);
            }
        }
    }

    public void Dispose()
    {
        _symbolPaint.Dispose();
        _shadowSymbolPaint.Dispose();
        _leaderPaint.Dispose();
        _dataBlockPaint.Dispose();
        _dataBlockFont.Dispose();
        _historyPaint.Dispose();
        _selectedBorderPaint.Dispose();
        _ptlPaint.Dispose();
        _strikethroughPaint.Dispose();
        _bubbleFillPaint.Dispose();
        _bubbleBorderPaint.Dispose();
        _bubbleFillPaintWarning.Dispose();
        _bubbleBorderPaintWarning.Dispose();
        _bubbleTextPaint.Dispose();
        _bubbleTextFont.Dispose();
        _tpaPaint.Dispose();
        _tpaTextPaint.Dispose();
        _tpaTextFont.Dispose();
        _tpaLabelBackingPaint.Dispose();
    }
}
