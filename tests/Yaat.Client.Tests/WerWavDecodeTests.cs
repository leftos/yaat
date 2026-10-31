using System.Buffers.Binary;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The rate-aware half of <see cref="WavHeader"/>: a decoded WAV reports the sample rate its header
/// declares, tolerates the extra chunks real encoders write between <c>fmt </c> and <c>data</c>, and
/// still refuses anything that is not mono 16-bit PCM.
/// </summary>
public sealed class WerWavDecodeTests
{
    private const int SampleCount = 800;

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void ReadPcm16WithRate_Reports_The_Declared_Rate_And_Round_Trips_Samples(int sampleRate)
    {
        float[] samples = BuildSamples(SampleCount);
        byte[] wav = Encode(samples, sampleRate);

        Pcm16Audio audio = WavHeader.ReadPcm16WithRate(wav, "test");

        Assert.Equal(sampleRate, audio.SampleRate);
        Assert.Equal(SampleCount, audio.Samples.Length);
        for (int i = 0; i < SampleCount; i++)
        {
            Assert.Equal(samples[i], audio.Samples[i], 1.0f / short.MaxValue);
        }
    }

    [Fact]
    public void ReadPcm16WithRate_Skips_A_List_Chunk_Between_Fmt_And_Data()
    {
        const int insertAt = 36;
        const int insertLength = 12;
        byte[] wav = Encode(BuildSamples(SampleCount), 16000);

        byte[] spliced = new byte[wav.Length + insertLength];
        Array.Copy(wav, 0, spliced, 0, insertAt);
        "LIST"u8.ToArray().CopyTo(spliced, insertAt);
        BinaryPrimitives.WriteInt32LittleEndian(spliced.AsSpan(insertAt + 4, 4), 4);
        Array.Copy(wav, insertAt, spliced, insertAt + insertLength, wav.Length - insertAt);
        int riffSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4, 4));
        BinaryPrimitives.WriteInt32LittleEndian(spliced.AsSpan(4, 4), riffSize + insertLength);

        Pcm16Audio audio = WavHeader.ReadPcm16WithRate(spliced, "test");

        Assert.Equal(16000, audio.SampleRate);
        Assert.Equal(SampleCount, audio.Samples.Length);
    }

    [Fact]
    public void ReadPcm16WithRate_Throws_On_A_Stereo_Header()
    {
        byte[] wav = Encode(BuildSamples(SampleCount), 8000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), 2);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => WavHeader.ReadPcm16WithRate(wav, "test"));

        Assert.Contains("test", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPcm16WithRate_Throws_On_Bytes_That_Are_Not_Riff()
    {
        byte[] notWav = [.. "this is not a wav file at all, only text"u8];

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => WavHeader.ReadPcm16WithRate(notWav, "text-blob"));

        Assert.Contains("text-blob", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPcm16WithRate_Throws_On_A_Wav_Without_A_Data_Chunk()
    {
        const int headerBeforeData = 36;
        byte[] wav = Encode(BuildSamples(SampleCount), 16000)[..headerBeforeData];
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), headerBeforeData - 8);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => WavHeader.ReadPcm16WithRate(wav, "no-data"));

        Assert.Contains("no-data", ex.Message, StringComparison.Ordinal);
    }

    private static byte[] Encode(float[] samples, int sampleRate)
    {
        using MemoryStream stream = WavHeader.WritePcm16(samples, sampleRate);
        return stream.ToArray();
    }

    private static float[] BuildSamples(int count)
    {
        float[] samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = (float)Math.Sin(i * 0.05) * 0.8f;
        }
        return samples;
    }
}
