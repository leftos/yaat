using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #233: SKW3396 (parked at SFO gate D2) told PUSH $5A pushes
/// tail-first ~999ft down taxiway T5 onto taxiway Alpha (the junction node), pivots, then
/// reverses back NW to spot 5A. Root cause: pushback-to-spot built a graph A* taxi route,
/// and the SFO graph's only path from D2 to spot 5A is forced through the taxiway-A junction.
///
/// A pushback is a nonmovement-area tug reverse — it must reverse directly to the spot and
/// never route onto an ATC-controlled taxiway (Alpha) to get there.
///
/// Recording: S1-SFO-2 | Ground Control 28/01. SKW3396 spawns at t=400 at gate D2 (stationary).
/// The offending PUSH $5A is issued live after replay (it is not in the recording's actions).
/// </summary>
public class Issue233SfoPushToSpotTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue233-sfo-push5a-recording.yaat-bug-report-bundle.zip";

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

    [Fact]
    public void SKW3396_PushToSpot5A_ReversesDirectly_NeverOntoTaxiwayA()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // SKW3396 spawns at t=400 (spawnDelay 400) at gate D2 and sits AtParking.
        engine.Replay(recording, 420);

        var ac = engine.FindAircraft("SKW3396");
        Assert.NotNull(ac);
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);

        // Isolate SKW3396: the routing fix is under test, not ground-conflict handling. The recorded
        // session has traffic packed around gate D2 (another aircraft stacked on the same stand) that
        // would stall any pushback via conflict detection, masking the trajectory.
        foreach (var other in engine.World.GetSnapshot())
        {
            if (!string.Equals(other.Callsign, "SKW3396", StringComparison.Ordinal))
            {
                engine.World.RemoveAircraft(other.Callsign);
            }
        }

        var layout = engine.World.GroundLayout;
        Assert.NotNull(layout);

        var spot5A = layout.FindSpotNodeByName("5A");
        Assert.NotNull(spot5A);

        // The taxiway-A junction the buggy route drives onto: nearest taxiway-A node to spot 5A.
        var junctionA = layout
            .GetNodesOnTaxiway("A")
            .OrderBy(n => GeoMath.DistanceNm(n.Position.Lat, n.Position.Lon, spot5A.Position.Lat, spot5A.Position.Lon))
            .FirstOrDefault();
        Assert.NotNull(junctionA);

        var startPos = ac.Position;
        double startToSpotFt = GeoMath.DistanceNm(startPos.Lat, startPos.Lon, spot5A.Position.Lat, spot5A.Position.Lon) * GeoMath.FeetPerNm;
        double spotToJunctionFt =
            GeoMath.DistanceNm(spot5A.Position.Lat, spot5A.Position.Lon, junctionA.Position.Lat, junctionA.Position.Lon) * GeoMath.FeetPerNm;
        output.WriteLine($"start->spot5A = {startToSpotFt:F0}ft; spot5A->junctionA(#{junctionA.Id}) = {spotToJunctionFt:F0}ft");

        var result = engine.SendCommand("SKW3396", "PUSH $5A");
        Assert.True(result.Success, $"PUSH $5A failed: {result.Message}");
        output.WriteLine($"Command: PUSH $5A -> {result.Message}; phase={ac.Phases?.CurrentPhase?.Name}");

        // Deterministic, traffic-independent: a pushback must never route through an ATC-controlled
        // taxiway to reach a ramp spot. The direct reverse sets no taxi route; the buggy graph route
        // traversed taxiway-A nodes (e.g. the junction). If a route is assigned, none of its nodes
        // may lie on taxiway A.
        var aNodeIds = layout.GetNodesOnTaxiway("A").Select(n => n.Id).ToHashSet();
        var pushRoute = ac.Ground.AssignedTaxiRoute;
        if (pushRoute is not null)
        {
            bool routesOntoA = pushRoute.Segments.Any(s => aNodeIds.Contains(s.FromNodeId) || aNodeIds.Contains(s.ToNodeId));
            Assert.False(
                routesOntoA,
                "Pushback to spot 5A routed onto taxiway A — a pushback must reverse directly to the spot, not taxi through Alpha"
            );
        }

        double maxDistFromStartFt = 0;
        double minDistToJunctionFt = double.MaxValue;
        double maxGroundSpeed = 0;
        bool reachedParking = false;

        output.WriteLine($"{"t", 4} {"gs", 6} {"distStart", 9} {"distJunc", 9} {"distSpot", 9} {"push", 5} {"nose", 5} {"phase", -16}");
        for (int tick = 1; tick <= 180; tick++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("SKW3396");
            Assert.NotNull(ac);

            double distStart = GeoMath.DistanceNm(startPos.Lat, startPos.Lon, ac.Position.Lat, ac.Position.Lon) * GeoMath.FeetPerNm;
            double distJunc =
                GeoMath.DistanceNm(junctionA.Position.Lat, junctionA.Position.Lon, ac.Position.Lat, ac.Position.Lon) * GeoMath.FeetPerNm;
            double distSpot = GeoMath.DistanceNm(spot5A.Position.Lat, spot5A.Position.Lon, ac.Position.Lat, ac.Position.Lon) * GeoMath.FeetPerNm;

            maxDistFromStartFt = Math.Max(maxDistFromStartFt, distStart);
            minDistToJunctionFt = Math.Min(minDistToJunctionFt, distJunc);
            maxGroundSpeed = Math.Max(maxGroundSpeed, ac.GroundSpeed);

            if (tick % 5 == 0 || ac.Phases?.CurrentPhase is AtParkingPhase)
            {
                output.WriteLine(
                    $"{tick, 4} {ac.GroundSpeed, 6:F2} {distStart, 9:F0} {distJunc, 9:F0} {distSpot, 9:F0} "
                        + $"{ac.Ground.PushbackTrueHeading?.Degrees ?? -1, 5:F0} {ac.TrueHeading.Degrees, 5:F0} {ac.Phases?.CurrentPhase?.Name ?? "null", -16}"
                );
            }

            if (ac.Phases?.CurrentPhase is AtParkingPhase)
            {
                reachedParking = true;
                break;
            }
        }

        double finalDistToSpotFt = GeoMath.DistanceNm(spot5A.Position.Lat, spot5A.Position.Lon, ac.Position.Lat, ac.Position.Lon) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"maxDistFromStart={maxDistFromStartFt:F0}ft minDistToJunction={minDistToJunctionFt:F0}ft "
                + $"maxGs={maxGroundSpeed:F1}kt finalDistToSpot={finalDistToSpotFt:F0}ft reachedParking={reachedParking}"
        );

        // 1. Never detour past the spot: the direct reverse stays within a small margin of the
        //    gate->spot chord. The bug ran ~999ft (1.9x the 529ft chord) down to the junction.
        Assert.True(
            maxDistFromStartFt <= startToSpotFt * 1.3,
            $"Aircraft detoured {maxDistFromStartFt:F0}ft from the gate (> 1.3x the {startToSpotFt:F0}ft gate->spot chord) — it overshot toward taxiway A"
        );

        // 2. Never reaches the taxiway-A junction. The direct reverse stays >= spot->junction (~171ft)
        //    away; the bug drove the tail onto the junction node (~0ft).
        Assert.True(
            minDistToJunctionFt >= 100,
            $"Aircraft came within {minDistToJunctionFt:F0}ft of the taxiway-A junction node #{junctionA.Id} — a pushback must not route onto taxiway Alpha"
        );

        // 3. Pushback speed stays within tug limits (<= 5kt, small epsilon).
        Assert.True(maxGroundSpeed <= 5.5, $"Pushback ground speed peaked at {maxGroundSpeed:F1}kt, above the ~5kt tug limit");

        // 4. Ends parked at spot 5A.
        Assert.True(reachedParking, $"Pushback should complete to AtParkingPhase, got: {ac.Phases?.CurrentPhase?.Name ?? "null"}");
        Assert.True(finalDistToSpotFt <= 40, $"Aircraft should end at spot 5A, but is {finalDistToSpotFt:F0}ft away");
    }
}
