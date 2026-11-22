using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The sidecar's <c>cornerFillets</c> section: <see cref="AirportSidecarLoader"/> validates each entry into a
/// <see cref="CornerFilletOverride"/>, warning and skipping a bad one, and <see cref="AirportSidecarCatalog.GetCornerFillets"/>
/// hands the merged section on.
/// </summary>
public class CornerFilletSidecarTests
{
    private const string Full = """
        { "apex": [-122.2, 37.7], "taxiways": [" a ", "B"], "bearings": [10, 100], "radiusFt": 120, "force": true,
          "tangentFt": [40, 50], "note": "why" }
        """;

    [Fact]
    public void FullEntry_LoadsIntoTheModel()
    {
        (AirportSidecarLoadResult result, _) = Load(Full);

        Assert.Empty(result.Warnings);
        CornerFilletOverride corner = Assert.Single(Assert.Single(result.Airports).CornerFillets);
        Assert.Equal(new CornerFilletOverride(37.7, -122.2, "A", "B", 10, 100, false, 120, true, 40, 50), corner);
    }

    [Fact]
    public void SuppressEntry_LoadsWithNoOtherEdit()
    {
        (AirportSidecarLoadResult result, _) = Load(
            """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "suppress": true }"""
        );

        Assert.Empty(result.Warnings);
        Assert.Equal(
            new CornerFilletOverride(37.7, -122.2, "A", "B", 10, 100, true, null, false, null, null),
            Assert.Single(Assert.Single(result.Airports).CornerFillets)
        );
    }

    [Theory]
    [InlineData("radiusFt")]
    [InlineData("force")]
    [InlineData("tangentFt")]
    [InlineData("note")]
    public void OptionalMemberAbsent_StillLoads(string member)
    {
        string entry = member switch
        {
            "radiusFt" => """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "force": true, "tangentFt": [40, 50] }""",
            "force" => """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 120, "tangentFt": [40, 50] }""",
            "tangentFt" => """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 120, "force": true }""",
            _ =>
                """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 120, "force": true, "tangentFt": [40, 50] }""",
        };

        (AirportSidecarLoadResult result, _) = Load(entry);

        Assert.Empty(result.Warnings);
        CornerFilletOverride corner = Assert.Single(Assert.Single(result.Airports).CornerFillets);
        Assert.Equal(member != "radiusFt" ? 120 : null, corner.RadiusFt);
        Assert.Equal(member != "force", corner.Force);
        Assert.Equal(member != "tangentFt" ? 40 : null, corner.TangentAFt);
        Assert.Equal(member != "tangentFt" ? 50 : null, corner.TangentBFt);
        Assert.False(corner.Suppress);
    }

