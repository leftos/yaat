using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #216 — auto-handoff between automated control positions. In the ZOA solo scenario
/// "S3-NCTA-1 | Area A Familiarization", background traffic owned by the autocontroller OAK_14_CTR is
/// supposed to auto-hand-off to another autocontroller (NCT) via preset commands of the form
/// <c>AT &lt;fix&gt; HO &lt;tcp&gt;</c>. Before the fix the conditional handoff hit
/// <c>CommandDispatcher.ApplyCommand</c>'s unhandled <c>default:</c> arm (the no-dispatcher-arm fallback)
/// because the preset path never routed track commands to the track engine.
///
/// Recording: the live bug bundle. Aircraft ASA221 spawns at t=0 owned by OAK_14_CTR and carries
/// <c>AT EPICK HO 2W</c>; it sequences EPICK around t=605. After the fix the preset must initiate a
/// handoff — <c>Track.HandoffPeer</c> resolves to the NCT subset-2 sector-W position (the bare TCP
/// <c>2W</c> resolves through the scenario's automated <c>atc</c> positions). Auto-accept is a
/// server-only step, so in this pure-Sim replay the handoff stays pending (HandoffPeer set).
///
/// The sibling <c>AT SERFR HO Q2B</c> (ERAM-prefixed) does NOT resolve here: the bundle's embedded
/// ARTCC config is lossy and lacks <c>neighboringStarsConfigurations</c>, so the <c>Q</c> prefix can't
/// map to NCT in this fixture. The prefix resolver is covered by a dedicated unit test instead.
/// </summary>
public class Issue216AutoHandoffTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue216-auto-handoff-autocontroller-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.InitializeForTest(loggerFactory);

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void AtEpickHandoff_ToAutomatedNctPosition_Initiates()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var recording = archive.ToBaseSessionRecording();

        // Forward-replay to a point before EPICK is sequenced so ASA221's presets dispatch fresh and the
        // AT EPICK block is still pending with its ApplyAction intact (a snapshot restore would drop the
        // ApplyAction). EPICK is sequenced somewhere after t=470; replay to just past the last snapshot
        // that still showed EPICK on the route.
        engine.Replay(recording, 475);

        var ac = engine.FindAircraft("ASA221");
        Assert.NotNull(ac);

        // Pure-Sim replay does not run the server-side autotrack step (TickProcessor.ApplyAutoTrack
        // conditions), so ASA221 is unowned here. Establish the ownership the server would have set, so
        // the AT EPICK HO 2W preset has an owner to hand off FROM — exactly the live precondition.
        ac!.Track.Owner = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");
        ac.Track.HandoffPeer = null;

        // Step forward until ASA221 sequences EPICK and the AT EPICK HO 2W preset fires.
        for (int i = 0; i < 200 && ac is { Track.HandoffPeer: null }; i++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("ASA221");
        }

        Assert.NotNull(ac);
        output.WriteLine(
            $"ASA221 owner={ac!.Track.Owner?.Callsign} handoffPeer={ac.Track.HandoffPeer?.Callsign} "
                + $"({ac.Track.HandoffPeer?.FacilityId}/{ac.Track.HandoffPeer?.Subset}{ac.Track.HandoffPeer?.SectorId})"
        );

        // After the fix, the AT EPICK HO 2W preset initiated a handoff to the automated NCT 2W position.
        Assert.NotNull(ac.Track.HandoffPeer);
        Assert.Equal("NCT", ac.Track.HandoffPeer!.FacilityId);
        Assert.Equal(2, ac.Track.HandoffPeer.Subset);
        Assert.Equal("W", ac.Track.HandoffPeer.SectorId);
    }

    /// <summary>
    /// The triggered handoff must survive a command-queue snapshot round-trip — exactly the production
    /// rewind path (RecordingManager restores a snapshot, then reconstructs forward). FromSnapshot drops
    /// the block's runtime ParsedCommands and ApplyAction, so ProcessTriggeredTrackBlocks has to re-parse
    /// SourceCommandText to recover the HO 2W command.
    /// </summary>
    [Fact]
    public void AtEpickHandoff_SurvivesQueueSnapshotRoundTrip()
    {
        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        var recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 475);

        var ac = engine.FindAircraft("ASA221");
        Assert.NotNull(ac);

        // Round-trip the queue through a snapshot, dropping ParsedCommands/ApplyAction while keeping the
        // durable HasTrackCommand flag, trigger, and SourceCommandText — what RestoreFromSnapshot leaves.
        ac!.Queue = CommandQueue.FromSnapshot(ac.Queue.ToSnapshot());
        Assert.Contains(ac.Queue.Blocks, b => b is { HasTrackCommand: true, TrackApplied: false, ParsedCommands: null });

        ac.Track.Owner = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");
        ac.Track.HandoffPeer = null;

        for (int i = 0; i < 200 && ac is { Track.HandoffPeer: null }; i++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("ASA221");
        }

        Assert.NotNull(ac);
        output.WriteLine(
            $"ASA221 (post-restore) handoffPeer={ac!.Track.HandoffPeer?.FacilityId}/{ac.Track.HandoffPeer?.Subset}{ac.Track.HandoffPeer?.SectorId}"
        );

        Assert.NotNull(ac.Track.HandoffPeer);
        Assert.Equal("NCT", ac.Track.HandoffPeer!.FacilityId);
        Assert.Equal(2, ac.Track.HandoffPeer.Subset);
        Assert.Equal("W", ac.Track.HandoffPeer.SectorId);
    }
}
