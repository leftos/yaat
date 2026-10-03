using System.ComponentModel;
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
