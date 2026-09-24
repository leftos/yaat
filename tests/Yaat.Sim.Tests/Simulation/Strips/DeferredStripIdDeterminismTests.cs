using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Strips;

/// <summary>
/// A preset or deferred creating strip verb fires from the aircraft's queue seconds after its record was written, so
/// it has no record to bake the id it mints onto. It derives the id from what every run kind shares instead — the
/// callsign, the second it fires and its place among that aircraft's dispatches in the second — so the live run, a
/// from-scratch reconstruction and a rewind over strips the rewind left in place all hold the item under one id.
/// </summary>
public class DeferredStripIdDeterminismTests
{
    private const string Callsign = "SWA1234";

    /// <summary>An IFR departure filed at KOAK, parked on the field, whose spawn prints the strip a <c>SCAN</c> copies.</summary>
    private const string DepartureAtOak = """
        {
          "id": "deferred-strip-id",
          "name": "Deferred strip id at OAK",
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

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public DeferredStripIdDeterminismTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The bare engine with an OAK tower student whose STARS TCP lives in NCT's configuration — which makes NCT's bay
    /// the external destination a <c>SCAN</c> needs — and the strip bays the creating verbs resolve against.
    /// </summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(DepartureAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");
        scenario.StudentPositionType = "TWR";
        engine.InitializeFromArtcc();
        engine.AfterAircraftSpawned(engine.FindAircraft(Callsign)!);
        return engine;
    }

    private static ActionOutcome Issue(SimulationEngine engine, IActionHost host, string command) =>
        engine.Actions.Issue(new ActionInput(Callsign, command, "conn-1", "XX", Baked: null), host);

    private SessionRecording Recording(SimulationEngine engine)
    {
        SimScenarioState scenario = engine.Scenario!;
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

    private static List<string> StripIds(SimulationEngine engine) => [.. engine.Strips.Items.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Each verb that mints an id at dispatch, fired twice in the same second from the queue. A <c>SCAN</c> copy never
    /// takes the canonical <c>STRIP_{callsign}</c> the spawn printed, so it always needs a suffix; the two dispatches
    /// sharing a second must still land as two items. The replay rebuilds the spawn strip at t=0 and re-fires both
    /// dispatches from the recorded <c>WAIT</c>s, and has to land on the live id set.
    /// </summary>
    [Theory]
    [InlineData("SCAN NCT/NCT")]
    [InlineData("HSC OAK/Ground 1/1 a")]
    [InlineData("SEP W OAK/Ground 1/1/1 Foo")]
    public void DeferredCreatingVerb_LiveAndFromScratchReplay_HoldTheSameIds(string verb)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        int spawned = engine.Strips.Items.Count;
        ActionOutcome first = Issue(engine, host, $"WAIT 1 {verb}");
        ActionOutcome second = Issue(engine, host, $"WAIT 1 {verb}");
        Assert.True(first.Result.Success, first.Result.Message);
        Assert.True(second.Result.Success, second.Result.Message);

        for (int t = 0; t < 3; t++)
        {
            engine.TickOneSecond();
        }

        List<string> liveIds = StripIds(engine);
        Assert.Equal(spawned + 2, liveIds.Count);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        replayed.Replay(Recording(engine), engine.Scenario!.ElapsedSeconds);

        Assert.Equal(liveIds, StripIds(replayed));
    }

    /// <summary>
    /// Ticks the engine until its strip store grows past <paramref name="before"/> items and returns the second that
    /// happened in; fails the test when nothing is created within a few seconds.
    /// </summary>
    private static long TickUntilCreated(SimulationEngine engine, int before)
    {
        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
            if (engine.Strips.Items.Count > before)
            {
                return (long)Math.Floor(engine.Scenario!.ElapsedSeconds);
            }
        }

        Assert.Fail("the deferred strip verb never fired");
        return -1;
    }

    /// <summary>
    /// A deferred creating verb fires, the same engine is rewound to a second before it fired, and the rewound session
    /// is replayed forward past it: the re-fired dispatch creates its item exactly once, under the id the live run held.
    /// </summary>
    [Theory]
    [InlineData("SCAN NCT/NCT")]
    [InlineData("HSC OAK/Ground 1/1 a")]
    [InlineData("SEP W OAK/Ground 1/1/1 Foo")]
    public void DeferredCreatingVerb_RewoundPastItsFireAndReplayed_HoldsTheLiveIdOnce(string verb)
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        for (int t = 0; t < 3; t++)
        {
            engine.TickOneSecond();
        }

        int spawned = engine.Strips.Items.Count;
        List<string> beforeFire = StripIds(engine);
        ActionOutcome issued = Issue(engine, host, $"WAIT 2 {verb}");
        Assert.True(issued.Result.Success, issued.Result.Message);

        long firedAt = TickUntilCreated(engine, spawned);
        for (int t = 0; t < 3; t++)
        {
            engine.TickOneSecond();
        }

        List<string> liveIds = StripIds(engine);
        string created = Assert.Single(liveIds, id => !beforeFire.Contains(id));
        SessionRecording recording = Recording(engine);
        int end = (int)engine.Scenario!.ElapsedSeconds;

        int cutoff = (int)firedAt - 1;
        engine.Replay(recording, cutoff);
        Assert.DoesNotContain(created, StripIds(engine));

        engine.FastForwardTo(end, recording.Actions);

        Assert.Equal(liveIds, StripIds(engine));
        Assert.Single(StripIds(engine), id => !beforeFire.Contains(id));
    }

    /// <summary>
    /// A deferred <c>SCAN</c> and <c>HSC</c> for one aircraft firing in the same second share that aircraft's
    /// per-second counter across prefixes: the copy takes sequence 0 and the half-strip sequence 1, so the two ids
    /// differ in more than the prefix, and a from-scratch replay lands on exactly those ids.
    /// </summary>
    [Fact]
    public void DeferredScanAndHalfStrip_SameAircraftSameSecond_TakeDistinctPinnedIdsLiveAndReplayed()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        int spawned = engine.Strips.Items.Count;
        List<string> beforeFire = StripIds(engine);
        Assert.True(Issue(engine, host, "WAIT 1 SCAN NCT/NCT").Result.Success);
        Assert.True(Issue(engine, host, "WAIT 1 HSC OAK/Ground 1/1 a").Result.Success);

        long firedAt = TickUntilCreated(engine, spawned);
        engine.TickOneSecond();

        List<string> created = [.. StripIds(engine).Where(id => !beforeFire.Contains(id))];
        List<string> expected = [$"HSTRIP_{Callsign}_{firedAt}_1", $"STRIP_{Callsign}_{firedAt}_0"];
        Assert.Equal(expected, created);

        var replayed = new SimulationEngine(new TestAirportGroundData());
        replayed.Replay(Recording(engine), engine.Scenario!.ElapsedSeconds);

        Assert.Equal(expected, [.. StripIds(replayed).Where(id => !beforeFire.Contains(id))]);
    }

    /// <summary>
    /// A rewind that re-fires the dispatch over strips it did not reset — the clock back at the second the dispatch
    /// fired, the dispatch queued again, the copy it printed still there — finds the copy under the id it derives and
    /// prints nothing, and says nothing: re-applying what is already held is not a failure.
    /// </summary>
    [Fact]
    public void DeferredScan_ReappliedOverStateHoldingItsId_PrintsNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        int spawned = engine.Strips.Items.Count;
        Assert.True(Issue(engine, host, "WAIT 1 SCAN NCT/NCT").Result.Success);

        double firedAt = -1;
        for (int t = 0; (t < 4) && (firedAt < 0); t++)
        {
            engine.TickOneSecond();
            if (engine.Strips.Items.Count > spawned)
            {
                firedAt = engine.Scenario!.ElapsedSeconds;
            }
        }

        Assert.True(firedAt >= 0, "the deferred SCAN never fired");
        List<string> liveIds = StripIds(engine);
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += terminal.Add;

        engine.Scenario!.ElapsedSeconds = firedAt;
        engine.FindAircraft(Callsign)!.PendingStripDispatches.Add(CommandParser.Parse("SCAN NCT/NCT").Value!);
        engine.TickStripDispatches();

        Assert.Equal(liveIds, StripIds(engine));
        Assert.DoesNotContain(terminal, e => string.Equals(e.Kind, "Warning", StringComparison.Ordinal));
    }
}
