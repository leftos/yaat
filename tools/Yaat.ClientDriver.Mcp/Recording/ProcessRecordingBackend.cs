using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Yaat.ClientDriver.Mcp.Recording;

/// <summary>
/// The real recording backend: ffmpeg from PATH, the recorder from this build's <c>recorder/</c> folder, windows read through
/// Win32 and UI Automation, and the pipeline run under cmd. Every process it starts has its stdout and stderr redirected,
/// since this server's own stdout carries the MCP protocol and an inherited handle would corrupt it.
/// </summary>
/// <param name="logger">Server logger, writing to stderr.</param>
public sealed partial class ProcessRecordingBackend(ILogger<ProcessRecordingBackend> logger) : IRecordingBackend
{
    /// <summary>How long a probe, an encoder listing or the nvenc probe encode may take.</summary>
    private static readonly TimeSpan HelperWait = TimeSpan.FromSeconds(15);

    /// <summary>The encoder each ffmpeg was found to encode with, probed once per server.</summary>
    private readonly ConcurrentDictionary<string, string> _encoders = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string? FindFfmpeg()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), "ffmpeg.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <inheritdoc />
    public string FindRecorder() => RecorderLocator.FindRecorder();

    /// <inheritdoc />
    public async Task<string> ChooseEncoderAsync(string ffmpeg, CancellationToken ct)
    {
        if (_encoders.TryGetValue(ffmpeg, out string? known))
        {
            return known;
        }

        ProcessRun listing = await RunAsync(ffmpeg, ["-hide_banner", "-encoders"], ct).ConfigureAwait(false);
        string encoder = await FfmpegPipeline
            .ChooseEncoderAsync(
                listing.StandardOutput,
                async () => (await RunAsync(ffmpeg, FfmpegPipeline.NvencProbeArguments, ct).ConfigureAwait(false)).ExitCode == 0
            )
            .ConfigureAwait(false);
        logger.LogInformation("Recording encoder for {Ffmpeg}: {Encoder}", ffmpeg, encoder);
        _encoders[ffmpeg] = encoder;
        return encoder;
    }

    /// <inheritdoc />
    public IReadOnlyList<TopLevelWindow> TopLevelWindows(int pid) =>
        UiaQuery.Guarded(
            logger,
            "record_start",
            $"pid {pid}",
            () =>
                UiaQuery
                    .TopLevelWindows(pid)
                    .Select(window => new TopLevelWindow((uint)window.Current.NativeWindowHandle, window.Current.Name))
                    .ToList()
        );

    /// <inheritdoc />
    public WindowGeometry? ReadWindow(long hwnd) => NativeInput.ReadWindowGeometry((nint)hwnd);

    /// <inheritdoc />
    public async Task<RecorderProbe> ProbeAsync(string recorder, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ProcessRun run = await RunAsync(recorder, arguments, ct).ConfigureAwait(false);
        Match size = SizePattern().Match(run.StandardOutput.Trim());
        if ((run.ExitCode == 0) && size.Success)
        {
            return new RecorderProbe(
                int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture),
                null
            );
        }

        string reason = run.StandardError.Trim();
        string said = (reason.Length > 0) ? reason : $"it printed '{run.StandardOutput.Trim()}' and no reason";
        return new RecorderProbe(0, 0, $"{said} (exit {run.ExitCode})");
    }

    /// <inheritdoc />
    public IRecordingProcess Start(PipelineCommandLine commandLine)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = FfmpegPipeline.CmdArguments(commandLine),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach ((string name, string value) in commandLine.Environment)
        {
            start.Environment[name] = value;
        }

        Process process = Process.Start(start) ?? throw new McpException("Windows did not start cmd.exe for the recording pipeline");
        process.StandardInput.Close();
        process.OutputDataReceived += (_, line) => LogPipelineLine(line.Data);
        process.ErrorDataReceived += (_, line) => LogPipelineLine(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new PipelineProcess(process, Path.GetFileNameWithoutExtension(commandLine.RecorderExecutable), logger);
    }

    /// <inheritdoc />
    public void WriteDeadline(string path, DateTime endUtc)
    {
        // Moved over the file under a name unique to this process, so the recorder never reads half a line.
        string temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, endUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\n");
        File.Move(temporary, path, overwrite: true);
    }

    private void LogPipelineLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            logger.LogWarning("Recording pipeline (cmd): {Line}", line);
        }
    }

    /// <summary>
    /// Runs a helper to its end, its output read whole; a helper that outlives <see cref="HelperWait"/>, or whose caller
    /// cancels, is killed.
    /// </summary>
    private static async Task<ProcessRun> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new McpException($"Windows did not start '{executable}'");
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelperWait);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new McpException(
                $"'{Path.GetFileName(executable)} {string.Join(' ', arguments)}' did not finish in {HelperWait.TotalSeconds:0} s and was killed."
            );
        }

        return new ProcessRun(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    /// <summary>The ids of the running processes whose parent is <paramref name="parentId"/>, from a Toolhelp snapshot.</summary>
    private static List<int> ChildProcessIds(int parentId)
    {
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateToolhelp32Snapshot failed listing the processes");
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            List<int> children = [];
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                if (entry.ParentProcessId == (uint)parentId)
                {
                    children.Add((int)entry.ProcessId);
                }
            }

            return children;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private const uint SnapshotProcesses = 0x00000002;

    private static readonly nint InvalidHandleValue = -1;

    [GeneratedRegex(@"^(\d+)x(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private sealed record ProcessRun(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Toolhelp's <c>PROCESSENTRY32W</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed ushort ExeFile[260];
    }

    /// <summary>
    /// The cmd that runs the pipeline; its exit code is ffmpeg's, the pipeline's last stage. The recorder is the child of
    /// this cmd whose image is <paramref name="recorderName"/>, started after it.
    /// </summary>
    private sealed class PipelineProcess(Process process, string recorderName, ILogger logger) : IRecordingProcess
    {
        public bool HasExited => process.HasExited;

        public int ExitCode => process.ExitCode;

        public bool IsRecorderRunning
        {
            get
            {
                if (process.HasExited)
                {
                    return false;
                }

                using Process? recorder = FindRecorderChild();
                return recorder is not null;
            }
        }

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct) => WaitAsync(process, timeout, ct);

        public async Task<bool> WaitForRecorderExitAsync(TimeSpan timeout, CancellationToken ct)
        {
            using Process? recorder = FindRecorder("wait for");
            return (recorder is null) || await WaitAsync(recorder, timeout, ct).ConfigureAwait(false);
        }

        public void KillRecorder()
        {
            using Process? recorder = FindRecorder("kill");
            recorder?.Kill();
        }

        public void KillTree() => process.Kill(entireProcessTree: true);

        public void Dispose() => process.Dispose();

        private static async Task<bool> WaitAsync(Process target, TimeSpan timeout, CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await target.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        /// <summary>
        /// The running recorder this cmd started, or null once it has exited; a running cmd without one is warned of, as the
        /// recorder may have ended a moment ago or never have been found, and <paramref name="action"/> then does nothing.
        /// </summary>
        private Process? FindRecorder(string action)
        {
            Process? recorder = FindRecorderChild();
            if ((recorder is null) && !process.HasExited)
            {
                logger.LogWarning(
                    "Recording pipeline: cmd pid {Pid} is running but has no {Recorder} child to {Action}; it may have just exited",
                    process.Id,
                    recorderName,
                    action
                );
            }

            return recorder;
        }

        private Process? FindRecorderChild()
        {
            foreach (int pid in ChildProcessIds(process.Id))
            {
                Process child;
                try
                {
                    child = Process.GetProcessById(pid);
                }
                catch (ArgumentException ex)
                {
                    logger.LogDebug(ex, "Recording pipeline: cmd's child pid {Pid} exited before it was read", pid);
                    continue;
                }

                if (IsRecorder(child))
                {
                    return child;
                }

                child.Dispose();
            }

            return null;
        }

        /// <summary>True for the recorder's image started after this cmd, so a pid reused from an older process never matches.</summary>
        private bool IsRecorder(Process child)
        {
            try
            {
                return string.Equals(child.ProcessName, recorderName, StringComparison.OrdinalIgnoreCase) && (child.StartTime >= process.StartTime);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                logger.LogDebug(ex, "Recording pipeline: cmd's child pid {Pid} could not be read; it is not taken for the recorder", child.Id);
                return false;
            }
        }
    }
}
