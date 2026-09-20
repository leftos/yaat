using System.IO.Pipes;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services.Discord;

/// <summary>
/// Finds the running Discord client's local RPC endpoint. Discord listens on up to ten of them —
/// <c>discord-ipc-0</c> through <c>discord-ipc-9</c>, one per concurrently running Discord build —
/// as named pipes on Windows and as Unix domain sockets elsewhere.
/// </summary>
internal sealed class DiscordIpcConnector : IDiscordIpcConnector
{
    private static readonly ILogger Log = AppLog.CreateLogger<DiscordIpcConnector>();

    /// <summary>
    /// How long one endpoint gets to answer. A running Discord accepts immediately; the bound keeps
    /// a full sweep of ten dead endpoints short, since it runs on every reconnect attempt.
    /// </summary>
    private const int ConnectTimeoutMs = 200;

    private const int LowestEndpointIndex = 0;
    private const int HighestEndpointIndex = 9;

    /// <summary>
    /// Whether the last sweep already reported that Discord is absent. A client without Discord
    /// sweeps forever, so the summary is logged once per absence and again only after a connection
    /// has succeeded in between — otherwise the log fills with the same line every minute.
    /// </summary>
    private bool _absenceLogged;

    public async Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
    {
        for (int index = LowestEndpointIndex; index <= HighestEndpointIndex; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream? stream = await TryConnectAsync(index, cancellationToken);
            if (stream is not null)
            {
                _absenceLogged = false;
                Log.LogDebug("Connected to Discord IPC endpoint discord-ipc-{Index}", index);
                return stream;
            }
        }

        if (!_absenceLogged)
        {
            _absenceLogged = true;
            Log.LogDebug("No Discord IPC endpoint answered (discord-ipc-0..9); Discord is not running");
        }

        return null;
    }

    private static async Task<Stream?> TryConnectAsync(int index, CancellationToken cancellationToken)
    {
        string name = $"discord-ipc-{index}";
        try
        {
            return OperatingSystem.IsWindows() ? await ConnectPipeAsync(name, cancellationToken) : await ConnectSocketAsync(name, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Every "Discord is not running" shape lands here: no such pipe, no such socket file,
            // connection refused, the connect timeout. Trace level, and the message rather than the
            // exception object: a machine without Discord produces ten of these per sweep forever,
            // and a stack trace for each says nothing the message does not.
            Log.LogTrace("Discord IPC endpoint {Name} did not accept a connection: {Reason}", name, ex.Message);
            return null;
        }
    }

    private static async Task<Stream> ConnectPipeAsync(string name, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static async Task<Stream> ConnectSocketAsync(string name, CancellationToken cancellationToken)
    {
        string path = Path.Combine(SocketDirectory(), name);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Where Discord places its socket files on Linux/macOS: the first of XDG_RUNTIME_DIR, TMPDIR,
    /// TMP and TEMP that is set, falling back to /tmp.
    /// </summary>
    private static string SocketDirectory()
    {
        foreach (string variable in new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return "/tmp";
    }
}
