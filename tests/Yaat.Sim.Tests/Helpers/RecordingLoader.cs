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

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFromZip(path);
        }

        // Non-ZIP: Brotli / gzip / plain JSON (v1/v2 legacy)
        var bytes = File.ReadAllBytes(path);

        // Could be a v3 ZIP without .zip extension
        if (RecordingCompression.IsZipArchive(bytes))
        {
            using var ms = new MemoryStream(bytes);
            return LoadFromZipStream(ms);
        }

        var json = RecordingCompression.Decompress(bytes);
        return JsonSerializer.Deserialize<SessionRecording>(json, RecordingJsonOptions.Default);
    }

    /// <summary>
    /// Open a v3 archive without materializing all snapshots. Returns a disposable reader
    /// that loads snapshots on demand. Returns null if the file is missing or not a v3 archive.
    /// </summary>
    public static RecordingArchive? OpenArchive(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return RecordingArchive.Open(path);
        }
        catch (InvalidOperationException)
        {
            // Not a v3 archive (missing manifest.json)
            return null;
        }
    }

    private static SessionRecording? LoadFromZip(string path)
    {
        // Peek at the ZIP to check for v3 manifest
        using var zip = ZipFile.OpenRead(path);

        if (zip.GetEntry("manifest.json") is not null)
        {
            // v3 archive — use RecordingArchive reader
            zip.Dispose();
            using var archive = RecordingArchive.Open(path);
            return archive.ToSessionRecording();
        }

        // Legacy bug-report bundle
        var entry =
            zip.GetEntry("recording.yaat-recording.br")
            ?? zip.GetEntry("recording.yaat-recording.json.gz")
            ?? zip.GetEntry("recording.yaat-recording.json");
        if (entry is null)
        {
            return null;
        }

        var json = RecordingCompression.Decompress(entry.Open());
        return JsonSerializer.Deserialize<SessionRecording>(json, RecordingJsonOptions.Default);
    }

    private static SessionRecording? LoadFromZipStream(MemoryStream ms)
    {
        using var archive = RecordingArchive.Open(ms);
        return archive.ToSessionRecording();
    }
}
