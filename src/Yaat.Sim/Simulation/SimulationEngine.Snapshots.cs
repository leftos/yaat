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

// Snapshot capture and restore, for both the sim state and the server's slice of it.
public sealed partial class SimulationEngine
{
    public StateSnapshotDto CaptureSnapshot(int actionIndex)
    {
        var scenario = Scenario ?? throw new InvalidOperationException("No scenario loaded.");
        var aircraft = World.GetSnapshot();

        return new StateSnapshotDto
        {
            ElapsedSeconds = scenario.ElapsedSeconds,
            Rng = World.Rng.GetState(),
            WeatherJson = World.Weather is not null ? JsonSerializer.Serialize(World.Weather) : null,
            Aircraft = aircraft.Select(ac => ac.ToSnapshot()).ToList(),
            Scenario = scenario.ToSnapshot(),
            Server = CaptureServerSnapshot(aircraft),
        };
    }

    public void RestoreFromSnapshot(StateSnapshotDto snapshot)
    {
        SnapshotSchemaMigrator.Migrate(snapshot);

        World.Clear();
        World.Rng = new SerializableRandom(snapshot.Rng.S0, snapshot.Rng.S1, snapshot.Rng.S2, snapshot.Rng.S3);
        World.Weather = snapshot.WeatherJson is not null ? JsonSerializer.Deserialize<WeatherProfile>(snapshot.WeatherJson) : null;

        var scenarioDto = snapshot.Scenario;

        // Resolve ground layout for the primary airport
        AirportGroundLayout? groundLayout = null;
        if (scenarioDto.PrimaryAirportId is not null)
        {
            groundLayout = _groundData.GetLayout(scenarioDto.PrimaryAirportId);
            World.GroundLayout = groundLayout;
        }

        foreach (var acDto in snapshot.Aircraft)
        {
            var ac = AircraftState.FromSnapshot(acDto, groundLayout);
            World.AddAircraft(ac);
        }

        // Restore scenario state — we need the original scenario JSON from the existing Scenario
        // (it's not in the snapshot DTO since it's immutable). The caller must ensure Scenario
        // is pre-populated with the original scenario metadata before calling RestoreFromSnapshot.
        if (Scenario is not null)
        {
            Scenario.ElapsedSeconds = scenarioDto.ElapsedSeconds;
            if (scenarioDto.MagneticModelDateUtc is { } magneticModelDateUtc)
            {
                Scenario.MagneticModelDateUtc = magneticModelDateUtc;
            }

            Scenario.AutoClearedToLand = scenarioDto.AutoClearedToLand;
            Scenario.AutoCrossRunway = scenarioDto.AutoCrossRunway;
            Scenario.AutoPullUpToParallel = scenarioDto.AutoPullUpToParallel;
            Scenario.AutoGoAroundOnOccupiedRunway = scenarioDto.AutoGoAroundOnOccupiedRunway;
            Scenario.AutoRejectTakeoffOnOccupiedRunway = scenarioDto.AutoRejectTakeoffOnOccupiedRunway;
            Scenario.LiveTrafficEnabled = scenarioDto.LiveTrafficEnabled;
            Scenario.LiveTrafficCeilingFt = scenarioDto.LiveTrafficCeilingFt;
            Scenario.LiveTrafficFilter = scenarioDto.LiveTrafficFilter;
            Scenario.ValidateDctFixes = scenarioDto.ValidateDctFixes;
            Scenario.SoloTrainingMode = scenarioDto.SoloTrainingMode;
            Scenario.SoloParkingInitialCallupRatePercent = scenarioDto.SoloParkingInitialCallupRatePercent;
            Scenario.SoloArrivalGeneratorRatePercent = scenarioDto.SoloArrivalGeneratorRatePercent;
            Scenario.SoloGoAroundProbabilityPercent = ScenarioPacing.ClampGoAroundProbabilityPercent(scenarioDto.SoloGoAroundProbabilityPercent);
            Scenario.FinalApproachSpeedVarietyEnabled = scenarioDto.FinalApproachSpeedVarietyEnabled;
            Scenario.HasSoloParkingInitialCallupSource = scenarioDto.HasSoloParkingInitialCallupSource;
            Scenario.HasSoloArrivalGeneratorSource = scenarioDto.HasSoloArrivalGeneratorSource;
            Scenario.NextSoloParkingInitialCallupSlotSeconds = scenarioDto.NextSoloParkingInitialCallupSlotSeconds;
            Scenario.RpoShowPilotSpeech = scenarioDto.RpoShowPilotSpeech;
            Scenario.MetarReissuanceEnabled = scenarioDto.MetarReissuanceEnabled;
            Scenario.WeatherSourceJson = scenarioDto.WeatherSourceJson;
            // The config is state; the service (brains, staffing, sink) is the host's and is built from the config the host
            // enabled. A host that lets the enabled positions change mid-session must rebuild the service alongside.
            Scenario.ControllerAi = scenarioDto.ControllerAi is { } controllerAi ? ControllerAiConfig.FromSnapshot(controllerAi) : null;
            Scenario.AiAnomalies.Clear();
            ControllerAi?.Reset();

            // Rebuild the forward-evolving weather timeline from the persisted source — otherwise it
            // is lost on a snapshot-based rewind and the weather freezes. World.Weather keeps the
            // snapshot's collapsed profile (restored above) as the authoritative current state.
            Scenario.WeatherTimeline = null;
            if (scenarioDto.WeatherSourceJson is { } weatherSourceJson)
            {
                var weatherParse = WeatherTimelineParser.Parse(weatherSourceJson);
                if (weatherParse.IsTimeline)
                {
                    Scenario.WeatherTimeline = weatherParse.Timeline;
                }
            }

            Scenario.IsPaused = scenarioDto.IsPaused;
            Scenario.SimRate = scenarioDto.SimRate;
            Scenario.CommandRunDelayMinSeconds = scenarioDto.CommandRunDelayMinSeconds;
            Scenario.CommandRunDelayMaxSeconds = scenarioDto.CommandRunDelayMaxSeconds;
            Scenario.AutoAcceptDelay = TimeSpan.FromSeconds(scenarioDto.AutoAcceptDelaySeconds);
            Scenario.IsStudentTowerPosition = scenarioDto.IsStudentTowerPosition;
            Scenario.ScenarioAutoDeleteMode = scenarioDto.ScenarioAutoDeleteMode;
            Scenario.ClientAutoDeleteOverride = scenarioDto.ClientAutoDeleteOverride;
            Scenario.HasOngoingTrafficSource = scenarioDto.HasOngoingTrafficSource;
            Scenario.StudentPosition = scenarioDto.StudentPosition is not null ? TrackOwner.FromSnapshot(scenarioDto.StudentPosition) : null;
            Scenario.StudentTcp = scenarioDto.StudentTcp is not null ? Tcp.FromSnapshot(scenarioDto.StudentTcp) : null;
            World.StudentTcp = Scenario.StudentTcp;
            Scenario.StudentPositionType = scenarioDto.StudentPositionType;

            // Clear and restore queues
            Scenario.DelayedQueue.Clear();
            Scenario.TriggerQueue.Clear();
            Scenario.PresetQueue.Clear();
            Scenario.DelayedHandoffQueue.Clear();
            Scenario.Generators.Clear();
            Scenario.HeldDepartureAirports.Clear();
            Scenario.ReleaseQueue.Clear();
            Scenario.ActiveTimers.Clear();

            if (scenarioDto.DelayedQueue is not null)
            {
                foreach (var d in scenarioDto.DelayedQueue)
                {
                    var aircraft = JsonSerializer.Deserialize<LoadedAircraft>(d.AircraftJson)!;
                    // Reattach ground layout — excluded from JSON by [JsonIgnore], resolve by airport ID
                    if (aircraft.State.Ground.LayoutAirportId is { } layoutAirportId)
                    {
                        aircraft.State.Ground.Layout = _groundData.GetLayout(layoutAirportId);
                    }

                    Scenario.DelayedQueue.Add(
                        new DelayedSpawn
                        {
                            Aircraft = aircraft,
                            SpawnAtSeconds = d.SpawnAtSeconds,
                            HeldForRelease = d.HeldForRelease,
                        }
                    );
                }
            }

            if (scenarioDto.HeldDepartureAirports is not null)
            {
                foreach (var airport in scenarioDto.HeldDepartureAirports)
                {
                    Scenario.HeldDepartureAirports.Add(airport);
                }
            }

            if (scenarioDto.ReleaseQueue is not null)
            {
                foreach (var r in scenarioDto.ReleaseQueue)
                {
                    Scenario.ReleaseQueue.Add(
                        new ScheduledRelease
                        {
                            Airport = r.Airport,
                            Callsign = r.Callsign,
                            FireAtSeconds = r.FireAtSeconds,
                        }
                    );
                }
            }

            Scenario.NextTimerId = scenarioDto.NextTimerId;
            if (scenarioDto.ActiveTimers is not null)
            {
                foreach (var t in scenarioDto.ActiveTimers)
                {
                    Scenario.ActiveTimers.Add(
                        new ActiveTimer
                        {
                            Id = t.Id,
                            Callsign = t.Callsign,
                            Message = t.Message,
                            FireAtSeconds = t.FireAtSeconds,
                            TotalSeconds = t.TotalSeconds,
                        }
                    );
                }
            }

            if (scenarioDto.TriggerQueue is not null)
            {
                foreach (var t in scenarioDto.TriggerQueue)
                {
                    Scenario.TriggerQueue.Add(new ScheduledTrigger { Command = t.Command, FireAtSeconds = t.FireAtSeconds });
                }
            }

            if (scenarioDto.PresetQueue is not null)
            {
                foreach (var p in scenarioDto.PresetQueue)
                {
                    Scenario.PresetQueue.Add(
                        new ScheduledPreset
                        {
                            Callsign = p.Callsign,
                            Command = p.Command,
                            FireAtSeconds = p.FireAtSeconds,
                        }
                    );
                }
            }

            if (scenarioDto.DelayedHandoffQueue is not null)
            {
                foreach (var h in scenarioDto.DelayedHandoffQueue)
                {
                    Scenario.DelayedHandoffQueue.Add(
                        new DelayedHandoff
                        {
                            Callsign = h.Callsign,
                            Target = TrackOwner.FromSnapshot(h.Target),
                            FireAtSeconds = h.FireAtSeconds,
                        }
                    );
                }
            }

            if (scenarioDto.Generators is not null)
            {
                foreach (var g in scenarioDto.Generators)
                {
                    var config = JsonSerializer.Deserialize<ScenarioGeneratorConfig>(g.ConfigJson)!;
                    Scenario.Generators.Add(
                        new GeneratorState
                        {
                            Config = config,
                            Runway = RunwayInfo.FromSnapshot(g.Runway),
                            NextSpawnSeconds = g.NextSpawnSeconds,
                            WasActive = g.WasActive,
                        }
                    );
                }
            }

            if (scenarioDto.VfrArrivalGenerators is not null)
            {
                foreach (var g in scenarioDto.VfrArrivalGenerators)
                {
                    Scenario.VfrArrivalGenerators.Add(
                        new VfrArrivalGeneratorState
                        {
                            Config = JsonSerializer.Deserialize<VfrArrivalGeneratorConfig>(g.ConfigJson)!,
                            NextSpawnSeconds = g.NextSpawnSeconds,
                            WasActive = g.WasActive,
                        }
                    );
                }
            }

            if (scenarioDto.OverflightGenerators is not null)
            {
                foreach (var g in scenarioDto.OverflightGenerators)
                {
                    Scenario.OverflightGenerators.Add(
                        new OverflightGeneratorState
                        {
                            Config = JsonSerializer.Deserialize<OverflightGeneratorConfig>(g.ConfigJson)!,
                            NextSpawnSeconds = g.NextSpawnSeconds,
                            WasActive = g.WasActive,
                        }
                    );
                }
            }

            CoordinationChannelSnapshotMapper.RestoreChannels(Scenario.CoordinationChannels, scenarioDto.CoordinationChannels);
        }

        // Reset engine-level state, then restore from snapshot if available
        ConsolidationState.Clear();
        ConflictAlerts.Conflicts.Clear();
        SoloTrainingEvaluator.Reset();
        BeaconCodePool.Clear();

        if (snapshot.Server is not null)
        {
            RestoreServerSnapshot(snapshot.Server);
        }

        // Advance replay cursors to match the restored scenario time. Without this,
        // a subsequent ReplayOneSecond() would treat actions from t=0 onward as
        // still-pending and re-apply them on top of the restored state. Enables
        // the hybrid-replay pattern: Replay(recording, 0) to load the scenario,
        // RestoreFromSnapshot to jump to a saved state, then ReplayOneSecond to
        // step forward from there with cursors already positioned.
        if (Scenario is not null)
        {
            _replay.ReseekAfterRestore((int)Scenario.ElapsedSeconds);
        }
    }

