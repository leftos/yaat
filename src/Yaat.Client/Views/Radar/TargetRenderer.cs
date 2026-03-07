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
    private static readonly SKColor HistoryColor = new(0, 100, 0);
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

    private const float SymbolSize = 5f;
    private const float LeaderLength = 40f;

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
        bool showTopDown = false
    )
    {
        foreach (var ac in aircraft)
        {
            var (sx, sy) = vp.LatLonToScreen(ac.Latitude, ac.Longitude);

            bool isSelected = ac == selectedAircraft;
            bool isOnGround = showTopDown && (int)(ac.Altitude / 100) < 1;
            var baseSymbolColor = isOnGround ? GroundColor : SymbolColor;
            var baseDbColor = isOnGround ? GroundColor : DataBlockColor;
            var symbolColor = isSelected ? SelectedColor : baseSymbolColor;
            var dbColor = isSelected ? SelectedColor : baseDbColor;
            bool isMinified = minifiedCallsigns is not null && minifiedCallsigns.Contains(ac.Callsign);

            if (ptlLengthMinutes > 0 && ShouldShowPtl(ac, ptlOwn, ptlAll))
            {
                DrawPtlLine(canvas, vp, sx, sy, ac, dbColor, ptlLengthMinutes);
            }

            DrawPositionSymbol(canvas, sx, sy, symbolColor);
            DrawLeaderAndDataBlock(canvas, sx, sy, ac, dbColor, dataBlockOffsets, isMinified, isSelected);
        }
    }

    private void DrawPtlLine(SKCanvas canvas, MapViewport vp, float sx, float sy, AircraftModel ac, SKColor color, double minutes)
    {
        if (ac.GroundSpeed < 1)
        {
            return;
        }

        var distNm = ac.GroundSpeed * minutes / 60.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(ac.Latitude, ac.Longitude, ac.Heading, distNm);
        var (ex, ey) = vp.LatLonToScreen(endLat, endLon);

        _ptlPaint.Color = color;
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
        float lineH = _dataBlockPaint.TextSize + 2;

        var altHundreds = ((int)ac.Altitude / 100).ToString("D3");
        var cwt = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";

        if (isMinified)
        {
            // Minified: single line with altitude + CWT
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
            // Full datablock: line1 = callsign (+ * for VFR), line2 = alt speed CWT/TYPE, line3 = owner + scratchpads
            bool isVfr = ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
            string line1 = isVfr ? $"{ac.Callsign}*" : ac.Callsign;
            var spdTens = ((int)ac.GroundSpeed / 10).ToString("D2");
            var cwtType = FormatCwtType(cwt, ac.AircraftType);
            string line2 = cwtType.Length > 0 ? $"{altHundreds} {spdTens} {cwtType}" : $"{altHundreds} {spdTens}";

            float w1 = _dataBlockPaint.MeasureText(line1);
            float w2 = _dataBlockPaint.MeasureText(line2);
            float textW = MathF.Max(w1, w2);
            int lineCount = 2;

            // Line 3: owner TCP + scratchpads on same line
            string? line3 = BuildOwnerScratchpadLine(ac.OwnerDisplay, ac.Scratchpad1, ac.Scratchpad2);
            if (line3 is not null)
            {
                float w3 = _dataBlockPaint.MeasureText(line3);
                textW = MathF.Max(textW, w3);
                lineCount = 3;
            }

            const float pad = 3f;
            var blockRect = new SKRect(
                blockX - pad,
                blockY - _dataBlockPaint.TextSize - pad,
                blockX + textW + pad,
                blockY + (lineCount - 1) * lineH + pad
            );

            if (isSelected)
            {
                canvas.DrawRect(blockRect, _selectedBorderPaint);
            }

            var leaderEnd = ClampToBlockEdge(cx, cy, blockRect);
            _leaderPaint.Color = color;
            canvas.DrawLine(cx, cy, leaderEnd.X, leaderEnd.Y, _leaderPaint);

            canvas.DrawText(line1, blockX, blockY, _dataBlockPaint);
            canvas.DrawText(line2, blockX, blockY + lineH, _dataBlockPaint);

            if (line3 is not null)
            {
                canvas.DrawText(line3, blockX, blockY + 2 * lineH, _dataBlockPaint);
            }
        }
    }

    private static string? BuildOwnerScratchpadLine(string? ownerDisplay, string? sp1, string? sp2)
    {
        bool hasOwner = !string.IsNullOrEmpty(ownerDisplay);
        bool hasSp1 = !string.IsNullOrEmpty(sp1);
        bool hasSp2 = !string.IsNullOrEmpty(sp2);

        if (!hasOwner && !hasSp1 && !hasSp2)
        {
            return null;
        }

        var parts = new List<string>(3);
        if (hasOwner)
        {
            parts.Add(ownerDisplay!);
        }

        if (hasSp1)
        {
            parts.Add($".{sp1}");
        }

        if (hasSp2)
        {
            parts.Add($"+{sp2}");
        }

        return string.Join(" ", parts);
    }

    private static string FormatCwtType(string cwt, string aircraftType)
    {
        var baseType = aircraftType.Trim();
        if (cwt.Length > 0 && baseType.Length > 0)
        {
            return $"{cwt}/{baseType}";
        }

        if (cwt.Length > 0)
        {
            return cwt;
        }

        return baseType;
    }

    private static SKPoint ClampToBlockEdge(float pointX, float pointY, SKRect rect)
    {
        if (rect.Contains(pointX, pointY))
        {
            return new SKPoint(pointX, pointY);
        }

        return new SKPoint(Math.Clamp(pointX, rect.Left, rect.Right), Math.Clamp(pointY, rect.Top, rect.Bottom));
    }

    public void Dispose()
    {
        _symbolPaint.Dispose();
        _leaderPaint.Dispose();
        _dataBlockPaint.Dispose();
        _historyPaint.Dispose();
        _selectedBorderPaint.Dispose();
        _ptlPaint.Dispose();
    }
}
