using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for per-ARTCC avoided taxiways in the AUTO pathfinder.
///
/// Synthetic-layout tests prove the two-pass mechanics deterministically: pass 1 hard-excludes the
/// avoided taxiway (used when an alternative exists), pass 2 (soft penalty) allows it only when the
/// destination is otherwise unreachable. OAK tests prove real-data wiring: ZOA marks taxiway "S" as
/// avoid (Data/ARTCCs/ZOA/AvoidTaxiways/oak.json), and the auto-router must still reach parkings that
/// hang off S (e.g. @A) while explicit/named-taxiway searches are never re-routed.
/// </summary>
public class AvoidTaxiwayPathfinderTests
{
    public AvoidTaxiwayPathfinderTests() => TestVnasData.EnsureInitialized();

    // --- synthetic layout helpers (mirror AutoRouterTests) ---

    private static GroundNode Node(int id, double lat, double lon) =>
        new()
        {
            Id = id,
            Position = new LatLon(lat, lon),
            Type = GroundNodeType.TaxiwayIntersection,
        };

    private static void Edge(GroundNode a, GroundNode b, string twy)
    {
        var edge = new GroundEdge
        {
            Nodes = [a, b],
            TaxiwayName = twy,
            DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
        };
        a.Edges.Add(edge);
        b.Edges.Add(edge);
    }

    private static SearchContext Ctx(AirportGroundLayout layout, int from, int to, IReadOnlySet<string> avoided, AvoidTaxiwayMode mode) =>
        new SearchContext(
            layout,
            from,
            new DestinationDescriptor(to, null, null, null, DestinationKind.Node),
            [],
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AircraftCategory.Jet,
            RoutePreference.FewestTurns,
            null
        )
        {
            AvoidedTaxiways = avoided,
            AvoidMode = mode,
        };

    private static IReadOnlySet<string> AvoidSet(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static bool UsesTaxiway(TaxiRoute route, string name) =>
        route.Segments.Any(s => string.Equals(s.TaxiwayName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds a layout where destination D is reachable both via a short "X" corridor and a longer "A"
    /// detour, and destination D2 is reachable only via X (hangs off the X corridor).
    /// </summary>
    private static AirportGroundLayout TwoPathLayout(out int start, out int destBoth, out int destOnlyX)
    {
        var n0 = Node(0, 37.700, -122.200); // start
        var nx = Node(1, 37.700, -122.190); // on X
        var nd = Node(2, 37.700, -122.180); // reachable via X (short) and via A detour (long)
        var na1 = Node(3, 37.730, -122.200); // A detour
        var na2 = Node(4, 37.730, -122.180); // A detour
        var nd2 = Node(5, 37.690, -122.190); // only reachable from nx via X

        Edge(n0, nx, "X");
        Edge(nx, nd, "X");
        Edge(nx, nd2, "X");
        Edge(n0, na1, "A");
        Edge(na1, na2, "A");
        Edge(na2, nd, "A");

        var layout = new AirportGroundLayout { AirportId = "TST" };
        foreach (var n in new[] { n0, nx, nd, na1, na2, nd2 })
        {
            layout.Nodes[n.Id] = n;
        }

        start = n0.Id;
        destBoth = nd.Id;
        destOnlyX = nd2.Id;
        return layout;
    }

    // -------------------------------------------------------------------------
    // Synthetic: two-pass mechanics
    // -------------------------------------------------------------------------

    [Fact]
    public void HardExclude_UsesAlternative_WhenDestinationReachableWithoutAvoidedTaxiway()
    {
        var layout = TwoPathLayout(out int start, out int destBoth, out _);

        var (route, failure) = AutoRouter.Run(Ctx(layout, start, destBoth, AvoidSet("X"), AvoidTaxiwayMode.HardExclude));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.False(UsesTaxiway(route, "X"), "Pass 1 must avoid X when the longer A detour reaches the destination.");
        Assert.True(UsesTaxiway(route, "A"));
    }

    [Fact]
    public void HardExclude_ReturnsNull_WhenDestinationReachableOnlyViaAvoidedTaxiway()
    {
        var layout = TwoPathLayout(out int start, out _, out int destOnlyX);

        var (route, failure) = AutoRouter.Run(Ctx(layout, start, destOnlyX, AvoidSet("X"), AvoidTaxiwayMode.HardExclude));

        Assert.Null(route);
        Assert.NotNull(failure);
    }

    [Fact]
    public void SoftPenalty_ReachesDestination_OnlyViaAvoidedTaxiway()
    {
        var layout = TwoPathLayout(out int start, out _, out int destOnlyX);

        var (route, failure) = AutoRouter.Run(Ctx(layout, start, destOnlyX, AvoidSet("X"), AvoidTaxiwayMode.SoftPenalty));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.True(UsesTaxiway(route, "X"), "Pass 2 (soft penalty) must still reach a destination only reachable via X.");
    }

    // -------------------------------------------------------------------------
    // OAK real-data wiring and behavior
    // -------------------------------------------------------------------------

    private static AirportGroundLayout? LoadOak()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null) : null;
    }

    [Fact]
    public void Oak_ConfiguresTaxiwayS_AsAvoided()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var avoided = NavigationDatabase.Instance.AvoidTaxiways.GetAvoidedTaxiways("OAK");
        Assert.Contains("S", avoided);
        // Same set resolves via the ICAO form.
        Assert.Contains("S", NavigationDatabase.Instance.AvoidTaxiways.GetAvoidedTaxiways("KOAK"));
    }

