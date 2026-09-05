using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The <c>CON</c> / <c>CON+</c> / <c>DECON</c> arms, one body on every run kind. A basic consolidation records the
/// override; a full one also moves the sender's whole block — the sender plus every descendant nobody attends — onto the
/// receiver, transferring owned tracks and redirecting in-progress handoffs. Whether a descendant is attended is the
/// host's answer (CRC attendance is room state), so the tests hand the router a host that answers it. Uses the real NCT
/// hierarchy from the ZOA config: <c>4U</c> is the parent of <c>4Q</c> and <c>4R</c>.
/// </summary>
public class ConsolidationArmTests
{
    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
    private static readonly TrackOwner Nct4Q = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ConsolidationArmTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;
        return engine;
    }

    private static RecordedCommand Recorded(string command) => new(0, "", command, "XX", "conn-1");

    private static string TcpId(SimulationEngine engine, string code) => TrackResolver.FindTcpByCode(engine.Scenario!, code)!.Id;

    [Fact]
    public void Consolidate_Basic_Replay_RecordsTheOverride_AndLeavesTracksAlone()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceHost();
        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Nct4Q;

        var outcome = engine.Actions.Apply(Recorded("CON 2B 4U"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("Basic consolidation: 4U → 2B", outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Consolidate, ActionScope.Global, IsHostSlot: false), outcome.Trace);
        var over = engine.ConsolidationState.GetOverride(TcpId(engine, "4U"));
        Assert.NotNull(over);
        Assert.Equal(TcpId(engine, "2B"), over!.ReceivingTcpId);
        Assert.True(over.IsBasic);
        Assert.True(ac.Track.Owner!.MatchesPosition(Nct4Q));
        Assert.Equal(1, host.ConsolidationChanges);
        Assert.Empty(engine.Scenario!.ActionLog);
    }

    [Fact]
    public void Consolidate_Full_Replay_TransfersTheBlocksTracks_AndRedirectsItsHandoffs()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceHost();
        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Nct4Q;
        ac.Track.HandoffPeer = Nct4U;

        var outcome = engine.Actions.Apply(Recorded("CON+ 2B 4U"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("Full consolidation: 4U → 2B (1 track(s) transferred, 1 handoff(s) redirected)", outcome.Result.Message);
        var over = engine.ConsolidationState.GetOverride(TcpId(engine, "4U"));
        Assert.NotNull(over);
        Assert.False(over!.IsBasic);
        Assert.True(ac.Track.Owner!.MatchesPosition(Student));
        Assert.True(ac.Track.HandoffPeer!.MatchesPosition(Student));
        Assert.True(ac.Track.HandoffRedirectedBy!.MatchesPosition(Nct4U));
        Assert.Equal(1, host.ConsolidationChanges);
    }

    [Fact]
    public void Consolidate_Full_LeavesTheTracksOfAnAttendedDescendant()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceHost();
        host.AttendedTcpIds.Add(TcpId(engine, "4Q"));
        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Nct4Q;

        var outcome = engine.Actions.Apply(Recorded("CON+ 2B 4U"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("Full consolidation: 4U → 2B", outcome.Result.Message);
        Assert.True(ac.Track.Owner!.MatchesPosition(Nct4Q));
    }

    [Fact]
    public void Consolidate_Refuses_AnUnknownPosition_AndALoop()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceHost();

        var unknown = engine.Actions.Apply(Recorded("CON 2B 6Q"), host);
        var first = engine.Actions.Apply(Recorded("CON 2B 4U"), host);
        var loop = engine.Actions.Apply(Recorded("CON 4U 2B"), host);

        Assert.False(unknown.Result.Success);
        Assert.Equal("Unknown position: 6Q", unknown.Result.Message);
        Assert.True(first.Result.Success, first.Result.Message);
        Assert.False(loop.Result.Success);
        Assert.Equal("Circular consolidation: 2B → 4U would create a loop", loop.Result.Message);
        Assert.Null(engine.ConsolidationState.GetOverride(TcpId(engine, "2B")));
        Assert.Equal(1, host.ConsolidationChanges);
    }

    [Fact]
    public void Deconsolidate_Replay_RemovesTheOverride()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceHost();
        engine.Actions.Apply(Recorded("CON 2B 4U"), host);

        var outcome = engine.Actions.Apply(Recorded("DECON 4U"), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("Deconsolidated: 4U", outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Deconsolidate, ActionScope.Global, IsHostSlot: false), outcome.Trace);
        Assert.Null(engine.ConsolidationState.GetOverride(TcpId(engine, "4U")));
        Assert.Equal(2, host.ConsolidationChanges);
    }

    /// <summary>A host with no room: every slot refused, every consumer a no-op — except the two the consolidation arms use.</summary>
    private sealed class AttendanceHost : IActionHost
    {
        public HashSet<string> AttendedTcpIds { get; } = [];

        public int ConsolidationChanges { get; private set; }

        public bool IsPositionAttended(Tcp tcp) => AttendedTcpIds.Contains(tcp.Id);

        public void OnConsolidationChanged() => ConsolidationChanges++;

        public CommandResult ApplyStrip(string callsign, ParsedCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

        public CommandResult ApplyTdls(AircraftState aircraft, ParsedCommand command) => ActionRefusals.HostOnly(command);

        public CommandResult ApplyTdlsOpsConfig(TdlsOpsConfigCommand command) => ActionRefusals.HostOnly(command);

        public CommandResult ApplyCoordination(AircraftState aircraft, ParsedCommand command, TrackOwner? identity) =>
            ActionRefusals.HostOnly(command);

        public CommandResult ApplyGlobalCoordination(CoordinationAutoAckCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

        public CommandResult ApplyAsdexEnableAllAlerts() => ActionRefusals.HostOnly("ASDXALERTS");

        public CommandResult ApplyBookmark(BookmarkCommand command) => ActionRefusals.HostOnly(command);

        public CommandResult ApplyTransport(ParsedCommand command) => ActionRefusals.HostOnly(command);

        public CommandResult ApplyFlightPlanCommand(string callsign, ParsedCommand command, TrackOwner? identity) => ActionRefusals.HostOnly(command);

        public void OnAircraftSpawned(AircraftState aircraft) { }

        public void OnAircraftDeleted(string callsign) { }

        public void OnPositionSelected(string connectionId, TrackOwner owner) { }

        public void OnTimersChanged() { }

        public void OnHeldDeparturesChanged() { }

        public void OnFlightPlanAmended(string callsign) { }
    }
}
