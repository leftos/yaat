extern alias mcp;

using System.Diagnostics;
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
            PingResult ping = await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, CancellationToken.None);
            Assert.Equal(Environment.ProcessId, ping.Pid);
        }
        finally
        {
            await directory.ForgetAsync(Environment.ProcessId);
        }
    }

    private static PipeElement Element(string id, int nodeId) => new(id, new PipeNodeRef(Environment.ProcessId, nodeId));

    private PipeDirectory NewPipeDirectory() =>
        new(DiscoveryDirectory, Process.GetCurrentProcess().ProcessName, NullLogger<PipeDirectory>.Instance, NullLogger<PipeClient>.Instance);
}
