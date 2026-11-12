using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Where to stage a helicopter whose air taxi must not draw a runway-crossing warning.</summary>
internal static class AirTaxiStands
{
    /// <summary>
    /// The first stand or helipad, by name, from which no <paramref name="destinations"/> token's straight air-taxi
    /// path crosses a runway, so an exact result line pinned on those destinations carries no crossing warning.
    /// </summary>
    public static GroundNode FirstClearOfRunways(AirportGroundLayout layout, params string[] destinations) =>
        layout
            .Nodes.Values.Where(n => (n.Type is GroundNodeType.Parking or GroundNodeType.Helipad) && (n.Name is { Length: > 0 }))
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ThenBy(n => n.Id)
            .First(n => destinations.All(d => CrossesNoRunway(layout, n.Position, d)));

    private static bool CrossesNoRunway(AirportGroundLayout layout, LatLon start, string destination) =>
        GroundCommandHandler.TryResolveAirTaxiDestination(layout, destination, start, out GroundCommandHandler.AirTaxiDestination? resolved)
        && (GroundCommandHandler.RunwaysCrossedByAirTaxi(start, resolved, layout).Count == 0);
}
