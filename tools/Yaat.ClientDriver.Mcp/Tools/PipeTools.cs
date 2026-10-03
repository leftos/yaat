using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;
using Yaat.ClientDriver.Mcp.Pipe;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>
/// Tools only a YAAT client's automation pipe offers, with no UI Automation counterpart: waiting inside the client for a
/// condition on its controls, and queueing answers for its file dialogs. Each goes to the pid given, or else to the client
/// the last pipe-routed call reached.
/// </summary>
/// <param name="pipes">Finds and caches the YAAT clients' automation pipes, and remembers the client last reached over one.</param>
[McpServerToolType]
public sealed class PipeTools(PipeDirectory pipes)
{
    /// <summary>The shortest wait the host accepts, in ms; it clamps a shorter one up to this.</summary>
    public const int MinWaitMs = 100;

    /// <summary>The longest wait the host accepts, in ms; it clamps a longer one down to this.</summary>
    public const int MaxWaitMs = 30000;

    /// <summary>The longest simulation-state wait <c>wait_until</c> accepts, in ms; a montage take runs about 315 s.</summary>
    public const int MaxWaitUntilMs = 600000;

    private const int DefaultWaitUntilMs = 30000;

    private const string NoTargetMessage = "No YAAT client to target: pass pid, or call a tool on a YAAT client first (launch_yaat, list_windows).";

    /// <summary>How much longer than the host's wait the pipe client waits for its answer, so a full-length wait is never cut short.</summary>
    internal static readonly TimeSpan WaitAnswerMargin = TimeSpan.FromSeconds(5);

    [McpServerTool]
    [Description(
        "Waits inside a YAAT client until a condition holds on the elements a selector matches, polling every 100 ms over the client's "
            + "automation pipe. Conditions: exists, not_exists, visible, enabled, text_equals and text_contains (with text; equals is "
            + "case-sensitive, contains is not), count_equals (with count). timeoutMs is clamped to 100–30000 (default 5000); a condition "
            + "that does not hold in time fails with TIMEOUT and the selector's last match count. Goes to pid, or with pid 0 to the client "
            + "the last pipe call reached; the result ends with (N matches, pipe)."
    )]
    public async Task<string> WaitForAsync(
        [Description("Selector over the client's visual tree: #Name, a type name such as Button, or a chain such as StackPanel > TextBox.")]
            string selector,
        [Description("exists, not_exists, visible, enabled, text_equals, text_contains or count_equals.")] string condition,
        CancellationToken cancellationToken,
        [Description("The YAAT client's process id, or 0 for the client the last pipe call reached.")] int pid = 0,
        [Description("The text text_equals or text_contains looks for; ignored by the other conditions.")] string text = "",
        [Description("The number of matches count_equals waits for; ignored by the other conditions.")] int count = 0,
        [Description("How long to wait, in ms, clamped to 100–30000.")] int timeoutMs = 5000
    )
    {
        int target = TargetPid(pipes, pid);
        (int hostMs, TimeSpan request) = WaitForTimeouts(timeoutMs);
        object parameters = WaitForParams(selector, condition, text, count, hostMs);
        WaitForResult result = await PipeCalls
            .SendForPidAsync<WaitForResult>(pipes, target, ProtocolMethods.WaitFor, parameters, request, cancellationToken)
            .ConfigureAwait(false);
        return $"{condition} held on '{selector}' after {result.ElapsedMs} ms ({result.MatchCount} matches, pipe)";
    }

