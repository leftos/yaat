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

// Scenario load and the ground-layout resolution it depends on.
public sealed partial class SimulationEngine
{
    // Reads the layouts that must already be loaded, so a read repeated every tick reports a missing one once.
    private ReadyLayoutReader? _readyLayouts;

    /// <summary>
    /// Loads a scenario into a fresh world. <paramref name="sessionStartUtc"/> is the instant t=0 of the session is
    /// anchored to (<see cref="SimScenarioState.SessionStartUtc"/>): the room clock for a new session, the recorded
    /// instant when replaying.
    /// </summary>
    public List<string> LoadScenario(string json, int rngSeed, DateTime sessionStartUtc) =>
        LoadScenario(json, rngSeed, sessionStartUtc, static _ => { });

    /// <summary>
    /// <see cref="LoadScenario(string, int, DateTime)"/>, with <paramref name="configureBeforePresets"/> run on the new
    /// scenario state before the immediate aircraft's presets dispatch: a replay restores there what the live room had in
    /// place when its presets dispatched (the ARTCC config and the towered airports).
    /// </summary>
    public List<string> LoadScenario(string json, int rngSeed, DateTime sessionStartUtc, Action<SimScenarioState> configureBeforePresets)
    {
        Scenario?.ReplaceWindGridPins(null);
        World.Clear();
        // A reload starts the frequency quiet and free of gates: a reload-and-replay from zero must not inherit airtime
        // recorded later in the run the load replaces, nor lines it still had queued.
        World.ActiveFrequency.Clear();
        _loggedExitCapacityRefusals.Clear();
        World.Rng = new SerializableRandom(rngSeed);
        World.ReactionDelayRng = new SerializableRandom(rngSeed);
        World.ReleaseJitterRng = new SerializableRandom(rngSeed);
        ApproachEvaluator.Reset();
        SoloTrainingEvaluator.Reset();
        BeaconCodePool.Clear();

        ScenarioLoadResult result = ScenarioLoader.Load(json, _groundData, World.Rng, sessionStartUtc.Date);

        // No ARTCC config reaches Yaat.Sim, so the pool has no banks here and falls back to sequential
        // codes. The server configures banks from the facility before running its own assignment pass.
        ScenarioLoader.AssignSpawnBeacons(BeaconCodePool, result.AllAircraftStates);

        Scenario = new SimScenarioState
        {
            ScenarioId = ScenarioIdentity.ResolveScenarioId(result.Id, json),
            ScenarioName = result.Name,
            RngSeed = rngSeed,
            SessionStartUtc = sessionStartUtc,
            OriginalScenarioJson = json,
            PrimaryAirportId = result.PrimaryAirportId,
            ArtccId = result.ArtccId,
            IsLiveSession = result.IsLiveSession,
            InitialContactTransfers = NavigationDatabase.Instance.InitialContactTransfers,
            WakeDirectives = NavigationDatabase.Instance.WakeDirectives,
            HasSoloParkingInitialCallupSource = result.HasParkingSpawns,
            HasSoloArrivalGeneratorSource = result.HasArrivalGenerators,
            InitialStripBayByCallsign = result.InitialStripBayByCallsign,
        };
        configureBeforePresets(Scenario);

        // Derived, not recorded: every ASDE-X facility starts on the configuration its ARTCC config, navdata and the
        // active runways set above give it, which the first snapshot then carries. No ARTCC config (a bare load) seeds nothing.
        Scenario.AsdexSafetyLogicConfigs = AsdexSafetyLogicSeed.Build(
            Scenario.ArtccConfig,
            NavigationDatabase.InstanceOrNull,
            Scenario.ActiveRunways
        );

        // Add immediate aircraft and dispatch their presets
        foreach (LoadedAircraft loaded in result.ImmediateAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            loaded.State.SpawnedAtSeconds = Scenario.ElapsedSeconds;
            World.AddAircraft(loaded.State);
            DispatchPresetCommands(loaded);
        }

        // Queue delayed aircraft
        foreach (LoadedAircraft loaded in result.DelayedAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            Scenario.DelayedQueue.Add(
                new DelayedSpawn
                {
                    Aircraft = loaded,
                    SpawnAtSeconds = loaded.SpawnDelaySeconds,
                    HeldForRelease = DepartureSpawnClassifier.IsHeldSpawnCandidate(loaded),
                }
            );
        }

        // Queue triggers
        foreach (InitializationTrigger trigger in result.Triggers)
        {
            Scenario.TriggerQueue.Add(new ScheduledTrigger { Command = trigger.Command, FireAtSeconds = trigger.TimeOffset });
        }

        // Initialize generators
        _generatorSpawnLog.Clear();
        foreach (ScenarioGeneratorConfig genConfig in result.Generators)
        {
            string runwayId = genConfig.Runway ?? "";
            RunwayInfo? runway = NavigationDatabase.Instance.GetRunway(result.PrimaryAirportId ?? "", runwayId);
            if (runway is null)
            {
                result.Warnings.Add($"Generator '{genConfig.Id}': runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} not found");
                continue;
            }

            // The first generator fires on its authored schedule. Each subsequent generator with
            // randomized intervals gets a random initial phase within its first interval, so multiple
            // generators that share a startTimeOffset don't all spawn on the same first tick. Keyed off
            // the count of already-added generators, so a generator skipped for a missing runway above
            // doesn't consume the "first" slot.
            double firstSpawnSeconds = (double)genConfig.StartTimeOffset;
            if (Scenario.Generators.Count > 0 && genConfig.RandomizeInterval)
            {
                firstSpawnSeconds += World.Rng.NextDouble() * genConfig.IntervalTime;
            }

            Scenario.Generators.Add(
                new GeneratorState
                {
                    Config = genConfig,
                    Runway = runway,
                    NextSpawnSeconds = firstSpawnSeconds,
                }
            );
        }

        foreach (VfrArrivalGeneratorConfig cfg in result.VfrArrivalGenerators)
        {
            Scenario.VfrArrivalGenerators.Add(new VfrArrivalGeneratorState { Config = cfg, NextSpawnSeconds = StaggeredFirstSpawn(cfg) });
        }

        foreach (OverflightGeneratorConfig cfg in result.OverflightGenerators)
        {
            Scenario.OverflightGenerators.Add(new OverflightGeneratorState { Config = cfg, NextSpawnSeconds = StaggeredFirstSpawn(cfg) });
        }

        // Set ground layout
        if (Scenario.PrimaryAirportId is not null)
        {
            World.GroundLayout = ReadReadyLayout(Scenario.PrimaryAirportId, "scenario load");
        }

        Scenario.ScenarioAutoDeleteMode = result.AutoDeleteMode;
        Scenario.HasOngoingTrafficSource = result.HasOngoingTrafficSource;

        return result.Warnings;
    }

