extern alias mcp;

using System.Diagnostics;
using System.Text.Json;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The <c>list_app_tools</c> and <c>call_app_tool</c> MCP tools against a scripted automation host: the request each sends, and
/// how each renders the tool list, a tool's message, an unavailable tool and the host's coded error. Each call goes through the
/// tool's own <see cref="AppTools"/> method over a real pipe.
/// </summary>
public sealed class AppToolsToolTests : AutomationHostFixture
{
    private static int Pid => Environment.ProcessId;

    [Fact]
    public async Task ListAppTools_PrintsOneLinePerTool()
    {
        AppToolInfo[] tools =
        [
            new("set_sim_rate", "Sets the room's sim rate.", [new AppToolParameterInfo("rate", "int", "The sim rate.")], true, null),
            new(
                "center_radar",
                "Centres the primary radar.",
                [new AppToolParameterInfo("callsign", "string", "The callsign."), new AppToolParameterInfo("rangeNm", "double", "The range.")],
                false,
                "No scenario is loaded in the room."
            ),
        ];

        Outcome outcome = await RunAsync(Answering(tools), appTools => appTools.ListAppToolsAsync(TestContext.Current.CancellationToken, Pid));

        string[] lines = outcome.Text.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Equal("2 app tools (pipe)", lines[0]);
        Assert.Equal("- set_sim_rate(rate: int — The sim rate.): Sets the room's sim rate. [available]", lines[1]);
        Assert.Equal(
            "- center_radar(callsign: string — The callsign.; rangeNm: double — The range.): Centres the primary radar. "
                + "[unavailable: No scenario is loaded in the room.]",
            lines[2]
        );
        Assert.Equal(ProtocolMethods.ListAppTools, outcome.Method);
    }

    [Fact]
    public async Task CallAppTool_PassesArgumentsAndPrintsTheMessage()
    {
        using var arguments = JsonDocument.Parse("""{"callsign":"UAL1","rangeNm":20.5}""");

        Outcome outcome = await RunAsync(
            Answering(new CallAppToolResult(true, null, "Primary radar centred on UAL1.")),
            appTools => appTools.CallAppToolAsync("center_radar", arguments.RootElement, TestContext.Current.CancellationToken, Pid)
        );

        Assert.Equal("center_radar: Primary radar centred on UAL1. (pipe)", outcome.Text);
        Assert.Equal(ProtocolMethods.CallAppTool, outcome.Method);
        JsonElement sent = Assert.NotNull(outcome.Parameters);
        Assert.Equal("center_radar", sent.GetProperty("tool").GetString());
        Assert.True(JsonElement.DeepEquals(arguments.RootElement, sent.GetProperty("arguments")), sent.GetRawText());
    }

    [Fact]
    public async Task CallAppTool_Unavailable_PrintsTheReason()
    {
        using var arguments = JsonDocument.Parse("""{"rate":4}""");

        Outcome outcome = await RunAsync(
            Answering(new CallAppToolResult(false, "Not in a room: connect, then create or join one.", null)),
            appTools => appTools.CallAppToolAsync("set_sim_rate", arguments.RootElement, TestContext.Current.CancellationToken, Pid)
        );

        Assert.Equal("set_sim_rate unavailable: Not in a room: connect, then create or join one. (pipe)", outcome.Text);
    }

    [Fact]
    public async Task CallAppTool_InvalidParam_PrintsTheError()
    {
        using var arguments = JsonDocument.Parse("""{"rate":1.5}""");
        var error = new AutomationError(
            "Argument 'rate' of 'set_sim_rate' must be an int (a whole JSON number), not 1.5.",
            AutomationErrorCodes.InvalidParam,
            "Provide a valid value for 'rate'.",
            null
        );

        McpException thrown = await Assert.ThrowsAsync<McpException>(() =>
            RunAsync(
                (id, _, _) => AutomationResponse.Failure(id, error),
                appTools => appTools.CallAppToolAsync("set_sim_rate", arguments.RootElement, TestContext.Current.CancellationToken, Pid)
            )
        );

        Assert.StartsWith("INVALID_PARAM: Argument 'rate' of 'set_sim_rate' must be an int", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>What the tool returned, and the method and params the scripted host saw it send.</summary>
    /// <param name="Text">The tool's text answer.</param>
    /// <param name="Method">The method of the request the host answered.</param>
    /// <param name="Parameters">The request's params, or null when it sent none.</param>
    private sealed record Outcome(string Text, string? Method, JsonElement? Parameters);

    private static Func<string, string, JsonElement?, AutomationResponse> Answering(object result) =>
        (id, _, _) => AutomationResponse.Success(id, ProtocolSerializer.ToElement(result));

    /// <summary>
    /// Serves a scripted host that records the request it is sent and answers it with <paramref name="respond"/>, and runs
    /// <paramref name="call"/> against it.
    /// </summary>
    private async Task<Outcome> RunAsync(Func<string, string, JsonElement?, AutomationResponse> respond, Func<AppTools, Task<string>> call)
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        await StubPipeHost.AdvertiseAsync(DiscoveryDirectory, pipeName, TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string? method = null;
        JsonElement? seen = null;
        Task host = StubPipeHost.ServeResponsesAsync(
            pipeName,
            (id, requestMethod, parameters) =>
            {
                method = requestMethod;
                seen = parameters?.Clone();
                return respond(id, requestMethod, parameters);
            },
            TimeSpan.Zero,
            timeout.Token
        );
        var directory = new PipeDirectory(
            DiscoveryDirectory,
            Process.GetCurrentProcess().ProcessName,
            NullLogger<PipeDirectory>.Instance,
            NullLogger<PipeClient>.Instance
        );
        try
        {
            string text = await call(new AppTools(directory));
            return new Outcome(text, method, seen);
        }
        finally
        {
            await directory.ForgetAsync(Pid);
            await timeout.CancelAsync();
            try
            {
                await host;
            }
            catch (OperationCanceledException)
            {
                // The stub's serve ends with this test's own timeout; there is nothing left to wait for.
            }
        }
    }
}
