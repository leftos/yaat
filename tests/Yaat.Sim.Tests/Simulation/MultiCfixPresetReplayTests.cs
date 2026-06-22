using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for the multi-CFIX preset scenario (bundle "Z | S3-NCTA-1 | Area A Familiarization", ZOA).
/// Arrivals chain CFIX presets to hand-build a descend-via profile, e.g. ASA221:
///   CFIX NRRLI 210 280 ; CFIX WWAVS 160 280 ; CFIX EPICK 120 280 ;
///   CFIX YERKS 9000 230 ; CFIX FOLET 8000 230 ; CFIX EDDYY 6000 230
///
/// The crossing restrictions already stack additively on the route, but each immediate CFIX used
/// to drop the previous (already-applied) CFIX queue block and emit a spurious
/// "queue cleared by CFIX … (lost: CFIX …)" warning that read as "only the last CFIX wins".
/// </summary>
public class MultiCfixPresetReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/multi-cfix-preset-recording.yaat-bug-report-bundle.zip";

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
    public void Asa221_MultiCfixPresets_StackOnRoute_NoQueueClearedWarning()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var warnings = new List<(string Callsign, string Warning)>();
        engine.WarningEmitted += (cs, w) => warnings.Add((cs, w));

        // Replay just past spawn so the preset dispatch and its post-physics warning drain run,
        // but not far enough to sequence any of the crossing fixes.
        engine.Replay(recording, 5);

        var aircraft = engine.FindAircraft("ASA221");
        Assert.NotNull(aircraft);

        // All six crossing restrictions stay stamped on the route (additive — nothing lost).
        var restrictedFixes = aircraft.Targets.NavigationRoute.Where(f => f.AltitudeRestriction is not null).Select(f => f.Name).ToArray();
        foreach (var fix in new[] { "NRRLI", "WWAVS", "EPICK", "YERKS", "FOLET", "EDDYY" })
        {
            Assert.Contains(fix, restrictedFixes);
        }

        // No spurious "queue cleared by CFIX … (lost: CFIX …)" warning for the CFIX chain.
        var queueCleared = warnings
            .Where(w => (w.Callsign == "ASA221") && w.Warning.Contains("queue cleared", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Warning)
            .ToList();
        Assert.True(queueCleared.Count == 0, $"Unexpected queue-cleared warnings: {string.Join(" | ", queueCleared)}");
    }
}
