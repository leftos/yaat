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
    private static readonly SKColor OwnedColor = SKColors.White;
    private static readonly SKColor UnownedColor = new(0, 184, 0);
    private static readonly SKColor HandoffColor = new(0, 200, 255);
    private static readonly SKColor SelectedColor = new(255, 255, 255);
    private static readonly SKColor HistoryColor = new(0, 100, 0);

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
        Typeface = SKTypeface.FromFamilyName("Consolas"),
    };

    private readonly SKPaint _historyPaint = new()
    {
        Color = HistoryColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _dataBlockBgPaint = new() { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Fill };

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
        bool ptlAll = false
    )
    {
        foreach (var ac in aircraft)
        {
            var (sx, sy) = vp.LatLonToScreen(ac.Latitude, ac.Longitude);

            bool isSelected = ac == selectedAircraft;
            var color = GetTargetColor(ac, isSelected);

            if (ptlLengthMinutes > 0 && ShouldShowPtl(ac, ptlOwn, ptlAll))
            {
                DrawPtlLine(canvas, vp, sx, sy, ac, color, ptlLengthMinutes);
            }

            DrawPositionSymbol(canvas, sx, sy, color);
            DrawLeaderAndDataBlock(canvas, sx, sy, ac, color, dataBlockOffsets);
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

    private static SKColor GetTargetColor(AircraftModel ac, bool isSelected)
    {
        if (isSelected)
        {
            return SelectedColor;
        }

        // Simple ownership heuristic: if has owner, it's "owned"
        if (!string.IsNullOrEmpty(ac.HandoffDisplay))
        {
            return HandoffColor;
        }

        if (!string.IsNullOrEmpty(ac.OwnerDisplay))
        {
            return OwnedColor;
        }

        return UnownedColor;
    }

    private void DrawPositionSymbol(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        _symbolPaint.Color = color;

        // Draw a small circle (primary radar return symbol)
        canvas.DrawCircle(cx, cy, SymbolSize, _symbolPaint);
    }

    private void DrawLeaderAndDataBlock(
        SKCanvas canvas,
        float cx,
        float cy,
        AircraftModel ac,
        SKColor color,
        IReadOnlyDictionary<string, SKPoint>? dataBlockOffsets
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
        string line1 = ac.Callsign;
        var altHundreds = ((int)ac.Altitude / 100).ToString("D3");
        var spdTens = ((int)ac.GroundSpeed / 10).ToString("D2");
        string line2 = $"{altHundreds} {spdTens}";

        float w1 = _dataBlockPaint.MeasureText(line1);
        float w2 = _dataBlockPaint.MeasureText(line2);
        float textW = MathF.Max(w1, w2);
        float lineH = _dataBlockPaint.TextSize + 2;

        const float pad = 3f;
        var blockRect = new SKRect(blockX - pad, blockY - _dataBlockPaint.TextSize - pad, blockX + textW + pad, blockY + lineH + pad);

        canvas.DrawRect(blockRect, _dataBlockBgPaint);

        var leaderEnd = ClampToBlockEdge(cx, cy, blockRect);
        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, leaderEnd.X, leaderEnd.Y, _leaderPaint);

        canvas.DrawText(line1, blockX, blockY, _dataBlockPaint);
        canvas.DrawText(line2, blockX, blockY + lineH, _dataBlockPaint);
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
        _dataBlockBgPaint.Dispose();
        _ptlPaint.Dispose();
    }
}
