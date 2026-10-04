using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>get_sim_time</c>: the sim clock the client sees, read once on the UI thread — scenario-elapsed seconds, whether the
/// sim is paused, and its rate. Takes no params. While the main window is not up it is <c>UNSUPPORTED_OPERATION</c>, as
/// <c>wait_until</c> answers.
/// </summary>
public sealed class GetSimTimeHandler(Func<IAutomationState?> stateProvider) : IRequestHandler
{
    public string Method => ProtocolMethods.GetSimTime;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        GetSimTimeResult? result = await Dispatcher.UIThread.InvokeAsync(Read).GetTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        return (result is not null) ? result : NoMainWindow();
    }

    private GetSimTimeResult? Read()
    {
        IAutomationState? state = stateProvider();
        return (state is null) ? null : new GetSimTimeResult(state.ScenarioElapsedSeconds, state.IsPaused, state.SimRate);
    }

    private static HandlerErrorResult NoMainWindow() =>
        HandlerResult.Error(
            AutomationErrorCodes.UnsupportedOperation,
            "The main window is not up yet, so there is no simulation state to read.",
            "Wait for the main window to open (wait_for on a selector in it), then call get_sim_time again.",
            null
        );
}
