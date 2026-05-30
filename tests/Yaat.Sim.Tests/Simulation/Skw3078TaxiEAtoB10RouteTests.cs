using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// SKW3078 received <c>TAXI E A @B10</c> in the original sfo-s1-ground-control-28-01
/// recording. The runtime route from that bundle had a long out-and-back on
/// taxiway A (segments 47-56 reversed direction at node 1292 and walked back
/// to node 1273). The bundle pre-dates the current sfo.geojson and references
/// node ids that no longer exist, so we cannot run the recorded route through
/// the current pathfinder directly.
///
/// Instead, this test re-runs the *current* pathfinder against the *current*
/// SFO layout for the same logical task: start at node 852 (a 10R/28L
/// hold-short on E with the SW edge bearing ~228°, i.e. heading down E toward
/// taxiway A), taxi via "E A" to parking B10. We log every segment so the
/// route is visible in test output, and assert there is no immediate reversal
/// (segment X→Y followed immediately by Y→X).
/// </summary>
public class Skw3078TaxiEAtoB10RouteTests(ITestOutputHelper output)
{
    private const int StartNodeId = 852;
    private const string ParkingName = "B10";
    private static readonly List<string> Taxiways = ["E", "A"];

    [Fact]
    public void Route_FromNode852_ToB10_NoImmediateReversal()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("TaxiPathfinder", LogLevel.Debug).InitializeSimLog();

