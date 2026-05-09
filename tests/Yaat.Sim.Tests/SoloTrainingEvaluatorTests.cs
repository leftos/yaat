using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
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
        var a = CreateAircraft("AAL1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 5000);
        var b = CreateAircraft("UAL2", "A320", flightRules: "IFR", GeoMath.ProjectPoint(a.Position, new TrueHeading(90), 2.5), altitude: 5000);
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
        var jet = CreateAircraft("SWA1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 3000);
        var vfr = CreateAircraft("N123AB", "C172", flightRules: "VFR", GeoMath.ProjectPoint(jet.Position, new TrueHeading(90), 1.4), altitude: 3000);
        var requirement = SoloTrainingEvaluator.ResolveRequirement(jet, vfr, AirspaceDatabase.Default);

        Assert.NotNull(requirement);
        Assert.Equal(1.5, requirement.RequiredHorizontalNm);
        Assert.Equal(500.0, requirement.RequiredVerticalFt);
        Assert.Contains("7110.65 §7-9-4", requirement.RuleReference);
    }

    [Fact]
    public void Evaluate_ClassBSmallVfrPair_UsesTargetResolutionOrFiveHundredFeet()
    {
        var first = CreateAircraft("N123AB", "C172", flightRules: "VFR", new LatLon(37.6213, -122.3790), altitude: 2500);
        var second = CreateAircraft(
            "N456CD",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(first.Position, new TrueHeading(90), 0.2),
            altitude: 2500
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
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", new LatLon(37.6213, -122.3790), altitude: 2500);
        var follower = CreateAircraft(
            "N222BB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(lead.Position, new TrueHeading(90), 0.1),
            altitude: 2500
        );
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = lead.Callsign;
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 30, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 30, new ApproachReportData([], [], 30, "N/A"));

        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    private static AircraftState CreateAircraft(string callsign, string type, string flightRules, LatLon position, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = altitude,
            IndicatedAirspeed = 180,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                AircraftType = type,
                FlightRules = flightRules,
            },
        };
}
