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

    /// <summary>Datablock text size in pixels. Updated from UserPreferences via RadarView.SyncAssignmentTint.</summary>
    public float DatablockTextSize
    {
        get => _dataBlockPaint.TextSize;
        set => _dataBlockPaint.TextSize = value;
    }

    /// <summary>
    /// Per-aircraft layout result captured during the last Render(). Populated only when
    /// <see cref="EuroScopeMode"/> is on. Consumed by RadarCanvas hit-testing.
    /// </summary>
    public IReadOnlyDictionary<string, EuroScopeTagResult> LastEuroScopeTags => _lastEuroScopeTags;

    private readonly Dictionary<string, EuroScopeTagResult> _lastEuroScopeTags = [];

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

        foreach (var ac in aircraft)
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
            DrawLeaderAndDataBlock(canvas, sx, sy, ac, dbColor, dataBlockOffsets, isMinified, isSelected);
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

    private void DrawLeaderAndDataBlock(
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
            DrawEuroScopeBlock(canvas, cx, cy, blockX, blockY, ac, color, isSelected);
            return;
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
        }
        else
        {
            var layout = RadarDatablockLayout.Compute(ac, blockX, blockY, _dataBlockPaint);

            if (isSelected)
            {
                canvas.DrawRect(layout.Rect, _selectedBorderPaint);
            }

            var leaderEnd = ClampToBlockEdge(cx, cy, layout.Rect);
            _leaderPaint.Color = color;
            canvas.DrawLine(cx, cy, leaderEnd.X, leaderEnd.Y, _leaderPaint);

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
            }
        }
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

    private void DrawEuroScopeBlock(SKCanvas canvas, float cx, float cy, float blockX, float blockY, AircraftModel ac, SKColor color, bool isSelected)
    {
        var result = EuroScopeTagLayout.Layout(ac, blockX, blockY, _dataBlockPaint, LocalUserInitials);
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
            // Anchor at baseline (= rect.Bottom) so the text aligns with how STARS draws elsewhere.
            canvas.DrawText(f.Text, f.Rect.Left, f.Rect.Bottom, _dataBlockPaint);
            if (f.Field == TagFieldId.ModeC)
            {
                DrawStrikethrough(canvas, f.Rect.Left, f.Rect.Bottom, f.Text, _dataBlockPaint, color);
            }
        }
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
    }
}
