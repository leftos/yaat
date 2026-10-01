// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Logging;

namespace Yaat.Client.Automation.Transport;

/// <summary>
/// The discovery file <c>&lt;directory&gt;/&lt;pid&gt;.json</c> through which a client finds this process's pipe.
/// <see cref="Write"/> first removes stale files (a process no longer running, a pid now held by another program, a
/// file that does not parse, an unfinished temporary file), then writes this process's file atomically;
/// <see cref="Dispose"/> deletes it.
/// </summary>
public sealed class DiscoveryFile(string directory, string pipeName) : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger("DiscoveryFile");

    private bool _written;

    /// <summary>The path of this process's discovery file.</summary>
    public string FilePath { get; } = Path.Combine(directory, $"{Environment.ProcessId}.json");

    public void Write()
    {
        Directory.CreateDirectory(directory);
        SweepStale();

        string processName;
        using (var current = Process.GetCurrentProcess())
        {
            processName = current.ProcessName;
        }

        var info = new DiscoveryInfo
        {
            Pid = Environment.ProcessId,
            PipeName = pipeName,
            ProcessName = processName,
            StartTime = DateTimeOffset.UtcNow,
            ProtocolVersion = ProtocolVersion.Current,
        };

        string tempPath = Path.Combine(directory, $"{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, ProtocolSerializer.Serialize(info));
            File.Move(tempPath, FilePath, overwrite: true);
            _written = true;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void SweepStale()
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (
                TryParsePid(Path.GetFileNameWithoutExtension(file), out int pid)
                && (pid != Environment.ProcessId)
                && (StaleReason(file, pid) is { } reason)
            )
            {
                TryDelete(file, reason);
            }
        }

        // <pid>.<guid>.tmp: a write that never reached its move.
        foreach (string file in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            string name = Path.GetFileName(file);
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            if ((dot > 0) && TryParsePid(name[..dot], out int pid) && (pid != Environment.ProcessId) && (RunningProcessName(pid) is null))
            {
                TryDelete(file, $"process {pid} is not running");
            }
        }
    }

    // Why the discovery file of process pid is stale, or null when it still belongs to a live client.
    private static string? StaleReason(string file, int pid)
    {
        DiscoveryInfo? info;
        try
        {
            info = ProtocolSerializer.Deserialize<DiscoveryInfo>(File.ReadAllText(file));
        }
        catch (JsonException ex)
        {
            return $"it does not parse ({ex.Message})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not read automation discovery file {File}; leaving it", file);
            return null;
        }

        if (info is null)
        {
            return "it is empty";
        }

        string? runningName = RunningProcessName(pid);
        if (runningName is null)
        {
            return $"process {pid} is not running";
        }

        if (!string.Equals(runningName, info.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return $"pid {pid} now belongs to {runningName}, not {info.ProcessName}";
        }

        return null;
    }

    private static bool TryParsePid(string text, out int pid) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out pid);

    // The name of the running process pid, or null when no such process is running.
    private static string? RunningProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryDelete(string file, string reason)
    {
        try
        {
            File.Delete(file);
            Log.LogInformation("Removed stale automation discovery file {File}: {Reason}", file, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not remove stale automation discovery file {File} ({Reason})", file, reason);
        }
    }

    public void Dispose()
    {
        if (!_written)
        {
            return;
        }

        _written = false;
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not delete automation discovery file {File}", FilePath);
        }
    }
}
