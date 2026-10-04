using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Logging;
using Yaat.Client.Models;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>wait_until</c>: polls the simulation state the client sees on the UI thread every 100 ms until its <c>conditions</c>
/// hold (any one, or all with <c>mode: all</c>) or <c>timeoutMs</c> (default 30000, clamped to 100–600000) of wall time runs
/// out. Conditions: <c>on_ground</c>, <c>landed</c> (on the ground after being airborne at an earlier poll of this wait),
/// <c>phase_is</c> and <c>phase_is_not</c> (case-insensitive, exact), <c>queue_empty</c> (no pending conditional commands),
/// each with a <c>callsign</c>; <c>log_matches</c> (a terminal line added after the call matching <c>pattern</c>, from
/// <c>callsign</c> when given); and <c>sim_seconds</c> (<c>atLeast</c>). An aircraft not in the client holds no condition and
/// reads <c>absent</c>. At the poll that sees the match it captures the optional <c>screenshot</c> target and starts the
/// <c>then</c> actions (<c>pause</c>, <c>set_rate</c>) in order. Met or not, the answer is a <see cref="WaitUntilResult"/>,
/// never a timeout error; while the main window is not up it is <c>UNSUPPORTED_OPERATION</c>. The poll stops when the
/// client disconnects or the host stops.
/// </summary>
public sealed class WaitUntilHandler(Func<IAutomationState?> stateProvider, ScreenshotHandler screenshots) : IRequestHandler
{
    private static readonly ILogger Log = AppLog.CreateLogger("WaitUntilHandler");
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>A <c>then</c> action started at the matching poll, and the task that says how it ended.</summary>
    private sealed record StartedAction(WaitUntilAction Action, Task<AutomationActionOutcome> Outcome);

    /// <summary>
    /// A poll that ended the wait: the conditions held, with the screenshot it captured (a <see cref="RenderedTarget"/> or an
    /// error message) and the actions it started, or the main window was not up (<see cref="NoState"/>).
    /// </summary>
    private sealed record Decision(bool NoState, WaitUntilResult Result, object? Captured, IReadOnlyList<StartedAction> Started);

    public string Method => ProtocolMethods.WaitUntil;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        object parsed = WaitUntilParams.Read(request.Params);
        if (parsed is not WaitUntilParams parameters)
        {
            return parsed;
        }

