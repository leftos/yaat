using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for "manual handoff ACCEPT skips the white-FDB datablock stage". When a track is handed off and
/// a position accepts it via the <c>ACCEPT</c> command, the previous owner's STARS datablock must stay a
/// white FDB (CRC's <c>WasPreviouslyOwned</c>) until that controller slews — exactly like the
/// <c>[AutoAccept]</c> timer path. Before the fix the manual path left <c>WasPreviouslyOwned</c> unset,
/// so the previous owner's block dropped straight to an unowned green PDB.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts (ZOA/NCT, OAK), aircraft N569SX. The student
/// is OAK_TWR (3O). The relevant event:
///   t=166 student "AS 3O HO 4R" -> handoff to OAK_DEP (4R)
///   t=171 RPO "ACCEPT"          -> Owner flips 3O -> 4R (the RPO/4R accepts the student's handoff)
/// After t=171 the student (3O) is the previous owner and must still see a white FDB for N569SX.
///
/// Hybrid replay: the track-ownership lead-up does not reconstruct from t=0, so this restores the
/// recorded snapshot at t=170 (N569SX owned by 3O, handoff to 4R already pending) and replays just the
/// ACCEPT (t=171) with current code.
/// </summary>
public class HandoffAcceptDatablockColorE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/handoff-accept-datablock-white-recording.zip";

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
    public void ManualAccept_PreviousOwnerKeepsWhiteFullDatablock()
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

        // Restore the recorded pre-accept state: at t=170 N569SX is owned by the student OAK_TWR (3O),
        // with a handoff to OAK_DEP (4R) already pending.
        var snap = archive.ReadSnapshotAt(170);
        if (snap is null)
        {
            output.WriteLine("No snapshot near t=170, skipping");
            return;
        }

        engine.RestoreFromSnapshot(snap.State);

        var scenario = engine.Scenario;
        Assert.NotNull(scenario);
        Assert.NotNull(scenario.StudentPosition);
        Assert.NotNull(scenario.StudentTcp);

        // The student (OAK_TWR / 3O) owns the track and is about to hand it off — it becomes the
        // previous owner once 4R accepts, and its scope is the one the bug regresses.
        var restored = engine.FindAircraft("N569SX");
        Assert.NotNull(restored);
        Assert.Equal(scenario.StudentPosition.Callsign, restored.Track.Owner?.Callsign);

        // The recording carries a sticky WasPreviouslyOwned on the student's TCP from earlier
        // (auto-accepted) handoff cycles. Clear it to simulate the student having slewed to acknowledge,
        // so the assertion isolates whether the t=171 manual accept re-sets it from a clean state.
        restored.Stars.SharedState.Remove(scenario.StudentTcp.Id);

        // Replay just the RPO's "ACCEPT" (t=171) with current code.
        engine.ReplayRange((int)snap.ElapsedSeconds, 172, recording.Actions);

        var ac = engine.FindAircraft("N569SX");
        Assert.NotNull(ac);

        // The accept applied: OAK_DEP (4R) now owns the track.
        Assert.NotNull(ac.Track.Owner);
        Assert.Equal("OAK_DEP", ac.Track.Owner.Callsign);

        // The student (the previous owner) must keep a white FDB: WasPreviouslyOwned set on its TCP.
        Assert.True(
            ac.Stars.SharedState.TryGetValue(scenario.StudentTcp.Id, out var shared) && shared.WasPreviouslyOwned,
            "Student (previous owner) should have WasPreviouslyOwned set after the RPO's manual accept"
        );

        var view = StarsDatablockClassifier.Classify(ac, scenario.StudentTcp, scenario.StudentPosition);

        output.WriteLine($"student={scenario.StudentPosition.Callsign} owner={ac.Track.Owner.Callsign} color={view.Color} level={view.Level}");

        Assert.Equal(StarsDatablockColor.Owned, view.Color); // white
        Assert.Equal(StarsDatablockLevel.Full, view.Level); // FDB
    }
}
