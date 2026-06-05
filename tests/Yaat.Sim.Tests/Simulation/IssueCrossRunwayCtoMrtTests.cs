using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the cross-runway closed-traffic departure bug (scenario
/// S2-OAK-5 "Practical Exam Preparation / Advanced Concepts", ZOA).
///
/// N629PU (C172) holding short of runway 33 was given <c>CTO MRT 28R</c>
/// ("cleared for takeoff runway 33, make right traffic runway 28R"). The
/// LineUpPhase lined up toward the PATTERN runway (28R) instead of the
/// DEPARTURE runway (33), faulted (aircraft physically at the 33 hold-short),
/// and the aircraft stuck on the ground forever (snapshot t=2090:
/// LineUp.RunwayHeadingDeg=292 = 28R, HoldingShort.RunwayId="33").
///
/// N785Q (also holding short of 33) was given <c>CTO</c> then <c>CTOC</c>
/// (Cancel Takeoff Clearance) mid-lineup. Instead of holding position
/// immediately, it finished lining up onto the runway and then held — CTOC
/// must hold position (7110.65 §3-9-11), never auto-complete the line-up.
///
/// Fix: lineup/takeoff use the departure runway (33) via PhaseList.DepartureRunway;
/// the first circuit climbs out on 33 then joins the 28R pattern; CTOC sets
/// LineUpPhase.HoldPosition so the aircraft stops where it is.
/// </summary>
public class IssueCrossRunwayCtoMrtTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/cross-rwy-cto-mrt-recording.yaat-bug-report-bundle.zip";

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
            .EnableCategory("LineUpPhase", LogLevel.Debug)
            .EnableCategory("DepartureClearanceHandler", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .EnableCategory("MidfieldCrossingPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N629PU_CtoMrt28RFrom33_LinesUpOn33_AndDeparts()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);

            // Restore just before the recorded CTO MRT 28R (t=2053).
            var snapshot = archive.ReadSnapshotAt(2050);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=2050 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var pre = engine.FindAircraft("N629PU");
            Assert.NotNull(pre);
            Assert.IsType<HoldingShortPhase>(pre.Phases?.CurrentPhase);
            double fieldElev = pre.Altitude;
            output.WriteLine(
                $"pre: phase={pre.Phases?.CurrentPhase?.Name} pos=({pre.Position.Lat:F5},{pre.Position.Lon:F5}) onGround={pre.IsOnGround}"
            );

            // Issue the exact command the controller gave.
            var cmd = engine.SendCommand("N629PU", "CTO MRT 28R");
            Assert.True(cmd.Success, $"CTO MRT 28R rejected: {cmd.Message}");

            // The lineup must target runway 33 (true ~344°), NOT 28R (~292°).
            var afterCmd = engine.FindAircraft("N629PU");
            var lineup = afterCmd!.Phases?.Phases.OfType<LineUpPhase>().FirstOrDefault();
            Assert.NotNull(lineup);
            // PatternRunway records the 28R circuit runway; DepartureRunway records 33.
            Assert.Equal("28R", afterCmd.Phases?.PatternRunway?.Designator);
            Assert.Equal("33", afterCmd.Phases?.DepartureRunway?.Designator);

            // Fly it out. Against the buggy code the LineUp faults and the
            // aircraft never leaves the ground.
            bool airborne = false;
            for (int t = 1; t <= 150; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft("N629PU");
                if (ac is null)
                {
                    break;
                }
                if (!ac.IsOnGround && ac.Altitude > fieldElev + 200)
                {
                    airborne = true;
                    output.WriteLine(
                        $"airborne at t+{t}s: alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0} phase={ac.Phases?.CurrentPhase?.Name}"
                    );
                    break;
                }
            }

            Assert.True(airborne, "N629PU never departed runway 33 after CTO MRT 28R (stuck lining up)");

            // After departing it must join the 28R closed-traffic circuit.
            var post = engine.FindAircraft("N629PU");
            Assert.NotNull(post);
            bool inCircuit =
                post.Phases?.Phases.Any(p =>
                    p is UpwindPhase or MidfieldCrossingPhase or DownwindPhase or BasePhase && p.Status != PhaseStatus.Completed
                ) == true;
            Assert.True(inCircuit, $"N629PU not in a pattern circuit; chain=[{Chain(post)}]");
        }
    }

    [Fact]
    public void N629PU_FirstCircuit_UpwindOnDepartureRunway_ThenJoins28R()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);
            var snapshot = archive.ReadSnapshotAt(2050);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var cmd = engine.SendCommand("N629PU", "CTO MRT 28R");
            Assert.True(cmd.Success);

            var ac = engine.FindAircraft("N629PU");
            // First circuit: upwind anchored to the departure runway (33, true
            // ~344°), then a join leg, then the 28R downwind onward.
            var upwind = ac!.Phases?.Phases.OfType<UpwindPhase>().FirstOrDefault();
            Assert.NotNull(upwind);
            Assert.NotNull(upwind!.Waypoints);
            double upwindHdg = upwind.Waypoints!.UpwindHeading.Degrees;
            Assert.True(System.Math.Abs(AngleDiff(upwindHdg, 344.0)) < 25.0, $"First upwind should be on runway 33 (~344°) but was {upwindHdg:F0}°");

            bool hasJoin = ac.Phases?.Phases.Any(p => p is MidfieldCrossingPhase && p.Status != PhaseStatus.Completed) == true;
            Assert.True(hasJoin, $"Expected a cross-runway join leg; chain=[{Chain(ac)}]");
        }
    }

    [Fact]
    public void N785Q_CtocDuringLineUp_HoldsPositionImmediately_ThenResumesOnCto()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);

            // N785Q: HoldingShort of 33 by t=2110, CTO at t=2180. Restore while
            // it is holding short (before the CTO) and drive it with commands —
            // restoring mid-LineUp re-runs OnStart with a stale ctx.Runway and
            // faults (a harness artifact, not the bug under test).
            var snapshot = archive.ReadSnapshotAt(2160);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var pre = engine.FindAircraft("N785Q");
            if (pre?.Phases?.CurrentPhase is not HoldingShortPhase)
            {
                output.WriteLine($"N785Q not holding short at t=2160 (phase={pre?.Phases?.CurrentPhase?.Name}) — skipping");
                return;
            }

            // Clear for takeoff (plain, runway 33) and let it begin lining up.
            Assert.True(engine.SendCommand("N785Q", "CTO").Success);
            bool liningUp = false;
            for (int t = 1; t <= 15; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft("N785Q");
                if (ac?.Phases?.CurrentPhase is LineUpPhase && ac.IndicatedAirspeed > 2.0)
                {
                    liningUp = true;
                    break;
                }
            }
            Assert.True(liningUp, "N785Q never started rolling into the line-up after CTO");

            var ctoc = engine.SendCommand("N785Q", "CTOC");
            Assert.True(ctoc.Success, $"CTOC rejected: {ctoc.Message}");
            Assert.Contains("hold in position", ctoc.Message!, System.StringComparison.OrdinalIgnoreCase);

            // Hold position immediately: the aircraft must NOT complete the
            // line-up and advance to LinedUpAndWaiting; it stays held in LineUp
            // and stops. Buggy code rolls onto the centerline and reaches
            // LinedUpAndWaitingPhase (~t=2210, ~23s later).
            for (int t = 1; t <= 30; t++)
            {
                engine.TickOneSecond();
            }
            var held = engine.FindAircraft("N785Q");
            Assert.NotNull(held);
            Assert.IsType<LineUpPhase>(held.Phases?.CurrentPhase);
            Assert.True(held.IndicatedAirspeed < 1.0, $"N785Q should be stopped (hold position) but IAS={held.IndicatedAirspeed:F1}");

            // Re-clearing for takeoff resumes the line-up and departs.
            var cto = engine.SendCommand("N785Q", "CTO");
            Assert.True(cto.Success, $"CTO rejected: {cto.Message}");
            double fieldElev = held.Altitude;
            bool airborne = false;
            for (int t = 1; t <= 120; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft("N785Q");
                if (ac is null)
                {
                    break;
                }
                if (!ac.IsOnGround && ac.Altitude > fieldElev + 200)
                {
                    airborne = true;
                    break;
                }
            }
            Assert.True(airborne, "N785Q did not resume and depart after re-clearance");
        }
    }

    private static string Chain(AircraftState ac) => string.Join(", ", ac.Phases?.Phases.Select(p => $"{p.GetType().Name}:{p.Status}") ?? []);

    private static double AngleDiff(double a, double b)
    {
        double d = (a - b) % 360.0;
        if (d > 180.0)
        {
            d -= 360.0;
        }
        if (d < -180.0)
        {
            d += 360.0;
        }
        return d;
    }
}
