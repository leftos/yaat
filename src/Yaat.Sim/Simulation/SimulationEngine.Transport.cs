using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation;

// The session clock: pause, resume and the warp rate, plus the dirty flag the drain turns into the host's one
// sim-state broadcast. The verb bodies live in Simulation/Transport/TransportCommandHandler.cs; the server's own
// RoomEngine.Pause / .Resume run the bodies here too. They are not the only writers of IsPaused: the server still
// pauses a room directly when it goes unattended and at each rewind and playback site, broadcasting for itself.
// Both fields are snapshotted scenario state, so a restore comes back at the rate and the pause the session was left
// at — but the verbs are never recorded: how a session is watched is not what it simulates, and a rewind replaying a
// PAUSE would pause itself.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// True when a transport body has changed the clock since the last drain. Payload-less: the host re-sends the
    /// whole sim state, which is what the wire carries.
    /// </summary>
    internal bool SimStateChanged { get; private set; }

    /// <summary>Marks the clock dirty; the next drain hands the host one <c>OnSimStateChanged</c>.</summary>
    internal void MarkSimStateChanged() => SimStateChanged = true;

    /// <summary>
    /// Takes the flag and clears it, for the paths that move the clock outside both the action router's drain and the
    /// post-physics one: the room's own pause/resume/rate entry points.
    /// </summary>
    internal bool DrainSimStateChanged()
    {
        var changed = SimStateChanged;
        SimStateChanged = false;
        return changed;
    }

    /// <summary>Stops the session clock. The message is the terminal response.</summary>
    public CommandResult Pause()
    {
        if (Scenario is not { } scenario)
        {
            return new CommandResult(false, "No active scenario");
        }

        scenario.IsPaused = true;
        MarkSimStateChanged();
        _logger.LogInformation("Scenario '{Name}' paused", scenario.ScenarioName);
        return new CommandResult(true, "Simulation paused");
    }

    /// <summary>Starts the session clock again.</summary>
    public CommandResult Resume()
    {
        if (Scenario is not { } scenario)
        {
            return new CommandResult(false, "No active scenario");
        }

        scenario.IsPaused = false;
        MarkSimStateChanged();
        _logger.LogInformation("Scenario '{Name}' resumed", scenario.ScenarioName);
        return new CommandResult(true, "Simulation resumed");
    }

    /// <summary>
    /// Sets the warp rate, clamped to 1–16×. Refused above 1× while live traffic is on: real aircraft cannot be
    /// accelerated, and re-timing their samples would freeze them.
    /// </summary>
    public CommandResult SetSimRate(int rate)
    {
        if (Scenario is not { } scenario)
        {
            return new CommandResult(false, "No active scenario");
        }

        var clampedRate = Math.Clamp(rate, 1, 16);
        if ((clampedRate > 1) && scenario.LiveTrafficEnabled)
        {
            return new CommandResult(false, "WARP is unavailable while live traffic is on — real traffic cannot be accelerated");
        }

        scenario.SimRate = clampedRate;
        _logger.LogInformation("Sim rate set to {Rate}x for scenario '{Name}'", clampedRate, scenario.ScenarioName);
        MarkSimStateChanged();
        return new CommandResult(true, null);
    }
}
