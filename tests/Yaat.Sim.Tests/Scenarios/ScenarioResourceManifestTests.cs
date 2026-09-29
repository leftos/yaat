using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// <see cref="ScenarioResourceManifest"/> names every resource a scenario load fetches before the loader runs. These pin
/// each rule the manifest reads from the JSON, and the corpus test pins that <see cref="ScenarioLoader.Load"/> never asks
/// for a ground layout the manifest did not name, which is what keeps the two from drifting.
/// </summary>
public class ScenarioResourceManifestTests
{
    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

    private static readonly string ExamplesDir = Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples");

    public ScenarioResourceManifestTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>Every committed scenario: the ATCTrainer examples under <c>docs/</c> and the test-data scenarios.</summary>
    public static TheoryData<string> CorpusScenarios()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(ExamplesDir, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        foreach (string path in Directory.EnumerateFiles(TestDataDir, "*scenario*.json").Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusScenarios))]
    public void Corpus_LoaderAsksForNoAirportOutsideTheManifest(string scenarioPath)
    {
        string json = File.ReadAllText(scenarioPath);
        var manifest = ScenarioResourceManifest.FromJson(json);
        Assert.Null(manifest.ReadError);

        var groundData = new RecordingGroundData();
        ScenarioLoader.Load(json, groundData, new Random(0), MagneticDeclination.EvaluationDateUtc);

        string[] missing = [.. groundData.Requested.Where(id => !manifest.AirportIds.Contains(id)).Order(StringComparer.Ordinal)];
        string missingList = string.Join(", ", missing);
        string manifestList = string.Join(", ", manifest.AirportIds);
        Assert.True(
            missing.Length == 0,
            $"{Path.GetFileName(scenarioPath)}: the loader asked for {missingList}, which the manifest ({manifestList}) does not name"
        );
    }

    [Fact]
    public void PrimaryAirport_IsNamed()
    {
        var manifest = ScenarioResourceManifest.FromJson("""{ "primaryAirportId": "KOAK" }""");

        Assert.Null(manifest.ReadError);
        Assert.Equal(["OAK"], manifest.AirportIds);
    }

    [Theory]
    [InlineData("""{ "airportId": "KHWD" }""")]
    [InlineData("""{ "flightplan": { "departure": "KHWD" } }""")]
    [InlineData("""{ "flightplan": { "destination": "KHWD" } }""")]
    public void EachAircraftField_IsNamed(string aircraftFields)
    {
        // An unknown starting-condition type makes the loader read no layout, so only the field rule can name KHWD.
        string aircraft = aircraftFields.TrimEnd().TrimEnd('}') + """, "aircraftId": "N1", "startingConditions": { "type": "None" } }""";
        var manifest = ScenarioResourceManifest.FromJson($$"""{ "primaryAirportId": "KOAK", "aircraft": [{{aircraft}}] }""");

        Assert.Equal(["OAK", "HWD"], manifest.AirportIds);
    }

    [Fact]
    public void ParkingChain_NamesTheAirportTheLoaderPlacesTheAircraftAt()
    {
        // Parking picks airportId, then the primary airport, then the departure: here the primary airport.
        const string json = """
            {
              "primaryAirportId": "KOAK",
              "aircraft": [
                { "aircraftId": "N1", "aircraftType": "C172", "startingConditions": { "type": "Parking", "parking": "A1" },
                  "flightplan": { "departure": "KSQL", "destination": "KSFO" } }
              ]
            }
            """;

        AssertLoaderReadsOnlyManifestAirports(json, expectedRequested: ["OAK"]);
    }

    [Theory]
    [InlineData("Coordinates", """ "coordinates": { "lat": 37.5119, "lon": -122.2495 } """)]
    [InlineData("FixOrFrd", """ "fix": "OSI" """)]
    public void CoordinatesAndFixOrFrdChain_NamesTheFieldUnderAGroundSpawn(string type, string position)
    {
        // With no airportId the field under the ground spawn is the filed departure, not the primary airport.
        string json = $$"""
            {
              "primaryAirportId": "KOAK",
              "aircraft": [
                { "aircraftId": "N1", "aircraftType": "C172", "startingConditions": { "type": "{{type}}", {{position}} },
                  "flightplan": { "departure": "KSQL", "destination": "KSFO" } }
              ]
            }
            """;

        AssertLoaderReadsOnlyManifestAirports(json, expectedRequested: ["SQL"]);
    }

    [Fact]
    public void KPrefixAndCase_AreOneAirport()
    {
        const string json = """
            {
              "primaryAirportId": "KOAK",
              "aircraft": [
                { "aircraftId": "N1", "airportId": "oak", "startingConditions": { "type": "None" },
                  "flightplan": { "departure": "OAK", "destination": "koak" } }
              ]
            }
            """;

        Assert.Equal(["OAK"], ScenarioResourceManifest.FromJson(json).AirportIds);
    }

    [Fact]
    public void RosterNeighbourArtccs_AreDeduplicated_AndExcludeTheScenariosOwn()
    {
        const string json = """
            {
              "artccId": "zoa",
              "atc": [
                { "artccId": "ZOA", "positionId": "A" },
                { "artccId": "ZLA", "positionId": "B" },
                { "artccId": "zla", "positionId": "C" },
                { "artccId": "", "positionId": "D" },
                { "artccId": "ZLC", "positionId": "E" }
              ]
            }
            """;

        var manifest = ScenarioResourceManifest.FromJson(json);

        Assert.Equal("ZOA", manifest.ArtccId);
        Assert.Equal(["ZLA", "ZLC"], manifest.NeighbourArtccIds);
    }

    [Theory]
    [InlineData("KHAF", true)]
    [InlineData("SUNOL", false)]
    public void VfrArrivalGeneratorDirectTo_IsNamedOnlyWhenItIsAnAirport(string directTo, bool expectedNamed)
    {
        string json = $$"""{ "primaryAirportId": "KOAK", "vfrArrivalGenerators": [{ "id": "g1", "directTo": "{{directTo}}" }] }""";

        var manifest = ScenarioResourceManifest.FromJson(json);

        string[] expected = expectedNamed ? ["OAK", "HAF"] : ["OAK"];
        Assert.Equal(expected, manifest.AirportIds);
    }

    [Theory]
    [InlineData("DEST KSQL", "OAK,SQL")]
    [InlineData("DCT SUNOL", "OAK")]
    [InlineData("SAY REQUEST VFR ON TOP", "OAK")]
    [InlineData("WAIT 300 APT FCH", "OAK,FCH")]
    public void PresetCommand_NamesOnlyTheAirportItCarries(string command, string expectedAirports)
    {
        string json = $$"""
            {
              "primaryAirportId": "KOAK",
              "aircraft": [
                { "aircraftId": "N1", "startingConditions": { "type": "None" }, "presetCommands": [{ "command": "{{command}}" }] }
              ]
            }
            """;

        var manifest = ScenarioResourceManifest.FromJson(json);

        Assert.Equal(expectedAirports.Split(','), manifest.AirportIds);
    }

    [Theory]
    [InlineData(""" "aircraft": null """)]
    [InlineData(""" "atc": null """)]
    [InlineData(""" "aircraft": [{ "aircraftId": "N1", "startingConditions": { "type": "None" }, "presetCommands": null }] """)]
    [InlineData(""" "aircraft": [{ "aircraftId": "N1", "startingConditions": { "type": "None" }, "presetCommands": [{ "command": null }] }] """)]
    public void ExplicitJsonNull_NamesWhatIsThere_WithoutThrowing(string member)
    {
        var manifest = ScenarioResourceManifest.FromJson($$"""{ "artccId": "ZOA", "primaryAirportId": "KOAK", {{member}} }""");

        Assert.Null(manifest.ReadError);
        Assert.Equal("ZOA", manifest.ArtccId);
        Assert.Empty(manifest.NeighbourArtccIds);
        Assert.Equal(["OAK"], manifest.AirportIds);
    }

    [Fact]
    public void UnreadableJson_ReportsTheFailure_AndNamesNothing()
    {
        var manifest = ScenarioResourceManifest.FromJson("{ \"aircraft\": [ not json");

        Assert.False(string.IsNullOrEmpty(manifest.ReadError));
        Assert.Null(manifest.ArtccId);
        Assert.Empty(manifest.NeighbourArtccIds);
        Assert.Empty(manifest.AirportIds);
    }

    [Fact]
    public void EmptyAircraftList_NamesThePrimaryAirportAndArtcc()
    {
        var manifest = ScenarioResourceManifest.FromJson("""{ "name": "Empty", "artccId": "ZOA", "primaryAirportId": "KOAK", "aircraft": [] }""");

        Assert.Null(manifest.ReadError);
        Assert.Equal("Empty", manifest.ScenarioName);
        Assert.Equal("ZOA", manifest.ArtccId);
        Assert.Empty(manifest.NeighbourArtccIds);
        Assert.Equal(["OAK"], manifest.AirportIds);
    }

    private static void AssertLoaderReadsOnlyManifestAirports(string json, string[] expectedRequested)
    {
        var groundData = new RecordingGroundData();
        ScenarioLoader.Load(json, groundData, new Random(0), MagneticDeclination.EvaluationDateUtc);
        var manifest = ScenarioResourceManifest.FromJson(json);

        Assert.Equal(expectedRequested, groundData.Requested.Order(StringComparer.Ordinal));
        Assert.All(groundData.Requested, id => Assert.Contains(id, manifest.AirportIds));
    }

    /// <summary>Records every airport the loader asks a layout for, FAA-coded, and has none to give.</summary>
    private sealed class RecordingGroundData : IAirportGroundData
    {
        public HashSet<string> Requested { get; } = new(StringComparer.Ordinal);

        public AirportGroundLayout? GetLayout(string airportId)
        {
            Requested.Add(AirportLayoutDownloader.ToFaaCode(airportId));
            return null;
        }

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
