using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
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

    // Midway between A and B, so it leaves the fitted view unchanged. Unmarked unless a test draws a route through it.
    private const double NodeCLat = 37.66;
    private const double NodeCLon = -122.345;

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
    public void ShiftDragPastTheThreshold_ShowsThePendingPointBeforeRelease()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            Point press = CanvasPointNearNodeA(canvas);
            var cursor = new Point(press.X + 40, press.Y);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(ToWindow(canvas, window, cursor), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            RenderFrame();

            // Only a repaint after the move can have drawn it: the frame drawn at the press had no pending point.
            Assert.NotNull(canvas.DrawnPendingPushPoint);
            PendingPushPoint pending = canvas.DrawnPendingPushPoint.Value;
            (double pressLat, double pressLon) = canvas.Viewport.ScreenToLatLon((float)press.X, (float)press.Y);
            (double cursorLat, double cursorLon) = canvas.Viewport.ScreenToLatLon((float)cursor.X, (float)cursor.Y);
            Assert.Equal(pressLat, pending.Point.Lat, 9);
            Assert.Equal(pressLon, pending.Point.Lon, 9);
            Assert.Equal(cursorLat, pending.Cursor.Lat, 9);
            Assert.Equal(cursorLon, pending.Cursor.Lon, 9);
            Assert.Equal(3, pending.Number);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ShiftDragBelowTheThreshold_ShowsNoPendingFacing()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            Point press = CanvasPointNearNodeA(canvas);
            var cursor = new Point(press.X + 3, press.Y);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(ToWindow(canvas, window, cursor), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            // A frame is drawn whatever the move repainted, so only the threshold can keep the pending point out of it.
            canvas.InvalidateVisual();
            RenderFrame();

            Assert.Null(canvas.DrawnPendingPushPoint);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ReleasingAShiftDrag_ClearsThePendingPoint()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            Point press = CanvasPointNearNodeA(canvas);
            var cursor = new Point(press.X + 40, press.Y);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(ToWindow(canvas, window, cursor), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            RenderFrame();
            Assert.NotNull(canvas.DrawnPendingPushPoint);

            window.MouseUp(ToWindow(canvas, window, cursor), MouseButton.Left, RawInputModifiers.Shift);
            RenderFrame();

            Assert.Null(canvas.DrawnPendingPushPoint);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LeftDragOnAnEarlierMarker_RaisesAMoveForThatIndex()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            ShowMarkersAtAThenCThenB(canvas);
            var moves = new List<(int Index, LatLon To)>();
            var nodeClicks = new List<int>();
            canvas.PushMarkerDragged += (index, to) => moves.Add((index, to));
            canvas.DrawNodeClicked += nodeClicks.Add;
            (float cx, float cy) = canvas.Viewport.LatLonToScreen(NodeCLat, NodeCLon);
            var press = new Point(cx + 3, cy);
            var release = new Point(cx + 40, cy + 10);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left);
            window.MouseMove(ToWindow(canvas, window, release), RawInputModifiers.LeftMouseButton);
            window.MouseUp(ToWindow(canvas, window, release), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)release.X, (float)release.Y);
            (int index, LatLon to) = Assert.Single(moves);
            Assert.Equal(1, index);
            Assert.Equal(lat, to.Lat, 9);
            Assert.Equal(lon, to.Lon, 9);
            Assert.Empty(nodeClicks);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LeftClickOnAMarkerWithoutDrag_ChangesNothing()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            ShowMarkersAtAThenCThenB(canvas);
            var moves = new List<(int Index, LatLon To)>();
            var nodeClicks = new List<int>();
            canvas.PushMarkerDragged += (index, to) => moves.Add((index, to));
            canvas.DrawNodeClicked += nodeClicks.Add;
            (float cx, float cy) = canvas.Viewport.LatLonToScreen(NodeCLat, NodeCLon);
            var click = new Point(cx + 3, cy);

            window.MouseDown(ToWindow(canvas, window, click), MouseButton.Left);
            window.MouseUp(ToWindow(canvas, window, click), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(moves);
            Assert.Empty(nodeClicks);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LeftClickOffAnyMarker_StillAddsTheNode()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            var moves = new List<(int Index, LatLon To)>();
            var nodeClicks = new List<int>();
            canvas.PushMarkerDragged += (index, to) => moves.Add((index, to));
            canvas.DrawNodeClicked += nodeClicks.Add;
            (float cx, float cy) = canvas.Viewport.LatLonToScreen(NodeCLat, NodeCLon);
            var click = new Point(cx + 3, cy);

            window.MouseDown(ToWindow(canvas, window, click), MouseButton.Left);
            window.MouseUp(ToWindow(canvas, window, click), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([3], nodeClicks);
            Assert.Empty(moves);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LeftDragOnAMarker_DrawsItAtTheCursorBeforeRelease()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            ShowMarkersAtAThenCThenB(canvas);
            (float cx, float cy) = canvas.Viewport.LatLonToScreen(NodeCLat, NodeCLon);
            var press = new Point(cx + 3, cy);
            var cursor = new Point(cx + 40, cy + 10);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left);
            window.MouseMove(ToWindow(canvas, window, cursor), RawInputModifiers.LeftMouseButton);
            RenderFrame();

            IReadOnlyList<PushWaypointMark> published = canvas.PushWaypointMarks!;
            IReadOnlyList<PushWaypointMark>? drawn = canvas.DrawnPushWaypointMarks;
            Assert.NotNull(drawn);
            (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)cursor.X, (float)cursor.Y);
            Assert.Equal(lat, drawn[1].Position.Lat, 9);
            Assert.Equal(lon, drawn[1].Position.Lon, 9);
            Assert.Equal(published[0], drawn[0]);
            Assert.Equal(published[2], drawn[2]);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void LeftDragOnOverlappingMarkers_MovesTheTopmost()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            canvas.DrawWaypoints = [1, 3, 3];
            canvas.PushWaypointMarks =
            [
                new PushWaypointMark(new LatLon(NodeALat, NodeALon), null, null),
                new PushWaypointMark(new LatLon(NodeCLat, NodeCLon), null, null),
                new PushWaypointMark(new LatLon(NodeCLat, NodeCLon), null, null),
            ];
            Dispatcher.UIThread.RunJobs();
            var moves = new List<(int Index, LatLon To)>();
            canvas.PushMarkerDragged += (index, to) => moves.Add((index, to));
            (float cx, float cy) = canvas.Viewport.LatLonToScreen(NodeCLat, NodeCLon);
            var press = new Point(cx + 3, cy);
            var release = new Point(cx + 40, cy + 10);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left);
            window.MouseMove(ToWindow(canvas, window, release), RawInputModifiers.LeftMouseButton);
            window.MouseUp(ToWindow(canvas, window, release), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, Assert.Single(moves).Index);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // The start's marker (index 0) is not a target: a press on it falls through to the node beneath.
    [AvaloniaFact]
    public void LeftClickOnTheStartMarker_AddsTheNode()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            var moves = new List<(int Index, LatLon To)>();
            var nodeClicks = new List<int>();
            canvas.PushMarkerDragged += (index, to) => moves.Add((index, to));
            canvas.DrawNodeClicked += nodeClicks.Add;
            Point click = CanvasPointNearNodeA(canvas);

            window.MouseDown(ToWindow(canvas, window, click), MouseButton.Left);
            window.MouseUp(ToWindow(canvas, window, click), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([1], nodeClicks);
            Assert.Empty(moves);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // In push-draw mode a marker wins over a datablock drawn over it: the press drags the marker, and neither selects the
    // aircraft nor drags its block.
    [AvaloniaFact]
    public void LeftPressOnAMarkerUnderADatablock_DragsTheMarker()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            var aircraft = new AircraftModel
            {
                Callsign = "UAL462",
                AircraftType = "B738",
                TransponderMode = "C",
                IsOnGround = true,
                Position = new LatLon(NodeCLat, NodeCLon),
            };
            canvas.Aircraft = [aircraft];
            canvas.InvalidateVisual();
            RenderFrame();
            Point onBlock = DataBlockPointOf(canvas, aircraft);
            (double blockLat, double blockLon) = canvas.Viewport.ScreenToLatLon((float)onBlock.X, (float)onBlock.Y);
            canvas.DrawWaypoints = [1, 3, 2];
            canvas.PushWaypointMarks =
            [
                new PushWaypointMark(new LatLon(NodeALat, NodeALon), null, null),
                new PushWaypointMark(new LatLon(blockLat, blockLon), null, null),
                new PushWaypointMark(new LatLon(NodeBLat, NodeBLon), null, null),
            ];
            Dispatcher.UIThread.RunJobs();
            var moves = new List<(int Index, LatLon To)>();
            var selected = new List<string>();
            canvas.PushMarkerDragged += (index, to) => moves.Add((index, to));
            canvas.AircraftLeftClicked += selected.Add;
            var release = new Point(onBlock.X + 40, onBlock.Y + 10);

            window.MouseDown(ToWindow(canvas, window, onBlock), MouseButton.Left);
            window.MouseMove(ToWindow(canvas, window, release), RawInputModifiers.LeftMouseButton);
            window.MouseUp(ToWindow(canvas, window, release), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, Assert.Single(moves).Index);
            Assert.Empty(selected);
            Assert.False(canvas.HasManualDataBlockOffset(aircraft.Callsign));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // An undo while a marker is being dragged republishes the markers: the drag ends there, so its release cannot move
    // whatever point now has the dragged index.
    [AvaloniaFact]
    public void RepublishingTheMarkersMidDrag_CancelsTheMarkerDrag()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            ShowMarkersAtAThenCThenB(canvas);
            var moves = new List<(int Index, LatLon To)>();
            canvas.PushMarkerDragged += (index, to) => moves.Add((index, to));
            (float cx, float cy) = canvas.Viewport.LatLonToScreen(NodeCLat, NodeCLon);
            var press = new Point(cx + 3, cy);
            var release = new Point(cx + 40, cy + 10);

            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left);
            window.MouseMove(ToWindow(canvas, window, release), RawInputModifiers.LeftMouseButton);
            canvas.DrawWaypoints = [1, 2];
            canvas.PushWaypointMarks =
            [
                new PushWaypointMark(new LatLon(NodeALat, NodeALon), null, null),
                new PushWaypointMark(new LatLon(NodeBLat, NodeBLon), null, null),
            ];
            canvas.InvalidateVisual();
            RenderFrame();
            Assert.Same(canvas.PushWaypointMarks, canvas.DrawnPushWaypointMarks);
            window.MouseUp(ToWindow(canvas, window, release), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(moves);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // Leaving draw mode (Escape, or the route sent) ends a Shift+drag and a marker drag: back in draw mode neither is
    // drawn, and their releases place and move nothing.
    [AvaloniaFact]
    public void LeavingDrawMode_ClearsThePendingPointAndTheMarkerDrag()
    {
        (GroundCanvas canvas, Window window) = ShowPushDrawCanvas();
        try
        {
            ShowMarkersAtAThenCThenB(canvas);
            var placed = new List<LatLon>();
            var moves = new List<(int Index, LatLon To)>();
            canvas.DrawFreePointPlaced += (point, _, _) => placed.Add(point);
            canvas.PushMarkerDragged += (index, to) => moves.Add((index, to));

            Point press = CanvasPointNearNodeA(canvas);
            var cursor = new Point(press.X + 40, press.Y);
            window.MouseDown(ToWindow(canvas, window, press), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(ToWindow(canvas, window, cursor), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            RenderFrame();
            Assert.NotNull(canvas.DrawnPendingPushPoint);
            LeaveAndReenterDrawMode(canvas);
            Assert.Null(canvas.DrawnPendingPushPoint);
            window.MouseUp(ToWindow(canvas, window, cursor), MouseButton.Left, RawInputModifiers.Shift);

            (float cx, float cy) = canvas.Viewport.LatLonToScreen(NodeCLat, NodeCLon);
            var markerPress = new Point(cx + 3, cy);
            var markerCursor = new Point(cx + 40, cy + 10);
            window.MouseDown(ToWindow(canvas, window, markerPress), MouseButton.Left);
            window.MouseMove(ToWindow(canvas, window, markerCursor), RawInputModifiers.LeftMouseButton);
            LeaveAndReenterDrawMode(canvas);
            Assert.Same(canvas.PushWaypointMarks, canvas.DrawnPushWaypointMarks);
            window.MouseUp(ToWindow(canvas, window, markerCursor), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(placed);
            Assert.Empty(moves);
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
                new GroundNodeDto(3, NodeCLat, NodeCLon, "TaxiwayIntersection", null, null, null),
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
        RenderFrame();
        return (canvas, window);
    }

    // The route A → C → B, so C's marker (index 1) is an earlier point's, not the last.
    private static void ShowMarkersAtAThenCThenB(GroundCanvas canvas)
    {
        canvas.DrawWaypoints = [1, 3, 2];
        canvas.PushWaypointMarks =
        [
            new PushWaypointMark(new LatLon(NodeALat, NodeALon), null, null),
            new PushWaypointMark(new LatLon(NodeCLat, NodeCLon), null, null),
            new PushWaypointMark(new LatLon(NodeBLat, NodeBLon), null, null),
        ];
        Dispatcher.UIThread.RunJobs();
    }

    private static void LeaveAndReenterDrawMode(GroundCanvas canvas)
    {
        canvas.IsDrawingRoute = false;
        canvas.IsDrawingRoute = true;
        RenderFrame();
    }

    // A point on the aircraft's datablock, found by hit-testing the block as the last frame placed it.
    private static Point DataBlockPointOf(GroundCanvas canvas, AircraftModel aircraft)
    {
        (float x, float y) = canvas.Viewport.LatLonToScreen(aircraft.Position.Lat, aircraft.Position.Lon);
        for (int dy = -80; dy <= 40; dy += 2)
        {
            for (int dx = -40; dx <= 160; dx += 2)
            {
                var point = new Point(x + dx, y + dy);
                if (canvas.FindDataBlockAtPoint(point) is not null)
                {
                    return point;
                }
            }
        }

        Assert.Fail($"no point on {aircraft.Callsign}'s datablock near its symbol");
        return default;
    }

    // Headless frames are drawn only on a render-timer tick, and only for a canvas invalidated since the last one.
    private static void RenderFrame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
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
