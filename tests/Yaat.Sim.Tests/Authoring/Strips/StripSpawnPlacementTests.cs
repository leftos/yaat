using System.Text.Json;
using Xunit;
using Yaat.Sim.Authoring.Strips;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring.Strips;

/// <summary>The spawn bay placement edit over <see cref="Scenario.FlightStripConfigurations"/>.</summary>
public class StripSpawnPlacementTests
{
    public StripSpawnPlacementTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const string ScenarioJson = """
        {
          "id": "test",
          "name": "Test",
          "primaryAirportId": "OAK",
          "aircraft": [
            { "id": "ac1", "aircraftId": "N111", "aircraftType": "C172", "startingConditions": { "type": "Parking", "parking": "A1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KSQL", "cruiseAltitude": 4500 } },
            { "id": "ac2", "aircraftId": "N222", "aircraftType": "C172", "startingConditions": { "type": "Parking", "parking": "A2" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500 } },
            { "id": "arr", "aircraftId": "ASA451", "aircraftType": "B738", "startingConditions": { "type": "Parking", "parking": "A3" },
              "flightplan": { "rules": "IFR", "departure": "KSEA", "destination": "KOAK", "cruiseAltitude": 35000 } },
            { "id": "aarr", "aircraftId": "UAL777", "aircraftType": "B738",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.9, "lon": -122.4 }, "altitude": 8000, "speed": 250 },
              "flightplan": { "rules": "IFR", "departure": "KSEA", "destination": "KOAK", "cruiseAltitude": 35000 } }
          ]
        }
        """;

    private static Scenario NewScenario() => JsonSerializer.Deserialize<Scenario>(ScenarioJson)!;

    private static string IdFactory() => "CFG-NEW";

    [Fact]
    public void Set_CreatesAnEntryWithTheMintedId()
    {
        Scenario scenario = NewScenario();

        Assert.Null(StripSpawnPlacement.Set(scenario, "ac1", "OAK", "BAY-GND", 1, IdFactory));

        FlightStripConfiguration entry = Assert.Single(scenario.FlightStripConfigurations);
        Assert.Equal("CFG-NEW", entry.Id);
        Assert.Equal(("OAK", "BAY-GND", 1), (entry.FacilityId, entry.BayId, entry.Rack));
        Assert.Equal(["ac1"], entry.AircraftIds);
    }

    [Fact]
    public void Set_ReusesAnEqualFacilityBayRackEntryAndKeepsItsExtensionDataAndIds()
    {
        Scenario scenario = NewScenario();
        using var doc = JsonDocument.Parse("{\"v\":1}");
        var extension = new Dictionary<string, JsonElement> { ["x"] = doc.RootElement.Clone() };
        var existing = new FlightStripConfiguration
        {
            Id = "CFG",
            FacilityId = "OAK",
            BayId = "BAY-GND",
            Rack = 1,
            AircraftIds = ["ac2"],
            ExtensionData = extension,
        };
        scenario.FlightStripConfigurations.Add(existing);

        Assert.Null(StripSpawnPlacement.Set(scenario, "ac1", "OAK", "BAY-GND", 1, IdFactory));

        FlightStripConfiguration entry = Assert.Single(scenario.FlightStripConfigurations);
        Assert.Same(existing, entry);
        Assert.Equal(["ac2", "ac1"], entry.AircraftIds);
        Assert.Same(extension, entry.ExtensionData);
    }

    [Fact]
    public void Set_MovesTheAircraftOutOfOtherEntriesAndDropsAnEmptiedOne()
    {
        Scenario scenario = NewScenario();
        var shared = new FlightStripConfiguration
        {
            Id = "S",
            FacilityId = "OAK",
            BayId = "BAY-LCL",
            Rack = 0,
            AircraftIds = ["ac1", "ac2"],
        };
        var alone = new FlightStripConfiguration
        {
            Id = "A",
            FacilityId = "OAK",
            BayId = "BAY-LCL",
            Rack = 2,
            AircraftIds = ["ac1"],
        };
        scenario.FlightStripConfigurations.AddRange([shared, alone]);

        Assert.Null(StripSpawnPlacement.Set(scenario, "ac1", "OAK", "BAY-GND", 0, IdFactory));

        Assert.Equal(2, scenario.FlightStripConfigurations.Count);
        Assert.Same(shared, scenario.FlightStripConfigurations[0]);
        Assert.Equal(["ac2"], shared.AircraftIds);
        Assert.Equal(["ac1"], scenario.FlightStripConfigurations[1].AircraftIds);
        Assert.Equal("BAY-GND", scenario.FlightStripConfigurations[1].BayId);
    }

    [Fact]
    public void Set_PlacesAParkedAircraftFiledIntoTheAirport()
    {
        Scenario scenario = NewScenario();

        Assert.Null(StripSpawnPlacement.Set(scenario, "arr", "OAK", "BAY-GND", 0, IdFactory));

        FlightStripConfiguration entry = Assert.Single(scenario.FlightStripConfigurations);
        Assert.Equal(["arr"], entry.AircraftIds);
    }

    [Fact]
    public void Set_RefusesAnAirborneArrival()
    {
        Scenario scenario = NewScenario();

        string? reason = StripSpawnPlacement.Set(scenario, "aarr", "OAK", "BAY-GND", 0, IdFactory);

        Assert.NotNull(reason);
        Assert.Empty(scenario.FlightStripConfigurations);
    }

