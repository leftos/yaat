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
/// <c>PAUSE</c> for a check that saw any while the sim runs. Automation only; nothing is saved.
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

    private static readonly ILogger Log = AppLog.CreateLogger("PauseTriggerMonitor");
    private static readonly ConditionalWeakTable<MainViewModel, PauseTriggerMonitor> Monitors = [];

    private readonly List<PauseEvent> _events = [];
    private readonly HashSet<string> _knownCallsigns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _conflictPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroundStopTracker> _groundStops = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingEvent> _pending = [];
    private DispatcherTimer? _timer;
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
                Fire(WarningTrigger, entry.Callsign, entry.Message);
            }
        }
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
