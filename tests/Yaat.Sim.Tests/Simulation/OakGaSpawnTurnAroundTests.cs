using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the OAK GA-spawn turn-around bug surfaced from the S2-OAK-4
/// VFR Transitions/Radar Concepts bundle.
///
/// N346G (parking GA3, spawn heading 290°, preset `TAXI C B 28R`) and N172SP
/// (parking GA7, spawn heading 135°, preset `TAXI D C B 28R`) make a wide
/// ~270° clockwise turn instead of the natural short-way turn when leaving
/// parking onto their first taxiway. Snapshot heading samples (every 5 s):
///
///   N346G: 290 -> 209 -> 266 ->  60 -> 126 -> 111 -> 113
///   N172SP: 135 -> 244 -> 260 ->  48 -> 183 -> 164 -> 164
///
/// Both rotations sweep CW the long way through nearly a full revolution.
/// Two suspects surfaced during plan investigation:
///   (a) The recorded `AssignedTaxiRoute` for N346G starts at node 619 (GA1),
///       not 621 (GA3) where the aircraft sits — `GroundCommandHandler.TryTaxi`
///       calls `groundLayout.FindNearestNode(aircraft.Position)` and may resolve
///       to a sibling parking spot.
///   (b) The fillet pass leaves duplicate collinear parking connectors at GA3
///       (#1222 / #1224 both bearing 209.1°) and GA7 (#1396 / #1397 both 243.1°),
///       not collapsed by the dedup step that runs after `phase-d-shorten`.
///
/// This file currently holds the diagnostic test that drives the investigation.
/// Once the diagnostic identifies the exact failure mode, an assertion test
/// is added that fails on `main` and passes after the fix.
/// </summary>
public class OakGaSpawnTurnAroundTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-ga-spawn-turnaround-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", LogLevel.Debug)
            .EnableCategory("GroundNavigator", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null) : null;
    }

    /// <summary>
    /// Diagnostic: replay 0..30 s and log per-tick state for N346G and N172SP.
    /// What we want to see:
    ///   • the chosen `AssignedTaxiRoute` (which start node? which path?),
    ///   • per-tick TrueHeading + IAS + position to confirm the CW spiral,
    ///   • the 3 closest ground nodes (NearestNodeHelper) so we know which edge
    ///     the aircraft is currently chasing,
    ///   • what the navigator's TargetTrueHeading is each tick (drives the turn
    ///     direction selection — if this jumps from short-way to long-way, the
    ///     bug is in the heading-target logic; if the route itself includes a
    ///     visit to the wrong sibling node, the bug is upstream in routing).
    /// </summary>
    [Fact]
    public void Diagnostic_LogParkingDeparture_N346G_N172SP()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        var layout = LoadOakLayout();
        if (recording is null || engine is null || layout is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        output.WriteLine("=== Replaying OAK GA spawn turn-around (0..30 s) ===");
        output.WriteLine($"Bundle: {RecordingPath}");
        output.WriteLine("");

        int total = (int)Math.Min(30, recording.TotalElapsedSeconds);
        for (int t = 0; t <= total; t++)
        {
            if (t > 0)
            {
                engine.ReplayOneSecond();
            }

            LogAircraftTick("N346G", t, engine, layout);
            LogAircraftTick("N172SP", t, engine, layout);
        }
    }

    /// <summary>
    /// Diagnostic: build the arc primitive for the GA3 ramp -> taxi-C transition
    /// (1222 -> 1221) and the GA7 ramp -> taxi-D transition (1396 -> 1395) and
    /// print the computed turn direction + sweep + center, so we can see
    /// directly whether `PathPrimitiveBuilder.BuildArc` is choosing the right
    /// way around. The bug observed in `Diagnostic_LogParkingDeparture_*` shows
    /// the aircraft sweeping ~270 degrees CW when only ~90 degrees CCW is
    /// required — this test localizes the failure to the arc primitive vs the
    /// navigator's tracking logic.
    /// </summary>
    [Fact]
    public void Diagnostic_BuildArcPrimitive_GA3_RampToC_And_GA7_RampToD()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        DescribeArc(layout, fromId: 1222, toId: 1221, label: "GA3 RAMP->C (1222->1221)");
        DescribeArc(layout, fromId: 1396, toId: 1395, label: "GA7 RAMP->D (1396->1395)");
    }

    private void DescribeArc(AirportGroundLayout layout, int fromId, int toId, string label)
    {
        var fromNode = layout.Nodes.GetValueOrDefault(fromId);
        var toNode = layout.Nodes.GetValueOrDefault(toId);
        Assert.NotNull(fromNode);
        Assert.NotNull(toNode);

        var edge = fromNode.Edges.FirstOrDefault(e => e.OtherNodeId(fromId) == toId && e is GroundArc);
        Assert.NotNull(edge);

        var dirEdge = new DirectionalEdge
        {
            Edge = edge,
            FromNode = fromNode,
            ToNode = toNode,
        };

        double dep = dirEdge.DepartureBearing;
        double arr = dirEdge.ArrivalBearing;
        double dthetaDeg = (((arr - dep) + 540.0) % 360.0) - 180.0;

        var seg = new TaxiRouteSegment { TaxiwayName = edge!.TaxiwayName, Edge = dirEdge };
        var prim = PathPrimitiveBuilder.FromSegment(seg);

        output.WriteLine($"=== {label} ===");
        output.WriteLine($"  DepartureBearing (entry tangent): {dep:F1} deg");
        output.WriteLine($"  ArrivalBearing   (exit tangent):  {arr:F1} deg");
        output.WriteLine($"  signed dtheta (short-way exit-entry): {dthetaDeg:F1} deg");
        output.WriteLine($"  -> short-way turn is {(dthetaDeg > 0 ? "RIGHT (CW)" : "LEFT (CCW)")} {Math.Abs(dthetaDeg):F1} deg");

        if (prim is PathPrimitiveArc arcPrim)
        {
            output.WriteLine(
                $"  primitive: ARC RightTurn={arcPrim.RightTurn} sweep={arcPrim.SweepDeg:F1} deg radius={arcPrim.RadiusFt:F0} ft len={arcPrim.LengthFt:F0} ft"
            );
            output.WriteLine($"  primitive: entryTangent={arcPrim.EntryTangentBearingDeg:F1} exitTangent={arcPrim.ExitTangentBearingDeg:F1}");
            output.WriteLine(
                $"  primitive: center=({arcPrim.CenterLat:F6},{arcPrim.CenterLon:F6}) startBearingFromCenter={arcPrim.StartBearingFromCenterDeg:F1}"
            );
        }
        else
        {
            output.WriteLine($"  primitive (NOT arc): {prim.Kind}");
        }

        output.WriteLine("");
    }

    /// <summary>
    /// Assertion: neither N346G nor N172SP should accumulate more than 220° of
    /// total heading rotation from spawn through t=30 s. A natural taxi-out
    /// from these parking spots requires ~150-180° of cumulative rotation
    /// (short-way connector + short-way arc onto the taxiway). The bug under
    /// investigation produces ~270-400° of cumulative CW rotation because the
    /// pathfinder picks an arc that lands the aircraft heading opposite the
    /// next segment's direction, forcing a 180° reversal at the arc endpoint.
    ///
    /// This test fails on `main` (the buggy code) and should pass after the
    /// pathfinder learns to penalize routes with large abrupt heading
    /// reversals at non-parking nodes.
    /// </summary>
    [Theory]
    [InlineData("N346G", 220.0)]
    [InlineData("N172SP", 220.0)]
    public void TaxiOut_DoesNotSpinNearlyFullCircle(string callsign, double maxCumulativeDeg)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);

        double prevHdg = ac.TrueHeading.Degrees;
        double cumulativeAbs = 0;
        double cumulativeSigned = 0;

        int total = (int)Math.Min(30, recording.TotalElapsedSeconds);
        for (int t = 1; t <= total; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);

            double curHdg = ac.TrueHeading.Degrees;
            double delta = (((curHdg - prevHdg) + 540.0) % 360.0) - 180.0;
            cumulativeAbs += Math.Abs(delta);
            cumulativeSigned += delta;
            prevHdg = curHdg;
        }

        output.WriteLine(
            $"{callsign}: cumulativeAbs={cumulativeAbs:F0}deg cumulativeSigned={cumulativeSigned:F0}deg (max allowed {maxCumulativeDeg:F0})"
        );
        Assert.True(
            cumulativeAbs <= maxCumulativeDeg,
            $"{callsign} accumulated {cumulativeAbs:F0}deg of rotation in 30s — expected <= {maxCumulativeDeg:F0}deg "
                + $"(short-way taxi out of parking should be ~150-180deg). Spinning the long way around an arc "
                + $"whose tangent opposes the next route segment is the symptom."
        );
    }

    private void LogAircraftTick(string callsign, int t, SimulationEngine engine, AirportGroundLayout layout)
    {
        var ac = engine.FindAircraft(callsign);
        if (ac is null)
        {
            output.WriteLine($"t={t, 2} {callsign, -7} (not found)");
            return;
        }

        var ground = ac.Ground;
        string route = ground?.AssignedTaxiRoute is { Segments.Count: > 0 } r
            ? string.Join(" | ", r.Segments.Take(4).Select(s => $"{s.FromNodeId}->{s.ToNodeId}({s.TaxiwayName})"))
            : "(no route)";

        double tgtHdg = ac.Targets.TargetTrueHeading?.Degrees ?? double.NaN;
        string nearest = NearestNodeHelper.Describe(ac, layout, count: 3);

        output.WriteLine(
            $"t={t, 2} {callsign, -7} hdg={ac.TrueHeading.Degrees, 6:F1} tgt={tgtHdg, 6:F1} ias={ac.IndicatedAirspeed, 5:F1} "
                + $"pos=({ac.Position.Lat:F5},{ac.Position.Lon:F5}) "
                + $"nearest=[{nearest}] "
                + $"route={route}"
        );
    }
}
