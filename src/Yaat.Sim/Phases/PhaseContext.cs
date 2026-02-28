namespace Yaat.Sim.Phases;

public sealed class PhaseContext
{
    public required AircraftState Aircraft { get; init; }
    public required ControlTargets Targets { get; init; }
    public required AircraftCategory Category { get; init; }
    public required double DeltaSeconds { get; init; }
    public RunwayInfo? Runway { get; init; }
    public double FieldElevation { get; init; }
}
