using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>
/// A test-side client of the automation pipe: one connection, one request line out and one response line back per call.
/// Every await carries a timeout so a host that never answers fails the test instead of hanging it.
/// </summary>
public sealed class AutomationPipeTestClient : IAsyncDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private int _nextId;

    private AutomationPipeTestClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Utf8NoBom);
    }

    /// <summary>Connects to <paramref name="pipeName"/>, failing with <see cref="TimeoutException"/> after <paramref name="timeout"/>.</summary>
    public static async Task<AutomationPipeTestClient> ConnectAsync(string pipeName, TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, CancellationToken.None);
        }
        catch (Exception)
        {
            await pipe.DisposeAsync();
            throw;
        }

        return new AutomationPipeTestClient(pipe);
    }

    /// <summary>Sends a request for <paramref name="method"/> with no params and returns the parsed response.</summary>
    public Task<JsonElement> SendAsync(string method)
    {
        int id = Interlocked.Increment(ref _nextId);
        return SendRawAsync(JsonSerializer.Serialize(new { id = id.ToString(CultureInfo.InvariantCulture), method }));
    }

    /// <summary>Sends <paramref name="line"/> verbatim as one request line and returns the parsed response line.</summary>
    public async Task<JsonElement> SendRawAsync(string line)
    {
        // Written straight to the pipe, without a StreamWriter: a writer's dispose-time flush throws once the host
        // has closed the pipe, which the shutdown test relies on.
        await _pipe.WriteAsync(Utf8NoBom.GetBytes(line + "\n")).AsTask().WaitAsync(ReplyTimeout);
        string? reply = await ReadLineAsync();
        Assert.NotNull(reply);
        using var document = JsonDocument.Parse(reply);
        return document.RootElement.Clone();
    }

    /// <summary>Sends <paramref name="line"/> as one request line without waiting for the response.</summary>
    public async Task WriteLineAsync(string line) => await _pipe.WriteAsync(Utf8NoBom.GetBytes(line + "\n")).AsTask().WaitAsync(ReplyTimeout);

    /// <summary>Reads the next line from the host; null once the host has closed the connection.</summary>
    public async Task<string?> ReadLineAsync() => await _reader.ReadLineAsync().WaitAsync(ReplyTimeout);

    /// <summary>The pipe's security descriptor as this client sees it.</summary>
    [SupportedOSPlatform("windows")]
    public PipeSecurity GetAccessControl() => _pipe.GetAccessControl();

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _pipe.DisposeAsync();
    }
}
