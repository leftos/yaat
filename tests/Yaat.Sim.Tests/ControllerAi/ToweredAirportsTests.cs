using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// The towered-airport set: built from every loaded ARTCC config (own, roster and geographic neighbours), so a tower in a
/// neighbouring ARTCC reads towered; an airport no config gives a tower reads untowered. A recording carries the set, and
/// one without it rebuilds the set from its one bundled config.
/// </summary>
public class ToweredAirportsTests
{
    private const string ScenarioAtOak = """
        {
          "id": "towered-airports",
          "name": "Towered airports",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": []
        }
        """;

    public ToweredAirportsTests() => TestVnasData.EnsureInitialized();

    private static ArtccConfigRoot Zoa() => TestArtccConfig.LoadZoa() ?? throw new InvalidOperationException("ZOA snapshot missing");

    private static ArtccConfigRoot Zla() => TestArtccConfig.LoadZla() ?? throw new InvalidOperationException("ZLA snapshot missing");

    [Fact]
    public void NeighbourTower_ReadsTowered_WhenTheNeighbourConfigIsLoaded()
    {
        var own = ToweredAirports.From([Zoa()]);
        var withNeighbour = ToweredAirports.From([Zoa(), Zla()]);

        Assert.False(own.IsTowered("KLAX"));
        Assert.True(withNeighbour.IsTowered("KLAX"));
        Assert.True(withNeighbour.IsTowered("LAX"));
        Assert.True(withNeighbour.IsTowered("KOAK"));
    }

    [Theory]
    [InlineData("O69")]
    [InlineData("KO69")]
    [InlineData("KZZZ")]
    public void FieldWithNoTowerInAnyConfig_ReadsUntowered(string airportId) =>
        Assert.False(ToweredAirports.From([Zoa(), Zla()]).IsTowered(airportId));

    [Fact]
    public void AgreesWithThePositionResolver_ForEveryOwnConfigTower()
    {
        ArtccConfigRoot zoa = Zoa();
        var set = ToweredAirports.From([zoa]);

        Assert.All(set.Ids, id => Assert.True(AiPositionResolver.IsTowered(zoa, id), id));
    }

    [Fact]
    public void Archive_RoundTripsTheTowerList()
    {
        var recording = new SessionRecording
        {
            ScenarioJson = "{}",
            RngSeed = 0,
            Actions = [],
            TotalElapsedSeconds = 0,
            ToweredAirportIds = ["LAX", "OAK"],
        };

        using var stream = new MemoryStream(RecordingArchiveWriter.WriteToBytes(recording));
        using var archive = RecordingArchive.Open(stream);

        Assert.True(archive.Manifest.HasToweredAirports);
        Assert.Equal(["LAX", "OAK"], archive.ReadToweredAirportIds());
    }

    [Fact]
    public void Replay_WithTheEntry_RestoresTheRecordedSet()
    {
        SimulationEngine engine = ReplayOf(["LAX"]);

        Assert.True(engine.Scenario!.ToweredAirports.IsTowered("KLAX"));
        Assert.False(engine.Scenario.ToweredAirports.IsTowered("KOAK"));
    }

    [Fact]
    public void Replay_WithoutTheEntry_RebuildsTheSetFromTheBundledConfig()
    {
        SimulationEngine engine = ReplayOf(null);

        Assert.True(engine.Scenario!.ToweredAirports.IsTowered("KOAK"));
        Assert.False(engine.Scenario.ToweredAirports.IsTowered("KLAX"));
    }

    /// <summary>
    /// A live room refuses a preset airport-level visual to a towered field (its towered set is in place before presets
    /// dispatch); the replay must refuse it too, so the recorded set is restored before the scenario's presets dispatch.
    /// </summary>
    [Fact]
    public void Replay_RefusesAPresetAirportLevelVisualAtATowerField_AsTheLiveRoomDid()
    {
        SimulationEngine engine = ReplayOf(ScenarioWithForcedVisualToOak, ["OAK"]);

        AircraftState aircraft = Assert.Single(engine.World.GetSnapshot());
        Assert.Null(aircraft.Phases?.ActiveApproach);
        Assert.Null(aircraft.Approach.Expected);
    }

