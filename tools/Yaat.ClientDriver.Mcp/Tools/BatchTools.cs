using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;
using Yaat.ClientDriver.Mcp.Pipe;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>
/// <c>batch_drive</c>: an ordered list of steps against one YAAT client, one pipe call per step, stopping at the first
/// failure — so a UI-path scenario needs neither a script nor a hard-coded sleep between its calls. Every step addresses
/// elements by selector, never by element id: an id handed out before the batch is stale by the time a later step runs.
/// </summary>
/// <param name="pipes">Finds and caches the YAAT clients' automation pipes, and remembers the client last reached over one.</param>
[McpServerToolType]
public sealed class BatchTools(PipeDirectory pipes)
{
    /// <summary>How long a whole batch may run, in seconds, before its steps are cut short and it fails.</summary>
    public const int MaxBatchSeconds = 300;

    private const int MinSteps = 1;
    private const int MaxSteps = 100;
    private const int DefaultWaitMs = 1000;

    /// <summary>
    /// The result's JSON settings: no indentation, and quotes, angle brackets and ampersands left as written rather than
    /// escaped to <c>"</c> and friends, so a step's error reads as the message batch_drive wrote it.
    /// </summary>
    private static readonly JsonSerializerOptions ResultJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string NodeIdRefusal = "steps address elements by selector; nodeId is not accepted in batch_drive";

    [McpServerTool]
    [Description(
        "Runs an ordered list of automation steps against one YAAT client over its automation pipe, one pipe call per step, and "
            + "stops at the first failure. steps is a JSON array of 1-100 objects, each one of: {\"method\": \"<pipe method>\", "
            + "\"params\": {...}} — the params go to the host unchanged, so any pipe method works and a method added later needs no "
            + "change here (a numeric \"timeoutMs\" in the params sets how long its answer may take); {\"assert\": \"wait\", "
            + "\"selector\": ..., \"condition\": ...} — as wait_for, with \"timeoutMs\" defaulting to 1000; or {\"assert\": "
            + "\"no_errors\"} — fails when the client logged errors since the batch started or the previous no_errors step. Steps "
            + "address elements by selector: a step whose params carry nodeId or windowNodeId is refused. A screenshot step's PNG is "
            + "saved to the shots folder and its result carries the file's path, width and height instead of the image. The whole "
            + "batch stops after 300 s. Returns a JSON result — {\"passed\": ..., \"steps\": [...], \"failedAt\": ...} — never an MCP "
            + "error, so a failed step is read from the result. Goes to pid, or with pid 0 to the client the last pipe call reached."
    )]
    public Task<string> BatchDriveAsync(
        [Description("A JSON array of 1-100 step objects; the tool description gives the three step shapes.")] JsonElement steps,
        CancellationToken cancellationToken,
        [Description("The YAAT client's process id, or 0 for the client the last pipe call reached.")] int pid = 0
    ) => RunAsync(steps, PipeTools.TargetPid(pipes, pid), TimeSpan.FromSeconds(MaxBatchSeconds), cancellationToken);

    /// <summary>
    /// Runs <paramref name="steps"/> to completion against <paramref name="pid"/>, cutting every step's pipe call to the time
    /// left of <paramref name="deadline"/>: the tool's own <see cref="MaxBatchSeconds"/>, or a shorter one a test passes.
    /// </summary>
    /// <param name="steps">The step array, already the caller's raw JSON.</param>
    /// <param name="pid">The client's process id, already resolved.</param>
    /// <param name="deadline">How long the whole batch may run.</param>
    /// <param name="cancellationToken">The caller's own token; its cancel propagates rather than failing a step.</param>
    /// <returns>The batch's JSON result, whether it passed or failed.</returns>
    /// <exception cref="McpException"><paramref name="steps"/> is not an array of <see cref="MinSteps"/>-<see cref="MaxSteps"/> steps.</exception>
    public async Task<string> RunAsync(JsonElement steps, int pid, TimeSpan deadline, CancellationToken cancellationToken)
    {
        JsonElement[] parsed = ParseSteps(steps);
        var deadlineWatch = Stopwatch.StartNew();
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(deadline);
        var run = new BatchRun(pipes, pid, deadline, deadlineCts, cancellationToken, deadlineWatch);
        return await run.ExecuteAsync(parsed).ConfigureAwait(false);
    }

