using System.Text.Json;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>click_point</c>: a synthetic mouse click at a point in a window, for surfaces whose targets are drawn rather than
/// elements (the radar and ground views). The window is <c>windowNodeId</c> or <c>windowSelector</c>; <c>x</c> and
/// <c>y</c> are DIPs from its client area's top-left corner. The press and release go to the element the window hit-tests
/// at the point (the window itself when nothing is hit). A point outside the client area is <c>OUT_OF_BOUNDS</c>; a disabled
/// window (one that owns an open modal dialog) is <c>ELEMENT_DISABLED</c>.
/// Params as <c>click</c>: <c>button</c>, <c>modifiers</c>, <c>clickCount</c>.
/// </summary>
public sealed class ClickPointHandler(NodeRegistry registry, PointerTargets pointers) : IRequestHandler
{
    private sealed record ClickPointParams(ElementTarget Window, Point Point, PointerClick Click);

    public string Method => ProtocolMethods.ClickPoint;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        object parsed = ParseParams(request.Params);
        if (parsed is not ClickPointParams parameters)
        {
            return parsed;
        }

        return await Dispatcher.UIThread.InvokeAsync(() => ClickAt(parameters));
    }

    private static object ParseParams(JsonElement? raw)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(raw);
        if (objectError is not null)
        {
            return objectError;
        }

        (ElementTarget window, HandlerErrorResult? windowError) = InputParams.ReadTarget(element, "windowNodeId", "windowSelector");
        (double x, HandlerErrorResult? xError) = InputParams.ReadRequiredNumber(element, "x");
        (double y, HandlerErrorResult? yError) = InputParams.ReadRequiredNumber(element, "y");
        (PointerClick click, HandlerErrorResult? clickError) = InputParams.ReadPointerClick(element);
        HandlerErrorResult? error = windowError ?? xError ?? yError ?? clickError;
        return (error is null) ? new ClickPointParams(window, new Point(x, y), click) : error;
    }

    private object ClickAt(ClickPointParams parameters)
    {
        object resolved = pointers.InWindow(parameters.Window, parameters.Point);
        if (resolved is not WindowPoint at)
        {
            return resolved;
        }

        Interactive receiver = PointerTargets.ReceiverAt(at);
        SyntheticPointer.Click(receiver, at.TopLevel, at.Point, parameters.Click);
        return new ClickPointResult(registry.GetOrRegister(receiver), receiver.GetType().Name, pointers.Site(at));
    }
}
