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
/// <para><see cref="Weather"/>, <see cref="FindAircraft"/>, and <see cref="ListAircraft"/> are nullable:
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
///
/// <para><see cref="PreserveConditionals"/> is true only when a deferred dispatch fires its
/// payload (<see cref="Simulation.SimulationEngine"/> deferred path). A firing deferral is
/// the execution of an already-issued conditional, not a fresh controller command, so its
/// payload must not supersede sibling pending conditionals: it preserves triggered queue
/// blocks and leaves other deferred dispatches intact. Every other dispatch (fresh live
/// command, preset, replay) passes false.</para>
///
/// <para><see cref="IsScenarioScripted"/> is true when the dispatch originates from scenario
/// scripting (a preset), not from a live or replayed controller action. Scripted automation
/// is not the student establishing two-way comms, so a successful scripted ground clearance
/// must NOT mark <see cref="AircraftState.HasMadeInitialContact"/> — otherwise a runway-spawn
/// CTO-preset departure would never make its post-takeoff check-in. Live and replayed
/// controller commands pass false (they additionally record contact via
/// <see cref="Pilot.PilotInitialContactEligibility.RegisterControllerContact"/>). A deferred
/// payload inherits the value from the deferral that produced it (a preset WAIT/BEHIND stays
/// scripted; a reaction-delay deferral of a live command stays non-scripted).</para>
/// </summary>
public sealed record DispatchContext(
    AirportGroundLayout? GroundLayout,
    Random Rng,
    WeatherProfile? Weather,
    Func<string, AircraftState?>? FindAircraft,
    Func<IReadOnlyList<AircraftState>>? ListAircraft,
    bool ValidateDctFixes,
    bool AutoCrossRunway,
    bool SoloTrainingMode,
    bool RpoShowPilotSpeech,
    Action<TerminalEntry>? TerminalEmitter,
    ArtccConfigRoot? ArtccConfig,
    double ScenarioElapsedSeconds,
    bool PreserveConditionals,
    bool IsScenarioScripted
);
