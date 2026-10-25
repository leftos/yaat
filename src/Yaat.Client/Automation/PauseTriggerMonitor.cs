using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Automation;

/// <summary>
/// Which events pause the sim for an agent working traffic: a pilot's own call, a warning or a conflict, a new aircraft, an aircraft
/// on a taxi route, hold or runway queue that stops, and a heartbeat every <paramref name="HeartbeatSeconds"/> sim seconds (0 for none).
/// </summary>
public sealed record PauseTriggerSet(bool PilotCalls, bool Warnings, bool NewAircraft, bool GroundStops, int HeartbeatSeconds);

/// <summary>
/// One event a pause trigger saw: its sequence in the monitor's log, the trigger's name, the aircraft (null for none), the sim time,
/// what happened, and whether this event paused the sim (false when the sim was already paused).
/// </summary>
public sealed record PauseEvent(long Sequence, string Trigger, string? Callsign, double SimSeconds, string Detail, bool Paused);

/// <summary>
/// Pauses the room's sim the moment something needs an agent's attention, while the agent is between calls. Armed with a
/// <see cref="PauseTriggerSet"/>, it checks the client every 200 ms on the UI thread, logs every event it sees, and sends one
/// <c>PAUSE</c> for a check that saw any while the sim runs. A warning or error line that only answers a command the automation
/// pipe itself sent — from <see cref="OwnCommandStarted"/> to <see cref="OwnCommandFinished"/> — fires nothing, since the caller
/// already has that answer. Automation only; nothing is saved.
/// </summary>
/// <param name="state">The client state it reads and the pause it sends.</param>
public sealed class PauseTriggerMonitor(IAutomationState state)
{
    public const string PilotCallTrigger = "pilot_call";
    public const string WarningTrigger = "warning";
    public const string NewAircraftTrigger = "new_aircraft";
    public const string GroundStopTrigger = "ground_stop";
    public const string HeartbeatTrigger = "heartbeat";

    private const int MaxEvents = 500;
    private const double MovingKnots = 1.0;
    private const double StoppedKnots = 0.5;
    private const double StoppedSeconds = 5.0;

    /// <summary>How long after a command's own answer comes back a late copy of it still counts as that same answer.</summary>
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(2);

    private static readonly ILogger Log = AppLog.CreateLogger("PauseTriggerMonitor");
    private static readonly ConditionalWeakTable<MainViewModel, PauseTriggerMonitor> Monitors = [];

    private readonly List<PauseEvent> _events = [];
    private readonly HashSet<string> _knownCallsigns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _conflictPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroundStopTracker> _groundStops = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingEvent> _pending = [];
    private readonly List<TerminalEntry> _held = [];
    private readonly List<TerminalEntry> _released = [];
    private readonly List<RecentAnswer> _recentAnswers = [];
    private DispatcherTimer? _timer;
    private int _ownCommandsInFlight;
    private long _terminalCursor;
    private double _heartbeatAnchor;
    private long _lastSequence;
    private bool _subscribed;

    /// <summary>Whether the triggers are armed.</summary>
    public bool IsArmed => Triggers is not null;

    /// <summary>The armed triggers, or null when disarmed.</summary>
    public PauseTriggerSet? Triggers { get; private set; }

    /// <summary>The monitor of <paramref name="viewModel"/>, created on first use and kept for the view model's life.</summary>
    public static PauseTriggerMonitor For(MainViewModel viewModel) =>
        Monitors.GetValue(viewModel, model => new PauseTriggerMonitor(new MainViewModelAutomationState(model)));

