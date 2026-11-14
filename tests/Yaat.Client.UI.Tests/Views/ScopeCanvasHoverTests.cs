using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// The scope's hover dwell, as CRC's: a pointer move onto a block dwells it at once, the next move off it or the pointer
// leaving the canvas releases it, and the kept frame is rebuilt only when the hovered callsign changes.
public class ScopeCanvasHoverTests
{
    private const double SideWidth = 100;

    [AvaloniaFact]
    public void ScopeCanvas_PointerMoves_DwellAndReleaseTheBlock_RebuildingOnlyOnAChange()
    {
        (MainViewModel main, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            ScopeTrack track = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            FieldRect callsign = track.Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign);
            Assert.Equal(ScopeBlockLevel.Full, track.Level);

            Move(window, view.Canvas, new Point(callsign.Rect.MidX, callsign.Rect.MidY));
            ScopeFrame dwelled = view.Canvas.CurrentFrame;
            Assert.Equal(ScopeBlockLevel.Dwell, Assert.Single(dwelled.Tracks).Level);

            // No forced render tick here, so only the move handler could replace the kept frame.
            MoveOnly(window, view.Canvas, new Point(callsign.Rect.MidX + 1, callsign.Rect.MidY));
            Assert.Same(dwelled, view.Canvas.CurrentFrame);

            Move(window, view.Canvas, new Point(600, 500));
            Assert.Equal(ScopeBlockLevel.Full, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_PanDrag_KeepsTheDwell_AndBuildsNoFrame()
    {
        (MainViewModel main, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            ScopeTrack track = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            // Inside the dwell box's 2 px margin but outside the block and the click radius: a right press there pans.
            var margin = new Point(track.DwellBox.Right - 1, track.DwellBox.Top + 1);
            Assert.False(track.Block.Bounds.Contains((float)margin.X, (float)margin.Y));
            Assert.True(SKPoint.Distance(track.Symbol, new SKPoint((float)margin.X, (float)margin.Y)) > ScopeFrame.TargetHitRadiusPx);

            Move(window, view.Canvas, margin);
            Assert.Equal("UAL238", view.Canvas.HoveredCallsign);
            Point? start = view.Canvas.TranslatePoint(margin, window);
            Assert.NotNull(start);
            window.MouseDown(start.Value, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.ActiveContextMenu);
            double centerLon = view.Canvas.Viewport.CenterLon;
            int built = view.Canvas.HoverFramesBuilt;

            window.MouseMove(start.Value + new Vector(40, 25), RawInputModifiers.RightMouseButton);
            Dispatcher.UIThread.RunJobs();

            Assert.NotEqual(centerLon, view.Canvas.Viewport.CenterLon);
            Assert.Equal("UAL238", view.Canvas.HoveredCallsign);
            // The pan dropped the kept frame; the move handler built none to re-test the dwell.
            Assert.Equal(built, view.Canvas.HoverFramesBuilt);
            window.MouseUp(start.Value + new Vector(40, 25), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_PointerExit_ReleasesTheDwell()
    {
        (MainViewModel main, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            Move(window, view.Canvas, new Point(300, 300));
            Assert.Equal(ScopeBlockLevel.Dwell, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);

            // The side panel lies outside the canvas, so this move leaves it.
            window.MouseMove(new Point(SideWidth / 2, 300));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ScopeBlockLevel.Full, Assert.Single(view.Canvas.CurrentFrame.Tracks).Level);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (MainViewModel Main, ScopeView View, Window Window) ShowScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
        var view = new ScopeView { DataContext = scope };
        var side = new Border { Width = SideWidth };
        DockPanel.SetDock(side, Dock.Left);
        var panel = new DockPanel();
        panel.Children.Add(side);
        panel.Children.Add(view);
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = panel,
        };
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, view, window);
    }

    // An airborne aircraft whose symbol sits at the given canvas point, added to the traffic the scope draws.
    private static void AddAircraft(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        main.Aircraft.Add(
            new AircraftModel
            {
                Callsign = callsign,
                AircraftType = "B738",
                FiledAircraftType = "B738",
                FlightRules = "IFR",
                TransponderMode = "C",
                Altitude = 5000,
                GroundSpeed = 250,
                Position = new LatLon(lat, lon),
            }
        );
        Dispatcher.UIThread.RunJobs();
    }

    // Moves the pointer to a canvas point and runs the queued jobs, with no render tick.
    private static void MoveOnly(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseMove(inWindow.Value);
        Dispatcher.UIThread.RunJobs();
    }

    // Moves the pointer to a canvas point and lets any repaint it asked for run, as the canvas's own would.
    private static void Move(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseMove(inWindow.Value);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
