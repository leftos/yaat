using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;

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

    // Bubble pill colors — subtle teal-tinted background with a SAY-green border so the
    // bubble reads as a pilot-transmission overlay without competing with the datablock.
    private static readonly SKColor SpeechBubbleFillColor = new(20, 60, 50, 220);
    private static readonly SKColor SpeechBubbleBorderColor = new(80, 220, 140);
    private static readonly SKColor SpeechBubbleTextColor = SKColors.White;

    private readonly SKPaint _symbolPaint = new()
    {
        StrokeWidth = 1.5f,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _leaderPaint = new()
    {
        StrokeWidth = 1,
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

    private readonly SKPaint _bubbleTextPaint = new()
    {
        TextSize = 12,
        Color = SpeechBubbleTextColor,
        IsAntialias = true,
        SubpixelText = true,
        Typeface = Services.PlatformHelper.MonospaceTypefaceBold,
    };

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
        var baseSymbolColor = tintColor ?? (isOnGround ? GroundColor : SymbolColor);
        var baseDbColor = tintColor ?? (isOnGround ? GroundColor : DataBlockColor);
        var selectedColor = SelectedOverrideColor ?? SelectedColor;
        var symbolColor = isSelected ? selectedColor : baseSymbolColor;
        var dbColor =
            isHighlighted ? SKColors.Cyan
            : isSelected ? selectedColor
            : baseDbColor;
        bool isMinified = minifiedCallsigns is not null && minifiedCallsigns.Contains(ac.Callsign);

        if (ptlLengthMinutes > 0 && ShouldShowPtl(ac, ptlOwn, ptlAll))
        {
            DrawPtlLine(canvas, vp, sx, sy, ac, ptlLengthMinutes);
        }

        DrawPositionSymbol(canvas, sx, sy, symbolColor);
        var blockRect = DrawLeaderAndDataBlock(canvas, sx, sy, ac, dbColor, dataBlockOffsets, isMinified, isSelected);

        if (drawBubble && ac.SpeechBubble is { } bubble)
        {
            var bubbleRect = DrawSpeechBubble(canvas, blockRect, bubble.Text);
            if (bubbleRect is { } r)
            {
                _lastBubbleRects[ac.Callsign] = r;
            }
        }
    }

    private void DrawPtlLine(SKCanvas canvas, MapViewport vp, float sx, float sy, AircraftModel ac, double minutes)
    {
        if (ac.GroundSpeed < 1)
        {
            return;
        }

        var distNm = ac.GroundSpeed * minutes / 60.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(ac.Position, new TrueHeading(ac.Heading), distNm);
        var (ex, ey) = vp.LatLonToScreen(endLat, endLon);

        _ptlPaint.Color = SKColors.White;
        canvas.DrawLine(sx, sy, ex, ey, _ptlPaint);
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
        SKPoint offset = new(28, -28);
        if (dataBlockOffsets is not null && dataBlockOffsets.TryGetValue(ac.Callsign, out var customOffset))
        {
            offset = customOffset;
        }

        float blockX = cx + offset.X;
        float blockY = cy + offset.Y;

        _dataBlockPaint.Color = color;

        if (EuroScopeMode && !isMinified)
        {
            return DrawEuroScopeBlock(canvas, cx, cy, blockX, blockY, ac, color, isSelected);
        }

        if (isMinified)
        {
            // Minified: single line with altitude + CWT
            string altHundreds = ((int)ac.Altitude / 100).ToString("D3");
            string cwt = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";
            string miniLine = cwt.Length > 0 ? $"{altHundreds} {cwt}" : altHundreds;
            float miniW = _dataBlockPaint.MeasureText(miniLine);

            const float pad = 3f;
            var blockRect = new SKRect(blockX - pad, blockY - _dataBlockPaint.TextSize - pad, blockX + miniW + pad, blockY + pad);

            if (isSelected)
            {
                canvas.DrawRect(blockRect, _selectedBorderPaint);
            }

            var leaderEnd = ClampToBlockEdge(cx, cy, blockRect);
            _leaderPaint.Color = color;
            canvas.DrawLine(cx, cy, leaderEnd.X, leaderEnd.Y, _leaderPaint);

            canvas.DrawText(miniLine, blockX, blockY, _dataBlockPaint);
            return blockRect;
        }

        var layout = RadarDatablockLayout.Compute(ac, blockX, blockY, _dataBlockPaint, FlashNoLandingClearance);

        if (isSelected)
        {
            canvas.DrawRect(layout.Rect, _selectedBorderPaint);
        }

        var leaderEndStars = ClampToBlockEdge(cx, cy, layout.Rect);
        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, leaderEndStars.X, leaderEndStars.Y, _leaderPaint);

        canvas.DrawText(layout.Line1, layout.TextX, layout.TextY, _dataBlockPaint);
        canvas.DrawText(layout.Line2, layout.TextX, layout.TextY + layout.LineHeight, _dataBlockPaint);

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

        return layout.Rect;
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
    private SKRect? DrawSpeechBubble(SKCanvas canvas, SKRect anchor, string text)
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

        canvas.DrawRoundRect(rect, 3f, 3f, _bubbleFillPaint);
        canvas.DrawRoundRect(rect, 3f, 3f, _bubbleBorderPaint);

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
        _bubbleTextPaint.Dispose();
    }
}
