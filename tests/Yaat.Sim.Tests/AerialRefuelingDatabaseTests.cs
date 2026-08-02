using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;

namespace Yaat.Sim.Tests;

/// <summary>
/// Fixture-level tests for the committed AP/1B chapter 5 aerial refueling data (built by
/// tools/build-mtr-data.py alongside the IR/VR/SR fixture).
/// </summary>
[Collection("NavDbMutator")]
public sealed class AerialRefuelingDatabaseTests
{
    public AerialRefuelingDatabaseTests() => TestVnasData.EnsureInitialized();

    private static MilitaryRouteDatabase Db => MilitaryRouteDatabase.Default;

    private static IEnumerable<MilitaryRoute> Refueling => Db.Routes.Where(r => r.IsAerialRefueling);

    [Fact]
    public void Default_LoadsEveryPublishedRefuelingEntry()
    {
        // AP/1B cycle 2607 chapter 5 publishes 156 refueling tracks and 91 anchors. The counts are
        // pinned because a drift means the extractor changed behaviour, not that the DoD
        // reorganised the book -- the build tool gates on the same numbers.
        Assert.Equal(247, Refueling.Count());
        Assert.Equal(156, Refueling.Count(r => r.ArKind == MilitaryRouteArKind.Track));
        Assert.Equal(91, Refueling.Count(r => r.ArKind == MilitaryRouteArKind.Anchor));
        Assert.All(Refueling, r => Assert.Equal(MilitaryRouteType.Ar, r.Type));
    }

    [Fact]
    public void TrainingRoutes_AreNotMarkedAsRefueling()
    {
        Assert.All(Db.Routes.Where(r => r.Type != MilitaryRouteType.Ar), r => Assert.Equal(MilitaryRouteArKind.None, r.ArKind));
        Assert.False(Db.Get("IR149")!.IsAerialRefueling);
    }

    [Fact]
    public void Ar1_MatchesThePublishedTrackDescription()
    {
        var route = Db.Get("AR1");

        Assert.NotNull(route);
        Assert.Equal(MilitaryRouteArKind.Track, route!.ArKind);
        // AP/1B page 5-1: ARIP BAM 060/30, ARCP MLD 225/94, check points MLD 090/10 and BOY 227/92,
        // exit OCS 008/118, flown eastbound at FL240/FL310.
        var variant = Assert.Single(route.Variants);
        Assert.Equal("East", variant.Direction);
        Assert.Equal(
            [
                MilitaryRoutePointRole.Arip,
                MilitaryRoutePointRole.Arcp,
                MilitaryRoutePointRole.CheckPoint,
                MilitaryRoutePointRole.CheckPoint,
                MilitaryRoutePointRole.Exit,
            ],
            variant.Points.Select(p => p.Role)
        );
        Assert.Equal("BAM060030", variant.Points[0].Frd);
        Assert.Equal("OCS008118", variant.Points[^1].Frd);
        Assert.Equal(24000, route.RouteAltitude.FloorFt);
        Assert.Equal(31000, route.RouteAltitude.CeilingFt);
    }

    [Fact]
    public void OpposingTrackDirections_AreSeparateGeometry()
    {
        // AR4A's two directions are laterally offset parallels, not one line flown backwards: the
        // southbound ARIP is ~50 NM from the northbound exit. Collapsing them into one point list
        // would fly half the traffic down the wrong track.
        var route = Db.Get("AR4A");

        Assert.NotNull(route);
        Assert.Equal(2, route!.Variants.Count);
        Assert.Equal(["North", "South"], route.Variants.Select(v => v.Direction));

        var northboundExit = route.Variants[0].Points[^1].Position;
        var southboundArip = route.Variants[1].Points[0].Position;
        Assert.True(GeoMath.DistanceNm(northboundExit, southboundArip) > 20);
    }

