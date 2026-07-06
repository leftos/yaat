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
        var distNm = GeoMath.DistanceNm(lat, lon, resolved.Value.Lat, resolved.Value.Lon);
        Assert.True(distNm < 2.0, $"Round-trip distance was {distNm:F2} nm, expected < 2.0");
    }

    [Fact]
    public void ToFrd_EmitsMagneticRadial_NotTrue()
    {
        // A point ~10 nm due-east of OAK (37.7213, -122.2208). FRD azimuths are conventionally
        // MAGNETIC (7110.65 4-4-3.a.1.2), not true. OAK sits in ~13-15 deg EAST declination, so the
        // emitted magnetic radial must be that many degrees LESS than the raw true bearing.
        double oakLat = 37.7213,
            oakLon = -122.2208;
        double lat = 37.7213,
            lon = -122.0500;

        double trueBrg = GeoMath.BearingTo(oakLat, oakLon, lat, lon);
        int trueRadial = (int)Math.Round(trueBrg);
        double magBrg = MagneticDeclination.TrueToMagnetic(trueBrg, oakLat, oakLon);
        int expectedRadial = (int)Math.Round(magBrg);

        var frd = FrdResolver.ToFrd(lat, lon, TestFixes);
        Assert.NotNull(frd);
        var parsed = FrdResolver.ParseFrd(frd);
        Assert.NotNull(parsed);
        Assert.Equal("OAK", parsed.Value.Fix);
        Assert.Equal(expectedRadial, parsed.Value.Radial);

        // Declination actually applied, correct sign and realistic magnitude for OAK.
        Assert.True(expectedRadial < trueRadial, $"magnetic {expectedRadial} should be east-of-true less than true {trueRadial}");
        Assert.InRange(trueRadial - expectedRadial, 10, 18);
    }

    [Fact]
    public void Resolve_InterpretsRadialAsMagnetic()
    {
        // The mirror of ToFrd: a typed FRD radial is magnetic, so Resolve must convert magnetic->true
        // before projecting. OAK090010 should land 10 nm along the 090 MAGNETIC radial.
        double oakLat = 37.7213,
            oakLon = -122.2208;
        var navDb = TestNavDbFactory.WithFixes(("OAK", oakLat, oakLon));

        var resolved = FrdResolver.Resolve("OAK090010", navDb);
        Assert.NotNull(resolved);

        double trueBrg = GeoMath.BearingTo(oakLat, oakLon, resolved.Value.Lat, resolved.Value.Lon);
        double magBrg = MagneticDeclination.TrueToMagnetic(trueBrg, oakLat, oakLon);
        Assert.InRange(magBrg, 89.0, 91.0); // resolved point is on the 090 magnetic radial
        Assert.InRange(GeoMath.DistanceNm(oakLat, oakLon, resolved.Value.Lat, resolved.Value.Lon), 9.5, 10.5);
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
