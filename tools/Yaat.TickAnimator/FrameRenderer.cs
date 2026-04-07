using SkiaSharp;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.TickAnimator;

/// <summary>
/// Renders tick-by-tick aircraft movement over an airport layout as an animated GIF.
/// Coordinate projection: simple equirectangular with cos(lat) correction.
/// </summary>
internal sealed class FrameRenderer
{
    private const double FeetPerNm = 6076.12;
    private const double NmPerDegreeLat = 60.0;
    private const double DegToRad = Math.PI / 180.0;

    private readonly AirportGroundLayout _layout;
    private readonly List<TickRecord> _ticks;
    private readonly double _lengthFt;
    private readonly double _wingspanFt;
    private readonly Options _opts;

    // Viewport in lat/lon
    private double _minLat,
        _maxLat,
        _minLon,
        _maxLon;
    private double _centerLat;
    private double _cosLat;

    // Pixel dimensions
    private int _width,
        _height;
    private double _scale; // pixels per degree latitude

    public FrameRenderer(AirportGroundLayout layout, List<TickRecord> ticks, double lengthFt, double wingspanFt, Options opts)
    {
        _layout = layout;
        _ticks = ticks;
        _lengthFt = lengthFt;
        _wingspanFt = wingspanFt;
        _opts = opts;

        ComputeViewport();
    }

    private void ComputeViewport()
    {
        double paddingDeg = _opts.PaddingNm / NmPerDegreeLat;

        if (_opts.FitLayout)
        {
            // Fit to entire airport layout
            var lats = _layout.Nodes.Values.Select(n => n.Latitude);
            var lons = _layout.Nodes.Values.Select(n => n.Longitude);
            _minLat = lats.Min() - paddingDeg;
            _maxLat = lats.Max() + paddingDeg;
        }
        else
        {
            // Fit to tick data
            _minLat = _ticks.Min(t => t.Lat) - paddingDeg;
            _maxLat = _ticks.Max(t => t.Lat) + paddingDeg;
        }

        _centerLat = (_minLat + _maxLat) / 2;
        _cosLat = Math.Cos(_centerLat * DegToRad);

        double paddingLon = paddingDeg / _cosLat;
        if (_opts.FitLayout)
        {
            var lons = _layout.Nodes.Values.Select(n => n.Longitude);
            _minLon = lons.Min() - paddingLon;
            _maxLon = lons.Max() + paddingLon;
        }
        else
        {
            _minLon = _ticks.Min(t => t.Lon) - paddingLon;
            _maxLon = _ticks.Max(t => t.Lon) + paddingLon;
        }

        double latSpan = _maxLat - _minLat;
        double lonSpan = (_maxLon - _minLon) * _cosLat; // normalized to lat-equivalent

        _width = _opts.Width;
        double aspect = lonSpan / latSpan;
        _height = Math.Max(200, (int)(_width / aspect));

        _scale = _height / latSpan;
    }

    private float ToX(double lon) => (float)(((lon - _minLon) * _cosLat) * _scale);

    private float ToY(double lat) => (float)((_maxLat - lat) * _scale); // Y inverted

    /// <summary>Convert a distance in feet to pixels at the current zoom.</summary>
    private float FeetToPx(double feet)
    {
        double nm = feet / FeetPerNm;
        double deg = nm / NmPerDegreeLat;
        return (float)(deg * _scale);
    }

