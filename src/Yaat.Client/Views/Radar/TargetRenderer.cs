using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;

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

    private const float SymbolSize = 5f;
    private const float LeaderLength = 40f;

    public void Render(SKCanvas canvas, MapViewport vp, IReadOnlyList<AircraftModel> aircraft, AircraftModel? selectedAircraft)
    {
        foreach (var ac in aircraft)
        {
            var (sx, sy) = vp.LatLonToScreen(ac.Latitude, ac.Longitude);

            bool isSelected = ac == selectedAircraft;
            var color = GetTargetColor(ac, isSelected);

            DrawPositionSymbol(canvas, sx, sy, color);
            DrawLeaderAndDataBlock(canvas, sx, sy, ac, color);
        }
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

    private void DrawLeaderAndDataBlock(SKCanvas canvas, float cx, float cy, AircraftModel ac, SKColor color)
    {
        // Leader line: default to upper-right (NE direction)
        float angle = -MathF.PI / 4f; // 45 degrees up-right
        float endX = cx + MathF.Cos(angle) * LeaderLength;
        float endY = cy + MathF.Sin(angle) * LeaderLength;

        _leaderPaint.Color = color;
        canvas.DrawLine(cx, cy, endX, endY, _leaderPaint);

        // Data block at end of leader line
        _dataBlockPaint.Color = color;

        // Line 1: Callsign
        canvas.DrawText(ac.Callsign, endX + 2, endY, _dataBlockPaint);

        // Line 2: Altitude (hundreds) + groundspeed (tens)
        var altHundreds = ((int)ac.Altitude / 100).ToString("D3");
        var spdTens = ((int)ac.GroundSpeed / 10).ToString("D2");
        canvas.DrawText($"{altHundreds} {spdTens}", endX + 2, endY + 14, _dataBlockPaint);
    }

    public void Dispose()
    {
        _symbolPaint.Dispose();
        _leaderPaint.Dispose();
        _dataBlockPaint.Dispose();
        _historyPaint.Dispose();
    }
}
