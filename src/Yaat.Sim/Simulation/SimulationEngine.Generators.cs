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

// Traffic generators -- arrival, VFR and overflight spawning, spacing and weight selection.
public sealed partial class SimulationEngine
{
    private readonly List<GeneratorSpawnRecord> _generatorSpawnLog = [];

    /// <summary>Diagnostic log of arrival-generator spawns (distance, spacing, timing) for the session.</summary>
    public IReadOnlyList<GeneratorSpawnRecord> GeneratorSpawnLog => _generatorSpawnLog;

    private void ProcessDelayedSpawns(List<AircraftState> spawned)
    {
        var scenario = Scenario!;
        for (int i = scenario.DelayedQueue.Count - 1; i >= 0; i--)
        {
            var entry = scenario.DelayedQueue[i];

            // Hold-for-release spawn gate: a held runway/airborne departure does not appear on the
            // scope while its airport is armed — it spawns only when released (REL clears the flag).
            if (HeldReleaseService.IsSpawnHeld(scenario, entry))
            {
                continue;
            }

            if (scenario.ElapsedSeconds >= entry.SpawnAtSeconds)
            {
                scenario.DelayedQueue.RemoveAt(i);
                entry.Aircraft.State.SpawnedAtSeconds = scenario.ElapsedSeconds;
                // A ground (parking/taxiway) departure spawning under an armed airport holds short
                // until released — mark it now so the runway-entry gate withholds LUAW/CTO.
                HeldReleaseService.MarkHeldOnSpawnIfArmed(scenario, entry.Aircraft.State);
                World.AddAircraft(entry.Aircraft.State);
                DispatchPresetCommands(entry.Aircraft);
                spawned.Add(entry.Aircraft.State);

                EmitTerminal("System", entry.Aircraft.State.Callsign, "[Spawn] Delayed");

                foreach (var msg in entry.Aircraft.AutoTrackMessages)
                {
                    EmitTerminal("System", entry.Aircraft.State.Callsign, msg);
                }
            }
        }

        if (spawned.Count > 0 && scenario.DelayedQueue.Count == 0)
        {
            EmitTerminal("System", "", "[Scenario] No delayed spawns left");
        }
    }

    private const double SpawnRetryBackoffSeconds = 5.0;
    private const double FinalCorridorHalfWidthNm = 2.0;
    private const double FinalCorridorMarginNm = 3.0;
    private const double TerminalRadarFloorNm = 3.0;

    /// <summary>How far past its spawn corridor an overflight flies before it is deleted, when the author gives no exitDistance.</summary>
    private const double DefaultOverflightExitMarginNm = 5.0;

    private void ProcessGenerators(List<GeneratorSpawn> generatorSpawns)
    {
        var scenario = Scenario!;
        if (
            (_isReplayingRecordedActions && _replayHasRecordedAircraftSpawns)
            || (scenario.IsPlaybackMode && scenario.ActionLog.Any(static a => a is RecordedAircraftSpawn))
        )
        {
            return;
        }

        // The solo arrival-rate slider scales arrival streams only; overflights are not an arrival source.
        var ratePercent = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);
        if (ratePercent > 0)
        {
            foreach (var gen in scenario.Generators)
            {
                if (IsGeneratorActive(gen))
                {
                    TrySpawnArrival(gen, ratePercent, generatorSpawns);
                }
            }

            foreach (var gen in scenario.VfrArrivalGenerators)
            {
                if (IsGeneratorActive(gen))
                {
                    TrySpawnVfrArrival(gen, ratePercent, generatorSpawns);
                }
            }
        }

