using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The pilot lens's intent path for one track, in screen points: the nav route's legs with a dashed leader from the symbol,
/// a diamond and label per named fix with its crossing-restriction lines, the server's shapes, and the vector tail of a
/// vectored aircraft; nothing for an aircraft with no route.
/// </summary>
public class ScopeIntentPathTests
{
    private static readonly NavigationDatabase NavDb = NavigationDatabase.ForTesting();

    private static MapViewport Viewport() =>
        new()
        {
            PixelWidth = 1000,
            PixelHeight = 800,
            CenterLat = 37.0,
            CenterLon = -122.0,
        };

    private static AircraftModel Aircraft()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Position = new LatLon(37.0, -122.0);
        ac.AssignedHeading = null;
        ac.NavRouteFixes = [];
        ac.NavRouteShapes = [];
        return ac;
    }

    private static SKPoint Screen(double lat, double lon)
    {
        (float x, float y) = Viewport().LatLonToScreen(lat, lon);
        return new SKPoint(x, y);
    }

    /// <summary><paramref name="ac"/>'s intent path through a fresh cache, as the canvas's first frame draws it.</summary>
    private static ScopeIntentPath? PathOf(AircraftModel ac, NavigationDatabase? navDb) => new ScopeIntentPathCache().PathFor(ac, navDb, Viewport());

    /// <summary>A cache that counts its route builds per callsign.</summary>
    private static ScopeIntentPathCache CountingCache(Dictionary<string, int> builds) =>
        new(
            (a, db) =>
            {
                builds[a.Callsign] = builds.GetValueOrDefault(a.Callsign) + 1;
                return ScopeIntentPaths.Routes(a, db);
            }
        );

    [Fact]
    public void Cache_KeepsAHeadingTail_AndStartsItWhereTheAircraftNowIs()
    {
        AircraftModel ac = Aircraft();
        ac.AssignedHeading = new MagneticHeading(90);
        var builds = new Dictionary<string, int>();
        ScopeIntentPathCache cache = CountingCache(builds);

        cache.PathFor(ac, NavDb, Viewport());
        ac.Position = new LatLon(37.02, -121.98);
        ScopeIntentPath moved = Assert.IsType<ScopeIntentPath>(cache.PathFor(ac, NavDb, Viewport()));

        Assert.Equal(1, builds[ac.Callsign]);
        ScopeIntentTail tail = Assert.IsType<ScopeIntentTail>(Assert.Single(moved.Segments).Tail);
        Assert.Equal(Screen(37.02, -121.98), tail.From);
    }

    [Fact]
    public void Cache_RebuildsForAnotherNavigationDatabase()
    {
        AircraftModel ac = Aircraft();
        ac.NavRouteFixes = [new NavRouteFixDto("ALPHA", 37.05, -122.0, null, null)];
        var builds = new Dictionary<string, int>();
        ScopeIntentPathCache cache = CountingCache(builds);
        var other = NavigationDatabase.ForTesting();
        Assert.NotSame(NavDb, other);

        cache.PathFor(ac, NavDb, Viewport());
        cache.PathFor(ac, other, Viewport());

        Assert.Equal(2, builds[ac.Callsign]);
    }

    [Fact]
    public void Cache_SweepDropsATrackNoFrameAskedFor()
    {
        AircraftModel a = Aircraft();
        AircraftModel b = Aircraft();
        b.Callsign = "SWA1";
        var builds = new Dictionary<string, int>();
        ScopeIntentPathCache cache = CountingCache(builds);

        cache.PathFor(a, NavDb, Viewport());
        cache.PathFor(b, NavDb, Viewport());
        cache.Sweep();
        cache.PathFor(a, NavDb, Viewport());
        cache.Sweep();
        cache.PathFor(b, NavDb, Viewport());

        Assert.Equal(1, builds[a.Callsign]);
        Assert.Equal(2, builds[b.Callsign]);
    }

    [Fact]
    public void Cache_KeysTheFixListByIdentity_AndTheScalarsByValue()
    {
        AircraftModel ac = Aircraft();
        ac.NavRouteFixes = [new NavRouteFixDto("ALPHA", 37.05, -122.0, null, null)];
        var builds = new Dictionary<string, int>();
        ScopeIntentPathCache cache = CountingCache(builds);

        cache.PathFor(ac, NavDb, Viewport());
        ac.Destination = new string(ac.Destination.ToCharArray());
        cache.PathFor(ac, NavDb, Viewport());
        Assert.Equal(1, builds[ac.Callsign]);

        // A new list with the same fixes (an update from the server) rebuilds.
        ac.NavRouteFixes = [.. ac.NavRouteFixes];
        cache.PathFor(ac, NavDb, Viewport());
        Assert.Equal(2, builds[ac.Callsign]);

        ac.ExpectedApproach = "ILS 30";
        cache.PathFor(ac, NavDb, Viewport());
        Assert.Equal(3, builds[ac.Callsign]);
    }

    [Fact]
    public void Route_DrawsItsLegs_ALeaderFromTheSymbol_AndADiamondPerNamedFix()
    {
        AircraftModel ac = Aircraft();
        List<List<NavRouteRestrictionToken>> restriction =
        [
            [new NavRouteRestrictionToken("A100", Latent: false)],
        ];
        ac.NavRouteFixes =
        [
            new NavRouteFixDto("ALPHA", 37.05, -122.0, null, restriction),
            new NavRouteFixDto("", 37.08, -121.98, null, null),
            new NavRouteFixDto("BRAVO", 37.1, -121.95, null, null),
        ];

        ScopeIntentPath path = Assert.IsType<ScopeIntentPath>(PathOf(ac, NavDb));

        ScopeIntentSegment segment = Assert.Single(path.Segments);
        Assert.Equal(Screen(37.0, -122.0), segment.LeaderFrom);
        Assert.Equal([Screen(37.05, -122.0), Screen(37.08, -121.98), Screen(37.1, -121.95)], segment.Points);
        Assert.Equal(["ALPHA", "BRAVO"], segment.Fixes.Select(f => f.Name));
        Assert.Equal(Screen(37.05, -122.0), segment.Fixes[0].At);
        Assert.Equal(
            "A100",
            Assert
                .Single(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>>>(segment.Fixes[0].Restrictions)))
                .Text
        );
        Assert.Null(segment.Fixes[1].Restrictions);
        Assert.Null(segment.Tail);
        Assert.Empty(path.Shapes);
    }

    [Fact]
    public void Shapes_DrawInScreenPoints_ThePendingHoldDashed_AndCarryTheirLabels()
    {
        AircraftModel ac = Aircraft();
        ac.NavRouteShapes =
        [
            new NavRouteShapeDto(
                NavRouteShapeKind.HoldRacetrack,
                [
                    [37.1, -122.1],
                    [37.12, -122.1],
                    [37.12, -122.05],
                ],
                ["HOLD"],
                37.11,
                -122.1
            ),
            new NavRouteShapeDto(
                NavRouteShapeKind.PendingHoldRacetrack,
                [
                    [36.9, -122.1],
                    [36.92, -122.1],
                ],
                null,
                null,
                null
            ),
        ];

        ScopeIntentPath path = Assert.IsType<ScopeIntentPath>(PathOf(ac, NavDb));

        Assert.Empty(path.Segments);
        Assert.Equal(2, path.Shapes.Count);
        Assert.Equal([Screen(37.1, -122.1), Screen(37.12, -122.1), Screen(37.12, -122.05)], path.Shapes[0].Points);
        Assert.False(path.Shapes[0].Dashed);
        Assert.Equal(Screen(37.11, -122.1), path.Shapes[0].LabelAt);
        Assert.Equal(["HOLD"], path.Shapes[0].Labels);
        Assert.True(path.Shapes[1].Dashed);
        Assert.Null(path.Shapes[1].LabelAt);
        Assert.Empty(path.Shapes[1].Labels);
    }

    [Fact]
    public void VectoredAircraft_DrawsAVectorTailFromItsSymbol_AlongItsHeading()
    {
        AircraftModel ac = Aircraft();
        ac.AssignedHeading = new MagneticHeading(90);

        ScopeIntentPath path = Assert.IsType<ScopeIntentPath>(PathOf(ac, NavDb));

        ScopeIntentSegment segment = Assert.Single(path.Segments);
        Assert.Empty(segment.Points);
        Assert.Null(segment.LeaderFrom);
        ScopeIntentTail tail = Assert.IsType<ScopeIntentTail>(segment.Tail);
        Assert.Equal(Screen(37.0, -122.0), tail.From);
        Assert.True(tail.To.X > tail.From.X + 20, "the tail does not run east");
        Assert.True(Math.Abs(tail.To.Y - tail.From.Y) < (tail.To.X - tail.From.X) / 2, "the tail is not near level");
    }

    [Fact]
    public void NoRoute_DrawsNothing() => Assert.Null(PathOf(Aircraft(), NavDb));

    [Fact]
    public void Cache_BuildsTheGeometryOnceForUnchangedInputs_AndRebuildsOnARouteChange()
    {
        AircraftModel ac = Aircraft();
        ac.NavRouteFixes = [new NavRouteFixDto("ALPHA", 37.05, -122.0, null, null)];
        var counts = new Dictionary<string, int>();
        ScopeIntentPathCache cache = CountingCache(counts);

        ScopeIntentPath first = Assert.IsType<ScopeIntentPath>(cache.PathFor(ac, NavDb, Viewport()));
        cache.Sweep();
        ScopeIntentPath second = Assert.IsType<ScopeIntentPath>(cache.PathFor(ac, NavDb, Viewport()));

        Assert.Equal(1, counts[ac.Callsign]);
        Assert.Equal(first.Segments[0].Points, second.Segments[0].Points);

        ac.NavRouteFixes = [.. ac.NavRouteFixes, new NavRouteFixDto("BRAVO", 37.1, -121.95, null, null)];
        ScopeIntentPath rerouted = Assert.IsType<ScopeIntentPath>(cache.PathFor(ac, NavDb, Viewport()));

        Assert.Equal(2, counts[ac.Callsign]);
        Assert.Equal([Screen(37.05, -122.0), Screen(37.1, -121.95)], rerouted.Segments[0].Points);
    }

    [Fact]
    public void NoNavigationDatabase_DrawsTheShapesButNoLegs()
    {
        AircraftModel ac = Aircraft();
        ac.NavRouteFixes = [new NavRouteFixDto("ALPHA", 37.05, -122.0, null, null)];
        ac.NavRouteShapes =
        [
            new NavRouteShapeDto(
                NavRouteShapeKind.OrbitCircle,
                [
                    [36.9, -122.1],
                    [36.92, -122.1],
                ],
                null,
                null,
                null
            ),
        ];

        ScopeIntentPath path = Assert.IsType<ScopeIntentPath>(PathOf(ac, null));

        Assert.Empty(path.Segments);
        Assert.Single(path.Shapes);
    }
}
