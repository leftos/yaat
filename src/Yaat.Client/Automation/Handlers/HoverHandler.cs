using System.Text.Json;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;
using Yaat.Client.InputSynthesis;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>hover</c>: moves the pointer onto a point as raw mouse input (<see cref="SyntheticMouse"/>) and rests there, so
/// PointerEntered, IsPointerOver and what hangs off them (a menu item's route preview, a tooltip) happen as for a real mouse.
/// The point is an element's centre, given as <c>nodeId</c> or <c>selector</c>, or <c>x</c> and <c>y</c> DIPs in a window
/// given as <c>windowNodeId</c> or <c>windowSelector</c>, as <c>click_point</c> takes them; exactly one of the two forms.
/// <c>durationMs</c> (0 to <see cref="MaxDurationMs"/>, required) is the rest before the answer; the pointer stays there.
/// </summary>
public sealed class HoverHandler(NodeRegistry registry, PointerTargets pointers) : IRequestHandler
{
    /// <summary>The longest rest a hover takes, well inside the driver's 30 s wait for an answer.</summary>
    public const int MaxDurationMs = 10_000;

    private sealed record HoverParams(ElementTarget Element, ElementTarget Window, Point Point, int DurationMs);

    public string Method => ProtocolMethods.Hover;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        object parsed = ParseParams(request.Params);
        if (parsed is not HoverParams parameters)
        {
            return parsed;
        }

        return await Dispatcher.UIThread.InvokeAsync(() => HoverAsync(parameters, cancellationToken));
    }

    private static object ParseParams(JsonElement? raw)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(raw);
        if (objectError is not null)
        {
            return objectError;
        }

        (ElementTarget target, HandlerErrorResult? targetError) = InputParams.ReadTarget(element, "nodeId", "selector");
        (ElementTarget window, HandlerErrorResult? windowError) = InputParams.ReadTarget(element, "windowNodeId", "windowSelector");
        (int durationMs, HandlerErrorResult? durationError) = InputParams.ReadRequiredInt(element, "durationMs", 0, MaxDurationMs);
        HandlerErrorResult? error = targetError ?? windowError ?? FormError(target, window) ?? durationError;
        if (error is not null)
        {
            return error;
        }

        if (target.IsGiven)
        {
            return new HoverParams(target, window, default, durationMs);
        }

        (double x, HandlerErrorResult? xError) = InputParams.ReadRequiredNumber(element, "x");
        (double y, HandlerErrorResult? yError) = InputParams.ReadRequiredNumber(element, "y");
        return (xError ?? yError) ?? (object)new HoverParams(target, window, new Point(x, y), durationMs);
    }

    /// <summary>The error for giving both forms of the point, or neither; null for exactly one.</summary>
    private static HandlerErrorResult? FormError(ElementTarget target, ElementTarget window)
    {
        if (target.IsGiven == window.IsGiven)
        {
            string both = target.IsGiven ? "not both" : "one of them";
            return HandlerResult.InvalidParam(
                "selector",
                "Give the point as an element ('nodeId' or 'selector') or as a window point ('windowNodeId' or 'windowSelector' with 'x' "
                    + $"and 'y'), {both}."
            );
        }

        return null;
    }

    private async Task<object> HoverAsync(HoverParams parameters, CancellationToken cancellationToken)
    {
        object resolved = parameters.Element.IsGiven ? pointers.AtCentre(parameters.Element) : pointers.InWindow(parameters.Window, parameters.Point);
        if (resolved is not WindowPoint at)
        {
            return resolved;
        }

        if (PointerTargets.Busy(at.TopLevel, ProtocolMethods.Hover) is { } busy)
        {
            return busy;
        }

        Interactive receiver = PointerTargets.ReceiverAt(at);
        var result = new HoverResult(registry.GetOrRegister(receiver), receiver.GetType().Name, pointers.Site(at), parameters.DurationMs);
        await SyntheticMouse.HoverAsync(at.TopLevel, at.Point, parameters.DurationMs, cancellationToken);
        return result;
    }
}
