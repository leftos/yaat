extern alias mcp;

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The MCP's pipe client and pipe directory against a real <see cref="AutomationHost"/> started in this process: a
/// request/response round trip, a coded remote error, the reconnect after the host stops, and the discovery-directory
/// lookup that decides whether a pid is driven over the pipe or over UI Automation.
/// </summary>
public sealed class PipeClientTests : AutomationHostFixture
{
    [AvaloniaFact]
    public async Task PipeClient_Ping_ReturnsPidAndVersion()
    {
        using AutomationHost host = StartHost(() => Windows);
        await using var client = new PipeClient(PipeName, null, NullLogger<PipeClient>.Instance);

        PingResult ping = await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None);

        Assert.Equal(Environment.ProcessId, ping.Pid);
        Assert.Equal(ProtocolVersion.Current, ping.ProtocolVersion);
    }

    [AvaloniaFact]
    public async Task PipeClient_ListWindows_ReturnsTheRootsWindows()
    {
        ShowWindow("AutomationRoot", Pad(), null);
        using AutomationHost host = StartHost(() => Windows);
        await using var client = new PipeClient(PipeName, null, NullLogger<PipeClient>.Instance);

        List<WindowInfo> windows = await client.SendAsync<List<WindowInfo>>(
            ProtocolMethods.ListWindows,
            null,
            PipeClient.RequestTimeout,
            CancellationToken.None
        );

        Assert.Contains(windows, window => window.Title == "AutomationRoot");
    }

    [AvaloniaFact]
    public async Task PipeClient_RemoteError_MapsCodeMessageAndHint()
    {
        using AutomationHost host = StartHost(() => Windows);
        await using var client = new PipeClient(PipeName, null, NullLogger<PipeClient>.Instance);

        PipeRemoteException error = await Assert.ThrowsAsync<PipeRemoteException>(() =>
            client.SendAsync<PingResult>("no_such_method", null, PipeClient.RequestTimeout, CancellationToken.None)
        );

        Assert.Equal(AutomationErrorCodes.InvalidParam, error.Code);
        Assert.Contains("Hint:", error.Message, StringComparison.Ordinal);
        Assert.Contains(ProtocolMethods.Ping, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Suggested);
        Assert.NotEmpty(error.RemoteMessage);
        Assert.DoesNotContain(" Hint: ", error.RemoteMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task PipeClient_DisposedDuringAnInFlightRequest_ReportsTheRequestFailure()
    {
        using AutomationHost host = StartHost(() => Windows);
        var client = new PipeClient(PipeName, null, NullLogger<PipeClient>.Instance);
        ShowWindow("AutomationRoot", Pad(), null);

        // A request the host will not answer soon: the wait_for polls to its timeout, so the send stays in flight.
        Task<WaitForResult> pending = client.SendAsync<WaitForResult>(
            ProtocolMethods.WaitFor,
            CountEqualsParams(),
            PipeClient.RequestTimeout,
            CancellationToken.None
        );
        ValueTask disposal = client.DisposeAsync();
        host.Dispose();

        McpException failure = await Assert.ThrowsAsync<McpException>(() => pending);
        Assert.Contains(PipeName, failure.Message, StringComparison.Ordinal);
        await disposal;
    }

    [AvaloniaFact]
    public async Task PipeClient_CancelledMidRequest_ReconnectsOnTheNextCall()
    {
        using AutomationHost host = StartHost(() => Windows);
        await using var client = new PipeClient(PipeName, null, NullLogger<PipeClient>.Instance);
        ShowWindow("AutomationRoot", Pad(), null);
        using var cancellation = new CancellationTokenSource();
        // Open the connection first, so the cancel lands on a request already on the wire rather than on the connect.
        _ = await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None);

        Task<WaitForResult> pending = client.SendAsync<WaitForResult>(
            ProtocolMethods.WaitFor,
            CountEqualsParams(),
            PipeClient.RequestTimeout,
            cancellation.Token
        );
        // The request is answered only when the wait_for's polls run out; the cancel lands well inside that window.
        await Task.Delay(300);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // The host's late answer to the cancelled request would desync a reused connection; this one was dropped.
        PingResult ping = await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None);
        Assert.Equal(Environment.ProcessId, ping.Pid);
    }

    [AvaloniaFact]
    public async Task PipeClient_HostStopped_ThrowsThenReconnects()
    {
        using AutomationHost host = StartHost(() => Windows);
        await using var client = new PipeClient(PipeName, null, NullLogger<PipeClient>.Instance);
        _ = await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None);

        host.Dispose();
        McpException closed = await Assert.ThrowsAsync<McpException>(() =>
            client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None)
        );
        Assert.Contains(PipeName, closed.Message, StringComparison.Ordinal);

        using AutomationHost restarted = StartHost(() => Windows);
        PingResult ping = await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None);
        Assert.Equal(Environment.ProcessId, ping.Pid);
    }

    [AvaloniaFact]
    public async Task PipeDirectory_LiveDiscoveryFile_ReturnsAPingedClient()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory(DiscoveryDirectory, CurrentProcessName());

        PipeClient? client = await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None);

        Assert.NotNull(client);
        PingResult ping = await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None);
        Assert.Equal(Environment.ProcessId, ping.Pid);
        await directory.ForgetAsync(Environment.ProcessId);
    }

    [AvaloniaFact]
    public async Task PipeDirectory_NoFile_ReturnsNull()
    {
        PipeDirectory directory = NewPipeDirectory(DiscoveryDirectory, CurrentProcessName());

        Assert.Null(await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task PipeDirectory_WrongProcessName_ReturnsNull()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory(DiscoveryDirectory, "NotTheClient");

        Assert.Null(await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task PipeDirectory_UnparsableFile_ReturnsNull()
    {
        Directory.CreateDirectory(DiscoveryDirectory);
        await File.WriteAllTextAsync(Path.Combine(DiscoveryDirectory, $"{Environment.ProcessId}.json"), "this is not JSON");
        PipeDirectory directory = NewPipeDirectory(DiscoveryDirectory, CurrentProcessName());

        Assert.Null(await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task PipeDirectory_DeadPid_ReturnsNull()
    {
        const int deadPid = 99999999;
        WriteDiscoveryFile(deadPid, PipeDirectory.ClientProcessName);
        PipeDirectory directory = NewPipeDirectory(DiscoveryDirectory, PipeDirectory.ClientProcessName);

        Assert.Null(await directory.TryGetAsync(deadPid, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task PipeDirectory_CachesOneClientPerPid()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory(DiscoveryDirectory, CurrentProcessName());

        PipeClient? first = await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None);
        PipeClient? second = await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        await directory.ForgetAsync(Environment.ProcessId);
    }

    [AvaloniaFact]
    public async Task PipeDirectory_Forget_DisposesTheClient()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory(DiscoveryDirectory, CurrentProcessName());
        PipeClient? client = await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None);
        Assert.NotNull(client);

        await directory.ForgetAsync(Environment.ProcessId);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None)
        );
        PipeClient? rebuilt = await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None);
        Assert.NotNull(rebuilt);
        Assert.NotSame(client, rebuilt);
        await directory.ForgetAsync(Environment.ProcessId);
    }

    [AvaloniaFact]
    public async Task PipeDirectory_CachedClient_IsDroppedWhenItsProcessIsGone()
    {
        using AutomationHost host = StartHost(() => Windows);
        // A long-lived child: the first lookup must not race its exit, and killing it stands in for a client that stops.
        using Process child =
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("cmd.exe did not start");
        try
        {
            int childPid = child.Id;
            WriteDiscoveryFile(childPid, "cmd", PipeName);
            PipeDirectory directory = NewPipeDirectory(DiscoveryDirectory, "cmd");

            PipeClient? cached = await directory.TryGetAsync(childPid, CancellationToken.None);
            Assert.NotNull(cached);

            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(20000), "the child process did not exit");
            PipeClient? afterExit = await directory.TryGetAsync(childPid, CancellationToken.None);

            Assert.Null(afterExit);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                cached.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None)
            );
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(20000);
            }
        }
    }

    private static string CurrentProcessName() => Process.GetCurrentProcess().ProcessName;

    private static PipeDirectory NewPipeDirectory(string discoveryDirectory, string expectedProcessName) =>
        new(discoveryDirectory, expectedProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);

    // Params of a wait_for that polls its whole timeout: the window count never reaches 9999.
    private static object CountEqualsParams() =>
        new
        {
            selector = "Window",
            condition = "count_equals",
            count = 9999,
            timeoutMs = 10000,
        };

    private void WriteDiscoveryFile(int pid, string processName) => WriteDiscoveryFile(pid, processName, $"yaat-automation-{pid}");

    private void WriteDiscoveryFile(int pid, string processName, string pipeName)
    {
        Directory.CreateDirectory(DiscoveryDirectory);
        var info = new DiscoveryInfo
        {
            Pid = pid,
            PipeName = pipeName,
            ProcessName = processName,
            StartTime = DateTimeOffset.UtcNow,
            ProtocolVersion = ProtocolVersion.Current,
        };
        File.WriteAllText(Path.Combine(DiscoveryDirectory, $"{pid}.json"), ProtocolSerializer.Serialize(info));
    }
}
