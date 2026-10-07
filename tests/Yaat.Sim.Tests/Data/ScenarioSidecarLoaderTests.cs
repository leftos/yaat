using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Data;

public sealed class ScenarioSidecarLoaderTests
{
    [Fact]
    public void Find_ReturnsParsedRunways()
    {
        string root = NewRoot();
        WriteSidecar(root, "ZOA", "oak.json", """{ "activeRunways": { "OAK": ["D28L", "A28R", "30"] } }""");

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            Assert.Empty(result.Warnings);
            ScenarioSidecar? sidecar = result.Find("ZOA", "oak");
            Assert.NotNull(sidecar);
            ActiveRunway[] expected = [new("28L", ActiveRunwayUse.Departure), new("28R", ActiveRunwayUse.Arrival), new("30", ActiveRunwayUse.Both)];
            Assert.Equal(expected, sidecar!.ActiveRunways.For("OAK"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Find_MatchesIdCaseInsensitively()
    {
        string root = NewRoot();
        WriteSidecar(root, "ZOA", "OAK.json", """{ "activeRunways": { "OAK": ["28L"] } }""");

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            ScenarioSidecar? sidecar = result.Find("zoa", "oak");
            Assert.NotNull(sidecar);
            Assert.Equal("28L", Assert.Single(sidecar!.ActiveRunways.For("OAK")).Designator);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Find_KeysIcaoAndMixedCaseToTheSameAirport()
    {
        string root = NewRoot();
        WriteSidecar(root, "ZOA", "oak.json", """{ "activeRunways": { "kOaK": ["28L"] } }""");

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            Assert.Empty(result.Warnings);
            ScenarioSidecar? sidecar = result.Find("ZOA", "oak");
            Assert.NotNull(sidecar);
            Assert.Equal("28L", Assert.Single(sidecar!.ActiveRunways.For("OAK")).Designator);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingScenariosFolder_FindsNothing()
    {
        string root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "ZOA", "CustomFixes"));

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            Assert.Null(result.Find("ZOA", "oak"));
            Assert.Empty(result.Warnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MalformedJson_IsSkippedAndOthersLoad()
    {
        string root = NewRoot();
        WriteSidecar(root, "ZOA", "broken.json", """{ "activeRunways": """);
        WriteSidecar(root, "ZOA", "oak.json", """{ "activeRunways": { "OAK": ["28L"] } }""");

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            Assert.Null(result.Find("ZOA", "broken"));
            Assert.NotNull(result.Find("ZOA", "oak"));
            Assert.Contains(
                result.Warnings,
                w => w.Contains("broken.json", StringComparison.Ordinal) && w.Contains("could not be read or parsed", StringComparison.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileOfNull_IsSkippedWithAWarning()
    {
        string root = NewRoot();
        WriteSidecar(root, "ZOA", "nothing.json", "null");
        WriteSidecar(root, "ZOA", "oak.json", """{ "activeRunways": { "OAK": ["28L"] } }""");

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            Assert.Null(result.Find("ZOA", "nothing"));
            Assert.NotNull(result.Find("ZOA", "oak"));
            Assert.Contains(
                result.Warnings,
                w => w.Contains("nothing.json", StringComparison.Ordinal) && w.Contains("deserialized to null", StringComparison.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        string root = NewRoot();
        WriteSidecar(root, "ZOA", "oak.json", """{ "activeRunways": { "OAK": ["28L"] }, "somethingElse": { "x": 1 }, "note": "future settings" }""");

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            ScenarioSidecar? sidecar = result.Find("ZOA", "oak");
            Assert.NotNull(sidecar);
            Assert.Equal("28L", Assert.Single(sidecar!.ActiveRunways.For("OAK")).Designator);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{ "activeRunways": { "OAK": null, "SFO": ["10L"] } }""", "OAK has no runway list")]
    [InlineData("""{ "activeRunways": { "OAK": ["28L", null], "SFO": ["10L"] } }""", "OAK has a null runway entry")]
    [InlineData("""{ "activeRunways": { "OAK": ["28L 28R"], "SFO": ["10L"] } }""", "OAK: Not a runway: 28L 28R")]
    [InlineData("""{ "activeRunways": { "OAK": ["28L", "D28L"], "SFO": ["10L"] } }""", "OAK: runway 28L listed twice")]
    [InlineData("""{ "activeRunways": { "OAK": ["D99"], "SFO": ["10L"] } }""", "OAK: Not a runway: D99")]
    public void UnreadableAirport_IsDroppedWithAWarningAndTheRestKept(string json, string expectedWarning)
    {
        string root = NewRoot();
        WriteSidecar(root, "ZOA", "oak.json", json);

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            ScenarioSidecar? sidecar = result.Find("ZOA", "oak");
            Assert.NotNull(sidecar);
            Assert.Empty(sidecar!.ActiveRunways.For("OAK"));
            Assert.Equal("10L", Assert.Single(sidecar.ActiveRunways.For("SFO")).Designator);
            Assert.Contains(
                result.Warnings,
                w => w.Contains("oak.json", StringComparison.Ordinal) && w.Contains(expectedWarning, StringComparison.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{ "activeRunways": { "OAK": ["28L"], "oak": ["10L"] } }""", "oak names the same airport as OAK")]
    [InlineData("""{ "activeRunways": { "KOAK": ["28L"], "OAK": ["10L"] } }""", "OAK names the same airport as KOAK")]
    public void SameAirportNamedTwice_SecondSpellingIsSkippedWithAWarning(string json, string expectedWarning)
    {
        string root = NewRoot();
        WriteSidecar(root, "ZOA", "oak.json", json);

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            ScenarioSidecar? sidecar = result.Find("ZOA", "oak");
            Assert.NotNull(sidecar);
            Assert.Equal("28L", Assert.Single(sidecar!.ActiveRunways.For("OAK")).Designator);
            Assert.Contains(result.Warnings, w => w.Contains(expectedWarning, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TwoFilesNormalizingToOneScenarioId_SecondIsSkippedWithAWarning()
    {
        // A case-insensitive filesystem cannot hold `oak.json` and `OAK.json` at once (the second write replaces the
        // first), so the twin here differs by a leading space: both names trim and upper-case to the same scenario id.
        // The loader orders the files ordinally, so the leading space sorts first and that file is the one that loads.
        string root = NewRoot();
        WriteSidecar(root, "ZOA", " oak.json", """{ "activeRunways": { "OAK": ["28L"] } }""");
        WriteSidecar(root, "ZOA", "oak.json", """{ "activeRunways": { "OAK": ["10L"] } }""");

        try
        {
            ScenarioSidecarLoadResult result = ScenarioSidecarLoader.LoadAll(root);

            ScenarioSidecar? sidecar = result.Find("ZOA", "oak");
            Assert.NotNull(sidecar);
            Assert.Equal("28L", Assert.Single(sidecar!.ActiveRunways.For("OAK")).Designator);
            string warning = Assert.Single(result.Warnings);
            Assert.Contains("is already loaded from", warning, StringComparison.Ordinal);
            Assert.Contains("oak.json", warning, StringComparison.Ordinal);
            Assert.Contains(" oak.json", warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "yaat-scenario-sidecar-tests", Guid.NewGuid().ToString("N"));

    private static void WriteSidecar(string root, string artccId, string fileName, string json)
    {
        string dir = Path.Combine(root, artccId, "Scenarios");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), json);
    }
}
