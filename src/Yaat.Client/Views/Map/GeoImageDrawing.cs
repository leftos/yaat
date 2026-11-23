using SkiaSharp;
using Yaat.Client.Services;
using Yaat.Sim.Authoring.Imagery;

namespace Yaat.Client.Views.Map;

/// <summary>
/// Draws a geo-referenced image (a vNAS tower-cab image) under a map: its corners projected through the viewport, so a
/// rotated view rotates the image too. The Ground View and the Airport Editor both draw through it.
/// </summary>
internal static class GeoImageDrawing
{
    /// <summary>Draws the image at its lat/lon box, white-tinted at the brightness's alpha.</summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="vp">The view, copied for this frame.</param>
    /// <param name="image">The image and its box.</param>
    /// <param name="brightness">The image's opacity, 0 to 100.</param>
    public static void Draw(SKCanvas canvas, MapViewport vp, TowerCabImage image, int brightness) =>
        Draw(canvas, vp, image.Image, new TileBounds(image.BottomLeftLat, image.BottomLeftLon, image.TopRightLat, image.TopRightLon), brightness);

    /// <summary>Draws an image at a lat/lon box, white-tinted at the brightness's alpha.</summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="vp">The view, copied for this frame.</param>
    /// <param name="image">The image: its top edge is the box's north edge.</param>
    /// <param name="box">The image's corners as a south/west/north/east box.</param>
    /// <param name="brightness">The image's opacity, 0 to 100.</param>
    public static void Draw(SKCanvas canvas, MapViewport vp, SKImage image, TileBounds box, int brightness)
    {
        // Project all 4 corners through the viewport (handles rotation)
        (float blX, float blY) = vp.LatLonToScreen(box.South, box.West);
        (float trX, float trY) = vp.LatLonToScreen(box.North, box.East);
        (float tlX, float tlY) = vp.LatLonToScreen(box.North, box.West);

        // Source: image rectangle
        var srcRect = new SKRect(0, 0, image.Width, image.Height);

        // Destination: the 4 projected screen corners
        // Image top-left → screen top-left, top-right → screen top-right, etc.
        // Note: image Y=0 is top, geo top-right lat is the "top" of the image
        SKMatrix matrix = ComputeBitmapTransform(srcRect, new SKPoint(tlX, tlY), new SKPoint(trX, trY), new SKPoint(blX, blY));

        byte alpha = (byte)Math.Clamp(brightness * 255 / 100, 0, 255);
        // Linear filtering with a linear mipmap chain. Mipmaps let Skia build a downscale chain on
        // the GPU once and reuse it; without them, sampling an 8K×8K tower-cab image at typical
        // airport zoom walked the full source per output pixel and ate ~30% GPU. Combined with the
        // immutable SKImage on TowerCabImage, the GPU texture and its mip chain stay cached across
        // redraws.
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        canvas.Save();
        canvas.Concat(in matrix);
        canvas.DrawImage(image, 0, 0, sampling, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Computes an affine matrix mapping 3 source points to 3 destination points.
    /// Maps: src(0,0)→dstTL, src(w,0)→dstTR, src(0,h)→dstBL.
    /// Solves the 2x3 affine system directly.
    /// </summary>
    private static SKMatrix ComputeBitmapTransform(SKRect src, SKPoint dstTL, SKPoint dstTR, SKPoint dstBL)
    {
        float sx0 = src.Left,
            sy0 = src.Top;
        float sx1 = src.Right,
            sy1 = src.Top;
        float sx2 = src.Left,
            sy2 = src.Bottom;

        // Solve: dst = A * src + T for affine coefficients
        // Using 3 point pairs: (sx0,sy0)→dstTL, (sx1,sy1)→dstTR, (sx2,sy2)→dstBL
        float dx1 = sx1 - sx0,
            dy1 = sy1 - sy0;
        float dx2 = sx2 - sx0,
            dy2 = sy2 - sy0;
        float det = (dx1 * dy2) - (dx2 * dy1);

        if (MathF.Abs(det) < 1e-10f)
        {
            return SKMatrix.CreateTranslation(dstTL.X, dstTL.Y);
        }

        float invDet = 1f / det;

        float ddx1 = dstTR.X - dstTL.X,
            ddy1 = dstTR.Y - dstTL.Y;
        float ddx2 = dstBL.X - dstTL.X,
            ddy2 = dstBL.Y - dstTL.Y;

        float scaleX = ((ddx1 * dy2) - (ddx2 * dy1)) * invDet;
        float skewX = ((ddx2 * dx1) - (ddx1 * dx2)) * invDet;
        float skewY = ((ddy1 * dy2) - (ddy2 * dy1)) * invDet;
        float scaleY = ((ddy2 * dx1) - (ddy1 * dx2)) * invDet;
        float transX = dstTL.X - (scaleX * sx0) - (skewX * sy0);
        float transY = dstTL.Y - (skewY * sx0) - (scaleY * sy0);

        return new SKMatrix(scaleX, skewX, transX, skewY, scaleY, transY, 0, 0, 1);
    }
}
