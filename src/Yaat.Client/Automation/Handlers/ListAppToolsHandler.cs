using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tools;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>list_app_tools</c>: every app tool (<see cref="AutomationTools"/>) with its parameters and whether it can run now, read
/// on the UI thread. While the main window is not up every tool is listed unavailable with
/// <see cref="AutomationTools.NoMainWindowReason"/>. Params are ignored.
/// </summary>
/// <param name="toolsProvider">The tools bound to the main window's view model, or null while it is not up; read on the UI thread.</param>
public sealed class ListAppToolsHandler(Func<AutomationTools?> toolsProvider) : IRequestHandler
{
    public string Method => ProtocolMethods.ListAppTools;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync(() => List(toolsProvider())).GetTask().WaitAsync(cancellationToken).ConfigureAwait(false);

    private static List<AppToolInfo> List(AutomationTools? tools) => [.. AutomationTools.Catalog.Select(tool => Describe(tool, tools))];

    private static AppToolInfo Describe(AppTool tool, AutomationTools? tools)
    {
        string? reason = (tools is null) ? AutomationTools.NoMainWindowReason : tool.UnavailableReason(tools);
        List<AppToolParameterInfo> parameters =
        [
            .. tool.Parameters.Select(parameter => new AppToolParameterInfo(parameter.Name, parameter.Type, parameter.Description)),
        ];
        return new AppToolInfo(tool.Name, tool.Description, parameters, reason is null, reason);
    }
}
