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
/// Hybrid replay from the exact reported state: the rejected CLAND was never recorded as an action
/// (only successful commands are), so the test restores the snapshot at t=2825 — N805FM with no
/// PhaseList and the <c>ERD 28R</c> block sitting unapplied in the queue — and issues the clearance
/// itself. The restored block has no <c>ParsedCommands</c>, so the pre-arm resolves the entry's runway
/// by re-parsing <c>SourceCommandText</c>, and the block itself only fires because
/// <c>SimulationEngine.RehydrateRestoredQueueBlocks</c> rebuilds its <c>ApplyAction</c> — this test
/// covers both recovery paths end-to-end on real recorded data.
/// </summary>
public class QueuedClandPreArmE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/8201087a0088.zip";
    private const string Callsign = "N805FM";
    private const int PreClandSeconds = 2825;
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

        var snap = archive.ReadSnapshotAt(PreClandSeconds);
        if (snap is null)
        {
            output.WriteLine($"No snapshot near t={PreClandSeconds}, skipping");
            return;
        }

        engine.RestoreFromSnapshot(snap.State);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.Null(ac.Phases); // the reported state: the entry has not built anything yet
        var queuedEntry = Assert.Single(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Contains("ERD 28R", queuedEntry.Description);
        Assert.Null(queuedEntry.ParsedCommands); // restored block — recovery runs from SourceCommandText

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
