using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="RoutePointResolver"/> turns each kind of route point into a position and the nav-path text the scenario loader
/// reads, on the committed navdata. Points that need a particular geometry are searched for in that navdata, so each case
/// states the geometry it needs and fails when the data no longer has it.
/// </summary>
public class RoutePointResolverTests
{
    public RoutePointResolverTests() => TestVnasData.EnsureInitialized();

    private static NavigationDatabase NavDb => NavigationDatabase.Instance;

    private static LatLon FixPosition(string name)
    {
        (double Lat, double Lon) pos = NavDb.GetFixPosition(name) ?? throw new InvalidOperationException(name + " is not in navdata");
        return new LatLon(pos.Lat, pos.Lon);
    }

    private static bool IsPlainFix(string name) => !FrdResolver.IsFrdIdentifier(name);

    [Fact]
    public void RealFix_ResolvesToItsNavdataPosition()
    {
        RoutePointResult result = RoutePointResolver.Resolve(RoutePoint.AtFix("SUNOL"), FixResolutionContext.None, NavDb);

        Assert.Null(result.Failure);
        Assert.Equal(FixPosition("SUNOL"), result.Position);
        Assert.Equal("SUNOL", result.Text);
    }

    [Fact]
    public void UnknownFix_ReturnsAFailure()
    {
        RoutePointResult result = RoutePointResolver.Resolve(RoutePoint.AtFix("QQQQZ"), FixResolutionContext.None, NavDb);

        Assert.NotNull(result.Failure);
        Assert.Contains("QQQQZ", result.Failure);
        Assert.Null(result.Position);
    }

    [Fact]
    public void NavdataNotLoaded_ReturnsNavdataNotLoaded()
    {
        RoutePointResult result = RoutePointResolver.Resolve(RoutePoint.AtFix("SUNOL"), FixResolutionContext.None, null);

        Assert.Equal("navdata not loaded", result.Failure);
        Assert.Equal("navdata not loaded", RoutePointResolver.Anchor(FixPosition("SUNOL"), null).Failure);
        Assert.Equal("navdata not loaded", RoutePointResolver.PickFix("SUNOL", FixPosition("SUNOL"), FixResolutionContext.None, null).Failure);
        Assert.Equal("navdata not loaded", RoutePointResolver.NavPathText([RoutePoint.AtFix("SUNOL")], FixResolutionContext.None, null).Failure);
    }

    [Fact]
    public void FrdPoint_ResolvesAsFrdResolverAndItsTextReparses()
    {
        RoutePointResult result = RoutePointResolver.Resolve(RoutePoint.AtFrd("SFO", 90, 10), FixResolutionContext.None, NavDb);

        LatLon expected = FrdResolver.Resolve("SFO090010", NavDb) ?? throw new InvalidOperationException("SFO090010 unresolved");
        Assert.Equal("SFO090010", result.Text);
        Assert.Equal(expected, result.Position);
        LatLon reparsed = FrdResolver.Resolve(result.Text!, NavDb) ?? throw new InvalidOperationException("text unresolved");
        Assert.True(GeoMath.DistanceNm(reparsed, result.Position!.Value) < 0.01);
    }

    [Fact]
    public void LatLonNearAVorAndAnIntersection_AnchorsOnTheVor()
    {
        LatLon? click = null;
        int found = 0;
        for (int radial = 5; (radial <= 360) && (click is null); radial += 5)
        {
            LatLon candidate = FrdResolver.Resolve($"SFO{radial:D3}020", NavDb)!.Value;
            bool vorNearest = NavDb.NavaidsNear(candidate, "")[0].Name == "SFO";
            bool intersectionClose = NavDb.FixesWithin(candidate, 5.0).Any(f => IsPlainFix(f.Name) && !NavDb.IsRadialNavaid(f.Name));
            if (vorNearest && intersectionClose)
            {
                click = candidate;
                found = radial;
            }
        }

        Assert.NotNull(click);
        RoutePointResult result = RoutePointResolver.Resolve(RoutePoint.AtLatLon(click.Value), FixResolutionContext.None, NavDb);

        Assert.Null(result.Failure);
        Assert.Equal($"SFO{found:D3}020", result.Text);
    }

