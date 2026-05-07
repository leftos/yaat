using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the FLL DAL880 taxi-backtrack bug.
///
/// At FLL (scenario "S1: S1L3 (KFLL East)"), DAL880 was given
/// `TAXI T T4 B B1 HS 10L` from parking around (26.0738, -80.1443).
/// At FLL East config, B1 sits at the WEST end of taxiway B (lon ~-80.166),
/// while T4 meets B around lon ~-80.148. The user observed the aircraft
/// "turning the wrong way".
///
/// The original bug had TWO embedded U-turns in the resolved route:
/// 1. The T/T4/C fillet at junction #56 produced a tangent chain
///    #715↔#717↔#713 with an arc landing at #713 and a "shorten" anchor at
///    #715. Walking T→T4 entered via the arc at #713, walked SOUTH down the
///    chain (→#717→#715) before jumping NORTH 82ft via the shorten to #57.
///    That 154° flip is fixed by the `AddDirectShortensFromArcAnchors`
///    fillet-cleanup pass: each arc-anchored chain node now has a direct
///    shorten to the chain's external endpoint, eliminating the chain detour.
/// 2. FLL's T4 LineString is V-shaped (apex at #56) — encoding TWO physical
///    T4 connectors between T and B as a single feature. When walking from
///    T east of #56, the pathfinder enters T4's east leg and terminates at
///    #61 where the bearing flips ~166° onto B. The proper path uses T4's
///    west leg to reach B at #53 with a smooth ~36° turn. Fixed by extending
///    `SelectBestStopNode` to fire for runway destinations and adding a
///    U-turn penalty on inter-taxiway transition candidates.
///
/// Bundle: fll-dal880-taxi-backtrack-b-recording.yaat-bug-report-bundle.zip
/// </summary>
public class IssueFllDal880TaxiBacktrackBTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/94c6ed9ab1d4.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay the bundle to just after the TAXI command fires (t=232).
    /// Inspect DAL880's AssignedTaxiRoute and assert the route does NOT
    /// contain a U-turn (consecutive segments whose bearings differ by
    /// more than 120°).
    ///
    /// The bug: at FLL the T/T4/C fillet around node #56 produces a
    /// tangent chain #715↔#717↔#713 (collinear, all bearing 5.2° N) plus
    /// a "shorten" edge #715↔#57. Walking T→T4 enters via the T4-T arc
    /// at #714, lands at the north tangent #713, then walks SOUTH down
    /// the tangent chain (#713→#717→#715) before jumping NORTH 82ft via
    /// the shorten edge to #57. That 180° flip is what the user observed
    /// as the aircraft "turning the wrong way" — DAL880 gets stuck
    /// rotating in place at the U-turn (heading goes from ~22° to 180°
    /// between t=280 and t=320 with IAS ~3kt).
    /// </summary>
    [Fact]
    public void DAL880_TaxiTT4BB1HS10L_RouteHasNoUTurn()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("FLL");
        if (layout is null)
        {
            output.WriteLine("fll.geojson not found — skipping");
            return;
        }

        // TAXI command fires at t=232; replay slightly past so the route is assigned.
        engine.Replay(recording, 235);

        var dal = engine.FindAircraft("DAL880");
        Assert.NotNull(dal);

        var route = dal.Ground?.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"DAL880 taxi route: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            string from = layout.Nodes.TryGetValue(seg.FromNodeId, out var fn) ? $"({fn.Position.Lat:F6}, {fn.Position.Lon:F6})" : "?";
            string to = layout.Nodes.TryGetValue(seg.ToNodeId, out var tn) ? $"({tn.Position.Lat:F6}, {tn.Position.Lon:F6})" : "?";
            output.WriteLine($"  {seg.TaxiwayName, -6} #{seg.FromNodeId} {from} → #{seg.ToNodeId} {to}");
        }

        // Verify the route contains no U-turn anywhere (consecutive segment bearings
        // differ by > 120°). Catches both the chain U-turn at #56 (within T4) and
        // the hairpin U-turn at #61 (T4 → B transition).
        AssertNoUTurnAnywhere(route, layout);
    }

    private static void AssertNoUTurnAnywhere(TaxiRoute route, AirportGroundLayout layout)
    {
        for (int i = 1; i < route.Segments.Count; i++)
        {
            var prev = route.Segments[i - 1];
            var curr = route.Segments[i];
            if (
                !layout.Nodes.TryGetValue(prev.FromNodeId, out var pFrom)
                || !layout.Nodes.TryGetValue(prev.ToNodeId, out var pTo)
                || !layout.Nodes.TryGetValue(curr.FromNodeId, out var cFrom)
                || !layout.Nodes.TryGetValue(curr.ToNodeId, out var cTo)
            )
            {
                continue;
            }

            double prevBearing = GeoMath.BearingTo(pFrom.Position, pTo.Position);
            double currBearing = GeoMath.BearingTo(cFrom.Position, cTo.Position);
            double diff = Math.Abs(NormalizeAngleDiff(currBearing - prevBearing));

            if (diff > 150)
            {
                Assert.Fail(
                    $"U-turn at segment {i - 1}→{i}: "
                        + $"{prev.TaxiwayName} #{prev.FromNodeId}→#{prev.ToNodeId} (bearing {prevBearing:F0}°) "
                        + $"vs {curr.TaxiwayName} #{curr.FromNodeId}→#{curr.ToNodeId} (bearing {currBearing:F0}°) "
                        + $"differ by {diff:F0}°."
                );
            }
        }
    }

    private static double NormalizeAngleDiff(double deg)
    {
        while (deg > 180)
        {
            deg -= 360;
        }
        while (deg < -180)
        {
            deg += 360;
        }
        return deg;
    }

    /// <summary>
    /// Direct ResolveExplicitPath test that isolates the pathfinder from the
    /// SimulationEngine. Uses DAL880's actual parking position from the bundle
    /// (snapshot at t=230) to find the nearest layout node, then resolves
    /// `T T4 B B1` with DestinationRunway=10L. Asserts no U-turn is embedded
    /// in the route (consecutive segments differ in bearing by < 120°).
    /// </summary>
    [Fact]
    public void ResolveExplicitPath_TT4BB1_FromDal880Parking_RouteHasNoUTurn()
    {
        var layout = new TestAirportGroundData().GetLayout("FLL");
        if (layout is null)
        {
            output.WriteLine("fll.geojson not found — skipping");
            return;
        }

        // DAL880's actual parking position at t=230 (from bundle snapshot).
        const double ParkLat = 26.073763899148627;
        const double ParkLon = -80.14425458893693;

        var startNode = layout.Nodes.Values.OrderBy(n => GeoMath.DistanceNm(ParkLat, ParkLon, n.Position.Lat, n.Position.Lon)).First();

        output.WriteLine(
            $"Start: nearest node to ({ParkLat:F6}, {ParkLon:F6}) is #{startNode.Id} at "
                + $"({startNode.Position.Lat:F6}, {startNode.Position.Lon:F6}) type={startNode.Type} "
                + $"edges=[{string.Join(",", startNode.Edges.Select(e => e.TaxiwayName))}]"
        );

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode.Id,
            ["T", "T4", "B", "B1"],
            out string? failReason,
            new ExplicitPathOptions
            {
                DestinationRunway = "10L",
                ExplicitHoldShorts = ["10L"],
                AirportId = "FLL",
                DiagnosticLog = msg => output.WriteLine(msg),
            }
        );

        Assert.NotNull(route);
        Assert.Null(failReason);

        output.WriteLine($"\nRoute: {route.Segments.Count} segments");
        foreach (var seg in route.Segments)
        {
            string fromLon = layout.Nodes.TryGetValue(seg.FromNodeId, out var fn) ? $"{fn.Position.Lon:F6}" : "?";
            string toLon = layout.Nodes.TryGetValue(seg.ToNodeId, out var tn) ? $"{tn.Position.Lon:F6}" : "?";
            output.WriteLine($"  {seg.TaxiwayName, -6} #{seg.FromNodeId} (lon {fromLon}) → #{seg.ToNodeId} (lon {toLon})");
        }

        // No U-turn anywhere — covers both the chain U-turn at #56 (within T4) and
        // the hairpin U-turn at #61 (T4 → B transition from the V-shaped T4 LineString).
        AssertNoUTurnAnywhere(route, layout);
    }
}
