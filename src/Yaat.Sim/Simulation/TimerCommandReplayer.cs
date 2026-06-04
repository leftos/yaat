using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Replay-time mutator for TIMER commands. Live dispatch lives in yaat-server
/// <c>RoomEngine.HandleTimerCmd</c>; this helper keeps rewind/export replay in sync.
/// </summary>
public static class TimerCommandReplayer
{
    /// <summary>
    /// Apply a TIMER command during replay. Returns false when a callsign-scoped timer
    /// targets an aircraft that does not exist (global timers always succeed).
    /// </summary>
    public static bool Apply(TimerCommand timer, SimScenarioState scenario, SimulationWorld world, string callsign)
    {
        if (timer.IsCancel)
        {
            if (timer.CancelAll)
            {
                scenario.ActiveTimers.Clear();
            }
            else
            {
                scenario.ActiveTimers.RemoveAll(t => t.Id == timer.CancelId);
            }

            return true;
        }

        if (callsign.Length > 0 && world.FindAircraft(callsign) is null)
        {
            return false;
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
        return true;
    }
}
