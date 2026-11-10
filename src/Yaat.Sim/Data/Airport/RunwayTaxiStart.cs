using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// How a taxi start treats an aircraft standing on a runway centreline, decided once from the aircraft's phase name
/// (<see cref="Phase.Name"/>) and assigned runway, so the server's TAXI, FOLLOWG and the client's previews start alike: every
/// state but the landing rollout (<c>Landing</c>, <c>Runway Exit</c>) starts on the centreline where the aircraft stands
/// (<see cref="FromCenterline"/>); <see cref="AssignedRunway"/> picks the runway when two centrelines qualify.
/// </summary>
/// <param name="FromCenterline">Whether a start between two runway centreline nodes starts at the centreline node ahead.</param>
/// <param name="AssignedRunway">The aircraft's assigned runway ("28R"), or null.</param>
public readonly record struct RunwayTaxiStart(bool FromCenterline, string? AssignedRunway)
{
    /// <summary>The <see cref="Phase.Name"/> values of the landing rollout, which keeps the nearest-node start it plans from.</summary>
    private static readonly string[] RolloutPhaseNames = ["Landing", "Runway Exit"];

    /// <summary>The start rule for an aircraft in the phase named <paramref name="phaseName"/>, assigned <paramref name="assignedRunway"/>.</summary>
    public static RunwayTaxiStart Of(string? phaseName, string? assignedRunway) =>
        new(!RolloutPhaseNames.Contains(phaseName ?? "", StringComparer.Ordinal), string.IsNullOrEmpty(assignedRunway) ? null : assignedRunway);

    /// <summary>The start rule for <paramref name="aircraft"/>: its current phase's name and its assigned runway.</summary>
    public static RunwayTaxiStart Of(AircraftState aircraft) => Of(aircraft.Phases?.CurrentPhase?.Name, aircraft.Phases?.AssignedRunway?.Designator);
}
