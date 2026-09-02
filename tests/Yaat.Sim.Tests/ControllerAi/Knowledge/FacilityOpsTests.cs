using System.Text.Json;
using Xunit;
using Yaat.Sim.ControllerAi.Knowledge;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.ControllerAi.Knowledge;

/// <summary>The knowledge-file contract: strict parsing, navdata cross-validation, every committed file valid, and the SOP aircraft classes.</summary>
public class FacilityOpsTests
{
    private static readonly string DataDirectory = Path.Combine(AppContext.BaseDirectory, "Data", "FacilityOps");

    public FacilityOpsTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static NavigationDatabase Navigation => TestVnasData.NavigationDb ?? throw new InvalidOperationException("navdata missing");

    private static string KoakJson => File.ReadAllText(Path.Combine(DataDirectory, "KOAK.json"));

    [Fact]
    public void EveryCommittedFile_Validates_AndIsLookedUpByEitherAirportForm()
    {
        var files = Directory.GetFiles(DataDirectory, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var ops = FacilityOpsDatabase.Load(file, Navigation);
            Assert.Equal(FacilityOps.CurrentSchemaVersion, ops.SchemaVersion);
        }

        Assert.True(FacilityOpsDatabase.IsInitialized);
        var oak = FacilityOpsDatabase.For("OAK");
        Assert.NotNull(oak);
        Assert.Same(oak, FacilityOpsDatabase.For("KOAK"));
        Assert.Equal("OAK", oak.FacilityId);
        Assert.Null(FacilityOpsDatabase.For("SFO"));
        Assert.Null(FacilityOpsDatabase.For(null));
    }

    [Fact]
    public void Koak_RoundTrips()
    {
        var ops = JsonSerializer.Deserialize<FacilityOps>(KoakJson, FacilityOpsJson.Options)!;
        var once = JsonSerializer.Serialize(ops, FacilityOpsJson.Options);
        var twice = JsonSerializer.Serialize(JsonSerializer.Deserialize<FacilityOps>(once, FacilityOpsJson.Options), FacilityOpsJson.Options);

        Assert.Equal(once, twice);
        Assert.Equal(["SFOW", "OAKE", "SFOE"], ops.RunwayConfigurations.Select(c => c.Name));
        Assert.Equal(["28L", "28R", "30"], ops.RunwaysAt("SFOW", "OAK")!.Departure);
        Assert.Equal(3, ops.RunwayAssignmentPolicy.Count);
        Assert.All(ops.RunwayConfigurations, c => Assert.StartsWith("OAK ATCT SOP", c.Source));
    }

    [Theory]
    [InlineData("\"28L\", \"28R\", \"30\"]", "\"28L\", \"28X\", \"30\"]", "no runway 28X")]
    [InlineData("\"effect\": \"Exclude\"", "\"effect\": \"Banish\"", "RunwayAssignmentEffect")]
    [InlineData("\"source\": \"OAK ATCT SOP 1-6\"", "\"source\": \"\"", "source is missing")]
    [InlineData("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"bogus\": 1,", "bogus")]
    [InlineData("\"calmConfiguration\": \"SFOW\"", "\"calmConfiguration\": \"NOPE\"", "NOPE is not declared")]
    [InlineData("\"partnerAirportId\": \"KSFO\"", "\"partnerAirportId\": \"ZZZZ\"", "ZZZZ is not in navdata")]
    [InlineData("\"applies\": { \"category\": \"Jet\" }", "\"applies\": { }", "applies to nothing")]
    [InlineData(
        "\"KOAK\": { \"departure\": [\"28L\", \"28R\", \"30\"], \"arrival\": [\"28L\", \"28R\", \"30\"] }",
        "\"KOAK\": { \"departure\": [\"28L\", \"28R\", \"30\"], \"arrival\": [\"28L\", \"28R\", \"30\"] }, \"OAK\": { \"departure\": [\"30\"], \"arrival\": [\"30\"] }",
        "both KOAK and OAK"
    )]
    public void ABrokenFile_FailsToLoad_NamingTheProblem(string find, string replaceWith, string expectedError)
    {
        var json = KoakJson;
        Assert.Contains(find, json);
        var broken = json.Replace(find, replaceWith);
        var path = Path.Combine(Path.GetTempPath(), $"yaat-facility-ops-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, broken);
        try
        {
            var error = Assert.Throws<FacilityOpsValidationException>(() => FacilityOpsDatabase.Load(path, Navigation));
            Assert.Contains(expectedError, error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingDirectory_IsNoKnowledge_NotAnError()
    {
        Assert.Empty(FacilityOpsDatabase.LoadDirectory(Path.Combine(Path.GetTempPath(), $"yaat-no-such-dir-{Guid.NewGuid():N}"), Navigation));
    }

    [Theory]
    [InlineData("B738", SopAircraftClass.J)]
    [InlineData("A332", SopAircraftClass.J)]
    [InlineData("DH8D", SopAircraftClass.T)]
    [InlineData("PC12", SopAircraftClass.T)]
    [InlineData("C208", SopAircraftClass.P)]
    [InlineData("C172", SopAircraftClass.P)]
    [InlineData("P28A", SopAircraftClass.P)]
    public void SopAircraftClass_FollowsTheNctDefinitions(string type, SopAircraftClass expected)
    {
        Assert.Equal(expected, SopAircraftClassifier.Classify(type));
    }

    [Fact]
    public void AssignmentPredicates_MatchOnEveryStatedField()
    {
        var jets = new AircraftPredicate { Category = AircraftCategory.Jet };
        var heavyTurboprops = new AircraftPredicate { Category = AircraftCategory.Turboprop, MtowOverLb = 17000 };
        var fourEngineRecips = new AircraftPredicate { Category = AircraftCategory.Piston, EngineCount = 4 };

        Assert.True(SopAircraftClassifier.Matches(jets, "B738"));
        Assert.False(SopAircraftClassifier.Matches(jets, "C172"));
        Assert.True(SopAircraftClassifier.Matches(heavyTurboprops, "DH8D"));
        Assert.False(SopAircraftClassifier.Matches(heavyTurboprops, "C208"));
        Assert.False(SopAircraftClassifier.Matches(heavyTurboprops, "B738"));
        Assert.False(SopAircraftClassifier.Matches(fourEngineRecips, "C172"));
    }
}
