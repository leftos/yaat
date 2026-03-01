using Xunit;

namespace Yaat.Sim.Tests;

public class TrackOwnerTests
{
    [Fact]
    public void CreateStars_SetsOwnerType()
    {
        var owner = TrackOwner.CreateStars("DAL123", "ZOA", 3, "R07");

        Assert.Equal(TrackOwnerType.Stars, owner.OwnerType);
        Assert.Equal("DAL123", owner.Callsign);
        Assert.Equal("ZOA", owner.FacilityId);
        Assert.Equal(3, owner.Subset);
        Assert.Equal("R07", owner.SectorId);
    }

    [Fact]
    public void CreateNonNas_SetsOwnerType()
    {
        var owner = TrackOwner.CreateNonNas("N12345");

        Assert.Equal(TrackOwnerType.Other, owner.OwnerType);
        Assert.Equal("N12345", owner.Callsign);
        Assert.Null(owner.FacilityId);
        Assert.Null(owner.Subset);
        Assert.Null(owner.SectorId);
    }

    [Fact]
    public void IsNasPosition_TrueForStars()
    {
        var owner = TrackOwner.CreateStars("DAL123", "ZOA", 3, "R07");

        Assert.True(owner.IsNasPosition);
    }

    [Fact]
    public void IsNasPosition_TrueForEram()
    {
        var owner = new TrackOwner("AAL456", "ZNY", 1, "12", TrackOwnerType.Eram);

        Assert.True(owner.IsNasPosition);
    }

    [Fact]
    public void IsNasPosition_FalseForOther()
    {
        var owner = TrackOwner.CreateNonNas("N12345");

        Assert.False(owner.IsNasPosition);
    }
}

public class TcpTests
{
    [Fact]
    public void Equality_ById()
    {
        var a = new Tcp(2, "B", "tcp-001", null);
        var b = new Tcp(9, "Z", "tcp-001", "parent-x");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Inequality_DifferentId()
    {
        var a = new Tcp(2, "B", "tcp-001", null);
        var b = new Tcp(2, "B", "tcp-002", null);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ToString_ReturnsSectorCode()
    {
        var tcp = new Tcp(2, "B", "xxx", null);

        Assert.Equal("2B", tcp.ToString());
    }
}

public class StarsPointoutTests
{
    private static Tcp MakeTcp(string id) => new(1, "A", id, null);

    [Fact]
    public void DefaultStatus_IsPending()
    {
        var pointout = new StarsPointout(MakeTcp("recipient"), MakeTcp("sender"));

        Assert.Equal(StarsPointoutStatus.Pending, pointout.Status);
        Assert.True(pointout.IsPending);
        Assert.False(pointout.IsAccepted);
        Assert.False(pointout.IsRejected);
    }

    [Fact]
    public void Accepted_StatusTransition()
    {
        var pointout = new StarsPointout(MakeTcp("recipient"), MakeTcp("sender"));

        pointout.Status = StarsPointoutStatus.Accepted;

        Assert.True(pointout.IsAccepted);
        Assert.False(pointout.IsPending);
        Assert.False(pointout.IsRejected);
    }

    [Fact]
    public void Rejected_StatusTransition()
    {
        var pointout = new StarsPointout(MakeTcp("recipient"), MakeTcp("sender"));

        pointout.Status = StarsPointoutStatus.Rejected;

        Assert.True(pointout.IsRejected);
        Assert.False(pointout.IsPending);
        Assert.False(pointout.IsAccepted);
    }
}
