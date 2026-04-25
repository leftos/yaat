using System.Text.Json;

namespace Yaat.LayoutInspector.Tick;

/// <summary>
/// Reads <c>TickRecorder</c> JSON files into <see cref="TickRecording"/>.
/// </summary>
public static class TickJsonReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Parse the JSON file at <paramref name="path"/>. Returns null if the
    /// file is empty or malformed. Throws <see cref="InvalidDataException"/>
    /// when the schema version is unsupported.
    /// </summary>
    public static TickRecording? Read(string path)
    {
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var recording = JsonSerializer.Deserialize<TickRecording>(json, Options);
        if (recording is null)
        {
            return null;
        }

        if (recording.Version != TickRecording.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported tick recording schema version {recording.Version}; this build expects {TickRecording.CurrentVersion}"
            );
        }

        return recording;
    }
}
