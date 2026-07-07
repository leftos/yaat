using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E regression test for "departures spawn with illegal assigned squawk codes".
///
/// Recording: S2-OAK-4 (VFR Transitions/Radar Concepts), OAK / ZOA, seed 394580556.
/// N157LE is a VFR departure from parking NEW5 with a filed scenario flight plan,
/// spawning at t≈1993. It received a random discrete beacon code at spawn.
///
/// Bug: <c>SimulationWorld.GenerateBeaconCode</c> drew four raw octal digits with no
/// reserved-code exclusion, so N157LE spawned squawking <c>7600</c> — the lost-comms
/// emergency SPC — and held it for over a hundred seconds. Reserved SPCs
/// (7500/7600/7700/7400/7777) and non-discrete block codes (anything ending in "00")
/// must never be auto-assigned. This is a full-replay test so the fixed generator runs
/// from t=0; a snapshot restore would replay the frozen pre-fix 7600 and prove nothing.
/// </summary>
public class IssueSawDeparturesReservedSquawkTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/saw-departures-reserved-squawk-recording.zip";
    private const string Callsign = "N157LE";
    private const int AssertAtSeconds = 2010;

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
    public void N157LE_DepartureSpawn_DoesNotSquawkReservedCode()
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

        output.WriteLine($"{Callsign} @ t={AssertAtSeconds}s: AssignedCode={ac.Transponder.AssignedCode:D4} Code={ac.Transponder.Code:D4}");

        Assert.NotEqual((uint)0, ac.Transponder.AssignedCode); // filed FP aircraft gets a discrete code
        Assert.True(
            BeaconCodePool.IsAssignableCode(ac.Transponder.AssignedCode),
            $"{Callsign} spawned squawking non-assignable code {ac.Transponder.AssignedCode:D4} (reserved SPC or xy00 block code)"
        );
    }

    [Fact]
    public void NoAircraftSpawnsWithReservedAssignedCode()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, AssertAtSeconds);

        // Cold-call / VFR aircraft carry AssignedCode == 0 (no discrete code, squawking 1200);
        // only aircraft with an assigned discrete code must satisfy the discreteness rule.
        var violations = engine
            .World.GetSnapshot()
            .Where(a => a.Transponder.AssignedCode != 0 && !BeaconCodePool.IsAssignableCode(a.Transponder.AssignedCode))
            .Select(a => $"{a.Callsign}={a.Transponder.AssignedCode:D4}")
            .ToList();

        Assert.True(violations.Count == 0, $"Aircraft spawned with non-assignable beacon codes: {string.Join(", ", violations)}");
    }
}
