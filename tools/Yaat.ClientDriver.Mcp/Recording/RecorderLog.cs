using System.Globalization;
using System.Text.RegularExpressions;

namespace Yaat.ClientDriver.Mcp.Recording;

/// <summary>What the recorder's end line reports: the frames it wrote and the seconds from the first of them to the end.</summary>
/// <param name="Frames">The frames written.</param>
/// <param name="Seconds">The seconds from the first frame to the end.</param>
public sealed record RecorderEnd(int Frames, double Seconds);

/// <summary>
/// Reads the window recorder's stderr, which the pipeline redirects to <c>&lt;clip&gt;-recorder.log</c>: the instant of the
/// first frame (the clip's zero), the end line's frame count and length, and the last lines a failure is explained by.
/// </summary>
public static partial class RecorderLog
{
    private const string Prefix = "Yaat.WindowRecorder: ";

    /// <summary>The line the recorder writes when its first frame goes out, at <paramref name="utc"/>.</summary>
    /// <param name="utc">The instant of the first frame.</param>
    public static string FirstFrameLine(DateTime utc) =>
        $"{Prefix}first frame at {utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)}";

    /// <summary>The log's lines, read while the recorder may still be writing it; empty when it does not exist yet.</summary>
    /// <param name="path">The recorder log.</param>
    public static IReadOnlyList<string> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>The instant of the first frame, or null before the recorder has written one.</summary>
    /// <param name="lines">The log's lines.</param>
    public static DateTime? FirstFrameUtc(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Match match = FirstFramePattern().Match(line);
            if (
                match.Success
                && DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime instant)
            )
            {
                return instant.ToUniversalTime();
            }
        }

        return null;
    }

    /// <summary>The end line's frames and seconds, or null when the recorder has not ended (or died before it could say).</summary>
    /// <param name="lines">The log's lines.</param>
    public static RecorderEnd? End(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Match match = EndPattern().Match(line);
            if (match.Success)
            {
                return new RecorderEnd(
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
                );
            }
        }

        return null;
    }

    /// <summary>The last <paramref name="count"/> non-blank lines, oldest first.</summary>
    /// <param name="lines">The log's lines.</param>
    /// <param name="count">How many to keep.</param>
    public static IReadOnlyList<string> Tail(IReadOnlyList<string> lines, int count) =>
        [.. lines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(count)];

    [GeneratedRegex(@"^Yaat\.WindowRecorder: first frame at (\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FirstFramePattern();

    [GeneratedRegex(@"^Yaat\.WindowRecorder: (\d+) frames in (\d+(?:\.\d+)?) s ", RegexOptions.CultureInvariant)]
    private static partial Regex EndPattern();
}
