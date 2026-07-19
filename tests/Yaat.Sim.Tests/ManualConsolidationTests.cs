using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for manual consolidation overrides: basic/full
/// consolidation, deconsolidation, cleanup on position deactivation,
/// and interaction with automatic hierarchy.
///
/// Uses FAT (Fresno ATCT) hierarchy from ZOA config:
///   1T (root)
///   ├── 1F (parent → 1T)
///   │   └── 1S (parent → 1F)
///   │       └── 1H (parent → 1S)
///   1G (root, separate chain)
///   1X (root, separate chain — used for chained-override cases)
/// </summary>
public class ManualConsolidationTests
{
    private const string IdT = "tcp-1T";
    private const string IdF = "tcp-1F";
    private const string IdS = "tcp-1S";
    private const string IdH = "tcp-1H";
    private const string IdG = "tcp-1G";
    private const string IdX = "tcp-1X";

    private static readonly Tcp Tcp1T = new(1, "T", IdT, null);
    private static readonly Tcp Tcp1F = new(1, "F", IdF, IdT);
    private static readonly Tcp Tcp1S = new(1, "S", IdS, IdF);
    private static readonly Tcp Tcp1H = new(1, "H", IdH, IdS);
    private static readonly Tcp Tcp1G = new(1, "G", IdG, null);
    private static readonly Tcp Tcp1X = new(1, "X", IdX, null);

    private static readonly List<Tcp> AllTcps = [Tcp1T, Tcp1F, Tcp1S, Tcp1H, Tcp1G, Tcp1X];

    private static ConsolidationItem FindItem(List<ConsolidationItem> items, string tcpId)
    {
        return items.First(i => i.Tcp.Id == tcpId);
    }

    private static HashSet<string> ChildIds(ConsolidationItem item)
    {
        return item.Children.Select(c => c.Id).ToHashSet();
    }

    // ── ConsolidationState unit tests ──────────────────────────

    [Fact]
    public void ConsolidateState_Consolidate_StoresOverride()
    {
        var state = new ConsolidationState();
        state.Consolidate(Tcp1T, Tcp1F, basic: true);

        var ov = state.GetOverride(IdF);
        Assert.NotNull(ov);
        Assert.Equal(IdT, ov!.ReceivingTcpId);
        Assert.True(ov.IsBasic);
    }

    [Fact]
    public void ConsolidateState_Deconsolidate_RemovesOverride()
    {
        var state = new ConsolidationState();
        state.Consolidate(Tcp1T, Tcp1F, basic: false);
        state.Deconsolidate(Tcp1F);

        Assert.Null(state.GetOverride(IdF));
    }

    [Fact]
    public void ConsolidateState_Clear_RemovesAll()
    {
        var state = new ConsolidationState();
        state.Consolidate(Tcp1T, Tcp1F, basic: true);
        state.Consolidate(Tcp1T, Tcp1S, basic: false);
        state.Clear();

        Assert.Null(state.GetOverride(IdF));
        Assert.Null(state.GetOverride(IdS));
        Assert.Empty(state.GetSnapshot());
    }

    [Fact]
    public void ConsolidateState_RemoveOverridesInvolving_RemovesSenderAndReceiver()
    {
        var state = new ConsolidationState();
        // 1F sends to 1T, 1S sends to 1F, 1H sends to 1G
        state.Consolidate(Tcp1T, Tcp1F, basic: true);
        state.Consolidate(Tcp1F, Tcp1S, basic: true);
        state.Consolidate(Tcp1G, Tcp1H, basic: false);

        // Remove overrides involving 1F: sender (key=1F) + receiver (1S→1F)
        state.RemoveOverridesInvolving(IdF);

        Assert.Null(state.GetOverride(IdF)); // was sender
        Assert.Null(state.GetOverride(IdS)); // was sending to 1F

        // 1H→1G override is unaffected
        var hOv = state.GetOverride(IdH);
        Assert.NotNull(hOv);
        Assert.Equal(IdG, hOv!.ReceivingTcpId);
    }

    [Fact]
    public void ConsolidateState_GetSnapshot_ReturnsIndependentCopy()
    {
        var state = new ConsolidationState();
        state.Consolidate(Tcp1T, Tcp1F, basic: true);

        var snap = state.GetSnapshot();
        Assert.Single(snap);

        // Mutating snapshot doesn't affect state
        snap.Clear();
        Assert.NotNull(state.GetOverride(IdF));
    }

