using System.Text.Json;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

public class SnapshotRoundTripTests
{
    [Fact]
    public void AircraftState_ToSnapshot_FromSnapshot_RoundTrips()
    {
        var ac = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738/L",
            Cid = "123",
            Latitude = 37.7213,
            Longitude = -122.2208,
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(268),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            VerticalSpeed = -1000,
            BankAngle = 5.2,
            Declination = 13.5,
            Departure = "KOAK",
            Destination = "KLAX",
            Route = "OAK6 OAK",
            FlightRules = "IFR",
            HasFlightPlan = true,
            CruiseAltitude = 35000,
            CruiseSpeed = 250,
            BeaconCode = 1234,
            AssignedBeaconCode = 1234,
            TransponderMode = "C",
            Owner = TrackOwner.CreateStars("OA1", "OAK", 10, "1R"),
            Scratchpad1 = "OA1",
            OnHandoff = true,
            SidViaMode = true,
            ActiveSidId = "OAK6",
            VoiceType = 1,
        };

        ac.WindComponents = (5.0, -3.0);
        ac.Targets.TargetAltitude = 10000;
        ac.Targets.TargetSpeed = 250;
        ac.Targets.AssignedAltitude = 10000;
        ac.Targets.TargetTrueHeading = new TrueHeading(270);

        var dto = ac.ToSnapshot();
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(ac.Callsign, restored.Callsign);
        Assert.Equal(ac.AircraftType, restored.AircraftType);
        Assert.Equal(ac.Latitude, restored.Latitude, 10);
        Assert.Equal(ac.Longitude, restored.Longitude, 10);
        Assert.Equal(ac.TrueHeading.Degrees, restored.TrueHeading.Degrees, 10);
        Assert.Equal(ac.Altitude, restored.Altitude, 4);
        Assert.Equal(ac.IndicatedAirspeed, restored.IndicatedAirspeed, 4);
        Assert.Equal(ac.VerticalSpeed, restored.VerticalSpeed, 4);
        Assert.Equal(ac.BankAngle, restored.BankAngle, 4);
        Assert.Equal(ac.WindComponents.N, restored.WindComponents.N, 4);
        Assert.Equal(ac.WindComponents.E, restored.WindComponents.E, 4);
        Assert.Equal(ac.Departure, restored.Departure);
        Assert.Equal(ac.Destination, restored.Destination);
        Assert.Equal(ac.Owner?.Callsign, restored.Owner?.Callsign);
        Assert.Equal(ac.Scratchpad1, restored.Scratchpad1);
        Assert.Equal(ac.OnHandoff, restored.OnHandoff);
        Assert.Equal(ac.SidViaMode, restored.SidViaMode);
        Assert.Equal(ac.ActiveSidId, restored.ActiveSidId);

