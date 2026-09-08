using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Tdls;

/// <summary>
/// The vTDLS bodies on the bare engine: the spawn hook's auto-queue, the four tick steps, the command handler behind
/// <c>TDLSQ</c> / <c>TDLSS</c> / <c>TDLSW</c> / <c>TDLSDUMP</c> / <c>TDLSOPS</c>, and the change tracker the router
/// drains into the host. They decide from engine state alone — the scenario's clock, the ARTCC's TDLS configuration and
/// the world — so a plain <see cref="SimulationEngine.TickOneSecond"/> over the real OAK data reaches every gate the
/// live room reaches, with no server in the process.
/// </summary>
public class TdlsStepTests
{
    private const string Callsign = "SWA1234";
    private const string Facility = "OAK";

    /// <summary>The nine <c>TDLSS</c> fields in canonical order: Expect|Sid|Transition|Climbout|Climbvia|InitialAlt|ContactInfo|DepFreq|LocalInfo.</summary>
    private const string SendCanonical = "TDLSS 10 MIN|OAKLAND4|ALTAM||CLIMB VIA SID|5000||120.9|";

    /// <summary>An IFR departure filed at KOAK, parked on the field — what the TDLS auto-queue is for.</summary>
    private const string DepartureAtOak = """
        {
          "id": "tdls-departure",
          "name": "TDLS departure at OAK",
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
            }
          ]
        }
        """;

