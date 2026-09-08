using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <see cref="CompoundPolicy.TrySplitSpecialCompound"/>'s split/no-split verdict. The splitter is the router's gate
/// into per-unit routing (<c>ActionRouter.RouteUnits</c> re-enters <c>Route</c> for each unit), so a "split" that
/// hands back the input unchanged is an infinite recursion — the case a condition-prefixed scoped special
/// (<c>WAIT 1 AN 1 ✓</c>) produces, since the scheme expander turns one comma-less block into two commands.
/// </summary>
public class SpecialCompoundSplitTests
{
    public SpecialCompoundSplitTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// One comma-less block whose expansion is a condition plus a scoped special is one dispatch unit, not a compound:
    /// splitting it on <c>,</c> yields the input back, which the router would route again forever.
    /// </summary>
    [Fact]
    public void ConditionPrefixedScopedSpecial_IsNotASplit()
    {
        Assert.False(CompoundPolicy.TrySplitSpecialCompound("WAIT 1 AN 1 ✓", out var units));
        Assert.Empty(units);
    }

    /// <summary>Two scoped specials in one block still split on <c>,</c> — each dispatches alone.</summary>
    [Fact]
    public void CommaSeparatedScopedSpecials_SplitIntoOneUnitEach()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("AN 1 X, AN 2 Y", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("AN 1 X", units[0].Text);
        Assert.Equal("AN 2 Y", units[1].Text);
    }

    /// <summary>
    /// A genuine <c>;</c> chain around the same condition-prefixed block still splits, and its first unit is the whole
    /// block — routing that unit has to land on the single-command path via the same guard, or the recursion is only
    /// pushed down one level.
    /// </summary>
    [Fact]
    public void ConditionPrefixedScopedSpecial_ChainedWithASecondBlock_SplitsPerBlock()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("WAIT 1 AN 1 ✓; HO 3G", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("WAIT 1 AN 1 ✓", units[0].Text);
        Assert.Equal("HO 3G", units[1].Text);

        // The first unit re-entering the splitter is exactly the no-split case, so the router routes it as one command.
        Assert.False(CompoundPolicy.TrySplitSpecialCompound(units[0].Text, out var nested));
        Assert.Empty(nested);
    }

    /// <summary>The existing contract: two scoped specials in separate <c>;</c> blocks are two units.</summary>
    [Fact]
    public void ScopedSpecialsInSeparateBlocks_AreTwoUnits()
    {
        Assert.True(CompoundPolicy.TrySplitSpecialCompound("HO 3G; ACCEPT", out var units));

        Assert.Equal(2, units.Count);
        Assert.Equal("HO 3G", units[0].Text);
        Assert.Equal("ACCEPT", units[1].Text);
    }
}
