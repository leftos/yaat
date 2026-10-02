using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Yaat.Client.Automation.Protocol;
using Yaat.ClientDriver.Mcp.Pipe;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>Starting, listing and stopping the processes an agent drives, plus the client's own log.</summary>
/// <param name="registry">Shared element registry; the ids handed out here resolve in every other tool.</param>
/// <param name="pipes">Finds and caches the YAAT clients' automation pipes; the launch wait polls them for the client's first window list.</param>
/// <param name="processes">Starts the client process, injected so a test can drive a launch without a real executable.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
[McpServerToolType]
public sealed class ProcessTools(ElementRegistry registry, PipeDirectory pipes, IProcessStarter processes, ILogger<ProcessTools> logger)
{
    private const string ClientProcessName = "Yaat.Client";
    private const string ClientExecutableName = "Yaat.Client.exe";

    /// <summary>The processes this server started, kept as live handles: a pid alone is reused by Windows and cannot identify one.</summary>
    private static readonly ConcurrentDictionary<int, Process> LaunchedProcesses = new();

    [McpServerTool]
    [Description(
        "Starts the YAAT desktop client with YAAT_APPDATA_DIR pointed at a scratch directory and its automation pipe enabled, so "
            + "the developer's own preferences, favorites and log stay untouched. Waits for the client's automation pipe to answer "
            + "with a window list and returns its pid, that window list and the path of the log tail_yaat_log reads."
    )]
    public async Task<string> LaunchYaatAsync(
        CancellationToken cancellationToken,
        [Description("Directory YAAT_APPDATA_DIR is set to; a relative path resolves against the repo root the server runs in.")]
            string appDataDir = ".tmp/client-driver/appdata",
        [Description("Path to the client executable; a relative path resolves against the repo root the server runs in.")]
            string exePath = "src/Yaat.Client/bin/Debug/net10.0/Yaat.Client.exe",
        [Description("How long to wait for the client's automation pipe to answer, in seconds.")] int waitSeconds = 30
    )
    {
        if (waitSeconds < 1)
        {
            throw new McpException($"waitSeconds must be at least 1 second, not {waitSeconds}");
        }

        string fullExePath = Path.GetFullPath(exePath);
        string executableName = Path.GetFileName(fullExePath);
        if (!string.Equals(executableName, ClientExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException($"launch_yaat only starts {ClientExecutableName}, not '{executableName}'");
        }

        if (!File.Exists(fullExePath))
        {
            throw new McpException($"No client executable at '{fullExePath}' — build it first with: dotnet build src/Yaat.Client");
        }

        string fullAppDataDir = Path.GetFullPath(appDataDir);
        Directory.CreateDirectory(fullAppDataDir);

        ProcessStartInfo startInfo = new(fullExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(fullExePath)! };
        startInfo.Environment["YAAT_APPDATA_DIR"] = fullAppDataDir;
        startInfo.Environment["YAAT_AUTOMATION"] = "1";
        Process process = processes.Start(startInfo);
        LaunchedProcesses[process.Id] = process;
        logger.LogInformation("Launched {Exe} as pid {Pid} with YAAT_APPDATA_DIR={AppData}", fullExePath, process.Id, fullAppDataDir);

        List<WindowInfo> windows = await WaitForPipeWindowsOrKillAsync(process, waitSeconds, cancellationToken).ConfigureAwait(false);
        string windowLines = string.Join(
            Environment.NewLine,
            windows.Select(window => PipeDescribe.Window(window, registry.Register(process.Id, window.NodeId)))
        );
        string logPath = Path.Combine(fullAppDataDir, "yaat-client.log");
        string header = $"pid={process.Id} appDataDir={fullAppDataDir} log={logPath}";
        return $"{header}{Environment.NewLine}windows ({windows.Count}):{Environment.NewLine}{windowLines}";
    }

    [McpServerTool]
    [Description("Lists running processes whose name contains the given text, with pid and main window title. Use \"Yaat.Client\" or \"CRC\".")]
    public string ListProcesses([Description("Case-insensitive substring of the process name.")] string nameContains)
    {
        List<string> rows = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (!process.ProcessName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows.Add($"pid={process.Id} | {process.ProcessName} | {MainWindowTitle(process)}");
            }
        }

        return rows.Count == 0 ? $"No process name contains '{nameContains}'." : string.Join(Environment.NewLine, rows);
    }

