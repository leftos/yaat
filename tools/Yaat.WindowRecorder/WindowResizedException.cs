using Windows.Graphics;

namespace Yaat.WindowRecorder;

/// <summary>Raised when a captured window changes size mid-run; the output size is fixed for the run, so the run stops.</summary>
internal sealed class WindowResizedException : Exception
{
    public WindowResizedException() { }

    public WindowResizedException(string message)
        : base(message) { }

    public WindowResizedException(string message, Exception innerException)
        : base(message, innerException) { }

    public WindowResizedException(SizeInt32 expected, SizeInt32 actual)
        : base($"the window resized from {expected.Width}x{expected.Height} to {actual.Width}x{actual.Height}") { }
}
