using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Client.Logging;

namespace Yaat.Client.Tests;

/// <summary>
/// The log must survive a relaunch. It previously opened with <c>FileMode.Create</c>, so a user who
/// hit a freeze or crash and then reopened YAAT to collect their log destroyed the only record of the
/// failure before anyone could read it — which is exactly what happened while diagnosing the Settings
/// deadlock. Each launch now rolls the previous session aside instead.
/// </summary>
public class FileLoggerRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaat-log-rotation-tests", Guid.NewGuid().ToString("N"));

    public FileLoggerRotationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string LogPath => Path.Combine(_dir, "yaat-client.log");

    private void WriteSession(string marker)
    {
        using var provider = new FileLoggerProvider(LogPath);
        provider.CreateLogger("Test").LogInformation("{Marker}", marker);
    }

    [Fact]
    public void PreviousSessionIsPreservedAcrossRelaunch()
    {
        WriteSession("first-session");
        WriteSession("second-session");

        Assert.Contains("second-session", File.ReadAllText(LogPath), StringComparison.Ordinal);
        Assert.Contains("first-session", File.ReadAllText($"{LogPath}.1"), StringComparison.Ordinal);
    }

    [Fact]
    public void OlderSessionsShiftDownAndTheOldestIsDropped()
    {
        // Four launches past the first fill .1 / .2 / .3; the original must fall off the end.
        foreach (var marker in new[] { "oldest", "third", "second", "newest-previous", "current" })
        {
            WriteSession(marker);
        }

        Assert.Contains("current", File.ReadAllText(LogPath), StringComparison.Ordinal);
        Assert.Contains("newest-previous", File.ReadAllText($"{LogPath}.1"), StringComparison.Ordinal);
        Assert.Contains("second", File.ReadAllText($"{LogPath}.2"), StringComparison.Ordinal);
        Assert.Contains("third", File.ReadAllText($"{LogPath}.3"), StringComparison.Ordinal);
        Assert.False(File.Exists($"{LogPath}.4"), "rotation must not keep more than three previous logs");
    }

    [Fact]
    public void FirstLaunchWithNoExistingLogDoesNotCreateRotatedFiles()
    {
        WriteSession("only-session");

        Assert.True(File.Exists(LogPath));
        Assert.False(File.Exists($"{LogPath}.1"));
    }
}
