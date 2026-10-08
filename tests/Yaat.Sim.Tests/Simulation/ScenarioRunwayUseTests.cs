using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// How many of a loaded scenario's aircraft already have a runway, by airport: a runway spawn starting on the ground counts
/// as a departure, an airborne one as an arrival, at its runway's airport; delayed spawns count, aircraft with no runway
/// do not; the primary airport also lists the runways its arrival generators feed. Real scenarios and real nav data — the corpus lives in
/// <c>docs/atctrainer-scenario-examples/</c>.
/// </summary>
public class ScenarioRunwayUseTests
{
    private const string S1Oak1 = "01H06NVK7VN8BS7MCDXHKJZ7MQ.json";
    private const string S1Oak234 = "01H08FSWF5NCDXTQMQ3BWB0BND.json";
    private const string S2Oak4 = "01HG3N8Q5PPR7QXZK33ZPC4D5M.json";
    private const string S3Fat7 = "01HM8ARK79GCPJZG7RW6A0EEKW.json";

    private static readonly string ExamplesDir = Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples");

    private static readonly DateTime ModelDate = MagneticDeclination.EvaluationDateUtc;

    public ScenarioRunwayUseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void OnRunwaySpawns_CountAsDepartures_AtTheirRunwaysAirport_DelayedOnesIncluded()
    {
        // S3-FAT-7 lines up departures at seven airports, most of them delayed spawns; its airborne spawns (at FAT and
        // MAE among others) have no runway, so MAE is absent and FAT counts no arrival.
        IReadOnlyDictionary<string, ScenarioRunwayUse> counts = ScenarioRunwayUse.CountAssigned(Load(S3Fat7));

        Assert.Equal(
            [("D86", 1, 0, ""), ("FAT", 15, 0, ""), ("FCH", 2, 0, ""), ("O32", 2, 0, ""), ("PTV", 1, 0, ""), ("TLR", 1, 0, ""), ("VIS", 2, 0, "")],
            Sorted(counts)
        );
    }

    [Fact]
    public void OnFinalSpawns_CountAsArrivals_AndParkedAircraftCountNothing()
    {
        // S2-OAK-4: six OnFinal arrivals at OAK (two of them delayed), eleven parked departures and thirteen airborne
        // spawns away from final, none of which has a runway.
        IReadOnlyDictionary<string, ScenarioRunwayUse> counts = ScenarioRunwayUse.CountAssigned(Load(S2Oak4));

        Assert.Equal([("OAK", 0, 6, "")], Sorted(counts));
    }

    [Fact]
    public void ARunwaySpawnOnTheGround_AndOneOnFinal_CountAtTheSameAirport()
    {
        ScenarioLoadResult result = LoadEdited(S2Oak4, root => AddOnRunwaySpawn(root, "OAK", "28R"));

        IReadOnlyDictionary<string, ScenarioRunwayUse> counts = ScenarioRunwayUse.CountAssigned(result);

        Assert.Equal([("OAK", 1, 6, "")], Sorted(counts));
    }

    [Fact]
    public void NoAircraftWithARunway_AndNoGenerator_LeavesEveryAirportAbsent() =>
        // S1-OAK-1 is all parked departures and has no arrival generator.
        Assert.Empty(ScenarioRunwayUse.CountAssigned(Load(S1Oak1)));

    [Fact]
    public void ArrivalGenerators_ListTheirRunwaysAtThePrimary_WithNoAircraftCounted()
    {
        // S1-OAK-234: parked departures only, plus arrival generators on 30 and 28R. OAK is present on its generators alone.
        Assert.Equal([("OAK", 0, 0, "28R 30")], Sorted(ScenarioRunwayUse.CountAssigned(Load(S1Oak234))));

        // A generator whose runway does not resolve is skipped; the other one still lists its runway.
        ScenarioLoadResult withUnknown = LoadEdited(S1Oak234, root => root["aircraftGenerators"]!.AsArray()[0]!["runway"] = "99");

        Assert.Equal([("OAK", 0, 0, "28R")], Sorted(ScenarioRunwayUse.CountAssigned(withUnknown)));
    }

    [Fact]
    public void ArrivalGenerators_AddTheirRunways_ToThePrimarysAircraftCounts()
    {
        ScenarioLoadResult result = LoadEdited(S1Oak234, root => AddOnRunwaySpawn(root, "OAK", "28L"));

        Assert.Equal([("OAK", 1, 0, "28R 30")], Sorted(ScenarioRunwayUse.CountAssigned(result)));
    }

    private static (string Airport, int Departures, int Arrivals, string GeneratorRunways)[] Sorted(
        IReadOnlyDictionary<string, ScenarioRunwayUse> counts
    ) =>
        [
            .. counts
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (entry.Key, entry.Value.Departures, entry.Value.Arrivals, string.Join(' ', entry.Value.GeneratorArrivalRunways))),
        ];

    private static ScenarioLoadResult Load(string fileName) => LoadEdited(fileName, _ => { });

    private static ScenarioLoadResult LoadEdited(string fileName, Action<JsonObject> mutate)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, fileName)))!;
        mutate(root.AsObject());

        return ScenarioLoader.Load(root.ToJsonString(), new NullGroundData(), new Random(7), ModelDate);
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
