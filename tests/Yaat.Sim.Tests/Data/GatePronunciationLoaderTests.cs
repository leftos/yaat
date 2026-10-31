using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Proto;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Data;

public sealed class GatePronunciationLoaderTests
{
    [Fact]
    public void ValidFile_LoadsEverySection()
    {
        GatePronunciationLoadResult result = Load(
            """
            {
              "airportId": " koak ",
              "gates": [ { "name": " sig1 ", "pronunciations": [" signature one ", " ", "sig one"] } ],
              "spots": [ { "name": "6b", "pronunciations": ["six bravo"] } ],
              "places": [
                {
                  "name": "signature",
                  "pronunciations": ["signature", "signature ramp"],
                  "stands": [" sig* ", "SIGX"],
                  "notes": "Signature FBO ramp."
                }
              ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        GatePronunciationFile file = Assert.Single(result.Files);
        Assert.Equal("KOAK", file.AirportId);
        NamePronunciation gate = Assert.Single(file.Gates);
        Assert.Equal("SIG1", gate.Name);
        Assert.Equal(["signature one", "sig one"], gate.Pronunciations);
        NamePronunciation spot = Assert.Single(file.Spots);
        Assert.Equal("6B", spot.Name);
        Assert.Equal(["six bravo"], spot.Pronunciations);
        GroundPlaceDefinition place = Assert.Single(file.Places);
        Assert.Equal("SIGNATURE", place.Name);
        Assert.Equal(["signature", "signature ramp"], place.Pronunciations);
        Assert.Equal(["SIG*", "SIGX"], place.Stands);
        Assert.Equal("Signature FBO ramp.", place.Notes);
    }

    [Fact]
    public void UnparseableFile_SkippedWithWarning()
    {
        GatePronunciationLoadResult result = Load("{ \"airportId\": ");

        Assert.Empty(result.Files);
        Assert.Contains(result.Warnings, warning => warning.Contains("Failed to parse", StringComparison.Ordinal));
    }

    [Fact]
    public void NullFile_SkippedWithWarning()
    {
        GatePronunciationLoadResult result = Load("null");

        Assert.Empty(result.Files);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void BlankAirportId_SkipsWholeFile()
    {
        GatePronunciationLoadResult result = Load(
            """
            { "airportId": " ", "gates": [ { "name": "SIG1", "pronunciations": ["signature one"] } ] }
            """
        );

        Assert.Empty(result.Files);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void BlankEntryName_SkippedWithWarning()
    {
        GatePronunciationLoadResult result = Load(
            """
            {
              "airportId": "KOAK",
              "gates": [ { "name": " ", "pronunciations": ["signature one"] }, { "name": "SIG2", "pronunciations": ["signature two"] } ]
            }
            """
        );

        GatePronunciationFile file = Assert.Single(result.Files);
        Assert.Equal("SIG2", Assert.Single(file.Gates).Name);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void EntryWithoutNonBlankPronunciation_SkippedWithWarning()
    {
        GatePronunciationLoadResult result = Load(
            """
            {
              "airportId": "KOAK",
              "spots": [ { "name": "6B", "pronunciations": [" ", ""] } ],
              "places": [ { "name": "KAISER", "pronunciations": [] } ]
            }
            """
        );

        GatePronunciationFile file = Assert.Single(result.Files);
        Assert.Empty(file.Spots);
        Assert.Empty(file.Places);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void BlankStandPattern_DroppedWithWarning()
    {
        GatePronunciationLoadResult result = Load(
            """
            { "airportId": "KOAK", "places": [ { "name": "SIGNATURE", "pronunciations": ["signature"], "stands": [" ", "SIG*"] } ] }
            """
        );

        GroundPlaceDefinition place = Assert.Single(Assert.Single(result.Files).Places);
        Assert.Equal(["SIG*"], place.Stands);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void StandPatternWithInnerStar_DroppedWithWarning()
    {
        GatePronunciationLoadResult result = Load(
            """
            { "airportId": "KOAK", "places": [ { "name": "SIGNATURE", "pronunciations": ["signature"], "stands": ["S*G", "SIG1"] } ] }
            """
        );

        GroundPlaceDefinition place = Assert.Single(Assert.Single(result.Files).Places);
        Assert.Equal(["SIG1"], place.Stands);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void NavigationDatabase_FaaAndIcaoAirportIdsResolveTheSameAirport()
    {
        string root = Path.Combine(Path.GetTempPath(), "yaat-gate-pronunciation-tests", Guid.NewGuid().ToString("N"));
        string category = Path.Combine(root, "ZZZ", "GatePronunciations");
        Directory.CreateDirectory(category);
        File.WriteAllText(
            Path.Combine(category, "a.json"),
            """
            {
              "airportId": "OAK",
              "gates": [ { "name": "SIG1", "pronunciations": ["signature one"] } ],
              "places": [ { "name": "SIGNATURE", "pronunciations": ["signature"], "stands": ["SIG*"] } ]
            }
            """
        );
        File.WriteAllText(
            Path.Combine(category, "b.json"),
            """
            {
              "airportId": "KOAK",
              "gates": [ { "name": "sig1", "pronunciations": ["sig one"] } ],
              "spots": [ { "name": "6B", "pronunciations": ["six bravo"] } ],
              "places": [ { "name": "signature", "pronunciations": ["signature ramp"], "stands": ["FBO1"] } ]
            }
            """
        );

        try
        {
            NavigationDatabase db = BuildDatabase(root);

            Assert.Equal(["signature one", "sig one"], db.GetGatePronunciations("KOAK", "SIG1"));
            Assert.Equal(["signature one", "sig one"], db.GetGatePronunciations("oak", "sig1"));
            Assert.Equal(["six bravo"], db.GetSpotPronunciations("OAK", "6b"));
            GroundPlaceDefinition place = Assert.Single(db.GetGroundPlaces("koak"));
            Assert.Equal("SIGNATURE", place.Name);
            Assert.Equal(["signature", "signature ramp"], place.Pronunciations);
            Assert.Equal(["SIG*", "FBO1"], place.Stands);
            Assert.Empty(db.GetGatePronunciations("KSFO", "SIG1"));
            Assert.Empty(db.GetSpotPronunciations("KOAK", "7"));
            Assert.Empty(db.GetGroundPlaces("KSFO"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GatePronunciationLoadResult Load(string json)
    {
        string root = Path.Combine(Path.GetTempPath(), "yaat-gate-pronunciation-tests", Guid.NewGuid().ToString("N"));
        string category = Path.Combine(root, "ZZZ", "GatePronunciations");
        Directory.CreateDirectory(category);
        File.WriteAllText(Path.Combine(category, "x.json"), json);
        try
        {
            return GatePronunciationLoader.LoadAll(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static NavigationDatabase BuildDatabase(string artccsBaseDir)
    {
        NavDataSet navData = NavDataSet.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat")));
        string? cifpPath = TestVnasData.GetCifpPath();
        Assert.NotNull(cifpPath);
        return new NavigationDatabase(navData, cifpPath, artccsBaseDir: artccsBaseDir, supplementaryCifpFilePaths: []);
    }
}
