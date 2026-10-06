using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tree;
using Yaat.Client.InputSynthesis;

namespace Yaat.Client.Automation.Handlers;

/// <summary>A point in DIPs from the top-left of a top level's client area.</summary>
public sealed record WindowPoint(TopLevel TopLevel, Point Point);

/// <summary>
/// Resolves where pointer input lands, for <c>click_point</c>, <c>hover</c> and <c>drag</c> over the pipe and the
/// <c>hover</c> and <c>drag</c> app tools: a point in a window, or an element's centre in its own top level. Every failure is a
/// coded error. Runs on the UI thread.
/// </summary>
public sealed class PointerTargets(NodeRegistry registry, TargetResolver targets)
{
    /// <summary>
    /// <paramref name="point"/> in the window <paramref name="window"/> names, as a <see cref="WindowPoint"/>; or the error: the
    /// target is not a window (<c>INVALID_PARAM</c>), the window is disabled because it owns an open modal dialog
    /// (<c>ELEMENT_DISABLED</c>), or the point is outside its client area (<c>OUT_OF_BOUNDS</c>).
    /// </summary>
    public object InWindow(ElementTarget window, Point point)
    {
        if (!targets.TryResolve(window, out Visual? visual, out HandlerErrorResult? error))
        {
            return error;
        }

        if (visual is not TopLevel topLevel)
        {
            return HandlerResult.InvalidParam(window.ParamName, $"'{window.ParamName}' must name a window, not a {visual.GetType().Name}.");
        }

        // A window owning an open modal dialog is disabled, and real input on it would not reach its content.
        if (!topLevel.IsEffectivelyEnabled)
        {
            return HandlerResult.ElementDisabled(registry.GetOrRegister(topLevel), topLevel.GetType().Name);
        }

        return Inside(topLevel, point) ?? (object)new WindowPoint(topLevel, point);
    }

    /// <summary>
    /// The centre of the element <paramref name="element"/> names, in its own top level; or the error: it does not resolve, it is
    /// not visible (<c>ELEMENT_DISABLED</c>), it is in no window (<c>STALE_NODE</c>), its window is disabled because it owns an
    /// open modal dialog (<c>ELEMENT_DISABLED</c>), or its centre is outside the window's client area, scrolled or clipped out of
    /// it (<c>OUT_OF_BOUNDS</c>).
    /// </summary>
    public object AtCentre(ElementTarget element)
    {
        if (!targets.TryResolve(element, out Visual? visual, out HandlerErrorResult? error))
        {
            return error;
        }

        int nodeId = registry.GetOrRegister((Visual)visual);
        if (!visual.IsEffectivelyVisible)
        {
            return HandlerResult.ElementDisabled(nodeId, visual.GetType().Name, "It is not visible.");
        }

        if (CentreOf((Visual)visual) is not { } centre)
        {
            return HandlerResult.StaleNode(nodeId, $"Node {nodeId} is not in a window. Call get_tree or list_windows for fresh node ids.");
        }

        TopLevel topLevel = centre.TopLevel;
        if (!topLevel.IsEffectivelyEnabled)
        {
            return HandlerResult.ElementDisabled(registry.GetOrRegister(topLevel), topLevel.GetType().Name);
        }

        return Inside(topLevel, centre.Point) ?? (object)centre;
    }

    /// <summary>
    /// The <c>UNSUPPORTED_OPERATION</c> error for a <paramref name="requested"/> gesture in a top level where a hover or drag is
    /// still under way, else null.
    /// </summary>
    public static HandlerErrorResult? Busy(TopLevel topLevel, string requested)
    {
        if (SyntheticMouse.GestureInProgress(topLevel) is not { } current)
        {
            return null;
        }

        string type = topLevel.GetType().Name;
        return HandlerResult.Error(
            AutomationErrorCodes.UnsupportedOperation,
            $"A {current} is still in progress in this {type}, so the {requested} was not started.",
            $"Send the {requested} once the {current} has answered.",
            new UnsupportedOperationDetails(requested, type)
        );
    }

    /// <summary>The <c>OUT_OF_BOUNDS</c> error for a point outside <paramref name="topLevel"/>'s client area, else null.</summary>
    public static HandlerErrorResult? Inside(TopLevel topLevel, Point point)
    {
        Size size = topLevel.ClientSize;
        bool inside = (point.X >= 0) && (point.Y >= 0) && (point.X < size.Width) && (point.Y < size.Height);
        return inside ? null : HandlerResult.OutOfBounds(point.X, point.Y, size.Width, size.Height);
    }

    /// <summary>The centre of <paramref name="visual"/> in its top level, or null when it is in none.</summary>
    public static WindowPoint? CentreOf(Visual visual)
    {
        if (TopLevel.GetTopLevel(visual) is not { } topLevel)
        {
            return null;
        }

        var centre = new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2);
        return new WindowPoint(topLevel, visual.TranslatePoint(centre, topLevel) ?? centre);
    }

    /// <summary>The element the window hit-tests at the point, which receives the input; the window itself when nothing is hit.</summary>
    public static Interactive ReceiverAt(WindowPoint at) => (at.TopLevel.InputHitTest(at.Point) as Interactive) ?? at.TopLevel;

    /// <summary>Where input at <paramref name="at"/> lands, as a result reports it.</summary>
    public PointerSite Site(WindowPoint at) => Site(registry, at);

    /// <summary>
    /// Where input at <paramref name="at"/> lands, as a result reports it, with the window's node id from <paramref name="nodes"/>.
    /// </summary>
    public static PointerSite Site(NodeRegistry nodes, WindowPoint at)
    {
        TopLevel topLevel = at.TopLevel;
        string name = (topLevel is Window { Title: { Length: > 0 } title }) ? title : topLevel.GetType().Name;
        return new PointerSite(nodes.GetOrRegister(topLevel), name, at.Point.X, at.Point.Y, topLevel.RenderScaling);
    }
}