    /// <summary>
    /// How long one method step waits for its answer: a numeric <c>timeoutMs</c> in its params plus the same answer margin
    /// <see cref="PipeTools.WaitForTimeouts"/> adds, else the pipe client's own request timeout.
    /// </summary>
    /// <param name="parameters">The step's params, or null when it sent none.</param>
    public static TimeSpan StepTimeoutFor(JsonElement? parameters)
    {
        int? requested = ((parameters is { } element) && (element.ValueKind == JsonValueKind.Object)) ? ReadInt(element, "timeoutMs") : null;
        return (requested is int ms) ? TimeSpan.FromMilliseconds(ms) + PipeTools.WaitAnswerMargin : PipeClient.RequestTimeout;
    }

    private static JsonElement[] ParseSteps(JsonElement steps)
    {
        if (steps.ValueKind != JsonValueKind.Array)
        {
            throw new McpException(
                $"steps must be a JSON array of {MinSteps} to {MaxSteps} step objects; got a {steps.ValueKind.ToString().ToLowerInvariant()}"
            );
        }

        int count = steps.GetArrayLength();
        if ((count < MinSteps) || (count > MaxSteps))
        {
            throw new McpException($"steps must hold {MinSteps} to {MaxSteps} steps; got {count}");
        }

        return [.. steps.EnumerateArray()];
    }

    /// <summary>The step shape's refusal: the message names every shape batch_drive accepts.</summary>
    private static string UnknownStepMessage(int index) =>
        $"step {index} is not a step batch_drive knows: give {{\"method\": \"<pipe method>\", \"params\": {{...}}}}, "
        + "{\"assert\": \"wait\", \"selector\": ..., \"condition\": ...}, or {\"assert\": \"no_errors\"}";

    /// <summary>
    /// The step's <paramref name="name"/> as text, when it is a non-empty JSON string; false leaves <paramref name="value"/> empty.
    /// </summary>
    private static bool TryText(JsonElement step, string name, out string value)
    {
        if (
            step.TryGetProperty(name, out JsonElement element)
            && (element.ValueKind == JsonValueKind.String)
            && (element.GetString() is { Length: > 0 } text)
        )
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// The step's <paramref name="name"/> as an integer, never negative and clamped to <see cref="int.MaxValue"/>; null when it
    /// is absent or not a number.
    /// </summary>
    private static int? ReadInt(JsonElement step, string name) =>
        (step.TryGetProperty(name, out JsonElement value) && (value.ValueKind == JsonValueKind.Number))
            ? (int)Math.Clamp(value.GetDouble(), 0, int.MaxValue)
            : null;

    /// <summary>The step's <paramref name="name"/> as text, empty when it is absent or not a string.</summary>
    private static string ReadText(JsonElement step, string name) =>
        (step.TryGetProperty(name, out JsonElement value) && (value.ValueKind == JsonValueKind.String))
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>One step's outcome: which shape it was, whether it passed, and its pipe answer or its error.</summary>
    private sealed record StepOutcome(string? Method, string? Assert, bool Ok, JsonElement? Result, string? Error)
    {
        public static StepOutcome Passed(string? method, string? assert, JsonElement result) => new(method, assert, true, result, null);

        public static StepOutcome Failed(string? method, string? assert, string error) => new(method, assert, false, null, error);
    }

    /// <summary>
    /// One batch's in-flight state: the step loop, the shape each step is dispatched to, and the JSON result it builds. The
    /// deadline is one clock and one linked <see cref="CancellationTokenSource"/> for the whole batch: the stopwatch started
    /// before the token was armed tells a step cut by the deadline from one the pipe client cut with its own timer, however
    /// close the two land.
    /// </summary>
    /// <param name="pipes">The directory the pipe calls go through.</param>
    /// <param name="pid">The client the batch drives.</param>
    /// <param name="deadline">How long the whole batch may run.</param>
    /// <param name="deadlineCts">The batch's token, cancelled at the deadline or by the caller.</param>
    /// <param name="callerToken">The caller's own token, told apart from the deadline so its cancel is never reported as one.</param>
    /// <param name="deadlineWatch">The batch's clock, started before <paramref name="deadlineCts"/> was armed.</param>
    private sealed class BatchRun(
        PipeDirectory pipes,
        int pid,
        TimeSpan deadline,
        CancellationTokenSource deadlineCts,
        CancellationToken callerToken,
        Stopwatch deadlineWatch
    )
    {
        private int _errorsSeen;
        private int _errorsOmittedSeen;

        /// <summary>Runs every step in order and returns the batch's JSON result, stopping at the first failure.</summary>
        public async Task<string> ExecuteAsync(JsonElement[] steps)
        {
            PinErrorCheckpoint();
            var results = new JsonArray();
            int? failedAt = null;
            for (int index = 0; index < steps.Length; index++)
            {
                var stepWatch = Stopwatch.StartNew();
                StepOutcome outcome = await RunStepAsync(steps[index], index).ConfigureAwait(false);
                results.Add(StepJson(index, outcome, stepWatch.ElapsedMilliseconds));
                if (!outcome.Ok)
                {
                    failedAt = index;
                    break;
                }
            }

            var batch = new JsonObject { ["passed"] = failedAt is null, ["steps"] = results };
            if (failedAt is int failed)
            {
                batch["failedAt"] = failed;
            }

            return batch.ToJsonString(ResultJson);
        }

        private async Task<StepOutcome> RunStepAsync(JsonElement step, int index)
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                return StepOutcome.Failed(null, null, UnknownStepMessage(index));
            }

            bool hasMethod = TryText(step, "method", out string method);
            bool hasAssert = TryText(step, "assert", out string assert);
            if (hasMethod == hasAssert)
            {
                return StepOutcome.Failed(null, null, UnknownStepMessage(index));
            }

            string named = hasMethod ? method : assert;
            string? methodName = hasMethod ? named : null;
            string? assertName = hasMethod ? null : named;
            TimeSpan remaining = deadline - deadlineWatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return StepOutcome.Failed(methodName, assertName, DeadlineMessage());
            }

