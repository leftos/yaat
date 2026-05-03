using Avalonia.Controls;

namespace Yaat.GuideCapture.Capture;

// One screenshot. Subclasses build the target window and optionally seed live
// state (App.AutoConnectTarget for autoconnect, scenario load via SignalR,
// flyout open) by overriding BeforeWindowAsync / AfterShowAsync.
internal abstract class Scene
{
    public abstract string Name { get; }

    public virtual int Width => 1600;

    public virtual int Height => 1000;

    public virtual TimeSpan SettleAfterShow => TimeSpan.FromMilliseconds(250);

    // Runs on the UI thread BEFORE CreateWindow. Use it to set process-wide
    // state the window constructor will read (e.g. App.AutoConnectTarget).
    public virtual Task BeforeWindowAsync(CaptureContext ctx) => Task.CompletedTask;

    public abstract Window CreateWindow(CaptureContext ctx);

    // Runs on the UI thread AFTER the window is shown and laid out, before the
    // settle delay and capture. Use it to wait for connection state, scenario
    // load completion, etc.
    public virtual Task AfterShowAsync(Window window, CaptureContext ctx) => Task.CompletedTask;

    // Override to capture a different window than the one CreateWindow returned
    // (e.g. a popped-out child window that the primary spawned in response to
    // a state change in AfterShowAsync).
    public virtual Window GetCaptureTarget(Window primary) => primary;
}
