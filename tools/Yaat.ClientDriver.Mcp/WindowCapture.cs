using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ModelContextProtocol;

namespace Yaat.ClientDriver.Mcp;

/// <summary>A screen grab that has been written to disk and encoded for the agent.</summary>
/// <param name="Path">Where the PNG was saved.</param>
/// <param name="SourceWidth">Width of the captured region, in physical pixels.</param>
/// <param name="SourceHeight">Height of the captured region, in physical pixels.</param>
/// <param name="Width">Width of the returned image after downscaling.</param>
/// <param name="Height">Height of the returned image after downscaling.</param>
/// <param name="Png">The returned image, PNG-encoded.</param>
/// <param name="Source">Where the pixels came from: the window's own content, or whatever the screen showed at its rectangle.</param>
internal sealed record CaptureResult(string Path, int SourceWidth, int SourceHeight, int Width, int Height, byte[] Png, string Source);

/// <summary>
/// Captures an element to a PNG — the only read path into surfaces UI Automation cannot see, such as CRC's scopes. A YAAT window
/// is asked to render itself (PrintWindow), so a covered window still captures correctly; anything else is copied off the screen.
/// </summary>
internal static partial class WindowCapture
{
    private const uint PrintWindowRenderFullContent = 0x00000002;

    /// <summary>Copies the screen pixels at the rectangle: whatever is on top there is what gets captured.</summary>
    internal static CaptureResult Capture(System.Windows.Rect rect, int maxWidth, string directory)
    {
        EnsureArea(rect);
        int sourceWidth = (int)Math.Round(rect.Width);
        int sourceHeight = (int)Math.Round(rect.Height);
        using Bitmap source = Grab((int)Math.Round(rect.X), (int)Math.Round(rect.Y), sourceWidth, sourceHeight);
        return Encode(source, maxWidth, directory, "screen pixels");
    }

    /// <summary>Has the window render itself through PrintWindow and crops the element's rectangle out of it.</summary>
    internal static CaptureResult CaptureWindow(nint windowHandle, System.Windows.Rect elementRect, int maxWidth, string directory)
    {
        EnsureArea(elementRect);
        if (!GetWindowRect(windowHandle, out Win32Rect window))
        {
            throw new McpException($"GetWindowRect failed for window 0x{windowHandle:X} — it has closed; find the element again");
        }

        int windowWidth = window.Right - window.Left;
        int windowHeight = window.Bottom - window.Top;
        var crop = Rectangle.Intersect(
            new Rectangle(
                (int)Math.Round(elementRect.X) - window.Left,
                (int)Math.Round(elementRect.Y) - window.Top,
                (int)Math.Round(elementRect.Width),
                (int)Math.Round(elementRect.Height)
            ),
            new Rectangle(0, 0, windowWidth, windowHeight)
        );
        if (crop.IsEmpty)
        {
            throw new McpException("The element lies outside its window's rectangle — scroll it into view and try again");
        }

        using Bitmap whole = Render(windowHandle, windowWidth, windowHeight);
        using Bitmap source = whole.Clone(crop, whole.PixelFormat);
        return Encode(source, maxWidth, directory, "the window's own content (PrintWindow)");
    }

    internal static void EnsureArea(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.X) || (rect.Width < 1) || (rect.Height < 1))
        {
            throw new McpException("The element has no on-screen area — it is minimised, collapsed or offscreen. Restore the window and try again");
        }
    }

    private static Bitmap Render(nint windowHandle, int width, int height)
    {
        // 32bppRgb, not Argb: GDI leaves the alpha byte zero, which an Argb bitmap would encode as a fully transparent PNG.
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            nint deviceContext = graphics.GetHdc();
            try
            {
                if (!PrintWindow(windowHandle, deviceContext, PrintWindowRenderFullContent))
                {
                    throw new McpException($"PrintWindow failed for window 0x{windowHandle:X} — it may have closed; find the element again");
                }
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static CaptureResult Encode(Bitmap source, int maxWidth, string directory, string sourceDescription)
    {
        int width = source.Width;
        int height = source.Height;
        if ((maxWidth > 0) && (source.Width > maxWidth))
        {
            width = maxWidth;
            height = Math.Max(1, (int)Math.Round(source.Height * (maxWidth / (double)source.Width)));
        }

        using Bitmap output = Resize(source, width, height);
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        using MemoryStream buffer = new();
        output.Save(buffer, ImageFormat.Png);
        byte[] png = buffer.ToArray();
        File.WriteAllBytes(path, png);
        return new CaptureResult(path, source.Width, source.Height, width, height, png, sourceDescription);
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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint windowHandle, nint deviceContext, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint windowHandle, out Win32Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
