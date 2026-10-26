using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Handlers;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// <c>wait_until</c>'s <c>client_log_matches</c> over a real named pipe and a log file the test writes as the client's own writer
/// does: only lines written after the call count, a partial line waits for its newline, the timeout reports how many lines it read,
/// and the coded errors for a callsign, a bad pattern and no log file at all.
/// </summary>
public sealed class AutomationWaitUntilClientLogTests : AutomationHostFixture
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    private async Task<JsonElement> WaitUntil(Func<IAutomationState?> state, object parameters)
    {
        using AutomationHost host = StartHost(() => Windows, state);
        await using AutomationPipeTestClient client = await Connect();
        return await Send(client, ProtocolMethods.WaitUntil, parameters);
    }

    /// <summary>
    /// Sends <c>wait_until</c> and makes <paramref name="changes"/> one per poll from the second, each inside that poll's read
    /// of the state, so the first poll always sees the file as it was before any change.
    /// </summary>
    private async Task<JsonElement> WaitUntilAcross(Func<IAutomationState?> state, object parameters, Action first, params Action[] rest)
    {
        Action[] changes = [first, .. rest];
        int polls = 0;
        int applied = 0;
        IAutomationState? ChangingState()
        {
            polls++;
            if ((polls >= 2) && (applied < changes.Length))
            {
                changes[applied]();
                applied++;
            }

            return state();
        }

        using AutomationHost host = StartHost(() => Windows, ChangingState);
        await using AutomationPipeTestClient client = await Connect();
        JsonElement response = await Send(client, ProtocolMethods.WaitUntil, parameters);

        Assert.True(applied == changes.Length, $"wait_until answered before change {applied + 1} of {changes.Length}: {response}");
        return response;
    }

    private static object Conditions(params object[] conditions) => new { conditions };

    private static string LastValue(JsonElement result, int index) => result.GetProperty("last")[index].GetProperty("value").GetString() ?? "";

    private static Func<IAutomationState?> StateOf(MainViewModel viewModel, string clientLogPath) =>
        () => new LogPathState(new MainViewModelAutomationState(viewModel), clientLogPath);

    private static T OffUiThread<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    private static void OffUiThread(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    private static void OffUiThread(Action work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>Runs queued UI work until the polls reach <paramref name="count"/>, failing as hung once the hang guard passes.</summary>
    private static void RunUntilPolls(Func<int> polls, int count)
    {
        var guard = Stopwatch.StartNew();
        while (polls() < count)
        {
            Assert.True(guard.Elapsed < HangGuard, $"wait_until hung: poll {count} never ran");
            Dispatcher.UIThread.RunJobs();
            if (polls() < count)
            {
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>A FakeTimeProvider that counts the timers made on it, so the test advances only once the wait has armed the one it means.</summary>
    private sealed class TimerCountingClock(FakeTimeProvider inner) : TimeProvider
    {
        private int _created;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _created);
            return timer;
        }

        public void Advance(TimeSpan by) => inner.Advance(by);

        /// <summary>Waits until the wait has created <paramref name="count"/> timers, failing as hung once the hang guard passes.</summary>
        public void UntilCreated(int count)
        {
            var guard = Stopwatch.StartNew();
            while (Volatile.Read(ref _created) < count)
            {
                Assert.True(guard.Elapsed < HangGuard, $"wait_until hung: timer {count} was never armed");
                Thread.Sleep(5);
            }
        }
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_ALineWrittenAfterTheFirstPoll_Meets()
    {
        using var log = new TempClientLog();
        log.Write("10:00:00.000 [info] NavData: Navigation database built");
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel, log.FilePath),
                Conditions(new { kind = "client_log_matches", pattern = "navigation database built" }),
                () => log.Write("10:00:01.000 [info] NavData: Navigation database built from the cache")
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal("matched: 10:00:01.000 [info] NavData: Navigation database built from the cache", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_PartialLine_MatchesOnlyOnceTheNewlineIsWritten()
    {
        using var log = new TempClientLog();
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel, log.FilePath),
                Conditions(new { kind = "client_log_matches", pattern = "navigation database built" }),
                () => log.WriteWithoutNewline("10:00:00.000 [info] NavData: Navigation database built"),
                () => log.Newline()
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal("matched: 10:00:00.000 [info] NavData: Navigation database built", LastValue(result, 0));
    }

    [AvaloniaFact]
    public void ClientLogMatches_Timeout_ReportsLinesRead()
    {
        // Polls 1 and 2 run when the test says; then the UI thread is held, as a starved one is, while the wait reads the two
        // lines and its clock passes the deadline: the answer counts the lines the wait read, not the last poll's view.
        using var log = new TempClientLog();
        var viewModel = new MainViewModel(new FakeFilePickerService());
        var clock = new TimerCountingClock(new FakeTimeProvider());
        int polls = 0;
        IAutomationState? State()
        {
            polls++;
            if (polls == 2)
            {
                log.Write("10:00:00.000 [info] filler one");
                log.Write("10:00:00.001 [info] filler two");
            }

            return new LogPathState(new MainViewModelAutomationState(viewModel), log.FilePath);
        }

        using AutomationHost host = StartHost(() => Windows, State, clock);
        AutomationPipeTestClient client = OffUiThread(Connect);
        string request = JsonSerializer.Serialize(
            new
            {
                id = ProtocolMethods.WaitUntil,
                method = ProtocolMethods.WaitUntil,
                @params = new { conditions = new[] { new { kind = "client_log_matches", pattern = "navigation database built" } }, timeoutMs = 500 },
            }
        );
        OffUiThread(() => client.WriteLineAsync(request));
        clock.UntilCreated(1); // poll 1 queued, its answer timer armed
        RunUntilPolls(() => polls, 1);
        clock.UntilCreated(2); // the pause after poll 1
        OffUiThread(() => clock.Advance(TimeSpan.FromMilliseconds(100)));
        clock.UntilCreated(3); // poll 2 queued
        RunUntilPolls(() => polls, 2);
        clock.UntilCreated(4); // the pause after poll 2
        OffUiThread(() => clock.Advance(TimeSpan.FromMilliseconds(100)));
        clock.UntilCreated(5); // poll 3 queued: the read before it has taken both lines
        OffUiThread(() => clock.Advance(TimeSpan.FromMilliseconds(300))); // deadline passes, poll 3 still held

        string? reply = OffUiThread(client.ReadLineAsync);
        Dispatcher.UIThread.RunJobs();
        OffUiThread(() => client.DisposeAsync().AsTask());

        Assert.NotNull(reply);
        using var document = JsonDocument.Parse(reply);
        JsonElement result = Result(document.RootElement);
        Assert.False(result.GetProperty("met").GetBoolean());
        Assert.Equal("no match (2 lines read)", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_Callsign_IsInvalidParam()
    {
        using var log = new TempClientLog();
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement error = Error(
            await WaitUntil(
                StateOf(viewModel, log.FilePath),
                Conditions(
                    new
                    {
                        kind = "client_log_matches",
                        pattern = "built",
                        callsign = "UAL123",
                    }
                )
            ),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("conditions[0].callsign", error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_BadPattern_IsInvalidParam()
    {
        using var log = new TempClientLog();
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement error = Error(
            await WaitUntil(StateOf(viewModel, log.FilePath), Conditions(new { kind = "client_log_matches", pattern = "([" })),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("conditions[0].pattern", error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_NoLogFile_IsUnsupported()
    {
        var viewModel = new MainViewModel(new FakeFilePickerService());
        object conditions = Conditions(new { kind = "client_log_matches", pattern = "built" });

        JsonElement emptyPath = Error(await WaitUntil(StateOf(viewModel, ""), conditions), AutomationErrorCodes.UnsupportedOperation);
        Assert.Equal("The client writes no log file, so client_log_matches has nothing to read.", emptyPath.GetProperty("message").GetString());

        string missing = Path.Combine(Path.GetTempPath(), $"yaat-client-log-missing-{Guid.NewGuid():N}", "yaat-client.log");
        JsonElement missingPath = Error(await WaitUntil(StateOf(viewModel, missing), conditions), AutomationErrorCodes.UnsupportedOperation);
        Assert.Equal("The client writes no log file, so client_log_matches has nothing to read.", missingPath.GetProperty("message").GetString());
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_StartMidLine_DoesNotCountTheOlderLinesTail()
    {
        // The log ends mid-line when the wait starts: the rest of that line is not a line written after the wait began.
        using var log = new TempClientLog();
        log.WriteWithoutNewline("10:00:00.000 [info] filler: navigation data");
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel, log.FilePath),
                Conditions(new { kind = "client_log_matches", pattern = "built" }),
                () => log.Write("base built"),
                () =>
                {
                    log.Newline();
                    log.Write("10:00:01.000 [info] NavData: Navigation database built");
                }
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal("matched: 10:00:01.000 [info] NavData: Navigation database built", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_ReadsOffTheUiThread()
    {
        using var log = new TempClientLog();
        var viewModel = new MainViewModel(new FakeFilePickerService());
        List<bool> uiThread = [];
        ClientLogTail.BeforeRead = () =>
        {
            lock (uiThread)
            {
                uiThread.Add(Dispatcher.UIThread.CheckAccess());
            }
        };

        try
        {
            JsonElement result = Result(
                await WaitUntilAcross(
                    StateOf(viewModel, log.FilePath),
                    Conditions(new { kind = "client_log_matches", pattern = "navigation database built" }),
                    () => log.Write("10:00:00.000 [info] NavData: Navigation database built")
                )
            );
            Assert.True(result.GetProperty("met").GetBoolean());
        }
        finally
        {
            ClientLogTail.BeforeRead = null;
        }

        Assert.NotEmpty(uiThread);
        Assert.DoesNotContain(true, uiThread);
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_FromStart_MatchesALineWrittenBeforeTheWait()
    {
        using var log = new TempClientLog();
        log.Write("10:00:00.000 [info] Yaat.Client.ViewModels.MainViewModel: Navdata loaded: 112763 fixes, CIFP initialized");
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement result = Result(
            await WaitUntil(
                StateOf(viewModel, log.FilePath),
                new
                {
                    conditions = new[]
                    {
                        new
                        {
                            kind = "client_log_matches",
                            pattern = "Navdata loaded:",
                            fromStart = true,
                        },
                    },
                    timeoutMs = 2000,
                }
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal(
            "matched: 10:00:00.000 [info] Yaat.Client.ViewModels.MainViewModel: Navdata loaded: 112763 fixes, CIFP initialized",
            LastValue(result, 0)
        );
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_WithoutFromStart_IgnoresALineWrittenBeforeTheWait()
    {
        using var log = new TempClientLog();
        log.Write("10:00:00.000 [info] Yaat.Client.ViewModels.MainViewModel: Navdata loaded: 112763 fixes, CIFP initialized");
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement result = Result(
            await WaitUntil(
                StateOf(viewModel, log.FilePath),
                new { conditions = new[] { new { kind = "client_log_matches", pattern = "Navdata loaded:" } }, timeoutMs = 500 }
            )
        );

        Assert.False(result.GetProperty("met").GetBoolean());
        Assert.Equal("no match (0 lines read)", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_FromStartNotBoolean_IsInvalidParam()
    {
        using var log = new TempClientLog();
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement error = Error(
            await WaitUntil(
                StateOf(viewModel, log.FilePath),
                Conditions(
                    new
                    {
                        kind = "client_log_matches",
                        pattern = "built",
                        fromStart = "yes",
                    }
                )
            ),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("conditions[0].fromStart", error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaFact]
    public async Task ClientLogMatches_MixedFromStart_IsInvalidParam()
    {
        using var log = new TempClientLog();
        var viewModel = new MainViewModel(new FakeFilePickerService());

        JsonElement error = Error(
            await WaitUntil(
                StateOf(viewModel, log.FilePath),
                Conditions(
                    new
                    {
                        kind = "client_log_matches",
                        pattern = "built",
                        fromStart = true,
                    },
                    new { kind = "client_log_matches", pattern = "loaded" }
                )
            ),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("conditions[1].fromStart", error.GetProperty("details").GetProperty("param").GetString());
    }

    /// <summary>A client log file the test writes, under its own temp directory, opened as the client's own writer opens it.</summary>
    private sealed class TempClientLog : IDisposable
    {
        private readonly string _directory;
        private readonly StreamWriter _writer;

        public TempClientLog()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"yaat-client-log-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            FilePath = Path.Combine(_directory, "yaat-client.log");
            var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream) { AutoFlush = true };
        }

        public string FilePath { get; }

        /// <summary>Writes one complete line, ending it the way the client's provider does on Windows.</summary>
        public void Write(string line) => _writer.WriteLine(line);

        /// <summary>Writes text with no newline, as the provider can while a line is still being written.</summary>
        public void WriteWithoutNewline(string text) => _writer.Write(text);

        public void Newline() => _writer.Write('\n');

        public void Dispose()
        {
            _writer.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// An <see cref="IAutomationState"/> that differs from the view model's own in one thing only: the log file it names.
    /// </summary>
    private sealed class LogPathState(MainViewModelAutomationState inner, string clientLogPath) : IAutomationState
    {
        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public IReadOnlyList<AircraftModel> Aircraft => inner.Aircraft;

        public double ScenarioElapsedSeconds => inner.ScenarioElapsedSeconds;

        public bool IsPaused => inner.IsPaused;

        public int SimRate => inner.SimRate;

        public string ClientLogPath => clientLogPath;

        public long TerminalCursor => inner.TerminalCursor;

        public IReadOnlyList<TerminalEntry> TerminalEntriesSince(long cursor) => inner.TerminalEntriesSince(cursor);

        public Task<AutomationActionOutcome> PauseAsync() => inner.PauseAsync();

        public Task<AutomationActionOutcome> UnpauseAsync() => inner.UnpauseAsync();

        public Task<AutomationActionOutcome> SetRateAsync(int rate) => inner.SetRateAsync(rate);
    }
}