        return await Poll(new Progress(parameters, screenshots), cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> Poll(Progress progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(progress.Parameters.TimeoutMs);
        while (true)
        {
            Decision? decision;
            try
            {
                decision = await PollWithin(progress, stopwatch, timeout - stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
                if (decision is null)
                {
                    await Task.Delay(PollDelay(timeout - stopwatch.Elapsed), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // No one will read the answer; a poll that decided meanwhile leaves a rendered bitmap to free.
                Release(progress.Close());
                throw;
            }

            if (decision is not null)
            {
                return await Answer(decision, cancellationToken).ConfigureAwait(false);
            }

            if (stopwatch.Elapsed >= timeout)
            {
                // A poll the UI thread ran after this wait stopped waiting for it may still have decided; its answer stands.
                Decision? late = progress.Close();
                return (late is not null)
                    ? await Answer(late, cancellationToken).ConfigureAwait(false)
                    : progress.NotMet((int)stopwatch.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>The pause before the next poll: the poll interval, or what is left of the wait when less; zero once it has run out.</summary>
    private static TimeSpan PollDelay(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return (remaining < PollInterval) ? remaining : PollInterval;
    }

    private static void Release(Decision? decision)
    {
        if (decision?.Captured is RenderedTarget rendered)
        {
            rendered.Bitmap.Dispose();
        }
    }

    /// <summary>
    /// One poll on the UI thread; null when the conditions do not hold or the UI thread does not run it within
    /// <paramref name="remaining"/>.
    /// </summary>
    private async Task<Decision?> PollWithin(Progress progress, Stopwatch stopwatch, TimeSpan remaining, CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        try
        {
            return await Dispatcher
                .UIThread.InvokeAsync(() => progress.Poll(stateProvider, stopwatch, cancellationToken))
                .GetTask()
                .WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The answer to a decision: encodes its screenshot off the UI thread and waits for its actions to end.</summary>
    private static async Task<object> Answer(Decision decision, CancellationToken cancellationToken)
    {
        if (decision.NoState)
        {
            return NoMainWindow();
        }

        (ScreenshotResult? screenshot, string? screenshotError) = Encode(decision.Captured);
        List<WaitUntilActionResult>? then =
            (decision.Started.Count > 0) ? await Finish(decision.Started, cancellationToken).ConfigureAwait(false) : null;
        return decision.Result with { Screenshot = screenshot, ScreenshotError = screenshotError, Then = then };
    }

    private static (ScreenshotResult? Screenshot, string? Error) Encode(object? captured)
    {
        if (captured is not RenderedTarget rendered)
        {
            return (null, captured as string);
        }

        try
        {
            return (ScreenshotHandler.Encode(rendered), null);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "wait_until could not encode the screenshot taken at the match");
            return (null, ex.Message);
        }
    }

    /// <summary>How each started action ended, in order; stops when the call is cancelled, so a hung send does not hold it.</summary>
    private static async Task<List<WaitUntilActionResult>> Finish(IReadOnlyList<StartedAction> started, CancellationToken cancellationToken)
    {
        List<WaitUntilActionResult> results = [];
        foreach (StartedAction action in started)
        {
            try
            {
                AutomationActionOutcome outcome = await action.Outcome.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!outcome.Ok)
                {
                    Log.LogWarning("wait_until action {Action} was refused: {Error}", action.Action.Name, outcome.Error);
                }

                results.Add(new WaitUntilActionResult(action.Action.Name, outcome.Ok, outcome.Error));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "wait_until action {Action} failed", action.Action.Name);
                results.Add(new WaitUntilActionResult(action.Action.Name, false, ex.Message));
            }
        }

        return results;
    }

    private static HandlerErrorResult NoMainWindow() =>
        HandlerResult.Error(
            AutomationErrorCodes.UnsupportedOperation,
            "The main window is not up yet, so there is no simulation state to wait on.",
            "Wait for the main window to open (wait_for on a selector in it), then call wait_until again.",
            null
        );

    /// <summary>
    /// What the polls of one wait have seen: each condition's last value, whether each <c>landed</c> aircraft has been seen
    /// airborne, how far each <c>log_matches</c> has read the terminal and what it matched, and the poll that decided the
    /// wait. A lock keeps a poll the UI thread runs late from deciding after the wait has answered.
    /// </summary>
    private sealed class Progress(WaitUntilParams parameters, ScreenshotHandler screenshots)
    {
        private readonly Lock _gate = new();
        private readonly string[] _last = [.. parameters.Conditions.Select(_ => "not polled")];
        private readonly bool[] _seenAirborne = new bool[parameters.Conditions.Count];
        private readonly long[] _logCursor = new long[parameters.Conditions.Count];
        private readonly string?[] _matched = new string?[parameters.Conditions.Count];
        private List<int> _held = [];
        private double _simSeconds;
        private bool _polled;
        private bool _closed;
        private Decision? _decided;

        public WaitUntilParams Parameters => parameters;

        /// <summary>Evaluates every condition against the state, on the UI thread; the decision when the wait is over, else null.</summary>
        public Decision? Poll(Func<IAutomationState?> stateProvider, Stopwatch stopwatch, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_closed || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                IAutomationState? state = stateProvider();
                if (state is null)
                {
                    return Decide(new Decision(NoState: true, Result(met: false, 0), null, []));
                }

                Evaluate(state);
                bool met = parameters.RequireAll ? (_held.Count == parameters.Conditions.Count) : (_held.Count > 0);
                if (!met)
                {
                    return null;
                }

                WaitUntilResult result = Result(met: true, (int)stopwatch.ElapsedMilliseconds);
                // The capture comes first, so it shows the state that met the conditions rather than what the actions change.
                object? captured = Capture();
                List<StartedAction> started = [.. parameters.Then.Select(action => Start(state, action))];
                return Decide(new Decision(NoState: false, result, captured, started));
            }
        }

        /// <summary>Ends the polling; the decision a poll reached before this, if any.</summary>
        public Decision? Close()
        {
            lock (_gate)
            {
                _closed = true;
                return _decided;
            }
        }

        public WaitUntilResult NotMet(int elapsedMs)
        {
            lock (_gate)
            {
                return Result(met: false, elapsedMs);
            }
        }

        private Decision Decide(Decision decision)
        {
            _closed = true;
            _decided = decision;
            return decision;
        }

        private WaitUntilResult Result(bool met, int elapsedMs) =>
            new(
                met,
                [.. _held],
                _simSeconds,
                elapsedMs,
                [.. parameters.Conditions.Select((condition, index) => new WaitUntilLastValue(index, condition.Name, _last[index]))],
                null,
                null,
                null
            );

        /// <summary>The screenshot target rendered now, or the message saying why it could not be; null when none was asked for.</summary>
        private object? Capture()
        {
            if (parameters.Screenshot is not { } target)
            {
                return null;
            }

            try
            {
                object captured = screenshots.Capture(target);
                return (captured is HandlerErrorResult error) ? $"{error.Error.Code}: {error.Error.Message}" : captured;
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "wait_until could not capture the screenshot at the match");
                return ex.Message;
            }
        }

        /// <summary>Starts <paramref name="action"/>: its synchronous part, which issues the command, runs here on the UI thread.</summary>
        private static StartedAction Start(IAutomationState state, WaitUntilAction action) => new(action, Run(state, action));

        private static async Task<AutomationActionOutcome> Run(IAutomationState state, WaitUntilAction action) =>
            (action.Kind == WaitUntilActionKind.Pause)
                ? await state.PauseAsync().ConfigureAwait(false)
                : await state.SetRateAsync(action.Rate).ConfigureAwait(false);

        private void Evaluate(IAutomationState state)
        {
            if (!_polled)
            {
                Array.Fill(_logCursor, state.TerminalCursor);
                _polled = true;
            }

            _simSeconds = state.ScenarioElapsedSeconds;
            List<int> held = [];
            for (int index = 0; index < parameters.Conditions.Count; index++)
            {
                (bool holds, string value) = EvaluateOne(index, parameters.Conditions[index], state);
                _last[index] = value;
                if (holds)
                {
                    held.Add(index);
                }
            }

            _held = held;
        }

        private (bool Holds, string Value) EvaluateOne(int index, WaitUntilCondition condition, IAutomationState state)
        {
            if (condition.Kind == WaitUntilKind.SimSeconds)
            {
                return (_simSeconds >= condition.AtLeast, $"sim {_simSeconds.ToString("F1", CultureInfo.InvariantCulture)}");
            }

            if (condition.Kind == WaitUntilKind.LogMatches)
            {
                _matched[index] ??= FirstMatch(index, condition, state);
                return (_matched[index] is { } message) ? (true, $"matched: {message}") : (false, "no match");
            }

            AircraftModel? aircraft = state.Aircraft.FirstOrDefault(candidate =>
                string.Equals(candidate.Callsign, condition.Callsign, StringComparison.OrdinalIgnoreCase)
            );
            return (aircraft is null) ? (false, "absent") : EvaluateAircraft(index, condition, aircraft);
        }

        private (bool Holds, string Value) EvaluateAircraft(int index, WaitUntilCondition condition, AircraftModel aircraft)
        {
            string groundValue = aircraft.IsOnGround ? "on ground" : "airborne";
            string phaseValue = $"phase {aircraft.CurrentPhase}";
            bool inPhase = string.Equals(aircraft.CurrentPhase, condition.Phase, StringComparison.OrdinalIgnoreCase);
            return condition.Kind switch
            {
                WaitUntilKind.OnGround => (aircraft.IsOnGround, groundValue),
                WaitUntilKind.Landed => EvaluateLanded(index, aircraft),
                WaitUntilKind.PhaseIs => (inPhase, phaseValue),
                WaitUntilKind.PhaseIsNot => (!inPhase, phaseValue),
                WaitUntilKind.QueueEmpty => string.IsNullOrEmpty(aircraft.PendingCommands)
                    ? (true, "queue empty")
                    : (false, $"queue: {aircraft.PendingCommands}"),
                _ => throw new UnreachableException($"wait_until kind {condition.Kind} is not an aircraft condition."),
            };
        }

        private (bool Holds, string Value) EvaluateLanded(int index, AircraftModel aircraft)
        {
            if (!aircraft.IsOnGround)
            {
                _seenAirborne[index] = true;
                return (false, "airborne");
            }

            return _seenAirborne[index] ? (true, "landed") : (false, "on ground, not seen airborne");
        }

        /// <summary>The message of the first terminal line past this condition's cursor that it matches; advances the cursor.</summary>
        private string? FirstMatch(int index, WaitUntilCondition condition, IAutomationState state)
        {
            foreach (TerminalEntry entry in state.TerminalEntriesSince(_logCursor[index]))
            {
                _logCursor[index] = entry.Sequence;
                if (!entry.IsHistory && Matches(condition, entry))
                {
                    return entry.Message;
                }
            }

            return null;
        }

        private static bool Matches(WaitUntilCondition condition, TerminalEntry entry)
        {
            if ((condition.Callsign.Length > 0) && !string.Equals(entry.Callsign, condition.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return condition.Pattern?.IsMatch(entry.Message) == true;
        }
    }
}
