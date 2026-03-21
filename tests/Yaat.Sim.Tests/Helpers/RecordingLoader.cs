using System.IO.Compression;
using System.Text.Json;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

public static class RecordingLoader
{
    public static SessionRecording? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string json;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry =
                archive.GetEntry("recording.yaat-recording.br")
                ?? archive.GetEntry("recording.yaat-recording.json.gz")
                ?? archive.GetEntry("recording.yaat-recording.json");
            if (entry is null)
            {
                return null;
            }

            json = RecordingCompression.Decompress(entry.Open());
        }
        else
        {
            json = RecordingCompression.Decompress(File.ReadAllBytes(path));
        }

        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
