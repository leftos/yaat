using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class ApproachDatabaseTests
{
    private static NavigationDatabase? GetNavDb()
    {
        var db = TestVnasData.NavigationDb;
        if (db is not null)
        {
            NavigationDatabase.SetInstance(db);
        }

        return db;
    }

    [Fact]
    public void GetApproaches_ReturnsNonEmpty()
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        var approaches = db.GetApproaches("OAK");
        Assert.NotEmpty(approaches);
    }

    [Fact]
    public void GetApproach_ExactId_ReturnsCorrectProcedure()
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        var proc = db.GetApproach("OAK", "I28R");

        Assert.NotNull(proc);
        Assert.Equal("I28R", proc.ApproachId);
        Assert.Equal("ILS", proc.ApproachTypeName);
        Assert.Equal("28R", proc.Runway);
    }

    [Fact]
    public void GetApproach_KPrefixNormalized()
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        // Both "KOAK" and "OAK" should resolve the same approach
        Assert.NotNull(db.GetApproach("KOAK", "I28R"));
        Assert.NotNull(db.GetApproach("OAK", "I28R"));
    }

    [Theory]
    [InlineData("ILS28R", "I28R")]
    [InlineData("I28R", "I28R")]
    [InlineData("LOC28R", "L28R")]
    [InlineData("RNAV28L", "H28LZ")]
    public void ResolveApproachId_VariousShorthands_ResolvesCorrectly(string shorthand, string expectedId)
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        var result = db.ResolveApproachId("OAK", shorthand);
        Assert.Equal(expectedId, result);
    }

    [Fact]
    public void ResolveApproachId_RunwayOnly_ReturnsHighestPriority()
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        // "28R" has both ILS (I28R) and LOC (L28R) — ILS should be preferred
        var result = db.ResolveApproachId("OAK", "28R");
        Assert.Equal("I28R", result);
    }

    [Fact]
    public void ResolveApproachId_UnknownShorthand_ReturnsNull()
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        Assert.Null(db.ResolveApproachId("OAK", "ILS99"));
        Assert.Null(db.ResolveApproachId("OAK", ""));
        Assert.Null(db.ResolveApproachId("OAK", "NONSENSE"));
    }

    [Fact]
    public void GetApproaches_CachesPerAirport()
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        // First call loads from file
        var result1 = db.GetApproaches("OAK");
        // Second call should return same cached list
        var result2 = db.GetApproaches("OAK");

        Assert.Same(result1, result2);
    }

    [Fact]
    public void GetApproaches_UnknownAirport_ReturnsEmpty()
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        var result = db.GetApproaches("ZZZ");
        Assert.Empty(result);
    }
}