    private static readonly TrackOwner Student = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TdlsStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// A bare engine with the ZOA configuration resolved and the student position set by hand — the Sim's own scenario
    /// load resolves neither — then the strip/TDLS initialisation the server's load runs, which is what puts OAK's TDLS
    /// configuration on the engine.
    /// </summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(DepartureAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;
        engine.InitializeFromArtcc();
        Assert.True(engine.Tdls.Configs.ContainsKey(Facility), "OAK should carry a TDLS configuration from the ZOA tree");
        return engine;
    }

    private static AircraftState Departure(SimulationEngine engine) => engine.FindAircraft(Callsign)!;

    private static ActionOutcome Issue(SimulationEngine engine, AttendanceActionHost host, string callsign, string command) =>
        engine.Actions.Issue(new ActionInput(callsign, command, "conn-1", "XX", Baked: null), host);

    /// <summary>A recording of everything <paramref name="engine"/> has run so far, carrying the ARTCC the replay re-initialises from.</summary>
    private SessionRecording Recording(SimulationEngine engine)
    {
        var scenario = engine.Scenario!;
        return new SessionRecording
        {
            ScenarioJson = DepartureAtOak,
            RngSeed = 7,
            Actions = [.. scenario.ActionLog],
            TotalElapsedSeconds = scenario.ElapsedSeconds,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };
    }

    /// <summary>Queues the Pending PDC the way a spawn does, and drops the change the hook marked so a test asserts only its own.</summary>
    private static TdlsItemRecord Queued(SimulationEngine engine)
    {
        engine.AfterAircraftSpawned(Departure(engine));
        var item = Assert.Single(engine.Tdls.Items.Values);
        engine.Tdls.Changes.Clear();
        return item;
    }

    [Fact]
    public void AfterAircraftSpawned_QueuesAPendingPdcAndReportsItsId()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        engine.AfterAircraftSpawned(Departure(engine));

        var item = Assert.Single(engine.Tdls.Items.Values);
        Assert.Equal(Callsign, item.AircraftId);
        Assert.Equal(Facility, item.FacilityId);
        Assert.Equal(TdlsItemStatus.Pending, item.Status);
        Assert.Equal(engine.Scenario!.SimTimeUtc, item.CreatedUtc);
        Assert.Equal(engine.Scenario.SimTimeUtc + TdlsMutations.DefaultTtl, item.ExpiresUtc);

        // The consumer learns of the hook's item at the next drain — here the router's, after an idempotent re-queue.
        var outcome = Issue(engine, host, Callsign, "TDLSQ");

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var changes = Assert.Single(host.TdlsChanges);
        Assert.Equal(item.Id, Assert.Single(changes.ChangedItemIds));
        Assert.Empty(changes.Removed);
        Assert.False(changes.FullState);
    }

    [Fact]
    public void Tdlss_MarksSentSchedulesWilcoAndEmitsTheTerminalLine()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var queued = Queued(engine);
        engine.DrainTerminalEntries();

        var outcome = Issue(engine, host, Callsign, SendCanonical);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var item = Assert.Single(engine.Tdls.Items.Values);
        Assert.Equal(TdlsItemStatus.Sent, item.Status);
        Assert.Equal(engine.Scenario!.SimTimeUtc, item.SentUtc);
        Assert.Equal(engine.Scenario.SimTimeUtc + TdlsCommandHandler.DefaultWilcoDelay, engine.Tdls.ScheduledWilcoAt[item.Id]);

        var payload = item.SentPayload!;
        Assert.Equal("10 MIN", payload.Expect);
        Assert.Equal("OAKLAND4", payload.Sid);
        Assert.Equal("ALTAM", payload.Transition);
        Assert.Null(payload.Climbout);
        Assert.Equal("CLIMB VIA SID", payload.Climbvia);
        Assert.Equal("5000", payload.InitialAlt);
        Assert.Null(payload.ContactInfo);
        Assert.Equal("120.9", payload.DepFreq);
        Assert.Null(payload.LocalInfo);

        var line = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Tdls");
        Assert.Equal(Callsign, line.Callsign);
        Assert.Contains("ACARS: PDC", line.Message, StringComparison.Ordinal);

        var changes = Assert.Single(host.TdlsChanges);
        Assert.Equal(queued.Id, Assert.Single(changes.ChangedItemIds));
    }

    [Fact]
    public void Tdlss_ASecondSendIsRefusedWhileTheFirstStands()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Queued(engine);
        Assert.True(Issue(engine, host, Callsign, SendCanonical).Result.Success);

        var second = Issue(engine, host, Callsign, SendCanonical);

        Assert.False(second.Result.Success);
        Assert.Contains("already in status Sent", second.Result.Message);
    }

    [Fact]
    public void AutoWilco_FiresThreeSimSecondsAfterSend()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var queued = Queued(engine);
        Assert.True(Issue(engine, host, Callsign, SendCanonical).Result.Success);

        // Driven through the spine host, so the acknowledgement the tick produces has to reach the consumer through
        // the post-physics drain step rather than through the router.
        var spine = new SpineCapturingHost(engine);
        engine.RunSecond(spine);
        engine.RunSecond(spine);

        Assert.Equal(TdlsItemStatus.Sent, engine.Tdls.Items[queued.Id].Status);
        Assert.Empty(spine.TdlsChanges);

        engine.RunSecond(spine);

        var item = engine.Tdls.Items[queued.Id];
        Assert.Equal(TdlsItemStatus.Wilco, item.Status);
        Assert.Equal(engine.Scenario!.SimTimeUtc, item.WilcoUtc);
        Assert.False(engine.Tdls.ScheduledWilcoAt.ContainsKey(queued.Id));
        var changes = Assert.Single(spine.TdlsChanges);
        Assert.Equal(queued.Id, Assert.Single(changes.ChangedItemIds));
    }

    [Fact]
    public void Tdlsw_BeforeTheTimerIsIdempotent()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var queued = Queued(engine);
        Assert.True(Issue(engine, host, Callsign, SendCanonical).Result.Success);

        var wilco = Issue(engine, host, Callsign, "TDLSW");

        Assert.True(wilco.Result.Success, wilco.Result.Message);
        var acknowledged = engine.Tdls.Items[queued.Id];
        Assert.Equal(TdlsItemStatus.Wilco, acknowledged.Status);
        Assert.Equal(engine.Scenario!.SimTimeUtc, acknowledged.WilcoUtc);
        Assert.Empty(engine.Tdls.ScheduledWilcoAt);

        // The scheduler entry is gone, so the second the timer would have fired on changes nothing.
        engine.TickOneSecond();
        engine.TickOneSecond();
        engine.TickOneSecond();

        var afterTimer = engine.Tdls.Items[queued.Id];
        Assert.Equal(TdlsItemStatus.Wilco, afterTimer.Status);
        Assert.Equal(acknowledged.WilcoUtc, afterTimer.WilcoUtc);
    }

    [Fact]
    public void TdlsDump_RemovesWithLockout()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var queued = Queued(engine);

        var dump = Issue(engine, host, Callsign, "TDLSDUMP");

        Assert.True(dump.Result.Success, dump.Result.Message);
        Assert.Empty(engine.Tdls.Items);
        var removal = Assert.Single(Assert.Single(host.TdlsChanges).Removed);
        Assert.Equal(queued.Id, removal.ItemId);
        Assert.Equal(Facility, removal.FacilityId);
        Assert.Equal(Callsign, removal.Callsign);
        Assert.True(removal.Dumped);

        // The lockout is what keeps the auto-queue from re-creating what the controller removed.
        engine.AfterAircraftSpawned(Departure(engine));
        Assert.Empty(engine.Tdls.Items);
    }

    [Fact]
    public void TtlExpiry_RemovesWithoutLockout()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var queued = Queued(engine);
        var scenario = engine.Scenario!;
        var spine = new SpineCapturingHost(engine);

        // One second short of the two-hour TTL: the tick that crosses it is what expires the item.
        scenario.ElapsedSeconds = TdlsMutations.DefaultTtl.TotalSeconds - 1;
        engine.RunSecond(spine);

        Assert.False(engine.Tdls.Items.ContainsKey(queued.Id));
        Assert.False(engine.Tdls.ScheduledWilcoAt.ContainsKey(queued.Id));
        Assert.DoesNotContain(new DumpedKey(Facility, Callsign), engine.Tdls.Dumped);

        // The spine's drain step is what carries a tick's removal to the host.
        var removal = Assert.Single(Assert.Single(spine.TdlsChanges).Removed);
        Assert.Equal(queued.Id, removal.ItemId);
        Assert.False(removal.Dumped);

        // TTL expiry is not a dump, so the aircraft can be queued again.
        engine.AfterAircraftSpawned(Departure(engine));
        var requeued = Assert.Single(engine.Tdls.Items.Values);
        Assert.Equal(TdlsItemStatus.Pending, requeued.Status);
    }

    [Fact]
    public void TrackRemoval_RemovesOnceTheAircraftIsOwned()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var queued = Queued(engine);
        Departure(engine).Track.Owner = Student;
        var spine = new SpineCapturingHost(engine);

        engine.RunSecond(spine);

        Assert.False(engine.Tdls.Items.ContainsKey(queued.Id));
        // An automatic lifecycle removal, not a controller dump — no lockout.
        Assert.DoesNotContain(new DumpedKey(Facility, Callsign), engine.Tdls.Dumped);

        var removal = Assert.Single(Assert.Single(spine.TdlsChanges).Removed);
        Assert.Equal(queued.Id, removal.ItemId);
        Assert.Equal(Facility, removal.FacilityId);
        Assert.False(removal.Dumped);
    }

    [Fact]
    public void Tdlsops_SelectsTheConfigAndFlagsFullState()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var east = engine.Tdls.Configs[Facility].OpConfigs.First(c => string.Equals(c.Name, "OAKE", StringComparison.Ordinal));

        var outcome = Issue(engine, host, "", "TDLSOPS OAK OAKE");

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(east.Id, engine.Tdls.ActiveOpConfigIds[Facility]);
        Assert.Equal(east.Id, engine.Tdls.ResolveActiveOpConfigId(Facility));
        var changes = Assert.Single(host.TdlsChanges);
        Assert.True(changes.FullState);

        var unknown = Issue(engine, host, "", "TDLSOPS OAK NOSUCHCONFIG");

        Assert.False(unknown.Result.Success);
        Assert.Contains("(have: OAKW, OAKE, SFOE)", unknown.Result.Message);
        Assert.Equal(east.Id, engine.Tdls.ActiveOpConfigIds[Facility]);
    }

    /// <summary>
    /// The Sim replay pin: a recording whose log carries the <c>TDLSQ</c> and <c>TDLSS</c> a live run issued rebuilds the
    /// PDC on a fresh engine — the item is Sent, under the same id, with the clearance and the send instant the run had.
    /// Client playback is the caller this is for; before the bodies crossed, the replay host refused both verbs.
    /// </summary>
    [Fact]
    public void Replay_RebuildsTheSentPdcFromTheRecordedVerbs()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        engine.AfterAircraftSpawned(Departure(engine));
        Assert.True(Issue(engine, host, Callsign, "TDLSQ").Result.Success);
        engine.TickOneSecond();
        Assert.True(Issue(engine, host, Callsign, SendCanonical).Result.Success);

        var live = Assert.Single(engine.Tdls.Items.Values);
        Assert.Equal(TdlsItemStatus.Sent, live.Status);

        var recording = Recording(engine);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        replayed.Replay(recording, engine.Scenario!.ElapsedSeconds);

        var item = Assert.Single(replayed.Tdls.Items.Values);
        Assert.Equal(live.Id, item.Id);
        Assert.Equal(TdlsItemStatus.Sent, item.Status);
        Assert.Equal(live.SentUtc, item.SentUtc);
        Assert.Equal(live.SentPayload, item.SentPayload);
    }

    /// <summary>
    /// The load-time half of the same pin: a departure already in the world when the scenario loads carries no recorded
    /// <c>TDLSQ</c> — the live room's spawn hook queued its PDC — so the replay has to run the hook at elapsed 0 too.
    /// Left to the next second's catch-up sweep the item would exist but be stamped a second late.
    /// </summary>
    [Fact]
    public void Replay_QueuesALoadTimeDeparturesPdcAtElapsedZero()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var live = Queued(engine);
        engine.TickOneSecond();
        engine.TickOneSecond();

        var recording = Recording(engine);
        // The premise: nothing in the log queues this PDC. Only the spawn hook can.
        Assert.Empty(recording.Actions);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        replayed.Replay(recording, engine.Scenario!.ElapsedSeconds);

        var item = Assert.Single(replayed.Tdls.Items.Values);
        Assert.Equal(live.Id, item.Id);
        Assert.Equal(TdlsItemStatus.Pending, item.Status);
        Assert.Equal(recording.SessionStartUtc!.Value, item.CreatedUtc);
        Assert.Equal(live.CreatedUtc, item.CreatedUtc);
        Assert.Equal(live.ExpiresUtc, item.ExpiresUtc);
    }
}