    // ── The block that moves with a consolidated TCP ───────────

    [Fact]
    public void GetConsolidatedDescendants_IncludesUnattendedSubsectors()
    {
        var state = new ConsolidationState();

        // Only 1T and 1G attended, so 1S and 1H below 1F are unattended.
        var attended = new HashSet<string> { IdT, IdG };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        state.Consolidate(Tcp1G, Tcp1F, basic: false);

        // Combining 1F takes its whole block — the sender plus its subsectors.
        var moved = ConsolidationEngine.GetConsolidatedDescendants(AllTcps, Tcp1F, isAttended, state).Select(t => t.Id).ToHashSet();
        Assert.Contains(IdF, moved);
        Assert.Contains(IdS, moved);
        Assert.Contains(IdH, moved);
        Assert.DoesNotContain(IdT, moved);
    }

    [Fact]
    public void GetConsolidatedDescendants_StopsAtAttendedSubsector()
    {
        var state = new ConsolidationState();

        // 1S is independently attended, so it keeps its own subtree (1S and 1H).
        var attended = new HashSet<string> { IdT, IdG, IdS };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        state.Consolidate(Tcp1G, Tcp1F, basic: false);

        var moved = ConsolidationEngine.GetConsolidatedDescendants(AllTcps, Tcp1F, isAttended, state).Select(t => t.Id).ToHashSet();
        Assert.Contains(IdF, moved);
        Assert.DoesNotContain(IdS, moved);
        Assert.DoesNotContain(IdH, moved);
    }

    [Fact]
    public void GetConsolidatedDescendants_SkipsDescendantWithItsOwnOverride()
    {
        var state = new ConsolidationState();

        var attended = new HashSet<string> { IdT, IdG };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        state.Consolidate(Tcp1G, Tcp1F, basic: false); // 1F → 1G
        state.Consolidate(Tcp1T, Tcp1S, basic: false); // 1S → 1T, separately

        // 1S follows its own override, taking 1H with it — neither moves with 1F.
        var moved = ConsolidationEngine.GetConsolidatedDescendants(AllTcps, Tcp1F, isAttended, state).Select(t => t.Id).ToHashSet();
        Assert.Contains(IdF, moved);
        Assert.DoesNotContain(IdS, moved);
        Assert.DoesNotContain(IdH, moved);
    }

    // ── Circular consolidation is rejected ─────────────────────

    [Fact]
    public void ConsolidateState_DirectCycle_Rejected()
    {
        var state = new ConsolidationState();

        Assert.True(state.Consolidate(Tcp1G, Tcp1F, basic: false)); // 1F → 1G

        // 1G → 1F would close a loop; the store must refuse it.
        Assert.False(state.Consolidate(Tcp1F, Tcp1G, basic: false));
        Assert.Null(state.GetOverride(IdG));

        // The accepted override is untouched.
        Assert.Equal(IdG, state.GetOverride(IdF)!.ReceivingTcpId);
    }

    [Fact]
    public void ConsolidateState_IndirectCycle_Rejected()
    {
        var state = new ConsolidationState();

        Assert.True(state.Consolidate(Tcp1G, Tcp1F, basic: false)); // 1F → 1G
        Assert.True(state.Consolidate(Tcp1X, Tcp1G, basic: false)); // 1G → 1X

        // 1X → 1F closes the 1F → 1G → 1X → 1F loop.
        Assert.False(state.Consolidate(Tcp1F, Tcp1X, basic: false));
        Assert.Null(state.GetOverride(IdX));
    }

    [Fact]
    public void ConsolidateState_SelfConsolidation_Rejected()
    {
        var state = new ConsolidationState();

        Assert.False(state.Consolidate(Tcp1F, Tcp1F, basic: false));
        Assert.Null(state.GetOverride(IdF));
    }

    [Fact]
    public void ConsolidateState_ReplacingOwnOverride_NotTreatedAsCycle()
    {
        var state = new ConsolidationState();

        Assert.True(state.Consolidate(Tcp1G, Tcp1F, basic: false)); // 1F → 1G

        // Re-pointing 1F at 1X must not trip the guard on 1F's own stale entry.
        Assert.True(state.Consolidate(Tcp1X, Tcp1F, basic: false));
        Assert.Equal(IdX, state.GetOverride(IdF)!.ReceivingTcpId);
    }

    // ── Basic consolidation: handoff redirect, tracks stay ─────

