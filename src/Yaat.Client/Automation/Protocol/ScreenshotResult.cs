namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// The result of <c>screenshot</c>: the PNG's pixel size (the target's DIP size times <paramref name="Scale"/>, rounded up), the
/// render scale of the target's window, and the PNG itself in base 64.
/// </summary>
public sealed record ScreenshotResult(int Width, int Height, double Scale, string PngBase64);
