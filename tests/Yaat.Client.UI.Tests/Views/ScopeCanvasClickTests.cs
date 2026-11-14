using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// The scope's clicks: a left click on a block selects its aircraft app-wide, as the radar's does, and a right
// click on a target opens the shared aircraft menu. Modelled on RadarCanvasRightClickTargetTests.
public class ScopeCanvasClickTests
{
    [AvaloniaFact]
    public void ScopeCanvas_LeftClickOnBlock_SelectsTheAircraft()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);
            ScopeTrack track = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            FieldRect callsign = track.Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign);

            Click(window, view.Canvas, new Point(callsign.Rect.MidX, callsign.Rect.MidY), MouseButton.Left);

            Assert.Same(ac, main.SelectedAircraft);
            Assert.Same(ac, scope.SelectedAircraft);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_MvaHintsTurnedOn_TintsTheAltitudeWithoutATrafficChange()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            var ac = new AircraftModel
            {
                Callsign = "UAL238",
                AircraftType = "B738",
                FiledAircraftType = "B738",
                FlightRules = "IFR",
                TransponderMode = "C",
                Altitude = 1800,
                GroundSpeed = 250,
                Position = new LatLon(37.7213, -122.2208),
            };
            main.Aircraft.Add(ac);
            Dispatcher.UIThread.RunJobs();
            Assert.False(scope.ShowMvaHints);
            Assert.Equal(ScopeColorToken.Default, AltitudeToken(view.Canvas.CurrentFrame));

            scope.ShowMvaHints = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ScopeColorToken.MvaBelow, AltitudeToken(view.Canvas.CurrentFrame));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ScopeCanvas_RightClickOnTarget_OpensTheAircraftMenu()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", 300, 300);

            Click(window, view.Canvas, new Point(300, 300), MouseButton.Right);

            ContextMenu? menu = view.ActiveContextMenu;
            Assert.NotNull(menu);
            Assert.True(menu!.IsOpen);
            Assert.NotEmpty(menu.Items);
            // Nothing was selected, so the right-clicked aircraft becomes the selection, as on the radar.
            Assert.Same(ac, main.SelectedAircraft);
            Assert.Same(ac, scope.SelectedAircraft);
            menu.Close();
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window) ShowScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main);
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    // An airborne aircraft whose symbol sits at the given canvas point, added to the traffic the scope draws.
    private static AircraftModel AddAircraft(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        var ac = new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static ScopeColorToken AltitudeToken(ScopeFrame frame) =>
        Assert.Single(Assert.Single(frame.Tracks).Block.Fields, f => f.Slot == ScopeFieldSlot.Altitude).Token;

    private static void Click(Window window, ScopeCanvas canvas, Point canvasPoint, MouseButton button)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, button);
        window.MouseUp(inWindow.Value, button);
        Dispatcher.UIThread.RunJobs();
    }
}