    [Fact]
    public void Set_RefusesAnUnknownAircraft()
    {
        Scenario scenario = NewScenario();

        Assert.NotNull(StripSpawnPlacement.Set(scenario, "nope", "OAK", "BAY-GND", 0, IdFactory));
        Assert.Empty(scenario.FlightStripConfigurations);
    }

    [Fact]
    public void Set_RefusesANegativeRack()
    {
        Scenario scenario = NewScenario();

        Assert.NotNull(StripSpawnPlacement.Set(scenario, "ac1", "OAK", "BAY-GND", -1, IdFactory));
        Assert.Empty(scenario.FlightStripConfigurations);
    }

    [Fact]
    public void Set_RefusesAnEmptyFacility()
    {
        Scenario scenario = NewScenario();

        Assert.NotNull(StripSpawnPlacement.Set(scenario, "ac1", "", "BAY-GND", 0, IdFactory));
        Assert.Empty(scenario.FlightStripConfigurations);
    }

    [Fact]
    public void Set_RefusesAnEmptyBay()
    {
        Scenario scenario = NewScenario();

        Assert.NotNull(StripSpawnPlacement.Set(scenario, "ac1", "OAK", "", 0, IdFactory));
        Assert.Empty(scenario.FlightStripConfigurations);
    }

    [Fact]
    public void Get_SkipsAnEntryWithAnEmptyBayThatSitsLast()
    {
        Scenario scenario = NewScenario();
        scenario.FlightStripConfigurations.AddRange([
            new FlightStripConfiguration
            {
                Id = "1",
                FacilityId = "OAK",
                BayId = "BAY-A",
                Rack = 0,
                AircraftIds = ["ac1"],
            },
            new FlightStripConfiguration
            {
                Id = "2",
                FacilityId = "OAK",
                BayId = "",
                Rack = 3,
                AircraftIds = ["ac1"],
            },
        ]);

        Assert.Equal(new ScenarioStripBayAssignment("OAK", "BAY-A", 0), StripSpawnPlacement.Get(scenario, "ac1"));
    }

    [Fact]
    public void Get_AgreesWithTheLoader()
    {
        Scenario scenario = NewScenario();
        scenario.FlightStripConfigurations.AddRange([
            new FlightStripConfiguration
            {
                Id = "1",
                FacilityId = "OAK",
                BayId = "BAY-A",
                Rack = 0,
                AircraftIds = ["ac1", "ac2"],
            },
            new FlightStripConfiguration
            {
                Id = "2",
                FacilityId = "OAK",
                BayId = "",
                Rack = 3,
                AircraftIds = ["ac1"],
            },
            new FlightStripConfiguration
            {
                Id = "3",
                FacilityId = "OAK",
                BayId = "BAY-B",
                Rack = 1,
                AircraftIds = ["ac1"],
            },
        ]);
        ScenarioLoadResult loaded = ScenarioLoader.Load(
            JsonSerializer.Serialize(scenario),
            groundData: null,
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        Assert.Equal(loaded.InitialStripBayByCallsign["N111"], StripSpawnPlacement.Get(scenario, "ac1"));
        Assert.Equal(loaded.InitialStripBayByCallsign["N222"], StripSpawnPlacement.Get(scenario, "ac2"));
        Assert.False(loaded.InitialStripBayByCallsign.ContainsKey("ASA451"));
        Assert.Null(StripSpawnPlacement.Get(scenario, "arr"));
    }

    [Fact]
    public void Clear_RemovesTheAircraftAndDropsAnEmptiedEntry()
    {
        Scenario scenario = NewScenario();
        scenario.FlightStripConfigurations.AddRange([
            new FlightStripConfiguration
            {
                Id = "1",
                BayId = "BAY-A",
                AircraftIds = ["ac1", "ac2"],
            },
            new FlightStripConfiguration
            {
                Id = "2",
                BayId = "BAY-B",
                AircraftIds = ["ac1"],
            },
        ]);

        Assert.True(StripSpawnPlacement.Clear(scenario, "ac1"));

        FlightStripConfiguration entry = Assert.Single(scenario.FlightStripConfigurations);
        Assert.Equal(["ac2"], entry.AircraftIds);
        Assert.Null(StripSpawnPlacement.Get(scenario, "ac1"));
        Assert.False(StripSpawnPlacement.Clear(scenario, "ac1"));
    }

    [Fact]
    public void Prune_RemovesADeletedAircraftsId()
    {
        Scenario scenario = NewScenario();
        scenario.FlightStripConfigurations.Add(
            new FlightStripConfiguration
            {
                Id = "1",
                BayId = "BAY-A",
                AircraftIds = ["gone", "ac2"],
            }
        );
        var untouched = new FlightStripConfiguration
        {
            Id = "2",
            BayId = "BAY-B",
            AircraftIds = [],
        };
        scenario.FlightStripConfigurations.Add(untouched);

        Assert.True(StripSpawnPlacement.Prune(scenario, "gone"));

        Assert.Equal(["ac2"], scenario.FlightStripConfigurations[0].AircraftIds);
        Assert.Same(untouched, scenario.FlightStripConfigurations[1]);
    }
}
