using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The 10 Hz repaint timer must live and die with the canvas's presence in the visual tree.
///
/// It used to be started in the constructor and held only in a constructor local, so nothing could stop
/// it. A running <c>DispatcherTimer</c> is rooted by the dispatcher and its Tick delegate roots the
/// canvas, so every Radar/Ground pop-out toggle abandoned a live canvas that kept calling
/// <c>InvalidateVisual()</c> ten times a second at render priority — forever, and compounding per
/// toggle. It also pinned the renderer's whole SKPaint/SKFont set, since a rooted object is never
/// finalized.
/// </summary>
public class MapCanvasTimerLifecycleTests
{
    [AvaloniaFact]
    public void RadarCanvas_RepaintTimer_StartsOnAttachAndStopsOnDetach()
    {
        var canvas = new RadarCanvas();
        Assert.False(canvas.IsRepaintTimerRunning, "a canvas outside the visual tree must not be repainting");

        var window = new Window { Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(canvas.IsRepaintTimerRunning);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.False(canvas.IsRepaintTimerRunning, "a detached canvas must stop repainting so it can be collected");
    }

    [AvaloniaFact]
    public void GroundCanvas_RepaintTimer_StopsOnDetach()
    {
        var canvas = new GroundCanvas();
        var window = new Window { Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(canvas.IsRepaintTimerRunning);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(canvas.IsRepaintTimerRunning);
    }
}
