using System.Diagnostics;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
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
    private const string RecordingPath = "TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip";

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
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    /// <summary>
    /// At t=722 AMX669 has completed its preset taxi to spot 2 on M2 and is waiting in
    /// HoldingInPositionPhase, which takes FOLLOWG: the command reaches the follow planner instead of
    /// the phase refusing it. JAL57 is taxiing on the controller's <c>TAXI A L F 28L</c> issued at t=660,
    /// which cancelled its preset <c>WAIT 30 TAXI M4 M1 $1</c> (expired and held through the push), so
    /// spot 2 lies on JAL57's route ahead of it and the planner refuses the follow as "ahead of JAL57".
    /// </summary>
    [Fact]
    public void AMX669_AcceptsFollowGroundAfterTaxiToSpot()
    {
        var swTotal = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        SessionRecording? recording = LoadRecording();
        output.WriteLine($"[TIMING] LoadRecording: {sw.Elapsed.TotalMilliseconds:F0}ms");

        sw.Restart();
        SimulationEngine? engine = BuildEngine();
        output.WriteLine($"[TIMING] BuildEngine: {sw.Elapsed.TotalMilliseconds:F0}ms");
        if (recording is null || engine is null)
        {
            return;
        }

        sw.Restart();
        engine.Replay(recording, ReplayTime);
        output.WriteLine($"[TIMING] Replay({ReplayTime}s): {sw.Elapsed.TotalMilliseconds:F0}ms");
        output.WriteLine(engine.DumpTickTimings());

        AircraftState? amx = engine.FindAircraft("AMX669");
        Assert.NotNull(amx);

        output.WriteLine(
            $"AMX669 at t={ReplayTime}: phase={amx.Phases?.CurrentPhase?.Name ?? "null"} "
                + $"pos=({amx.Position.Lat:F6},{amx.Position.Lon:F6}) gs={amx.GroundSpeed:F1} parkingSpot={amx.Ground.ParkingSpot ?? "null"}"
        );

        // Aircraft is at a taxi spot awaiting further instructions — not parked.
        // It should be in HoldingInPositionPhase, not AtParkingPhase.
        Assert.NotNull(amx.Phases);
        Assert.IsType<HoldingInPositionPhase>(amx.Phases.CurrentPhase);

        // JAL57 obeys the controller's fresh TAXI A L F 28L: the held preset taxi to spot 1 was cancelled by it.
        AircraftState? jal = engine.FindAircraft("JAL57");
        Assert.NotNull(jal);
        output.WriteLine(
            $"JAL57 at t={ReplayTime}: phase={jal.Phases?.CurrentPhase?.Name ?? "null"} "
                + $"route={jal.Ground.AssignedTaxiRoute?.ToSummary() ?? "null"} deferred={jal.DeferredDispatches.Count}"
        );
        Assert.DoesNotContain(jal.DeferredDispatches, d => (d.SourceText ?? "").Contains("M4 M1", StringComparison.Ordinal));
        Assert.NotNull(jal.Ground.AssignedTaxiRoute);
        Assert.Null(jal.Ground.AssignedTaxiRoute.DestinationSpot);
        Assert.Contains(jal.Ground.AssignedTaxiRoute.Segments, s => s.TaxiwayName == "L");
        Assert.Contains(jal.Ground.AssignedTaxiRoute.Segments, s => s.TaxiwayName == "F");

        // The phase takes FOLLOWG; the follow planner refuses it because spot 2 is on JAL57's route ahead of it.
        CommandResult result = engine.SendCommand("AMX669", "FOLLOWG JAL57");
        output.WriteLine($"FOLLOWG JAL57 result: success={result.Success} msg={result.Message}");
        Assert.False(result.Success, "FOLLOWG JAL57 should be refused: AMX669 holds on JAL57's route ahead of it");
        Assert.Equal("unable, ahead of JAL57 on its route — issue HOLD, GIVEWAY or TAXI first", result.Message);
        Assert.IsType<HoldingInPositionPhase>(amx.Phases.CurrentPhase);
        output.WriteLine($"[TIMING] Test total: {swTotal.Elapsed.TotalMilliseconds:F0}ms");
    }
}
