using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The active runways a loaded scenario implies: the ends its runway spawns and arrival generators can only be
/// working, else the primary airport's facility knowledge or the generic runway-in-use rule. Real scenarios and real
/// nav data throughout — the corpus lives in <c>docs/atctrainer-scenario-examples/</c>.
/// </summary>
public class ImpliedActiveRunwaysTests
{
    private const string S1Oak1 = "01H06NVK7VN8BS7MCDXHKJZ7MQ.json";
    private const string S1Oak234 = "01H08FSWF5NCDXTQMQ3BWB0BND.json";
    private const string S1SfoP = "01HCE8FW7P81CGQQFZP0HRM9F7.json";
    private const string S2Oak4 = "01HG3N8Q5PPR7QXZK33ZPC4D5M.json";
    private const string S3Fat7 = "01HM8ARK79GCPJZG7RW6A0EEKW.json";
    private const string C1Zoa5 = "01HR2JAY7SSS096ZZP0EZ2G91A.json";

    private static readonly string ExamplesDir = Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples");

    private static readonly DateTime ModelDate = MagneticDeclination.EvaluationDateUtc;

    public ImpliedActiveRunwaysTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void OnFinalSpawns_ImplyArrivalEnds_AndNothingElseIsAdded()
    {
        // Six OnFinal arrivals at OAK (28L, 28R and 30), eleven parking departures that imply nothing. OAK is the
        // primary and already implies ends, so the resolver must not widen it: spawns win whole.
        ScenarioLoadResult result = Load(S2Oak4);

        ActiveRunways active = ImpliedActiveRunways.For(result, weather: null, ModelDate);

        Assert.Equal(["A28L", "A28R", "A30"], Tokens(active, "OAK"));
        Assert.Equal(["OAK"], active.Airports);
    }

    [Fact]
    public void ArrivalGenerators_ImplyTheirRunwayForArrivals_AndAnUnknownRunwayImpliesNothing()
    {
        // Two generators (30 and 28R) over a field of parking departures: the generator runways are all that implies.
        ActiveRunways active = ImpliedActiveRunways.For(Load(S1Oak234), weather: null, ModelDate);

        Assert.Equal(["A28R", "A30"], Tokens(active, "OAK"));

        // A generator whose runway does not resolve implies nothing; the other one still carries the airport.
        ActiveRunways withUnknown = ImpliedActiveRunways.For(
            LoadEdited(S1Oak234, root => SetGeneratorRunway(root, 0, "99")),
            weather: null,
            ModelDate
        );

        Assert.Equal(["A28R"], Tokens(withUnknown, "OAK"));
    }

    [Fact]
    public void OnRunwaySpawns_ImplyDepartureEnds_AtEverySpawnAirport_DelayedOnesIncluded()
    {
        // S3-FAT-7 lines up departures at FAT, FCH, O32, VIS, TLR, D86 and PTV; most are delayed spawns, so the
        // delayed bucket has to be read too.
        ScenarioLoadResult result = Load(S3Fat7);

        ActiveRunways active = ImpliedActiveRunways.For(result, weather: null, ModelDate);

        Assert.Equal(["D29L", "D29R"], Tokens(active, "FAT"));
        Assert.Equal(["D12"], Tokens(active, "FCH"));
        Assert.Equal(["D16", "D34"], Tokens(active, "O32"));
        Assert.Equal(["D30"], Tokens(active, "VIS"));
        Assert.Equal(["D31"], Tokens(active, "TLR"));
        Assert.Equal(["D13"], Tokens(active, "D86"));
        Assert.Equal(["D12"], Tokens(active, "PTV"));
        Assert.Equal(["D86", "FAT", "FCH", "O32", "PTV", "TLR", "VIS"], active.Airports);
    }

    [Fact]
    public void AnEndImpliedByBothKinds_IsOneBothEntry()
    {
        // S2-OAK-4's arrivals use 28L, 28R and 30; add one OnRunway departure on 28R and 28R is used both ways.
        ScenarioLoadResult result = LoadEdited(S2Oak4, root => AddOnRunwaySpawn(root, "OAK", "28R"));

        ActiveRunways active = ImpliedActiveRunways.For(result, weather: null, ModelDate);

        Assert.Equal(["A28L", "28R", "A30"], Tokens(active, "OAK"));
        Assert.Equal(ActiveRunwayUse.Both, active.For("OAK").Single(r => r.Designator == "28R").Use);
    }

    [Fact]
    public void PrimaryWithNoImpliedEnds_TakesTheFacilityKnowledgeConfiguration()
    {
        // S1-OAK-1 is all parking departures, so nothing implies an end at OAK and the knowledge file decides.
        // 12 kt from 300 picks KOAK's west configuration (SFOW): the 28s and 30 both ways.
        WeatherProfile weather = new() { WindLayers = [new WindLayer { Direction = 300, Speed = 12 }] };

        ActiveRunways active = ImpliedActiveRunways.For(Load(S1Oak1), weather, ModelDate);

        Assert.Equal(["28L", "28R", "30"], Tokens(active, "OAK"));
        Assert.All(active.For("OAK"), runway => Assert.Equal(ActiveRunwayUse.Both, runway.Use));
        Assert.Equal(["OAK"], active.Airports);
    }

