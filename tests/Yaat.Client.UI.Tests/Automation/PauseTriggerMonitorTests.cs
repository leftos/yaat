using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Models;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// <see cref="PauseTriggerMonitor"/> over a fake client state, each check run by calling <see cref="PauseTriggerMonitor.Evaluate"/>:
/// which events each trigger fires on and which it does not, the one pause per check, and the baseline taken at arming.
/// </summary>
public sealed class PauseTriggerMonitorTests
{
    private static readonly PauseTriggerSet PilotCallsOnly = new(true, false, false, false, 0);
    private static readonly PauseTriggerSet WarningsOnly = new(false, true, false, false, 0);
    private static readonly PauseTriggerSet NewAircraftOnly = new(false, false, true, false, 0);
    private static readonly PauseTriggerSet GroundStopsOnly = new(false, false, false, true, 0);
    private static readonly PauseTriggerSet HeartbeatEveryTen = new(false, false, false, false, 10);

    private static TerminalEntry Entry(TerminalEntryKind kind, string callsign, string message, string? pilotKind) =>
        new()
        {
            Timestamp = DateTime.Now,
            ElapsedSeconds = null,
            Initials = "",
            Kind = kind,
            Callsign = callsign,
            Message = message,
            PilotKind = pilotKind,
        };

    private static TerminalEntry PilotLine(string? pilotKind) =>
        Entry(TerminalEntryKind.Say, "SWA123", "Oakland Ground, Southwest 123, ready to taxi", pilotKind);

    private static AircraftModel Taxiing(string callsign) =>
        new()
        {
            Callsign = callsign,
            IsOnGround = true,
            HasActiveTaxiRoute = true,
            GroundSpeed = 12,
            CurrentTaxiway = "B",
        };

    /// <summary>Arms <paramref name="triggers"/> on a monitor over <paramref name="state"/>, runs <paramref name="body"/>, then disarms.</summary>
    private static void WithMonitor(FakeState state, PauseTriggerSet triggers, Action<PauseTriggerMonitor> body)
    {
        var monitor = new PauseTriggerMonitor(state);
        monitor.Arm(triggers);
        try
        {
            body(monitor);
        }
        finally
        {
            monitor.Disarm();
        }
    }

    private static string[] Triggers(PauseTriggerMonitor monitor) => [.. monitor.EventsSince(0).Select(logged => logged.Trigger)];

    [AvaloniaFact]
    public void ProactiveLine_FiresAndPausesOnce()
    {
        var state = new FakeState();
        WithMonitor(
            state,
            PilotCallsOnly,
            monitor =>
            {
                state.Entries.Add(PilotLine("Proactive"));
                monitor.Evaluate();
                monitor.Evaluate();

                PauseEvent fired = Assert.Single(monitor.EventsSince(0));
                Assert.Equal(1, fired.Sequence);
                Assert.Equal("pilot_call", fired.Trigger);
                Assert.Equal("SWA123", fired.Callsign);
                Assert.Equal("Oakland Ground, Southwest 123, ready to taxi", fired.Detail);
                Assert.True(fired.Paused);
                Assert.Equal(1, state.Pauses);
            }
        );
    }

    [AvaloniaFact]
    public void ReportLine_Fires()
    {
        var state = new FakeState();
        WithMonitor(
            state,
            PilotCallsOnly,
            monitor =>
            {
                state.Entries.Add(PilotLine("Report"));
                monitor.Evaluate();

                Assert.Equal(["pilot_call"], Triggers(monitor));
            }
        );
    }

    [AvaloniaFact]
    public void ReadbackAndUnkindedLines_DoNotFire()
    {
        var state = new FakeState();
        WithMonitor(
            state,
            PilotCallsOnly,
            monitor =>
            {
                state.Entries.Add(PilotLine("Readback"));
                state.Entries.Add(PilotLine("SayReadback"));
                state.Entries.Add(PilotLine(null));
                monitor.Evaluate();

                Assert.Empty(monitor.EventsSince(0));
                Assert.Equal(0, state.Pauses);
            }
        );
    }

    [AvaloniaFact]
    public void LineAlreadyInTheLogAtArming_DoesNotFire()
    {
        var state = new FakeState();
        state.Entries.Add(PilotLine("Proactive"));
        WithMonitor(
            state,
            PilotCallsOnly,
            monitor =>
            {
                monitor.Evaluate();

                Assert.Empty(monitor.EventsSince(0));
            }
        );
    }