    /// <summary>ICAO and FAA spellings of a non-CONUS tower match through the navigation data (ANC/PANC, HNL/PHNL).</summary>
    [Fact]
    public void NonConusTower_MatchesItsOtherSpelling()
    {
        var set = ToweredAirports.FromIds(["ANC", "PHNL"]);

        Assert.True(set.IsTowered("PANC"));
        Assert.True(set.IsTowered("ANC"));
        Assert.True(set.IsTowered("HNL"));
        Assert.False(ToweredAirports.FromIds(["ANC"]).IsTowered("PHNL"));
    }

    /// <summary>
    /// Live, the room's active runways are seeded before presets dispatch, so a preset airport-level visual to Petaluma
    /// (O69, untowered) lands on the active 11 even from a track of 290; the replay restores the recorded active runways
    /// there too, so it lands on 11 as well.
    /// </summary>
    [Fact]
    public void Replay_RestoresTheRecordedActiveRunwaysBeforePresets()
    {
        SimulationEngine engine = ReplayOf(ScenarioWithForcedVisualToPetaluma, ["OAK"], new() { ["O69"] = ["11"] });

        AircraftState aircraft = Assert.Single(engine.World.GetSnapshot());
        Assert.Equal("VIS11", aircraft.Phases?.ActiveApproach?.ApproachId);
    }

    private const string ScenarioWithForcedVisualToPetaluma = """
        {
          "id": "active-runway-cva-replay",
          "name": "Active runway CVA replay",
          "artccId": "ZOA",
          "primaryAirportId": "O69",
          "aircraft": [{
            "aircraftId": "N123",
            "aircraftType": "C172",
            "startingConditions": {
              "type": "Coordinates",
              "coordinates": { "lat": 38.17, "lon": -122.50 },
              "altitude": 3000,
              "speed": 110,
              "heading": 290
            },
            "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "O69", "cruiseAltitude": 5000, "cruiseSpeed": 110, "route": "" },
            "presetCommands": [ { "id": "p0", "command": "CVAF O69", "timeOffset": 0 } ]
          }]
        }
        """;

    private const string ScenarioWithForcedVisualToOak = """
        {
          "id": "towered-cva-replay",
          "name": "Towered CVA replay",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [{
            "aircraftId": "N123",
            "aircraftType": "B738",
            "startingConditions": {
              "type": "Coordinates",
              "coordinates": { "lat": 37.735, "lon": -122.12 },
              "altitude": 3000,
              "speed": 180,
              "heading": 295
            },
            "flightplan": { "rules": "IFR", "departure": "KLAX", "destination": "KOAK", "cruiseAltitude": 30000, "cruiseSpeed": 450, "route": "" },
            "presetCommands": [ { "id": "p0", "command": "CVAF", "timeOffset": 0 } ]
          }]
        }
        """;

    private static SimulationEngine ReplayOf(IReadOnlyList<string>? toweredIds) => ReplayOf(ScenarioAtOak, toweredIds);

    private static SimulationEngine ReplayOf(string scenarioJson, IReadOnlyList<string>? toweredIds) => ReplayOf(scenarioJson, toweredIds, null);

    private static SimulationEngine ReplayOf(
        string scenarioJson,
        IReadOnlyList<string>? toweredIds,
        Dictionary<string, List<string>?>? initialActiveRunways
    )
    {
        var recording = new SessionRecording
        {
            Version = 4,
            ScenarioJson = scenarioJson,
            RngSeed = 1,
            Actions = [],
            TotalElapsedSeconds = 0,
            ArtccConfigJson = System.Text.Json.JsonSerializer.Serialize(Zoa()),
            ToweredAirportIds = toweredIds,
            InitialActiveRunways = initialActiveRunways,
        };

        using var stream = new MemoryStream(RecordingArchiveWriter.WriteToBytes(recording));
        using var archive = RecordingArchive.Open(stream);
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(archive.ToBaseSessionRecording(), 0);
        return engine;
    }
}
