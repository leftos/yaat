using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 W5 — the <c>CLRWY</c> pull-forward command. After W1/W2, JBU577 holds short of taxiway B
/// with its tail over RWY 01L/19R (B sits closer than a fuselage length past the runway, so it cannot be
/// both short of B and clear of the runway). <c>CLRWY</c> pulls it forward just until the tail clears the
/// runway's far hold-short bars, then holds in position — clearing the W2 "runway not clear" warning and
/// releasing the occupied runway hold-short node. Valid only from the tail-over-runway hold.
/// Recording: issue172-sfo-taxiing (ZOA/SFO).
/// </summary>
public class Issue172Jbu577ClearRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip";

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

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Clrwy_FromTailOverRunwayHold_PullsForwardClearOfRunwayAndHolds()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        // Replay to JBU577 holding short of B with its tail over the runway. Recorded commands
        // replay only up to t=513 (TAXI B M1 Y @B5 at t=514 would extend the route past B); past
        // the window the sim ticks physics-only (bounded) until the tail-over hold establishes —
        // exit/taxi geometry changes legitimately shift the crossing past the recording's cutoff.
        const int WindowEnd = 513;
        const int PhysicsOnlyEnd = WindowEnd + 60;
        engine.Replay(recording, 0);
        AircraftState? jbu = null;
        for (int t = 1; t <= PhysicsOnlyEnd; t++)
        {
            if (t <= WindowEnd)
            {
                engine.ReplayOneSecond();
            }
            else
            {
                engine.TickOneSecond();
            }

            jbu = engine.FindAircraft("JBU577");
            bool tailOver =
                jbu?.Phases?.CurrentPhase is HoldingShortPhase hp && hp.HoldShort.TailOverRunwayNodeId is not null && jbu.IndicatedAirspeed < 0.5;
            if (tailOver)
            {
                break;
            }
        }

        Assert.NotNull(jbu);
        var holdPhase = jbu.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(holdPhase);
        var runwayNodeId = holdPhase.HoldShort.TailOverRunwayNodeId;
        Assert.NotNull(runwayNodeId);
        var runwayNode = layout.Nodes[runwayNodeId.Value];

        var route = jbu.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"Route: {route.ToSummary()} ({route.Segments.Count} segments)");
        output.WriteLine("  segs: " + string.Join("; ", route.Segments.Select(s => $"{s.TaxiwayName}@{s.ToNodeId}")));
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine(
                $"  HS: node={hs.NodeId} reason={hs.Reason} target={hs.TargetName} cleared={hs.IsCleared} tailOver={hs.TailOverRunwayNodeId}"
            );
        }

        double distRunwayBefore = GeoMath.DistanceNm(jbu.Position, runwayNode.Position) * 6076.12;
        bool occupiedBefore = engine.ComputeOccupiedHoldShortNodes().Contains(runwayNodeId.Value);
        output.WriteLine($"BEFORE: phase={jbu.Phases?.CurrentPhase?.GetType().Name} distRunway={distRunwayBefore:F0} occupied={occupiedBefore}");
        Assert.True(occupiedBefore, "runway node should be occupied while JBU577 holds with its tail over it");

        var result = engine.SendCommand("JBU577", "CLRWY");
        output.WriteLine($"CLRWY echo: {result.Message}");
        Assert.True(result.Success, result.Message);

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            jbu = engine.FindAircraft("JBU577");
            if (jbu is null)
            {
                break;
            }

            if (t % 5 == 0 || jbu.Phases?.CurrentPhase is HoldingInPositionPhase)
            {
                double d = GeoMath.DistanceNm(jbu.Position, runwayNode.Position) * 6076.12;
                NearestNodeHelper.Log(
                    output,
                    $"t={t} phase={jbu.Phases?.CurrentPhase?.GetType().Name ?? "null"} ias={jbu.IndicatedAirspeed:F1} distRunway={d:F0}",
                    jbu,
                    layout
                );
            }
        }

        Assert.NotNull(jbu);
        double distRunwayAfter = GeoMath.DistanceNm(jbu.Position, runwayNode.Position) * 6076.12;
        bool occupiedAfter = engine.ComputeOccupiedHoldShortNodes().Contains(runwayNodeId.Value);
        double lengthFt = FaaAircraftDatabase.Get(jbu.AircraftType)?.LengthFt ?? 123.0;
        output.WriteLine(
            $"AFTER: phase={jbu.Phases?.CurrentPhase?.GetType().Name} ias={jbu.IndicatedAirspeed:F1} distRunway={distRunwayAfter:F0} occupied={occupiedAfter} lengthFt={lengthFt:F0}"
        );

        // Pulled forward, fully clear of the runway, then holds in position.
        Assert.True(
            jbu.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"expected HoldingInPositionPhase after clearing; got {jbu.Phases?.CurrentPhase?.GetType().Name ?? "null"}"
        );
        Assert.True(jbu.IndicatedAirspeed < 0.5, $"aircraft should be stopped; ias={jbu.IndicatedAirspeed:F1}");
        Assert.True(
            distRunwayAfter > distRunwayBefore + 20,
            $"aircraft should have pulled forward away from the runway (distRunway {distRunwayBefore:F0} -> {distRunwayAfter:F0})"
        );

        // Stopped at the ½-length tail-clearance node — tail just clear of the bars (the same node a
        // crossing-with-no-hold-short stops at), NOT walked on to the next junction (#155, ~148 ft past).
        Assert.True(
            distRunwayAfter >= lengthFt * 0.5 - 5,
            $"the tail should be clear of the runway bars (~½ length past); distRunway={distRunwayAfter:F0}, ½ length={lengthFt * 0.5:F0}"
        );
        Assert.True(
            distRunwayAfter <= lengthFt * 0.5 + 30,
            $"should hold at the ½-length tail-clearance node, not continue to the junction; distRunway={distRunwayAfter:F0}"
        );
        Assert.False(occupiedAfter, "the runway node must no longer be occupied once JBU577 has pulled clear");
    }
}