    [Fact]
    public void PrimaryWithoutFacilityKnowledge_TakesTheGenericRulesEndAsBoth()
    {
        // SFO has no facility knowledge file, so the generic rule decides. Drop its arrivals so nothing implies an
        // end there: 12 kt from 280 aligns with SFO's 28s, and the longest pavement (10L/28R) breaks the tie.
        ScenarioLoadResult result = LoadEdited(S1SfoP, root => DropSpawns(root, "OnFinal"));

        WeatherProfile weather = new() { WindLayers = [new WindLayer { Direction = 280, Speed = 12 }] };

        ActiveRunways active = ImpliedActiveRunways.For(result, weather, ModelDate);

        Assert.Equal(["28R"], Tokens(active, "SFO"));
        Assert.Equal(ActiveRunwayUse.Both, active.For("SFO").Single().Use);
    }

    [Fact]
    public void KnowledgeConfigurationWithNoUsableDepartureRunway_FallsBackToTheGenericRule()
    {
        // A variable 15 kt wind is a full tailwind on every end, so the gate refuses every departure runway of the
        // configuration KOAK's knowledge picks. The generic rule's calm end stands in instead — the longest
        // pavement (12/30), its end by designator — worked both ways.
        WeatherProfile weather = new()
        {
            WindLayers =
            [
                new WindLayer
                {
                    Direction = 300,
                    Speed = 10,
                    Gusts = 15,
                    Variable = true,
                },
            ],
        };

        ActiveRunways active = ImpliedActiveRunways.For(Load(S1Oak1), weather, ModelDate);

        Assert.Equal(["12"], Tokens(active, "OAK"));
        Assert.Equal(ActiveRunwayUse.Both, active.For("OAK").Single().Use);
        Assert.Equal(["OAK"], active.Airports);
    }

    [Fact]
    public void KnowledgeConfiguration_WhoseGatePrunesSomeDepartureEnds_KeepsThePrunedEndAsAnArrival()
    {
        // Wet, 6 kt from 070 is below the calm threshold, so KOAK's west configuration is picked (SFOW: 28L, 28R
        // and 30, both ways). The gate prunes the two 28s — a 5.2 kt tailwind over the 5 kt wet limit — but keeps
        // 30 at 4.1 kt, so 28L and 28R come back arrivals-only while 30 stays both ways.
        WeatherProfile weather = new() { Precipitation = "RA", WindLayers = [new WindLayer { Direction = 70, Speed = 6 }] };

        ActiveRunways active = ImpliedActiveRunways.For(Load(S1Oak1), weather, ModelDate);

        Assert.Equal(["A28L", "A28R", "30"], Tokens(active, "OAK"));
        Assert.Equal(ActiveRunwayUse.Arrival, active.For("OAK")[0].Use);
        Assert.Equal(ActiveRunwayUse.Arrival, active.For("OAK")[1].Use);
        Assert.Equal(ActiveRunwayUse.Both, active.For("OAK")[2].Use);
    }

    [Fact]
    public void DeferredAircraft_ImplyNothing()
    {
        // The loader builds a deferred aircraft without phases, but a hand-built one that does carry an OnFinal
        // phase must still be ignored: OAK 12 is an end nothing else in the scenario uses.
        ScenarioLoadResult loaded = Load(S2Oak4);
        LoadedAircraft moved = loaded.ImmediateAircraft[0];
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "12")!;
        moved.State.Phases = AircraftInitializer.InitializeOnFinal(runway, AircraftCategory.Jet, moved.State.Callsign).Phases;

        ScenarioLoadResult result = new()
        {
            PrimaryAirportId = loaded.PrimaryAirportId,
            ImmediateAircraft = [.. loaded.ImmediateAircraft.Skip(1)],
            DelayedAircraft = loaded.DelayedAircraft,
            DeferredAircraft = [new LoadedAircraft { State = moved.State, DeferralReason = "test" }],
        };

        ActiveRunways active = ImpliedActiveRunways.For(result, weather: null, ModelDate);

