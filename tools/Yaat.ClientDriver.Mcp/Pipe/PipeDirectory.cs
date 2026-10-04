using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Automation.Protocol;

namespace Yaat.ClientDriver.Mcp.Pipe;

/// <summary>
/// Finds the automation pipe of a YAAT client: reads the pid's discovery file, checks the pid is still a live client
/// process, and pings the pipe it names. One <see cref="PipeClient"/> per pid is kept and reused; a pid that fails any
/// check is left to the UI Automation path, and why is logged at debug.
/// </summary>
/// <param name="discoveryDirectory">The directory the client writes <c>&lt;pid&gt;.json</c> into.</param>
/// <param name="expectedProcessName">The process name a discovery file must belong to, e.g. <c>Yaat.Client</c>.</param>
/// <param name="logger">Where the fallback reasons go.</param>
/// <param name="clientLogger">The logger the cached <see cref="PipeClient"/>s report through.</param>
public sealed class PipeDirectory(
    string discoveryDirectory,
    string expectedProcessName,
    ILogger<PipeDirectory> logger,
    ILogger<PipeClient> clientLogger
)
{
    /// <summary>The process name a client's discovery file carries in the standard deployment.</summary>
    public const string ClientProcessName = "Yaat.Client";

    private readonly ConcurrentDictionary<int, PipeClient> _clients = new();
    private readonly Lock _lastTargetGate = new();
    private int? _lastTargetPid;

    /// <summary>
    /// The pid of the YAAT client the last successful pipe-routed call reached: where <c>send_keys</c> without an element
    /// types over the pipe. Null until a pipe call succeeds, and again once that pid's client is forgotten or dropped, or a
    /// call routed through UI Automation succeeds: untargeted keys follow whatever was touched last, YAAT or CRC.
    /// </summary>
    public int? LastTargetPid
    {
        get
        {
            lock (_lastTargetGate)
            {
                return _lastTargetPid;
            }
        }
    }

    /// <summary>Records <paramref name="pid"/> as the client the last successful pipe-routed call reached.</summary>
    public void RememberTarget(int pid)
    {
        lock (_lastTargetGate)
        {
            _lastTargetPid = pid;
        }
    }

    /// <summary>
    /// Returns a connected client for <paramref name="pid"/>, or null when the pid has no automation pipe this process
    /// can use: no discovery file, an unparsable one, a pid that is not running, a process name that is not the client's,
    /// or a pipe that does not answer a ping. A client already found for the pid is returned unchanged, unless the
    /// process behind it has since exited or its pid was reused, in which case that client is dropped and the pid's pipe
    /// is looked up again.
    /// </summary>
    public async Task<PipeClient?> TryGetAsync(int pid, CancellationToken ct)
    {
        if (_clients.TryGetValue(pid, out PipeClient? cached))
        {
            if (IsExpectedClient(pid))
            {
                return cached;
            }

            logger.LogDebug("Pid {Pid} is no longer {Expected}; dropping its cached automation pipe", pid, expectedProcessName);
            ForgetTarget(pid);
            if (_clients.TryRemove(new KeyValuePair<int, PipeClient>(pid, cached)))
            {
                await cached.DisposeAsync().ConfigureAwait(false);
            }
        }

        string file = Path.Combine(discoveryDirectory, $"{pid}.json");
        DiscoveryInfo? info;
        try
        {
            info = ProtocolSerializer.Deserialize<DiscoveryInfo>(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "No usable automation discovery file for pid {Pid} at {File}", pid, file);
            return null;
        }

        if (info is null)
        {
            logger.LogDebug("The automation discovery file for pid {Pid} at {File} is empty", pid, file);
            return null;
        }

        string? actualName = RunningProcessName(pid);
        if (actualName is null)
        {
            logger.LogDebug("Pid {Pid} has a discovery file at {File} but is not running", pid, file);
            return null;
        }

        if (
            !string.Equals(actualName, expectedProcessName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(info.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase)
        )
        {
            logger.LogDebug(
                "Pid {Pid} is {Actual} and its discovery file says {Recorded}, not {Expected}",
                pid,
                actualName,
                info.ProcessName,
                expectedProcessName
            );
            return null;
        }

        var client = new PipeClient(info.PipeName, pid, clientLogger);
        try
        {
            await client.SendAsync<PingResult>(ProtocolMethods.Ping, null, PipeClient.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The automation pipe for pid {Pid} did not answer a ping", pid);
            await client.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        PipeClient existing = _clients.GetOrAdd(pid, client);
        if (!ReferenceEquals(existing, client))
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        return existing;
    }

    /// <summary>Drops the client cached for <paramref name="pid"/>, if any, disposing it, and stops remembering it as the last target.</summary>
    public async Task ForgetAsync(int pid)
    {
        ForgetTarget(pid);
        if (_clients.TryRemove(pid, out PipeClient? client))
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops remembering any pid as the last target, because a call routed through UI Automation succeeded: what the agent
    /// touched last is no longer a client reached over its pipe.
    /// </summary>
    public void ForgetLastTarget()
    {
        lock (_lastTargetGate)
        {
            _lastTargetPid = null;
        }
    }

    /// <summary>Stops remembering <paramref name="pid"/> as the last target, if it still is; another remembered pid is left alone.</summary>
    public void ForgetTarget(int pid)
    {
        lock (_lastTargetGate)
        {
            if (_lastTargetPid == pid)
            {
                _lastTargetPid = null;
            }
        }
    }

    private bool IsExpectedClient(int pid) =>
        RunningProcessName(pid) is { } name && string.Equals(name, expectedProcessName, StringComparison.OrdinalIgnoreCase);

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
}
