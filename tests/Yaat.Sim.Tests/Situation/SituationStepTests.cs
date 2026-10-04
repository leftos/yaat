using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Situation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Situation;

/// <summary>
/// The stored situation: the <c>Situation</c> spine step stamps <see cref="AircraftSituationState.AirborneAtSeconds"/>
/// at each liftoff and classifies every aircraft once a second into <see cref="AircraftSituationState.Current"/>,
/// passing the stored value and the sim time to the classifier; both survive a snapshot round-trip.
/// </summary>
public class SituationStepTests(ITestOutputHelper output)
{
    private const string CtoppRecordingPath = "TestData/cmd6-ctopp-hover-recording.yaat-bug-report-bundle.zip";

    private readonly ArtccConfigRoot? _zoa = LoadZoa();

    private static ArtccConfigRoot? LoadZoa()
    {
        TestVnasData.EnsureInitialized();
        return TestArtccConfig.LoadZoa();
    }

    private SimulationEngine? Engine(string scenarioJson) => _zoa is null ? null : AiTestFixture.Load(scenarioJson, _zoa, 7, []);

    [Fact]
    public void OneSecond_StoresTheClassifiedSituation()
    {
        if (Engine(AiTestFixture.ParkedAtOak) is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        Assert.Equal(AircraftSituation.Unknown, ac.Situation.Current);

        engine.TickOneSecond();

        Assert.Equal(AircraftSituation.AtParking, ac.Situation.Current);
    }

    [Fact]
    public void TickSituation_StandaloneTurn_KeepsTheStoredSituation()
    {
        if (Engine(AiTestFixture.ParkedAtOak) is not { } engine)
        {
            return;
        }

        // VFR, 10 NM south of its destination and closing: the predicates alone would call it inbound.
        AircraftState ac = Airborne(engine, OffAirport("OAK", 180, 10), trackDeg: 0);
        var phases = new PhaseList();
        phases.Add(new MakeTurnPhase { Direction = TurnDirection.Left, TargetDegrees = 360 });
        ac.Phases = phases;
        ac.Situation.Current = AircraftSituation.VfrFlightFollowing;

        engine.TickSituation();

        Assert.Equal(AircraftSituation.VfrFlightFollowing, ac.Situation.Current);
    }

    [Theory]
    [InlineData(240.0, AircraftSituation.VfrDeparting)]
    [InlineData(360.0, AircraftSituation.VfrFlightFollowing)]
    public void TickSituation_VfrWithNoOrigin_MeasuresLiftoffAgainstSimTime(double secondsSinceLiftoff, AircraftSituation expected)
    {
        if (Engine(AiTestFixture.ParkedAtOak) is not { } engine)
        {
            return;
        }

        engine.Scenario!.ElapsedSeconds = 1000;
        AircraftState ac = Airborne(engine, OffAirport("OAK", 45, 5), trackDeg: 45);
        ac.FlightPlan.Departure = "";
        ac.FlightPlan.Destination = "SAC";
        ac.Situation.AirborneAtSeconds = engine.Scenario.ElapsedSeconds - secondsSinceLiftoff;

        engine.TickSituation();

        Assert.Equal(expected, ac.Situation.Current);
    }

    [Fact]
    public void TickSituation_Shadow_IsLiveTraffic()
    {
        if (Engine(AiTestFixture.ParkedAtOak) is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        ac.LiveTraffic = new AircraftLiveTraffic();

        engine.TickSituation();

        Assert.Equal(AircraftSituation.LiveTraffic, ac.Situation.Current);
    }

    [Fact]
    public void TickSituation_StampsEveryLiftoff()
    {
        if (Engine(AiTestFixture.ParkedAtOak) is not { } engine)
        {
            return;
        }

        SimScenarioState scenario = engine.Scenario!;
        AircraftState ac = engine.World.GetSnapshot()[0];
        engine.TickSituation();
        Assert.True(ac.Situation.WasOnGround);
        Assert.Null(ac.Situation.AirborneAtSeconds);

        scenario.ElapsedSeconds = 100;
        ac.IsOnGround = false;
        engine.TickSituation();
        Assert.Equal(100, ac.Situation.AirborneAtSeconds);

        scenario.ElapsedSeconds = 150;
        engine.TickSituation();
        Assert.Equal(100, ac.Situation.AirborneAtSeconds);

        // A stop-and-go, or a full stop and a second departure: the most recent liftoff counts.
        scenario.ElapsedSeconds = 200;
        ac.IsOnGround = true;
        engine.TickSituation();
        Assert.Equal(100, ac.Situation.AirborneAtSeconds);

        scenario.ElapsedSeconds = 300;
        ac.IsOnGround = false;
        engine.TickSituation();
        Assert.Equal(300, ac.Situation.AirborneAtSeconds);
    }

    [Fact]
    public void AirborneSpawn_IsNeverStamped()
    {
        if (Engine(AiTestFixture.OnFinalAtOak) is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        Assert.False(ac.IsOnGround);

        engine.TickOneSecond();
        engine.TickOneSecond();

        Assert.False(ac.Situation.WasOnGround);
        Assert.Null(ac.Situation.AirborneAtSeconds);
    }

    [Fact]
    public void WarpIntoTheAir_IsNotALiftoff()
    {
        if (Engine(AiTestFixture.ParkedAtOak) is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.World.GetSnapshot()[0];
        engine.TickOneSecond();
        Assert.True(ac.Situation.WasOnGround);

        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "WARP SUNOL 270 50 120");
        Assert.True(result.Success, result.Message);
        engine.TickOneSecond();
        engine.TickOneSecond();

        Assert.False(ac.IsOnGround);
        Assert.Null(ac.Situation.AirborneAtSeconds);
    }

    /// <summary>
    /// CTOPP lifts the helicopter off inside command dispatch (<c>HelicopterTakeoffPhase.OnStart</c>), before the next
    /// physics tick; the liftoff time is still stamped, to the second.
    /// </summary>
    [Fact]
    public void Ctopp_StampsTheLiftoffWithinOneSecond()
    {
        TestVnasData.EnsureInitialized();
        SessionRecording? recording = RecordingLoader.Load(CtoppRecordingPath);
        if ((recording is null) || (TestVnasData.NavigationDb is null))
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.Replay(recording, 850);
        AircraftState? ac = engine.FindAircraft("CMD6");
        Assert.NotNull(ac);
        Assert.True(ac.IsOnGround);
        engine.TickOneSecond();
        Assert.True(ac.Situation.WasOnGround);

        double issuedAt = engine.Scenario!.ElapsedSeconds;
        CommandResult result = engine.SendCommand("CMD6", "CTOPP");
        Assert.True(result.Success, result.Message);
        engine.TickOneSecond();
        engine.TickOneSecond();

        Assert.False(ac.IsOnGround);
        Assert.NotNull(ac.Situation.AirborneAtSeconds);
        Assert.InRange(ac.Situation.AirborneAtSeconds.Value, issuedAt, issuedAt + 1);
    }

    [Fact]
    public void Snapshot_RoundTripsTheSituation()
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            IsOnGround = false,
        };
        ac.Situation.Current = AircraftSituation.VfrDeparting;
        ac.Situation.AirborneAtSeconds = 1234.5;
        ac.Situation.WasOnGround = true;

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(AircraftSituation.VfrDeparting, restored.Situation.Current);
        Assert.Equal(1234.5, restored.Situation.AirborneAtSeconds);
        Assert.True(restored.Situation.WasOnGround);
    }

    [Fact]
    public void Snapshot_WithoutTheSituation_RestoresTheCleanDefaults()
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            IsOnGround = false,
        };
        ac.Situation.Current = AircraftSituation.IfrArrival;
        ac.Situation.AirborneAtSeconds = 99;
        ac.Situation.WasOnGround = true;