    // --- Three-phase tick API ---

    /// <summary>
    /// The ground layout for <paramref name="airportId"/>'s own field, or null when the engine's ground data has no
    /// map for it. <see cref="SimulationWorld.GroundLayout"/> — the scenario's primary airport, resolved through the
    /// same ground data — answers for that airport without a second lookup.
    ///
    /// <para>This is the datum a measurement <em>about a runway</em> resolves through: a landing threshold
    /// (<see cref="LandingThreshold"/>) and a distance to it (<see cref="RunwayOccupancy"/>) belong to the runway's
    /// field, not to whichever layout the aircraft happens to be carrying. An airborne arrival's
    /// <see cref="AircraftGroundOps.Layout"/> is null for a scripted arrival with no destination and for every
    /// live-traffic shadow, so reading a displacement off it measures one aircraft in a stream against the pavement
    /// end while the aircraft beside it measures against the real landing threshold — hundreds of feet apart on a
    /// displaced runway, and enough to put one of them inside the §5-7-1.b.4 window and the other outside it.</para>
    /// </summary>
    internal AirportGroundLayout? ResolveAirportLayout(string airportId)
    {
        if ((World.GroundLayout is { } primary) && NavigationDatabase.AirportIdsMatch(airportId, primary.AirportId))
        {
            return primary;
        }

        return _groundData.Read(airportId).Layout;
    }

