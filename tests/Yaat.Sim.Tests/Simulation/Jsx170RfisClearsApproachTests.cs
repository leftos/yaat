using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// JSX170 (E145/L) spawned on final to OAK rwy 30 with GS captured and CLAND issued.
/// Controller sent RFIS at t=796; the snapshot at t=800 shows aircraft.Phases = null —
/// the entire FinalApproach → Landing pipeline was wiped. Root cause: the default arm
/// of FinalApproachPhase.CanAcceptCommand returns ClearsPhase for any non-whitelisted
/// command, including the report-only RFIS, which has no navigation effect and should
/// never clear a phase.
/// </summary>
public class Jsx170RfisClearsApproachTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/a67670e50d58.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay just before the recorded RFIS at t=796, then issue RFIS manually.
    /// FinalApproachPhase must remain active and HasReportedFieldInSight must flip to true.
    /// </summary>
    [Fact]
    public void Rfis_DoesNotClearFinalApproach()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 795);

        var before = engine.FindAircraft("JSX170");
        Assert.NotNull(before);
        Assert.NotNull(before.Phases);
        Assert.IsType<FinalApproachPhase>(before.Phases.CurrentPhase);
        Assert.False(before.Approach.HasReportedFieldInSight);

        var result = engine.SendCommand("JSX170", "RFIS");
        output.WriteLine($"RFIS result: success={result.Success} message={result.Message}");
        Assert.True(result.Success, result.Message);

        var after = engine.FindAircraft("JSX170");
        Assert.NotNull(after);
        Assert.NotNull(after.Phases);
        Assert.IsType<FinalApproachPhase>(after.Phases.CurrentPhase);
        Assert.True(after.Approach.HasReportedFieldInSight);
    }
}