    [McpServerTool]
    [Description(
        "Waits inside a YAAT client until a condition list holds on the simulation state it sees — an aircraft on the ground or landed, "
            + "in a phase or out of it, with an empty conditional queue, a terminal log line matching, or the scenario clock reaching a "
            + "time — polling every 100 ms over the client's automation pipe. conditions is a non-empty array of {kind, ...} objects: "
            + "on_ground, landed, phase_is and phase_is_not (each with callsign, and phase for the phase kinds), queue_empty (callsign), "
            + "log_matches (pattern, a .NET regex matched case-insensitively, and an optional callsign), and sim_seconds (atLeast). mode is "
            + "\"any\" (the default: one condition is enough) or \"all\". timeoutMs is clamped to 100–600000 (default 30000). screenshot is an "
            + "object naming what to capture by selector: {\"windowSelector\": \"<selector>\"} for a window's client area or {\"selector\": "
            + "\"<selector>\"} for one element (not an element id from find_elements). then is an array of client actions run "
            + "at that poll, in order: {action: \"pause\"} or {action: \"set_rate\", rate: <one of the client's sim rates>}. A timeout is a "
            + "result, not an error: the text says met or not met, lists each condition's last value, then each action's outcome, and the "
            + "saved screenshot's path. Goes to pid, or with pid 0 to the client the last pipe call reached; the first line ends with (pipe)."
    )]
    public async Task<string> WaitUntilAsync(
        [Description("A non-empty JSON array of {kind, ...} condition objects; the tool description lists the kinds and their fields.")]
            JsonElement conditions,
        CancellationToken cancellationToken,
        [Description("The YAAT client's process id, or 0 for the client the last pipe call reached.")] int pid = 0,
        [Description("\"any\" (the default) to be met by one condition, or \"all\" to need every one.")] string mode = "any",
        [Description("How long to wait, in ms, clamped to 100–600000.")] int timeoutMs = DefaultWaitUntilMs,
        [Description("Optional capture once the wait ends: {\"windowSelector\": \"<selector>\"} or {\"selector\": \"<selector>\"}.")]
            JsonElement? screenshot = null,
        [Description("Client actions run at the match, in order: {action: \"pause\"} or {action: \"set_rate\", rate: n}.")] JsonElement? then = null
    )
    {
        int target = TargetPid(pipes, pid);
        (int hostMs, TimeSpan request) = WaitUntilTimeouts(timeoutMs);
        object parameters = new
        {
            conditions,
            mode,
            timeoutMs = hostMs,
            screenshot,
            then,
        };
        WaitUntilResult result = await PipeCalls
            .SendForPidAsync<WaitUntilResult>(pipes, target, ProtocolMethods.WaitUntil, parameters, request, cancellationToken)
            .ConfigureAwait(false);
        return FormatWaitUntil(result);
    }

