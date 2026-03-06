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
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8));

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL MODESTO", fixes);

        Assert.Empty(resolved);
    }

    [Fact]
    public void LastFixMatchesMidRoute_AppendSubsequentFixes()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0), ("OXNARD", 34.2, -119.2));

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL MODESTO OXNARD", fixes);

        Assert.Equal(3, resolved.Count);
        Assert.Equal("SUNOL", resolved[0].Name);
        Assert.Equal("MODESTO", resolved[1].Name);
        Assert.Equal("OXNARD", resolved[2].Name);
    }

    [Fact]
    public void LastFixMatchesEndOfRoute_NothingAppended()
    {
        var resolved = new List<ResolvedFix> { new("OXNARD", 34.2, -119.2) };
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0), ("OXNARD", 34.2, -119.2));

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL MODESTO OXNARD", fixes);

        Assert.Single(resolved);
        Assert.Equal("OXNARD", resolved[0].Name);
    }

    [Fact]
    public void LastFixNotInRoute_NothingAppended()
    {
        var resolved = new List<ResolvedFix> { new("BRIXX", 37.7, -121.9) };
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0));

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL MODESTO", fixes);

        Assert.Single(resolved);
        Assert.Equal("BRIXX", resolved[0].Name);
    }

    [Fact]
    public void RouteTokenWithAltitudeConstraint_StripsConstraintAndMatches()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0));

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL.A50 MODESTO", fixes);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("SUNOL", resolved[0].Name);
        Assert.Equal("MODESTO", resolved[1].Name);
    }

    [Fact]
    public void UnknownFixInRemainder_Skipped()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8), ("OXNARD", 34.2, -119.2));

        RouteChainer.AppendRouteRemainder(resolved, "SUNOL UNKNOWN OXNARD", fixes);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("SUNOL", resolved[0].Name);
        Assert.Equal("OXNARD", resolved[1].Name);
    }

    [Fact]
    public void EmptyRouteString_NothingAppended()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8));

        RouteChainer.AppendRouteRemainder(resolved, "", fixes);

        Assert.Single(resolved);
    }

    [Fact]
    public void CaseInsensitiveMatch_MixedCaseRouteToken_StillMatches()
    {
        var resolved = new List<ResolvedFix> { new("SUNOL", 37.5, -121.8) };
        var fixes = new StubFixLookup(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0));

        RouteChainer.AppendRouteRemainder(resolved, "Sunol MODESTO", fixes);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("SUNOL", resolved[0].Name);
        Assert.Equal("MODESTO", resolved[1].Name);
    }

    private class StubFixLookup : IFixLookup
    {
        private readonly Dictionary<string, (double Lat, double Lon)> _fixes = new(StringComparer.OrdinalIgnoreCase);

        public StubFixLookup(params (string Name, double Lat, double Lon)[] fixes)
        {
            foreach (var (name, lat, lon) in fixes)
            {
                _fixes[name] = (lat, lon);
            }
        }

        public (double Lat, double Lon)? GetFixPosition(string name) => _fixes.TryGetValue(name, out var pos) ? pos : null;

        public double? GetAirportElevation(string code) => null;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }
}
