using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.Views;

// Ctrl+hover node inspection: while Ctrl is held over the Ground View, the nearest node of any type
// within 40 px of the cursor is marked and labelled "#<id> · <Type>[ <Name>]", where "#<id>" is the
// node-reference token a TAXI clearance accepts.
public class GroundCanvasCtrlHoverTests
{
    private const double NodeALat = 37.62;
    private const double NodeALon = -122.39;
    private const double NodeBLat = 37.70;
    private const double NodeBLon = -122.30;

    [AvaloniaFact]
    public void FindCtrlHoverNode_WithinRadius_ReturnsNearestNode()
    {
        GroundCanvas canvas = MakeCanvas(800, 600);
        canvas.Layout = LayoutWithTwoNodes();

        (float ax, float ay) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon);
        // 30 px away: outside the 20 px node hit radius, inside the 40 px Ctrl+hover radius.
        GroundNodeDto? node = canvas.FindCtrlHoverNode(new Point(ax + 30, ay));

        Assert.NotNull(node);
        Assert.Equal(1, node.Id);
    }

    [AvaloniaFact]
    public void FindCtrlHoverNode_BeyondRadius_ReturnsNull()
    {
        GroundCanvas canvas = MakeCanvas(800, 600);
        canvas.Layout = LayoutWithTwoNodes();

        (float ax, float ay) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon);
        var farPoint = new Point(ax + 41, ay);

        // A node still exists nearest to the point; it is just too far for the Ctrl+hover marker.
        Assert.NotNull(canvas.FindNearestNode(farPoint));
        Assert.Null(canvas.FindCtrlHoverNode(farPoint));
    }

    [AvaloniaFact]
    public void FindCtrlHoverNode_PicksNearestOfAnyType()
    {
        GroundCanvas canvas = MakeCanvas(800, 600);
        canvas.Layout = new GroundLayoutDto(
            "SFO",
            [
                new GroundNodeDto(1, NodeALat, NodeALon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(7, NodeALat, NodeALon + 0.001, "Parking", "A7", 280, null),
            ],
            [],
            null,
            null,
            null
        );

        (float ax, float ay) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon);
        (float px, float py) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon + 0.001);
        // Precondition at the fitted zoom: the two nodes are pixels apart, so "nearest" is decided by position, not a tie.
        Assert.True(MathF.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay))) > 10);
        GroundNodeDto? node = canvas.FindCtrlHoverNode(new Point(px + 1, py));

        Assert.NotNull(node);
        Assert.Equal(7, node.Id);
        Assert.Equal("Parking", node.Type);
    }

    [AvaloniaFact]
    public void FindCtrlHoverNode_NoLayout_ReturnsNull()
    {
        GroundCanvas canvas = MakeCanvas(800, 600);
        Assert.Null(canvas.FindCtrlHoverNode(new Point(400, 300)));
    }

    [AvaloniaFact]
    public void FormatCtrlNodeLabel_WithName()
    {
        string label = GroundCanvas.FormatCtrlNodeLabel(new GroundNodeDto(123, NodeALat, NodeALon, "Parking", "A7", 280, null));

        Assert.Equal("#123 · Parking A7", label);
        Assert.True(NodeRefToken.IsNodeReference(label.Split(' ')[0]));
    }

    [AvaloniaFact]
    public void FormatCtrlNodeLabel_WithoutName()
    {
        string label = GroundCanvas.FormatCtrlNodeLabel(new GroundNodeDto(42, NodeALat, NodeALon, "RunwayHoldShort", null, null, "28L"));

        Assert.Equal("#42 · RunwayHoldShort", label);
        Assert.True(NodeRefToken.IsNodeReference(label.Split(' ')[0]));
    }

    [AvaloniaFact]
    public void FormatCtrlNodeLabel_EmptyName_OmitsName()
    {
        string label = GroundCanvas.FormatCtrlNodeLabel(new GroundNodeDto(5, NodeALat, NodeALon, "Spot", "", null, null));

        Assert.Equal("#5 · Spot", label);
    }

    [AvaloniaFact]
    public void CtrlPointerMove_OverNode_SetsHover_ThenPlainMove_ClearsIt()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            Point nearA = PointNearNodeA(canvas, window);

            window.MouseMove(nearA, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            (string Label, SkiaSharp.SKPoint NodePos)? hover = canvas.ResolveCtrlNodeHover();
            Assert.NotNull(hover);
            Assert.Equal("#1 · TaxiwayIntersection", hover.Value.Label);
            (float ax, float ay) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon);
            Assert.Equal(ax, hover.Value.NodePos.X, 0.01f);
            Assert.Equal(ay, hover.Value.NodePos.Y, 0.01f);

            window.MouseMove(nearA, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(canvas.ResolveCtrlNodeHover());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void CtrlPointerMove_YaatLayoutHidden_NoHover()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            canvas.ShowYaatLayout = false;

            window.MouseMove(PointNearNodeA(canvas, window), RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(canvas.ResolveCtrlNodeHover());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void CtrlKeyUp_WithoutPointerMove_ClearsHover()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            window.MouseMove(PointNearNodeA(canvas, window), RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(canvas.ResolveCtrlNodeHover());

            canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.LeftCtrl });

            Assert.Null(canvas.ResolveCtrlNodeHover());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void CtrlKeyDown_WithoutPointerMove_ShowsHover()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            window.MouseMove(PointNearNodeA(canvas, window), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(canvas.ResolveCtrlNodeHover());

            canvas.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.RightCtrl,
                    KeyModifiers = KeyModifiers.Control,
                }
            );

            Assert.NotNull(canvas.ResolveCtrlNodeHover());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // Only the pixel size is set: assigning Layout fits the viewport to the layout's nodes, which sets center and zoom.
    private static GroundCanvas MakeCanvas(double width, double height)
    {
        var canvas = new GroundCanvas();
        canvas.Viewport.PixelWidth = (float)width;
        canvas.Viewport.PixelHeight = (float)height;
        return canvas;
    }

    private static (GroundCanvas Canvas, Window Window) ShowCanvas()
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

        canvas.Layout = LayoutWithTwoNodes();
        Dispatcher.UIThread.RunJobs();
        return (canvas, window);
    }

    /// <summary>
    /// A window point ~7 px up-right of node A: inside the Ctrl+hover radius, far from node B, and inside the canvas even
    /// though the fit puts node A (the layout's south-west corner) at the canvas's lower-left edge.
    /// </summary>
    private static Point PointNearNodeA(GroundCanvas canvas, Window window)
    {
        (float ax, float ay) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon);
        Point? inWindow = canvas.TranslatePoint(new Point(ax + 5, ay - 5), window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }

    private static GroundLayoutDto LayoutWithTwoNodes() =>
        new(
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
}