    [Fact]
    public void BasicConsolidation_OwnerRedirects_ExistingTracksStay()
    {
        var state = new ConsolidationState();
        var attended = new HashSet<string> { IdT, IdF };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        // Manually consolidate 1F under 1T (basic)
        state.Consolidate(Tcp1T, Tcp1F, basic: true);

        // GetConsolidationOwner for 1F should return 1T
        var owner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1F, isAttended, state);
        Assert.NotNull(owner);
        Assert.Equal(IdT, owner!.Id);

        // Items should show 1F owned by 1T
        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);
        var fItem = FindItem(items, IdF);
        Assert.Equal(IdT, fItem.Owner!.Id);
        Assert.True(fItem.BasicConsolidation);
    }

    // ── Full consolidation: tracks transfer ────────────────────

    [Fact]
    public void FullConsolidation_ItemsShowNonBasic()
    {
        var state = new ConsolidationState();
        var attended = new HashSet<string> { IdT, IdF };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        // Full consolidation: 1F under 1T
        state.Consolidate(Tcp1T, Tcp1F, basic: false);

        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);
        var fItem = FindItem(items, IdF);
        Assert.Equal(IdT, fItem.Owner!.Id);
        Assert.False(fItem.BasicConsolidation);
    }

    // ── Deconsolidate reverts to automatic hierarchy ───────────

    [Fact]
    public void Deconsolidate_RevertsToAutoHierarchy()
    {
        var state = new ConsolidationState();
        var attended = new HashSet<string> { IdT, IdF };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        // Consolidate 1F under 1T, then deconsolidate
        state.Consolidate(Tcp1T, Tcp1F, basic: true);
        state.Deconsolidate(Tcp1F);

        // 1F should now own itself again (attended → null in DTO)
        var owner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1F, isAttended, state);
        Assert.NotNull(owner);
        Assert.Equal(IdF, owner!.Id);

        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);
        var fItem = FindItem(items, IdF);
        Assert.Null(fItem.Owner);
        Assert.False(fItem.BasicConsolidation);
    }

    // ── Manual override on top of automatic consolidation ──────

    [Fact]
    public void ManualOverride_MergesWithAutoHierarchy()
    {
        var state = new ConsolidationState();

        // Only 1T and 1G attended; 1F/1S/1H auto-consolidated under 1T
        var attended = new HashSet<string> { IdT, IdG };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        // Without manual overrides: 1S auto-consolidates to 1T
        var autoOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1S, isAttended);
        Assert.Equal(IdT, autoOwner!.Id);

        // Manual override: move 1S under 1G
        state.Consolidate(Tcp1G, Tcp1S, basic: false);

        // 1S should now be owned by 1G
        var manualOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1S, isAttended, state);
        Assert.NotNull(manualOwner);
        Assert.Equal(IdG, manualOwner!.Id);

        // 1F still auto-consolidated under 1T
        var fOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1F, isAttended, state);
        Assert.Equal(IdT, fOwner!.Id);

        // Items: 1G's children should include 1S + 1H (descendant of 1S); self excluded
        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);
        var gItem = FindItem(items, IdG);
        var gChildren = ChildIds(gItem);
        Assert.DoesNotContain(IdG, gChildren);
        Assert.Contains(IdS, gChildren);
        Assert.Contains(IdH, gChildren);
    }

    [Fact]
    public void ManualOverride_ReceivingTcpUnattended_WalksUpToAttendedAncestor()
    {
        var state = new ConsolidationState();

        // Only 1T attended; manually consolidate 1G under 1F (unattended)
        var attended = new HashSet<string> { IdT };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        state.Consolidate(Tcp1F, Tcp1G, basic: true);

        // 1G's owner should walk up from 1F to 1T
        var owner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1G, isAttended, state);
        Assert.NotNull(owner);
        Assert.Equal(IdT, owner!.Id);
    }

    // ── Position deactivation cleanup ──────────────────────────

    [Fact]
    public void PositionClose_OverridesCleanedUp()
    {
        var state = new ConsolidationState();

        // Multiple overrides involving various TCPs
        state.Consolidate(Tcp1T, Tcp1F, basic: true); // 1F → 1T
        state.Consolidate(Tcp1F, Tcp1S, basic: false); // 1S → 1F
        state.Consolidate(Tcp1G, Tcp1H, basic: true); // 1H → 1G

        // Position holding 1F deactivates → remove overrides involving 1F
        state.RemoveOverridesInvolving(IdF);

        // 1F as sender (key=1F) removed
        Assert.Null(state.GetOverride(IdF));

        // 1S sending to 1F (receiver=1F) removed
        Assert.Null(state.GetOverride(IdS));

        // 1H→1G unaffected
        var snap = state.GetSnapshot();
        Assert.Single(snap);
        Assert.True(snap.ContainsKey(IdH));
    }

    // ── Scenario unload clears state ───────────────────────────

    [Fact]
    public void ScenarioUnload_ClearsConsolidationState()
    {
        var state = new ConsolidationState();
        state.Consolidate(Tcp1T, Tcp1F, basic: true);
        state.Consolidate(Tcp1G, Tcp1H, basic: false);

        // Simulate scenario unload
        state.Clear();

        Assert.Empty(state.GetSnapshot());
    }

    // ── Consolidation with automatic consolidation disabled ────

    [Fact]
    public void ManualOverride_AutoConsolidationFalse_StillApplies()
    {
        var state = new ConsolidationState();
        var attended = new HashSet<string> { IdT, IdF };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        // Without manual overrides: 1S has no owner (auto off, not attended)
        var autoOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, false, Tcp1S, isAttended);
        Assert.Null(autoOwner);

        // Manual override: consolidate 1S under 1F
        state.Consolidate(Tcp1F, Tcp1S, basic: false);

        // Now 1S should be owned by 1F
        var manualOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, false, Tcp1S, isAttended, state);
        Assert.NotNull(manualOwner);
        Assert.Equal(IdF, manualOwner!.Id);
    }

    // ── Consolidation chain: override replaces previous ────────

    [Fact]
    public void ConsolidateReplace_UpdatesReceiver()
    {
        var state = new ConsolidationState();

        // First consolidate 1F under 1T
        state.Consolidate(Tcp1T, Tcp1F, basic: true);
        Assert.Equal(IdT, state.GetOverride(IdF)!.ReceivingTcpId);

        // Then re-consolidate 1F under 1G
        state.Consolidate(Tcp1G, Tcp1F, basic: false);
        var ov = state.GetOverride(IdF);
        Assert.NotNull(ov);
        Assert.Equal(IdG, ov!.ReceivingTcpId);
        Assert.False(ov.IsBasic);
    }

    // ── Children list correctness ──────────────────────────────

    [Fact]
    public void ManualConsolidation_ReceivingTcpChildrenUpdated()
    {
        var state = new ConsolidationState();

        // 1T and 1F both attended
        var attended = new HashSet<string> { IdT, IdF };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        // Without overrides: 1T owns itself; CRC adds OurTcp automatically → Children empty
        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended);
        var tChildren = ChildIds(FindItem(items, IdT));
        Assert.Empty(tChildren);

        // Consolidate 1F under 1T (basic)
        state.Consolidate(Tcp1T, Tcp1F, basic: true);

        // Now 1T should own [1F, 1S, 1H] (self excluded; CRC adds OurTcp automatically)
        var itemsWithOv = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);
        var tChildrenOv = ChildIds(FindItem(itemsWithOv, IdT));
        Assert.DoesNotContain(IdT, tChildrenOv);
        Assert.Contains(IdF, tChildrenOv);
        Assert.Contains(IdS, tChildrenOv);
        Assert.Contains(IdH, tChildrenOv);
        Assert.Equal(3, tChildrenOv.Count);
    }

    [Fact]
    public void ManualOverride_NestedOverriddenDescendant_NotDoubleCounted()
    {
        var state = new ConsolidationState();

        // 1T and 1G attended.
        var attended = new HashSet<string> { IdT, IdG };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        // 1F (child of 1T) is consolidated into 1G.
        state.Consolidate(Tcp1G, Tcp1F, basic: false); // 1F → 1G
        // 1S, a *descendant* of 1F, is SEPARATELY consolidated into 1T.
        state.Consolidate(Tcp1T, Tcp1S, basic: false); // 1S → 1T

        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);

        // 1S's own item owner is 1T — its explicit override wins.
        Assert.Equal(IdT, FindItem(items, IdS).Owner!.Id);

        var gChildren = ChildIds(FindItem(items, IdG));
        var tChildren = ChildIds(FindItem(items, IdT));

        // 1S is consolidated under 1T, so it must appear under 1T...
        Assert.Contains(IdS, tChildren);
        // ...and must NOT ALSO be double-listed under 1G (1F's override receiver).
        Assert.DoesNotContain(IdS, gChildren);

        // 1F itself still follows its own override into 1G.
        Assert.Contains(IdF, gChildren);
    }

    [Fact]
    public void ManualConsolidation_DescendantsOfSendingTcpFollowOverride()
    {
        var state = new ConsolidationState();

        // Only 1T and 1G attended; consolidate 1F under 1G
        var attended = new HashSet<string> { IdT, IdG };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        state.Consolidate(Tcp1G, Tcp1F, basic: false);

        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);

        // 1G should have 1F + 1S + 1H as children (descendants of 1F); self excluded
        var gChildren = ChildIds(FindItem(items, IdG));
        Assert.DoesNotContain(IdG, gChildren);
        Assert.Contains(IdF, gChildren);
        Assert.Contains(IdS, gChildren);
        Assert.Contains(IdH, gChildren);

        // 1T should only have itself excluded; 1F and descendants moved away → empty
        var tChildren = ChildIds(FindItem(items, IdT));
        Assert.DoesNotContain(IdT, tChildren);
        Assert.DoesNotContain(IdF, tChildren);
        Assert.DoesNotContain(IdS, tChildren);
        Assert.DoesNotContain(IdH, tChildren);
    }

    // ── Ownership agrees with the children list ────────────────

    [Fact]
    public void ManualOverride_DescendantOfOverriddenNonLeaf_OwnerFollowsIntoReceiver()
    {
        var state = new ConsolidationState();

        // Only 1T and 1G attended; consolidate the *non-leaf* 1F (which has
        // unattended descendants 1S and 1H) under 1G.
        var attended = new HashSet<string> { IdT, IdG };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        state.Consolidate(Tcp1G, Tcp1F, basic: false); // 1F → 1G (non-leaf sender)

        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);

        // GetConsolidationItems already lists 1S and 1H under 1G's children
        // (asserted by ManualConsolidation_DescendantsOfSendingTcpFollowOverride).
        var gChildren = ChildIds(FindItem(items, IdG));
        Assert.Contains(IdS, gChildren);
        Assert.Contains(IdH, gChildren);

        // Ownership resolution must AGREE with that: a descendant of the overridden
        // non-leaf resolves to the override receiver (1G), not its natural attended
        // ancestor (1T). Both the per-item Owner and the single-TCP resolver are
        // checked because handoff redirect / auto-accept use the latter.
        Assert.Equal(IdG, FindItem(items, IdS).Owner!.Id);
        Assert.Equal(IdG, FindItem(items, IdH).Owner!.Id);

        var sOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1S, isAttended, state);
        Assert.Equal(IdG, sOwner!.Id);
        var hOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1H, isAttended, state);
        Assert.Equal(IdG, hOwner!.Id);
    }

    [Fact]
    public void ManualOverride_ChainedOverrides_ResolveThroughToFinalReceiver()
    {
        var state = new ConsolidationState();

        // Only 1X attended. 1F folds into 1G, and 1G in turn folds into 1X.
        var attended = new HashSet<string> { IdX };
        Func<Tcp, bool> isAttended = tcp => attended.Contains(tcp.Id);

        state.Consolidate(Tcp1G, Tcp1F, basic: false); // 1F → 1G
        state.Consolidate(Tcp1X, Tcp1G, basic: false); // 1G → 1X

        // The chain resolves through the unattended intermediate receiver.
        var fOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1F, isAttended, state);
        Assert.Equal(IdX, fOwner!.Id);

        // Descendants of 1F follow the whole chain too.
        var sOwner = ConsolidationEngine.GetConsolidationOwner(AllTcps, true, Tcp1S, isAttended, state);
        Assert.Equal(IdX, sOwner!.Id);

        var items = ConsolidationEngine.GetConsolidationItems(AllTcps, true, isAttended, state);
        Assert.Equal(IdX, FindItem(items, IdF).Owner!.Id);
        Assert.Equal(IdX, FindItem(items, IdS).Owner!.Id);
        Assert.Equal(IdX, FindItem(items, IdH).Owner!.Id);

        // ...and the children list lands on the same position; self excluded.
        var xChildren = ChildIds(FindItem(items, IdX));
        Assert.DoesNotContain(IdX, xChildren);
        Assert.Contains(IdG, xChildren);
        Assert.Contains(IdF, xChildren);
        Assert.Contains(IdS, xChildren);
        Assert.Contains(IdH, xChildren);
    }
}
