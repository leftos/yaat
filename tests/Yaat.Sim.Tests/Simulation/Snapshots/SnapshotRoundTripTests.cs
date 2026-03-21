using System.Text.Json;
using Xunit;
using Yaat.Sim;
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
