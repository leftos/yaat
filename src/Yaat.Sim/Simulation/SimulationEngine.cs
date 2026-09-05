using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Result from <see cref="SimulationEngine.TickPrePhysics"/>. <see cref="SpawnedAircraft"/> lists the
/// delayed-queue aircraft spawned this tick; <see cref="GeneratorSpawns"/> lists the arrival-generator
/// spawns paired with their autotrack configuration. The server broadcasts both and, for generator
/// spawns that carry autotrack, applies the owner/scratchpad/handoff before broadcasting.
/// </summary>
public record struct TickPrePhysicsResult(List<AircraftState> SpawnedAircraft, List<GeneratorSpawn> GeneratorSpawns);

/// <summary>
/// One arrival-generator spawn this tick paired with its generator's <see cref="AutoTrackConditions"/>
/// (null when the generator has none). Threaded out of the sim so the server can apply the autotrack and
/// record the spawn AFTER, so the owner/scratchpad land in the initial broadcast and the recorded
/// snapshot replays with them intact (the eager in-sim recording would capture an untracked state).
/// </summary>
public readonly record struct GeneratorSpawn(AircraftState State, AutoTrackConditions? AutoTrack);

/// <summary>
/// Diagnostic record of one arrival-generator spawn. Lets the time-first spawn cadence and placement
/// be inspected and asserted in tests. <see cref="RearmostAtSpawnNm"/> is null when the corridor was
/// empty (the arrival spawned at <c>InitialDistance</c>); <see cref="RequiredGapNm"/> is then 0 — otherwise
/// it is the binding in-trail gap (max of <c>IntervalDistance</c> and the wake minimum) the arrival was
/// placed behind the rearmost.
/// </summary>
public readonly record struct GeneratorSpawnRecord(
    string GeneratorId,
    string Callsign,
    double ElapsedSeconds,
    double SpawnDistanceNm,
    double? RearmostAtSpawnNm,
    double RequiredGapNm
);

/// <summary>
/// Result of <see cref="SimulationEngine.TickConflictAlerts"/>: the terminal Conflict Alert pairs that
/// opened (<see cref="New"/>) and closed (<see cref="Cleared"/> ids) this tick, for the host to broadcast.
/// </summary>
public readonly record struct ConflictAlertChanges(List<ActiveConflict> New, List<string> Cleared);

/// <summary>
/// Result of <see cref="SimulationEngine.TickEramConflictAlerts"/>: the ERAM STCA pairs that opened
/// (<see cref="New"/>) and closed (<see cref="Cleared"/> ids) this tick, for the host to broadcast.
/// </summary>
public readonly record struct EramConflictAlertChanges(List<EramActiveConflict> New, List<string> Cleared);

public sealed partial class SimulationEngine
{
    public const int PhysicsSubTickRate = 4;

    private readonly IAirportGroundData _groundData;

    private readonly ILogger _logger;
    private readonly List<TerminalEntry> _terminalEntries = [];

    /// <summary>
    /// The per-connection position selections (<c>AS {tcp}</c>) every identity resolution on this engine reads.
    /// Settable so a host that outlives its engines — the server room — hands each engine the same instance;
    /// a Sim-only host keeps this default. Cleared at the start of a fresh replay (<c>startSeconds == 0</c>),
    /// captured in the snapshot's server section and replaced on restore.
    /// </summary>
    public PositionSelections PositionSelections { get; set; } = new();

    /// <summary>
    /// The one router every controller action on this engine goes through — fresh (<see cref="SendCommand"/>,
    /// <see cref="DispatchAiCommand"/>) or recorded (the replay driver). See <see cref="ActionRouter"/>.
    /// </summary>
    public ActionRouter Actions { get; }

    /// <summary>
    /// The connection id a bare <see cref="SendCommand"/> issues under: the standalone engine's one controller (a
    /// test, the solo client), so its <c>AS</c> selections and identity resolve like any other connection's.
    /// </summary>
    public const string LocalConnectionId = "local";

