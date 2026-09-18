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
using Yaat.Sim.Phases.Approach;
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
        SimScenarioState scenario = Scenario!;
        for (int i = scenario.DelayedQueue.Count - 1; i >= 0; i--)
        {
            DelayedSpawn entry = scenario.DelayedQueue[i];

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

                foreach (string msg in entry.Aircraft.AutoTrackMessages)
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

    private void ProcessGenerators(List<AircraftState> spawned)
    {
        SimScenarioState scenario = Scenario!;
        if (!RunProfile.RunsGenerators)
        {
            return;
        }

        // The solo arrival-rate slider scales arrival streams only; overflights are not an arrival source.
        int ratePercent = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);
        if (ratePercent > 0)
        {
            foreach (GeneratorState gen in scenario.Generators)
            {
                if (IsGeneratorActive(gen))
                {
                    TrySpawnArrival(gen, ratePercent, spawned);
                }
            }

            foreach (VfrArrivalGeneratorState gen in scenario.VfrArrivalGenerators)
            {
                if (IsGeneratorActive(gen))
                {
                    TrySpawnVfrArrival(gen, ratePercent, spawned);
                }
            }
        }

        foreach (OverflightGeneratorState gen in scenario.OverflightGenerators)
        {
            if (IsGeneratorActive(gen))
            {
                TrySpawnOverflight(gen, spawned);
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
        SimScenarioState scenario = Scenario!;
        IGeneratorConfig config = state.ConfigBase;
        bool isActive = GeneratorActivation.IsActive(config, scenario.ElapsedSeconds);

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
        double firstSpawnSeconds = (double)config.StartTimeOffset;
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
    private void TrySpawnArrival(GeneratorState gen, int ratePercent, List<AircraftState> spawned)
    {
        SimScenarioState scenario = Scenario!;
        if (scenario.ElapsedSeconds < gen.NextSpawnSeconds)
        {
            return;
        }

        EngineKind engine = ResolveEngine(gen.Config.EngineType);
        WeightClass weight = ResolveWeight(gen.Config, engine, World.Rng);
        List<(double DistanceNm, AircraftState Aircraft)> corridor = CorridorAircraft(gen);
        (double DistanceNm, AircraftState Aircraft)? rearmost = RearmostInbound(corridor);

        double gap;
        double placement;
        if (rearmost is null)
        {
            gap = 0;
            placement = gen.Config.InitialDistance;
        }
        else
        {
            (double leaderDistance, AircraftState? leader) = rearmost.Value;
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

        if (!IsArrivalSpawnClearOfTraffic(gen, placement, engine, corridor))
        {
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        AircraftState? state = SpawnGeneratedArrival(gen, placement, weight, engine);
        if (state is null)
        {
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        AutoTrackGeneratedSpawn(state, gen.Config.AutoTrackConfiguration);
        spawned.Add(state);
        _generatorSpawnLog.Add(
            new GeneratorSpawnRecord(gen.Config.Id, state.Callsign, scenario.ElapsedSeconds, placement, rearmost?.DistanceNm, gap)
        );
        gen.NextSpawnSeconds = scenario.ElapsedSeconds + EffectiveSpawnIntervalSeconds(gen, ratePercent);
    }

    /// <summary>
    /// False when the point an arrival would spawn at, <paramref name="distanceNm"/> out on the generator's final, is
    /// inside standard separation (<see cref="VfrSpawnSiting.IsClearOfTraffic"/>: 3 nm / 1,000 ft, the check the VFR and
    /// overflight generators use) of any airborne aircraft other than the corridor arrivals placement already spaced
    /// behind (<paramref name="corridor"/>, the <see cref="CorridorAircraft"/> set placement used, matched by reference).
    /// Everything else counts: an outbound departure, a VFR passing through the final, an arrival holding near the FAF or
    /// IAF, one joining this final from outside the corridor band (a wide intercept, a procedure turn), and another
    /// runway's approach crossing this final. The corridor arrivals are at least the binding gap (≥ 3 nm) ahead along the
    /// course, so also at least that far in straight-line distance; checking them could only hold the spawn through a
    /// floating-point tie. An arrival on a parallel final about 2–3 nm away can hold the spawn for a small stagger, which
    /// is acceptable. The check is a snapshot of where traffic is now, with no look-ahead — an accepted limitation. The
    /// spawn point and altitude are where <see cref="AircraftInitializer.InitializeOnFinal"/> will put it; the type is not
    /// chosen yet, but every type the generator draws for <paramref name="engine"/> categorizes as that engine's category.
    /// </summary>
    private bool IsArrivalSpawnClearOfTraffic(
        GeneratorState gen,
        double distanceNm,
        EngineKind engine,
        List<(double DistanceNm, AircraftState Aircraft)> corridor
    )
    {
        (LatLon position, double altitudeFt) = AircraftInitializer.FinalApproachPoint(gen.Runway, AircraftGenerator.CategoryFor(engine), distanceNm);
        foreach (AircraftState other in World.GetSnapshot())
        {
            if (corridor.Exists(e => ReferenceEquals(e.Aircraft, other)) || VfrSpawnSiting.IsClearOfTraffic(position, altitudeFt, [other]))
            {
                continue;
            }

            _logger.LogDebug(
                "Generator '{Id}' holds its spawn at {Dist:F1}nm: {Callsign} is {Lateral:F2}nm / {Vertical:F0}ft from the spawn point",
                gen.Config.Id,
                distanceNm,
                other.Callsign,
                GeoMath.DistanceNm(position.Lat, position.Lon, other.Position.Lat, other.Position.Lon),
                Math.Abs(other.Altitude - altitudeFt)
            );
            return false;
        }
        return true;
    }

    private double EffectiveSpawnIntervalSeconds(GeneratorState gen, int ratePercent) =>
        JitteredInterval(gen.Config, ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(gen.Config.IntervalTime, ratePercent));

    /// <summary>Applies the generator's ±25% interval jitter, never dropping below the retry backoff.</summary>
    private double JitteredInterval(IGeneratorConfig config, double intervalSeconds)
    {
        if (config.RandomizeInterval)
        {
            double jitter = intervalSeconds * 0.25;
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
        double wakeFloor = WakeTurbulenceData.OnApproachWakeSeparationNm(
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
    /// Airborne arrivals to the generator's runway inside its final-approach corridor (any generator's arrivals
    /// plus manual adds), each with its along-final distance-to-threshold (nm). Used so concurrent streams to
    /// the same runway don't overlap and the cold-start seed doesn't double up on existing traffic.
    ///
    /// <para>Position alone does not make an arrival: the corridor is within 2 nm of the extended centreline
    /// and out to <c>MaxDistance</c> plus a margin, and an aircraft there counts only when it is on final for
    /// the runway (<see cref="ApproachCommandHandler.IsOnFinal"/>) or is inbound to land
    /// (<see cref="ApproachCommandHandler.IsInboundToLand"/>) on this runway — its assigned runway, else its
    /// active approach's runway — whatever its track, which keeps a wide intercept or a base leg inside the
    /// band. A departure turned out along the extended centreline, or climbing out with its departure runway
    /// assigned, is not an arrival and is left out.</para>
    ///
    /// <para><see cref="ApproachCommandHandler.IsOnFinal"/>'s phase shortcut (a final, landing or low-approach phase counts
    /// whatever the runway) also counts an aircraft on final to a close parallel whose final lies inside the band. That is
    /// harmless — it can only add spacing at spawn — and matches the 7110.65 §5-5-4 note that parallel runways less than
    /// 2,500 feet apart are considered a single runway.</para>
    /// </summary>
    private List<(double DistanceNm, AircraftState Aircraft)> CorridorAircraft(GeneratorState gen)
    {
        RunwayInfo rwy = gen.Runway;
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var outbound = new TrueHeading((rwy.TrueHeading.Degrees + 180.0) % 360.0);
        double maxAlong = gen.Config.MaxDistance + FinalCorridorMarginNm;

        var result = new List<(double DistanceNm, AircraftState Aircraft)>();
        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (ac.IsOnGround)
            {
                continue;
            }
            double cross = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, outbound));
            if (cross > FinalCorridorHalfWidthNm)
            {
                continue;
            }
            double along = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, outbound);
            if (along <= 0 || along > maxAlong)
            {
                continue;
            }
            if (!IsArrivalToRunway(ac, rwy))
            {
                continue;
            }
            result.Add((along, ac));
        }
        return result;
    }

    /// <summary>
    /// True when <paramref name="aircraft"/> is on final for <paramref name="runway"/>, or intends to land on it
    /// (inbound to land, with this runway as its assigned runway or its active approach's runway). Intent is
    /// required on top of the runway match because a departure also carries its departure runway as
    /// <c>AssignedRunway</c>. The on-final test's phase shortcut does not check the runway, so an aircraft on final to a
    /// close parallel inside the corridor band counts too — harmless, since it only adds spacing at spawn, and in line with
    /// the 7110.65 §5-5-4 note that parallel runways less than 2,500 feet apart are considered a single runway.
    /// </summary>
    private static bool IsArrivalToRunway(AircraftState aircraft, RunwayInfo runway)
    {
        if (ApproachCommandHandler.IsOnFinal(aircraft, runway))
        {
            return true;
        }

        string? landingRunway = aircraft.Phases?.AssignedRunway?.Designator ?? aircraft.Phases?.ActiveApproach?.RunwayId;
        return ApproachCommandHandler.IsInboundToLand(aircraft)
            && string.Equals(landingRunway, runway.Designator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rearmost (greatest distance-to-threshold) arrival in <paramref name="corridor"/>, the runway's final-approach
    /// corridor as <see cref="CorridorAircraft"/> built it — or null when the corridor is empty.
    /// </summary>
    private static (double DistanceNm, AircraftState Aircraft)? RearmostInbound(List<(double DistanceNm, AircraftState Aircraft)> corridor)
    {
        (double DistanceNm, AircraftState Aircraft)? rearmost = null;
        foreach ((double DistanceNm, AircraftState Aircraft) entry in corridor)
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
    /// aircraft immediately ahead and stamps a <see cref="ControlTargets.SpeedCeiling"/> from
    /// <see cref="ArrivalSpacingManager.InTrailCeilingKts"/>: the follower equalizes to its leader and
    /// holds the spawn spacing (<c>SpacingGapNm</c>) down the final instead of overrunning it (the
    /// QXE831/SWA8154 compression), and while it is farther back than that target behind a leader on final
    /// it may close faster, up to its scheduled speed, as long as the gap never drops below the target during
    /// the leader's remaining run (the leader's Vref bounds that run; see <see cref="SpaceFollower"/>). The ceiling only
    /// ever lowers the phase's speed target (<see cref="FlightPhysics.UpdateSpeed"/> applies it
    /// as a continuous <c>min</c>), floors at the follower's Vref, and collapses to Vref by the
    /// threshold, so it never blocks the landing deceleration. Uses no RNG, so replay/rewind stay
    /// deterministic; it runs during replay too (old recordings have <c>IsGeneratorArrival</c>
    /// false and are unaffected).
    ///
    /// <para>The simulated TRACON spaces an arrival only while it owns it, and it stops in one of two ways, latched
    /// one-way on <see cref="AircraftApproachState.AutoSpacingReleased"/>. The student taking the track is a
    /// <em>hand-over</em>: the ceiling is left exactly where it stands, because the receiving controller inherits the
    /// restrictions the aircraft is flying (§5-4-5.h.3, §5-4-6.c) and it then lapses like any other assigned speed —
    /// the student's own speed command, or <see cref="FlightPhysics"/>'s auto-cancel at the 5 nm / FAF window
    /// (§5-7-1.d, AIM 4-4-12.a.7). The controller assigning a speed or deleting the speed restrictions is a
    /// <em>release</em>: that assignment owns the speed, so the managed ceiling comes off
    /// (<see cref="ReleaseManagedSpeedCeiling"/>). Either way the manager never writes the ceiling again.</para>
    ///
    /// <para>While it still owns an arrival, the simulated TRACON also ends its own speed adjustment once it is no
    /// longer needed — "resume normal speed" (§5-7-1, §5-7-4.a, AIM 4-4-12.f.1). A ceiling only ever lowers the speed, and
    /// <see cref="FinalApproachPhase"/> writes no target between spawn and its own deceleration stages, so an arrival
    /// the ceiling slowed would otherwise keep the slowed speed after the ceiling rose or came off (FDX7106 flew the
    /// last ~25 nm of the OAK 30 final at 164 kt once its leader had landed). <see cref="RestoreManagedSpeed"/> gives
    /// the stream lead its scheduled speed back, and a follower its current ceiling, outside
    /// <see cref="SpeedRestoreGateNm"/> and never while a handoff to the student is in progress: from the moment one is
    /// initiated (§5-4-5.b) the lead's standing ceiling is held for the student to inherit (§5-4-6.c) rather than
    /// released.</para>
    ///
    /// <para>The simulated approach controller only exists while the student works a position below approach
    /// (<see cref="SimScenarioState.HasSimulatedApproachController"/>): a student on APP or CTR does this spacing
    /// themselves. With that gate closed no ceiling is stamped and nothing is restored, and every ceiling the manager
    /// still holds — a generator arrival in <see cref="FinalApproachPhase"/> not yet let go of — is handed back
    /// (<see cref="ReleaseManagedSpeedCeiling"/>). The release runs on every tick the gate is closed, not only the first,
    /// which is harmless because it is idempotent. <see cref="ApplySameRunwayArrivalProtection"/> applies the same gate. The gate closing is not a release: <see cref="AircraftApproachState.AutoSpacingReleased"/> is not latched,
    /// so the manager picks the stream up again if the gate reopens.</para>
    /// </summary>
    private void ApplyArrivalSpacing()
    {
        SimScenarioState? scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        if (!scenario.HasSimulatedApproachController)
        {
            ReleaseAllManagedSpacingCeilings();
            return;
        }

        foreach (GeneratorState gen in scenario.Generators)
        {
            ManageGeneratorStream(gen, scenario);
        }
    }

    /// <summary>
    /// One tick of <see cref="ApplyArrivalSpacing"/> for one generator runway: sorts the runway's final corridor
    /// (<see cref="CorridorAircraft"/>, arrivals assigned this runway) closest-first and, for each generator arrival the
    /// manager still holds (<see cref="HoldsSpacingAuthority"/>), manages the front of the stream
    /// (<see cref="ManageStreamLead"/>) or spaces a follower behind the aircraft immediately ahead of it
    /// (<see cref="SpaceFollower"/>).
    /// </summary>
    private void ManageGeneratorStream(GeneratorState gen, SimScenarioState scenario)
    {
        var stream = CorridorAircraft(gen)
            .Where(e => string.Equals(e.Aircraft.Phases?.AssignedRunway?.Designator, gen.Runway.Designator, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.DistanceNm)
            .ToList();

        for (int i = 0; i < stream.Count; i++)
        {
            (double distanceNm, AircraftState? aircraft) = stream[i];
            if (!HoldsSpacingAuthority(aircraft, scenario))
            {
                continue;
            }

            if (i == 0)
            {
                ManageStreamLead(aircraft, distanceNm, scenario);
            }
            else
            {
                SpaceFollower(gen, stream[i - 1], stream[i], scenario);
            }
        }
    }

    /// <summary>
    /// True when the simulated approach controller still holds <paramref name="aircraft"/>'s speed this tick: a generator
    /// arrival in <see cref="FinalApproachPhase"/> it has not let go of. Letting go is one-way, latched here on
    /// <see cref="AircraftApproachState.AutoSpacingReleased"/>: the student taking the track (a hand-over, the ceiling left
    /// standing) or the controller touching the speed (a release, the managed ceiling removed).
    /// </summary>
    private static bool HoldsSpacingAuthority(AircraftState aircraft, SimScenarioState scenario)
    {
        // Scope: only generator arrivals actively on final are managed.
        if (!aircraft.IsGeneratorArrival || (aircraft.Phases?.CurrentPhase is not FinalApproachPhase))
        {
            return false;
        }

        // Already let go of (one-way latch): whatever ceiling stands is somebody else's now — the speed the
        // student inherited, or one the controller's own command left — so nothing is written here.
        if (aircraft.Approach.AutoSpacingReleased)
        {
            return false;
        }

        // The student taking the track is a handoff, not a release: the receiving controller inherits the
        // restrictions the aircraft is flying (§5-4-5.h.3, §5-4-6.c), so the manager lets go of the speed
        // without giving it back and the arrival does not accelerate on the tick the handoff is accepted.
        // The ceiling left standing is then an ordinary assigned speed with nobody re-stamping it: it lapses
        // the way any other does — the student's own speed command, or
        // FlightPhysics.AutoCancelSpeedAtFinal at the 5 nm / FAF window (§5-7-1.d, AIM 4-4-12.a.7).
        if (StudentOwnsTrack(aircraft, scenario))
        {
            aircraft.Approach.AutoSpacingReleased = true;
            return false;
        }

        // The controller touching this aircraft's speed is a release: the assignment (or the deletion of its
        // restrictions) is now the sole speed authority, so the managed ceiling comes off with it.
        if (aircraft.Targets.HasExplicitSpeedCommand || aircraft.Procedure.SpeedRestrictionsDeleted)
        {
            aircraft.Approach.AutoSpacingReleased = true;
            ReleaseManagedSpeedCeiling(aircraft);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Spaces a generator-arrival follower behind the aircraft immediately ahead of it on the same final. The target gap
    /// is <c>max(IntervalDistance, 3 nm radar floor, wake minimum)</c> for the pair; the follower's
    /// <see cref="ControlTargets.SpeedCeiling"/> is stamped toward it, and the follower is given that ceiling back when it
    /// is slower (<see cref="RestoreManagedSpeed"/>). The time-based allowance of
    /// <see cref="ArrivalSpacingManager.InTrailCeilingKts"/> applies only behind a leader on final
    /// (<see cref="ApproachCommandHandler.IsOnFinal"/>): its bound assumes the leader's along-course closure on the
    /// threshold (ground speed × cos of its track offset from the final course) is no less than its Vref-bounded speed,
    /// which a leader in the stream only through its landing intent — on a base leg, or a pattern aircraft on a downwind
    /// flying away — does not. Behind such a leader the ceiling is the proportional
    /// <see cref="ArrivalSpacingManager.SpacingCeilingKts"/> alone.
    /// </summary>
    private void SpaceFollower(
        GeneratorState gen,
        (double DistanceNm, AircraftState Aircraft) leaderEntry,
        (double DistanceNm, AircraftState Aircraft) followerEntry,
        SimScenarioState scenario
    )
    {
        (double leaderDist, AircraftState? leader) = leaderEntry;
        (double followerDist, AircraftState? follower) = followerEntry;
        (AircraftCategory followerCategory, double vref, double scheduled) = ManagedSpeeds(follower, followerDist);
        AircraftCategory leaderCategory = AircraftCategorization.Categorize(leader.AircraftType);
        double wakeFloor = WakeTurbulenceData.OnApproachWakeSeparationNm(
            leader.AircraftType,
            leaderCategory,
            follower.AircraftType,
            followerCategory
        );
        double target = Math.Max(gen.Config.IntervalDistance, Math.Max(TerminalRadarFloorNm, wakeFloor));

        // The allowance's time bound needs the leader's closure on the threshold along the final course, not its ground
        // speed: a leader on final by its track alone may fly up to 45° off the course. An intercepting leader's offset only
        // shrinks as it turns in, so this cosine stays a lower bound for the rest of its run; for an established leader it is ~1.
        double leaderOffsetRad = leader.TrueTrack.AbsAngleTo(gen.Runway.TrueHeading) * Math.PI / 180.0;
        double leaderAlongCourseGsKts = leader.GroundSpeed * Math.Max(0.0, Math.Cos(leaderOffsetRad));

        double ceiling = ApproachCommandHandler.IsOnFinal(leader, gen.Runway)
            ? ArrivalSpacingManager.InTrailCeilingKts(
                new InTrailPair
                {
                    LeaderIasKts = leader.IndicatedAirspeed,
                    LeaderGsKts = leaderAlongCourseGsKts,
                    LeaderVrefKts = AircraftPerformance.ApproachSpeed(leader.AircraftType, leaderCategory),
                    LeaderDistanceNm = leaderDist,
                    FollowerDistanceNm = followerDist,
                    FollowerIasKts = follower.IndicatedAirspeed,
                    FollowerGsKts = follower.GroundSpeed,
                    FollowerVrefKts = vref,
                    FollowerScheduledKts = scheduled,
                    TargetNm = target,
                }
            )
            : ArrivalSpacingManager.SpacingCeilingKts(leader.IndicatedAirspeed, followerDist - leaderDist, target, vref, scheduled);
        follower.Targets.SpeedCeiling = ceiling;
        RestoreManagedSpeed(follower, followerDist, ceiling, isStreamLead: false, scenario);
    }

    /// <summary>
    /// The category, Vref and scheduled final-approach speed at <paramref name="distanceNm"/> from the threshold
    /// (<see cref="ArrivalSpacingManager.ScheduledFinalSpeedKts"/>) of an arrival the manager holds.
    /// </summary>
    private static (AircraftCategory Category, double VrefKts, double ScheduledKts) ManagedSpeeds(AircraftState aircraft, double distanceNm)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double vref = AircraftPerformance.ApproachSpeed(aircraft.AircraftType, category);
        double scheduled = ArrivalSpacingManager.ScheduledFinalSpeedKts(aircraft.AircraftType, category, vref, aircraft.Callsign, distanceNm);
        return (category, vref, scheduled);
    }

    /// <summary>
    /// The front of a generator stream has no one to follow. While a handoff of it to the student is in progress its
    /// standing ceiling stays for the receiving controller to inherit (§5-4-6.c) and nothing is restored — the
    /// transferring controller changes no speed once a handoff is initiated (§5-4-5.b). Otherwise the managed ceiling
    /// comes off and the aircraft is given its scheduled profile speed back.
    /// </summary>
    private void ManageStreamLead(AircraftState lead, double distanceNm, SimScenarioState scenario)
    {
        if (HandoffToStudentInProgress(lead, scenario))
        {
            return;
        }

        ReleaseManagedSpeedCeiling(lead);
        (AircraftCategory _, double _, double scheduled) = ManagedSpeeds(lead, distanceNm);
        RestoreManagedSpeed(lead, distanceNm, scheduled, isStreamLead: true, scenario);
    }

    /// <summary>
    /// Gives a generator arrival the simulated approach controller still owns back the speed it wants it flying
    /// (<paramref name="desiredKts"/>: the stream lead's scheduled speed, or a follower's in-trail ceiling), by writing
    /// <see cref="ControlTargets.TargetSpeed"/>. <see cref="FinalApproachPhase"/> writes no speed target between spawn
    /// and its own deceleration stages and <see cref="FlightPhysics"/> nulls a reached target, so an arrival slowed by
    /// the ceiling would otherwise keep the slowed speed after the ceiling rises or comes off. Issued only when no
    /// handoff to the student is in progress (§5-4-5.b), the same-runway protection pass does not own the ceiling
    /// (<see cref="AircraftApproachState.SameRunwayProtectionCeilingKts"/>), nothing else is driving the speed, the
    /// aircraft is more than <see cref="SpeedRestoreDeadbandKts"/> below the wanted speed, and it is still outside
    /// <see cref="SpeedRestoreGateNm"/>. Each restore is logged at debug level.
    /// </summary>
    private void RestoreManagedSpeed(AircraftState aircraft, double distanceNm, double desiredKts, bool isStreamLead, SimScenarioState scenario)
    {
        if (HandoffToStudentInProgress(aircraft, scenario) || (aircraft.Approach.SameRunwayProtectionCeilingKts is not null))
        {
            return;
        }

        if ((aircraft.Targets.TargetSpeed is not null) || (aircraft.IndicatedAirspeed >= desiredKts - SpeedRestoreDeadbandKts))
        {
            return;
        }

        if (distanceNm < SpeedRestoreGateNm(AircraftCategorization.Categorize(aircraft.AircraftType), aircraft.Callsign))
        {
            return;
        }

        aircraft.Targets.TargetSpeed = desiredKts;
        _logger.LogDebug(
            "Arrival spacing restores {Callsign} ({Role}) from {Ias:F0} kt to {Target:F0} kt at {Dist:F1}nm",
            aircraft.Callsign,
            isStreamLead ? "stream lead" : "follower",
            aircraft.IndicatedAirspeed,
            desiredKts,
            distanceNm
        );
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
    /// Hands back every ceiling <see cref="ApplyArrivalSpacing"/> still manages — each generator arrival in
    /// <see cref="FinalApproachPhase"/> it has not let go of — without latching
    /// <see cref="AircraftApproachState.AutoSpacingReleased"/>, for the ticks the simulated approach controller does not
    /// exist.
    /// </summary>
    private void ReleaseAllManagedSpacingCeilings()
    {
        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            if (aircraft.IsGeneratorArrival && (aircraft.Phases?.CurrentPhase is FinalApproachPhase) && !aircraft.Approach.AutoSpacingReleased)
            {
                ReleaseManagedSpeedCeiling(aircraft);
            }
        }
    }

    /// <summary>
    /// How far (kt) below the speed the simulated approach controller wants an arrival may fly before a restore is
    /// issued. A pilot complying with a speed adjustment holds it within ±10 kt (AIM 4-4-12.c; 7110.65 §5-7-1.g
    /// NOTE 1), so a difference inside that band is not one a controller would correct.
    /// </summary>
    internal const double SpeedRestoreDeadbandKts = 10.0;

    /// <summary>
    /// Margin (nm) kept outside the first deceleration stage <see cref="FinalApproachPhase"/> would start on its own,
    /// inside which no restore is issued, so an arrival is never sped up only to be slowed again moments later. A
    /// judgement call under §5-7-1.a.2(b) (speed adjustments are not achieved instantaneously) and §5-7-1.a.3(c) (allow
    /// increased time and distance for a speed adjustment at greater speed and in a clean configuration).
    /// </summary>
    internal const double RestoreGateMarginNm = 5.0;

    /// <summary>
    /// Distance from the threshold (nm) inside which the simulated approach controller no longer restores an arrival's
    /// speed: the latest point the phase could start its first deceleration stage, plus <see cref="RestoreGateMarginNm"/>.
    /// That stage is the clean → approach-flap bleed, which starts no farther out than the aircraft's approach-flap
    /// reach gate plus <see cref="FinalApproachPhase.ApproachFlapTriggerHeadroomNm"/>. A category with no approach-flap
    /// stage (a piston) starts with the configuration bleed, bounded here by <see cref="FinalApproachPhase.MaxConfigTriggerNm"/>:
    /// the phase's own cap slides outward with a larger FAS reach gate, but a piston's clean-to-configuration bleed is
    /// under ~20 kt and starts no farther out than about 7.3 nm, so the gate still keeps more than 5 nm of margin.
    /// </summary>
    internal static double SpeedRestoreGateNm(AircraftCategory category, string callsign)
    {
        double firstStageTriggerCapNm = FinalApproachSpeedSchedule.ApproachFlapReachGateNm(category, callsign) is { } flapGate
            ? flapGate + FinalApproachPhase.ApproachFlapTriggerHeadroomNm
            : FinalApproachPhase.MaxConfigTriggerNm;
        return firstStageTriggerCapNm + RestoreGateMarginNm;
    }

    /// <summary>
    /// Distance to the landing threshold (nm) inside which §5-7-1.b.4 forbids a speed adjustment — "within 5 flying
    /// miles of the runway", the outer of the two cutoffs there and the same figure
    /// <see cref="FlightPhysics"/>'s auto-cancel uses.
    /// </summary>
    private const double FinalSpeedAdjustmentCutoffNm = 5.0;

    /// <summary>
    /// Same-runway arrival protection — the simulated TRACON for arrivals the arrival generator never touches.
    /// <see cref="ApplyArrivalSpacing"/> only manages generator arrivals inside a generator's own corridor, so a
    /// scenario-scripted arrival stream is delivered with no in-trail management at all and the trailing arrival
    /// can reach the threshold before the leading one has cleared the runway — an illegal delivery under §3-10-3.a.1
    /// that the occupied-runway go-around then has to catch. Each tick this pass walks every runway's arrivals
    /// front-to-back, predicts the interval between consecutive threshold crossings, and where the leader will still
    /// be on the pavement stamps a <see cref="ControlTargets.SpeedCeiling"/> on the follower — §5-7-1.a.3.a, reduce
    /// the trailing aircraft — until the interval opens. The ceiling only ever lowers the phase's speed target and
    /// floors at the follower's Vref (§5-7-3.f authorises going below the §5-7-3.c floors for spacing). It is
    /// released — restoring whatever ceiling it displaced, which on a scripted STAR arrival may be a published
    /// crossing-speed restriction — once the interval has opened past the required one by
    /// <see cref="SameRunwayArrivalProtection.ReleaseHysteresisSeconds"/>, or as soon as the follower reaches the
    /// §5-7-1.b.4 window or stops being eligible. It is not a command:
    /// <see cref="ControlTargets.TargetSpeed"/> and <see cref="ControlTargets.HasExplicitSpeedCommand"/> are never
    /// touched, matching the precedent <see cref="ApplyArrivalSpacing"/> set. No RNG, so replay and rewind stay
    /// deterministic. When the reduction cannot open the interval in time the go-around still fires — the safety
    /// net is unchanged.
    ///
    /// <para>The whole issuing side is gated on
    /// <see cref="SimScenarioState.AutoArrivalSpacingOnOccupiedRunway"/> and on
    /// <see cref="SimScenarioState.HasSimulatedApproachController"/> — the controller this pass speaks for only exists
    /// while the student works a position below approach, and a student on APP or CTR does this sequencing themselves.
    /// With either gate closed the arrival streams are not walked at all, and the release loop — which runs either
    /// way — hands back every ceiling and latched instruction the pass still owns on that same tick.</para>
    /// </summary>
    private void ApplySameRunwayArrivalProtection()
    {
        SimScenarioState? scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        var protectedThisTick = new HashSet<string>(StringComparer.Ordinal);

        // The stream walk is the whole of the issuing side, so the setting — and the existence of the simulated
        // approach controller doing the spacing, which a student on APP or CTR is themselves — gates it here; the
        // release loop below stays unconditional, which is what hands every owned speed back on the tick the setting
        // is switched off or the student takes a position that owns the stream.
        if (scenario.AutoArrivalSpacingOnOccupiedRunway && scenario.HasSimulatedApproachController)
        {
            foreach (List<(double DistanceNm, AircraftState Aircraft, RunwayInfo Runway, bool PreClearance)> stream in BuildRunwayArrivalStreams())
            {
                for (int i = 0; i < stream.Count; i++)
                {
                    (double followerDistance, AircraftState? follower, RunwayInfo? runway, bool preClearance) = stream[i];

                    // The student taking the track is a handoff, not a release: the receiving controller inherits the
                    // restrictions the aircraft is flying (§5-4-5.h.3, §5-4-6.c), so the pass lets go of the speed
                    // without giving it back. Ahead of the eligibility test because that test would only make the
                    // follower ineligible, and an ineligible follower falls to the release loop below.
                    if (StudentOwnsTrack(follower, scenario) && PassOwnsProtection(follower))
                    {
                        HandOverSameRunwayProtection(follower);
                        continue;
                    }

                    if (!IsProtectionEligible(follower, runway, scenario, preClearance))
                    {
                        continue;
                    }

                    // Index 0 is the aircraft at the front of the stream — nobody ahead of it to be protected from,
                    // so the only thing to do for it is keep re-applying what it was already told: a tower
                    // instruction, or an approach reduction it is still flying inside the tower boundary. Neither is
                    // withdrawn merely because the aircraft ahead has landed.
                    bool owned =
                        i == 0
                            ? HoldTowerFasInstruction(follower, runway)
                                || (
                                    SameRunwayArrivalProtection.IsInsideTowerSpeedAuthority(followerDistance)
                                    && HoldApproachReductionInsideTowerBoundary(follower)
                                )
                            : TryProtectFollower(follower, followerDistance, stream[i - 1].Aircraft, runway, scenario);
                    if (owned)
                    {
                        protectedThisTick.Add(follower.Callsign);
                    }
                }
            }
        }

        // Anything the pass owned but did not re-stamp this tick has either cleared its conflict, entered the
        // §5-7-1.b.4 window, or stopped being eligible: hand its speed back. A latched tower instruction is released
        // here too — it survives the conflict clearing and the §5-7-1.b.4 window, but not the aircraft leaving the
        // arrival stream (it landed or went around) or someone else taking its speed.
        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            if (PassOwnsProtection(aircraft) && !protectedThisTick.Contains(aircraft.Callsign))
            {
                ReleaseSameRunwayProtection(aircraft);
            }
        }
    }

    /// <summary>
    /// True while the same-runway protection pass owns this aircraft's speed — it has a ceiling stamped, a latched
    /// simulated-tower instruction, or both.
    /// </summary>
    private static bool PassOwnsProtection(AircraftState aircraft) =>
        (aircraft.Approach.SameRunwayProtectionCeilingKts is not null) || aircraft.Approach.SameRunwayProtectionFasInstructed;

    /// <summary>
    /// Stops owning the follower's speed without touching <see cref="ControlTargets.SpeedCeiling"/>: what the student
    /// controller inherits when they take the track. §5-4-5.h.3 and §5-4-6.c put the restrictions an aircraft is
    /// flying on the receiving controller's account rather than cancelling them at the boundary, so the reduction
    /// stands and the arrival does not accelerate on the tick the handoff is accepted. Unlike
    /// <see cref="ReleaseSameRunwayProtection"/> nothing is put back, because nothing is being taken away.
    ///
    /// <para>The ceiling left standing is then an ordinary assigned speed with nobody in the sim re-stamping it: it
    /// lapses the way any other does — the student's own speed command, or <c>FlightPhysics.AutoCancelSpeedAtFinal</c>
    /// at the 5 nm / FAF window (§5-7-1.d, AIM 4-4-12.a.7).</para>
    /// </summary>
    private static void HandOverSameRunwayProtection(AircraftState aircraft)
    {
        AircraftApproachState approach = aircraft.Approach;
        approach.SameRunwayProtectionCeilingKts = null;
        approach.SameRunwayProtectionDisplacedCeilingKts = null;
        approach.SameRunwayProtectionFasInstructed = false;
    }

    /// <summary>
    /// Hands back a <see cref="ControlTargets.SpeedCeiling"/> the same-runway protection pass owns, putting the
    /// ceiling it displaced back rather than clearing the field. Unlike a generator arrival — which spawns on final
    /// with its route cleared and so carries no other ceiling, the invariant
    /// <see cref="ReleaseManagedSpeedCeiling"/> relies on — a scenario-scripted arrival flies a STAR and may be
    /// carrying a published crossing-speed restriction it is required to comply with (§5-7-1.b NOTE; a controller
    /// removes one only with DELETE SPEED RESTRICTIONS, §5-7-2.e). <see cref="FlightPhysics"/> publishes that
    /// restriction on the single tick the fix is sequenced and never re-stamps it, so clearing the field outright
    /// would delete it for the rest of the flight. Restores only while the ceiling is still the exact value this
    /// pass stamped — anything that has lowered it since owns it now and is left alone, the compare-before-clear
    /// shape <see cref="Phases.Approach.ProcedureTurnPhase"/> uses. Also the one place a latched simulated-tower
    /// final-approach-speed instruction (<see cref="AircraftApproachState.SameRunwayProtectionFasInstructed"/>) is
    /// withdrawn. Clears nothing else when the pass is not engaged. The student controller taking the track is not
    /// this path: there the speed goes with the track (<see cref="HandOverSameRunwayProtection"/>).
    /// </summary>
    private static void ReleaseSameRunwayProtection(AircraftState aircraft)
    {
        AircraftApproachState approach = aircraft.Approach;
        approach.SameRunwayProtectionFasInstructed = false;
        if (approach.SameRunwayProtectionCeilingKts is not { } stamped)
        {
            return;
        }

        if (aircraft.Targets.SpeedCeiling is { } current && current == stamped)
        {
            aircraft.Targets.SpeedCeiling = approach.SameRunwayProtectionDisplacedCeilingKts;
        }

        approach.SameRunwayProtectionCeilingKts = null;
        approach.SameRunwayProtectionDisplacedCeilingKts = null;
    }

    /// <summary>
    /// Resolves the runway an aircraft is arriving on: its <c>AssignedRunway</c> when it has been cleared for an
    /// approach or to land, else — while it is airborne and still flying its route with the clearance waiting in the
    /// command queue — the runway its <see cref="AircraftApproachState.Expected"/> approach (or its filed
    /// <c>DestinationRunway</c>) serves. Null means "not an arrival": no hint, no navdata, or a hint the navdata
    /// cannot resolve. This runs inside the tick loop, so a miss is always a null and never an exception.
    ///
    /// <para><see cref="ApproachCommandHandler.ResolveApproach"/> consults both hint sources itself when it is given
    /// no approach id, so the hint here only decides <em>whether</em> to ask and keys the memo — a
    /// procedure-to-runway mapping is navdata, fixed for the session, and re-resolving it for every arrival on every
    /// tick would walk the approach catalog each time.</para>
    /// </summary>
    private RunwayInfo? ResolveArrivalRunway(AircraftState aircraft)
    {
        if (aircraft.Phases?.AssignedRunway is { } assigned)
        {
            return assigned;
        }

        if (aircraft.IsOnGround || NavigationDatabase.InstanceOrNull is null)
        {
            return null;
        }

        if ((aircraft.Approach.Expected ?? aircraft.Procedure.DestinationRunway) is not { } hint)
        {
            return null;
        }

        string airport = CommandDispatcher.ResolveAirport(aircraft);
        (string Airport, string ApproachId) key = (Airport: airport.Length > 0 ? airport : aircraft.AirportId, ApproachId: hint);
        if (_expectedArrivalRunways.TryGetValue(key, out RunwayInfo? memoized))
        {
            return memoized;
        }

        ApproachCommandHandler.ResolvedApproach resolved = ApproachCommandHandler.ResolveApproach(null, null, aircraft);
        RunwayInfo? runway = resolved.Success ? resolved.Runway : null;
        _expectedArrivalRunways[key] = runway;
        return runway;
    }

    /// <summary>
    /// The runway each <see cref="AircraftApproachState.Expected"/> approach serves, keyed by airport and hint — see
    /// <see cref="ResolveArrivalRunway"/>. Navdata, so it neither expires nor needs clearing on a rewind or restore.
    /// </summary>
    private readonly Dictionary<(string Airport, string ApproachId), RunwayInfo?> _expectedArrivalRunways = [];

    /// <summary>
    /// Every landing runway's arrival stream, grouped by airport and runway designator and ordered front-to-back by
    /// distance to the landing threshold, so a reduction cascades back through the aircraft behind. Membership is
    /// an aircraft that has landed and is still on the pavement, one airborne and inbound to land, or one airborne
    /// and known to be inbound to the runway by its expected approach — a departure carries an <c>AssignedRunway</c>
    /// too and is not part of the arrival stream. The distance is measured along the arrival runway's own course
    /// (<see cref="RunwayOccupancy.DistanceToAssignedThresholdNm"/>), never the aircraft's track: an arrival still on
    /// downwind or base is 90°-plus off the final course, and a track-derived datum would measure it to the
    /// reciprocal threshold and sort it to the front of the stream.
    /// </summary>
    private List<List<(double DistanceNm, AircraftState Aircraft, RunwayInfo Runway, bool PreClearance)>> BuildRunwayArrivalStreams()
    {
        var groups =
            new Dictionary<
                (string Airport, string Designator),
                List<(double DistanceNm, AircraftState Aircraft, RunwayInfo Runway, bool PreClearance)>
            >();
        foreach (AircraftState aircraft in World.GetSnapshot())
        {
            if (ResolveArrivalRunway(aircraft) is not { } runway)
            {
                continue;
            }

            bool preClearance = aircraft.Phases?.AssignedRunway is null;
            if (!IsRunwayArrival(aircraft, runway, preClearance))
            {
                continue;
            }

            double distance = RunwayOccupancy.DistanceToAssignedThresholdNm(aircraft, runway, World.GroundLayout);

            // An airborne aircraft whose along-course position puts it behind the landing threshold is not part of
            // this runway's arrival stream — it is overflying, going around, or set up for the reciprocal end — and
            // admitting it on a negative distance would make it the leader everyone behind is spaced against. One
            // that has touched down keeps its place: its rollout is the occupancy this pass exists to protect.
            if (!double.IsFinite(distance) || ((!aircraft.IsOnGround) && (distance < 0.0)))
            {
                continue;
            }

            (string AirportId, string Designator) key = (runway.AirportId, runway.Designator);
            if (!groups.TryGetValue(key, out List<(double DistanceNm, AircraftState Aircraft, RunwayInfo Runway, bool PreClearance)>? stream))
            {
                stream = [];
                groups[key] = stream;
            }
            stream.Add((distance, aircraft, runway, preClearance));
        }

        // Callsign breaks a distance tie so the ordering — and therefore who follows whom — is reproducible.
        return
        [
            .. groups
                .OrderBy(g => g.Key.Airport, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Designator, StringComparer.Ordinal)
                .Select(g => g.Value.OrderBy(e => e.DistanceNm).ThenBy(e => e.Aircraft.Callsign, StringComparer.Ordinal).ToList()),
        ];
    }

    /// <summary>
    /// Part of a runway's arrival stream. With a clearance in hand (<paramref name="preClearance"/> false) that is
    /// being down and still on the pavement (rolling out or turning off), or airborne with a landing or approach
    /// clearance. Before the clearance it is the narrower case <see cref="ResolveArrivalRunway"/> admits: airborne,
    /// no runway and no phase of its own — flying a route or a vector, not holding, in the pattern or going around —
    /// established on the landing course, and inside <see cref="SameRunwayArrivalProtection.PreClearanceRangeNm"/> of
    /// the threshold but not past it. "On final" there has to be the track test, because there is no approach phase
    /// to read it off.
    /// </summary>
    private bool IsRunwayArrival(AircraftState aircraft, RunwayInfo runway, bool preClearance)
    {
        if (!preClearance)
        {
            return (aircraft.Phases?.CurrentPhase is LandingPhase or RunwayExitPhase)
                || ((!aircraft.IsOnGround) && ApproachCommandHandler.IsInboundToLand(aircraft));
        }

        if (aircraft.IsOnGround || (aircraft.Phases?.CurrentPhase is not null) || !ApproachCommandHandler.IsOnFinal(aircraft, runway))
        {
            return false;
        }

        double distance = RunwayOccupancy.DistanceToAssignedThresholdNm(aircraft, runway, World.GroundLayout);
        return distance is > 0.0 and <= SameRunwayArrivalProtection.PreClearanceRangeNm;
    }

    /// <summary>
    /// True when the simulated TRACON may still slow this follower: it is a simulated aircraft, it is being vectored
    /// or flown down an approach — or, before its approach clearance, flying a route toward the runway its expected
    /// approach serves — it is part of the runway's arrival stream, it has not reached the §5-7-1.b.4 no-adjustment
    /// window, and nobody else owns its speed. Admitting the pre-clearance case is what lets the pass work the
    /// scripted <c>CFIX … ; CAPP …</c> composition, where the approach clearance waits in the command queue behind a
    /// fix condition and the aircraft has no phase of its own until it fires.
    /// </summary>
    private bool IsProtectionEligible(AircraftState follower, RunwayInfo runway, SimScenarioState scenario, bool preClearance)
    {
        // Followers only — deliberately asymmetric. A live-traffic shadow flies its feed and skips the sim's speed
        // integrator entirely, so a ceiling stamped on it would change nothing while the instructor still read a
        // "reduce speed" line for an instruction no one issued. A shadow ahead is a different matter: it really is
        // on the runway, so it stays in the stream as a leader.
        if (follower.IsShadow)
        {
            return false;
        }

        Phase? phase = follower.Phases?.CurrentPhase;
        if ((phase is not (ApproachNavigationPhase or FinalApproachPhase)) && !(preClearance && (phase is null)))
        {
            return false;
        }

        if (!IsRunwayArrival(follower, runway, preClearance))
        {
            return false;
        }

        // §5-7-1.b.4: no speed adjustment inside the FAF or 5 nm from the runway, whichever is closer. The same
        // pair of tests FlightPhysics.AutoCancelSpeedAtFinal uses, so the two agree on where the window starts and
        // pattern traffic — which flies its whole circuit inside 5 nm — is not caught by distance alone. An approach
        // reduction still standing here is released because the approach clearance supersedes it (§5-7-1.d, AIM
        // 4-4-12.a.7). A simulated-tower instruction already issued outside the window is exempt: the paragraph
        // forbids issuing a speed adjustment in there, not continuing to fly one, and final approach speed is the
        // pilot's own speed from the FAF anyway.
        double thresholdDistance = GeoMath.DistanceNm(follower.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));
        if (
            (!follower.Approach.SameRunwayProtectionFasInstructed)
            && (thresholdDistance <= FinalSpeedAdjustmentCutoffNm)
            && ApproachCommandHandler.IsOnFinal(follower, runway)
        )
        {
            return false;
        }

        return !HasOtherSpeedAuthority(follower, scenario);
    }

    /// <summary>
    /// True when someone other than the simulated TRACON owns this aircraft's speed: a human controller assigned it
    /// (a scenario preset or an AI position does not), its speed restrictions were deleted, or the student
    /// controller holds the track — the simulated TRACON only spaces an arrival while it owns it.
    /// </summary>
    private static bool HasOtherSpeedAuthority(AircraftState aircraft, SimScenarioState scenario)
    {
        if (aircraft.Targets.SpeedCommandIsControllerIssued || aircraft.Procedure.SpeedRestrictionsDeleted)
        {
            return true;
        }

        return StudentOwnsTrack(aircraft, scenario);
    }

    /// <summary>
    /// True when the student controller holds this aircraft's track — they are working it, so its speed is theirs.
    /// Split out from <see cref="HasOtherSpeedAuthority"/> because the pass treats this case differently from the
    /// rest: a student taking the track is handed the speed (<see cref="HandOverSameRunwayProtection"/>) rather than
    /// having it released back.
    /// </summary>
    private static bool StudentOwnsTrack(AircraftState aircraft, SimScenarioState scenario) =>
        aircraft.Track.Owner is { } owner && scenario.StudentPosition is { } student && owner.MatchesPosition(student);

    /// <summary>
    /// True from the moment a handoff of this aircraft's track to the student controller is initiated until it is
    /// accepted or retracted: the track's <see cref="AircraftTrack.HandoffPeer"/> is the student's position, matched the
    /// way <see cref="StudentOwnsTrack"/> matches the owner. §5-4-5.b bars the transferring controller from changing
    /// the aircraft's speed while the handoff is being initiated, not only after acceptance.
    /// </summary>
    private static bool HandoffToStudentInProgress(AircraftState aircraft, SimScenarioState scenario) =>
        aircraft.Track.HandoffPeer is { } peer && scenario.StudentPosition is { } student && peer.MatchesPosition(student);

    /// <summary>
    /// Predicts the two threshold crossings and, when the follower's would fall inside the interval the leader needs
    /// to clear the runway, stamps the speed ceiling that opens it. Returns true when the pass now owns this
    /// follower's ceiling.
    ///
    /// <para>Where the follower is decides who may speak to it. Outside
    /// <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/> it is the simulated approach controller's, and
    /// the figure is the §5-7-3.c floor that controller may assign. Inside, the arrival is on the local controller's
    /// frequency, so the approach controller issues nothing new and a reduction it already assigned is merely held
    /// (<see cref="HoldApproachReductionInsideTowerBoundary"/>). The simulated tower may issue in there — "reduce to
    /// final approach speed" (§5-7-3.f), the figure being Vapp — but only when the student is working a ground
    /// position (<see cref="SimScenarioState.IsStudentGroundPosition"/>): a student on the tower or on approach is
    /// the one who would have said it. That instruction latches: it is re-applied every tick without being
    /// re-announced, because
    /// <see cref="FlightPhysics"/>'s auto-cancel at the §5-7-1.b.4 window and a scripted <c>RNS</c> both null
    /// <see cref="ControlTargets.SpeedCeiling"/>, and a pilot already flying final approach speed does not speed up
    /// when the controller stops talking.</para>
    /// </summary>
    private bool TryProtectFollower(
        AircraftState follower,
        double followerDistanceNm,
        AircraftState leader,
        RunwayInfo runway,
        SimScenarioState scenario
    )
    {
        AirportGroundLayout? layout = World.GroundLayout;
        double followerEta = RunwayOccupancy.SecondsToAssignedThreshold(follower, runway, layout);
        double leaderEta = LeaderThresholdEtaSeconds(leader, runway, layout);
        if (!double.IsFinite(followerEta) || !double.IsFinite(leaderEta))
        {
            return false;
        }

        if (follower.Approach.SameRunwayProtectionFasInstructed)
        {
            return HoldTowerFasInstruction(follower, runway);
        }

        // Inside the tower boundary the arrival is on the local controller's frequency: the simulated approach
        // controller has nothing more to say to it, and the simulated tower speaks only for a student who is working
        // the ground. With neither able to issue, all that is left is to keep flying what was already assigned.
        bool insideTowerBoundary = SameRunwayArrivalProtection.IsInsideTowerSpeedAuthority(followerDistanceNm);
        bool towerAuthority = scenario.IsStudentGroundPosition && insideTowerBoundary && ApproachCommandHandler.IsOnFinal(follower, runway);
        if (insideTowerBoundary && !towerAuthority)
        {
            return HoldApproachReductionInsideTowerBoundary(follower);
        }

        AircraftCategory followerCategory = AircraftCategorization.Categorize(follower.AircraftType);
        AircraftCategory leaderCategory = AircraftCategorization.Categorize(leader.AircraftType);
        double vref = AircraftPerformance.ApproachSpeed(follower.AircraftType, followerCategory);
        double wakeNm = WakeTurbulenceData.OnApproachWakeSeparationNm(leader.AircraftType, leaderCategory, follower.AircraftType, followerCategory);
        double required = SameRunwayArrivalProtection.RequiredThresholdIntervalSeconds(
            leaderCategory,
            SameRunwayArrivalProtection.TryBuildRollout(leader, -leaderEta),
            wakeNm,
            vref
        );

        // Engage on the required interval; once engaged, hold until the interval has opened past it by the
        // hysteresis deadband. Without the separation the boundary limit-cycles: release, the follower accelerates,
        // the interval closes, re-engage — with a fresh terminal line every lap.
        bool engaged = follower.Approach.SameRunwayProtectionCeilingKts is not null;
        double conflictInterval = engaged ? required + SameRunwayArrivalProtection.ReleaseHysteresisSeconds : required;
        if (!SameRunwayArrivalProtection.IsConflictPredicted(leaderEta, followerEta, conflictInterval))
        {
            return false;
        }

        double scheduled = ArrivalSpacingManager.ScheduledFinalSpeedKts(
            follower.AircraftType,
            followerCategory,
            vref,
            follower.Callsign,
            followerDistanceNm
        );
        double finalApproachSpeed = TowerFinalApproachSpeedKts(follower, runway);
        double ceiling = towerAuthority
            ? finalApproachSpeed
            : SameRunwayArrivalProtection.ProtectionCeilingKts(
                leader.IndicatedAirspeed,
                leaderEta + required - followerEta,
                new SameRunwayArrivalProtection.FollowerProfile(followerCategory, follower.GroundSpeed, followerDistanceNm, vref, scheduled)
            );

        // Lowering only: a ceiling already at or below what this pass would ask for (a published crossing
        // restriction, the generator spacing manager) is already doing the work, so there is nothing to stamp — and
        // therefore nothing to restore later. An engagement overtaken that way hands its ceiling back now, and an
        // instruction that was never needed is never issued.
        double? displaced = DisplacedCeilingKts(follower);
        if (displaced is { } binding && binding <= ceiling)
        {
            ReleaseSameRunwayProtection(follower);
            return false;
        }

        if (towerAuthority)
        {
            AnnounceTowerFasInstruction(follower, runway, ResolveTowerLabel(runway));
            follower.Approach.SameRunwayProtectionFasInstructed = true;
        }
        else
        {
            AnnounceProtectionEngaged(follower, runway, ceiling);
        }

        StampProtectionCeiling(follower, ceiling, displaced);
        _logger.LogDebug(
            "[SameRunwayProtection] {Follower} ({Dist:F1}nm) behind {Leader}: followerEta={FollowerEta:F0}s leaderEta={LeaderEta:F0}s required={Required:F0}s → ceiling {Ceiling:F0}kt (vref {Vref:F0}, scheduled {Scheduled:F0}, fas={Fas:F0}, tower={Tower})",
            follower.Callsign,
            followerDistanceNm,
            leader.Callsign,
            followerEta,
            leaderEta,
            required,
            follower.Targets.SpeedCeiling,
            vref,
            scheduled,
            finalApproachSpeed,
            towerAuthority
        );
        return true;
    }

    /// <summary>
    /// Re-applies a simulated-tower final-approach-speed instruction that is already in force, with no conflict test
    /// of its own: the instruction outlives the geometry that prompted it (§5-7-1 warns against the alternate
    /// decreases and increases that withdrawing it at the first favourable tick would produce), and the aircraft is
    /// flying it whether or not anyone is talking. Always returns true — the pass owns this follower until something
    /// releases it — including when a lower ceiling from another writer is left standing, because a held instruction
    /// is not withdrawn merely because someone else is holding the aircraft slower still.
    /// </summary>
    private bool HoldTowerFasInstruction(AircraftState follower, RunwayInfo runway)
    {
        if (!follower.Approach.SameRunwayProtectionFasInstructed)
        {
            return false;
        }

        double finalApproachSpeed = TowerFinalApproachSpeedKts(follower, runway);
        double? displaced = DisplacedCeilingKts(follower);
        if (displaced is { } binding && binding <= finalApproachSpeed)
        {
            return true;
        }

        StampProtectionCeiling(follower, finalApproachSpeed, displaced);
        return true;
    }

    /// <summary>
    /// Re-applies a speed reduction the simulated approach controller assigned outside
    /// <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/> to a follower that has since crossed inside it,
    /// with no conflict test of its own and no new terminal line. That controller may not issue in there — the
    /// arrival is on the tower's frequency — but the speed it assigned is flown until somebody takes it over or takes
    /// it back: the tower student accepting the handoff (<see cref="HandOverSameRunwayProtection"/>, which hands the
    /// speed over with the track and leaves the reduction standing) or the 5 nm / FAF window
    /// (<see cref="IsProtectionEligible"/>'s test, which releases a non-FAS engagement — §5-7-1.d and AIM 4-4-12.a.7:
    /// the approach clearance supersedes a prior speed assignment and the pilot makes their own adjustments from
    /// there; §5-7-1.b.4 only forbids issuing in there). Returns false when nothing is stamped or the ceiling has been
    /// cancelled — there is no reduction to hold — and true otherwise, including when a lower ceiling from another
    /// writer is left standing.
    /// </summary>
    private static bool HoldApproachReductionInsideTowerBoundary(AircraftState follower)
    {
        // Nothing standing means somebody cancelled it — RNS nulls the ceiling without claiming the speed for a human
        // controller — and there is nothing to hold. The release loop then hands the (already null) ceiling back.
        if (follower.Targets.SpeedCeiling is null)
        {
            return false;
        }

        if (follower.Approach.SameRunwayProtectionCeilingKts is not { } stamped)
        {
            return false;
        }

        double? displaced = DisplacedCeilingKts(follower);
        if (displaced is { } binding && binding <= stamped)
        {
            return true;
        }

        StampProtectionCeiling(follower, stamped, displaced);
        return true;
    }

    /// <summary>
    /// The speed "reduce to final approach speed" means for this arrival: Vapp — its Vref plus the wind/gust additive
    /// <see cref="Phases.Tower.FinalApproachPhase"/> flies — never bare Vref.
    /// </summary>
    private double TowerFinalApproachSpeedKts(AircraftState follower, RunwayInfo runway)
    {
        double vref = AircraftPerformance.ApproachSpeed(follower.AircraftType, AircraftCategorization.Categorize(follower.AircraftType));
        return SameRunwayArrivalProtection.FinalApproachSpeedKts(
            vref,
            AircraftPerformance.WindApproachAdditive(World.Weather, runway.TrueHeading.Degrees)
        );
    }

    /// <summary>
    /// The ceiling that would be in force with this pass out of the picture: its own stamp is not a constraint to
    /// defer to, so while engaged that is the value it displaced — unless something has overwritten the stamp since,
    /// in which case whatever is there now belongs to that writer.
    /// </summary>
    private static double? DisplacedCeilingKts(AircraftState follower) =>
        (follower.Approach.SameRunwayProtectionCeilingKts is { } stamped) && (follower.Targets.SpeedCeiling is { } live) && (live == stamped)
            ? follower.Approach.SameRunwayProtectionDisplacedCeilingKts
            : follower.Targets.SpeedCeiling;

    /// <summary>
    /// Takes ownership of the follower's speed ceiling at <paramref name="ceilingKts"/>, stashing
    /// <paramref name="displacedKts"/> (from <see cref="DisplacedCeilingKts"/>) so the release can put back whatever
    /// this pass covered up.
    /// </summary>
    private static void StampProtectionCeiling(AircraftState follower, double ceilingKts, double? displacedKts)
    {
        follower.Approach.SameRunwayProtectionDisplacedCeilingKts = displacedKts;
        follower.Approach.SameRunwayProtectionCeilingKts = ceilingKts;
        follower.Targets.SpeedCeiling = ceilingKts;
    }

    /// <summary>
    /// Terminal line for a fresh engagement of the protection, attributed to the position that owns the track — the
    /// simulated TRACON speaks on its own frequency, so this is a notification about what that controller did and
    /// never a pilot transmission on the student's. A non-null
    /// <see cref="AircraftApproachState.SameRunwayProtectionCeilingKts"/> is what makes it one line per engagement
    /// rather than one per tick; a follower whose conflict clears and returns gets a second line, which is the truth
    /// of what the controller had to do. The spoken figure is rounded to the nearest 5 kt per §5-7-1.a.7 ("express
    /// speed adjustments … in 5-knot increments") while <paramref name="ceilingKts"/> itself stays continuous — the
    /// physics is not a controller's radio and has no reason to quantise.
    /// </summary>
    private static void AnnounceProtectionEngaged(AircraftState follower, RunwayInfo runway, double ceilingKts)
    {
        if (follower.Approach.SameRunwayProtectionCeilingKts is not null)
        {
            return;
        }

        string position = follower.Track.Owner?.Callsign ?? "TRACON";
        double spoken = Math.Round(ceilingKts / 5.0, MidpointRounding.AwayFromZero) * 5.0;
        follower.PendingNotifications.Add($"{position} → {follower.Callsign}: reduce speed to {spoken:F0} (in-trail spacing, {runway.Designator})");
    }

    /// <summary>
    /// Terminal line for the simulated local controller's "reduce to final approach speed" (§5-7-3.f), attributed to
    /// the tower position that would have said it rather than to the track owner — inside
    /// <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/> the arrival is on that frequency. One line per
    /// instruction: the latch is what makes the re-stamp every tick silent. The instruction names no figure, so
    /// unlike <see cref="AnnounceProtectionEngaged"/> there is nothing to express in the 5-knot increments
    /// §5-7-1.a.7 requires.
    /// </summary>
    private static void AnnounceTowerFasInstruction(AircraftState follower, RunwayInfo runway, string towerLabel)
    {
        if (follower.Approach.SameRunwayProtectionFasInstructed)
        {
            return;
        }

        follower.PendingNotifications.Add(
            $"{towerLabel} → {follower.Callsign}: reduce to final approach speed (in-trail spacing, {runway.Designator})"
        );
    }

    /// <summary>
    /// Who the instruction is attributed to: the scenario's own tower position for that airport, else the AI position
    /// answering local control there, else the generic <c>TWR</c>. Callsign prefixes are matched with
    /// <see cref="NavigationDatabase.AirportIdsMatch"/> rather than a literal compare because a runway's airport id
    /// and a position callsign's prefix need not agree on the K, the way
    /// <c>ControllerAi.AiPositionResolver</c> matches them.
    /// </summary>
    private string ResolveTowerLabel(RunwayInfo runway)
    {
        if (Scenario is not { } scenario)
        {
            return GenericTowerLabel;
        }

        foreach (ResolvedAtcPosition position in scenario.AtcPositions)
        {
            string callsign = position.Owner.Callsign;
            if ((AtcPositionTypeClassifier.Classify(callsign) == GenericTowerLabel) && CallsignIsAtAirport(callsign, runway.AirportId))
            {
                return callsign;
            }
        }

        foreach (PilotAnsweringPosition contact in scenario.PilotContacts.Positions)
        {
            bool covers = contact.AirportIds.Any(id => NavigationDatabase.AirportIdsMatch(id, runway.AirportId));
            if ((contact.PositionType == GenericTowerLabel) && covers && contact.Owner?.Callsign is { Length: > 0 } aiCallsign)
            {
                return aiCallsign;
            }
        }

        return GenericTowerLabel;
    }

    /// <summary>The position-type code for local control, and the fallback attribution when no tower position is staffed.</summary>
    private const string GenericTowerLabel = "TWR";

    /// <summary>True when a position callsign's facility prefix is this airport — <c>OAK_TWR</c> at OAK.</summary>
    private static bool CallsignIsAtAirport(string callsign, string airportId)
    {
        int underscore = callsign.IndexOf('_');
        return (underscore > 0) && NavigationDatabase.AirportIdsMatch(callsign[..underscore], airportId);
    }

    /// <summary>
    /// Seconds until the leader crosses the landing threshold, negative once it is past. Measured along the assigned
    /// runway's own course, not the leader's track, for the reason on
    /// <see cref="RunwayOccupancy.DistanceToAssignedThresholdNm"/>. An aircraft already on the ground crossed the
    /// threshold to get there, so its ETA is clamped to zero-or-negative however slowly it is now rolling (the raw
    /// helper reports positive infinity for a stopped aircraft).
    /// </summary>
    private static double LeaderThresholdEtaSeconds(AircraftState leader, RunwayInfo runway, AirportGroundLayout? layout)
    {
        double eta = RunwayOccupancy.SecondsToAssignedThreshold(leader, runway, layout);
        if (!leader.IsOnGround)
        {
            return eta;
        }

        return double.IsFinite(eta) ? Math.Min(0.0, eta) : 0.0;
    }

    /// <summary>
    /// Builds, adds, records, and announces one generated arrival placed <c>OnFinal</c> at
    /// <paramref name="distanceNm"/> with the already-resolved <paramref name="weight"/> and
    /// <paramref name="engine"/>. Returns the spawned state, or null if generation failed.
    /// </summary>
    private AircraftState? SpawnGeneratedArrival(GeneratorState gen, double distanceNm, WeightClass weight, EngineKind engine)
    {
        SimScenarioState scenario = Scenario!;
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

        List<AircraftState> existing = World.GetSnapshot();
        AirportGroundLayout? groundLayout = scenario.PrimaryAirportId is not null ? _groundData.GetLayout(scenario.PrimaryAirportId) : null;
        (AircraftState? state, string? error) = AircraftGenerator.Generate(
            request,
            scenario.PrimaryAirportId,
            existing,
            groundLayout,
            World.Rng,
            BeaconCodePool
        );

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
        // generator carries an AutoTrackConfiguration, AutoTrackGeneratedSpawn applies it and records after,
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

    private void TrySpawnVfrArrival(VfrArrivalGeneratorState gen, int ratePercent, List<AircraftState> spawned)
    {
        SimScenarioState scenario = Scenario!;
        if (scenario.ElapsedSeconds < gen.NextSpawnSeconds)
        {
            return;
        }

        AircraftState? state = SpawnGeneratedVfrArrival(gen);
        if (state is null)
        {
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        AutoTrackGeneratedSpawn(state, gen.Config.AutoTrackConfiguration);
        spawned.Add(state);
        gen.NextSpawnSeconds =
            scenario.ElapsedSeconds
            + JitteredInterval(gen.Config, ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(gen.Config.IntervalTime, ratePercent));
    }

    private void TrySpawnOverflight(OverflightGeneratorState gen, List<AircraftState> spawned)
    {
        SimScenarioState scenario = Scenario!;
        if (scenario.ElapsedSeconds < gen.NextSpawnSeconds)
        {
            return;
        }

        AircraftState? state = SpawnGeneratedOverflight(gen);
        if (state is null)
        {
            gen.NextSpawnSeconds = scenario.ElapsedSeconds + SpawnRetryBackoffSeconds;
            return;
        }

        spawned.Add(state);
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
        List<AircraftState> existing = World.GetSnapshot();
        AirspaceDatabase airspace = AirspaceDatabase.Default;

        for (int attempt = 0; attempt < VfrSpawnSiting.MaxSpawnAttempts; attempt++)
        {
            double bearingMagnetic = VfrSpawnSiting.RollBearing(bearingFrom, bearingTo, World.Rng);
            double distanceNm = VfrSpawnSiting.RollInRange(minDistanceNm, maxDistanceNm, World.Rng);
            double altitudeFt = VfrSpawnSiting.RollInRange(minAltitudeFt, maxAltitudeFt, World.Rng);

            double bearingTrue = MagneticDeclination.MagneticToTrue(bearingMagnetic, airport, Scenario!.MagneticModelDateUtc);
            (double lat, double lon) = GeoMath.ProjectPoint(airport.Lat, airport.Lon, new TrueHeading(bearingTrue), distanceNm);
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
        SimScenarioState scenario = Scenario!;
        VfrArrivalGeneratorConfig config = gen.Config;
        string? airportId = scenario.PrimaryAirportId;
        if (string.IsNullOrEmpty(airportId))
        {
            _logger.LogWarning("VFR arrival generator '{Id}' skipped: scenario has no primary airport", config.Id);
            return null;
        }

        (double Lat, double Lon)? airportPos = NavigationDatabase.Instance.GetFixPosition(airportId);
        if (airportPos is null)
        {
            _logger.LogWarning("VFR arrival generator '{Id}': primary airport '{Airport}' not in navdata", config.Id, airportId);
            return null;
        }

        var airport = new LatLon(airportPos.Value.Lat, airportPos.Value.Lon);
        (LatLon Position, double BearingTrue, double BearingMagnetic, double DistanceNm, double AltitudeFt)? site = RollVfrSpawnSite(
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

        AirportGroundLayout? groundLayout = _groundData.GetLayout(airportId);
        (AircraftState? state, string? error) = AircraftGenerator.Generate(
            request,
            airportId,
            World.GetSnapshot(),
            groundLayout,
            World.Rng,
            BeaconCodePool
        );
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
        string directTo = string.IsNullOrWhiteSpace(config.DirectTo) ? airportId : config.DirectTo;
        ArrivalRouteResolver.PopulateNavigationRoute(state, directTo, routeWarnings);
        foreach (string warning in routeWarnings)
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
        SimScenarioState scenario = Scenario!;
        OverflightGeneratorConfig config = gen.Config;
        string? airportId = scenario.PrimaryAirportId;
        if (string.IsNullOrEmpty(airportId))
        {
            _logger.LogWarning("Overflight generator '{Id}' skipped: scenario has no primary airport", config.Id);
            return null;
        }

        (double Lat, double Lon)? airportPos = NavigationDatabase.Instance.GetFixPosition(airportId);
        if (airportPos is null)
        {
            _logger.LogWarning("Overflight generator '{Id}': primary airport '{Airport}' not in navdata", config.Id, airportId);
            return null;
        }

        var airport = new LatLon(airportPos.Value.Lat, airportPos.Value.Lon);
        double exitDistanceNm = config.ExitDistance ?? (config.MaxDistance + DefaultOverflightExitMarginNm);

        (LatLon Position, double BearingTrue, double BearingMagnetic, double DistanceNm, double AltitudeFt)? site = RollVfrSpawnSite(
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

        double exitBearingMagnetic = VfrSpawnSiting.RollBearing(config.ToBearingFrom, config.ToBearingTo, World.Rng);
        double exitBearingTrue = MagneticDeclination.MagneticToTrue(exitBearingMagnetic, airport, Scenario!.MagneticModelDateUtc);
        LatLon exitPoint = GeoMath.ProjectPoint(airport, new TrueHeading(exitBearingTrue), exitDistanceNm);

        // Name the exit point as an FRD off the field so the route overlay labels it rather than drawing it
        // as an unnamed arc vertex.
        string exitName = $"{airportId}{(int)Math.Round(exitBearingMagnetic) % 360:000}{(int)Math.Round(exitDistanceNm):000}";

        // 91.159(a) binds level cruising flight more than 3000 ft above the surface, and it keys on the
        // aircraft's magnetic course over the ground -- which runs spawn -> exit point, not along the
        // author's "to" radial from the field.
        double altitudeFt = site.Value.AltitudeFt;
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

        AirportGroundLayout? groundLayout = _groundData.GetLayout(airportId);
        (AircraftState? state, string? error) = AircraftGenerator.Generate(
            request,
            airportId,
            World.GetSnapshot(),
            groundLayout,
            World.Rng,
            BeaconCodePool
        );
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
        double fieldElevation = NavigationDatabase.Instance.GetAirportElevation(airportId) ?? 0;
        if (rolledAltitudeFt - fieldElevation <= HemisphericAltitude.AglFloorFt)
        {
            return rolledAltitudeFt;
        }

        double courseTrue = GeoMath.BearingTo(spawn, exitPoint);
        double courseMagnetic = MagneticDeclination.TrueToMagnetic(courseTrue, spawn, Scenario!.MagneticModelDateUtc);
        double? snapped = HemisphericAltitude.Snap(courseMagnetic, rolledAltitudeFt, config.AltitudeMin, config.AltitudeMax);

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

        LatLon first = state.Targets.NavigationRoute[0].Position;
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

        double fieldElevation = NavigationDatabase.Instance.GetAirportElevation(airportId) ?? 0;
        AircraftCategory category = AircraftCategorization.Categorize(state.AircraftType);
        double patternAglFt = category is AircraftCategory.Jet or AircraftCategory.Turboprop ? 1500.0 : 1000.0;
        double descendTo = config.DescendToAltitude ?? (Math.Round((fieldElevation + patternAglFt) / 100.0) * 100.0);

        double performanceRate = AircraftPerformance.DescentRate(state.AircraftType, category, state.Altitude);
        state.Targets.TargetAltitude = Math.Min(descendTo, state.Altitude);
        state.Targets.DesiredVerticalRate = Math.Min(Math.Abs(config.InitialVsFpm), performanceRate);
    }

    private bool TryReserveSoloParkingInitialCallupSlot(double nowSeconds)
    {
        SimScenarioState? scenario = Scenario;
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
        SimScenarioState? scenario = Scenario;
        if (scenario is null)
        {
            return;
        }

        int oldParkingRate = ScenarioPacing.ClampParkingInitialCallupPercent(scenario.SoloParkingInitialCallupRatePercent);
        int newParkingRate = ScenarioPacing.ClampParkingInitialCallupPercent(parkingInitialCallupRatePercent);
        bool parkingChanged = oldParkingRate != newParkingRate;
        int oldArrivalRate = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);
        int newArrivalRate = ScenarioPacing.ClampArrivalGeneratorPercent(arrivalGeneratorRatePercent);
        bool arrivalChanged = oldArrivalRate != newArrivalRate;

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

        double now = scenario.ElapsedSeconds;
        if ((oldRate <= 0) || (newRate > oldRate))
        {
            scenario.NextSoloParkingInitialCallupSlotSeconds = now;
            return;
        }

        if (newRate < oldRate)
        {
            double slowerSlot = now + ScenarioPacing.EffectiveParkingInitialCallupIntervalSeconds(newRate);
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
        int rate = ScenarioPacing.ClampArrivalGeneratorPercent(scenario.SoloArrivalGeneratorRatePercent);

        foreach (IGeneratorRuntimeState? gen in scenario.Generators.Cast<IGeneratorRuntimeState>().Concat(scenario.VfrArrivalGenerators))
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
        WeightClass baseWeight = ParseWeightCategory(config.WeightCategory);
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

        double pick = rng.NextDouble() * band.Sum(e => e.Share);
        foreach ((WeightClass Weight, double Share) entry in band)
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
    /// A generated aircraft whose generator carries an autotrack configuration is owned / scratchpad-tagged /
    /// queued for a student handoff before its spawn is recorded, so the recorded snapshot carries the owner and
    /// the first broadcast shows no untracked flash. Generated aircraft without one are recorded eagerly at spawn
    /// (see <see cref="SpawnGeneratedArrival"/>), which is why this records only the autotrack-bearing path.
    /// </summary>
    private void AutoTrackGeneratedSpawn(AircraftState state, AutoTrackConditions? autoTrack)
    {
        if (autoTrack is null)
        {
            return;
        }

        var loaded = new LoadedAircraft
        {
            State = state,
            AutoTrackConditions = autoTrack,
            SpawnDelaySeconds = (int)state.SpawnedAtSeconds,
        };

        ApplyAutoTrackConditions(loaded);
        RecordGeneratedAircraftSpawn(state);

        foreach (string msg in loaded.AutoTrackMessages)
        {
            EmitTerminal("System", state.Callsign, msg);
        }
    }
}
