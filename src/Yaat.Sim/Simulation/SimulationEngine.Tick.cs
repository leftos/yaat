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
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// The per-tick execution path: pre-physics, physics, the detectors, post-physics, and the two whole-second entry points.
public sealed partial class SimulationEngine
{
    // Holds the set of hold-short node IDs currently occupied by aircraft.
    // Built at the start of each TickPhysics, used by PreTick to prevent stacking.
    private HashSet<int>? _occupiedHoldShortNodes;

    /// <summary>
    /// Pre-physics: process delayed spawns, generators, triggers, timed presets,
    /// and ensure ground layout. Returns a list of aircraft spawned this tick.
    /// Terminal entries are accumulated and can be drained via <see cref="DrainTerminalEntries"/>.
    /// </summary>
    public TickPrePhysicsResult TickPrePhysics()
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return new TickPrePhysicsResult([], []);
        }

        var spawned = new List<AircraftState>();
        var generatorSpawns = new List<GeneratorSpawn>();

        ProcessDelayedSpawns(spawned);
        ProcessGenerators(generatorSpawns);
        ApplyArrivalSpacing();
        ProcessTriggers();
        ProcessTimedPresets();
        ProcessReleaseQueue();
        ProcessTimers();
        ProcessReleasedGroundDepartures();

        // Ensure ground layout is set
        if (scenario.PrimaryAirportId is not null && World.GroundLayout is null)
        {
            World.GroundLayout = _groundData.GetLayout(scenario.PrimaryAirportId);
        }

        // Refresh the per-hold-short departure-queue ordinals over the live world. This runs in
        // TickPrePhysics because it is the one per-second hook BOTH the standalone TickOneSecond path AND
        // the live server share (TickProcessor.ProcessPrePhysicsCore → sim.TickPrePhysics); the server
        // runs its own post-physics and never calls SimulationEngine.TickPostPhysics. The broadcast later
        // this tick reads RunwayQueuePosition for the datablock "#N" and the Info-column status.
        RunwayDepartureQueue.UpdatePositions(World.GetSnapshot());

        return new TickPrePhysicsResult(spawned, generatorSpawns);
    }

    /// <summary>
    /// Physics step: runs FlightPhysics.Update and phase runner for all aircraft.
    /// Call multiple times per sim-second for sub-tick granularity.
    /// </summary>
    public void TickPhysics(double delta)
    {
        var sw = Stopwatch.StartNew();
        _occupiedHoldShortNodes = BuildOccupiedHoldShortNodes();
        AccumulateTiming("Physics.BuildHoldShort", sw);

        // Cache scenario mode flags onto the World so FlightPhysics → PilotObservationUpdater
        // can route resolved RTIS/RFIS pilot transmissions to the correct pending list.
        World.SoloTrainingMode = Scenario?.SoloTrainingMode ?? false;
        World.RpoShowPilotSpeech = Scenario?.RpoShowPilotSpeech ?? false;
        World.MagneticModelDateUtc = Scenario?.MagneticModelDateUtc ?? MagneticDeclination.EvaluationDateUtc;

        sw.Restart();
        RehydrateRestoredQueueBlocks();
        AccumulateTiming("Physics.RehydrateBlocks", sw);

        sw.Restart();
        World.Tick(delta, Scenario?.ElapsedSeconds ?? 0, PreTick, RecordWorldTiming);
        AccumulateTiming("Physics.WorldTick", sw);

        _occupiedHoldShortNodes = null;

        sw.Restart();
        ProcessDeferredDispatches(delta);
        AccumulateTiming("Physics.Deferred", sw);

        sw.Restart();
        ProcessTriggeredTrackBlocks();
        AccumulateTiming("Physics.TrackBlocks", sw);
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
        foreach (var aircraft in World.GetSnapshot())
        {
            List<CommandBlock>? failed = null;
            foreach (var block in aircraft.Queue.Blocks)
            {
                if (block.ApplyAction is not null || block.IsApplied || string.IsNullOrEmpty(block.SourceCommandText))
                {
                    continue;
                }

                var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
                var ctx = new DispatchContext(
                    groundLayout,
                    World.Rng,
                    World.Weather,
                    FindAircraft,
                    () => World.GetSnapshot(),
                    Scenario?.ValidateDctFixes ?? true,
                    Scenario?.AutoCrossRunway ?? false,
                    Scenario?.SoloTrainingMode ?? false,
                    Scenario?.RpoShowPilotSpeech ?? false,
                    AddTerminalEntry,
                    Scenario?.ArtccConfig,
                    Scenario?.ElapsedSeconds ?? 0,
                    PreserveConditionals: true,
                    IsScenarioScripted: false
                );

                if (!CommandDispatcher.RehydrateRestoredBlock(block, aircraft, ctx))
                {
                    (failed ??= []).Add(block);
                }
            }

            if (failed is null)
            {
                continue;
            }

            // A block that cannot be recovered would fire as a silent no-op; drop it with a warning so
            // the RPO knows the instruction was lost rather than believing it is still pending.
            foreach (var block in failed)
            {
                _logger.LogWarning(
                    "[Restore] {Callsign}: could not rehydrate queued block '{Description}' from '{Source}' — dropping it",
                    aircraft.Callsign,
                    block.Description,
                    block.SourceCommandText
                );
                aircraft.PendingWarnings.Add($"{aircraft.Callsign} queued command lost after restore: {block.Description}");
                aircraft.Queue.Blocks.Remove(block);
            }

            if (aircraft.Queue.CurrentBlockIndex >= aircraft.Queue.Blocks.Count)
            {
                aircraft.Queue.CurrentBlockIndex = Math.Max(0, aircraft.Queue.Blocks.Count - 1);
            }
        }
    }

    private void RecordWorldTiming(string bucket, double ms)
    {
        if (TickTimings.TryGetValue(bucket, out var entry))
        {
            TickTimings[bucket] = (entry.Count + 1, entry.Ms + ms);
        }
        else
        {
            TickTimings[bucket] = (1, ms);
        }
    }

    /// <summary>Stops <paramref name="sw"/> and folds its elapsed time into <paramref name="bucket"/>.</summary>
    internal void AccumulateTiming(string bucket, Stopwatch sw)
    {
        sw.Stop();
        RecordWorldTiming(bucket, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Per-second post-physics pilot-proactive behaviors: solo check-in, arrival approach request,
    /// airspace-boundary respect, pending-request follow-ups, and — in all modes — deferred REPORT
    /// triggers. The SINGLE owner of this orchestration: both the standalone <see cref="TickPostPhysics"/>
    /// AND the live server (<c>TickProcessor.ProcessPostPhysics</c>) call it, so a behavior added here runs
    /// in both hosts. Airborne check-in fires before the caller's notification drain so it emits the same
    /// tick it is produced; TickReportTriggers runs in solo and RPO mode, so it sits outside the solo gate.
    /// </summary>
    public void TickPilotProactive()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        bool solo = scenario.SoloTrainingMode;
        foreach (var ac in World.GetSnapshot())
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
    /// Per-second transponder maintenance: advances each aircraft's IDENT timer so the ident flash
    /// auto-clears after <see cref="AircraftTransponder.IdentDurationSeconds"/>, and latches the
    /// has-reported-Mode-C flag CRC's lost-Mode-C indication reads. The SINGLE owner of this logic — both
    /// the standalone <see cref="TickPostPhysics"/> AND the live server
    /// (<c>TickProcessor.ProcessPostPhysics</c>) call it, so it runs identically in both hosts.
    /// </summary>
    public void TickTransponders()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        foreach (var ac in World.GetSnapshot())
        {
            ac.Transponder.Tick(scenario.ElapsedSeconds);
        }
    }

    /// <summary>
    /// Per-second visual-acquisition update for aircraft on a visual approach (ApproachId starts with
    /// <c>VIS</c>): field-in-sight acquisition/loss and FOLLOW traffic-in-sight acquisition/loss, using the
    /// active <see cref="SimulationWorld.Weather"/> (cloud layers + visibility) and the nav DB. Sets
    /// <c>Approach.HasReported*InSight</c> and enqueues the pilot/RPO transmissions + notifications the host
    /// drains after post-physics. The SINGLE owner of this orchestration — both the standalone
    /// <see cref="TickPostPhysics"/> AND the live server (<c>TickProcessor.ProcessPostPhysics</c>) call it,
    /// so the visual-acquisition behavior runs identically in both hosts.
    /// </summary>
    public void TickVisualDetection()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        var snapshot = World.GetSnapshot();
        var weather = World.Weather;

        foreach (var ac in snapshot)
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

            var aptPos = NavigationDatabase.Instance.GetFixPosition(airport);
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
                var metarData = weather.GetWeatherForAirport(airport);
                layers = metarData?.Layers;
                visibilitySm = metarData?.VisibilityStatuteMiles;
                primaryCeilingForLogs = metarData?.CeilingFeetAgl;
            }

            // Field in sight check (with runway direction awareness)
            // Bank angle affects initial acquisition only — once acquired, pilot can track through turns.
            var runway = ac.Phases.AssignedRunway;
            double airportSizeCapNm = VisualAcquisition.AirportSizeCapNm(airport);
            bool fieldLostThisTick = false;

            if (!ac.Approach.HasReportedFieldInSight)
            {
                // Initial acquisition: use actual bank angle
                var acquireResult = runway is not null
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

                var maintainResult = VisualDetection.TryMaintainAirportContact(ac, aptElevation.Value, layers, visibilitySm, fieldDistanceNm);

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
                var target = snapshot.FirstOrDefault(t => t.Callsign.Equals(followCs, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                {
                    if (!ac.Approach.HasReportedTrafficInSight)
                    {
                        var acquireTrafficResult = VisualAcquisition.TryAcquireTraffic(ac, target, weather);
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
                        var maintainTrafficResult = VisualAcquisition.TryMaintainTrafficContact(ac, target, weather);
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
    /// that opened and closed this tick for the host to broadcast. Both <see cref="TickPostPhysics"/> and the
    /// live server call it; only a broadcasting host consumes the returned diff, but the detection itself has
    /// to run on every path or a restored conflict is never re-examined.
    /// </summary>
    public ConflictAlertChanges TickConflictAlerts()
    {
        var snapshot = World.GetSnapshot();
        var conflicts = ConflictAlerts.Conflicts;
        var existingIds = new HashSet<string>(conflicts.Keys);
        var corridors = ConflictAlertDetector.BuildCorridors(ResolveInternalAirports(), NavigationDatabase.Instance);
        var context = new ConflictAlertContext(ExistingConflictIds: existingIds, ApproachCorridors: corridors);

        var detected = ConflictAlertDetector.Detect(snapshot, context);
        var detectedIds = new HashSet<string>(detected.Select(c => c.Id));

        var newConflicts = new List<ActiveConflict>();
        foreach (var pair in detected)
        {
            if (!conflicts.ContainsKey(pair.Id))
            {
                var conflict = new ActiveConflict
                {
                    Id = pair.Id,
                    CallsignA = pair.CallsignA,
                    CallsignB = pair.CallsignB,
                };
                conflicts[pair.Id] = conflict;
                newConflicts.Add(conflict);

                _logger.LogWarning(
                    "Conflict alert detected: {CallsignA} <-> {CallsignB} at t={T}s",
                    pair.CallsignA,
                    pair.CallsignB,
                    Scenario?.ElapsedSeconds ?? 0
                );
            }
        }

        var clearedIds = new List<string>();
        foreach (var id in existingIds)
        {
            if (!detectedIds.Contains(id))
            {
                var cleared = conflicts[id];
                conflicts.Remove(id);
                clearedIds.Add(id);

                _logger.LogInformation(
                    "Conflict alert cleared: {CallsignA} <-> {CallsignB} at t={T}s",
                    cleared.CallsignA,
                    cleared.CallsignB,
                    Scenario?.ElapsedSeconds ?? 0
                );
            }
        }

        return new ConflictAlertChanges(newConflicts, clearedIds);
    }

    /// <summary>
    /// Per-second ERAM Short-Term Conflict Alert pass (docs/crc/eram.md §377-383) — the en-route sibling of
    /// <see cref="TickConflictAlerts"/>. Runs <see cref="EramConflictDetector"/>, refreshes each pair's
    /// owning ERAM facilities and Mode-C-intruder classification, updates the engine-owned
    /// <see cref="EramConflicts"/> set, and returns the pairs that opened and closed this tick for the host
    /// to broadcast. Server-only, like <see cref="TickConflictAlerts"/>.
    /// </summary>
    public EramConflictAlertChanges TickEramConflictAlerts()
    {
        var snapshot = World.GetSnapshot();
        var conflicts = EramConflicts.Conflicts;
        var existingIds = new HashSet<string>(conflicts.Keys);

        var detected = EramConflictDetector.Detect(snapshot, existingIds);
        var detectedIds = new HashSet<string>(detected.Select(c => c.Id));

        var ownerFacility = new Dictionary<string, string?>(snapshot.Count);
        var isTracked = new Dictionary<string, bool>(snapshot.Count);
        var isCorrelated = new Dictionary<string, bool>(snapshot.Count);
        foreach (var ac in snapshot)
        {
            ownerFacility[ac.Callsign] = ac.Track.Owner is { OwnerType: TrackOwnerType.Eram, FacilityId: { } facility } ? facility : null;
            isTracked[ac.Callsign] = ac.Track.Owner is not null;
            isCorrelated[ac.Callsign] = ac.FlightPlan.HasFlightPlan;
        }

        var newConflicts = new List<EramActiveConflict>();
        foreach (var pair in detected)
        {
            ownerFacility.TryGetValue(pair.CallsignA, out var facilityA);
            ownerFacility.TryGetValue(pair.CallsignB, out var facilityB);
            isTracked.TryGetValue(pair.CallsignA, out var trackedA);
            isTracked.TryGetValue(pair.CallsignB, out var trackedB);
            isCorrelated.TryGetValue(pair.CallsignA, out var correlatedA);
            isCorrelated.TryGetValue(pair.CallsignB, out var correlatedB);

            // A conflict alert protects a controlled aircraft (7110.65 §2-1-6, §5-13-1), and the §377 facility
            // gate needs a target owned in some ERAM facility — so two untracked returns are not an alert.
            if (!trackedA && !trackedB)
            {
                detectedIds.Remove(pair.Id);
                continue;
            }

            // The Mode-C intruder (CDB, "TFC"+beacon — an uncorrelated presentation, eram.md §844-852) is a
            // target that is BOTH untracked AND uncorrelated (no flight plan). A correlated-but-unowned target
            // (a filed flight plan that no controller has tracked yet) is NOT an intruder — it flashes an
            // ordinary data block as a normal conflict alert. At most one side is untracked here.
            var intruder =
                (!trackedA && !correlatedA) ? pair.CallsignA
                : (!trackedB && !correlatedB) ? pair.CallsignB
                : null;

            if (conflicts.TryGetValue(pair.Id, out var existing))
            {
                existing.OwnerFacilityA = facilityA;
                existing.OwnerFacilityB = facilityB;
                existing.IntruderCallsign = intruder;
                continue;
            }

            var conflict = new EramActiveConflict
            {
                Id = pair.Id,
                CallsignA = pair.CallsignA,
                CallsignB = pair.CallsignB,
                OwnerFacilityA = facilityA,
                OwnerFacilityB = facilityB,
                IntruderCallsign = intruder,
            };
            conflicts[pair.Id] = conflict;
            newConflicts.Add(conflict);

            _logger.LogWarning(
                "ERAM STCA detected: {CallsignA} <-> {CallsignB} at t={T}s",
                pair.CallsignA,
                pair.CallsignB,
                Scenario?.ElapsedSeconds ?? 0
            );
        }

        var clearedIds = new List<string>();
        foreach (var id in existingIds)
        {
            if (!detectedIds.Contains(id))
            {
                conflicts.Remove(id);
                clearedIds.Add(id);
            }
        }

        return new EramConflictAlertChanges(newConflicts, clearedIds);
    }

    /// <summary>
    /// Per-second auto-delete pass: decides which aircraft to remove (per-aircraft <c>ONHS DEL</c> opt-in,
    /// stuck-after-landing at a layout-less airport, a generated overflight past its exit radius, or the
    /// scenario's <c>OnLanding</c>/<c>Parked</c> mode), removes them from the world, and returns the removed
    /// states so the host can fan out its delete broadcasts (each state carries the last position for a
    /// surface-track coast/drop). Server-only: only a broadcasting host consumes the result, so
    /// <see cref="TickPostPhysics"/> does not call this. The narrower <see cref="SweepPendingAutoDeletes"/>
    /// stays for standalone tests that only exercise the <c>PendingAutoDelete</c> path.
    /// </summary>
    public IReadOnlyList<AircraftState> TickAutoDelete()
    {
        if (Scenario is not { } scenario)
        {
            return [];
        }

        var mode = scenario.EffectiveAutoDeleteMode;
        bool modeDisabled =
            string.IsNullOrEmpty(mode)
            || mode.Equals("None", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("Never", StringComparison.OrdinalIgnoreCase);

        var toDelete = new List<AircraftState>();
        foreach (var ac in World.GetSnapshot())
        {
            // Per-aircraft opt-in raised by a queued ONHS DEL (or any queued DeleteCommand) whose trigger
            // has fired. This bypasses AutoDeleteExempt — the controller explicitly asked for the delete.
            bool perAircraftDelete = ac.Ground.PendingAutoDelete;

            if (!perAircraftDelete && ac.Ground.AutoDeleteExempt)
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

        foreach (var ac in toDelete)
        {
            // An overflight leaving its corridor is a completed transit, not a drop: stamp it so the removal below
            // records a debrief row (landings and handoffs are stamped where they happen).
            if ((ac.CompletionReason == CompletionReason.Active) && HasLeftOverflightCorridor(ac, scenario.PrimaryAirportId))
            {
                ac.CompletedAtSeconds = scenario.ElapsedSeconds;
                ac.CompletionReason = CompletionReason.Transited;
            }

            World.RemoveAircraft(ac.Callsign);
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

        if (string.IsNullOrEmpty(primaryAirportId) || NavigationDatabase.Instance.GetFixPosition(primaryAirportId) is not { } airport)
        {
            return false;
        }

        return GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, airport.Lat, airport.Lon) > exitDistanceNm;
    }

    /// <summary>
    /// Per-second solo-training evaluation: builds the evaluation context from the active scenario, runs
    /// the solo-training evaluator against the current world, and returns the resulting events for the host
    /// to broadcast. Empty when not in solo mode. Server-only: the standalone/replay host has no controller
    /// to notify, so <see cref="TickPostPhysics"/> does not call this — the engine owns the evaluation
    /// orchestration and returns the results, and the host decides how to surface them.
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
                scenario.WakeDirectives
            )
        );
    }

    /// <summary>
    /// Post-physics: drains warnings, notifications, and approach scores from the world.
    /// The server reads these before calling this method to broadcast them.
    /// </summary>
    public void TickPostPhysics()
    {
        TickLiveTrafficRunwayUse();
        TickTransponders();
        TickVisualDetection();

        // Re-evaluate the engine-owned conflict sets. Their returned diffs exist for a broadcasting host to fan out
        // to CRC, so only the server consumes them — but the detection itself has to run on this path too. Without it
        // a conflict that RestoreFromSnapshot repopulated is never re-examined, so it is pinned for the rest of a
        // replay: simultaneously stale and unable to clear.
        TickConflictAlerts();
        TickEramConflictAlerts();

        // After the detectors, matching the live server, where it is 14th of 32 — behind the ASDE-X and
        // solo-training passes this path does not have. A proactive rule that reads what visual detection or
        // conflict alerting writes therefore sees the same picture on both, which it did not when this ran
        // second here. It still precedes the drains below, so an airborne check-in emits the tick it is produced.
        TickPilotProactive();

        var warnings = World.DrainAllWarnings();
        foreach (var (callsign, warning) in warnings)
        {
            FireWarningEmitted(callsign, warning);
        }

        var stripDispatches = World.DrainAllStripDispatches();
        foreach (var (callsign, command) in stripDispatches)
        {
            FireStripDispatchRequested(callsign, command);
        }

        var notifications = World.DrainAllNotifications();
        foreach (var (callsign, notification) in notifications)
        {
            EmitTerminal("Response", callsign, notification);
        }

        var readbacks = World.DrainAllPilotReadbacks();
        foreach (var (callsign, readback) in readbacks)
        {
            EmitTerminal("SayReadback", callsign, readback);
        }

        // The one buffer this path used to leave un-drained. Phases and handlers that run under replay
        // produce into it, so without this it grows for the whole session and any assertion against
        // PendingPilotSpeech matches a line from an arbitrarily earlier tick.
        var pilotSpeech = World.DrainAllPilotSpeech();
        foreach (var (callsign, speech) in pilotSpeech)
        {
            EmitTerminal("PilotSpeech", callsign, speech);
            FirePilotSpeechEmitted(callsign, speech);
        }

        // Pilot transmissions are addressed to whoever answers pilots — the solo student or an AI position; with nobody
        // answering (an instructor room with the AI off) they are discarded, the pre-roster instructor-mode behavior.
        if (Scenario is { } activeScenario && activeScenario.PilotContacts.AnyAnswering)
        {
            foreach (var transmission in World.DrainReadyPilotTransmissions(activeScenario.ElapsedSeconds))
            {
                EmitTerminal(ToSayKind(transmission), transmission.Callsign, transmission.Text);
            }
        }
        else
        {
            World.DiscardAllPilotTransmissions();
        }

        World.DrainAllApproachScores();
    }

    /// <summary>
    /// Removes any aircraft whose <see cref="AircraftGroundOps.PendingAutoDelete"/>
    /// flag is set. Returns the callsigns that were removed. Hosting servers (e.g.
    /// yaat-server's <c>TickProcessor.ProcessAutoDelete</c>) call this after their
    /// per-tick post-physics step so they can fan out CRC/SignalR delete broadcasts
    /// for each removed callsign. Standalone Yaat.Sim tests can call this directly
    /// to observe end-to-end <c>ONHS DEL</c> behaviour without a server wrapper.
    /// </summary>
    public IReadOnlyList<string> SweepPendingAutoDeletes()
    {
        var removed = new List<string>();
        foreach (var ac in World.GetSnapshot())
        {
            if (!ac.Ground.PendingAutoDelete)
            {
                continue;
            }

            removed.Add(ac.Callsign);
            World.RemoveAircraft(ac.Callsign);
        }
        return removed;
    }

    private static string ToSayKind(PilotTransmission transmission) =>
        transmission.Kind == PilotTransmissionKind.SayReadback ? "SayReadback" : "SayPilot";

    private static LatLon? LookupAirportPosition(string airportId)
    {
        var pos = NavigationDatabase.Instance.GetFixPosition(airportId);
        return pos.HasValue ? new LatLon(pos.Value.Lat, pos.Value.Lon) : null;
    }

    /// <summary>
    /// Convenience wrapper that runs all three phases for one sim-second.
    /// Used by the client and tests. Drains and discards terminal entries.
    /// </summary>
    /// <summary>The controller AI hosted on this engine, or null when the session runs without one. Set by the host after load.</summary>
    public AiControllerService? ControllerAi { get; set; }

    /// <summary>
    /// One AI tick, run by the host after the sim-second it observes — <c>RoomEngine.AdvanceLiveSecond</c> on the
    /// server. <see cref="TickOneSecond"/> does NOT call this, so a standalone host that wants the brains to run
    /// invokes it itself after the second (see the test hosts). Never runs in a replay (<see cref="RunProfile.RunsControllerAi"/>)
    /// — an AI-driven recording carries the AI's commands as recorded actions, and re-running the brains would double them.
    /// </summary>
    public void TickControllerAi()
    {
        if (Scenario is not { ControllerAi: not null } scenario || !RunProfile.RunsControllerAi || ControllerAi is not { } ai)
        {
            return;
        }

        ai.Tick(
            new AiTickInputs(
                scenario,
                World,
                World.GetSnapshot(),
                ResolveGroundLayout,
                RunwayOccupancy.AirportRunways,
                ConflictAlerts.Conflicts.Values.ToList(),
                EramConflicts.Conflicts.Values.ToList()
            )
        );
    }

    public void TickOneSecond()
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        scenario.ElapsedSeconds += 1;

        TickPrePhysics();

        double subDelta = 1.0 / PhysicsSubTickRate;
        for (int sub = 0; sub < PhysicsSubTickRate; sub++)
        {
            TickPhysics(subDelta);
        }

        TickPostPhysics();

        // Discard terminal entries (client doesn't use them yet)
        _terminalEntries.Clear();

        FireTickCompleted((int)scenario.ElapsedSeconds);
    }

    /// <summary>
    /// Advance one physics sub-tick (0.25 s). Does NOT run pre/post-physics or
    /// advance <c>ElapsedSeconds</c> — call this in a manual tick loop for
    /// fine-grained inspection (e.g., tick-by-tick test traces).
    /// </summary>
    public void TickOnce()
    {
        TickPhysics(1.0 / PhysicsSubTickRate);
    }

    // --- Replay ---

    /// <summary>
    /// Computes the hold-short nodes currently occupied by a holding or exiting aircraft from live
    /// aircraft state. Includes runway hold-short nodes an aircraft's tail hangs over while holding
    /// short of a taxiway (issue #172). The per-tick cache is transient, so this recomputes on demand —
    /// for diagnostics and tests querying between ticks.
    /// </summary>
    public IReadOnlySet<int> ComputeOccupiedHoldShortNodes() => BuildOccupiedHoldShortNodes();

    private HashSet<int> BuildOccupiedHoldShortNodes()
    {
        var occupied = new HashSet<int>();
        foreach (var ac in World.GetSnapshot())
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
                continue;
            }

            // Aircraft navigating toward an exit are claiming their target hold-short node
            if (ac.Phases?.CurrentPhase is RunwayExitPhase rep && rep.TargetHoldShortNodeId is { } repNodeId)
            {
                occupied.Add(repNodeId);
                continue;
            }

            // Aircraft holding after runway exit occupy their hold-short node
            if (ac.Phases?.CurrentPhase is HoldingAfterExitPhase haep && haep.HoldShortNodeId is { } haepNodeId)
            {
                occupied.Add(haepNodeId);
            }
        }

        return occupied;
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
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        var runway = aircraft.Phases?.AssignedRunway;
        var groundLayout = aircraft.Ground.Layout ?? ResolveGroundLayout(aircraft);
        var occupiedNodes = _occupiedHoldShortNodes;

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
            Weather = World.Weather,
            ScenarioElapsedSeconds = Scenario?.ElapsedSeconds ?? 0,
            AutoClearedToLand = Scenario?.AutoClearedToLand ?? false,
            AutoPullUpToParallel = Scenario?.AutoPullUpToParallel ?? false,
            AutoGoAroundOnOccupiedRunway = Scenario?.AutoGoAroundOnOccupiedRunway ?? false,
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
            StudentPosition = Scenario?.StudentPosition,
            ArtccId = Scenario?.ArtccId,
            PrimaryAirportId = Scenario?.PrimaryAirportId,
            AtisLetter = PilotResponder.ResolvePrimaryFieldAtisLetter(Scenario),
            InitialContactTransfers = Scenario?.InitialContactTransfers ?? Yaat.Sim.Data.InitialContactTransferCatalog.Empty,
            PilotContacts = Scenario?.PilotContacts ?? PilotContactRoster.Empty,
            IsHoldShortNodeOccupied = occupiedNodes is not null ? nodeId => occupiedNodes.Contains(nodeId) : null,
            OccupiedHoldShortNodes = occupiedNodes,
            MarkHoldShortNodeOccupied = occupiedNodes is not null ? nodeId => occupiedNodes.Add(nodeId) : null,
            TowerPosition = (Scenario?.IsStudentTowerPosition == true) ? Scenario.StudentPosition : null,
            // Phases that consult follow targets (pattern spacing, VfrFollowPhase)
            // need a way to resolve the lead aircraft by callsign.
            AircraftLookup = World.FindAircraft,
        };
    }
}
