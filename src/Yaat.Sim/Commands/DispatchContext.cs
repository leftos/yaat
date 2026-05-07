using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

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
///
/// <para><see cref="TerminalEmitter"/> is nullable: SAY-class verbs broadcast pilot
/// transmissions through it. Callers running outside a simulation (parser tests,
/// dry-run dispatch) leave it null and the broadcasts are discarded.</para>
///
/// <para><see cref="ArtccConfig"/> is nullable: the CT (contact) command resolves a target
/// position to a frequency through it. Callers without an ARTCC config (parser tests,
/// minimal harnesses) leave it null and CT falls back to the verbatim target callsign for
/// the spoken facility name with no frequency component.</para>
/// </summary>
public sealed record DispatchContext(
    AirportGroundLayout? GroundLayout,
    Random Rng,
    WeatherProfile? Weather,
    Func<string, AircraftState?>? FindAircraft,
    bool ValidateDctFixes,
    bool AutoCrossRunway,
    bool SoloTrainingMode,
    bool RpoShowPilotSpeech,
    Action<TerminalEntry>? TerminalEmitter,
    ArtccConfigRoot? ArtccConfig
);