    [McpServerTool]
    [Description(
        "Reads the last lines of the client log written under a launch_yaat appDataDir. An 'Unhandled UI-thread exception (recovered)' "
            + "entry is the usual cause of a control that looks like it did nothing."
    )]
    public string TailYaatLog(
        [Description("The same appDataDir that was passed to launch_yaat.")] string appDataDir = ".tmp/client-driver/appdata",
        [Description("How many trailing lines to return.")] int lines = 80
    )
    {
        string logPath = Path.Combine(Path.GetFullPath(appDataDir), "yaat-client.log");
        if (!File.Exists(logPath))
        {
            throw new McpException($"No log at '{logPath}' — the client writes it on startup, so launch_yaat with this appDataDir first");
        }

        Queue<string> tail = new();
        using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        string? line = reader.ReadLine();
        while (line is not null)
        {
            tail.Enqueue(line);
            if (tail.Count > lines)
            {
                tail.Dequeue();
            }

            line = reader.ReadLine();
        }

        return $"{logPath} (last {tail.Count} lines):{Environment.NewLine}{string.Join(Environment.NewLine, tail)}";
    }

    [McpServerTool]
    [Description(
        "Closes a process this server launched: asks its main window to close, then kills the tree if it is still alive after 5 seconds. "
            + "Refuses any other process unless it is a Yaat.Client — CRC is never stopped from here."
    )]
    public string StopProcess([Description("The process id, as returned by launch_yaat or list_processes.")] int pid)
    {
        using Process process = OpenProcess(pid);
        string name = process.ProcessName;
        if (!WasLaunchedHere(pid, process) && !string.Equals(name, ClientProcessName, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"Refusing to stop pid {pid} ('{name}'): this server did not launch it and it is not a {ClientProcessName} process. Close it yourself if you meant to"
            );
        }

        bool asked = process.CloseMainWindow();
        bool exited = process.WaitForExit(TimeSpan.FromSeconds(5));
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }

