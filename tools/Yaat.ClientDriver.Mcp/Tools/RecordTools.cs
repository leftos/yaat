using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;
using Yaat.ClientDriver.Mcp.Pipe;
using Yaat.ClientDriver.Mcp.Recording;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>
/// Recording one window to an MP4 while a session drives it: start, marks at the moments worth finding again, and stop. The
/// recorder captures the window by handle through Windows.Graphics.Capture and pipes it into ffmpeg, cropped to the client
/// area; a YAAT client's own audio (the solo pilot's voice) goes in beside it. One recording runs at a time.
/// </summary>
/// <param name="session">The server's one recording.</param>
/// <param name="backend">Finds the windows of a process without an automation pipe.</param>
/// <param name="registry">Resolves a window element id from list_windows.</param>
/// <param name="pipes">Finds the YAAT clients' automation pipes, for their windows and their scenario clock.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
[McpServerToolType]
public sealed class RecordTools(
    RecordingSession session,
    IRecordingBackend backend,
    ElementRegistry registry,
    PipeDirectory pipes,
    ILogger<RecordTools> logger
)
{
    /// <summary>The highest frame rate record_start takes.</summary>
    public const int MaxFps = 60;

    private const int DefaultFps = 30;

    private static readonly TimeSpan SimTimeWait = TimeSpan.FromSeconds(2);

    [McpServerTool]
    [Description(
        "Starts recording one window to an MP4: the client area only, at fps frames per second, with the process's own audio when audio "
            + "is on. Give exactly one of pid (the process's main window: the first top-level window with a native handle, preferring a "
            + "title starting YAAT or CRC) or windowElementId (a window id from list_windows); both or neither is INVALID_PARAM. out "
            + "must name an .mp4 file (another extension or an existing folder is INVALID_PARAM), defaults to "
            + ".tmp/client-driver/recordings/<pid>-<yyyyMMdd-HHmmss>.mp4 under the server's working folder and is never "
            + "overwritten; fps is 1–60 (default 30); audio defaults to on for a YAAT client with an automation pipe and off otherwise "
            + "(CRC). Refuses a minimized window, a second recording while one runs (one that ended on its own is closed instead), and "
            + "a missing ffmpeg; a pipeline that dies at once is reported with its exit code and the recorder's last log lines. Marks go to "
            + "<clip>-marks.json beside the MP4; a recording ends at record_stop, at wait_until's stop_recording action, or after one hour."
    )]
    public async Task<string> RecordStartAsync(
        CancellationToken cancellationToken,
        [Description("The process whose main window to record, or 0 when windowElementId names the window.")] int pid = 0,
        [Description("A window id from list_windows, or empty when pid names the process.")] string windowElementId = "",
        [Description("The MP4 to write; empty for .tmp/client-driver/recordings/<pid>-<yyyyMMdd-HHmmss>.mp4.")] string @out = "",
        [Description("Frames per second, 1–60.")] int fps = DefaultFps,
        [Description("Record the process's audio: null (the default) for on with a YAAT automation pipe and off without one.")] bool? audio = null
    )
    {
        if ((pid != 0) == (windowElementId.Length > 0))
        {
            throw new McpException("INVALID_PARAM: give exactly one of pid and windowElementId.");
        }

        if (fps is < 1 or > MaxFps)
        {
            throw new McpException($"INVALID_PARAM: fps must be 1–{MaxFps}; got {fps}.");
        }

        if (@out.Length > 0)
        {
            RequireMp4File(@out);
        }

        string ffmpeg = await session.RequireFfmpegAsync(cancellationToken).ConfigureAwait(false);
        RecordingTarget target =
            (pid != 0)
                ? await ResolvePidAsync(pid, cancellationToken).ConfigureAwait(false)
                : await ResolveElementAsync(windowElementId, cancellationToken).ConfigureAwait(false);
        RecordingStarted started = await session
            .StartAsync(new RecordingRequest(target, ffmpeg, @out, fps, audio ?? target.HasPipe), cancellationToken)
            .ConfigureAwait(false);
        return DescribeStart(started);
    }

    [McpServerTool]
    [Description(
        "Marks the current moment of the running recording with a label, and rewrites <clip>-marks.json with it: the wall time, the "
            + "seconds into the clip (unknown before the first frame) and, for a YAAT client, the scenario clock from its pipe (unknown "
            + "for CRC or when the client does not answer in 2 s). Refuses when nothing is recording; a recording whose pipeline died is "
            + "reported with its exit code and the recorder's last log lines, and ends."
    )]
    public async Task<string> RecordMarkAsync(
        [Description("What happens at this moment, e.g. \"cleared to land\".")] string label,
        CancellationToken cancellationToken
    )
    {
        RecordingMarked marked = await session.MarkAsync(label, SimSecondsAsync, cancellationToken).ConfigureAwait(false);
        RecordingMark mark = marked.Mark;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"mark {marked.Count} '{mark.Label}': clip {Format(mark.ClipSeconds, "F2")} s, sim {Format(mark.SimSeconds, "F1")} s; "
                + $"marks: {marked.MarksPath}"
        );
    }

    [McpServerTool]
    [Description(
        "Stops the running recording and waits for it: up to 15 s for the recorder to stop (past that the recorder alone is killed and "
            + "the clip ends there), then up to 120 s for ffmpeg to finish the MP4 (past that the pipeline is killed and the MP4 may be "
            + "unplayable). Returns the MP4's path, its length, the frames written, the frames dropped (fps x length - written) and the "
            + "marks file; a recording that ended on its own (the one-hour cap or a window change) is stopped as such. A cancelled call "
            + "still lets the encode finish. Refuses when nothing is recording."
    )]
    public async Task<string> RecordStopAsync(CancellationToken cancellationToken)
    {
        RecordingStopResult stopped = await session.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!stopped.Mp4Exists)
        {
            throw new McpException($"{stopped.Problem ?? "The recording ended"}; no MP4 was written at {stopped.Paths.Mp4}.");
        }

        string summary = string.Create(
            CultureInfo.InvariantCulture,
            $"stopped {stopped.Paths.Mp4}: {Format(stopped.End?.Seconds, "F1")} s, {Format(stopped.End?.Frames)} frames, "
                + $"{Format(stopped.DroppedFrames)} dropped; marks: {stopped.Paths.Marks}"
        );
        if (stopped.Note is { } note)
        {
            summary = $"{summary}{Environment.NewLine}{note}";
        }

        return (stopped.Problem is null) ? summary : $"{summary}{Environment.NewLine}warning: {stopped.Problem}";
    }

    /// <summary>Refuses an <c>out</c> that is not an <c>.mp4</c> or that names an existing folder.</summary>
    private static void RequireMp4File(string @out)
    {
        if (!string.Equals(Path.GetExtension(@out), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException($"INVALID_PARAM: out must name an .mp4 file; got '{@out}'.");
        }

        if (Directory.Exists(@out))
        {
            throw new McpException($"INVALID_PARAM: out names an existing folder, {Path.GetFullPath(@out)}; pass a file path ending in .mp4.");
        }
    }

    private static string DescribeStart(RecordingStarted started)
    {
        CropBox crop = started.Crop;
        FrameFormat frame = started.Frame;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"recording {started.Paths.Mp4} (pid {started.Target.Pid}, window 0x{started.Target.Hwnd:X}, {frame.Width}x{frame.Height} "
                + $"cropped to the {crop.Width}x{crop.Height} client area at {crop.X},{crop.Y}, {frame.Fps} fps, {started.Encoding.Encoder}, "
                + $"audio {(started.Encoding.Audio ? "on" : "off")}); marks: {started.Paths.Marks}"
        );
    }

    private static string Format(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "unknown";

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    /// <summary>
    /// The client's scenario clock, or null when it does not answer: no main window yet, a closed pipe, or no answer within
    /// <see cref="SimTimeWait"/>, which covers the wait for the pipe too (another call, a long wait_until, may hold it).
    /// </summary>
    private async Task<double?> SimSecondsAsync(int pid, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SimTimeWait);
        try
        {
            GetSimTimeResult result = await PipeCalls
                .SendForPidAsync<GetSimTimeResult>(pipes, pid, ProtocolMethods.GetSimTime, null, SimTimeWait, limit.Token)
                .ConfigureAwait(false);
            return result.SimSeconds;
        }
        catch (Exception ex) when (ex is McpException or JsonException or InvalidOperationException)
        {
            // JsonException and InvalidOperationException are an answer that is not a sim time: a bad line, or neither result nor error.
            logger.LogDebug(ex, "record_mark: pid {Pid} gave no scenario clock; the mark's simSeconds is null", pid);
            return null;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(
                ex,
                "record_mark: pid {Pid} did not answer within {Seconds} s; the mark's simSeconds is null",
                pid,
                SimTimeWait.TotalSeconds
            );
            return null;
        }
    }

    /// <summary>A pid's main window: over its pipe for a YAAT client, through UI Automation otherwise.</summary>
    private async Task<RecordingTarget> ResolvePidAsync(int pid, CancellationToken ct)
    {
        bool hasPipe = await PipeCalls.TryRouteAsync(pipes, pid, ct).ConfigureAwait(false) is not null;
        IReadOnlyList<TopLevelWindow> windows = hasPipe ? await PipeWindowsAsync(pid, ct).ConfigureAwait(false) : backend.TopLevelWindows(pid);
        List<TopLevelWindow> handled = [.. windows.Where(window => window.Hwnd != 0)];
        TopLevelWindow window =
            handled.FirstOrDefault(IsMainTitle)
            ?? handled.FirstOrDefault()
            ?? throw new McpException(
                $"Process {pid} has no top-level window with a native handle to record; call list_windows({pid}) to see its windows."
            );
        return new RecordingTarget(pid, window.Hwnd, hasPipe);
    }

    private static bool IsMainTitle(TopLevelWindow window) =>
        (window.Title is { } title)
        && (title.StartsWith("YAAT", StringComparison.OrdinalIgnoreCase) || title.StartsWith("CRC", StringComparison.OrdinalIgnoreCase));

    /// <summary>The windows a YAAT client lists over its pipe, its popups left out.</summary>
    private async Task<IReadOnlyList<TopLevelWindow>> PipeWindowsAsync(int pid, CancellationToken ct)
    {
        List<WindowInfo>? windows = await ListPipeWindowsAsync(pid, ct).ConfigureAwait(false);
        return [.. (windows ?? []).Where(window => !window.IsPopup).Select(window => new TopLevelWindow(window.Hwnd, window.Title))];
    }

    private async Task<List<WindowInfo>?> ListPipeWindowsAsync(int pid, CancellationToken ct)
    {
        try
        {
            return await PipeCalls.TryListWindowsAsync(pipes, pid, logger, ct).ConfigureAwait(false);
        }
        catch (PipeRemoteException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>A window id from list_windows: a pipe window's handle from the client's own list, a UI Automation window's from UIA.</summary>
    private async Task<RecordingTarget> ResolveElementAsync(string elementId, CancellationToken ct)
    {
        if (registry.Resolve(elementId) is PipeNodeRef node)
        {
            List<WindowInfo> windows = await ListPipeWindowsAsync(node.Pid, ct).ConfigureAwait(false) ?? [];
            WindowInfo window =
                windows.FirstOrDefault(candidate => candidate.NodeId == node.NodeId)
                ?? throw new McpException($"Element '{elementId}' is not one of pid {node.Pid}'s windows; pass a window id from list_windows.");
            if (window.Hwnd == 0)
            {
                throw new McpException(
                    $"Window '{elementId}' has no native handle (an overlay popup lives inside its window); record that window instead."
                );
            }

            return new RecordingTarget(node.Pid, window.Hwnd, true);
        }

        AutomationElement element = registry.ResolveUia(elementId);
        (long hwnd, int pid) = UiaQuery.Guarded(
            logger,
            "record_start",
            elementId,
            () => ((long)(uint)element.Current.NativeWindowHandle, element.Current.ProcessId)
        );
        if (hwnd == 0)
        {
            throw new McpException($"Element '{elementId}' has no native window handle; pass a window id from list_windows.");
        }

        bool hasPipe = await PipeCalls.TryRouteAsync(pipes, pid, ct).ConfigureAwait(false) is not null;
        return new RecordingTarget(pid, hwnd, hasPipe);
    }
}
