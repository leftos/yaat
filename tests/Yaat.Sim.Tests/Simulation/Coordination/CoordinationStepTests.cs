using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Coordination;

/// <summary>
/// The STARS coordination bodies on the bare engine: the seven aircraft verbs (<c>RD</c>, <c>RDH</c>, <c>RDR</c>,
/// <c>RDACK</c>, <c>RDDEL</c>, <c>RDPOS</c>, <c>RDTXT</c>), the global <c>RDAUTO</c>, the expiry/recall timers spine
/// step and the removal a <c>TRACK</c> owes. They decide from engine state alone — the scenario's clock, the ARTCC's
/// coordination lists and the acting position — so a plain <see cref="SimulationEngine.RunSecond"/> over the real ZOA
/// data reaches every gate the live room reaches, with no server in the process.
///
/// <para>
/// The list under test is ZOA's <c>POAK</c> ("OAK DEP"), whose only sender is NCT's TCP 3O — the TCP the student
/// position OAK_TWR works — and whose receivers include 4U. A sender on exactly one list may omit the list id, which
/// is why a bare <c>RD</c> here resolves to <c>POAK</c>.
/// </para>
/// </summary>
public class CoordinationStepTests
{
    private const string ListId = "POAK";
    private const string CallsignA = "SWA1234";
    private const string CallsignB = "SWA5678";

    /// <summary>ZOA's OAK_TWR ("LC1"), whose STARS TCP is 3O — <c>POAK</c>'s only sender.</summary>
    private const string OakTwrPositionId = "01GEAMB98RKCPP9HCNPW5AVDA5";

    /// <summary>The connection acting as 3O (the sender), and the one acting as 4U (a receiver).</summary>
    private const string SenderConnection = "conn-1";

    private const string ReceiverConnection = "conn-2";

