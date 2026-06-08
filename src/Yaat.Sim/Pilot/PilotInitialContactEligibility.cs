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

    /// <summary>
    /// Records that the controller has spoken to <paramref name="aircraft"/>. Always marks the
    /// controller side of two-way comms. When the pilot can never proactively check in with the
    /// student — the track is owned by another position with no handoff inbound, e.g. a tower student
    /// whose arrivals stay with approach — the controller's own instruction is what establishes
    /// two-way comms, so the pilot side is marked too. This lets the Class B/C boundary-hold gate
    /// clear in scenarios where the AI pilot would otherwise never speak to the student.
    /// </summary>
    public static void RegisterControllerContact(AircraftState aircraft, SimScenarioState? scenario)
    {
        aircraft.HasControllerAcknowledgedInitialContact = true;
        if (scenario is null || aircraft.HasMadeInitialContact)
        {
            return;
        }

        if (!CanInitiateWithStudent(aircraft, scenario))
        {
            aircraft.HasMadeInitialContact = true;
        }
    }

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