        foreach (var gen in scenario.OverflightGenerators)
        {
            if (IsGeneratorActive(gen))
            {
                TrySpawnOverflight(gen, generatorSpawns);
            }
        }
    }

    /// <summary>
    /// Derives activation fresh each tick (never latched, so an instructor can switch a generator back on
    /// after its window has expired) and logs the transition once. When a generator is switched on manually
    /// while its next spawn is still scheduled in the future, the cadence is pulled forward so ticking the
    /// Active box produces traffic immediately rather than after a silent wait.
    /// </summary>
    private bool IsGeneratorActive(IGeneratorRuntimeState state)
    {
        var scenario = Scenario!;
        var config = state.ConfigBase;
        var isActive = GeneratorActivation.IsActive(config, scenario.ElapsedSeconds);

        if (isActive != state.WasActive)
        {
            if (isActive && config.Enabled == true)
            {
                state.NextSpawnSeconds = Math.Min(state.NextSpawnSeconds, scenario.ElapsedSeconds);
            }

            _logger.LogInformation(
                "Generator '{Id}' {Transition} at t={T}s",
                config.Id,
                isActive ? "activated" : "deactivated",
                scenario.ElapsedSeconds
            );
            state.WasActive = isActive;
        }

        return isActive;
    }

    /// <summary>
    /// A generator with a randomized interval gets a random initial phase within its first interval, so
    /// several generators sharing a <c>StartTimeOffset</c> don't all fire on the same tick.
    /// </summary>
    private double StaggeredFirstSpawn(IGeneratorConfig config)
    {
        var firstSpawnSeconds = (double)config.StartTimeOffset;
        if (config.RandomizeInterval)
        {
            firstSpawnSeconds += World.Rng.NextDouble() * config.IntervalTime;
        }
        return firstSpawnSeconds;
    }

    /// <summary>
    /// Time-first spawn: <see cref="ScenarioGeneratorConfig.IntervalTime"/> drives cadence (when the next
    /// arrival is due). When due, the new arrival is placed at the back of the stream at
    /// <c>D = max(InitialDistance, rearmostDistance + gap)</c>, where <c>gap</c> is the larger (binding) of
    /// the configured <c>IntervalDistance</c> and the 7110.65 wake minimum. The placement is capped at
    /// <c>MaxDistance</c>: if no room exists within the cap the spawn waits (retry backoff) so the cap is
    /// never exceeded. An empty corridor has no rearmost, so the arrival spawns exactly at
    /// <c>InitialDistance</c> — the cold start needs no special case.
    /// </summary>
    private void TrySpawnArrival(GeneratorState gen, int ratePercent, List<GeneratorSpawn> generatorSpawns)
    {
        var scenario = Scenario!;
        if (scenario.ElapsedSeconds < gen.NextSpawnSeconds)
        {
            return;
        }

        var engine = ResolveEngine(gen.Config.EngineType);
        var weight = ResolveWeight(gen.Config, engine, World.Rng);
        var rearmost = RearmostInbound(gen);

        double gap;
        double placement;
        if (rearmost is null)
        {
            gap = 0;
            placement = gen.Config.InitialDistance;
        }
        else
        {
            var (leaderDistance, leader) = rearmost.Value;
            gap = SpacingGapNm(gen, leader, weight);
            placement = Math.Max(gen.Config.InitialDistance, leaderDistance + gap);
        }

        if (placement > gen.Config.MaxDistance)
        {
            // No room within the corridor cap — wait and retry so the average rate is preserved.
            if (rearmost is null)
            {
                _logger.LogWarning(
                    "Generator '{Id}' cannot place arrival: InitialDistance {Init}nm exceeds MaxDistance {Max}nm",
                    gen.Config.Id,
                    gen.Config.InitialDistance,
                    gen.Config.MaxDistance
                );
            }
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        var state = SpawnGeneratedArrival(gen, placement, weight, engine);
        if (state is null)
        {
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        generatorSpawns.Add(new GeneratorSpawn(state, gen.Config.AutoTrackConfiguration));
        _generatorSpawnLog.Add(
            new GeneratorSpawnRecord(gen.Config.Id, state.Callsign, scenario.ElapsedSeconds, placement, rearmost?.DistanceNm, gap)
        );
        gen.NextSpawnSeconds = scenario.ElapsedSeconds + EffectiveSpawnIntervalSeconds(gen, ratePercent);
    }

    private double EffectiveSpawnIntervalSeconds(GeneratorState gen, int ratePercent) =>
        JitteredInterval(gen.Config, ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(gen.Config.IntervalTime, ratePercent));

    /// <summary>Applies the generator's ±25% interval jitter, never dropping below the retry backoff.</summary>
    private double JitteredInterval(IGeneratorConfig config, double intervalSeconds)
    {
        if (config.RandomizeInterval)
        {
            var jitter = intervalSeconds * 0.25;
            intervalSeconds += ((World.Rng.NextDouble() * 2) - 1) * jitter;
        }
        return Math.Max(intervalSeconds, SpawnRetryBackoffSeconds);
    }

    /// <summary>
    /// Minimum in-trail gap (nm) the new arrival must sit behind the rearmost aircraft inbound to the
    /// runway: the largest (binding) of the generator's configured <c>IntervalDistance</c>, the 3 NM
    /// terminal radar floor, and the 7110.65 Table 5-5-2 wake-turbulence minimum for the leader/follower
    /// pair. The constraints bind, they do not add — a 5 nm author spacing behind a non-wake leader stays
    /// 5 nm, while a heavy leader can widen it to the wake minimum. The follower's specific type is not yet
    /// chosen at placement time, so the wake floor uses the coarse weight-class minima (the leader's class
    /// still reflects its CWT category); ATPA spacing uses the precise per-type CWT minima.
    /// </summary>
    private static double SpacingGapNm(GeneratorState gen, AircraftState leader, WeightClass followerWeight)
    {
        var wakeFloor = WakeTurbulenceData.OnApproachWakeSeparationNm(
            WakeTurbulenceData.WakeClassForType(leader.AircraftType, AircraftCategorization.Categorize(leader.AircraftType)),
            WakeClassForWeight(followerWeight)
        );
        return Math.Max(gen.Config.IntervalDistance, Math.Max(TerminalRadarFloorNm, wakeFloor));
    }

    private static WakeTurbulenceData.WakeClass WakeClassForWeight(WeightClass weight) =>
        weight switch
        {
            WeightClass.Heavy => WakeTurbulenceData.WakeClass.Heavy,
            WeightClass.Small => WakeTurbulenceData.WakeClass.Small,
            // SmallPlus spans CWT G (weightCode Large) and H (weightCode Small); Large is the
            // conservative-realistic coarse class for the on-approach wake floor behind it.
            WeightClass.SmallPlus => WakeTurbulenceData.WakeClass.Large,
            _ => WakeTurbulenceData.WakeClass.Large,
        };

    /// <summary>
    /// Airborne aircraft inside the runway's final-approach corridor (any generator's arrivals plus
    /// manual adds), each with its along-final distance-to-threshold (nm). Used so concurrent streams to
    /// the same runway don't overlap and the cold-start seed doesn't double up on existing traffic.
    /// </summary>
    private List<(double DistanceNm, AircraftState Aircraft)> CorridorAircraft(GeneratorState gen)
    {
        var rwy = gen.Runway;
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var outbound = new TrueHeading((rwy.TrueHeading.Degrees + 180.0) % 360.0);
        var maxAlong = gen.Config.MaxDistance + FinalCorridorMarginNm;

        var result = new List<(double DistanceNm, AircraftState Aircraft)>();
        foreach (var ac in World.GetSnapshot())
        {
            if (ac.IsOnGround)
            {
                continue;
            }
            var cross = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, outbound));
            if (cross > FinalCorridorHalfWidthNm)
            {
                continue;
            }
            var along = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, outbound);
            if (along <= 0 || along > maxAlong)
            {
                continue;
            }
            result.Add((along, ac));
        }
        return result;
    }

    /// <summary>
    /// Rearmost (greatest distance-to-threshold) aircraft in the runway's final-approach corridor, or
    /// null when the corridor is empty.
    /// </summary>
    private (double DistanceNm, AircraftState Aircraft)? RearmostInbound(GeneratorState gen)
    {
        (double DistanceNm, AircraftState Aircraft)? rearmost = null;
        foreach (var entry in CorridorAircraft(gen))
        {
            if (rearmost is null || entry.DistanceNm > rearmost.Value.DistanceNm)
            {
                rearmost = entry;
            }
        }
        return rearmost;
    }

    /// <summary>
    /// In-trail speed management for the arrival-generator stream — the simulated approach
    /// controller (TRACON) that feeds correctly-spaced traffic to the tower (LC) student. Each
    /// tick, for every generator runway, pairs each generator-arrival follower on final with the
    /// aircraft immediately ahead and stamps a <see cref="ControlTargets.SpeedCeiling"/> so the
    /// follower equalizes to its leader and holds the spawn spacing (<c>SpacingGapNm</c>) down
    /// the final instead of overrunning it (the QXE831/SWA8154 compression). The ceiling only
    /// ever lowers the phase's speed target (<see cref="FlightPhysics.UpdateSpeed"/> applies it
    /// as a continuous <c>min</c>), floors at the follower's Vref, and collapses to Vref by the
    /// threshold, so it never blocks the landing deceleration. Uses no RNG, so replay/rewind stay
    /// deterministic; it runs during replay too (old recordings have <c>IsGeneratorArrival</c>
    /// false and are unaffected).
    /// </summary>
    private void ApplyArrivalSpacing()
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        foreach (var gen in scenario.Generators)
        {
            var stream = CorridorAircraft(gen)
                .Where(e => string.Equals(e.Aircraft.Phases?.AssignedRunway?.Designator, gen.Runway.Designator, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.DistanceNm)
                .ToList();

            for (int i = 0; i < stream.Count; i++)
            {
                var (followerDist, follower) = stream[i];

                // Scope: only generator arrivals actively on final are managed as followers.
                if (!follower.IsGeneratorArrival || follower.Phases?.CurrentPhase is not FinalApproachPhase)
                {
                    continue;
                }

                // Override: once the controller touches this aircraft's speed or the student
                // takes the track, hand speed authority back for good (one-way latch).
                if (follower.Approach.AutoSpacingReleased || ShouldReleaseAutoSpacing(follower, scenario))
                {
                    follower.Approach.AutoSpacingReleased = true;
                    ReleaseManagedSpeedCeiling(follower);
                    continue;
                }

                // The lead aircraft of the stream has no one to follow — fly the normal profile.
                if (i == 0)
                {
                    ReleaseManagedSpeedCeiling(follower);
                    continue;
                }

                var (leaderDist, leader) = stream[i - 1];
                var followerCategory = AircraftCategorization.Categorize(follower.AircraftType);
                double vref = AircraftPerformance.ApproachSpeed(follower.AircraftType, followerCategory);
                double scheduled = ArrivalSpacingManager.ScheduledFinalSpeedKts(
                    follower.AircraftType,
                    followerCategory,
                    vref,
                    follower.Callsign,
                    followerDist
                );
                double wakeFloor = WakeTurbulenceData.OnApproachWakeSeparationNm(
                    leader.AircraftType,
                    AircraftCategorization.Categorize(leader.AircraftType),
                    follower.AircraftType,
                    AircraftCategorization.Categorize(follower.AircraftType)
                );
                double target = Math.Max(gen.Config.IntervalDistance, Math.Max(TerminalRadarFloorNm, wakeFloor));
                double gap = followerDist - leaderDist;

                follower.Targets.SpeedCeiling = ArrivalSpacingManager.SpacingCeilingKts(leader.IndicatedAirspeed, gap, target, vref, scheduled);
            }
        }
    }

    /// <summary>
    /// True when the in-trail spacing manager should hand speed authority back for this
    /// generator arrival: a manual speed command was issued, its speed restrictions were
    /// deleted, or the student controller now owns the track (the simulated TRACON spaces an
    /// arrival only while it owns it).
    /// </summary>
    private static bool ShouldReleaseAutoSpacing(AircraftState aircraft, SimScenarioState scenario)
    {
        if (aircraft.Targets.HasExplicitSpeedCommand || aircraft.Procedure.SpeedRestrictionsDeleted)
        {
            return true;
        }

        return aircraft.Track.Owner is { } owner && scenario.StudentPosition is { } student && owner.MatchesPosition(student);
    }

    /// <summary>
    /// Clears a <see cref="ControlTargets.SpeedCeiling"/> the spacing manager owns. Generator
    /// arrivals spawn directly on final with their navigation route cleared, so they carry no
    /// crossing-speed or procedural ceiling — the manager is the sole non-manual ceiling source.
    /// Skips when the controller has set an explicit speed (which owns the ceiling).
    /// </summary>
    private static void ReleaseManagedSpeedCeiling(AircraftState aircraft)
    {
        if (!aircraft.Targets.HasExplicitSpeedCommand && aircraft.Targets.SpeedCeiling is not null)
        {
            aircraft.Targets.SpeedCeiling = null;
        }
    }

    /// <summary>
    /// Builds, adds, records, and announces one generated arrival placed <c>OnFinal</c> at
    /// <paramref name="distanceNm"/> with the already-resolved <paramref name="weight"/> and
    /// <paramref name="engine"/>. Returns the spawned state, or null if generation failed.
    /// </summary>
    private AircraftState? SpawnGeneratedArrival(GeneratorState gen, double distanceNm, WeightClass weight, EngineKind engine)
    {
        var scenario = Scenario!;
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = weight,
            Engine = engine,
            PositionType = SpawnPositionType.OnFinal,
            RunwayId = gen.Config.Runway,
            FinalDistanceNm = distanceNm,
            PreferredAirlineAirportId = scenario.PrimaryAirportId,
        };

        var existing = World.GetSnapshot();
        var groundLayout = scenario.PrimaryAirportId is not null ? _groundData.GetLayout(scenario.PrimaryAirportId) : null;
        var (state, error) = AircraftGenerator.Generate(request, scenario.PrimaryAirportId, existing, groundLayout, World.Rng, BeaconCodePool);

        if (state is null)
        {
            _logger.LogWarning("Generator '{Id}' spawn failed at t={T}s: {Error}", gen.Config.Id, scenario.ElapsedSeconds, error);
            return null;
        }

        state.ScenarioId = scenario.ScenarioId;
        state.Ground.Layout = groundLayout;
        state.SpawnedAtSeconds = scenario.ElapsedSeconds;
        state.IsGeneratorArrival = true;

        World.AddAircraft(state);

        // A generator without autotrack has no owner/scratchpad to wait for, so record it now. When the
        // generator carries an AutoTrackConfiguration, the server applies it then calls RecordGeneratedSpawn
        // so the recorded snapshot captures the owner/scratchpad and replays with them intact.
        if (gen.Config.AutoTrackConfiguration is null)
        {
            RecordGeneratedAircraftSpawn(state);
        }

        EmitTerminal("System", state.Callsign, $"[Spawn] Generated ({gen.Config.Id})");

        _logger.LogInformation(
            "Generator '{Id}' spawned {Callsign} ({Type}) at {Dist:F1}nm on RWY {Runway}, t={T}s",
            gen.Config.Id,
            state.Callsign,
            state.AircraftType,
            distanceNm,
            gen.Config.Runway,
            scenario.ElapsedSeconds
        );

        return state;
    }

    private void TrySpawnVfrArrival(VfrArrivalGeneratorState gen, int ratePercent, List<GeneratorSpawn> generatorSpawns)
    {
        var scenario = Scenario!;
        if (scenario.ElapsedSeconds < gen.NextSpawnSeconds)
        {
            return;
        }

        var state = SpawnGeneratedVfrArrival(gen);
        if (state is null)
        {
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        generatorSpawns.Add(new GeneratorSpawn(state, gen.Config.AutoTrackConfiguration));
        gen.NextSpawnSeconds =
            scenario.ElapsedSeconds
            + JitteredInterval(gen.Config, ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(gen.Config.IntervalTime, ratePercent));
    }

    private void TrySpawnOverflight(OverflightGeneratorState gen, List<GeneratorSpawn> generatorSpawns)
    {
        var scenario = Scenario!;
        if (scenario.ElapsedSeconds < gen.NextSpawnSeconds)
        {
            return;
        }

        var state = SpawnGeneratedOverflight(gen);
        if (state is null)
        {
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        generatorSpawns.Add(new GeneratorSpawn(state, null));
        gen.NextSpawnSeconds = scenario.ElapsedSeconds + JitteredInterval(gen.Config, gen.Config.IntervalTime);
    }

    /// <summary>
    /// Rolls a bearing/distance/altitude inside the generator's configured ranges until the resulting point
    /// is legal to spawn into — clear of Class B/C (a 1200 code cannot appear inside either) and clear of
    /// standard radar separation from every airborne aircraft. Returns null when the ranges cannot produce
    /// a usable point, which is an authoring problem rather than a transient one.
    /// </summary>
    private (LatLon Position, double BearingTrue, double BearingMagnetic, double DistanceNm, double AltitudeFt)? RollVfrSpawnSite(
        string generatorId,
        LatLon airport,
        double bearingFrom,
        double bearingTo,
        double minDistanceNm,
        double maxDistanceNm,
        double minAltitudeFt,
        double maxAltitudeFt
    )
    {
        var existing = World.GetSnapshot();
        var airspace = AirspaceDatabase.Default;

        for (var attempt = 0; attempt < VfrSpawnSiting.MaxSpawnAttempts; attempt++)
        {
            var bearingMagnetic = VfrSpawnSiting.RollBearing(bearingFrom, bearingTo, World.Rng);
            var distanceNm = VfrSpawnSiting.RollInRange(minDistanceNm, maxDistanceNm, World.Rng);
            var altitudeFt = VfrSpawnSiting.RollInRange(minAltitudeFt, maxAltitudeFt, World.Rng);

            var bearingTrue = MagneticDeclination.MagneticToTrue(bearingMagnetic, airport, Scenario!.MagneticModelDateUtc);
            var (lat, lon) = GeoMath.ProjectPoint(airport.Lat, airport.Lon, new TrueHeading(bearingTrue), distanceNm);
            var position = new LatLon(lat, lon);

            if (VfrSpawnSiting.IsUsableSpawn(position, altitudeFt, airspace, existing))
            {
                return (position, bearingTrue, bearingMagnetic, distanceNm, altitudeFt);
            }
        }

        _logger.LogWarning(
            "Generator '{Id}': no spawn point clear of Class B/C and existing traffic after {Attempts} attempts "
                + "(bearing {From}-{To}, {MinD}-{MaxD}nm, {MinA}-{MaxA}ft)",
            generatorId,
            VfrSpawnSiting.MaxSpawnAttempts,
            bearingFrom,
            bearingTo,
            minDistanceNm,
            maxDistanceNm,
            minAltitudeFt,
            maxAltitudeFt
        );
        return null;
    }

    /// <summary>
    /// Spawns one VFR arrival on the generator's bearing arc, proceeding direct to the configured fix (or the
    /// field). It files a VFR plan to the primary airport rather than cold-calling: an arriving VFR aircraft
    /// at a Class C primary must establish two-way and be sequenced (7110.65 §7-8-2.a.1), so it is already
    /// receiving service and holds a discrete code — which also lets auto-delete recognise it once it lands.
    /// </summary>
    private AircraftState? SpawnGeneratedVfrArrival(VfrArrivalGeneratorState gen)
    {
        var scenario = Scenario!;
        var config = gen.Config;
        var airportId = scenario.PrimaryAirportId;
        if (string.IsNullOrEmpty(airportId))
        {
            _logger.LogWarning("VFR arrival generator '{Id}' skipped: scenario has no primary airport", config.Id);
            return null;
        }

        var airportPos = NavigationDatabase.Instance.GetFixPosition(airportId);
        if (airportPos is null)
        {
            _logger.LogWarning("VFR arrival generator '{Id}': primary airport '{Airport}' not in navdata", config.Id, airportId);
            return null;
        }

        var airport = new LatLon(airportPos.Value.Lat, airportPos.Value.Lon);
        var site = RollVfrSpawnSite(
            config.Id,
            airport,
            config.BearingFrom,
            config.BearingTo,
            config.InitialDistance,
            config.MaxDistance,
            config.AltitudeMin,
            config.AltitudeMax
        );
        if (site is null)
        {
            return null;
        }

        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Vfr,
            Weight = ParseWeightCategory(config.WeightCategory),
            Engine = ResolveEngine(config.EngineType),
            PositionType = SpawnPositionType.Bearing,
            Bearing = site.Value.BearingTrue,
            DistanceNm = site.Value.DistanceNm,
            Altitude = site.Value.AltitudeFt,
            VfrFiledDestination = airportId,
        };

        var groundLayout = _groundData.GetLayout(airportId);
        var (state, error) = AircraftGenerator.Generate(request, airportId, World.GetSnapshot(), groundLayout, World.Rng, BeaconCodePool);
        if (state is null)
        {
            _logger.LogWarning("VFR arrival generator '{Id}' spawn failed at t={T}s: {Error}", config.Id, scenario.ElapsedSeconds, error);
            return null;
        }

        state.ScenarioId = scenario.ScenarioId;
        state.Ground.Layout = groundLayout;
        state.SpawnedAtSeconds = scenario.ElapsedSeconds;
        state.FlightPlan.Altitude = PlannedAltitude.Vfr((int)Math.Round(site.Value.AltitudeFt));

        var routeWarnings = new List<string>();
        var directTo = string.IsNullOrWhiteSpace(config.DirectTo) ? airportId : config.DirectTo;
        ArrivalRouteResolver.PopulateNavigationRoute(state, directTo, routeWarnings);
        foreach (var warning in routeWarnings)
        {
            _logger.LogWarning("VFR arrival generator '{Id}': {Warning}", config.Id, warning);
        }

        PointAtFirstRouteFix(state);
        ApplyInitialVerticalProfile(state, config, airportId);

        World.AddAircraft(state);
        if (config.AutoTrackConfiguration is null)
        {
            RecordGeneratedAircraftSpawn(state);
        }

        EmitTerminal("System", state.Callsign, $"[Spawn] Generated VFR arrival ({config.Id})");
        _logger.LogInformation(
            "VFR arrival generator '{Id}' spawned {Callsign} ({Type}) {Dist:F1}nm on the {Brg:F0} radial at {Alt:F0}ft, direct {Direct}, t={T}s",
            config.Id,
            state.Callsign,
            state.AircraftType,
            site.Value.DistanceNm,
            site.Value.BearingMagnetic,
            site.Value.AltitudeFt,
            directTo,
            scenario.ElapsedSeconds
        );

        return state;
    }

    /// <summary>
    /// Spawns one VFR transit: in on the generator's <c>From</c> arc, routed to an exit point on its <c>To</c>
    /// arc. Overflights stay cold calls squawking 1200 — realistic for a transient not receiving service, and
    /// legal because <see cref="RollVfrSpawnSite"/> keeps them clear of Class B/C.
    /// </summary>
    private AircraftState? SpawnGeneratedOverflight(OverflightGeneratorState gen)
    {
        var scenario = Scenario!;
        var config = gen.Config;
        var airportId = scenario.PrimaryAirportId;
        if (string.IsNullOrEmpty(airportId))
        {
            _logger.LogWarning("Overflight generator '{Id}' skipped: scenario has no primary airport", config.Id);
            return null;
        }

        var airportPos = NavigationDatabase.Instance.GetFixPosition(airportId);
        if (airportPos is null)
        {
            _logger.LogWarning("Overflight generator '{Id}': primary airport '{Airport}' not in navdata", config.Id, airportId);
            return null;
        }

        var airport = new LatLon(airportPos.Value.Lat, airportPos.Value.Lon);
        var exitDistanceNm = config.ExitDistance ?? (config.MaxDistance + DefaultOverflightExitMarginNm);

        var site = RollVfrSpawnSite(
            config.Id,
            airport,
            config.FromBearingFrom,
            config.FromBearingTo,
            config.InitialDistance,
            config.MaxDistance,
            config.AltitudeMin,
            config.AltitudeMax
        );
        if (site is null)
        {
            return null;
        }

        var exitBearingMagnetic = VfrSpawnSiting.RollBearing(config.ToBearingFrom, config.ToBearingTo, World.Rng);
        var exitBearingTrue = MagneticDeclination.MagneticToTrue(exitBearingMagnetic, airport, Scenario!.MagneticModelDateUtc);
        var exitPoint = GeoMath.ProjectPoint(airport, new TrueHeading(exitBearingTrue), exitDistanceNm);

        // Name the exit point as an FRD off the field so the route overlay labels it rather than drawing it
        // as an unnamed arc vertex.
        var exitName = $"{airportId}{(int)Math.Round(exitBearingMagnetic) % 360:000}{(int)Math.Round(exitDistanceNm):000}";

        // 91.159(a) binds level cruising flight more than 3000 ft above the surface, and it keys on the
        // aircraft's magnetic course over the ground -- which runs spawn -> exit point, not along the
        // author's "to" radial from the field.
        var altitudeFt = site.Value.AltitudeFt;
        if (config.SnapHemisphericAltitude)
        {
            altitudeFt = SnapOverflightAltitude(config, site.Value.Position, exitPoint, altitudeFt, airportId);
        }

        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Vfr,
            Weight = ParseWeightCategory(config.WeightCategory),
            Engine = ResolveEngine(config.EngineType),
            PositionType = SpawnPositionType.Bearing,
            Bearing = site.Value.BearingTrue,
            DistanceNm = site.Value.DistanceNm,
            Altitude = altitudeFt,
        };

        var groundLayout = _groundData.GetLayout(airportId);
        var (state, error) = AircraftGenerator.Generate(request, airportId, World.GetSnapshot(), groundLayout, World.Rng, BeaconCodePool);
        if (state is null)
        {
            _logger.LogWarning("Overflight generator '{Id}' spawn failed at t={T}s: {Error}", config.Id, scenario.ElapsedSeconds, error);
            return null;
        }

        state.ScenarioId = scenario.ScenarioId;
        state.Ground.Layout = groundLayout;
        state.SpawnedAtSeconds = scenario.ElapsedSeconds;
        state.IsGeneratedOverflight = true;
        state.OverflightExitDistanceNm = exitDistanceNm;

        state.Targets.NavigationRoute.Add(new NavigationTarget { Name = exitName, Position = exitPoint });
        PointAtFirstRouteFix(state);

        World.AddAircraft(state);
        RecordGeneratedAircraftSpawn(state);

        EmitTerminal("System", state.Callsign, $"[Spawn] Generated overflight ({config.Id})");
        _logger.LogInformation(
            "Overflight generator '{Id}' spawned {Callsign} ({Type}) {Dist:F1}nm on the {Brg:F0} radial at {Alt:F0}ft, "
                + "exiting on the {Exit:F0} radial at {ExitDist:F0}nm, t={T}s",
            config.Id,
            state.Callsign,
            state.AircraftType,
            site.Value.DistanceNm,
            site.Value.BearingMagnetic,
            altitudeFt,
            exitBearingMagnetic,
            exitDistanceNm,
            scenario.ElapsedSeconds
        );

        return state;
    }

    private double SnapOverflightAltitude(OverflightGeneratorConfig config, LatLon spawn, LatLon exitPoint, double rolledAltitudeFt, string airportId)
    {
        var fieldElevation = NavigationDatabase.Instance.GetAirportElevation(airportId) ?? 0;
        if (rolledAltitudeFt - fieldElevation <= HemisphericAltitude.AglFloorFt)
        {
            return rolledAltitudeFt;
        }

        var courseTrue = GeoMath.BearingTo(spawn, exitPoint);
        var courseMagnetic = MagneticDeclination.TrueToMagnetic(courseTrue, spawn, Scenario!.MagneticModelDateUtc);
        var snapped = HemisphericAltitude.Snap(courseMagnetic, rolledAltitudeFt, config.AltitudeMin, config.AltitudeMax);

        if (snapped is null)
        {
            _logger.LogWarning(
                "Overflight generator '{Id}': altitude band {Min}-{Max}ft contains no VFR cruising altitude for a "
                    + "{Course:F0} magnetic course; spawning at the rolled altitude. Widen the band or disable snapHemisphericAltitude.",
                config.Id,
                config.AltitudeMin,
                config.AltitudeMax,
                courseMagnetic
            );
            return rolledAltitudeFt;
        }

        return snapped.Value;
    }

    /// <summary>Turns a freshly spawned aircraft toward the first fix on its route, if it has one.</summary>
    private static void PointAtFirstRouteFix(AircraftState state)
    {
        if (state.Targets.NavigationRoute.Count == 0)
        {
            return;
        }

        var first = state.Targets.NavigationRoute[0].Position;
        TrueHeading heading = new(GeoMath.BearingTo(state.Position, first));
        state.TrueHeading = heading;
        state.TrueTrack = heading;
    }

    /// <summary>
    /// A level spawn (<c>initialVsFpm == 0</c>) gets no altitude target, so the controller steps it down.
    /// A descending spawn needs a target altitude to descend toward — physics zeroes vertical speed without
    /// one — defaulting to traffic-pattern altitude (AIM 4-3-3.a.1: 1000 ft AGL for propeller-driven, 1500
    /// for large/turbine). The authored rate is capped at the type's own descent performance.
    /// </summary>
    private static void ApplyInitialVerticalProfile(AircraftState state, VfrArrivalGeneratorConfig config, string airportId)
    {
        if (config.InitialVsFpm >= 0)
        {
            return;
        }

        var fieldElevation = NavigationDatabase.Instance.GetAirportElevation(airportId) ?? 0;
        var category = AircraftCategorization.Categorize(state.AircraftType);
        var patternAglFt = category is AircraftCategory.Jet or AircraftCategory.Turboprop ? 1500.0 : 1000.0;
        var descendTo = config.DescendToAltitude ?? (Math.Round((fieldElevation + patternAglFt) / 100.0) * 100.0);

        var performanceRate = AircraftPerformance.DescentRate(state.AircraftType, category, state.Altitude);
        state.Targets.TargetAltitude = Math.Min(descendTo, state.Altitude);
        state.Targets.DesiredVerticalRate = Math.Min(Math.Abs(config.InitialVsFpm), performanceRate);
    }

    private bool TryReserveSoloParkingInitialCallupSlot(double nowSeconds)
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return true;
        }

        return ScenarioPacing.TryReserveParkingInitialCallupSlot(scenario, nowSeconds);
    }

    public void ApplySoloPacingRates(
        int parkingInitialCallupRatePercent,
        int arrivalGeneratorRatePercent,
        int goAroundProbabilityPercent,
        bool rescheduleFromNow
    )
    {
        var scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        var oldParkingRate = ScenarioPacing.ClampParkingInitialCallupPercent(scenario.SoloParkingInitialCallupRatePercent);
        var newParkingRate = ScenarioPacing.ClampParkingInitialCallupPercent(parkingInitialCallupRatePercent);
        var parkingChanged = oldParkingRate != newParkingRate;
        var oldArrivalRate = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);
        var newArrivalRate = ScenarioPacing.ClampArrivalGeneratorPercent(arrivalGeneratorRatePercent);
        var arrivalChanged = oldArrivalRate != newArrivalRate;

        scenario.SoloParkingInitialCallupRatePercent = newParkingRate;
        scenario.SoloArrivalGeneratorRatePercent = newArrivalRate;
        scenario.SoloGoAroundProbabilityPercent = ScenarioPacing.ClampGoAroundProbabilityPercent(goAroundProbabilityPercent);

        if (rescheduleFromNow && parkingChanged)
        {
            RescheduleSoloParkingInitialCallupsFromNow(scenario, oldParkingRate, newParkingRate);
        }

        if (rescheduleFromNow && arrivalChanged)
        {
            RescheduleArrivalGeneratorsFromNow(scenario);
        }
    }

    private static void RescheduleSoloParkingInitialCallupsFromNow(SimScenarioState scenario, int oldRate, int newRate)
    {
        if (newRate <= 0)
        {
            scenario.NextSoloParkingInitialCallupSlotSeconds = double.PositiveInfinity;
            return;
        }

        var now = scenario.ElapsedSeconds;
        if ((oldRate <= 0) || (newRate > oldRate))
        {
            scenario.NextSoloParkingInitialCallupSlotSeconds = now;
            return;
        }

        if (newRate < oldRate)
        {
            var slowerSlot = now + ScenarioPacing.EffectiveParkingInitialCallupIntervalSeconds(newRate);
            scenario.NextSoloParkingInitialCallupSlotSeconds = double.IsPositiveInfinity(scenario.NextSoloParkingInitialCallupSlotSeconds)
                ? slowerSlot
                : Math.Max(scenario.NextSoloParkingInitialCallupSlotSeconds, slowerSlot);
        }
    }

    /// <summary>
    /// Re-phases every arrival stream after the solo arrival-rate slider moves. Overflight generators are
    /// not an arrival source and are not scaled by the slider, so they keep their cadence.
    /// </summary>
    private static void RescheduleArrivalGeneratorsFromNow(SimScenarioState scenario)
    {
        var rate = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);

        foreach (var gen in scenario.Generators.Cast<IGeneratorRuntimeState>().Concat(scenario.VfrArrivalGenerators))
        {
            if (!GeneratorActivation.IsActive(gen.ConfigBase, scenario.ElapsedSeconds))
            {
                continue;
            }

            gen.NextSpawnSeconds =
                rate <= 0
                    ? double.PositiveInfinity
                    : scenario.ElapsedSeconds + ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(gen.ConfigBase.IntervalTime, rate);
        }
    }

    private static WeightClass ResolveWeight(ScenarioGeneratorConfig config, EngineKind engine, Random rng)
    {
        var baseWeight = ParseWeightCategory(config.WeightCategory);
        return config.RandomizeWeightCategory ? RandomWeightForEngine(engine, baseWeight, rng) : baseWeight;
    }

    private static WeightClass ParseWeightCategory(string category) =>
        category switch
        {
            "Small" => WeightClass.Small,
            "SmallPlus" => WeightClass.SmallPlus,
            "Heavy" => WeightClass.Heavy,
            _ => WeightClass.Large,
        };

    /// <summary>
    /// The weight classes a randomize-weight generator may roll, with their relative shares, bounded to a
    /// band around the generator's configured base weight (aviation-reviewed). Bounding keeps a generator
    /// from feeding a runway an aircraft it can't take — a Small/SmallPlus generator (short runway) never
    /// rolls a mainline jet, and a Large/Heavy generator never drops below the upper-small tier:
    /// <list type="bullet">
    /// <item>Small / SmallPlus — {Small, SmallPlus} (light GA + upper-small business jets / commuters).</item>
    /// <item>Large — {SmallPlus, Large, Heavy}.</item>
    /// <item>Heavy — {Large, Heavy}.</item>
    /// </list>
    /// The configured base class always carries the plurality of the mix.
    /// </summary>
    private static IReadOnlyList<(WeightClass Weight, double Share)> BaseWeightBand(WeightClass baseWeight) =>
        baseWeight switch
        {
            WeightClass.Small => [(WeightClass.Small, 0.65), (WeightClass.SmallPlus, 0.35)],
            WeightClass.SmallPlus => [(WeightClass.Small, 0.35), (WeightClass.SmallPlus, 0.65)],
            WeightClass.Large => [(WeightClass.SmallPlus, 0.10), (WeightClass.Large, 0.80), (WeightClass.Heavy, 0.10)],
            WeightClass.Heavy => [(WeightClass.Large, 0.40), (WeightClass.Heavy, 0.60)],
            _ => [(WeightClass.Large, 1.0)],
        };

    /// <summary>
    /// Rolls a random arrival weight class for the <c>randomizeWeightCategory</c> option, bounded to the
    /// <see cref="BaseWeightBand"/> around the generator's configured base weight and then intersected with
    /// the classes that actually have a type pool for the generator's fixed <paramref name="engine"/> (per
    /// <see cref="AircraftGenerator.GetTypesForCombo"/>). The intersection is what keeps a randomized
    /// turboprop/piston generator from rolling a class that would only degrade through the fallback chain to
    /// a nonsensical type — no Large/Heavy turboprop exists, and piston is Small singles or Large twins with
    /// nothing between. A Small-class roll resolves to general-aviation types (bizjets, light pistons, light
    /// turboprops) that no scheduled airline operates, so those spawns come up under N-number callsigns.
    /// </summary>
    public static WeightClass RandomWeightForEngine(EngineKind engine, WeightClass baseWeight, Random rng)
    {
        var band = BaseWeightBand(baseWeight).Where(e => AircraftGenerator.GetTypesForCombo(e.Weight, engine) is not null).ToList();

        if (band.Count == 0)
        {
            // Misconfigured base/engine combo (e.g. a Heavy turboprop generator, whose whole band has no
            // pool). Fall back to a uniform roll over the classes the engine does have a pool for, so the
            // spawn still resolves to a real type instead of degrading through the fallback chain.
            band = Enum.GetValues<WeightClass>()
                .Where(w => AircraftGenerator.GetTypesForCombo(w, engine) is not null)
                .Select(w => (Weight: w, Share: 1.0))
                .ToList();
        }

        var pick = rng.NextDouble() * band.Sum(e => e.Share);
        foreach (var entry in band)
        {
            pick -= entry.Share;
            if (pick <= 0)
            {
                return entry.Weight;
            }
        }
        return band[^1].Weight;
    }

    private static EngineKind ResolveEngine(string engineType)
    {
        return engineType switch
        {
            "Piston" => EngineKind.Piston,
            "Turboprop" => EngineKind.Turboprop,
            _ => EngineKind.Jet,
        };
    }

    /// <summary>
    /// Records a generated arrival's spawn for replay AFTER the server has applied its autotrack
    /// configuration, so the recorded snapshot carries the owner / scratchpad / temporary altitude.
    /// Generated arrivals without an autotrack configuration are instead recorded eagerly at spawn
    /// (see <see cref="SpawnGeneratedArrival"/>); this method is only for the autotrack-bearing path.
    /// </summary>
    public void RecordGeneratedSpawn(AircraftState state) => RecordGeneratedAircraftSpawn(state);
}
