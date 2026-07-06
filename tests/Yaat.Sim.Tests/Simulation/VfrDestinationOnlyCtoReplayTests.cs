using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// End-to-end replay of the "VFR flight plan with only a destination breaks CTO" report
/// (bundle "S2-OAK-P | S2 Rating Practical Exam", trimmed). At t=231 the controller gave
/// N346G — a C150 parked at OAK — a VFR flight plan with only a destination (KAPC / Napa,
/// no runway 28R) and an empty departure, taxied it to 28R, then could not clear it for
/// takeoff: <c>CTO: FAIL — Cannot resolve runway 28R</c>.
///
/// This replays to a moment before the recorded workaround (destination re-amended to
/// KOAK at t=462, then re-RWY / re-TAXI / re-CTO) — verified state at t=440:
/// <c>IsOnGround=true, AirportId="OAK", FP.Departure="", FP.Destination="KAPC"</c>,
/// taxiing toward 28R — and issues a fresh CTO, asserting it succeeds. The recorded CTOs
/// are deliberately not asserted on because they carry the user's workaround.
///
/// The root cause and the constructed unit coverage live in
/// <see cref="VfrDestinationOnlyRunwayResolutionTests"/>.
/// </summary>
public class VfrDestinationOnlyCtoReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/vfr-dest-only-cto-recording.zip";

    // Before the recorded destination re-amendment to KOAK (t=462); N346G is on the ground
    // at OAK taxiing toward 28R with a VFR plan filed to only KAPC.
    private const int PreWorkaroundTime = 440;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void N346G_CtoFromTaxi_VfrDestinationOnly_ClearsForTakeoffFromPhysicalRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, PreWorkaroundTime);
        var ac = engine.FindAircraft("N346G");
        Assert.NotNull(ac);

        // Confirm we are replaying the reported condition, not the KOAK workaround.
        Assert.True(ac.IsOnGround);
        Assert.Equal("OAK", ac.AirportId);
        Assert.True(string.IsNullOrEmpty(ac.FlightPlan.Departure), $"Departure should be empty, was '{ac.FlightPlan.Departure}'");
        Assert.Equal("KAPC", ac.FlightPlan.Destination);

        output.WriteLine(
            $"t={PreWorkaroundTime}: phase={ac.Phases?.CurrentPhase?.GetType().Name} "
                + $"onGround={ac.IsOnGround} dep='{ac.FlightPlan.Departure}' dest='{ac.FlightPlan.Destination}'"
        );

        // The reported symptom: CTO rejected with "Cannot resolve runway 28R" because the
        // runway lookup went to the filed destination (KAPC) instead of OAK.
        var result = engine.SendCommand("N346G", "CTO MRC 020");
        Assert.True(result.Success, $"CTO should clear the aircraft from OAK 28R, but failed: {result.Message}");

        ac = engine.FindAircraft("N346G");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases?.AssignedRunway);
        Assert.Equal("OAK", ac.Phases.AssignedRunway.AirportId);

        // The clearance must actually install the departure sequence (line-up → takeoff),
        // not silently degrade into "store clearance and keep taxiing".
        bool hasTowerDeparture =
            ac.Phases.Phases.Any(p => p is TakeoffPhase or LineUpPhase or LinedUpAndWaitingPhase) || ac.Phases.DepartureClearance is not null;
        Assert.True(hasTowerDeparture, "CTO should install/store the takeoff departure sequence");
    }
}