    [AvaloniaFact]
    public void WarningAndErrorEntries_Fire()
    {
        var state = new FakeState();
        WithMonitor(
            state,
            WarningsOnly,
            monitor =>
            {
                state.Entries.Add(Entry(TerminalEntryKind.Warning, "", "ELD is a VFR command", null));
                state.Entries.Add(Entry(TerminalEntryKind.Error, "UAL5", "Command error: unable", null));
                state.Entries.Add(Entry(TerminalEntryKind.System, "", "Scenario loaded", null));
                monitor.Evaluate();

                IReadOnlyList<PauseEvent> events = monitor.EventsSince(0);
                Assert.Equal(["warning", "warning"], events.Select(logged => logged.Trigger));
                Assert.Null(events[0].Callsign);
                Assert.Equal("ELD is a VFR command", events[0].Detail);
                Assert.Equal("UAL5", events[1].Callsign);
                Assert.Equal(1, state.Pauses);
            }
        );
    }

    [AvaloniaFact]
    public void ConflictPeerAppearing_FiresOnce()
    {
        var state = new FakeState();
        AircraftModel aircraft = Taxiing("SWA123");
        state.Aircraft.Add(aircraft);
        WithMonitor(
            state,
            WarningsOnly,
            monitor =>
            {
                monitor.Evaluate();
                aircraft.ConflictPeerCallsign = "UAL5";
                monitor.Evaluate();
                monitor.Evaluate();

                PauseEvent fired = Assert.Single(monitor.EventsSince(0));
                Assert.Equal("warning", fired.Trigger);
                Assert.Equal("conflict with UAL5", fired.Detail);
            }
        );
    }

    [AvaloniaFact]
    public void ConflictPeerSetAtArming_DoesNotFire()
    {
        var state = new FakeState();
        AircraftModel aircraft = Taxiing("SWA123");
        aircraft.ConflictPeerCallsign = "UAL5";
        state.Aircraft.Add(aircraft);
        WithMonitor(
            state,
            WarningsOnly,
            monitor =>
            {
                monitor.Evaluate();

                Assert.Empty(monitor.EventsSince(0));
            }
        );
    }

    [AvaloniaFact]
    public void NewCallsign_Fires_KnownCallsignDoesNot()
    {
        var state = new FakeState();
        state.Aircraft.Add(Taxiing("SWA123"));
        WithMonitor(
            state,
            NewAircraftOnly,
            monitor =>
            {
                monitor.Evaluate();
                state.Aircraft.Add(
                    new AircraftModel
                    {
                        Callsign = "UAL5",
                        AircraftType = "B738",
                        Departure = "OAK",
                        Destination = "SAN",
                    }
                );
                monitor.Evaluate();
                monitor.Evaluate();

                PauseEvent fired = Assert.Single(monitor.EventsSince(0));
                Assert.Equal("new_aircraft", fired.Trigger);
                Assert.Equal("UAL5", fired.Callsign);
                Assert.Equal("B738 OAK→SAN", fired.Detail);
            }
        );
    }

    [AvaloniaFact]
    public void DelayedAircraft_FiresNewAircraftWhenItSpawns_NotWhileDelayed()
    {
        var state = new FakeState();
        var delayed = new AircraftModel
        {
            Callsign = "DAL3",
            AircraftType = "A321",
            Departure = "LAX",
            Destination = "OAK",
            Status = "Delayed (120s)",
        };
        state.Aircraft.Add(delayed);
        WithMonitor(
            state,
            NewAircraftOnly,
            monitor =>
            {
                monitor.Evaluate();
                Assert.Empty(monitor.EventsSince(0));

                delayed.Status = "Active";
                monitor.Evaluate();
                monitor.Evaluate();

                PauseEvent fired = Assert.Single(monitor.EventsSince(0));
                Assert.Equal("new_aircraft", fired.Trigger);
                Assert.Equal("DAL3", fired.Callsign);
            }
        );
    }

    [AvaloniaFact]
    public void TaxiingAircraftThatStops_FiresAtFiveSecondsOnce_AndAgainAfterMovingAndStoppingAgain()
    {
        var state = new FakeState();
        AircraftModel aircraft = Taxiing("SWA123");
        aircraft.HasTaxiwayHoldShortAhead = true;
        state.Aircraft.Add(aircraft);
        WithMonitor(
            state,
            GroundStopsOnly,
            monitor =>
            {
                EvaluateAt(monitor, state, 1);
                aircraft.GroundSpeed = 0;
                EvaluateAt(monitor, state, 2);
                EvaluateAt(monitor, state, 6.9);
                Assert.Empty(monitor.EventsSince(0));

                EvaluateAt(monitor, state, 7);
                PauseEvent fired = Assert.Single(monitor.EventsSince(0));
                Assert.Equal("ground_stop", fired.Trigger);
                Assert.Equal("stopped on B, holding short", fired.Detail);
                Assert.Equal(7, fired.SimSeconds);

                EvaluateAt(monitor, state, 30);
                Assert.Single(monitor.EventsSince(0));

                aircraft.GroundSpeed = 8;
                EvaluateAt(monitor, state, 31);
                aircraft.GroundSpeed = 0;
                EvaluateAt(monitor, state, 32);
                EvaluateAt(monitor, state, 37);
                Assert.Equal(["ground_stop", "ground_stop"], Triggers(monitor));
            }
        );
    }

