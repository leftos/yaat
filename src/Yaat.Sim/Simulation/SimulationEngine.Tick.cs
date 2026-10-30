using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Situation;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// The bodies of the engine's own spine steps: pre-physics, the physics sub-tick, the detectors and the per-second
// passes. The order they run in is SpineOrder's; the segment entry points are in SimulationEngine.Spine.cs.
public sealed partial class SimulationEngine
{
    // Holds the set of hold-short node IDs currently occupied by aircraft.
    // Built at the start of each TickPhysics, used by PreTick to prevent stacking.
    private HashSet<int>? _occupiedHoldShortNodes;

    private static readonly ILogger ExitCapacityLog = SimLog.CreateLogger("ExitCapacity");

    // The aircraft inside each exit-capacity segment, built beside _occupiedHoldShortNodes for the same window.
    // Null when no aircraft is inside one.
    private Dictionary<ExitCapacitySegment, List<AircraftState>>? _exitSegmentOccupants;

    // Refusals already logged (arrival, exit bar, occupants), so a rollout that stays refused logs once per distinct
    // occupancy rather than every sub-tick. Only filled while Debug logging is on for the category; emptied on scenario
    // load and snapshot restore.
    private readonly HashSet<string> _loggedExitCapacityRefusals = [];

    /// <summary>
    /// Pre-physics: process delayed spawns, generators, triggers, timed presets,
    /// and ensure ground layout. Returns a list of aircraft spawned this tick.
    /// Terminal entries are accumulated and can be drained via <see cref="DrainTerminalEntries"/>.
    /// </summary>
    public TickPrePhysicsResult TickPrePhysics()
    {
        SimScenarioState? scenario = Scenario;
        if (scenario is null)
        {
            return new TickPrePhysicsResult([]);
        }

        var spawned = new List<AircraftState>();

        ProcessDelayedSpawns(spawned);
        ProcessGenerators(spawned);

        // The spawn hooks run before the result reaches the host, so what a spawn queues (its PDC) is engine state on
        // every run kind and the host's own spawn tail — the broadcast — still sees the callsign first.
        foreach (AircraftState state in spawned)
        {
            AfterAircraftSpawned(state);
        }

        ApplyArrivalSpacing();
        ApplySameRunwayArrivalProtection();
        ProcessTriggers();
        ProcessTimedPresets();
        ProcessReleaseQueue();
        ProcessTimers();
        ProcessReleasedGroundDepartures();
        ProcessRunwaySpawnAutoTakeoffs();
        ProcessSimulatedTowerDepartures();

        // Ensure ground layout is set
        if (scenario.PrimaryAirportId is not null && World.GroundLayout is null)
        {
            World.GroundLayout = _groundData.GetLayout(scenario.PrimaryAirportId);
        }

        // Refresh the per-hold-short departure-queue ordinals over the live world. The broadcast later this
        // second reads RunwayQueuePosition for the datablock "#N" and the Info-column status.
        RunwayDepartureQueue.UpdatePositions(World.GetSnapshot());

        return new TickPrePhysicsResult(spawned);
    }

    /// <summary>
    /// Physics step: runs FlightPhysics.Update and phase runner for all aircraft.
    /// Call multiple times per sim-second for sub-tick granularity.
    /// </summary>
    public void TickPhysics(double delta)
    {
        long start = TimingStart();
        _occupiedHoldShortNodes = BuildOccupiedHoldShortNodes(out _exitSegmentOccupants);
        TimingStop("Physics.BuildHoldShort", start);

        // Cache scenario mode flags onto the World so FlightPhysics → PilotObservationUpdater
        // can route resolved RTIS/RFIS pilot transmissions to the correct pending list.
        World.SoloTrainingMode = Scenario?.SoloTrainingMode ?? false;
        World.RpoShowPilotSpeech = Scenario?.RpoShowPilotSpeech ?? false;
        World.MagneticModelDateUtc = Scenario?.MagneticModelDateUtc ?? MagneticDeclination.EvaluationDateUtc;

        start = TimingStart();
        RehydrateRestoredQueueBlocks();
        TimingStop("Physics.RehydrateBlocks", start);

        start = TimingStart();
        World.Tick(delta, Scenario?.ElapsedSeconds ?? 0, PreTick, RecordWorldTiming);
        TimingStop("Physics.WorldTick", start);

        _occupiedHoldShortNodes = null;
        _exitSegmentOccupants = null;

        start = TimingStart();
        ProcessDeferredDispatches(delta);
        TimingStop("Physics.Deferred", start);

        start = TimingStart();
        ProcessTriggeredTrackBlocks();
        TimingStop("Physics.TrackBlocks", start);
    }

