using SkiaSharp;

namespace Yaat.Client.Services.Imagery;

/// <summary>
/// Decodes a tile's JPEG into a raster <see cref="SKImage"/> on the calling (fetch) thread, never lazily on the render
/// thread, and turns down a tile of one uniform colour: the service's answer where it has no imagery.
/// </summary>
internal static class UsgsTileDecoder
{
    /// <summary>The decoded tile, or null when the bytes are no image or the image is one uniform colour.</summary>
    public static SKImage? Decode(byte[] bytes)
    {
        // The codec first: it answers null for bytes that are no image, where a bare Decode of them can throw.
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        using var bitmap = SKBitmap.Decode(codec);
        if ((bitmap is null) || (IsUniform(bitmap)))
        {
            return null;
        }

        // FromBitmap snapshots the decoded pixels into an immutable raster image, as TowerCabImageService does.
        return SKImage.FromBitmap(bitmap);
    }

    private static bool IsUniform(SKBitmap bitmap)
    {
        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
        int pixelBytes = bitmap.BytesPerPixel;
        int rowLength = bitmap.Width * pixelBytes;
        if ((pixelBytes == 0) || (pixels.Length < pixelBytes))
        {
            return true;
        }

        ReadOnlySpan<byte> first = pixels[..pixelBytes];
        for (int y = 0; y < bitmap.Height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * bitmap.RowBytes, rowLength);
            for (int x = 0; x < rowLength; x += pixelBytes)
            {
                if (!row.Slice(x, pixelBytes).SequenceEqual(first))
                {
                    return false;
                }
            }
        }
        return true;
    }
}
