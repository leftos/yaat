using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// A radar right-click gathers every aircraft whose datablock or symbol it lands on; two or more open a picker instead
// of the nearest aircraft's menu, so overlapping targets stay reachable. One aircraft keeps today's direct menu.
public class RadarCanvasRightClickTargetTests
{
    [AvaloniaFact]
    public void RadarRightClick_TwoOverlappingAircraft_OffersBoth()
    {
        (RadarCanvas canvas, Window window) = ShowCanvas();
        try
        {
            var click = new Point(300, 300);
            canvas.Aircraft =
            [
                MakeAircraft(canvas, "UAL238", "B738", click.X + 4, click.Y),
                MakeAircraft(canvas, "SWA12", "B737", click.X - 6, click.Y),
            ];
            var raised = new List<string>();
            canvas.AircraftRightClicked += (cs, _) => raised.Add(cs);

            Assert.Equal(["UAL238", "SWA12"], canvas.FindRightClickTargets(click).Select(t => t.Callsign));

            RightClick(window, canvas, click);

            ContextMenu? picker = canvas.ActiveRightClickPicker;
            Assert.NotNull(picker);
            Assert.Equal(["UAL238 (B738)", "SWA12 (B737)"], picker!.Items.OfType<MenuItem>().Select(i => i.Header as string));
            Assert.Empty(raised);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void RadarRightClick_SingleAircraft_RaisesAircraftRightClickedDirectly()
    {
        (RadarCanvas canvas, Window window) = ShowCanvas();
        try
        {
            var click = new Point(300, 300);
            canvas.Aircraft = [MakeAircraft(canvas, "UAL238", "B738", click.X + 4, click.Y)];
            var raised = new List<(string Callsign, Point Pos)>();
            canvas.AircraftRightClicked += (cs, pos) => raised.Add((cs, pos));

            RightClick(window, canvas, click);

            Assert.Equal([("UAL238", click)], raised);
            Assert.Null(canvas.ActiveRightClickPicker);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void RadarRightClick_ChoosingADataBlockEntry_RaisesItAtTheClickAndSurfacesItsDataBlock()
    {
        (RadarCanvas canvas, Window window) = ShowCanvas();
        try
        {
            var state = new RadarDataBlockViewState();
            canvas.DataBlockState = state;
            AircraftModel blockOwner = MakeAircraft(canvas, "SWA12", "B737", 300, 300);
            canvas.Aircraft = [blockOwner];
            Point click = DataBlockPointOf(canvas, blockOwner);
            AircraftModel nearSymbol = MakeAircraft(canvas, "UAL238", "B738", click.X + 2, click.Y);
            canvas.Aircraft = [blockOwner, nearSymbol];
            Assert.Equal([("SWA12", true), ("UAL238", false)], canvas.FindRightClickTargets(click).Select(t => (t.Callsign, t.ViaDataBlock)));
            var raised = new List<(string Callsign, Point Pos)>();
            canvas.AircraftRightClicked += (cs, pos) => raised.Add((cs, pos));

            RightClick(window, canvas, click);
            ContextMenu? picker = canvas.ActiveRightClickPicker;
            Assert.NotNull(picker);
            Assert.False(state.DataBlockZOrder.ContainsKey("SWA12"));
            MenuItem entry = picker!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "SWA12 (B737)"));
            entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([("SWA12", click)], raised);
            Assert.True(state.DataBlockZOrder.ContainsKey("SWA12"), "choosing a datablock hit must bring its datablock to the front");
            Assert.False(state.DataBlockZOrder.ContainsKey("UAL238"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // A point on the aircraft's datablock, found by hit-testing the block where the canvas places it.
    private static Point DataBlockPointOf(RadarCanvas canvas, AircraftModel aircraft)
    {
        (float x, float y) = canvas.Viewport.LatLonToScreen(aircraft.Position.Lat, aircraft.Position.Lon);
        for (int dy = -80; dy <= 40; dy += 2)
        {
            for (int dx = -40; dx <= 160; dx += 2)
            {
                var point = new Point(x + dx, y + dy);
                if (canvas.FindDataBlockAtPoint(point) == aircraft)
                {
                    return point;
                }
            }
        }

        Assert.Fail($"no point on {aircraft.Callsign}'s datablock near its symbol");
        return default;
    }

    private static (RadarCanvas Canvas, Window Window) ShowCanvas()
    {
        var canvas = new RadarCanvas();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (canvas, window);
    }

    // An airborne aircraft whose symbol sits at the given canvas point.
    private static AircraftModel MakeAircraft(RadarCanvas canvas, string callsign, string type, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        return new AircraftModel
        {
            Callsign = callsign,
            AircraftType = type,
            FiledAircraftType = type,
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };
    }

    private static void RightClick(Window window, RadarCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Right);
        window.MouseUp(inWindow.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }
}