    private ServerSnapshotDto CaptureServerSnapshot(List<AircraftState> aircraft)
    {
        var consolidation = ConsolidationState
            .GetSnapshot()
            .ToDictionary(kv => kv.Key, kv => new ConsolidationOverrideDto { ReceivingTcpId = kv.Value.ReceivingTcpId, IsBasic = kv.Value.IsBasic });

        var conflicts = ConflictAlerts
            .Conflicts.Values.Select(c => new ActiveConflictDto
            {
                Id = c.Id,
                CallsignA = c.CallsignA,
                CallsignB = c.CallsignB,
                IsAcknowledged = c.IsAcknowledged,
            })
            .ToList();

        var beaconCodes = new Dictionary<uint, string>();
        foreach (var ac in aircraft)
        {
            if (ac.Transponder.AssignedCode > 0)
            {
                beaconCodes[ac.Transponder.AssignedCode] = ac.Callsign;
            }
        }

        var eramConflicts = EramConflicts
            .Conflicts.Values.Select(c => new EramActiveConflictDto
            {
                Id = c.Id,
                CallsignA = c.CallsignA,
                CallsignB = c.CallsignB,
                OwnerFacilityA = c.OwnerFacilityA,
                OwnerFacilityB = c.OwnerFacilityB,
            })
            .ToList();

        return new ServerSnapshotDto
        {
            ConsolidationOverrides = consolidation,
            ActiveConflicts = conflicts,
            EramConflicts = eramConflicts,
            BeaconCodePool = new BeaconCodePoolDto
            {
                AssignedCodes = beaconCodes,
                NextCandidate = BeaconCodePool.NextCandidate,
                BankCursors = new Dictionary<int, uint>(BeaconCodePool.BankCursors),
            },
        };
    }