        Forget(pid);
        string how = exited ? (asked ? "closed its main window" : "exited") : "killed the process tree";
        return $"Stopped pid {pid} ('{name}') — {how}.";
    }

    private static Process OpenProcess(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            throw new McpException($"No running process with pid {pid} — it has already exited");
        }
    }

    /// <summary>
    /// Polls the pid's automation pipe until it answers <c>list_windows</c> with at least one window, or the process exits,
    /// or the deadline passes, or <paramref name="cancellationToken"/> is cancelled. The deadline is linked into every pipe
    /// call, so one stalled connect or request cannot outlive it; only a cancelled caller reports a cancellation.
    /// </summary>
    private async Task<List<WindowInfo>> WaitForPipeWindowsAsync(Process process, int waitSeconds, CancellationToken cancellationToken)
    {
        var limit = TimeSpan.FromSeconds(waitSeconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < limit)
        {
            if (process.HasExited)
            {
                throw ExitedBeforeWindow(process);
            }

            List<WindowInfo>? windows;
            try
            {
                windows = await TryListPipeWindowsAsync(process.Id, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (windows is { Count: > 0 })
            {
                return windows;
            }

            try
            {
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        // A client that exits just as the deadline lands reports why, rather than the timeout that raced it.
        if (process.HasExited)
        {
            throw ExitedBeforeWindow(process);
        }

        throw new McpException(
            $"no automation pipe answered within {waitSeconds} s (pid {process.Id}); is Yaat.Client built from this branch? See tail_yaat_log."
        );
    }

    private static McpException ExitedBeforeWindow(Process process) =>
        new($"The client (pid {process.Id}) exited with code {process.ExitCode} before opening a window — read the log with tail_yaat_log");

    /// <summary>The pid's pipe window list, or null while the client has no pipe that answers — the wait polls again.</summary>
    private async Task<List<WindowInfo>?> TryListPipeWindowsAsync(int pid, CancellationToken cancellationToken)
    {
        PipeClient? client = await PipeCalls.TryRouteAsync(pipes, pid, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return null;
        }

        try
        {
            List<WindowInfo> windows = await client
                .SendAsync<List<WindowInfo>>(ProtocolMethods.ListWindows, null, PipeClient.RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (windows.Count > 0)
            {
                pipes.RememberTarget(pid);
            }

            return windows;
        }
        catch (ObjectDisposedException ex)
        {
            logger.LogDebug(ex, "The automation pipe client for pid {Pid} was disposed mid-call; waiting for it to answer again", pid);
            return null;
        }
        catch (PipeRemoteException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (McpException ex)
        {
            // A broken pipe or the client's own request timeout (PipeRemoteException is a plain Exception, so a coded
            // host error above still fails at once). The client may have died: the wait polls again and reports its exit
            // code or the deadline, not the pipe's own wording.
            logger.LogDebug(ex, "The automation pipe for pid {Pid} failed mid-launch; polling again", pid);
            return null;
        }
    }

    /// <summary>Drops the handle to the process this server launched as <paramref name="pid"/>, if it still holds one.</summary>
    public static void Forget(int pid)
    {
        if (LaunchedProcesses.TryRemove(pid, out Process? launched))
        {
            launched.Dispose();
        }
    }

    /// <summary>
    /// The launch wait, with the launched process killed and forgotten however it ends without a window list — an early
    /// exit, the deadline, a cancelled call, or any failure a pipe call reports. The pid is dropped from the pipe
    /// directory too, so neither the remembered target nor a cached client points at a process that is now gone.
    /// </summary>
    private async Task<List<WindowInfo>> WaitForPipeWindowsOrKillAsync(Process process, int waitSeconds, CancellationToken cancellationToken)
    {
        try
        {
            return await WaitForPipeWindowsAsync(process, waitSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Killing pid {Pid}: the launch wait ended without an automation pipe answering", process.Id);
            try
            {
                KillQuietly(process);
            }
            finally
            {
                await pipes.ForgetAsync(process.Id).ConfigureAwait(false);
                Forget(process.Id);
            }

            throw;
        }
    }

    /// <summary>Stops <paramref name="process"/> and its tree, logging — never swallowing — whatever stops the kill short.</summary>
    private void KillQuietly(Process process)
    {
        if (process.Id == Environment.ProcessId)
        {
            logger.LogWarning("Refusing to kill pid {Pid}: it is this server's own process", process.Id);
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
            when ((ex is InvalidOperationException) || (ex is Win32Exception) || (ex is NotSupportedException) || (ex is AggregateException))
        {
            logger.LogWarning(ex, "Could not kill pid {Pid} after the launch wait ended", process.Id);
        }
    }

    /// <summary>True only when the pid still belongs to the very process this server started — pids are reused.</summary>
    private bool WasLaunchedHere(int pid, Process target)
    {
        if (!LaunchedProcesses.TryGetValue(pid, out Process? launched))
        {
            return false;
        }

        try
        {
            if (launched.HasExited)
            {
                Forget(pid);
                return false;
            }

            return launched.StartTime == target.StartTime;
        }
        catch (Exception ex) when ((ex is InvalidOperationException) || (ex is Win32Exception))
        {
            logger.LogDebug(ex, "Could not compare the start time of pid {Pid} against the process this server launched", pid);
            return false;
        }
    }

    private string MainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Could not read the main window title of pid {Pid}", process.Id);
            return "(no window)";
        }
    }
}
