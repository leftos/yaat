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

        var ownerPositionType = AtcPositionTypeClassifier.Classify(owner.Callsign);
        var studentPositionType = context.StudentPositionType ?? AtcPositionTypeClassifier.Classify(studentPosition.Callsign);
        var observedTiming =
            aircraft.Track.HandoffPeer?.MatchesPosition(studentPosition) == true
                ? InitialContactTransferTiming.HandoffInitiated
                : InitialContactTransferTiming.NoHandoffNecessary;

        return context.InitialContactTransfers.AllowsInitialContact(
            context.ArtccId,
            CandidateAirportIds(aircraft, context.PrimaryAirportId).ToList(),
            ownerPositionType,
            owner.Callsign,
            studentPositionType,
            studentPosition.Callsign,
            observedTiming
        );
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
