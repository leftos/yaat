using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The resolved ATC roster (<see cref="SimScenarioState.AtcPositions"/>) rides the snapshot and the recording. The
/// scenario JSON carries only unresolved <c>atc</c> entries — the server resolves them at load — so without a carrier
/// a Sim-side replay runs the deferred autotrack pass over an empty roster and a departure crossing the display floor
/// replays unowned. Real ZOA positions from the committed config snapshot.
/// </summary>
public class ReplayAtcRosterTests
{
    private const string OakTwrPositionId = "01GEAMB98RKCPP9HCNPW5AVDA5"; // OAK_TWR (3O)
    private const string OakDepPositionId = "01GEAS78D6ZW10PQJ90P44Q364"; // OAK_DEP (4R)
    private const string ZoaSnapshotPath = "TestData/artcc-zoa-snapshot.json";

    private const string Callsign = "UAL200";

    /// <summary>
    /// A B738 airborne over OAK at 60 ft MSL — below the 100 ft AGL acquisition floor — climbing on a preset, so it
    /// crosses the floor a few seconds in. OAK_DEP owns <c>KOAK</c> departures; the roster entry is unresolved in the
    /// JSON exactly as a real scenario's is.
    /// </summary>
    private static readonly string OakDepartureScenario = $$"""
        {
            "id": "roster-replay",
            "name": "Roster replay",
            "artccId": "ZOA",
            "primaryAirportId": "OAK",
            "studentPositionId": "{{OakTwrPositionId}}",
            "atc": [
                {
                    "id": "{{OakDepPositionId}}",
                    "artccId": "ZOA",
                    "positionId": "{{OakDepPositionId}}",
                    "autoTrackAirportIds": ["KOAK"]
                }
            ],
            "aircraft": [
                {
                    "id": "ac-{{Callsign}}",
                    "aircraftId": "{{Callsign}}",
                    "aircraftType": "B738",
                    "transponderMode": "C",
                    "startingConditions": {
                        "type": "Coordinates",
                        "coordinates": { "lat": 37.7213, "lon": -122.2208 },
                        "altitude": 60,
                        "heading": 280,
                        "speed": 160
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
                    "presetCommands": [{ "id": "p0", "command": "CM 050", "timeOffset": 0 }],
                    "spawnDelay": 0,
                    "airportId": "OAK",
                    "difficulty": "Easy"
                }
            ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ReplayAtcRosterTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>The bare engine over the parked-at-OAK fixture with the ZOA config attached, roster empty.</summary>
    private SimulationEngine? Engine()
    {
        return _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
    }

    private static ResolvedAtcPosition Resolve(ArtccConfigRoot config, string positionId, List<string> autoTrackAirportIds) =>
        new()
        {
            Source = new ScenarioAtc
            {
                Id = positionId,
                ArtccId = "ZOA",
                PositionId = positionId,
                AutoTrackAirportIds = autoTrackAirportIds,
            },
            Owner = config.ResolvePosition(positionId)!,
            Tcp = config.GetTcpForPosition(positionId),
        };

    [Fact]
    public void RosterRoundTripsASnapshot()
    {
        if (_zoa is null || Engine() is not { } engine)
        {
            return;
        }

        engine.Scenario!.AtcPositions.Add(Resolve(_zoa, OakDepPositionId, ["KOAK"]));
        engine.Scenario.AtcPositions.Add(Resolve(_zoa, OakTwrPositionId, []));

        var snapshot = engine.CaptureSnapshot(actionIndex: 0);

        if (Engine() is not { } restored)
        {
            return;
        }

        restored.RestoreFromSnapshot(snapshot);

        var roster = restored.Scenario!.AtcPositions;
        Assert.Equal(2, roster.Count);
        Assert.Equal([OakDepPositionId, OakTwrPositionId], roster.Select(p => p.Source.PositionId));
        Assert.Equal(["ZOA", "ZOA"], roster.Select(p => p.Source.ArtccId));
        Assert.Equal(["KOAK"], roster[0].Source.AutoTrackAirportIds);
        Assert.Empty(roster[1].Source.AutoTrackAirportIds);
        Assert.Equal(_zoa.ResolvePosition(OakDepPositionId), roster[0].Owner);
        Assert.Equal(_zoa.ResolvePosition(OakTwrPositionId), roster[1].Owner);
        Assert.Equal(_zoa.GetTcpForPosition(OakDepPositionId), roster[0].Tcp);
        Assert.Equal(_zoa.GetTcpForPosition(OakTwrPositionId), roster[1].Tcp);

        // The restored roster owns its own lists — a later .AUTOTRACK on the restored engine must not reach back into
        // the snapshot the caller may restore again.
        roster[0].Source.AutoTrackAirportIds.Add("KSFO");
        Assert.Equal(["KOAK"], snapshot.Scenario.AtcPositions![0].AutoTrackAirportIds);
    }

    /// <summary>
    /// A snapshot written before the roster was carried (its <c>AtcPositions</c> is absent) leaves the roster the
    /// loader resolved in place — unlike the other nullable server-side fields, whose other source is the snapshot
    /// alone.
    /// </summary>
    [Fact]
    public void ASnapshotWithoutTheRosterKeepsTheLoadersRoster()
    {
        if (_zoa is null || Engine() is not { } engine)
        {
            return;
        }

        engine.Scenario!.AtcPositions.Add(Resolve(_zoa, OakDepPositionId, ["KOAK"]));

        var node = JsonNode.Parse(JsonSerializer.Serialize(engine.CaptureSnapshot(actionIndex: 0), RecordingJsonOptions.Default))!;
        node["Scenario"]!.AsObject().Remove("AtcPositions");
        var legacy = JsonSerializer.Deserialize<StateSnapshotDto>(node.ToJsonString(), RecordingJsonOptions.Default)!;
        Assert.Null(legacy.Scenario.AtcPositions);

        if (Engine() is not { } restored)
        {
            return;
        }

        restored.Scenario!.AtcPositions.Add(Resolve(_zoa, OakTwrPositionId, ["KOAK"]));
        restored.RestoreFromSnapshot(legacy);

        var kept = Assert.Single(restored.Scenario.AtcPositions);
        Assert.Equal(OakTwrPositionId, kept.Source.PositionId);
    }

    /// <summary>
    /// A Sim-side replay — client playback, with no server to resolve the scenario's <c>atc</c> entries — auto-tracks
    /// the departure the moment it crosses the display floor, because the recording carries the resolved roster.
    /// Pinned against the legacy case: the same recording without the roster replays the departure unowned.
    /// </summary>
    [Fact]
    public void AReplayRestoresSnapshotZerosRosterAndAutoTracksTheFloorCrossing()
    {
        if (_zoa is null || !File.Exists(ZoaSnapshotPath))
        {
            return;
        }

        var owner = _zoa.ResolvePosition(OakDepPositionId)!;
        var recording = BuildRecording([
            new AtcPositionDto
            {
                Id = OakDepPositionId,
                ArtccId = "ZOA",
                FacilityId = "",
                PositionId = OakDepPositionId,
                AutoConnect = false,
                AutoTrackAirportIds = ["KOAK"],
                Owner = owner.ToSnapshot(),
                Tcp = _zoa.GetTcpForPosition(OakDepPositionId)?.ToSnapshot(),
            },
        ]);

        var engine = new SimulationEngine(new TestAirportGroundData());
        var lines = new List<string>();
        engine.TerminalEntryEmitted += entry => lines.Add(entry.Message);

        engine.Replay(recording, 0);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.True(FieldElevationResolver.IsBelowDisplayFloor(aircraft!, NavigationDatabase.Instance));
        Assert.Null(aircraft!.Track.Owner);

        int crossedAt = TickToFloorCrossing(engine, aircraft);
        Assert.True(crossedAt > 0, "the departure never climbed through the display floor");

        Assert.NotNull(aircraft.Track.Owner);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(owner));
        Assert.Contains(lines, line => line.Contains("[AutoTrack] On STARS", StringComparison.Ordinal));

        // Without the roster there is nothing to resolve the departure against — the legacy behavior, pinned.
        var legacyEngine = new SimulationEngine(new TestAirportGroundData());
        legacyEngine.Replay(BuildRecording(initialAtcPositions: null), crossedAt);
        var legacyAircraft = legacyEngine.FindAircraft(Callsign)!;
        Assert.False(FieldElevationResolver.IsBelowDisplayFloor(legacyAircraft, NavigationDatabase.Instance));
        Assert.Null(legacyAircraft.Track.Owner);
    }

    /// <summary>Steps the armed replay until the aircraft is above the display floor; returns that second, or 0.</summary>
    private static int TickToFloorCrossing(SimulationEngine engine, AircraftState aircraft)
    {
        for (int t = 1; t <= 30; t++)
        {
            engine.ReplayOneSecond();
            if (!FieldElevationResolver.IsBelowDisplayFloor(aircraft, NavigationDatabase.Instance))
            {
                return t;
            }
        }

        return 0;
    }

    private static SessionRecording BuildRecording(IReadOnlyList<AtcPositionDto>? initialAtcPositions) =>
        new()
        {
            Version = 2,
            ScenarioJson = OakDepartureScenario,
            RngSeed = 7,
            ArtccConfigJson = File.ReadAllText(ZoaSnapshotPath),
            Actions = [],
            TotalElapsedSeconds = 30,
            InitialAtcPositions = initialAtcPositions,
        };
}
