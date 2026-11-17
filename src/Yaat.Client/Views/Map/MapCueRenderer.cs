using SkiaSharp;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Map;

/// <summary>
/// Draws a menu row's cue on a map view: the solid ring on the aircraft the row names, the dashed bearing line to it from the
/// aircraft the menu commands, labelled with the clock and miles between them, and the dashed ring on the aircraft the call
/// resolves to when that is another one. The Radar and the Scope share it, so a cue reads the same on both.
/// </summary>
public sealed class MapCueRenderer : IDisposable
{
    /// <summary>The radius of the rings a menu cue draws around an aircraft symbol.</summary>
    public const float MenuCueRingRadiusPx = 13f;

    /// <summary>Where a menu cue's bearing label starts, from the bearing line's midpoint: right of it, the baseline just below.</summary>
    private static readonly SKPoint MenuCueLabelOffset = new(8, 4);

    private static readonly SKColor MenuCueColor = SKColor.Parse("#8fc1ff");

    private static readonly SKColor MenuCueResolvedColor = SKColor.Parse("#7fd1b9");

    private readonly SKPaint _ringPaint = new()
    {
        Color = MenuCueColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _resolvedRingPaint = new()
    {
        Color = MenuCueResolvedColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash([4, 3], 0),
    };

    private readonly SKPaint _linePaint = new()
    {
        Color = MenuCueColor,
        StrokeWidth = 1.4f,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash([5, 4], 0),
    };

    private readonly SKPaint _labelPaint = new() { Color = MenuCueColor, IsAntialias = true };

    private readonly SKFont _labelFont = PlatformHelper.MonospaceFont(11);

    /// <summary>
    /// Draws <paramref name="cue"/> over <paramref name="vp"/>'s screen: the bearing line and its label when the cue places
    /// both ends, the solid ring on the target and the dashed ring on the resolved other aircraft, each only when the cue
    /// places it; nothing for null.
    /// </summary>
    public void Draw(SKCanvas canvas, MapViewport vp, ResolvedMapCue? cue)
    {
        if (cue is null)
        {
            return;
        }

        if (cue.Target is { } target)
        {
            (float tx, float ty) = vp.LatLonToScreen(target.Lat, target.Lon);
            if (cue.From is { } from)
            {
                (float fx, float fy) = vp.LatLonToScreen(from.Lat, from.Lon);
                canvas.DrawLine(fx, fy, tx, ty, _linePaint);
                if (cue.BearingLabel is { } label)
                {
                    float x = ((fx + tx) / 2) + MenuCueLabelOffset.X;
                    float y = ((fy + ty) / 2) + MenuCueLabelOffset.Y;
                    canvas.DrawText(label, x, y, SKTextAlign.Left, _labelFont, _labelPaint);
                }
            }

            canvas.DrawCircle(tx, ty, MenuCueRingRadiusPx, _ringPaint);
        }

        if (cue.ResolvedOther is { } other)
        {
            (float ox, float oy) = vp.LatLonToScreen(other.Lat, other.Lon);
            canvas.DrawCircle(ox, oy, MenuCueRingRadiusPx, _resolvedRingPaint);
        }
    }

    public void Dispose()
    {
        _ringPaint.Dispose();
        _resolvedRingPaint.Dispose();
        _linePaint.Dispose();
        _labelPaint.Dispose();
        _labelFont.Dispose();
    }
}
