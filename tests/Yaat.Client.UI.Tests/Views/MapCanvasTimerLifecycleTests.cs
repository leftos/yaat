using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

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

    // The canvas repaints on its datablock state's offset changes only while it is in the visual tree, so a view-model state
    // never roots a canvas a pop-out or dock-back abandoned.
    [AvaloniaFact]
    public void DataBlockStateSubscription_FollowsAttachBindingAndDetach()
    {
        var canvas = new RadarCanvas();
        Assert.Null(canvas.WatchedDataBlockState);

        var window = new Window { Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        RadarDataBlockViewState? local = canvas.WatchedDataBlockState;
        Assert.NotNull(local);

        var vmState = new RadarDataBlockViewState();
        canvas.DataBlockState = vmState;
        Assert.Same(vmState, canvas.WatchedDataBlockState);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(canvas.WatchedDataBlockState);
    }

    // The ground canvas subscribes its highlight state the same way, so a dock-back never leaves an abandoned canvas
    // listening to a view-model state.
    [AvaloniaFact]
    public void GroundCanvas_DataBlockStateSubscription_FollowsAttachBindingAndDetach()
    {
        var canvas = new GroundCanvas();
        Assert.Null(canvas.WatchedDataBlockState);

        var window = new Window { Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        GroundDataBlockViewState? local = canvas.WatchedDataBlockState;
        Assert.NotNull(local);

        var vmState = new GroundDataBlockViewState();
        canvas.DataBlockState = vmState;
        Assert.Same(vmState, canvas.WatchedDataBlockState);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(canvas.WatchedDataBlockState);
    }

    [AvaloniaFact]
    public void ScopeCanvas_RepaintTimer_StartsOnAttachAndStopsOnDetach()
    {
        var canvas = new ScopeCanvas();
        Assert.False(canvas.IsRepaintTimerRunning, "a canvas outside the visual tree must not be repainting");

        var window = new Window { Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(canvas.IsRepaintTimerRunning);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.False(canvas.IsRepaintTimerRunning, "a detached canvas must stop repainting so it can be collected");
        window.Close();
    }

    // The scope canvas watches its traffic collection only while in the visual tree, so a closed scope is never rooted by
    // the main view model's aircraft list.
    [AvaloniaFact]
    public void ScopeCanvas_Detached_StopsWatchingTheTrafficCollection()
    {
        var traffic = new ObservableCollection<AircraftModel>();
        var canvas = new ScopeCanvas { Aircraft = traffic };
        Assert.Null(canvas.WatchedAircraft);

        var window = new Window { Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(traffic, canvas.WatchedAircraft);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(canvas.WatchedAircraft);
        window.Close();
    }

    // A traffic list bound after the canvas is attached is watched too: an aircraft added to it shows in the kept frame
    // the next click hit-tests, without waiting for a repaint.
    [AvaloniaFact]
    public void ScopeCanvas_AircraftBoundAfterAttach_IsWatched()
    {
        var canvas = new ScopeCanvas();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(canvas.WatchedAircraft);

        var traffic = new ObservableCollection<AircraftModel>();
        canvas.Aircraft = traffic;
        Assert.Same(traffic, canvas.WatchedAircraft);
        Assert.Empty(canvas.CurrentFrame.Tracks);

        (double lat, double lon) = canvas.Viewport.ScreenToLatLon(300, 300);
        traffic.Add(
            new AircraftModel
            {
                Callsign = "UAL238",
                AircraftType = "B738",
                FlightRules = "IFR",
                Altitude = 5000,
                GroundSpeed = 250,
                Position = new LatLon(lat, lon),
            }
        );

        Assert.Equal("UAL238", Assert.Single(canvas.CurrentFrame.Tracks).Aircraft.Callsign);
        window.Close();
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