            return hasMethod
                ? await MethodStepAsync(step, named, remaining).ConfigureAwait(false)
                : await AssertStepAsync(step, index, named, remaining).ConfigureAwait(false);
        }

        private async Task<StepOutcome> MethodStepAsync(JsonElement step, string method, TimeSpan remaining)
        {
            if (!TryReadParameters(step, out JsonElement? parameters, out string? refusal))
            {
                return StepOutcome.Failed(method, null, refusal);
            }

            try
            {
                JsonElement answer = await PipeCalls
                    .SendForPidAsync<JsonElement>(
                        pipes,
                        pid,
                        method,
                        parameters,
                        CutToRemaining(StepTimeoutFor(parameters), remaining),
                        deadlineCts.Token
                    )
                    .ConfigureAwait(false);
                return string.Equals(method, ProtocolMethods.Screenshot, StringComparison.Ordinal)
                    ? ScreenshotOutcome(method, answer)
                    : StepOutcome.Passed(method, null, answer);
            }
            catch (McpException) when (DeadlineReached())
            {
                return StepOutcome.Failed(method, null, DeadlineMessage());
            }
            catch (McpException ex)
            {
                return StepOutcome.Failed(method, null, ex.Message);
            }
            catch (OperationCanceledException) when (DeadlineReached())
            {
                return StepOutcome.Failed(method, null, DeadlineMessage());
            }
        }

        /// <summary>
        /// The step's own params, or the error that refuses them: every element is named by a selector, so a node id — which
        /// goes stale between steps — is refused here rather than reaching the host.
        /// </summary>
        private static bool TryReadParameters(JsonElement step, out JsonElement? parameters, out string refusal)
        {
            parameters = null;
            refusal = string.Empty;
            if (!step.TryGetProperty("params", out JsonElement raw))
            {
                return true;
            }

            if (raw.ValueKind == JsonValueKind.Null)
            {
                // JSON null is the same as no params at all: the host is told nothing rather than that its arguments are null.
                return true;
            }

            if (raw.ValueKind != JsonValueKind.Object)
            {
                refusal = "\"params\" must be a JSON object of pipe arguments";
                return false;
            }

            if (raw.TryGetProperty("nodeId", out _) || raw.TryGetProperty("windowNodeId", out _))
            {
                refusal = NodeIdRefusal;
                return false;
            }

            parameters = raw;
            return true;
        }

        /// <summary>
        /// A screenshot step's result: the PNG the client rendered saved like the screenshot tool saves one, so the batch result
        /// carries its path and pixel size instead of the whole image. A missing or undecodable PNG is
        /// <see cref="InspectTools.SavePipeShot"/>'s to refuse, as it is for the screenshot tool.
        /// </summary>
        private static StepOutcome ScreenshotOutcome(string method, JsonElement answer)
        {
            ScreenshotResult? shot = ProtocolSerializer.Deserialize<ScreenshotResult>(answer.GetRawText());
            CaptureResult saved = InspectTools.SavePipeShot(shot, 0, "the batch's screenshot step");
            JsonElement summary = JsonSerializer.SerializeToElement(
                new
                {
                    path = saved.Path,
                    width = saved.Width,
                    height = saved.Height,
                },
                ResultJson
            );
            return StepOutcome.Passed(method, null, summary);
        }

        private async Task<StepOutcome> AssertStepAsync(JsonElement step, int index, string assert, TimeSpan remaining)
        {
            if (string.Equals(assert, "wait", StringComparison.Ordinal))
            {
                return await WaitAssertAsync(step, index, remaining).ConfigureAwait(false);
            }

            return string.Equals(assert, "no_errors", StringComparison.Ordinal)
                ? NoErrorsOutcome()
                : StepOutcome.Failed(null, assert, UnknownStepMessage(index));
        }

        private async Task<StepOutcome> WaitAssertAsync(JsonElement step, int index, TimeSpan remaining)
        {
            string selector = ReadText(step, "selector");
            string condition = ReadText(step, "condition");
            if ((selector.Length == 0) || (condition.Length == 0))
            {
                return StepOutcome.Failed(null, "wait", $"step {index}: an \"assert\": \"wait\" step needs a \"selector\" and a \"condition\"");
            }

            (int hostMs, TimeSpan request) = PipeTools.WaitForTimeouts(ReadInt(step, "timeoutMs") ?? DefaultWaitMs);
            object parameters = PipeTools.WaitForParams(selector, condition, ReadText(step, "text"), ReadInt(step, "count") ?? 0, hostMs);
            try
            {
                JsonElement answer = await PipeCalls
                    .SendForPidAsync<JsonElement>(
                        pipes,
                        pid,
                        ProtocolMethods.WaitFor,
                        parameters,
                        CutToRemaining(request, remaining),
                        deadlineCts.Token
                    )
                    .ConfigureAwait(false);
                return StepOutcome.Passed(null, "wait", answer);
            }
            catch (McpException) when (DeadlineReached())
            {
                return StepOutcome.Failed(null, "wait", DeadlineMessage());
            }
            catch (McpException ex)
            {
                return StepOutcome.Failed(null, "wait", ex.Message);
            }
            catch (OperationCanceledException) when (DeadlineReached())
            {
                return StepOutcome.Failed(null, "wait", DeadlineMessage());
            }
        }

        /// <summary>
        /// The <c>no_errors</c> assert: fails when the client logged errors since the batch started, or since the last
        /// <c>no_errors</c> checkpoint, and moves the checkpoint to now either way.
        /// </summary>
        private StepOutcome NoErrorsOutcome()
        {
            (IReadOnlyList<ClientLogEntry> entries, int omitted) = PipeCallErrors.Snapshot();
            int fresh = (entries.Count - _errorsSeen) + (omitted - _errorsOmittedSeen);
            string listed = string.Join("; ", entries.Skip(_errorsSeen).Select(entry => entry.Message));
            PinErrorCheckpoint();
            if (fresh <= 0)
            {
                return StepOutcome.Passed(null, "no_errors", JsonSerializer.SerializeToElement(new { errors = 0 }, ResultJson));
            }

            string detail = (listed.Length > 0) ? listed : "the answers counted them without listing them";
            return StepOutcome.Failed(null, "no_errors", $"client logged {fresh} error(s) during this batch: {detail}");
        }

        private void PinErrorCheckpoint()
        {
            (IReadOnlyList<ClientLogEntry> entries, int omitted) = PipeCallErrors.Snapshot();
            _errorsSeen = entries.Count;
            _errorsOmittedSeen = omitted;
        }

        private static JsonObject StepJson(int index, StepOutcome outcome, long elapsedMs)
        {
            var step = new JsonObject
            {
                ["index"] = index,
                ["ok"] = outcome.Ok,
                ["elapsedMs"] = elapsedMs,
            };
            if (outcome.Method is { } method)
            {
                step["method"] = method;
            }

            if (outcome.Assert is { } assert)
            {
                step["assert"] = assert;
            }

            if (outcome.Result is JsonElement result)
            {
                step["result"] = JsonSerializer.SerializeToNode(result, ResultJson);
            }

            if (outcome.Error is { } error)
            {
                step["error"] = error;
            }

            return step;
        }

        /// <summary>
        /// True when the batch's deadline cut the step, whether the deadline token fired or the step's own timer ran out at
        /// about the same moment: the caller's cancel is never this batch's failure, and a step past the deadline is never
        /// reported as a pipe timeout.
        /// </summary>
        private bool DeadlineReached() =>
            !callerToken.IsCancellationRequested && (deadlineCts.IsCancellationRequested || (deadlineWatch.Elapsed >= deadline));

        private string DeadlineMessage() => $"batch deadline of {deadline.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s reached";

        private static TimeSpan CutToRemaining(TimeSpan request, TimeSpan remaining) => (request < remaining) ? request : remaining;
    }
}
