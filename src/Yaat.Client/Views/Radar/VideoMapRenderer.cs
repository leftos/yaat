using SkiaSharp;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Renders parsed video map line features onto a SkiaSharp canvas.
/// Pre-computes screen coordinates on viewport change.
/// </summary>
public sealed class VideoMapRenderer : IDisposable
{
    private static readonly SKColor BaseMapColor = new(160, 160, 160);

    private readonly SKPaint _mapPaintA = new()
    {
        Color = BaseMapColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _mapPaintB = new()
    {
        Color = BaseMapColor.WithAlpha(128),
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    // Rendered CRC-style (no AA, device-pixel width) by RadarLineStyle.Apply each frame; the tuple
    // carries each paint's base width in DIPs.
    private readonly (SKPaint Paint, float BaseWidth)[] _strokePaints;

    public VideoMapRenderer()
    {
        _strokePaints = [(_mapPaintA, 1f), (_mapPaintB, 1f)];
    }

    public float BrightnessA { get; set; } = 1.0f;
    public float BrightnessB { get; set; } = 0.6f;

    public void Render(SKCanvas canvas, MapViewport vp, IReadOnlyList<VideoMapData> maps, IReadOnlyDictionary<string, string> brightnessLookup)
    {
        RadarLineStyle.Apply(_strokePaints, RadarLineStyle.GetScale(canvas));

        foreach (var map in maps)
        {
            var category = brightnessLookup.GetValueOrDefault(map.MapId, "A");
            var paint = category == "B" ? _mapPaintB : _mapPaintA;

            var brightness = category == "B" ? BrightnessB : BrightnessA;
            paint.Color = BaseMapColor.WithAlpha((byte)(brightness * 255));

            foreach (var line in map.Lines)
            {
                if (line.Points.Count < 2)
                {
                    continue;
                }

                using var path = new SKPath();
                var (sx, sy) = vp.LatLonToScreen(line.Points[0].Lat, line.Points[0].Lon);
                path.MoveTo(sx, sy);

                for (int i = 1; i < line.Points.Count; i++)
                {
                    (sx, sy) = vp.LatLonToScreen(line.Points[i].Lat, line.Points[i].Lon);
                    path.LineTo(sx, sy);
                }

                canvas.DrawPath(path, paint);
            }
        }
    }

    public void Dispose()
    {
        _mapPaintA.Dispose();
        _mapPaintB.Dispose();
    }
}
