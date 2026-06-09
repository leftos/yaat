using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that the SFO L/F blocked turn keeps real traffic off the apex corner and onto the LF
/// connector — for AUTO routes and for explicit named-taxiway clearances in either direction — without
/// over-blocking straight-through traffic. Resolved sets are injected into <see cref="SearchContext"/> so
/// no live <see cref="NavigationDatabase"/> is needed; the implicit connector is supplied the same way the
/// sidecar would.
/// </summary>
public class BlockedTurnPathfinderTests
{
    private static readonly OneWayPoint L = new(37.61494338638182, -122.37339328086573, "L");
    private static readonly OneWayPoint ApexA = new(37.6161316665853, -122.37260390313256, "L");
    private static readonly OneWayPoint ApexB = new(37.616129801005414, -122.3726033797592, "F");
    private static readonly OneWayPoint F = new(37.615463060496644, -122.37101193249117, "F");

    private const int FSouthEast = 281; // a node further SE along F (past the LF/F junction #280)
    private const int LSouthWest = 324; // a node further SW along L (past the L/LF junction #325)

    private static readonly List<ImplicitConnectorEntry> Connectors = [new() { Connector = "LF", Between = ["L", "F"] }];

    private static AirportGroundLayout? LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null) : null;
    }

    private static BlockedTurnResult Blocked(AirportGroundLayout layout) =>
        BlockedTurnResolver.Resolve(layout, [new BlockedTurn([L, ApexA, ApexB, F], "SFO L/F apex")]);

    private static bool UsesTaxiway(TaxiRoute route, string name) =>
        route.Segments.Any(s => string.Equals(s.TaxiwayName, name, StringComparison.OrdinalIgnoreCase));

    private static bool TraversesArc(TaxiRoute route, IReadOnlySet<(int A, int B)> pairs) =>
        route.Segments.Any(s => pairs.Contains((s.FromNodeId, s.ToNodeId)) || pairs.Contains((s.ToNodeId, s.FromNodeId)));

    private static SearchContext Ctx(AirportGroundLayout layout, int start, int dest, string[] seq, BlockedTurnResult blocked) =>
        new SearchContext(
            layout,
            start,
            new DestinationDescriptor(dest, null, null, null, DestinationKind.Node),
            seq,
            seq.Length == 0 ? null : new HashSet<string>(["L", "F", "LF"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(),
            AircraftCategory.Jet,
            RoutePreference.FewestTurns,
            null
        )
        {
            ImplicitConnectors = Connectors,
            BlockedTurnTriples = blocked.ForbiddenTurns,
            BlockedArcMoves = blocked.ForbiddenArcMoves,
        };

    [Fact]
    public void AutoRoute_FromL_ToF_UsesTheConnector_NeverTheBlockedCorner()
    {
        var layout = LoadSfo();
        if (layout is null || !layout.Nodes.ContainsKey(FSouthEast))
        {
            return;
        }

        int start = layout.FindNearestNode(L.Lat, L.Lon)!.Id; // #325
        var blocked = Blocked(layout);

        var (route, failure) = AutoRouter.Run(Ctx(layout, start, FSouthEast, [], blocked));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.True(UsesTaxiway(route, "LF"), "AUTO must reach F via the LF connector");
        Assert.False(TraversesArc(route, blocked.HiddenArcPairs), "AUTO must not use the blocked apex corner arc");
    }

    [Fact]
    public void ExplicitTaxi_LF_RoutesViaConnector_NotTheApex()
    {
        var layout = LoadSfo();
        if (layout is null || !layout.Nodes.ContainsKey(FSouthEast))
        {
            return;
        }

        int start = layout.FindNearestNode(L.Lat, L.Lon)!.Id; // #325
        var blocked = Blocked(layout);

        var (route, failure) = SegmentExpander.Run(Ctx(layout, start, FSouthEast, ["L", "F"], blocked));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.True(UsesTaxiway(route, "LF"), "explicit L F must reroute via the connector");
        Assert.False(TraversesArc(route, blocked.HiddenArcPairs), "explicit L F must not use the blocked apex corner arc");
    }

    [Fact]
    public void ExplicitTaxi_FL_RoutesViaConnector_ReverseDirection()
    {
        var layout = LoadSfo();
        if (layout is null || !layout.Nodes.ContainsKey(FSouthEast) || !layout.Nodes.ContainsKey(LSouthWest))
        {
            return;
        }

        var blocked = Blocked(layout);

        var (route, failure) = SegmentExpander.Run(Ctx(layout, FSouthEast, LSouthWest, ["F", "L"], blocked));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.True(UsesTaxiway(route, "LF"), "explicit F L must reroute via the connector in the reverse direction");
        Assert.False(TraversesArc(route, blocked.HiddenArcPairs), "explicit F L must not use the blocked apex corner arc");
    }

    [Fact]
    public void StraightThroughF_AcrossTheApex_IsNotOverBlocked()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var blocked = Blocked(layout);
        int apex = layout.FindNearestNode(ApexA.Lat, ApexA.Lon)!.Id; // #279

        // The two F tangent cuts on opposite sides of the apex: straight-through on F (1602 ↔ 1603 across
        // #279) must still route — the turn-triple blocks only the L↔F pivot, not F-straight traffic.
        int fNw = layout.FindNearestNode(37.616322, -122.373062)!.Id; // #1602 (NW F tangent)
        int fSe = layout.FindNearestNode(37.616112, -122.372561)!.Id; // #1603 (SE F tangent)

        var (route, failure) = AutoRouter.Run(Ctx(layout, fNw, fSe, [], blocked));

        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.True(route.Segments.Any(s => s.FromNodeId == apex || s.ToNodeId == apex), "F-straight traffic must still pass through the apex node");
    }
}
