namespace Yaat.Sim.Phases;

/// <summary>
/// A phase that is flying a ground roll along the <see cref="GroundRollProfile"/> spool ramp and can
/// report how far into that ramp it is. Predictors read the clock through
/// <see cref="GroundRollProfile.RollClockSeconds"/> so they place the aircraft where the roll itself
/// has it, rather than inferring a ramp position from the speed it happens to be making.
/// </summary>
public interface IGroundRollClock
{
    /// <summary>Seconds of spool ramp the roll has flown, 0 at brake release.</summary>
    double RollElapsedSeconds { get; }
}
