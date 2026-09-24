using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// The push-route draw gestures on the ground canvas, driven through the headless mouse: Shift+click places a marked
// point where the press lands (never the node under it), Shift+drag also gives it a facing toward the release,
// Shift+right-click places one and finishes, and a plain right-click reports the point markers under the pointer.
public class GroundCanvasPushDrawGestureTests
{
    private const double NodeALat = 37.62;
    private const double NodeALon = -122.39;
    private const double NodeBLat = 37.70;
    private const double NodeBLon = -122.30;

    [AvaloniaFact]
    public void ShiftClick_OverANode_PlacesAMarkedPointAtThePress_NotTheNode()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            var placed = new List<(LatLon Point, LatLon? DragTo, bool Finish)>();
            var nodeClicks = new List<int>();
            canvas.DrawFreePointPlaced += (point, dragTo, finish) => placed.Add((point, dragTo, finish));
            canvas.DrawNodeClicked += nodeClicks.Add;
            Point press = CanvasPointNearNodeA(canvas);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseUp(ToWindow(canvas, window, press), MouseButton.Left, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();

            (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)press.X, (float)press.Y);
            (LatLon point, LatLon? dragTo, bool finish) = Assert.Single(placed);
            Assert.Equal(lat, point.Lat, 9);
            Assert.Equal(lon, point.Lon, 9);
            Assert.Null(dragTo);
            Assert.False(finish);
            Assert.Empty(nodeClicks);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ShiftDrag_GivesTheMarkedPointAFacingTowardTheRelease()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            var placed = new List<(LatLon Point, LatLon? DragTo, bool Finish)>();
            canvas.DrawFreePointPlaced += (point, dragTo, finish) => placed.Add((point, dragTo, finish));
            Point press = CanvasPointNearNodeA(canvas);
            var release = new Point(press.X + 40, press.Y);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(ToWindow(canvas, window, release), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            window.MouseUp(ToWindow(canvas, window, release), MouseButton.Left, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();

            (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)release.X, (float)release.Y);
            (_, LatLon? dragTo, bool finish) = Assert.Single(placed);
            Assert.NotNull(dragTo);
            Assert.Equal(lat, dragTo.Value.Lat, 9);
            Assert.Equal(lon, dragTo.Value.Lon, 9);
            Assert.False(finish);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ShiftRightClick_PlacesAMarkedPointAndFinishes()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            var placed = new List<(LatLon Point, LatLon? DragTo, bool Finish)>();
            int rightClicks = 0;
            canvas.DrawFreePointPlaced += (point, dragTo, finish) => placed.Add((point, dragTo, finish));
            canvas.PushRouteRightClicked += (_, _) => rightClicks++;
            Point press = CanvasPointNearNodeA(canvas);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Right, RawInputModifiers.Shift);
            window.MouseUp(ToWindow(canvas, window, press), MouseButton.Right, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();

            Assert.True(Assert.Single(placed).Finish);
            Assert.Equal(0, rightClicks);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void RightClick_OnAMarker_ReportsItsIndexAndTheNode()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            var reports = new List<(IReadOnlyList<int> Hits, int? NodeId)>();
            canvas.PushRouteRightClicked += (hits, nodeId) => reports.Add((hits, nodeId));
            (float bx, float by) = canvas.Viewport.LatLonToScreen(NodeBLat, NodeBLon);
            var click = new Point(bx - 3, by + 3);

            window.MouseDown(ToWindow(canvas, window, click), MouseButton.Right);
            window.MouseUp(ToWindow(canvas, window, click), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            (IReadOnlyList<int> hits, int? nodeId) = Assert.Single(reports);
            Assert.Equal([1], hits);
            Assert.Equal(2, nodeId);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // A push route being drawn from node A to node B: the markers are published, which is what puts the canvas in
    // push-draw mode.
    private static (GroundCanvas Canvas, Window Window) ShowPushDrawCanvas()
    {
        var canvas = new GroundCanvas();
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

        canvas.Layout = new GroundLayoutDto(
            "SFO",
            [
                new GroundNodeDto(1, NodeALat, NodeALon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, NodeBLat, NodeBLon, "TaxiwayIntersection", null, null, null),
            ],
            [],
            null,
            null,
            null
        );
        canvas.IsDrawingRoute = true;
        canvas.DrawWaypoints = [1, 2];
        canvas.PushWaypointMarks =
        [
            new PushWaypointMark(new LatLon(NodeALat, NodeALon), null, null),
            new PushWaypointMark(new LatLon(NodeBLat, NodeBLon), null, null),
        ];
        Dispatcher.UIThread.RunJobs();
        return (canvas, window);
    }

    // ~7 px up-right of node A (the fitted layout's south-west corner, at the canvas's lower-left edge): inside the
    // canvas, and inside node A's 20 px hit radius, so a plain click there would pick the node.
    private static Point CanvasPointNearNodeA(GroundCanvas canvas)
    {
        (float ax, float ay) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon);
        return new Point(ax + 5, ay - 5);
    }

    private static Point ToWindow(GroundCanvas canvas, Window window, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }
}
