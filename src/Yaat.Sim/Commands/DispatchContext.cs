using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Commands;

/// <summary>
/// Bundle of context required to dispatch a command. Passed through
/// <see cref="CommandDispatcher.Dispatch"/> and
/// <see cref="CommandDispatcher.DispatchCompound"/> and any internal helpers
/// that need the scenario's ground layout, weather, aircraft lookup, RNG, or
/// dispatch flags.
///
/// All fields are positional and required — tests and production must construct
/// a context explicitly rather than relying on defaults, so that future context
/// additions break at the compiler instead of silently passing null.
///
/// <para><see cref="Weather"/> and <see cref="FindAircraft"/> are nullable:
/// commands that need them (currently RFIS / RTIS for live visual acquisition)
/// fail gracefully when they are absent, which is the normal case for tests
/// that don't exercise visual detection.</para>
/// </summary>
public sealed record DispatchContext(
    AirportGroundLayout? GroundLayout,
    Random Rng,
    WeatherProfile? Weather,
    Func<string, AircraftState?>? FindAircraft,
    bool ValidateDctFixes,
    bool AutoCrossRunway,
    bool SoloTrainingMode
);
