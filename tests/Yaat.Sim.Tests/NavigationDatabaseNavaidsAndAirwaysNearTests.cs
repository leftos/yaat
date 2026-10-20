using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="NavigationDatabase.NavaidsNear"/> and <see cref="NavigationDatabase.AirwaysNear"/> over the real navigation data
/// around the OAK VOR: the radial navaids nearest first, and the airways through the route's fixes and the fixes nearby, nearest
/// first, military routes left out.
/// </summary>
public class NavigationDatabaseNavaidsAndAirwaysNearTests
{
    public NavigationDatabaseNavaidsAndAirwaysNearTests() => TestVnasData.EnsureInitialized();

    private static NavigationDatabase Db => NavigationDatabase.Instance;

    private static LatLon Position(string fix)
    {
        (double lat, double lon) = Db.GetFixPosition(fix) ?? throw new InvalidOperationException($"No {fix} in navdata.");
        return new LatLon(lat, lon);
    }

    /// <summary>The airways (military routes left out) whose fixes include <paramref name="fix"/>, read straight off the airway table.</summary>
    private static HashSet<string> AirwaysThrough(string fix) =>
        [.. Db.AirwayIds.Where(id => !Db.IsMilitaryRoute(id) && (Db.GetAirwayFixes(id) ?? []).Contains(fix, StringComparer.OrdinalIgnoreCase))];

