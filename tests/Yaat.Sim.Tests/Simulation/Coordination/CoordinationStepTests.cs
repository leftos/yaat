using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.ControllerAi;

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

        SimulationEngine engine = AiTestFixture.Load(TwoAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
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
        CommandResult sender = Issue(engine, host, SenderConnection, "", "AS 3O");
        Assert.True(sender.Success, sender.Message);
        CommandResult receiver = Issue(engine, host, ReceiverConnection, "", "AS 4U");
        Assert.True(receiver.Success, receiver.Message);
    }

    private static CommandResult Send(SimulationEngine engine, IActionHost host, string callsign, string command) =>
        Issue(engine, host, SenderConnection, callsign, command);

    private static CommandResult Receive(SimulationEngine engine, IActionHost host, string callsign, string command) =>
        Issue(engine, host, ReceiverConnection, callsign, command);

    /// <summary>A recording of everything <paramref name="engine"/> has run so far, carrying the ARTCC the replay re-initialises from.</summary>
    private SessionRecording Recording(SimulationEngine engine)
    {
        SimScenarioState scenario = engine.Scenario!;
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

        CommandResult first = Send(engine, host, CallsignA, "RD");

        Assert.True(first.Success, first.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal(CallsignA, item.AircraftId);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
        Assert.Equal("POAK-1", item.Id);
        Assert.Equal(1, item.SequenceNumber);

        CommandResult second = Send(engine, host, CallsignB, "RD");

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

        CommandResult ack = Receive(engine, host, CallsignA, "RDACK");

        Assert.True(ack.Success, ack.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items);
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
        // warning is a short "about to expire" caution — long enough for the RPO to act by hand, short enough that the
        // state still means the item is nearly gone.
        engine.Scenario!.ElapsedSeconds = SimScenarioState.CoordinationAckExpirySeconds - 30 - 1;
        engine.RunSecond(spine);

        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.DepartureExpirationWarning, item.Status);
        Assert.True(spine.CoordinationChangeCount > beforeWarning, "the spine's drain step should have handed the timer transition over");

        engine.Scenario!.ElapsedSeconds = SimScenarioState.CoordinationAckExpirySeconds - 1;
        engine.RunSecond(spine);

        CoordinationItem voided = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.VoidUnacknowledged, voided.Status);
        Assert.Null(voided.ExpireTime);
    }

    [Fact]
    public void Rdh_HoldsThenSends_AndRdrRecallsUntilTheTimersRevertItToUnsent()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);

        CommandResult held = Send(engine, spine, CallsignA, "RDH POAK EXPECT 28R");

        Assert.True(held.Success, held.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.Unsent, item.Status);
        Assert.Equal("EXPECT 28R", item.Message);
        int afterCreate = spine.CoordinationChangeCount;
        Assert.True(afterCreate > 0, "creating a held message should have reached the host");

        CommandResult sent = Send(engine, spine, CallsignA, "RDH POAK");

        Assert.True(sent.Success, sent.Message);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, Assert.Single(Channel(engine).Items).Status);
        int afterSend = spine.CoordinationChangeCount;
        Assert.True(afterSend > afterCreate, "sending the held message should have reached the host");

        CommandResult recalled = Send(engine, spine, CallsignA, "RDR");

        Assert.True(recalled.Success, recalled.Message);
        Assert.True(spine.CoordinationChangeCount > afterSend, "the recall should have reached the host");
        CoordinationItem lingering = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.Recalled, lingering.Status);
        Assert.Equal(engine.Scenario!.ElapsedSeconds + SimScenarioState.CoordinationRecallLingerSeconds, lingering.ExpireTime);

        // What a receiver's auto-acknowledge would have set on the release. It describes the send the recall just took
        // back, so the revert has to clear it: re-sending the reverted item and acknowledging it by hand reaches
        // Acknowledged, and CRC plays the release chime only for an acknowledged release that was not automatic.
        lingering.WasAutomaticRelease = true;

        int beforeLinger = spine.CoordinationChangeCount;
        for (int i = 0; i < (int)SimScenarioState.CoordinationRecallLingerSeconds; i++)
        {
            engine.RunSecond(spine);
        }

        // The linger expiring takes the receiver's copy away and hands the sender its text back as an Unsent item:
        // one shared item, drawn only at its origin TCP while Unsent, so reverting it is the whole of the split.
        CoordinationItem reverted = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.Unsent, reverted.Status);
        Assert.Null(reverted.ExpireTime);
        Assert.Equal("EXPECT 28R", reverted.Message);
        Assert.False(reverted.WasAutomaticRelease);
        Assert.True(spine.CoordinationChangeCount > beforeLinger, "the timers step's revert should have reached the host");
    }

    /// <summary>
    /// The reverted item is the sender's alone, and <c>RDR</c> on an Unsent item removes it outright — the path that
    /// clears the sender's copy once the linger has already taken the receiver's.
    /// </summary>
    [Fact]
    public void RdrOnTheRevertedUnsentItem_RemovesIt()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);

        Assert.True(Send(engine, spine, CallsignA, "RDH POAK EXPECT 28R").Success);
        Assert.True(Send(engine, spine, CallsignA, "RDH POAK").Success);
        Assert.True(Send(engine, spine, CallsignA, "RDR").Success);

        for (int i = 0; i < (int)SimScenarioState.CoordinationRecallLingerSeconds; i++)
        {
            engine.RunSecond(spine);
        }

        Assert.Equal(StarsCoordinationStatus.Unsent, Assert.Single(Channel(engine).Items).Status);
        int beforeDelete = spine.CoordinationChangeCount;

        CommandResult deleted = Send(engine, spine, CallsignA, "RDR");

        Assert.True(deleted.Success, deleted.Message);
        Assert.Empty(Channel(engine).Items);
        Assert.True(spine.CoordinationChangeCount > beforeDelete, "the delete should have reached the host");
    }

    /// <summary>
    /// The sender holds one item per aircraft per list, and the reverted item is still one: <c>RD</c> beside it
    /// refuses rather than stacking a duplicate. Re-sending it is <c>RDH</c>'s held branch and clearing it is
    /// <c>RDDEL</c>'s.
    /// </summary>
    [Fact]
    public void Rd_AfterTheRevertedUnsentItem_Refuses()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);

        Assert.True(Send(engine, spine, CallsignA, "RDH POAK EXPECT 28R").Success);
        Assert.True(Send(engine, spine, CallsignA, "RDH POAK").Success);
        Assert.True(Send(engine, spine, CallsignA, "RDR").Success);

        for (int i = 0; i < (int)SimScenarioState.CoordinationRecallLingerSeconds; i++)
        {
            engine.RunSecond(spine);
        }

        Assert.Equal(StarsCoordinationStatus.Unsent, Assert.Single(Channel(engine).Items).Status);

        CommandResult released = Send(engine, spine, CallsignA, "RD POAK");

        Assert.False(released.Success);
        Assert.Equal($"{CallsignA} already has a coordination item on list {ListId}", released.Message);
        Assert.Equal(StarsCoordinationStatus.Unsent, Assert.Single(Channel(engine).Items).Status);
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

        CommandResult recalled = Send(engine, host, CallsignA, "RDR");

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

        CommandResult deleted = Send(engine, host, CallsignA, "RDDEL");

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

        CommandResult moved = Send(engine, host, CallsignB, "RDPOS 1");

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

        CommandResult changed = Send(engine, host, CallsignA, "RDTXT NEW TEXT");

        Assert.True(changed.Success, changed.Message);
        Assert.Equal("NEW TEXT", Assert.Single(Channel(engine).Items).Message);
        Assert.True(host.CoordinationChanges > beforeEdit, "the text edit should have reached the host");

        Assert.True(Send(engine, host, CallsignA, "RDH POAK").Success);
        int beforeRefusal = host.CoordinationChanges;

        CommandResult refused = Send(engine, host, CallsignA, "RDTXT LATER TEXT");

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

        CommandResult toggled = Receive(engine, host, "", "RDAUTO POAK");

        Assert.True(toggled.Success, toggled.Message);
        Assert.True(Channel(engine).Receivers.Single(r => r.Tcp.ToString() == "4U").AutoAcknowledge);
        Assert.True(host.CoordinationChanges > beforeToggle, "the auto-acknowledge toggle should have reached the host");

        Assert.True(Send(engine, host, CallsignA, "RD").Success);

        CoordinationItem item = Assert.Single(Channel(engine).Items);
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

        CommandResult tracked = Receive(engine, host, CallsignA, "TRACK");

        Assert.True(tracked.Success, tracked.Message);
        CoordinationItem remaining = Assert.Single(Channel(engine).Items);
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

        CommandResult inferred = Receive(engine, host, CallsignA, "RD");

        Assert.False(inferred.Success);
        Assert.Contains("not a sender", inferred.Message);

        CommandResult named = Receive(engine, host, CallsignA, "RD POAK");

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
        CoordinationItem live = Assert.Single(Channel(engine).Items);

        SessionRecording recording = Recording(engine);

        var replayed = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        replayed.Replay(recording, engine.Scenario!.ElapsedSeconds + 5);

        CoordinationItem item = Assert.Single(replayed.Scenario!.CoordinationChannels[ListId].Items);
        Assert.Equal(live.Id, item.Id);
        Assert.Equal(live.AircraftId, item.AircraftId);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
    }

    /// <summary>The connection a third position acts on: the parent a receiver consolidates into, or an unrelated one.</summary>
    private const string ThirdConnection = "conn-3";

    /// <summary>
    /// A receiver's list follows its airspace: with only 2B worked, 4U (a <c>POAK</c> receiver, NCT's child of 2B)
    /// consolidates into 2B, so 2B acknowledges the release though 2B is not on the list itself.
    /// </summary>
    [Fact]
    public void RdAck_FromAPositionTheReceiverIsConsolidatedInto_Acknowledges()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Issue(engine, host, ThirdConnection, "", "AS 2B").Success);
        AttendanceTestSupport.Attend(engine, "2B");
        Assert.True(Send(engine, host, CallsignA, "RD").Success);

        CommandResult ack = Issue(engine, host, ThirdConnection, CallsignA, "RDACK POAK");

        Assert.True(ack.Success, ack.Message);
        Assert.Equal(StarsCoordinationStatus.Acknowledged, Assert.Single(Channel(engine).Items).Status);
    }

    /// <summary>1J neither receives on <c>POAK</c> nor holds any of its receivers' airspace, so its acknowledge is refused.</summary>
    [Fact]
    public void RdAck_FromAnUnrelatedPosition_IsRefused()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Issue(engine, host, ThirdConnection, "", "AS 1J").Success);
        AttendanceTestSupport.Attend(engine, "1J");
        Assert.True(Send(engine, host, CallsignA, "RD").Success);

        CommandResult ack = Issue(engine, host, ThirdConnection, CallsignA, "RDACK POAK");

        Assert.False(ack.Success);
        Assert.Contains("not a receiver on list POAK", ack.Message);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, Assert.Single(Channel(engine).Items).Status);
    }

    /// <summary>
    /// One item per aircraft per list holds through a recall: an <c>RD</c> inside the recall linger sends the recalled
    /// item again under its own id, as a fresh release would, and the linger no longer reverts it afterwards.
    /// </summary>
    [Fact]
    public void Rd_DuringRecallLinger_RevivesTheSameItem()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);
        Assert.True(Send(engine, spine, CallsignA, "RD").Success);
        Assert.True(Send(engine, spine, CallsignA, "RDR").Success);
        int beforeRevive = spine.CoordinationChangeCount;

        CommandResult released = Send(engine, spine, CallsignA, "RD");

        Assert.True(released.Success, released.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal("POAK-1", item.Id);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
        Assert.Null(item.ExpireTime);
        Assert.False(item.WasAutomaticRelease);
        Assert.True(spine.CoordinationChangeCount > beforeRevive, "the revive should have reached the host");

        for (int i = 0; i < (int)SimScenarioState.CoordinationRecallLingerSeconds + 1; i++)
        {
            engine.RunSecond(spine);
        }

        CoordinationItem after = Assert.Single(Channel(engine).Items);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, after.Status);
    }

    /// <summary>An <c>RDH</c> inside the recall linger holds the recalled item again, as a fresh hold would, keeping its id and text.</summary>
    [Fact]
    public void Rdh_DuringRecallLinger_RevivesTheSameItem()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);
        Assert.True(Send(engine, spine, CallsignA, "RDH POAK EXPECT 28R").Success);
        Assert.True(Send(engine, spine, CallsignA, "RDH POAK").Success);
        Assert.True(Send(engine, spine, CallsignA, "RDR").Success);

        CommandResult held = Send(engine, spine, CallsignA, "RDH POAK");

        Assert.True(held.Success, held.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal("POAK-1", item.Id);
        Assert.Equal(StarsCoordinationStatus.Unsent, item.Status);
        Assert.Null(item.ExpireTime);
        Assert.Equal("EXPECT 28R", item.Message);

        for (int i = 0; i < (int)SimScenarioState.CoordinationRecallLingerSeconds + 1; i++)
        {
            engine.RunSecond(spine);
        }

        Assert.Equal(StarsCoordinationStatus.Unsent, Assert.Single(Channel(engine).Items).Status);
    }

    /// <summary>The 4U roster entry that auto-tracks OAK departures, so a departure crossing the display floor is acquired.</summary>
    private static void AddOakAutoTracker(SimulationEngine engine)
    {
        SimScenarioState scenario = engine.Scenario!;
        scenario.AtcPositions.Add(
            new ResolvedAtcPosition
            {
                Source = new ScenarioAtc { PositionId = "POS-4U", AutoTrackAirportIds = ["OAK"] },
                Owner = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U"),
                Tcp = TrackResolver.FindTcpByCode(scenario, "4U")!,
            }
        );
    }

    /// <summary>
    /// STARS acquiring the departure ends its release rundown whichever path acquires it: here the deferred autotrack,
    /// as the departure climbs through the display floor. The other aircraft, still on the ground, keeps its item.
    /// </summary>
    [Fact]
    public void AutoTrackAcquisition_RemovesTheDeparturesCoordinationItems()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AddOakAutoTracker(engine);
        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);
        Assert.True(Send(engine, spine, CallsignA, "RD").Success);
        Assert.True(Send(engine, spine, CallsignB, "RD").Success);
        Assert.True(Receive(engine, spine, CallsignA, "RDACK").Success);
        int before = spine.CoordinationChangeCount;

        AircraftState departure = engine.FindAircraft(CallsignA)!;
        departure.IsOnGround = false;
        departure.Altitude = 3000;
        engine.RunSecond(spine);

        Assert.NotNull(departure.Track.Owner);
        Assert.Equal(CallsignB, Assert.Single(Channel(engine).Items).AircraftId);
        Assert.True(spine.CoordinationChangeCount > before, "the acquisition removal should have reached the host");
    }

    /// <summary>The same removal on the claim a <c>.AUTOTRACK</c> change makes of an airborne untracked departure.</summary>
    [Fact]
    public void AutoTrackClaim_RemovesTheDeparturesCoordinationItems()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD").Success);
        Assert.True(Send(engine, host, CallsignB, "RD").Success);
        Assert.True(Receive(engine, host, CallsignA, "RDACK").Success);
        AircraftState departure = engine.FindAircraft(CallsignA)!;
        departure.IsOnGround = false;
        departure.Altitude = 3000;
        int before = host.CoordinationChanges;

        CommandResult claimed = engine.Actions.IssueDerived(
            new RecordedAutoTrackChange(engine.Scenario!.ElapsedSeconds, OakTwrPositionId, ["KOAK"]),
            host
        );

        Assert.True(claimed.Success, claimed.Message);
        Assert.NotNull(departure.Track.Owner);
        Assert.Equal(CallsignB, Assert.Single(Channel(engine).Items).AircraftId);
        Assert.True(host.CoordinationChanges > before, "the claim removal should have reached the host");
    }

    /// <summary>
    /// The <c>.AUTOTRACK</c> claim of an airborne departure STARS does not yet display keeps its acknowledged release
    /// item; once it climbs above the display floor the item is removed.
    /// </summary>
    [Fact]
    public void AutoTrackClaimBelowTheFloor_RemovesTheItemOnceAboveTheFloor()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);
        Assert.True(Send(engine, spine, CallsignA, "RD").Success);
        Assert.True(Send(engine, spine, CallsignB, "RD").Success);
        Assert.True(Receive(engine, spine, CallsignA, "RDACK").Success);
        AircraftState departure = engine.FindAircraft(CallsignA)!;
        departure.IsOnGround = false;
        departure.Altitude = 50;

        CommandResult claimed = engine.Actions.IssueDerived(
            new RecordedAutoTrackChange(engine.Scenario!.ElapsedSeconds, OakTwrPositionId, ["KOAK"]),
            spine
        );

        Assert.True(claimed.Success, claimed.Message);
        Assert.NotNull(departure.Track.Owner);
        Assert.Equal([CallsignA, CallsignB], Channel(engine).Items.Select(i => i.AircraftId));
        Assert.True(departure.Track.RemoveCoordinationAtFloor);

        departure.Altitude = 3000;
        engine.RunSecond(spine);

        Assert.Equal([CallsignB], Channel(engine).Items.Select(i => i.AircraftId));
        Assert.False(departure.Track.RemoveCoordinationAtFloor);
    }

    /// <summary>A handoff accept moves a track between two positions; STARS already had it, so nothing is acquired or removed.</summary>
    [Fact]
    public void HandoffAccept_DoesNotRemoveItems()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD").Success);
        Assert.True(engine.PositionSelections.TryGet(ReceiverConnection, out TrackOwner? receiver));
        AircraftState aircraft = engine.FindAircraft(CallsignA)!;
        aircraft.Track.Owner = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
        aircraft.Track.HandoffPeer = receiver;
        aircraft.Track.HandoffInitiatedAt = engine.Scenario!.ElapsedSeconds;

        CommandResult accepted = Receive(engine, host, CallsignA, "ACCEPT");

        Assert.True(accepted.Success, accepted.Message);
        Assert.Equal(CallsignA, Assert.Single(Channel(engine).Items).AircraftId);
    }

    /// <summary>
    /// The flight-plan-creator autotrack acquires a departure squawking at the gate before STARS displays it, which is
    /// not the radar acquisition that ends its release rundown; one acquired above the display floor is.
    /// </summary>
    [Fact]
    public void FlightPlanCreatorAcquisition_KeepsTheItemBelowTheFloor_AndRemovesItAbove()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);
        Assert.True(Send(engine, spine, CallsignA, "RD").Success);
        Assert.True(Send(engine, spine, CallsignB, "RD").Success);
        var creator = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
        AircraftState atGate = engine.FindAircraft(CallsignA)!;
        AircraftState airborne = engine.FindAircraft(CallsignB)!;
        airborne.IsOnGround = false;
        airborne.Altitude = 3000;
        AircraftState[] acquired = [atGate, airborne];
        foreach (AircraftState ac in acquired)
        {
            ac.FlightPlan.CreatedByOwner = creator;
            ac.Transponder.AssignedCode = 1234;
            ac.Transponder.Code = 1234;
        }

        engine.RunSecond(spine);

        Assert.NotNull(atGate.Track.Owner);
        Assert.NotNull(airborne.Track.Owner);
        Assert.Equal(CallsignA, Assert.Single(Channel(engine).Items).AircraftId);
    }

    /// <summary>
    /// A departure the flight-plan creator acquires at the gate keeps its acknowledged release item while STARS does
    /// not yet display it; once it climbs above the display floor the item is removed.
    /// </summary>
    [Fact]
    public void FlightPlanCreatorAcquisitionAtTheGate_RemovesTheItemOnceAboveTheFloor()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        SelectPositions(engine, spine);
        Assert.True(Send(engine, spine, CallsignA, "RD").Success);
        Assert.True(Send(engine, spine, CallsignB, "RD").Success);
        Assert.True(Receive(engine, spine, CallsignA, "RDACK").Success);
        AircraftState atGate = engine.FindAircraft(CallsignA)!;
        atGate.FlightPlan.CreatedByOwner = TrackOwner.CreateStars("NCT_4Q", "NCT", 4, "Q");
        atGate.Transponder.AssignedCode = 1234;
        atGate.Transponder.Code = 1234;

        engine.RunSecond(spine);

        Assert.NotNull(atGate.Track.Owner);
        Assert.Equal([CallsignA, CallsignB], Channel(engine).Items.Select(i => i.AircraftId));
        Assert.True(atGate.Track.RemoveCoordinationAtFloor);
        Assert.True(AircraftTrack.FromSnapshot(atGate.Track.ToSnapshot()).RemoveCoordinationAtFloor);

        atGate.IsOnGround = false;
        atGate.Altitude = 3000;
        engine.RunSecond(spine);

        Assert.Equal([CallsignB], Channel(engine).Items.Select(i => i.AircraftId));
        Assert.False(atGate.Track.RemoveCoordinationAtFloor);
    }

    /// <summary>
    /// A recalled item is its own sender's to send again: another sender on the list, during the linger, is refused
    /// on both <c>RD</c> and <c>RDH</c>, and the item stays recalled.
    /// </summary>
    [Fact]
    public void RdAndRdh_ByAnotherSenderDuringRecallLinger_AreRefused()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Channel(engine).SendingTcps.Add(TrackResolver.FindTcpByCode(engine.Scenario!, "1J")!);
        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Issue(engine, host, ThirdConnection, "", "AS 1J").Success);
        Assert.True(Send(engine, host, CallsignA, "RD POAK").Success);
        Assert.True(Send(engine, host, CallsignA, "RDR POAK").Success);

        CommandResult released = Issue(engine, host, ThirdConnection, CallsignA, "RD POAK");
        CommandResult held = Issue(engine, host, ThirdConnection, CallsignA, "RDH POAK");

        Assert.False(released.Success);
        Assert.Equal($"{CallsignA} is recalled by 3O", released.Message);
        Assert.False(held.Success);
        Assert.Equal($"{CallsignA} is recalled by 3O", held.Message);
        Assert.Equal(StarsCoordinationStatus.Recalled, Assert.Single(Channel(engine).Items).Status);
    }

    /// <summary>
    /// A named list goes through the same sender check as an inferred one: a receiver-only position naming the list on
    /// <c>RDH</c> is refused with the sender error, and the sender's held message stays held rather than being sent.
    /// </summary>
    [Fact]
    public void Rdh_NamedList_FromReceiverOnly_IsRefused()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RDH POAK EXPECT 28R").Success);

        CommandResult held = Receive(engine, host, CallsignA, "RDH POAK");

        Assert.False(held.Success);
        Assert.Equal($"TCP 4U is not a sender on list {ListId}", held.Message);
        Assert.Equal(StarsCoordinationStatus.Unsent, Assert.Single(Channel(engine).Items).Status);
    }

    /// <summary>
    /// A receiver-only position naming the list on <c>RDR</c> is refused with the sender error, and the sent item is
    /// not recalled.
    /// </summary>
    [Fact]
    public void Rdr_NamedList_FromReceiverOnly_IsRefused()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD POAK").Success);

        CommandResult recalled = Receive(engine, host, CallsignA, "RDR POAK");

        Assert.False(recalled.Success);
        Assert.Equal($"TCP 4U is not a sender on list {ListId}", recalled.Message);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, Assert.Single(Channel(engine).Items).Status);
    }

    /// <summary>
    /// RDH with text on an aircraft whose item is still Unsent is refused, as the STARS entries make "/ ## TEXT" only
    /// create a held message: the text is changed by its own entry (RDTXT) and the item sent with plain RDH.
    /// </summary>
    [Fact]
    public void Rdh_WithText_OnUnsentItem_IsRefusedAndKeepsTheItem()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RDH POAK EXPECT 28R").Success);
        int beforeRefusal = host.CoordinationChanges;

        CommandResult refused = Send(engine, host, CallsignA, "RDH POAK NEW TEXT");

        Assert.False(refused.Success);
        Assert.Equal($"{CallsignA} already has an unsent item on list {ListId}: change its text with RDTXT, then send it with RDH", refused.Message);
        Assert.Equal(beforeRefusal, host.CoordinationChanges);
        CoordinationItem item = Assert.Single(Channel(engine).Items, i => i.AircraftId == CallsignA);
        Assert.Equal(StarsCoordinationStatus.Unsent, item.Status);
        Assert.Equal("EXPECT 28R", item.Message);
    }

    /// <summary>Plain RDH on an aircraft whose item is still Unsent sends it, keeping its text.</summary>
    [Fact]
    public void Rdh_Plain_OnUnsentItem_StillSends()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RDH POAK EXPECT 28R").Success);

        CommandResult sent = Send(engine, host, CallsignA, "RDH POAK");

        Assert.True(sent.Success, sent.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items, i => i.AircraftId == CallsignA);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
        Assert.Equal("EXPECT 28R", item.Message);
    }

    /// <summary>
    /// One item per aircraft per list holds for <c>RDH</c> as for <c>RD</c>: a hold on an aircraft whose item is already
    /// sent and unacknowledged is refused with <c>RD</c>'s message and stacks no second, Unsent item beside it.
    /// </summary>
    [Fact]
    public void Rdh_OnUnacknowledgedItem_RefusesAndKeepsOneItem()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD POAK").Success);

        CommandResult held = Send(engine, host, CallsignA, "RDH POAK");

        Assert.False(held.Success);
        Assert.Equal($"{CallsignA} already has a coordination item on list {ListId}", held.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items, i => i.AircraftId == CallsignA);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
    }

    /// <summary>The same refusal on an item the receiver has already acknowledged.</summary>
    [Fact]
    public void Rdh_OnAcknowledgedItem_RefusesAndKeepsOneItem()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD POAK").Success);
        CommandResult ack = Receive(engine, host, CallsignA, "RDACK");
        Assert.True(ack.Success, ack.Message);

        CommandResult held = Send(engine, host, CallsignA, "RDH POAK");

        Assert.False(held.Success);
        Assert.Equal($"{CallsignA} already has a coordination item on list {ListId}", held.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items, i => i.AircraftId == CallsignA);
        Assert.Equal(StarsCoordinationStatus.Acknowledged, item.Status);
    }

    /// <summary>
    /// The refusal covers every status RD treats as sent, the timer states of a sent item included: an item voided
    /// unacknowledged still refuses a hold and stays the one item.
    /// </summary>
    [Fact]
    public void Rdh_OnVoidUnacknowledgedItem_RefusesAndKeepsOneItem()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        SelectPositions(engine, host);
        Assert.True(Send(engine, host, CallsignA, "RD POAK").Success);
        Assert.Single(Channel(engine).Items).Status = StarsCoordinationStatus.VoidUnacknowledged;

        CommandResult held = Send(engine, host, CallsignA, "RDH POAK");

        Assert.False(held.Success);
        Assert.Equal($"{CallsignA} already has a coordination item on list {ListId}", held.Message);
        CoordinationItem item = Assert.Single(Channel(engine).Items, i => i.AircraftId == CallsignA);
        Assert.Equal(StarsCoordinationStatus.VoidUnacknowledged, item.Status);
    }
}
