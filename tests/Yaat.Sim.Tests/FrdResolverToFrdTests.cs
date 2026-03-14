using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class FrdResolverToFrdTests
{
    private static readonly IReadOnlyList<(string Name, double Lat, double Lon)> TestFixes =
    [
        ("OAK", 37.7213, -122.2208),
        ("SFO", 37.6213, -122.3790),
        ("SAC", 38.5125, -121.4944),
        ("SUNOL", 37.5922, -121.8822),
    ];

    [Fact]
    public void ToFrd_AtFixPosition_ReturnsBareFixName()
    {
        var result = FrdResolver.ToFrd(37.7213, -122.2208, TestFixes);

        Assert.Equal("OAK", result);
    }

    [Fact]
    public void ToFrd_NearFixPosition_ReturnsBareFixName()
    {
        // Within 0.1nm of OAK
        var result = FrdResolver.ToFrd(37.7214, -122.2207, TestFixes);

        Assert.Equal("OAK", result);
    }

    [Fact]
    public void ToFrd_AwayFromFix_ReturnsFrdString()
    {
        // ~10nm north of OAK
        var result = FrdResolver.ToFrd(37.8880, -122.2208, TestFixes);

        Assert.NotNull(result);
        Assert.StartsWith("OAK", result);
        Assert.Matches(@"^OAK\d{6}$", result);
    }

    [Fact]
    public void ToFrd_NoFixesInRange_ReturnsNull()
    {
        var result = FrdResolver.ToFrd(0.0, 0.0, TestFixes, maxNm: 1.0);

        Assert.Null(result);
    }

    [Fact]
    public void ToFrd_EmptyFixList_ReturnsNull()
    {
        var result = FrdResolver.ToFrd(37.7213, -122.2208, []);

        Assert.Null(result);
    }

    [Fact]
    public void ToFrd_RoundTrip_ResolvesCloseToOriginal()
    {
        // A position 10nm east of OAK
        double lat = 37.7213;
        double lon = -122.0500;

        var frdString = FrdResolver.ToFrd(lat, lon, TestFixes);
        Assert.NotNull(frdString);

        // Resolve it back
        var stubFixes = TestNavDbFactory.WithFixes(("OAK", 37.7213, -122.2208));
        var resolved = FrdResolver.Resolve(frdString, stubFixes);
        Assert.NotNull(resolved);

        // Should be within 2nm of original (rounding of radial and distance)
        var distNm = GeoMath.DistanceNm(lat, lon, resolved.Latitude, resolved.Longitude);
        Assert.True(distNm < 2.0, $"Round-trip distance was {distNm:F2} nm, expected < 2.0");
    }

    [Fact]
    public void ToFrd_PicksNearestFix()
    {
        // Position much closer to SUNOL than OAK
        var result = FrdResolver.ToFrd(37.59, -121.88, TestFixes);

        Assert.NotNull(result);
        Assert.StartsWith("SUNOL", result);
    }

    [Fact]
    public void ToFrd_RadialFormattedThreeDigits()
    {
        // Position north of OAK — radial should be zero-padded
        var result = FrdResolver.ToFrd(37.9, -122.2208, TestFixes);

        Assert.NotNull(result);
        // The string after the fix name should be 6 digits: radial(3) + distance(3)
        var suffix = result["OAK".Length..];
        Assert.Equal(6, suffix.Length);
        Assert.True(int.TryParse(suffix, out _));
    }
}
