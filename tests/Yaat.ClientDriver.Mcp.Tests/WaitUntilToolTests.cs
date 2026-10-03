extern alias mcp;

using System.Diagnostics;
using System.Text.Json;
using mcp::Yaat.ClientDriver.Mcp.Pipe;
using mcp::Yaat.ClientDriver.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Client.Automation.Handlers;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// The <c>wait_until</c> MCP tool against a scripted automation host: the params it forwards and the wait it asks the pipe
/// client for, and how it renders a met, an unmet and an action-running answer. Each call goes through the tool's own
/// <see cref="PipeTools.WaitUntilAsync"/> over a real pipe, as the batch and client-error tests do.
/// </summary>
public sealed class WaitUntilToolTests : AutomationHostFixture
{
    private static int Pid => Environment.ProcessId;

    private static string ShotFixture => Path.Combine(AppContext.BaseDirectory, "TestData", "shot-200x100.png");

    private const string OnGround = """[{"kind":"on_ground","callsign":"AAL1"}]""";

    // The conditions and the two optional targets go to the host as the caller wrote them, and the timeout is the host's own
    // clamp of 100–600000 rather than the caller's 999999.
    [Fact]
    public async Task WaitUntil_SendsConditionsModeAndClampedTimeout()
    {
        const string then = """[{"action":"pause"},{"action":"set_rate","rate":4}]""";
        const string screenshot = """{"windowSelector":"Main"}""";
        Outcome outcome = await CallWaitUntilAsync(
            new WaitRequest(OnGround, "all", 999_999, screenshot, then),
            Answering(new WaitUntilResult(true, [0], 3.5, 12, [new WaitUntilLastValue(0, "on_ground", "on ground")], null, null, null))
        );

        JsonElement? parameters = outcome.Parameters;
        Assert.NotNull(parameters);
        JsonElement sent = parameters.Value;
        Assert.True(JsonElement.DeepEquals(Parse(OnGround), sent.GetProperty("conditions")), sent.GetProperty("conditions").GetRawText());
        Assert.Equal("all", sent.GetProperty("mode").GetString());
        Assert.Equal(600000, sent.GetProperty("timeoutMs").GetInt32());
        Assert.True(JsonElement.DeepEquals(Parse(then), sent.GetProperty("then")));
        Assert.True(JsonElement.DeepEquals(Parse(screenshot), sent.GetProperty("screenshot")));
    }

    // The answer timeout is the host's clamped wait plus exactly the shared margin: a literal 605 s fails if the margin stops
    // being added, and the 600000 host wait fails if the clamp's own ceiling replaces it.
    [Fact]
    public void WaitUntil_RequestTimeoutCoversTheHostWait()
    {
        (int hostMs, TimeSpan request) = PipeTools.WaitUntilTimeouts(999_999);
        Assert.Equal(600000, hostMs);
        Assert.Equal(TimeSpan.FromSeconds(605), request);
        Assert.Equal((30000, TimeSpan.FromSeconds(35)), PipeTools.WaitUntilTimeouts(30000));
        Assert.Equal(100, PipeTools.WaitUntilTimeouts(0).HostMs);
    }

