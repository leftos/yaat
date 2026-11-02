using System.Text;
using Xunit;
using Yaat.Client.Services.Speech.Bench;

namespace Yaat.Client.Tests;

/// <summary>Reading the committed bench clips, and refusing any WAV that is not 16 kHz mono 16-bit PCM.</summary>
public sealed class WavReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaat-tests", "wav-reader-" + Guid.NewGuid().ToString("N"));

    public WavReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_Committed_Clip_Decodes_To_A_Plausible_Length()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Speech", "BenchClips", "clip-01.wav");

        float[] samples = WavReader.ReadMono16k(path);

        double seconds = samples.Length / 16000.0;
        Assert.InRange(seconds, 0.5, 10);
        Assert.Contains(samples, s => s != 0);
        Assert.All(samples, s => Assert.InRange(s, -1f, 1f));
    }

    [Fact]
    public void A_File_Written_In_The_Accepted_Format_Decodes_Its_Samples()
    {
        string path = WriteWav("ok.wav", channels: 1, sampleRate: 16000, bitsPerSample: 16, [0, 16384, -32768]);

        float[] samples = WavReader.ReadMono16k(path);

        Assert.Equal([0f, 0.5f, -1f], samples);
    }

    [Fact]
    public void A_Stereo_File_Is_Refused()
    {
        string path = WriteWav("stereo.wav", channels: 2, sampleRate: 16000, bitsPerSample: 16, [0, 0, 0, 0]);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => WavReader.ReadMono16k(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 channels", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_44_1_kHz_File_Is_Refused()
    {
        string path = WriteWav("cd.wav", channels: 1, sampleRate: 44100, bitsPerSample: 16, [0, 0]);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => WavReader.ReadMono16k(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("44100 Hz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_File_That_Is_Not_A_WAV_Is_Refused()
    {
        string path = Path.Combine(_dir, "text.wav");
        File.WriteAllText(path, "not a wave file at all, just text");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => WavReader.ReadMono16k(path));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    private string WriteWav(string name, short channels, int sampleRate, short bitsPerSample, short[] samples)
    {
        string path = Path.Combine(_dir, name);
        using var writer = new BinaryWriter(File.Create(path));
        int dataBytes = samples.Length * 2;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }
        return path;
    }
}
