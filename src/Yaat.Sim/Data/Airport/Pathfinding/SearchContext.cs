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
        Action<string>? diagnosticLog
    )
    {
        var holdShorts = explicitHoldShortRunways is { Count: > 0 }
            ? (IReadOnlySet<string>)new HashSet<string>(explicitHoldShortRunways, StringComparer.OrdinalIgnoreCase)
            : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var authorized = BuildAuthorizedTaxiwaySet(waypointSequence);

        var destination = ResolveDestination(layout, destinationRunway, destinationParking, destinationSpot, destinationNodeId);

        // Per-airport avoided taxiways apply to AUTO routes only (empty waypoint sequence). An explicit
        // named-taxiway path (waypointSequence non-empty) is a controller instruction and is never
        // re-routed around an avoided taxiway, so AvoidMode stays Off for it.
        var avoidedTaxiways = ResolveAvoidedTaxiways(layout);
        var avoidMode = (avoidedTaxiways.Count > 0) && (waypointSequence.Count == 0) ? AvoidTaxiwayMode.HardExclude : AvoidTaxiwayMode.Off;

        return new SearchContext(layout, startNodeId, destination, waypointSequence, authorized, holdShorts, category, preference, diagnosticLog)
        {
            AvoidedTaxiways = avoidedTaxiways,
            AvoidMode = avoidMode,
        };
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
    /// Build the authorized-taxiway set from the waypoint sequence.
    /// Letter-only taxiway names (e.g., "A", "Y") become the authorization boundary.
    /// Numbered taxiways (e.g., "A1", "AY1") are excluded — they are always free.
    /// Returns null when the sequence is empty (auto-route — all taxiways allowed).
    /// </summary>
    private static IReadOnlySet<string>? BuildAuthorizedTaxiwaySet(IReadOnlyList<string> waypointSequence)
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

        return set.Count == 0 ? null : set;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is a letter-only taxiway name
    /// (e.g., "A", "B", "Y") — controllers explicitly authorize these.
    /// Numbered taxiways ("A1", "AY1", "M1") contain at least one digit.
    /// Node-reference tokens ("#1234") and runway tokens are excluded.
    /// <c>RAMP</c> is apron / parking access, not a controller-authorized lettered taxiway, so it
    /// is excluded too — otherwise RAMP edges would draw an unauthorized-taxiway cost penalty
    /// (<see cref="RouteCostFunction"/>) and "not in authorized path" warnings
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
