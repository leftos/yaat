using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The YAAT Radar View's assigned-vs-reported beacon-code mismatch flash is an RPO aid that fires at
/// any datablock level (unlike CRC, which only shows the reported/assigned line in a Full Data Block).
/// Once the pilot is told to squawk VFR (<c>SQVFR</c>/<c>SQV</c>), the stale assigned discrete code is
/// noise the RPO should ignore, so a latch (<see cref="AircraftTransponder.CommandedSquawkVfr"/>)
/// suppresses the flash. Squawk VFR is only a pilot instruction — it does NOT change the assigned code.
/// The latch releases ONLY when a new beacon code is assigned (Flight Plan Editor recycle,
/// <see cref="SimulationEngine.RequestNewBeaconCode"/>, an FP amendment, or a CRC beacon assign).
///
/// Repro: bundle S2-OAK-4 (VFR Transitions/Radar Concepts) — N427MX is issued <c>SQVFR</c> at t=1610
/// while assigned 0303 and squawking 1200, and the datablock kept flashing 1200/0303 afterward.
/// The client-side render gate is covered by <c>RadarDatablockLayoutTests</c> (Yaat.Client.Tests).
/// </summary>
public class SquawkVfrFlashLatchTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sqvfr-flash-latch-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static void AddVfrAircraft(SimulationEngine engine, string callsign, uint assigned, uint code)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "PA28",
            Transponder = new AircraftTransponder
            {
                AssignedCode = assigned,
                Code = code,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", HasFlightPlan = true },
        };
        engine.World.AddAircraft(ac);
    }

    [Fact]
    public void SquawkVfr_LatchesSuppression_WithoutTouchingAssignedCode()
    {
        SimulationEngine engine = BuildEngine();
        AddVfrAircraft(engine, "N427MX", assigned: 303, code: 303);

        CommandResult result = engine.SendCommand("N427MX", "SQVFR");

        Assert.True(result.Success);
        AircraftState? ac = engine.FindAircraft("N427MX");
        Assert.NotNull(ac);
        Assert.True(ac.Transponder.CommandedSquawkVfr); // latch set
        Assert.Equal(1200u, ac.Transponder.Code); // pilot now squawks VFR
        Assert.Equal(303u, ac.Transponder.AssignedCode); // assigned code untouched
    }

    /// <summary>
    /// <c>SQ 1200</c> is the same instruction as <c>SQVFR</c> spelled with the code: the commanded code
    /// latches the suppression too, so the RPO does not see the datablock flash 1200 against the stale
    /// assigned discrete.
    /// </summary>
    [Fact]
    public void Squawk1200_LatchesSuppression_LikeSquawkVfr()
    {
        SimulationEngine engine = BuildEngine();
        AddVfrAircraft(engine, "N427MX", assigned: 303, code: 303);

        CommandResult result = engine.SendCommand("N427MX", "SQ 1200");

        Assert.True(result.Success, result.Message);
        AircraftState? ac = engine.FindAircraft("N427MX");
        Assert.NotNull(ac);
        Assert.Equal(1200u, ac.Transponder.Code);
        Assert.True(ac.Transponder.CommandedSquawkVfr); // latch set by the commanded code
        Assert.Equal(303u, ac.Transponder.AssignedCode); // assigned code untouched
    }

    /// <summary>
    /// A later <c>SQ &lt;other code&gt;</c> clears the latch: the pilot is no longer squawking VFR, so the
    /// assigned-vs-reported mismatch is real information again.
    /// </summary>
    [Fact]
    public void SquawkOtherCode_AfterSquawkVfr_ReleasesLatch()
    {
        SimulationEngine engine = BuildEngine();
        AddVfrAircraft(engine, "N427MX", assigned: 303, code: 303);
        engine.SendCommand("N427MX", "SQVFR");
        Assert.True(engine.FindAircraft("N427MX")!.Transponder.CommandedSquawkVfr);

        CommandResult result = engine.SendCommand("N427MX", "SQ 4306");

        Assert.True(result.Success, result.Message);
        AircraftState? ac = engine.FindAircraft("N427MX");
        Assert.NotNull(ac);
        Assert.Equal(4306u, ac.Transponder.Code);
        Assert.False(ac.Transponder.CommandedSquawkVfr); // no longer squawking VFR
        Assert.Equal(303u, ac.Transponder.AssignedCode);
    }

    /// <summary>
    /// A bare <c>SQ</c> puts the pilot back on the assigned code, which is the discrete the RPO assigned — the
    /// mismatch is resolved, so the latch that hid it has to release.
    /// </summary>
    [Fact]
    public void SquawkReset_AfterSquawkVfr_ReleasesLatch()
    {
        SimulationEngine engine = BuildEngine();
        AddVfrAircraft(engine, "N427MX", assigned: 303, code: 303);
        engine.SendCommand("N427MX", "SQVFR");
        Assert.True(engine.FindAircraft("N427MX")!.Transponder.CommandedSquawkVfr);

        CommandResult result = engine.SendCommand("N427MX", "SQ");

        Assert.True(result.Success, result.Message);
        AircraftState? ac = engine.FindAircraft("N427MX");
        Assert.NotNull(ac);
        Assert.Equal(303u, ac.Transponder.Code); // back on the assigned code
        Assert.False(ac.Transponder.CommandedSquawkVfr); // no longer squawking VFR
    }

    [Fact]
    public void RequestNewBeaconCode_ReleasesLatch()
    {
        SimulationEngine engine = BuildEngine();
        AddVfrAircraft(engine, "N427MX", assigned: 303, code: 303);
        engine.SendCommand("N427MX", "SQVFR");
        Assert.True(engine.FindAircraft("N427MX")!.Transponder.CommandedSquawkVfr);

        uint newCode = engine.RequestNewBeaconCode("N427MX", assignedByFacilityId: null, assignedBySectorId: null);

        AircraftState? ac = engine.FindAircraft("N427MX");
        Assert.NotNull(ac);
        Assert.NotEqual(0u, newCode);
        Assert.Equal(newCode, ac.Transponder.AssignedCode);
        Assert.False(ac.Transponder.CommandedSquawkVfr); // latch released by the fresh assignment
    }

    [Fact]
    public void FlightPlanBeaconAmend_ReleasesLatch()
    {
        SimulationEngine engine = BuildEngine();
        AddVfrAircraft(engine, "N427MX", assigned: 303, code: 303);
        engine.SendCommand("N427MX", "SQVFR");

        engine.AmendFlightPlan(
            "N427MX",
            new FlightPlanAmendment(
                AircraftType: null,
                EquipmentSuffix: null,
                Departure: null,
                Destination: null,
                CruiseSpeed: null,
                Altitude: null,
                FlightRules: null,
                Route: null,
                Remarks: null,
                Scratchpad1: null,
                Scratchpad2: null,
                BeaconCode: 404
            )
        );

        AircraftState? ac = engine.FindAircraft("N427MX");
        Assert.NotNull(ac);
        Assert.False(ac.Transponder.CommandedSquawkVfr); // latch released
        Assert.Equal(404u, ac.Transponder.AssignedCode);
    }

    /// <summary>
    /// Full-replay E2E: replay the recording up to just before the recorded <c>SQVFR</c> (latch clear),
    /// then advance past it and confirm the latch sets — while the underlying assigned-vs-reported
    /// mismatch (0303 vs 1200) still exists, so the datablock would flash if not for the latch.
    /// </summary>
    [Fact]
    public void Replay_N427MX_LatchesOnRecordedSqvfr()
    {
        TestVnasData.EnsureInitialized();
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        if (recording is null || TestVnasData.NavigationDb is null)
        {
            return; // silent skip when test data is unavailable
        }

        SimulationEngine engine = BuildEngine();
        engine.Replay(recording, 1605);

        AircraftState? before = engine.FindAircraft("N427MX");
        Assert.NotNull(before);
        Assert.False(before.Transponder.CommandedSquawkVfr); // not yet told to squawk VFR

        // Advance past the recorded SQVFR at t=1610 (ReplayOneSecond applies recorded actions).
        for (int t = 1606; t <= 1615; t++)
        {
            engine.ReplayOneSecond();
        }

        AircraftState? after = engine.FindAircraft("N427MX");
        Assert.NotNull(after);
        Assert.True(after.Transponder.CommandedSquawkVfr); // latch set by the recorded SQVFR
        Assert.Equal(1200u, after.Transponder.Code); // squawking VFR
        // The discrete assigned code is untouched, so a reported-vs-assigned mismatch still exists —
        // the datablock would flash it if the latch did not suppress it.
        Assert.NotEqual(0u, after.Transponder.AssignedCode);
        Assert.NotEqual(after.Transponder.Code, after.Transponder.AssignedCode);
    }
}
