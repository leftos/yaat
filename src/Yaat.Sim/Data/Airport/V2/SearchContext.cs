namespace Yaat.Sim.Data.Airport.V2;

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

        return new SearchContext(layout, startNodeId, destination, waypointSequence, authorized, holdShorts, category, preference, diagnosticLog);
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
    /// </summary>
    public static bool IsLetterOnlyTaxiway(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '#')
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
            int? resolvedId = layout.FindParkingByName(parkingName)?.Id ?? layout.FindHelipadByName(parkingName)?.Id;

            DestinationKind kind =
                parkingName.Length > 0 && char.IsLetter(parkingName[0]) && parkingName.Contains('H')
                    ? DestinationKind.Helipad
                    : DestinationKind.Parking;

            return new DestinationDescriptor(resolvedId, null, parkingName, null, kind);
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
