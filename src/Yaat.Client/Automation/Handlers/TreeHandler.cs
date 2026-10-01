// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Selectors;
using Yaat.Client.Automation.Tree;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// <c>get_tree</c>: the element tree as a JSON array of <see cref="NodeInfo"/> roots.
/// <list type="bullet">
/// <item>No root given: every root the registry reports (each window and, recursively, the windows it owns), each
/// followed by the overlay popups open in it. A popup is listed only as its own root, never again inside its window.</item>
/// <item><c>nodeId</c> or <c>selector</c>: that one element, as an array of one.</item>
/// </list>
/// Params, all optional: <c>treeKind</c> (<c>"Visual"</c>, the default, or <c>"Logical"</c>, case-insensitive),
/// <c>depth</c> (levels of children below each root, default 10; 0 lists the roots alone), and at most one of
/// <c>nodeId</c> and <c>selector</c>.
/// </summary>
public sealed class TreeHandler(NodeRegistry registry, SelectorRequestHelper selectors, NodeInfoBuilder builder) : IRequestHandler
{
    private const int DefaultDepth = 10;

    private enum TreeKind
    {
        Visual,
        Logical,
    }

    private sealed record TreeParams(TreeKind Kind, int Depth, int? NodeId, string? Selector);

    public string Method => ProtocolMethods.GetTree;

    public async Task<object> Handle(AutomationRequest request)
    {
        object parsed = ParseParams(request.Params);
        if (parsed is not TreeParams parameters)
        {
            return parsed;
        }

        return await Dispatcher.UIThread.InvokeAsync(() => BuildTree(parameters));
    }

    /// <summary>The request's params as a <see cref="TreeParams"/>, or the <see cref="HandlerErrorResult"/> naming the bad one.</summary>
    private static object ParseParams(JsonElement? raw)
    {
        if ((raw is not { } element) || (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return new TreeParams(TreeKind.Visual, DefaultDepth, null, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return HandlerResult.InvalidParam("params", "'params' must be a JSON object.");
        }

        (TreeKind kind, HandlerErrorResult? kindError) = ReadKind(element);
        (int depth, HandlerErrorResult? depthError) = ReadDepth(element);
        (int? nodeId, HandlerErrorResult? nodeIdError) = ReadNodeId(element);
        (string? selector, HandlerErrorResult? selectorError) = ReadSelector(element);
        HandlerErrorResult? error = kindError ?? depthError ?? nodeIdError ?? selectorError;
        if (error is not null)
        {
            return error;
        }

        if ((nodeId is not null) && (selector is not null))
        {
            return HandlerResult.InvalidParam("selector", "Give either 'nodeId' or 'selector' as the root, not both.");
        }

        return new TreeParams(kind, depth, nodeId, selector);
    }

    private static (TreeKind Kind, HandlerErrorResult? Error) ReadKind(JsonElement element)
    {
        if (!element.TryGetProperty("treeKind", out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (TreeKind.Visual, null);
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.Equals(text, nameof(TreeKind.Visual), StringComparison.OrdinalIgnoreCase))
        {
            return (TreeKind.Visual, null);
        }

        if (string.Equals(text, nameof(TreeKind.Logical), StringComparison.OrdinalIgnoreCase))
        {
            return (TreeKind.Logical, null);
        }

        return (TreeKind.Visual, HandlerResult.InvalidParam("treeKind", "'treeKind' must be \"Visual\" or \"Logical\"."));
    }

    private static (int Depth, HandlerErrorResult? Error) ReadDepth(JsonElement element)
    {
        if (!element.TryGetProperty("depth", out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (DefaultDepth, null);
        }

        if ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(out int depth) && (depth >= 0))
        {
            return (depth, null);
        }

        return (DefaultDepth, HandlerResult.InvalidParam("depth", "'depth' must be a whole number of 0 or more."));
    }

    private static (int? NodeId, HandlerErrorResult? Error) ReadNodeId(JsonElement element)
    {
        if (!element.TryGetProperty("nodeId", out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (null, null);
        }

        if ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(out int nodeId))
        {
            return (nodeId, null);
        }

        return (null, HandlerResult.InvalidParam("nodeId", "'nodeId' must be a node id from list_windows or get_tree."));
    }

    // A present but blank selector stays a selector, so the selector helper answers it as MISSING_SELECTOR.
    private static (string? Selector, HandlerErrorResult? Error) ReadSelector(JsonElement element)
    {
        if (!element.TryGetProperty("selector", out JsonElement value) || (value.ValueKind == JsonValueKind.Null))
        {
            return (null, null);
        }

        return value.ValueKind == JsonValueKind.String
            ? (value.GetString(), null)
            : (null, HandlerResult.InvalidParam("selector", "'selector' must be a string."));
    }

    private object BuildTree(TreeParams parameters)
    {
        if (parameters.NodeId is int nodeId)
        {
            (Visual? visual, string? reason) = registry.ResolveChecked(nodeId);
            if (visual is null)
            {
                return (reason is null) ? HandlerResult.StaleNode(nodeId) : HandlerResult.StaleNode(nodeId, reason);
            }

            return new List<NodeInfo> { Describe(visual, parameters.Kind, parameters.Depth) };
        }

        if (parameters.Selector is not null)
        {
            if (!selectors.TryResolveSingle(parameters.Selector, out Visual? match, out HandlerErrorResult? error))
            {
                return error;
            }

            return new List<NodeInfo> { Describe(match, parameters.Kind, parameters.Depth) };
        }

        List<NodeInfo> roots = [];
        foreach (TopLevel root in registry.GetRoots())
        {
            roots.Add(Describe(root, parameters.Kind, parameters.Depth));
            // Avalonia 12 keeps overlay popups in a popup overlay layer with no public accessor, so they are found by type.
            foreach (OverlayPopupHost popup in root.GetVisualDescendants().OfType<OverlayPopupHost>())
            {
                roots.Add(Describe(popup, parameters.Kind, parameters.Depth));
            }
        }

        return roots;
    }

    private NodeInfo Describe(Visual visual, TreeKind kind, int depth)
    {
        List<NodeInfo>? children = null;
        if (depth > 0)
        {
            List<Visual> childVisuals = ChildrenOf(visual, kind);
            if (childVisuals.Count > 0)
            {
                children = [.. childVisuals.Select(child => Describe(child, kind, depth - 1))];
            }
        }

        return builder.Create(visual, children);
    }

    // An open popup's content is listed under its overlay popup root only: the visual walk leaves the popup hosts out,
    // and the logical walk leaves out a Popup's child, which lives in a different popup host than the Popup itself.
    private static OverlayPopupHost? PopupHostOf(Visual visual) => visual.FindAncestorOfType<OverlayPopupHost>(includeSelf: true);

    private static List<Visual> ChildrenOf(Visual visual, TreeKind kind) =>
        kind == TreeKind.Logical
            ? [.. ((ILogical)visual).LogicalChildren.OfType<Visual>().Where(child => PopupHostOf(child) == PopupHostOf(visual))]
            : [.. visual.GetVisualChildren().Where(child => child is not OverlayPopupHost)];
}
