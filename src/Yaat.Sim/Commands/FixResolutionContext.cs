namespace Yaat.Sim.Commands;

/// <summary>
/// The airports a command-argument fix lookup may fall back to when NavData lacks the name: the destination's CIFP
/// terminal waypoints first, then the departure's. <see cref="None"/> (the default) is the answer for a parse with no
/// aircraft, and resolves exactly what <see cref="Data.NavigationDatabase.GetFixPosition(string)"/> resolves.
/// </summary>
/// <param name="Destination">The aircraft's destination airport, FAA or ICAO id; null or empty for none.</param>
/// <param name="Departure">The aircraft's departure airport, FAA or ICAO id; null or empty for none.</param>
public readonly record struct FixResolutionContext(string? Destination, string? Departure)
{
    /// <summary>No airport context: NavData (and the military route points) only.</summary>
    public static FixResolutionContext None => default;

    /// <summary>The context of an aircraft's flight plan, or <see cref="None"/> when there is no flight plan.</summary>
    public static FixResolutionContext From(AircraftFlightPlan? flightPlan) =>
        flightPlan is null ? None : new FixResolutionContext(flightPlan.Destination, flightPlan.Departure);
}