        JsonObject node = JsonSerializer.SerializeToNode(ac.ToSnapshot(), RecordingJsonOptions.Default)!.AsObject();
        Assert.True(node.Remove(nameof(AircraftSnapshotDto.Situation)));
        AircraftSnapshotDto dto = node.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(AircraftSituation.Unknown, restored.Situation.Current);
        Assert.Null(restored.Situation.AirborneAtSeconds);
        Assert.False(restored.Situation.WasOnGround);
    }

    [Fact]
    public void TickSituation_StoresTheFlags()
    {
        if (Engine(AiTestFixture.ParkedAtOak) is not { } engine)
        {
            return;
        }

        AircraftState ac = Airborne(engine, OffAirport("OAK", 180, 10), trackDeg: 0);
        ac.FlightPlan.FlightRules = "IFR";
        ac.Approach.HasReportedFieldInSight = true;
        Assert.Equal(SituationFlags.None, ac.Situation.Flags);

        engine.TickSituation();

        Assert.Equal(SituationFlags.HasReportedFieldInSight, ac.Situation.Flags);
    }

    /// <summary>
    /// The inside-FAF test measures against the assigned runway's own airport map, not the aircraft's ground layout:
    /// on KSJC 30L (landing threshold displaced 2,537 ft) with an OAK ground layout, at a point the displacement puts
    /// outside the FAF, the flag stays clear; well inside it, it is set.
    /// </summary>
    [Fact]
    public void TickSituation_InsideFaf_UsesTheRunwaysAirportLayout()
    {
        if (Engine(AiTestFixture.ParkedAtOak) is not { } engine)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        AirportGroundLayout sjc = groundData.GetLayout("SJC") ?? throw new InvalidOperationException("KSJC layout missing from TestData");
        AirportGroundLayout oak = groundData.GetLayout("OAK") ?? throw new InvalidOperationException("KOAK layout missing from TestData");
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("KSJC", "30L") ?? throw new InvalidOperationException("KSJC 30L missing");
        AircraftState ac = Airborne(engine, OnFinal(runway, 1.0), trackDeg: runway.TrueHeading.Degrees);
        ac.Ground.Layout = oak;
        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new FinalApproachPhase());
        ac.Phases = phases;

        double? decisiveNm = null;
        for (double alongNm = 1.0; (alongNm < 15.0) && (decisiveNm is null); alongNm += 0.05)
        {
            ac.Position = OnFinal(runway, alongNm);
            if (FinalApproachFix.IsInside(ac, runway, oak) && (!FinalApproachFix.IsInside(ac, runway, sjc)))
            {
                decisiveNm = alongNm;
            }
        }

        Assert.NotNull(decisiveNm);
        engine.TickSituation();
        Assert.Equal(SituationFlags.None, ac.Situation.Flags & SituationFlags.InsideFinalApproachFix);

        ac.Position = OnFinal(runway, 1.0);
        engine.TickSituation();
        Assert.Equal(SituationFlags.InsideFinalApproachFix, ac.Situation.Flags & SituationFlags.InsideFinalApproachFix);
    }

    [Fact]
    public void Snapshot_RoundTripsTheFlags()
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            IsOnGround = false,
        };
        ac.Situation.Flags = SituationFlags.InsideFinalApproachFix | SituationFlags.HasReportedTrafficInSight;

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(SituationFlags.InsideFinalApproachFix | SituationFlags.HasReportedTrafficInSight, restored.Situation.Flags);
    }

    [Fact]
    public void Snapshot_WithoutFlags_RestoresNone()
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            IsOnGround = false,
        };
        ac.Situation.Current = AircraftSituation.Final;
        ac.Situation.Flags = SituationFlags.RolloutDecelerating;

        JsonObject node = JsonSerializer.SerializeToNode(ac.ToSnapshot(), RecordingJsonOptions.Default)!.AsObject();
        JsonObject situation = node[nameof(AircraftSnapshotDto.Situation)]!.AsObject();
        Assert.True(situation.Remove(nameof(AircraftSituationStateDto.Flags)));
        AircraftSnapshotDto dto = node.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(AircraftSituation.Final, restored.Situation.Current);
        Assert.Equal(SituationFlags.None, restored.Situation.Flags);
    }

    /// <summary>The fixture's parked aircraft lifted into the air at <paramref name="position"/>: no phase, 2,000 ft, 100 kt.</summary>
    private static AircraftState Airborne(SimulationEngine engine, LatLon position, double trackDeg)
    {
        AircraftState ac = engine.World.GetSnapshot()[0];
        ac.Phases = null;
        ac.IsOnGround = false;
        ac.Position = position;
        ac.Altitude = 2000;
        ac.IndicatedAirspeed = 100;
        ac.TrueHeading = new TrueHeading(trackDeg);
        ac.TrueTrack = new TrueHeading(trackDeg);
        return ac;
    }

    private static LatLon OnFinal(RunwayInfo runway, double alongNm) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading.ToReciprocal(), alongNm);

    private static LatLon OffAirport(string airport, double bearingDeg, double distanceNm)
    {
        (double lat, double lon) =
            NavigationDatabase.Instance.GetAirportPosition(airport) ?? throw new InvalidOperationException($"{airport} missing from NavData");
        return GeoMath.ProjectPoint(new LatLon(lat, lon), new TrueHeading(bearingDeg), distanceNm);
    }
}
