namespace Yaat.Sim.Commands.Arguments;

/// <summary>
/// The declared type of one positional command argument. Deliberately minimal: only the argument
/// slots that have migrated onto typed arguments are represented, and the enum grows as more do.
/// Every member needs a shape validator in <see cref="CommandArgumentResolver"/>; where two types can
/// appear at the same position, the resolver's binding precedence decides which claims the token.
/// </summary>
public enum CommandArgumentType
{
    /// <summary>A runway designator — <see cref="RunwayArgument"/>.</summary>
    Runway,

    /// <summary>An altitude in a slot where a runway competes — <see cref="AltitudeArgument"/>.</summary>
    Altitude,
}
