using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests;

public sealed class SoloTrainingEvaluatorTests
{
    public SoloTrainingEvaluatorTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Evaluate_IfrPairInsideThreeMiles_RecordsSafetyEvent()
    {
        var a = CreateAircraft("AAL1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 5000, isOnGround: false);
        var b = CreateAircraft(
            "UAL2",
            "A320",
            flightRules: "IFR",
            GeoMath.ProjectPoint(a.Position, new TrueHeading(90), 2.5),
            altitude: 5000,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 100, new ApproachReportData([], [], 100, "N/A"));

        var notice = Assert.Single(notices);
        Assert.Equal(SoloTrainingEventSeverity.Safety, notice.Severity);
        Assert.Contains("7110.65 §5-5-4", notice.RuleReference);
        Assert.Single(report.ActiveEvents);
        Assert.True(report.Score < 100);
    }

    [Fact]
    public void Evaluate_ClassBVfrBehindTurbojet_UsesOnePointFiveMilesOrFiveHundredFeet()
    {
        var jet = CreateAircraft("SWA1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 3000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(jet.Position, new TrueHeading(90), 1.4),
            altitude: 3000,
            isOnGround: false
        );
        var requirement = SoloTrainingEvaluator.ResolveRequirement(jet, vfr, AirspaceDatabase.Default);

        Assert.NotNull(requirement);
        Assert.Equal(1.5, requirement.RequiredHorizontalNm);
        Assert.Equal(500.0, requirement.RequiredVerticalFt);
        Assert.Contains("7110.65 §7-9-4", requirement.RuleReference);
    }

    [Fact]
    public void Evaluate_ClassBSmallVfrPair_UsesTargetResolutionOrFiveHundredFeet()
    {
        var first = CreateAircraft("N123AB", "C172", flightRules: "VFR", new LatLon(37.6213, -122.3790), altitude: 2500, isOnGround: false);
        var second = CreateAircraft(
            "N456CD",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(first.Position, new TrueHeading(90), 0.2),
            altitude: 2500,
            isOnGround: false
        );
        var requirement = SoloTrainingEvaluator.ResolveRequirement(first, second, AirspaceDatabase.Default);

        Assert.NotNull(requirement);
        Assert.Equal(0.25, requirement.RequiredHorizontalNm);
        Assert.Equal(500.0, requirement.RequiredVerticalFt);
        Assert.Contains("target-resolution", requirement.Name);
    }

    [Fact]
    public void Evaluate_VisualFollowPair_DoesNotRecordSeparationEvent()
    {
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", new LatLon(37.6213, -122.3790), altitude: 2500, isOnGround: false);
        var follower = CreateAircraft(
            "N222BB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(lead.Position, new TrueHeading(90), 0.1),
            altitude: 2500,
            isOnGround: false
        );
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = lead.Callsign;
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 30, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 30, new ApproachReportData([], [], 30, "N/A"));

        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_DepartureBehindDepartureBeforeDerOrSpacing_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 2500);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 11, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Equal(SoloTrainingEventSeverity.Safety, notice.Severity);
        Assert.Equal("28R", notice.RunwayId);
        Assert.Contains("7110.65 §3-9-6(a)", notice.RuleReference);
        Assert.Contains("6,000 ft", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_DepartureBehindDepartureAfterDer_DoesNotRecordViolation()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 11, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 11, new ApproachReportData([], [], 11, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_DepartureBehindLandingStillOnRunway_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", PositionOnRunway(runway, 1500), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new LandingPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 21, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Contains("7110.65 §3-9-6(b)", notice.RuleReference);
        Assert.Contains("clear of the runway", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ArrivalBehindDepartureInsideRequiredThresholdDistance_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 2500), altitude: 500, isOnGround: false);
        SetPhase(lead, runway, new InitialClimbPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, -500), altitude: 100, isOnGround: false);
        SetPhase(follower, runway, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 30, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(runway, 100);
        SetPhase(follower, runway, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 31, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Contains("7110.65 §3-10-3(a)(2)", notice.RuleReference);
        Assert.Contains("6,000 ft", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ArrivalBehindLandedAircraft_AppliesCategoryOneDistanceException()
    {
        var runway = CreateRunway();
        var evaluator = new SoloTrainingEvaluator();
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", PositionOnRunway(runway, 2500), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new LandingPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(runway, -500), altitude: 100, isOnGround: false);
        SetPhase(follower, runway, new FinalApproachPhase());
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 40, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(runway, 100);
        SetPhase(follower, runway, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 41, AirspaceDatabase.Default);
        var notice = Assert.Single(notices);
        Assert.Contains("7110.65 §3-10-3(a)(1)", notice.RuleReference);
        Assert.Contains("3,000 ft", notice.RequiredText);

        evaluator.Reset();
        lead.Position = PositionOnRunway(runway, 3500);
        follower.Position = PositionOnRunway(runway, -500);
        SetPhase(follower, runway, new FinalApproachPhase());
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 50, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(runway, 100);
        SetPhase(follower, runway, new LandingPhase());

        notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 51, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 51, new ApproachReportData([], [], 51, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_SameRunwayViolation_DedupesAcrossRepeatedTicks()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 60, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 2500);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var first = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 61, AirspaceDatabase.Default);
        var second = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 62, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 62, new ApproachReportData([], [], 62, "N/A"));

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(report.Timeline);
        Assert.Single(report.ActiveEvents);
    }

    [Fact]
    public void Reset_ClearsSameRunwayTrackerState()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 70, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 2500);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());
        Assert.Single(evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 71, AirspaceDatabase.Default));

        evaluator.Reset();

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 72, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 72, new ApproachReportData([], [], 72, "N/A"));
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    private static AircraftState CreateAircraft(
        string callsign,
        string type,
        string flightRules,
        LatLon position,
        double altitude,
        bool isOnGround
    ) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = altitude,
            IndicatedAirspeed = 180,
            IsOnGround = isOnGround,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                AircraftType = type,
                FlightRules = flightRules,
            },
        };

    private static RunwayInfo CreateRunway() =>
        new()
        {
            AirportId = "KOAK",
            Id = new RunwayIdentifier("28R", "10L"),
            Designator = "28R",
            Lat1 = 37.721300,
            Lon1 = -122.220000,
            Elevation1Ft = 10,
            TrueHeading1 = new TrueHeading(90),
            Lat2 = 37.721300,
            Lon2 = -122.190000,
            Elevation2Ft = 10,
            TrueHeading2 = new TrueHeading(270),
            LengthFt = 10000,
            WidthFt = 150,
        };

    private static LatLon PositionOnRunway(RunwayInfo runway, double alongFt) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading, alongFt / GeoMath.FeetPerNm);

    private static void SetPhase(AircraftState aircraft, RunwayInfo runway, Phase phase)
    {
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(phase);
    }
}
