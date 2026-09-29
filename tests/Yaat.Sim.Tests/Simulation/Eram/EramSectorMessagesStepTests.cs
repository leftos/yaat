using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Eram;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Eram;

/// <summary>
/// The ERAM sector messages (the <c>SM</c> entry) on the bare engine: a <see cref="RecordedEramRoomEntry"/> of the shape
/// <c>SM {sector} {text}</c> stores one facility sector's message, <c>SMDE {sector}</c> deletes it, the conflict-alert
/// settings and their host notification stay untouched, the snapshot carries the messages and a replay from zero rebuilds
/// them.
/// </summary>
public class EramSectorMessagesStepTests
{
    private const string Facility = "ZOA";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public EramSectorMessagesStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static RecordedEramRoomEntry Entry(double elapsed, string entry) => new(elapsed, Facility, entry);

    /// <summary>A recording of everything the engine has run so far, carrying the ARTCC the replay re-initialises from.</summary>
    private SessionRecording Recording(SimulationEngine engine)
    {
        SimScenarioState scenario = engine.Scenario!;
        return new SessionRecording
        {
            ScenarioJson = AiTestFixture.ParkedAtOak,
            RngSeed = 7,
            Actions = [.. scenario.ActionLog],
            TotalElapsedSeconds = scenario.ElapsedSeconds,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };
    }

    private static string? Message(SimulationEngine engine, string facilityId, string sectorId) =>
        engine.EramSectorMessages.TryGet(facilityId, sectorId, out string? text) ? text : null;

    [Fact]
    public void SmEntry_StoresPerSector()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "SM 44 WX DEVIATIONS NORTH"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "SM 62 RELIEF IN 5"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(new RecordedEramRoomEntry(0, "ZLA", "SM 44 OTHER CENTER"), host).Success);

        Assert.Equal("WX DEVIATIONS NORTH", Message(engine, Facility, "44"));
        Assert.Equal("RELIEF IN 5", Message(engine, Facility, "62"));
        Assert.Equal("OTHER CENTER", Message(engine, "ZLA", "44"));
        Assert.Null(Message(engine, Facility, "45"));
        Assert.Equal(3, engine.EramSectorMessages.Messages.Count);
        Assert.Empty(engine.EramRoomSettings.Facilities);
        Assert.Equal(0, host.EramConflictSettingsChanges);
    }

    [Fact]
    public void SmEntry_OverwritesSameSector()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "SM 44 FIRST"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(1, "SM 44 SECOND MESSAGE"), host).Success);

        Assert.Equal("SECOND MESSAGE", Message(engine, Facility, "44"));
        Assert.Single(engine.EramSectorMessages.Messages);
    }

    [Fact]
    public void SmDeEntry_Deletes()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "SM 44 FIRST"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "SM 62 SECOND"), host).Success);

        Assert.True(engine.Actions.ApplyRecorded(Entry(1, "SMDE 44"), host).Success);

        Assert.Null(Message(engine, Facility, "44"));
        Assert.Equal("SECOND", Message(engine, Facility, "62"));

        // Deleting a sector that holds no message is still a well-formed entry and changes nothing.
        Assert.True(engine.Actions.ApplyRecorded(Entry(2, "SMDE 44"), host).Success);
        Assert.Single(engine.EramSectorMessages.Messages);
        Assert.Equal(0, host.EramConflictSettingsChanges);
    }

    [Theory]
    [InlineData("SM")]
    [InlineData("SM ")]
    [InlineData("SM 44")]
    [InlineData("SM 44 ")]
    [InlineData("SM 44    ")]
    [InlineData("SM  HELLO")]
    [InlineData("SM ALL HELLO")]
    [InlineData("SM 44  HELLO")]
    [InlineData("SM 44 HELLO ")]
    [InlineData("SMDE")]
    [InlineData("SMDE ")]
    [InlineData("SMDE ALL")]
    [InlineData("SMDE 44 62")]
    [InlineData("sm 44 HELLO")]
    [InlineData("SMX 44 HELLO")]
    [InlineData("SW 44 HELLO")]
    public void MalformedSmEntry_Fails(string entry)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "SM 62 KEEP"), host).Success);

        CommandResult applied = engine.Actions.ApplyRecorded(Entry(1, entry), host);

        Assert.False(applied.Success);
        EramSectorMessage message = Assert.Single(engine.EramSectorMessages.Messages);
        Assert.Equal(new EramSectorMessage(Facility, "62", "KEEP"), message);
        Assert.Empty(engine.EramRoomSettings.Facilities);
    }

    /// <summary>
    /// <see cref="SimulationEngine.CaptureSnapshot"/> carries the messages in facility then sector order, and a restore
    /// replaces them whole: a snapshot taken while no sector held one carries no section and restores none.
    /// </summary>
    [Fact]
    public void SectorMessages_SurviveSnapshotRoundTrip()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        StateSnapshotDto empty = engine.CaptureSnapshot();
        Assert.Null(empty.Server!.EramSectorMessages);

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "SM 62 SECOND SECTOR"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(Entry(0, "SM 44 FIRST SECTOR"), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(new RecordedEramRoomEntry(0, "ZLA", "SM 12 OTHER CENTER"), host).Success);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();

        List<EramSectorMessageSnapshotDto> captured = Assert.IsType<List<EramSectorMessageSnapshotDto>>(snapshot.Server!.EramSectorMessages);
        Assert.Equal(
            ["ZLA/12/OTHER CENTER", "ZOA/44/FIRST SECTOR", "ZOA/62/SECOND SECTOR"],
            captured.Select(m => $"{m.FacilityId}/{m.SectorId}/{m.Text}")
        );

        SimulationEngine restored = Engine()!;
        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal("FIRST SECTOR", Message(restored, Facility, "44"));
        Assert.Equal("SECOND SECTOR", Message(restored, Facility, "62"));
        Assert.Equal("OTHER CENTER", Message(restored, "ZLA", "12"));

        restored.RestoreFromSnapshot(empty);

        Assert.Empty(restored.EramSectorMessages.Messages);
    }

    /// <summary>
    /// The Sim replay pin: a recording whose log carries the entries a live run wrote rebuilds the messages, and a replay
    /// from zero starts from none — a message the replaying engine held before is gone.
    /// </summary>
    [Fact]
    public void ReplayFromZero_ReproducesSectorMessages()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        for (int i = 0; i < 5; i++)
        {
            engine.TickOneSecond();
        }

        double now = engine.Scenario!.ElapsedSeconds;
        Assert.True(engine.Actions.IssueDerived(Entry(now, "SM 44 FIRST"), host).Success);
        Assert.True(engine.Actions.IssueDerived(Entry(now, "SM 62 SECOND"), host).Success);
        Assert.True(engine.Actions.IssueDerived(Entry(now, "SMDE 62"), host).Success);
        SessionRecording recording = Recording(engine);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        Assert.True(replayed.EramSectorMessages.TryApply(Facility, "SM 13 STALE"));
        replayed.Replay(recording, now + 5);

        EramSectorMessage message = Assert.Single(replayed.EramSectorMessages.Messages);
        Assert.Equal(new EramSectorMessage(Facility, "44", "FIRST"), message);
    }
}
