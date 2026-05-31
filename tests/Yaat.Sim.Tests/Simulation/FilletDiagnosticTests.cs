using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression tests for the fillet-arc regression cluster.
/// Master plan: docs/plans/open-issues/fillet-regressions-master.md
///
/// Root cause: MergeCoincidentNodes translated bezier control points instead of
/// recomputing them, producing degenerate arcs (radius=0, maxSafe=0) that stalled
/// aircraft. Also, CubicBezier.RadiusOfCurvatureFt returned near-zero for degenerate
/// beziers where parametric speed → 0 (singularity, not a real tight turn).
///
/// Four bugs, one fix:
///   Plan A — WJA1508 28R exit overshoot (~120° snap instead of ~90°)
///   Plan B — SKW3078/DAL2581 TAXI A @B10 stall + wrong-direction reissue
///   Plan C — SFO A/T6 and A/T6B fillet arcs with radius=0ft
///   Plan D — OAK G/D junction #1208 arcs with radius=0ft
///
/// Recording (Plans A+B): S1-SFO-2 | Ground Control 28/01, RngSeed 91127251.
/// Plans C+D are pure in-process (no recording).
/// </summary>
public class FilletDiagnosticTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    private static AirportGroundLayout? LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return !File.Exists(path) ? null : GeoJsonParser.Parse("SFO", File.ReadAllText(path), null);
    }

    private static AirportGroundLayout? LoadOak()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return !File.Exists(path) ? null : GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);
    }

    // ─── Plan B: SKW3078 taxi stall ───

    /// <summary>
    /// SKW3078 receives TAXI E A @B10 at t=816. Must advance past segment 19
    /// (the former stall point on arc 1218→1221) within 180 seconds. The
    /// window was originally 120s; widened after GroundConflictDetector got
    /// wingspan-based lateral-clearance skips for parked obstacles, which
    /// changed traffic-interaction timing on this route (now t+138 instead
    /// of t+114).
    /// </summary>
    [Fact]
    public void SKW3078_TaxiAtoB10_AdvancesPastFormerStallSegment()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 816);

        int maxSegReached = -1;
        for (int t = 1; t <= 180; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("SKW3078");
            if (ac?.Ground.AssignedTaxiRoute is { } route)
            {
                if (route.CurrentSegmentIndex > maxSegReached)
                {
                    maxSegReached = route.CurrentSegmentIndex;
                }
            }

            if (ac?.Phases?.CurrentPhase is AtParkingPhase)
            {
                output.WriteLine($"SKW3078 reached parking at t+{t}");
                break;
            }
        }

        output.WriteLine($"SKW3078 max segment reached: {maxSegReached}");
        Assert.True(maxSegReached > 19, $"SKW3078 should advance past segment 19 (former stall point), got {maxSegReached}");
    }

    // ─── Plan C: SFO A/T6 and A/T6B fillet arc geometry ───

    /// <summary>
    /// Each T-junction (A/T6, A/T6A, A/T6B) must have 2 junction arcs, and all
    /// junction arcs must have MinRadiusOfCurvatureFt > 1ft (not degenerate).
    /// </summary>
    [Fact]
    public void SFO_FilletArcs_TTerminalStubs_EachHaveTwoNonDegenerateArcs()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        foreach (string stubTaxiway in new[] { "T6", "T6A", "T6B" })
        {
            var arcs = layout.AllEdges.OfType<GroundArc>().Where(a => a.MatchesTaxiway("A") && a.MatchesTaxiway(stubTaxiway)).ToList();

            output.WriteLine($"A/{stubTaxiway}: {arcs.Count} junction arcs");
            foreach (var arc in arcs)
            {
                output.WriteLine(
                    $"  #{arc.Nodes[0].Id}->{arc.Nodes[1].Id} "
                        + $"radius={arc.MinRadiusOfCurvatureFt:F1}ft "
                        + $"maxSafe={arc.MaxSafeSpeedKts(AircraftCategory.Jet):F1}kt"
                );
            }

            Assert.True(arcs.Count >= 2, $"A/{stubTaxiway}: expected ≥2 arcs, got {arcs.Count}");
            foreach (var arc in arcs)
            {
                Assert.True(
                    arc.MinRadiusOfCurvatureFt > 1.0,
                    $"A/{stubTaxiway} arc #{arc.Nodes[0].Id}->{arc.Nodes[1].Id} has degenerate radius {arc.MinRadiusOfCurvatureFt:F2}ft"
                );
            }
        }
    }

    // ─── Plan D: OAK G/D junction arcs ───

    /// <summary>
    /// All arcs at OAK node #1208 must have a non-degenerate curvature radius.
    /// The D·G junction arcs previously had radius=0ft due to the merge bug; the lateral-accel speed
    /// cap is floored at SlowTurnSpeedKts, so the radius — not the speed — is what catches degeneration.
    /// </summary>
    [Fact]
    public void OAK_GDJunction_AllArcsHaveNonDegenerateRadius()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(1208, out var node), "OAK node 1208 not found");

        var arcs = node!.Edges.OfType<GroundArc>().ToList();
        output.WriteLine($"OAK #1208: {arcs.Count} arcs");

        foreach (var arc in arcs)
        {
            var other = arc.Nodes[0].Id == node.Id ? arc.Nodes[1] : arc.Nodes[0];
            double maxSafe = arc.MaxSafeSpeedKts(AircraftCategory.Jet);
            output.WriteLine($"  -> #{other.Id} {arc.TaxiwayName} radius={arc.MinRadiusOfCurvatureFt:F1}ft maxSafe={maxSafe:F1}kt");

            Assert.True(
                arc.MinRadiusOfCurvatureFt > 1.0,
                $"Arc -> #{other.Id} ({arc.TaxiwayName}) has degenerate radius={arc.MinRadiusOfCurvatureFt:F2}ft"
            );
        }
    }

    /// <summary>
    /// No genuine turn arc (TurnAngleDeg > 30°) should have a degenerate curvature radius.
    /// Near-collinear arcs (≤30°) are exempt. This catches merge-induced bezier corruption on real
    /// turns by checking the radius directly — the lateral-accel speed cap is floored at
    /// SlowTurnSpeedKts, so a collapsed radius no longer surfaces as a near-zero speed.
    /// </summary>
    [Fact]
    public void GenuineTurnArcs_HaveNonDegenerateRadius()
    {
        const double DegenerateRadiusFt = 5.0;
        foreach (string airport in new[] { "SFO", "OAK" })
        {
            string path = Path.Combine("TestData", $"{airport.ToLowerInvariant()}.geojson");
            if (!File.Exists(path))
            {
                continue;
            }

            var layout = GeoJsonParser.Parse(airport, File.ReadAllText(path), null);
            int badCount = 0;
            int genuineTurnCount = 0;
            int collinearExemptCount = 0;

            foreach (var arc in layout.Arcs)
            {
                double maxSafe = arc.MaxSafeSpeedKts(AircraftCategory.Jet);

                if (arc.TurnAngleDeg <= 30.0)
                {
                    collinearExemptCount++;
                    continue;
                }

                genuineTurnCount++;

                if (arc.MinRadiusOfCurvatureFt < DegenerateRadiusFt)
                {
                    output.WriteLine(
                        $"{airport}: degenerate turn arc #{arc.Nodes[0].Id}->{arc.Nodes[1].Id} "
                            + $"{arc.TaxiwayName} turn={arc.TurnAngleDeg:F1}° "
                            + $"radius={arc.MinRadiusOfCurvatureFt:F2}ft "
                            + $"maxSafe={maxSafe:F2}kt origin={arc.Origin}"
                    );
                    badCount++;
                }
            }

            output.WriteLine(
                $"{airport}: {layout.Arcs.Count} total arcs, {genuineTurnCount} genuine turns, "
                    + $"{collinearExemptCount} collinear-exempt, {badCount} degenerate turns"
            );
            Assert.Equal(0, badCount);
        }
    }
}
