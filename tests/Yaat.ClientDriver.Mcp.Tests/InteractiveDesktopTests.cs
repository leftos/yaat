using System.Diagnostics;
using Xunit;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The no-desktop decision, covered through <see cref="InteractiveDesktop.Describe"/>: a real headless run (session 0, a
/// service, <c>psexec -s</c>, a locked or disconnected session) cannot be automated on this machine, so the pure decision
/// stands in for it. The one case that needs a real desktop is the Desktop-trait fact below.
/// </summary>
public sealed class InteractiveDesktopTests
{
    [Theory]
    [InlineData(false, 1u, true, true, "The Desktop tests drive Windows windows; this is not Windows.")]
    [InlineData(true, 0u, true, true, "No interactive desktop: the process runs in session 0 (a service session).")]
    [InlineData(true, 3u, true, false, "No interactive desktop: the process's window station is not visible.")]
    [InlineData(true, 3u, false, true, "No interactive desktop: no input desktop can be opened (locked, disconnected or headless session).")]
    [InlineData(true, 3u, true, true, null)]
    public void Describe_ReportsTheMissingFact(bool isWindows, uint sessionId, bool hasInputDesktop, bool windowStationVisible, string? expected) =>
        Assert.Equal(expected, InteractiveDesktop.Describe(isWindows, sessionId, hasInputDesktop, windowStationVisible));

    [Fact]
    [Trait("Category", "Desktop")]
    public void MissingReason_AtThisDesktop_ReportsADesktop()
    {
        using var process = Process.GetCurrentProcess();
        uint sessionId = (uint)process.SessionId;
        string? headless = InteractiveDesktop.Describe(OperatingSystem.IsWindows(), sessionId, hasInputDesktop: true, windowStationVisible: true);
        Assert.SkipWhen(headless is not null, headless ?? "");
        Assert.Null(InteractiveDesktop.MissingReason());
    }
}
