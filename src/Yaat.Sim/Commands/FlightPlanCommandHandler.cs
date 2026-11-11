using Yaat.Sim.Data;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Pilot;

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
    /// ICAO form, and writes it to <c>aircraft.FlightPlan.Destination</c>. A real change of
    /// airport also clears the arrival state tied to the old one, cancelling an approach or
    /// pattern flown to a runway there. Returns a failure <see cref="CommandResult"/>
    /// (without mutating the aircraft) if the input is empty or does not match any known airport.
    /// </summary>
    internal static CommandResult TryChangeDestination(AircraftState aircraft, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new CommandResult(false, "Change destination requires an airport code");
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        if (!navDb.TryResolveAirport(input, out string? canonical))
        {
            return new CommandResult(false, $"Unknown airport {input.Trim().ToUpperInvariant()}");
        }

        // On the clearance-limit direct, or holding at the limit, when the airport changes: the pilot turns direct to the new airport.
        bool flyingClearanceLimitDirect = (aircraft.Targets.NavigationRoute.Count > 0) && !aircraft.Targets.HasRouteToFly;
        bool inClearanceLimitHold = aircraft.Phases?.CurrentPhase is HoldingPatternPhase { IsClearanceLimitHold: true };
        string? previous = aircraft.FlightPlan.Destination;
        bool newAirport = !string.IsNullOrEmpty(previous) && !previous.Equals(canonical, StringComparison.OrdinalIgnoreCase);
        if (newAirport)
        {
            ApproachCommandHandler.ClearArrivalProcedureState(aircraft, canonical, $"APT {NavigationDatabase.NormalizeAirport(canonical)}");
        }

        aircraft.FlightPlan.Destination = canonical;
        aircraft.ClearedDestination = canonical;
        FollowTheNewClearanceLimit(aircraft, canonical, newAirport, flyingClearanceLimitDirect, inClearanceLimitHold);
        return new CommandResult(true, $"Destination changed to {canonical}");
    }

    /// <summary>
    /// The clearance-limit part of a destination change: a pilot on the clearance-limit direct turns direct to
    /// <paramref name="canonical"/>, and so does one holding at the limit when the airport changed. A new airport is the
    /// further clearance the pilot asked for, so an open request for it is answered.
    /// </summary>
    private static void FollowTheNewClearanceLimit(
        AircraftState aircraft,
        string canonical,
        bool newAirport,
        bool flyingClearanceLimitDirect,
        bool inClearanceLimitHold
    )
    {
        if (newAirport && (flyingClearanceLimitDirect || inClearanceLimitHold))
        {
            AnswerFurtherClearanceRequest(aircraft);
        }

        bool turnsDirect = flyingClearanceLimitDirect || (inClearanceLimitHold && newAirport);
        if (turnsDirect && (FlightPhysics.ClearanceLimitDirectTo(canonical, aircraft.Position) is { } direct))
        {
            aircraft.Targets.StripClearanceLimitDirect();
            aircraft.Targets.NavigationRoute.Add(direct);
        }
    }

    private static void AnswerFurtherClearanceRequest(AircraftState aircraft)
    {
        if (aircraft.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.FurtherClearance } request)
        {
            request.ResponseState = PilotPendingRequestResponseState.Satisfied;
        }
    }
}
