using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Commands;

/// <summary>
/// How an aircraft parked on a stand leaves it: the departure of the stand its <see cref="AircraftGroundOps.ParkingSpot"/>
/// names, while it is at that stand (<see cref="AtParkingPhase"/>): <see cref="StandDeparture.PushBack"/>,
/// <see cref="StandDeparture.TaxiOut"/> or <see cref="StandDeparture.Either"/>. A parking stand reads
/// <see cref="StandDepartures.StandDepartureOf"/> (the layout's geometric answer, overridden by the airport sidecar's area
/// rules and per-name entries); a helipad is always <see cref="StandDeparture.TaxiOut"/>. The client's menus hide the push
/// entries for a taxi-out stand, and a live push off one carries an RPO note; an either stand shows them and a push off it
/// carries none.
/// </summary>
public static class ParkedStandDeparture
{
    /// <summary>
    /// <see cref="Of(AircraftState, AirportGroundLayout?, AirportSidecarCatalog)"/> against the sidecar catalog of the
    /// <see cref="NavigationDatabase"/> current in this async flow; with no database initialized, no override applies.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="layout">The airport layout the aircraft is on, or null when it has none.</param>
    /// <returns>The departure of the stand the aircraft is parked on, or null when it is not parked on a known stand.</returns>
    public static StandDeparture? Of(AircraftState aircraft, AirportGroundLayout? layout) =>
        Of(aircraft, layout, NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty);

    /// <summary>
    /// The departure of the stand the aircraft is parked on. Null when the aircraft is not at its stand (any phase but
    /// <see cref="AtParkingPhase"/>), when there is no layout, and when its parking spot names no parking stand or
    /// helipad of the layout (a ramp spot, or an unknown name).
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="layout">The airport layout the aircraft is on, or null when it has none.</param>
    /// <param name="sidecars">The airport sidecars a stand's overrides and area rules are read from.</param>
    /// <returns>The departure of the stand the aircraft is parked on, or null.</returns>
    public static StandDeparture? Of(AircraftState aircraft, AirportGroundLayout? layout, AirportSidecarCatalog sidecars)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(sidecars);
        if ((aircraft.Phases?.CurrentPhase is not AtParkingPhase) || (layout is null) || (aircraft.Ground.ParkingSpot is not { Length: > 0 } name))
        {
            return null;
        }

        if (layout.FindParkingByName(name) is { } stand)
        {
            return StandDepartures.StandDepartureOf(layout, stand, sidecars);
        }

        return layout.FindHelipadByName(name) is null ? null : StandDeparture.TaxiOut;
    }
}
