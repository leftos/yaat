using Parquet.Serialization;
using Xunit;
using Yaat.Client.Services;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The <c>--corpus-wer</c> corpus plumbing: roles read from segment ids, the seeded subset, and the
/// UWB-ATCC parquet reader turning embedded 8 kHz WAV rows into 16 kHz segments.
/// </summary>
public sealed class CorpusWerCorpusTests : IDisposable
{
    private static readonly string[] FiftyIds = [.. Enumerable.Range(0, 50).Select(i => $"id{i:00}")];

    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-wer-parquet-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("x_PI", CorpusRole.Pilot)]
    [InlineData("x_AT", CorpusRole.Controller)]
    [InlineData("x_PIAT", CorpusRole.Mixed)]
    [InlineData("x", CorpusRole.Unknown)]
    public void FromId_Reads_The_Role_From_The_Id_Ending(string id, CorpusRole expected) => Assert.Equal(expected, CorpusRoles.FromId(id));

    [Fact]
    public void Select_With_The_Same_Seed_Picks_The_Same_Ids()
    {
        IReadOnlyList<string> first = CorpusSubset.Select(FiftyIds, 10, 7);
        IReadOnlyList<string> second = CorpusSubset.Select(FiftyIds, 10, 7);

        Assert.Equal(10, first.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Select_With_Different_Seeds_Picks_Different_Ids() =>
        Assert.NotEqual(CorpusSubset.Select(FiftyIds, 10, 1), CorpusSubset.Select(FiftyIds, 10, 2));

    [Fact]
    public void Select_Without_A_Count_Returns_Every_Id_Sorted() => Assert.Equal(FiftyIds, CorpusSubset.Select([.. FiftyIds.Reverse()], null, 1));

    [Fact]
    public void Select_With_A_Count_Above_The_Id_Count_Returns_Every_Id() => Assert.Equal(FiftyIds, CorpusSubset.Select(FiftyIds, 60, 1));

    [Fact]
    public void Select_Returns_Ids_In_Ordinal_Order()
    {
        IReadOnlyList<string> picked = CorpusSubset.Select([.. FiftyIds.Reverse()], 10, 3);

        Assert.Equal(picked.Order(StringComparer.Ordinal), picked);
    }

    [Fact]
    public async Task LoadAsync_Reads_Every_Row_As_A_16k_Segment()
    {
        string path = await WriteParquetAsync();

        IReadOnlyList<CorpusSegment> segments = await UwbAtccCorpus.LoadAsync(path, subset: null, seed: 1);

        string[] expectedIds = ["a_PI", "b_AT", "c_PIAT"];
        CorpusRole[] expectedRoles = [CorpusRole.Pilot, CorpusRole.Controller, CorpusRole.Mixed];
        string[] expectedReferences = ["one two", "three", "four five six"];
        Assert.Equal(expectedIds, segments.Select(s => s.Id));
        Assert.Equal(expectedRoles, segments.Select(s => s.Role));
        Assert.All(segments, s => Assert.Equal(1600, s.Samples16k.Length));
        Assert.Equal(expectedReferences, segments.Select(s => s.Reference));
    }

    [Fact]
    public async Task LoadAsync_With_A_Subset_Returns_That_Many_Segments()
    {
        string path = await WriteParquetAsync();

        IReadOnlyList<CorpusSegment> segments = await UwbAtccCorpus.LoadAsync(path, subset: 2, seed: 1);

        Assert.Equal(2, segments.Count);
    }

    [Fact]
    public async Task LoadAsync_Throws_On_A_Duplicate_Id()
    {
        byte[] wav = WavHeader.WritePcm16(new float[800], 8000).ToArray();
        string path = await WriteRowsAsync([Row("a_PI", wav, "one"), Row("b_AT", wav, "two"), Row("a_PI", wav, "three")]);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => UwbAtccCorpus.LoadAsync(path, subset: null, seed: 1));

        Assert.Contains("a_PI", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_Throws_On_A_Row_Without_Audio_Bytes()
    {
        byte[] wav = WavHeader.WritePcm16(new float[800], 8000).ToArray();
        string path = await WriteRowsAsync([Row("a_PI", wav, "one"), Row("b_AT", null, "two")]);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => UwbAtccCorpus.LoadAsync(path, subset: null, seed: 1));

        Assert.Contains("b_AT", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8000, 1600)]
    [InlineData(16000, 800)]
    public void To16k_Accepts_Rates_Up_To_16k(int sampleRate, int expectedLength) =>
        Assert.Equal(expectedLength, CorpusRoles.To16k(new Pcm16Audio(new float[800], sampleRate), "seg").Length);

    [Theory]
    [InlineData(0)]
    [InlineData(44100)]
    public void To16k_Rejects_A_Rate_It_Cannot_Resample(int sampleRate)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            CorpusRoles.To16k(new Pcm16Audio(new float[800], sampleRate), "seg_PI")
        );

        Assert.Contains("seg_PI", error.Message, StringComparison.Ordinal);
        Assert.Contains(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    private async Task<string> WriteParquetAsync()
    {
        byte[] wav = WavHeader.WritePcm16(new float[800], 8000).ToArray();
        return await WriteRowsAsync([Row("a_PI", wav, "one two"), Row("b_AT", wav, "three"), Row("c_PIAT", wav, "four five six")]);
    }

    private async Task<string> WriteRowsAsync(UwbAtccRow[] rows)
    {
        string path = Path.Combine(_dir, "test.parquet");
        await ParquetSerializer.SerializeAsync(rows, path);
        return path;
    }

    private static UwbAtccRow Row(string id, byte[]? wav, string text) =>
        new()
        {
            Id = id,
            Audio = new UwbAtccAudio { Bytes = wav },
            Text = text,
        };
}
