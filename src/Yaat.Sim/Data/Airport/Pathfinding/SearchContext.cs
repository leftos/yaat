namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// What the route must reach at its terminal end.
/// </summary>
public enum DestinationKind
{
    Node,
    Runway,
    Parking,
    Spot,
    EndOfLastTaxiway,
    Helipad,
}

/// <summary>
/// Describes the terminal target of a route.
/// </summary>
public sealed record DestinationDescriptor(int? TargetNodeId, string? RunwayId, string? ParkingName, string? SpotName, DestinationKind Kind);

/// <summary>
/// Records a connector the pathfinder had to insert between two consecutive cleared taxiways
/// that have no direct edge or arc joining them (verified mandatory). Surfaced to the controller
/// as an informative route notification rather than an "unauthorized taxiway" warning.
/// </summary>
public sealed record ConnectorInsertion(string FromTaxiway, string ToTaxiway, IReadOnlyList<string> Connectors);

/// <summary>
/// Compiled context for one pathfinding call. Immutable after construction.
/// </summary>
public sealed record SearchContext(
    AirportGroundLayout Layout,
    int StartNodeId,
    DestinationDescriptor Destination,
    IReadOnlyList<string> WaypointSequence,
    IReadOnlySet<string>? AuthorizedTaxiways,
    IReadOnlySet<string> ExplicitHoldShorts,
    AircraftCategory Category,
    RoutePreference? Preference,
    Action<string>? DiagnosticLog
)
{
    private static readonly IReadOnlySet<string> EmptyAvoidedTaxiways = new HashSet<string>();
    private static readonly IReadOnlySet<(int, int)> EmptyForbiddenMoves = new HashSet<(int, int)>();
    private static readonly IReadOnlySet<(int, int, int)> EmptyBlockedTurns = new HashSet<(int, int, int)>();

    /// <summary>
    /// Taxiway names the AUTO router should avoid at this airport, resolved from
    /// <see cref="NavigationDatabase.AirportSidecars"/> keyed by <c>Layout.AirportId</c>. Empty when the
    /// feature is off or the airport is unconfigured. Honoured only by <see cref="AutoRouter"/> /
    /// <see cref="RouteCostFunction"/> in auto mode; <see cref="SegmentExpander"/> (explicit named-taxiway
    /// paths) never reads it, so controller <c>TAXI</c> commands are unaffected.
    /// </summary>
    public IReadOnlySet<string> AvoidedTaxiways { get; init; } = EmptyAvoidedTaxiways;

    /// <summary>How the avoided taxiways are enforced for this search; see <see cref="AvoidTaxiwayMode"/>.</summary>
    public AvoidTaxiwayMode AvoidMode { get; init; } = AvoidTaxiwayMode.Off;

    /// <summary>
    /// Directed node moves <c>(fromId, toId)</c> forbidden by this airport's one-way taxiway constraints,
    /// resolved from <see cref="NavigationDatabase.AirportSidecars"/> against <c>Layout</c>. Empty when the
    /// airport has none. See <see cref="OneWayResolver"/>.
    /// </summary>
    public IReadOnlySet<(int From, int To)> ForbiddenOneWayMoves { get; init; } = EmptyForbiddenMoves;

    /// <summary>How one-way constraints are enforced for this search; see <see cref="Pathfinding.OneWayMode"/>.</summary>
    public OneWayMode OneWayMode { get; init; } = OneWayMode.Off;

    /// <summary>
    /// True when traversing <paramref name="fromId"/> → <paramref name="toId"/> is hard-forbidden by an
    /// active one-way constraint. Only <see cref="Pathfinding.OneWayMode.HardExclude"/> (auto-routes)
    /// hard-blocks; in <see cref="Pathfinding.OneWayMode.Warn"/> the move is allowed and surfaced as a
    /// route warning by <see cref="RouteMaterialiser"/> instead.
    /// </summary>
    public bool IsForbiddenMove(int fromId, int toId) => OneWayMode == OneWayMode.HardExclude && ForbiddenOneWayMoves.Contains((fromId, toId));

    /// <summary>
    /// Directed pivot turns <c>(prev, apex, next)</c> a blocked turn forbids — the sharp straight pivot
    /// through a surviving intersection apex. Resolved from <see cref="NavigationDatabase.AirportSidecars"/>
    /// against <c>Layout</c>; empty when the airport has none. See <see cref="BlockedTurnResolver"/>.
    /// </summary>
    public IReadOnlySet<(int Prev, int Apex, int Next)> BlockedTurnTriples { get; init; } = EmptyBlockedTurns;

    /// <summary>
    /// Directed 2-node moves over a blocked turn's fillet corner arc (the smooth bypass of the apex).
    /// Resolved alongside <see cref="BlockedTurnTriples"/>; empty when the airport has none.
    /// </summary>
    public IReadOnlySet<(int From, int To)> BlockedArcMoves { get; init; } = EmptyForbiddenMoves;

    /// <summary>
    /// True when arriving at <paramref name="apexId"/> from <paramref name="prevId"/> and departing to
    /// <paramref name="nextId"/> is a blocked turn. Hard for both AUTO and explicit routes — unlike
    /// one-way constraints, a blocked turn has no painted line, so it is never warned-through.
    /// </summary>
    public bool IsBlockedTurn(int prevId, int apexId, int nextId) => BlockedTurnTriples.Contains((prevId, apexId, nextId));

    /// <summary>True when traversing the blocked-turn corner arc <paramref name="fromId"/> → <paramref name="toId"/> is forbidden (hard, both route kinds).</summary>
    public bool IsBlockedArcMove(int fromId, int toId) => BlockedArcMoves.Contains((fromId, toId));

    /// <summary>
    /// Canonical centerline names (<c>"RWY28R/10L"</c>) of runways this route may travel ALONG:
    /// every runway the controller named as a path waypoint, plus any runway whose centerline passes
    /// through the start node (a landed or lined-up aircraft must be able to taxi off the surface it
    /// is standing on). For every other runway, a search may keep at most
    /// <see cref="MaxUnclearedCenterlineRunNm"/> of consecutive centerline pavement — enough to
    /// CROSS a runway whose crossing is stitched through centerline nodes (MIA taxiway S over
    /// 12/30), never enough to back-taxi one (OAK "TAXI C D @GA1" back-taxied all of 10L).
    /// </summary>
    public IReadOnlySet<string> AllowedCenterlineNames { get; init; } = EmptyAvoidedTaxiways;

    /// <summary>
    /// Longest consecutive along-runway run permitted on a runway the route is not cleared onto —
    /// generous for a perpendicular-to-diagonal crossing (~900 ft), far below any real back-taxi.
    /// </summary>
    private const double MaxUnclearedCenterlineRunNm = 0.15;

    /// <summary>True when <paramref name="edge"/> is a single along-runway hop the route may never take.</summary>
    public bool IsForbiddenCenterlineEdge(IGroundEdge edge) => IsUnclearedCenterline(edge) && (edge.DistanceNm > MaxUnclearedCenterlineRunNm);

    /// <summary>
    /// True when extending <paramref name="current"/> with <paramref name="edge"/> would run the
    /// route along an uncleared runway's pavement for longer than a crossing needs.
    /// </summary>
    public bool IsForbiddenCenterlineMove(PartialRoute current, IGroundEdge edge)
    {
        if (!IsUnclearedCenterline(edge))
        {
            return false;
        }

        double runNm = edge.DistanceNm;
        for (var p = current; p is { LastEdge: { } prevEdge }; p = p.Previous)
        {
            if (!prevEdge.IsRunwayCenterline || !prevEdge.SharesTaxiway(edge))
            {
                break;
            }

            runNm += prevEdge.DistanceNm;
        }

        return runNm > MaxUnclearedCenterlineRunNm;
    }

    private bool IsUnclearedCenterline(IGroundEdge edge)
    {
        if (!edge.IsRunwayCenterline)
        {
            return false;
        }

        foreach (string allowed in AllowedCenterlineNames)
        {
            if (edge.MatchesTaxiway(allowed))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the edge sequence contains an uncleared centerline run that ENTERS and EXITS on
    /// the SAME side of the runway. A genuine crossing comes out the opposite side; an
    /// enter-here-exit-there hop between two exits on one side is travel ALONG the runway that the
    /// per-run length cap alone cannot catch (OAK: onto 10L at G, off at the adjacent E exit).
    /// Checked on a candidate route rather than per-expansion — side depends on how the run was
    /// entered, and folding that history into A* admissibility would poison the
    /// (node, bearing-bucket) closed set with path-dependent dead ends.
    /// </summary>
    public bool HasSameSideCenterlineRun(IReadOnlyList<DirectionalEdge> edges)
    {
        for (int i = 0; i < edges.Count; i++)
        {
            if (!IsUnclearedCenterline(edges[i].Edge))
            {
                continue;
            }

            var runEdge = edges[i].Edge;

            // Extend over everything sharing the runway name — including the flanking junction
            // arcs ("G - RWY28R/10L"), whose outer endpoints are the off-runway entry/exit nodes.
            int a = i;
            while (a > 0 && edges[a - 1].Edge.SharesTaxiway(runEdge))
            {
                a--;
            }

            int b = i;
            while (b < edges.Count - 1 && edges[b + 1].Edge.SharesTaxiway(runEdge))
            {
                b++;
            }

            var axisA = runEdge.Nodes[0].Position;
            var axisB = runEdge.Nodes[1].Position;
            double entrySide = SideOfLine(axisA, axisB, edges[a].FromNode.Position);
            double exitSide = SideOfLine(axisA, axisB, edges[b].ToNode.Position);
            if ((entrySide * exitSide) > 0)
            {
                return true;
            }

            i = b;
        }

        return false;
    }

    /// <summary>Signed side of <paramref name="p"/> relative to the line A→B (local equirectangular cross product).</summary>
    private static double SideOfLine(LatLon a, LatLon b, LatLon p)
    {
        double cosLat = Math.Cos(a.Lat * Math.PI / 180.0);
        double abX = (b.Lon - a.Lon) * cosLat;
        double abY = b.Lat - a.Lat;
        double apX = (p.Lon - a.Lon) * cosLat;
        double apY = p.Lat - a.Lat;
        return (abX * apY) - (abY * apX);
    }

    /// <summary>
    /// The airport's implicit connectors (e.g. <c>LF</c> between <c>L</c> and <c>F</c>), resolved from
    /// <see cref="NavigationDatabase.AirportSidecars"/>. Used both to authorize a connector contextually
    /// and to let an explicit named-taxiway transition prefer the painted connector over crossing at the
    /// taxiways' shared apex. Empty when the airport has none.
    /// </summary>
    public IReadOnlyList<ImplicitConnectorEntry> ImplicitConnectors { get; init; } = [];

    /// <summary>The connector taxiway bridging <paramref name="fromTaxiway"/> and <paramref name="toTaxiway"/> (unordered), or null when none.</summary>
    public string? GetImplicitConnectorName(string fromTaxiway, string toTaxiway)
    {
        foreach (var connector in ImplicitConnectors)
        {
            if (connector.Between.Count == 2 && PairMatches(connector.Between[0], connector.Between[1], fromTaxiway, toTaxiway))
            {
                return connector.Connector;
            }
        }

        return null;
    }

    /// <summary>
    /// Per-taxiway turn-direction hints (issue #172 W7), index-aligned with <see cref="WaypointSequence"/>:
    /// entry <c>i</c> is the turn the aircraft should make onto <c>WaypointSequence[i]</c> (null = no hint).
    /// Null when no token carries a hint. Read only by <see cref="SegmentExpander"/> junction selection.
    /// </summary>
    public IReadOnlyList<TurnDirection?>? WaypointTurnHints { get; init; }

    /// <summary>
    /// The aircraft's current true heading in degrees, the turn reference for a hint on the first
    /// taxiway. Null when unknown or irrelevant (auto routes, mid-route-only hints).
    /// </summary>
    public double? StartHeadingTrue { get; init; }

    /// <summary>
    /// Sink for advisories the explicit resolver appends while committing the route — a
    /// <c>&gt;</c>/<c>&lt;</c> turn hint it could not honor at the committed junction, or a bare final
    /// taxiway with no onward direction that makes the route hold at the transition junction. Mutable so
    /// the resolver records into it as it commits each transition; <see cref="RouteMaterialiser"/> copies
    /// the entries onto the route's warnings so the controller's TAXI echo reports them.
    /// One list per search (shared across <c>with</c> copies); only the top-level resolution records.
    /// </summary>
    public List<string> ResolutionAdvisories { get; init; } = [];

    /// <summary>
    /// Build a <see cref="SearchContext"/> from parsed command inputs.
    /// Resolves destination token to a node id, assembles authorized-taxiway set,
    /// and reads category limits. Pure — does not mutate the layout.
    /// </summary>
    public static SearchContext Compile(
        AirportGroundLayout layout,
        int startNodeId,
        IReadOnlyList<string> waypointSequence,
        string? destinationRunway,
        string? destinationParking,
        string? destinationSpot,
        int? destinationNodeId,
        IReadOnlyList<string>? explicitHoldShortRunways,
        AircraftCategory category,
        RoutePreference? preference,
        Action<string>? diagnosticLog,
        IReadOnlyList<TurnDirection?>? waypointTurnHints,
        double? startHeadingTrue
    )
    {
        var holdShorts = explicitHoldShortRunways is { Count: > 0 }
            ? (IReadOnlySet<string>)new HashSet<string>(explicitHoldShortRunways, StringComparer.OrdinalIgnoreCase)
            : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var implicitConnectors = ResolveImplicitConnectors(layout);
        var authorized = BuildAuthorizedTaxiwaySet(waypointSequence, implicitConnectors);

        var destination = ResolveDestination(layout, destinationRunway, destinationParking, destinationSpot, destinationNodeId);

        // Per-airport avoided taxiways apply to AUTO routes only (empty waypoint sequence). An explicit
        // named-taxiway path (waypointSequence non-empty) is a controller instruction and is never
        // re-routed around an avoided taxiway, so AvoidMode stays Off for it.
        var avoidedTaxiways = ResolveAvoidedTaxiways(layout);
        var avoidMode = (avoidedTaxiways.Count > 0) && (waypointSequence.Count == 0) ? AvoidTaxiwayMode.HardExclude : AvoidTaxiwayMode.Off;

        // One-way constraints hard-exclude the wrong direction on auto routes; an explicit named-taxiway
        // path (waypointSequence non-empty) is allowed to traverse the wrong way but is flagged with a
        // warning by RouteMaterialiser.
        var forbiddenOneWay = ResolveOneWayMoves(layout);
        var oneWayMode =
            forbiddenOneWay.Count == 0 ? OneWayMode.Off
            : waypointSequence.Count == 0 ? OneWayMode.HardExclude
            : OneWayMode.Warn;

        // Blocked turns are hard for AUTO and explicit alike (no painted line at the apex), so they are
        // resolved unconditionally — there is no warn mode and no waypoint-sequence gate.
        var blocked = ResolveBlockedTurns(layout);

        // Along-runway travel is admissible only for runways the controller named in the path, plus
        // the runway the aircraft is already standing on (post-landing / lined-up starts).
        var allowedCenterlines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in waypointSequence)
        {
            if (layout.TryGetRunwayCenterlineName(token, out string? centerlineName))
            {
                allowedCenterlines.Add(centerlineName);
            }
            else if (token.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                allowedCenterlines.Add(token);
            }
        }

        if (layout.Nodes.TryGetValue(startNodeId, out var startNode))
        {
            foreach (var edge in startNode.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    allowedCenterlines.Add(RouteCostFunction.ResolveTaxiwayName(edge, startNodeId));
                }
            }
        }

        return new SearchContext(layout, startNodeId, destination, waypointSequence, authorized, holdShorts, category, preference, diagnosticLog)
        {
            AvoidedTaxiways = avoidedTaxiways,
            AvoidMode = avoidMode,
            ForbiddenOneWayMoves = forbiddenOneWay,
            OneWayMode = oneWayMode,
            BlockedTurnTriples = blocked.ForbiddenTurns,
            BlockedArcMoves = blocked.ForbiddenArcMoves,
            ImplicitConnectors = implicitConnectors,
            WaypointTurnHints = waypointTurnHints,
            StartHeadingTrue = startHeadingTrue,
            AllowedCenterlineNames = allowedCenterlines,
        };
    }

    /// <summary>
    /// Resolves the forbidden directed moves for <paramref name="layout"/>'s one-way constraints via
    /// <see cref="OneWayResolver"/> (per-layout cached). Empty when no database is initialized or the
    /// airport is unconfigured.
    /// </summary>
    private static IReadOnlySet<(int, int)> ResolveOneWayMoves(AirportGroundLayout layout)
    {
        return NavigationDatabase.InstanceOrNull is null ? EmptyForbiddenMoves : OneWayResolver.GetForbiddenMoves(layout);
    }

    /// <summary>
    /// Resolves the blocked turns for <paramref name="layout"/> via <see cref="BlockedTurnResolver"/>
    /// (per-layout cached). Empty when no database is initialized or the airport is unconfigured.
    /// </summary>
    private static BlockedTurnResult ResolveBlockedTurns(AirportGroundLayout layout)
    {
        return NavigationDatabase.InstanceOrNull is null ? BlockedTurnResult.Empty : BlockedTurnResolver.GetBlocked(layout);
    }

    /// <summary>
    /// Looks up the avoided-taxiway set for <paramref name="layout"/>'s airport from the global
    /// <see cref="NavigationDatabase"/>. Best-effort: returns an empty set when no database is
    /// initialized (e.g. synthetic-layout unit tests) or the airport is unconfigured. Reads a
    /// process-global catalog and mutates nothing — the layout is untouched.
    /// </summary>
    private static IReadOnlySet<string> ResolveAvoidedTaxiways(AirportGroundLayout layout)
    {
        var db = NavigationDatabase.InstanceOrNull;
        return db is null ? EmptyAvoidedTaxiways : db.AirportSidecars.GetAvoidedTaxiways(layout.AirportId);
    }

    /// <summary>
    /// Looks up the implicitly-allowed named connectors for <paramref name="layout"/>'s airport from the
    /// global <see cref="NavigationDatabase"/>. Best-effort: empty when no database is initialized or the
    /// airport is unconfigured.
    /// </summary>
    private static IReadOnlyList<ImplicitConnectorEntry> ResolveImplicitConnectors(AirportGroundLayout layout)
    {
        var db = NavigationDatabase.InstanceOrNull;
        return db is null ? [] : db.AirportSidecars.GetImplicitConnectors(layout.AirportId);
    }

    /// <summary>
    /// Build the authorized-taxiway set from the waypoint sequence.
    /// Letter-only taxiway names (e.g., "A", "Y") become the authorization boundary.
    /// Numbered taxiways (e.g., "A1", "AY1") are excluded — they are always free.
    /// An implicit connector (e.g. "LF") is added when the sequence places its two
    /// <c>between</c> taxiways adjacent (unordered) — so it is authorized for "L F" but not "L A F".
    /// Returns null when the sequence is empty (auto-route — all taxiways allowed).
    /// </summary>
    internal static IReadOnlySet<string>? BuildAuthorizedTaxiwaySet(
        IReadOnlyList<string> waypointSequence,
        IReadOnlyList<ImplicitConnectorEntry> implicitConnectors
    )
    {
        if (waypointSequence.Count == 0)
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in waypointSequence)
        {
            if (IsLetterOnlyTaxiway(token))
            {
                set.Add(token);
            }
        }

        AddContextualConnectors(waypointSequence, implicitConnectors, set);

        return set.Count == 0 ? null : set;
    }

    private static void AddContextualConnectors(
        IReadOnlyList<string> waypointSequence,
        IReadOnlyList<ImplicitConnectorEntry> implicitConnectors,
        HashSet<string> set
    )
    {
        if (implicitConnectors.Count == 0)
        {
            return;
        }

        for (int i = 0; i + 1 < waypointSequence.Count; i++)
        {
            string a = waypointSequence[i];
            string b = waypointSequence[i + 1];
            foreach (var connector in implicitConnectors)
            {
                if (connector.Between.Count == 2 && PairMatches(connector.Between[0], connector.Between[1], a, b))
                {
                    set.Add(connector.Connector);
                }
            }
        }
    }

    private static bool PairMatches(string x, string y, string a, string b)
    {
        bool forward = x.Equals(a, StringComparison.OrdinalIgnoreCase) && y.Equals(b, StringComparison.OrdinalIgnoreCase);
        bool reverse = x.Equals(b, StringComparison.OrdinalIgnoreCase) && y.Equals(a, StringComparison.OrdinalIgnoreCase);
        return forward || reverse;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is a letter-only taxiway name
    /// (e.g., "A", "B", "Y") — controllers explicitly authorize these.
    /// Numbered taxiways ("A1", "AY1", "M1") contain at least one digit.
    /// Node-reference tokens ("#1234") and runway tokens are excluded.
    /// <c>RAMP</c> is apron / parking access, not a controller-authorized lettered taxiway, so it
    /// is excluded too — otherwise RAMP edges would draw an unauthorized-taxiway cost penalty
    /// (<see cref="RouteCostFunction"/>) and "not in the route issued" warnings
    /// (<see cref="RouteMaterialiser"/>) even though apron access is always permitted.
    /// </summary>
    public static bool IsLetterOnlyTaxiway(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '#')
        {
            return false;
        }

        if (name.Equals("RAMP", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (char.IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static DestinationDescriptor ResolveDestination(
        AirportGroundLayout layout,
        string? runwayId,
        string? parkingName,
        string? spotName,
        int? nodeId
    )
    {
        if (runwayId is not null)
        {
            return new DestinationDescriptor(null, runwayId, null, null, DestinationKind.Runway);
        }

        if (parkingName is not null)
        {
            // Try helipad first, then parking — matches AirportGroundLayout.FindParkingByName conventions
            // and lets the node's actual GroundNodeType drive DestinationKind classification.
            var helipadNode = layout.FindHelipadByName(parkingName);
            if (helipadNode is not null)
            {
                return new DestinationDescriptor(helipadNode.Id, null, parkingName, null, DestinationKind.Helipad);
            }

            var parkingNode = layout.FindParkingByName(parkingName);
            return new DestinationDescriptor(parkingNode?.Id, null, parkingName, null, DestinationKind.Parking);
        }

        if (spotName is not null)
        {
            int? resolvedId = layout.FindSpotNodeByName(spotName)?.Id;
            return new DestinationDescriptor(resolvedId, null, null, spotName, DestinationKind.Spot);
        }

        if (nodeId is not null)
        {
            return new DestinationDescriptor(nodeId, null, null, null, DestinationKind.Node);
        }

        return new DestinationDescriptor(null, null, null, null, DestinationKind.EndOfLastTaxiway);
    }
}
