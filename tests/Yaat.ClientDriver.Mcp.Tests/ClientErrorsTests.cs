extern alias mcp;

using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;
using McpClientLogEntry = mcp::Yaat.Client.Automation.Protocol.ClientLogEntry;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The client errors a pipe answer carries, as an agent sees them: a tool called through the MCP server pipeline, with the
/// call-tool filter the server registers, against a scripted host whose answers carry <c>clientErrors</c> or not.
/// </summary>
public sealed class ClientErrorsTests : AutomationHostFixture
{
    private const string ExpectedLines =
        "- [Error] Yaat.Client.Radar: Render failed (System.InvalidOperationException: bad state)\n" + "- [Critical] Yaat.Client.Speech: Engine died";

    private const string ExpectedBlock = $"Client logged 2 error(s) during this call:\n{ExpectedLines}";

    private static readonly ClientLogEntry[] TwoErrors =
    [
        new("Error", "Yaat.Client.Radar", "Render failed", "System.InvalidOperationException: bad state"),
        new("Critical", "Yaat.Client.Speech", "Engine died", null),
    ];

    private static int Pid => Environment.ProcessId;

    // The MCP compiles the protocol types as its own linked copy, so its formatter takes its own ClientLogEntry.
    [Fact]
    public void Format_ListsEveryEntry_WithItsExceptionWhenThereIsOne() => Assert.Equal(ExpectedBlock, PipeCallErrors.Format(McpEntries(), 0));

    [Fact]
    public void Format_WithOmittedEntries_CountsThemInTheHeader() =>
        Assert.Equal($"Client logged 4 error(s) during this call (2 not shown):\n{ExpectedLines}", PipeCallErrors.Format(McpEntries(), 2));

    [Fact]
    public async Task ToolCall_AnswerWithClientErrors_AppendsTheFormattedBlock()
    {
        CallToolResult result = await CallWaitForAsync(
            (id, _, _) =>
                new AutomationResponse
                {
                    Id = id,
                    Result = ProtocolSerializer.ToElement(new WaitForResult(12, 1)),
                    ClientErrors = TwoErrors,
                }
        );

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(["exists held on '#Box' after 12 ms (1 matches, pipe)", ExpectedBlock], Texts(result));
    }

    [Fact]
    public async Task ToolCall_ErrorAnswerWithClientErrors_ReturnsAnErrorResultEndingWithTheBlock()
    {
        CallToolResult result = await CallWaitForAsync(
            (id, _, _) =>
                new AutomationResponse
                {
                    Id = id,
                    ErrorInfo = new AutomationError("Condition 'exists' did not hold", AutomationErrorCodes.Timeout, null, null),
                    ClientErrors = TwoErrors,
                }
        );

        // The tool's McpException passes through the call-tool filters as an exception, and the SDK turns it into an IsError
        // result outside them: the block arrives at the end of the one error text, after a blank line.
        Assert.True(result.IsError);
        string text = Assert.Single(Texts(result));
        Assert.Contains("TIMEOUT: Condition 'exists' did not hold", text, StringComparison.Ordinal);
        Assert.EndsWith($"did not hold\n\n{ExpectedBlock}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCall_AnswerWithoutClientErrors_LeavesTheResultUnchanged()
    {
        CallToolResult result = await CallWaitForAsync(
            (id, _, _) => AutomationResponse.Success(id, ProtocolSerializer.ToElement(new WaitForResult(7, 1)))
        );

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(["exists held on '#Box' after 7 ms (1 matches, pipe)"], Texts(result));
    }

    [Fact]
    public async Task ToolCall_NonMcpExceptionAfterAPipeCallWithClientErrors_ReturnsAnErrorResultWithTheBlock()
    {
        CallToolResult result = await CallToolAsync<ThrowingProbeTool>(
            "throwing_probe",
            new Dictionary<string, object?> { ["pid"] = Pid },
            (id, _, _) =>
                new AutomationResponse
                {
                    Id = id,
                    Result = ProtocolSerializer.ToElement(new WaitForResult(12, 1)),
                    ClientErrors = TwoErrors,
                }
        );

        // The SDK answers a non-MCP exception with a generic error result of its own; the filter keeps that answer but adds
        // the client errors the call collected, which a tool that failed after a pipe call is exactly the case for.
        Assert.True(result.IsError);
        Assert.Equal(["An error occurred invoking 'throwing_probe'.", ExpectedBlock], Texts(result));
    }

    private static McpClientLogEntry[] McpEntries() => [.. TwoErrors.Select(e => new McpClientLogEntry(e.Level, e.Category, e.Message, e.Exception))];

    private static List<string> Texts(CallToolResult result) => [.. result.Content.Select(block => Assert.IsType<TextContentBlock>(block).Text)];

    /// <summary>Calls the wait_for tool over <see cref="CallToolAsync{TTool}"/>.</summary>
    private Task<CallToolResult> CallWaitForAsync(Func<string, string, JsonElement?, AutomationResponse> respond) =>
        CallToolAsync<PipeTools>(
            "wait_for",
            new Dictionary<string, object?>
            {
                ["selector"] = "#Box",
                ["condition"] = "exists",
                ["pid"] = Pid,
            },
            respond
        );

    /// <summary>
    /// Serves a scripted host answering every non-ping request with <paramref name="respond"/>, and calls
    /// <paramref name="toolName"/> on it through an MCP server built as Program builds it: <typeparamref name="TTool"/> over a
    /// pipe directory, with the client-errors call-tool filter the server registers.
    /// </summary>
    private async Task<CallToolResult> CallToolAsync<TTool>(
        string toolName,
        Dictionary<string, object?> arguments,
        Func<string, string, JsonElement?, AutomationResponse> respond
    )
        where TTool : class
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        await StubPipeHost.AdvertiseAsync(DiscoveryDirectory, pipeName, testToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task host = StubPipeHost.ServeResponsesAsync(pipeName, respond, TimeSpan.Zero, timeout.Token);
        var directory = new PipeDirectory(
            DiscoveryDirectory,
            Process.GetCurrentProcess().ProcessName,
            NullLogger<PipeDirectory>.Instance,
            NullLogger<PipeClient>.Instance
        );

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(directory);
        services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<TTool>()
            .WithPipeCallErrors();
        await using ServiceProvider provider = services.BuildServiceProvider();
        McpServer server = provider.GetRequiredService<McpServer>();
        Task serving = server.RunAsync(timeout.Token);
        try
        {
            var transport = new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
            await using McpClient client = await McpClient.CreateAsync(transport, null, NullLoggerFactory.Instance, testToken);
            return await client.CallToolAsync(toolName, arguments, null, null, testToken);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
            await host;
            await timeout.CancelAsync();
            try
            {
                await serving;
            }
            catch (OperationCanceledException)
            {
                // The server's run ends with its token's cancel; nothing else is left to wait for.
            }
        }
    }
}

/// <summary>
/// A test-only tool that makes one pipe call and then throws a non-MCP exception: the state in which the call's client errors
/// would be lost if the call-tool filter only handled an McpException.
/// </summary>
[McpServerToolType]
public sealed class ThrowingProbeTool(PipeDirectory pipes)
{
    [McpServerTool(Name = "throwing_probe")]
    public async Task<string> ProbeAsync(int pid, CancellationToken cancellationToken)
    {
        _ = await PipeCalls
            .SendForPidAsync<WaitForResult>(
                pipes,
                pid,
                ProtocolMethods.WaitFor,
                new { selector = "#Box", condition = "exists" },
                PipeClient.RequestTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
        throw new InvalidOperationException("the probe threw after its pipe call");
    }
}
