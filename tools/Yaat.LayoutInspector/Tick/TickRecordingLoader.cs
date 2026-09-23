using System.Text.Json;

namespace Yaat.LayoutInspector.Tick;

/// <summary>Why a <c>--ticks</c> source produced no recording.</summary>
public enum TickSourceIssue
{
    /// <summary>The path does not exist.</summary>
    FileNotFound,

    /// <summary>The file is empty, malformed, or not a tick recording.</summary>
    EmptyOrUnreadable,
}

/// <summary>A <c>--ticks</c> source that produced no recording, and why.</summary>
public sealed record TickSourceProblem(string Path, TickSourceIssue Issue);

/// <summary>A <c>--ticks</c> source that produced a recording, with its optional label.</summary>
public sealed record LoadedTickSource(string? Label, string Path, TickRecording Recording);

/// <summary>Outcome of reading every <c>--ticks</c> source: what loaded, and what did not.</summary>
public sealed record TickSourceRead(List<LoadedTickSource> Loaded, List<TickSourceProblem> Problems);

/// <summary>
/// Reads every <c>--ticks</c> source. One missing, empty or malformed file never hides the
/// others — it becomes a <see cref="TickSourceProblem"/> the caller reports in its own words.
/// A recording with an unsupported schema version still throws, from <see cref="TickJsonReader"/>.
/// </summary>
public static class TickRecordingLoader
{
    public static TickSourceRead ReadAll(IReadOnlyList<(string? Label, string Path)> sources)
    {
        var loaded = new List<LoadedTickSource>();
        var problems = new List<TickSourceProblem>();
        foreach ((string? label, string path) in sources)
        {
            if (!File.Exists(path))
            {
                problems.Add(new TickSourceProblem(path, TickSourceIssue.FileNotFound));
                continue;
            }

            TickRecording? recording = Read(path);
            if (recording is null)
            {
                problems.Add(new TickSourceProblem(path, TickSourceIssue.EmptyOrUnreadable));
                continue;
            }

            loaded.Add(new LoadedTickSource(label, path, recording));
        }

        return new TickSourceRead(loaded, problems);
    }

    private static TickRecording? Read(string path)
    {
        try
        {
            return TickJsonReader.Read(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
