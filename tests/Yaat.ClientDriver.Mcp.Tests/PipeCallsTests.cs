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
        Task server = ServePingAsync(pipeName, childPid, timeout.Token);
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

    /// <summary>Answers every request with a ping result for <paramref name="pid"/>, until the client hangs up.</summary>
    private static async Task ServePingAsync(string pipeName, int pid, CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(ct);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, utf8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            using var request = JsonDocument.Parse(line);
            string id = request.RootElement.GetProperty("id").GetString()!;
            var response = AutomationResponse.Success(id, ProtocolSerializer.ToElement(new PingResult(pid, ProtocolVersion.Current)));
            await writer.WriteLineAsync(ProtocolSerializer.Serialize(response).AsMemory(), ct);
        }
    }

    private static PipeElement Element(string id, int nodeId) => new(id, new PipeNodeRef(Environment.ProcessId, nodeId));

    private PipeDirectory NewPipeDirectory() =>
        new(DiscoveryDirectory, Process.GetCurrentProcess().ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
}
