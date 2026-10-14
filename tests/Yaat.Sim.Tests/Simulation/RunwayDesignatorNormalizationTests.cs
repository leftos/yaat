using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Runway designators must match regardless of a leading zero on single-digit
/// numbers. <see cref="RunwayIdentifier"/> already normalizes ("8R" == "08R"),
/// but the edge-name matcher <see cref="IGroundEdge.RunwayNameContainsDesignator"/>
/// did an exact comparison, so "8R" silently failed to match "RWY08R/26L". That
/// drove <see cref="TaxiPathfinder.FindRunwayRoute"/> to a different hold-short
/// for "8R" vs "08R" — the root cause of a MIA auto-taxi mis-route.
/// </summary>
public sealed class RunwayDesignatorNormalizationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("RWY08R/26L", "08R", true)]
    [InlineData("RWY08R/26L", "8R", true)]
    [InlineData("RWY08R/26L", "26L", true)]
    [InlineData("RWY08R/26L", "27", false)]
    [InlineData("RWY8R/26L", "08R", true)]
    [InlineData("RWY09/27", "9", true)]
    [InlineData("RWY09/27", "09", true)]
    public void RunwayNameContainsDesignator_NormalizesLeadingZero(string edgeName, string designator, bool expected) =>
        Assert.Equal(expected, IGroundEdge.RunwayNameContainsDesignator(edgeName, designator));

    [Fact]
    public void FindRunwayRoute_PaddedAndUnpaddedDesignatorMatch()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = RecordingGroundData.Evergreen("the 8R and 08R runway routes from a taxiway-N start node on today's MIA layout");
        AirportGroundLayout? layout = groundData.GetLayout("mia");
        if (layout is null)
        {
            return;
        }

        AircraftCategory category = AircraftCategorization.Categorize("E75L");

        // The recorded vertex on taxiway N, resolved by position so the test survives node renumbering.
        GroundNode? startNode = layout.FindNearestNodeOnTaxiway(new LatLon(25.798985183057184, -80.2966386201263), "N", 50);
        Assert.NotNull(startNode);

        TaxiRoute? padded = TaxiPathfinder.FindRunwayRoute(layout, startNode, "08R", category, WakeTurbulenceData.WakeClass.Large);
        TaxiRoute? unpadded = TaxiPathfinder.FindRunwayRoute(layout, startNode, "8R", category, WakeTurbulenceData.WakeClass.Large);

        Assert.NotNull(padded);
        Assert.NotNull(unpadded);

        int paddedEnd = padded.Segments[^1].ToNodeId;
        int unpaddedEnd = unpadded.Segments[^1].ToNodeId;
        output.WriteLine($"08R -> finalNode={paddedEnd} segs={padded.Segments.Count}; 8R -> finalNode={unpaddedEnd} segs={unpadded.Segments.Count}");

        Assert.Equal(paddedEnd, unpaddedEnd);
        Assert.Equal(padded.Segments.Count, unpadded.Segments.Count);
    }
}
