using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class RouteExpanderTests
{
    public RouteExpanderTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // ── SID expansion ──

    [Fact]
    public void Sid_ExpandsBodyFixes()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("LEJAY", "CNDEL", "PORTE"),
            sidBodies: new Dictionary<string, IReadOnlyList<string>> { ["CNDEL5"] = ["LEJAY", "CNDEL", "PORTE"] }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        List<string> result = RouteExpander.Expand("CNDEL5");

        Assert.Equal(["LEJAY", "CNDEL", "PORTE"], result);
    }

    [Fact]
    public void Sid_MatchesTransitionToNextToken()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("LEJAY", "CNDEL", "PORTE", "FFOIL", "GROVE"),
            sidBodies: new Dictionary<string, IReadOnlyList<string>> { ["CNDEL5"] = ["LEJAY", "CNDEL"] },
            sidTransitions: new Dictionary<string, IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>>
            {
                ["CNDEL5"] =
                [
                    (Name: "PORTE", Fixes: (IReadOnlyList<string>)["CNDEL", "PORTE"]),
                    (Name: "GROVE", Fixes: (IReadOnlyList<string>)["CNDEL", "GROVE"]),
                ],
            }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        List<string> result = RouteExpander.Expand("CNDEL5 PORTE FFOIL");

        // Should pick the PORTE transition, not GROVE
        Assert.Equal(["LEJAY", "CNDEL", "PORTE", "FFOIL"], result);
    }

    [Fact]
    public void Sid_EmitsAllTransitions_WhenNoNextTokenMatch()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("LEJAY", "CNDEL", "PORTE", "GROVE"),
            sidBodies: new Dictionary<string, IReadOnlyList<string>> { ["CNDEL5"] = ["LEJAY", "CNDEL"] },
            sidTransitions: new Dictionary<string, IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>>
            {
                ["CNDEL5"] =
                [
                    (Name: "PORTE", Fixes: (IReadOnlyList<string>)["CNDEL", "PORTE"]),
                    (Name: "GROVE", Fixes: (IReadOnlyList<string>)["CNDEL", "GROVE"]),
                ],
            }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        // No next token at all — should emit all transition fixes
        List<string> result = RouteExpander.Expand("CNDEL5");

        Assert.Contains("LEJAY", result);
        Assert.Contains("CNDEL", result);
        Assert.Contains("PORTE", result);
        Assert.Contains("GROVE", result);
    }

    [Fact]
    public void Sid_VersionResolution_StripsDigits()
    {
        // "CNDEL5" in route but "CNDEL6" in navDb
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("LEJAY", "CNDEL"),
            sidBodies: new Dictionary<string, IReadOnlyList<string>> { ["CNDEL6"] = ["LEJAY", "CNDEL"] }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        List<string> result = RouteExpander.Expand("CNDEL5");

        Assert.Equal(["LEJAY", "CNDEL"], result);
    }

    // ── STAR expansion ──

    [Fact]
    public void Star_ExpandsBodyFixes()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("BDEGA", "CORKK", "BRIXX"),
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CORKK", "BRIXX"] }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        List<string> result = RouteExpander.Expand("BDEGA4");

        Assert.Equal(["BDEGA", "CORKK", "BRIXX"], result);
    }

    [Fact]
    public void Star_JoinPoint_SkipsAlreadyEmittedFixes()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("SUNOL", "BDEGA", "CORKK", "BRIXX"),
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CORKK", "BRIXX"] }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        // BDEGA is already in the route before the STAR — should start from CORKK
        List<string> result = RouteExpander.Expand("SUNOL BDEGA BDEGA4");

        Assert.Equal(["SUNOL", "BDEGA", "CORKK", "BRIXX"], result);
    }

    [Fact]
    public void Star_JoinPoint_ViaTransition()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("FAITH", "AMNTS", "CORKK", "BRIXX"),
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["CORKK", "BRIXX"] },
            starTransitions: new Dictionary<string, IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>>
            {
                ["BDEGA4"] = [(Name: "FAITH", Fixes: (IReadOnlyList<string>)["FAITH", "AMNTS"])],
            }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        // FAITH is in a transition — should emit remaining transition fixes (AMNTS) then body
        List<string> result = RouteExpander.Expand("FAITH BDEGA4");

        Assert.Equal(["FAITH", "AMNTS", "CORKK", "BRIXX"], result);
    }

    // ── Airway expansion ──

    [Fact]
    public void Airway_BareToken_ExpandsSegment()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("FIX_A", "FIX_B", "FIX_C", "FIX_D"),
            airways: new Dictionary<string, IReadOnlyList<string>> { ["V108"] = ["FIX_A", "FIX_B", "FIX_C", "FIX_D"] }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        List<string> result = RouteExpander.Expand("FIX_A V108 FIX_C");

        Assert.Equal(["FIX_A", "FIX_B", "FIX_C"], result);
    }

    [Fact]
    public void Airway_DotNotation_ExpandsSegment()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("PORTE", "MID", "CNDEL"),
            airways: new Dictionary<string, IReadOnlyList<string>> { ["V25"] = ["PORTE", "MID", "CNDEL"] }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        List<string> result = RouteExpander.Expand("PORTE.V25 CNDEL");

        Assert.Equal(["PORTE", "MID", "CNDEL"], result);
    }

    [Fact]
    public void Airway_NoFromFix_SkipsExpansion()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("FIX_B"),
            airways: new Dictionary<string, IReadOnlyList<string>> { ["V108"] = ["FIX_A", "FIX_B"] }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        List<string> result = RouteExpander.Expand("V108 FIX_B");

        // V108 is first token — no from fix, so airway is skipped; FIX_B still emitted
        Assert.Contains("FIX_B", result);
    }

    // ── Plain fixes emitted as-is (no digit stripping — SID/STAR resolution handles versions) ──

    [Fact]
    public void Fix_WithTrailingDigits_EmittedExactly()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting(fixes: Fixes("C83")));

        List<string> result = RouteExpander.Expand("C83");

        Assert.Equal(["C83"], result);
    }

    [Fact]
    public void Fix_UnknownProcedureName_EmittedAsIs()
    {
        // "BDEGA4" is not a SID/STAR in navDb — emitted verbatim, no digit stripping
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting(fixes: Fixes("BDEGA")));

        List<string> result = RouteExpander.Expand("BDEGA4");

        Assert.Equal(["BDEGA4"], result);
    }

    [Fact]
    public void Fix_Q136_EmittedExactly()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting(fixes: Fixes("Q136")));

        List<string> result = RouteExpander.Expand("Q136");

        Assert.Equal(["Q136"], result);
    }

    // ── Deduplication ──

    [Fact]
    public void AdjacentDuplicates_Deduplicated()
    {
        var navDb = NavigationDatabase.ForTesting(
            fixes: Fixes("SUNOL", "BRIXX"),
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["SUNOL", "BRIXX"] }
        );
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        // SUNOL appears before the STAR, and STAR body starts with SUNOL
        // After join-point logic, SUNOL should not be duplicated
        List<string> result = RouteExpander.Expand("SUNOL BDEGA4");

        Assert.Equal(["SUNOL", "BRIXX"], result);
    }

    // ── Numeric skip ──

    [Fact]
    public void NumericTokens_Skipped()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting(fixes: Fixes("SUNOL")));

        List<string> result = RouteExpander.Expand("SUNOL 050");

        Assert.Equal(["SUNOL"], result);
    }

    // ── Empty/null ──

    [Fact]
    public void EmptyRoute_ReturnsEmpty()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());

        Assert.Empty(RouteExpander.Expand(""));
        Assert.Empty(RouteExpander.Expand("  "));
    }

    // ── Unknown fix emitted as-is ──

    [Fact]
    public void UnknownFix_EmittedAsIs()
    {
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());

        List<string> result = RouteExpander.Expand("XYZZY");

        Assert.Equal(["XYZZY"], result);
    }

    // ── Helper ──

    private static Dictionary<string, (double Lat, double Lon)> Fixes(params string[] names)
    {
        var dict = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase);
        double lat = 37.0;
        foreach (string name in names)
        {
            dict[name] = (lat, -122.0);
            lat += 0.1;
        }

        return dict;
    }
}
