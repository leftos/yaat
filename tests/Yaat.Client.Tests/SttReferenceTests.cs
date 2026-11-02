using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.Tests;

/// <summary>The reference WER and callsign-recall file the speech benchmark shows beside this machine's figures.</summary>
public sealed class SttReferenceTests : IDisposable
{
    /// <summary>A valid file's header up to the opening of its entries list.</summary>
    private const string EntriesOpening = "{ \"schema\": 1, \"corpus\": null, \"sttNative\": null, \"entries\": [ ";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaat-tests", "stt-reference-" + Guid.NewGuid().ToString("N"));

    public SttReferenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_Committed_File_Loads_With_No_Entries()
    {
        var reference = SttReference.Load(Path.Combine(AppContext.BaseDirectory, "Speech", "stt-reference.json"));

        Assert.Equal(1, reference.Schema);
        Assert.Empty(reference.Entries);
        Assert.Null(reference.Find(SttEngine.RasrV2Fp32, SttMode.Accurate));
    }

    [Fact]
    public void An_Entry_Is_Found_By_Engine_And_Mode()
    {
        string path = Write(
            "one.json",
            """
            { "schema": 1, "corpus": "liveatc-us", "sttNative": "abc1234567",
              "entries": [ { "engine": "RasrV2Fp32", "mode": "Fast", "ownerWer": 0.296, "callsignRecall": 0.362 } ] }
            """
        );

        var reference = SttReference.Load(path);

        Assert.Equal(new SttReferenceEntry(SttEngine.RasrV2Fp32, SttMode.Fast, 0.296, 0.362), reference.Find(SttEngine.RasrV2Fp32, SttMode.Fast));
        Assert.Null(reference.Find(SttEngine.RasrV2Fp32, SttMode.Accurate));
        Assert.Null(reference.Find(SttEngine.RasrV2Int8, SttMode.Fast));
        Assert.Equal("liveatc-us", reference.Corpus);
        Assert.Equal("abc1234567", reference.SttNative);
    }

    [Fact]
    public void Null_Figures_Load_As_Null()
    {
        string path = Write(
            "nulls.json",
            """
            { "schema": 1, "corpus": null, "sttNative": null,
              "entries": [ { "engine": "RasrV2Int8", "mode": "Accurate", "ownerWer": null, "callsignRecall": null } ] }
            """
        );

        SttReferenceEntry? entry = SttReference.Load(path).Find(SttEngine.RasrV2Int8, SttMode.Accurate);

        Assert.Equal(new SttReferenceEntry(SttEngine.RasrV2Int8, SttMode.Accurate, null, null), entry);
    }

    [Fact]
    public void An_Unknown_Engine_Is_Skipped()
    {
        string path = Write(
            "unknown.json",
            """
            { "schema": 1, "corpus": null, "sttNative": null, "entries": [
              { "engine": "SomeFutureModel", "mode": "Fast", "ownerWer": 0.1, "callsignRecall": 0.9 },
              { "engine": "RasrV2Int8", "mode": "Fast", "ownerWer": 0.3, "callsignRecall": 0.4 } ] }
            """
        );

        var reference = SttReference.Load(path);

        SttReferenceEntry entry = Assert.Single(reference.Entries);
        Assert.Equal(SttEngine.RasrV2Int8, entry.Engine);
    }

    [Theory]
    [InlineData("{ \"schema\": 1, \"entries\": [ ")]
    [InlineData("[]")]
    [InlineData("{ \"schema\": 2, \"corpus\": null, \"sttNative\": null, \"entries\": [] }")]
    [InlineData("{ \"schema\": 1, \"corpus\": null, \"sttNative\": null }")]
    [InlineData("{ \"schema\": 1, \"entries\": [ { \"engine\": \"RasrV2Fp32\", \"mode\": \"Turbo\" } ] }")]
    [InlineData(EntriesOpening + "{ \"engine\": \"RasrV2Fp32\", \"ownerWer\": 0.3, \"callsignRecall\": 0.4 } ] }")]
    [InlineData(EntriesOpening + "{ \"mode\": \"Fast\", \"ownerWer\": 0.3, \"callsignRecall\": 0.4 } ] }")]
    public void A_Malformed_File_Throws_Naming_Its_Path(string json)
    {
        string path = Write("bad.json", json);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => SttReference.Load(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    private string Write(string name, string json)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }
}
