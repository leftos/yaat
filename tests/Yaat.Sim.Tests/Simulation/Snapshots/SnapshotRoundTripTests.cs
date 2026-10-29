using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

public class SnapshotRoundTripTests
{
    public SnapshotRoundTripTests()
    {
        // FlightPhysics_UpdateSpeed_NullDesiredDecelRateUsesDefault makes two
        // AircraftPerformance.DecelRate calls inside a single test body. That
        // helper reads the static AircraftProfileDatabase singleton, which is
        // populated by TestVnasData.EnsureInitialized in other test classes'
        // constructors. xUnit runs test classes in parallel; if a sibling class
        // initializes profiles BETWEEN our two DecelRate calls, the first sees
        // the empty-fallback (CategoryPerformance.DecelRate(Jet)=2.0) and the
        // second sees the loaded B738 profile (3.5), causing a flaky failure.
        //
        // Pinning profile state here makes our DecelRate calls observe a single
        // consistent snapshot — either profiles are already loaded (no-op) or
        // we trigger the load and the EnsureInitialized lock holds off siblings
        // until _lookup is populated.
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void AircraftState_ToSnapshot_FromSnapshot_RoundTrips()
    {
        var ac = new AircraftState
        {
            Callsign = "AAL100",
            AircraftType = "B738/L",
            Cid = "123",
            Position = new LatLon(37.7213, -122.2208),
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(268),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            VerticalSpeed = -1000,
            BankAngle = 5.2,
            Declination = 13.5,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KLAX",
                Route = "OAK6 OAK",
                FlightRules = "IFR",
                HasFlightPlan = true,
                Altitude = PlannedAltitude.Ifr(35000),
                CruiseSpeed = 250,
            },
            Transponder = new AircraftTransponder
            {
                Code = 1234,
                AssignedCode = 1234,
                Mode = "C",
            },
            Track = new AircraftTrack { Owner = TrackOwner.CreateStars("OA1", "OAK", 10, "1R"), OnHandoff = true },
            Stars = new AircraftStarsState { Scratchpad1 = "OA1" },
            Procedure = new AircraftProcedure { SidViaMode = true, ActiveSidId = "OAK6" },
            Voice = new AircraftVoice { Type = 1 },
            HasMadeInitialContact = true,
            HasControllerAcknowledgedInitialContact = true,
            IsClearedIntoBravo = true,
            HasAnnouncedLinedUpReady = true,
            PendingPilotRequest = new PilotPendingRequest
            {
                Kind = PilotPendingRequestKind.Approach,
                ResponseState = PilotPendingRequestResponseState.Standby,
                FirstRequestedAtSeconds = 20,
                LastRequestedAtSeconds = 40,
                NextFollowUpDueSeconds = 340,
                LastPilotLine = "approach, ten miles to land runway two eight right.",
                LastPilotLineTts = "approach, american one hundred ten miles to land runway two eight right.",
                RunwayId = "28R",
                FacilityCallName = "approach",
            },
        };
        ac.Ground.InitialCallupDecisionProcessed = true;
        ac.Ground.InitialCallup = InitialCallupPlan.AfterPush;
        ac.Ground.SpawnTaxiway = "K";
        ac.Ground.PushedBackFrom = "F8";
        ac.Ground.PushEndSpot = "5A";
        ac.Ground.PresetTaxiStop = new PresetTaxiStop(PresetTaxiStopKind.TaxiwayHoldShort, "C", "T41W");
        ac.Ground.VfrDepartureDirection = "north";
        ac.Ground.LeftStudentFrequencyAtSeconds = 64.0;

        ac.WindComponents = (5.0, -3.0);
        ac.Targets.TargetAltitude = 10000;
        ac.Targets.TargetSpeed = 250;
        ac.Targets.AssignedAltitude = 10000;
        ac.Targets.TargetTrueHeading = new TrueHeading(270);

        AircraftSnapshotDto dto = ac.ToSnapshot();
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(ac.Callsign, restored.Callsign);
        Assert.Equal(ac.AircraftType, restored.AircraftType);
        Assert.Equal(ac.Position.Lat, restored.Position.Lat, 10);
        Assert.Equal(ac.Position.Lon, restored.Position.Lon, 10);
        Assert.Equal(ac.TrueHeading.Degrees, restored.TrueHeading.Degrees, 10);
        Assert.Equal(ac.Altitude, restored.Altitude, 4);
        Assert.Equal(ac.IndicatedAirspeed, restored.IndicatedAirspeed, 4);
        Assert.Equal(ac.VerticalSpeed, restored.VerticalSpeed, 4);
        Assert.Equal(ac.BankAngle, restored.BankAngle, 4);
        Assert.Equal(ac.WindComponents.N, restored.WindComponents.N, 4);
        Assert.Equal(ac.WindComponents.E, restored.WindComponents.E, 4);
        Assert.Equal(ac.FlightPlan.Departure, restored.FlightPlan.Departure);
        Assert.Equal(ac.FlightPlan.Destination, restored.FlightPlan.Destination);
        Assert.Equal(ac.Track.Owner?.Callsign, restored.Track.Owner?.Callsign);
        Assert.Equal(ac.Stars.Scratchpad1, restored.Stars.Scratchpad1);
        Assert.Equal(ac.Track.OnHandoff, restored.Track.OnHandoff);
        Assert.Equal(ac.Procedure.SidViaMode, restored.Procedure.SidViaMode);
        Assert.Equal(ac.Procedure.ActiveSidId, restored.Procedure.ActiveSidId);

        Assert.Equal(ac.Targets.TargetAltitude, restored.Targets.TargetAltitude);
        Assert.Equal(ac.Targets.TargetSpeed, restored.Targets.TargetSpeed);
        Assert.Equal(ac.Targets.AssignedAltitude, restored.Targets.AssignedAltitude);
        Assert.True(restored.HasMadeInitialContact);
        Assert.True(restored.HasControllerAcknowledgedInitialContact);
        Assert.True(restored.IsClearedIntoBravo);
        Assert.True(restored.HasAnnouncedLinedUpReady);
        Assert.True(restored.Ground.InitialCallupDecisionProcessed);
        Assert.Equal(InitialCallupPlan.AfterPush, restored.Ground.InitialCallup);
        Assert.Equal("K", restored.Ground.SpawnTaxiway);
        Assert.Equal("F8", restored.Ground.PushedBackFrom);
        Assert.Equal("5A", restored.Ground.PushEndSpot);
        Assert.Equal(new PresetTaxiStop(PresetTaxiStopKind.TaxiwayHoldShort, "C", "T41W"), restored.Ground.PresetTaxiStop);
        Assert.Equal("north", restored.Ground.VfrDepartureDirection);
        Assert.Equal(64.0, restored.Ground.LeftStudentFrequencyAtSeconds);
        Assert.NotNull(restored.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Approach, restored.PendingPilotRequest.Kind);
        Assert.Equal(PilotPendingRequestResponseState.Standby, restored.PendingPilotRequest.ResponseState);
        Assert.Equal(340, restored.PendingPilotRequest.NextFollowUpDueSeconds);
        Assert.Equal("approach, ten miles to land runway two eight right.", restored.PendingPilotRequest.LastPilotLine);
        Assert.Equal("approach, american one hundred ten miles to land runway two eight right.", restored.PendingPilotRequest.LastPilotLineTts);
        Assert.Equal("28R", restored.PendingPilotRequest.RunwayId);
        Assert.Equal(ac.Targets.TargetTrueHeading?.Degrees, restored.Targets.TargetTrueHeading?.Degrees);
    }

    /// <summary>The last crossed published speed round-trips both ways: a value after a crossing, and null before any.</summary>
    [Fact]
    public void AircraftProcedure_LastCrossedPublishedSpeed_RoundTrips()
    {
        var crossed = new AircraftProcedure { LastCrossedPublishedSpeedKts = 260 };

        Assert.Equal(260, AircraftProcedure.FromSnapshot(crossed.ToSnapshot()).LastCrossedPublishedSpeedKts);
        Assert.Null(AircraftProcedure.FromSnapshot(new AircraftProcedure().ToSnapshot()).LastCrossedPublishedSpeedKts);
    }

