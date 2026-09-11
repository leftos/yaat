using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="VirtualNode"/> ids must be a pure function of the node's position: a free-space leg's negative
/// from-node id is serialised into every snapshot, so a process-global counter makes two same-seed runs
/// disagree byte-for-byte (the determinism invariant) and makes a snapshot restore rebuild the same point
/// under a different id.
/// </summary>
public class VirtualNodeTests(ITestOutputHelper output)
{
    private const string AirportId = "OAK";

    /// <summary>The route start node of the OAK gate-15 push-then-taxi fixture.</summary>
    private const int StartNodeId = 763;

    private const double PushedLat = 37.710217680439534;
    private const double PushedLon = -122.21728593336832;

    [Fact]
    public void Create_SamePosition_SameId()
    {
        var first = VirtualNode.Create(PushedLat, PushedLon);
        var second = VirtualNode.Create(PushedLat, PushedLon);

        output.WriteLine($"ids: {first.Id} / {second.Id}");

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void Create_PositionsOneFootApart_DifferentIds()
    {
        var origin = new LatLon(PushedLat, PushedLon);
        var oneFootNorth = GeoMath.ProjectPoint(origin, new TrueHeading(0), 1.0 / GeoMath.FeetPerNm);
        var oneFootEast = GeoMath.ProjectPoint(origin, new TrueHeading(90), 1.0 / GeoMath.FeetPerNm);

        int here = VirtualNode.Create(origin.Lat, origin.Lon).Id;
        int north = VirtualNode.Create(oneFootNorth.Lat, oneFootNorth.Lon).Id;
        int east = VirtualNode.Create(oneFootEast.Lat, oneFootEast.Lon).Id;

        output.WriteLine($"here={here} north={north} east={east}");

        Assert.NotEqual(here, north);
        Assert.NotEqual(here, east);
        Assert.NotEqual(north, east);
    }

    [Fact]
    public void Create_EveryId_IsBelowMinusOneHundred()
    {
        for (int i = 0; i < 500; i++)
        {
            double lat = PushedLat + (i * 0.0013);
            double lon = PushedLon - (i * 0.0007);
            int id = VirtualNode.Create(lat, lon).Id;
            Assert.True(id < -100, $"virtual id {id} at ({lat:F7},{lon:F7}) is not below -100 — it could collide with a layout node id");
        }

        Assert.True(VirtualNode.Create(0, 0).Id < -100);
        Assert.True(VirtualNode.Create(-89.9999999, 179.9999999).Id < -100);
    }

    [Fact]
    public void VirtualFirstLeg_RoundTripsThroughSnapshot_WithTheSameFromNodeId()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout(AirportId);
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(StartNodeId, out var startNode), $"node {StartNodeId} missing from the {AirportId} layout");

        var route = new TaxiRoute
        {
            Segments = [VirtualNode.CreateSegment(VirtualNode.Create(PushedLat, PushedLon), startNode, "RAMP")],
            HoldShortPoints = [],
        };

        int beforeId = route.Segments[0].FromNodeId;
        var restored = TaxiRoute.FromSnapshot(route.ToSnapshot(), layout);

        Assert.NotNull(restored);
        output.WriteLine($"from-node id before={beforeId} after={restored.Segments[0].FromNodeId}");

        Assert.Equal(beforeId, restored.Segments[0].FromNodeId);
    }
}
