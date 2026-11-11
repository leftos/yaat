using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Tdls;

/// <summary>
/// What a sent PDC keeps beside the ids the canonical <c>TDLSS</c> carries: the SID and transition names resolved against
/// the ops config in force at send time, so the PDC text names the SID at a facility whose SIDs live only in ops configs
/// (OAK) and keeps naming it after the active config changes; and the <c>$FREQ</c> token a contact or local info value
/// carries, filled with the student position's primary frequency.
/// </summary>
public class TdlsClearanceNamesTests
{
    private const string Callsign = "SWA1234";
    private const string Facility = "OAK";

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

    /// <summary>OAK clearance delivery, a real ZOA position on 121.100 (NCT TCP 3O).</summary>
    private static readonly TrackOwner OakDelivery = TrackOwner.CreateStars("OAK_DEL", "NCT", 3, "O");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TdlsClearanceNamesTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A bare engine with the ZOA configuration resolved, the student set by hand and OAK's TDLS configuration loaded.</summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(DepartureAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "2B")!;
        engine.InitializeFromArtcc();
        Assert.True(engine.Tdls.Configs.ContainsKey(Facility), "OAK should carry a TDLS configuration from the ZOA tree");
        return engine;
    }

    private static AircraftState Departure(SimulationEngine engine) => engine.FindAircraft(Callsign)!;

    private static ActionOutcome Issue(SimulationEngine engine, AttendanceActionHost host, string callsign, string command) =>
        engine.Actions.Issue(new ActionInput(callsign, command, "conn-1", "XX", Baked: null), host);

    /// <summary>Queues the Pending PDC the way a spawn does.</summary>
    private static void Queue(SimulationEngine engine)
    {
        engine.AfterAircraftSpawned(Departure(engine));
        Assert.Single(engine.Tdls.Items.Values);
        engine.Tdls.Changes.Clear();
        engine.DrainTerminalEntries();
    }

    /// <summary>The first SID in the active ops config with a named transition, and that transition.</summary>
    private static (TdlsSidConfig Sid, TdlsSidTransitionConfig Transition) ActiveSidWithTransition(SimulationEngine engine)
    {
        TdlsSidConfig sid = engine
            .Tdls.ResolveSids(Facility)
            .First(s => !TdlsPlaceholder.IsPlaceholder(s.Name) && s.Transitions.Any(t => !TdlsPlaceholder.IsPlaceholder(t.Name)));
        return (sid, sid.Transitions.First(t => !TdlsPlaceholder.IsPlaceholder(t.Name)));
    }

    [Fact]
    public void Tdlss_AtAnOpsConfigFacility_StoresTheSidAndTransitionNamesAndThePdcNamesTheSid()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Assert.Empty(engine.Tdls.Configs[Facility].Sids);
        var host = new AttendanceActionHost();
        Queue(engine);
        (TdlsSidConfig sid, TdlsSidTransitionConfig transition) = ActiveSidWithTransition(engine);

        ActionOutcome outcome = Issue(engine, host, Callsign, $"TDLSS 10 MIN|{sid.Id}|{transition.Id}||CLIMB VIA SID|5000||120.9|");

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        TdlsClearance payload = Assert.Single(engine.Tdls.Items.Values).SentPayload!;
        Assert.Equal(sid.Id, payload.Sid);
        Assert.Equal(transition.Id, payload.Transition);
        Assert.Equal(sid.Name, payload.SidName);
        Assert.Equal(transition.Name, payload.TransitionName);

        TerminalEntry line = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Tdls");
        Assert.Contains($"REMARKS: CLEARED {sid.Name} DEPARTURE {transition.Name}", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPilotPdcMessage_AfterTheActiveOpsConfigChanges_StillNamesTheSidTheClearanceWasSentWith()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Queue(engine);
        (TdlsSidConfig sid, TdlsSidTransitionConfig transition) = ActiveSidWithTransition(engine);
        Assert.True(Issue(engine, host, Callsign, $"TDLSS 10 MIN|{sid.Id}|{transition.Id}||CLIMB VIA SID|5000||120.9|").Result.Success);
        TdlsOpConfig east = engine.Tdls.Configs[Facility].OpConfigs.First(c => string.Equals(c.Name, "OAKE", StringComparison.Ordinal));
        Assert.NotEqual(east.Id, engine.Tdls.ResolveActiveOpConfigId(Facility));

        Assert.True(Issue(engine, host, "", "TDLSOPS OAK OAKE").Result.Success);
        Assert.Equal(east.Id, engine.Tdls.ResolveActiveOpConfigId(Facility));

        TdlsClearance payload = Assert.Single(engine.Tdls.Items.Values).SentPayload!;
        string message = TdlsCommandHandler.FormatPilotPdcMessage(Callsign, Departure(engine), engine.Tdls.Configs[Facility], payload);
        Assert.Contains($"REMARKS: CLEARED {sid.Name} DEPARTURE {transition.Name}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASnapshotRoundTrip_KeepsTheSidAndTransitionNames_AndThePdcStillNamesTheSid()
    {
        if ((Engine() is not { } engine) || (Engine() is not { } restored))
        {
            return;
        }

        var host = new AttendanceActionHost();
        Queue(engine);
        (TdlsSidConfig sid, TdlsSidTransitionConfig transition) = ActiveSidWithTransition(engine);
        Assert.True(Issue(engine, host, Callsign, $"TDLSS 10 MIN|{sid.Id}|{transition.Id}||CLIMB VIA SID|5000||120.9|").Result.Success);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        StateSnapshotDto serialized = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;
        restored.RestoreFromSnapshot(serialized);

        TdlsClearance payload = Assert.Single(restored.Tdls.Items.Values).SentPayload!;
        Assert.Equal(sid.Name, payload.SidName);
        Assert.Equal(transition.Name, payload.TransitionName);
        string message = TdlsCommandHandler.FormatPilotPdcMessage(Callsign, Departure(restored), restored.Tdls.Configs[Facility], payload);
        Assert.Contains($"REMARKS: CLEARED {sid.Name} DEPARTURE {transition.Name}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tdlss_WithThePlaceholderTransition_StoresItsNameAndThePdcLeavesItOut()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        Queue(engine);
        TdlsSidConfig oak6 = Assert.Single(engine.Tdls.ResolveSids(Facility), s => string.Equals(s.Name, "OAK6", StringComparison.Ordinal));
        TdlsSidTransitionConfig none = Assert.Single(oak6.Transitions, t => string.Equals(t.Name, "- - - -", StringComparison.Ordinal));

        ActionOutcome outcome = Issue(engine, host, Callsign, $"TDLSS 10 MIN|{oak6.Id}|{none.Id}||CLIMB VIA SID|5000||120.9|");

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        TdlsClearance payload = Assert.Single(engine.Tdls.Items.Values).SentPayload!;
        Assert.Equal("OAK6", payload.SidName);
        Assert.Equal(none.Name, payload.TransitionName);
        TerminalEntry line = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Tdls");
        Assert.Contains("REMARKS: CLEARED OAK6 DEPARTURE CLIMB VIA SID", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tdlss_ContactInfoCarryingTheToken_StoresTheStudentsFrequency()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        engine.Scenario!.StudentPosition = OakDelivery;
        var host = new AttendanceActionHost();
        Queue(engine);

        ActionOutcome outcome = Issue(engine, host, Callsign, "TDLSS 10 MIN|OAKLAND4|ALTAM||CLIMB VIA SID|5000|CTC $FREQ TO PUSH|120.9|");

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        TdlsClearance payload = Assert.Single(engine.Tdls.Items.Values).SentPayload!;
        Assert.Equal("CTC 121.100 TO PUSH", payload.ContactInfo);
        TerminalEntry line = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Tdls");
        Assert.Contains("CTC 121.100 TO PUSH", line.Message, StringComparison.Ordinal);
    }
}
