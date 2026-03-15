using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class RouteChainerTests
{
    [Fact]
    public void EmptyResolvedList_IsNoOp()
    {
        var resolved = new List<ResolvedFix>();
        var fixes = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        NavigationDatabase.SetInstance(fixes);

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL MODESTO");

        Assert.Empty(resolved);
    }

    [Fact]
    public void LastFixMatchesMidRoute_AppendSubsequentFixes()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0), ("OXNARD", 34.2, -119.2));
        NavigationDatabase.SetInstance(fixes);

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL MODESTO OXNARD");

        Assert.Equal(3, resolved.Count);
        Assert.Equal("SUNOL", resolved[0].Name);
        Assert.Equal("MODESTO", resolved[1].Name);
        Assert.Equal("OXNARD", resolved[2].Name);
    }

    [Fact]
    public void LastFixMatchesEndOfRoute_NothingAppended()
    {
        var resolved = new List<ResolvedFix> { new("OXNARD", 34.2, -119.2) };
        var fixes = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0), ("OXNARD", 34.2, -119.2));
        NavigationDatabase.SetInstance(fixes);

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL MODESTO OXNARD");

        Assert.Single(resolved);
        Assert.Equal("OXNARD", resolved[0].Name);
    }

    [Fact]
    public void LastFixNotInRoute_NothingAppended()
    {
        var resolved = new List<ResolvedFix> { new("BRIXX", 37.7, -121.9) };
        var fixes = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0));
        NavigationDatabase.SetInstance(fixes);

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL MODESTO");

        Assert.Single(resolved);
        Assert.Equal("BRIXX", resolved[0].Name);
    }

    [Fact]
    public void RouteTokenWithAltitudeConstraint_StripsConstraintAndMatches()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0));
        NavigationDatabase.SetInstance(fixes);

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL.A50 MODESTO");

        Assert.Equal(2, resolved.Count);
        Assert.Equal("SUNOL", resolved[0].Name);
        Assert.Equal("MODESTO", resolved[1].Name);
    }

    [Fact]
    public void UnknownFixInRemainder_Skipped()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("OXNARD", 34.2, -119.2));
        NavigationDatabase.SetInstance(fixes);

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL UNKNOWN OXNARD");

        Assert.Equal(2, resolved.Count);
        Assert.Equal("SUNOL", resolved[0].Name);
        Assert.Equal("OXNARD", resolved[1].Name);
    }

    [Fact]
    public void EmptyRouteString_NothingAppended()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        NavigationDatabase.SetInstance(fixes);

        RouteChainer.AppendRouteRemainder(resolved, "");

        Assert.Single(resolved);
    }

    [Fact]
    public void CaseInsensitiveMatch_MixedCaseRouteToken_StillMatches()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0));
        NavigationDatabase.SetInstance(fixes);

        RouteChainer.AppendRouteRemainder(resolved, "Sunol MODESTO");

        Assert.Equal(2, resolved.Count);
        Assert.Equal("SUNOL", resolved[0].Name);
        Assert.Equal("MODESTO", resolved[1].Name);
    }
}
