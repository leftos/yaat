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
            var entry = archive.GetEntry("recording.yaat-recording.json");
            if (entry is null)
            {
                return null;
            }

            using var reader = new StreamReader(entry.Open());
            json = reader.ReadToEnd();
        }
        else
        {
            json = File.ReadAllText(path);
        }

        return JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
