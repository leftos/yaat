namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// A climb that counts as the circuit's upwind and can carry a pending pursuit of a lead with no runway: FOLLOW keeps the climb
/// and arms the flag, and the pursuit starts when the climb hands over to its circuit's upwind
/// (<see cref="PhaseRunner.Tick"/>). Implemented by <see cref="TakeoffPhase"/> and <see cref="GoAroundPhase"/> so the sites
/// that arm, disarm or read it need no type switch.
/// </summary>
internal interface IPendingPursuitClimb
{
    /// <summary>The pursuit of a lead with no runway this climb is holding off, armed by FOLLOW and read at the hand-over.</summary>
    bool PursuesRunwaylessLeadAfterClimb { get; set; }
}
