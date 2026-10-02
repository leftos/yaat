extern alias mcp;

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using mcp::Yaat.ClientDriver.Mcp;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;
using McpWindowInfo = mcp::Yaat.Client.Automation.Protocol.WindowInfo;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// <see cref="PipeCalls"/> against a real <see cref="AutomationHost"/> in this process: a host stale-node error reads as
/// the shared gone message, any other coded error keeps the host's own text, a closed pipe reads as gone, and the pid
/// routing decision returns the client or null.
/// </summary>
public sealed class PipeCallsTests : AutomationHostFixture
{
    [AvaloniaFact]
    public async Task SendForElement_StaleNode_ReadsElementIsGone()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                PipeCalls.SendForElementAsync<JsonElement>(
                    directory,
                    Element("e7", 4242),
                    ProtocolMethods.GetTree,
                    new { nodeId = 4242 },
                    CancellationToken.None
                )
            );

            Assert.Equal(
                "Element 'e7' is gone — its window or process has exited; call list_windows or find_elements again for a fresh id",
                failure.Message
            );
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
        }
    }

    [AvaloniaFact]
    public async Task SendForElement_OtherRemoteError_KeepsTheHostMessage()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                PipeCalls.SendForElementAsync<JsonElement>(
                    directory,
                    Element("e7", 4242),
                    ProtocolMethods.GetTree,
                    new { nodeId = 4242, selector = "Window" },
                    CancellationToken.None
                )
            );

            Assert.StartsWith($"{AutomationErrorCodes.InvalidParam}:", failure.Message, StringComparison.Ordinal);
            Assert.Contains("not both", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
        }
    }

    [AvaloniaFact]
    public async Task SendForElement_ForgottenClient_ReadsPipeClosed()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            PipeClient cached = (await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None))!;

            // Another thread forgetting the pid between this lookup and the send disposes the client the directory still
            // hands out, which is what a send from a stale id sees.
            await cached.DisposeAsync();

            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                PipeCalls.SendForElementAsync<JsonElement>(
                    directory,
                    Element("e3", 1),
                    ProtocolMethods.GetTree,
                    new { nodeId = 1 },
                    CancellationToken.None
                )
            );

            Assert.Equal(
                $"Element 'e3' is gone — the automation pipe for pid {Environment.ProcessId} closed; call list_windows again for a fresh id",
                failure.Message
            );
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
        }
    }

    [Fact]
    public async Task SendForElement_PidWithoutAPipe_ReadsPipeClosed()
    {
        PipeDirectory directory = NewPipeDirectory();

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            PipeCalls.SendForElementAsync<JsonElement>(
                directory,
                new PipeElement("e9", new PipeNodeRef(99999999, 1)),
                ProtocolMethods.GetTree,
                new { nodeId = 1 },
                CancellationToken.None
            )
        );

        Assert.Equal("Element 'e9' is gone — the automation pipe for pid 99999999 closed; call list_windows again for a fresh id", failure.Message);
    }

    [AvaloniaFact]
    public async Task TryRoute_PidWithoutDiscoveryFile_ReturnsNull()
    {
        PipeDirectory directory = NewPipeDirectory();

        Assert.Null(await PipeCalls.TryRouteAsync(directory, 99999999, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task TryRoute_LiveHost_ReturnsClient()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            PipeClient? client = await PipeCalls.TryRouteAsync(directory, Environment.ProcessId, CancellationToken.None);

            Assert.NotNull(client);
            PingResult ping = await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, CancellationToken.None);
            Assert.Equal(Environment.ProcessId, ping.Pid);
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
        }
    }

    [AvaloniaFact]
    public async Task TryListWindows_LiveHost_ReturnsTheWindowsAndRemembersNothing()
    {
        using AutomationHost host = StartHost(() => Windows);
        ShowWindow("Main", Pad(), null);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            List<McpWindowInfo>? windows = await PipeCalls.TryListWindowsAsync(
                directory,
                Environment.ProcessId,
                NullLogger.Instance,
                CancellationToken.None
            );

            Assert.NotNull(windows);
            McpWindowInfo window = Assert.Single(windows);
            Assert.Equal("Main", window.Title);
            Assert.Null(directory.LastTargetPid);
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
        }
    }

    [Fact]
    public async Task TryListWindows_PidWithNoPipe_ReturnsNull()
    {
        PipeDirectory directory = NewPipeDirectory();

        List<McpWindowInfo>? windows = await PipeCalls.TryListWindowsAsync(directory, 99999999, NullLogger.Instance, CancellationToken.None);

        Assert.Null(windows);
        Assert.Null(directory.LastTargetPid);
    }

    [AvaloniaFact]
    public async Task TryListWindows_DisposedClient_ReturnsNull()
    {
        using AutomationHost host = StartHost(() => Windows);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            PipeClient cached = (await directory.TryGetAsync(Environment.ProcessId, CancellationToken.None))!;

            // Another caller forgetting the pid between the lookup and the send disposes the client the directory still hands out.
            await cached.DisposeAsync();

            List<McpWindowInfo>? windows = await PipeCalls.TryListWindowsAsync(
                directory,
                Environment.ProcessId,
                NullLogger.Instance,
                CancellationToken.None
            );

            Assert.Null(windows);
            Assert.Null(directory.LastTargetPid);
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
        }
    }

    [Fact]
    public async Task TryListWindows_HostCodedError_ThrowsPipeRemoteException()
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await WriteDiscoveryAsync(Environment.ProcessId, pipeName, Process.GetCurrentProcess().ProcessName);
        var busy = new AutomationError("The host could not list its windows.", "HOST_BUSY", "try again", null);
        Task server = ServeAsync(pipeName, Environment.ProcessId, busy, timeout.Token);
        PipeDirectory directory = NewPipeDirectory();
        try
        {
            PipeRemoteException failure = await Assert.ThrowsAsync<PipeRemoteException>(() =>
                PipeCalls.TryListWindowsAsync(directory, Environment.ProcessId, NullLogger.Instance, CancellationToken.None)
            );

            Assert.Equal("HOST_BUSY", failure.Code);
            Assert.Equal("HOST_BUSY: The host could not list its windows. Hint: try again", failure.Message);
            Assert.Null(directory.LastTargetPid);
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
        }

        await server;
    }

    [Fact]
    public async Task SendForPid_PidWithoutAPipe_ReadsPipeClosedAndForgetsThePid()
    {
        PipeDirectory directory = NewPipeDirectory();
        directory.RememberTarget(99999999);

        McpException failure = await Assert.ThrowsAsync<McpException>(() =>
            PipeCalls.SendForPidAsync<JsonElement>(
                directory,
                99999999,
                ProtocolMethods.SendKeys,
                new { keys = "x" },
                PipeClient.RequestTimeout,
                CancellationToken.None
            )
        );

        Assert.Equal("The automation pipe for pid 99999999 closed; call list_windows again, or pass an element id", failure.Message);
        Assert.Null(directory.LastTargetPid);
    }

    [Fact]
    public async Task ForgetAsync_OtherPid_LeavesTheRememberedPid()
    {
        PipeDirectory directory = NewPipeDirectory();
        directory.RememberTarget(Environment.ProcessId);

        await directory.ForgetAsync(99999999);

        Assert.Equal(Environment.ProcessId, directory.LastTargetPid);
    }

    // The directory drops a cached client whose process has exited; a ping.exe child stands in for a client that exits, and a
    // scripted pipe server answers the directory's ping for it.
    [Fact]
    public async Task Eviction_OfTheRememberedPidsClient_ClearsIt()
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        var start = new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false };
        Process child = Process.Start(start)!;
        int childPid = child.Id;
        string childName = child.ProcessName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await WriteDiscoveryAsync(childPid, pipeName, childName);
        Task server = ServeAsync(pipeName, childPid, null, timeout.Token);
        var directory = new PipeDirectory(DiscoveryDirectory, childName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
        try
        {
            Assert.NotNull(await directory.TryGetAsync(childPid, CancellationToken.None));
            directory.RememberTarget(childPid);
            child.Kill();
            await child.WaitForExitAsync(timeout.Token);

            // The handle this test holds would keep the exited process listed, so it goes before the directory looks again.
            child.Dispose();
            Assert.Null(await directory.TryGetAsync(childPid, CancellationToken.None));

            Assert.Null(directory.LastTargetPid);
        }
        finally
        {
            await directory.ForgetAsync(childPid);
            child.Dispose();
        }

        await server;
    }

    private async Task WriteDiscoveryAsync(int pid, string pipeName, string processName)
    {
        Directory.CreateDirectory(DiscoveryDirectory);
        var discovery = new DiscoveryInfo
        {
            Pid = pid,
            PipeName = pipeName,
            ProcessName = processName,
            StartTime = DateTimeOffset.Now,
            ProtocolVersion = ProtocolVersion.Current,
        };
        await File.WriteAllTextAsync(
            Path.Combine(DiscoveryDirectory, $"{pid}.json"),
            ProtocolSerializer.Serialize(discovery),
            TestContext.Current.CancellationToken
        );
    }

    /// <summary>
    /// Answers a ping with a ping result for <paramref name="pid"/>, and every other request with <paramref name="otherError"/>,
    /// or with the same ping result when it is null, until the client hangs up. Each answer is flushed as it is written.
    /// </summary>
    private static async Task ServeAsync(string pipeName, int pid, AutomationError? otherError, CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(ct);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var writer = new StreamWriter(pipe, utf8, bufferSize: 1024, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                using var request = JsonDocument.Parse(line);
                string id = request.RootElement.GetProperty("id").GetString()!;
                bool isPing = request.RootElement.GetProperty("method").GetString() == ProtocolMethods.Ping;
                AutomationResponse response =
                    (isPing || (otherError is null))
                        ? AutomationResponse.Success(id, ProtocolSerializer.ToElement(new PingResult(pid, ProtocolVersion.Current)))
                        : AutomationResponse.Failure(id, otherError);
                await writer.WriteLineAsync(ProtocolSerializer.Serialize(response).AsMemory(), ct);
                await writer.FlushAsync(ct);
            }
        }
        finally
        {
            await CloseAfterHangUpAsync(writer);
        }
    }

    /// <summary>Disposes the stub's writer, whose closing flush may find the pipe already broken by the client.</summary>
    private static async Task CloseAfterHangUpAsync(StreamWriter writer)
    {
        try
        {
            await writer.DisposeAsync();
        }
        catch (IOException ex)
        {
            // The client closes the pipe after a coded error, so the closing flush can hit a broken pipe; every answer was
            // already flushed when it was written, so nothing is lost.
            TestContext.Current.TestOutputHelper?.WriteLine($"The client hung up before the stub writer closed: {ex.Message}");
        }
    }

    private static PipeElement Element(string id, int nodeId) => new(id, new PipeNodeRef(Environment.ProcessId, nodeId));

    private PipeDirectory NewPipeDirectory() =>
        new(DiscoveryDirectory, Process.GetCurrentProcess().ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
}
