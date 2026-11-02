using System.Text.Json;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>One bench clip: its id, the words it says and its 16 kHz mono samples.</summary>
/// <param name="Id">The clip's id, also its WAV file's name without the extension.</param>
/// <param name="Expected">The words the clip says.</param>
/// <param name="Samples">The clip's audio, 16 kHz mono Float32.</param>
public sealed record BenchClip(string Id, string Expected, float[] Samples);

/// <summary>One line of <c>clips.json</c>: a clip's id and the words it says.</summary>
/// <param name="Id">The clip's id.</param>
/// <param name="Expected">The words the clip says.</param>
public sealed record BenchClipEntry(string Id, string Expected);

/// <summary>The committed bench clip set: <c>clips.json</c> beside one <c>&lt;id&gt;.wav</c> per entry.</summary>
public static class BenchClips
{
    /// <summary>The clip list's file name inside the clips folder.</summary>
    public const string ManifestFileName = "clips.json";

    /// <summary>Every clip <c>clips.json</c> lists, in its order, with its audio read.</summary>
    /// <param name="directory">The clips folder.</param>
    /// <exception cref="FileNotFoundException">The folder has no <c>clips.json</c>, or a listed clip's WAV is missing.</exception>
    /// <exception cref="InvalidDataException"><c>clips.json</c> is malformed, or a WAV is not 16 kHz mono 16-bit PCM.</exception>
    public static IReadOnlyList<BenchClip> Load(string directory)
    {
        string manifestPath = Path.Combine(directory, ManifestFileName);
        List<BenchClipEntry> entries = ReadManifest(manifestPath);
        return [.. entries.Select(e => new BenchClip(e.Id, e.Expected, WavReader.ReadMono16k(Path.Combine(directory, e.Id + ".wav"))))];
    }

    private static List<BenchClipEntry> ReadManifest(string manifestPath)
    {
        List<BenchClipEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), SttBenchJsonContext.Default.ListBenchClipEntry);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{manifestPath} is not a valid clip list: {ex.Message}", ex);
        }
        if ((entries is null) || entries.Any(e => string.IsNullOrEmpty(e.Id) || string.IsNullOrEmpty(e.Expected)))
        {
            throw new InvalidDataException($"{manifestPath} must be a list of {{\"id\", \"expected\"}} entries, each with both set");
        }
        return entries;
    }
}
