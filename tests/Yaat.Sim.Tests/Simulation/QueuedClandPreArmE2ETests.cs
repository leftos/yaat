using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for "CLAND behind a queued pattern entry is rejected". Recording: S2-OAK-5 (1) | Practical Exam
/// Preparation/Advanced Concepts (ZOA/NCT, OAK), aircraft N805FM. The reported sequence:
///   t=2824  RPO "DCT VPCOL; ERD 28R"  -> accepted, ERD queued behind the direct
///   t=2826  RPO "CLAND"               -> FAIL "Aircraft has no active phase sequence"
///   t=2890  the queued ERD finally fires and builds the circuit
/// The clearance must instead be pre-issued against the queued entry and become the circuit's standing
/// clearance when it builds — without the RPO re-issuing it a minute later.
///
/// Hybrid replay, restored *before* the setup compound: the rejected CLAND was never recorded as an
/// action (only successful commands are), so the test restores the snapshot at t=2820 and lets the
/// recorded <c>DCT VPCOL; ERD 28R</c> at t=2824 dispatch live, then issues the clearance itself. The
/// restore has to precede the compound because a queued block restored from a snapshot has no
/// <c>ApplyAction</c> and never fires — so a restore taken after it was enqueued could not reach the
/// moment the entry builds its circuit.
/// </summary>
public class QueuedClandPreArmE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/queued-cland-prearm-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N805FM";
    private const int PreSetupSeconds = 2820;
    private const int PreClandSeconds = 2826;
    private const int EntryFiresSeconds = 2960;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.InitializeForTest(loggerFactory);

        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void ClandBehindQueuedErd_AppliesWhenTheEntryFires()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);

        var snap = archive.ReadSnapshotAt(PreSetupSeconds);
        if (snap is null)
        {
            output.WriteLine($"No snapshot near t={PreSetupSeconds}, skipping");
            return;
        }

        engine.RestoreFromSnapshot(snap.State);

        // Let the recorded "DCT VPCOL; ERD 28R" at t=2824 dispatch live.
        engine.ReplayRange((int)snap.ElapsedSeconds, PreClandSeconds, recording.Actions);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.Null(ac.Phases); // the reported state: the entry has not built anything yet
        var queuedEntry = Assert.Single(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Contains("ERD 28R", queuedEntry.Description);

        var cland = engine.SendCommand(Callsign, "CLAND");
        output.WriteLine($"CLAND at t={PreClandSeconds}: success={cland.Success} — {cland.Message}");
        Assert.True(cland.Success, $"CLAND behind the queued ERD should be accepted: {cland.Message}");

        ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Pattern.PendingLandingClearance?.Clearance);
        Assert.Equal("28R", ac.Pattern.PendingLandingClearance?.RunwayId);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // the queued ERD survived the clearance

        // Fly on until the queued entry reaches VPCOL and builds its circuit.
        for (int t = PreClandSeconds + 1; t <= EntryFiresSeconds; t++)
        {
            engine.ReplayRange(t - 1, t, recording.Actions);
            if (engine.FindAircraft(Callsign)?.Phases is not null)
            {
                output.WriteLine($"queued ERD built its circuit at t={t}");
                break;
            }
        }

        ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        Assert.Equal("28R", ac.Phases.ClearedRunwayId);
        Assert.Contains(ac.Phases.Phases, p => p is LandingPhase);
        Assert.Null(ac.Pattern.PendingLandingClearance); // consumed once
    }
}
