namespace Yaat.Sim.Training;

/// <summary>
/// Why an aircraft's session ended from the student controller's perspective.
/// Drives the M12.4 per-aircraft debrief and is preserved by
/// <see cref="SimulationWorld"/> after the aircraft is removed from the world.
/// </summary>
public enum CompletionReason
{
    /// <summary>Session still in progress — no completion stamp yet.</summary>
    Active,

    /// <summary>Aircraft touched down on a runway. Set by <see cref="Phases.Tower.LandingPhase"/>.</summary>
    Landed,

    /// <summary>Controller issued <c>CT</c> or <c>FCA</c>. Set by <see cref="Commands.ContactCommandHandler"/>.</summary>
    HandedOff,

    /// <summary>Aircraft was explicitly deleted (DEL command, scenario unload) without a prior completion stamp.</summary>
    Dropped,
}

/// <summary>
/// How an aircraft fits the student's airspace — derived from filed flight plan
/// against the scenario primary airport. Pure UX label for the M12.4 debrief tab.
/// </summary>
public enum OperationKind
{
    /// <summary>Departure or destination not yet known.</summary>
    Unknown,

    /// <summary>Filed Departure matches the student's primary airport.</summary>
    Departure,

    /// <summary>Filed Destination matches the student's primary airport, but Departure does not.</summary>
    Arrival,

    /// <summary>Aircraft transits the student's airspace — neither end matches primary airport.</summary>
    Transit,
}

/// <summary>
/// Per-aircraft debrief metadata captured at the moment of removal from
/// <see cref="SimulationWorld"/> so the M12.4 Aircraft tab can still show
/// completed aircraft after their <see cref="AircraftState"/> is gone.
/// Stored on <see cref="SimulationWorld"/>; cleared on scenario reset.
/// </summary>
public sealed record CompletedAircraftRecord(
    string Callsign,
    string AircraftType,
    string Cid,
    string? FiledDeparture,
    string? FiledDestination,
    double SpawnedAtSeconds,
    double CompletedAtSeconds,
    CompletionReason Reason,
    string? Detail
);

/// <summary>
/// One row in the M12.4 Aircraft tab. Aggregated by <see cref="SoloTrainingEvaluator"/>
/// from current <see cref="AircraftState"/> data or from a <see cref="CompletedAircraftRecord"/>
/// for aircraft that have already been removed from the world. Findings are grouped by
/// callsign via the existing <c>SoloTrainingEvent.Callsigns</c> list — no new schema
/// in the evaluator's event tracking.
/// </summary>
public sealed record AircraftDebriefData(
    string Callsign,
    string AircraftType,
    string? FiledDeparture,
    string? FiledDestination,
    OperationKind Operation,
    double SpawnedAtSeconds,
    double? CompletedAtSeconds,
    CompletionReason CompletionReason,
    string? CompletionDetail,
    int SeparationFindingCount,
    int RunwayWakeFindingCount,
    int AdvisoryFindingCount,
    int ApproachFindingCount,
    int CoachFindingCount,
    int WarningFindingCount,
    int SafetyFindingCount,
    string CoachingNote,
    IReadOnlyList<string> FindingIds
);

/// <summary>
/// Inputs required by <see cref="SoloTrainingEvaluator.BuildReport"/> to produce the
/// M12.4 per-aircraft debriefs. Bundled so the report-building surface stays under
/// the five-positional-parameter rule and so <see cref="Empty"/> works for tests that
/// don't exercise the debrief tab.
/// </summary>
public sealed record AircraftDebriefContext(
    IReadOnlyList<AircraftState> ActiveAircraft,
    IReadOnlyList<CompletedAircraftRecord> CompletedAircraft,
    string? PrimaryAirportId
)
{
    public static AircraftDebriefContext Empty { get; } = new([], [], null);
}
