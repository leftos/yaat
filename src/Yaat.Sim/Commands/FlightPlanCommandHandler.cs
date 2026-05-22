using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

/// <summary>
/// Handles flight-plan amendment commands (currently APT/DEST). Resolves user-typed
/// airport identifiers through <see cref="NavigationDatabase.TryResolveAirport"/>,
/// stores the canonical ICAO form on the aircraft's flight plan, and rejects
/// unknown airports with a clear error.
/// </summary>
internal static class FlightPlanCommandHandler
{
    /// <summary>
    /// Validates the user-supplied airport identifier, normalizes it to the canonical
    /// ICAO form, and writes it to <c>aircraft.FlightPlan.Destination</c>. Returns a
    /// failure <see cref="CommandResult"/> (without mutating the aircraft) if the input
    /// is empty or does not match any known airport.
    /// </summary>
    internal static CommandResult TryChangeDestination(AircraftState aircraft, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new CommandResult(false, "Change destination requires an airport code");
        }

        var navDb = NavigationDatabase.Instance;
        if (!navDb.TryResolveAirport(input, out var canonical))
        {
            return new CommandResult(false, $"Unknown airport {input.Trim().ToUpperInvariant()}");
        }

        string? previous = aircraft.FlightPlan.Destination;
        if (!string.IsNullOrEmpty(previous) && !previous.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            ApproachCommandHandler.ClearArrivalProcedureState(aircraft);
        }

        aircraft.FlightPlan.Destination = canonical;
        return new CommandResult(true, $"Destination changed to {canonical}");
    }
}
