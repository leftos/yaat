using Xunit;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests.Pathfinding;

public sealed class VisitedNodeSetTests
{
    [Fact]
    public void Single_ContainsOnlyThatNode()
    {
        var set = VisitedNodeSet.Single(7);

        Assert.True(set.Contains(7));
        Assert.False(set.Contains(6));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Add_IsPersistent_OriginalUnchanged()
    {
        var a = VisitedNodeSet.Single(5);
        var b = a.Add(3).Add(9);

        Assert.Equal(1, a.Count);
        Assert.False(a.Contains(3));
        Assert.Equal(3, b.Count);
        Assert.True(b.Contains(3) && b.Contains(5) && b.Contains(9));
    }

    [Fact]
    public void Add_ExistingNode_ReturnsSameCount()
    {
        var set = VisitedNodeSet.Single(4).Add(4);

        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Default_IsEmpty()
    {
        VisitedNodeSet set = default;

        Assert.Equal(0, set.Count);
        Assert.False(set.Contains(0));
        Assert.True(set.Add(0).Contains(0));
    }
}