        Assert.True(layout.Nodes.TryGetValue(StartNodeId, out var startNode), $"Node {StartNodeId} missing from current sfo layout");
        var parkingNode = layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type == GroundNodeType.Parking || n.Type == GroundNodeType.Spot)
            && string.Equals(n.Name, ParkingName, StringComparison.OrdinalIgnoreCase)
        );
        Assert.NotNull(parkingNode);

        output.WriteLine($"start node #{startNode.Id} type={startNode.Type} pos=({startNode.Position.Lat:F6},{startNode.Position.Lon:F6})");
        output.WriteLine($"parking #{parkingNode.Id} name={parkingNode.Name} pos=({parkingNode.Position.Lat:F6},{parkingNode.Position.Lon:F6})");

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: StartNodeId,
            taxiwayNames: Taxiways,
            out string? failReason,
            new ExplicitPathOptions
            {
                AirportId = "SFO",
                DestinationHintNode = parkingNode,
                DiagnosticLog = msg => output.WriteLine(msg),
            }
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        output.WriteLine("");
        output.WriteLine($"=== ROUTE: {route.Segments.Count} segments ===");
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var s = route.Segments[i];
            output.WriteLine($"  [{i, 3}] {s.FromNodeId, 5} -> {s.ToNodeId, 5} ({s.TaxiwayName})");
        }

        var reversals = new List<(int Index, int A, int B)>();
        for (int i = 0; i < route.Segments.Count - 1; i++)
        {
            var a = route.Segments[i];
            var b = route.Segments[i + 1];
            if (a.FromNodeId == b.ToNodeId && a.ToNodeId == b.FromNodeId)
            {
                reversals.Add((i, a.FromNodeId, a.ToNodeId));
            }
        }

        if (reversals.Count > 0)
        {
            output.WriteLine("");
            output.WriteLine($"!!! {reversals.Count} immediate reversal(s) detected:");
            foreach (var (idx, a, b) in reversals)
            {
                output.WriteLine($"  segment [{idx}] {a}->{b} immediately followed by {b}->{a}");
            }
        }

        Assert.Empty(reversals);
    }

    /// <summary>
    /// Stronger invariant: <c>TAXI E A @B10</c> from node 852 must produce a
    /// route that stays on the authorized taxiways (E and A only — no
    /// unauthorized F detour) and contains no un-taxiable corner arcs
    /// (radius so tight that maxSafe drops below ~3 kt — physically
    /// impossible to follow without spinning out).
    ///
    /// <para>
    /// Today this fails because @141/@268/@57 each create their own tangent
    /// chain on the shared E centerline, leaving a parallel-collinear bypass
    /// branch (141→1748→1754) that dead-ends at @268's F-side tangent. The
    /// bridge code then routes through the F edges 1753→1752→1755 to reach
    /// the legitimate E continuation, traversing the 9 ft / 1.9 kt
    /// 1755↔1750 arc — the spin captured by Skw3078FixComparisonCapture.
    /// </para>
    ///
    /// <para>
    /// Should pass once the post-build merger collapses the parallel bypass
    /// chains into a single canonical chain on the @141↔@268↔@57 E line.
    /// </para>
    /// </summary>
    [Fact]
    public void Route_FromNode852_ToB10_StaysOnAuthorizedTaxiwaysAndAvoidsUntaxiableArcs()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        Assert.True(layout.Nodes.TryGetValue(StartNodeId, out _), $"Node {StartNodeId} missing from current sfo layout");
        var parkingNode = layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type == GroundNodeType.Parking || n.Type == GroundNodeType.Spot)
            && string.Equals(n.Name, ParkingName, StringComparison.OrdinalIgnoreCase)
        );
        Assert.NotNull(parkingNode);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: StartNodeId,
            taxiwayNames: Taxiways,
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", DestinationHintNode = parkingNode }
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        // F is never legitimately needed for TAXI E A @B10 — its presence
        // means the bridge code took the unauthorized 1753→1752→1755 detour.
        // (Y/M1/M3/RAMP segments that appear after the A walk are the
        // legitimate parking extension to reach B10 and aren't checked here.)
        var fSegments = route
            .Segments.Select((s, i) => (Index: i, Segment: s))
            .Where(x => string.Equals(x.Segment.TaxiwayName, "F", StringComparison.OrdinalIgnoreCase))
            .ToList();

        const double DegenerateRadiusFt = 5.0;

        var tightArcs = route
            .Segments.Select((s, i) => (Index: i, Segment: s))
            .Where(x => x.Segment.Edge.Edge is GroundArc arc && arc.MinRadiusOfCurvatureFt < DegenerateRadiusFt)
            .ToList();

        if (fSegments.Count > 0)
        {
            output.WriteLine($"!!! {fSegments.Count} segment(s) on un-authorized taxiway F:");
            foreach (var (idx, seg) in fSegments)
            {
                output.WriteLine($"  [{idx}] {seg.FromNodeId}->{seg.ToNodeId}");
            }
        }

        if (tightArcs.Count > 0)
        {
            output.WriteLine($"!!! {tightArcs.Count} degenerate-radius arc segment(s) (radius < {DegenerateRadiusFt:F0}ft):");
            foreach (var (idx, seg) in tightArcs)
            {
                var arc = (GroundArc)seg.Edge.Edge;
                output.WriteLine(
                    $"  [{idx}] {seg.FromNodeId}->{seg.ToNodeId} on {seg.TaxiwayName} radius={arc.MinRadiusOfCurvatureFt:F1}ft maxSafe={arc.MaxSafeSpeedKts(AircraftCategory.Jet):F1}kt"
                );
            }
        }

        Assert.Empty(fSegments);
        Assert.Empty(tightArcs);
    }

    /// <summary>
    /// Bias check: <c>TAXI E A @B10</c> from node 852 must not enter
    /// taxiway Y in the parking extension. Y is a letter-only taxiway
    /// (parallel to A) that the controller did not authorize. The current
    /// route uses A → AY1 → Y → M1 → M3 → RAMP because Y happens to be
    /// marginally shorter; the desired route stays on A and uses only
    /// numbered/RAMP connectors past A.
    ///
    /// <para>
    /// Numbered taxiways (containing any digit — AY1, M1, M3) and RAMP are
    /// fine because they're typically required ramp/connector links.
    /// Letter-only taxiways not in the original instruction (Y, F, etc.) are
    /// the ones to avoid — controllers would name them explicitly if they
    /// wanted them in the route.
    /// </para>
    /// </summary>
    [Fact]
    public void Route_FromNode852_ToB10_DoesNotEnterUnauthorizedLetterOnlyTaxiway_Y()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var parkingNode = layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type == GroundNodeType.Parking || n.Type == GroundNodeType.Spot)
            && string.Equals(n.Name, ParkingName, StringComparison.OrdinalIgnoreCase)
        );
        Assert.NotNull(parkingNode);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: StartNodeId,
            taxiwayNames: Taxiways,
            out string? failReason,
            new ExplicitPathOptions { AirportId = "SFO", DestinationHintNode = parkingNode }
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        var ySegments = route
            .Segments.Select((s, i) => (Index: i, Segment: s))
            .Where(x => string.Equals(x.Segment.TaxiwayName, "Y", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ySegments.Count > 0)
        {
            output.WriteLine($"!!! {ySegments.Count} segment(s) on un-authorized letter-only taxiway Y:");
            foreach (var (idx, seg) in ySegments)
            {
                output.WriteLine($"  [{idx}] {seg.FromNodeId}->{seg.ToNodeId}");
            }
        }

        Assert.Empty(ySegments);
    }

    /// <summary>
    /// Replay the actual sfo-s1-ground-control-28-01 bundle through the
    /// current engine + current sfo.geojson and dump SKW3078's live taxi
    /// route once it's been built. The bundle was recorded against an older
    /// layout, so the bundle's saved snapshot references node ids that no
    /// longer exist — but Replay() ticks from t=0 with current code, so the
    /// route observed here is the *current* pathfinder's output applied to
    /// SKW3078's actual recorded position when TAXI E A @B10 fires at t=816.
    ///
    /// Use this to compare against the synthetic node-852 route above and
    /// confirm whether the spin captured by Skw3078FixComparisonCapture is
    /// caused by a malformed route (would show reversals here) or by
    /// something downstream (navigator/fillet handling of a clean route).
    /// </summary>
    [Fact]
    public void BundleReplay_LiveRoute_NoImmediateReversal()
    {
        const string RecordingPath = "TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip";

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        var engine = new SimulationEngine(groundData);
        engine.Replay(recording, 820);

        var ac = engine.FindAircraft("SKW3078");
        Assert.NotNull(ac);

        output.WriteLine($"position=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F1} ias={ac.IndicatedAirspeed:F1}");

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        output.WriteLine("");
        output.WriteLine($"=== ROUTE: {route.Segments.Count} segments, currentIdx={route.CurrentSegmentIndex} ===");
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var s = route.Segments[i];
            string marker = i == route.CurrentSegmentIndex ? "  <- current" : "";
            output.WriteLine($"  [{i, 3}] {s.FromNodeId, 5} -> {s.ToNodeId, 5} ({s.TaxiwayName}){marker}");
        }

        var reversals = new List<int>();
        for (int i = 0; i < route.Segments.Count - 1; i++)
        {
            var a = route.Segments[i];
            var b = route.Segments[i + 1];
            if (a.FromNodeId == b.ToNodeId && a.ToNodeId == b.FromNodeId)
            {
                reversals.Add(i);
                output.WriteLine($"  REVERSAL at [{i}]/[{i + 1}]: {a.FromNodeId}->{a.ToNodeId} then {b.FromNodeId}->{b.ToNodeId}");
            }
        }

        Assert.Empty(reversals);
    }
}
