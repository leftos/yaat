using Xunit;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// A route overlay can carry free-space legs — the approach leg from the aircraft to its route's first node,
/// and the #396/#400 ramp-lane cuts — whose endpoints are <see cref="VirtualNode"/>s the layout never held.
/// Resolving a segment's screen endpoints purely from the layout's node table drops those legs silently, so
/// the resolution falls back to the segment's own node references.
/// </summary>
public class GroundRendererRouteDrawTests
{
    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    private static MapViewport MakeViewport(LatLon center)
    {
        return new MapViewport
        {
            CenterLat = center.Lat,
            CenterLon = center.Lon,
            Zoom = 20.0,
            PixelWidth = 800,
            PixelHeight = 600,
        };
    }

    [Fact]
    public void FreeSpaceLeg_ResolvesEndpointsFromTheSegmentsOwnNodes()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return; // test data absent — skip
        }

        var position = new LatLon(37.710217680439534, -122.21728593336832);
        var startNode = layout.FindNearestNode(position)!;
        var vp = MakeViewport(position);

        // The layout's node table is what the renderer projects; a virtual node is not in it.
        var nodeScreenPos = new Dictionary<int, (float X, float Y)>
        {
            [startNode.Id] = vp.LatLonToScreen(startNode.Position.Lat, startNode.Position.Lon),
        };

        var seg = VirtualNode.CreateSegment(VirtualNode.Create(position.Lat, position.Lon), startNode, "RAMP");

        var from = GroundRenderer.RouteSegmentEndpoint(vp, nodeScreenPos, seg.FromNodeId, seg.Edge.FromNode);
        var to = GroundRenderer.RouteSegmentEndpoint(vp, nodeScreenPos, seg.ToNodeId, seg.Edge.ToNode);

        Assert.Equal(vp.LatLonToScreen(position.Lat, position.Lon), from);
        Assert.Equal(nodeScreenPos[startNode.Id], to);
    }

    [Fact]
    public void GraphSegment_ResolvesEndpointsFromTheLayoutTable()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return; // test data absent — skip
        }

        var position = new LatLon(37.710217680439534, -122.21728593336832);
        var startNode = layout.FindNearestNode(position)!;
        var neighbor = startNode.Edges[0].OtherNode(startNode)!;
        var vp = MakeViewport(position);

        var nodeScreenPos = new Dictionary<int, (float X, float Y)>
        {
            [startNode.Id] = vp.LatLonToScreen(startNode.Position.Lat, startNode.Position.Lon),
            [neighbor.Id] = vp.LatLonToScreen(neighbor.Position.Lat, neighbor.Position.Lon),
        };

        var seg = new TaxiRouteSegment { TaxiwayName = startNode.Edges[0].TaxiwayName, Edge = startNode.Edges[0].Directed(startNode, neighbor) };

        var from = GroundRenderer.RouteSegmentEndpoint(vp, nodeScreenPos, seg.FromNodeId, seg.Edge.FromNode);
        var to = GroundRenderer.RouteSegmentEndpoint(vp, nodeScreenPos, seg.ToNodeId, seg.Edge.ToNode);

        Assert.Equal(nodeScreenPos[startNode.Id], from);
        Assert.Equal(nodeScreenPos[neighbor.Id], to);
    }
}
