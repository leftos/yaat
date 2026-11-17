using SkiaSharp;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// Draws the map tools on the Scope: under the tracks the range ring, gray at the RINGS brightness group, and at the MAP B
/// brightness the FIX overlay's crosses (teal for the selected aircraft's programmed fixes, labelled with the hovered one) and
/// the pinned markers' yellow diamonds with their names; and over every other layer the weather lines, STARS green mono 14
/// at the bottom left growing upward, and the Ctrl-hover MVA label, white on a dark rounded box beside the pointer.
/// </summary>
internal sealed class ScopeMapToolsRenderer : IDisposable
{
    /// <summary>How far the weather lines sit from the canvas's left edge, and from its bottom on a STARS profile, in pixels.</summary>
    internal const float WeatherMargin = 10f;

    /// <summary>The distance between two weather lines' baselines, in pixels.</summary>
    internal const float WeatherLineHeight = 18f;

    /// <summary>The room under the lowest weather line's baseline for its descenders, in pixels.</summary>
    internal const float WeatherDescent = 4f;

    // The closest two range-ring circles may sit, in pixels: a finer step at a long range would draw hundreds of full-screen
    // circles every frame.
    private const float MinRingStepPx = 4f;

    private const float WeatherFontSize = 14f;

    private const float LabelFontSize = 13f;
    private const float PadX = 5f;
    private const float PadY = 3f;
    private const float Gap = 14f;
    private const float CornerRadius = 3f;

    // How far outside the viewport a fix or a marker may sit and still be drawn, so a label reaching into the view shows.
    private const float CullMargin = 50f;

    private const float FixCrossSize = 4f;
    private const float ProgrammedFixCrossSize = 6f;
    private const float FixLabelOffset = 6f;
    private const float LabelRise = 2f;
    private const float FixFontSize = 14f;
    private const float MarkerSize = 6f;

    private static readonly SKColor RangeRingColor = new(100, 100, 100);

    private static readonly SKColor FixColor = new(100, 100, 100);

    private static readonly SKColor ProgrammedFixColor = new(0, 220, 200);

    // Yellow, apart from the gray fixes and the teal programmed ones.
    private static readonly SKColor MarkerColor = new(255, 220, 0);