    /// <summary>
    /// A fix's speed stamp round-trips through the recording serializer apart from its crossing stamp, and a published speed
    /// with no stamp comes back unstamped.
    /// </summary>
    [Fact]
    public void NavigationTarget_SpeedTransmissionId_RoundTrips()
    {
        var stamped = new NavigationTarget
        {
            Name = "GRUDG",
            Position = new LatLon(37.6, -121.9),
            SpeedRestriction = new CifpSpeedRestriction(230, CifpSpeedRestrictionType.AtOrBelow),
            CrossingTransmissionId = 3,
            SpeedTransmissionId = 2,
        };
        var published = new NavigationTarget
        {
            Name = "GRUDG",
            Position = new LatLon(37.6, -121.9),
            SpeedRestriction = new CifpSpeedRestriction(260, CifpSpeedRestrictionType.AtOrBelow),
        };

        NavigationTarget restoredStamped = RoundTripThroughJson(stamped);

        Assert.Equal(2, restoredStamped.SpeedTransmissionId);
        Assert.Equal(3, restoredStamped.CrossingTransmissionId);
        Assert.Null(RoundTripThroughJson(published).SpeedTransmissionId);
    }

    /// <summary>
    /// The transition an approach clearance selected round-trips through the recording serializer, and a clearance that
    /// selected none writes no transition and comes back with none.
    /// </summary>
    [Fact]
    public void ApproachClearance_TransitionName_RoundTrips()
    {
        static ApproachClearance Cleared(string? transitionName) =>
            new()
            {
                TransitionName = transitionName,
                ApproachId = "I30",
                AirportCode = "KOAK",
                RunwayId = "30",
                FinalApproachCourse = new TrueHeading(294),
            };

        Assert.Equal("MYSHN", RoundTripThroughJson(Cleared("MYSHN")).TransitionName);
        Assert.Null(RoundTripThroughJson(Cleared(null)).TransitionName);
        Assert.DoesNotContain(
            "TransitionName",
            JsonSerializer.Serialize(Cleared(null).ToSnapshot(), RecordingJsonOptions.Default),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// The towbar heading — from the nose gear out along the towbar to the tug — round-trips both ways: a value while
    /// a tug is attached, and null once it is gone.
    /// </summary>
    [Fact]
    public void AircraftGroundOps_TowbarTrueHeading_RoundTrips()
    {
        var attached = new AircraftGroundOps { TowbarTrueHeading = new TrueHeading(123.5) };

        var restoredAttached = AircraftGroundOps.FromSnapshot(attached.ToSnapshot(), null);

        Assert.Equal(123.5, restoredAttached.TowbarTrueHeading!.Value.Degrees, 9);

        var detached = new AircraftGroundOps { TowbarTrueHeading = null };

        var restoredDetached = AircraftGroundOps.FromSnapshot(detached.ToSnapshot(), null);

        Assert.Null(restoredDetached.TowbarTrueHeading);
    }

    /// <summary>The crossing stop curve carried into the next detector pass round-trips through a snapshot.</summary>
    [Fact]
    public void AircraftGroundOps_CrossingStopCurveCarry_RoundTrips()
    {
        var carried = new AircraftGroundOps { CrossingStopCurveCarry = true };

        string json = JsonSerializer.Serialize(carried.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = AircraftGroundOps.FromSnapshot(JsonSerializer.Deserialize<AircraftGroundOpsDto>(json, RecordingJsonOptions.Default)!, null);

        Assert.True(restored.CrossingStopCurveCarry);
    }

    /// <summary>
    /// With no crossing stop curve carried, the snapshot names no carry at all, so its bytes are those of a snapshot written
    /// before the field existed.
    /// </summary>
    [Fact]
    public void AircraftGroundOps_NoCrossingStopCurveCarry_WritesNoCarryField()
    {
        string json = JsonSerializer.Serialize(new AircraftGroundOps().ToSnapshot(), RecordingJsonOptions.Default);

        Assert.DoesNotContain(nameof(AircraftGroundOps.CrossingStopCurveCarry), json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A snapshot written before the carry existed (no such field) restores with no curve carried.</summary>
    [Fact]
    public void AircraftGroundOpsDto_WithoutCrossingStopCurveCarry_RestoresNoCarry()
    {
        JsonObject json = Assert.IsType<JsonObject>(
            JsonSerializer.SerializeToNode(new AircraftGroundOps { CrossingStopCurveCarry = true }.ToSnapshot(), RecordingJsonOptions.Default)
        );
        string key = Assert
            .Single(json, p => string.Equals(p.Key, nameof(AircraftGroundOps.CrossingStopCurveCarry), StringComparison.OrdinalIgnoreCase))
            .Key;
        json.Remove(key);

        var restored = AircraftGroundOps.FromSnapshot(json.Deserialize<AircraftGroundOpsDto>(RecordingJsonOptions.Default)!, null);

        Assert.False(restored.CrossingStopCurveCarry);
    }

    /// <summary>
    /// The largest closure travel the crossing stop curve counted since its carry began round-trips through a snapshot to the
    /// bit, so a replay releases a held crosser on the same pass.
    /// </summary>
    [Fact]
    public void AircraftGroundOps_CrossingStopCurveBoundClosureTravelFt_RoundTripsBitExact()
    {
        double boundTravelFt = 6.123456789;
        var bound = new AircraftGroundOps { CrossingStopCurveBoundClosureTravelFt = boundTravelFt };

        string json = JsonSerializer.Serialize(bound.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = AircraftGroundOps.FromSnapshot(JsonSerializer.Deserialize<AircraftGroundOpsDto>(json, RecordingJsonOptions.Default)!, null);

        Assert.Equal(BitConverter.DoubleToInt64Bits(boundTravelFt), BitConverter.DoubleToInt64Bits(restored.CrossingStopCurveBoundClosureTravelFt));
    }

    /// <summary>
    /// With no closure travel bound, the snapshot names no bound closure travel at all, so its bytes are those of a snapshot
    /// written before the field existed.
    /// </summary>
    [Fact]
    public void AircraftGroundOps_NoBoundClosureTravel_WritesNoBoundClosureTravelField()
    {
        string json = JsonSerializer.Serialize(new AircraftGroundOps().ToSnapshot(), RecordingJsonOptions.Default);

        Assert.DoesNotContain(nameof(AircraftGroundOps.CrossingStopCurveBoundClosureTravelFt), json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The detector's speed limit and its record that it braked the aircraft in its last pass round-trip through a snapshot, the
    /// limit to the bit, so a replay resumes the next pass from the same state.
    /// </summary>
    [Fact]
    public void AircraftGroundOps_SpeedLimitAndBrakedByDetectorLastPass_RoundTripBitExact()
    {
        double limitKts = Math.Sqrt(81.3);
        var braked = new AircraftGroundOps { SpeedLimit = limitKts, BrakedByDetectorLastPass = true };

        string json = JsonSerializer.Serialize(braked.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = AircraftGroundOps.FromSnapshot(JsonSerializer.Deserialize<AircraftGroundOpsDto>(json, RecordingJsonOptions.Default)!, null);

        double restoredKts = Assert.IsType<double>(restored.SpeedLimit);
        Assert.Equal(BitConverter.DoubleToInt64Bits(limitKts), BitConverter.DoubleToInt64Bits(restoredKts));
        Assert.True(restored.BrakedByDetectorLastPass);
    }

    [Fact]
    public void HoldAnnotation_RawRadialAndBlankInstructions_RoundTripThroughJson()
    {
        var withRadial = new AircraftHoldAnnotation
        {
            Fix = "OAK090010",
            Direction = 4,
            Turns = 1,
            LegLength = 10,
            LegLengthInNm = true,
            Efc = 1230,
            Radial = 93,
        };
        var fixOnly = new AircraftHoldAnnotation { Fix = "SUNOL" };

        AircraftHoldAnnotation restoredRadial = RoundTripThroughJson(withRadial);
        AircraftHoldAnnotation restoredFixOnly = RoundTripThroughJson(fixOnly);

        Assert.Equal(93, restoredRadial.Radial);
        Assert.Equal(4, restoredRadial.Direction);
        Assert.Equal(1, restoredRadial.Turns);
        Assert.Equal("SUNOL", restoredFixOnly.Fix);
        Assert.Null(restoredFixOnly.Direction);
        Assert.Null(restoredFixOnly.Turns);
        Assert.Null(restoredFixOnly.Radial);
    }

    [Theory]
    [InlineData(29, 0, null)] // before V30 an Efc of 0 meant no EFC
    [InlineData(29, 1230, 1230)]
    [InlineData(30, 0, 0)] // from V30 on, 0 is an EFC of 0000
    public void HoldAnnotation_Efc_MigratesAZeroFromBeforeV30ToNoEfc(int schemaVersion, int storedEfc, int? expectedEfc)
    {
        string json =
            "{ \"SchemaVersion\": "
            + schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ", \"ElapsedSeconds\": 10, \"Rng\": { \"S0\": 1, \"S1\": 2, \"S2\": 3, \"S3\": 4 },"
            + " \"Aircraft\": [ { \"Callsign\": \"AAL1\", \"AircraftType\": \"B738\", \"HoldAnnotation\": { \"Fix\": \"OAK\", \"Efc\": "
            + storedEfc.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ", \"LegLengthInNm\": false } } ],"
            + " \"Scenario\": { \"ScenarioId\": \"t\", \"ScenarioName\": \"T\", \"RngSeed\": 1, \"ElapsedSeconds\": 10, \"SimRate\": 1 } }";
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(expectedEfc, AircraftHoldAnnotation.FromSnapshot(Assert.Single(snapshot.Aircraft).HoldAnnotation).Efc);
    }

    [Theory]
    [InlineData(", \"AutoCrossRunway\": true", AutoCrossRunwayMode.All)]
    [InlineData(", \"AutoCrossRunway\": false", AutoCrossRunwayMode.Off)]
    [InlineData("", AutoCrossRunwayMode.Off)]
    public void Scenario_AutoCrossRunway_MigratesToAnAutoCrossRunwayModeFromBeforeV35(string legacyField, AutoCrossRunwayMode expected)
    {
        string json =
            "{ \"SchemaVersion\": 34, \"ElapsedSeconds\": 10, \"Rng\": { \"S0\": 1, \"S1\": 2, \"S2\": 3, \"S3\": 4 }, \"Aircraft\": [],"
            + " \"Scenario\": { \"ScenarioId\": \"t\", \"ScenarioName\": \"T\", \"RngSeed\": 1, \"ElapsedSeconds\": 10, \"SimRate\": 1"
            + legacyField
            + " } }";
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(expected, snapshot.Scenario.AutoCrossRunwayMode);
        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        // The legacy field is read once and dropped, so a rewritten snapshot no longer carries it.
        Assert.DoesNotContain("\"AutoCrossRunway\"", JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InitialCallupPlan.RunwaySayOnly, PilotPendingRequestKind.Clearance)]
    [InlineData(InitialCallupPlan.RunwayNoPreset, PilotPendingRequestKind.Release)]
    public void RunwayPlansAndTheNewRequestKinds_RoundTrip(InitialCallupPlan plan, PilotPendingRequestKind kind)
    {
        var ac = new AircraftState { Callsign = "N513SJ", AircraftType = "C421" };
        ac.Ground.InitialCallup = plan;
        ac.PendingPilotRequest = new PilotPendingRequest
        {
            Kind = kind,
            FirstRequestedAtSeconds = 90,
            LastRequestedAtSeconds = 90,
            NextFollowUpDueSeconds = 210,
            LastPilotLine = "approach, runway 25 at Auburn, ready for departure, request release.",
            LastPilotLineTts = "approach, november five one three sierra juliet, runway two five at Auburn, ready for departure, request release.",
        };

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);

        Assert.Equal(plan, restored.Ground.InitialCallup);
        Assert.Equal(kind, restored.PendingPilotRequest!.Kind);
    }

    [Theory]
    [InlineData(true, false, "AtParking", InitialCallupPlan.None)] // a scripted departure never called
    [InlineData(false, false, "AtParking", InitialCallupPlan.StandCall)] // an open decision at its stand would have made the stand call
    [InlineData(false, true, "AtParking", InitialCallupPlan.None)] // a decided one never calls again
    [InlineData(true, true, "AtParking", InitialCallupPlan.None)]
    [InlineData(false, false, "HoldingInPosition", InitialCallupPlan.None)] // an open decision away from a stand never calls from one
    [InlineData(false, false, null, InitialCallupPlan.None)] // nor does one with no phase (an airborne arrival)
    public void Ground_IsScriptedDeparture_MigratesToAnInitialCallupPlanFromBeforeV33(
        bool scriptedDeparture,
        bool decisionProcessed,
        string? currentPhase,
        InitialCallupPlan expected
    )
    {
        string phases = currentPhase is null
            ? ""
            : ", \"Phases\": { \"CurrentIndex\": 0, \"Phases\": [ { \"$type\": \"" + currentPhase + "\", \"Status\": 1, \"ElapsedSeconds\": 3 } ] }";
        string json =
            "{ \"SchemaVersion\": 32, \"ElapsedSeconds\": 10, \"Rng\": { \"S0\": 1, \"S1\": 2, \"S2\": 3, \"S3\": 4 },"
            + " \"Aircraft\": [ { \"Callsign\": \"AAL1\", \"AircraftType\": \"B738\", \"Ground\": { \"IsHeld\": false, \"AutoDeleteExempt\": true,"
            + " \"ConflictBreakRemainingSeconds\": 0, \"HasAnnouncedReady\": false, \"IsScriptedDeparture\": "
            + (scriptedDeparture ? "true" : "false")
            + ", \"InitialCallupDecisionProcessed\": "
            + (decisionProcessed ? "true" : "false")
            + " }"
            + phases
            + " } ],"
            + " \"Scenario\": { \"ScenarioId\": \"t\", \"ScenarioName\": \"T\", \"RngSeed\": 1, \"ElapsedSeconds\": 10, \"SimRate\": 1 } }";
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        AircraftGroundOpsDto ground = Assert.Single(snapshot.Aircraft).Ground;
        Assert.Equal(expected, ground.InitialCallup);
        Assert.Null(ground.SpawnTaxiway);
        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        // The legacy field is read once and dropped, so a rewritten snapshot no longer carries it.
        Assert.DoesNotContain("IsScriptedDeparture", JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default), StringComparison.Ordinal);
    }

    [Fact]
    public void Ground_InitialCallup_IsNotRederivedFromAV33Snapshot()
    {
        string json =
            "{ \"SchemaVersion\": 33, \"ElapsedSeconds\": 10, \"Rng\": { \"S0\": 1, \"S1\": 2, \"S2\": 3, \"S3\": 4 },"
            + " \"Aircraft\": [ { \"Callsign\": \"AAL1\", \"AircraftType\": \"B738\", \"Ground\": { \"IsHeld\": false, \"AutoDeleteExempt\": true,"
            + " \"ConflictBreakRemainingSeconds\": 0, \"HasAnnouncedReady\": false, \"InitialCallupDecisionProcessed\": false,"
            + " \"InitialCallup\": 2 } } ],"
            + " \"Scenario\": { \"ScenarioId\": \"t\", \"ScenarioName\": \"T\", \"RngSeed\": 1, \"ElapsedSeconds\": 10, \"SimRate\": 1 } }";
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(InitialCallupPlan.AfterPush, Assert.Single(snapshot.Aircraft).Ground.InitialCallup);
    }

    [Fact]
    public void BlockTrigger_V33Radial_MigratesToTrueRadial_AndToIssuedRadialForAnFrdTrigger()
    {
        static string Block(BlockTriggerType type, int radial) =>
            $"{{ \"Trigger\": {{ \"Type\": {(int)type}, \"FixName\": \"OAK\", \"FixLat\": 37.72, \"FixLon\": -122.22, \"Radial\": {radial} }},"
            + " \"Commands\": [], \"IsApplied\": false, \"TriggerMet\": false, \"TriggerClosestApproach\": 0, \"TriggerMissed\": false }";
        string json =
            "{ \"SchemaVersion\": 33, \"ElapsedSeconds\": 10, \"Rng\": { \"S0\": 1, \"S1\": 2, \"S2\": 3, \"S3\": 4 },"
            + " \"Aircraft\": [ { \"Callsign\": \"AAL1\", \"AircraftType\": \"B738\", \"Queue\": { \"CurrentBlockIndex\": 0, \"Blocks\": [ "
            + Block(BlockTriggerType.InterceptRadial, 90)
            + ", "
            + Block(BlockTriggerType.ReachFrdPoint, 120)
            + " ] } } ],"
            + " \"Scenario\": { \"ScenarioId\": \"t\", \"ScenarioName\": \"T\", \"RngSeed\": 1, \"ElapsedSeconds\": 10, \"SimRate\": 1 } }";
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        List<CommandBlockDto> blocks = Assert.Single(snapshot.Aircraft).Queue.Blocks;
        BlockTriggerDto intercept = blocks[0].Trigger!;
        BlockTriggerDto frd = blocks[1].Trigger!;
        Assert.Equal(90, intercept.TrueRadial);
        Assert.Null(intercept.IssuedRadial);
        Assert.Equal(120, frd.TrueRadial);
        Assert.Equal(120, frd.IssuedRadial);
        Assert.Null(intercept.LegacyRadial);
        Assert.Null(frd.LegacyRadial);
        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        // The legacy key is read once and dropped, so a rewritten snapshot no longer carries it.
        Assert.DoesNotContain("\"Radial\"", JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default), StringComparison.Ordinal);
    }

    /// <summary>
    /// Schema 37 splits a fix's speed stamp off its crossing stamp. Through schema 36 the crossing stamp also marked the
    /// fix's speed, so a fix carrying both a crossing stamp and a speed restriction takes that stamp as its speed stamp; a
    /// fix stamped with an altitude only, and a published speed with no stamp, get none. The departure clearance's route
    /// and every route a phase keeps to fly or resume later (an airspace boundary hold's, an initial climb's, a departure
    /// procedure's) migrate the same way as the live route.
    /// </summary>
    [Fact]
    public void NavigationTarget_V36CrossingStamp_MigratesToTheSpeedStampOfAFixWithASpeed()
    {
        string position = JsonSerializer.Serialize(new LatLon(37.6, -121.9), RecordingJsonOptions.Default);
        int atOrBelow = SpeedRestrictionDto.ToTypeCode(CifpSpeedRestrictionType.AtOrBelow);
        int atOrAbove = (int)CifpAltitudeRestrictionType.AtOrAbove;
        string Fix(string name, string restrictions) => $"{{ \"Name\": \"{name}\", \"Position\": {position}, \"IsFlyOver\": false{restrictions} }}";
        string StampedSpeed(int stamp) => $", \"SpeedRestriction\": {{ \"Type\": {atOrBelow}, \"Speed\": 230 }}, \"CrossingTransmissionId\": {stamp}";
        string json =
            "{ \"SchemaVersion\": 36, \"ElapsedSeconds\": 10, \"Rng\": { \"S0\": 1, \"S1\": 2, \"S2\": 3, \"S3\": 4 },"
            + " \"Aircraft\": [ { \"Callsign\": \"AAL1\", \"AircraftType\": \"B738\", \"Targets\": { \"HasExplicitSpeedCommand\": false,"
            + " \"NavigationRoute\": [ "
            + Fix("GRUDG", StampedSpeed(4))
            + ", "
            + Fix("BANND", $", \"AltitudeRestriction\": {{ \"Type\": {atOrAbove}, \"Altitude1\": 11000 }}, \"CrossingTransmissionId\": 4")
            + ", "
            + Fix("FFIST", $", \"SpeedRestriction\": {{ \"Type\": {atOrBelow}, \"Speed\": 200 }}")
            + " ] }, \"Phases\": { \"CurrentIndex\": 0, \"DepartureClearance\": { \"Type\": 0, \"Departure\": { \"$type\": \"Default\" },"
            + " \"DepartureRoute\": [ "
            + Fix("WESLA", StampedSpeed(6))
            + " ] }, \"Phases\": [ { \"$type\": \"AirspaceBoundaryHold\", \"Status\": 1, \"ElapsedSeconds\": 3,"
            + " \"AirspaceClass\": 0, \"Ident\": \"SFO\", \"NameText\": \"SFO\", \"ReferenceLat\": 37.6, \"ReferenceLon\": -122.4,"
            + " \"OrbitDirection\": 1, \"CumulativeTurn\": 0, \"LastHeadingDeg\": 0, \"OriginalRoute\": [ "
            + Fix("GRUDG", StampedSpeed(5))
            + " ] }, { \"$type\": \"InitialClimb\", \"Status\": 0, \"ElapsedSeconds\": 0, \"DepartureRoute\": [ "
            + Fix("PORTE", StampedSpeed(7))
            + " ] }, { \"$type\": \"DepartureProcedure\", \"Status\": 0, \"ElapsedSeconds\": 0, \"Legs\": [], \"PostRoute\": [ "
            + Fix("MCKEY", StampedSpeed(8))
            + " ] } ] } } ],"
            + " \"Scenario\": { \"ScenarioId\": \"t\", \"ScenarioName\": \"T\", \"RngSeed\": 1, \"ElapsedSeconds\": 10, \"SimRate\": 1 } }";
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        AircraftSnapshotDto aircraft = Assert.Single(snapshot.Aircraft);
        List<NavigationTargetDto> route = aircraft.Targets.NavigationRoute!;
        Assert.Equal(4, route[0].SpeedTransmissionId);
        Assert.Null(route[1].SpeedTransmissionId);
        Assert.Null(route[2].SpeedTransmissionId);
        PhaseListDto phases = aircraft.Phases!;
        Assert.Equal(6, Assert.Single(phases.DepartureClearance!.DepartureRoute!).SpeedTransmissionId);
        AirspaceBoundaryHoldPhaseDto hold = Assert.IsType<AirspaceBoundaryHoldPhaseDto>(phases.Phases[0]);
        Assert.Equal(5, Assert.Single(hold.OriginalRoute!).SpeedTransmissionId);
        InitialClimbPhaseDto climb = Assert.IsType<InitialClimbPhaseDto>(phases.Phases[1]);
        Assert.Equal(7, Assert.Single(climb.DepartureRoute!).SpeedTransmissionId);
        DepartureProcedurePhaseDto procedure = Assert.IsType<DepartureProcedurePhaseDto>(phases.Phases[2]);
        Assert.Equal(8, Assert.Single(procedure.PostRoute).SpeedTransmissionId);
        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
    }

    private static NavigationTarget RoundTripThroughJson(NavigationTarget target)
    {
        string json = JsonSerializer.Serialize(target.ToSnapshot(), RecordingJsonOptions.Default);
        return NavigationTarget.FromSnapshot(JsonSerializer.Deserialize<NavigationTargetDto>(json, RecordingJsonOptions.Default)!);
    }

    private static ApproachClearance RoundTripThroughJson(ApproachClearance clearance)
    {
        string json = JsonSerializer.Serialize(clearance.ToSnapshot(), RecordingJsonOptions.Default);
        return ApproachClearance.FromSnapshot(JsonSerializer.Deserialize<ApproachClearanceDto>(json, RecordingJsonOptions.Default)!);
    }

    /// <summary>
    /// The mark on the hold ending a route cut short of a taxiway it never drives survives a snapshot through JSON, and an
    /// unmarked hold writes no field for it.
    /// </summary>
    [Fact]
    public void HoldShortPoint_EndsNoDestinationRoute_RoundTripsThroughJson_AndIsOmittedWhileFalse()
    {
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        var route = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "B",
                    EndsNoDestinationRoute = true,
                },
                new HoldShortPoint
                {
                    NodeId = 2,
                    Reason = HoldShortReason.ExplicitHoldShort,
                    TargetName = "C",
                },
            ],
        };

        string json = JsonSerializer.Serialize(route.ToSnapshot(), RecordingJsonOptions.Default);
        JsonArray holds = Assert.IsType<JsonArray>(JsonNode.Parse(json)?["HoldShortPoints"]);
        Assert.True(holds[0]?["EndsNoDestinationRoute"]?.GetValue<bool>() == true, json);
        Assert.Null(holds[1]?["EndsNoDestinationRoute"]);

        TaxiRoute restored = Assert.IsType<TaxiRoute>(
            TaxiRoute.FromSnapshot(JsonSerializer.Deserialize<TaxiRouteDto>(json, RecordingJsonOptions.Default)!, layout)
        );
        Assert.True(restored.HoldShortPoints[0].EndsNoDestinationRoute, "the end-of-route mark did not survive the snapshot");
        Assert.False(restored.HoldShortPoints[1].EndsNoDestinationRoute, "an unmarked hold restored marked");
    }

    private static AircraftHoldAnnotation RoundTripThroughJson(AircraftHoldAnnotation annotation)
    {
        string json = JsonSerializer.Serialize(annotation.ToSnapshot(), RecordingJsonOptions.Default);
        return AircraftHoldAnnotation.FromSnapshot(JsonSerializer.Deserialize<AircraftHoldAnnotationDto>(json, RecordingJsonOptions.Default)!);
    }

    [Fact]
    public void ControlTargets_DesiredRates_RoundTrip()
    {
        var targets = new ControlTargets
        {
            TargetSpeed = 40.0,
            DesiredDecelRate = 4.5,
            DesiredAccelRate = 0.3,
        };

        ControlTargetsDto dto = targets.ToSnapshot();
        Assert.Equal(4.5, dto.DesiredDecelRate);
        Assert.Equal(0.3, dto.DesiredAccelRate);

        var restored = new ControlTargets();
        ControlTargets.RestoreFrom(dto, restored);

        Assert.Equal(4.5, restored.DesiredDecelRate);
        Assert.Equal(0.3, restored.DesiredAccelRate);
        Assert.Equal(40.0, restored.TargetSpeed);
    }

    [Fact]
    public void ControlTargets_DesiredRates_NullRoundTrip()
    {
        var targets = new ControlTargets { TargetSpeed = 100.0 };

        ControlTargetsDto dto = targets.ToSnapshot();
        Assert.Null(dto.DesiredDecelRate);
        Assert.Null(dto.DesiredAccelRate);

        var restored = new ControlTargets();
        ControlTargets.RestoreFrom(dto, restored);

        Assert.Null(restored.DesiredDecelRate);
        Assert.Null(restored.DesiredAccelRate);
    }

    /// <summary>
    /// The SPDN flag survives JSON when set, is not written when clear (so a snapshot without it hashes as before), and
    /// a snapshot without the member restores it clear.
    /// </summary>
    [Fact]
    public void ControlTargets_SpeedLimitsLifted_RoundTrips()
    {
        string liftedJson = JsonSerializer.Serialize(new ControlTargets { SpeedLimitsLifted = true }.ToSnapshot(), RecordingJsonOptions.Default);
        var lifted = new ControlTargets();
        ControlTargets.RestoreFrom(JsonSerializer.Deserialize<ControlTargetsDto>(liftedJson, RecordingJsonOptions.Default)!, lifted);

        Assert.Contains("SpeedLimitsLifted", liftedJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(lifted.SpeedLimitsLifted);

        string clearJson = JsonSerializer.Serialize(new ControlTargets().ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain("SpeedLimitsLifted", clearJson, StringComparison.OrdinalIgnoreCase);

        var restored = new ControlTargets { SpeedLimitsLifted = true };
        ControlTargets.RestoreFrom(JsonSerializer.Deserialize<ControlTargetsDto>(clearJson, RecordingJsonOptions.Default)!, restored);
        Assert.False(restored.SpeedLimitsLifted);
    }

    [Fact]
    public void SituationState_ExitsAhead_TurnOffAngle_RoundTrips()
    {
        var situation = new Yaat.Sim.Situation.AircraftSituationState
        {
            ExitsAhead = [new ExitAheadDto("J", ExitSide.Left, 2800, true, 39), new ExitAheadDto("P", ExitSide.Left, 2500, false, 128)],
        };

        string json = JsonSerializer.Serialize(situation.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSituationStateDto back = JsonSerializer.Deserialize<AircraftSituationStateDto>(json, RecordingJsonOptions.Default)!;
        var restored = Yaat.Sim.Situation.AircraftSituationState.FromSnapshot(back);

        Assert.Equal([39, 128], restored.ExitsAhead!.Select(row => row.TurnOffAngleDeg));
        Assert.Equal(situation.ExitsAhead, restored.ExitsAhead);
    }

    [Fact]
    public void PhaseList_RequestedExit_SideAndTaxiway_RoundTrips()
    {
        var phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(designator: "28", heading: 280),
            RequestedExit = new ExitPreference { Side = ExitSide.Left, Taxiway = "A3" },
        };

        PhaseListDto dto = phases.ToSnapshot();
        string json = JsonSerializer.Serialize(dto, RecordingJsonOptions.Default);
        PhaseListDto back = JsonSerializer.Deserialize<PhaseListDto>(json, RecordingJsonOptions.Default)!;
        var restored = PhaseList.FromSnapshot(back, null);

        Assert.NotNull(restored.RequestedExit);
        Assert.Equal(ExitSide.Left, restored.RequestedExit!.Side);
        Assert.Equal("A3", restored.RequestedExit.Taxiway);
    }

    [Fact]
    public void PhaseList_RequestedExit_TaxiwayOnly_RoundTrips()
    {
        // A bare `EXIT A3` carries a taxiway with no side — the old snapshot dropped it entirely
        // because it only serialized Side.
        var phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(designator: "28", heading: 280),
            RequestedExit = new ExitPreference { Taxiway = "B2" },
        };

        var restored = PhaseList.FromSnapshot(phases.ToSnapshot(), null);

        Assert.NotNull(restored.RequestedExit);
        Assert.Null(restored.RequestedExit!.Side);
        Assert.Equal("B2", restored.RequestedExit.Taxiway);
    }

    /// <summary>
    /// The pattern-altitude change an option modifier deferred to the runway-transition circuit survives a snapshot,
    /// including the runway change's clear (a deferral whose altitude is null), which is not the same as none armed.
    /// </summary>
    [Theory]
    [InlineData(2000.0)]
    [InlineData(null)]
    public void PhaseList_TransitionPatternAltitude_RoundTrips(double? altitudeFt)
    {
        var phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(designator: "28R", heading: 292),
            PatternRunway = TestRunwayFactory.Make(designator: "28L", heading: 292),
            TransitionPatternAltitude = new DeferredPatternAltitude(altitudeFt),
        };

        string json = JsonSerializer.Serialize(phases.ToSnapshot(), RecordingJsonOptions.Default);
        PhaseListDto back = JsonSerializer.Deserialize<PhaseListDto>(json, RecordingJsonOptions.Default)!;
        var restored = PhaseList.FromSnapshot(back, null);

        Assert.Equal(new DeferredPatternAltitude(altitudeFt), restored.TransitionPatternAltitude);
    }

    /// <summary>None armed is not written at all, so a snapshot without one serializes as it did before the field existed.</summary>
    [Fact]
    public void PhaseList_TransitionPatternAltitude_NoneArmed_IsNotWritten()
    {
        var phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(designator: "28", heading: 280) };

        string json = JsonSerializer.Serialize(phases.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = PhaseList.FromSnapshot(JsonSerializer.Deserialize<PhaseListDto>(json, RecordingJsonOptions.Default)!, null);

        Assert.DoesNotContain(nameof(PhaseListDto.TransitionPatternAltitude), json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(restored.TransitionPatternAltitude);
    }

    [Fact]
    public void PhaseList_RequestedExit_Null_RoundTrips()
    {
        var phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(designator: "28", heading: 280) };

        var restored = PhaseList.FromSnapshot(phases.ToSnapshot(), null);

        Assert.Null(restored.RequestedExit);
    }

    [Fact]
    public void FlightPhysics_UpdateSpeed_RespectsDesiredDecelRateOverride()
    {
        // An aircraft decelerating from 100 to 50 kt with a 4.0 kt/s override
        // should lose exactly 4.0 kt in one second (vs the ~2.5 kt/s jet default).
        var ac = new AircraftState
        {
            Callsign = "TEST",
            AircraftType = "B738",
            IndicatedAirspeed = 100.0,
            Altitude = 0,
            IsOnGround = true,
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(90),
        };
        ac.Targets.TargetSpeed = 50.0;
        ac.Targets.DesiredDecelRate = 4.0;

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(96.0, ac.IndicatedAirspeed, 2);
    }

    [Fact]
    public void FlightPhysics_UpdateSpeed_NullDesiredDecelRateUsesDefault()
    {
        // Same setup but with null override: on the ground FlightPhysics falls back to the taxi brake
        // rate, CategoryPerformance.TaxiDecelRate(Jet) = 5.0 kt/s, so one second takes 100 -> 95 kt.
        // (The airborne fallback is AircraftPerformance.DecelRate(B738, Jet); this aircraft is IsOnGround.)
        // The point of the test is unchanged: the null override must resolve to the category taxi
        // braking rate rather than silently reusing the 4.0 kt/s firm-brake override of the sibling test.
        var ac = new AircraftState
        {
            Callsign = "TEST",
            AircraftType = "B738",
            IndicatedAirspeed = 100.0,
            Altitude = 0,
            IsOnGround = true,
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(90),
        };
        ac.Targets.TargetSpeed = 50.0;
        // DesiredDecelRate deliberately left null

        FlightPhysics.Update(ac, 1.0);

        double expectedDefault = 100.0 - CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        Assert.Equal(expectedDefault, ac.IndicatedAirspeed, 2);
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
                    Position = new LatLon(37.0, -122.0),
                    TrueHeadingDeg = 270,
                    TrueTrackDeg = 270,
                    Declination = 13,
                    Altitude = 5000,
                    IndicatedAirspeed = 250,
                    VerticalSpeed = 0,
                    BankAngle = 0,
                    WindN = 0,
                    WindE = 0,
                    FlightPlan = new AircraftFlightPlanDto
                    {
                        HasFlightPlan = true,
                        AircraftType = "B738/L",
                        Departure = "KOAK",
                        Destination = "KLAX",
                        Route = "",
                        InterfacilityRemarks = "",
                        EquipmentSuffix = "L",
                        FlightRules = "IFR",
                        AltitudeCruiseFeet = 35000,
                        CruiseSpeed = 250,
                    },
                    Transponder = new AircraftTransponderDto
                    {
                        Mode = "C",
                        AssignedCode = 1234,
                        Code = 1234,
                        IsIdenting = false,
                    },
                    IsOnGround = false,
                    Ground = new AircraftGroundOpsDto
                    {
                        IsHeld = false,
                        AutoDeleteExempt = false,
                        ConflictBreakRemainingSeconds = 0,
                        HasAnnouncedReady = false,
                        InitialCallupDecisionProcessed = true,
                        InitialCallup = InitialCallupPlan.AfterTaxiArrival,
                        SpawnTaxiway = "K",
                        PushedBackFrom = "G9",
                        PushEndSpot = "5A",
                        PresetTaxiStop = new PresetTaxiStopDto
                        {
                            Kind = PresetTaxiStopKind.TaxiwayHoldShort,
                            Name = "C",
                            OnTaxiway = "T41W",
                        },
                        VfrDepartureDirection = "north",
                        LeftStudentFrequencyAtSeconds = 33.0,
                    },
                    Track = new AircraftTrackDto { OnHandoff = false, HandoffAccepted = false },
                    Stars = new AircraftStarsStateDto
                    {
                        WasScratchpad1Cleared = false,
                        IsAnnotated = false,
                        IsCaInhibited = false,
                        IsModeCInhibited = false,
                        IsMsawInhibited = false,
                        IsDuplicateBeaconInhibited = false,
                        AsdexSuspended = false,
                        AsdexTerminated = false,
                        AsdexAlertsInhibited = false,
                    },
                    Approach = new AircraftApproachStateDto { HasReportedFieldInSight = false, HasReportedTrafficInSight = false },
                    Procedure = new AircraftProcedureDto
                    {
                        SidViaMode = false,
                        StarViaMode = false,
                        SpeedRestrictionsDeleted = false,
                        IsExpediting = false,
                    },
                    Pattern = new AircraftPatternDto(),
                    Voice = new AircraftVoiceDto { Type = 1, TdlsDumped = false },
                    HoldAnnotation = new AircraftHoldAnnotationDto
                    {
                        Direction = 0,
                        Turns = 0,
                        LegLengthInNm = false,
                        Efc = 0,
                    },
                    Eram = new AircraftEramStateDto(),
                    Clearance = new AircraftClearanceDto(),
                    Ghost = new AircraftGhostTrackDto { IsUnsupported = false, IsVehicle = false },
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
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
                ValidateDctFixes = true,
                SoloTrainingMode = true,
                SoloParkingInitialCallupRatePercent = 45,
                SoloArrivalGeneratorRatePercent = 70,
                HasSoloParkingInitialCallupSource = true,
                HasSoloArrivalGeneratorSource = true,
                NextSoloParkingInitialCallupSlotSeconds = 85,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        string json = JsonSerializer.Serialize(snapshot, options);
        StateSnapshotDto? deserialized = JsonSerializer.Deserialize<StateSnapshotDto>(json, options);

        Assert.NotNull(deserialized);
        Assert.Equal(snapshot.ElapsedSeconds, deserialized.ElapsedSeconds);
        Assert.Equal(snapshot.Rng.S0, deserialized.Rng.S0);
        Assert.Single(deserialized.Aircraft);
        Assert.Equal("AAL100", deserialized.Aircraft[0].Callsign);
        Assert.True(deserialized.Aircraft[0].Ground.InitialCallupDecisionProcessed);
        Assert.Equal(InitialCallupPlan.AfterTaxiArrival, deserialized.Aircraft[0].Ground.InitialCallup);
        Assert.Equal("K", deserialized.Aircraft[0].Ground.SpawnTaxiway);
        Assert.Equal("G9", deserialized.Aircraft[0].Ground.PushedBackFrom);
        Assert.Equal("5A", deserialized.Aircraft[0].Ground.PushEndSpot);
        PresetTaxiStopDto stop = Assert.IsType<PresetTaxiStopDto>(deserialized.Aircraft[0].Ground.PresetTaxiStop);
        Assert.Equal(PresetTaxiStopKind.TaxiwayHoldShort, stop.Kind);
        Assert.Equal("C", stop.Name);
        Assert.Equal("T41W", stop.OnTaxiway);
        Assert.Equal("north", deserialized.Aircraft[0].Ground.VfrDepartureDirection);
        Assert.Equal(33.0, deserialized.Aircraft[0].Ground.LeftStudentFrequencyAtSeconds);
        Assert.DoesNotContain("IsScriptedDeparture", json, StringComparison.Ordinal);
        Assert.Equal(45, deserialized.Scenario.SoloParkingInitialCallupRatePercent);
        Assert.Equal(70, deserialized.Scenario.SoloArrivalGeneratorRatePercent);
        Assert.True(deserialized.Scenario.HasSoloParkingInitialCallupSource);
        Assert.True(deserialized.Scenario.HasSoloArrivalGeneratorSource);
        Assert.Equal(85, deserialized.Scenario.NextSoloParkingInitialCallupSlotSeconds);
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
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
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
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
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
    public void SnapshotSchemaMigrator_LegacyV1_MigratesToCurrent()
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
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Null(snapshot.Server);
    }

    [Fact]
    public void SnapshotSchemaMigrator_V3AircraftWithNullFlightPlan_DoesNotThrow()
    {
        // A legacy v3 snapshot whose aircraft JSON omits FlightPlan deserializes (leniently) to a null
        // FlightPlan; the v3→v4 seed must skip it rather than dereference. Regression: #264 hit this on
        // a real .br fixture, and SimulationEngine.RestoreFromSnapshot runs the same migrator on rewind.
        const string json = """
            {
              "SchemaVersion": 3,
              "ElapsedSeconds": 10,
              "Rng": { "S0": 1, "S1": 2, "S2": 3, "S3": 4 },
              "Aircraft": [ { "Callsign": "AAL1", "AircraftType": "B738" } ],
              "Scenario": { "ScenarioId": "t", "ScenarioName": "T", "RngSeed": 1, "ElapsedSeconds": 10, "SimRate": 1 }
            }
            """;
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
        Assert.Null(Assert.Single(snapshot.Aircraft).FlightPlan); // precondition: lenient resolver yields null

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
    }

    [Fact]
    public void SnapshotSchemaMigrator_V23QueryFlash_DropsTheAbsoluteInstant()
    {
        // Through v23 a track's STARS query flash was snapshotted as the absolute instant CRC computed on its
        // own clock; v24 keys it to session time instead. The legacy instant has no mapping onto session time
        // — it belongs to a session that has ended — so it is dropped and the restored track simply does not
        // flash, rather than flashing at an instant long past.
        const string json = """
            {
              "SchemaVersion": 23,
              "ElapsedSeconds": 10,
              "Rng": { "S0": 1, "S1": 2, "S2": 3, "S3": 4 },
              "Aircraft": [
                {
                  "Callsign": "AAL1",
                  "AircraftType": "B738",
                  "Stars": {
                    "SharedState": {
                      "3O": {
                        "ForceFdb": true,
                        "IsHighlighted": false,
                        "LeaderDirection": 6,
                        "IsQueriedUntil": "2026-06-05T12:00:00Z",
                        "WasPreviouslyOwned": true,
                        "TpaType": 0,
                        "TpaSize": 0
                      }
                    }
                  }
                }
              ],
              "Scenario": { "ScenarioId": "t", "ScenarioName": "T", "RngSeed": 1, "ElapsedSeconds": 10, "SimRate": 1 }
            }
            """;
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
        SharedStateDto shared = Assert.Single(Assert.Single(snapshot.Aircraft).Stars.SharedState!).Value;
        Assert.True(shared.ForceFdb); // precondition: the legacy entry itself deserialized

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Null(shared.QueriedUntilElapsedSeconds);
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
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
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
                PositionSelections = new Dictionary<string, TrackOwnerDto>
                {
                    ["conn-1"] = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U").ToSnapshot(),
                },
            },
        };

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        string json = JsonSerializer.Serialize(snapshot, options);
        StateSnapshotDto? deserialized = JsonSerializer.Deserialize<StateSnapshotDto>(json, options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Server);
        Assert.Single(deserialized.Server.ConsolidationOverrides!);
        Assert.Equal("12B", deserialized.Server.ConsolidationOverrides!["12A"].ReceivingTcpId);
        Assert.True(deserialized.Server.ConsolidationOverrides!["12A"].IsBasic);
        Assert.Single(deserialized.Server.ActiveConflicts!);
        Assert.Equal("AAL100", deserialized.Server.ActiveConflicts![0].CallsignA);
        Assert.Equal(2, deserialized.Server.BeaconCodePool!.AssignedCodes!.Count);
        Assert.Equal("SFO_DEP", deserialized.Server.PositionSelections!["conn-1"].Callsign);
        Assert.Equal(4, deserialized.Server.PositionSelections["conn-1"].Subset);
    }

    [Fact]
    public void ConsolidationState_Restore_RoundTrips()
    {
        var state = new ConsolidationState();
        var tcp1 = new Tcp(10, "1R", "12A", null);
        var tcp2 = new Tcp(10, "2R", "12B", null);

        state.Consolidate(tcp2, tcp1, true);

        Dictionary<string, ConsolidationState.ManualOverride> snapshot = state.GetSnapshot();
        Assert.Single(snapshot);

        var fresh = new ConsolidationState();
        fresh.Restore(snapshot);

        Dictionary<string, ConsolidationState.ManualOverride> restored = fresh.GetSnapshot();
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

        Dictionary<string, ConsolidationState.ManualOverride> restored = state.GetSnapshot();
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
        uint code = pool.AssignNextCode(false);
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
        pool.MarkUsed(101);

        pool.Clear();

        // After clear, bank config should still be active (assigns from bank, not sequential). The bank
        // starts at 0100, but that is a non-discrete block code (ends in "00"), so the first assignable
        // code drawn from the bank is 0101.
        uint code = pool.AssignNextCode(false);
        Assert.Equal((uint)101, code);
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
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
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
        string json = JsonSerializer.Serialize(snapshot, options);
        StateSnapshotDto? deserialized = JsonSerializer.Deserialize<StateSnapshotDto>(json, options);

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
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
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
                            WidthFt = 150,
                        },
                        NextSpawnSeconds = 180.5,
                        WasActive = true,
                    },
                ],
                VfrArrivalGenerators =
                [
                    new VfrArrivalGeneratorStateDto
                    {
                        ConfigJson = """{"Id":"vfr1","BearingFrom":150,"BearingTo":210}""",
                        NextSpawnSeconds = 240.0,
                        WasActive = true,
                    },
                ],
                OverflightGenerators =
                [
                    new OverflightGeneratorStateDto
                    {
                        ConfigJson = """{"Id":"of1","ExitDistance":30}""",
                        NextSpawnSeconds = 300.0,
                        WasActive = false,
                    },
                ],
            },
        };

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        string json = JsonSerializer.Serialize(snapshot, options);
        StateSnapshotDto? deserialized = JsonSerializer.Deserialize<StateSnapshotDto>(json, options);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Scenario.Generators);
        Assert.Single(deserialized.Scenario.Generators);
        GeneratorStateDto gen = deserialized.Scenario.Generators[0];
        Assert.Contains("gen1", gen.ConfigJson);
        Assert.Equal("KOAK", gen.Runway.AirportId);
        Assert.Equal("28R", gen.Runway.Designator);
        Assert.Equal(180.5, gen.NextSpawnSeconds);
        Assert.True(gen.WasActive);

        Assert.NotNull(deserialized.Scenario.VfrArrivalGenerators);
        VfrArrivalGeneratorStateDto vfrGen = Assert.Single(deserialized.Scenario.VfrArrivalGenerators);
        Assert.Contains("vfr1", vfrGen.ConfigJson);
        Assert.Equal(240.0, vfrGen.NextSpawnSeconds);
        Assert.True(vfrGen.WasActive);

        Assert.NotNull(deserialized.Scenario.OverflightGenerators);
        OverflightGeneratorStateDto overflightGen = Assert.Single(deserialized.Scenario.OverflightGenerators);
        Assert.Contains("of1", overflightGen.ConfigJson);
        Assert.Equal(300.0, overflightGen.NextSpawnSeconds);
        Assert.False(overflightGen.WasActive);
    }

    [Fact]
    public void SessionRecording_V1_HasNoSnapshots()
    {
        string json = """
            {
                "ScenarioJson": "{}",
                "RngSeed": 42,
                "Actions": [],
                "TotalElapsedSeconds": 0
            }
            """;

        SessionRecording? recording = JsonSerializer.Deserialize<SessionRecording>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

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
                            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
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

    /// <summary>The OAK scenario every engine-level arm here loads; its primary airport is KOAK.</summary>
    private const string ScenarioPath = "TestData/issue153-s2-oak-5-2-scenario.json";

    /// <summary>
    /// A restore puts each aircraft back on the layout it was carrying, not on the primary airport's.
    /// <c>AircraftGroundOps.Layout</c> is <c>[JsonIgnore]</c> and <c>LayoutAirportId</c> is the carrier that exists so
    /// restore can reattach it — the shape <c>SimulationEngine.RestoreFromSnapshot</c> already uses for the delayed
    /// spawn queue. Rebinding everything to the primary instead silently moves an aircraft bound to a second field
    /// onto the wrong airport's runways, and the on-final and distance-to-threshold verdicts measured from them flip
    /// across a rewind — which replay determinism does not allow. An aircraft with no id keeps the primary, which is
    /// the only case that fallback is for.
    /// </summary>
    [Fact]
    public void Restore_RebindsAnAircraftToItsOwnAirportsLayout()
    {
        if (!File.Exists(ScenarioPath))
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        if ((groundData.GetLayout("OAK") is not { } oakLayout) || (groundData.GetLayout("SJC") is not { } sjcLayout))
        {
            return;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is null)
        {
            return;
        }

        Assert.Same(oakLayout, engine.World.GroundLayout); // premise: the primary is OAK, so SJC is the second field
        engine.World.AddAircraft(Bound("SJC900", sjcLayout));
        engine.World.AddAircraft(Bound("OAK900", layout: null));

        engine.RestoreFromSnapshot(engine.CaptureSnapshot());

        AircraftState restoredSatellite = Assert.IsType<AircraftState>(engine.FindAircraft("SJC900"));
        AircraftState restoredPrimary = Assert.IsType<AircraftState>(engine.FindAircraft("OAK900"));
        Assert.Same(sjcLayout, restoredSatellite.Ground.Layout);
        Assert.Equal("SJC", restoredSatellite.Ground.LayoutAirportId);
        Assert.Same(oakLayout, restoredPrimary.Ground.Layout);
    }

    /// <summary>
    /// A rewind resumes in the frequency-load band the run was in. The meter is keyed on sim time, so a restore that
    /// left it empty (or kept the pre-rewind times) would put pilot speech into a band the run never reached — and
    /// a replay after a restore would sound different from the run it replays.
    /// </summary>
    [Fact]
    public void RestoreFromSnapshot_KeepsTheFrequencyLoad()
    {
        if (!File.Exists(ScenarioPath))
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is not { } scenario)
        {
            return;
        }

        double now = scenario.ElapsedSeconds;
        // 45 s ended before the snapshot and one transmission still on air at it: 47 s of the last minute, Busy.
        FrequencyTransmission[] beforeSnapshot = [new(now - 50, 45), new(now - 2, 5)];
        foreach (FrequencyTransmission transmission in beforeSnapshot)
        {
            engine.World.ActiveFrequency.ActivityMeter.Record(transmission.Start, transmission.Duration);
        }

        string json = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);

        engine.World.ActiveFrequency.ActivityMeter.Record(now + 1, 20);

        engine.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!);

        Assert.Equal(beforeSnapshot, engine.World.ActiveFrequency.ActivityMeter.Transmissions);
        Assert.Equal(FrequencyLoadBand.Busy, engine.World.ActiveFrequency.GetLoadBand(now));
    }

    /// <summary>
    /// A V38 snapshot stored only the second each transmission started; each becomes a 3 s transmission from that second,
    /// and an old snapshot without times restores an empty meter.
    /// </summary>
    [Fact]
    public void Migrate_V38FrequencyTransmissionTimes_BecomeThreeSecondTransmissions()
    {
        const string withTimes = """
            {
              "SchemaVersion": 38,
              "ElapsedSeconds": 10,
              "Rng": { "S0": 1, "S1": 2, "S2": 3, "S3": 4 },
              "FrequencyTransmissionTimes": [ 5, 8.5 ],
              "Aircraft": [],
              "Scenario": { "ScenarioId": "t", "ScenarioName": "T", "RngSeed": 1, "ElapsedSeconds": 10, "SimRate": 1 }
            }
            """;
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(withTimes, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(
            [new FrequencyTransmissionDto { Start = 5, Duration = 3 }, new FrequencyTransmissionDto { Start = 8.5, Duration = 3 }],
            snapshot.FrequencyTransmissions
        );
        string rewritten = JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default);
        Assert.DoesNotContain("FrequencyTransmissionTimes", rewritten, StringComparison.Ordinal);

        StateSnapshotDto withoutTimes = JsonSerializer.Deserialize<StateSnapshotDto>(
            withTimes.Replace("\"FrequencyTransmissionTimes\": [ 5, 8.5 ],", "", StringComparison.Ordinal),
            RecordingJsonOptions.Default
        )!;

        SnapshotSchemaMigrator.Migrate(withoutTimes);

        Assert.Null(withoutTimes.FrequencyTransmissions);
    }

    /// <summary>
    /// A V38 snapshot restored end to end: its 15 stored times, 3 s apart across the last 45 s, become back-to-back 3 s
    /// transmissions, 25% dead air, and the run resumes Busy.
    /// </summary>
    [Fact]
    public void RestoreFromSnapshot_V38FrequencyTransmissionTimes_ResumeInTheirBand()
    {
        if (!File.Exists(ScenarioPath))
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is not { } scenario)
        {
            return;
        }

        double now = scenario.ElapsedSeconds;
        double[] times = [.. Enumerable.Range(0, 15).Select(i => now - 45 + (3 * i))];
        JsonObject node = JsonNode.Parse(JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default))!.AsObject();
        node[nameof(StateSnapshotDto.SchemaVersion)] = 38;
        node.Remove(nameof(StateSnapshotDto.FrequencyTransmissions));
        node["FrequencyTransmissionTimes"] = new JsonArray([.. times.Select(t => (JsonNode)t)]);
        StateSnapshotDto v38 = JsonSerializer.Deserialize<StateSnapshotDto>(node.ToJsonString(), RecordingJsonOptions.Default)!;

        engine.RestoreFromSnapshot(v38);

        Assert.Equal(
            [.. times.Select(t => new FrequencyTransmission(t, SnapshotSchemaMigrator.LegacyTransmissionSeconds))],
            engine.World.ActiveFrequency.ActivityMeter.Transmissions
        );
        Assert.Equal(FrequencyLoadBand.Busy, engine.World.ActiveFrequency.GetLoadBand(now));
    }

    /// <summary>
    /// A snapshot written before the meter was captured omits the field entirely; the restore then starts with an
    /// empty meter instead of throwing, and stale times from before a rewind never survive.
    /// </summary>
    [Fact]
    public void RestoreFromSnapshot_WithoutFrequencyTimes_RestoresAnEmptyMeter()
    {
        if (!File.Exists(ScenarioPath))
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is not { } scenario)
        {
            return;
        }

        engine.World.ActiveFrequency.ActivityMeter.Record(scenario.ElapsedSeconds - 25, 25);

        string json = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        JsonObject node = JsonNode.Parse(json)!.AsObject();
        Assert.True(node.ContainsKey(nameof(StateSnapshotDto.FrequencyTransmissions)));
        node.Remove(nameof(StateSnapshotDto.FrequencyTransmissions));

        StateSnapshotDto withoutTimes = JsonSerializer.Deserialize<StateSnapshotDto>(node.ToJsonString(), RecordingJsonOptions.Default)!;

        engine.RestoreFromSnapshot(withoutTimes);

        Assert.Empty(engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void StudentLowestWorkedLevel_RoundTrips_AndIsOmittedWhenUnset()
    {
        if (!File.Exists(ScenarioPath))
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is not { } scenario)
        {
            return;
        }

        scenario.StudentLowestWorkedLevel = AtcPositionLevel.Ground;
        string json = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        scenario.StudentLowestWorkedLevel = null;
        engine.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!);
        Assert.Equal(AtcPositionLevel.Ground, engine.Scenario!.StudentLowestWorkedLevel);
        Assert.Contains("\"StudentLowestWorkedLevel\"", json, StringComparison.Ordinal);

        engine.Scenario.StudentLowestWorkedLevel = null;
        string unset = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain("\"StudentLowestWorkedLevel\"", unset, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A reload starts the frequency quiet: a reload-and-replay from zero must not inherit times recorded later in the
    /// run the load replaces, or the replay's first minute runs in the wrong band.
    /// </summary>
    [Fact]
    public void LoadScenario_ResetsTheFrequencyLoad()
    {
        if (!File.Exists(ScenarioPath))
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is not { } scenario)
        {
            return;
        }

        engine.World.ActiveFrequency.ActivityMeter.Record(scenario.ElapsedSeconds - 15, 15);

        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);

        Assert.Empty(engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    /// <summary>
    /// An empty meter writes no field at all, so a snapshot of a quiet frequency is byte-identical to one taken before
    /// the meter was captured — which is what keeps a pinned snapshot hash over the whole DTO from moving.
    /// </summary>
    [Fact]
    public void CaptureSnapshot_WithAnEmptyMeter_WritesNoFrequencyTimes()
    {
        if (!File.Exists(ScenarioPath))
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is null)
        {
            return;
        }

        // Plain default options, as yaat-server's pinned snapshot-zero hash test serializes the snapshot.
        string json = JsonSerializer.Serialize(engine.CaptureSnapshot());

        Assert.DoesNotContain("FrequencyTransmission", json, StringComparison.Ordinal);
    }

    /// <summary>An airborne aircraft carrying <paramref name="layout"/>, or none at all.</summary>
    private static AircraftState Bound(string callsign, AirportGroundLayout? layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B739",
            Position = new LatLon(37.40, -121.85),
            TrueHeading = new TrueHeading(300),
            TrueTrack = new TrueHeading(300),
            Altitude = 3000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
        };
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