    [AvaloniaFact]
    public void StoppedAircraftWithNoTaxiRouteHoldOrQueue_NeverFires()
    {
        var state = new FakeState();
        AircraftModel aircraft = Taxiing("SWA123");
        aircraft.HasActiveTaxiRoute = false;
        state.Aircraft.Add(aircraft);
        WithMonitor(
            state,
            GroundStopsOnly,
            monitor =>
            {
                EvaluateAt(monitor, state, 1);
                aircraft.GroundSpeed = 0;
                EvaluateAt(monitor, state, 2);
                EvaluateAt(monitor, state, 20);

                Assert.Empty(monitor.EventsSince(0));
            }
        );
    }

    [AvaloniaFact]
    public void Heartbeat_FiresEveryInterval_NotDuringAPause_NorRightAfterALongPause()
    {
        var state = new FakeState();
        WithMonitor(
            state,
            HeartbeatEveryTen,
            monitor =>
            {
                EvaluateAt(monitor, state, 9.9);
                Assert.Empty(monitor.EventsSince(0));

                EvaluateAt(monitor, state, 10);
                PauseEvent fired = Assert.Single(monitor.EventsSince(0));
                Assert.Equal("heartbeat", fired.Trigger);
                Assert.Null(fired.Callsign);
                Assert.Equal("scan", fired.Detail);

                state.IsPaused = true;
                EvaluateAt(monitor, state, 25);
                EvaluateAt(monitor, state, 200);
                Assert.Single(monitor.EventsSince(0));

                state.IsPaused = false;
                EvaluateAt(monitor, state, 200.2);
                Assert.Single(monitor.EventsSince(0));

                EvaluateAt(monitor, state, 210.2);
                Assert.Equal(["heartbeat", "heartbeat"], Triggers(monitor));
            }
        );
    }

    [AvaloniaFact]
    public void EventsWhileAlreadyPaused_AreLoggedAsNotPausing_AndSendNoPause()
    {
        var state = new FakeState { IsPaused = true };
        WithMonitor(
            state,
            PilotCallsOnly,
            monitor =>
            {
                state.Entries.Add(PilotLine("Proactive"));
                monitor.Evaluate();

                PauseEvent logged = Assert.Single(monitor.EventsSince(0));
                Assert.False(logged.Paused);
                Assert.Equal(0, state.Pauses);
            }
        );
    }

    [AvaloniaFact]
    public void EventsSince_ReturnsOnlyNewerEvents()
    {
        var state = new FakeState();
        WithMonitor(
            state,
            PilotCallsOnly,
            monitor =>
            {
                state.Entries.Add(PilotLine("Proactive"));
                monitor.Evaluate();
                state.Entries.Add(PilotLine("Report"));
                monitor.Evaluate();

                PauseEvent newer = Assert.Single(monitor.EventsSince(1));
                Assert.Equal(2, newer.Sequence);
            }
        );
    }

    [AvaloniaFact]
    public void Disarm_StopsFiring()
    {
        var state = new FakeState();
        WithMonitor(
            state,
            PilotCallsOnly,
            monitor =>
            {
                monitor.Disarm();
                state.Entries.Add(PilotLine("Proactive"));
                monitor.Evaluate();

                Assert.False(monitor.IsArmed);
                Assert.Empty(monitor.EventsSince(0));
                Assert.Equal(0, state.Pauses);
            }
        );
    }

    private static void EvaluateAt(PauseTriggerMonitor monitor, FakeState state, double simSeconds)
    {
        state.ScenarioElapsedSeconds = simSeconds;
        monitor.Evaluate();
    }

    /// <summary>An <see cref="IAutomationState"/> whose aircraft, terminal, clock and pause state a test sets, counting each pause.</summary>
    private sealed class FakeState : IAutomationState
    {
        public List<AircraftModel> Aircraft { get; } = [];

        public List<TerminalEntry> Entries { get; } = [];

        public int Pauses { get; private set; }

        IReadOnlyList<AircraftModel> IAutomationState.Aircraft => Aircraft;

        public double ScenarioElapsedSeconds { get; set; }

        public bool IsPaused { get; set; }

        public int SimRate => 1;

        public long TerminalCursor => (Entries.Count > 0) ? Entries[^1].Sequence : 0;

        public IReadOnlyList<TerminalEntry> TerminalEntriesSince(long cursor) => [.. Entries.Where(entry => entry.Sequence > cursor)];

        public Task<AutomationActionOutcome> PauseAsync()
        {
            Pauses++;
            return Task.FromResult(new AutomationActionOutcome(true, null));
        }

        public Task<AutomationActionOutcome> UnpauseAsync() => throw new InvalidOperationException("The pause triggers never unpause.");

        public Task<AutomationActionOutcome> SetRateAsync(int rate) => throw new InvalidOperationException("The pause triggers never set the rate.");
    }
}