    [Fact]
    public async Task WaitUntil_MetFalse_IsAResultNotAnError()
    {
        Outcome outcome = await CallWaitUntilAsync(
            new WaitRequest("""[{"kind":"landed","callsign":"AAL1"}]""", "any", 30000, null, null),
            Answering(new WaitUntilResult(false, [], 12.5, 30000, [new WaitUntilLastValue(0, "landed", "airborne")], null, null, null))
        );

        Assert.StartsWith("not met in 30000 ms at sim 12.5 s (pipe)", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("- [0] landed: airborne", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitUntil_SavesTheScreenshotAndReturnsItsPath()
    {
        byte[] png = await File.ReadAllBytesAsync(ShotFixture, TestContext.Current.CancellationToken);
        string base64 = Convert.ToBase64String(png);
        Outcome outcome = await CallWaitUntilAsync(
            new WaitRequest(OnGround, "any", 30000, """{"windowSelector":"Main"}""", null),
            Answering(
                new WaitUntilResult(
                    true,
                    [0],
                    45.6,
                    120,
                    [new WaitUntilLastValue(0, "on_ground", "on ground")],
                    new ScreenshotResult(200, 100, 1.0, base64),
                    null,
                    null
                )
            )
        );

        Assert.DoesNotContain(base64, outcome.Text, StringComparison.Ordinal);
        string[] lines = outcome.Text.Split(Environment.NewLine);
        string shotLine = Assert.Single(lines, line => line.StartsWith("screenshot: ", StringComparison.Ordinal));
        Assert.EndsWith("(200x100)", shotLine, StringComparison.Ordinal);
        string path = shotLine["screenshot: ".Length..shotLine.LastIndexOf(" (", StringComparison.Ordinal)];
        Assert.Contains(Path.Combine(".tmp", "client-driver", "shots"), path, StringComparison.Ordinal);
        Assert.True(File.Exists(path), $"the screenshot should be saved under .tmp/client-driver/shots: {path}");
        Assert.Equal(png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitUntil_ReportsThenActionResults()
    {
        Outcome outcome = await CallWaitUntilAsync(
            new WaitRequest(OnGround, "any", 30000, null, """[{"action":"pause"},{"action":"set_rate","rate":4}]"""),
            Answering(
                new WaitUntilResult(
                    true,
                    [0],
                    1.0,
                    50,
                    [new WaitUntilLastValue(0, "on_ground", "on ground")],
                    null,
                    null,
                    [new WaitUntilActionResult("pause", true, null), new WaitUntilActionResult("set_rate", false, "x")]
                )
            )
        );

        Assert.StartsWith("met after 50 ms at sim 1.0 s: held on_ground (pipe)", outcome.Text, StringComparison.Ordinal);
        string[] lines = outcome.Text.Split(Environment.NewLine);
        int paused = Array.FindIndex(lines, line => line == "then pause: ok");
        int rated = Array.FindIndex(lines, line => line == "then set_rate: failed — x");
        Assert.True(paused >= 0, outcome.Text);
        Assert.True(rated > paused, $"the actions should be reported in order: {outcome.Text}");
    }

    // The host's own capture failure is a line in the answer, never a lost wait.
    [Fact]
    public async Task WaitUntil_ReportsTheScreenshotError()
    {
        Outcome outcome = await CallWaitUntilAsync(
            new WaitRequest(OnGround, "any", 30000, null, null),
            Answering(new WaitUntilResult(true, [0], 1.0, 40, [new WaitUntilLastValue(0, "on_ground", "on ground")], null, "CAPTURE_FAILED: x", null))
        );

        string[] lines = outcome.Text.Split(Environment.NewLine);
        Assert.Contains("screenshot failed: CAPTURE_FAILED: x", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("screenshot: ", StringComparison.Ordinal));
    }

    // A PNG this server cannot decode costs the screenshot line, not the met/held/last/then answer already in hand.
    [Fact]
    public async Task WaitUntil_UnsavableScreenshot_IsALineNotAnError()
    {
        Outcome outcome = await CallWaitUntilAsync(
            new WaitRequest(OnGround, "any", 30000, null, null),
            Answering(
                new WaitUntilResult(
                    true,
                    [0],
                    2.0,
                    30,
                    [new WaitUntilLastValue(0, "on_ground", "on ground")],
                    new ScreenshotResult(10, 10, 1.0, "not base64!"),
                    null,
                    null
                )
            )
        );

        Assert.StartsWith("met after 30 ms at sim 2.0 s: held on_ground (pipe)", outcome.Text, StringComparison.Ordinal);
        string[] lines = outcome.Text.Split(Environment.NewLine);
        Assert.Contains(lines, line => line.StartsWith("screenshot failed: ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("screenshot: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WaitUntil_FailedActionWithoutError_SaysNoReasonGiven()
    {
        Outcome outcome = await CallWaitUntilAsync(
            new WaitRequest(OnGround, "any", 30000, null, null),
            Answering(
                new WaitUntilResult(
                    true,
                    [0],
                    1.0,
                    20,
                    [new WaitUntilLastValue(0, "on_ground", "on ground")],
                    null,
                    null,
                    [new WaitUntilActionResult("set_rate", false, null)]
                )
            )
        );

        string[] lines = outcome.Text.Split(Environment.NewLine);
        Assert.Contains("then set_rate: failed — no reason given", lines);
    }

    // The tool's ceiling is the host's own: drift between the two would send a wait the host clamps to something else.
    [Fact]
    public void WaitUntil_CeilingMatchesTheHost() => Assert.Equal(WaitUntilParams.MaxTimeoutMs, PipeTools.MaxWaitUntilMs);

    /// <summary>One call's arguments, as the tool's own parameters: the conditions and options the caller wrote.</summary>
    /// <param name="Conditions">The <c>conditions</c> JSON array.</param>
    /// <param name="Mode">The <c>mode</c>, <c>any</c> or <c>all</c>.</param>
    /// <param name="TimeoutMs">The caller's requested wait, in ms.</param>
    /// <param name="Screenshot">The <c>screenshot</c> target JSON, or null for none.</param>
    /// <param name="Then">The <c>then</c> actions JSON, or null for none.</param>
    private sealed record WaitRequest(string Conditions, string Mode, int TimeoutMs, string? Screenshot, string? Then);

    /// <summary>What the tool returned and the params the scripted host saw it send.</summary>
    /// <param name="Text">The tool's text answer.</param>
    /// <param name="Parameters">The request's params, or null when it sent none.</param>
    private sealed record Outcome(string Text, JsonElement? Parameters);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Func<string, string, JsonElement?, AutomationResponse> Answering(WaitUntilResult result) =>
        (id, _, _) => AutomationResponse.Success(id, ProtocolSerializer.ToElement(result));

    /// <summary>
    /// Serves a scripted host that records the params it is sent and answers every <c>wait_until</c> with
    /// <paramref name="respond"/>, and runs the tool against it.
    /// </summary>
    private async Task<Outcome> CallWaitUntilAsync(WaitRequest request, Func<string, string, JsonElement?, AutomationResponse> respond)
    {
        using var conditions = JsonDocument.Parse(request.Conditions);
        JsonElement? screenshot = (request.Screenshot is null) ? null : Parse(request.Screenshot);
        JsonElement? then = (request.Then is null) ? null : Parse(request.Then);
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        await StubPipeHost.AdvertiseAsync(DiscoveryDirectory, pipeName, TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        JsonElement? seen = null;
        Task host = StubPipeHost.ServeResponsesAsync(
            pipeName,
            (id, method, parameters) =>
            {
                seen = parameters;
                return respond(id, method, parameters);
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
            string text = await new PipeTools(directory).WaitUntilAsync(
                conditions.RootElement,
                TestContext.Current.CancellationToken,
                Pid,
                request.Mode,
                request.TimeoutMs,
                screenshot,
                then
            );
            return new Outcome(text, seen);
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
