using Xunit;
using Xunit.Abstractions;
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
    public void RunwayNameContainsDesignator_NormalizesLeadingZero(string edgeName, string designator, bool expected)
    {
        Assert.Equal(expected, IGroundEdge.RunwayNameContainsDesignator(edgeName, designator));
    }

    [Fact]
    public void FindRunwayRoute_PaddedAndUnpaddedDesignatorMatch()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var archive = RecordingLoader.OpenArchive("TestData/c000c2b0afc8.zip");
        if (archive is null)
        {
            return;
        }

        var layout = archive.ReadLayout("mia");
        var category = AircraftCategorization.Categorize("E75L");
        var startNode = layout.Nodes[304];

        var padded = TaxiPathfinder.FindRunwayRoute(layout, startNode, "08R", category);
        var unpadded = TaxiPathfinder.FindRunwayRoute(layout, startNode, "8R", category);

        Assert.NotNull(padded);
        Assert.NotNull(unpadded);

        int paddedEnd = padded.Segments[^1].ToNodeId;
        int unpaddedEnd = unpadded.Segments[^1].ToNodeId;
        output.WriteLine($"08R -> finalNode={paddedEnd} segs={padded.Segments.Count}; 8R -> finalNode={unpaddedEnd} segs={unpadded.Segments.Count}");

        Assert.Equal(paddedEnd, unpaddedEnd);
        Assert.Equal(padded.Segments.Count, unpadded.Segments.Count);
    }
}
