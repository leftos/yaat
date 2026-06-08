using Yaat.Sim.Commands;
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
    /// clear in scenarios where the AI pilot would otherwise never speak to the student. Only an
    /// instruction that establishes two-way communication counts: a clearance, vector, routing,
    /// sequencing, contact acknowledgement, or a directed report request the pilot answers on
    /// frequency (say altitude/speed/heading/position/...). Dictating verbatim text for the aircraft
    /// to broadcast (<c>SAY</c>) or a display-only readout of queued commands (<c>SHOW</c>) does not.
    /// </summary>
    public static void RegisterControllerContact(AircraftState aircraft, SimScenarioState? scenario, CompoundCommand command)
    {
        aircraft.HasControllerAcknowledgedInitialContact = true;
        if (scenario is null || aircraft.HasMadeInitialContact || !EstablishesTwoWayComms(command))
        {
            return;
        }

        if (!CanInitiateWithStudent(aircraft, scenario))
        {
            aircraft.HasMadeInitialContact = true;
        }
    }

    /// <summary>
    /// True when the command represents the controller and pilot establishing two-way radio
    /// communication — a clearance, vector, routing, sequencing, contact acknowledgement, or a
    /// directed report request the pilot answers on frequency (say altitude/speed/heading/position/...).
    /// Excludes only a verbatim <c>SAY</c> broadcast (the controller scripting the aircraft's own
    /// transmission rather than addressing the pilot) and a <c>SHOW</c> readout of queued commands
    /// (a display, not a radio transmission), neither of which justifies releasing a self-imposed
    /// airspace boundary hold.
    /// </summary>
    private static bool EstablishesTwoWayComms(CompoundCommand command) =>
        command.Blocks.Any(block => block.Commands.Any(c => c is not (SayCommand or ShowQueuedCommand)));

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
