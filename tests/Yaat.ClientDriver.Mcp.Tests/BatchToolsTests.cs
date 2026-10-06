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
using McpClientLogEntry = mcp::Yaat.Client.Automation.Protocol.ClientLogEntry;

namespace Yaat.ClientDriver.Mcp.Tests;

/// <summary>
/// <c>batch_drive</c> against a scripted automation host: the steps run in order, the first failure stops the batch, a step
/// that names an element by node id is refused before any call, and the result is JSON — never an MCP error — whether the
/// batch passed or failed. Each batch runs through the public <see cref="BatchTools.RunAsync"/> seam, so a test pins a short
/// deadline where it needs one.
/// </summary>
public sealed class BatchToolsTests : AutomationHostFixture
{
    private const string NodeIdRefusal = "steps address elements by selector; nodeId is not accepted in batch_drive";

    private static int Pid => Environment.ProcessId;

    private static string ShotFixture => Path.Combine(AppContext.BaseDirectory, "TestData", "shot-200x100.png");

    /// <summary>The methods the last <see cref="RunBatchAsync"/> call's host answered, for the tests whose batch throws before it returns.</summary>
    private List<string> _seen = [];

    [Fact]
    public async Task Batch_RunsStepsInOrder_AndPasses()
    {
        Outcome outcome = await RunBatchAsync(
            """[{"method":"list_windows"},{"method":"get_tree","params":{"selector":"#Box","depth":2}}]""",
            Answer((method, _) => new { ok = method }),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["list_windows", "get_tree"], outcome.Methods);
        Assert.Equal("#Box", outcome.Arguments[1]!.Value.GetProperty("selector").GetString());
        JsonElement root = Parse(outcome.Result);
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.False(root.TryGetProperty("failedAt", out _), "a passing batch names no failed step");
        JsonElement[] steps = [.. root.GetProperty("steps").EnumerateArray()];
        Assert.Equal(2, steps.Length);
        Assert.Equal(0, steps[0].GetProperty("index").GetInt32());
        Assert.Equal("list_windows", steps[0].GetProperty("method").GetString());
        Assert.True(steps[0].GetProperty("ok").GetBoolean());
        Assert.True(steps[0].GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.Equal("list_windows", steps[0].GetProperty("result").GetProperty("ok").GetString());
        Assert.Equal(1, steps[1].GetProperty("index").GetInt32());
        Assert.Equal("get_tree", steps[1].GetProperty("method").GetString());
        Assert.Equal("get_tree", steps[1].GetProperty("result").GetProperty("ok").GetString());
    }

    [Fact]
    public async Task Batch_StopsAtTheFirstFailingStep_AndNamesIt()
    {
        Outcome outcome = await RunBatchAsync(
            """[{"method":"list_windows"},{"method":"get_tree"}]""",
            Failing(AutomationErrorCodes.Timeout, "Condition did not hold"),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["list_windows"], outcome.Methods);
        JsonElement root = Parse(outcome.Result);
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(0, root.GetProperty("failedAt").GetInt32());
        JsonElement step = Assert.Single(root.GetProperty("steps").EnumerateArray());
        Assert.False(step.GetProperty("ok").GetBoolean());
        Assert.Equal("list_windows", step.GetProperty("method").GetString());
        Assert.Equal("TIMEOUT: Condition did not hold", step.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_StepWithNodeId_FailsWithoutCallingThePipe()
    {
        Outcome nodeId = await RunBatchAsync(
            """[{"method":"click","params":{"nodeId":7}}]""",
            Answer((method, _) => new { ok = method }),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );
        Outcome windowNodeId = await RunBatchAsync(
            """[{"method":"screenshot","params":{"windowNodeId":7}}]""",
            Answer((method, _) => new { ok = method }),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(nodeId.Methods);
        Assert.Empty(windowNodeId.Methods);
        JsonElement step = Step(nodeId.Result, 0);
        Assert.False(step.GetProperty("ok").GetBoolean());
        Assert.Equal("click", step.GetProperty("method").GetString());
        Assert.Equal(NodeIdRefusal, step.GetProperty("error").GetString());
        Assert.Equal(NodeIdRefusal, Step(windowNodeId.Result, 0).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_EmptyOrOverLimit_IsRefusedBeforeAnyStep()
    {
        string overLimit = "[" + string.Join(",", Enumerable.Repeat("""{"method":"list_windows"}""", 101)) + "]";
        foreach (string stepsJson in new[] { "[]", overLimit })
        {
            McpException failure = await Assert.ThrowsAsync<McpException>(() =>
                RunBatchAsync(
                    stepsJson,
                    Answer((method, _) => new { ok = method }),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.Zero,
                    TestContext.Current.CancellationToken
                )
            );

            Assert.Contains("1 to 100", failure.Message, StringComparison.Ordinal);
            Assert.Empty(_seen);
        }
    }

    [Fact]
    public async Task AssertWait_SendsWaitFor_WithAOneSecondDefault()
    {
        Outcome outcome = await RunBatchAsync(
            """[{"assert":"wait","selector":"#Box","condition":"exists"}]""",
            Answer((_, _) => new { elapsedMs = 12, matchCount = 1 }),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["wait_for"], outcome.Methods);
        JsonElement parameters = outcome.Arguments[0]!.Value;
        Assert.Equal("#Box", parameters.GetProperty("selector").GetString());
        Assert.Equal("exists", parameters.GetProperty("condition").GetString());
        Assert.Equal(1000, parameters.GetProperty("timeoutMs").GetInt32());
        JsonElement step = Step(outcome.Result, 0);
        Assert.True(step.GetProperty("ok").GetBoolean());
        Assert.Equal("wait", step.GetProperty("assert").GetString());
        Assert.Equal(1, step.GetProperty("result").GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task AssertWait_TimeoutIsClamped()
    {
        Outcome outcome = await RunBatchAsync(
            """[{"assert":"wait","selector":"#Box","condition":"exists","timeoutMs":999999}]""",
            Answer((_, _) => new { elapsedMs = 1, matchCount = 1 }),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["wait_for"], outcome.Methods);
        Assert.Equal(30000, outcome.Arguments[0]!.Value.GetProperty("timeoutMs").GetInt32());
        Assert.True(Step(outcome.Result, 0).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task AssertNoErrors_FailsAfterAStepThatLoggedClientErrors()
    {
        _ = PipeCallErrors.Begin();
        Outcome outcome = await RunBatchAsync(
            """[{"method":"list_windows"},{"assert":"no_errors"}]""",
            AnsweredWithClientErrors((method, _) => new { ok = method }, [new ClientLogEntry("Error", "Yaat.Client.Radar", "Render failed", null)]),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["list_windows"], outcome.Methods);
        JsonElement root = Parse(outcome.Result);
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(1, root.GetProperty("failedAt").GetInt32());
        JsonElement step = Step(outcome.Result, 1);
        Assert.False(step.GetProperty("ok").GetBoolean());
        Assert.Equal("no_errors", step.GetProperty("assert").GetString());
        string error = step.GetProperty("error").GetString()!;
        Assert.Contains("client logged 1 error(s)", error, StringComparison.Ordinal);
        Assert.Contains("Render failed", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertNoErrors_IgnoresErrorsFromBeforeTheBatch()
    {
        _ = PipeCallErrors.Begin();
        PipeCallErrors.Add([new McpClientLogEntry("Error", "Yaat.Client.Radar", "A failure from an earlier call", null)], 0);

        Outcome outcome = await RunBatchAsync(
            """[{"assert":"no_errors"}]""",
            Answer((method, _) => new { ok = method }),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(outcome.Methods);
        JsonElement step = Step(outcome.Result, 0);
        Assert.True(step.GetProperty("ok").GetBoolean(), $"an error logged before the batch is not this batch's: {outcome.Result}");
    }

    // A real wait past the default request timeout would make this test slow, so the step's arithmetic is checked exactly, and
    // then shown to reach the pipe client: timeoutMs 0 leaves only the answer margin, so an answer six seconds late is cut.
    [Fact]
    public async Task MethodStep_TimeoutMs_IsTheAnswerTimeout()
    {
        using var document = JsonDocument.Parse("""{"timeoutMs":40000}""");
        Assert.Equal(TimeSpan.FromSeconds(45), BatchTools.StepTimeoutFor(document.RootElement));
        Assert.Equal(PipeClient.RequestTimeout, BatchTools.StepTimeoutFor(null));

        Outcome outcome = await RunBatchAsync(
            """[{"method":"list_windows","params":{"timeoutMs":0}}]""",
            Answer((method, _) => new { ok = method }),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(6),
            TestContext.Current.CancellationToken
        );

        JsonElement step = Step(outcome.Result, 0);
        Assert.False(step.GetProperty("ok").GetBoolean());
        Assert.Contains("closed or timed out", step.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("deadline", step.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MethodStep_NullParams_IsSentAsNoParams()
    {
        Outcome outcome = await RunBatchAsync(
            """[{"method":"list_windows","params":null}]""",
            Answer((method, _) => new { ok = method }),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["list_windows"], outcome.Methods);
        Assert.Null(outcome.Arguments[0]);
        Assert.True(Step(outcome.Result, 0).GetProperty("ok").GetBoolean(), outcome.Result);
    }

    // The step's own answer timeout equals the batch's remaining time, so the pipe client's timer and the deadline token race:
    // whichever fires first, the step must name the deadline rather than report a pipe timeout.
    [Fact]
    public async Task Batch_Deadline_StopsTheStepInFlight()
    {
        Outcome outcome = await RunBatchAsync(
            """[{"method":"list_windows","params":{"timeoutMs":3000}},{"method":"get_tree"}]""",
            Answer((method, _) => new { ok = method }),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["list_windows"], outcome.Methods);
        JsonElement root = Parse(outcome.Result);
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.Equal(0, root.GetProperty("failedAt").GetInt32());
        Assert.Single(root.GetProperty("steps").EnumerateArray());
        JsonElement step = Step(outcome.Result, 0);
        Assert.False(step.GetProperty("ok").GetBoolean());
        Assert.Equal("batch deadline of 3 s reached", step.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_CallerCancel_PropagatesAsCancellation()
    {
        using var cancel = new CancellationTokenSource();
        Task<Outcome> batch = RunBatchAsync(
            """[{"method":"list_windows"},{"method":"get_tree"}]""",
            Answer((method, _) => new { ok = method }),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10),
            cancel.Token
        );
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch);
    }

    [Fact]
    public async Task ScreenshotStep_ReturnsAPath_NotTheImage()
    {
        byte[] png = await File.ReadAllBytesAsync(ShotFixture, TestContext.Current.CancellationToken);
        string base64 = Convert.ToBase64String(png);
        Outcome outcome = await RunBatchAsync(
            """[{"method":"screenshot","params":{"selector":"#Box"}}]""",
            Answer(
                (_, _) =>
                    new
                    {
                        width = 200,
                        height = 100,
                        scale = 1.0,
                        pngBase64 = base64,
                    }
            ),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["screenshot"], outcome.Methods);
        Assert.Equal("#Box", outcome.Arguments[0]!.Value.GetProperty("selector").GetString());
        Assert.DoesNotContain(base64, outcome.Result, StringComparison.Ordinal);
        JsonElement result = Step(outcome.Result, 0).GetProperty("result");
        Assert.Equal(200, result.GetProperty("width").GetInt32());
        Assert.Equal(100, result.GetProperty("height").GetInt32());
        string path = result.GetProperty("path").GetString()!;
        Assert.Equal(png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScreenshotStep_WithoutAPng_FailsTheStep()
    {
        Outcome outcome = await RunBatchAsync(
            """[{"method":"screenshot","params":{"selector":"#Box"}}]""",
            Answer((_, _) => new { width = 200, height = 100 }),
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken
        );

        JsonElement step = Step(outcome.Result, 0);
        Assert.False(step.GetProperty("ok").GetBoolean());
        Assert.Equal("The client's screenshot of the batch's screenshot step carried no PNG to save", step.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnknownStepShape_FailsNamingTheAcceptedShapes()
    {
        string[] shapes = ["""[{"assert":"nope"}]""", """[{"method":"list_windows","assert":"no_errors"}]""", """[{"selector":"#Box"}]"""];
        foreach (string stepsJson in shapes)
        {
            Outcome outcome = await RunBatchAsync(
                stepsJson,
                Answer((method, _) => new { ok = method }),
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                TestContext.Current.CancellationToken
            );

            Assert.Empty(outcome.Methods);
            JsonElement step = Step(outcome.Result, 0);
            Assert.False(step.GetProperty("ok").GetBoolean());
            string error = step.GetProperty("error").GetString()!;
            Assert.Contains("\"method\"", error, StringComparison.Ordinal);
            Assert.Contains("\"assert\"", error, StringComparison.Ordinal);

            // The result text is JSON an agent reads as written: the step's own braces and quotes are not escaped into ".
            Assert.Contains("{\\\"method\\\": \\\"<pipe method>\\\"", outcome.Result, StringComparison.Ordinal);
        }
    }

    /// <summary>A batch result and the pipe calls the scripted host saw, in order.</summary>
    /// <param name="Result">The JSON string the tool returned.</param>
    /// <param name="Methods">The method of each request the host answered, in order.</param>
    /// <param name="Arguments">Each request's params, or null when it sent none.</param>
    private sealed record Outcome(string Result, List<string> Methods, List<JsonElement?> Arguments);

    private static JsonElement Parse(string result) => JsonDocument.Parse(result).RootElement.Clone();

    private static JsonElement Step(string result, int index) => Parse(result).GetProperty("steps")[index];

    private static Func<string, string, JsonElement?, AutomationResponse> Answer(Func<string, JsonElement?, object> answer) =>
        (id, method, parameters) => AutomationResponse.Success(id, ProtocolSerializer.ToElement(answer(method, parameters)));

    private static Func<string, string, JsonElement?, AutomationResponse> AnsweredWithClientErrors(
        Func<string, JsonElement?, object> answer,
        ClientLogEntry[] errors
    ) =>
        (id, method, parameters) =>
            new AutomationResponse
            {
                Id = id,
                Result = ProtocolSerializer.ToElement(answer(method, parameters)),
                ClientErrors = errors,
            };

    private static Func<string, string, JsonElement?, AutomationResponse> Failing(string code, string message) =>
        (id, _, _) => AutomationResponse.Failure(id, new AutomationError(message, code, null, null));

    /// <summary>
    /// Serves a scripted host answering every request with <paramref name="respond"/> — recorded as it answers — and runs the
    /// steps against it through <see cref="BatchTools.RunAsync"/>, with <paramref name="delay"/> before each answer.
    /// </summary>
    private async Task<Outcome> RunBatchAsync(
        string stepsJson,
        Func<string, string, JsonElement?, AutomationResponse> respond,
        TimeSpan deadline,
        TimeSpan delay,
        CancellationToken callerToken
    )
    {
        string pipeName = $"yaat-scripted-host-{Guid.NewGuid():N}";
        // The host runs to the test's own lifetime, never the batch's caller token: a caller cancel must reach RunAsync, not
        // cut the stub's own advertisement short.
        await StubPipeHost.AdvertiseAsync(DiscoveryDirectory, pipeName, TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        List<string> methods = [];
        _seen = methods;
        List<JsonElement?> arguments = [];
        Task host = StubPipeHost.ServeResponsesAsync(
            pipeName,
            (id, method, parameters) =>
            {
                methods.Add(method);
                arguments.Add(parameters);
                return respond(id, method, parameters);
            },
            delay,
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
            using var document = JsonDocument.Parse(stepsJson);
            string result = await new BatchTools(directory, RecordingFakes.NewSession()).RunAsync(document.RootElement, Pid, deadline, callerToken);
            return new Outcome(result, methods, arguments);
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
                // The stub's delay was cancelled with this test's own timeout; there is nothing left to wait for.
            }
        }
    }
}
