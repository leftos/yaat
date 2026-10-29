using Xunit;
using Yaat.Sim;
using Yaat.Sim.Asdex;

namespace Yaat.Sim.Tests.Asdex;

/// <summary>
/// <see cref="AsdexConfiguredTaxiways.Filter"/> — the airport's sidecar <c>asdexLandingTaxiways</c> applied to the layout's
/// taxiway segments: no list keeps every segment (any taxiway alerts), an empty list keeps none, and a list keeps only its
/// own taxiways, matched as the layout spells them whatever the authored case.
/// </summary>
public class AsdexConfiguredTaxiwaysTests
{
    private static readonly IReadOnlyList<AsdexTaxiwaySegment> Segments =
    [
        new("A", new LatLon(37.70, -122.20), new LatLon(37.71, -122.20)),
        new("B", new LatLon(37.70, -122.21), new LatLon(37.71, -122.21)),
        new("C", new LatLon(37.70, -122.22), new LatLon(37.71, -122.22)),
    ];

    /// <summary>No list authored is any taxiway: the segments come back as they were handed in.</summary>
    [Fact]
    public void Filter_NullSet_ReturnsTheSegmentsUnchanged() => Assert.Same(Segments, AsdexConfiguredTaxiways.Filter(Segments, null));

    /// <summary>An empty list names no taxiway, so no segment is left to alert on.</summary>
    [Fact]
    public void Filter_EmptySet_KeepsNone() =>
        Assert.Empty(AsdexConfiguredTaxiways.Filter(Segments, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// A list keeps only its own taxiways, matched whatever case the file authored: the set is the case-insensitive one the loader
    /// and the catalog build.
    /// </summary>
    [Fact]
    public void Filter_AList_KeepsOnlyItsTaxiways()
    {
        IReadOnlyList<AsdexTaxiwaySegment> kept = AsdexConfiguredTaxiways.Filter(
            Segments,
            new HashSet<string>(["a", "c"], StringComparer.OrdinalIgnoreCase)
        );

        Assert.Equal(["A", "C"], [.. kept.Select(segment => segment.Name)]);
    }

    /// <summary>A list naming none of the layout's taxiways leaves nothing to alert on.</summary>
    [Fact]
    public void Filter_AListOfOtherNames_KeepsNone() =>
        Assert.Empty(AsdexConfiguredTaxiways.Filter(Segments, new HashSet<string>(["Y", "Z"], StringComparer.OrdinalIgnoreCase)));
}