    /// <summary>
    /// A bare <c>AS {tcp}</c>: the connection acts as the position <paramref name="tcpCode"/> resolves to until it
    /// selects another. The one write body for live, replay and reconstruction.
    /// </summary>
    public CommandResult SelectPosition(string connectionId, string tcpCode)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return new CommandResult(false, "No scenario loaded");
        }

        var owner = TrackResolver.ResolveTcpToOwner(scenario, tcpCode);
        if (owner is null)
        {
            return new CommandResult(false, $"Unknown position: {tcpCode}");
        }

        PositionSelections.Select(connectionId, owner);
        return new CommandResult(true, $"Now acting as {owner.Callsign} ({tcpCode})");
    }

    /// <summary>The identity a command from <paramref name="connectionId"/> acts as; see <see cref="TrackResolver.ResolveIdentity"/>.</summary>
    public TrackOwner? ResolveIdentity(string connectionId, string? asOverrideTcp) =>
        TrackResolver.ResolveIdentity(
            Scenario ?? throw new InvalidOperationException("No scenario loaded"),
            PositionSelections,
            connectionId,
            asOverrideTcp
        );

    /// <summary>The engine's logger, so collaborators it owns log under the same category.</summary>
    internal ILogger Logger => _logger;

    public SimulationWorld World { get; } = new();
    public SimScenarioState? Scenario { get; set; }
    public ConsolidationState ConsolidationState { get; } = new();
    public ApproachEvaluator ApproachEvaluator { get; } = new();
    public SoloTrainingEvaluator SoloTrainingEvaluator { get; } = new();
    public BeaconCodePool BeaconCodePool { get; } = new();
    public TowerListTracker TowerListTracker { get; } = new();
    public ConflictAlertState ConflictAlerts { get; } = new();
    public EramConflictState EramConflicts { get; } = new();

    /// <summary>
    /// Fires at the end of every sim-second, after the end-of-second steps (<see cref="RunEndOfSecond"/>), on every
    /// run kind. The int argument is <c>Scenario.ElapsedSeconds</c> at second end. Intended for test
    /// instrumentation (see <c>TickRecorder.Attach</c>).
    /// </summary>
    public event Action<int>? TickCompleted;

    internal void FireTickCompleted(int elapsedSeconds)
    {
        TickCompleted?.Invoke(elapsedSeconds);
    }

    /// <summary>
    /// Fires during the post-physics drain for each <see cref="AircraftState.PendingWarnings"/>
    /// entry produced this tick — queue-clear notices, missed AT/AT-fix conditions, deferred
    /// commands rejected when their trigger fires, etc. Mirrors the server's
    /// <c>TickProcessor.BroadcastWarnings</c> fan-out so non-server consumers (solo client,
    /// tests) can react to the same per-aircraft warnings the RPO would see in the terminal
    /// log. Default null = warnings are still drained from the aircraft (so they don't
    /// accumulate) but otherwise discarded by this engine instance.
    /// </summary>
    public event Action<string, string>? WarningEmitted;

    internal void FireWarningEmitted(string callsign, string warning)
    {
        WarningEmitted?.Invoke(callsign, warning);
    }

    /// <summary>
    /// Fires during the post-physics drain for each <see cref="AircraftState.PendingStripDispatches"/>
    /// entry — a strip command (AN / STRIP / SCAN / …) produced by preset, deferred, or triggered
    /// dispatch that the Sim cannot apply (strip state is host-owned). The host (yaat-server) drains
    /// <see cref="SimulationWorld.DrainAllStripDispatches"/> directly and routes to
    /// <c>StripCommandHandler</c>; this event lets standalone consumers (solo client, tests) observe
    /// the same commands. Default null = the entry is still drained (so it does not accumulate) but
    /// otherwise discarded. Mirrors <see cref="WarningEmitted"/>.
    /// </summary>
    /// <summary>
    /// Fires during the post-physics drain for each <see cref="AircraftState.PendingPilotSpeech"/>
    /// entry — an RPO-mode pilot transmission produced this tick. Mirrors the server's
    /// <c>TickProcessor.BroadcastPilotSpeech</c> fan-out so non-server consumers (solo client, tests)
    /// can observe the same lines. Default null = the entry is still drained (so it does not
    /// accumulate) but otherwise discarded. Mirrors <see cref="WarningEmitted"/>.
    /// </summary>
    public event Action<string, string>? PilotSpeechEmitted;

    internal void FirePilotSpeechEmitted(string callsign, string speech)
    {
        PilotSpeechEmitted?.Invoke(callsign, speech);
    }

    public event Action<string, ParsedCommand>? StripDispatchRequested;

    internal void FireStripDispatchRequested(string callsign, ParsedCommand command)
    {
        StripDispatchRequested?.Invoke(callsign, command);
    }

    public SimulationEngine(IAirportGroundData groundData, ILogger? logger = null)
    {
        _groundData = groundData;
        _logger = logger ?? SimLog.CreateLogger<SimulationEngine>();
        _bareHost = new BareHost(this);
        _replay = new ReplayDriver(this);
        Actions = new ActionRouter(this);
    }

    // --- Drain collections ---

    /// <summary>
    /// Hands over and clears the entries accumulated since the last drain. The spine drains once per second in
    /// pre-physics (<see cref="Spine.StepId.TerminalEntries"/>) and gives them to the host; the live server also
    /// drains after each command it dispatches.
    /// </summary>
    public List<TerminalEntry> DrainTerminalEntries()
    {
        var entries = new List<TerminalEntry>(_terminalEntries);
        _terminalEntries.Clear();
        return entries;
    }

    /// <summary>
    /// Append a terminal entry produced by an external dispatcher (e.g., the server's
    /// RoomEngine) so it surfaces through the same drain path as engine-internal entries.
    /// Callers wire this into <see cref="DispatchContext.TerminalEmitter"/> when they
    /// dispatch commands outside the engine's own SendCommand/preset/replay paths.
    /// </summary>
    public void EmitTerminalEntry(TerminalEntry entry) => AddTerminalEntry(entry);

    /// <summary>
    /// Fires for every terminal entry the engine produces — command echoes, preset and deferred outcomes,
    /// warnings. The entry list behind <see cref="DrainTerminalEntries"/> is cleared at the end of every tick,
    /// so consumers that need entries as they happen (tests, the solo client) subscribe here instead of polling.
    /// </summary>
    public event Action<TerminalEntry>? TerminalEntryEmitted;

    private void AddTerminalEntry(TerminalEntry entry)
    {
        _terminalEntries.Add(entry);
        TerminalEntryEmitted?.Invoke(entry);
    }

    // --- Snapshots ---

    public AircraftState? FindAircraft(string callsign)
    {
        return World.GetSnapshot().FirstOrDefault(a => a.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
    }

    // --- Public mutations ---

    internal void EmitTerminal(string kind, string callsign, string message)
    {
        AddTerminalEntry(new TerminalEntry(kind, callsign, message));
    }

    /// <summary>
    /// The sim-side half of a <c>DEL</c>: stamps <see cref="CompletionReason.Dropped"/> on a still-active aircraft so
    /// <see cref="SimulationWorld.RemoveAircraft"/> records a debrief row instead of a silent vanish, clears a
    /// still-queued delayed spawn, and removes the aircraft from the world. The live server
    /// (<c>RoomEngine.RemoveSimulatedAircraft</c>) and the router's <c>DEL</c> arm both call this so the two cannot
    /// drift. Landed / HandedOff / Transited stamps are preserved.
    /// </summary>
    public void DeleteAircraft(string callsign)
    {
        var ac = World.FindAircraft(callsign);
        if (ac is { CompletionReason: CompletionReason.Active })
        {
            ac.CompletedAtSeconds = Scenario?.ElapsedSeconds;
            ac.CompletionReason = CompletionReason.Dropped;
            ac.CompletionDetail = "DEL";
        }

        Scenario?.DelayedQueue.RemoveAll(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        World.RemoveAircraft(callsign);
    }
}
