// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Yaat.Client.Automation.Tree;

/// <summary>
/// Gives every element the automation host reports a node id that stays the same across calls for as long as the
/// element lives, and resolves ids back to elements. One registry per host; every member runs on the UI thread.
/// </summary>
/// <param name="rootsProvider">The host's top-level windows.</param>
public sealed class NodeRegistry(Func<IEnumerable<TopLevel>> rootsProvider)
{
    private readonly Dictionary<int, WeakReference<Visual>> _nodes = [];
    private readonly ConditionalWeakTable<Visual, StrongBox<int>> _reverseMap = [];
    private int _nextId;
    private int _pruneThreshold = 64;

    public Visual? Resolve(int nodeId) => ResolveChecked(nodeId).Visual;

    /// <summary>Resolves a node and checks it is still attached to the visual tree; the error says why a stale node is stale.</summary>
    public (Visual? Visual, string? Error) ResolveChecked(int nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out WeakReference<Visual>? weakRef))
        {
            return (null, $"Node {nodeId} not found (may have been garbage collected)");
        }

        if (!weakRef.TryGetTarget(out Visual? visual))
        {
            _nodes.Remove(nodeId);
            return (null, $"Node {nodeId} not found (may have been garbage collected)");
        }

        if ((visual.GetVisualParent() is null) && (visual is not TopLevel))
        {
            return (null, $"Node {nodeId} is stale (detached from the visual tree). Call get_tree or list_windows for fresh node ids.");
        }

        return (visual, null);
    }

    public int GetOrRegister(Visual visual)
    {
        if (_reverseMap.TryGetValue(visual, out StrongBox<int>? box))
        {
            return box.Value;
        }

        return Register(visual);
    }

    private int Register(Visual visual)
    {
        PruneDeadNodesWhenGrown();
        int id = ++_nextId;
        _nodes[id] = new WeakReference<Visual>(visual);
        _reverseMap.AddOrUpdate(visual, new StrongBox<int>(id));
        return id;
    }

    // Drops the entries of collected elements whenever the table has doubled since the last sweep, so a long session
    // keeps it bounded at an amortised constant cost per registration.
    private void PruneDeadNodesWhenGrown()
    {
        if (_nodes.Count < _pruneThreshold)
        {
            return;
        }

        foreach (KeyValuePair<int, WeakReference<Visual>> entry in _nodes)
        {
            if (!entry.Value.TryGetTarget(out _))
            {
                _nodes.Remove(entry.Key);
            }
        }

        _pruneThreshold = Math.Max(64, _nodes.Count * 2);
    }

    /// <summary>Every root to report: the provider's top levels and, recursively, the windows they own.</summary>
    public IReadOnlyList<TopLevel> GetRoots()
    {
        HashSet<TopLevel> seen = new(ReferenceEqualityComparer.Instance);
        List<TopLevel> roots = [];
        foreach (TopLevel root in rootsProvider())
        {
            AddWithOwnedWindows(root, seen, roots);
        }

        return roots;
    }

    private static void AddWithOwnedWindows(TopLevel root, HashSet<TopLevel> seen, List<TopLevel> roots)
    {
        if (!seen.Add(root))
        {
            return;
        }

        roots.Add(root);
        if (root is not Window window)
        {
            return;
        }

        foreach (Window owned in window.OwnedWindows)
        {
            AddWithOwnedWindows(owned, seen, roots);
        }
    }
}
