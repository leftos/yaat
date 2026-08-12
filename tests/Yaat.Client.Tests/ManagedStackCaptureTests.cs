using System;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Exercises the ClrMD snapshot self-attach that <see cref="UiThreadWatchdog"/> uses to log every
/// thread's managed stack on a hard UI freeze (GitHub #347). Runs the real capture against the
/// test process itself — a stub would not catch a broken DAC load or snapshot-attach regression.
/// </summary>
public class ManagedStackCaptureTests
{
    [Fact]
    public void CaptureListsRequestedThreadFirstWithUiMarker()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Snapshot self-attach (PssCaptureSnapshot) is Windows-only.");

        string? stacks = ManagedStackCapture.TryCaptureAllThreads(Environment.CurrentManagedThreadId);

        Assert.NotNull(stacks);
        Assert.StartsWith($"--- Thread managedId={Environment.CurrentManagedThreadId} ", stacks);
        Assert.Contains("[UI THREAD]", stacks);
        Assert.Contains("    at ", stacks);
    }

    [Fact]
    public void CaptureWithUnknownUiThreadStillListsStacks()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Snapshot self-attach (PssCaptureSnapshot) is Windows-only.");

        string? stacks = ManagedStackCapture.TryCaptureAllThreads(-1);

        Assert.NotNull(stacks);
        Assert.DoesNotContain("[UI THREAD]", stacks);
        Assert.Contains("--- Thread managedId=", stacks);
        Assert.Contains("    at ", stacks);
    }
}
