using SkiaSharp;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Scope;

/// <summary>Draws the map tools over the Scope: the Ctrl-hover MVA label, white on a dark rounded box beside the pointer.</summary>
internal sealed class ScopeMapToolsRenderer : IDisposable
{
    private const float LabelFontSize = 13f;
    private const float PadX = 5f;
    private const float PadY = 3f;
    private const float Gap = 14f;
    private const float CornerRadius = 3f;

    private readonly SKPaint _textPaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _backgroundPaint = new() { Color = new SKColor(0, 0, 0, 200), IsAntialias = true };
    private readonly SKFont _font = PlatformHelper.MonospaceFont(LabelFontSize);

    /// <summary>Draws the map tools' overlays of <paramref name="overlays"/>, over every other layer.</summary>
    public void DrawOverlay(SKCanvas canvas, ScopeOverlays overlays)
    {
        if (overlays.MvaHover is { } hover)
        {
            DrawMvaHover(canvas, hover);
        }
    }

    public void Dispose()
    {
        _textPaint.Dispose();
        _backgroundPaint.Dispose();
        _font.Dispose();
    }

    private void DrawMvaHover(SKCanvas canvas, ScopeMvaHover hover)
    {
        float width = _font.MeasureText(hover.Text);
        float x = hover.At.X + Gap;
        float baseline = hover.At.Y + Gap + _font.Size;
        var background = new SKRect(x - PadX, baseline - _font.Size - PadY, x + width + PadX, baseline + PadY);
        canvas.DrawRoundRect(background, CornerRadius, CornerRadius, _backgroundPaint);
        canvas.DrawText(hover.Text, x, baseline, SKTextAlign.Left, _font, _textPaint);
    }
}
