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
    private readonly SKPaint _mapPaintA = new()
    {
        Color = new SKColor(0, 184, 0),
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _mapPaintB = new()
    {
        Color = new SKColor(0, 184, 0, 128),
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    public float BrightnessA { get; set; } = 1.0f;
    public float BrightnessB { get; set; } = 0.6f;

    public void Render(SKCanvas canvas, MapViewport vp, IReadOnlyList<VideoMapData> maps, IReadOnlyDictionary<string, string> brightnessLookup)
    {
        foreach (var map in maps)
        {
            var category = brightnessLookup.GetValueOrDefault(map.MapId, "A");
            var paint = category == "B" ? _mapPaintB : _mapPaintA;

            var brightness = category == "B" ? BrightnessB : BrightnessA;
            paint.Color = new SKColor(0, 184, 0, (byte)(brightness * 255));

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
