using System.ComponentModel;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    [AutomationTool("set_sim_rate", "Sets the room's sim rate, as the SIMRATE command does.", nameof(NotInRoom))]
    public async Task<AppToolOutcome> SetSimRate([Description("The sim rate to set, one the room accepts (e.g. 1, 2, 4, 8).")] int rate)
    {
        AutomationActionOutcome outcome = await _state.SetRateAsync(rate);
        return outcome.Ok
            ? AppToolOutcome.Done($"Sim rate set to {rate}.")
            : AppToolOutcome.Done($"The room refused SIMRATE {rate}: {outcome.Error ?? "no reason given"}");
    }

    [AutomationTool("play", "Resumes the room's sim, as the UNPAUSE command does.", nameof(NotInRoom))]
    public async Task<AppToolOutcome> Play()
    {
        AutomationActionOutcome outcome = await _state.UnpauseAsync();
        return outcome.Ok
            ? AppToolOutcome.Done("Sim resumed.")
            : AppToolOutcome.Done($"The room refused UNPAUSE: {outcome.Error ?? "no reason given"}");
    }

    [AutomationTool("pause", "Pauses the room's sim, as the PAUSE command does.", nameof(NotInRoom))]
    public async Task<AppToolOutcome> Pause()
    {
        AutomationActionOutcome outcome = await _state.PauseAsync();
        return outcome.Ok ? AppToolOutcome.Done("Sim paused.") : AppToolOutcome.Done($"The room refused PAUSE: {outcome.Error ?? "no reason given"}");
    }
}
