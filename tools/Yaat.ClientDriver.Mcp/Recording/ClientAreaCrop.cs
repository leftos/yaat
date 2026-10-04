using System.Globalization;

namespace Yaat.ClientDriver.Mcp.Recording;

/// <summary>A rectangle in physical screen pixels, right and bottom exclusive.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">One past the right edge.</param>
/// <param name="Bottom">One past the bottom edge.</param>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>The width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>The height in pixels.</summary>
    public int Height => Bottom - Top;
}

/// <summary>What a recording needs to know of a window, in physical screen pixels.</summary>
/// <param name="Frame">The window's visible frame (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>), which is what the capture delivers.</param>
/// <param name="Client">The client area on the screen: <c>ClientToScreen</c> of its origin, sized by <c>GetClientRect</c>.</param>
/// <param name="IsMinimized">True for a minimized window, which has no surface to capture.</param>
public sealed record WindowGeometry(ScreenRect Frame, ScreenRect Client, bool IsMinimized);

/// <summary>The part of each captured frame a recording keeps, in frame pixels.</summary>
/// <param name="X">The left edge inside the frame.</param>
/// <param name="Y">The top edge inside the frame.</param>
/// <param name="Width">The kept width, even.</param>
/// <param name="Height">The kept height, even.</param>
public readonly record struct CropBox(int X, int Y, int Width, int Height)
{
    /// <summary>The ffmpeg filter that keeps this box: <c>crop=w:h:x:y</c>.</summary>
    public string Filter => string.Create(CultureInfo.InvariantCulture, $"crop={Width}:{Height}:{X}:{Y}");
}

/// <summary>
/// Keeps a recording to the window's client area: the capture delivers the whole frame, title bar included. Both rectangles
/// come from the same per-monitor-v2 aware process in physical pixels, so the offset holds at any DPI.
/// </summary>
public static class ClientAreaCrop
{
    /// <summary>
    /// The client area's box inside the frame. Its size is rounded down to even, since yuv420p needs even sides and growing it
    /// would take in a column or row of the frame.
    /// </summary>
    /// <param name="window">The window's frame and client area.</param>
    public static CropBox Compute(WindowGeometry window) =>
        new(
            Math.Max(0, window.Client.Left - window.Frame.Left),
            Math.Max(0, window.Client.Top - window.Frame.Top),
            RoundDownToEven(window.Client.Width),
            RoundDownToEven(window.Client.Height)
        );

    private static int RoundDownToEven(int value) => Math.Max(0, value) & ~1;
}
