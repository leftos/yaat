using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Regression for the SFO ground bug (bundle S1-SFO-2, 2026-07-03): UAL194 at spot 9 on
/// taxiway T9 was cleared <c>TAXI A B1 B K A F HS 1L RWY 28L</c>, but the resolver dropped
/// A and B1 and routed <c>T9 B K A F</c>. On the recorded layout, taxiway B1 fell ~15 ft
/// short of A so they shared no junction, and <c>TryDetour</c>'s blind cost search bridged
/// via the digit-bearing <c>T9</c> (which escapes the unauthorized-letter penalty) instead
/// of the real connector <c>Q</c>.
///
/// Fixture <c>sfo-b1short.geojson</c> is the bundle's embedded layout (A/B1 disconnected).
/// The live vNAS map now connects A/B1, but this is kept as defensive coverage in case ZOA
/// reverts. With the curated <c>A ↔ B1 via Q</c> connector plus the resolver preferring a
/// clearance-honoring variant over a blind-detour bypass, the route must taxi A → Q → B1.
/// </summary>
public class SfoT9AB1ConnectorTests
{
    private readonly ITestOutputHelper _output;

    public SfoT9AB1ConnectorTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin shared NavData/sidecar singletons before layout build (CLAUDE.md singleton races).
        TestVnasData.EnsureInitialized();
    }

    private const string FixturePath = "TestData/sfo-b1short.geojson";

    // UAL194 at t=340 when "TAXI A B1 B K A F" was issued (bundle snapshot).
    private const double StartLat = 37.61986617066067;
    private const double StartLon = -122.39263533007237;
    private const double StartHeading = 348.03;

    private AirportGroundLayout? LoadLayout()
    {
        if (!File.Exists(FixturePath) || TestVnasData.NavigationDb is null)
        {
            return null;
        }

        return GeoJsonParser.Parse("SFO", File.ReadAllText(FixturePath), null);
    }

    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );

    [Fact]
    public void Sfo_ProductionData_AuthorizesQBetweenAAndB1()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        // The SFO A↔B1↔Q connector must ship in production data — fail loud if missing.
        var connectors = NavigationDatabase.Instance.AirportSidecars.GetImplicitConnectors("SFO");
        Assert.Contains(connectors, c => (c.Connector == "Q") && (c.Between.Count == 2) && c.Between.Contains("A") && c.Between.Contains("B1"));
    }

    [Fact]
    public void TaxiAB1BKAF_FromT9_RoutesViaQ_NotT9Bypass()
    {
        var layout = LoadLayout();
        if (layout is null)
        {
            _output.WriteLine("sfo-b1short.geojson or navdata not found — skipping");
            return;
        }

        // Precondition: this fixture is the disconnected variant (A/B1 share no junction).
        var ab1 = layout.Nodes.Values.Count(n => n.Edges.Any(e => e.MatchesTaxiway("A")) && n.Edges.Any(e => e.MatchesTaxiway("B1")));
        Assert.Equal(0, ab1);

        var startNode =
            layout.FindNearestNodeForTaxi(new LatLon(StartLat, StartLon), new TrueHeading(StartHeading))
            ?? layout.FindNearestNode(StartLat, StartLon);
        Assert.NotNull(startNode);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode!.Id,
            ["A", "B1", "B", "K", "A", "F"],
            out string? failReason,
            new ExplicitPathOptions
            {
                DestinationRunway = "28L",
                ExplicitHoldShorts = ["1L"],
                AirportId = "SFO",
                StartHeadingTrue = StartHeading,
                DiagnosticLog = msg => _output.WriteLine(msg),
            },
            AircraftCategory.Jet
        );

        Assert.NotNull(route);
        Assert.Null(failReason);
        _output.WriteLine("route: " + string.Join(" ", route!.Segments.Select(s => s.TaxiwayName)));

        // The real connector Q must be used and the named B1 actually traversed — not silently
        // dropped in favor of a T9 → B bypass. (B1 being traversed is itself proof the route did
        // not bypass it; a short T9 lead-in from the start spot is legitimate and not asserted on.)
        Assert.True(Traverses(route, "Q"), "route must taxi via the real A↔B1 connector Q");
        Assert.True(Traverses(route, "B1"), "route must actually traverse the cleared taxiway B1");

        // The chosen route honors the clearance with no blind-detour insertion (it threaded the
        // curated Q connector), so Run's insertion-count ranking selected it over any bypass.
        Assert.Equal(0, route.MandatoryConnectorCount);
    }

    // ----- Run variant-ranking policy (robustness) -----------------------
    // For UAL194 the curated Q variant is also the shorter route, so distance alone already picks
    // it; these pin the ranking's INDEPENDENT guarantee — a clearance-honoring variant (no blind
    // detour) wins even when it is LONGER than a bypass that dropped a named taxiway.

    private static TaxiRoute RouteWith(int mandatoryConnectors, double distanceNm)
    {
        var a = new GroundNode
        {
            Id = 0,
            Position = new LatLon(37.0, -122.0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var b = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.001, -122.0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [a, b],
            TaxiwayName = "A",
            DistanceNm = distanceNm,
        };
        return new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { Edge = edge.Directed(a, b), TaxiwayName = "A" }],
            HoldShortPoints = [],
            MandatoryConnectorCount = mandatoryConnectors,
        };
    }

    [Fact]
    public void IsBetterRoute_PrefersFewerInsertions_EvenWhenLonger()
    {
        var honoring = RouteWith(mandatoryConnectors: 0, distanceNm: 10.0);
        var bypass = RouteWith(mandatoryConnectors: 1, distanceNm: 5.0);

        Assert.True(SegmentExpander.IsBetterRoute(honoring, bypass));
        Assert.False(SegmentExpander.IsBetterRoute(bypass, honoring));
    }

    [Fact]
    public void IsBetterRoute_TieOnInsertions_PrefersShorter()
    {
        var shorter = RouteWith(mandatoryConnectors: 0, distanceNm: 5.0);
        var longer = RouteWith(mandatoryConnectors: 0, distanceNm: 10.0);

        Assert.True(SegmentExpander.IsBetterRoute(shorter, longer));
        Assert.False(SegmentExpander.IsBetterRoute(longer, shorter));
    }
}
