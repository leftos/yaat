using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Data.Airport.Precompute;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// <see cref="TugOutlineClearance.Clearance"/> is a copy of <see cref="GroundOutline.Clearance"/> without its arrays; it
/// must give the same figure to the bit, so a fix to one cannot silently skip the other.
/// </summary>
public class TugOutlineClearanceTests
{
    private const int StandCount = 30;

    public TugOutlineClearanceTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Over OAK's stands nearest gate 26, each drawn as a parked B738 and as a pushed and a pulled one at a spread of
    /// poses off the stand (along, across and turned), every pair measures the same by both. The spread holds both
    /// overlapping pairs (zero) and clear ones.
    /// </summary>
    [Fact]
    public void Clearance_MatchesGroundOutline_OverOakStands()
    {
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        GroundNode gate = PushTargetPlannerFixtures.Stand(layout, "26");
        var frame = new GroundOutlineFrame(gate.Position);
        var footprint = AircraftFootprint.FromType("B738");
        var pushing = GroundOutlineSize.Of(footprint, towedNoseFirst: false);
        var pulling = GroundOutlineSize.Of(footprint, towedNoseFirst: true);
        List<GroundOutline> outlines = [];
        foreach (
            GroundNode stand in layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.TrueHeading is not null))
                .OrderBy(n => GeoMath.DistanceNm(gate.Position, n.Position))
                .ThenBy(n => n.Id)
                .Take(StandCount)
        )
        {
            double headingDeg = stand.TrueHeading!.Value.Degrees;
            OutlinePoint centre = frame.ToLocal(stand.Position);
            outlines.Add(GroundOutline.At(centre, headingDeg, pushing));
            outlines.Add(GroundOutline.At(Offset(centre, headingDeg + 180.0, 40.0), headingDeg + 30.0, pushing));
            outlines.Add(GroundOutline.At(Offset(centre, headingDeg + 90.0, 25.0), headingDeg - 75.0, pulling));
        }

        int overlapping = 0;
        int clear = 0;
        foreach (GroundOutline a in outlines)
        {
            foreach (GroundOutline b in outlines)
            {
                double expected = GroundOutline.Clearance(a, b);
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(TugOutlineClearance.Clearance(a, b)));
                overlapping += expected <= 0.0 ? 1 : 0;
                clear += expected > 0.0 ? 1 : 0;
            }
        }

        Assert.True(overlapping > outlines.Count, $"only {overlapping} overlapping pairs");
        Assert.True(clear > outlines.Count, $"only {clear} clear pairs");
    }

    private static OutlinePoint Offset(OutlinePoint point, double bearingDeg, double distanceFt)
    {
        double rad = bearingDeg * Math.PI / 180.0;
        return point + new OutlinePoint(Math.Sin(rad) * distanceFt, Math.Cos(rad) * distanceFt);
    }
}