    private readonly SKPaint _fixPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1,
        IsAntialias = true,
    };

    private readonly SKPaint _programmedFixPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2,
        IsAntialias = true,
    };

    private readonly SKPaint _markerPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2,
        IsAntialias = true,
    };

    private readonly SKPaint _fixLabelPaint = new() { IsAntialias = true };
    private readonly SKPaint _programmedFixLabelPaint = new() { IsAntialias = true };
    private readonly SKPaint _markerLabelPaint = new() { IsAntialias = true };
    private readonly SKFont _fixFont = PlatformHelper.MonospaceFont(FixFontSize);

    private readonly SKPaint _textPaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _backgroundPaint = new() { Color = new SKColor(0, 0, 0, 200), IsAntialias = true };
    private readonly SKFont _font = PlatformHelper.MonospaceFont(LabelFontSize);

    private readonly SKPaint _ringPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1,
        IsAntialias = true,
    };

    private readonly SKPaint _weatherPaint = new() { Color = new SKColor(0x00, 0xC8, 0x00), IsAntialias = true };
    private readonly SKFont _weatherFont = PlatformHelper.MonospaceFont(WeatherFontSize);

    // The stroke paints put in the radar's line style each frame; each carries its base width in DIPs.
    private readonly (SKPaint Paint, float BaseWidth)[] _strokePaints;

    public ScopeMapToolsRenderer() => _strokePaints = [(_ringPaint, 1f), (_fixPaint, 1f), (_programmedFixPaint, 2f), (_markerPaint, 2f)];

    /// <summary>
    /// The radii of the range ring's circles <paramref name="stepPx"/> apart around <paramref name="center"/> that cross a
    /// <paramref name="width"/> by <paramref name="height"/> viewport: from the first that reaches it to the last inside its
    /// farthest corner. None when a step is under four pixels.
    /// </summary>
    internal static IEnumerable<float> RingRadii(SKPoint center, float stepPx, float width, float height)
    {
        if ((stepPx < MinRingStepPx) || (width < 1) || (height < 1))
        {
            yield break;
        }

        float nearX = Math.Clamp(center.X, 0, width) - center.X;
        float nearY = Math.Clamp(center.Y, 0, height) - center.Y;
        float farX = MathF.Max(MathF.Abs(center.X), MathF.Abs(width - center.X));
        float farY = MathF.Max(MathF.Abs(center.Y), MathF.Abs(height - center.Y));
        int first = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt((nearX * nearX) + (nearY * nearY)) / stepPx));
        int last = (int)MathF.Floor(MathF.Sqrt((farX * farX) + (farY * farY)) / stepPx);
        for (int k = first; k <= last; k++)
        {
            yield return k * stepPx;
        }
    }

    /// <summary>
    /// The baselines of <paramref name="count"/> weather lines on a canvas <paramref name="height"/> tall, top to bottom,
    /// <see cref="WeatherLineHeight"/> apart: the last line ends <paramref name="bottomInset"/> above the bottom edge, and each
    /// line before it sits one line higher.
    /// </summary>
    internal static float[] WeatherBaselines(int count, float height, float bottomInset)
    {
        float[] baselines = new float[count];
        float lowest = height - bottomInset - WeatherDescent;
        for (int i = 0; i < count; i++)
        {
            baselines[i] = lowest - ((count - 1 - i) * WeatherLineHeight);
        }

        return baselines;
    }

    /// <summary>
    /// Draws the map tools under the tracks, in the radar's line style like every other map stroke: the range ring of
    /// <paramref name="overlays"/> at the RINGS brightness of <paramref name="brightness"/>, then its fixes and its pinned
    /// markers at the MAP B brightness.
    /// </summary>
    public void DrawUnder(SKCanvas canvas, ScopeOverlays overlays, MapViewport vp, ScopeBrightness brightness)
    {
        MapLineStyle.Apply(_strokePaints, MapLineStyle.GetScale(canvas));
        if (overlays.RangeRing is { } ring)
        {
            DrawRangeRing(canvas, ring, vp, brightness.Rings);
        }

        if (overlays.Fixes is { } fixes)
        {
            DrawFixes(canvas, fixes, vp, brightness.MapB);
        }

        if (overlays.Markers is { } markers)
        {
            DrawMarkers(canvas, markers, vp, brightness.MapB);
        }
    }

    /// <summary>
    /// Whether a point drawn at (<paramref name="x"/>, <paramref name="y"/>) lies within <see cref="CullMargin"/> of a
    /// <paramref name="width"/> by <paramref name="height"/> viewport, so its symbol or label may show.
    /// </summary>
    internal static bool IsNearView(float x, float y, float width, float height) =>
        (x >= -CullMargin) && (x <= width + CullMargin) && (y >= -CullMargin) && (y <= height + CullMargin);

    /// <summary>Draws the map tools' overlays of <paramref name="overlays"/>, over every other layer.</summary>
    public void DrawOverlay(SKCanvas canvas, ScopeOverlays overlays, MapViewport vp)
    {
        if (overlays.Weather is { } weather)
        {
            DrawWeather(canvas, weather, vp.PixelHeight);
        }

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
        _ringPaint.Dispose();
        _weatherPaint.Dispose();
        _weatherFont.Dispose();
        _fixPaint.Dispose();
        _programmedFixPaint.Dispose();
        _markerPaint.Dispose();
        _fixLabelPaint.Dispose();
        _programmedFixLabelPaint.Dispose();
        _markerLabelPaint.Dispose();
        _fixFont.Dispose();
    }

    private void DrawRangeRing(SKCanvas canvas, ScopeRangeRing ring, MapViewport vp, int percent)
    {
        (float cx, float cy) = vp.LatLonToScreen(ring.Center.Lat, ring.Center.Lon);
        (_, float edgeY) = vp.LatLonToScreen(ring.Center.Lat + (ring.SizeNm / 60.0), ring.Center.Lon);
        _ringPaint.Color = ScopeBrightness.Apply(RangeRingColor, percent);
        foreach (float radius in RingRadii(new SKPoint(cx, cy), MathF.Abs(cy - edgeY), vp.PixelWidth, vp.PixelHeight))
        {
            canvas.DrawCircle(cx, cy, radius, _ringPaint);
        }
    }

    private void DrawFixes(SKCanvas canvas, ScopeFixLayer layer, MapViewport vp, int percent)
    {
        _fixPaint.Color = ScopeBrightness.Apply(FixColor, percent);
        _fixLabelPaint.Color = ScopeBrightness.Apply(SKColors.White, percent);
        _programmedFixPaint.Color = ScopeBrightness.Apply(ProgrammedFixColor, percent);
        _programmedFixLabelPaint.Color = _programmedFixPaint.Color;
        foreach ((string name, double lat, double lon) in layer.Fixes)
        {
            (float x, float y) = vp.LatLonToScreen(lat, lon);
            if (IsNearView(x, y, vp.PixelWidth, vp.PixelHeight))
            {
                DrawFix(canvas, name, new SKPoint(x, y), layer);
            }
        }
    }

    // A cross per fix, larger and teal for a programmed one; a programmed or the hovered fix is labelled.
    private void DrawFix(SKCanvas canvas, string name, SKPoint at, ScopeFixLayer layer)
    {
        bool programmed = layer.Programmed.Contains(name);
        SKPaint paint = programmed ? _programmedFixPaint : _fixPaint;
        float size = programmed ? ProgrammedFixCrossSize : FixCrossSize;
        canvas.DrawLine(at.X - size, at.Y, at.X + size, at.Y, paint);
        canvas.DrawLine(at.X, at.Y - size, at.X, at.Y + size, paint);
        if (programmed || (name == layer.Hovered))
        {
            SKPaint labelPaint = programmed ? _programmedFixLabelPaint : _fixLabelPaint;
            canvas.DrawText(name, at.X + FixLabelOffset, at.Y - LabelRise, SKTextAlign.Left, _fixFont, labelPaint);
        }
    }

    private void DrawMarkers(SKCanvas canvas, IReadOnlyList<ScopeMarker> markers, MapViewport vp, int percent)
    {
        _markerPaint.Color = ScopeBrightness.Apply(MarkerColor, percent);
        _markerLabelPaint.Color = _markerPaint.Color;
        foreach (ScopeMarker marker in markers)
        {
            (float x, float y) = vp.LatLonToScreen(marker.Position.Lat, marker.Position.Lon);
            if (!IsNearView(x, y, vp.PixelWidth, vp.PixelHeight))
            {
                continue;
            }

            // A diamond, to read apart from the fixes' crosses.
            using var path = new SKPath();
            path.MoveTo(x, y - MarkerSize);
            path.LineTo(x + MarkerSize, y);
            path.LineTo(x, y + MarkerSize);
            path.LineTo(x - MarkerSize, y);
            path.Close();
            canvas.DrawPath(path, _markerPaint);
            canvas.DrawText(marker.Name, x + MarkerSize + 2, y - LabelRise, SKTextAlign.Left, _fixFont, _markerLabelPaint);
        }
    }

    private void DrawWeather(SKCanvas canvas, ScopeWeatherText weather, float height)
    {
        float[] baselines = WeatherBaselines(weather.Lines.Count, height, weather.BottomInset);
        for (int i = 0; i < baselines.Length; i++)
        {
            canvas.DrawText(weather.Lines[i], WeatherMargin, baselines[i], SKTextAlign.Left, _weatherFont, _weatherPaint);
        }
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
