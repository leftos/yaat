using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Data;

public sealed class ProcedurePronunciationLoaderTests
{
    [Fact]
    public void ValidFile_LoadsEveryEntry()
    {
        ProcedurePronunciationLoadResult result = Load(
            """
            [
              { "procedure": " nimi ", "pronunciations": [" nimitz ", " "] },
              { "procedure": "SKYLN", "pronunciations": ["skyline"] }
            ]
            """
        );

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Definitions.Count);
        Assert.Equal("NIMI", result.Definitions[0].Procedure);
        Assert.Equal(["nimitz"], result.Definitions[0].Pronunciations);
        Assert.Equal("SKYLN", result.Definitions[1].Procedure);
    }

    [Fact]
    public void UnparseableFile_SkippedWithWarning()
    {
        ProcedurePronunciationLoadResult result = Load("[ { \"procedure\": ");

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Warnings, warning => warning.Contains("Failed to parse", StringComparison.Ordinal));
    }

    [Fact]
    public void NullFile_SkippedWithWarning()
    {
        ProcedurePronunciationLoadResult result = Load("null");

        Assert.Empty(result.Definitions);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void BlankProcedure_SkippedWithWarning()
    {
        ProcedurePronunciationLoadResult result = Load("""[ { "procedure": " ", "pronunciations": ["nimitz"] } ]""");

        Assert.Empty(result.Definitions);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void ProcedureWithVersionDigit_SkippedWithWarning()
    {
        ProcedurePronunciationLoadResult result = Load("""[ { "procedure": "NIMI5", "pronunciations": ["nimitz"] } ]""");

        Assert.Empty(result.Definitions);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void EntryWithoutNonBlankPronunciation_SkippedWithWarning()
    {
        ProcedurePronunciationLoadResult result = Load("""[ { "procedure": "NIMI", "pronunciations": [" "] } ]""");

        Assert.Empty(result.Definitions);
        Assert.Single(result.Warnings);
    }

    private static ProcedurePronunciationLoadResult Load(string json)
    {
        string root = Path.Combine(Path.GetTempPath(), "yaat-procedure-pronunciation-tests", Guid.NewGuid().ToString("N"));
        string category = Path.Combine(root, "ZZZ", "ProcedurePronunciations");
        Directory.CreateDirectory(category);
        File.WriteAllText(Path.Combine(category, "x.json"), json);
        try
        {
            return ProcedurePronunciationLoader.LoadAll(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
