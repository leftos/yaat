using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;

namespace Yaat.Client.UI.Tests.Views;

// Hit-testing for the "snap to nearest node" right-click fallback. With an aircraft
// selected, a right-click anywhere must resolve to the closest graph node so the node
// menu (taxi route + "Warp here") is always reachable — not only within the 20 px node
// hit radius. FindNodeAtPoint keeps the radius; FindNearestNode ignores it.
public class GroundCanvasNearestNodeTests
{
    private const double NodeALat = 37.62;
    private const double NodeALon = -122.39;
    private const double NodeBLat = 37.70;
    private const double NodeBLon = -122.30;

    [AvaloniaFact]
    public void FindNearestNode_ClickFarFromAnyNode_StillReturnsClosest()
    {
        var canvas = MakeCanvas(800, 600);
        canvas.Layout = LayoutWithTwoNodes();

        var (ax, ay) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon);
        // 100 px away from node A — well outside the 20 px node hit radius, but still
        // far closer to A than to B.
        var farPoint = new Point(ax + 100, ay);

        Assert.Null(canvas.FindNodeAtPoint(farPoint));
        var nearest = canvas.FindNearestNode(farPoint);
        Assert.NotNull(nearest);
        Assert.Equal(1, nearest!.Id);
    }

    [AvaloniaFact]
    public void FindNearestNode_PicksTheCloserOfTwoNodes()
    {
        var canvas = MakeCanvas(800, 600);
        canvas.Layout = LayoutWithTwoNodes();

        var (bx, by) = canvas.Viewport.LatLonToScreen(NodeBLat, NodeBLon);
        var nearest = canvas.FindNearestNode(new Point(bx + 40, by + 40));

        Assert.NotNull(nearest);
        Assert.Equal(2, nearest!.Id);
    }

    [AvaloniaFact]
    public void FindNodeAtPoint_WithinRadius_StillReturnsNode()
    {
        var canvas = MakeCanvas(800, 600);
        canvas.Layout = LayoutWithTwoNodes();

        var (ax, ay) = canvas.Viewport.LatLonToScreen(NodeALat, NodeALon);
        var node = canvas.FindNodeAtPoint(new Point(ax, ay));

        Assert.NotNull(node);
        Assert.Equal(1, node!.Id);
    }

    [AvaloniaFact]
    public void FindNearestNode_NoLayout_ReturnsNull()
    {
        var canvas = MakeCanvas(800, 600);
        Assert.Null(canvas.FindNearestNode(new Point(400, 300)));
    }

    private static GroundCanvas MakeCanvas(double width, double height)
    {
        var canvas = new GroundCanvas();
        canvas.Viewport.CenterLat = NodeALat;
        canvas.Viewport.CenterLon = NodeALon;
        canvas.Viewport.Zoom = 1.0;
        canvas.Viewport.PixelWidth = (float)width;
        canvas.Viewport.PixelHeight = (float)height;
        return canvas;
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
            null
        );
}
