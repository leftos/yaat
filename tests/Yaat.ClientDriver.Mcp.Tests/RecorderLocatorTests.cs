extern alias mcp;

using System.Diagnostics;
using mcp::Yaat.ClientDriver.Mcp.Recording;
using Xunit;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The window recorder travels with the server's build output: the build copies it under <c>recorder/</c>, and that copy
/// is a runnable exe, not a stray dll. A folder without it is refused with the project to rebuild.
/// </summary>
public sealed class RecorderLocatorTests
{
    [Fact]
    public async Task FindRecorder_PointsAtTheCopiedHelper()
    {
        string path = RecorderLocator.FindRecorder();

        Assert.True(File.Exists(path), $"no recorder at {path}");
        Assert.EndsWith(Path.Combine("recorder", "Yaat.WindowRecorder.exe"), path, StringComparison.Ordinal);

        ProcessStartInfo start = new(path, "--help") { RedirectStandardError = true, RedirectStandardOutput = true };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{path} did not start");
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            string usage = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);
            Assert.StartsWith("usage: Yaat.WindowRecorder", usage, StringComparison.Ordinal);
            Assert.Equal("", await stdout);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void FindRecorder_EmptyFolder_ThrowsNamingTheMcpProject()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"yaat-recorder-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => RecorderLocator.FindRecorder(folder));

            Assert.Contains(Path.Combine(folder, "recorder", "Yaat.WindowRecorder.exe"), failure.Message, StringComparison.Ordinal);
            Assert.Contains("Rebuild tools/Yaat.ClientDriver.Mcp", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }
}