        Assert.Equal(["A28L", "A28R", "A30"], Tokens(active, "OAK"));
        Assert.Equal("12", result.DeferredAircraft[0].State.Phases!.AssignedRunway!.Designator);
    }

    [Fact]
    public void NoSignalsAndNoPrimary_OrNoRunwayData_IsEmpty()
    {
        // C1-ZOA-05 has no primary airport and no runway spawns, generators or presets that name one.
        ActiveRunways none = ImpliedActiveRunways.For(Load(C1Zoa5), weather: null, ModelDate);

        Assert.Equal([], none.Airports);
        Assert.Equal(ActiveRunways.Empty, none);

        // A primary the nav data has no runways for resolves to nothing either.
        ActiveRunways noRunways = ImpliedActiveRunways.For(LoadEdited(C1Zoa5, root => root["primaryAirportId"] = "ZZZZ"), weather: null, ModelDate);

        Assert.Equal([], noRunways.Airports);
    }

    [Fact]
    public void RoomDefault_KeepsASingleRunwayAirportImpliedOnOneEnd()
    {
        // VIS has one runway (12/30); naming 30 names the runway, so the guess stands and keeps its use.
        ActiveRunways implied = ActiveRunways.Empty.With("VIS", [new ActiveRunway("30", ActiveRunwayUse.Departure)]);

        ActiveRunways room = ImpliedActiveRunways.RoomDefault(implied);

        Assert.Equal(["VIS"], room.Airports);
        Assert.Equal(["D30"], Tokens(room, "VIS"));

        // The same holds for every airport S3-FAT-7's departures imply: each is a single runway, or (FAT) both of
        // its two parallel runways are named.
        ActiveRunways fromScenario = ImpliedActiveRunways.RoomDefault(ImpliedActiveRunways.For(Load(S3Fat7), weather: null, ModelDate));

        Assert.Equal(["D86", "FAT", "FCH", "O32", "PTV", "TLR", "VIS"], fromScenario.Airports);
    }

    [Fact]
    public void RoomDefault_KeepsBothEndsOfOneRunway()
    {
        // O32 has one runway, 16/34; naming both ends covers it.
        ActiveRunways implied = ActiveRunways.Empty.With(
            "O32",
            [new ActiveRunway("16", ActiveRunwayUse.Departure), new ActiveRunway("34", ActiveRunwayUse.Departure)]
        );

        ActiveRunways room = ImpliedActiveRunways.RoomDefault(implied);

        Assert.Equal(["D16", "D34"], Tokens(room, "O32"));
    }

    [Fact]
    public void RoomDefault_DropsAnAirportWithARunwayNoEndNames()
    {
        // OAK has four runways (12/30, 15/33, 10L/28R, 10R/28L); the arrivals' 28L, 28R and 30 leave 15/33 unnamed.
        ActiveRunways implied = ImpliedActiveRunways.For(Load(S2Oak4), weather: null, ModelDate);
        Assert.Equal(["A28L", "A28R", "A30"], Tokens(implied, "OAK"));

        ActiveRunways room = ImpliedActiveRunways.RoomDefault(implied);

        Assert.Equal([], room.Airports);
    }

    [Fact]
    public void RoomDefault_KeepsFAT_WhoseTwoParallelRunwaysAreBothNamed()
    {
        // FAT's only pavements are 11L/29R and 11R/29L, so naming the two 29s covers the field.
        ActiveRunways implied = ActiveRunways.Empty.With(
            "FAT",
            [new ActiveRunway("29L", ActiveRunwayUse.Departure), new ActiveRunway("29R", ActiveRunwayUse.Departure)]
        );

        ActiveRunways room = ImpliedActiveRunways.RoomDefault(implied);

        Assert.Equal(["D29L", "D29R"], Tokens(room, "FAT"));
    }

    [Fact]
    public void RoomDefault_DropsAnAirportWithNoRunwayData()
    {
        ActiveRunways implied = ActiveRunways.Empty.With("ZZZZ", [new ActiveRunway("12", ActiveRunwayUse.Both)]);

        Assert.Equal([], ImpliedActiveRunways.RoomDefault(implied).Airports);
    }

    [Fact]
    public void RoomDefault_OfEmpty_IsEmpty()
    {
        ActiveRunways room = ImpliedActiveRunways.RoomDefault(ActiveRunways.Empty);

        Assert.Equal(ActiveRunways.Empty, room);
        Assert.Equal([], room.Airports);
    }

    private static string[] Tokens(ActiveRunways active, string airport) => [.. active.For(airport).Select(runway => runway.ToToken())];

    private static ScenarioLoadResult Load(string fileName) => LoadEdited(fileName, _ => { });

    private static ScenarioLoadResult LoadEdited(string fileName, Action<JsonObject> mutate)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, fileName)))!;
        mutate(root.AsObject());

        return ScenarioLoader.Load(root.ToJsonString(), new NullGroundData(), new Random(7), ModelDate);
    }

    private static void SetGeneratorRunway(JsonObject root, int index, string runway) =>
        root["aircraftGenerators"]!.AsArray()[index]!["runway"] = runway;

    private static void DropSpawns(JsonObject root, string type)
    {
        JsonArray aircraft = root["aircraft"]!.AsArray();
        for (int i = aircraft.Count - 1; i >= 0; i--)
        {
            if (string.Equals((string?)aircraft[i]!["startingConditions"]!["type"], type, StringComparison.Ordinal))
            {
                aircraft.RemoveAt(i);
            }
        }
    }

    /// <summary>Adds a runway spawn modelled on an existing aircraft, so the loader sees a shape it knows.</summary>
    private static void AddOnRunwaySpawn(JsonObject root, string airportId, string runway)
    {
        JsonArray aircraft = root["aircraft"]!.AsArray();
        JsonNode spawn = JsonNode.Parse(aircraft[0]!.ToJsonString())!;
        spawn["aircraftId"] = "N461RW";
        spawn["airportId"] = airportId;
        spawn["spawnDelay"] = 0;
        spawn["startingConditions"] = new JsonObject { ["type"] = "OnRunway", ["runway"] = runway };
        aircraft.Add(spawn);
    }
}
