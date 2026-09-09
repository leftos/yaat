using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Eram;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Eram;

/// <summary>
/// The ERAM Continuous Range Readout groups on the bare engine: a <see cref="RecordedEramCrrGroup"/> creates, replaces,
/// recolors or (null latitude) deletes one through <see cref="SimulationEngine.ApplyCrrGroup"/>, the drain hands the
/// host one payload-less notification per applied record, the snapshot carries the set, and a replay of a recording
/// holding the record rebuilds it. Before the body crossed, every one of these went to a host slot the bare and replay
/// hosts discarded, so a Sim-side run held no groups at all.
///
/// <para>
/// Membership is not here: it rides each aircraft's <c>CrrGroupLabel</c> through the <c>LF</c> ERAM entries, which
/// were already the Sim's.
/// </para>
/// </summary>
public class EramCrrGroupStepTests
{
    private const string Label = "BOSOX";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public EramCrrGroupStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static RecordedEramCrrGroup Created(double elapsed, string color, double lat, double lon) => new(elapsed, Label, color, lat, lon);

    /// <summary>A recording of everything the engine has run so far, carrying the ARTCC the replay re-initialises from.</summary>
    private SessionRecording Recording(SimulationEngine engine)
    {
        var scenario = engine.Scenario!;
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

    [Fact]
    public void ACreateRecord_PutsTheGroupOnTheEngine_AndTellsTheHostOnce()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        var applied = engine.Actions.ApplyRecorded(Created(0, "Yellow", 37.5, -122.0), host);

        Assert.True(applied.Success, applied.Message);
        var group = Assert.Single(engine.CrrGroups.Values);
        Assert.Equal(Label, group.Label);
        Assert.Equal(EramCrrColor.Yellow, group.Color);
        Assert.Equal(37.5, group.Latitude, 6);
        Assert.Equal(-122.0, group.Longitude, 6);
        Assert.Equal(1, host.EramCrrGroupChanges);
    }

    /// <summary>
    /// A redefinition is a second create under the same label: the group moves, and there is still exactly one — the
    /// dictionary is keyed by label, not by write.
    /// </summary>
    [Fact]
    public void ASecondCreateUnderTheSameLabel_ReplacesTheLocation()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Created(0, "White", 37.5, -122.0), host).Success);

        Assert.True(engine.Actions.ApplyRecorded(Created(1, "White", 38.25, -121.5), host).Success);

        var group = Assert.Single(engine.CrrGroups.Values);
        Assert.Equal(38.25, group.Latitude, 6);
        Assert.Equal(-121.5, group.Longitude, 6);
        Assert.Equal(EramCrrColor.White, group.Color);
        Assert.Equal(2, host.EramCrrGroupChanges);
    }

    /// <summary>The CRR View menu's recolor: the same location under a new colour, and still one group.</summary>
    [Fact]
    public void ARecolorRecord_KeepsTheLocationAndChangesTheColour()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Created(0, "White", 37.5, -122.0), host).Success);

        Assert.True(engine.Actions.ApplyRecorded(Created(2, "Coral", 37.5, -122.0), host).Success);

        var group = Assert.Single(engine.CrrGroups.Values);
        Assert.Equal(EramCrrColor.Coral, group.Color);
        Assert.Equal(37.5, group.Latitude, 6);
        Assert.Equal(-122.0, group.Longitude, 6);
        Assert.Equal(2, host.EramCrrGroupChanges);
    }

    [Fact]
    public void ANullLatitudeRecord_RemovesTheGroup()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Created(0, "White", 37.5, -122.0), host).Success);
        Assert.NotEmpty(engine.CrrGroups);

        var deleted = engine.Actions.ApplyRecorded(new RecordedEramCrrGroup(3, Label, Color: null, Lat: null, Lon: null), host);

        Assert.True(deleted.Success, deleted.Message);
        Assert.Empty(engine.CrrGroups);
        Assert.Equal(2, host.EramCrrGroupChanges);
    }

    /// <summary>An unknown or missing colour name is the sector default rather than a refusal, as the room's body was.</summary>
    [Fact]
    public void AnUnknownColour_FallsBackToWhite()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        Assert.True(engine.Actions.ApplyRecorded(Created(0, "Chartreuse", 37.5, -122.0), host).Success);

        Assert.Equal(EramCrrColor.White, Assert.Single(engine.CrrGroups.Values).Color);
    }

    /// <summary>
    /// <see cref="SimulationEngine.CaptureSnapshot"/> carries the groups, so an engine that never applied the record —
    /// a bundle reconstruction from a snapshot, a session restore — holds the same set as the run that made them.
    /// </summary>
    [Fact]
    public void ASnapshotRoundTrip_KeepsTheGroups()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Assert.True(engine.Actions.ApplyRecorded(Created(0, "Green", 37.5, -122.0), host).Success);
        Assert.True(engine.Actions.ApplyRecorded(new RecordedEramCrrGroup(0, "ALT", "Coral", 38.0, -121.0), host).Success);

        var snapshot = engine.CaptureSnapshot(engine.Scenario!.ActionLog.Count);

        var restored = Engine()!;
        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal(2, restored.CrrGroups.Count);
        Assert.Equal(
            engine.CrrGroups.Values.OrderBy(g => g.Label, StringComparer.Ordinal),
            restored.CrrGroups.Values.OrderBy(g => g.Label, StringComparer.Ordinal)
        );
    }

    /// <summary>
    /// The Sim replay pin: a recording whose log carries the CRR record a live run wrote rebuilds the group on a fresh
    /// engine. Before the body crossed, the replay host discarded the record and the replayed engine held nothing.
    /// </summary>
    [Fact]
    public void Replay_RebuildsTheGroupFromTheRecordedWrite()
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

        Assert.True(engine.Actions.IssueDerived(Created(engine.Scenario!.ElapsedSeconds, "Yellow", 37.5, -122.0), host).Success);
        var live = Assert.Single(engine.CrrGroups.Values);

        var recording = Recording(engine);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        replayed.Replay(recording, engine.Scenario!.ElapsedSeconds + 5);

        Assert.Equal(live, Assert.Single(replayed.CrrGroups.Values));
    }

    /// <summary>
    /// <see cref="EramCrrColor"/> is cast straight to the server's wire enum with no mapping in between, so its
    /// numbering is a wire contract; the server side pins the two against each other, and this pins the numbering
    /// itself so a reorder here fails on both sides.
    /// </summary>
    [Fact]
    public void TheCrrColours_AreTheWireNumbering()
    {
        Assert.Equal(0, (int)EramCrrColor.Green);
        Assert.Equal(1, (int)EramCrrColor.Coral);
        Assert.Equal(2, (int)EramCrrColor.White);
        Assert.Equal(3, (int)EramCrrColor.Yellow);
        Assert.Equal(4, Enum.GetValues<EramCrrColor>().Length);
    }
}
