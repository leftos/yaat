using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>focus</c>: gives one element (<c>nodeId</c> or <c>selector</c>) the keyboard focus with <c>Focus()</c>, which does
/// not activate its window. An element that is not focusable, enabled and visible, or that refuses the focus, is
/// <c>NOT_FOCUSABLE</c>.
/// </summary>
public sealed class FocusHandler(NodeRegistry registry, TargetResolver targets) : IRequestHandler
{
    public string Method => ProtocolMethods.Focus;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(request.Params);
        if (objectError is not null)
        {
            return objectError;
        }

        (ElementTarget target, HandlerErrorResult? targetError) = InputParams.ReadTarget(element, "nodeId", "selector");
        if (targetError is not null)
        {
            return targetError;
        }

        return await Dispatcher.UIThread.InvokeAsync(() => Focus(target));
    }

    private object Focus(ElementTarget target)
    {
        if (!targets.TryResolve(target, out Visual? visual, out HandlerErrorResult? error))
        {
            return error;
        }

        int nodeId = registry.GetOrRegister(visual);
        bool focused = visual is InputElement { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true } element && element.Focus();
        return focused ? new FocusResult(nodeId) : HandlerResult.NotFocusable(nodeId, visual.GetType().Name);
    }
}