        Assert.Equal(ac.Targets.TargetAltitude, restored.Targets.TargetAltitude);
        Assert.Equal(ac.Targets.TargetSpeed, restored.Targets.TargetSpeed);
        Assert.Equal(ac.Targets.AssignedAltitude, restored.Targets.AssignedAltitude);
        Assert.Equal(ac.Targets.TargetTrueHeading?.Degrees, restored.Targets.TargetTrueHeading?.Degrees);
    }

    [Fact]
    public void StateSnapshotDto_JsonRoundTrips()
    {
        var snapshot = new StateSnapshotDto
        {
            ElapsedSeconds = 60,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft =
            [
                new AircraftSnapshotDto
                {
                    Callsign = "AAL100",
                    AircraftType = "B738",
                    Cid = "1",
                    Latitude = 37.0,
                    Longitude = -122.0,
                    TrueHeadingDeg = 270,
                    TrueTrackDeg = 270,
                    Declination = 13,
                    Altitude = 5000,
                    IndicatedAirspeed = 250,
                    VerticalSpeed = 0,
                    BankAngle = 0,
                    WindN = 0,
                    WindE = 0,
                    HasFlightPlan = true,
                    Departure = "KOAK",
                    Destination = "KLAX",
                    Route = "",
                    Remarks = "",
                    EquipmentSuffix = "L",
                    FlightRules = "IFR",
                    CruiseAltitude = 35000,
                    CruiseSpeed = 250,
                    TransponderMode = "C",
                    AssignedBeaconCode = 1234,
                    BeaconCode = 1234,
                    IsIdenting = false,
                    IsOnGround = false,
                    IsHeld = false,
                    AutoDeleteExempt = false,
                    ConflictBreakRemainingSeconds = 0,
                    WasScratchpad1Cleared = false,
                    IsAnnotated = false,
                    OnHandoff = false,
                    HandoffAccepted = false,
                    SidViaMode = false,
                    StarViaMode = false,
                    SpeedRestrictionsDeleted = false,
                    IsExpediting = false,
                    HasReportedFieldInSight = false,
                    HasReportedTrafficInSight = false,
                    VoiceType = 1,
                    TdlsDumped = false,
                    HoldAnnotationDirection = 0,
                    HoldAnnotationTurns = 0,
                    HoldAnnotationLegLengthInNm = false,
                    HoldAnnotationEfc = 0,
                    IsUnsupported = false,
                    IsCaInhibited = false,
                    IsModeCInhibited = false,
                    IsMsawInhibited = false,
                    IsDuplicateBeaconInhibited = false,
                    Targets = new ControlTargetsDto { HasExplicitSpeedCommand = false },
                    Queue = new CommandQueueDto { Blocks = [], CurrentBlockIndex = 0 },
                },
            ],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 42,
                ElapsedSeconds = 60,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var json = JsonSerializer.Serialize(snapshot, options);
        var deserialized = JsonSerializer.Deserialize<StateSnapshotDto>(json, options);

        Assert.NotNull(deserialized);
        Assert.Equal(snapshot.ElapsedSeconds, deserialized.ElapsedSeconds);
        Assert.Equal(snapshot.Rng.S0, deserialized.Rng.S0);
        Assert.Single(deserialized.Aircraft);
        Assert.Equal("AAL100", deserialized.Aircraft[0].Callsign);
        Assert.Equal(snapshot.SchemaVersion, deserialized.SchemaVersion);
    }

    [Fact]
    public void SnapshotSchemaMigrator_CurrentVersion_NoOp()
    {
        var snapshot = new StateSnapshotDto
        {
            SchemaVersion = SnapshotSchemaMigrator.CurrentSchemaVersion,
            ElapsedSeconds = 0,
            Rng = new RngState(0, 0, 0, 0),
            Aircraft = [],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "",
                ScenarioName = "",
                RngSeed = 0,
                ElapsedSeconds = 0,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };

        SnapshotSchemaMigrator.Migrate(snapshot);
        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
    }

    [Fact]
    public void SnapshotSchemaMigrator_FutureVersion_Throws()
    {
        var snapshot = new StateSnapshotDto
        {
            SchemaVersion = SnapshotSchemaMigrator.CurrentSchemaVersion + 1,
            ElapsedSeconds = 0,
            Rng = new RngState(0, 0, 0, 0),
            Aircraft = [],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "",
                ScenarioName = "",
                RngSeed = 0,
                ElapsedSeconds = 0,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };

        Assert.Throws<SnapshotSchemaException>(() => SnapshotSchemaMigrator.Migrate(snapshot));
    }

    [Fact]
    public void SnapshotSchemaMigrator_V1ToV2_Succeeds()
    {
        var snapshot = new StateSnapshotDto
        {
            SchemaVersion = 1,
            ElapsedSeconds = 30,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 42,
                ElapsedSeconds = 30,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(2, snapshot.SchemaVersion);
        Assert.Null(snapshot.Server);
    }

    [Fact]
    public void ServerSnapshotDto_JsonRoundTrips()
    {
        var snapshot = new StateSnapshotDto
        {
            ElapsedSeconds = 60,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 42,
                ElapsedSeconds = 60,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
            Server = new ServerSnapshotDto
            {
                ConsolidationOverrides = new Dictionary<string, ConsolidationOverrideDto>
                {
                    ["12A"] = new ConsolidationOverrideDto { ReceivingTcpId = "12B", IsBasic = true },
                },
                ActiveConflicts =
                [
                    new ActiveConflictDto
                    {
                        Id = "c1",
                        CallsignA = "AAL100",
                        CallsignB = "UAL200",
                        IsAcknowledged = false,
                    },
                ],
                BeaconCodePool = new BeaconCodePoolDto
                {
                    AssignedCodes = new Dictionary<uint, string> { [1234] = "AAL100", [5670] = "UAL200" },
                },
            },
        };

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var json = JsonSerializer.Serialize(snapshot, options);
        var deserialized = JsonSerializer.Deserialize<StateSnapshotDto>(json, options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Server);
        Assert.Single(deserialized.Server.ConsolidationOverrides!);
        Assert.Equal("12B", deserialized.Server.ConsolidationOverrides!["12A"].ReceivingTcpId);
        Assert.True(deserialized.Server.ConsolidationOverrides!["12A"].IsBasic);
        Assert.Single(deserialized.Server.ActiveConflicts!);
        Assert.Equal("AAL100", deserialized.Server.ActiveConflicts![0].CallsignA);
        Assert.Equal(2, deserialized.Server.BeaconCodePool!.AssignedCodes!.Count);
    }

    [Fact]
    public void ConsolidationState_Restore_RoundTrips()
    {
        var state = new ConsolidationState();
        var tcp1 = new Tcp(10, "1R", "12A", null);
        var tcp2 = new Tcp(10, "2R", "12B", null);

        state.Consolidate(tcp2, tcp1, true);

        var snapshot = state.GetSnapshot();
        Assert.Single(snapshot);

        var fresh = new ConsolidationState();
        fresh.Restore(snapshot);

        var restored = fresh.GetSnapshot();
        Assert.Single(restored);
        Assert.Equal("12B", restored["12A"].ReceivingTcpId);
        Assert.True(restored["12A"].IsBasic);
    }

    [Fact]
    public void ConsolidationState_Restore_ClearsPreviousState()
    {
        var state = new ConsolidationState();
        var tcp1 = new Tcp(10, "1R", "12A", null);
        var tcp2 = new Tcp(10, "2R", "12B", null);
        var tcp3 = new Tcp(10, "3R", "12C", null);

        state.Consolidate(tcp2, tcp1, false);
        state.Consolidate(tcp3, tcp2, true);

        Assert.Equal(2, state.GetSnapshot().Count);

        var newOverrides = new Dictionary<string, ConsolidationState.ManualOverride> { ["12C"] = new("12A", false) };
        state.Restore(newOverrides);

        var restored = state.GetSnapshot();
        Assert.Single(restored);
        Assert.Equal("12A", restored["12C"].ReceivingTcpId);
    }

    [Fact]
    public void BeaconCodePool_Clear_ResetsAssignments()
    {
        var pool = new BeaconCodePool();
        pool.MarkUsed(1234);
        pool.MarkUsed(5670);

        pool.Clear();

        // After clear, previously used codes should be assignable again
        pool.MarkUsed(1234);
        var code = pool.AssignNextCode(false);
        // Sequential starts at 0001 after clear, should get 0001 (1234 is re-marked)
        Assert.Equal((uint)0001, code);
    }

    [Fact]
    public void BeaconCodePool_Clear_PreservesBankConfig()
    {
        var banks = new List<BeaconCodeBankConfig>
        {
            new()
            {
                Start = 100,
                End = 107,
                Type = "Ifr",
            },
        };
        var pool = new BeaconCodePool(banks);
        pool.MarkUsed(100);

        pool.Clear();

        // After clear, bank config should still be active (assigns from bank, not sequential)
        var code = pool.AssignNextCode(false);
        Assert.Equal((uint)100, code);
    }

    [Fact]
    public void ScenarioSnapshotDto_DelayedQueue_JsonRoundTrips()
    {
        var snapshot = new StateSnapshotDto
        {
            ElapsedSeconds = 120,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 42,
                ElapsedSeconds = 120,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
                DelayedQueue =
                [
                    new DelayedSpawnDto { AircraftJson = """{"State":{"Callsign":"N2BP"},"SpawnDelaySeconds":300}""", SpawnAtSeconds = 300 },
                    new DelayedSpawnDto { AircraftJson = """{"State":{"Callsign":"N152SP"},"SpawnDelaySeconds":600}""", SpawnAtSeconds = 600 },
                ],
            },
        };

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var json = JsonSerializer.Serialize(snapshot, options);
        var deserialized = JsonSerializer.Deserialize<StateSnapshotDto>(json, options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Scenario.DelayedQueue);
        Assert.Equal(2, deserialized.Scenario.DelayedQueue.Count);
        Assert.Equal(300, deserialized.Scenario.DelayedQueue[0].SpawnAtSeconds);
        Assert.Contains("N2BP", deserialized.Scenario.DelayedQueue[0].AircraftJson);
        Assert.Equal(600, deserialized.Scenario.DelayedQueue[1].SpawnAtSeconds);
        Assert.Contains("N152SP", deserialized.Scenario.DelayedQueue[1].AircraftJson);
    }

    [Fact]
    public void ScenarioSnapshotDto_Generators_JsonRoundTrips()
    {
        var snapshot = new StateSnapshotDto
        {
            ElapsedSeconds = 60,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 42,
                ElapsedSeconds = 60,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
                Generators =
                [
                    new GeneratorStateDto
                    {
                        ConfigJson = """{"Id":"gen1","Runway":"28R"}""",
                        Runway = new RunwayInfoDto
                        {
                            AirportId = "KOAK",
                            End1 = "28R",
                            End2 = "10L",
                            Designator = "28R",
                            Lat1 = 37.72,
                            Lon1 = -122.22,
                            Elevation1Ft = 9,
                            TrueHeading1Deg = 280,
                            Lat2 = 37.73,
                            Lon2 = -122.23,
                            Elevation2Ft = 9,
                            TrueHeading2Deg = 100,
                            LengthFt = 10000,
                            WidthFt = 150,
                        },
                        NextSpawnSeconds = 180.5,
                        NextSpawnDistance = 8.0,
                        IsExhausted = false,
                    },
                ],
            },
        };

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var json = JsonSerializer.Serialize(snapshot, options);
        var deserialized = JsonSerializer.Deserialize<StateSnapshotDto>(json, options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Scenario.Generators);
        Assert.Single(deserialized.Scenario.Generators);
        var gen = deserialized.Scenario.Generators[0];
        Assert.Contains("gen1", gen.ConfigJson);
        Assert.Equal("KOAK", gen.Runway.AirportId);
        Assert.Equal("28R", gen.Runway.Designator);
        Assert.Equal(180.5, gen.NextSpawnSeconds);
        Assert.Equal(8.0, gen.NextSpawnDistance);
        Assert.False(gen.IsExhausted);
    }

    [Fact]
    public void SessionRecording_V1_HasNoSnapshots()
    {
        var json = """
            {
                "ScenarioJson": "{}",
                "RngSeed": 42,
                "Actions": [],
                "TotalElapsedSeconds": 0
            }
            """;

        var recording = JsonSerializer.Deserialize<SessionRecording>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(recording);
        Assert.Equal(1, recording.Version);
        Assert.False(recording.HasSnapshots);
    }

    [Fact]
    public void SessionRecording_V2_HasSnapshots()
    {
        var recording = new SessionRecording
        {
            Version = 2,
            ScenarioJson = "{}",
            RngSeed = 42,
            Actions = [],
            TotalElapsedSeconds = 60,
            Snapshots =
            [
                new TimedSnapshot
                {
                    ElapsedSeconds = 0,
                    ActionIndex = -1,
                    State = new StateSnapshotDto
                    {
                        ElapsedSeconds = 0,
                        Rng = new RngState(0, 0, 0, 0),
                        Aircraft = [],
                        Scenario = new ScenarioSnapshotDto
                        {
                            ScenarioId = "",
                            ScenarioName = "",
                            RngSeed = 42,
                            ElapsedSeconds = 0,
                            AutoClearedToLand = false,
                            AutoCrossRunway = false,
                            ValidateDctFixes = true,
                            IsPaused = false,
                            SimRate = 1,
                            AutoAcceptDelaySeconds = 5,
                            IsStudentTowerPosition = false,
                        },
                    },
                },
            ],
        };

        Assert.True(recording.HasSnapshots);
        Assert.Equal(2, recording.Version);
    }
}