    [Theory]
    [InlineData("""{ "taxiways": ["A", "B"], "bearings": [10, 100] }""", "missing apex")]
    [InlineData("""{ "apex": [-122.2, 37.7], "bearings": [10, 100] }""", "missing taxiways")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"] }""", "missing bearings")]
    [InlineData("""{ "apex": [37.7], "taxiways": ["A", "B"], "bearings": [10, 100] }""", "apex needs [lon, lat]")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A"], "bearings": [10, 100] }""", "taxiways needs two taxiway names")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", " "], "bearings": [10, 100] }""", "taxiways needs two taxiway names")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10] }""", "bearings needs [degA, degB]")]
    [InlineData("""{ "apex": [-122.2, "NaN"], "taxiways": ["A", "B"], "bearings": [10, 100] }""", "apex has a non-finite number")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, "Infinity"] }""", "bearings has a non-finite number")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 360] }""", "bearing 360 is outside [0, 360)")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [-1, 100] }""", "bearing -1 is outside [0, 360)")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 0 }""", "radiusFt must be above 0")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": -5 }""", "radiusFt must be above 0")]
    [InlineData(
        """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": "NaN" }""",
        "radiusFt is not a finite number"
    )]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "tangentFt": [40, 0] }""", "tangentFt must be above 0")]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "tangentFt": [40] }""", "tangentFt needs [a, b]")]
    [InlineData(
        """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "tangentFt": ["-Infinity", 40] }""",
        "tangentFt has a non-finite number"
    )]
    [InlineData(
        """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "suppress": true, "radiusFt": 120 }""",
        "suppress cannot be combined with radiusFt, force or tangentFt"
    )]
    [InlineData(
        """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "suppress": true, "force": true }""",
        "suppress cannot be combined with radiusFt, force or tangentFt"
    )]
    [InlineData(
        """{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "suppress": true, "tangentFt": [40, 50] }""",
        "suppress cannot be combined with radiusFt, force or tangentFt"
    )]
    [InlineData("""{ "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radius": 120 }""", "has unknown key 'radius'")]
    public void InvalidEntry_IsDroppedWithAWarning_AndTheRestLoad(string badEntry, string problem)
    {
        (AirportSidecarLoadResult result, string filePath) = Load($"{badEntry}, {Full}");

        Assert.Equal([$"{filePath}: KMIA cornerFillets[0]: {problem}, skipping"], result.Warnings);
        Assert.Equal(40, Assert.Single(Assert.Single(result.Airports).CornerFillets).TangentAFt);
    }

    [Fact]
    public void DuplicateCorner_WarnsAndLastWinsInPlace()
    {
        (AirportSidecarLoadResult result, string filePath) = Load(
            """
            { "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 100 },
            { "apex": [-122.2, 37.8], "taxiways": ["A", "C"], "bearings": [10, 100], "radiusFt": 90 },
            { "apex": [-122.2, 37.70000005], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 150 }
            """
        );

        Assert.Equal([$"{filePath}: KMIA cornerFillets[2]: duplicate of cornerFillets[0], last wins"], result.Warnings);
        IReadOnlyList<CornerFilletOverride> corners = Assert.Single(result.Airports).CornerFillets;
        Assert.Equal([150.0, 90.0], corners.Select(c => c.RadiusFt!.Value));
    }

    [Fact]
    public void SwappedLowerCaseTaxiwayPair_IsTheSameCorner()
    {
        (AirportSidecarLoadResult result, string filePath) = Load(
            """
            { "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 100 },
            { "apex": [-122.2, 37.7], "taxiways": ["b", "a"], "bearings": [100, 10], "radiusFt": 150 }
            """
        );

        Assert.Equal([$"{filePath}: KMIA cornerFillets[1]: duplicate of cornerFillets[0], last wins"], result.Warnings);
        CornerFilletOverride corner = Assert.Single(Assert.Single(result.Airports).CornerFillets);
        Assert.Equal(("B", "A", 150.0), (corner.TaxiwayA, corner.TaxiwayB, corner.RadiusFt!.Value));
    }

    [Fact]
    public void TwoCornersOfOneCrossing_BothLoad()
    {
        (AirportSidecarLoadResult result, _) = Load(
            """
            { "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [0, 90], "radiusFt": 100 },
            { "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [180, 90], "radiusFt": 150 }
            """
        );

        Assert.Empty(result.Warnings);
        Assert.Equal([100.0, 150.0], Assert.Single(result.Airports).CornerFillets.Select(c => c.RadiusFt!.Value));
    }

    [Fact]
    public void Catalog_HandsOnTheSection_AndNoneWithoutIt()
    {
        (AirportSidecarLoadResult withSection, _) = Load(Full);
        (AirportSidecarLoadResult withoutSection, _) = LoadFile("""{ "airportId": "KMIA" }""");

        CornerFilletOverrides corners = new AirportSidecarCatalog(withSection.Airports).GetCornerFillets("MIA");
        Assert.Equal("A", Assert.Single(corners.Entries).TaxiwayA);
        Assert.Empty(Assert.Single(withoutSection.Airports).CornerFillets);
        Assert.Same(CornerFilletOverrides.None, new AirportSidecarCatalog(withoutSection.Airports).GetCornerFillets("KMIA"));
        Assert.Same(CornerFilletOverrides.None, AirportSidecarCatalog.Empty.GetCornerFillets("KMIA"));
    }

    [Fact]
    public void NullEntry_IsDroppedWithAWarning()
    {
        (AirportSidecarLoadResult result, string filePath) = Load($"null, {Full}");

        Assert.Equal([$"{filePath}: KMIA cornerFillets[0]: empty entry, skipping"], result.Warnings);
        Assert.Single(Assert.Single(result.Airports).CornerFillets);
    }

    [Theory]
    [InlineData("[-122.2, 37.7000002]", "[10, 100]")]
    [InlineData("[-122.2000002, 37.7]", "[10, 100]")]
    [InlineData("[-122.2, 37.7]", "[10.000002, 100]")]
    [InlineData("[-122.2, 37.7]", "[10, 99.999998]")]
    public void KeysJustOutsideTolerance_StayDistinct(string apex, string bearings)
    {
        (AirportSidecarLoadResult result, _) = Load(
            $$"""
            { "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 100 },
            { "apex": {{apex}}, "taxiways": ["A", "B"], "bearings": {{bearings}}, "radiusFt": 150 }
            """
        );

        Assert.Empty(result.Warnings);
        Assert.Equal(2, Assert.Single(result.Airports).CornerFillets.Count);
    }

    [Fact]
    public void BearingsAcrossTheWrap_AreTheSameKey()
    {
        (AirportSidecarLoadResult result, string filePath) = Load(
            """
            { "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [359.9999995, 100], "radiusFt": 100 },
            { "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [0.0000002, 100], "radiusFt": 150 }
            """
        );

        Assert.Equal([$"{filePath}: KMIA cornerFillets[1]: duplicate of cornerFillets[0], last wins"], result.Warnings);
        Assert.Equal(150, Assert.Single(Assert.Single(result.Airports).CornerFillets).RadiusFt);
    }

    [Fact]
    public void Catalog_MergesFiles_ALaterFileReplacingTheSameCornerInPlace()
    {
        AirportSidecarLoadResult result = LoadFiles(
            """
            { "airportId": "KMIA", "cornerFillets": [
              { "apex": [-122.2, 37.7], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 100 },
              { "apex": [-122.2, 37.8], "taxiways": ["A", "C"], "bearings": [10, 100], "radiusFt": 90 } ] }
            """,
            """
            { "airportId": "MIA", "cornerFillets": [
              { "apex": [-122.2, 37.7], "taxiways": ["b", "a"], "bearings": [100, 10], "radiusFt": 150 },
              { "apex": [-122.2, 37.9], "taxiways": ["A", "D"], "bearings": [10, 100], "radiusFt": 80 } ] }
            """
        );

        Assert.Empty(result.Warnings);
        CornerFilletOverrides corners = new AirportSidecarCatalog(result.Airports).GetCornerFillets("KMIA");
        Assert.Equal([150.0, 90.0, 80.0], corners.Entries.Select(c => c.RadiusFt!.Value));
    }

    /// <summary>Loads each JSON as its own sidecar file, in order, through <c>LoadFiles</c>.</summary>
    private static AirportSidecarLoadResult LoadFiles(params string[] jsons)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new List<string>();
            for (int i = 0; i < jsons.Length; i++)
            {
                string path = Path.Combine(tempDir, $"mia{i}.json");
                File.WriteAllText(path, jsons[i]);
                paths.Add(path);
            }

            return AirportSidecarLoader.LoadFiles(paths);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>Loads one KMIA sidecar whose <c>cornerFillets</c> section holds the given entries.</summary>
    private static (AirportSidecarLoadResult Result, string FilePath) Load(string entries) =>
        LoadFile($$"""{ "airportId": "KMIA", "cornerFillets": [ {{entries}} ] }""");

    private static (AirportSidecarLoadResult Result, string FilePath) LoadFile(string json)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        string filePath = Path.Combine(tempDir, "mia.json");
        try
        {
            File.WriteAllText(filePath, json);
            return (AirportSidecarLoader.LoadFiles([filePath]), filePath);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