    [Fact]
    public void LatLonWithNoNavaidWithin30Nm_AnchorsOnTheNearestFixWithin50Nm()
    {
        LatLon? click = null;
        string? nearest = null;
        for (double lat = 36.0; (lat <= 44.0) && (click is null); lat += 0.25)
        {
            for (double lon = -124.0; (lon <= -112.0) && (click is null); lon += 0.25)
            {
                var candidate = new LatLon(lat, lon);
                NearbyFix? fix = NavDb.FixesWithin(candidate, 50.0).FirstOrDefault(f => IsPlainFix(f.Name));
                if ((NavDb.NavaidsNear(candidate, "")[0].DistanceNm > 30.0) && (fix is not null) && (fix.DistanceNm > 1.0))
                {
                    click = candidate;
                    nearest = fix.Name;
                }
            }
        }

        Assert.NotNull(click);
        RoutePointResult result = RoutePointResolver.Anchor(click.Value, NavDb);

        Assert.Null(result.Failure);
        Assert.Matches("^" + Regex.Escape(nearest!) + "[0-9]{6}$", result.Text);
    }

    [Fact]
    public void LatLonWithNothingWithin50Nm_AsksToAddAFix()
    {
        LatLon[] candidates = [new(30.0, -140.0), new(10.0, -150.0), new(-40.0, -120.0), new(-60.0, 0.0)];
        LatLon click = candidates.First(c => !NavDb.FixesWithin(c, 50.0).Any(f => IsPlainFix(f.Name)));

        RoutePointResult result = RoutePointResolver.Anchor(click, NavDb);

        Assert.NotNull(result.Failure);
        Assert.Contains("add a fix", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Text);
    }

    [Fact]
    public void DisplacementOverOneNm_Warns_AndWithinOneNmDoesNot()
    {
        LatLon sunol = FixPosition("SUNOL");
        var farther = new LatLon(sunol.Lat + (1.1 / 60.0), sunol.Lon);
        var closer = new LatLon(sunol.Lat + (0.9 / 60.0), sunol.Lon);

        Assert.NotNull(RoutePointResolver.DisplacementWarning(farther, sunol, "SUNOL"));
        Assert.Null(RoutePointResolver.DisplacementWarning(closer, sunol, "SUNOL"));
    }

    [Fact]
    public void AnchoredPointOnAnFrd_CarriesNoWarning()
    {
        LatLon click = FrdResolver.Resolve("SFO090010", NavDb)!.Value;

        RoutePointResult result = RoutePointResolver.Anchor(click, NavDb);

        Assert.Equal("OAK148010", result.Text);
        Assert.Null(result.Warning);
        Assert.True(GeoMath.DistanceNm(click, result.Position!.Value) <= 1.0);
    }

    // FAA terminal waypoint idents are unique in the committed data, so the NavData instance of a duplicated ident is the one
    // seeded entry (tests/CLAUDE.md: a case real data cannot express goes through a database of its own); the terminal
    // instance is the real KSFO waypoint ADDMM read from the committed CIFP.
    private static (NavigationDatabase Db, LatLon Terminal, LatLon NavData) DuplicatedAddmm()
    {
        Assert.True(NavDb.TryGetTerminalWaypoint("KSFO", "ADDMM", out (double Lat, double Lon) terminal));
        var navData = new LatLon(terminal.Lat + 0.5, terminal.Lon);
        var db = NavigationDatabase.ForTesting(
            fixes: new Dictionary<string, (double Lat, double Lon)> { ["ADDMM"] = (navData.Lat, navData.Lon) },
            cifpFilePath: TestVnasData.GetCifpPath()
        );
        return (db, new LatLon(terminal.Lat, terminal.Lon), navData);
    }

    [Fact]
    public void DuplicatedIdent_PicksTheNearestInstanceAndWarnsWhenTheSimResolvesTheOther()
    {
        (NavigationDatabase db, LatLon terminal, _) = DuplicatedAddmm();
        var ctx = new FixResolutionContext("KSFO", null);

        RoutePointResult result = RoutePointResolver.PickFix("ADDMM", terminal, ctx, db);

        Assert.Null(result.Failure);
        Assert.Equal("ADDMM", result.Text);
        Assert.True(GeoMath.DistanceNm(terminal, result.Position!.Value) < 0.01);
        Assert.Contains("30.0 NM", result.Warning);
    }

