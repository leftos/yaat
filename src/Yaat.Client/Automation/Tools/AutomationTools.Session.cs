using System.ComponentModel;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    /// <summary>
    /// Sets the session toggle the settings window binds, so its change handler sends the room the new mode. That send does not
    /// wait for the room, so the message reports the request; the room's next settings broadcast confirms or overrides it.
    /// </summary>
    [AutomationTool("set_solo", "Turns the room's solo training mode on or off, as the session settings toggle does.", nameof(NoScenario))]
    public Task<AppToolOutcome> SetSolo([Description("True for solo training mode, false to leave it.")] bool enabled)
    {
        _viewModel.SessionSoloTrainingMode = enabled;
        string state = enabled ? "on" : "off";
        return Task.FromResult(
            AppToolOutcome.Done($"Solo training mode requested {state}; the room's next settings broadcast confirms or overrides it.")
        );
    }
}
