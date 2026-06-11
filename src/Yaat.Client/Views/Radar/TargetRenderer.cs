using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;

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

    // STARS TPA graphics color (CRC's TCW value, DisplayElementTracks: Color.FromArgb(90, 180, 255)) —
    // used for the instructor J-Ring / Cone overlay so it reads like the TPA graphics a controller sees.
    private static readonly SKColor TpaColor = new(90, 180, 255);

    private const int TpaJRing = 1;
    private const int TpaCone = 2;

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

    // Drawn thicker than the 1px PTL so the line to the data block stays
    // distinguishable from the predicted-track vector when both emanate from the symbol.
    private readonly SKPaint _leaderPaint = new()
    {
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _dataBlockPaint = new()
    {
        TextSize = 12,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypefaceBold,
    };

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

    private readonly SKPaint _tpaTextPaint = new()
    {
        TextSize = 11,
        Color = TpaColor,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypefaceBold,
    };

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

    private readonly SKPaint _bubbleTextPaint = new()
    {
        TextSize = 12,
        Color = SpeechBubbleTextColor,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypefaceBold,
    };

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

    /// <summary>Datablock text size in pixels. Updated from UserPreferences via RadarView.SyncAssignmentTint.</summary>
    public float DatablockTextSize
    {
        get => _dataBlockPaint.TextSize;
        set
        {
            _dataBlockPaint.TextSize = value;
            _bubbleTextPaint.TextSize = value;
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

    public void Render(
        SKCanvas canvas,
        MapViewport vp,
        IReadOnlyList<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        double ptlLengthMinutes = 0,
        bool ptlOwn = false,
        bool ptlAll = false,
        IReadOnlySet<string>? minifiedCallsigns = null,
        IReadOnlySet<string>? highlightedCallsigns = null,
        bool showTopDown = false,
        int historyCount = 0
    )
    {
        // Draw history trails first (behind position symbols)
        if (historyCount > 0)
        {
            DrawHistoryTrails(canvas, vp, aircraft, historyCount);
        }

        _lastEuroScopeTags.Clear();
        _lastBubbleRects.Clear();

        // Two-pass render: aircraft with an active speech bubble are deferred to pass 2 so
        // their symbol, datablock, and bubble all paint on top of neighboring aircraft.
        // Without this, an overlapping datablock from a non-bubbled aircraft would obscure
        // the bubble we're trying to surface. List is allocated only when speech bubbles
        // are enabled; otherwise the per-aircraft check is a single property read.
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
            DrawOneAircraft(
                canvas,
                vp,
                ac,
                selectedAircraft,
                dataBlockOffsets,
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
            foreach (var ac in deferred)
            {
                DrawOneAircraft(
                    canvas,
                    vp,
                    ac,
                    selectedAircraft,
                    dataBlockOffsets,
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
        IReadOnlySet<string>? minifiedCallsigns,
        IReadOnlySet<string>? highlightedCallsigns,
        bool showTopDown,
        double ptlLengthMinutes,
        bool ptlOwn,
        bool ptlAll,
        bool drawBubble
    )
    {
        var (sx, sy) = vp.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

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
        var (symbolColor, dbColor) = ResolveTargetColors(
            new TargetColorInputs(isSelected, isHighlighted, isOnGround, tintColor, studentColor, SelectedOverrideColor ?? SelectedColor)
        );
        bool isMinified = minifiedCallsigns is not null && minifiedCallsigns.Contains(ac.Callsign);

        if (ptlLengthMinutes > 0 && ShouldShowPtl(ac, ptlOwn, ptlAll))
        {
            DrawPtlLine(canvas, vp, sx, sy, ac, ptlLengthMinutes);
        }

        if (ac.TpaType != 0 && ac.TpaSize > 0)
        {
            DrawTpaGraphic(canvas, vp, sx, sy, ac);
        }

        DrawPositionSymbol(canvas, sx, sy, symbolColor);
        var blockRect = DrawLeaderAndDataBlock(canvas, sx, sy, ac, dbColor, dataBlockOffsets, isMinified, isSelected);

        if (drawBubble && ac.SpeechBubble is { } bubble)
        {
            var bubbleRect = DrawSpeechBubble(canvas, blockRect, bubble.Text, bubble.Severity);
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
        SKColor SelectedColor
    );

    /// <summary>
    /// Resolves the position-symbol and datablock (text + leader) colors for one target. Selection
    /// brightens the position symbol to <see cref="TargetColorInputs.SelectedColor"/> but leaves the
    /// datablock text and leader at their unselected color — the white rectangular border is the
    /// datablock's selection cue. An RPO tint outranks the student-scope color, which outranks the
    /// ground/white defaults; a middle-click highlight overrides the datablock color entirely.
    /// </summary>
    internal static (SKColor Symbol, SKColor DataBlock) ResolveTargetColors(TargetColorInputs i)
    {
        var baseSymbolColor = i.TintColor ?? (i.IsOnGround ? GroundColor : SymbolColor);
        var baseDbColor = i.TintColor ?? i.StudentColor ?? (i.IsOnGround ? GroundColor : DataBlockColor);
        var symbolColor = i.IsSelected ? i.SelectedColor : baseSymbolColor;
        var dbColor = i.IsHighlighted ? SKColors.Cyan : baseDbColor;
        return (symbolColor, dbColor);
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

        var distNm = ac.GroundSpeed * minutes / 60.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(ac.Position, ac.Heading, distNm);
        var (ex, ey) = vp.LatLonToScreen(endLat, endLon);

        _ptlPaint.Color = SKColors.White;
        canvas.DrawLine(sx, sy, ex, ey, _ptlPaint);
    }

    /// <summary>
    /// Draws the instructor TPA overlay (emulating STARS *J/*P) on YAAT's own radar. A J-Ring is a
    /// circle of radius <see cref="AircraftModel.TpaSize"/> nm centered on the target; a Cone is a thin
    /// wedge projecting from the target along its track for <see cref="AircraftModel.TpaSize"/> nm. Both
    /// are geo-anchored (ring/wedge vertices are projected, not pixel-scaled) so they stay accurate at
    /// any zoom, and carry a numeric size label like CRC's optional TPA size display.
    /// </summary>
    private void DrawTpaGraphic(SKCanvas canvas, MapViewport vp, float sx, float sy, AircraftModel ac)
    {
        if (ac.TpaType == TpaJRing)
        {
            DrawTpaJRing(canvas, vp, ac);
        }
        else if (ac.TpaType == TpaCone)
        {
            DrawTpaCone(canvas, vp, sx, sy, ac);
        }
    }

    private void DrawTpaJRing(SKCanvas canvas, MapViewport vp, AircraftModel ac)
    {
        using var path = new SKPath();
        for (int deg = 0; deg <= 360; deg += 5)
        {
            var (lat, lon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(deg), ac.TpaSize);
            var (px, py) = vp.LatLonToScreen(lat, lon);
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
        var (lblLat, lblLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(180), ac.TpaSize);
        var (lx, ly) = vp.LatLonToScreen(lblLat, lblLon);
        DrawTpaSizeLabel(canvas, lx, ly, ac.TpaSize);
    }

    private void DrawTpaCone(SKCanvas canvas, MapViewport vp, float sx, float sy, AircraftModel ac)
    {
        // CRC projects the cone along ground track; YAAT draws its leader line along Heading, so the
        // cone shares that axis to stay visually consistent with the rest of the YAAT target.
        var axis = ac.Heading.Degrees;
        var (leftLat, leftLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis + TpaConeHalfAngleDegrees), ac.TpaSize);
        var (rightLat, rightLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis - TpaConeHalfAngleDegrees), ac.TpaSize);
        var (lx, ly) = vp.LatLonToScreen(leftLat, leftLon);
        var (rx, ry) = vp.LatLonToScreen(rightLat, rightLon);

        using var path = new SKPath();
        path.MoveTo(sx, sy);
        path.LineTo(lx, ly);
        path.LineTo(rx, ry);
        path.Close();
        canvas.DrawPath(path, _tpaPaint);

        // Size label at the cone's midpoint along the track axis (mirrors CRC).
        var (midLat, midLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(axis), ac.TpaSize / 2.0);
        var (mx, my) = vp.LatLonToScreen(midLat, midLon);
        DrawTpaSizeLabel(canvas, mx, my, ac.TpaSize);
    }

    private void DrawTpaSizeLabel(SKCanvas canvas, float centerX, float centerY, double sizeNm)
    {
        var text = sizeNm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        var width = _tpaTextPaint.MeasureText(text);
        var height = _tpaTextPaint.TextSize;
        var baselineX = centerX - (width / 2f);
        var baselineY = centerY + (height / 2f);

        // Black backing rect keeps the value legible over targets / video maps (mirrors CRC's RenderQuad).
        canvas.DrawRect(baselineX - 2f, baselineY - height - 1f, width + 4f, height + 4f, _tpaLabelBackingPaint);
        canvas.DrawText(text, baselineX, baselineY, _tpaTextPaint);
    }

    private static bool ShouldShowPtl(AircraftModel ac, bool ptlOwn, bool ptlAll)
    {
        if (ptlAll)
        {
            return true;
        }

        return ptlOwn && !string.IsNullOrEmpty(ac.Owner);
    }

    private void DrawPositionSymbol(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        _symbolPaint.Color = color;
        canvas.DrawCircle(cx, cy, SymbolSize, _symbolPaint);
    }

    /// <summary>
    /// The MVA altitude-tint color for an aircraft, or null when the altitude field should render in the
    /// normal block color (tint disabled, on the ground, MSAW-inhibited, above the floor, or outside MVA
    /// coverage). VFR is MSAW-inhibited by default (7110.65 §5-14-7); an explicit per-aircraft inhibit
    /// flag would extend this gate.
    /// </summary>
    private SKColor? ResolveMvaAltitudeTint(AircraftModel ac)
    {
        if (!ShowMvaAltitudeTint || ac.IsOnGround || ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var (relation, _) = MvaDatabase.Default.Classify(ac.Position, ac.Altitude, MvaAtBandFt);
        return relation switch
        {
            MvaRelation.Below => MvaBelowColor,
            MvaRelation.At => MvaAtColor,
            _ => null,
        };
    }

    /// <summary>
    /// Draws a datablock altitude line, tinting the leading altitude token (everything before the first
    /// space) when <paramref name="altTint"/> is set and leaving the rest of the line in the block color.
    /// </summary>
    private void DrawAltitudeLine(SKCanvas canvas, string line, float x, float baseline, SKColor? altTint)
    {
        if (altTint is not { } tint || line.Length == 0)
        {
            canvas.DrawText(line, x, baseline, _dataBlockPaint);
            return;
        }

        int space = line.IndexOf(' ');
        string altToken = space < 0 ? line : line[..space];
        var prev = _dataBlockPaint.Color;
        _dataBlockPaint.Color = tint;
        canvas.DrawText(altToken, x, baseline, _dataBlockPaint);
        _dataBlockPaint.Color = prev;
        if (space >= 0)
        {
            float altWidth = _dataBlockPaint.MeasureText(altToken);
            canvas.DrawText(line[space..], x + altWidth, baseline, _dataBlockPaint);
        }
    }

    private SKRect DrawLeaderAndDataBlock(
        SKCanvas canvas,
        float cx,
        float cy,
        AircraftModel ac,
        SKColor color,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets,
        bool isMinified,
        bool isSelected
    )
    {
        _dataBlockPaint.Color = color;

        SKPoint manualOffset = default;
        bool hasManualOffset = dataBlockOffsets is not null && dataBlockOffsets.TryGetValue(ac.Callsign, out manualOffset);

        if (EuroScopeMode && !isMinified)
        {
            // EuroScope tags keep manual/default placement; student-scope sync targets STARS tags only.
            var esOffset = hasManualOffset ? manualOffset : RadarDatablockLayout.DefaultOffset;
            return DrawEuroScopeBlock(canvas, cx, cy, cx + esOffset.X, cy + esOffset.Y, ac, color, isSelected);
        }

        // Collapse to the reduced datablock the student actually sees (LDB/PDB) when enabled and the
        // student does not see a full block. The instructor's manual minify toggle takes precedence.
        bool collapse =
            !isMinified && CollapseStudentDatablocks && ac.StudentDatablockLevel is StarsDatablockLevel.Limited or StarsDatablockLevel.Partial;

        if (isMinified || collapse)
        {
            IReadOnlyList<string> lines = isMinified ? [RadarDatablockLayout.BuildMinifiedLine(ac)] : RadarDatablockLayout.BuildCollapsedLines(ac);
            return DrawReducedBlock(canvas, cx, cy, ac, color, lines, isSelected, hasManualOffset, manualOffset);
        }

        // Full STARS block, optionally annotated with the student's (LDB)/(PDB) marker.
        string marker = MarkStudentLimitedDatablocks ? RadarDatablockLayout.StudentLevelMarker(ac.StudentDatablockLevel) : "";
        var rectAtOrigin = RadarDatablockLayout.Compute(ac, 0, 0, _dataBlockPaint, FlashNoLandingClearance, marker).Rect;
        var offset = RadarDatablockLayout.ResolveBlockOffset(ac, SyncStudentLeaderDirection, hasManualOffset, manualOffset, rectAtOrigin);
        float blockX = cx + offset.X;
        float blockY = cy + offset.Y;

        var layout = RadarDatablockLayout.Compute(ac, blockX, blockY, _dataBlockPaint, FlashNoLandingClearance, marker);

        if (isSelected)
        {
            canvas.DrawRect(layout.Rect, _selectedBorderPaint);
        }

        var leaderEndStars = ClampToBlockEdge(cx, cy, layout.Rect);
        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, leaderEndStars.X, leaderEndStars.Y, _leaderPaint);

        canvas.DrawText(layout.Line1, layout.TextX, layout.TextY, _dataBlockPaint);
        DrawAltitudeLine(canvas, layout.Line2, layout.TextX, layout.TextY + layout.LineHeight, ResolveMvaAltitudeTint(ac));

        int row = 2;
        if (layout.Line3.Length > 0)
        {
            canvas.DrawText(layout.Line3, layout.TextX, layout.TextY + row * layout.LineHeight, _dataBlockPaint);
            row++;
        }

        if (layout.Line4.Length > 0)
        {
            float modeCBaseline = layout.TextY + row * layout.LineHeight;
            canvas.DrawText(layout.Line4, layout.TextX, modeCBaseline, _dataBlockPaint);
            DrawStrikethrough(canvas, layout.TextX, modeCBaseline, layout.Line4, _dataBlockPaint, color);
            row++;
        }

        if (layout.Line5.Length > 0)
        {
            var prev = _dataBlockPaint.Color;
            _dataBlockPaint.Color = SKColors.Red;
            canvas.DrawText(layout.Line5, layout.TextX, layout.TextY + row * layout.LineHeight, _dataBlockPaint);
            _dataBlockPaint.Color = prev;
        }

        // Instructor note: always the bottom line of the block, drawn in amber.
        if (layout.Line6.Length > 0)
        {
            var prev = _dataBlockPaint.Color;
            _dataBlockPaint.Color = NoteColor;
            canvas.DrawText(layout.Line6, layout.TextX, layout.TextY + (layout.LineCount - 1) * layout.LineHeight, _dataBlockPaint);
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
        bool isSelected,
        bool hasManualOffset,
        SKPoint manualOffset
    )
    {
        var rectAtOrigin = RadarDatablockLayout.ReducedRect(lines, _dataBlockPaint, 0, 0);
        var offset = RadarDatablockLayout.ResolveBlockOffset(ac, SyncStudentLeaderDirection, hasManualOffset, manualOffset, rectAtOrigin);
        float blockX = cx + offset.X;
        float blockY = cy + offset.Y;
        var rect = RadarDatablockLayout.ReducedRect(lines, _dataBlockPaint, blockX, blockY);

        if (isSelected)
        {
            canvas.DrawRect(rect, _selectedBorderPaint);
        }

        var leaderEnd = ClampToBlockEdge(cx, cy, rect);
        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, leaderEnd.X, leaderEnd.Y, _leaderPaint);

        float lineH = _dataBlockPaint.TextSize + 2;
        for (int i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], blockX, blockY + (i * lineH), _dataBlockPaint);
        }

        return rect;
    }

    private void DrawStrikethrough(SKCanvas canvas, float textX, float textBaseline, string text, SKPaint textPaint, SKColor color)
    {
        textPaint.GetFontMetrics(out var metrics);
        // StrikeoutPosition is negative (above baseline). Some fonts report null/0 — fall back to one-third of cap height.
        float strikeOffset = metrics.StrikeoutPosition.GetValueOrDefault();
        if (strikeOffset == 0)
        {
            strikeOffset = -textPaint.TextSize / 3f;
        }
        float strikeY = textBaseline + strikeOffset;
        _strikethroughPaint.Color = color;
        _strikethroughPaint.StrokeWidth = MathF.Max(1f, metrics.StrikeoutThickness.GetValueOrDefault());
        float w = textPaint.MeasureText(text);
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
        var result = EuroScopeTagLayout.Layout(ac, blockX, blockY, _dataBlockPaint, LocalUserInitials, FlashNoLandingClearance);
        _lastEuroScopeTags[ac.Callsign] = result;
        var mvaTint = ResolveMvaAltitudeTint(ac);

        if (isSelected)
        {
            canvas.DrawRect(result.Bounds, _selectedBorderPaint);
        }

        var leaderEnd = ClampToBlockEdge(cx, cy, result.Bounds);
        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, leaderEnd.X, leaderEnd.Y, _leaderPaint);

        foreach (var f in result.Fields)
        {
            if (f.Field == TagFieldId.NoLandingClearance)
            {
                var prev = _dataBlockPaint.Color;
                _dataBlockPaint.Color = SKColors.Red;
                canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, _dataBlockPaint);
                _dataBlockPaint.Color = prev;
                continue;
            }

            if (f.Field == TagFieldId.Note)
            {
                var prev = _dataBlockPaint.Color;
                _dataBlockPaint.Color = NoteColor;
                canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, _dataBlockPaint);
                _dataBlockPaint.Color = prev;
                continue;
            }

            if (f.Field == TagFieldId.CurrentAltitude && mvaTint is { } altTint)
            {
                var prev = _dataBlockPaint.Color;
                _dataBlockPaint.Color = altTint;
                canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, _dataBlockPaint);
                _dataBlockPaint.Color = prev;
                continue;
            }

            // Anchor at baseline (= rect.Bottom) so the text aligns with how STARS draws elsewhere.
            canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, _dataBlockPaint);
            if (f.Field == TagFieldId.ModeC)
            {
                DrawStrikethrough(canvas, f.Rect.Left, f.Rect.Bottom, f.Text, _dataBlockPaint, color);
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

    private const int SpeechBubbleMaxLineChars = 36;
    private const int SpeechBubbleMaxLines = 3;

    private static List<string> WrapBubbleText(string text)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();
        foreach (var word in words)
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

        foreach (var ac in aircraft)
        {
            if (ac.PositionHistory is not { Count: > 0 })
            {
                continue;
            }

            var dots = ac.PositionHistory;
            int start = Math.Max(0, dots.Count - historyCount);
            int visibleCount = dots.Count - start;
            for (int i = start; i < dots.Count; i++)
            {
                int dotIndex = i - start;
                byte alpha = (byte)(baseAlpha * (dotIndex + 1) / visibleCount);
                _historyPaint.Color = HistoryColor.WithAlpha(alpha);
                var dot = dots[i];
                var (hx, hy) = vp.LatLonToScreen(dot[0], dot[1]);
                canvas.DrawCircle(hx, hy, SymbolSize / 2f, _historyPaint);
            }
        }
    }

    public void Dispose()
    {
        _symbolPaint.Dispose();
        _leaderPaint.Dispose();
        _dataBlockPaint.Dispose();
        _historyPaint.Dispose();
        _selectedBorderPaint.Dispose();
        _ptlPaint.Dispose();
        _strikethroughPaint.Dispose();
        _bubbleFillPaint.Dispose();
        _bubbleBorderPaint.Dispose();
        _bubbleFillPaintWarning.Dispose();
        _bubbleBorderPaintWarning.Dispose();
        _bubbleTextPaint.Dispose();
        _tpaPaint.Dispose();
        _tpaTextPaint.Dispose();
        _tpaLabelBackingPaint.Dispose();
    }
}