    /// <summary>
    /// Rebuilds the non-serialized <c>ParsedCommands</c>/<c>ApplyAction</c> halves of queued blocks that
    /// came back from a snapshot restore — without this a restored block reaches its turn inside
    /// <c>FlightPhysics.UpdateCommandQueue</c>, marks itself applied, and silently does nothing.
    /// Runs at the top of <see cref="TickPhysics"/> (BEFORE <c>World.Tick</c> fires the queue) because
    /// that is the one physics hook both the standalone sim/replay and the live server share, mirroring
    /// <see cref="ProcessTriggeredTrackBlocks"/>. Cheap when there is nothing to do: a live block has a
    /// non-null <c>ApplyAction</c> and is skipped by the first check.
    /// </summary>
    private void RehydrateRestoredQueueBlocks()
    {
        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            List<CommandBlock>? failed = null;
            foreach (CommandBlock block in aircraft.Queue.Blocks)
            {
                if (block.ApplyAction is not null || block.IsApplied)
                {
                    continue;
                }

                // Nothing can rebuild a block with no source text — it would reach its turn, mark itself
                // applied, and fire as a silent no-op. Treat it as unrecoverable and drop it below.
                if (string.IsNullOrEmpty(block.SourceCommandText))
                {
                    (failed ??= []).Add(block);
                    continue;
                }

                AirportGroundLayout? groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
                var ctx = new DispatchContext
                {
                    GroundLayout = groundLayout,
                    Rng = World.Rng,
                    Weather = World.Weather,
                    FindAircraft = FindAircraft,
                    ListAircraft = () => World.GetSnapshot(),
                    ValidateDctFixes = Scenario?.ValidateDctFixes ?? true,
                    AutoCrossRunwayMode = Scenario?.AutoCrossRunwayMode ?? AutoCrossRunwayMode.Off,
                    SoloTrainingMode = Scenario?.SoloTrainingMode ?? false,
                    SoloRpoCommandsAllowed = Scenario?.SoloRpoCommandsAllowed ?? false,
                    RpoShowPilotSpeech = Scenario?.RpoShowPilotSpeech ?? false,
                    TerminalEmitter = AddTerminalEntry,
                    ArtccConfig = Scenario?.ArtccConfig,
                    AsdexSafetyLogicConfigs = Scenario?.AsdexSafetyLogicConfigs ?? AsdexSafetyLogicSeed.Empty,
                    ToweredAirports = Scenario?.ToweredAirports ?? ToweredAirports.Empty,
                    ActiveRunways = Scenario?.ActiveRunways ?? ActiveRunways.Empty,
                    ScenarioElapsedSeconds = Scenario?.ElapsedSeconds ?? 0,
                    SessionStartUtc = Scenario?.SessionStartUtc ?? SimScenarioState.ProcessDayUtc,
                    PreserveConditionals = true,
                    IsScenarioScripted = block.IsScenarioScripted,
                    FacilityHint = Scenario?.StudentPosition?.FacilityId,
                    LayoutForAirport = ResolveAirportLayout,
                    SpeechContextFor = ac => PilotSpeechContextFor(ac, ac.HasControllerAcknowledgedInitialContact),
                };

                if (!CommandDispatcher.RehydrateRestoredBlock(block, aircraft, ctx))
                {
                    (failed ??= []).Add(block);
                }
            }

            if (failed is null)
            {
                continue;
            }

            DropUnrehydratableBlocks(aircraft, failed);
        }
    }

    /// <summary>
    /// Drops the blocks a restore could not rebuild and tells the RPO: a block that cannot be recovered would fire as
    /// a silent no-op, so it is named rather than left looking like a pending instruction.
    /// </summary>
    private void DropUnrehydratableBlocks(AircraftState aircraft, List<CommandBlock> failed)
    {
        foreach (CommandBlock block in failed)
        {
            string source = string.IsNullOrEmpty(block.SourceCommandText) ? "(no source text)" : block.SourceCommandText;
            _logger.LogWarning(
                "[Restore] {Callsign}: could not rehydrate queued block '{Description}' from '{Source}' — dropping it",
                aircraft.Callsign,
                block.Description,
                source
            );
            aircraft.PendingWarnings.Add($"{aircraft.Callsign} queued command lost after restore: {block.Description}");
            aircraft.Queue.Blocks.Remove(block);
        }

        if (aircraft.Queue.CurrentBlockIndex >= aircraft.Queue.Blocks.Count)
        {
            aircraft.Queue.CurrentBlockIndex = Math.Max(0, aircraft.Queue.Blocks.Count - 1);
        }
    }

    private void RecordWorldTiming(string bucket, double ms)
    {
        if (TickTimings is not { } timings)
        {
            return;
        }

        if (timings.TryGetValue(bucket, out (int Count, double Ms) entry))
        {
            timings[bucket] = (entry.Count + 1, entry.Ms + ms);
        }
        else
        {
            timings[bucket] = (1, ms);
        }
    }

    /// <summary>
    /// Per-second post-physics pilot-proactive behaviors: solo check-in, arrival approach request,
    /// airspace-boundary respect, pending-request follow-ups, and — in all modes — deferred REPORT
    /// triggers. A spine step (<see cref="Spine.StepId.PilotProactive"/>), so it runs on every run kind.
    /// Airborne check-in fires before the drains so it emits the same second it is produced;
    /// TickReportTriggers runs in solo and RPO mode, so it sits outside the solo gate.
    /// </summary>
    public void TickPilotProactive()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        bool solo = scenario.SoloTrainingMode;
        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (ac.IsShadow)
            {
                continue;
            }

            // The radar-side proactive calls stay student-only until the AI approach/center brains exist; the
            // request follow-ups gate themselves on whether anyone (student or AI position) answers pilots.
            if (solo)
            {
                Pilot.PilotProactive.TickAirborneCheckIn(ac, scenario, LookupAirportPosition);
                Pilot.PilotProactive.TickArrivalApproachRequest(ac, scenario, LookupAirportPosition);
                Pilot.PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirportPosition);
            }

            Pilot.PilotProactive.TickPendingRequests(ac, scenario);
            Pilot.PilotProactive.TickReportTriggers(ac, scenario);
        }
    }

    /// <summary>
    /// The post-physics situation pass: each aircraft, shadows included, is classified into
    /// <see cref="AircraftSituationState.Current"/> with the stored value as the previous one, which the classifier's
    /// hysteresis bands read. First it stamps <see cref="AircraftSituationState.AirborneAtSeconds"/> when an aircraft the
    /// last step saw on the ground is now airborne, so every liftoff counts, including one begun inside command dispatch
    /// and a second departure. Then it computes <see cref="AircraftSituationState.Flags"/>
    /// (<see cref="SituationFlagCalculator"/>) against the new situation, the stored flags, the aircraft's ground
    /// layout (resolved only while holding short or in the taxiing, holding-on-ground and rollout/exit situations: taxi
    /// and hold-short flags) and its assigned runway's own airport layout (inside-FAF), and beside them
    /// <see cref="AircraftSituationState.NextCrossingRunway"/> from the same ground layout, and <see cref="AircraftSituationState.ExitsAhead"/>
    /// (<see cref="ExitsAheadOf"/>). Last it stores <see cref="AircraftSituationState.MeetPoints"/>, null for every aircraft but
    /// those <see cref="GroundConflictDetector.MeetCandidates"/> lists, each paired against the others in world order
    /// (<see cref="GroundConflictDetector.MeetPointsOf"/>, read-only). A spine step (<see cref="Spine.StepId.Situation"/>) after
    /// the pilot-proactive step, so it sees this second's phase changes; it runs on every run kind because the stored situation is snapshotted state
    /// that depends on the previous second's.
    /// </summary>
    public void TickSituation()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        List<AircraftState> traffic = World.GetSnapshot();
        foreach (AircraftState ac in traffic)
        {
            AircraftSituationState situation = ac.Situation;
            if ((!ac.IsOnGround) && situation.WasOnGround)
            {
                situation.AirborneAtSeconds = scenario.ElapsedSeconds;
            }

            situation.WasOnGround = ac.IsOnGround;
            situation.Current = SituationClassifier.Classify(ac, scenario.ElapsedSeconds, situation.Current);
            // Layouts are resolved only for the phases that read them: a resolve can fetch and pin an airport's map.
            Phase? phase = ac.Phases?.CurrentPhase;
            AirportGroundLayout? groundLayout = SituationFlagCalculator.NeedsGroundLayout(situation.Current, phase)
                ? (ac.Ground.Layout ?? ResolveGroundLayout(ac))
                : null;
            AirportGroundLayout? runwayLayout =
                (SituationFlagCalculator.IsInstrumentApproachPhase(phase) && (ac.Phases?.AssignedRunway is { } runway))
                    ? ResolveAirportLayout(runway.AirportId)
                    : null;
            situation.Flags = SituationFlagCalculator.Compute(ac, situation.Current, situation.Flags, groundLayout, runwayLayout);
            situation.NextCrossingRunway = SituationFlagCalculator.NextCrossingRunway(ac, situation.Current, groundLayout);
            situation.ExitsAhead = ExitsAheadOf(ac, phase, runwayLayout, World.Weather, scenario.ElapsedSeconds);
            situation.MeetPoints = null;
        }

        List<GroundMeetCandidate> candidates = GroundConflictDetector.MeetCandidates(traffic);
        foreach (GroundMeetCandidate own in candidates)
        {
            own.Aircraft.Situation.MeetPoints = GroundConflictDetector.MeetPointsOf(own, candidates);
        }
    }

    /// <summary>
    /// The named exits ahead for <see cref="AircraftSituationState.ExitsAhead"/>: the landing's own list while it rolls out
    /// (<see cref="LandingPhase.ListExitsAhead(AircraftState)"/>), the touchdown forecast on final
    /// (<see cref="FinalApproachExitForecast.ListExitsAhead"/>, from the assigned runway's own layout), and null in every other phase —
    /// <see cref="RunwayExitPhase"/> included. Both are read-only, so the step changes no tick.
    /// </summary>
    private static IReadOnlyList<ExitAheadDto>? ExitsAheadOf(
        AircraftState ac,
        Phase? phase,
        AirportGroundLayout? runwayLayout,
        WeatherProfile? weather,
        double elapsedSeconds
    ) =>
        phase switch
        {
            LandingPhase landing => landing.ListExitsAhead(ac),
            FinalApproachPhase => FinalApproachExitForecast.ListExitsAhead(ac, runwayLayout, weather, elapsedSeconds),
            _ => null,
        };

    /// <summary>
    /// Per-second transponder maintenance: advances each aircraft's IDENT timer so the ident flash
    /// auto-clears after <see cref="AircraftTransponder.IdentDurationSeconds"/>, and latches the
    /// has-reported-Mode-C flag CRC's lost-Mode-C indication reads. A spine step
    /// (<see cref="Spine.StepId.Transponders"/>), so it runs on every run kind.
    /// </summary>
    public void TickTransponders()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        foreach (AircraftState ac in World.GetSnapshot())
        {
            ac.Transponder.Tick(scenario.ElapsedSeconds);
        }
    }

    /// <summary>The distance within which an aircraft closes on and passes an altitude fix (<see cref="TickAltitudeFixPassage"/>).</summary>
    public const double AltitudeFixPassageRadiusNm = 10.0;

    /// <summary>
    /// Latches <see cref="AircraftFlightPlan.AltitudeFixPassed"/> for each airborne aircraft whose flight-plan altitude is
    /// fix-qualified (<c>170/SJC/110</c>, ERAM <c>AM ALT</c>) once it has passed the fix abeam after closing on it: first the
    /// fix is within <see cref="AltitudeFixPassageRadiusNm"/> and no more than 90° off the aircraft's track
    /// (<see cref="AircraftFlightPlan.AltitudeFixApproached"/>), then more than 90° off it. A fix already behind when the
    /// altitude is entered, an aircraft on the ground and a fix that does not resolve never latch. Data only — the ERAM data
    /// block and QF then show the altitude after the fix; nothing the pilot flies changes. A spine step
    /// (<see cref="Spine.StepId.AltitudeFixPassage"/>), so it runs on every run kind.
    /// </summary>
    public void TickAltitudeFixPassage()
    {
        if (Scenario is null)
        {
            return;
        }

        foreach (AircraftState ac in World.GetSnapshot())
        {
            AircraftFlightPlan plan = ac.FlightPlan;
            if (ac.IsOnGround || plan.AltitudeFixPassed || (plan.AltitudeFixPosition(NavigationDatabase.Instance) is not { } fix))
            {
                continue;
            }
            AdvanceAltitudeFixPassage(ac, fix);
        }
    }

    private void AdvanceAltitudeFixPassage(AircraftState ac, LatLon fix)
    {
        AircraftFlightPlan plan = ac.FlightPlan;
        if (GeoMath.DistanceNm(ac.Position, fix) > AltitudeFixPassageRadiusNm)
        {
            return;
        }

        bool fixBehind = ac.TrueTrack.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(ac.Position, fix))) > 90.0;
        if (!fixBehind)
        {
            plan.AltitudeFixApproached = true;
            return;
        }
        if (!plan.AltitudeFixApproached)
        {
            return;
        }

        plan.AltitudeFixPassed = true;
        _logger.LogDebug(
            "{Callsign} passed altitude fix {Fix}; ERAM altitude now {Feet} ft",
            ac.Callsign,
            plan.Altitude.AltitudeFix,
            plan.EramAltitudeFeet
        );
    }

    /// <summary>
    /// Per-second visual-acquisition update for aircraft on a visual approach (ApproachId starts with
    /// <c>VIS</c>): field-in-sight acquisition/loss and FOLLOW traffic-in-sight acquisition/loss, using the
    /// active <see cref="SimulationWorld.Weather"/> (cloud layers + visibility) and the nav DB. Sets
    /// <c>Approach.HasReported*InSight</c> and enqueues the pilot/RPO transmissions + notifications the spine
    /// drains later in post-physics. A spine step (<see cref="Spine.StepId.VisualDetection"/>), so it runs on
    /// every run kind.
    /// </summary>
    public void TickVisualDetection()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        List<AircraftState> snapshot = World.GetSnapshot();
        WeatherProfile? weather = World.Weather;

        foreach (AircraftState ac in snapshot)
        {
            // Only check aircraft on a visual approach (ApproachId starts with VIS)
            if (ac.Phases?.ActiveApproach is not { } approach || !approach.ApproachId.StartsWith("VIS", StringComparison.Ordinal))
            {
                continue;
            }

            string airport = approach.AirportCode;
            double? aptElevation = NavigationDatabase.Instance.GetAirportElevation(airport);
            if (aptElevation is null)
            {
                continue;
            }

            (double Lat, double Lon)? aptPos = NavigationDatabase.Instance.GetAirportPosition(airport);
            if (aptPos is null)
            {
                continue;
            }

            // Get cloud layers / visibility from METAR
            IReadOnlyList<MetarParser.CloudLayer>? layers = null;
            double? visibilitySm = null;
            int? primaryCeilingForLogs = null;
            if (weather is not null)
            {
                MetarParser.ParsedMetar? metarData = weather.GetWeatherForAirport(airport);
                layers = metarData?.Layers;
                visibilitySm = metarData?.VisibilityStatuteMiles;
                primaryCeilingForLogs = metarData?.CeilingFeetAgl;
            }

            // Field in sight check (with runway direction awareness)
            // Bank angle affects initial acquisition only — once acquired, pilot can track through turns.
            RunwayInfo? runway = ac.Phases.AssignedRunway;
            double airportSizeCapNm = VisualAcquisition.AirportSizeCapNm(airport);
            bool fieldLostThisTick = false;

            if (!ac.Approach.HasReportedFieldInSight)
            {
                // Initial acquisition: use actual bank angle
                VisualAcquisitionResult acquireResult = runway is not null
                    ? VisualDetection.TryAcquireAirportForRunway(
                        ac,
                        aptPos.Value.Lat,
                        aptPos.Value.Lon,
                        aptElevation.Value,
                        layers,
                        visibilitySm,
                        runway.TrueHeading,
                        ac.BankAngle,
                        airportSizeCapNm
                    )
                    : VisualDetection.TryAcquireAirport(
                        ac,
                        aptPos.Value.Lat,
                        aptPos.Value.Lon,
                        aptElevation.Value,
                        layers,
                        visibilitySm,
                        ac.BankAngle,
                        airportSizeCapNm
                    );

                if (acquireResult.Acquired)
                {
                    ac.Approach.HasReportedFieldInSight = true;
                    ac.PendingNotifications.Add($"{ac.Callsign} has the field in sight");
                    _logger.LogInformation(
                        "Field acquisition: {Callsign} acquired {Airport} at t={T}s (alt={Alt}ft, vis={Vis}sm, ceil={Ceil}ft AGL)",
                        ac.Callsign,
                        airport,
                        scenario.ElapsedSeconds,
                        ac.Altitude,
                        visibilitySm,
                        primaryCeilingForLogs
                    );
                }
                else
                {
                    _logger.LogDebug(
                        "Field acquisition attempt failed: {Callsign} cannot see {Airport} at t={T}s (alt={Alt}ft, "
                            + "vis={Vis}sm, ceil={Ceil}ft AGL, bank={Bank}°)",
                        ac.Callsign,
                        airport,
                        scenario.ElapsedSeconds,
                        ac.Altitude,
                        visibilitySm,
                        primaryCeilingForLogs,
                        ac.BankAngle
                    );
                }
            }
            else
            {
                // Maintained contact: weather-only check. The finding-geometry checks
                // (BehindOwnship/OppositeSideOfRunway and the horizon/conspicuity range
                // caps) produce false "lost sight of the field" reports as the aircraft
                // crosses the threshold and the airport reference point falls behind the
                // nose. Once acquired, only weather — Class A, a BKN/OVC layer above the
                // aircraft, or a flight-visibility collapse below the distance to the
                // field — can realistically obscure the airport polygon. The visibility
                // distance is the MINIMUM over the ARP (what acquisition measured — the
                // maintain datum must never exceed it, or maintain manufactures a loss
                // acquisition would not have allowed) and the assigned runway threshold
                // (at a sprawling field the ARP can sit beyond the visibility range while
                // the landing runway is right off the nose).
                double fieldDistanceNm = GeoMath.DistanceNm(ac.Position, new LatLon(aptPos.Value.Lat, aptPos.Value.Lon));
                if (runway is not null && NavigationDatabase.AirportIdsMatch(runway.AirportId, airport))
                {
                    double thresholdDistanceNm = GeoMath.DistanceNm(ac.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
                    fieldDistanceNm = Math.Min(fieldDistanceNm, thresholdDistanceNm);
                }

                VisualAcquisitionResult maintainResult = VisualDetection.TryMaintainAirportContact(
                    ac,
                    aptElevation.Value,
                    layers,
                    visibilitySm,
                    fieldDistanceNm
                );

                if (!maintainResult.Acquired)
                {
                    ac.Approach.HasReportedFieldInSight = false;
                    fieldLostThisTick = true;
                    _logger.LogWarning(
                        "Field loss: {Callsign} lost sight of {Airport} at t={T}s (alt={Alt}ft, vis={Vis}sm, ceil={Ceil}ft AGL)",
                        ac.Callsign,
                        airport,
                        scenario.ElapsedSeconds,
                        ac.Altitude,
                        visibilitySm,
                        primaryCeilingForLogs
                    );

                    // AIM §5-5-11.a.3 disjunction: still following with the traffic in sight →
                    // legal steady state (§7-4-3.c.2 NOTE — the field report was never required
                    // on a FOLLOW clearance), stay silent on frequency. Otherwise the field was
                    // the only reference — the visual is no longer legal and ends here.
                    bool trafficStillHeld = (ac.Approach.FollowingCallsign is not null) && ac.Approach.HasReportedTrafficInSight;
                    if (!trafficStillHeld)
                    {
                        VisualApproachHelper.EndVisualLostReference(BuildPhaseContext(ac, 0.0), true, false, null);
                    }
                }
            }

            // Traffic in sight check (for FOLLOW variant). Weather for BOTH the acquire
            // and maintain paths comes from the air mass the follower is flying in — the
            // nearest reporting station to its position, via the VisualAcquisition
            // wrappers — never from the destination METAR used by the field branch above.
            // AirborneFollowHelper.CheckLeadLifecycle and PilotObservationUpdater use the
            // same wrappers; a destination-sourced check here would judge the same
            // follower against two different cloud decks/visibilities in the same tick.
            if (ac.Approach.FollowingCallsign is { } followCs)
            {
                AircraftState? target = snapshot.FirstOrDefault(t => t.Callsign.Equals(followCs, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                {
                    if (!ac.Approach.HasReportedTrafficInSight)
                    {
                        VisualAcquisitionResult acquireTrafficResult = VisualAcquisition.TryAcquireTraffic(ac, target, weather);
                        if (acquireTrafficResult.Acquired)
                        {
                            ac.Approach.HasReportedTrafficInSight = true;
                            // Stamp the identity with the report — the CVA FOLLOW gate verifies
                            // the report names the traffic being followed, and a bare FOLLOW
                            // defaults to the last-reported callsign.
                            ac.Approach.LastReportedTrafficCallsign = followCs.ToUpperInvariant();
                            PilotResponder.RouteRpoTransmission(
                                ac,
                                scenario.SoloTrainingMode,
                                scenario.RpoShowPilotSpeech,
                                PilotResponder.BuildTrafficInSight(ac, followCs)
                            );
                            _logger.LogInformation(
                                "Traffic acquisition: {Callsign} acquired {Target} at t={T}s (dist={Dist}nm, maxRange={MaxRange}nm)",
                                ac.Callsign,
                                followCs,
                                scenario.ElapsedSeconds,
                                acquireTrafficResult.DistanceNm,
                                acquireTrafficResult.MaxRangeNm
                            );
                        }
                    }
                    else
                    {
                        // Maintained contact: weather-only check. The type-detection-range /
                        // forward-hemisphere / bank-occlusion geometry models FINDING unknown
                        // traffic, not TRACKING traffic already called in sight; re-applying it
                        // here produces false "lost sight of traffic" reports as the lead merely
                        // pulls ahead (a growing gap increases separation — the controller's to
                        // re-sequence, never the follower's cue to break off; AIM §5-5-12.a.2 /
                        // §4-4-14 NOTE). A flight-visibility collapse below the gap DOES break
                        // contact — that is weather, not finding-geometry (AIM §5-5-11.a.3).
                        // Mirrors the field-maintain path above and AirborneFollowHelper.
                        VisualAcquisitionResult maintainTrafficResult = VisualAcquisition.TryMaintainTrafficContact(ac, target, weather);
                        if (!maintainTrafficResult.Acquired)
                        {
                            ac.Approach.HasReportedTrafficInSight = false;
                            _logger.LogWarning(
                                "Traffic loss: {Callsign} lost sight of {Target} at t={T}s ({Reason})",
                                ac.Callsign,
                                followCs,
                                scenario.ElapsedSeconds,
                                maintainTrafficResult.Reason
                            );
                            // Consequence (transmission, follow handback, or end-of-visual when
                            // nothing else is in sight) is owned by VisualApproachHelper — the
                            // same handler CheckLeadLifecycle routes to, so the loss event gets
                            // one outcome regardless of which detector saw it first.
                            VisualApproachHelper.HandleTrafficContactLost(BuildPhaseContext(ac, 0.0), followCs, fieldLostThisTick);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "Traffic acquisition attempt: {Callsign} following {Target}, but target not found at t={T}s",
                        ac.Callsign,
                        followCs,
                        scenario.ElapsedSeconds
                    );
                }
            }

            // AIM §5-5-11.a.3 backstop: a visual approach with NOTHING in sight and no loss
            // event this tick still ends. Reached when the follow was torn down elsewhere
            // (e.g. the lead landed under CheckLeadLifecycle while the field was never
            // reported — the acquisition attempt above got its chance first) or when
            // restored state carries a clearance with no basis. The loss-event paths above
            // void the clearance when they fire, so this only sees aircraft they skipped.
            if (
                (ac.Phases?.ActiveApproach?.ApproachId.StartsWith("VIS", StringComparison.Ordinal) == true)
                && (!ac.IsOnGround)
                && (ac.Phases.CurrentPhase is not GoAroundPhase)
                && (!ac.Approach.HasReportedFieldInSight)
                && (!((ac.Approach.FollowingCallsign is not null) && ac.Approach.HasReportedTrafficInSight))
            )
            {
                _logger.LogWarning(
                    "Visual reference lost: {Callsign} has neither the field nor traffic in sight on {Approach} at t={T}s — ending the visual",
                    ac.Callsign,
                    ac.Phases.ActiveApproach.ApproachId,
                    scenario.ElapsedSeconds
                );
                VisualApproachHelper.EndVisualLostReference(BuildPhaseContext(ac, 0.0), false, false, null);
            }
        }
    }

    /// <summary>
    /// The facility's internal airports, whose approach corridors <see cref="TickConflictAlerts"/> suppresses
    /// alerts inside. Resolved from scenario state rather than passed in, so every run kind classifies
    /// conflicts identically: the argument used to come from the server's STARS config, and the paths that
    /// had no host to supply one alerted where live did not. Empty when the scenario carries no ARTCC config
    /// (a bare test engine) or no student position, which is the same set the argument defaulted to.
    /// </summary>
    private IReadOnlyList<string> ResolveInternalAirports() =>
        Scenario is { ArtccConfig: { } config, StudentPosition.FacilityId: { } facilityId }
            ? config.GetStarsConfigForFacility(facilityId)?.InternalAirports ?? []
            : [];

    /// <summary>
    /// Per-second terminal Conflict Alert (STARS CA) pass: runs <see cref="ConflictAlertDetector"/> against
    /// the current world, updates the engine-owned <see cref="ConflictAlerts"/> set, and returns the pairs
    /// that opened and closed this tick. A spine step (<see cref="Spine.StepId.ConflictAlerts"/>): the detection
    /// runs on every run kind, or a restored conflict is never re-examined, and the host consumes the diff —
    /// only a broadcasting host does anything with it.
    /// </summary>
    public ConflictAlertChanges TickConflictAlerts()
    {
        List<AircraftState> snapshot = World.GetSnapshot();
        Dictionary<string, ActiveConflict> conflicts = ConflictAlerts.Conflicts;
        var existingIds = new HashSet<string>(conflicts.Keys);
        IReadOnlyList<RunwayCorridor> corridors = ConflictAlertDetector.BuildCorridors(ResolveInternalAirports(), NavigationDatabase.Instance);
        var context = new ConflictAlertContext(ExistingConflictIds: existingIds, ApproachCorridors: corridors);

        List<ConflictAlertDetector.ConflictPair> detected = ConflictAlertDetector.Detect(snapshot, context);
        var detectedIds = new HashSet<string>(detected.Select(c => c.Id));

        var newConflicts = new List<ActiveConflict>();
        foreach (ConflictAlertDetector.ConflictPair pair in detected)
        {
            if (!conflicts.ContainsKey(pair.Id))
            {
                OpenConflictAlert(pair, conflicts, newConflicts);
            }
        }

        var clearedIds = new List<string>();
        foreach (string id in existingIds)
        {
            if (!detectedIds.Contains(id))
            {
                ActiveConflict cleared = conflicts[id];
                conflicts.Remove(id);
                clearedIds.Add(id);
                if (cleared.Suppressed)
                {
                    ConflictAlerts.LatchedSuppressions[id] = new LatchedConflictSuppression(cleared.CallsignA, cleared.CallsignB);
                }

                _logger.LogInformation(
                    "Conflict alert cleared: {CallsignA} <-> {CallsignB} at t={T}s",
                    cleared.CallsignA,
                    cleared.CallsignB,
                    Scenario?.ElapsedSeconds ?? 0
                );
            }
        }

        ExpireSuppressionLatches(snapshot, corridors);
        (List<string> suppressedIds, List<ActiveConflict> restored) = TakeConflictSuppressionChanges(conflicts);
        return new ConflictAlertChanges(newConflicts, clearedIds, suppressedIds, restored);
    }

    /// <summary>
    /// Opens the alert for a newly detected pair. A pair whose <c>CASUP</c> suppression is latched from an earlier alert in
    /// the same encounter re-uses it: the alert opens suppressed, acknowledged and already published as such, so the host
    /// hears nothing; any other pair opens as a new alert.
    /// </summary>
    private void OpenConflictAlert(
        ConflictAlertDetector.ConflictPair pair,
        Dictionary<string, ActiveConflict> conflicts,
        List<ActiveConflict> newConflicts
    )
    {
        bool latched = ConflictAlerts.LatchedSuppressions.Remove(pair.Id);
        var conflict = new ActiveConflict
        {
            Id = pair.Id,
            CallsignA = pair.CallsignA,
            CallsignB = pair.CallsignB,
            IsAcknowledged = latched,
            Suppressed = latched,
            PublishedSuppressed = latched,
        };
        conflicts[pair.Id] = conflict;
        if (latched)
        {
            _logger.LogDebug("Conflict alert re-detected under a latched CASUP: {CallsignA} <-> {CallsignB}", pair.CallsignA, pair.CallsignB);
            return;
        }

        newConflicts.Add(conflict);
        _logger.LogWarning(
            "Conflict alert detected: {CallsignA} <-> {CallsignB} at t={T}s",
            pair.CallsignA,
            pair.CallsignB,
            Scenario?.ElapsedSeconds ?? 0
        );
    }

    /// <summary>Drops each latched <c>CASUP</c> suppression whose pair left the hysteresis box or stopped being eligible.</summary>
    private void ExpireSuppressionLatches(List<AircraftState> snapshot, IReadOnlyList<RunwayCorridor> corridors)
    {
        foreach ((string id, LatchedConflictSuppression latch) in ConflictAlerts.LatchedSuppressions.ToList())
        {
            AircraftState? a = snapshot.Find(ac => ac.Callsign == latch.CallsignA);
            AircraftState? b = snapshot.Find(ac => ac.Callsign == latch.CallsignB);
            if ((a is null) || (b is null) || !ConflictAlertDetector.IsSuppressionLatchHeld(a, b, corridors))
            {
                ConflictAlerts.LatchedSuppressions.Remove(id);
            }
        }
    }

    /// <summary>
    /// The alerts whose <see cref="ActiveConflict.Suppressed"/> flag a <c>CASUP</c> changed since the last pass, split into
    /// the suppressed ids and the restored alerts; each is marked published. A new alert starts with both flags false, so
    /// only an alert that already existed can appear here.
    /// </summary>
    private static (List<string> Suppressed, List<ActiveConflict> Restored) TakeConflictSuppressionChanges(
        Dictionary<string, ActiveConflict> conflicts
    )
    {
        var suppressed = new List<string>();
        var restored = new List<ActiveConflict>();
        foreach (ActiveConflict conflict in conflicts.Values)
        {
            if (conflict.Suppressed == conflict.PublishedSuppressed)
            {
                continue;
            }

            conflict.PublishedSuppressed = conflict.Suppressed;
            if (conflict.Suppressed)
            {
                suppressed.Add(conflict.Id);
            }
            else
            {
                restored.Add(conflict);
            }
        }

        return (suppressed, restored);
    }

    /// <summary>Seconds between ERAM conflict-alert passes; a pass runs when the scenario clock is a multiple of it.</summary>
    public const int EramConflictPassSeconds = 5;

    /// <summary>
    /// ERAM Short-Term Conflict Alert pass (docs/crc/eram.md §377-383) — the en-route sibling of
    /// <see cref="TickConflictAlerts"/>. Every <see cref="EramConflictPassSeconds"/> seconds of scenario time it runs
    /// <see cref="EramConflictDetector"/>, classifies each pair (<see cref="ClassifyEramPair"/>), updates the
    /// engine-owned <see cref="EramConflicts"/> set, and returns the pairs that opened and closed. Every second, pass or
    /// not, it reports the <c>CO</c> suppressions and restores. A spine step, like <see cref="TickConflictAlerts"/>.
    /// </summary>
    public EramConflictAlertChanges TickEramConflictAlerts()
    {
        Dictionary<string, EramActiveConflict> conflicts = EramConflicts.Conflicts;
        if (((int)(Scenario?.ElapsedSeconds ?? 0) % EramConflictPassSeconds) != 0)
        {
            (List<string> offPassSuppressed, List<EramActiveConflict> offPassRestored) = TakeEramSuppressionChanges(conflicts);
            return new EramConflictAlertChanges([], [], offPassSuppressed, offPassRestored, []);
        }

        List<AircraftState> snapshot = World.GetSnapshot();
        var existingIds = new HashSet<string>(conflicts.Keys);

        List<EramConflictDetector.ConflictPair> detected = EramConflictDetector.Detect(snapshot, existingIds);
        var detectedIds = new HashSet<string>(detected.Select(c => c.Id));
        Dictionary<string, EramPairSide> sides = IndexEramPairSides(snapshot);

        var newConflicts = new List<EramActiveConflict>();
        var reclassified = new List<EramActiveConflict>();
        foreach (EramConflictDetector.ConflictPair pair in detected)
        {
            EramPairSide a = sides[pair.CallsignA];
            EramPairSide b = sides[pair.CallsignB];
            (bool keep, string? intruder) = ClassifyEramPair(pair, a, b);
            if (!keep)
            {
                detectedIds.Remove(pair.Id);
                continue;
            }

            (EramActiveConflict conflict, EramUpsert upsert) = UpsertEramConflict(pair, a, b, intruder);
            if (upsert == EramUpsert.Opened)
            {
                newConflicts.Add(conflict);
            }
            else if (upsert == EramUpsert.Reclassified)
            {
                reclassified.Add(conflict);
            }
        }

        List<string> clearedIds = ClearUndetectedEramConflicts(existingIds, detectedIds, conflicts);
        (List<string> suppressedIds, List<EramActiveConflict> restored) = TakeEramSuppressionChanges(conflicts);
        return new EramConflictAlertChanges(newConflicts, clearedIds, suppressedIds, restored, reclassified);
    }

    /// <summary>One aircraft's side of an ERAM pair: its ERAM owner facility, whether it is tracked, and whether it is correlated.</summary>
    private readonly record struct EramPairSide(AircraftState Aircraft, string? Facility, bool Tracked, bool Correlated);

    /// <summary>
    /// Indexes the snapshot by callsign for pair classification. Correlation (<see cref="TrackCorrelation.IsEramCorrelated"/>)
    /// is a filed flight plan on a target that is tracked or squawking its assigned beacon code: before a track exists only
    /// the assigned code correlates (7110.65 §5-2-1b2 NOTE) and the non-discrete VFR code 1200 never starts a correlation
    /// (AIM §4-1-20); a track keeps the association through a code change, 1200 included (§5-4-6e2 NOTE). It is the same
    /// rule the ERAM target symbol uses.
    /// </summary>
    private static Dictionary<string, EramPairSide> IndexEramPairSides(List<AircraftState> snapshot)
    {
        var sides = new Dictionary<string, EramPairSide>(snapshot.Count);
        foreach (AircraftState ac in snapshot)
        {
            string? facility = ac.Track.Owner is { OwnerType: TrackOwnerType.Eram, FacilityId: { } id } ? id : null;
            sides[ac.Callsign] = new EramPairSide(ac, facility, ac.Track.Owner is not null, TrackCorrelation.IsEramCorrelated(ac));
        }

        return sides;
    }

    /// <summary>
    /// Whether a detected pair alerts, and its Mode-C intruder. Two untracked returns never alert: a conflict alert
    /// protects a controlled aircraft (7110.65 §2-1-6, §5-13-1) and the §377 facility gate needs a target owned in some
    /// ERAM facility. The Mode-C intruder (CDB, "TFC"+beacon, eram.md §844-852) is the side that is both untracked and
    /// uncorrelated; a correlated-but-unowned target is not one and flashes an ordinary data block. A pair with an
    /// intruder alerts only while the intruder is inside the MCI altitude band of the tracked side's facility
    /// (<see cref="EramConflictDetector.IsMciAltitudeEligible"/>), so an intruder that leaves the band clears the alert.
    /// </summary>
    private (bool Keep, string? Intruder) ClassifyEramPair(EramConflictDetector.ConflictPair pair, EramPairSide a, EramPairSide b)
    {
        if (!a.Tracked && !b.Tracked)
        {
            return (false, null);
        }

        string? intruder = PickEramIntruder(pair, a, b);
        if (intruder is null)
        {
            return (true, null);
        }

        (EramPairSide intruderSide, EramPairSide trackedSide) = intruder == pair.CallsignA ? (a, b) : (b, a);
        int floorFeet = EramConflictAlertFloorFeet(trackedSide.Facility);
        return (EramConflictDetector.IsMciAltitudeEligible(intruderSide.Aircraft.Altitude, floorFeet), intruder);
    }

    /// <summary>The callsign of the pair's side that is both untracked and uncorrelated, or null when neither is.</summary>
    private static string? PickEramIntruder(EramConflictDetector.ConflictPair pair, EramPairSide a, EramPairSide b)
    {
        if (!a.Tracked && !a.Correlated)
        {
            return pair.CallsignA;
        }

        return (!b.Tracked && !b.Correlated) ? pair.CallsignB : null;
    }

    /// <summary>What <see cref="UpsertEramConflict"/> did to the pair's alert.</summary>
    private enum EramUpsert
    {
        /// <summary>A new alert opened.</summary>
        Opened,

        /// <summary>The standing alert was refreshed and is still the same kind (conventional or MCI).</summary>
        Refreshed,

        /// <summary>The standing alert was refreshed and its Mode C Intruder status flipped.</summary>
        Reclassified,
    }

    /// <summary>
    /// Refreshes the owner facilities and intruder of the pair's active alert, or opens a new alert, and returns the alert
    /// and which of the two happened — a refresh that gained or lost the intruder is <see cref="EramUpsert.Reclassified"/>.
    /// </summary>
    private (EramActiveConflict Conflict, EramUpsert Upsert) UpsertEramConflict(
        EramConflictDetector.ConflictPair pair,
        EramPairSide a,
        EramPairSide b,
        string? intruder
    )
    {
        Dictionary<string, EramActiveConflict> conflicts = EramConflicts.Conflicts;
        if (conflicts.TryGetValue(pair.Id, out EramActiveConflict? existing))
        {
            bool wasMci = existing.IntruderCallsign is not null;
            existing.OwnerFacilityA = a.Facility;
            existing.OwnerFacilityB = b.Facility;
            existing.IntruderCallsign = intruder;
            return (existing, wasMci == (intruder is not null) ? EramUpsert.Refreshed : EramUpsert.Reclassified);
        }

        var conflict = new EramActiveConflict
        {
            Id = pair.Id,
            CallsignA = pair.CallsignA,
            CallsignB = pair.CallsignB,
            OwnerFacilityA = a.Facility,
            OwnerFacilityB = b.Facility,
            IntruderCallsign = intruder,
        };
        conflicts[pair.Id] = conflict;

        _logger.LogWarning(
            "ERAM STCA detected: {CallsignA} <-> {CallsignB} at t={T}s",
            pair.CallsignA,
            pair.CallsignB,
            Scenario?.ElapsedSeconds ?? 0
        );
        return (conflict, EramUpsert.Opened);
    }

    /// <summary>Removes every alert that was active before this pass and is no longer detected, and returns their ids.</summary>
    private static List<string> ClearUndetectedEramConflicts(
        HashSet<string> existingIds,
        HashSet<string> detectedIds,
        Dictionary<string, EramActiveConflict> conflicts
    )
    {
        var clearedIds = new List<string>();
        foreach (string id in existingIds)
        {
            if (!detectedIds.Contains(id))
            {
                conflicts.Remove(id);
                clearedIds.Add(id);
            }
        }

        return clearedIds;
    }

    /// <summary>The configured <c>ConflictAlertFloor</c> of an ERAM facility in the scenario's ARTCC config, or 0 when none is known.</summary>
    private int EramConflictAlertFloorFeet(string? facilityId)
    {
        if ((facilityId is null) || (Scenario?.ArtccConfig is not { } config))
        {
            return 0;
        }

        return config.FindFacility(facilityId)?.EramConfiguration?.ConflictAlertFloor ?? 0;
    }

    /// <summary>
    /// The alerts whose <see cref="EramActiveConflict.Suppressed"/> flag a <c>CO</c> entry changed since the last pass,
    /// split into the suppressed ids and the restored alerts; each is marked published. A new alert starts with both
    /// flags false, so only an alert that already existed can appear here.
    /// </summary>
    private static (List<string> Suppressed, List<EramActiveConflict> Restored) TakeEramSuppressionChanges(
        Dictionary<string, EramActiveConflict> conflicts
    )
    {
        var suppressed = new List<string>();
        var restored = new List<EramActiveConflict>();
        foreach (EramActiveConflict conflict in conflicts.Values)
        {
            if (conflict.Suppressed == conflict.PublishedSuppressed)
            {
                continue;
            }

            conflict.PublishedSuppressed = conflict.Suppressed;
            if (conflict.Suppressed)
            {
                suppressed.Add(conflict.Id);
            }
            else
            {
                restored.Add(conflict);
            }
        }

        return (suppressed, restored);
    }

    /// <summary>
    /// Per-second auto-delete pass: decides which aircraft to remove (per-aircraft <c>ONHS DEL</c> opt-in,
    /// stuck-after-landing at a layout-less airport, a generated overflight past its exit radius, a departure past the
    /// session's <see cref="SimScenarioState.DepartureAutoDeleteDistanceNm"/>, or the scenario's
    /// <c>OnLanding</c>/<c>Parked</c> mode), removes them from the world, and returns the removed
    /// states so the host can fan out its delete broadcasts (each state carries the last position for a
    /// surface-track coast/drop). A spine step (<see cref="Spine.StepId.AutoDelete"/>) on every run kind; the host
    /// receives the removed states through <see cref="Spine.IHostConsumers.OnAutoDeleted"/>.
    /// </summary>
    public IReadOnlyList<AircraftState> TickAutoDelete()
    {
        if (Scenario is not { } scenario)
        {
            return [];
        }

        string? mode = scenario.EffectiveAutoDeleteMode;
        bool modeDisabled =
            string.IsNullOrEmpty(mode)
            || mode.Equals("None", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("Never", StringComparison.OrdinalIgnoreCase);

        var toDelete = new List<AircraftState>();
        foreach (AircraftState ac in World.GetSnapshot())
        {
            // Per-aircraft opt-in raised by a queued ONHS DEL (or any queued DeleteCommand) whose trigger
            // has fired. This bypasses AutoDeleteExempt — the controller explicitly asked for the delete.
            bool perAircraftDelete = ac.Ground.PendingAutoDelete;

            // The session's departure distance ignores AutoDeleteExempt — spawn sets it on every ground-started
            // aircraft, so honouring it would keep every departure — and answers to NODEL alone.
            bool departureBeyondRange = IsDepartureBeyondAutoDeleteDistance(ac, scenario);

            if (!perAircraftDelete && !departureBeyondRange && ac.Ground.AutoDeleteExempt)
            {
                continue;
            }

            // Delete aircraft holding after exit at airports with no ground layout — they can't taxi.
            // Check the world-level layout (airport has data?) not the per-aircraft cache (may be null).
            // Respect "Never" mode — if the user explicitly disabled auto-delete, don't force it.
            bool stuckAfterLanding = (ac.Phases?.CurrentPhase is HoldingAfterExitPhase) && (World.GroundLayout is null) && !modeDisabled;

            // Only auto-delete arrivals: aircraft whose destination matches this airport. Departures
            // repositioning between parking spots should never be auto-deleted.
            bool isArrival = NavigationDatabase.AirportIdsMatch(ac.FlightPlan.Destination, scenario.PrimaryAirportId);

            // A generated overflight never lands, so the destination-matching modes above can never reach it.
            // It leaves the scenario by flying past its exit radius instead — unless a controller has taken
            // the track, in which case it stays until they drop it rather than vanishing under them.
            bool departedOverflight = !modeDisabled && HasLeftOverflightCorridor(ac, scenario.PrimaryAirportId) && ac.Track.Owner is null;

            bool shouldDelete =
                perAircraftDelete
                || stuckAfterLanding
                || departedOverflight
                || departureBeyondRange
                || (
                    !modeDisabled
                    && mode switch
                    {
                        "OnLanding" => ac.IsOnGround
                            && (ac.Phases?.CurrentPhase is HoldingAfterExitPhase || (ac.Phases?.CurrentPhase is AtParkingPhase && isArrival)),
                        "Parked" => ac.Phases?.CurrentPhase is AtParkingPhase && isArrival,
                        _ => false,
                    }
                );

            if (shouldDelete)
            {
                _logger.LogDebug(
                    "AutoDelete: {Callsign} marked for deletion (phase={Phase})",
                    ac.Callsign,
                    ac.Phases?.CurrentPhase?.GetType().Name ?? "none"
                );
                toDelete.Add(ac);
            }
        }

        if (toDelete.Count > 0)
        {
            _logger.LogDebug("AutoDelete: {Count} aircraft pending deletion at t={T}s", toDelete.Count, scenario.ElapsedSeconds);
        }

        foreach (AircraftState ac in toDelete)
        {
            // An overflight leaving its corridor is a completed transit, not a drop: stamp it so the removal below
            // records a debrief row (landings and handoffs are stamped where they happen).
            if ((ac.CompletionReason == CompletionReason.Active) && HasLeftOverflightCorridor(ac, scenario.PrimaryAirportId))
            {
                ac.CompletedAtSeconds = scenario.ElapsedSeconds;
                ac.CompletionReason = CompletionReason.Transited;
            }

            // Likewise a departure the session's distance removes has left the area, not been dropped.
            if ((ac.CompletionReason == CompletionReason.Active) && IsDepartureBeyondAutoDeleteDistance(ac, scenario))
            {
                ac.CompletedAtSeconds = scenario.ElapsedSeconds;
                ac.CompletionReason = CompletionReason.Departed;
            }

            RegisterDisconnectCoast(ac);
            RemoveFromWorld(ac.Callsign);
            _logger.LogInformation(
                "Auto-deleted {Callsign} (mode={Mode}) in scenario '{Name}' at t={T}s",
                ac.Callsign,
                mode,
                scenario.ScenarioName,
                scenario.ElapsedSeconds
            );
        }

        return toDelete;
    }

    /// <summary>
    /// True when the session's <see cref="SimScenarioState.DepartureAutoDeleteDistanceNm"/> is set and this is an airborne
    /// departure from the primary airport, farther from the airport's reference point than that distance, that no controller
    /// has kept with <c>NODEL</c>. A track owner does not keep it. A local flight filed back to the primary airport is kept.
    /// Live-traffic shadows are left to the feed, which owns their lifetime and would re-spawn a removed one on its next sync.
    /// </summary>
    private static bool IsDepartureBeyondAutoDeleteDistance(AircraftState aircraft, SimScenarioState scenario)
    {
        if (scenario.DepartureAutoDeleteDistanceNm is not { } distanceNm)
        {
            return false;
        }

        if (aircraft.IsOnGround || aircraft.IsShadow || aircraft.Ground.NoDeleteRequested)
        {
            return false;
        }

        string? primaryAirportId = scenario.PrimaryAirportId;
        if (!NavigationDatabase.AirportIdsMatch(aircraft.FlightPlan.Departure, primaryAirportId))
        {
            return false;
        }

        // A local flight (closed traffic, practice approaches) files back to the primary airport and is coming back.
        if (NavigationDatabase.AirportIdsMatch(aircraft.FlightPlan.Destination, primaryAirportId))
        {
            return false;
        }

        if (string.IsNullOrEmpty(primaryAirportId) || NavigationDatabase.Instance.GetAirportPosition(primaryAirportId) is not { } airport)
        {
            return false;
        }

        return GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, airport.Lat, airport.Lon) > distanceNm;
    }

    /// <summary>
    /// True once a generated overflight is farther from the primary airport than its exit radius. The radius
    /// is validated to exceed the generator's spawn corridor, so a transit always starts inside it and can
    /// only cross the threshold on the way out.
    /// </summary>
    private static bool HasLeftOverflightCorridor(AircraftState aircraft, string? primaryAirportId)
    {
        if (!aircraft.IsGeneratedOverflight || aircraft.OverflightExitDistanceNm is not { } exitDistanceNm)
        {
            return false;
        }

        if (string.IsNullOrEmpty(primaryAirportId) || NavigationDatabase.Instance.GetAirportPosition(primaryAirportId) is not { } airport)
        {
            return false;
        }

        return GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, airport.Lat, airport.Lon) > exitDistanceNm;
    }

    /// <summary>
    /// End-of-second advance of the weather timeline: re-collapses <see cref="SimScenarioState.WeatherTimeline"/> at the
    /// completed second into <see cref="SimulationWorld.Weather"/> — the continuous wind physics and visual acquisition
    /// read — and returns the profile so the host can mirror it; null when the scenario carries no timeline. Ungated on
    /// every run kind (ADR 0002): a slowly veering wind reaches the aircraft as the sub-degree per-second change it is,
    /// not in 1° / 0.5 kt steps. The reported METAR is issued separately by the host's <c>MetarIssuance</c> step. First,
    /// on every run kind and with or without a timeline, the entered ERAM weather reports whose expiry the second reached
    /// are removed.
    /// </summary>
    public WeatherProfile? AdvanceWeatherTimeline()
    {
        ExpireEramWeatherReports();
        if (Scenario is not { WeatherTimeline: { } timeline } scenario)
        {
            return null;
        }

        WeatherProfile profile = timeline.GetWeatherAt(scenario.ElapsedSeconds);
        World.Weather = profile;
        return profile;
    }

    /// <summary>
    /// Per-second, after physics: adds to each moving ground aircraft's <see cref="AircraftGroundOps.TaxiEdgeTrail"/> the
    /// straight edge it is on, a ramp connector included (<see cref="TaxiEdgeLocator.DrivenEdgeUnder"/>, looked for first around
    /// the trail's newest edge), whatever its phase except a runway roll (<see cref="IsRunwayRoll"/>), and empties the trail of every airborne
    /// aircraft. An aircraft with no ground layout, standing still, or rolling along a runway is left as it is. A spine step
    /// (<see cref="Spine.StepId.TaxiEdgeTrail"/>) on every run kind.
    /// </summary>
    public void TickTaxiEdgeTrails()
    {
        foreach (AircraftState ac in World.GetSnapshot())
        {
            TaxiEdgeTrail trail = ac.Ground.TaxiEdgeTrail;
            if (!ac.IsOnGround)
            {
                trail.Clear();
                continue;
            }

            if ((ac.Ground.Layout is not { } layout) || (ac.GroundSpeed <= 0.0) || IsRunwayRoll(ac.Phases?.CurrentPhase))
            {
                continue;
            }

            (int NodeA, int NodeB)? lastEdge = trail.Newest is { } newest ? (newest.NodeA, newest.NodeB) : null;
            if (TaxiEdgeLocator.DrivenEdgeUnder(layout, ac.Position, lastEdge) is { } edge)
            {
                trail.Record(edge, TaxiEdgeTrail.EntryNodeOf(edge, ac));
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="phase"/> rolls the aircraft along a runway — lining up, the takeoff roll and its rejection, the
    /// landing rollout and the touch-and-go / stop-and-go roll — where it passes over the taxiways crossing the runway without
    /// driving them. A runway crossing and a runway exit are taxiing, and are not runway rolls.
    /// </summary>
    private static bool IsRunwayRoll(Phase? phase) =>
        phase is LineUpPhase or LinedUpAndWaitingPhase or TakeoffPhase or RejectedTakeoffPhase or LandingPhase or TouchAndGoPhase or StopAndGoPhase;

    /// <summary>
    /// End-of-second sample of <see cref="AircraftState.PositionHistory"/>, the history-trail dots every display
    /// projects: one ring-buffer entry per aircraft every <see cref="AircraftState.PositionHistorySampleSeconds"/>,
    /// <see cref="AircraftState.PositionHistoryCapacity"/> deep. A spine step (<see cref="Spine.StepId.PositionHistory"/>)
    /// on every run kind, so a replay's trail is the live session's.
    /// </summary>
    public void SamplePositionHistory()
    {
        if (Scenario is not { } scenario || (int)scenario.ElapsedSeconds % AircraftState.PositionHistorySampleSeconds != 0)
        {
            return;
        }

        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (ac.PositionHistory.Count >= AircraftState.PositionHistoryCapacity)
            {
                ac.PositionHistory.RemoveAt(0);
            }

            ac.PositionHistory.Add((ac.Position.Lat, ac.Position.Lon));
        }
    }

    /// <summary>
    /// Per-second solo-training evaluation: builds the evaluation context from the active scenario, runs
    /// the solo-training evaluator against the current world, and returns the resulting events for the host
    /// to broadcast. Empty when not in solo mode. A spine step (<see cref="Spine.StepId.SoloTrainingEvaluation"/>)
    /// on every run kind, so a replayed solo session rebuilds the evaluator's record of what it scored.
    /// </summary>
    public IReadOnlyList<SoloTrainingEvent> TickSoloTrainingEvaluation()
    {
        if (Scenario is not { SoloTrainingMode: true } scenario)
        {
            return [];
        }

        return SoloTrainingEvaluator.Evaluate(
            World.GetSnapshot(),
            scenario.ElapsedSeconds,
            AirspaceDatabase.Default,
            new SoloTrainingServiceContext(
                new InitialContactEligibilityContext(
                    scenario.StudentPosition,
                    scenario.StudentPositionType,
                    scenario.ArtccId,
                    scenario.PrimaryAirportId,
                    scenario.InitialContactTransfers
                ),
                scenario.WakeDirectives,
                scenario.ArtccConfig,
                scenario.AsdexSafetyLogicConfigs,
                scenario.PilotContacts.TowerIsSimulatedAt
            )
        );
    }

    /// <summary>
    /// The post-physics segment under the bare host: the detectors, the proactive pass and the drains in
    /// <see cref="Spine.SpineOrder.PostPhysics"/> order, with the drained lines surfacing as this engine's events.
    /// Tests that stage a world by hand call this directly; <see cref="TickOneSecond"/> reaches it through
    /// <see cref="RunSecond"/>.
    /// </summary>
    public void TickPostPhysics() => RunPostPhysics(_bareHost);

    internal static string ToSayKind(PilotTransmission transmission) =>
        transmission.Kind == PilotTransmissionKind.SayReadback ? "SayReadback" : "SayPilot";

    private static LatLon? LookupAirportPosition(string airportId)
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetAirportPosition(airportId);
        return pos.HasValue ? new LatLon(pos.Value.Lat, pos.Value.Lon) : null;
    }

    /// <summary>The controller AI hosted on this engine, or null when the session runs without one. Set by the host after load.</summary>
    public AiControllerService? ControllerAi { get; set; }

    /// <summary>
    /// One AI tick over the completed second — the last spine step (<see cref="Spine.StepId.ControllerAi"/>), so it
    /// runs on every run kind that is allowed to: never in a replay (<see cref="RunProfile.RunsControllerAi"/>),
    /// because an AI-driven recording carries the AI's commands as recorded actions and re-running the brains would
    /// double them.
    /// </summary>
    public void TickControllerAi()
    {
        if (Scenario is not { ControllerAi: not null } scenario || !RunProfile.RunsControllerAi || ControllerAi is not { } ai)
        {
            return;
        }

        ai.Tick(
            new AiTickInputs
            {
                Scenario = scenario,
                World = World,
                Aircraft = World.GetSnapshot(),
                LayoutFor = ResolveGroundLayout,
                RunwaysFor = RunwayOccupancy.AirportRunways,
                ActiveConflicts = [.. ConflictAlerts.Conflicts.Values],
                EramConflicts = [.. EramConflicts.Conflicts.Values],
                Attendance = Attendance,
                ConsolidationState = ConsolidationState,
            }
        );
    }

    /// <summary>
    /// One sim-second as a bare (<see cref="RunKind.Test"/>) run: the whole spine under the bare host, whose slots
    /// are empty and whose consumers are this engine's events. A no-op with no scenario loaded.
    /// </summary>
    public void TickOneSecond()
    {
        if (Scenario is null)
        {
            return;
        }

        RunSecond(_bareHost);
    }

    /// <summary>
    /// Computes the hold-short nodes currently occupied by a holding or exiting aircraft from live
    /// aircraft state. Includes runway hold-short nodes an aircraft's tail hangs over while holding
    /// short of a taxiway (issue #172). The per-tick cache is transient, so this recomputes on demand —
    /// for diagnostics and tests querying between ticks.
    /// </summary>
    public IReadOnlySet<int> ComputeOccupiedHoldShortNodes() => BuildOccupiedHoldShortNodes(out _);

    /// <summary>
    /// The occupied hold-short set <paramref name="aircraft"/>'s exit choice on <paramref name="layout"/> would read now:
    /// the shared occupied set plus the exit bar of every exit-capacity segment full for it
    /// (<see cref="OccupancyForExitChoice"/>), computed from the world as it stands.
    /// </summary>
    public IReadOnlySet<int> ComputeOccupancyForExitChoice(AircraftState aircraft, AirportGroundLayout layout)
    {
        HashSet<int> occupied = BuildOccupiedHoldShortNodes(out Dictionary<ExitCapacitySegment, List<AircraftState>>? exitSegmentOccupants);
        return OccupancyForExitChoice(aircraft, layout, occupied, exitSegmentOccupants) ?? occupied;
    }

    /// <summary>
    /// The occupied hold-short nodes, and in the same pass the aircraft inside each exit-capacity segment
    /// (<paramref name="exitSegmentOccupants"/>, see <see cref="AddExitSegmentOccupancy"/>).
    /// </summary>
    private HashSet<int> BuildOccupiedHoldShortNodes(out Dictionary<ExitCapacitySegment, List<AircraftState>>? exitSegmentOccupants)
    {
        var occupied = new HashSet<int>();
        Dictionary<ExitCapacitySegment, List<AircraftState>>? segmentOccupants = null;
        foreach (AircraftState ac in World.GetSnapshot())
        {
            AddOccupiedHoldShort(ac, occupied);
            AddExitSegmentOccupancy(ac, ref segmentOccupants);
        }

        exitSegmentOccupants = segmentOccupants;
        return occupied;
    }

    private static void AddOccupiedHoldShort(AircraftState ac, HashSet<int> occupied)
    {
        if (ac.Phases?.CurrentPhase is HoldingShortPhase hs)
        {
            occupied.Add(hs.HoldShort.NodeId);

            // Tail-over-runway (issue #172): an aircraft holding short of a taxiway with its tail
            // over a runway also occupies that runway's hold-short node, so arrivals don't plan to
            // use the exit it is blocking. Read from the route — it survives snapshot restore,
            // unlike the phase's reconstructed HoldShort copy.
            if (ac.Ground.AssignedTaxiRoute?.GetHoldShortAt(hs.HoldShort.NodeId)?.TailOverRunwayNodeId is { } tailOverNode)
            {
                occupied.Add(tailOverNode);
            }
            return;
        }

        // Aircraft navigating toward an exit are claiming their target hold-short node
        if (ac.Phases?.CurrentPhase is RunwayExitPhase rep && rep.TargetHoldShortNodeId is { } repNodeId)
        {
            occupied.Add(repNodeId);
            return;
        }

        // Aircraft holding after runway exit occupy their hold-short node
        if (ac.Phases?.CurrentPhase is HoldingAfterExitPhase haep && haep.HoldShortNodeId is { } haepNodeId)
        {
            occupied.Add(haepNodeId);
        }
    }

    /// <summary>
    /// Adds <paramref name="ac"/> to every exit-capacity segment (<see cref="ExitCapacitySegment"/>) it is inside, hold
    /// line to hold line, from either bar: exiting toward one of its bars, holding after exiting there, pulling up along
    /// it from one bar toward the other, holding short at either bar, or simply standing or moving on its pavement. Also
    /// called mid-tick when an arrival commits to an exit (<see cref="PhaseContext.JoinExitSegmentOccupants"/>), so an
    /// arrival ticking later in that tick counts it, first come first served in world order; an aircraft already in a
    /// segment's list is not added twice. An aircraft in the air, or at an airport with no exit-capacity rules, is skipped
    /// before its layout is resolved. A sidecar rule that does not resolve on an aircraft's layout is dropped by
    /// <see cref="ExitCapacityResolver.GetSegments"/>, which logs it once per layout.
    /// </summary>
    private void AddExitSegmentOccupancy(AircraftState ac, ref Dictionary<ExitCapacitySegment, List<AircraftState>>? occupants)
    {
        if (!ac.IsOnGround)
        {
            return;
        }

        AirportGroundLayout? layout = ac.Ground.Layout;
        string? airportId = layout?.AirportId ?? ac.Phases?.AssignedRunway?.AirportId ?? ac.AirportId;
        if (!string.IsNullOrEmpty(airportId) && (NavigationDatabase.InstanceOrNull?.AirportSidecars.GetExitCapacity(airportId) is not { Count: > 0 }))
        {
            return;
        }

        if ((layout ?? ResolveGroundLayout(ac)) is not { } resolved)
        {
            return;
        }

        foreach (ExitCapacitySegment segment in ExitCapacityResolver.GetSegments(resolved))
        {
            if (!IsInsideExitSegment(ac, segment))
            {
                continue;
            }

            occupants ??= [];
            if (!occupants.TryGetValue(segment, out List<AircraftState>? inside))
            {
                inside = [];
                occupants[segment] = inside;
            }

            if (!inside.Contains(ac))
            {
                inside.Add(ac);
            }
        }
    }

    /// <summary>Below this ground speed an exit-segment occupant counted only by its position has no direction of travel.</summary>
    private const double ExitSegmentStationaryKts = 1.0;

    private static bool IsInsideExitSegment(AircraftState ac, ExitCapacitySegment segment) =>
        (PhaseBarTravelledToward(ac, segment) is not null) || segment.ContainsPosition(ac.Position);

    /// <summary>
    /// The bar of <paramref name="segment"/> that <paramref name="ac"/>'s phase says it travels toward, or null when its
    /// phase places it on no bar of the segment. Exiting toward a bar, or holding after exiting there, means it entered by
    /// that bar and travels toward the other; holding short at a bar, or pulling up along the segment, means it travels
    /// toward that bar.
    /// </summary>
    private static int? PhaseBarTravelledToward(AircraftState ac, ExitCapacitySegment segment) =>
        ac.Phases?.CurrentPhase switch
        {
            RunwayExitPhase { TargetHoldShortNodeId: { } target } when segment.IsBar(target) => segment.OtherBar(target),
            HoldingAfterExitPhase { HoldShortNodeId: { } holdShort } when segment.IsBar(holdShort) => segment.OtherBar(holdShort),
            HoldingShortPhase holdingShort when segment.IsBar(holdingShort.HoldShort.NodeId) => holdingShort.HoldShort.NodeId,
            TaxiingPhase => PullUpFarBar(ac.Ground.AssignedTaxiRoute, segment),
            _ => null,
        };

    /// <summary>
    /// The bar of <paramref name="segment"/> that <paramref name="ac"/>, one of its occupants, travels toward: from its
    /// phase (<see cref="PhaseBarTravelledToward"/>), else from its track against the bar-to-bar bearing. Null for an
    /// occupant with no phase case that is stopped: it gives no direction and blocks the strip both ways.
    /// </summary>
    private static int? BarTravelledToward(AircraftState ac, ExitCapacitySegment segment)
    {
        if (PhaseBarTravelledToward(ac, segment) is { } phaseBar)
        {
            return phaseBar;
        }

        if (ac.GroundSpeed < ExitSegmentStationaryKts)
        {
            return null;
        }

        int exitBar = segment.ExitBarNodeId;
        return (ac.TrueTrack.AbsAngleTo(segment.BearingToward(exitBar)) < 90.0) ? exitBar : segment.OtherBar(exitBar);
    }

    /// <summary>
    /// The far bar of the pull-up route <see cref="RunwayExitPhase"/> builds after an exit between parallels — a route
    /// that starts at one of the segment's bars while the aircraft has not yet passed the other — or null for any other
    /// route.
    /// </summary>
    private static int? PullUpFarBar(TaxiRoute? route, ExitCapacitySegment segment)
    {
        if ((route is null) || (route.Segments.Count == 0) || !segment.IsBar(route.Segments[0].FromNodeId))
        {
            return null;
        }

        int farBar = segment.OtherBar(route.Segments[0].FromNodeId);
        int farBarIndex = route.Segments.FindIndex(s => s.ToNodeId == farBar);
        return ((farBarIndex >= 0) && (route.CurrentSegmentIndex <= farBarIndex)) ? farBar : null;
    }

    /// <summary>
    /// The occupancy set <paramref name="aircraft"/>'s exit choice reads: the shared <paramref name="occupied"/> set, plus
    /// the exit bar of every exit-capacity segment on its landing runway that is full for it — so a full segment reads
    /// exactly like an occupied exit and the arrival takes the next one (AIM 4-3-21.a). Only an arrival on final, landing
    /// or exiting gets a copy; every other aircraft, and an arrival with no full segment, gets the shared set.
    /// </summary>
    private HashSet<int>? OccupancyForExitChoice(
        AircraftState aircraft,
        AirportGroundLayout? layout,
        HashSet<int>? occupied,
        Dictionary<ExitCapacitySegment, List<AircraftState>>? exitSegmentOccupants
    )
    {
        if (
            (occupied is null)
            || (layout is null)
            || (exitSegmentOccupants is not { } occupantsBySegment)
            || (aircraft.Phases?.CurrentPhase is not (FinalApproachPhase or LandingPhase or RunwayExitPhase))
            || (aircraft.Phases.AssignedRunway?.Designator is not { } runway)
        )
        {
            return occupied;
        }

        HashSet<int>? forArrival = null;
        foreach (ExitCapacitySegment segment in ExitCapacityResolver.GetSegments(layout))
        {
            if (IsExitSegmentFullFor(aircraft, runway, segment, occupied, occupantsBySegment))
            {
                forArrival ??= [.. occupied];
                forArrival.Add(segment.ExitBarFor(runway));
            }
        }

        return forArrival ?? occupied;
    }

    /// <summary>
    /// True when <paramref name="segment"/> governs <paramref name="runway"/> and is full for <paramref name="arrival"/>
    /// (<see cref="ExitSegmentRefusal"/>). An exit bar already in the shared set is left to it, and an explicit
    /// <c>EXIT</c> naming the segment's taxiway is the controller's call, so neither is refused here.
    /// </summary>
    private bool IsExitSegmentFullFor(
        AircraftState arrival,
        string runway,
        ExitCapacitySegment segment,
        HashSet<int> occupied,
        Dictionary<ExitCapacitySegment, List<AircraftState>> occupantsBySegment
    )
    {
        int exitBar = segment.ExitBarFor(runway);
        if (
            !segment.AppliesToRunway(runway)
            || occupied.Contains(exitBar)
            || !occupantsBySegment.TryGetValue(segment, out List<AircraftState>? inside)
            || string.Equals(arrival.Phases?.RequestedExit?.Taxiway, segment.Rule.Taxiway, StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        List<AircraftState> others = [.. inside.Where(ac => !ReferenceEquals(ac, arrival))];
        if ((others.Count == 0) || (ExitSegmentRefusal(segment, exitBar, others, arrival.AircraftType) is not { } reason))
        {
            return false;
        }

        LogExitCapacityRefusal(arrival, runway, segment, others, reason);
        return true;
    }

    /// <summary>
    /// Why <paramref name="segment"/> is full for an arrival of <paramref name="arrivalType"/> exiting into it across
    /// <paramref name="exitBar"/> with <paramref name="others"/> on it, or null when it may enter. An occupant that is not
    /// travelling away from the exit bar (<see cref="BarTravelledToward"/>) fills it outright, nose to nose. Otherwise it
    /// is full at the rule's maximum for the CWTs involved, or when the pavement free from the exit bar to the tail of the
    /// nearest occupant, where it stands (its position projected onto the walked path, less half its length), is shorter
    /// than the arrival plus the ground conflict detector's stop gap to that occupant
    /// (<see cref="GroundConflictDetector.StopGapFt"/>; <see cref="AircraftLength.ResolveFt"/> for every aircraft) plus
    /// <see cref="RunwayTailClearance.MarginFt"/>, the margin past the exit bar its exit stops its tail at. The
    /// gap is a sim convention, the trail ring <see cref="GroundConflictDetector"/> stops the arrival at; the rule it
    /// serves is AIM 4-3-21.a/b, that the whole aircraft must clear the hold line.
    /// </summary>
    private static string? ExitSegmentRefusal(ExitCapacitySegment segment, int exitBar, List<AircraftState> others, string arrivalType)
    {
        int farBar = segment.OtherBar(exitBar);
        if (others.FirstOrDefault(ac => BarTravelledToward(ac, segment) != farBar) is { } opposing)
        {
            return $"{opposing.Callsign} on it is travelling toward the exit bar or stopped";
        }

        bool othersAtOrBelowThreshold = others.All(ac => segment.IsAtOrBelowThreshold(WakeTurbulenceData.GetCwt(ac.AircraftType)));
        int capacity = segment.CapacityFor(WakeTurbulenceData.GetCwt(arrivalType), othersAtOrBelowThreshold);
        if (others.Count >= capacity)
        {
            return $"full at {others.Count}/{capacity}";
        }

        Debug.Assert(others.Count > 0, "ExitSegmentRefusal needs at least one occupant; its caller returns early on none");
        AircraftState nearest = others[0];
        double freeFt = TailDistanceFromBarFt(segment, exitBar, nearest);
        for (int i = 1; i < others.Count; i++)
        {
            double tailFt = TailDistanceFromBarFt(segment, exitBar, others[i]);
            if (tailFt < freeFt)
            {
                nearest = others[i];
                freeFt = tailFt;
            }
        }

        double arrivalFt = AircraftLength.ResolveFt(arrivalType);
        double neededFt =
            arrivalFt + GroundConflictDetector.StopGapFt(AircraftLength.ResolveFt(nearest.AircraftType), arrivalFt) + RunwayTailClearance.MarginFt;
        return (freeFt < neededFt) ? $"{Math.Max(freeFt, 0):F0} ft free from the exit bar to the nearest tail, {neededFt:F0} ft needed" : null;
    }

    /// <summary>
    /// The distance along <paramref name="segment"/> from <paramref name="exitBar"/> to <paramref name="ac"/>'s tail, where
    /// it stands.
    /// </summary>
    private static double TailDistanceFromBarFt(ExitCapacitySegment segment, int exitBar, AircraftState ac) =>
        segment.DistanceAlongFromBarFt(exitBar, ac.Position) - (AircraftLength.ResolveFt(ac.AircraftType) / 2.0);

    private void LogExitCapacityRefusal(AircraftState arrival, string runway, ExitCapacitySegment segment, List<AircraftState> others, string reason)
    {
        if (!ExitCapacityLog.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        string occupants = string.Join(
            ", ",
            others.Select(ac => $"{ac.Callsign} ({ac.AircraftType}, CWT {WakeTurbulenceData.GetCwt(ac.AircraftType) ?? "?"})")
        );
        if (!_loggedExitCapacityRefusals.Add($"{arrival.Callsign}|{segment.ExitBarFor(runway)}|{occupants}"))
        {
            return;
        }

        ExitCapacityLog.LogDebug(
            "[ExitCapacity] {Callsign} ({Type}, CWT {Cwt}): exit {Twy} off {Rwy} refused ({Reason}), taking a later exit; inside: {Occupants}",
            arrival.Callsign,
            arrival.AircraftType,
            WakeTurbulenceData.GetCwt(arrival.AircraftType) ?? "?",
            segment.Rule.Taxiway,
            runway,
            reason,
            occupants
        );
    }

    private void PreTick(AircraftState aircraft, double deltaSeconds)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return;
        }

        PhaseRunner.Tick(aircraft, BuildPhaseContext(aircraft, deltaSeconds));
    }

    /// <summary>
    /// Builds the full-fidelity <see cref="PhaseContext"/> the phase machinery runs under.
    /// Shared by the per-sub-tick <see cref="PreTick"/> and by post-physics consumers that
    /// need to drive phase-level consequences outside the phase loop (e.g.
    /// <see cref="TickVisualDetection"/> ending a visual approach) — those pass
    /// <paramref name="deltaSeconds"/> 0. <c>CommandDispatcher.BuildMinimalContext</c> is NOT
    /// a substitute: it leaves the solo-training/RPO routing fields unset, which misroutes
    /// pilot transmissions.
    /// </summary>
    private PhaseContext BuildPhaseContext(AircraftState aircraft, double deltaSeconds)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        AirportGroundLayout? groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        HashSet<int>? occupiedNodes = _occupiedHoldShortNodes;

        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = cat,
            DeltaSeconds = deltaSeconds,
            Logger = _logger,
            Runway = runway,
            FieldElevation = runway?.ElevationFt ?? CommandDispatcher.ResolveFieldElevation(aircraft, groundLayout),
            GroundLayout = groundLayout,
            // No scenario loaded: the room has named no active runways.
            ActiveRunways = Scenario?.ActiveRunways ?? ActiveRunways.Empty,
            Weather = World.Weather,
            ScenarioElapsedSeconds = Scenario?.ElapsedSeconds ?? 0,
            AutoClearedToLand = Scenario?.IsAutoClearedToLandFor(aircraft) ?? false,
            AutoPullUpToParallel = Scenario?.AutoPullUpToParallel ?? false,
            // The simulated tower sends its own arrivals around when the runway is not clear (7110.65 3-10-3.a), whatever the room toggle.
            AutoGoAroundOnOccupiedRunway =
                (Scenario?.AutoGoAroundOnOccupiedRunway ?? false) || (Scenario?.IsSimulatedTowerArrival(aircraft) ?? false),
            AutoRejectTakeoffOnOccupiedRunway = Scenario?.AutoRejectTakeoffOnOccupiedRunway ?? false,
            ListAircraft = World.GetSnapshot,
            SoloTrainingMode = Scenario?.SoloTrainingMode ?? false,
            ScenarioId = Scenario?.ScenarioId,
            SoloParkingInitialCallupRatePercent = Scenario?.SoloParkingInitialCallupRatePercent ?? 100,
            SoloGoAroundProbabilityPercent = Scenario?.SoloGoAroundProbabilityPercent ?? 0,
            FinalApproachSpeedVarietyEnabled = Scenario?.FinalApproachSpeedVarietyEnabled ?? false,
            Rng = World.Rng,
            TryReserveSoloParkingInitialCallupSlot = TryReserveSoloParkingInitialCallupSlot,
            RpoShowPilotSpeech = Scenario?.RpoShowPilotSpeech ?? false,
            StudentPositionType = Scenario?.StudentPositionType,
            IsRunwaySpawnFieldTowered = IsRunwaySpawnFieldTowered(aircraft),
            StudentPosition = Scenario?.StudentPosition,
            ArtccId = Scenario?.ArtccId,
            PrimaryAirportId = Scenario?.PrimaryAirportId,
            AtisLetter = PilotResponder.ResolvePrimaryFieldAtisLetter(Scenario),
            InitialContactTransfers = Scenario?.InitialContactTransfers ?? Yaat.Sim.Data.InitialContactTransferCatalog.Empty,
            PilotContacts = Scenario?.PilotContacts ?? PilotContactRoster.Empty,
            IsHoldShortNodeOccupied = (occupiedNodes is not null) ? nodeId => occupiedNodes.Contains(nodeId) : null,
            OccupiedHoldShortNodes = OccupancyForExitChoice(aircraft, groundLayout, occupiedNodes, _exitSegmentOccupants),
            MarkHoldShortNodeOccupied = (occupiedNodes is not null) ? nodeId => occupiedNodes.Add(nodeId) : null,
            JoinExitSegmentOccupants = (occupiedNodes is not null) ? () => AddExitSegmentOccupancy(aircraft, ref _exitSegmentOccupants) : null,
            TowerPosition = (Scenario?.IsStudentTowerPosition == true) ? Scenario.StudentPosition : null,
            // Phases that consult follow targets (pattern spacing, VfrFollowPhase)
            // need a way to resolve the lead aircraft by callsign.
            AircraftLookup = World.FindAircraft,
        };
    }
}
