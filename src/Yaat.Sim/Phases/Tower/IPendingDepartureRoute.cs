namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// A departure phase holding the route a SID handover will load into <see cref="ControlTargets.NavigationRoute"/> once the
/// phase's own lateral guidance ends: <see cref="DepartureProcedurePhase"/> while it flies the coded legs, and
/// <see cref="InitialClimbPhase"/> while it holds the route for the TERPS-gate hand-off or for a radar-vectors SID's
/// post-vector load. While a handover is pending the live route stays empty, and a crossing restriction acts on this route
/// instead: the handover re-adds the same <see cref="NavigationTarget"/> objects, so a stamp on them survives it.
/// </summary>
public interface IPendingDepartureRoute
{
    /// <summary>The route the handover will load, or null when no handover is pending.</summary>
    List<NavigationTarget>? PendingRoute { get; }
}
