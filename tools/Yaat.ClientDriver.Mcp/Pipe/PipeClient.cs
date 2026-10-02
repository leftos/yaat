using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Yaat.Client.Automation.Protocol;

namespace Yaat.ClientDriver.Mcp.Pipe;

/// <summary>
/// One connection to one YAAT client's automation pipe: line-delimited JSON requests, one in flight at a time, each
/// answered by the line carrying its own id. A request that does not complete cleanly — the connection breaking, this
/// client's timeout, the caller's cancel, an unparsable line or an answer to another request — leaves the pipe's
/// framing unknown, so the connection is dropped; the next request opens a fresh one.
/// </summary>
/// <param name="pipeName">The named pipe to connect to.</param>
/// <param name="pid">The client's process id, named in connection failures; null when the caller does not know it.</param>
/// <param name="logger">Where connection diagnostics go (stderr through the host's logging).</param>
public sealed class PipeClient(string pipeName, int? pid, ILogger<PipeClient> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _nextRequestId;
    private int _disposed;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <summary>Sends one request and deserializes the answer's <c>result</c> as <typeparamref name="T"/>.</summary>
    /// <param name="method">The host method, e.g. <c>ping</c> (see <see cref="ProtocolMethods"/>).</param>
    /// <param name="parameters">The request's params object, or null for a method that takes none.</param>
    /// <param name="ct">Cancels the send; the cancel propagates unchanged and drops the connection.</param>
    /// <exception cref="PipeRemoteException">The host answered with a coded error.</exception>
    /// <exception cref="McpException">The connection closed, failed or timed out; the next call reconnects.</exception>
    /// <exception cref="JsonException">The host answered with a line that is not a response; the connection is dropped.</exception>
    /// <exception cref="InvalidOperationException">The host answered with a foreign id, or with neither result nor error.</exception>
    public async Task<T> SendAsync<T>(string method, object? parameters, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A request queued behind DisposeAsync would otherwise revive a client that is being disposed.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await SendCoreAsync<T>(method, parameters, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The cancel can land after the request is on the wire; the host still answers it, and that late line
                // would be read as the next request's answer, so the connection goes with the cancellation.
                DisposeConnection();
                throw;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                logger.LogDebug(ex, "The automation pipe {PipeName} failed; the next request reconnects", pipeName);
                DisposeConnection();
                throw new McpException(ConnectionFailureMessage(ex), ex);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Stops the client: waits for a request in flight to finish, then closes the connection. A later
    /// <see cref="SendAsync{T}"/> faults with <see cref="ObjectDisposedException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            DisposeConnection();
        }
        finally
        {
            // The gate is never disposed: the request holding it releases it on the way out, and disposing it under
            // that request would replace the request's own failure with an ObjectDisposedException.
            _sendGate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is not null)
        {
            return;
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, Utf8NoBom);
        _writer = new StreamWriter(pipe, Utf8NoBom) { AutoFlush = true };
    }

    private async Task<T> SendCoreAsync<T>(string method, object? parameters, CancellationToken ct)
    {
        StreamWriter writer = _writer ?? throw new InvalidOperationException("The automation pipe is not connected");
        StreamReader reader = _reader ?? throw new InvalidOperationException("The automation pipe is not connected");

        string id = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        var request = new AutomationRequest
        {
            Id = id,
            Method = method,
            Params = parameters is null ? null : ProtocolSerializer.ToElement(parameters),
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);

        await writer.WriteLineAsync(ProtocolSerializer.Serialize(request).AsMemory(), timeoutCts.Token).ConfigureAwait(false);
        string line =
            await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)
            ?? throw new IOException("The host closed the connection without answering");

        AutomationResponse response = ParseResponse(line);
        if (!string.Equals(response.Id, id, StringComparison.Ordinal))
        {
            // An answer to another request means the pipe's framing is off; nothing read from it can be trusted.
            DisposeConnection();
            throw new InvalidOperationException($"The host answered request id '{response.Id}' while request '{id}' was in flight");
        }

        if (response.ErrorInfo is { } error)
        {
            throw new PipeRemoteException(error.Code, error.Message, error.Suggested);
        }

        JsonElement result =
            response.Result ?? throw new InvalidOperationException($"The host answered request '{id}' with neither a result nor an error");
        return ProtocolSerializer.Deserialize<T>(result.GetRawText())
            ?? throw new InvalidOperationException($"The result of '{method}' did not deserialize as {typeof(T).Name}");
    }

    // A line that is not a response leaves the framing unknown, so the connection goes with the parse failure.
    private AutomationResponse ParseResponse(string line)
    {
        AutomationResponse? response;
        try
        {
            response = ProtocolSerializer.Deserialize<AutomationResponse>(line);
        }
        catch (JsonException)
        {
            DisposeConnection();
            throw;
        }

        if (response is null)
        {
            // A null answer carries no id, so the answer to this request may still be coming: the connection is desynced.
            DisposeConnection();
            throw new InvalidOperationException($"The host answered '{line}', which is not an automation response");
        }

        return response;
    }

    // A failure of the connection itself rather than one the host answered with: a broken pipe, a disposed handle, or
    // this client's own connect or request timeout. The caller's cancel is handled separately in SendAsync.
    private static bool IsConnectionFailure(Exception ex) =>
        (ex is IOException) || (ex is ObjectDisposedException) || (ex is TimeoutException) || (ex is OperationCanceledException);

    private string ConnectionFailureMessage(Exception ex)
    {
        string who = pid is { } processId ? $" of YAAT client pid {processId}" : string.Empty;
        return $"The automation pipe '{pipeName}'{who} closed or timed out: {ex.Message}";
    }

    private void DisposeConnection()
    {
        DisposeResource(_writer);
        DisposeResource(_reader);
        DisposeResource(_pipe);
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    private void DisposeResource(IDisposable? resource)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The caller is already being told the pipe is gone; a flush or a close on the broken handle adds nothing.
            logger.LogDebug(ex, "Disposing the broken automation pipe {PipeName} failed", pipeName);
        }
    }
}
