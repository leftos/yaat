using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for a drawn taxi route whose leading nodes the aircraft had already taxied past.
///
/// Recording: S1-OAK-P (A) — N16390 (C182) taxiing north-west on OAK taxiway C after landing.
/// At t=681 the controller finished drawing a route to stand GA14 and the client sent the dense
/// node list <c>TAXI #1124 #352 #1126 #1127 #353 #517 … #683 @GA14, CROSS 15</c>. The drawn path
/// is a simple <c>C → F → RAMP</c> taxi crossing 15/33 once.
///
/// The aircraft kept taxiing while the route was being drawn, so by dispatch time its start node
/// was 1127 — three nodes *past* the drawn path's first node (#1124). The resolver routed backwards
/// to reach them (permitted only because the admissibility gate is bypassed on the first edge), and
/// every hop after that needed a ~180° reversal the gate hard-rejects. Each hop looped the whole
/// <c>C → J → K → F → C</c> block instead: 544 segments, ten laps, reading back as
/// <c>"Taxi via C J K F C J K F … on 28R/10L C1 C F RAMP"</c>.
///
/// **Replay strategy:** Hybrid (snapshot restore at t=680, then <see cref="SimulationEngine.ReplayRange"/>
/// through the TAXI at t=681). The fix changes taxi-route resolution generally, so the snapshot pins
/// the pre-command state — where the aircraft had got to along its previous <c>E C HS 15/33</c>
/// clearance is exactly what makes the drawn prefix stale. The window stops at t=690, before the
/// controller's DEL at t=692.
/// </summary>
public class DrawnRoutePassedPrefixTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/drawn-route-passed-prefix-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N16390";

    /// <summary>Snapshot restored just before the drawn TAXI at t=681.</summary>
    private const int RestoreAtSeconds = 680;

    /// <summary>End of the assertion window — after the TAXI, before the controller's DEL at t=692.</summary>
    private const int AssertAtSeconds = 690;

    /// <summary>Node id of stand GA14, the drawn route's terminus.</summary>
    private const int Ga14NodeId = 683;

    /// <summary>
    /// Generous ceiling on the resolved segment count. The drawn path is 48 nodes, so a faithful
    /// resolution is ~50 segments; the bug produced 544.
    /// </summary>
    private const int MaxReasonableSegments = 100;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private TaxiRoute? ResolveDrawnRoute()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            return null;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                return null;
            }

            engine.RestoreFromSnapshot(snapshot.State);
            engine.ReplayRange((int)snapshot.ElapsedSeconds, AssertAtSeconds, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var route = ac.Ground?.AssignedTaxiRoute;
            Assert.NotNull(route);
            output.WriteLine($"resolved {route.Segments.Count} segments, summary: {route.ToSummary()}");
            return route;
        }
    }

    /// <summary>Taxiway names an edge belongs to, decomposing a composite junction label ("C - J").</summary>
    private static string[] SegmentTaxiwayNames(TaxiRouteSegment seg) => seg.Edge.Edge is GroundArc arc ? arc.TaxiwayNames : [seg.TaxiwayName];

    [Fact]
    public void DrawnRoute_DoesNotLoopTheBlock()
    {
        var route = ResolveDrawnRoute();
        if (route is null)
        {
            return;
        }

        Assert.True(
            route.Segments.Count <= MaxReasonableSegments,
            $"drawn 48-node route resolved to {route.Segments.Count} segments — the aircraft had already taxied "
                + $"past the first drawn nodes and the resolver looped the block to turn around. Summary: {route.ToSummary()}"
        );

        var seen = new HashSet<(int From, int To)>();
        foreach (var seg in route.Segments)
        {
            Assert.True(
                seen.Add((seg.FromNodeId, seg.ToNodeId)),
                $"segment {seg.FromNodeId}->{seg.ToNodeId} ({seg.TaxiwayName}) is traversed more than once — the route loops. "
                    + $"Summary: {route.ToSummary()}"
            );
        }
    }

    [Fact]
    public void DrawnRoute_StaysOnTheDrawnTaxiways()
    {
        var route = ResolveDrawnRoute();
        if (route is null)
        {
            return;
        }

        // The drawn path runs C -> F -> RAMP. J, K and the 28R/10L surface are the detour the
        // block loop took to reverse direction; none of them is on the drawn geometry.
        foreach (var seg in route.Segments)
        {
            var names = SegmentTaxiwayNames(seg);
            Assert.False(
                names.Contains("J", StringComparer.OrdinalIgnoreCase) || names.Contains("K", StringComparer.OrdinalIgnoreCase),
                $"route uses taxiway {seg.TaxiwayName} ({seg.FromNodeId}->{seg.ToNodeId}); the drawn route only uses C, F and RAMP"
            );
            Assert.False(
                seg.Edge.Edge.IsRunwayCenterline,
                $"route taxis along runway {seg.TaxiwayName} ({seg.FromNodeId}->{seg.ToNodeId}); the drawn route only crosses 15/33"
            );
        }
    }

    [Fact]
    public void DrawnRoute_EndsAtTheDrawnStand()
    {
        var route = ResolveDrawnRoute();
        if (route is null)
        {
            return;
        }

        Assert.Equal("GA14", route.DestinationParking);
        Assert.Equal(Ga14NodeId, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void DrawnRoute_ReadbackNamesOnlyTheDrawnTaxiways()
    {
        var route = ResolveDrawnRoute();
        if (route is null)
        {
            return;
        }

        string summary = route.ToSummary();
        Assert.StartsWith("C F", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(" J ", $" {summary} ", StringComparison.Ordinal);
        Assert.DoesNotContain("28R/10L", summary, StringComparison.Ordinal);
    }
}
