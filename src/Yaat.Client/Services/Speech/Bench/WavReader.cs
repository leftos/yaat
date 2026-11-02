using System.Text;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>Reads the bench clips: 16 kHz mono 16-bit PCM WAV files, the format the engines transcribe.</summary>
public static class WavReader
{
    private const int SampleRate = 16000;
    private const int PcmFormat = 1;

    /// <summary>The file's samples as Float32 in [-1, 1).</summary>
    /// <param name="path">The WAV file.</param>
    /// <exception cref="InvalidDataException">The file is not a 16 kHz mono 16-bit PCM WAV; the message names it and what it found.</exception>
    public static float[] ReadMono16k(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        CheckRiffWave(path, bytes);

        WavFormat? format = null;
        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            (string id, int body, int size) = ChunkAt(path, bytes, offset);
            if (id == "fmt ")
            {
                format = ReadFormat(path, bytes, body, size);
            }
            else if (id == "data")
            {
                return Decode(path, format, bytes, body, size);
            }
            offset = body + size + (size % 2);
        }
        throw new InvalidDataException($"{path} has no data chunk");
    }

    private static void CheckRiffWave(string path, byte[] bytes)
    {
        if ((bytes.Length < 12) || (Tag(bytes, 0) != "RIFF") || (Tag(bytes, 8) != "WAVE"))
        {
            throw new InvalidDataException($"{path} is not a RIFF WAVE file");
        }
    }

    /// <summary>The chunk header at <paramref name="offset"/>: its id, where its body starts and how long it is.</summary>
    private static (string Id, int Body, int Size) ChunkAt(string path, byte[] bytes, int offset)
    {
        string id = Tag(bytes, offset);
        int size = BitConverter.ToInt32(bytes, offset + 4);
        int body = offset + 8;
        if ((size < 0) || (size > bytes.Length - body))
        {
            throw new InvalidDataException($"{path} has a truncated '{id}' chunk");
        }
        return (id, body, size);
    }

    private static WavFormat ReadFormat(string path, byte[] bytes, int body, int size)
    {
        if (size < 16)
        {
            throw new InvalidDataException($"{path} has a {size}-byte fmt chunk, shorter than PCM's 16");
        }
        return new WavFormat(
            BitConverter.ToInt16(bytes, body),
            BitConverter.ToInt16(bytes, body + 2),
            BitConverter.ToInt32(bytes, body + 4),
            BitConverter.ToInt16(bytes, body + 14)
        );
    }

    private static float[] Decode(string path, WavFormat? format, byte[] bytes, int body, int size)
    {
        if (format is null)
        {
            throw new InvalidDataException($"{path} has its data chunk before any fmt chunk");
        }
        if ((format.AudioFormat != PcmFormat) || (format.Channels != 1) || (format.SampleRate != SampleRate) || (format.BitsPerSample != 16))
        {
            throw new InvalidDataException(
                $"{path} must be 16-bit PCM, mono, 16000 Hz; found format {format.AudioFormat}, {format.Channels} channels, "
                    + $"{format.SampleRate} Hz, {format.BitsPerSample}-bit"
            );
        }

        float[] samples = new float[size / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, body + (i * 2)) / 32768f;
        }
        return samples;
    }

    private static string Tag(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);

    private sealed record WavFormat(short AudioFormat, short Channels, int SampleRate, short BitsPerSample);
}
