using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;
using Yaat.ClientDriver.Mcp.Pipe;
using Yaat.ClientDriver.Mcp.Recording;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>
/// Tools only a YAAT client's automation pipe offers, with no UI Automation counterpart: waiting inside the client for a
/// condition on its controls, and queueing answers for its file dialogs. Each goes to the pid given, or else to the client
/// the last pipe-routed call reached.
/// </summary>
/// <param name="pipes">Finds and caches the YAAT clients' automation pipes, and remembers the client last reached over one.</param>
/// <param name="recordings">The server's one recording, which <c>wait_until</c>'s <c>stop_recording</c> action stops.</param>
[McpServerToolType]
public sealed class PipeTools(PipeDirectory pipes, RecordingSession recordings)
{
    /// <summary>The <c>then</c> action this server runs itself, once the client's answer is back; the client never sees it.</summary>
    private const string StopRecordingAction = "stop_recording";

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
            + "at that poll, in order: {action: \"pause\"} or {action: \"set_rate\", rate: <one of the client's sim rates>}; "
            + "{action: \"stop_recording\"} is run by this server after the client's answer, only when met: it adds a final 'wait_until met' "
            + "mark at the match's sim time and stops the record_start recording that was running when the wait began (a recording "
            + "started during the wait goes on). A timeout is a result, not an error: the text "
            + "says met or not met, lists each condition's last value, then each action's outcome, and the saved screenshot's path. Goes to "
            + "pid, or with pid 0 to the client the last pipe call reached; the first line ends with (pipe)."
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
        [Description("Actions run at the match, in order: {action: \"pause\"}, {action: \"set_rate\", rate: n} or {action: \"stop_recording\"}.")]
            JsonElement? then = null
    )
    {
        int target = TargetPid(pipes, pid);
        (int hostMs, TimeSpan request) = WaitUntilTimeouts(timeoutMs);
        // stop_recording stops the recording running now, never one started while the wait goes on.
        Guid? recordingId = recordings.RecordingId;
        (JsonElement? clientThen, bool stopRecording) = SplitStopRecording(then);
        object parameters = new
        {
            conditions,
            mode,
            timeoutMs = hostMs,
            screenshot,
            then = clientThen,
        };
        WaitUntilResult result = await PipeCalls
            .SendForPidAsync<WaitUntilResult>(pipes, target, ProtocolMethods.WaitUntil, parameters, request, cancellationToken)
            .ConfigureAwait(false);
        string? stopLine = stopRecording ? await StopRecordingLineAsync(result, recordingId, cancellationToken).ConfigureAwait(false) : null;
        return FormatWaitUntil(result, stopLine);
    }

    /// <summary>
    /// <paramref name="then"/> without its <c>stop_recording</c> actions, which the client does not accept, and whether there
    /// were any; null when nothing is left for the client.
    /// </summary>
    internal static (JsonElement? ClientThen, bool StopRecording) SplitStopRecording(JsonElement? then)
    {
        if (then is not { ValueKind: JsonValueKind.Array } actions)
        {
            return (then, false);
        }

        List<JsonElement> kept = [.. actions.EnumerateArray().Where(action => !IsStopRecording(action))];
        if (kept.Count == actions.GetArrayLength())
        {
            return (then, false);
        }

        return ((kept.Count == 0) ? null : JsonSerializer.SerializeToElement(kept), true);
    }

    private static bool IsStopRecording(JsonElement action) =>
        (action.ValueKind == JsonValueKind.Object)
        && action.TryGetProperty("action", out JsonElement name)
        && (name.ValueKind == JsonValueKind.String)
        && string.Equals(name.GetString(), StopRecordingAction, StringComparison.Ordinal);

    /// <summary>
    /// Runs <c>stop_recording</c> once the client has answered: only when met, with a final mark at the match's sim time. Every
    /// outcome is a line of the answer, never an error, since the wait itself succeeded.
    /// </summary>
    private async Task<string> StopRecordingLineAsync(WaitUntilResult result, Guid? recordingId, CancellationToken ct)
    {
        if (!result.Met)
        {
            return $"then {StopRecordingAction}: not run — the wait was not met, so the recording goes on";
        }

        if (recordingId is not { } id)
        {
            return $"then {StopRecordingAction}: failed — no recording was running when the wait began";
        }

        RecordingStopResult? stopped;
        try
        {
            stopped = await recordings.StopAtMatchAsync(id, result.SimSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or McpException
                        or OperationCanceledException
                        or Win32Exception
                        or InvalidOperationException
            )
        {
            return $"then {StopRecordingAction}: failed — {ex.Message}";
        }

        return (stopped is null)
            ? $"then {StopRecordingAction}: failed — the recording that was running when the wait began has ended"
            : StoppedLine(stopped);
    }

    /// <summary>The <c>stop_recording</c> line for a stop that ran: ok with its summary, or failed with what went wrong.</summary>
    private static string StoppedLine(RecordingStopResult stopped)
    {
        if ((stopped.Problem is null) && stopped.Mp4Exists)
        {
            string note = (stopped.Note is { } ended) ? $"; {ended}" : string.Empty;
            return $"then {StopRecordingAction}: ok — {stopped.Summary}{note}";
        }

        string mp4 = stopped.Mp4Exists ? $" (an MP4 exists: {stopped.Summary})" : $" (no MP4 at {stopped.Paths.Mp4})";
        return $"then {StopRecordingAction}: failed — {stopped.Problem ?? "the recording ended"}{mp4}";
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
    /// value, then each action's outcome in order (the client's, then this server's <c>stop_recording</c> line when one was
    /// asked for) and the path of the screenshot taken at the match when there was one.
    /// </summary>
    private static string FormatWaitUntil(WaitUntilResult result, string? stopRecordingLine)
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

        if (stopRecordingLine is not null)
        {
            lines.Add(stopRecordingLine);
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
