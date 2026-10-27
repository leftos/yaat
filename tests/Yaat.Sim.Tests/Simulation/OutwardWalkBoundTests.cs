using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The outward walk from a short branch end to the runway's hold line is bounded by the largest cross-track among the
/// runway's hold-line bars, since an angled taxiway's bar sits beyond the base separation S; with no bar flagged as a
/// hold line, by S plus half the taxiway width proxy, the angle term's ceiling.
/// </summary>
public class OutwardWalkBoundTests
{
    [Fact]
    public void Oak30_12_BoundIsTheLargestHoldLineBarCrossTrack()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        GroundRunway runway = layout.FindRunway("30")!;
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        var reference = new LatLon(rect.RefLat, rect.RefLon);
        double largestFt = layout
            .Nodes.Values.Where(node =>
                (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is { } id) && id.Equals(rect.CombinedId) && (node.IsShortBar == false)
            )
            .Max(node => Math.Abs(GeoMath.SignedCrossTrackDistanceNm(node.Position, reference, rect.TrueHeading)) * GeoMath.FeetPerNm);

        double boundFt = layout.OutwardWalkBoundFt(rect);

        Assert.Equal(largestFt, boundFt, precision: 6);
        Assert.True(boundFt >= 305.0, $"bound {boundFt:F1} ft is short of the W3/W4/W5 bars");
    }

    [Fact]
    public void Oak30_12_WithNoFlaggedBars_BoundIsSPlusHalfTheTaxiwayProxy()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } source)
        {
            return;
        }

        AirportGroundLayout layout = LayoutCloner.DeepClone(source);
        GroundRunway runway = layout.FindRunway("30")!;
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        RunwayIdentifier runwayId = rect.CombinedId;
        foreach (GroundNode node in layout.Nodes.Values.Where(node => (node.RunwayId is { } id) && id.Equals(runwayId)))
        {
            node.IsShortBar = null;
        }

        Assert.Equal(runway.HoldShortStandoffFt!.Value + 37.5, layout.OutwardWalkBoundFt(rect), precision: 6);
    }
}
