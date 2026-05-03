using Avalonia.Controls;

namespace Yaat.GuideCapture.Capture;

// One screenshot. Subclasses build the target window and optionally seed live
// state (scenario load, aircraft selection, flyout open) via SetupAsync before
// the runner shows the window and calls CaptureRenderedFrame.
internal abstract class Scene
{
    public abstract string Name { get; }

    public virtual int Width => 1600;

    public virtual int Height => 1000;

    public virtual TimeSpan SettleAfterShow => TimeSpan.FromMilliseconds(250);

    public abstract Window CreateWindow(CaptureContext ctx);

    public virtual Task SetupAsync(CaptureContext ctx) => Task.CompletedTask;
}
