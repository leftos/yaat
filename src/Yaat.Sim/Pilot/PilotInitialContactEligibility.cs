using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Pilot;

public sealed record InitialContactEligibilityContext(
    TrackOwner? StudentPosition,
    string? StudentPositionType,
    string? ArtccId,
    string? PrimaryAirportId,
    InitialContactTransferCatalog InitialContactTransfers
)
{
    public static InitialContactEligibilityContext Empty { get; } = new(null, null, null, null, InitialContactTransferCatalog.Empty);
}

public static class PilotInitialContactEligibility
{
    public static bool CanInitiateWithStudent(AircraftState aircraft, SimScenarioState scenario) =>
        CanInitiateWithStudent(
            aircraft,
            new InitialContactEligibilityContext(
                scenario.StudentPosition,
                scenario.StudentPositionType,
                scenario.ArtccId,
                scenario.PrimaryAirportId,
                scenario.InitialContactTransfers
            )
        );

    public static bool CanInitiateWithStudent(AircraftState aircraft, InitialContactEligibilityContext context)
    {
        if (context.StudentPosition is not { } studentPosition)
        {
            return true;
        }

        if (aircraft.Track.Owner is not { } owner)
        {
            return true;
        }

        if (owner.MatchesPosition(studentPosition))
        {
            return true;
        }

        if (aircraft.Track.HandoffPeer?.MatchesPosition(studentPosition) == true)
        {
            return context.StudentPositionType == "TWR";
        }

        if (context.StudentPositionType != "TWR")
        {
            return false;
        }

        var ownerPositionType = AtcPositionTypeClassifier.Classify(owner.Callsign);
        if (string.IsNullOrWhiteSpace(ownerPositionType))
        {
            return false;
        }

        foreach (var airportId in CandidateAirportIds(aircraft, context.PrimaryAirportId))
        {
            if (context.InitialContactTransfers.AllowsWithoutTrackHandoff(context.ArtccId, airportId, ownerPositionType, context.StudentPositionType))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidateAirportIds(AircraftState aircraft, string? primaryAirportId)
    {
        if (!string.IsNullOrWhiteSpace(aircraft.FlightPlan.Destination))
        {
            yield return aircraft.FlightPlan.Destination;
        }

        if (!string.IsNullOrWhiteSpace(aircraft.AirportId))
        {
            yield return aircraft.AirportId;
        }

        if (!string.IsNullOrWhiteSpace(aircraft.Ground.LayoutAirportId))
        {
            yield return aircraft.Ground.LayoutAirportId;
        }

        if (!string.IsNullOrWhiteSpace(primaryAirportId))
        {
            yield return primaryAirportId;
        }
    }
}
