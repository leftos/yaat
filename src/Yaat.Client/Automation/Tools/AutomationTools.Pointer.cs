using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Automation.Handlers;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Selectors;
using Yaat.Client.Automation.Tree;
using Yaat.Client.InputSynthesis;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    [AutomationTool(
        "hover",
        "Move the mouse onto a point of a client window and rest there, so hover effects fire as for a real mouse (PointerEntered, "
            + "IsPointerOver, a menu item's route preview, a tooltip); the pointer stays there afterwards. Raw mouse input through the "
            + "window's own input path; the real cursor never moves. Give exactly one of window (with x and y) or selector.",
        nameof(AlwaysAvailable)
    )]
    public async Task<AppToolOutcome> Hover(
        [Description("Selector of the window x and y are in (e.g. \"MainWindow\", \"#SettingsWindow\"); empty when selector is given.")]
            string window,
        [Description(
            "Selector of the element to hover at the centre of (#Name, a type name, A > B, :nth(N)), searched across the client's windows; "
                + "empty to hover at x, y in window."
        )]
            string selector,
        [Description("X in DIPs from the left edge of window's client area; ignored when selector is given.")] double x,
        [Description("Y in DIPs from the top edge of window's client area; ignored when selector is given.")] double y,
        [Description("How long the pointer rests on the point before the tool answers, 0-10000 ms.")] int durationMs
    )
    {
        if ((window.Length > 0) == (selector.Length > 0))
        {
            throw new AppToolArgumentException("selector", "Give exactly one of 'window' (with x and y) and 'selector'; leave the other empty.");
        }

        RequireRange("durationMs", durationMs, 0, HoverHandler.MaxDurationMs);
        PointerTargets pointers = AppPointerTargets();
        object resolved =
            (selector.Length > 0)
                ? pointers.AtCentre(new ElementTarget(null, selector, "selector", "selector"))
                : pointers.InWindow(new ElementTarget(null, window, "window", "window"), new Point(x, y));
        WindowPoint at = Resolved(resolved, (selector.Length > 0) ? "selector" : "window", "x");
        Interactive receiver = PointerTargets.ReceiverAt(at);
        PointerSite site = pointers.Site(at);
        await SyntheticMouse.HoverAsync(at.TopLevel, at.Point, durationMs, CancellationToken.None);
        return AppToolOutcome.Done(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Hovered {receiver.GetType().Name} at ({site.X}, {site.Y}) in '{site.Window}' for {durationMs} ms; the pointer stays there."
            )
        );
    }

    [AutomationTool(
        "drag",
        "Drag with a mouse button held in a client window: press at (fromX, fromY), move to (toX, toY) in steps equal moves 16 ms apart "
            + "with the button held, rest holdMs, release there. Raw mouse input through the window's own input path, so a capture taken "
            + "on the press gets every move and the drag thresholds are passed as by a real mouse; the real cursor never moves.",
        nameof(AlwaysAvailable)
    )]
    public async Task<AppToolOutcome> Drag(
        [Description("Selector of the window both points are in (e.g. \"MainWindow\", \"#SettingsWindow\").")] string window,
        [Description("Press X in DIPs from the left edge of window's client area.")] double fromX,
        [Description("Press Y in DIPs from the top edge of window's client area.")] double fromY,
        [Description("Release X in DIPs from the left edge of window's client area.")] double toX,
        [Description("Release Y in DIPs from the top edge of window's client area.")] double toY,
        [Description("The button held: left, right or middle.")] string button,
        [Description("How many equal moves take the pointer from the press to the release, 1-100.")] int steps,
        [Description("How long the button stays held at the release point before it is released, 0-10000 ms.")] int holdMs
    )
    {
        (MouseButton mouseButton, HandlerErrorResult? buttonError) = InputParams.ParseButton(button);
        if (buttonError is not null)
        {
            throw new AppToolArgumentException("button", buttonError.Error.Message);
        }

        RequireRange("steps", steps, 1, DragHandler.MaxSteps);
        RequireRange("holdMs", holdMs, 0, DragHandler.MaxHoldMs);
        PointerTargets pointers = AppPointerTargets();
        WindowPoint from = Resolved(
            pointers.InWindow(new ElementTarget(null, window, "window", "window"), new Point(fromX, fromY)),
            "window",
            "fromX"
        );
        var to = new Point(toX, toY);
        if (PointerTargets.Inside(from.TopLevel, to) is { } outside)
        {
            throw new AppToolArgumentException("toX", outside.Error.Message);
        }

        Interactive receiver = PointerTargets.ReceiverAt(from);
        await SyntheticMouse.DragAsync(from.TopLevel, new MouseDrag(from.Point, to, mouseButton, steps, holdMs), CancellationToken.None);
        return AppToolOutcome.Done(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Dragged {button.ToLowerInvariant()} from ({fromX}, {fromY}) to ({toX}, {toY}) in {steps} steps, held {holdMs} ms, "
                    + $"pressed on {receiver.GetType().Name}."
            )
        );
    }

    /// <summary>The pointer targets over the client's open windows, with a node registry of their own for the selectors.</summary>
    private static PointerTargets AppPointerTargets()
    {
        var nodes = new NodeRegistry(() => OpenWindows());
        var selectors = new SelectorRequestHelper(new SelectorEngine(nodes), nodes);
        return new PointerTargets(nodes, new TargetResolver(nodes, selectors));
    }

    /// <summary>
    /// The resolved point, or the resolution's error as an argument error: an out-of-bounds point names
    /// <paramref name="pointParam"/>, any other error <paramref name="targetParam"/>.
    /// </summary>
    private static WindowPoint Resolved(object resolved, string targetParam, string pointParam)
    {
        if (resolved is WindowPoint at)
        {
            return at;
        }

        AutomationError error = ((HandlerErrorResult)resolved).Error;
        string param = (error.Code == AutomationErrorCodes.OutOfBounds) ? pointParam : targetParam;
        throw new AppToolArgumentException(param, (error.Suggested is { } hint) ? $"{error.Message} {hint}" : error.Message);
    }

    private static void RequireRange(string name, int value, int min, int max)
    {
        if ((value < min) || (value > max))
        {
            throw new AppToolArgumentException(name, $"'{name}' must be from {min} to {max}, not {value}.");
        }
    }
}
