using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// <c>wait_until</c> over a real named pipe, on a real <see cref="MainViewModel"/>: each condition met across a delayed state
/// change, the any/all modes, a timeout answered as a result with the last values, and the coded errors.
/// </summary>
public sealed class AutomationWaitUntilTests : AutomationHostFixture
{
    private const string Callsign = "UAL123";

    private static readonly TimeSpan ChangeDelay = TimeSpan.FromMilliseconds(300);

    private static (MainViewModel ViewModel, AircraftModel Aircraft) MainWith(bool onGround, string phase)
    {
        var viewModel = new MainViewModel(new FakeFilePickerService());
        var aircraft = new AircraftModel
        {
            Callsign = Callsign,
            IsOnGround = onGround,
            CurrentPhase = phase,
        };
        viewModel.Aircraft.Add(aircraft);
        return (viewModel, aircraft);
    }

    private static Func<IAutomationState?> StateOf(MainViewModel viewModel) => () => new MainViewModelAutomationState(viewModel);

    private async Task<JsonElement> WaitUntil(Func<IAutomationState?> state, object parameters)
    {
        using AutomationHost host = StartHost(() => Windows, state);
        await using AutomationPipeTestClient client = await Connect();
        return await Send(client, ProtocolMethods.WaitUntil, parameters);
    }

    /// <summary>
    /// Arms <paramref name="change"/> to run after <see cref="ChangeDelay"/>, sends <c>wait_until</c>, and checks the answer
    /// came after it.
    /// </summary>
    private async Task<JsonElement> WaitUntilAcross(Func<IAutomationState?> state, Action change, object parameters)
    {
        using AutomationHost host = StartHost(() => Windows, state);
        await using AutomationPipeTestClient client = await Connect();
        bool changed = false;
        DispatcherTimer.RunOnce(
            () =>
            {
                change();
                changed = true;
            },
            ChangeDelay
        );

        JsonElement response = await Send(client, ProtocolMethods.WaitUntil, parameters);

        Assert.True(changed, $"wait_until answered before the change: {response}");
        return response;
    }

    private static object Conditions(params object[] conditions) => new { conditions };

    private static int[] Held(JsonElement result) => [.. result.GetProperty("held").EnumerateArray().Select(index => index.GetInt32())];

    private static string LastValue(JsonElement result, int index) => result.GetProperty("last")[index].GetProperty("value").GetString() ?? "";

    [AvaloniaFact]
    public async Task OnGround_HeldWhenTheAircraftTouchesDown()
    {
        (MainViewModel viewModel, AircraftModel aircraft) = MainWith(onGround: false, "Landing");

        JsonElement result = Result(
            await WaitUntilAcross(StateOf(viewModel), () => aircraft.IsOnGround = true, Conditions(new { kind = "on_ground", callsign = Callsign }))
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal([0], Held(result));
        Assert.Equal("on ground", LastValue(result, 0));
        Assert.Equal("on_ground", result.GetProperty("last")[0].GetProperty("kind").GetString());
    }

    [AvaloniaFact]
    public async Task Landed_NotHeldForAGroundSpawn()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: true, "At Parking");

        JsonElement result = Result(
            await WaitUntil(StateOf(viewModel), new { conditions = new[] { new { kind = "landed", callsign = Callsign } }, timeoutMs = 300 })
        );

