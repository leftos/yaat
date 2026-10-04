using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Yaat.Client.Automation.Selectors;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// Resolves a method's <see cref="ElementTarget"/> to its element, on the UI thread, answering every failure as a coded error.
/// </summary>
public sealed class TargetResolver(NodeRegistry registry, SelectorRequestHelper selectors)
{
    public bool TryResolve(ElementTarget target, [NotNullWhen(true)] out Visual? visual, [NotNullWhen(false)] out HandlerErrorResult? error)
    {
        if (target.NodeId is int nodeId)
        {
            (visual, string? reason) = registry.ResolveChecked(nodeId);
            error =
                (visual is not null) ? null
                : (reason is null) ? HandlerResult.StaleNode(nodeId)
                : HandlerResult.StaleNode(nodeId, reason);
            return visual is not null;
        }

        if (target.Selector is not null)
        {
            return selectors.TryResolveSingle(target.Selector, out visual, out error);
        }

        visual = null;
        error = HandlerResult.InvalidParam(target.SelectorParam, $"Give the target element as '{target.NodeIdParam}' or '{target.SelectorParam}'.");
        return false;
    }
}
