using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Fillet;

public class FilletReachabilityDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FilletReachabilityDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("fll")]
    [InlineData("oak")]
    [InlineData("sfo")]
    public void OnlyLegacyStableNodes_DecodeMissingLink(string shortId)
    {
        var pre = Load(shortId);
        if (pre is null)
        {
            return;
        }

        var legacy = LayoutCloner.DeepClone(pre);
        var v2 = LayoutCloner.DeepClone(pre);
        _ = new LegacyFilletArcGenerator().Apply(legacy);
        _ = new FilletArcGeneratorV2().Apply(v2);

        var analyses = FilletReachabilityDiagnostics.AnalyzeOnlyLegacyStableNodes(pre, legacy, v2, maxSamples: 5);
        _output.WriteLine(FilletReachabilityDiagnostics.FormatAnalysis(shortId, analyses));

        var (legacyParking, v2Parking, onlyLegacyParking) = FilletReachabilityDiagnostics.CompareParking(legacy, v2);
        _output.WriteLine($"{shortId} parking: legacy={legacyParking.Count} v2={v2Parking.Count} only-legacy-parking={onlyLegacyParking.Count}");
        if (onlyLegacyParking.Count > 0)
        {
            _output.WriteLine($"  only-legacy parking sample: {string.Join(", ", onlyLegacyParking)}");
        }
    }

    private static AirportGroundLayout? Load(string shortId)
    {
        string path = Path.Combine("TestData", $"{shortId}.geojson");
        return !File.Exists(path) ? null : GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, FilletMode.None);
    }
}
