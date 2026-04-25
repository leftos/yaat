using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: FOLLOWG rejected with "Cannot follow during At Parking" when the
/// aircraft had taxied to an intermediate <em>taxi spot</em> (not a gate) and was
/// waiting for further instructions.
///
/// Root cause: <c>TaxiingPhase.ArriveAtNode</c> inserted <c>AtParkingPhase</c>
/// whenever <c>route.DestinationParking ?? route.DestinationSpot</c> was non-null,
/// so taxiing to a spot misrepresented the aircraft as "At Parking" and rejected
/// sequencing commands like FOLLOWG, LUAW, HOLD. A taxi spot should land the
/// aircraft in <c>HoldingInPositionPhase</c> — the catch-all idle state that
/// accepts follow-up ground commands.
///
/// Recording: S1-SFO-2 | Ground Control 28/01. AMX669 spawns with preset
/// <c>PUSH M2; WAIT 30 SN; WAIT 30 TAXI M2 $2</c>. At t≈722 the user attempts
/// <c>FOLLOWG JAL57</c> while AMX669 sits at spot 2 and the command is rejected.
/// </summary>
public class IssueAmxFollowAtSpotTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue-amx-followg-at-spot-recording.yaat-bug-report-bundle.zip";

    // t=722 is after AMX669 reaches spot 2 and before the recorded TAXI B M1 1L at t=760.
    private const int ReplayTime = 722;

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

    /// <summary>
    /// At t=722 AMX669 has completed its preset taxi to spot 2 on M2 and is waiting.
    /// FOLLOWG JAL57 should be accepted and transition AMX669 into FollowingPhase.
    /// </summary>
    [Fact]
    public void AMX669_AcceptsFollowGroundAfterTaxiToSpot()
    {
        var swTotal = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        var recording = LoadRecording();
        output.WriteLine($"[TIMING] LoadRecording: {sw.Elapsed.TotalMilliseconds:F0}ms");

        sw.Restart();
        var engine = BuildEngine();
        output.WriteLine($"[TIMING] BuildEngine: {sw.Elapsed.TotalMilliseconds:F0}ms");
        if (recording is null || engine is null)
        {
            return;
        }

        sw.Restart();
        engine.Replay(recording, ReplayTime);
        output.WriteLine($"[TIMING] Replay({ReplayTime}s): {sw.Elapsed.TotalMilliseconds:F0}ms");
        output.WriteLine(engine.DumpTickTimings());

        var amx = engine.FindAircraft("AMX669");
        Assert.NotNull(amx);

        output.WriteLine(
            $"AMX669 at t={ReplayTime}: phase={amx.Phases?.CurrentPhase?.Name ?? "null"} "
                + $"pos=({amx.Position.Lat:F6},{amx.Position.Lon:F6}) gs={amx.GroundSpeed:F1} parkingSpot={amx.Ground.ParkingSpot ?? "null"}"
        );

        // Aircraft is at a taxi spot awaiting further instructions — not parked.
        // It should be in HoldingInPositionPhase, not AtParkingPhase.
        Assert.NotNull(amx.Phases);
        Assert.IsType<HoldingInPositionPhase>(amx.Phases.CurrentPhase);

        // FOLLOWG JAL57 should succeed.
        var result = engine.SendCommand("AMX669", "FOLLOWG JAL57");
        output.WriteLine($"FOLLOWG JAL57 result: success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"FOLLOWG should succeed but got: {result.Message}");

        amx = engine.FindAircraft("AMX669");
        Assert.NotNull(amx);
        Assert.IsType<FollowingPhase>(amx.Phases?.CurrentPhase);
        output.WriteLine($"[TIMING] Test total: {swTotal.Elapsed.TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// Ticking a no-destination / spot-only taxi completion also exposes the bug
    /// without relying on the recording. Pure regression guard for the phase
    /// inserted by <c>TaxiingPhase.ArriveAtNode</c>.
    /// </summary>
    [Fact]
    public void TaxiToSpot_CompletesInHoldingInPosition_NotAtParking()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, ReplayTime);

        var amx = engine.FindAircraft("AMX669");
        Assert.NotNull(amx);
        Assert.NotNull(amx.Phases);

        output.WriteLine($"AMX669 phase stack at t={ReplayTime}:");
        for (int i = 0; i < amx.Phases.Phases.Count; i++)
        {
            output.WriteLine($"  [{i}] {amx.Phases.Phases[i].Name} status={amx.Phases.Phases[i].Status}");
        }

        // AMX669 reached spot 2 — the aircraft is idle awaiting further instructions,
        // NOT parked. HoldingInPositionPhase accepts Taxi/Pushback/FollowGround/etc.
        Assert.IsType<HoldingInPositionPhase>(amx.Phases.CurrentPhase);
    }
}
