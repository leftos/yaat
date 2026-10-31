using System.Collections.Immutable;
using Yaat.Sim.Asdex;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Bundle of context required to dispatch a command. Passed through
/// <see cref="CommandDispatcher.Dispatch"/> and
/// <see cref="CommandDispatcher.DispatchCompound"/> and any internal helpers
/// that need the scenario's ground layout, weather, aircraft lookup, RNG, or
/// dispatch flags.
///
/// Every property is required — tests and production must construct
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
/// <para><see cref="IsScenarioScripted"/> is true when the dispatch is not the human student
/// talking: scenario scripting (a preset) or an AI-controller dispatch
/// (<see cref="DispatchOrigin.ControllerAi"/>, derived from the <see cref="AiConnectionId"/>
/// connection id live and on replay). Neither is the student establishing two-way comms, so a
/// successful scripted ground clearance must NOT mark
/// <see cref="AircraftState.HasMadeInitialContact"/> — otherwise a runway-spawn CTO-preset
/// departure, or one cleared by AI Local, would never make its post-takeoff check-in with a
/// student radar position. Human-origin live and replayed commands pass false (they
/// additionally record contact via
/// <see cref="Pilot.PilotInitialContactEligibility.RegisterControllerContact"/>). A deferred
/// payload inherits the value from the deferral that produced it (a preset WAIT/BEHIND stays
/// scripted; a reaction-delay deferral carries its command's origin).</para>
///
/// <para><see cref="SessionStartUtc"/> is the instant t=0 of <see cref="ScenarioElapsedSeconds"/> is
/// anchored to (<see cref="Simulation.SimScenarioState.SessionStartUtc"/>); the two together give the
/// session clock a dispatch answers with, so a verb whose reply carries a time of day (SAYEXIT's exit-fix
/// estimate) reads the same clock on a replay as it did live. Callers outside a scenario pass
/// <see cref="Simulation.SimScenarioState.ProcessDayUtc"/>.</para>
/// </summary>
public sealed record DispatchContext
{
    public required AirportGroundLayout? GroundLayout { get; init; }

    public required Random Rng { get; init; }

    public required WeatherProfile? Weather { get; init; }

    public required Func<string, AircraftState?>? FindAircraft { get; init; }

    public required Func<IReadOnlyList<AircraftState>>? ListAircraft { get; init; }

    public required bool ValidateDctFixes { get; init; }

    /// <summary>Which runway crossings the scenario pre-clears (<see cref="SimScenarioState.AutoCrossRunwayMode"/>).</summary>
    public required AutoCrossRunwayMode AutoCrossRunwayMode { get; init; }

    public required bool SoloTrainingMode { get; init; }

    /// <summary>
    /// The scenario's <see cref="Simulation.SimScenarioState.SoloRpoCommandsAllowed"/> development flag: lets solo training
    /// accept the RPO-only commands it otherwise refuses.
    /// </summary>
    public required bool SoloRpoCommandsAllowed { get; init; }

    public required bool RpoShowPilotSpeech { get; init; }

    public required Action<TerminalEntry>? TerminalEmitter { get; init; }

    public required ArtccConfigRoot? ArtccConfig { get; init; }

    /// <summary>
    /// The room's live ASDE-X Safety Logic configuration per facility (<see cref="SimScenarioState.AsdexSafetyLogicConfigs"/>),
    /// <see cref="AsdexSafetyLogicSeed.Empty"/> outside a scenario. The runway-occupancy advisories stand down only where it
    /// has occupied-runway alerting live (<see cref="AsdexSafetyLogicSeed.OccupiedRunwayAlertingIsLive"/>).
    /// </summary>
    public required ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> AsdexSafetyLogicConfigs { get; init; }

    /// <summary>The room's towered airports (<see cref="SimScenarioState.ToweredAirports"/>); empty reads every airport as untowered.</summary>
    public required ToweredAirports ToweredAirports { get; init; }

    /// <summary>The room's active runways (<see cref="SimScenarioState.ActiveRunways"/>); empty when it names none.</summary>
    public required ActiveRunways ActiveRunways { get; init; }

    public required double ScenarioElapsedSeconds { get; init; }

    public required DateTime SessionStartUtc { get; init; }

    public required bool PreserveConditionals { get; init; }

    public required bool IsScenarioScripted { get; init; }

    /// <summary>
    /// The facility a callsign naming positions in several facilities (<c>CT OAK_TWR</c>, listed under NCT and O90)
    /// resolves in: the issuing identity's, else the student's; null picks the first in the config.
    /// </summary>
    public required string? FacilityHint { get; init; }

    /// <summary>
    /// The engine's ground layout for an airport id, which a destination change (<c>APT</c>) on an airborne aircraft not
    /// low over its current field switches it to; null where no engine supplies one (a test harness), and the layout then
    /// stays as it is.
    /// </summary>
    public required Func<string, AirportGroundLayout?>? LayoutForAirport { get; init; }

    /// <summary>
    /// The spoken-wording context a handler-built pilot transmission (the CT and FCA readbacks) uses for an aircraft:
    /// the engine's live band, seed, second and abbreviation draws when a pilot is answering, else
    /// <see cref="PilotSpeechContext.Canonical"/>. Read before the controller's transmission is counted on the frequency.
    /// </summary>
    public required Func<AircraftState, PilotSpeechContext> SpeechContextFor { get; init; }

    /// <summary>
    /// The id of the transmission being dispatched when it carries a crossing restriction (a CFIX or a constrained
    /// DCTF): <see cref="CommandDispatcher.DispatchCompound"/> takes it from the aircraft's
    /// <see cref="CommandQueue.NextTransmissionId"/>, and a queued block puts its own back when it fires. Every other
    /// transmission keeps 0. Not required, unlike the rest of the record: only the dispatcher sets it.
    /// </summary>
    public int TransmissionId { get; internal init; }

    /// <summary>
    /// True when the transmission restated a speed before the command being applied: SPD, SPEEDF, the forced final-approach
    /// speed, MACH, SMAX or RFAS (<c>CommandDispatcher.AssignsSpeed</c>). It has two sources: such a speed applied earlier
    /// in the same block (set by the block's apply action), or one in an earlier block of the same transmission (set when
    /// the block is built, from the transmission's blocks, and carried on the block
    /// (<see cref="CommandBlock.SpeedRestatedInTransmission"/>) through a snapshot or a split). A DVIA, CVIA or approach
    /// clearance then leaves the speed in force assigned, since a speed still required is restated with the clearance
    /// (7110.65 5-7-1.c). A speed after the clearance does not count: it applies after it, or is refused and the clearance's
    /// cancel stands. A crossing speed (a CFIX or constrained DCTF carrying one, DVIA SPD n FIX) does not count either: it
    /// restates only its own fix's restriction, which the fix's speed stamp keeps.
    /// </summary>
    public bool SpeedRestatedInTransmission { get; internal init; }

    /// <summary>
    /// The session clock at this dispatch: the record's mirror of <see cref="Simulation.SimScenarioState.SimTimeUtc"/>,
    /// for the verbs whose answer carries a time of day.
    /// </summary>
    public DateTime SessionNowUtc => SessionStartUtc.AddSeconds(ScenarioElapsedSeconds);

    /// <summary>
    /// Whether an RPO-only command (FOLLOWF, CVAF, RFISF, RTISF, CLANDF) is refused: in solo training, unless the
    /// scenario's <see cref="SoloRpoCommandsAllowed"/> flag lets it through.
    /// </summary>
    public bool RefusesRpoOnly => SoloTrainingMode && !SoloRpoCommandsAllowed;
}
