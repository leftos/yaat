namespace Yaat.Sim.Simulation;

/// <summary>
/// Consolidation item describing ownership and children for a TCP.
/// </summary>
public record ConsolidationItem(Tcp Tcp, Tcp? Owner, List<Tcp> Children, bool BasicConsolidation);

/// <summary>
/// Pure consolidation algorithm operating on a TCP list. No config/server dependencies.
/// </summary>
public static class ConsolidationEngine
{
    /// <summary>
    /// Builds consolidation items for all TCPs in the provided list.
    /// Each item describes who owns the TCP and what children it consolidates,
    /// based on the hierarchy and which TCPs are currently attended.
    /// Manual overrides from ConsolidationState take priority over automatic hierarchy.
    /// </summary>
    public static List<ConsolidationItem> GetConsolidationItems(
        List<Tcp> allTcps,
        bool autoConsolidate,
        Func<Tcp, bool> isAttended,
        ConsolidationState? manualOverrides = null
    )
    {
        var byId = new Dictionary<string, Tcp>();
        foreach (var tcp in allTcps)
        {
            byId[tcp.Id] = tcp;
        }

        var childrenOf = BuildChildrenIndex(allTcps);

        var results = new List<ConsolidationItem>();

        // Collect IDs of TCPs that have manual overrides — these should
        // be excluded from auto-computed children lists since they've
        // been moved to a different receiver.
        var manuallyOverriddenIds = manualOverrides is not null ? manualOverrides.GetSnapshot().Keys.ToHashSet() : new HashSet<string>();

        foreach (var tcp in allTcps)
        {
            // Check for manual override first
            var manualOv = manualOverrides?.GetOverride(tcp.Id);
            if (manualOv is not null && byId.ContainsKey(manualOv.ReceivingTcpId))
            {
                // Manual override: ResolveOwner follows the override to the receiving
                // TCP, then onward to whoever actually owns it.
                var owner = ResolveOwner(tcp, byId, isAttended, manualOverrides, autoConsolidate);
                results.Add(new ConsolidationItem(tcp, owner, [], manualOv.IsBasic));
                continue;
            }

            Tcp? autoOwner;
            List<Tcp> children;

            if (!autoConsolidate)
            {
                // CRC convention: Owner = null means "I'm the root hub".
                // Owner = some TCP means "I'm consolidated under that TCP".
                // When the TCP is attended, it owns itself → Owner = null.
                // Don't include self in children — CRC adds OurTcp automatically.
                autoOwner = null;
                children = [];
            }
            else
            {
                autoOwner = ResolveOwner(tcp, byId, isAttended, manualOverrides, autoConsolidate);
                // If the resolved owner is the TCP itself, it's the root → null
                if (autoOwner is not null && autoOwner.Id == tcp.Id)
                {
                    autoOwner = null;
                }

                // CRC adds OurTcp to ConsolidatedTcps automatically, so exclude
                // self from children to avoid duplicates.
                var allDescendants = isAttended(tcp) ? CollectConsolidatedDescendants(tcp, childrenOf, isAttended, manuallyOverriddenIds) : [];
                children = allDescendants.Where(c => c.Id != tcp.Id).ToList();
            }

            results.Add(new ConsolidationItem(tcp, autoOwner, children, false));
        }

        // For attended TCPs that are the receiver of manual overrides,
        // add the manually consolidated TCPs + their descendants to
        // the children list.
        if (manualOverrides is not null)
        {
            var overrideSnapshot = manualOverrides.GetSnapshot();
            foreach (var (sendingId, ov) in overrideSnapshot)
            {
                if (!byId.TryGetValue(sendingId, out var sendingTcp))
                {
                    continue;
                }

                // Find the result entry for the receiving TCP
                var receivingResult = results.FirstOrDefault(r => r.Tcp.Id == ov.ReceivingTcpId);
                if (receivingResult is null)
                {
                    continue;
                }

                // Owner == null means the TCP owns itself (attended root).
                // Owner != null means it's consolidated under another TCP.
                // In either case, find the actual owning result to add children to.
                var ownerTcp = receivingResult.Owner ?? receivingResult.Tcp;
                var ownerResult = results.FirstOrDefault(r => r.Tcp.Id == ownerTcp.Id);
                if (ownerResult is null)
                {
                    continue;
                }

                if (!ownerResult.Children.Contains(sendingTcp))
                {
                    ownerResult.Children.Add(sendingTcp);
                }

                // Also add descendants of the sending TCP that aren't
                // independently attended. Exclude TCPs that carry their own
                // manual override (manuallyOverriddenIds) — they follow their
                // own override receiver, not this sender's, and would otherwise
                // be double-counted under both owners.
                var descendants = CollectConsolidatedDescendants(sendingTcp, childrenOf, isAttended, manuallyOverriddenIds);
                foreach (var desc in descendants)
                {
                    if (desc.Id != sendingTcp.Id && !ownerResult.Children.Contains(desc))
                    {
                        ownerResult.Children.Add(desc);
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the attended TCP that currently owns the given TCP via consolidation,
    /// or null if none. Manual overrides take priority over automatic hierarchy.
    /// </summary>
    public static Tcp? GetConsolidationOwner(
        List<Tcp> allTcps,
        bool autoConsolidate,
        Tcp tcp,
        Func<Tcp, bool> isAttended,
        ConsolidationState? manualOverrides = null
    )
    {
        var byId = new Dictionary<string, Tcp>();
        foreach (var t in allTcps)
        {
            byId[t.Id] = t;
        }

        return ResolveOwner(tcp, byId, isAttended, manualOverrides, autoConsolidate);
    }

    /// <summary>
    /// Returns the TCPs whose airspace currently folds into <paramref name="tcp"/> from
    /// below — <paramref name="tcp"/> itself plus every descendant that is neither
    /// independently attended nor carrying its own manual override. This is the same set
    /// <see cref="GetConsolidationItems"/> appends to a receiver's <c>Children</c> when an
    /// override moves <paramref name="tcp"/>, exposed for callers that must act on the
    /// whole block rather than the single TCP (e.g. transferring tracks on a full CONS).
    /// </summary>
    public static List<Tcp> GetConsolidatedDescendants(List<Tcp> allTcps, Tcp tcp, Func<Tcp, bool> isAttended, ConsolidationState? manualOverrides)
    {
        var childrenOf = BuildChildrenIndex(allTcps);
        var overriddenIds = manualOverrides is not null ? manualOverrides.GetSnapshot().Keys.ToHashSet() : [];
        return CollectConsolidatedDescendants(tcp, childrenOf, isAttended, overriddenIds);
    }

    /// <summary>
    /// Returns the TCPs that would consolidate under the given TCP
    /// if it were the only attended position.
    /// </summary>
    public static List<Tcp> GetDefaultConsolidation(List<Tcp> allTcps, Tcp tcp)
    {
        var childrenOf = BuildChildrenIndex(allTcps);

        // Only this TCP is "attended" → collect all descendants
        return CollectConsolidatedDescendants(tcp, childrenOf, _ => false);
    }

    /// <summary>
    /// Indexes TCPs by parent id, so the downward walks can enumerate a TCP's children.
    /// Roots (no <c>ParentTcpId</c>) collect under the empty-string key.
    /// </summary>
    private static Dictionary<string, List<Tcp>> BuildChildrenIndex(List<Tcp> allTcps)
    {
        var childrenOf = new Dictionary<string, List<Tcp>>();
        foreach (var tcp in allTcps)
        {
            var parentKey = tcp.ParentTcpId ?? "";
            if (!childrenOf.ContainsKey(parentKey))
            {
                childrenOf[parentKey] = [];
            }

            childrenOf[parentKey].Add(tcp);
        }

        return childrenOf;
    }

    /// <summary>
    /// Resolves the attended TCP that owns <paramref name="tcp"/>, or null if none.
    /// At each hop the walk checks, in order: a manual override on the current TCP
    /// (hop to its receiver), attendance (that TCP is the owner), then the natural
    /// <c>ParentTcpId</c> link. Consulting overrides at every hop — not just on the
    /// starting TCP — keeps ownership in agreement with the children lists built by
    /// <see cref="GetConsolidationItems"/>, which already pull an overridden non-leaf's
    /// descendants into the receiver. Cycle-guarded with a visited set, which also
    /// bounds chained overrides that loop back on themselves.
    /// </summary>
    private static Tcp? ResolveOwner(
        Tcp tcp,
        Dictionary<string, Tcp> byId,
        Func<Tcp, bool> isAttended,
        ConsolidationState? manualOverrides,
        bool autoConsolidate
    )
    {
        var current = tcp;
        var visited = new HashSet<string>();
        while (current is not null)
        {
            if (!visited.Add(current.Id))
            {
                return null;
            }

            var manualOv = manualOverrides?.GetOverride(current.Id);
            if (manualOv is not null && byId.TryGetValue(manualOv.ReceivingTcpId, out var receivingTcp))
            {
                current = receivingTcp;
                continue;
            }

            if (isAttended(current))
            {
                return current;
            }

            // Without automatic consolidation a TCP never folds into its natural
            // parent — only an explicit override moves ownership.
            if (!autoConsolidate || current.ParentTcpId is null || !byId.TryGetValue(current.ParentTcpId, out var parent))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static List<Tcp> CollectConsolidatedDescendants(
        Tcp root,
        Dictionary<string, List<Tcp>> childrenOf,
        Func<Tcp, bool> isAttended,
        HashSet<string>? excludeIds = null
    )
    {
        var result = new List<Tcp> { root };
        var stack = new Stack<Tcp>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!childrenOf.TryGetValue(current.Id, out var kids))
            {
                continue;
            }

            foreach (var child in kids)
            {
                if (isAttended(child))
                {
                    continue;
                }

                // Skip TCPs that have been manually overridden to a different receiver
                if (excludeIds is not null && excludeIds.Contains(child.Id))
                {
                    continue;
                }

                result.Add(child);
                stack.Push(child);
            }
        }

        return result;
    }
}
