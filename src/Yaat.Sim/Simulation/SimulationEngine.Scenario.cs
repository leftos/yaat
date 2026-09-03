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

// Scenario load and the ground-layout resolution it depends on.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Loads a scenario into a fresh world. <paramref name="magneticModelDateUtc"/> is the UTC day the magnetic model
    /// is evaluated at for the whole session (<see cref="SimScenarioState.MagneticModelDateUtc"/>): today for a new
    /// session, the recorded date when replaying.
    /// </summary>
    public List<string> LoadScenario(string json, int rngSeed, DateTime magneticModelDateUtc)
    {
        World.Clear();
        World.Rng = new SerializableRandom(rngSeed);
        World.ReactionDelayRng = new SerializableRandom(rngSeed);
        World.ReleaseJitterRng = new SerializableRandom(rngSeed);
        ApproachEvaluator.Reset();
        SoloTrainingEvaluator.Reset();
        BeaconCodePool.Clear();

        var result = ScenarioLoader.Load(json, _groundData, World.Rng, magneticModelDateUtc);

        // No ARTCC config reaches Yaat.Sim, so the pool has no banks here and falls back to sequential
        // codes. The server configures banks from the facility before running its own assignment pass.
        ScenarioLoader.AssignSpawnBeacons(BeaconCodePool, result.AllAircraftStates);

        Scenario = new SimScenarioState
        {
            ScenarioId = ScenarioIdentity.ResolveScenarioId(result.Id, json),
            ScenarioName = result.Name,
            RngSeed = rngSeed,
            MagneticModelDateUtc = magneticModelDateUtc,
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

        // Add immediate aircraft and dispatch their presets
        foreach (var loaded in result.ImmediateAircraft)
        {
            loaded.State.ScenarioId = Scenario.ScenarioId;
            loaded.State.SpawnedAtSeconds = Scenario.ElapsedSeconds;
            World.AddAircraft(loaded.State);
            DispatchPresetCommands(loaded);
        }

        // Queue delayed aircraft
        foreach (var loaded in result.DelayedAircraft)
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
        foreach (var trigger in result.Triggers)
        {
            Scenario.TriggerQueue.Add(new ScheduledTrigger { Command = trigger.Command, FireAtSeconds = trigger.TimeOffset });
        }

        // Initialize generators
        _generatorSpawnLog.Clear();
        foreach (var genConfig in result.Generators)
        {
            var runwayId = genConfig.Runway ?? "";
            var runway = NavigationDatabase.Instance.GetRunway(result.PrimaryAirportId ?? "", runwayId);
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
            var firstSpawnSeconds = (double)genConfig.StartTimeOffset;
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

        foreach (var cfg in result.VfrArrivalGenerators)
        {
            Scenario.VfrArrivalGenerators.Add(new VfrArrivalGeneratorState { Config = cfg, NextSpawnSeconds = StaggeredFirstSpawn(cfg) });
        }

        foreach (var cfg in result.OverflightGenerators)
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

    public AirportGroundLayout? ResolveGroundLayout(AircraftState aircraft)
    {
        // An aircraft physically on the ground taxis on the airport its wheels are on —
        // never on a filed destination. A departure that files a destination but no
        // departure (e.g. a VFR plan created via CRC to KSMF while parked at OAK) would
        // otherwise load the destination's layout and reject every taxiway/parking lookup.
        if (aircraft.IsOnGround)
        {
            var physicalAirport = aircraft.Phases?.AssignedRunway?.AirportId;
            if (string.IsNullOrEmpty(physicalAirport))
            {
                physicalAirport = aircraft.AirportId;
            }

            var physicalLayout = string.IsNullOrEmpty(physicalAirport) ? null : _groundData.GetLayout(physicalAirport);
            if (physicalLayout is not null)
            {
                return physicalLayout;
            }
        }

        var depLayout = string.IsNullOrEmpty(aircraft.FlightPlan.Departure) ? null : _groundData.GetLayout(aircraft.FlightPlan.Departure);
        var destLayout = string.IsNullOrEmpty(aircraft.FlightPlan.Destination) ? null : _groundData.GetLayout(aircraft.FlightPlan.Destination);

        // Cold-call VFR aircraft (pattern work, full-stop requests) frequently file
        // neither departure nor destination. Treat the assigned arrival runway's
        // airport — or the spawn-time operational airport context — as the implicit
        // destination so the ground layout is available for runway exit and taxi
        // after landing, without writing into the flight plan.
        if (depLayout is null && destLayout is null)
        {
            var implicitAirport = aircraft.Phases?.AssignedRunway?.AirportId;
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

        var depNode = depLayout.FindNearestNode(aircraft.Position);
        var destNode = destLayout.FindNearestNode(aircraft.Position);

        double depDist = depNode is not null ? GeoMath.DistanceNm(aircraft.Position, depNode.Position) : double.MaxValue;
        double destDist = destNode is not null ? GeoMath.DistanceNm(aircraft.Position, destNode.Position) : double.MaxValue;

        return destDist < depDist ? destLayout : depLayout;
    }

    // --- Private tick methods ---
}
