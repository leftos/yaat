using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="NavigationDatabase.FixesWithin"/> over the real navigation data: every fix within a radius, nearest first, the
/// same fixes in the same order as <see cref="NavigationDatabase.FixesNear"/> lists inside that radius.
/// </summary>
public class NavigationDatabaseFixesWithinTests
{
    public NavigationDatabaseFixesWithinTests() => TestVnasData.EnsureInitialized();

    private static LatLon OakVor()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("OAK") ?? throw new InvalidOperationException("No OAK in navdata.");
        return new LatLon(lat, lon);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(10.0)]
    public void FixesWithin_OakVor_ListsTheFixesNearListsInsideTheRadius(double radiusNm)
    {
        LatLon oak = OakVor();

        IReadOnlyList<NearbyFix> within = NavigationDatabase.Instance.FixesWithin(oak, radiusNm);

        Assert.Equal("OAK", within[0].Name);
        Assert.All(within, fix => Assert.InRange(fix.DistanceNm, 0, radiusNm));
        Assert.Equal(NavigationDatabase.Instance.FixesNear(oak, "").TakeWhile(fix => fix.DistanceNm <= radiusNm), within);
    }

    [Fact]
    public void FixesWithin_TenNmOfOak_HoldsAFixInsideAndNoneBeyond()
    {
        IReadOnlyList<NearbyFix> within = NavigationDatabase.Instance.FixesWithin(OakVor(), 10);

        Assert.True(within.Count > 1);
        Assert.DoesNotContain(within, fix => fix.Name is "SUNOL" or "SAC" or "KJFK");
    }
}
