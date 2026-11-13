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
            World.GroundLayout = _groundData.GetLayout(Scenario.PrimaryAirportId);
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

        return _groundData.GetLayout(airportId);
    }

    public AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft)
    {
        // An aircraft physically on the ground taxis on the airport its wheels are on —
        // never on a filed destination. A departure that files a destination but no
        // departure (e.g. a VFR plan created via CRC to KSMF while parked at OAK) would
        // otherwise load the destination's layout and reject every taxiway/parking lookup. An aircraft on the ground at, or
        // landing at, a field runs on that field's layout alone, and on none at a field with no layout: never another
        // airport's (an airport first named mid-session has no layout, so it runs as at an airport with no vNAS map).
        if (FieldAirport(aircraft) is { Length: > 0 } fieldAirport)
        {
            return _groundData.GetLayout(fieldAirport);
        }

        AirportGroundLayout? depLayout = string.IsNullOrEmpty(aircraft.FlightPlan.Departure)
            ? null
            : _groundData.GetLayout(aircraft.FlightPlan.Departure);
        AirportGroundLayout? destLayout = string.IsNullOrEmpty(aircraft.FlightPlan.Destination)
            ? null
            : _groundData.GetLayout(aircraft.FlightPlan.Destination);

        // Cold-call VFR aircraft (pattern work, full-stop requests) frequently file
        // neither departure nor destination. Treat the assigned arrival runway's
        // airport — or the spawn-time operational airport context — as the implicit
        // destination so the ground layout is available for runway exit and taxi
        // after landing, without writing into the flight plan.
        if (depLayout is null && destLayout is null)
        {
            string? implicitAirport = aircraft.Phases?.AssignedRunway?.AirportId;
            if (string.IsNullOrEmpty(implicitAirport))
            {
                implicitAirport = aircraft.AirportId;
            }

            return string.IsNullOrEmpty(implicitAirport) ? null : _groundData.GetLayout(implicitAirport);
        }

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

    /// <summary>
    /// Drops the layout <paramref name="aircraft"/> carries when it is on the ground at, or landing at, a field (see
    /// <see cref="FieldAirport(AircraftState)"/>) the layout is not of: an airborne aircraft amended to another airport
    /// keeps the layout it was given, which its landing there must not take onto that airport's runway.
    /// <see cref="ResolveGroundLayout"/> then supplies the field's own layout, or none.
    /// </summary>
    internal void DropLayoutOfAnotherField(AircraftState aircraft)
    {
        if ((aircraft.Ground.Layout is { } carried) && !IsLayoutOfField(carried, FieldAirport(aircraft)))
        {
            aircraft.Ground.Layout = null;
        }
    }

    /// <summary>
    /// The layout of the airport <paramref name="aircraft"/>, holding after its runway exit, landed at: the one it carries
    /// when that is its field's (<see cref="FieldAirport(AircraftState)"/>), else its field's. With no field known, the one
    /// it carries, else its destination's.
    /// </summary>
    private AirportGroundLayout? LayoutWhereLanded(AircraftState aircraft)
    {
        AirportGroundLayout? carried = aircraft.Ground.Layout;
        string? field = FieldAirport(aircraft);
        if (!string.IsNullOrEmpty(field))
        {
            return ((carried is not null) && IsLayoutOfField(carried, field)) ? carried : ResolveAirportLayout(field);
        }

        string? destination = aircraft.FlightPlan.Destination;
        return carried ?? (string.IsNullOrEmpty(destination) ? null : ResolveAirportLayout(destination));
    }

    /// <summary>
    /// Whether <paramref name="layout"/> may be carried by an aircraft whose field is <paramref name="field"/>: it is that
    /// field's, or the aircraft has no field (an airborne aircraft not landing keeps what it carries).
    /// </summary>
    private static bool IsLayoutOfField(AirportGroundLayout layout, string? field) =>
        string.IsNullOrEmpty(field) || NavigationDatabase.AirportIdsMatch(layout.AirportId, field);

    /// <summary>
    /// The airport whose layout alone <paramref name="aircraft"/> may run on (see
    /// <see cref="FieldAirport(bool, bool, string?, string?, string?)"/>).
    /// </summary>
    private static string? FieldAirport(AircraftState aircraft) =>
        FieldAirport(
            aircraft.IsOnGround,
            IsLanding(
                aircraft.Phases?.CurrentPhase is LandingPhase,
                aircraft.Phases?.LandingClearance is not null,
                aircraft.Pattern.PendingLandingClearance is not null
            ),
            aircraft.Phases?.AssignedRunway?.AirportId,
            aircraft.AirportId,
            aircraft.FlightPlan.Destination
        );

    /// <summary>
    /// The field airport of an aircraft: on the ground, the assigned runway's airport, else its own (the field it spawned
    /// at or last touched down on); landing, the assigned runway's airport, else its filed destination. Null for any other
    /// airborne aircraft, and when none of those is known. Live and restored aircraft read it alike.
    /// </summary>
    internal static string? FieldAirport(bool isOnGround, bool isLanding, string? runwayAirportId, string? airportId, string? destination)
    {
        if ((isOnGround || isLanding) && !string.IsNullOrEmpty(runwayAirportId))
        {
            return runwayAirportId;
        }

        if (isOnGround)
        {
            return string.IsNullOrEmpty(airportId) ? null : airportId;
        }

        return (isLanding && !string.IsNullOrEmpty(destination)) ? destination : null;
    }

    /// <summary>
    /// Landing: in its landing phase, or holding a landing clearance (a pattern's pending one included). The one predicate
    /// live and restored aircraft are read by.
    /// </summary>
    internal static bool IsLanding(bool inLandingPhase, bool holdsLandingClearance, bool holdsPendingLandingClearance) =>
        inLandingPhase || holdsLandingClearance || holdsPendingLandingClearance;

    // --- Private tick methods ---
}
