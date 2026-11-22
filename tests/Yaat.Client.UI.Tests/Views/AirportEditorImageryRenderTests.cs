using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Authoring;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Airport Editor draws the tower-cab image right after the clear, under every feature, at the brightness the frame
/// carries, and draws none while imagery is off.
/// </summary>
public class AirportEditorImageryRenderTests
{
    private const int Size = 200;
    private const double MinLat = 37.70;
    private const double MaxLat = 37.74;
    private const double MinLon = -122.25;
    private const double MaxLon = -122.19;
    private const double MidLat = (MinLat + MaxLat) / 2;

    // The mock's taxiway colour, #ffc83c.
    private static readonly SKColor TaxiwayColour = new(0xFF, 0xC8, 0x3C);
    private static readonly SKColor ImageColour = new(255, 0, 0);

    [AvaloniaFact]
    public void AnImage_DrawsUnderAFeature_AndShowsWhereOnlyTheImageIs()
    {
        using SKBitmap frame = Render(showImagery: true, brightness: 100);

        (int x, int y) = OnlyImagePixel();
        Assert.Equal(ImageColour, frame.GetPixel(x, y));
        // The 1.4 px antialiased line may straddle two rows; the image (green 0) drawn over it would leave no green.
        int row = LineRow();
        int lineGreen = Enumerable.Range(row - 2, 5).Max(y => (int)frame.GetPixel(Size / 2, y).Green);
        Assert.True(lineGreen >= TaxiwayColour.Green / 2, $"the line's greenest pixel has green {lineGreen}");
    }

    private static int LineRow()
    {
        (_, float y) = Viewport().LatLonToScreen(MidLat, -122.22);
        return (int)MathF.Round(y);
    }

    [AvaloniaFact]
    public void ImageryOff_DrawsTheBackgroundWhereTheImageWouldBe()
    {
        using SKBitmap on = Render(showImagery: true, brightness: 100);
        using SKBitmap off = Render(showImagery: false, brightness: 100);

        (int x, int y) = OnlyImagePixel();
        Assert.NotEqual(ImageColour, off.GetPixel(x, y));
        Assert.Equal(off.GetPixel(Size - 1, Size - 1), off.GetPixel(x, y));
    }

    [AvaloniaFact]
    public void HalfBrightness_BlendsTheImageWithTheBackground()
    {
        using SKBitmap off = Render(showImagery: false, brightness: 50);
        using SKBitmap half = Render(showImagery: true, brightness: 50);

        (int x, int y) = OnlyImagePixel();
        SKColor background = off.GetPixel(x, y);
        SKColor blended = half.GetPixel(x, y);
        int red = (255 + background.Red) / 2;
        int green = background.Green / 2;
        Assert.InRange((int)blended.Red, red - 3, red + 3);
        Assert.InRange((int)blended.Green, green - 3, green + 3);
    }

    private static SKBitmap Render(bool showImagery, int brightness)
    {
        using var source = new SKBitmap(16, 16);
        source.Erase(ImageColour);
        var image = new TowerCabImage(SKImage.FromBitmap(source), MinLat, MinLon, MaxLat, MaxLon);
        AirportEditorScene scene = new(
            [new AirportEditorShape(0, AirportEditorFeatureKind.Taxiway, [new MapPosition(-122.24, MidLat), new MapPosition(-122.20, MidLat)], null)],
            new AirportEditorBounds(MinLat, MaxLat, MinLon, MaxLon)
        );
        var snapshot = new AirportEditorRenderSnapshot(scene, AirportEditorCanvas.ModeText, null, null, null)
        {
            Image = image,
            ShowImagery = showImagery,
            ImageryBrightness = brightness,
        };

        var bitmap = new SKBitmap(Size, Size);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new AirportEditorRenderer();
        renderer.Render(canvas, Viewport(), snapshot);
        canvas.Flush();
        return bitmap;
    }

    private static MapViewport Viewport()
    {
        var viewport = new MapViewport { PixelWidth = Size, PixelHeight = Size };
        viewport.FitBounds(MinLat, MaxLat, MinLon, MaxLon);
        return viewport;
    }

    // Inside the image, a quarter of its height above the line and clear of its west end: no feature there.
    private static (int X, int Y) OnlyImagePixel()
    {
        (float x, float y) = Viewport().LatLonToScreen(MinLat + ((MaxLat - MinLat) * 0.75), MinLon + ((MaxLon - MinLon) * 0.5));
        return ((int)x, (int)y);
    }
}