    public void RenderGif()
    {
        string? outputDir = Path.GetDirectoryName(_opts.OutputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Render each frame as PNG, then combine via SkiaSharp GIF-like approach.
        // SkiaSharp doesn't natively encode animated GIF, so we output individual
        // frames and attempt ffmpeg. If ffmpeg isn't available, output frames only.
        string framesDir = Path.Combine(outputDir ?? ".", "frames");
        Directory.CreateDirectory(framesDir);

        for (int i = 0; i < _ticks.Count; i++)
        {
            using var surface = SKSurface.Create(new SKImageInfo(_width, _height));
            var canvas = surface.Canvas;

            RenderFrame(canvas, i);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            string framePath = Path.Combine(framesDir, $"frame_{i:D4}.png");
            File.WriteAllBytes(framePath, data.ToArray());
        }

        Console.Error.WriteLine($"Rendered {_ticks.Count} frames to {framesDir}");

        // Try ffmpeg to create output
        bool created = TryCreateWithFfmpeg(framesDir);
        if (!created)
        {
            Console.Error.WriteLine("ffmpeg not found. Frames saved to: " + framesDir);
            Console.Error.WriteLine(
                $"To create GIF manually: ffmpeg -framerate {_opts.Fps} -i {framesDir}/frame_%04d.png -vf palettegen=stats_mode=diff palette.png "
                    + $"&& ffmpeg -framerate {_opts.Fps} -i {framesDir}/frame_%04d.png -i palette.png -lavfi paletteuse=dither=bayer {_opts.OutputPath}"
            );
        }
    }

    private bool TryCreateWithFfmpeg(string framesDir)
    {
        try
        {
            string ext = Path.GetExtension(_opts.OutputPath).ToLowerInvariant();
            string inputPattern = Path.Combine(framesDir, "frame_%04d.png");

            string ffmpegArgs;
            if (ext == ".gif")
            {
                // Two-pass for good GIF quality
                string palettePath = Path.Combine(framesDir, "palette.png");
                var palProc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-y -framerate {_opts.Fps} -i \"{inputPattern}\" -vf palettegen=stats_mode=diff \"{palettePath}\"",
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                );
                palProc?.WaitForExit();
                if (palProc?.ExitCode != 0)
                {
                    return false;
                }

                ffmpegArgs =
                    $"-y -framerate {_opts.Fps} -i \"{inputPattern}\" -i \"{palettePath}\" "
                    + $"-lavfi \"paletteuse=dither=bayer\" \"{_opts.OutputPath}\"";
            }
            else
            {
                ffmpegArgs = $"-y -framerate {_opts.Fps} -i \"{inputPattern}\" " + $"-c:v libx264 -pix_fmt yuv420p \"{_opts.OutputPath}\"";
            }

            var proc = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void RenderFrame(SKCanvas canvas, int tickIndex)
    {
        // Background
        canvas.Clear(new SKColor(14, 14, 26));

        DrawLayout(canvas);
        DrawTrail(canvas, tickIndex);
        DrawAircraft(canvas, tickIndex);
        DrawOverlay(canvas, tickIndex);
    }

    private void DrawLayout(SKCanvas canvas)
    {
        // Draw runways (filled)
        using var runwayFillPaint = new SKPaint
        {
            Color = new SKColor(100, 100, 120, 60),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        foreach (var rwy in _layout.Runways)
        {
            if (rwy.Coordinates.Count < 2)
            {
                continue;
            }

            double halfWidthNm = (rwy.WidthFt / 2) / FeetPerNm;
            double halfWidthDeg = halfWidthNm / NmPerDegreeLat;

            // Draw as a thick line
            using var rwyPaint = new SKPaint
            {
                Color = new SKColor(140, 140, 160, 80),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = FeetToPx(rwy.WidthFt),
                StrokeCap = SKStrokeCap.Butt,
                IsAntialias = true,
            };
            using var path = new SKPath();
            path.MoveTo(ToX(rwy.Coordinates[0].Lon), ToY(rwy.Coordinates[0].Lat));
            for (int i = 1; i < rwy.Coordinates.Count; i++)
            {
                path.LineTo(ToX(rwy.Coordinates[i].Lon), ToY(rwy.Coordinates[i].Lat));
            }

            canvas.DrawPath(path, rwyPaint);
        }

        // Draw edges
        using var taxiwayPaint = new SKPaint
        {
            Color = new SKColor(85, 153, 221, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        };
        using var runwayEdgePaint = new SKPaint
        {
            Color = new SKColor(120, 120, 120, 80),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true,
        };

        foreach (var edge in _layout.Edges)
        {
            var n0 = edge.Nodes[0];
            var n1 = edge.Nodes[1];
            var paint = edge.IsRunwayCenterline ? runwayEdgePaint : taxiwayPaint;

            if (edge.IntermediatePoints.Count > 0)
            {
                using var path = new SKPath();
                path.MoveTo(ToX(n0.Longitude), ToY(n0.Latitude));
                foreach (var (lat, lon) in edge.IntermediatePoints)
                {
                    path.LineTo(ToX(lon), ToY(lat));
                }

                path.LineTo(ToX(n1.Longitude), ToY(n1.Latitude));
                canvas.DrawPath(path, paint);
            }
            else
            {
                canvas.DrawLine(ToX(n0.Longitude), ToY(n0.Latitude), ToX(n1.Longitude), ToY(n1.Latitude), paint);
            }
        }

        // Draw arcs
        using var arcPaint = new SKPaint
        {
            Color = new SKColor(80, 200, 80, 120),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true,
        };

        foreach (var arc in _layout.Arcs)
        {
            var bezier = arc.ToBezier();
            const int steps = 16;
            using var path = new SKPath();
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                var (lat, lon) = bezier.Evaluate(t);
                float sx = ToX(lon);
                float sy = ToY(lat);
                if (s == 0)
                {
                    path.MoveTo(sx, sy);
                }
                else
                {
                    path.LineTo(sx, sy);
                }
            }

            canvas.DrawPath(path, arcPaint);
        }

        // Draw nodes
        using var intersectionPaint = new SKPaint
        {
            Color = new SKColor(100, 130, 180, 80),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var holdShortPaint = new SKPaint
        {
            Color = new SKColor(255, 50, 50, 200),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var labelPaint = new SKPaint
        {
            Color = new SKColor(180, 180, 200, 160),
            TextSize = 10,
            IsAntialias = true,
        };

        foreach (var node in _layout.Nodes.Values)
        {
            float nx = ToX(node.Longitude);
            float ny = ToY(node.Latitude);

            // Skip nodes outside viewport (with margin)
            if (nx < -50 || nx > _width + 50 || ny < -50 || ny > _height + 50)
            {
                continue;
            }

            switch (node.Type)
            {
                case GroundNodeType.RunwayHoldShort:
                {
                    canvas.DrawCircle(nx, ny, 4, holdShortPaint);
                    // Label with taxiway name from connected non-runway edges
                    string? twyName = node.Edges.Select(e => e.TaxiwayName).FirstOrDefault(n => !n.StartsWith("RWY"));
                    string hsLabel = twyName is not null ? $"{twyName} HS" : $"HS{node.Id}";
                    canvas.DrawText(hsLabel, nx + 6, ny - 2, labelPaint);
                    break;
                }
                case GroundNodeType.TaxiwayIntersection:
                    canvas.DrawCircle(nx, ny, 2, intersectionPaint);
                    break;
            }
        }

        // Draw taxiway labels on edges
        using var twyLabelPaint = new SKPaint
        {
            Color = new SKColor(85, 153, 221, 140),
            TextSize = 11,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Bold),
        };
        var labeledEdges = new HashSet<string>();
        foreach (var edge in _layout.Edges)
        {
            if (edge.IsRunwayCenterline || string.IsNullOrEmpty(edge.TaxiwayName))
            {
                continue;
            }

            string key = edge.TaxiwayName;
            if (labeledEdges.Contains(key))
            {
                continue;
            }

            var n0 = edge.Nodes[0];
            var n1 = edge.Nodes[1];
            float mx = (ToX(n0.Longitude) + ToX(n1.Longitude)) / 2;
            float my = (ToY(n0.Latitude) + ToY(n1.Latitude)) / 2;

            if (mx >= 0 && mx <= _width && my >= 0 && my <= _height)
            {
                canvas.DrawText(edge.TaxiwayName, mx + 3, my - 3, twyLabelPaint);
                labeledEdges.Add(key);
            }
        }
    }

    private void DrawTrail(SKCanvas canvas, int tickIndex)
    {
        int start = Math.Max(0, tickIndex - _opts.TrailLength);
        for (int i = start; i < tickIndex; i++)
        {
            float age = (float)(tickIndex - i) / _opts.TrailLength;
            byte alpha = (byte)(200 * (1.0f - age));
            float radius = 2.5f * (1.0f - age * 0.5f);
            using var paint = new SKPaint
            {
                Color = new SKColor(100, 200, 255, alpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };
            canvas.DrawCircle(ToX(_ticks[i].Lon), ToY(_ticks[i].Lat), radius, paint);
        }
    }

    private void DrawAircraft(SKCanvas canvas, int tickIndex)
    {
        var tick = _ticks[tickIndex];
        float cx = ToX(tick.Lon);
        float cy = ToY(tick.Lat);

        float lengthPx = FeetToPx(_lengthFt);
        float wingspanPx = FeetToPx(_wingspanFt);

        // Ensure minimum visibility
        lengthPx = Math.Max(lengthPx, 12);
        wingspanPx = Math.Max(wingspanPx, 8);

        canvas.Save();
        canvas.Translate(cx, cy);
        // SkiaSharp: 0° = right, rotate clockwise. Heading: 0° = north, CW.
        // So rotate by (heading - 90) to align nose-up heading with canvas.
        canvas.RotateDegrees((float)(tick.Hdg - 90));

        // Fuselage rectangle (along X axis after rotation)
        float halfLen = lengthPx / 2;
        float halfWing = wingspanPx / 2;
        float fuselageHalfWidth = halfWing * 0.15f; // narrow fuselage

        // Wings (wider rectangle, shorter)
        using var wingPaint = new SKPaint
        {
            Color = new SKColor(220, 220, 240, 180),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        var wingRect = new SKRect(-halfLen * 0.1f, -halfWing, halfLen * 0.1f, halfWing);
        canvas.DrawRect(wingRect, wingPaint);

        // Fuselage
        using var fuselagePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 220),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        var fuselageRect = new SKRect(-halfLen, -fuselageHalfWidth, halfLen, fuselageHalfWidth);
        canvas.DrawRect(fuselageRect, fuselagePaint);

        // Nose indicator (triangle)
        using var nosePaint = new SKPaint
        {
            Color = new SKColor(50, 255, 100, 255),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var nosePath = new SKPath();
        nosePath.MoveTo(halfLen, 0);
        nosePath.LineTo(halfLen - 6, -4);
        nosePath.LineTo(halfLen - 6, 4);
        nosePath.Close();
        canvas.DrawPath(nosePath, nosePaint);

        // Tail indicator
        using var tailPaint = new SKPaint
        {
            Color = new SKColor(255, 100, 50, 200),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var tailPath = new SKPath();
        float tailSpan = halfWing * 0.5f;
        tailPath.MoveTo(-halfLen, -tailSpan);
        tailPath.LineTo(-halfLen - 4, 0);
        tailPath.LineTo(-halfLen, tailSpan);
        tailPath.Close();
        canvas.DrawPath(tailPath, tailPaint);

        canvas.Restore();

        // Heading line extending forward from nose
        double hdgRad = tick.Hdg * DegToRad;
        float lineLen = 40;
        using var headingLinePaint = new SKPaint
        {
            Color = new SKColor(50, 255, 100, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([4, 4], 0),
        };
        float endX = cx + lineLen * (float)Math.Sin(hdgRad);
        float endY = cy - lineLen * (float)Math.Cos(hdgRad);
        canvas.DrawLine(cx, cy, endX, endY, headingLinePaint);
    }

    private void DrawOverlay(SKCanvas canvas, int tickIndex)
    {
        var tick = _ticks[tickIndex];

        using var bgPaint = new SKPaint { Color = new SKColor(20, 20, 40, 200), Style = SKPaintStyle.Fill };
        canvas.DrawRect(0, 0, 250, 100, bgPaint);

        using var textPaint = new SKPaint
        {
            Color = new SKColor(200, 200, 220),
            TextSize = 13,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Consolas"),
        };

        float y = 18;
        float lineH = 16;
        canvas.DrawText($"t={tick.Time}s  hdg={tick.Hdg:F1}°  gs={tick.Gs:F1}kts", 8, y, textPaint);
        y += lineH;
        canvas.DrawText($"phase: {tick.Phase}", 8, y, textPaint);
        y += lineH;
        canvas.DrawText($"twy: {tick.Twy}", 8, y, textPaint);
        y += lineH;
        canvas.DrawText($"pos: {tick.Lat:F6}, {tick.Lon:F6}", 8, y, textPaint);
        y += lineH;
        canvas.DrawText($"frame {tickIndex + 1}/{_ticks.Count}", 8, y, textPaint);
    }
}
