using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>
/// Guards against the stable-anchor ID collision bug: when ExtendWithStableAnchors redirects
/// a cut to a pre-existing node ID, that ID can collide with a cut ID from a different junction
/// in the executor's cutNode dictionary, causing corner arc/chord endpoints to resolve to the
/// wrong tangent-cut node hundreds or thousands of feet away.
/// </summary>
public class FilletV2CornerSpanGuardTests
{
    private const double MaxSaneCornerSpanFt = 300.0;

    private readonly ITestOutputHelper _output;

    public FilletV2CornerSpanGuardTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("sfo")]
    [InlineData("oak")]
    [InlineData("fll")]
    public void V2_CornerEdgesAndChords_NoSpanExceedsMaxSane(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        if (!File.Exists(path))
        {
            return;
        }

        var pre = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
        var layout = LayoutCloner.DeepClone(pre);
        new FilletArcGeneratorV2().Apply(layout);

        var violations = new List<string>();

        foreach (var edge in layout.Edges)
        {
            string origin = edge.Origin ?? "";
            if (!origin.StartsWith("V2:corner", StringComparison.Ordinal))
            {
                continue;
            }

            double distFt = edge.DistanceNm * GeoMath.FeetPerNm;
            if (distFt > MaxSaneCornerSpanFt)
            {
                violations.Add(
                    $"{edge.Nodes[0].Id}->{edge.Nodes[1].Id} {distFt:F0}ft [{origin}] " + $"from={edge.Nodes[0].Origin} to={edge.Nodes[1].Origin}"
                );
            }
        }

        foreach (var arc in layout.Arcs)
        {
            string origin = arc.Origin ?? "";
            if (!origin.StartsWith("V2:corner", StringComparison.Ordinal))
            {
                continue;
            }

            double distFt = arc.DistanceNm * GeoMath.FeetPerNm;
            if (distFt > MaxSaneCornerSpanFt)
            {
                violations.Add($"ARC {arc.Nodes[0].Id}->{arc.Nodes[1].Id} {distFt:F0}ft [{origin}]");
            }
        }

        foreach (var v in violations.Take(10))
        {
            _output.WriteLine(v);
        }

        Assert.Empty(violations);
    }
}
