using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Strokes radar vector lines the way CRC's STARS engine does: analytic anti-aliasing off, and widths
/// pinned to physical device pixels so a nominal 1px line stays a hard one-device-pixel hairline
/// regardless of monitor DPI (matching CRC's raw <c>GL.LineWidth</c>), rather than a smooth,
/// DPI-scaled Skia stroke. The radar renderers call <see cref="Apply"/> on their scope stroke paints
/// at the top of every frame, so it is the single source of truth for those paints' anti-aliasing and
/// stroke width — the values in the paint initializers are placeholders it overwrites.
/// </summary>
internal static class RadarLineStyle
{
    /// <summary>
    /// Device-independent-pixel → physical-pixel scale of the leased canvas (the DPI factor Avalonia
    /// bakes into the canvas transform). Used to convert a device-pixel width into the DIP-space
    /// <see cref="SKPaint.StrokeWidth"/> the canvas expects.
    /// </summary>
    public static float GetScale(SKCanvas canvas)
    {
        var m = canvas.TotalMatrix;
        float scale = MathF.Sqrt((m.ScaleX * m.ScaleX) + (m.SkewY * m.SkewY));
        return scale > 0f ? scale : 1f;
    }

    /// <summary>
    /// Applies the CRC line style to a set of stroke paints: anti-aliasing off, and the width pinned
    /// to physical pixels. A 1px line becomes a Skia hairline (always exactly one device pixel,
    /// matching CRC's <c>GL.LineWidth(1)</c>); wider lines divide by <paramref name="scale"/> so they
    /// stay N device pixels regardless of DPI. Each entry carries the paint's base width in DIPs.
    /// </summary>
    public static void Apply(IReadOnlyList<(SKPaint Paint, float BaseWidth)> paints, float scale)
    {
        for (int i = 0; i < paints.Count; i++)
        {
            var (paint, baseWidth) = paints[i];
            paint.IsAntialias = false;
            paint.StrokeWidth = (baseWidth <= 1f) ? 0f : (baseWidth / scale);
        }
    }
}