    /// <summary>
    /// Arms <paramref name="triggers"/>, replacing any armed before, from now: nothing already in the terminal, the aircraft list or
    /// the sim clock fires.
    /// </summary>
    public void Arm(PauseTriggerSet triggers)
    {
        Triggers = triggers;
        if (!_subscribed)
        {
            state.Changed += OnStateChanged;
            _subscribed = true;
        }

        _terminalCursor = state.TerminalCursor;
        _held.Clear();
        _released.Clear();
        _knownCallsigns.Clear();
        _conflictPeers.Clear();
        _groundStops.Clear();
        foreach (AircraftModel aircraft in SpawnedAircraft())
        {
            _knownCallsigns.Add(aircraft.Callsign);
            _conflictPeers[aircraft.Callsign] = aircraft.ConflictPeerCallsign;
        }

        _heartbeatAnchor = state.ScenarioElapsedSeconds;
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Evaluate());

        _timer.Start();
    }

    /// <summary>Disarms the triggers; the event log stays.</summary>
    public void Disarm()
    {
        Triggers = null;
        if (_subscribed)
        {
            state.Changed -= OnStateChanged;
            _subscribed = false;
        }

        _timer?.Stop();
    }

    /// <summary>
    /// Notes that the automation pipe has just sent a command of its own: until <see cref="OwnCommandFinished"/> says how it ended,
    /// warning and error lines are held back rather than fired, since the answer to that command is what the caller already gets.
    /// A counter, so commands that overlap hold the lines until the last of them ends.
    /// </summary>
    public void OwnCommandStarted() => _ownCommandsInFlight++;

    /// <summary>
    /// Notes that a command <see cref="OwnCommandStarted"/> began has ended: <paramref name="callsign"/> is the aircraft it named,
    /// empty or null for a command that named no aircraft, and <paramref name="answeredTexts"/> the texts that answer it. Those are
    /// remembered from this finish, so a line held while a command was in flight is dropped at the release when it answers any of
    /// them, however long it was held; for two seconds after that a late copy of an answer is dropped as it arrives. When no
    /// command is left in flight the other held lines fire at once as a normal check would.
    /// </summary>
    public void OwnCommandFinished(string? callsign, IReadOnlyCollection<string> answeredTexts)
    {
        if (_ownCommandsInFlight > 0)
        {
            _ownCommandsInFlight--;
        }

        if (answeredTexts.Count > 0)
        {
            _recentAnswers.Add(new RecentAnswer(callsign, [.. answeredTexts], Stopwatch.GetTimestamp()));
        }

        if (_ownCommandsInFlight > 0)
        {
            return;
        }

        foreach (TerminalEntry entry in _held)
        {
            if (!ClaimedByAnAnswer(entry))
            {
                _released.Add(entry);
            }
        }

        _held.Clear();
        Evaluate();
    }

    /// <summary>Checks the client at once when a terminal entry or an aircraft is added or removed; a no-op while disarmed.</summary>
    private void OnStateChanged(object? sender, EventArgs e) => Evaluate();

    /// <summary>The logged events above <paramref name="sequence"/>, oldest first.</summary>
    public IReadOnlyList<PauseEvent> EventsSince(long sequence) => [.. _events.Where(logged => logged.Sequence > sequence)];

    /// <summary>
    /// Checks the client once: logs every event the armed triggers see since the last check, and pauses the sim once when any
    /// fired while it runs. Does nothing while disarmed.
    /// </summary>
    public void Evaluate()
    {
        if (Triggers is not { } triggers)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        bool paused = state.IsPaused;
        double now = state.ScenarioElapsedSeconds;
        _pending.Clear();
        CheckTerminal(triggers);
        CheckAircraft(triggers, now);
        CheckHeartbeat(triggers, paused, now);
        if (_pending.Count == 0)
        {
            return;
        }

        bool pauses = !paused;
        foreach (PendingEvent pending in _pending)
        {
            _lastSequence++;
            _events.Add(new PauseEvent(_lastSequence, pending.Trigger, pending.Callsign, now, pending.Detail, pauses));
        }

        if (_events.Count > MaxEvents)
        {
            _events.RemoveRange(0, _events.Count - MaxEvents);
        }

        if (pauses)
        {
            _ = PauseAsync(_pending[0], started);
        }
    }

    private void CheckTerminal(PauseTriggerSet triggers)
    {
        ReleaseHeld(triggers);
        foreach (TerminalEntry entry in state.TerminalEntriesSince(_terminalCursor))
        {
            _terminalCursor = entry.Sequence;
            if (entry.IsHistory)
            {
                continue;
            }

            if (triggers.PilotCalls && (entry.PilotKind is "Proactive" or "Report"))
            {
                Fire(PilotCallTrigger, entry.Callsign, entry.Message);
            }
            else if (triggers.Warnings && (entry.Kind is TerminalEntryKind.Warning or TerminalEntryKind.Error))
            {
                if (_ownCommandsInFlight > 0)
                {
                    _held.Add(entry);
                }
                else if (!DropsAsOwnAnswer(entry))
                {
                    Fire(WarningTrigger, entry.Callsign, entry.Message);
                }
            }
        }
    }

    /// <summary>Fires the warning and error lines a finished own command released, which its own answer did not claim.</summary>
    private void ReleaseHeld(PauseTriggerSet triggers)
    {
        foreach (TerminalEntry entry in _released)
        {
            if (triggers.Warnings)
            {
                Fire(WarningTrigger, entry.Callsign, entry.Message);
            }
        }

        _released.Clear();
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is a late copy of an answer that came back in the last
    /// <see cref="AnswerWindow"/>; consumes the text it answers so the same text fires next time.
    /// </summary>
    private bool DropsAsOwnAnswer(TerminalEntry entry)
    {
        long now = Stopwatch.GetTimestamp();
        _recentAnswers.RemoveAll(answer => Stopwatch.GetElapsedTime(answer.Started, now) > AnswerWindow);
        return ClaimedByAnAnswer(entry);
    }

    /// <summary>Whether a recent answer claims <paramref name="entry"/>, consuming its text; the answer's age does not matter.</summary>
    private bool ClaimedByAnAnswer(TerminalEntry entry)
    {
        foreach (RecentAnswer answer in _recentAnswers)
        {
            if (IsAnsweredBy(answer.Texts, answer.Callsign, entry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> carries one of <paramref name="texts"/> as its message, from <paramref name="callsign"/>
    /// or from no aircraft, consuming that text: each text answers one line only.
    /// </summary>
    private static bool IsAnsweredBy(List<string> texts, string? callsign, TerminalEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Callsign) && !string.Equals(entry.Callsign, callsign, StringComparison.Ordinal))
        {
            return false;
        }

        int index = texts.FindIndex(text => string.Equals(text, entry.Message, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        texts.RemoveAt(index);
        return true;
    }

    private void CheckAircraft(PauseTriggerSet triggers, double now)
    {
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        foreach (AircraftModel aircraft in SpawnedAircraft())
        {
            present.Add(aircraft.Callsign);
            if (_knownCallsigns.Add(aircraft.Callsign) && triggers.NewAircraft)
            {
                Fire(NewAircraftTrigger, aircraft.Callsign, $"{aircraft.AircraftType} {aircraft.Departure}→{aircraft.Destination}");
            }

            CheckConflict(triggers, aircraft);
            CheckGroundStop(triggers, aircraft, now);
        }

        foreach (string gone in _groundStops.Keys.Where(callsign => !present.Contains(callsign)).ToList())
        {
            _groundStops.Remove(gone);
        }

        foreach (string gone in _conflictPeers.Keys.Where(callsign => !present.Contains(callsign)).ToList())
        {
            _conflictPeers.Remove(gone);
        }
    }

    private void CheckConflict(PauseTriggerSet triggers, AircraftModel aircraft)
    {
        string? peer = aircraft.ConflictPeerCallsign;
        bool hadPeer = _conflictPeers.TryGetValue(aircraft.Callsign, out string? previous) && !string.IsNullOrEmpty(previous);
        _conflictPeers[aircraft.Callsign] = peer;
        if (triggers.Warnings && !hadPeer && !string.IsNullOrEmpty(peer))
        {
            Fire(WarningTrigger, aircraft.Callsign, $"conflict with {peer}");
        }
    }

    private void CheckGroundStop(PauseTriggerSet triggers, AircraftModel aircraft, double now)
    {
        if (!IsOnAGroundClearance(aircraft))
        {
            _groundStops.Remove(aircraft.Callsign);
            return;
        }

        if (!_groundStops.TryGetValue(aircraft.Callsign, out GroundStopTracker? tracker))
        {
            tracker = new GroundStopTracker();
            _groundStops[aircraft.Callsign] = tracker;
        }

        if (tracker.StopCompleted(aircraft.GroundSpeed, now) && triggers.GroundStops)
        {
            string holdingShort = aircraft.HasTaxiwayHoldShortAhead ? ", holding short" : "";
            Fire(GroundStopTrigger, aircraft.Callsign, $"stopped on {aircraft.CurrentTaxiway}{holdingShort}");
        }
    }

    /// <summary>The aircraft in the sim now: a delayed spawn is left out until it joins, so it fires <c>new_aircraft</c> then.</summary>
    private IEnumerable<AircraftModel> SpawnedAircraft() => state.Aircraft.Where(aircraft => !aircraft.IsDelayed);

    /// <summary>Whether <paramref name="aircraft"/> is on the ground with a taxi route, a hold or a place in a runway queue.</summary>
    private static bool IsOnAGroundClearance(AircraftModel aircraft) =>
        aircraft.IsOnGround && (aircraft.HasActiveTaxiRoute || aircraft.IsHeld || (aircraft.RunwayQueuePosition > 0));

    private void CheckHeartbeat(PauseTriggerSet triggers, bool paused, double now)
    {
        if (paused)
        {
            _heartbeatAnchor = now;
            return;
        }

        if ((triggers.HeartbeatSeconds > 0) && ((now - _heartbeatAnchor) >= triggers.HeartbeatSeconds))
        {
            _heartbeatAnchor = now;
            Fire(HeartbeatTrigger, null, "scan");
        }
    }

    private void Fire(string trigger, string? callsign, string detail) =>
        _pending.Add(new PendingEvent(trigger, string.IsNullOrEmpty(callsign) ? null : callsign, detail));

    private async Task PauseAsync(PendingEvent cause, long started)
    {
        try
        {
            AutomationActionOutcome outcome = await state.PauseAsync();
            if (!outcome.Ok)
            {
                Log.LogWarning("Pause trigger {Trigger} could not pause the sim: {Error}", cause.Trigger, outcome.Error ?? "no reason given");
                return;
            }

            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.LogDebug("Pause trigger {Trigger} paused in {Ms} ms", cause.Trigger, Math.Round(milliseconds));
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Pause trigger {Trigger} failed to send PAUSE", cause.Trigger);
        }
    }

    private sealed record PendingEvent(string Trigger, string? Callsign, string Detail);

    /// <summary>A finished own command's answer: the aircraft it named, the texts not yet claimed, and when it came back.</summary>
    private sealed record RecentAnswer(string? Callsign, List<string> Texts, long Started);

    /// <summary>An aircraft's stop watch: whether it has moved since its last stop, and the sim time it stopped.</summary>
    private sealed class GroundStopTracker
    {
        private bool _moving;
        private double? _stoppedAt;

        /// <summary>
        /// Takes the aircraft's ground speed at <paramref name="now"/>; true once, when an aircraft that moved has stood still for
        /// <see cref="StoppedSeconds"/>. It must move again before the next.
        /// </summary>
        public bool StopCompleted(double groundSpeed, double now)
        {
            if (groundSpeed > MovingKnots)
            {
                _moving = true;
                _stoppedAt = null;
                return false;
            }

            if (!_moving || (groundSpeed >= StoppedKnots))
            {
                _stoppedAt = null;
                return false;
            }

            _stoppedAt ??= now;
            if ((now - _stoppedAt.Value) < StoppedSeconds)
            {
                return false;
            }

            _moving = false;
            _stoppedAt = null;
            return true;
        }
    }
}
