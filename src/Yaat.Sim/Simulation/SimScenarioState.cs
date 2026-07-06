using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

public sealed class SimScenarioState
{
    public required string ScenarioId { get; init; }
    public required string ScenarioName { get; init; }
    public required int RngSeed { get; init; }
    public required string OriginalScenarioJson { get; init; }
    public string? PrimaryAirportId { get; set; }
    public double ElapsedSeconds { get; set; }

    // Queues
    public List<DelayedSpawn> DelayedQueue { get; } = [];
    public List<ScheduledTrigger> TriggerQueue { get; } = [];
    public List<ScheduledPreset> PresetQueue { get; } = [];
    public List<GeneratorState> Generators { get; } = [];

    /// <summary>
    /// Airports armed for hold-for-release (uppercased as stored on <c>FlightPlan.Departure</c>).
    /// Single source of truth for "airport X is holding IFR departures for release." Mutated only by
    /// the HFR/HFROFF handlers via <see cref="HeldReleaseService"/>.
    /// </summary>
    public HashSet<string> HeldDepartureAirports { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pending auto-spaced releases (one entry per departure when a field's whole held queue is
    /// released with an interval). Fired by <c>ProcessReleaseQueue</c> against <see cref="ElapsedSeconds"/>.
    /// </summary>
    public List<ScheduledRelease> ReleaseQueue { get; } = [];

    /// <summary>
    /// Active countdown timers set via the TIMER command. Fired by <c>ProcessTimers</c> against
    /// <see cref="ElapsedSeconds"/>; each emits a green SAY-style terminal entry on expiry.
    /// </summary>
    public List<ActiveTimer> ActiveTimers { get; } = [];

    /// <summary>Monotonic id source for <see cref="ActiveTimers"/> (deterministic, replay-safe).</summary>
    public int NextTimerId { get; set; }

    // Settings affecting command dispatch
    public bool AutoClearedToLand { get; set; }
    public bool AutoCrossRunway { get; set; }

    /// <summary>
    /// When true, an aircraft that vacates a runway between two parallels auto-advances to hold
    /// short of the parallel runway when it is reachable on the same exit taxiway with no
    /// intervening taxiway intersection (issue #175). When false, the aircraft stops at the landing
    /// runway's exit hold-short. Independent of <see cref="AutoCrossRunway"/> — the aircraft still
    /// requires an explicit CROSS to cross the parallel.
    ///
    /// Defaults to false here so pre-feature recordings replay faithfully; the user-facing default
    /// is opt-out (on), set from the client preference and pushed to the server at scenario load.
    /// </summary>
    public bool AutoPullUpToParallel { get; set; }

    public bool ValidateDctFixes { get; set; } = true;

    // When true, every successful command dispatch produces a deterministic pilot-readback
    // line into AircraftState.PendingNotifications and AtParkingPhase emits a spawn check-in
    // once per parked IFR aircraft. Default false — instructor topology gets zero behavior
    // change.
    public bool SoloTrainingMode { get; set; }

    public int SoloParkingInitialCallupRatePercent { get; set; } = 100;

    public int SoloArrivalGeneratorRatePercent { get; set; } = 100;

    /// <summary>
    /// Per-approach chance (0–100) that an AI aircraft in solo training will spontaneously
    /// go around when entering <c>FinalApproachPhase</c>. 0 = never (default, preserves
    /// existing behavior); 100 = every approach. Single roll on phase entry.
    /// </summary>
    public int SoloGoAroundProbabilityPercent { get; set; }

    /// <summary>
    /// When true, arriving aircraft slow to final approach speed at a per-aircraft distance
    /// (right-skewed 2.0-5.0 NM, deterministic per callsign) instead of the uniform tight floor,
    /// reproducing the live-network spread. Defaults false here so pre-feature recordings replay
    /// faithfully; the server turns it on for every live session (and its rewinds), and it is
    /// captured in the recording's snapshots so replays reproduce the same variety.
    /// </summary>
    public bool FinalApproachSpeedVarietyEnabled { get; set; }

    public bool HasSoloParkingInitialCallupSource { get; set; }

    public bool HasSoloArrivalGeneratorSource { get; set; }

    public double NextSoloParkingInitialCallupSlotSeconds { get; set; }

    // When true (and SoloTrainingMode is false), sim-initiated pilot transmissions
    // (RTIS/RFIS resolution, midfield/short-final-no-clearance reminders, holding-short and
    // clear-of-runway position reports, going-around, lost-sight, follow-cancel, etc.) are
    // routed into AircraftState.PendingPilotSpeech and broadcast as TerminalEntryKind.PilotSpeech
    // (green) with the spoken form built by PilotResponder. When false (default), those events
    // continue to land in PendingWarnings (orange) — current behavior preserved.
    public bool RpoShowPilotSpeech { get; set; }

    // Weather timeline (v2 time-based weather evolution)
    public WeatherTimeline? WeatherTimeline { get; set; }

    // Discrete reported-METAR issuance (routine at :53, SPECI on significant change).
    // Null during replay/playback and whenever no dynamic re-issuance is active; the server tick
    // loop rebuilds it from MetarReissuanceEnabled when returning to live.
    public MetarIssuer? MetarIssuer { get; set; }

    // Whether dynamic METAR re-issuance is intended for the current weather (true for file/API
    // weather, false for live-fetched weather). Persisted so it survives snapshot restore, replay,
    // and recording load — MetarIssuer itself is runtime-only and torn down on every replay.
    public bool MetarReissuanceEnabled { get; set; }

    // The last-applied weather JSON (timeline or static profile). Persisted so RestoreFromSnapshot
    // can rebuild WeatherTimeline (which is otherwise lost on a snapshot-based rewind).
    public string? WeatherSourceJson { get; set; }

    // Scenario metadata
    public string? InitialWeatherJson { get; set; }
    public List<RecordedAction> ActionLog { get; } = [];

    // The room's broadcast terminal stream (commands, responses, SAY, warnings, chat, …) with each
    // entry's scenario-elapsed time. Persisted into the recording so a loaded recording repopulates
    // the full terminal faithfully and every line is a replay-scrub target. Appended only while live
    // (not during playback/reconstruction) so it captures the original session exactly once.
    public List<RecordedTerminalEntry> TerminalLog { get; } = [];
    public bool IsPlaybackMode { get; set; }
    public int PlaybackCursor { get; set; }
    public double PlaybackEndSeconds { get; set; }
    public string? ArtccId { get; set; }
    public string? ScenarioAutoDeleteMode { get; set; }
    public string? ClientAutoDeleteOverride { get; set; }
    public string? EffectiveAutoDeleteMode => ClientAutoDeleteOverride ?? ScenarioAutoDeleteMode;

    // Simulation control
    public bool IsPaused { get; set; } = true;
    public double SimRate { get; set; } = 1.0;

    // State snapshots loaded from a v2 recording (null for live sessions and v1 recordings)
    public List<TimedSnapshot>? LoadedSnapshots { get; set; }

    // On-demand snapshot provider backed by a v3 RecordingArchive (null for v1/v2 or live sessions).
    // When set, LoadedSnapshots is null — snapshots are loaded one at a time via this provider.
    public Func<int, TimedSnapshot>? SnapshotProvider { get; set; }

    // Number of snapshots available via SnapshotProvider (0 when SnapshotProvider is null)
    public int SnapshotCount { get; set; }

    // Keeps the v3 archive open for on-demand snapshot reads; disposed on scenario unload
    public RecordingArchive? SnapshotArchive { get; set; }

    // Handoff tracking
    public List<DelayedHandoff> DelayedHandoffQueue { get; } = [];

    // ATC positions
    public TrackOwner? StudentPosition { get; set; }
    public Tcp? StudentTcp { get; set; }
    public string? StudentPositionType { get; set; }
    public List<ResolvedAtcPosition> AtcPositions { get; set; } = [];

    // The current ATIS information letter for the primary field ("A".."Z"), the single source for
    // the "with information X" clause pilots append on initial contact. Static for the session
    // (defaults to "A"); not snapshotted because there is no runtime setter and restore mutates the
    // existing scenario object, leaving this untouched — like ArtccConfig, which drives whether the
    // clause is spoken at all (suppressed when the primary field has no ATIS position).
    public string AtisLetter { get; set; } = "A";

    // ARTCC config — populated by the server's ArtccConfigService at scenario load time, or
    // by SimulationEngine.Replay when the recording bundle includes a config snapshot.
    // Used by TrackResolver as a fallback when a TCP code isn't in StudentTcp/AtcPositions.
    // Not snapshotted — set by the loader / replay entry point and preserved across snapshot
    // restores (RestoreFromSnapshot leaves this field untouched).
    public ArtccConfigRoot? ArtccConfig { get; set; }

    // ARTCC-specific SOP exceptions for initial pilot contact transfer. Runtime data loaded
    // alongside navigation/custom data; not snapshotted.
    public InitialContactTransferCatalog InitialContactTransfers { get; set; } = InitialContactTransferCatalog.Empty;

    // ARTCC-specific static wake scoring directives and waivers. Runtime data loaded
    // alongside navigation/custom data; not snapshotted.
    public WakeDirectiveCatalog WakeDirectives { get; set; } = WakeDirectiveCatalog.Empty;

    /// <summary>
    /// Pilot-reaction "command run delay" range. When <see cref="CommandRunDelayMaxSeconds"/> &gt; 0,
    /// each pilot-actionable command takes effect a sampled [min, max] seconds after the controller
    /// issues it, simulating the time a pilot needs to set up the FMC / autopilot panel. 0/0 disables
    /// (the default — opt-in teaching tool). Equal min and max produce a fixed delay. The user-facing
    /// default lives in client UserPreferences (also 0) and is pushed to the server at scenario load.
    /// </summary>
    public int CommandRunDelayMinSeconds { get; set; }

    public int CommandRunDelayMaxSeconds { get; set; }

    // Timing and settings
    public TimeSpan AutoAcceptDelay { get; set; } = TimeSpan.FromSeconds(5);
    public bool IsStudentTowerPosition { get; set; }
    public Dictionary<string, CoordinationChannel> CoordinationChannels { get; set; } = [];

    public ScenarioSnapshotDto ToSnapshot() =>
        new()
        {
            ScenarioId = ScenarioId,
            ScenarioName = ScenarioName,
            RngSeed = RngSeed,
            PrimaryAirportId = PrimaryAirportId,
            ElapsedSeconds = ElapsedSeconds,
            AutoClearedToLand = AutoClearedToLand,
            AutoCrossRunway = AutoCrossRunway,
            AutoPullUpToParallel = AutoPullUpToParallel,
            ValidateDctFixes = ValidateDctFixes,
            SoloTrainingMode = SoloTrainingMode,
            SoloParkingInitialCallupRatePercent = SoloParkingInitialCallupRatePercent,
            SoloArrivalGeneratorRatePercent = SoloArrivalGeneratorRatePercent,
            SoloGoAroundProbabilityPercent = SoloGoAroundProbabilityPercent,
            FinalApproachSpeedVarietyEnabled = FinalApproachSpeedVarietyEnabled,
            HasSoloParkingInitialCallupSource = HasSoloParkingInitialCallupSource,
            HasSoloArrivalGeneratorSource = HasSoloArrivalGeneratorSource,
            NextSoloParkingInitialCallupSlotSeconds = NextSoloParkingInitialCallupSlotSeconds,
            RpoShowPilotSpeech = RpoShowPilotSpeech,
            MetarReissuanceEnabled = MetarReissuanceEnabled,
            WeatherSourceJson = WeatherSourceJson,
            IsPaused = IsPaused,
            SimRate = SimRate,
            CommandRunDelayMinSeconds = CommandRunDelayMinSeconds,
            CommandRunDelayMaxSeconds = CommandRunDelayMaxSeconds,
            AutoAcceptDelaySeconds = AutoAcceptDelay.TotalSeconds,
            IsStudentTowerPosition = IsStudentTowerPosition,
            ScenarioAutoDeleteMode = ScenarioAutoDeleteMode,
            ClientAutoDeleteOverride = ClientAutoDeleteOverride,
            ArtccId = ArtccId,
            StudentPosition = StudentPosition?.ToSnapshot(),
            StudentTcp = StudentTcp?.ToSnapshot(),
            StudentPositionType = StudentPositionType,
            DelayedQueue =
                DelayedQueue.Count > 0
                    ? DelayedQueue
                        .Select(d => new DelayedSpawnDto
                        {
                            AircraftJson = JsonSerializer.Serialize(d.Aircraft),
                            SpawnAtSeconds = d.SpawnAtSeconds,
                            HeldForRelease = d.HeldForRelease,
                        })
                        .ToList()
                    : null,
            TriggerQueue =
                TriggerQueue.Count > 0
                    ? TriggerQueue.Select(t => new ScheduledTriggerDto { Command = t.Command, FireAtSeconds = t.FireAtSeconds }).ToList()
                    : null,
            PresetQueue =
                PresetQueue.Count > 0
                    ? PresetQueue
                        .Select(p => new ScheduledPresetDto
                        {
                            Callsign = p.Callsign,
                            Command = p.Command,
                            FireAtSeconds = p.FireAtSeconds,
                        })
                        .ToList()
                    : null,
            Generators =
                Generators.Count > 0
                    ? Generators
                        .Select(g => new GeneratorStateDto
                        {
                            ConfigJson = JsonSerializer.Serialize(g.Config),
                            Runway = g.Runway.ToSnapshot(),
                            NextSpawnSeconds = g.NextSpawnSeconds,
                            IsExhausted = g.IsExhausted,
                        })
                        .ToList()
                    : null,
            DelayedHandoffQueue =
                DelayedHandoffQueue.Count > 0
                    ? DelayedHandoffQueue
                        .Select(h => new DelayedHandoffDto
                        {
                            Callsign = h.Callsign,
                            Target = h.Target.ToSnapshot(),
                            FireAtSeconds = h.FireAtSeconds,
                        })
                        .ToList()
                    : null,
            CoordinationChannels = CoordinationChannelSnapshotMapper.ToSnapshotDictionary(CoordinationChannels),
            HeldDepartureAirports = HeldDepartureAirports.Count > 0 ? HeldDepartureAirports.ToList() : null,
            ReleaseQueue =
                ReleaseQueue.Count > 0
                    ? ReleaseQueue
                        .Select(r => new ScheduledReleaseDto
                        {
                            Airport = r.Airport,
                            Callsign = r.Callsign,
                            FireAtSeconds = r.FireAtSeconds,
                        })
                        .ToList()
                    : null,
            ActiveTimers =
                ActiveTimers.Count > 0
                    ? ActiveTimers
                        .Select(t => new ActiveTimerDto
                        {
                            Id = t.Id,
                            Callsign = t.Callsign,
                            Message = t.Message,
                            FireAtSeconds = t.FireAtSeconds,
                            TotalSeconds = t.TotalSeconds,
                        })
                        .ToList()
                    : null,
            NextTimerId = NextTimerId,
        };
}
