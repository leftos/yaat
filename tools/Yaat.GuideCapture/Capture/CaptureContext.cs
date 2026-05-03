namespace Yaat.GuideCapture.Capture;

// Per-run shared state. Carries the in-process server URL (Phase B) and
// will grow with scene action helpers (Phase C — load scenario, select
// aircraft, open flyout).
internal sealed class CaptureContext
{
    public required string ServerUrl { get; init; }
}
