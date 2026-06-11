using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for the S1-OAK-P S1 Rating Practical Exam bundle: a controller used CRC's
/// Flight Plan Editor to create a VFR flight plan for the cold-call radar target
/// N513SJ (C421, parked at OAK). Because YAAT broadcasts a blank flight plan for a
/// cold-call target, CRC's editor submits an <c>AmendFlightPlan</c> (not Create), and
/// the amend path never established the plan nor assigned a beacon code.
///
/// Recorded actions: two AmendFlightPlan events at t=732 and t=846, both with
/// BeaconCode=null. Snapshot at t=900 showed Transponder.AssignedCode=0 and
/// FlightPlan.HasFlightPlan=false — the plan never "stuck" and no squawk was issued,
/// which is what made the recycle-beacon button appear to do nothing (DtoConverter
/// suppresses AssignedBeaconCode while HasFlightPlan is false).
///
/// After the fix, an FPE amend that establishes a plan on a no-plan target sets
/// HasFlightPlan=true and assigns a discrete code from the beacon pool — for both VFR
/// and IFR, matching the typed DA/VP create path.
/// </summary>
public class IssueFpeColdCallBeaconTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/fpe-cold-call-beacon-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N513SJ";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("SimulationEngine", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replays past both FPE amends (t=732, t=846) to t=900 and asserts the flight
    /// plan was established with a discrete beacon code. Fails on current code:
    /// HasFlightPlan stays false and AssignedCode stays 0.
    /// </summary>
    [Fact]
    public void FpeAmend_OnColdCallTarget_EstablishesPlanAndAssignsBeacon()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 900);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        output.WriteLine(
            $"{Callsign}: HasFlightPlan={ac.FlightPlan.HasFlightPlan} FlightRules={ac.FlightPlan.FlightRules} "
                + $"AssignedCode={ac.Transponder.AssignedCode:D4} Code={ac.Transponder.Code:D4}"
        );

        Assert.True(ac.FlightPlan.HasFlightPlan, "FPE amend on a cold-call target should establish a flight plan (HasFlightPlan).");
        Assert.NotEqual(0u, ac.Transponder.AssignedCode);
    }
}
