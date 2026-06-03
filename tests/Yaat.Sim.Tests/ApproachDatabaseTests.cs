using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class ApproachDatabaseTests
{
    private static NavigationDatabase? GetNavDb()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb;
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

    [Theory]
    [InlineData("I7L", "I07L")] // single-letter type + no-leading-zero runway (FAA style)
    [InlineData("ILS7L", "I07L")] // spelled-out type + no-leading-zero runway
    [InlineData("L6R", "L06R")] // LOC, different single-digit runway
    [InlineData("7L", "I07L")] // runway-only, no-leading-zero, highest priority wins
    [InlineData("I07L", "I07L")] // canonical zero-padded form still resolves
    [InlineData("I25R", "I25R")] // two-digit runway unaffected by normalization
    public void ResolveApproachId_NoLeadingZeroRunway_NormalizesAndResolves(string shorthand, string expectedId)
    {
        var db = GetNavDb();
        if (db is null)
        {
            return;
        }

        // KLAX has single-digit runways 06/07 with ILS, LOC and RNAV approaches in the test CIFP.
        var result = db.ResolveApproachId("LAX", shorthand);
        Assert.Equal(expectedId, result);
    }

    [Theory]
    [InlineData("I8R", "I08R", true)] // single-letter type, no leading zero
    [InlineData("ILS8R", "I08R", true)] // spelled-out type, no leading zero
    [InlineData("ILS28R", "I28R", true)] // spelled-out type, two-digit runway
    [InlineData("ILS", "I08R", true)] // type word alone surfaces all its approaches
    [InlineData("LOC6R", "L06R", true)] // spelled LOC → L code
    [InlineData("RNAV6L", "H06LZ", true)] // spelled RNAV → H code
    [InlineData("i8r", "I08R", true)] // case-insensitive
    [InlineData("I8R", "I08L", false)] // different runway side
    [InlineData("28R", "I28R", false)] // runway-only never prefixes a type-coded id
    public void NormalizeApproachShorthand_AsPrefix_MatchesCanonicalId(string typed, string storedId, bool shouldMatch)
    {
        // Mirrors the autocomplete suggester filter: storedId.StartsWith(normalized, OrdinalIgnoreCase).
        string normalized = NavigationDatabase.NormalizeApproachShorthand(typed);
        Assert.Equal(shouldMatch, storedId.StartsWith(normalized, StringComparison.OrdinalIgnoreCase));
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
