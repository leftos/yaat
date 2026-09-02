using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// Shadows are first-class runway and ground-conflict participants: obstacles that simulated
/// aircraft yield to (never subjects themselves), occupants for the 3-9-4 / 3-10-5 advisories,
/// traffic on final for 3-9-4.d, same-runway lead aircraft for the solo evaluator, and a landing
/// stamps <see cref="CompletionReason.Landed"/> on the room's primary airport.
/// </summary>
public class LiveTrafficParticipationTests
{
    private static readonly RunwayInfo Runway28R = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;

    public LiveTrafficParticipationTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LatLon OnRunway(double alongFt) =>
        GeoMath.ProjectPoint(
            new LatLon(Runway28R.ThresholdLatitude, Runway28R.ThresholdLongitude),
            Runway28R.TrueHeading,
            alongFt / GeoMath.FeetPerNm
        );

    private static LatLon OnFinal(double nm) =>
        GeoMath.ProjectPoint(new LatLon(Runway28R.ThresholdLatitude, Runway28R.ThresholdLongitude), Runway28R.TrueHeading.ToReciprocal(), nm);

    private static AircraftState Shadow(string callsign, LatLon pos, double altFt, double gs, double track, double vs, LiveTrafficSource source) =>
        LiveTrafficKinematics.CreateShadow(
            callsign,
            "B738",
            new LiveTrafficSample(0, pos.Lat, pos.Lon, altFt, gs, track, vs, source, 4521),
            new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KLAX",
                Destination = "KOAK",
            }
        );

    private static AircraftState Simulated(string callsign, LatLon pos, double heading, double gs, bool onGround, Phase? phase)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = pos,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = onGround ? Runway28R.ElevationFt : Runway28R.ElevationFt + 1200,
            IndicatedAirspeed = gs,
            IsOnGround = onGround,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KOAK",
                Destination = "KLAX",
            },
            Phases = new PhaseList { AssignedRunway = Runway28R },
        };
        if (phase is not null)
        {
            ac.Phases.Add(phase);
            ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        }

        return ac;
    }

    // --- ground conflict ---

    [Fact]
    public void GroundConflict_ShadowIsAnObstacle_NeverASubject()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        var shadow = Shadow("LIVE1", OnRunway(2000), Runway28R.ElevationFt, 15, Runway28R.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex);
        shadow.Position = GeoMath.ProjectPoint(shadow.Position, Runway28R.TrueHeading + 90, 0.2);
        shadow.TrueHeading = Runway28R.TrueHeading;
        var ahead = GeoMath.ProjectPoint(shadow.Position, Runway28R.TrueHeading, 150 / GeoMath.FeetPerNm);
        var taxiing = Simulated("SIM1", ahead, Runway28R.TrueHeading.Degrees + 180, 10, onGround: true, new TaxiingPhase());

        GroundConflictDetector.ApplySpeedLimits([shadow, taxiing], layout, 0.25);

        Assert.Null(shadow.Ground.SpeedLimit);
        Assert.NotNull(taxiing.Ground.SpeedLimit);
    }

    [Fact]
    public void GroundConflict_CoastingSurfaceShadow_StopsBeingAnObstacle()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        var shadow = Shadow("LIVE1", OnRunway(2000), Runway28R.ElevationFt, 15, Runway28R.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex);
        shadow.Position = GeoMath.ProjectPoint(shadow.Position, Runway28R.TrueHeading + 90, 0.2);
        shadow.LiveTraffic!.IsCoasting = true;
        shadow.LiveTraffic.DeliverySilenceSeconds =
            (GroundConflictDetector.ExternalCoastGraceFraction * LiveTrafficKinematics.RemovalAfterSeconds(LiveTrafficSource.Asdex)) + 1;
        var ahead = GeoMath.ProjectPoint(shadow.Position, Runway28R.TrueHeading, 150 / GeoMath.FeetPerNm);
        var taxiing = Simulated("SIM1", ahead, Runway28R.TrueHeading.Degrees + 180, 10, onGround: true, new TaxiingPhase());

        GroundConflictDetector.ApplySpeedLimits([shadow, taxiing], layout, 0.25);

        Assert.Null(taxiing.Ground.SpeedLimit);
    }

    [Fact]
    public void GroundConflict_ShadowOnTheRunway_HasPriorityOverACrosser()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var rolling = Shadow("LIVE1", OnRunway(3000), Runway28R.ElevationFt, 60, Runway28R.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex);
        var ahead = GeoMath.ProjectPoint(rolling.Position, Runway28R.TrueHeading, 120 / GeoMath.FeetPerNm);
        var crosser = Simulated("SIM1", ahead, Runway28R.TrueHeading.Degrees + 90, 8, onGround: true, new TaxiingPhase());

        GroundConflictDetector.ApplySpeedLimits([rolling, crosser], layout, 0.25);

        Assert.True(rolling.Ground.ExternalOnRunway);
        Assert.Null(rolling.Ground.SpeedLimit);
        Assert.Equal(0.0, crosser.Ground.SpeedLimit);
    }

    // --- runway safety advisories ---

    private static DispatchContext Ctx(params AircraftState[] all) => TestDispatch.Context(new Random(1), listAircraft: () => all);

    [Fact]
    public void LandingClearance_WarnsForAShadowLinedUpOnTheRunway()
    {
        var linedUp = Shadow("LIVE1", OnRunway(300), Runway28R.ElevationFt, 0, Runway28R.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex);
        var arrival = Simulated("ARR1", OnFinal(4), Runway28R.TrueHeading.Degrees, 140, onGround: false, new FinalApproachPhase());

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, Runway28R, Ctx(arrival, linedUp));

        var warning = Assert.Single(arrival.PendingWarnings);
        Assert.Contains("LIVE1", warning);
        Assert.Contains("3-10-5.e", warning);
        Assert.DoesNotContain("3-10-3.a.1", warning);
    }

    [Fact]
    public void LandingClearance_WarnsForAShadowStillInTheAirOverTheRunway()
    {
        // Over the pavement below threshold-crossing height the shadow has not touched down and is not clear of the
        // runway (P/CG CLEAR OF THE RUNWAY) — for a rotorcraft that descent can last a minute.
        var flaring = Shadow("LIVE1", OnRunway(300), Runway28R.ElevationFt + 30, 130, Runway28R.TrueHeading.Degrees, -300, LiveTrafficSource.Stars);
        var arrival = Simulated("ARR1", OnFinal(4), Runway28R.TrueHeading.Degrees, 140, onGround: false, new FinalApproachPhase());

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, Runway28R, Ctx(arrival, flaring));

        Assert.Contains(arrival.PendingWarnings, w => w.Contains("LIVE1", StringComparison.Ordinal));
    }

    [Fact]
    public void LandingClearance_WarnsForALiveDepartureRollingOnTheRunway_WithTheLandmarkWording()
    {
        var rolling = Shadow("LIVE1", OnRunway(300), Runway28R.ElevationFt, 60, Runway28R.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex);
        var arrival = Simulated("ARR1", OnFinal(4), Runway28R.TrueHeading.Degrees, 140, onGround: false, new FinalApproachPhase());

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, Runway28R, Ctx(arrival, rolling));

        var warning = Assert.Single(arrival.PendingWarnings);
        Assert.Contains("LIVE1", warning);
        Assert.Contains("3-10-3.a.2", warning);
    }

    [Fact]
    public void LandingClearance_DesignatorOnly_WarnsForAShadowOnTheRunwaySurface()
    {
        var rollout = Shadow("LIVE1", OnRunway(2500), Runway28R.ElevationFt, 60, Runway28R.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex);
        rollout.LiveTraffic!.LandedOnRunway = true;
        var arrival = Simulated("ARR1", OnFinal(6), Runway28R.TrueHeading.Degrees, 150, onGround: false, new FinalApproachPhase());
        arrival.FlightPlan.Destination = "KOAK";

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, "28R", Ctx(arrival, rollout));

        var warning = Assert.Single(arrival.PendingWarnings);
        Assert.Contains("LIVE1", warning);
        Assert.Contains("3-10-3.a.1", warning);
    }

    [Fact]
    public void LandingClearance_AShadowOnShortFinalIsSequencing_NotAnOccupant()
    {
        var shortFinal = Shadow(
            "LIVE1",
            OnFinal(1.2),
            Runway28R.ElevationFt + 400,
            130,
            Runway28R.TrueHeading.Degrees,
            -600,
            LiveTrafficSource.Stars
        );
        var arrival = Simulated("ARR1", OnFinal(6), Runway28R.TrueHeading.Degrees, 150, onGround: false, new FinalApproachPhase());

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, Runway28R, Ctx(arrival, shortFinal));

        Assert.Empty(arrival.PendingWarnings);
    }

    [Fact]
    public void LineUpAndWait_OnFinalAdvisory_ReportsTheClosestRunwayOnly_ForParallels()
    {
        var runway28L = NavigationDatabase.Instance.GetRunway("OAK", "28L")!;
        var threshold28L = new LatLon(runway28L.ThresholdLatitude, runway28L.ThresholdLongitude);
        var on28L = GeoMath.ProjectPoint(threshold28L, runway28L.TrueHeading.ToReciprocal(), 4);
        var shadow = Shadow("LIVE1", on28L, runway28L.ElevationFt + 1300, 140, runway28L.TrueHeading.Degrees, -700, LiveTrafficSource.Stars);
        var departure = Simulated("DEP1", OnRunway(0), Runway28R.TrueHeading.Degrees, 0, onGround: true, new LinedUpAndWaitingPhase());

        RunwaySafetyAdvisor.WarnIfTrafficOnFinal(departure, Runway28R, Ctx(departure, shadow));

        Assert.Empty(departure.PendingWarnings);
    }

    [Fact]
    public void LandingClearance_IgnoresAShadowCrossingTheRunway()
    {
        var crossing = Shadow("LIVE1", OnRunway(6000), Runway28R.ElevationFt, 15, Runway28R.TrueHeading.Degrees + 90, 0, LiveTrafficSource.Asdex);
        var arrival = Simulated("ARR1", OnFinal(4), Runway28R.TrueHeading.Degrees, 140, onGround: false, new FinalApproachPhase());

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(arrival, Runway28R, Ctx(arrival, crossing));

        Assert.Empty(arrival.PendingWarnings);
    }

    [Fact]
    public void LineUpAndWait_WarnsForLiveTrafficOnFinal_WithinSixMiles()
    {
        var onFinal = Shadow("LIVE1", OnFinal(4.5), Runway28R.ElevationFt + 1400, 150, Runway28R.TrueHeading.Degrees, -700, LiveTrafficSource.Stars);
        var farOut = Shadow("LIVE2", OnFinal(9), Runway28R.ElevationFt + 2800, 170, Runway28R.TrueHeading.Degrees, -700, LiveTrafficSource.Stars);
        var departure = Simulated("DEP1", OnRunway(0), Runway28R.TrueHeading.Degrees, 0, onGround: true, new LinedUpAndWaitingPhase());

        RunwaySafetyAdvisor.WarnIfTrafficOnFinal(departure, Runway28R, Ctx(departure, onFinal, farOut));

        var warning = Assert.Single(departure.PendingWarnings);
        Assert.Contains("traffic, LIVE1, 4.5 mile final", warning);
        Assert.DoesNotContain("LIVE2", warning);
        Assert.Contains("3-9-4.d", warning);
    }

    [Fact]
    public void LineUpAndWait_ThroughTheDispatcher_CarriesTheOnFinalAdvisory()
    {
        var onFinal = Shadow("LIVE1", OnFinal(3), Runway28R.ElevationFt + 950, 140, Runway28R.TrueHeading.Degrees, -700, LiveTrafficSource.Stars);
        var departure = Simulated("DEP1", OnRunway(0), Runway28R.TrueHeading.Degrees, 0, onGround: true, null);
        departure.Position = GeoMath.ProjectPoint(OnRunway(0), Runway28R.TrueHeading - 90, 300 / GeoMath.FeetPerNm);
        departure.Phases = new PhaseList();
        departure.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = Runway28R.Id.ToString(),
                }
            )
        );
        departure.Phases.Start(CommandDispatcher.BuildMinimalContext(departure));

        var parsed = CommandParser.ParseCompound("LUAW");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, departure, Ctx(departure, onFinal));

        Assert.True(result.Success, result.Message);
        Assert.Contains(
            departure.PendingWarnings,
            w => w.Contains("LIVE1", StringComparison.Ordinal) && w.Contains("3-9-4.d", StringComparison.Ordinal)
        );
    }

    // --- solo evaluator ---

    [Fact]
    public void Evaluator_DepartureBehindALiveArrivalStillOnTheRunway_RecordsTheWakeEvent()
    {
        var lead = Shadow("LIVE1", OnRunway(1500), Runway28R.ElevationFt, 40, Runway28R.TrueHeading.Degrees, 0, LiveTrafficSource.Asdex);
        lead.AircraftType = "C172";
        lead.LiveTraffic!.LandedOnRunway = true;
        var follower = Simulated("N222BB", OnRunway(0), Runway28R.TrueHeading.Degrees, 0, onGround: true, new LinedUpAndWaitingPhase());
        follower.AircraftType = "C172";
        follower.FlightPlan.FlightRules = "VFR";
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        follower.Phases = new PhaseList { AssignedRunway = Runway28R };
        follower.Phases.Add(new TakeoffPhase());
        follower.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        follower.IndicatedAirspeed = 40;

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 21, AirspaceDatabase.Default);

        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Contains("clear of the runway", notice.RequiredText);
    }

    [Fact]
    public void Evaluator_ArrivalBehindAJustDepartedShadow_IsScoredAfterLiftoff()
    {
        // The departure latch keeps the shadow a Departing on 28R after liftoff, inside the §3-9-6 landmarks.
        var lead = Shadow("LIVE1", OnRunway(2500), Runway28R.ElevationFt + 150, 150, Runway28R.TrueHeading.Degrees, 1_500, LiveTrafficSource.Stars);
        lead.LiveTraffic!.DepartedOnRunway = true;
        lead.LiveTraffic.LatchedRunwayAirport = "OAK";
        lead.LiveTraffic.LatchedRunwayDesignator = "28R";
        lead.LiveTraffic.LastRunwayUse = RunwayUseKind.Departing;
        var arrival = Simulated("AAL2", OnFinal(0.6), Runway28R.TrueHeading.Degrees, 130, onGround: false, new FinalApproachPhase());
        arrival.Altitude = Runway28R.ElevationFt + 200;
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, arrival], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        arrival.Position = OnRunway(100);
        arrival.Altitude = Runway28R.ElevationFt + 20;
        arrival.Phases = new PhaseList { AssignedRunway = Runway28R, LandingClearance = ClearanceType.ClearedToLand };
        arrival.Phases.Add(new LandingPhase());
        arrival.Phases.CurrentPhase!.Status = PhaseStatus.Active;

        var notices = evaluator.Evaluate([lead, arrival], scenarioElapsedSeconds: 21, AirspaceDatabase.Default);

        Assert.Contains(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
    }

    // --- completion ---

    [Fact]
    public void ShadowLanding_StampsLanded_AndRemovalRecordsTheCompletion()
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "KOAK",
            },
        };
        var overThreshold = OnRunway(200);
        var spawn = Shadow("LIVE1", overThreshold, Runway28R.ElevationFt + 30, 130, Runway28R.TrueHeading.Degrees, -300, LiveTrafficSource.Stars);
        engine.World.AddAircraft(spawn);

        engine.TickOneSecond();
        Assert.Equal(RunwayUseKind.Landing, spawn.LiveTraffic!.LastRunwayUse);
        Assert.Equal(CompletionReason.Active, spawn.CompletionReason);

        LiveTrafficKinematics.Apply(
            spawn,
            new LiveTrafficSample(
                2,
                OnRunway(1500).Lat,
                OnRunway(1500).Lon,
                Runway28R.ElevationFt,
                80,
                Runway28R.TrueHeading.Degrees,
                0,
                LiveTrafficSource.Asdex,
                4521
            )
        );
        engine.TickOneSecond();

        Assert.Equal(CompletionReason.Landed, spawn.CompletionReason);
        Assert.True(spawn.LiveTraffic!.LandedOnRunway);
        Assert.Equal(RunwayUseKind.OnSurface, spawn.LiveTraffic.LastRunwayUse);
        Assert.Equal("28R", spawn.LiveTraffic.LatchedRunwayDesignator);
        Assert.True(engine.RemoveLiveTraffic("LIVE1", LiveTrafficRemovalReason.Dropped));
        Assert.Contains(engine.World.GetCompletedAircraft(), r => r.Callsign == "LIVE1");
    }
}