    private static void AssertNearestFirst(IReadOnlyList<NearbyAirway> airways)
    {
        Assert.Equal(airways.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), airways.Count);
        IEnumerable<NearbyAirway> sorted = airways.OrderBy(a => a.JoinFix.DistanceNm).ThenBy(a => a.Id, StringComparer.Ordinal);
        Assert.Equal(sorted.Select(a => a.Id), airways.Select(a => a.Id));
    }

    [Fact]
    public void NavaidsNear_OakVor_ListsEveryRadialNavaidNearestFirst()
    {
        IReadOnlyList<NearbyFix> navaids = Db.NavaidsNear(Position("OAK"), "");

        Assert.Equal("OAK", navaids[0].Name);
        Assert.Equal(0, navaids[0].DistanceNm, 3);
        Assert.Contains(navaids, navaid => navaid.Name == "SFO");
        Assert.DoesNotContain(navaids, navaid => navaid.Name == "SUNOL");
        Assert.All(navaids, navaid => Assert.True(Db.IsRadialNavaid(navaid.Name), $"{navaid.Name} is a {Db.GetNavaidType(navaid.Name)}"));
        Assert.Equal(Db.GetFixTuples().Count(fix => Db.IsRadialNavaid(fix.Name)), navaids.Count);
        Assert.Equal(navaids.Select(navaid => navaid.DistanceNm).Order(), navaids.Select(navaid => navaid.DistanceNm));
    }

    /// <summary>A navaid that gives no radial (a DME, an NDB) is never listed, though the navigation data knows its type.</summary>
    [Fact]
    public void NavaidsNear_LeavesOutNavaidsWithoutRadials()
    {
        string other = Db.GetFixTuples().Select(fix => fix.Name).First(name => (Db.GetNavaidType(name) is not null) && !Db.IsRadialNavaid(name));

        Assert.DoesNotContain(Db.NavaidsNear(Position(other), ""), navaid => navaid.Name == other);
    }

    [Theory]
    [InlineData("VOR", true)]
    [InlineData("VORTAC", true)]
    [InlineData("NDB", false)]
    public void IsRadialNavaid_ReadsTheNavaidType(string type, bool radial)
    {
        string name = Db.GetFixTuples().Select(fix => fix.Name).First(name => Db.GetNavaidType(name) == type);

        Assert.Equal(radial, Db.IsRadialNavaid(name));
    }

    [Fact]
    public void NavaidsNear_WithAPrefix_ListsOnlyNamesStartingWithIt_IgnoringCase()
    {
        IReadOnlyList<NearbyFix> navaids = Db.NavaidsNear(Position("OAK"), "sf");

        NearbyFix sfo = Assert.Single(navaids, navaid => navaid.Name == "SFO");
        Assert.Equal(GeoMath.DistanceNm(Position("OAK"), Position("SFO")), sfo.DistanceNm, 6);
        Assert.All(navaids, navaid => Assert.StartsWith("SF", navaid.Name, StringComparison.Ordinal));
    }

    /// <summary>Every airway through a fix within the radius, each once, nearest first, joined at its nearest such fix.</summary>
    [Fact]
    public void AirwaysNear_ListsTheAirwaysThroughFixesWithinTheRadius_NearestFirst()
    {
        LatLon oak = Position("OAK");
        IReadOnlyList<NearbyAirway> airways = Db.AirwaysNear([], oak, 30);

        HashSet<string> expected = [];
        foreach (NearbyFix fix in Db.FixesNear(oak, "").TakeWhile(fix => fix.DistanceNm <= 30))
        {
            expected.UnionWith(AirwaysThrough(fix.Name));
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Order(StringComparer.Ordinal), airways.Select(a => a.Id).Order(StringComparer.Ordinal));
        AssertNearestFirst(airways);
        Assert.All(
            airways,
            airway =>
            {
                Assert.InRange(airway.JoinFix.DistanceNm, 0, 30);
                Assert.Contains(airway.JoinFix.Name, Db.GetAirwayFixes(airway.Id) ?? [], StringComparer.OrdinalIgnoreCase);
            }
        );
    }

    /// <summary>A route fix beyond the radius still brings its airways, joined there.</summary>
    [Fact]
    public void AirwaysNear_ARouteFixBeyondTheRadius_StillListsItsAirways()
    {
        LatLon oak = Position("OAK");
        Assert.True(GeoMath.DistanceNm(oak, Position("RBL")) > 30);
        HashSet<string> throughRbl = AirwaysThrough("RBL");
        HashSet<string> nearby = [.. Db.AirwaysNear([], oak, 30).Select(a => a.Id)];
        string farOnly = throughRbl.First(id => !nearby.Contains(id));

        IReadOnlyList<NearbyAirway> airways = Db.AirwaysNear(["RBL"], oak, 30);

        Assert.Superset(throughRbl, airways.Select(a => a.Id).ToHashSet());
        Assert.Equal("RBL", Assert.Single(airways, a => a.Id == farOnly).JoinFix.Name);
        AssertNearestFirst(airways);
    }

    /// <summary>OAK lies on several airways: each is listed once.</summary>
    [Fact]
    public void AirwaysNear_AFixOnSeveralAirways_ListsEachOnce()
    {
        HashSet<string> throughOak = AirwaysThrough("OAK");
        Assert.True(throughOak.Count >= 2, $"OAK is on {throughOak.Count} airways");

        IReadOnlyList<NearbyAirway> airways = Db.AirwaysNear(["OAK", "OAK"], Position("OAK"), 0);

        Assert.Equal(throughOak.Order(StringComparer.Ordinal), airways.Select(a => a.Id).Order(StringComparer.Ordinal));
        Assert.All(airways, airway => Assert.Equal("OAK", airway.JoinFix.Name));
        AssertNearestFirst(airways);
    }

    /// <summary>The airway table also holds the military training routes; they are never offered.</summary>
    [Fact]
    public void AirwaysNear_LeavesOutMilitaryRoutes()
    {
        (string route, string point) = Db
            .AirwayIds.Where(Db.IsMilitaryRoute)
            .SelectMany(id => (Db.GetAirwayFixes(id) ?? []).Select(point => (id, point)))
            .First(pair => Db.GetFixPosition(pair.point) is not null);

        IReadOnlyList<NearbyAirway> airways = Db.AirwaysNear([point], Position(point), 0);

        Assert.DoesNotContain(airways, airway => airway.Id == route);
        Assert.All(airways, airway => Assert.False(Db.IsMilitaryRoute(airway.Id)));
    }

    [Fact]
    public void AirwaysNear_ARouteFixWithoutAPosition_AddsNothing()
    {
        Assert.Null(Db.GetFixPosition("NOSUCHFIX"));

        Assert.DoesNotContain(Db.AirwaysNear(["NOSUCHFIX"], Position("OAK"), 0), a => a.JoinFix.Name == "NOSUCHFIX");
    }
}