    [McpServerTool]
    [Description(
        "Queues one answer for the next file dialog a YAAT client opens in automation mode: a path, or cancel. Call it before the action "
            + "that opens the dialog — the dialog takes the oldest queued answer at once and fails when none is queued; queue one answer "
            + "per dialog, in the order the dialogs open. Goes over the client's automation pipe to pid, or with pid 0 to the client the "
            + "last pipe call reached; the result counts the answers waiting and ends with (N waiting, pipe)."
    )]
    public async Task<string> QueueFilePickAsync(
        CancellationToken cancellationToken,
        [Description("The path the dialog returns; empty when cancel is true.")] string path = "",
        [Description("True to have the dialog cancelled instead of returning a path.")] bool cancel = false,
        [Description("The YAAT client's process id, or 0 for the client the last pipe call reached.")] int pid = 0
    )
    {
        int target = TargetPid(pipes, pid);
        QueueFilePickResult result = await PipeCalls
            .SendForPidAsync<QueueFilePickResult>(
                pipes,
                target,
                ProtocolMethods.QueueFilePick,
                FilePickParams(path, cancel),
                PipeClient.RequestTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
        string queued = cancel ? "a cancel" : path;
        return $"queued {queued} ({result.Queued} waiting, pipe)";
    }

    /// <summary>
    /// The wait the host runs for <paramref name="timeoutMs"/>, clamped to its own <see cref="MinWaitMs"/>–<see cref="MaxWaitMs"/>,
    /// and how long the pipe client waits for its answer: that wait plus a margin, so the host's own TIMEOUT always arrives first.
    /// </summary>
    public static (int HostMs, TimeSpan Request) WaitForTimeouts(int timeoutMs)
    {
        int hostMs = Math.Clamp(timeoutMs, MinWaitMs, MaxWaitMs);
        return (hostMs, TimeSpan.FromMilliseconds(hostMs) + WaitAnswerMargin);
    }

    /// <summary>
    /// The simulation-state wait the host runs for <paramref name="timeoutMs"/>, clamped to <see cref="MinWaitMs"/>–
    /// <see cref="MaxWaitUntilMs"/>, and how long the pipe client waits for its answer: that wait plus <see cref="WaitAnswerMargin"/>,
    /// so the host's own answer — met or not — always arrives before the client's timer cuts it.
    /// </summary>
    public static (int HostMs, TimeSpan Request) WaitUntilTimeouts(int timeoutMs)
    {
        int hostMs = Math.Clamp(timeoutMs, MinWaitMs, MaxWaitUntilMs);
        return (hostMs, TimeSpan.FromMilliseconds(hostMs) + WaitAnswerMargin);
    }

    /// <summary>
    /// The text an agent reads for a <c>wait_until</c> answer: whether the conditions were met, when, and every condition's last
    /// value, then each action's outcome in order and the path of the screenshot taken at the match when there was one.
    /// </summary>
    private static string FormatWaitUntil(WaitUntilResult result)
    {
        string header = result.Met
            ? $"met after {result.ElapsedMs} ms at sim {SimSeconds(result.SimSeconds)} s: held {Held(result)}"
            : $"not met in {result.ElapsedMs} ms at sim {SimSeconds(result.SimSeconds)} s";
        List<string> lines = [header + " (pipe)"];
        lines.AddRange(result.Last.Select(entry => $"- [{entry.Index}] {entry.Kind}: {entry.Value}"));
        if (result.Then is { } actions)
        {
            lines.AddRange(actions.Select(ActionLine));
        }

        if (result.ScreenshotError is { } error)
        {
            lines.Add($"screenshot failed: {error}");
        }
        else if (result.Screenshot is { } shot)
        {
            try
            {
                CaptureResult saved = InspectTools.SavePipeShot(shot, 0, "the wait_until screenshot at the match");
                lines.Add($"screenshot: {saved.Path} ({saved.Width}x{saved.Height})");
            }
            catch (McpException ex)
            {
                // The wait's own answer is what the caller is here for; an undecodable PNG costs only the screenshot line.
                lines.Add($"screenshot failed: {ex.Message}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>One <c>then</c> action's line: <c>ok</c>, or the refusal it failed with — <c>no reason given</c> when it named none.</summary>
    private static string ActionLine(WaitUntilActionResult action)
    {
        if (action.Ok)
        {
            return $"then {action.Action}: ok";
        }

        string reason = action.Error ?? "no reason given";
        return $"then {action.Action}: failed — {reason}";
    }

    /// <summary>The scenario-elapsed seconds as the client's own <c>last</c> values render them: one decimal, invariant.</summary>
    private static string SimSeconds(double seconds) => seconds.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>The kind of each condition that held, in the order the conditions were given.</summary>
    private static string Held(WaitUntilResult result) =>
        string.Join(", ", result.Last.Where(entry => result.Held.Contains(entry.Index)).Select(entry => entry.Kind));

    /// <summary>
    /// The pid a pipe-only tool goes to: <paramref name="pid"/> when given, else the client the last pipe call reached. Shared
    /// by every pipe-only tool, batch_drive included, so an untargeted call has one meaning across all of them.
    /// </summary>
    /// <param name="pipes">The directory whose remembered target answers a pid of 0.</param>
    /// <param name="pid">The caller's pid, or 0 for the remembered one.</param>
    /// <exception cref="McpException">The caller gave no pid and no pipe call has succeeded yet.</exception>
    internal static int TargetPid(PipeDirectory pipes, int pid)
    {
        if (pid != 0)
        {
            return pid;
        }

        return pipes.LastTargetPid ?? throw new McpException(NoTargetMessage);
    }

    /// <summary>The wait_for params: text only for the text conditions, count only for count_equals, as the host reads them.</summary>
    internal static object WaitForParams(string selector, string condition, string text, int count, int timeoutMs) =>
        condition.Trim().ToLowerInvariant() switch
        {
            "text_equals" or "text_contains" => new
            {
                selector,
                condition,
                text,
                timeoutMs,
            },
            "count_equals" => new
            {
                selector,
                condition,
                count,
                timeoutMs,
            },
            _ => new
            {
                selector,
                condition,
                timeoutMs,
            },
        };

    /// <summary>
    /// The queue_file_pick params: a non-empty path and a true cancel each sent when given, so the host's own INVALID_PARAM
    /// answers both, neither, and a blank path.
    /// </summary>
    private static object FilePickParams(string path, bool cancel) =>
        (path.Length > 0, cancel) switch
        {
            (true, true) => new { path, cancel },
            (true, false) => new { path },
            (false, true) => new { cancel },
            (false, false) => new { },
        };
}
