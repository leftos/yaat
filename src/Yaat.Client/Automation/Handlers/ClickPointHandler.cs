using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
public sealed class ClickPointHandler(NodeRegistry registry, TargetResolver targets) : IRequestHandler
{
    private sealed record ClickPointParams(ElementTarget Window, Point Point, PointerClick Click);

    public string Method => ProtocolMethods.ClickPoint;

    public async Task<object> Handle(AutomationRequest request)
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
        if (!targets.TryResolve(parameters.Window, out Visual? visual, out HandlerErrorResult? error))
        {
            return error;
        }

        if (visual is not TopLevel topLevel)
        {
            return HandlerResult.InvalidParam(
                parameters.Window.ParamName,
                $"'{parameters.Window.ParamName}' must name a window, not a {visual.GetType().Name}."
            );
        }

        // A window owning an open modal dialog is disabled, and a real click on it would not reach its content.
        if (!topLevel.IsEffectivelyEnabled)
        {
            return HandlerResult.ElementDisabled(registry.GetOrRegister(topLevel), topLevel.GetType().Name);
        }

        Point point = parameters.Point;
        Size size = topLevel.ClientSize;
        if ((point.X < 0) || (point.Y < 0) || (point.X >= size.Width) || (point.Y >= size.Height))
        {
            return HandlerResult.OutOfBounds(point.X, point.Y, size.Width, size.Height);
        }

        Interactive receiver = (topLevel.InputHitTest(point) as Interactive) ?? topLevel;
        SyntheticPointer.Click(receiver, topLevel, point, parameters.Click);
        return new ClickPointResult(registry.GetOrRegister(receiver), receiver.GetType().Name);
    }
}