        Assert.False(result.GetProperty("met").GetBoolean());
        Assert.Empty(Held(result));
        Assert.Equal("on ground, not seen airborne", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task Landed_HeldAfterAirborneThenGround()
    {
        (MainViewModel viewModel, AircraftModel aircraft) = MainWith(onGround: false, "Landing");

        JsonElement result = Result(
            await WaitUntilAcross(StateOf(viewModel), () => aircraft.IsOnGround = true, Conditions(new { kind = "landed", callsign = Callsign }))
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal("landed", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task PhaseIs_CaseInsensitiveExact()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");

        JsonElement result = Result(
            await WaitUntil(
                StateOf(viewModel),
                Conditions(
                    new
                    {
                        kind = "phase_is",
                        callsign = Callsign,
                        phase = "Appr",
                    },
                    new
                    {
                        kind = "PHASE_IS",
                        callsign = Callsign,
                        phase = "approach",
                    }
                )
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal([1], Held(result));
        Assert.Equal("phase Approach", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task PhaseIsNot_HeldWhenThePhaseChanges()
    {
        (MainViewModel viewModel, AircraftModel aircraft) = MainWith(onGround: false, "Approach");

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel),
                () => aircraft.CurrentPhase = "Landing",
                Conditions(
                    new
                    {
                        kind = "phase_is_not",
                        callsign = Callsign,
                        phase = "approach",
                    }
                )
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal("phase Landing", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task QueueEmpty_HeldWhenPendingCommandsClears()
    {
        (MainViewModel viewModel, AircraftModel aircraft) = MainWith(onGround: false, "Approach");
        aircraft.PendingCommands = "AT 2000 CM 50";

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel),
                () => aircraft.PendingCommands = "",
                Conditions(new { kind = "queue_empty", callsign = Callsign })
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal("queue empty", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task SimSeconds_HeldAtTheThreshold()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");
        viewModel.ScenarioElapsedSeconds = 10;

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel),
                () => viewModel.ScenarioElapsedSeconds = 12,
                Conditions(new { kind = "sim_seconds", atLeast = 12 })
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal(12, result.GetProperty("simSeconds").GetDouble());
        Assert.Equal("sim 12.0", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task ModeAll_WaitsForEveryCondition()
    {
        (MainViewModel viewModel, AircraftModel aircraft) = MainWith(onGround: false, "Landing");

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel),
                () => aircraft.IsOnGround = true,
                new
                {
                    conditions = new object[]
                    {
                        new { kind = "on_ground", callsign = Callsign },
                        new
                        {
                            kind = "phase_is",
                            callsign = Callsign,
                            phase = "Landing",
                        },
                    },
                    mode = "all",
                }
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal([0, 1], Held(result));
    }

    [AvaloniaFact]
    public async Task Timeout_AnswersMetFalseWithLastValues_NotAnError()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");
        viewModel.ScenarioElapsedSeconds = 5;

        JsonElement result = Result(
            await WaitUntil(
                StateOf(viewModel),
                new
                {
                    conditions = new object[] { new { kind = "on_ground", callsign = Callsign }, new { kind = "sim_seconds", atLeast = 100 } },
                    timeoutMs = 300,
                }
            )
        );

        Assert.False(result.GetProperty("met").GetBoolean());
        Assert.Empty(Held(result));
        Assert.True(result.GetProperty("elapsedMs").GetInt32() >= 300);
        Assert.Equal("airborne", LastValue(result, 0));
        Assert.Equal("sim 5.0", LastValue(result, 1));
        Assert.Equal(1, result.GetProperty("last")[1].GetProperty("index").GetInt32());
    }

    [AvaloniaFact]
    public async Task AbsentCallsign_IsNotHeldAndReportsAbsent()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: true, "At Parking");

        JsonElement result = Result(
            await WaitUntil(StateOf(viewModel), new { conditions = new[] { new { kind = "on_ground", callsign = "DAL9" } }, timeoutMs = 200 })
        );

        Assert.False(result.GetProperty("met").GetBoolean());
        Assert.Equal("absent", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task UnknownKind_IsInvalidParam()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: true, "At Parking");

        JsonElement error = Error(
            await WaitUntil(StateOf(viewModel), Conditions(new { kind = "teleported", callsign = Callsign })),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Contains("teleported", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task NoMainViewModel_AnswersACodedError()
    {
        JsonElement error = Error(
            await WaitUntil(() => null, Conditions(new { kind = "on_ground", callsign = Callsign })),
            AutomationErrorCodes.UnsupportedOperation
        );

        Assert.Contains("main window", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static TerminalEntry Entry(string callsign, string message) =>
        new()
        {
            Timestamp = DateTime.Now,
            ElapsedSeconds = null,
            Initials = "",
            Kind = TerminalEntryKind.System,
            Callsign = callsign,
            Message = message,
        };

    [AvaloniaFact]
    public async Task LogMatches_OnlyEntriesAfterTheCall()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");
        viewModel.AddSystemEntry("old: cleared to land");

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel),
                () => viewModel.AddSystemEntry("new: cleared to land"),
                Conditions(new { kind = "log_matches", pattern = "CLEARED TO LAND" })
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal("matched: new: cleared to land", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task LogMatches_SurvivesATrimOfTheLog()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");
        for (int line = 0; line < 2000; line++)
        {
            viewModel.AddSystemEntry($"filler {line}");
        }

        JsonElement result = Result(
            await WaitUntilAcross(
                StateOf(viewModel),
                () =>
                {
                    for (int line = 0; line < 5; line++)
                    {
                        viewModel.AddSystemEntry($"more filler {line}");
                    }

                    viewModel.AddSystemEntry("go around");
                },
                Conditions(new { kind = "log_matches", pattern = "^go around$" })
            )
        );

        Assert.Equal(2000, viewModel.TerminalEntries.Count);
        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.Equal("matched: go around", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task LogMatches_CallsignFilter()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");
        using AutomationHost host = StartHost(() => Windows, StateOf(viewModel));
        await using AutomationPipeTestClient client = await Connect();
        bool ownLineAdded = false;
        DispatcherTimer.RunOnce(() => viewModel.TerminalEntries.Add(Entry("DAL9", "contact tower")), ChangeDelay);
        DispatcherTimer.RunOnce(
            () =>
            {
                viewModel.TerminalEntries.Add(Entry(Callsign, "contact tower"));
                ownLineAdded = true;
            },
            ChangeDelay * 2
        );

        JsonElement result = Result(
            await Send(
                client,
                ProtocolMethods.WaitUntil,
                Conditions(
                    new
                    {
                        kind = "log_matches",
                        pattern = "contact",
                        callsign = "ual123",
                    }
                )
            )
        );

        Assert.True(ownLineAdded, $"wait_until matched another aircraft's line: {result}");
        Assert.True(result.GetProperty("met").GetBoolean());
    }

    [AvaloniaFact]
    public async Task LogMatches_InvalidRegex_IsInvalidParam()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");

        JsonElement error = Error(
            await WaitUntil(StateOf(viewModel), Conditions(new { kind = "log_matches", pattern = "([" })),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("conditions[0].pattern", error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaFact]
    public async Task Screenshot_TakenAtTheMatchingPoll()
    {
        Border pad = Pad();
        Window window = ShowWindow("ShotWindow", pad, null);
        // The pause, run in the deciding poll after the capture, widens the pad again: a capture after it would be 300 wide.
        var state = new RecordingState(onGround: false)
        {
            OnPause = () =>
            {
                pad.Width = 300;
                window.UpdateLayout();
            },
        };

        JsonElement result = Result(
            await WaitUntilAcross(
                () => state,
                () =>
                {
                    state.Aircraft[0].IsOnGround = true;
                    pad.Width = 200;
                    window.UpdateLayout();
                },
                new
                {
                    conditions = new[] { new { kind = "on_ground", callsign = Callsign } },
                    screenshot = new { selector = "#ShotWindow #Pad" },
                    then = new[] { new { action = "pause" } },
                }
            )
        );

        JsonElement screenshot = result.GetProperty("screenshot");
        double scale = screenshot.GetProperty("scale").GetDouble();
        Assert.Equal(300, pad.Width);
        Assert.Equal((int)Math.Ceiling(200 * scale), screenshot.GetProperty("width").GetInt32());
        Assert.Equal((int)Math.Ceiling(80 * scale), screenshot.GetProperty("height").GetInt32());
    }

    [AvaloniaFact]
    public async Task LogMatches_IgnoresLinesRebuiltByARecordingLoad()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");
        using AutomationHost host = StartHost(() => Windows, StateOf(viewModel));
        await using AutomationPipeTestClient client = await Connect();
        bool liveLineAdded = false;
        DispatcherTimer.RunOnce(
            () =>
                viewModel.RepopulateTerminalFromRecording([
                    new TerminalBroadcastDto("", "System", "", "history: cleared to land", DateTime.UtcNow, 10),
                ]),
            ChangeDelay
        );
        DispatcherTimer.RunOnce(
            () =>
            {
                viewModel.AddSystemEntry("live: cleared to land");
                liveLineAdded = true;
            },
            ChangeDelay * 2
        );

        JsonElement result = Result(
            await Send(client, ProtocolMethods.WaitUntil, Conditions(new { kind = "log_matches", pattern = "cleared to land" }))
        );

        Assert.True(liveLineAdded, $"wait_until matched a line rebuilt from the recording: {result}");
        Assert.Equal("matched: live: cleared to land", LastValue(result, 0));
    }

    [AvaloniaFact]
    public async Task LogMatches_BacktrackingPattern_IsInvalidParam()
    {
        (MainViewModel viewModel, _) = MainWith(onGround: false, "Approach");

        JsonElement error = Error(
            await WaitUntil(StateOf(viewModel), new { conditions = new[] { new { kind = "log_matches", pattern = @"(\w+) \1" } }, timeoutMs = 300 }),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("conditions[0].pattern", error.GetProperty("details").GetProperty("param").GetString());
    }

    [AvaloniaFact]
    public async Task Then_MatchDecidedAsTheTimeoutFires_AnswersMetWithItsActions()
    {
        // The first poll holds the UI thread past the timeout, then sees the aircraft down: that poll decides the wait.
        var state = new RecordingState(onGround: false)
        {
            OnFirstRead = aircraft =>
            {
                Thread.Sleep(500);
                aircraft.IsOnGround = true;
            },
        };

        JsonElement result = Result(
            await WaitUntil(
                () => state,
                new
                {
                    conditions = new[] { new { kind = "on_ground", callsign = Callsign } },
                    timeoutMs = 200,
                    then = new[] { new { action = "pause" } },
                }
            )
        );

        Assert.True(result.GetProperty("met").GetBoolean());
        Assert.True(result.GetProperty("then")[0].GetProperty("ok").GetBoolean());
        Assert.Equal(["pause (on ground)"], state.Actions);
    }

    [AvaloniaFact]
    public async Task Then_PauseAndSetRate_RunOnMatch_InOrder()
    {
        var state = new RecordingState(onGround: false);

        JsonElement result = Result(
            await WaitUntilAcross(
                () => state,
                () => state.Aircraft[0].IsOnGround = true,
                new
                {
                    conditions = new[] { new { kind = "on_ground", callsign = Callsign } },
                    then = new object[] { new { action = "pause" }, new { action = "set_rate", rate = 4 } },
                }
            )
        );

        Assert.Equal(["pause (on ground)", "set_rate 4 (on ground)"], state.Actions);
        JsonElement then = result.GetProperty("then");
        Assert.Equal("pause", then[0].GetProperty("action").GetString());
        Assert.True(then[0].GetProperty("ok").GetBoolean());
        Assert.Equal("set_rate", then[1].GetProperty("action").GetString());
        Assert.True(then[1].GetProperty("ok").GetBoolean());
    }

    [AvaloniaFact]
    public async Task Then_RateNotInTheOptions_IsInvalidParamBeforeWaiting()
    {
        var state = new RecordingState(onGround: false);

        JsonElement error = Error(
            await WaitUntil(
                () => state,
                new
                {
                    conditions = new[] { new { kind = "on_ground", callsign = Callsign } },
                    timeoutMs = 5000,
                    then = new[] { new { action = "set_rate", rate = 3 } },
                }
            ),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("then[0].rate", error.GetProperty("details").GetProperty("param").GetString());
        Assert.Equal(0, state.AircraftReads);
        Assert.Empty(state.Actions);
    }

    [AvaloniaFact]
    public async Task Then_NotRunOnTimeout()
    {
        var state = new RecordingState(onGround: false);

        JsonElement result = Result(
            await WaitUntil(
                () => state,
                new
                {
                    conditions = new[] { new { kind = "on_ground", callsign = Callsign } },
                    timeoutMs = 300,
                    then = new[] { new { action = "pause" } },
                }
            )
        );

        Assert.False(result.GetProperty("met").GetBoolean());
        Assert.False(result.TryGetProperty("then", out _));
        Assert.Empty(state.Actions);
    }

    /// <summary>
    /// An <see cref="IAutomationState"/> with one aircraft that records each action and the aircraft's state when it ran;
    /// <see cref="OnFirstRead"/> runs inside the first read of the aircraft list and <see cref="OnPause"/> inside the pause.
    /// </summary>
    private sealed class RecordingState(bool onGround) : IAutomationState
    {
        private readonly List<AircraftModel> _aircraft = [new AircraftModel { Callsign = Callsign, IsOnGround = onGround }];

        public List<string> Actions { get; } = [];

        public int AircraftReads { get; private set; }

        public Action<AircraftModel> OnFirstRead { get; init; } = _ => { };

        public Action OnPause { get; init; } = () => { };

        public IReadOnlyList<AircraftModel> Aircraft
        {
            get
            {
                AircraftReads++;
                if (AircraftReads == 1)
                {
                    OnFirstRead(_aircraft[0]);
                }

                return _aircraft;
            }
        }

        public double ScenarioElapsedSeconds => 0;

        public bool IsPaused => false;

        public int SimRate => 1;

        public long TerminalCursor => 0;

        public IReadOnlyList<TerminalEntry> TerminalEntriesSince(long cursor) => [];

        public Task<AutomationActionOutcome> PauseAsync()
        {
            Task<AutomationActionOutcome> recorded = Record("pause");
            OnPause();
            return recorded;
        }

        public Task<AutomationActionOutcome> UnpauseAsync() => throw new InvalidOperationException("wait_until never unpauses.");

        public Task<AutomationActionOutcome> SetRateAsync(int rate) => Record($"set_rate {rate}");

        private Task<AutomationActionOutcome> Record(string action)
        {
            Actions.Add($"{action} ({(_aircraft[0].IsOnGround ? "on ground" : "airborne")})");
            return Task.FromResult(new AutomationActionOutcome(true, null));
        }
    }
}
