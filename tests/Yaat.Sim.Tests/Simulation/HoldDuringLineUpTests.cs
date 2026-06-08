using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the "HOLD ignored during line-up" bug (scenario S2-OAK-5
/// "Practical Exam Preparation / Advanced Concepts", ZOA).
///
/// N86687 (C180) was holding short of runway 28R, given <c>LUAW</c>, and while
/// it was taxiing onto the runway it was issued <c>HOLD</c>. It kept rolling
/// onto the runway instead of stopping — it never reduced ground speed to 0, so
/// the instructor had to <c>WARPG</c> it back. HOLD set <c>Ground.Hold</c>, but
/// <see cref="LineUpPhase"/>.OnTick only honored its own CTOC hold flag and
/// never checked <c>Ground.IsImmobile</c>, so the hold was silently ignored.
///
/// Fix: a HOLD command mid-line-up sets <see cref="LineUpPhase"/>.HoldPosition
/// (stop where you are, stay in phase), the same freeze CTOC uses. Resume is via
/// LUAW or CTO (not RES). HOLD on the takeoff roll is rejected in favor of CTOC.
///
/// Like <see cref="IssueCrossRunwayCtoMrtTests"/>, these restore a snapshot while
/// N86687 is holding short (restoring mid-LineUp re-runs OnStart with a stale
/// ctx.Runway and faults — a harness artifact) and drive the line-up with
/// commands.
/// </summary>
public class HoldDuringLineUpTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/hold-during-lineup-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N86687";

    // N86687 is holding short of 28R from ~t=2795 until the recorded LUAW at
    // t=3018. Restore just before that.
    private const int RestoreAt = 3015;

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
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("DepartureClearanceHandler", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Restores N86687 holding short of 28R. Returns false (caller should skip)
    /// when test data is missing or the aircraft is not in the expected state.
    /// </summary>
    private bool TryRestoreHoldingShort(RecordingArchive archive, SimulationEngine engine)
    {
        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);

        var snapshot = archive.ReadSnapshotAt(RestoreAt);
        if (snapshot is null)
        {
            output.WriteLine($"No snapshot near t={RestoreAt} — skipping");
            return false;
        }
        engine.RestoreFromSnapshot(snapshot.State);

        var pre = engine.FindAircraft(Callsign);
        if (pre?.Phases?.CurrentPhase is not HoldingShortPhase)
        {
            output.WriteLine($"{Callsign} not holding short at t={RestoreAt} (phase={pre?.Phases?.CurrentPhase?.Name}) — skipping");
            return false;
        }
        return true;
    }

    /// <summary>Issues LUAW and ticks until the aircraft is rolling into the line-up.</summary>
    private bool DriveIntoLineUp(SimulationEngine engine)
    {
        Assert.True(engine.SendCommand(Callsign, "LUAW").Success);
        for (int t = 1; t <= 20; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac?.Phases?.CurrentPhase is LineUpPhase && ac.IndicatedAirspeed > 2.0)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void Hold_DuringLineUp_StopsImmediately_StaysInLineUp()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null || !TryRestoreHoldingShort(archive, engine))
            {
                return;
            }

            Assert.True(DriveIntoLineUp(engine), $"{Callsign} never started rolling into the line-up after LUAW");

            var moving = engine.FindAircraft(Callsign);
            var holdPos = moving!.Position;
            output.WriteLine($"lining up: IAS={moving.IndicatedAirspeed:F1} pos=({holdPos.Lat:F6},{holdPos.Lon:F6})");

            var hold = engine.SendCommand(Callsign, "HOLD");
            Assert.True(hold.Success, $"HOLD rejected: {hold.Message}");

            // The aircraft must brake to a stop where it is and stay in LineUp —
            // it must NOT continue rolling onto the centerline and reach
            // LinedUpAndWaiting. Buggy code ignores the hold and completes the
            // line-up (~LinedUpAndWaiting within ~30s).
            for (int t = 1; t <= 30; t++)
            {
                engine.TickOneSecond();
            }

            var held = engine.FindAircraft(Callsign);
            Assert.NotNull(held);
            double rolledFt = GeoMath.DistanceNm(holdPos, held.Position) * GeoMath.FeetPerNm;
            output.WriteLine($"held: phase={held.Phases?.CurrentPhase?.Name} IAS={held.IndicatedAirspeed:F1} rolled={rolledFt:F0}ft");

            Assert.IsType<LineUpPhase>(held.Phases?.CurrentPhase);
            Assert.True(held.IndicatedAirspeed < 1.0, $"{Callsign} should be stopped (hold position) but IAS={held.IndicatedAirspeed:F1}");
            Assert.True(rolledFt < 120.0, $"{Callsign} should stop where it is but rolled {rolledFt:F0}ft onto the runway");

            var lineup = held.Phases?.Phases.OfType<LineUpPhase>().FirstOrDefault();
            Assert.True(lineup!.HoldPosition, "LineUpPhase.HoldPosition should be set by HOLD");
        }
    }

    [Fact]
    public void Hold_ThenLuaw_ResumesLineUp()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null || !TryRestoreHoldingShort(archive, engine))
            {
                return;
            }

            Assert.True(DriveIntoLineUp(engine));
            Assert.True(engine.SendCommand(Callsign, "HOLD").Success);
            for (int t = 1; t <= 15; t++)
            {
                engine.TickOneSecond();
            }
            var held = engine.FindAircraft(Callsign);
            Assert.IsType<LineUpPhase>(held!.Phases?.CurrentPhase);
            Assert.True(held.IndicatedAirspeed < 1.0);

            // Re-issuing LUAW lifts the hold and resumes the line-up; the aircraft
            // finishes lining up and waits (LinedUpAndWaitingPhase).
            var luaw = engine.SendCommand(Callsign, "LUAW");
            Assert.True(luaw.Success, $"LUAW (resume) rejected: {luaw.Message}");
            Assert.False(held.Phases?.Phases.OfType<LineUpPhase>().First().HoldPosition, "LUAW should lift the hold");

            bool linedUp = false;
            for (int t = 1; t <= 40; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft(Callsign);
                if (ac?.Phases?.CurrentPhase is LinedUpAndWaitingPhase)
                {
                    linedUp = true;
                    break;
                }
            }
            Assert.True(linedUp, $"{Callsign} did not resume the line-up after LUAW");
        }
    }

    [Fact]
    public void Hold_ThenCto_ResumesAndDeparts()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null || !TryRestoreHoldingShort(archive, engine))
            {
                return;
            }

            Assert.True(DriveIntoLineUp(engine));
            Assert.True(engine.SendCommand(Callsign, "HOLD").Success);
            for (int t = 1; t <= 15; t++)
            {
                engine.TickOneSecond();
            }
            var held = engine.FindAircraft(Callsign);
            Assert.IsType<LineUpPhase>(held!.Phases?.CurrentPhase);
            double fieldElev = held.Altitude;

            var cto = engine.SendCommand(Callsign, "CTO");
            Assert.True(cto.Success, $"CTO rejected: {cto.Message}");

            bool airborne = false;
            for (int t = 1; t <= 120; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft(Callsign);
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
            Assert.True(airborne, $"{Callsign} did not resume and depart after CTO");
        }
    }

    [Fact]
    public void Hold_ThenRes_IsRejected_AircraftStaysHeld()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null || !TryRestoreHoldingShort(archive, engine))
            {
                return;
            }

            Assert.True(DriveIntoLineUp(engine));
            Assert.True(engine.SendCommand(Callsign, "HOLD").Success);
            for (int t = 1; t <= 15; t++)
            {
                engine.TickOneSecond();
            }

            // RES does not resume a held line-up — the user must use LUAW or CTO.
            var res = engine.SendCommand(Callsign, "RES");
            Assert.False(res.Success, $"RES should not resume a held line-up but succeeded: {res.Message}");

            for (int t = 1; t <= 10; t++)
            {
                engine.TickOneSecond();
            }
            var stillHeld = engine.FindAircraft(Callsign);
            Assert.IsType<LineUpPhase>(stillHeld!.Phases?.CurrentPhase);
            Assert.True(stillHeld.IndicatedAirspeed < 1.0, $"{Callsign} should remain held after RES but IAS={stillHeld.IndicatedAirspeed:F1}");
            Assert.True(stillHeld.Phases?.Phases.OfType<LineUpPhase>().First().HoldPosition, "hold should persist after RES");
        }
    }

    [Fact]
    public void Hold_OnTakeoffRoll_IsRejected()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null || !TryRestoreHoldingShort(archive, engine))
            {
                return;
            }

            // Clear for takeoff straight from holding short and let it begin the
            // ground roll.
            Assert.True(engine.SendCommand(Callsign, "CTO").Success);
            bool rolling = false;
            for (int t = 1; t <= 60; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft(Callsign);
                if (ac is { IsOnGround: true } && ac.Phases?.CurrentPhase is TakeoffPhase && ac.IndicatedAirspeed > 20.0)
                {
                    rolling = true;
                    break;
                }
            }
            Assert.True(rolling, $"{Callsign} never reached the takeoff roll");

            var before = engine.FindAircraft(Callsign)!.IndicatedAirspeed;
            var hold = engine.SendCommand(Callsign, "HOLD");
            Assert.False(hold.Success, $"HOLD on the takeoff roll should be rejected but succeeded: {hold.Message}");
            Assert.Contains("takeoff roll", hold.Message!, System.StringComparison.OrdinalIgnoreCase);

            // It keeps accelerating — the hold had no effect.
            for (int t = 1; t <= 3; t++)
            {
                engine.TickOneSecond();
            }
            var after = engine.FindAircraft(Callsign);
            Assert.NotNull(after);
            Assert.True(
                after.IndicatedAirspeed > before,
                $"{Callsign} should keep accelerating after a rejected HOLD ({before:F0} -> {after.IndicatedAirspeed:F0})"
            );
        }
    }
}
