using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// Draws a frame's speech bubbles: a SAY / pilot transmission's green pill and a warning's amber one, each a rounded rect with
/// its wrapped lines in the block's monospace face. Owns its paints and its font; the renderer keeps one instance.
/// </summary>
public sealed class ScopeBubbleRenderer : IDisposable
{
    private const float CornerRadius = 3f;

    // Subtle teal-tinted background with a SAY-green border, so the bubble reads as a pilot-transmission overlay without
    // competing with the datablock; the amber variant matches the terminal's warning colour.
    private static readonly SKColor SpeechFillColor = new(20, 60, 50, 220);
    private static readonly SKColor SpeechBorderColor = new(80, 220, 140);
    private static readonly SKColor WarningFillColor = new(70, 50, 10, 220);
    private static readonly SKColor WarningBorderColor = new(230, 170, 40);

    private readonly SKPaint _speechFillPaint = new()
    {
        Color = SpeechFillColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _speechBorderPaint = new()
    {
        Color = SpeechBorderColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _warningFillPaint = new()
    {
        Color = WarningFillColor,
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
    };

    private readonly SKPaint _warningBorderPaint = new()
    {
        Color = WarningBorderColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _textPaint = new() { Color = SKColors.White, IsAntialias = true };

    private readonly SKFont _font = PlatformHelper.MonospaceFontBold(12);

    /// <summary>Draws <paramref name="bubbles"/>, each in its severity's colours, its lines at <paramref name="fontSize"/>.</summary>
    public void Draw(SKCanvas canvas, IReadOnlyList<ScopeBubble>? bubbles, float fontSize)
    {
        if (bubbles is not { Count: > 0 })
        {
            return;
        }

        _font.Size = fontSize;
        float lineHeight = ScopeBubbles.LineHeight(_font);
        foreach (ScopeBubble bubble in bubbles)
        {
            DrawOne(canvas, bubble, lineHeight);
        }
    }

    public void Dispose()
    {
        _speechFillPaint.Dispose();
        _speechBorderPaint.Dispose();
        _warningFillPaint.Dispose();
        _warningBorderPaint.Dispose();
        _textPaint.Dispose();
        _font.Dispose();
    }

    private void DrawOne(SKCanvas canvas, ScopeBubble bubble, float lineHeight)
    {
        bool warning = bubble.Severity == SpeechBubbleSeverity.Warning;
        canvas.DrawRoundRect(bubble.Rect, CornerRadius, CornerRadius, warning ? _warningFillPaint : _speechFillPaint);
        canvas.DrawRoundRect(bubble.Rect, CornerRadius, CornerRadius, warning ? _warningBorderPaint : _speechBorderPaint);

        float x = bubble.Rect.Left + ScopeBubbles.Pad;
        float baseline = bubble.Rect.Top + ScopeBubbles.Pad + _font.Size;
        for (int i = 0; i < bubble.Lines.Count; i++)
        {
            canvas.DrawText(bubble.Lines[i], x, baseline + (i * lineHeight), SKTextAlign.Left, _font, _textPaint);
        }
    }
}