    [Fact]
    public void MultiDirectionEntries_GiveEachDirectionDistinctPointNames()
    {
        // Both directions share one designator, so an undecorated label would map a single
        // synthetic fix name to two positions far apart.
        foreach (var route in Refueling.Where(r => r.Variants.Count > 1))
        {
            var byName = new Dictionary<string, LatLon>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in route.AllPoints)
            {
                if (byName.TryGetValue(point.Name, out var seen))
                {
                    Assert.True(GeoMath.DistanceNm(seen, point.Position) < 1.0, $"{route.Designator} reuses {point.Name} for two positions");
                    continue;
                }

                byName[point.Name] = point.Position;
            }
        }
    }

    [Fact]
    public void Anchors_PublishAnOrbitPattern()
    {
        var anchors = Refueling.Where(r => r.ArKind == MilitaryRouteArKind.Anchor).ToList();

        // AR662V is a VFR helicopter refueling area and genuinely publishes no orbit pattern; every
        // other anchor does.
        var withoutPattern = anchors.Where(a => a.Variants.All(v => v.Pattern.Count == 0)).Select(a => a.Designator);
        Assert.Equal(["AR662V"], withoutPattern);
        Assert.All(anchors.SelectMany(a => a.Variants).SelectMany(v => v.Pattern), p => Assert.Equal(MilitaryRoutePointRole.PatternCorner, p.Role));
    }

    [Fact]
    public void Ar601_CarriesItsAnchorPointAndAtcAssignedAirspace()
    {
        var route = Db.Get("AR601");

        Assert.NotNull(route);
        Assert.Equal(MilitaryRouteArKind.Anchor, route!.ArKind);
        var variant = Assert.Single(route.Variants);
        Assert.Contains(variant.Points, p => p.Role == MilitaryRoutePointRole.AnchorPoint);
        Assert.Equal(4, variant.Pattern.Count);
        // AP/1B prints a 14-vertex ATC Assigned Airspace polygon for AR601.
        Assert.True(route.AtcAssignedAirspace.Count >= 12);
        Assert.Equal(16000, route.RouteAltitude.FloorFt);
        Assert.Equal(26000, route.RouteAltitude.CeilingFt);
    }

    [Fact]
    public void EveryRefuelingEntry_HasAFlyableFirstDirection()
    {
        Assert.All(
            Refueling,
            route =>
            {
                Assert.NotEmpty(route.Variants);
                Assert.True(route.Points.Count >= 2, $"{route.Designator} has {route.Points.Count} point(s)");
                Assert.Same(route.Variants[0].Points[0], route.Points[0]);
            }
        );
    }

    [Fact]
    public void PublishedAltitudes_AreABlockWithinTheRefuelingBand()
    {
        var parsed = Refueling.Where(r => r.RouteAltitude.Kind == MilitaryRouteAltitudeKind.Block).ToList();

        // 241 of the 247 entries publish a parseable floor/ceiling pair; the remainder print prose
        // such as "FL180 and above" that carries no block.
        Assert.Equal(241, parsed.Count);
        Assert.All(
            parsed,
            route =>
            {
                Assert.True(route.RouteAltitude.FloorFt < route.RouteAltitude.CeilingFt, route.Designator);
                Assert.InRange(route.RouteAltitude.CeilingFt!.Value, 1, 60000);
            }
        );
    }

    [Fact]
    public void FiledAnchors_SelectThePublishedDirection()
    {
        TestVnasData.EnsureInitialized();
        var navDb = NavigationDatabase.Instance;
        var route = navDb.GetMilitaryRoute("AR4A");
        Assert.NotNull(route);
        Assert.Equal(2, route!.Variants.Count);

        // Filing the northbound ARIP and exit must pick North; filing the southbound pair must pick
        // South. The two directions are offset parallels, so only scoring the anchor *pair* tells
        // them apart.
        foreach (var expected in route.Variants)
        {
            var selected = MilitaryRouteExpander.SelectVariant(route, expected.Points[0].Name, expected.Points[^1].Name, navDb);

            Assert.NotNull(selected);
            Assert.Equal(expected.Direction, selected!.Direction);
        }
    }

    [Fact]
    public void Expand_TwoDirectionTrack_FliesTheDirectionItsAnchorsDescribe()
    {
        TestVnasData.EnsureInitialized();
        var navDb = NavigationDatabase.Instance;
        var route = navDb.GetMilitaryRoute("AR4A")!;
        var southbound = route.Variants[1];

        var names = MilitaryRouteExpander.Expand("AR4A", southbound.Points[0].Name, southbound.Points[^1].Name, navDb);

        Assert.Equal(southbound.Points.Select(p => p.Name), names);
    }

    [Fact]
    public void SyntheticPointNames_AreNeverReadAsFrdAnchors()
    {
        // FrdResolver.ParseFrd reads a name as an FRD when its last three or six characters are all
        // digits. Every refueling label is alphabetic or alphanumeric-with-a-leading-letter, so a
        // minted name can never collide with that rule.
        foreach (var point in Refueling.SelectMany(r => r.AllPoints))
        {
            Assert.False(point.Name[^3..].All(char.IsDigit), point.Name);
            Assert.False(point.Name.Length >= 6 && point.Name[^6..].All(char.IsDigit), point.Name);
        }
    }
}
