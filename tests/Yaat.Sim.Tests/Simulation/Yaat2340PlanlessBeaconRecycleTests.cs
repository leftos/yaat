using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-2340: N775JW (C182, a VFR cold call with no filed plan, preset SQV) showed an assigned beacon code before any
/// flight plan existed. Bundle S2-OAK-4 (VFR Transitions/Radar Concepts): N775JW spawns at t=360 with AssignedCode 0 and
/// HasFlightPlan false; the RPO's DCT VPCOL (t=408) and CM 020 (t=410) leave the transponder alone; at t=418, while
/// paused, the student's CRC sends RequestNewBeaconCode three times (the Flight Plan Editor's beacon button, enabled
/// because YAAT sends CRC a blank flight plan for a planless aircraft) and each one draws a discrete code into
/// AssignedCode while HasFlightPlan stays false.
///
/// Real CRC never sends RequestNewBeaconCode for an aircraft with no flight plan (its editor disables the button when it
/// holds no plan), and 7110.65 5-2-7.a issues a computer-assigned code to a VFR aircraft receiving radar advisories,
/// which is the flight plan the computer assigns it with; until then the pilot squawks 1200 (AIM 4-1-20.a.5.a). A
/// recycle on a planless aircraft therefore assigns nothing: the code comes with the plan (AmendFlightPlan's
/// planless-target branch, or VP/DA).
///
/// Fixture: the S2-OAK-4 bundle, as TestData/yaat2340-oak-planless-beacon-recycle-recording.yaat-bug-report-bundle.zip.
/// </summary>
public class Yaat2340PlanlessBeaconRecycleTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/yaat2340-oak-planless-beacon-recycle-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var groundData = RecordingGroundData.Evergreen("a beacon recycle on a planless VFR aircraft assigns no code");
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    /// <summary>
    /// Full replay: the fix changes only what RequestNewBeaconCode does at t=418, after every earlier action has run.
    /// Today N775JW leaves t=418 with a non-zero AssignedCode and no plan.
    /// </summary>
    [Fact]
    public void Replay_RecordedRecycleOnPlanlessN775JW_AssignsNoCode()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return; // silent skip when the fixture is not installed
        }

        // The fixture must hold the recycle under test, or the replay assertions below pass vacuously.
        Assert.Contains(
            recording.Actions.OfType<RecordedRequestNewBeaconCode>(),
            a => (a.Callsign == "N775JW") && (a.ElapsedSeconds >= 418) && (a.ElapsedSeconds < 419)
        );

        SimulationEngine engine = BuildEngine();
        engine.Replay(recording, 417);

        AircraftState? before = engine.FindAircraft("N775JW");
        Assert.NotNull(before);
        Assert.False(before.FlightPlan.HasFlightPlan); // a cold call: no plan filed
        Assert.Equal(0u, before.Transponder.AssignedCode); // the RPO's DCT/CM at 408/410 assigned nothing
        Assert.Equal(1200u, before.Transponder.Code);

        engine.ReplayOneSecond(); // t=418: the three recorded CRC RequestNewBeaconCode actions

        AircraftState? after = engine.FindAircraft("N775JW");
        Assert.NotNull(after);
        Assert.False(after.FlightPlan.HasFlightPlan);
        Assert.Equal(0u, after.Transponder.AssignedCode); // today: a discrete code (0034 here, 0325 live) with no plan
        Assert.Equal(1200u, after.Transponder.Code);
        Assert.True(after.Transponder.CommandedSquawkVfr); // today: released by the assignment
    }

    /// <summary>The same contract without the recording: a recycle on a planless aircraft draws nothing from the pool.</summary>
    [Fact]
    public void RequestNewBeaconCode_PlanlessVfrAircraft_AssignsNoCode()
    {
        SimulationEngine engine = BuildEngine();
        engine.World.AddAircraft(
            new AircraftState
            {
                Callsign = "N775JW",
                AircraftType = "C182",
                Transponder = new AircraftTransponder
                {
                    AssignedCode = 0,
                    Code = 1200,
                    Mode = "C",
                },
                FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", HasFlightPlan = false },
            }
        );

        uint drawn = engine.RequestNewBeaconCode("N775JW", assignedByFacilityId: null, assignedBySectorId: null);

        AircraftState? ac = engine.FindAircraft("N775JW");
        Assert.NotNull(ac);
        Assert.Equal(0u, drawn); // today: 1
        Assert.Equal(0u, ac.Transponder.AssignedCode); // today: 0001
        Assert.False(ac.FlightPlan.HasFlightPlan);
    }
}
