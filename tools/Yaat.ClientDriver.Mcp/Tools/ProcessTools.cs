using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Yaat.ClientDriver.Mcp.Tools;

/// <summary>Starting, listing and stopping the processes an agent drives, plus the client's own log.</summary>
/// <param name="registry">Shared element registry; the ids handed out here resolve in every other tool.</param>
/// <param name="logger">Server logger, writing to stderr.</param>
[McpServerToolType]
public sealed class ProcessTools(ElementRegistry registry, ILogger<ProcessTools> logger)
{
    private const string ClientProcessName = "Yaat.Client";
    private const string ClientExecutableName = "Yaat.Client.exe";

    /// <summary>The processes this server started, kept as live handles: a pid alone is reused by Windows and cannot identify one.</summary>
    private static readonly ConcurrentDictionary<int, Process> LaunchedProcesses = new();

    [McpServerTool]
    [Description(
        "Starts the YAAT desktop client with YAAT_APPDATA_DIR pointed at a scratch directory, so the developer's own preferences, "
            + "favorites and log stay untouched. Waits for the client's first top-level window and returns its pid, that window list "
            + "and the path of the log tail_yaat_log reads."
    )]
    public string LaunchYaat(
        [Description("Directory YAAT_APPDATA_DIR is set to; a relative path resolves against the repo root the server runs in.")]
            string appDataDir = ".tmp/client-driver/appdata",
        [Description("Path to the client executable; a relative path resolves against the repo root the server runs in.")]
            string exePath = "src/Yaat.Client/bin/Debug/net10.0/Yaat.Client.exe",
        [Description("How long to wait for the first top-level window, in seconds.")] int waitSeconds = 30
    )
    {
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
        Process process = Process.Start(startInfo) ?? throw new McpException($"Windows did not start '{fullExePath}'");
        LaunchedProcesses[process.Id] = process;
        logger.LogInformation("Launched {Exe} as pid {Pid} with YAAT_APPDATA_DIR={AppData}", fullExePath, process.Id, fullAppDataDir);

        List<AutomationElement> windows = WaitForWindowsOrKill(process, waitSeconds);
        string windowLines = string.Join(Environment.NewLine, windows.Select(window => UiaQuery.Describe(window, registry.Register(window))));
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

    private static List<AutomationElement> WaitForWindows(Process process, int waitSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new McpException(
                    $"The client (pid {process.Id}) exited with code {process.ExitCode} before opening a window — read the log with tail_yaat_log"
                );
            }

            List<AutomationElement> windows = UiaQuery.TopLevelWindows(process.Id);
            if (windows.Count > 0)
            {
                return windows;
            }

            Thread.Sleep(250);
        }

        throw new McpException($"The client (pid {process.Id}) opened no window within {waitSeconds} s — read the log with tail_yaat_log");
    }

    private static void Forget(int pid)
    {
        if (LaunchedProcesses.TryRemove(pid, out Process? launched))
        {
            launched.Dispose();
        }
    }

    private List<AutomationElement> WaitForWindowsOrKill(Process process, int waitSeconds)
    {
        try
        {
            return WaitForWindows(process, waitSeconds);
        }
        catch (McpException ex)
        {
            logger.LogWarning(ex, "Killing pid {Pid}: it never showed a window", process.Id);
            KillQuietly(process);
            Forget(process.Id);
            throw;
        }
    }

    private void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when ((ex is InvalidOperationException) || (ex is Win32Exception) || (ex is NotSupportedException))
        {
            logger.LogWarning(ex, "Could not kill pid {Pid} after it failed to show a window", process.Id);
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
