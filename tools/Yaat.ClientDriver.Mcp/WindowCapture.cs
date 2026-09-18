using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ModelContextProtocol;

namespace Yaat.ClientDriver.Mcp;

/// <summary>A screen grab that has been written to disk and encoded for the agent.</summary>
/// <param name="Path">Where the PNG was saved.</param>
/// <param name="SourceWidth">Width of the captured screen region, in physical pixels.</param>
/// <param name="SourceHeight">Height of the captured screen region, in physical pixels.</param>
/// <param name="Width">Width of the returned image after downscaling.</param>
/// <param name="Height">Height of the returned image after downscaling.</param>
/// <param name="Png">The returned image, PNG-encoded.</param>
internal sealed record CaptureResult(string Path, int SourceWidth, int SourceHeight, int Width, int Height, byte[] Png);

/// <summary>Copies a screen rectangle to a PNG — the only read path into surfaces UI Automation cannot see, such as CRC's scopes.</summary>
internal static class WindowCapture
{
    internal static CaptureResult Capture(System.Windows.Rect rect, int maxWidth, string directory)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.X) || (rect.Width < 1) || (rect.Height < 1))
        {
            throw new McpException("The element has no on-screen area — it is minimised, collapsed or offscreen. Restore the window and try again");
        }

        int sourceWidth = (int)Math.Round(rect.Width);
        int sourceHeight = (int)Math.Round(rect.Height);
        using Bitmap source = Grab((int)Math.Round(rect.X), (int)Math.Round(rect.Y), sourceWidth, sourceHeight);

        int width = sourceWidth;
        int height = sourceHeight;
        if ((maxWidth > 0) && (sourceWidth > maxWidth))
        {
            width = maxWidth;
            height = Math.Max(1, (int)Math.Round(sourceHeight * (maxWidth / (double)sourceWidth)));
        }

        using Bitmap output = Resize(source, width, height);
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        using MemoryStream buffer = new();
        output.Save(buffer, ImageFormat.Png);
        byte[] png = buffer.ToArray();
        File.WriteAllBytes(path, png);
        return new CaptureResult(path, sourceWidth, sourceHeight, width, height, png);
    }

    private static Bitmap Grab(int x, int y, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
