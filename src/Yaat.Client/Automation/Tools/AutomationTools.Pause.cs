using System.ComponentModel;
using System.Text.Json;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    private const int MinHeartbeatSeconds = 5;
    private const int MaxHeartbeatSeconds = 600;

    [AutomationTool(
        "arm_pause_triggers",
        "Arms the pause triggers: from now the client pauses the sim by itself the moment one fires, even between tool calls, and logs "
            + "each event for get_pause_events. Re-arming replaces the set and starts afresh; nothing already on the terminal or in the "
            + "aircraft list fires.",
        nameof(NoScenario)
    )]
    public Task<AppToolOutcome> ArmPauseTriggers(
        [Description("Pause when a pilot calls on their own (an initial call, a request or a report), not on a readback.")] bool pilotCalls,
        [Description("Pause on a new warning or error line on the terminal, and when an aircraft gets a conflict alert.")] bool warnings,
        [Description("Pause when an aircraft appears that the client has not shown before.")] bool newAircraft,
        [Description("Pause when an aircraft on a taxi route, a hold or a runway queue has stood still for 5 sim seconds.")] bool groundStops,
        [Description("Pause every this many sim seconds of running time, from 5 to 600; 0 for no heartbeat.")] int heartbeatSeconds
    )
    {
        if ((heartbeatSeconds != 0) && ((heartbeatSeconds < MinHeartbeatSeconds) || (heartbeatSeconds > MaxHeartbeatSeconds)))
        {
            throw new AppToolArgumentException(
                "heartbeatSeconds",
                $"'heartbeatSeconds' must be 0 (no heartbeat) or from {MinHeartbeatSeconds} to {MaxHeartbeatSeconds}, not {heartbeatSeconds}."
            );
        }

        PauseTriggerSet triggers = new(pilotCalls, warnings, newAircraft, groundStops, heartbeatSeconds);
        PauseTriggerMonitor.For(_viewModel).Arm(triggers);
        return Task.FromResult(AppToolOutcome.Done($"Pause triggers armed: {DescribeTriggers(triggers)}."));
    }

    [AutomationTool("disarm_pause_triggers", "Disarms the pause triggers; the event log stays readable with get_pause_events.", nameof(NoScenario))]
    public Task<AppToolOutcome> DisarmPauseTriggers()
    {
        var monitor = PauseTriggerMonitor.For(_viewModel);
        if (!monitor.IsArmed)
        {
            return Task.FromResult(AppToolOutcome.Done("Pause triggers were not armed."));
        }

        monitor.Disarm();
        return Task.FromResult(AppToolOutcome.Done("Pause triggers disarmed."));
    }

    [AutomationTool(
        "get_pause_events",
        "Reports as JSON whether the pause triggers are armed and the events they logged after a sequence number, oldest first: each "
            + "event's sequence, trigger, callsign, sim time, detail and whether it paused the sim. Pass the answer's cursor next time to "
            + "read only newer events.",
        nameof(NoScenario)
    )]
    public Task<AppToolOutcome> GetPauseEvents(
        [Description("The sequence number to read after: 0 for every logged event, or the cursor of the last answer.")] int sinceSequence
    )
    {
        var monitor = PauseTriggerMonitor.For(_viewModel);
        IReadOnlyList<PauseEvent> events = monitor.EventsSince(sinceSequence);
        long cursor = (events.Count > 0) ? events[^1].Sequence : sinceSequence;
        PauseEventsAnswer answer = new(
            monitor.IsArmed,
            cursor,
            [
                .. events.Select(logged => new PauseEventAnswer(
                    logged.Sequence,
                    logged.Trigger,
                    logged.Callsign,
                    logged.SimSeconds,
                    logged.Detail,
                    logged.Paused
                )),
            ]
        );
        return Task.FromResult(AppToolOutcome.Done(JsonSerializer.Serialize(answer, CoachingJsonOptions)));
    }

    /// <summary>The armed triggers as a list of words, such as <c>pilot calls, ground stops, heartbeat every 10 s</c>.</summary>
    private static string DescribeTriggers(PauseTriggerSet triggers)
    {
        List<string> names = [];
        AddIf(names, triggers.PilotCalls, "pilot calls");
        AddIf(names, triggers.Warnings, "warnings");
        AddIf(names, triggers.NewAircraft, "new aircraft");
        AddIf(names, triggers.GroundStops, "ground stops");
        AddIf(names, triggers.HeartbeatSeconds > 0, $"heartbeat every {triggers.HeartbeatSeconds} s");
        return (names.Count > 0) ? string.Join(", ", names) : "none";
    }

    private static void AddIf(List<string> names, bool armed, string name)
    {
        if (armed)
        {
            names.Add(name);
        }
    }

    private sealed record PauseEventsAnswer(bool Armed, long Cursor, IReadOnlyList<PauseEventAnswer> Events);

    private sealed record PauseEventAnswer(long Seq, string Trigger, string? Callsign, double T, string Detail, bool Paused);
}
