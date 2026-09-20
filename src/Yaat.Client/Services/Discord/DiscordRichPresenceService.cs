using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services.Discord;

/// <summary>
/// Keeps Discord's "playing YAAT" entry in step with the scenario the user is running. One
/// background worker owns the whole conversation: it connects to the local Discord client only
/// while an activity is wanted, performs the RPC handshake, sends SET_ACTIVITY, answers Discord's
/// keepalive pings, and closes the connection when the activity is withdrawn — closing is what
/// makes Discord drop the entry.
///
/// Every failure mode is silent and cheap. Discord not running is the ordinary case: the connect
/// sweep returns nothing, the worker waits out a back-off that starts at five seconds and doubles
/// to a minute, and <see cref="Publish"/>/<see cref="Clear"/> keep returning immediately either way.
/// </summary>
public sealed class DiscordRichPresenceService : IRichPresencePublisher, IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<DiscordRichPresenceService>();

    /// <summary>How long the worker waits after the first failed connection attempt.</summary>
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling the doubling back-off stops at, so a machine without Discord keeps polling cheaply.</summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long Discord gets to acknowledge the handshake. Something else listening on the pipe can
    /// accept the connection and then say nothing at all; without a bound the worker would wait there
    /// for the life of the app, publishing nothing and never retrying.
    /// </summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long <see cref="Dispose"/> waits for the worker. Shutdown runs on the UI thread, so a
    /// worker wedged in a native pipe call must not be able to hold the window open.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly string _clientId;
    private readonly IDiscordIpcConnector _connector;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly Task _worker;

    private DiscordActivity? _wanted;
    private TaskCompletionSource _wantedChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <param name="clientId">The Discord application id the presence is published under.</param>
    public DiscordRichPresenceService(string clientId)
        : this(clientId, new DiscordIpcConnector(), TimeProvider.System) { }

    /// <summary>Test seam: lets tests script Discord's side of the wire and drive the back-off clock.</summary>
    internal DiscordRichPresenceService(string clientId, IDiscordIpcConnector connector, TimeProvider time)
    {
        _clientId = clientId;
        _connector = connector;
        _time = time;
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    public void Publish(DiscordActivity activity) => SetWanted(activity);

    public void Clear() => SetWanted(null);

    /// <summary>
    /// Records the activity the worker should be showing and wakes it. Only the latest one ever
    /// reaches Discord: a change that lands while the worker is still connecting replaces the
    /// earlier one rather than queueing behind it.
    /// </summary>
    private void SetWanted(DiscordActivity? activity)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (_disposed || (_wanted == activity))
            {
                return;
            }

            _wanted = activity;
            changed = _wantedChanged;
            _wantedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    /// <summary>
    /// Completes the next time the wanted activity changes. Capture it <em>before</em> reading
    /// <see cref="CurrentWanted"/>, so a change racing the read wakes the waiter rather than being lost.
    /// </summary>
    private Task WantedChanged
    {
        get
        {
            lock (_gate)
            {
                return _wantedChanged.Task;
            }
        }
    }

    private DiscordActivity? CurrentWanted
    {
        get
        {
            lock (_gate)
            {
                return _wanted;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan retry = InitialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOnceAsync(cancellationToken))
                {
                    retry = InitialRetryDelay;
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.LogDebug(ex, "Discord rich presence session failed; retrying in {Seconds}s", retry.TotalSeconds);
            }

            try
            {
                await BackOffAsync(retry, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            retry = NextRetryDelay(retry);
        }
    }

    /// <summary>
    /// One pass of the worker: idle until something is wanted, then connect and run a session.
    /// Returns true when the pass ended cleanly (nothing wanted, or the activity was withdrawn) and
    /// false when Discord could not be reached or the connection dropped with an activity still wanted.
    /// </summary>
    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        Task changed = WantedChanged;
        if (CurrentWanted is null)
        {
            await changed.WaitAsync(cancellationToken);
            return true;
        }

        Stream? stream = await _connector.ConnectAsync(cancellationToken);
        if (stream is null)
        {
            return false;
        }

        return await RunSessionAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Runs one connected conversation, closing the stream on the way out. Returns true when the
    /// session ended because nothing is wanted any more, false when Discord went away.
    /// </summary>
    private async Task<bool> RunSessionAsync(Stream stream, CancellationToken cancellationToken)
    {
        Task<(int Opcode, byte[] Payload)>? pendingRead = null;
        try
        {
            await DiscordIpcFrame.WriteAsync(stream, DiscordIpcFrame.HandshakeOpcode, DiscordRpcJson.Handshake(_clientId), cancellationToken);
            if (!await AwaitReadyAsync(stream, cancellationToken))
            {
                return true;
            }

            DiscordActivity? sent = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                Task changed = WantedChanged;
                DiscordActivity? wanted = CurrentWanted;
                if (wanted is null)
                {
                    return true;
                }

                if (wanted != sent)
                {
                    await SendActivityAsync(stream, wanted, cancellationToken);
                    sent = wanted;
                    continue;
                }

                pendingRead ??= DiscordIpcFrame.ReadAsync(stream, cancellationToken);
                if (await Task.WhenAny(pendingRead, changed) != pendingRead)
                {
                    continue;
                }

                (int opcode, byte[] payload) = await pendingRead;
                pendingRead = null;
                if (!await HandleFrameAsync(stream, opcode, payload, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            await CloseAsync(stream, pendingRead);
        }
    }

    /// <summary>
    /// Reads frames until Discord acknowledges the handshake with a READY event. Anything else it
    /// sends first (a ping, a response to an earlier session) is handled and skipped. Returns false
    /// when the activity was withdrawn while the handshake was outstanding, so the caller closes the
    /// connection instead of finishing a conversation nobody wants; throws when Discord does not
    /// answer within <see cref="HandshakeTimeout"/>, which the caller's back-off then handles.
    /// </summary>
    private async Task<bool> AwaitReadyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(HandshakeTimeout, _time, deadlineCts.Token);
        Task<(int Opcode, byte[] Payload)>? pendingRead = null;
        try
        {
            while (true)
            {
                Task changed = WantedChanged;
                if (CurrentWanted is null)
                {
                    return false;
                }

                pendingRead ??= DiscordIpcFrame.ReadAsync(stream, cancellationToken);
                Task first = await Task.WhenAny(pendingRead, changed, deadline);
                if (first == deadline)
                {
                    throw new IOException($"Discord did not acknowledge the RPC handshake within {HandshakeTimeout.TotalSeconds}s");
                }

                if (first != pendingRead)
                {
                    continue;
                }

                (int opcode, byte[] payload) = await pendingRead;
                pendingRead = null;
                if (opcode == DiscordIpcFrame.CloseOpcode)
                {
                    throw new IOException("Discord closed the connection during the RPC handshake");
                }

                if (opcode == DiscordIpcFrame.PingOpcode)
                {
                    await DiscordIpcFrame.WriteAsync(stream, DiscordIpcFrame.PongOpcode, payload, cancellationToken);
                    continue;
                }

                if ((opcode == DiscordIpcFrame.FrameOpcode) && IsReady(payload))
                {
                    return true;
                }
            }
        }
        finally
        {
            await deadlineCts.CancelAsync();
            Observe(pendingRead);
        }
    }

    private static bool IsReady(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("evt", out JsonElement evt)
                && (evt.ValueKind == JsonValueKind.String)
                && (evt.GetString() == "READY");
        }
        catch (JsonException ex)
        {
            Log.LogDebug(ex, "Discord sent a frame that is not JSON while the handshake was outstanding");
            return false;
        }
    }

    /// <summary>Answers a keepalive ping, reports a close. Returns false when Discord ended the connection.</summary>
    private static async Task<bool> HandleFrameAsync(Stream stream, int opcode, byte[] payload, CancellationToken cancellationToken)
    {
        if (opcode == DiscordIpcFrame.CloseOpcode)
        {
            Log.LogDebug("Discord closed the rich presence connection");
            return false;
        }

        if (opcode == DiscordIpcFrame.PingOpcode)
        {
            await DiscordIpcFrame.WriteAsync(stream, DiscordIpcFrame.PongOpcode, payload, cancellationToken);
        }

        return true;
    }

    private async Task SendActivityAsync(Stream stream, DiscordActivity activity, CancellationToken cancellationToken)
    {
        byte[] json = DiscordRpcJson.SetActivity(activity, Environment.ProcessId, Guid.NewGuid().ToString());
        await DiscordIpcFrame.WriteAsync(stream, DiscordIpcFrame.FrameOpcode, json, cancellationToken);
        Log.LogDebug("Discord rich presence set to \"{Details}\"", activity.Details);
    }

    /// <summary>
    /// Closing the stream is how the presence is withdrawn — Discord drops an application's activity
    /// as soon as its IPC connection goes away, so no null-activity frame is needed.
    /// </summary>
    private static async Task CloseAsync(Stream stream, Task<(int Opcode, byte[] Payload)>? pendingRead)
    {
        try
        {
            await stream.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Error closing the Discord IPC connection");
        }

        Observe(pendingRead);
    }

    /// <summary>
    /// A read left in flight fails once the stream is gone, or is simply abandoned. Observe it so the
    /// failure is logged rather than resurfacing later as an unobserved task exception.
    /// </summary>
    private static void Observe(Task<(int Opcode, byte[] Payload)>? pendingRead)
    {
        if (pendingRead is null)
        {
            return;
        }

        _ = pendingRead.ContinueWith(
            static t => Log.LogDebug(t.Exception, "Discord IPC read ended with the connection"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>
    /// Waits out the back-off, cutting it short if the wanted activity changes — a user who just
    /// started a scenario should not wait out a minute inherited from an earlier failure.
    /// </summary>
    private async Task BackOffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task changed = WantedChanged;
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Task.Delay(delay, _time, timerCts.Token);
        Task first = await Task.WhenAny(changed, timer);
        await timerCts.CancelAsync();
        if (first == timer)
        {
            await timer;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TimeSpan NextRetryDelay(TimeSpan current)
    {
        TimeSpan doubled = current + current;
        return doubled > MaxRetryDelay ? MaxRetryDelay : doubled;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _wanted = null;
        }

        _cts.Cancel();

        bool stopped;
        try
        {
            stopped = _worker.Wait(ShutdownTimeout);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Discord rich presence worker ended with an error");
            stopped = true;
        }

        if (stopped)
        {
            _cts.Dispose();
            return;
        }

        // The worker is wedged in something that ignored cancellation (a native pipe connect, say).
        // Shutdown proceeds and process exit reaps it; the token source stays alive because the
        // abandoned worker still holds the token.
        Log.LogWarning("Discord rich presence worker did not stop within {Seconds}s; abandoning it", ShutdownTimeout.TotalSeconds);
    }
}
