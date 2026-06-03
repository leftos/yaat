using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 sub-case (JBU577 taxi spin): JBU577 landed at SFO, was given
/// <c>TAXI G B HS B</c> + <c>CROSS</c>, then at t=514 <c>TAXI B M1 Y @B5</c>
/// (echo <c>G B M1 Y AY2 A M2 M3 RAMP @B5</c>). It then turned back toward the runway and
/// toward B again — failing to settle on a direction at one of the route's junctions — and the
/// controller deleted it at t=878. This reproduces the taxi and asserts it does not orbit.
///
/// Recording: issue172-sfo-taxiing-recording (ZOA/SFO).
/// </summary>
public class Issue172Jbu577TaxiSpinTests(ITestOutputHelper output)
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

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundNavigator", LogLevel.Debug)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .EnableCategory("RunwayExitPhase", LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Jbu577_TickByTick_Diagnostic()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        for (int t = 1; t <= 880; t++)
        {
            try
            {
                engine.ReplayOneSecond();
            }
            catch (Exception ex)
            {
                output.WriteLine($"t={t}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                break;
            }

            var ac = engine.FindAircraft("JBU577");
            if (ac is null)
            {
                if (t >= 510)
                {
                    output.WriteLine($"t={t}: (despawned)");
                }

                continue;
            }

            if (t < 420)
            {
                continue;
            }

            var route = ac.Ground.AssignedTaxiRoute;
            string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            int segIdx = route?.CurrentSegmentIndex ?? -1;
            int segTotal = route?.Segments.Count ?? 0;
            int target = (route is not null && segIdx >= 0 && segIdx < segTotal) ? route.Segments[segIdx].ToNodeId : -1;
            string sl = ac.Ground.SpeedLimit is { } s ? $"{s:F0}" : "-";
            string ay = ac.Ground.AutoYieldTarget ?? "-";
            output.WriteLine(
                $"t={t, 3} ias={ac.IndicatedAirspeed, 5:F1} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees, 5:F1} phase={phaseName} seg={segIdx}/{segTotal}->{target} sl={sl} ay={ay} cbreak={ac.Ground.ConflictBreakRemainingSeconds:F0}"
            );
        }
    }

    /// <summary>
    /// Tick the engine forward past the recording's t=878 delete so the full taxi to @B5 plays out
    /// (the controller deleted JBU577 before it finished). Replays to t=600 (post-crossing, taxiing
    /// the resolved route), then advances with <see cref="SimulationEngine.TickOneSecond"/> — which
    /// does not apply the recorded DEL — and logs the trajectory.
    /// </summary>
    [Fact]
    public void Jbu577_TickForwardPastDelete_Diagnostic()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 446);
        var ac0 = engine.FindAircraft("JBU577");
        if (ac0 is null)
        {
            output.WriteLine("JBU577 not present at t=446");
            return;
        }

        var r0 = ac0.Ground.AssignedTaxiRoute;
        output.WriteLine($"@446 route ({r0?.Segments.Count} segs): {r0?.ToSummary()}  curIdx={r0?.CurrentSegmentIndex}");
        if (r0 is not null)
        {
            var layout = new TestAirportGroundData().GetLayout("SFO");
            for (int i = 0; i < r0.Segments.Count; i++)
            {
                var s = r0.Segments[i];
                double brg =
                    layout is not null && layout.Nodes.TryGetValue(s.FromNodeId, out var fn) && layout.Nodes.TryGetValue(s.ToNodeId, out var tn)
                        ? GeoMath.BearingTo(fn.Position, tn.Position)
                        : double.NaN;
                output.WriteLine(
                    $"  seg[{i}] {s.TaxiwayName, -6} {s.FromNodeId, 5}->{s.ToNodeId, -5} brg={brg, 5:F1} edgeArr={s.Edge.ArrivalBearing, 5:F1} dep={s.Edge.DepartureBearing, 5:F1}"
                );
            }
        }

        for (int t = 601; t <= 1000; t++)
        {
            try
            {
                engine.TickOneSecond();
            }
            catch (Exception ex)
            {
                output.WriteLine($"t={t}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                break;
            }

            var ac = engine.FindAircraft("JBU577");
            if (ac is null)
            {
                output.WriteLine($"t={t}: (gone)");
                break;
            }

            var route = ac.Ground.AssignedTaxiRoute;
            string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            int segIdx = route?.CurrentSegmentIndex ?? -1;
            int segTotal = route?.Segments.Count ?? 0;
            output.WriteLine(
                $"t={t, 4} ias={ac.IndicatedAirspeed, 5:F1} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees, 5:F1} phase={phaseName} seg={segIdx}/{segTotal}"
            );
        }
    }
}
