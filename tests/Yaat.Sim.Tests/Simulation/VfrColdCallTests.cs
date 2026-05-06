using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for VFR cold-call aircraft spawn behavior.
///
/// Recording: S2-OAK-5 (Practical Exam Preparation/Advanced Concepts).
/// N2BP is defined in the scenario JSON with no <c>flightplan</c> field —
/// only <c>startingConditions</c> (FixOrFrd OAK360010, alt 4500) and a few
/// preset commands (<c>WAIT 60 DM 20</c>, a <c>SAY</c>, and <c>SQV</c>).
///
/// Expected behavior: a missing scenario flight plan means the aircraft is a
/// VFR "cold call" — it spawns squawking 1200 with no AssignedCode, with an
/// empty Destination/Route, and <c>HasFlightPlan == false</c>. Controllers
/// then issue <c>DA</c> / <c>VP</c> STARS commands to file an abbreviated
/// VFR plan and assign a discrete beacon.
///
/// Bug: N2BP spawned with <c>Destination = "OAK"</c> (auto-filled from
/// scenario primary airport) and <c>AssignedCode = 5277</c> (random discrete
/// beacon), so the data block read like a filed VFR plan instead of a cold
/// target.
/// </summary>
public class VfrColdCallTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/a67670e50d58.zip";
    private const string Callsign = "N2BP";
    private const int SpawnDelaySeconds = 1811;
    private const int AssertAtSeconds = 1815;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("ScenarioLoader", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N2BP_NoScenarioFlightPlan_SpawnsAsColdCall()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, AssertAtSeconds);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        output.WriteLine(
            $"N2BP @ t={AssertAtSeconds}s: HasFlightPlan={ac.FlightPlan.HasFlightPlan} "
                + $"Destination='{ac.FlightPlan.Destination}' Route='{ac.FlightPlan.Route}' "
                + $"FlightRules='{ac.FlightPlan.FlightRules}' "
                + $"AssignedCode={ac.Transponder.AssignedCode} Code={ac.Transponder.Code} "
                + $"Mode={ac.Transponder.Mode}"
        );

        Assert.False(ac.FlightPlan.HasFlightPlan, "Cold-call aircraft should not have a filed flight plan");
        Assert.True(
            string.IsNullOrEmpty(ac.FlightPlan.Destination),
            $"Destination should be empty for cold call (was '{ac.FlightPlan.Destination}')"
        );
        Assert.True(string.IsNullOrEmpty(ac.FlightPlan.Route), $"Route should be empty for cold call (was '{ac.FlightPlan.Route}')");
        Assert.True(string.IsNullOrEmpty(ac.FlightPlan.Departure), $"Departure should be empty for cold call (was '{ac.FlightPlan.Departure}')");
        Assert.Equal(0, ac.FlightPlan.CruiseAltitude);
        Assert.Equal(0, ac.FlightPlan.CruiseSpeed);
        Assert.Equal("VFR", ac.FlightPlan.FlightRules);
        Assert.Equal("", ac.FlightPlan.EquipmentSuffix);

        Assert.Equal((uint)0, ac.Transponder.AssignedCode);
        Assert.Equal((uint)1200, ac.Transponder.Code);
    }
}
