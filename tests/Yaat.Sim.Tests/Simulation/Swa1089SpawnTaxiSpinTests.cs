using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the SWA1089 spawn-and-preset-taxi spin bug from the
/// S2-OAK-4 (VFR Transitions / Radar Concepts) bundle.
///
/// SWA1089 (B737) is scenario-spawned at KOAK parking "6" with a preset
/// <c>TAXI TC U W W1 30</c> and spawn delay = 1790 s. Immediately after spawn,
/// the aircraft is essentially stuck rotating in place: heading swings 80-100°
/// per 5-second sample while position changes &lt; 10 ft per sample, and IAS
/// decays from ~15 kts down to ~2.7 kts over 60 s. The user gives up and
/// manually warps + re-taxis at t=1851 (which works correctly).
///
/// Same family as <c>project_fillet_arc_natural_forward.md</c> and the
/// <see cref="OakTaxiJcSpinTests"/> spin: an aircraft on a tight ramp arc with
/// a steering target that doesn't match its actual position settles into a
/// fixed-rate heading chase that never converges at low taxi speeds.
///
/// **Replay strategy:** Full replay from t=0 so the scenario-level spawn
/// (spawn delay 1790 s) and the preset <c>TAXI TC U W W1 30</c> both fire
/// under current code. We don't want to restore the captured snapshot of the
/// already-built route because that hides whether the bug is in route
/// construction vs. steering.
/// </summary>
public class Swa1089SpawnTaxiSpinTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/swa1089-spawn-taxi-spin-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "SWA1089";

    /// <summary>Scenario time at which SWA1089 spawns (spawn delay = 1790 s).</summary>
    private const int SpawnAtSeconds = 1790;

    /// <summary>Seconds after spawn before the user issued WARPG to bail out.</summary>
    private const int TicksToObserve = 60;

    /// <summary>Minimum forward progress (feet) we require in the observation window.</summary>
    private const double MinProgressFeet = 500.0;

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

    /// <summary>
    /// Diagnostic dump: per-tick state of SWA1089 from spawn (t=1790) through
    /// t=1850. Records heading, IAS, position, route segment, nearest nodes.
    /// No assertion - just used to confirm the bug reproduces and to inspect
    /// what FindNearestNode / ResolveExplicitPath produced for the preset
    /// taxi command at spawn.
    /// </summary>
    [Fact]
    public void Diagnostic_LogSpawnAndTaxiTrajectory()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, SpawnAtSeconds);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        DumpAircraftState(ac, layout, t: 0);
        DumpRoute(ac);

        var spawnPos = ac.Position;

        for (int t = 1; t <= TicksToObserve; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                output.WriteLine($"t=+{t, 3} aircraft removed");
                break;
            }

            if (t % 5 == 0 || t <= 10)
            {
                DumpAircraftState(ac, layout, t);
            }
        }

        if (ac is not null)
        {
            double movedFt = GeoMath.DistanceNm(spawnPos, ac.Position) * 6076.12;
            output.WriteLine($"Total displacement from spawn over {TicksToObserve}s: {movedFt:F0} ft");
        }
    }

    /// <summary>
    /// Assertion: SWA1089 must make forward progress after spawn + preset
    /// TAXI. A B737 taxiing at the 30 kt default ground speed covers roughly
    /// 50 ft/s; in 60 s a healthy taxi covers 1500-3000 ft. The bug today
    /// produces &lt; 300 ft of net displacement (position oscillates near the
    /// initial location). We require ≥ 500 ft to leave generous margin while
    /// still firmly catching the spin.
    /// </summary>
    [Fact]
    public void Spawn_TaxiPresetMakesForwardProgress()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, SpawnAtSeconds);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        var spawnPos = ac.Position;

        for (int t = 1; t <= TicksToObserve; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
        }

        double movedFt = GeoMath.DistanceNm(spawnPos, ac!.Position) * 6076.12;
        output.WriteLine(
            $"After {TicksToObserve}s: moved={movedFt:F0}ft hdg={ac.TrueHeading.Degrees:F0} "
                + $"ias={ac.IndicatedAirspeed:F1} segIdx={ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex}"
        );

        Assert.True(
            movedFt >= MinProgressFeet,
            $"SWA1089 only moved {movedFt:F0} ft in {TicksToObserve}s after spawn + preset TAXI - "
                + $"expected ≥ {MinProgressFeet:F0} ft. The aircraft is spinning in place near the parking spot."
        );
    }

    private void DumpAircraftState(AircraftState ac, AirportGroundLayout layout, int t)
    {
        var route = ac.Ground.AssignedTaxiRoute;
        string segDesc = "(no route)";
        if (route is not null && route.CurrentSegmentIndex < route.Segments.Count)
        {
            var seg = route.Segments[route.CurrentSegmentIndex];
            segDesc = $"seg[{route.CurrentSegmentIndex}]={seg.FromNodeId}->{seg.ToNodeId} {seg.TaxiwayName}";
        }

        output.WriteLine(
            $"t=+{t, 3} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F1} "
                + $"ias={ac.IndicatedAirspeed:F1} phase={ac.Phases?.CurrentPhase?.Name} {segDesc}"
        );
        NearestNodeHelper.Log(output, $"  t=+{t, 3}", ac, layout);
    }

    private void DumpRoute(AircraftState ac)
    {
        var route = ac.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            output.WriteLine("Route: (none)");
            return;
        }

        output.WriteLine($"Route: \"{route.ToSummary()}\"  CurrentSegmentIndex={route.CurrentSegmentIndex}");
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var s = route.Segments[i];
            output.WriteLine($"  [{i, 2}] {s.FromNodeId, 4} -> {s.ToNodeId, 4}  {s.TaxiwayName}");
        }
        if (route.HoldShortPoints.Count > 0)
        {
            output.WriteLine("HoldShortPoints:");
            foreach (var hs in route.HoldShortPoints)
            {
                output.WriteLine($"  node={hs.NodeId} target={hs.TargetName} reason={hs.Reason}");
            }
        }
    }
}
