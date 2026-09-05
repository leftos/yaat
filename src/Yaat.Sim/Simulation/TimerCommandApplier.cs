using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation;

/// <summary>
/// The one body of a <c>TIMER</c> command: sets a scenario timer (global, or attributed to an aircraft so its expiry
/// <c>SAY</c> has a callsign) or cancels one or all. The router's arm and the live server's wrapper both call it; the
/// server adds the broadcasts.
/// </summary>
public static class TimerCommandApplier
{
    public static CommandResult Apply(TimerCommand timer, SimScenarioState scenario, SimulationWorld world, string callsign)
    {
        if (timer.IsCancel)
        {
            return Cancel(timer, scenario);
        }

        // A callsign-scoped timer requires the aircraft to exist so the expiry SAY is attributable; a bare
        // (empty-callsign) timer is a global instructor reminder.
        if (callsign.Length > 0 && world.FindAircraft(callsign) is null)
        {
            return new CommandResult(false, $"Aircraft '{callsign}' not found");
        }

        var seconds = timer.Seconds!.Value;
        var id = scenario.NextTimerId++;
        scenario.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = id,
                Callsign = callsign.Length > 0 ? callsign : null,
                Message = timer.Message,
                FireAtSeconds = scenario.ElapsedSeconds + seconds,
                TotalSeconds = seconds,
            }
        );

        return new CommandResult(true, $"Timer #{id} set for {FormatDuration((int)seconds)}");
    }

    private static CommandResult Cancel(TimerCommand timer, SimScenarioState scenario)
    {
        int removed;
        if (timer.CancelAll)
        {
            removed = scenario.ActiveTimers.Count;
            scenario.ActiveTimers.Clear();
        }
        else
        {
            removed = scenario.ActiveTimers.RemoveAll(t => t.Id == timer.CancelId);
        }

        if (removed == 0)
        {
            return new CommandResult(false, timer.CancelAll ? "No active timers" : $"No timer #{timer.CancelId}");
        }

        return new CommandResult(true, timer.CancelAll ? $"Cancelled {removed} timer(s)" : $"Cancelled timer #{timer.CancelId}");
    }

    private static string FormatDuration(int seconds) => $"{seconds / 60}:{seconds % 60:D2}";
}