    /// <summary>
    /// The ground layout <paramref name="aircraft"/> taxis on, or null when there is none or when any airport the choice
    /// reads is still loading: a pick that skips a loading airport for another one is never made.
    /// </summary>
    public AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft)
    {
        // An aircraft physically on the ground taxis on the airport its wheels are on —
        // never on a filed destination. A departure that files a destination but no
        // departure (e.g. a VFR plan created via CRC to KSMF while parked at OAK) would
        // otherwise load the destination's layout and reject every taxiway/parking lookup.
        if (aircraft.IsOnGround)
        {
            GroundDataRead physical = ReadOptionalLayout(RunwayOrContextAirport(aircraft));
            if ((physical.Layout is not null) || !physical.IsLoaded)
            {
                return physical.Layout;
            }
        }

        GroundDataRead dep = ReadOptionalLayout(aircraft.FlightPlan.Departure);
        GroundDataRead dest = ReadOptionalLayout(aircraft.FlightPlan.Destination);
        if (!dep.IsLoaded || !dest.IsLoaded)
        {
            return null;
        }

        // Cold-call VFR aircraft (pattern work, full-stop requests) frequently file
        // neither departure nor destination. Treat the assigned arrival runway's
        // airport — or the spawn-time operational airport context — as the implicit
        // destination so the ground layout is available for runway exit and taxi
        // after landing, without writing into the flight plan.
        if ((dep.Layout is null) && (dest.Layout is null))
        {
            return ReadOptionalLayout(RunwayOrContextAirport(aircraft)).Layout;
        }

        return NearerLayout(aircraft, dep.Layout, dest.Layout);
    }

    // The assigned runway's airport, else the aircraft's spawn-time operational airport.
    private static string? RunwayOrContextAirport(AircraftState aircraft)
    {
        string? airportId = aircraft.Phases?.AssignedRunway?.AirportId;
        return string.IsNullOrEmpty(airportId) ? aircraft.AirportId : airportId;
    }

    // An airport the aircraft does not name reads as loaded with no layout.
    private GroundDataRead ReadOptionalLayout(string? airportId) =>
        string.IsNullOrEmpty(airportId) ? new GroundDataRead(true, null, null) : _groundData.Read(airportId);

    /// <summary>
    /// The layout of an airport that must already be loaded at <paramref name="site"/> (a scenario load after its
    /// prefetch, a restore, a recording or snapshot load, a generator spawn). One that is still loading reads as null
    /// and is logged as an error, once per airport for this engine.
    /// </summary>
    private AirportGroundLayout? ReadReadyLayout(string airportId, string site) =>
        (_readyLayouts ??= new ReadyLayoutReader(_groundData, _logger)).Read(airportId, site);

    // Departure and destination both resolved: the one whose nearest node is closer to the aircraft.
    private static AirportGroundLayout? NearerLayout(AircraftState aircraft, AirportGroundLayout? depLayout, AirportGroundLayout? destLayout)
    {
        if (depLayout is null)
        {
            return destLayout;
        }

        if (destLayout is null || destLayout == depLayout)
        {
            return depLayout;
        }

        GroundNode? depNode = depLayout.FindNearestNode(aircraft.Position);
        GroundNode? destNode = destLayout.FindNearestNode(aircraft.Position);

        double depDist = depNode is not null ? GeoMath.DistanceNm(aircraft.Position, depNode.Position) : double.MaxValue;
        double destDist = destNode is not null ? GeoMath.DistanceNm(aircraft.Position, destNode.Position) : double.MaxValue;

        return destDist < depDist ? destLayout : depLayout;
    }

    // --- Private tick methods ---
}
