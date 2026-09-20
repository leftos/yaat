using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Yaat.Client.Services.Discord;

namespace Yaat.Client.Tests;

/// <summary>
/// Drives <see cref="DiscordRichPresenceService"/> against a scripted Discord: an in-memory duplex
/// stream the test reads the client's frames from and writes Discord's answers into, plus a manual
/// clock so the reconnect back-off is asserted in fake seconds rather than waited out.
/// </summary>
public class DiscordRichPresenceServiceTests
{
    private const string ClientId = "1551088521731772439";

    private static readonly byte[] ReadyFrame = Encoding.UTF8.GetBytes("""{"cmd":"DISPATCH","evt":"READY","data":{"v":1}}""");

    private static DiscordActivity Activity(string details, string? state) => new(details, state, 1_758_240_000);

    [Fact]
    public async Task Publish_HandshakesThenSetsTheActivity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new FakeConnection();
        var connector = new ScriptedConnector(attempt => attempt == 0 ? connection.ClientStream : null, TimeProvider.System);
        using var service = new DiscordRichPresenceService(ClientId, connector, TimeProvider.System);

        service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));

        (int handshakeOpcode, byte[] handshakePayload) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);
        Assert.Equal(DiscordIpcFrame.HandshakeOpcode, handshakeOpcode);
        using (var handshake = JsonDocument.Parse(handshakePayload))
        {
            Assert.Equal(1, handshake.RootElement.GetProperty("v").GetInt32());
            Assert.Equal(ClientId, handshake.RootElement.GetProperty("client_id").GetString());
        }

        await DiscordIpcFrame.WriteAsync(connection.DiscordStream, DiscordIpcFrame.FrameOpcode, ReadyFrame, deadline.Token);

        (int setOpcode, byte[] setPayload) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);
        Assert.Equal(DiscordIpcFrame.FrameOpcode, setOpcode);
        using (var set = JsonDocument.Parse(setPayload))
        {
            JsonElement root = set.RootElement;
            Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("nonce").GetString()));

            JsonElement args = root.GetProperty("args");
            Assert.Equal(Environment.ProcessId, args.GetProperty("pid").GetInt32());

            JsonElement activity = args.GetProperty("activity");
            Assert.Equal("OAK Ground 7", activity.GetProperty("details").GetString());
            Assert.Equal("ZOA · KOAK", activity.GetProperty("state").GetString());
            Assert.Equal(1_758_240_000L, activity.GetProperty("timestamps").GetProperty("start").GetInt64());
        }

        // A scenario with neither an ARTCC nor an airport leaves nothing for the second line: the
        // field is omitted from the wire rather than sent as null, which Discord rejects.
        service.Publish(Activity("Solo session", null));

        (_, byte[] statelessPayload) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);
        using (var stateless = JsonDocument.Parse(statelessPayload))
        {
            JsonElement activity = stateless.RootElement.GetProperty("args").GetProperty("activity");
            Assert.Equal("Solo session", activity.GetProperty("details").GetString());
            Assert.False(activity.TryGetProperty("state", out _));
        }
    }

    /// <summary>
    /// Discord rate-limits activity updates, so nothing queues: an activity superseded before the
    /// connection was ready must never reach the wire at all.
    /// </summary>
    [Fact]
    public async Task Publish_Twice_SendsOnlyTheLatestOnceConnected()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new FakeConnection();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new GatedConnector(opened.Task, connection.ClientStream);
        using var service = new DiscordRichPresenceService(ClientId, connector, TimeProvider.System);

        service.Publish(Activity("Superseded scenario", "ZOA"));
        service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));
        opened.SetResult();

        await ReadHandshakeAndReplyReadyAsync(connection, deadline.Token);

        (_, byte[] payload) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);
        using var set = JsonDocument.Parse(payload);
        Assert.Equal("OAK Ground 7", set.RootElement.GetProperty("args").GetProperty("activity").GetProperty("details").GetString());
    }

    /// <summary>
    /// Clearing closes the IPC connection, which is how Discord is told to drop the entry — there is
    /// no "no activity" frame to send.
    /// </summary>
    [Fact]
    public async Task Clear_ClosesTheConnection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new FakeConnection();
        var connector = new ScriptedConnector(attempt => attempt == 0 ? connection.ClientStream : null, TimeProvider.System);
        using var service = new DiscordRichPresenceService(ClientId, connector, TimeProvider.System);

        service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));
        await ReadHandshakeAndReplyReadyAsync(connection, deadline.Token);
        await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);

        service.Clear();

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token));
    }

    /// <summary>
    /// The ordinary case on most machines: no Discord. Publishing must cost the caller nothing, and
    /// the worker must not hammer the endpoints — five seconds, doubling, capped at a minute.
    /// </summary>
    [Fact]
    public async Task DiscordNotRunning_PublishNeverThrows_AndRetriesWithBackoff()
    {
        var time = new ManualTimeProvider();
        var connector = new ScriptedConnector(_ => null, time);
        using var service = new DiscordRichPresenceService(ClientId, connector, time);

        service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));

        await WaitUntilAsync(() => connector.Attempts.Count >= 1, "the first connection attempt");

        TimeSpan[] expectedGaps =
        [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60),
        ];

        for (int i = 0; i < expectedGaps.Length; i++)
        {
            await WaitUntilAsync(() => time.PendingTimerCount > 0, $"the back-off before attempt {i + 2} to be armed");
            time.Advance(expectedGaps[i]);
            await WaitUntilAsync(() => connector.Attempts.Count >= i + 2, $"connection attempt {i + 2}");

            Assert.Equal(expectedGaps[i], connector.Attempts[i + 1] - connector.Attempts[i]);
        }
    }

    [Fact]
    public async Task Ping_IsAnsweredWithPong()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new FakeConnection();
        var connector = new ScriptedConnector(attempt => attempt == 0 ? connection.ClientStream : null, TimeProvider.System);
        using var service = new DiscordRichPresenceService(ClientId, connector, TimeProvider.System);

        service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));
        await ReadHandshakeAndReplyReadyAsync(connection, deadline.Token);
        await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);

        byte[] ping = Encoding.UTF8.GetBytes("""{"nonce":"keepalive-1"}""");
        await DiscordIpcFrame.WriteAsync(connection.DiscordStream, DiscordIpcFrame.PingOpcode, ping, deadline.Token);

        (int opcode, byte[] payload) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);
        Assert.Equal(DiscordIpcFrame.PongOpcode, opcode);
        Assert.Equal(ping, payload);
    }

    /// <summary>Discord restarting mid-scenario must end with the presence back up, not gone.</summary>
    [Fact]
    public async Task ServerClose_ReconnectsWhileAnActivityIsWanted()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var time = new ManualTimeProvider();
        var first = new FakeConnection();
        var second = new FakeConnection();
        var connector = new ScriptedConnector(
            attempt =>
                attempt switch
                {
                    0 => first.ClientStream,
                    1 => second.ClientStream,
                    _ => null,
                },
            time
        );
        using var service = new DiscordRichPresenceService(ClientId, connector, time);

        service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));
        await ReadHandshakeAndReplyReadyAsync(first, deadline.Token);
        await DiscordIpcFrame.ReadAsync(first.DiscordStream, deadline.Token);

        first.DiscordStream.Dispose();

        await WaitUntilAsync(() => time.PendingTimerCount > 0, "the reconnect back-off to be armed");
        time.Advance(TimeSpan.FromSeconds(5));

        await ReadHandshakeAndReplyReadyAsync(second, deadline.Token);
        (_, byte[] payload) = await DiscordIpcFrame.ReadAsync(second.DiscordStream, deadline.Token);
        using var set = JsonDocument.Parse(payload);
        Assert.Equal("OAK Ground 7", set.RootElement.GetProperty("args").GetProperty("activity").GetProperty("details").GetString());
    }

    /// <summary>
    /// Dispose runs on the UI thread during shutdown. A connector wedged in a native pipe call must
    /// not be able to hold the window open, the way the global key hook did in GitHub #347.
    /// </summary>
    [Fact]
    public async Task Dispose_ReturnsPromptly_WhileConnectIsHanging()
    {
        var connector = new HangingConnector();
        var service = new DiscordRichPresenceService(ClientId, connector, TimeProvider.System);
        try
        {
            service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));
            await WaitUntilAsync(() => connector.Entered, "the worker to enter ConnectAsync");

            var dispose = Task.Run(service.Dispose, TestContext.Current.CancellationToken);
            Task completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.True(completed == dispose, "Dispose must return even when the connector ignores cancellation");
        }
        finally
        {
            connector.Release();
        }
    }

    /// <summary>
    /// Something else can hold the pipe, accept the connection and never answer. The handshake has to
    /// give up and let the ordinary retry take over, or the presence is stuck for the life of the app.
    /// </summary>
    [Fact]
    public async Task HandshakeThatNeverAnswers_TimesOutAndRetries()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var time = new ManualTimeProvider();
        var silent = new FakeConnection();
        var answering = new FakeConnection();
        var connector = new ScriptedConnector(
            attempt =>
                attempt switch
                {
                    0 => silent.ClientStream,
                    1 => answering.ClientStream,
                    _ => null,
                },
            time
        );
        using var service = new DiscordRichPresenceService(ClientId, connector, time);

        service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));

        (int opcode, _) = await DiscordIpcFrame.ReadAsync(silent.DiscordStream, deadline.Token);
        Assert.Equal(DiscordIpcFrame.HandshakeOpcode, opcode);

        await WaitUntilAsync(() => time.PendingTimerCount > 0, "the handshake deadline to be armed");
        time.Advance(DiscordRichPresenceService.HandshakeTimeout);

        // The silent connection is dropped...
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await DiscordIpcFrame.ReadAsync(silent.DiscordStream, deadline.Token));

        // ...and the ordinary back-off brings the worker back to a Discord that does answer.
        await WaitUntilAsync(() => time.PendingTimerCount > 0, "the retry back-off to be armed");
        time.Advance(DiscordRichPresenceService.InitialRetryDelay);

        await ReadHandshakeAndReplyReadyAsync(answering, deadline.Token);
        (_, byte[] payload) = await DiscordIpcFrame.ReadAsync(answering.DiscordStream, deadline.Token);
        using var set = JsonDocument.Parse(payload);
        Assert.Equal("OAK Ground 7", set.RootElement.GetProperty("args").GetProperty("activity").GetProperty("details").GetString());
    }

    /// <summary>A scenario that ends mid-handshake must not leave the worker waiting on a READY it no longer needs.</summary>
    [Fact]
    public async Task Clear_DuringHandshake_AbandonsIt()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new FakeConnection();
        var connector = new ScriptedConnector(attempt => attempt == 0 ? connection.ClientStream : null, TimeProvider.System);
        using var service = new DiscordRichPresenceService(ClientId, connector, TimeProvider.System);

        service.Publish(Activity("OAK Ground 7", "ZOA · KOAK"));
        (int opcode, _) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);
        Assert.Equal(DiscordIpcFrame.HandshakeOpcode, opcode);

        // No READY is ever sent; the scenario ends while the handshake is outstanding.
        service.Clear();

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token));
    }

    /// <summary>
    /// Discord rejects an activity field over 128 characters, and the rejection only shows up in a
    /// response nobody reads, so an over-long scenario name has to be clamped before it goes out.
    /// </summary>
    [Fact]
    public async Task LongScenarioName_IsClampedTo128Characters()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = new FakeConnection();
        var connector = new ScriptedConnector(attempt => attempt == 0 ? connection.ClientStream : null, TimeProvider.System);
        using var service = new DiscordRichPresenceService(ClientId, connector, TimeProvider.System);

        service.Publish(new DiscordActivity(new string('A', 200), new string('B', 200), 1_758_240_000));
        await ReadHandshakeAndReplyReadyAsync(connection, deadline.Token);

        (_, byte[] payload) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);
        using (var set = JsonDocument.Parse(payload))
        {
            JsonElement activity = set.RootElement.GetProperty("args").GetProperty("activity");
            Assert.Equal(new string('A', 127) + "…", activity.GetProperty("details").GetString());
            Assert.Equal(128, activity.GetProperty("state").GetString()!.Length);
        }

        // A cut that would land inside a surrogate pair moves back a character rather than putting a
        // lone surrogate on the wire.
        service.Publish(new DiscordActivity(new string('A', 126) + "\U0001F600" + new string('A', 100), null, 1_758_240_000));

        (_, byte[] surrogatePayload) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, deadline.Token);
        using (var set = JsonDocument.Parse(surrogatePayload))
        {
            // 126 characters, not 127: cutting at 127 would have split the emoji's surrogate pair.
            string details = set.RootElement.GetProperty("args").GetProperty("activity").GetProperty("details").GetString()!;
            Assert.Equal(new string('A', 126) + "…", details);
        }
    }

    private static async Task ReadHandshakeAndReplyReadyAsync(FakeConnection connection, CancellationToken cancellationToken)
    {
        (int opcode, _) = await DiscordIpcFrame.ReadAsync(connection.DiscordStream, cancellationToken);
        Assert.Equal(DiscordIpcFrame.HandshakeOpcode, opcode);
        await DiscordIpcFrame.WriteAsync(connection.DiscordStream, DiscordIpcFrame.FrameOpcode, ReadyFrame, cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for {what}");
    }

    /// <summary>A connector that hands out a scripted stream per attempt and records the fake time of each.</summary>
    private sealed class ScriptedConnector(Func<int, Stream?> streamForAttempt, TimeProvider time) : IDiscordIpcConnector
    {
        private readonly Lock _gate = new();
        private readonly List<DateTimeOffset> _attempts = [];

        public IReadOnlyList<DateTimeOffset> Attempts
        {
            get
            {
                lock (_gate)
                {
                    return [.. _attempts];
                }
            }
        }

        public Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
        {
            int attempt;
            lock (_gate)
            {
                attempt = _attempts.Count;
                _attempts.Add(time.GetUtcNow());
            }

            return Task.FromResult(streamForAttempt(attempt));
        }
    }

    /// <summary>A connector that stays shut until the test opens it, so publishes can pile up first.</summary>
    private sealed class GatedConnector(Task opened, Stream stream) : IDiscordIpcConnector
    {
        private int _attempts;

        public async Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
        {
            await opened.WaitAsync(cancellationToken);
            return Interlocked.Increment(ref _attempts) == 1 ? stream : null;
        }
    }

    /// <summary>
    /// A connector wedged the way a native pipe connect can wedge: it ignores cancellation and never
    /// completes, so only Dispose's bounded wait can end the shutdown. Backed by a task that is never
    /// completed rather than a blocking wait, so it holds no thread pool thread while it hangs.
    /// </summary>
    private sealed class HangingConnector : IDiscordIpcConnector
    {
        private readonly TaskCompletionSource<Stream?> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Entered { get; private set; }

        public Task<Stream?> ConnectAsync(CancellationToken cancellationToken)
        {
            Entered = true;
            return _never.Task;
        }

        /// <summary>Lets the abandoned worker run to completion once the test is done with it.</summary>
        public void Release() => _never.TrySetResult(null);
    }

    /// <summary>
    /// A clock the test advances by hand. Only <see cref="TimeProvider.CreateTimer"/> and
    /// <see cref="TimeProvider.GetUtcNow"/> are needed: the service's back-off is a
    /// <c>Task.Delay</c> on this provider, and the connector stamps each attempt from it.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public int PendingTimerCount
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        public void Advance(TimeSpan by)
        {
            var due = new List<ManualTimer>();
            lock (_gate)
            {
                _now += by;
                foreach (ManualTimer timer in _timers)
                {
                    if (timer.DueAt <= _now)
                    {
                        due.Add(timer);
                    }
                }

                foreach (ManualTimer timer in due)
                {
                    _timers.Remove(timer);
                }
            }

            foreach (ManualTimer timer in due)
            {
                // Off the test thread: Task.Delay may run the awaiting worker's continuation inline
                // on whichever thread completes the timer.
                ThreadPool.QueueUserWorkItem(_ => timer.Fire());
            }
        }

        internal void Arm(ManualTimer timer, TimeSpan dueTime)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    timer.DueAt = _now + dueTime;
                    _timers.Add(timer);
                }
            }
        }

        internal void Disarm(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Arm(this, dueTime);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Disarm(this);

        public ValueTask DisposeAsync()
        {
            owner.Disarm(this);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The two ends of an in-memory IPC connection: what the client sees and what Discord sees.</summary>
    private sealed class FakeConnection
    {
        public FakeConnection()
        {
            var toClient = new ByteChannel();
            var fromClient = new ByteChannel();
            ClientStream = new DuplexStream(toClient, fromClient);
            DiscordStream = new DuplexStream(fromClient, toClient);
        }

        public Stream ClientStream { get; }

        public Stream DiscordStream { get; }
    }

    /// <summary>One direction of the connection: bytes queue up, a reader waits for them.</summary>
    private sealed class ByteChannel
    {
        private readonly Lock _gate = new();
        private readonly Queue<byte> _bytes = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _closed;

        public void Write(ReadOnlySpan<byte> data)
        {
            TaskCompletionSource changed;
            lock (_gate)
            {
                foreach (byte value in data)
                {
                    _bytes.Enqueue(value);
                }

                changed = Swap();
            }

            changed.TrySetResult();
        }

        public void Close()
        {
            TaskCompletionSource changed;
            lock (_gate)
            {
                _closed = true;
                changed = Swap();
            }

            changed.TrySetResult();
        }

        public async Task<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_bytes.Count > 0)
                    {
                        int count = Math.Min(destination.Length, _bytes.Count);
                        for (int i = 0; i < count; i++)
                        {
                            destination.Span[i] = _bytes.Dequeue();
                        }

                        return count;
                    }

                    if (_closed)
                    {
                        return 0;
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(cancellationToken);
            }
        }

        private TaskCompletionSource Swap()
        {
            TaskCompletionSource previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return previous;
        }
    }

    /// <summary>A stream over a pair of <see cref="ByteChannel"/>s. Closing it closes both directions.</summary>
    private sealed class DuplexStream(ByteChannel readFrom, ByteChannel writeTo) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            new(readFrom.ReadAsync(buffer, cancellationToken));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            writeTo.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            readFrom.Close();
            writeTo.Close();
            base.Dispose(disposing);
        }
    }
}
