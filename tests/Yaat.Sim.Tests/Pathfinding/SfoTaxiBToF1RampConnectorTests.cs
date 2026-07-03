using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// At SFO, UAL2627 (just cleared runway 28L, holding after exit) was cleared <c>TAXI B @F1 HS B4</c> to
/// gate F1, but the resolved route looped across the field:
/// <c>B A L C Z B1 Q A T9 RAMP HS B4 @F1</c> (232 segments). The controller re-issued the intent as an
/// explicit node list and it resolved to the correct <c>B T9 RAMP @F1</c> (79 segments): stay on cleared
/// taxiway B, join the numbered ramp connector T9 (which branches directly off B), take RAMP into the F
/// ramp.
///
/// Root cause: for a SINGLE named taxiway to a parking destination that hangs off a numbered connector,
/// <see cref="Yaat.Sim.Data.Airport.Pathfinding.SegmentExpander"/>'s <c>SelectBestParkingStop</c> chose
/// its on-taxiway stop-node candidates purely by straight-line distance to the gate. The real join node
/// (the B/T9 junction) is farther in straight-line terms — T9 loops away before curving back to the ramp
/// — so it was excluded from the candidate pool; every nearer candidate was a topological dead-end for
/// the arrival bearing, the selector gave up, and the route walked B to its far terminus and looped back.
/// The issue-#235 reach-probe only covers the multi-taxiway (junction-transition) case; this is the
/// single-taxiway gap. Fixed by seeding the candidate pool with the taxiway's numbered-connector/RAMP
/// junctions so the reach-cost picker can select the B/T9 junction.
///
/// Uses the full-precision SFO map the recording was captured on (issue172-sfo.geojson — matches the bug
/// bundle's own layout within one byte); the shared TestData/sfo.geojson is a stale, smaller map.
/// </summary>
public class SfoTaxiBToF1RampConnectorTests
{
    private const string PinnedSfoPath = "TestData/issue172-sfo.geojson";

    // UAL2627's position at t≈790 (from the bug bundle), holding after exiting runway 28L.
    private const double StartLat = 37.61935192788897;
    private const double StartLon = -122.3796147778547;
    private const double StartHeadingTrue = 182.16;

    private readonly ITestOutputHelper _output;

    public SfoTaxiBToF1RampConnectorTests(ITestOutputHelper output)
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
    public void TaxiB_ToF1_JoinsT9RampConnector_NotCrossFieldLoop()
    {
        var layout = new PinnedSfoGroundData(PinnedSfoPath).GetLayout("SFO");
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            _output.WriteLine("SFO layout / navdata unavailable — skipping");
            return;
        }

        var f1 = layout.FindParkingByName("F1");
        Assert.NotNull(f1);

        var start = NearestNodeOnTaxiway(layout, "B", StartLat, StartLon);
        _output.WriteLine($"start node = {start.Id} on B, F1 = {f1.Id}");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["B"],
            out string? failReason,
            new ExplicitPathOptions
            {
                AirportId = "SFO",
                DestinationHintNode = f1,
                ExplicitHoldShorts = ["B4"],
                StartHeadingTrue = StartHeadingTrue,
                DiagnosticLog = msg => _output.WriteLine(msg),
            },
            AircraftCategory.Jet
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        _output.WriteLine($"Route: {route.Segments.Count} segments, {route.TotalDistanceNm:F2} nm → {route.ToSummary()}");
        _output.WriteLine($"Taxiways: {route.FormatTaxiwaySequence()}");

        // Reaches F1.
        Assert.Equal(f1.Id, route.Segments[^1].ToNodeId);

        // The cross-field loop signature: it wanders through terminal-area letter taxiways (L, C, Z, Q)
        // and the B1 connector on its way back to the F ramp. The correct route stays on B, joins the T9
        // ramp connector, and takes RAMP into the gate — it never touches any of these.
        foreach (var strayTaxiway in new[] { "L", "C", "Z", "Q", "B1" })
        {
            Assert.DoesNotContain(route.Segments, s => SegmentIncludesTaxiway(s.TaxiwayName, strayTaxiway));
        }

        // The correct B → T9 → RAMP route is short; the cross-field loop is ~9 nm. Guard well below the
        // loop distance so a regression to it fails here too.
        Assert.True(route.TotalDistanceNm < 2.0, $"expected a short B/T9/RAMP route to F1, got {route.TotalDistanceNm:F2} nm");
    }
}