    [Fact]
    public void Oak_Compile_AutoMode_EnablesHardExclude_ExplicitMode_Disables()
    {
        var layout = LoadOak();
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ga1 = layout.FindParkingByName("GA1");
        var dest = layout.FindParkingByName("A");
        Assert.NotNull(ga1);
        Assert.NotNull(dest);

        var auto = SearchContext.Compile(
            layout,
            ga1.Id,
            [],
            null,
            null,
            null,
            dest.Id,
            null,
            AircraftCategory.Jet,
            RoutePreference.FewestTurns,
            null
        );
        Assert.Equal(AvoidTaxiwayMode.HardExclude, auto.AvoidMode);
        Assert.Contains("S", auto.AvoidedTaxiways);

        // A controller-named (explicit) sequence is never re-routed around an avoided taxiway.
        var explicitCtx = SearchContext.Compile(layout, ga1.Id, ["C", "B"], null, null, null, dest.Id, null, AircraftCategory.Jet, null, null);
        Assert.Equal(AvoidTaxiwayMode.Off, explicitCtx.AvoidMode);
    }

    [Fact]
    public void Oak_HardExcludeAlone_CannotReachParkingOnlyOffS()
    {
        var layout = LoadOak();
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ga1 = layout.FindParkingByName("GA1");
        var parkingA = layout.FindParkingByName("A");
        Assert.NotNull(ga1);
        Assert.NotNull(parkingA);

        // Parking A hangs off taxiway S — with S hard-excluded (pass 1), it must be unreachable.
        var (route, failure) = AutoRouter.Run(Ctx(layout, ga1.Id, parkingA.Id, AvoidSet("S"), AvoidTaxiwayMode.HardExclude));

        Assert.Null(route);
        Assert.NotNull(failure);
    }

    [Fact]
    public void Oak_AutoRoute_ReachesParkingOnlyOffS_ViaTwoPassFallback()
    {
        var layout = LoadOak();
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ga1 = layout.FindParkingByName("GA1");
        var parkingA = layout.FindParkingByName("A");
        Assert.NotNull(ga1);
        Assert.NotNull(parkingA);

        // FindRoute runs the two-pass: pass 1 (hard-exclude S) fails, pass 2 reaches A via S.
        var route = TaxiPathfinder.FindRoute(layout, ga1.Id, parkingA.Id, AircraftCategory.Jet);

        Assert.NotNull(route);
        Assert.True(UsesTaxiway(route, "S"), "A is only reachable via S, so the fallback route must use S.");
    }
}
