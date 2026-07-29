using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

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

    [Theory]
    [InlineData("OAK169001", true)]
    [InlineData("ABI037030", true)]
    [InlineData("DUMBA090010", true)]
    [InlineData("OAK", false)]
    [InlineData("SUNOL", false)]
    [InlineData("OAK169", false)] // radial-only shape is not treated as an FRD identifier
    [InlineData("AA409", false)]
    [InlineData("OAK000010", false)] // radial 000 is not a valid FRD azimuth
    [InlineData("XYZ999123", false)] // radial 999 is not a valid FRD azimuth
    [InlineData("A169001", false)] // single-letter anchor
    [InlineData("TOOLONG169001", false)] // anchor longer than 5 chars
    [InlineData("OA1169001", false)] // digit inside the anchor
    [InlineData("", false)]
    public void IsFrdIdentifier_ClassifiesNames(string name, bool expected)
    {
        Assert.Equal(expected, FrdResolver.IsFrdIdentifier(name));
    }

    // vNAS NavData publishes thousands of adapted fixes whose identifiers are themselves FRD
    // strings (OAK169001, ABI037030, …). Anchoring on one emits an FRD-of-an-FRD such as
    // "DCTF OAK169001231001", which no controller or pilot can read back.
    private static readonly IReadOnlyList<(string Name, double Lat, double Lon)> FrdNamedFixes =
    [
        ("OAK", 37.7213, -122.2208),
        ("OAK169001", 37.7053, -122.2166), // ~1 nm from OAK, on its 169 radial
    ];

    [Fact]
    public void ToFrd_NearFrdNamedFix_AnchorsOnRealFixInstead()
    {
        // ~3 nm south of the OAK169001 adapted fix — that fix is the nearest, but it must not anchor.
        var result = FrdResolver.ToFrd(37.6553, -122.2166, FrdNamedFixes);

        Assert.NotNull(result);
        Assert.Matches(@"^OAK\d{6}$", result);
    }

    [Fact]
    public void ToFrd_OnTopOfFrdNamedFix_ReturnsBareIdentifier()
    {
        // Within 0.1 nm: the identifier itself is a real, resolvable fix, so naming it is correct.
        var result = FrdResolver.ToFrd(37.7054, -122.2165, FrdNamedFixes);

        Assert.Equal("OAK169001", result);
    }

    [Fact]
    public void ToFrd_RealNavData_NeverAnchorsOnFrdNamedFix()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        // Guards the premise: the real vNAS dataset really does publish these as fixes.
        Assert.NotNull(navDb.GetFixPosition("OAK169001"));

        var allFixes = navDb.GetFixTuples();
        var frdNamed = allFixes.Where(f => FrdResolver.IsFrdIdentifier(f.Name)).Take(200).ToList();
        Assert.NotEmpty(frdNamed);

        // Probe 2 nm north of each adapted fix: far enough that the distance doesn't round to zero
        // (which would return a bare identifier rather than a constructed FRD), close enough that the
        // adapted fix or one of its neighbours is still the nearest entry of any kind.
        int probesWhereFrdFixIsNearest = 0;
        foreach (var probeAnchor in frdNamed)
        {
            double lat = probeAnchor.Lat + (2.0 / 60.0);
            double lon = probeAnchor.Lon;

            var nearest = allFixes.MinBy(f => GeoMath.DistanceNm(lat, lon, f.Lat, f.Lon));
            if (!FrdResolver.IsFrdIdentifier(nearest.Name))
            {
                continue;
            }

            probesWhereFrdFixIsNearest++;

            var result = FrdResolver.ToFrd(lat, lon, allFixes);
            Assert.NotNull(result);
            var anchor = FrdResolver.ParseFrd(result)?.Fix;
            Assert.NotNull(anchor);
            Assert.False(FrdResolver.IsFrdIdentifier(anchor), $"ToFrd anchored on FRD-named fix '{anchor}' (result '{result}')");
        }

        Assert.True(probesWhereFrdFixIsNearest > 0, "no probe had an FRD-named fix as its nearest entry — the test proves nothing");
    }
}
