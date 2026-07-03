using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// GitHub issue #235: at SFO, SKW5571 was cleared <c>TAXI B K A @F10</c> (the handler prepends the
/// current taxiway D → <c>D B K A</c>) to gate F10, but the resolved route looped:
/// <c>D B K A Q1 B D A RAMP @F10</c> — it entered taxiway A at the wrong K/A junction, walked
/// northwest to A's far terminus (away from F10), then detoured Q1 → B → D → A → RAMP back to the
/// F10 ramp.
///
/// Root cause: the final K→A transition into a PARKING destination scored junction candidates with
/// the coarse, arrival-bearing-blind <c>ComputeLookaheadPenalty</c>; both K/A junctions "point
/// toward F10" geographically, so the cheaper-to-reach (wrong-direction) junction won. F10 is a RAMP
/// spur more than one hop off A, so the destination-aware terminus search could not rescue the wrong
/// junction, and the append-only extension found the long loop. Fixed by probing the actual
/// destination-reach cost per junction candidate for the final transition into a parking destination.
///
/// Uses the full-precision SFO map the recording was captured on (issue172-sfo.geojson); the shared
/// TestData/sfo.geojson is a stale, smaller map that does not reproduce the topology.
/// </summary>
public class Issue235SfoTaxiBKAParkingLoopTests
{
    private const string PinnedSfoPath = "TestData/issue172-sfo.geojson";

    // SKW5571's position at t≈1663 (from the bug bundle), on taxiway D just after the runway exit.
    private const double StartLat = 37.62128237750699;
    private const double StartLon = -122.38357702970711;

    private readonly ITestOutputHelper _output;

    public Issue235SfoTaxiBKAParkingLoopTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static GroundNode NearestNodeOnTaxiway(AirportGroundLayout layout, string taxiway, double lat, double lon) =>
        layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway(taxiway)))
            .OrderBy(n => GeoMath.DistanceNm(lat, lon, n.Position.Lat, n.Position.Lon))
            .First();

    private static bool SegmentIncludesTaxiway(string taxiwayName, string target)
    {
        var parts = taxiwayName.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => string.Equals(part, target, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TaxiBKA_ToF10_DoesNotLoopThroughQ1()
    {
        var layout = new PinnedSfoGroundData(PinnedSfoPath).GetLayout("SFO");
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            _output.WriteLine("SFO layout / navdata unavailable — skipping");
            return;
        }

        var f10 = layout.FindParkingByName("F10");
        Assert.NotNull(f10);

        var start = NearestNodeOnTaxiway(layout, "D", StartLat, StartLon);
        _output.WriteLine($"start node = {start.Id} on D, F10 = {f10.Id}");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["D", "B", "K", "A"],
            out string? failReason,
            new ExplicitPathOptions
            {
                AirportId = "SFO",
                DestinationHintNode = f10,
                DiagnosticLog = msg => _output.WriteLine(msg),
            },
            AircraftCategory.Jet
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        _output.WriteLine($"Route: {route.Segments.Count} segments, {route.TotalDistanceNm:F2} nm → {route.ToSummary()}");
        _output.WriteLine($"Taxiways: {route.FormatTaxiwaySequence()}");

        // Reaches F10.
        Assert.Equal(f10.Id, route.Segments[^1].ToNodeId);

        // The loop signature: the buggy route detours onto Q1 (and re-traverses B/D after leaving
        // them). The F10 ramp sits directly off A/K near the K/A junction — a correct route never
        // touches Q1, a connector on the far northwest side of A.
        Assert.DoesNotContain(route.Segments, s => SegmentIncludesTaxiway(s.TaxiwayName, "Q1"));

        // A direct route to F10 off the B/K/A junction is short (~0.22 nm); the buggy Q1→B→D→A→RAMP
        // loop is ~0.73 nm. Guard well below the loop distance so a regression to it fails here too.
        Assert.True(route.TotalDistanceNm < 0.4, $"expected a short direct route to F10, got {route.TotalDistanceNm:F2} nm");
    }
}
