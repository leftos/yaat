using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Commands;

/// <summary>
/// Bundle of context required to dispatch a command. Passed through
/// <see cref="CommandDispatcher.Dispatch"/> and
/// <see cref="CommandDispatcher.DispatchCompound"/> and any internal helpers
/// that need the scenario's ground layout, RNG, or dispatch flags.
///
/// All fields are positional and required — tests and production must construct
/// a context explicitly rather than relying on defaults, so that future context
/// additions break at the compiler instead of silently passing null.
/// </summary>
public sealed record DispatchContext(AirportGroundLayout? GroundLayout, Random Rng, bool ValidateDctFixes, bool AutoCrossRunway);
