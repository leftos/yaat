using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;
using Yaat.Client.InputSynthesis;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>drag</c>: a mouse drag as raw input (<see cref="SyntheticMouse"/>) in a window given as <c>windowNodeId</c> or
/// <c>windowSelector</c>: a press at <c>fromX</c>, <c>fromY</c>, <c>steps</c> moves to <c>toX</c>, <c>toY</c> with the button
/// held, a rest of <c>holdMs</c>, and the release there. Both points are DIPs from the window's client-area corner and must be
/// inside it (<c>OUT_OF_BOUNDS</c>). Every param is required: <c>button</c> (left, right or middle), <c>steps</c> (1 to
/// <see cref="MaxSteps"/>) and <c>holdMs</c> (0 to <see cref="MaxHoldMs"/>).
/// </summary>
public sealed class DragHandler(NodeRegistry registry, PointerTargets pointers) : IRequestHandler
{
    /// <summary>The most moves a drag takes: <see cref="SyntheticMouse.StepInterval"/> apart, 100 moves take 1.6 s.</summary>
    public const int MaxSteps = 100;

    /// <summary>The longest rest before the release, well inside the driver's 30 s wait for an answer.</summary>
    public const int MaxHoldMs = 10_000;

    private sealed record DragParams(ElementTarget Window, Point From, Point To, MouseButton Button, int Steps, int HoldMs);

    public string Method => ProtocolMethods.Drag;

    public async Task<object> Handle(AutomationRequest request, CancellationToken cancellationToken)
    {
        object parsed = ParseParams(request.Params);
        if (parsed is not DragParams parameters)
        {
            return parsed;
        }

        return await Dispatcher.UIThread.InvokeAsync(() => DragAsync(parameters, cancellationToken));
    }

    private static object ParseParams(JsonElement? raw)
    {
        (JsonElement element, HandlerErrorResult? objectError) = InputParams.RequireObject(raw);
        if (objectError is not null)
        {
            return objectError;
        }

        (ElementTarget window, HandlerErrorResult? windowError) = InputParams.ReadTarget(element, "windowNodeId", "windowSelector");
        (double fromX, HandlerErrorResult? fromXError) = InputParams.ReadRequiredNumber(element, "fromX");
        (double fromY, HandlerErrorResult? fromYError) = InputParams.ReadRequiredNumber(element, "fromY");
        (double toX, HandlerErrorResult? toXError) = InputParams.ReadRequiredNumber(element, "toX");
        (double toY, HandlerErrorResult? toYError) = InputParams.ReadRequiredNumber(element, "toY");
        (MouseButton button, HandlerErrorResult? buttonError) = InputParams.ReadRequiredButton(element);
        (int steps, HandlerErrorResult? stepsError) = InputParams.ReadRequiredInt(element, "steps", 1, MaxSteps);
        (int holdMs, HandlerErrorResult? holdError) = InputParams.ReadRequiredInt(element, "holdMs", 0, MaxHoldMs);
        HandlerErrorResult? error = windowError ?? fromXError ?? fromYError ?? toXError ?? toYError ?? buttonError ?? stepsError ?? holdError;
        return error ?? (object)new DragParams(window, new Point(fromX, fromY), new Point(toX, toY), button, steps, holdMs);
    }

    private async Task<object> DragAsync(DragParams parameters, CancellationToken cancellationToken)
    {
        object resolved = pointers.InWindow(parameters.Window, parameters.From);
        if (resolved is not WindowPoint from)
        {
            return resolved;
        }

        if (PointerTargets.Inside(from.TopLevel, parameters.To) is { } outside)
        {
            return outside;
        }

        if (PointerTargets.Busy(from.TopLevel, ProtocolMethods.Drag) is { } busy)
        {
            return busy;
        }

        Interactive receiver = PointerTargets.ReceiverAt(from);
        int nodeId = registry.GetOrRegister(receiver);
        var elapsed = Stopwatch.StartNew();
        var drag = new MouseDrag(from.Point, parameters.To, parameters.Button, parameters.Steps, parameters.HoldMs);
        await SyntheticMouse.DragAsync(from.TopLevel, drag, cancellationToken);
        return new DragResult(
            nodeId,
            receiver.GetType().Name,
            pointers.Site(from),
            parameters.To.X,
            parameters.To.Y,
            parameters.Button.ToString().ToLowerInvariant(),
            parameters.Steps,
            parameters.HoldMs,
            (int)elapsed.ElapsedMilliseconds
        );
    }
}
