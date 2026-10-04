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

    // Cancelled on dispose, never disposed: connection tasks may still observe its token after the listen loop ends,
    // and a source with no timer or wait handle holds nothing to free.
    private readonly CancellationTokenSource _cts = new();
    private int _started;

    /// <summary>
    /// Starts listening on a background task and returns at once. <paramref name="connectionHandler"/> serves one client.
    /// A transport starts once; a second call throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Start(Func<Stream, CancellationToken, Task> connectionHandler)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException($"Automation pipe {pipeName} is already started; a transport starts once.");
        }

        CancellationToken ct = _cts.Token;
        _ = Task.Run(() => ListenLoop(connectionHandler, ct), ct);
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
        catch (Exception ex)
        {
            Log.LogError(ex, "Automation pipe {PipeName} stopped listening: a server instance could not be created or accepted", pipeName);
        }
    }

    private async Task AcceptOne(Func<Stream, CancellationToken, Task> connectionHandler, CancellationToken ct)
    {
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );

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
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, Func<Stream, CancellationToken, Task> handler, CancellationToken ct)
    {
        try
        {
            await using (pipe)
            {
                await handler(pipe, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log.LogDebug("Automation pipe {PipeName}: connection closed by host shutdown", pipeName);
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

    /// <summary>Stops accepting clients and closes every open connection.</summary>
    public void Dispose() => _cts.Cancel();
}