    [Fact]
    public void DuplicatedIdent_PickedWhereTheSimResolvesIt_CarriesNoWarning()
    {
        (NavigationDatabase db, _, LatLon navData) = DuplicatedAddmm();
        var ctx = new FixResolutionContext("KSFO", null);

        RoutePointResult result = RoutePointResolver.PickFix("ADDMM", navData, ctx, db);

        Assert.Null(result.Failure);
        Assert.True(GeoMath.DistanceNm(navData, result.Position!.Value) < 0.01);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void NavPathText_JoinsFixFixFrdWithSingleSpaces()
    {
        RoutePoint[] points = [RoutePoint.AtFix("SUNOL"), RoutePoint.AtFix("SFO"), RoutePoint.AtFrd("SFO", 90, 10)];

        RoutePointResult result = RoutePointResolver.NavPathText(points, FixResolutionContext.None, NavDb);

        Assert.Null(result.Failure);
        Assert.Equal("SUNOL SFO SFO090010", result.Text);
    }

    [Fact]
    public void TerminalOnlyFix_ResolvesWithTheContextPickFixUsedAndFailsWithout()
    {
        Assert.True(NavDb.TryGetTerminalWaypoint("KSFO", "ADDMM", out (double Lat, double Lon) terminal));
        var db = NavigationDatabase.ForTesting(cifpFilePath: TestVnasData.GetCifpPath());
        var ctx = new FixResolutionContext("KSFO", null);
        RoutePointResult picked = RoutePointResolver.PickFix("ADDMM", new LatLon(terminal.Lat, terminal.Lon), ctx, db);
        Assert.Null(picked.Failure);

        RoutePointResult withContext = RoutePointResolver.Resolve(RoutePoint.AtFix(picked.Text!), ctx, db);
        RoutePointResult without = RoutePointResolver.Resolve(RoutePoint.AtFix(picked.Text!), FixResolutionContext.None, db);

        Assert.Null(withContext.Failure);
        Assert.Equal(picked.Position, withContext.Position);
        Assert.Equal("Unknown fix ADDMM.", without.Failure);
    }

    [Fact]
    public void FrdOnAnFrdShapedAnchor_IsRefused()
    {
        RoutePointResult result = RoutePointResolver.Resolve(RoutePoint.AtFrd("OAK169001", 90, 10), FixResolutionContext.None, NavDb);

        Assert.NotNull(result.Failure);
        Assert.Contains("OAK169001", result.Failure);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(361, 10)]
    [InlineData(90, 1000)]
    public void FrdOutOfRange_IsAFailure(int radial, int distanceNm)
    {
        RoutePointResult result = RoutePointResolver.Resolve(RoutePoint.AtFrd("SFO", radial, distanceNm), FixResolutionContext.None, NavDb);

        Assert.Equal("The FRD point needs a fix, a radial from 1 to 360 and a distance from 0 to 999 NM.", result.Failure);
    }

    [Fact]
    public void LatLonPointWithoutCoordinates_IsAFailure()
    {
        var point = new RoutePoint(RoutePointKind.LatLon, null, null, null, null, null);

        RoutePointResult result = RoutePointResolver.Resolve(point, FixResolutionContext.None, NavDb);

        Assert.Equal("The LatLon point is missing its fields.", result.Failure);
    }

    [Fact]
    public void UndefinedKind_IsAFailure()
    {
        var point = new RoutePoint((RoutePointKind)99, "SUNOL", null, null, null, null);

        RoutePointResult result = RoutePointResolver.Resolve(point, FixResolutionContext.None, NavDb);

        Assert.Equal("Unknown route point kind 99.", result.Failure);
    }

    [Fact]
    public void NavPathText_NamesTheFirstFailingPointByNumber()
    {
        RoutePoint[] points = [RoutePoint.AtFix("SUNOL"), RoutePoint.AtFix("QQQQZ"), RoutePoint.AtFix("QQQQY")];

        RoutePointResult result = RoutePointResolver.NavPathText(points, FixResolutionContext.None, NavDb);

        Assert.Equal("Point 2: Unknown fix QQQQZ.", result.Failure);
        Assert.Null(result.Text);
    }
}