    private void RestoreServerSnapshot(ServerSnapshotDto server)
    {
        if (server.ConsolidationOverrides is not null)
        {
            var overrides = server.ConsolidationOverrides.ToDictionary(
                kv => kv.Key,
                kv => new ConsolidationState.ManualOverride(kv.Value.ReceivingTcpId, kv.Value.IsBasic)
            );
            ConsolidationState.Restore(overrides);
        }

        if (server.ActiveConflicts is not null)
        {
            foreach (var c in server.ActiveConflicts)
            {
                ConflictAlerts.Conflicts[c.Id] = new ActiveConflict
                {
                    Id = c.Id,
                    CallsignA = c.CallsignA,
                    CallsignB = c.CallsignB,
                    IsAcknowledged = c.IsAcknowledged,
                };
            }
        }

        if (server.EramConflicts is not null)
        {
            foreach (var c in server.EramConflicts)
            {
                EramConflicts.Conflicts[c.Id] = new EramActiveConflict
                {
                    Id = c.Id,
                    CallsignA = c.CallsignA,
                    CallsignB = c.CallsignB,
                    OwnerFacilityA = c.OwnerFacilityA,
                    OwnerFacilityB = c.OwnerFacilityB,
                };
            }
        }

        if (server.BeaconCodePool is { } beaconPool)
        {
            if (beaconPool.AssignedCodes is not null)
            {
                foreach (var code in beaconPool.AssignedCodes.Keys)
                {
                    BeaconCodePool.MarkUsed(code);
                }
            }

            BeaconCodePool.RestoreCursors(beaconPool.NextCandidate, beaconPool.BankCursors);
        }
    }

    // --- Scenario loading ---
}
