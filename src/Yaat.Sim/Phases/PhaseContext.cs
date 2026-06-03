using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases;

public sealed class PhaseContext
{
    public required AircraftState Aircraft { get; init; }
    public required ControlTargets Targets { get; init; }
    public required AircraftCategory Category { get; init; }

    /// <summary>Shortcut for <c>Aircraft.AircraftType</c>.</summary>
    public string AircraftType => Aircraft.AircraftType;
    public required double DeltaSeconds { get; init; }
    public RunwayInfo? Runway { get; init; }
    public double FieldElevation { get; init; }
    public AirportGroundLayout? GroundLayout { get; init; }

    /// <summary>
    /// Lookup function to find other aircraft by callsign.
    /// Used by FollowingPhase and ground conflict detection.
    /// </summary>
    public Func<string, AircraftState?>? AircraftLookup { get; init; }

    /// <summary>
    /// Logger for phase diagnostics. Provided by the server tick loop.
    /// </summary>
    public required ILogger Logger { get; init; }

    /// <summary>Active weather profile. Null when no weather is loaded.</summary>
    public WeatherProfile? Weather { get; init; }

    /// <summary>Scenario elapsed time in seconds. Used for approach score timestamps.</summary>
    public double ScenarioElapsedSeconds { get; init; }

    /// <summary>When true, aircraft are automatically cleared to land (no CLAND command needed).</summary>
    public bool AutoClearedToLand { get; init; }

    /// <summary>
    /// When true, an aircraft vacating a runway between two parallels auto-advances to hold short of
    /// the parallel runway when it is reachable on the same exit taxiway with no intervening taxiway
    /// intersection (issue #175). The aircraft still requires an explicit CROSS to cross the parallel.
    /// </summary>
    public bool AutoPullUpToParallel { get; init; }

    /// <summary>
    /// When true, the simulator plays all pilots and emits readbacks / proactive comms via
    /// <c>PilotResponder</c>. Phases gate any pilot-side speech (e.g.,
    /// <c>AtParkingPhase</c>'s spawn check-in) on this flag so instructor-mode users see
    /// zero behavior change.
    /// </summary>
    public bool SoloTrainingMode { get; init; }

    public string? ScenarioId { get; init; }

    public int SoloParkingInitialCallupRatePercent { get; init; } = 100;

    /// <summary>
    /// Per-approach chance (0–100) that an AI aircraft in solo training spontaneously goes
    /// around on entering <see cref="Tower.FinalApproachPhase"/>. Sourced from
    /// <c>SimScenarioState.SoloGoAroundProbabilityPercent</c>; gated by
    /// <see cref="SoloTrainingMode"/>.
    /// </summary>
    public int SoloGoAroundProbabilityPercent { get; init; }

    /// <summary>
    /// Shared deterministic RNG, captured on snapshot for replay fidelity. Phases that
    /// consume RNG (e.g. <see cref="Tower.FinalApproachPhase"/> solo-training go-around
    /// roll) must use this instead of <c>new Random()</c> so replays regenerate the
    /// same outcomes. Null only in tests that don't exercise RNG-dependent code paths.
    /// </summary>
    public SerializableRandom? Rng { get; init; }

    public Func<double, bool>? TryReserveSoloParkingInitialCallupSlot { get; init; }

    /// <summary>
    /// When true (and <see cref="SoloTrainingMode"/> is false), sim-initiated pilot
    /// transmissions route to <c>AircraftState.PendingPilotSpeech</c> with the spoken form
    /// from <c>PilotResponder</c>. When false, the same events fall through to
    /// <c>PendingWarnings</c> with terse controller-debug text. Default false.
    /// </summary>
    public bool RpoShowPilotSpeech { get; init; }

    /// <summary>
    /// Student controller position type (TWR/GND/APP/CTR), derived from the vNAS position callsign.
    /// Used by pilot speech to address the right facility radio name without changing non-matching
    /// generic calls such as a ramp aircraft calling ground during a tower-only scenario.
    /// </summary>
    public string? StudentPositionType { get; init; }

    /// <summary>Student controller track owner, if the scenario selected a student TCP.</summary>
    public TrackOwner? StudentPosition { get; init; }

    /// <summary>Scenario ARTCC id used to resolve ARTCC-scoped custom SOP data.</summary>
    public string? ArtccId { get; init; }

    /// <summary>Scenario primary airport id used when an aircraft lacks a filed destination.</summary>
    public string? PrimaryAirportId { get; init; }

    /// <summary>ARTCC-specific SOP exceptions for initial pilot contact transfer.</summary>
    public InitialContactTransferCatalog InitialContactTransfers { get; init; } = InitialContactTransferCatalog.Empty;

    /// <summary>
    /// vNAS radio name for the student position, e.g. "Oakland Tower". Null falls back to
    /// generic "tower", "ground", "approach", or "center" wording.
    /// </summary>
    public string? StudentRadioName { get; init; }

    /// <summary>
    /// The local tower position (TrackOwner), if the student is controlling tower.
    /// Used by InitialClimbPhase to hold RV SID heading while tower owns the track.
    /// </summary>
    public TrackOwner? TowerPosition { get; init; }

    /// <summary>
    /// Returns true if the given hold-short node is already occupied by another aircraft.
    /// Provided by the server tick loop to prevent stacking at hold-short points.
    /// </summary>
    public Func<int, bool>? IsHoldShortNodeOccupied { get; init; }

    /// <summary>
    /// The set of occupied hold-short node IDs. Passed directly to
    /// <see cref="AirportGroundLayout.FindExitFromCenterline"/> so the BFS
    /// excludes occupied exits during scoring (not just after).
    /// </summary>
    public HashSet<int>? OccupiedHoldShortNodes { get; init; }

    /// <summary>
    /// Marks a hold-short node as occupied within the current tick.
    /// Called when an aircraft snaps to a hold-short, so subsequent aircraft
    /// in the same tick see it as occupied.
    /// </summary>
    public Action<int>? MarkHoldShortNodeOccupied { get; init; }
}
