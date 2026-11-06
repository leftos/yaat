using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>KOAK hold-short bars named by the side of a crossing they sit on.</summary>
internal static class OakBars
{
    /// <summary>
    /// 28L's bar on B on the far side from B's 28R bars: B crosses both runways, so the runway has a bar on each side of 28L
    /// there, and the far one is the more distant from the 28R bars.
    /// </summary>
    public static GroundNode Bar28LOnBFarFrom28R(AirportGroundLayout layout)
    {
        List<GroundNode> bars28R = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        return TestLayoutNodes
            .RunwayHoldShortsOnTaxiway(layout, "28L", "B")
            .MaxBy(node => bars28R.Min(other => GeoMath.DistanceNm(node.Position, other.Position)))!;
    }
}
