using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="NavigationDatabase.FixesNear"/> over the real navigation data: every fix whose name starts with a prefix, nearest
/// first, however far.
/// </summary>
public class NavigationDatabaseFixesNearTests
{
    public NavigationDatabaseFixesNearTests() => TestVnasData.EnsureInitialized();

    private static LatLon OakVor()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("OAK") ?? throw new InvalidOperationException("No OAK in navdata.");
        return new LatLon(lat, lon);
    }

    [Fact]
    public void FixesNear_OakVor_ListsEveryFixNearestFirst()
    {
        IReadOnlyList<NearbyFix> fixes = NavigationDatabase.Instance.FixesNear(OakVor(), "");

        Assert.Equal("OAK", fixes[0].Name);
        Assert.Equal(0, fixes[0].DistanceNm, 3);
        Assert.Contains(fixes, fix => fix.Name == "SUNOL");
        Assert.Contains(fixes, fix => fix.Name == "SAC");
        Assert.Equal(NavigationDatabase.Instance.GetFixTuples().Count, fixes.Count);
        Assert.Equal(fixes.Select(fix => fix.DistanceNm).Order(), fixes.Select(fix => fix.DistanceNm));
    }

    [Fact]
    public void FixesNear_WithAPrefix_ListsOnlyNamesStartingWithIt_IgnoringCase()
    {
        IReadOnlyList<NearbyFix> fixes = NavigationDatabase.Instance.FixesNear(OakVor(), "sa");

        Assert.NotEmpty(fixes);
        Assert.All(fixes, fix => Assert.StartsWith("SA", fix.Name, StringComparison.Ordinal));
        Assert.Contains(fixes, fix => fix.Name == "SAC");
        Assert.Equal(fixes.Select(fix => fix.DistanceNm).Order(), fixes.Select(fix => fix.DistanceNm));
    }

    [Fact]
    public void FixesNear_ListsAFixAcrossTheCountry()
    {
        IReadOnlyList<NearbyFix> fixes = NavigationDatabase.Instance.FixesNear(OakVor(), "KJF");

        NearbyFix kjfk = Assert.Single(fixes, fix => fix.Name == "KJFK");
        Assert.InRange(kjfk.DistanceNm, 2000, 2300);
    }

    [Fact]
    public void FixesNear_ReportsEachFixsPositionAndDistance()
    {
        LatLon oak = OakVor();
        NearbyFix sunol = Assert.Single(NavigationDatabase.Instance.FixesNear(oak, "SUNOL"), fix => fix.Name == "SUNOL");

        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("SUNOL")!.Value;
        Assert.Equal(new LatLon(lat, lon), sunol.Position);
        Assert.Equal(GeoMath.DistanceNm(oak, sunol.Position), sunol.DistanceNm, 6);
    }
}