    /// <summary>Two IFR departures parked on the OAK field — two so a per-aircraft removal has a control.</summary>
    private const string TwoAtOak = """
        {
          "id": "coordination-two",
          "name": "Two departures at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "SWA1234",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7213, "lon": -122.2208 },
                "altitude": 9,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KOAK",
                "destination": "KSFO",
                "cruiseAltitude": 35000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            },
            {
              "id": "a2",
              "aircraftId": "SWA5678",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7205, "lon": -122.2190 },
                "altitude": 9,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KOAK",
                "destination": "KSAN",
                "cruiseAltitude": 33000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public CoordinationStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// A bare engine with the ZOA configuration resolved and the student position set the way the server's load
    /// resolves it, then the ARTCC initialisation that load runs — which is what puts <c>POAK</c> on the engine.
    /// </summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(TwoAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = _zoa.ResolvePosition(OakTwrPositionId);
        scenario.StudentTcp = _zoa.GetTcpForPosition(OakTwrPositionId);
        engine.InitializeFromArtcc();
        Assert.True(scenario.CoordinationChannels.ContainsKey(ListId), "POAK should carry a coordination channel from the ZOA tree");

        // Building the channels is itself a change the host is owed a push for: the server's load drains this flag at
        // the end of the load, which is what puts the lists on a CRC client joining a freshly loaded room.
        Assert.True(engine.DrainCoordinationChanged(), "InitializeFromArtcc should have marked the coordination lists changed");
        return engine;
    }

    private static CoordinationChannel Channel(SimulationEngine engine) => engine.Scenario!.CoordinationChannels[ListId];

    private static CommandResult Issue(SimulationEngine engine, IActionHost host, string connectionId, string callsign, string command) =>
        engine.Actions.Issue(new ActionInput(callsign, command, connectionId, "XX", Baked: null), host).Result;

    /// <summary>The identity every verb below acts under: 3O on one connection, 4U on the other.</summary>
    private static void SelectPositions(SimulationEngine engine, IActionHost host)
    {
        var sender = Issue(engine, host, SenderConnection, "", "AS 3O");
        Assert.True(sender.Success, sender.Message);
        var receiver = Issue(engine, host, ReceiverConnection, "", "AS 4U");
        Assert.True(receiver.Success, receiver.Message);
    }

    private static CommandResult Send(SimulationEngine engine, IActionHost host, string callsign, string command) =>
        Issue(engine, host, SenderConnection, callsign, command);

    private static CommandResult Receive(SimulationEngine engine, IActionHost host, string callsign, string command) =>
        Issue(engine, host, ReceiverConnection, callsign, command);

    /// <summary>A recording of everything <paramref name="engine"/> has run so far, carrying the ARTCC the replay re-initialises from.</summary>
    private SessionRecording Recording(SimulationEngine engine)
    {
        var scenario = engine.Scenario!;
        return new SessionRecording
        {
            ScenarioJson = TwoAtOak,
            RngSeed = 7,
            Actions = [.. scenario.ActionLog],
            TotalElapsedSeconds = scenario.ElapsedSeconds,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };
    }

    [Fact]
    public void Rd_CreatesAnUnacknowledgedItemUnderADeterministicId()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);

        var first = Send(engine, host, CallsignA, "RD");

        Assert.True(first.Success, first.Message);
        var item = Assert.Single(Channel(engine).Items);
        Assert.Equal(CallsignA, item.AircraftId);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
        Assert.Equal("POAK-1", item.Id);
        Assert.Equal(1, item.SequenceNumber);

        var second = Send(engine, host, CallsignB, "RD");

        Assert.True(second.Success, second.Message);
        Assert.Equal(["POAK-1", "POAK-2"], Channel(engine).Items.Select(i => i.Id));
    }

    [Fact]
    public void Rdack_AcknowledgesAndStartsTheExpiryClock()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD").Success);

        var ack = Receive(engine, host, CallsignA, "RDACK");

        Assert.True(ack.Success, ack.Message);
        var item = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.Acknowledged, item.Status);
        Assert.Equal(engine.Scenario!.ElapsedSeconds + SimScenarioState.CoordinationAckExpirySeconds, item.ExpireTime);
    }

    /// <summary>
    /// The timers are a spine step, so the transitions have to come out of <see cref="SimulationEngine.RunSecond"/>
    /// and the change they raise has to reach the host through the post-physics drain — not through the router's,
    /// which the acknowledgement above already used.
    /// </summary>
    [Fact]
    public void TheTimersStep_WarnsThenVoids_AndRaisesTheChangeFromTheSpine()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);
        Assert.True(Send(engine, spine, CallsignA, "RD").Success);
        Assert.True(Receive(engine, spine, CallsignA, "RDACK").Success);

        // What the router's own drain has already handed over; the tick has to raise the count past this.
        int afterAck = spine.CoordinationChangeCount;
        Assert.True(afterAck > 0, "the acknowledgement should have reached the host through the router's drain");

        // The window is pinned as a literal here rather than read back from CoordinationExpiryWarningSeconds, so
        // that moving the constant moves this test. With 31 s of life left (t = ack + 149) the item is still
        // plain Acknowledged: the caution is about to expire, not most of the ack's life.
        engine.Scenario!.ElapsedSeconds = SimScenarioState.CoordinationAckExpirySeconds - 31 - 1;
        engine.RunSecond(spine);

        Assert.Equal(StarsCoordinationStatus.Acknowledged, Assert.Single(Channel(engine).Items).Status);
        int beforeWarning = spine.CoordinationChangeCount;

        // The second that drops the remaining life to 30 s (t = ack + 150) is the one that warns. 30 s because the
        // warning is a short "about to expire" caution — the same judgement as PointoutNoActionSeconds above it:
        // long enough for the RPO to act by hand, short enough that the state still means the item is nearly gone.
        engine.Scenario!.ElapsedSeconds = SimScenarioState.CoordinationAckExpirySeconds - 30 - 1;
        engine.RunSecond(spine);

        var item = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.DepartureExpirationWarning, item.Status);
        Assert.True(spine.CoordinationChangeCount > beforeWarning, "the spine's drain step should have handed the timer transition over");

        engine.Scenario!.ElapsedSeconds = SimScenarioState.CoordinationAckExpirySeconds - 1;
        engine.RunSecond(spine);

        var voided = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.VoidUnacknowledged, voided.Status);
        Assert.Null(voided.ExpireTime);
    }

    [Fact]
    public void Rdh_HoldsThenSends_AndRdrRecallsUntilTheTimersRemoveIt()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);

        var held = Send(engine, spine, CallsignA, "RDH POAK EXPECT 28R");

        Assert.True(held.Success, held.Message);
        var item = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.Unsent, item.Status);
        Assert.Equal("EXPECT 28R", item.Message);
        int afterCreate = spine.CoordinationChangeCount;
        Assert.True(afterCreate > 0, "creating a held message should have reached the host");

        var sent = Send(engine, spine, CallsignA, "RDH POAK");

        Assert.True(sent.Success, sent.Message);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, Assert.Single(Channel(engine).Items).Status);
        int afterSend = spine.CoordinationChangeCount;
        Assert.True(afterSend > afterCreate, "sending the held message should have reached the host");

        var recalled = Send(engine, spine, CallsignA, "RDR");

        Assert.True(recalled.Success, recalled.Message);
        Assert.True(spine.CoordinationChangeCount > afterSend, "the recall should have reached the host");
        var lingering = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.Recalled, lingering.Status);
        Assert.Equal(engine.Scenario!.ElapsedSeconds + SimScenarioState.CoordinationRecallLingerSeconds, lingering.ExpireTime);

        int beforeLinger = spine.CoordinationChangeCount;
        for (int i = 0; i < (int)SimScenarioState.CoordinationRecallLingerSeconds; i++)
        {
            engine.RunSecond(spine);
        }

        Assert.Empty(Channel(engine).Items);
        Assert.True(spine.CoordinationChangeCount > beforeLinger, "the timers step's removal should have reached the host");
    }

    /// <summary>A recall of a message that was never sent has nothing to show the receiver, so it goes outright.</summary>
    [Fact]
    public void Rdr_OnAHeldMessageRemovesItOutright()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RDH POAK EXPECT 28R").Success);

        int before = host.CoordinationChanges;

        var recalled = Send(engine, host, CallsignA, "RDR");

        Assert.True(recalled.Success, recalled.Message);
        Assert.Empty(Channel(engine).Items);
        Assert.True(host.CoordinationChanges > before, "the recall should have reached the host");
    }

    [Fact]
    public void Rddel_RemovesTheSendersItem()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD").Success);
        int before = host.CoordinationChanges;

        var deleted = Send(engine, host, CallsignA, "RDDEL");

        Assert.True(deleted.Success, deleted.Message);
        Assert.Empty(Channel(engine).Items);
        Assert.True(host.CoordinationChanges > before, "the delete should have reached the host");
    }

    [Fact]
    public void Rdpos_MovesTheItemToTheNamedLine()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD").Success);
        Assert.True(Send(engine, host, CallsignB, "RD").Success);
        int before = host.CoordinationChanges;

        var moved = Send(engine, host, CallsignB, "RDPOS 1");

        Assert.True(moved.Success, moved.Message);
        Assert.Equal(["POAK-2", "POAK-1"], Channel(engine).Items.Select(i => i.Id));
        Assert.True(host.CoordinationChanges > before, "the reorder should have reached the host");
    }

    [Fact]
    public void Rdtxt_ChangesAHeldMessageAndIsRefusedOnASentOne()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RDH POAK EXPECT 28R").Success);
        int beforeEdit = host.CoordinationChanges;

        var changed = Send(engine, host, CallsignA, "RDTXT NEW TEXT");

        Assert.True(changed.Success, changed.Message);
        Assert.Equal("NEW TEXT", Assert.Single(Channel(engine).Items).Message);
        Assert.True(host.CoordinationChanges > beforeEdit, "the text edit should have reached the host");

        Assert.True(Send(engine, host, CallsignA, "RDH POAK").Success);
        int beforeRefusal = host.CoordinationChanges;

        var refused = Send(engine, host, CallsignA, "RDTXT LATER TEXT");

        Assert.False(refused.Success);
        Assert.Equal(beforeRefusal, host.CoordinationChanges);
        Assert.Contains("already sent", refused.Message);
        Assert.Equal("NEW TEXT", Assert.Single(Channel(engine).Items).Message);
    }

    [Fact]
    public void Rdauto_MakesTheNextReleaseAcknowledgeItself()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);

        int beforeToggle = host.CoordinationChanges;

        var toggled = Receive(engine, host, "", "RDAUTO POAK");

        Assert.True(toggled.Success, toggled.Message);
        Assert.True(Channel(engine).Receivers.Single(r => r.Tcp.ToString() == "4U").AutoAcknowledge);
        Assert.True(host.CoordinationChanges > beforeToggle, "the auto-acknowledge toggle should have reached the host");

        Assert.True(Send(engine, host, CallsignA, "RD").Success);

        var item = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.Acknowledged, item.Status);
        Assert.True(item.WasAutomaticRelease);
        Assert.Equal(engine.Scenario!.ElapsedSeconds + SimScenarioState.CoordinationAckExpirySeconds, item.ExpireTime);
    }

    /// <summary>Once a controller owns the track the release rundown has served its purpose, so the aircraft's items go.</summary>
    [Fact]
    public void Track_RemovesTheAcquiredAircraftsItems()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD").Success);
        Assert.True(Send(engine, host, CallsignB, "RD").Success);
        int before = host.CoordinationChanges;

        var tracked = Receive(engine, host, CallsignA, "TRACK");

        Assert.True(tracked.Success, tracked.Message);
        var remaining = Assert.Single(Channel(engine).Items);
        Assert.Equal(CallsignB, remaining.AircraftId);
        Assert.True(host.CoordinationChanges > before, "the track removal should have reached the host");
    }

    /// <summary>
    /// Both refusal routes, because they are different guards: a list-less release from a position that sends on
    /// nothing cannot resolve a channel at all, while one that names <c>POAK</c> resolves the channel and is then
    /// turned away by the sender check on it.
    /// </summary>
    [Fact]
    public void ANonSendersRelease_IsRefused()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);

        var inferred = Receive(engine, host, CallsignA, "RD");

        Assert.False(inferred.Success);
        Assert.Contains("not a sender", inferred.Message);

        var named = Receive(engine, host, CallsignA, "RD POAK");

        Assert.False(named.Success);
        Assert.Contains("not a sender on list POAK", named.Message);
        Assert.Empty(Channel(engine).Items);
    }

    /// <summary>
    /// The Sim replay pin: a recording whose log carries the <c>RD</c> a live run issued rebuilds the item on a fresh
    /// engine, under the id the live run gave it. The id is <c>{ListId}-{SequenceNumber}</c> rather than a fresh GUID
    /// precisely so this holds; before the bodies crossed, the replay host refused the verb and rebuilt nothing.
    /// </summary>
    [Fact]
    public void Replay_RebuildsTheReleaseFromTheRecordedVerb()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        for (int i = 0; i < 5; i++)
        {
            engine.TickOneSecond();
        }

        Assert.True(Send(engine, host, CallsignA, "RD").Success);
        var live = Assert.Single(Channel(engine).Items);

        var recording = Recording(engine);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        replayed.Replay(recording, engine.Scenario!.ElapsedSeconds + 5);

        var item = Assert.Single(replayed.Scenario!.CoordinationChannels[ListId].Items);
        Assert.Equal(live.Id, item.Id);
        Assert.Equal(live.AircraftId, item.AircraftId);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
    }
}
