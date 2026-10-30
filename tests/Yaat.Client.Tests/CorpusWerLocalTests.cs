using Xunit;
using Yaat.Client.Services;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The <c>--corpus-wer &lt;dir&gt;</c> loader: WAV files paired with a <c>.txt</c> transcript of the
/// same name, resampled to 16 kHz, and the errors a malformed folder raises.
/// </summary>
public sealed class CorpusWerLocalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-wer-local-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_Pairs_Each_Wav_With_Its_Transcript()
    {
        WritePair("a_PI", 800, 8000, "  hello world \n");
        WritePair("b", 400, 16000, "b text");

        IReadOnlyList<CorpusSegment> segments = LocalCorpus.Load(_dir, subset: null, seed: 1);

        Assert.Equal(2, segments.Count);
        Assert.Equal("a_PI", segments[0].Id);
        Assert.Equal(CorpusRole.Pilot, segments[0].Role);
        Assert.Equal(1600, segments[0].Samples16k.Length);
        Assert.Equal("hello world", segments[0].Reference);
        Assert.Equal("b", segments[1].Id);
        Assert.Equal(CorpusRole.Unknown, segments[1].Role);
        Assert.Equal(400, segments[1].Samples16k.Length);
    }

    [Fact]
    public void Load_Throws_When_A_Wav_Has_No_Transcript()
    {
        WritePair("a_PI", 800, 8000, "hello world");
        WritePair("b", 400, 16000, "b text");
        WriteWav("c", 400, 16000);

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(() => LocalCorpus.Load(_dir, subset: null, seed: 1));

        Assert.Contains("c.txt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_Is_Stable_And_Follows_The_Transcripts()
    {
        WritePair("a_PI", 800, 8000, "hello world");
        WritePair("b", 400, 16000, "b text");

        string first = LocalCorpus.Fingerprint(_dir);
        string second = LocalCorpus.Fingerprint(_dir);
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b changed");
        string changed = LocalCorpus.Fingerprint(_dir);

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void Fingerprint_Follows_The_Wav_Bytes()
    {
        WritePair("a_PI", 800, 8000, "hello world");
        WritePair("b", 400, 16000, "b text");

        string before = LocalCorpus.Fingerprint(_dir);
        WriteWav("b", 401, 16000);
        string after = LocalCorpus.Fingerprint(_dir);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Load_Throws_On_A_Directory_Without_Wavs() => Assert.Throws<InvalidDataException>(() => LocalCorpus.Load(_dir, subset: null, seed: 1));

    private void WritePair(string id, int samples, int sampleRate, string transcript)
    {
        WriteWav(id, samples, sampleRate);
        File.WriteAllText(Path.Combine(_dir, id + ".txt"), transcript);
    }

    private void WriteWav(string id, int samples, int sampleRate) =>
        File.WriteAllBytes(Path.Combine(_dir, id + ".wav"), WavHeader.WritePcm16(new float[samples], sampleRate).ToArray());
}
