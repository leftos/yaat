// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Automation.Transport;

/// <summary>
/// The automation pipe's server side: a named pipe open to the current user only, accepting any number of
/// sequential and concurrent clients, each served by the connection handler on its own task.
/// </summary>
public sealed class NamedPipeTransport(string pipeName) : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger("NamedPipeTransport");
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long Dispose waits for the listen loop to end once cancelled. A hang guard: the cancelled accept ends at once on
    /// a working machine, and expiry only logs an error and carries on closing the instances.
    /// </summary>
    private static readonly TimeSpan ListenStopGuard = TimeSpan.FromSeconds(30);

    // Cancelled on dispose, never disposed: connection tasks may still observe its token after the listen loop ends,
    // and a source with no timer or wait handle holds nothing to free.
    private readonly CancellationTokenSource _cts = new();

    // Every server instance not yet disposed, the pending one and the connected ones. On Unix all instances of a pipe
    // name share one listening socket, which stays connectable until the last instance is disposed, so Dispose closes
    // them all itself rather than leaving them to the listen loop and the connection tasks.
    private readonly Lock _instancesLock = new();
    private readonly HashSet<NamedPipeServerStream> _instances = [];
    private bool _stopped;
    private int _started;

    // The listen loop, which Dispose waits for: its pending accept can still take a client until the accept itself ends.
    private Task _listenLoop = Task.CompletedTask;

    /// <summary>
    /// Starts listening on a background task and returns at once. <paramref name="connectionHandler"/> serves one client.
    /// A transport starts once; a second call throws <see cref="InvalidOperationException"/>. The transport owns the stream
    /// it hands the handler and disposes it when the handler ends; the handler must not dispose it.
    /// </summary>
    public void Start(Func<Stream, CancellationToken, Task> connectionHandler)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException($"Automation pipe {pipeName} is already started; a transport starts once.");
        }

        CancellationToken ct = _cts.Token;
        // Started without the token: a task cancelled before it runs would make Dispose's wait throw instead of return.
        Volatile.Write(ref _listenLoop, Task.Run(() => ListenLoop(connectionHandler, ct)));
    }

    private async Task ListenLoop(Func<Stream, CancellationToken, Task> connectionHandler, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await AcceptOne(connectionHandler, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log.LogDebug("Automation pipe {PipeName} stopped listening: host shutdown", pipeName);
        }
        catch (Exception ex) when (IsShutdownClose(ex, ct))
        {
            Log.LogDebug(ex, "Automation pipe {PipeName} stopped listening: host shutdown closed the pending instance", pipeName);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Automation pipe {PipeName} stopped listening: a server instance could not be created or accepted", pipeName);
        }
    }

    private async Task AcceptOne(Func<Stream, CancellationToken, Task> connectionHandler, CancellationToken ct)
    {
        NamedPipeServerStream pipe = CreateInstance(ct);

        bool handedOff = false;
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            handedOff = true;
            _ = HandleAsync(pipe, connectionHandler, ct);
        }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            Log.LogWarning(ex, "Automation pipe {PipeName}: accepting a client failed; listening again", pipeName);
            await Task.Delay(AcceptRetryDelay, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!handedOff)
            {
                Release(pipe);
            }
        }
    }

    /// <summary>A new server instance, tracked until it is released; none once the transport is disposed.</summary>
    private NamedPipeServerStream CreateInstance(CancellationToken ct)
    {
        lock (_instancesLock)
        {
            if (_stopped)
            {
                throw new OperationCanceledException($"Automation pipe {pipeName} is disposed; no new server instance is created.", ct);
            }

            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            );
            _instances.Add(pipe);
            return pipe;
        }
    }

    /// <summary>
    /// Disposes <paramref name="pipe"/> and stops tracking it, both under the instances lock. On Unix a second concurrent
    /// dispose of a server stream returns before the first has closed the shared listening socket, so every disposal of a
    /// tracked instance holds the lock, and <see cref="Dispose"/> cannot return while one is still closing.
    /// </summary>
    private void Release(NamedPipeServerStream pipe)
    {
        lock (_instancesLock)
        {
            pipe.Dispose();
            _instances.Remove(pipe);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, Func<Stream, CancellationToken, Task> handler, CancellationToken ct)
    {
        try
        {
            try
            {
                await handler(pipe, ct).ConfigureAwait(false);
            }
            finally
            {
                Release(pipe);
            }
        }
        catch (OperationCanceledException)
        {
            Log.LogDebug("Automation pipe {PipeName}: connection closed by host shutdown", pipeName);
        }
        catch (Exception ex) when (IsShutdownClose(ex, ct))
        {
            Log.LogDebug(ex, "Automation pipe {PipeName}: connection closed by host shutdown", pipeName);
        }
        catch (IOException ex)
        {
            Log.LogDebug(ex, "Automation pipe {PipeName}: client disconnected", pipeName);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Automation pipe {PipeName}: connection handler failed; the connection is closed", pipeName);
        }
    }

    /// <summary>What an instance raises when <see cref="Dispose"/> closes it under a pending accept, read or write.</summary>
    private static bool IsShutdownClose(Exception ex, CancellationToken ct) =>
        ct.IsCancellationRequested && (ex is ObjectDisposedException or IOException);

    /// <summary>
    /// Stops accepting clients and closes every open connection. It cancels the pending accept and waits for the listen loop
    /// to end, since an instance closed while its accept is still in flight can take one more client; then every server
    /// instance is disposed before this returns, so a client connecting afterwards finds no pipe. Calling it again does nothing.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();

        lock (_instancesLock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        if (!Volatile.Read(ref _listenLoop).Wait(ListenStopGuard))
        {
            Log.LogError(
                "Automation pipe {PipeName}: the listen loop did not end within {Guard} of dispose; closing its instances anyway",
                pipeName,
                ListenStopGuard
            );
        }

        // Disposed under the lock, so a connection task's release still closing an instance finishes before this returns.
        // Iterated over a copy: the lock is reentrant, and a continuation run inline by a dispose could release into the set.
        List<NamedPipeServerStream> open;
        lock (_instancesLock)
        {
            open = [.. _instances];
            foreach (NamedPipeServerStream pipe in open)
            {
                try
                {
                    pipe.Dispose();
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "Automation pipe {PipeName}: closing a server instance on dispose failed", pipeName);
                }
            }
        }

        Log.LogDebug("Automation pipe {PipeName}: closed {Count} server instance(s) on dispose", pipeName, open.Count);
    }
}
