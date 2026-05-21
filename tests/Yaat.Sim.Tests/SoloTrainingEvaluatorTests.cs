using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
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
        var report = evaluator.BuildReport(true, 100, new ApproachReportData([], [], 100, "N/A"), AircraftDebriefContext.Empty);

        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(SoloTrainingEventSeverity.Safety, notice.Severity);
        Assert.Contains("7110.65 §5-5-4", notice.RuleReference);
        Assert.Single(report.ActiveEvents, e => e.Category == SoloTrainingEventCategory.Separation);
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
    public void Evaluate_ClassCIfrVfrPairInsideCharlie_UsesTargetResolutionOrFiveHundredFeet()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.7213, -122.2208), altitude: 1000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 1000,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([ifr, vfr], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        var separation = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(SoloTrainingEventSeverity.Safety, separation.Severity);
        Assert.Contains("7110.65 §7-8-3", separation.RuleReference);
        Assert.Equal(0.25, separation.RequiredHorizontalNm);
        Assert.Equal(500.0, separation.RequiredVerticalFt);
    }

    [Fact]
    public void Evaluate_ClassCIfrVfrPairOutsideCharlie_DoesNotRecordSeparationEvent()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.0000, -121.0000), altitude: 2500, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 2500,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([ifr, vfr], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 20, new ApproachReportData([], [], 20, "N/A"), AircraftDebriefContext.Empty);

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
        Assert.DoesNotContain(report.Timeline, e => e.Category == SoloTrainingEventCategory.Separation);
    }

    [Fact]
    public void Evaluate_ClassCVfrPairInsideCharlie_DoesNotRecordSeparationEvent()
    {
        var first = CreateAircraft("N123AB", "C172", flightRules: "VFR", new LatLon(37.7213, -122.2208), altitude: 1000, isOnGround: false);
        var second = CreateAircraft(
            "N456CD",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(first.Position, new TrueHeading(90), 0.2),
            altitude: 1000,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([first, second], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 20, new ApproachReportData([], [], 20, "N/A"), AircraftDebriefContext.Empty);

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
        Assert.All(report.Timeline, e => Assert.Equal(SoloTrainingEventCategory.AdvisoryVisual, e.Category));
    }

    [Fact]
    public void Evaluate_ProjectedClassCEntryUsesProjectedAirspaceForCoach()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.8300, -122.4200), altitude: 2000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 2000,
            isOnGround: false
        );
        ifr.TrueTrack = new TrueHeading(90);
        ifr.IndicatedAirspeed = 120;
        vfr.TrueTrack = new TrueHeading(90);
        vfr.IndicatedAirspeed = 120;
        var evaluator = new SoloTrainingEvaluator();

        var currentRequirement = SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default);
        var projectedRequirement = SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default, lookaheadSeconds: 60);
        Assert.NotNull(currentRequirement);
        Assert.Contains("outer-area", currentRequirement.Name);
        Assert.NotNull(projectedRequirement);

        var notices = evaluator.Evaluate([ifr, vfr], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        var separation = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(SoloTrainingEventSeverity.Safety, separation.Severity);
        Assert.Contains("7110.65 §7-8-3", separation.RuleReference);
    }

    [Fact]
    public void ResolveRequirement_SkipsClassCShelfUntilProjectedAltitudeEntersVolume()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.8400, -122.3000), altitude: 1000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 1000,
            isOnGround: false
        );

        var currentRequirement = SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default);
        Assert.NotNull(currentRequirement);
        Assert.Contains("outer-area", currentRequirement.Name);

        ifr.VerticalSpeed = 600;
        vfr.VerticalSpeed = 600;

        var projectedRequirement = SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default, lookaheadSeconds: 60);
        Assert.NotNull(projectedRequirement);
        Assert.Contains("7110.65 §7-8-3", projectedRequirement.RuleReference);
    }

    [Fact]
    public void Evaluate_ClassCSafetyAlertUsesDirectedSafalProof()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.7213, -122.2208), altitude: 1000, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 1000,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(
            ifr,
            SingleCommand(new SafetyAlertCommand(new SafetyAlertDetails(12, 1, null, null))),
            scenarioElapsedSeconds: 19,
            [ifr, vfr]
        );

        var notices = evaluator.Evaluate([ifr, vfr], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(vfr.Callsign, advisory.Callsigns[0]);
        Assert.Equal(ifr.Callsign, advisory.Callsigns[1]);
        Assert.Contains("7110.65 §2-1-6", advisory.RuleReference);
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
        var report = evaluator.BuildReport(true, 30, new ApproachReportData([], [], 30, "N/A"), AircraftDebriefContext.Empty);

        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_MissingTrafficAdvisoryForProjectedSeparationLoss_RecordsAdvisoryEvents()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        var advisories = notices.Where(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual).ToList();
        Assert.Equal(2, advisories.Count);
        Assert.All(advisories, e => Assert.Equal(SoloTrainingEventSeverity.Warning, e.Severity));
        Assert.All(advisories, e => Assert.Contains("7110.65 §2-1-21", e.RuleReference));
        Assert.All(advisories, e => Assert.Contains("structured RTIS", e.ActualText));
    }

    [Fact]
    public void Evaluate_StructuredRtisProofSuppressesOnlyDirectedAdvisory()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(
            a,
            SingleCommand(new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(12, 5, "W", b.AircraftType, 5000))),
            scenarioElapsedSeconds: 5,
            [a, b]
        );

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(b.Callsign, advisory.Callsigns[0]);
        Assert.Equal(a.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Evaluate_RpoShortcutRtisDoesNotSuppressAdvisoryScoring()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(a, SingleCommand(new ReportTrafficInSightCommand(b.Callsign)), scenarioElapsedSeconds: 5, [a, b]);
        evaluator.RecordControllerCommand(b, SingleCommand(new ReportTrafficInSightForcedCommand(a.Callsign)), scenarioElapsedSeconds: 6, [a, b]);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Traffic advisory needed"));
    }

    [Fact]
    public void Evaluate_BareRtisDoesNotSuppressAdvisoryScoring()
    {
        var (a, b) = CreateClosingIfrPair();
        a.Approach.LastReportedTrafficCallsign = b.Callsign;
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(a, SingleCommand(new ReportTrafficInSightCommand(null)), scenarioElapsedSeconds: 5, [a, b]);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Traffic advisory needed"));
    }

    [Fact]
    public void Evaluate_QueuedRtisDoesNotCountBeforeDispatch()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        var queuedCommand = new CompoundCommand([
            new ParsedBlock(null, [new WaitCommand(10)]),
            new ParsedBlock(null, [new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(12, 5, "W", b.AircraftType, 5000))]),
        ]);
        evaluator.RecordControllerCommand(a, queuedCommand, scenarioElapsedSeconds: 5, [a, b]);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
    }

    [Fact]
    public void Evaluate_AdvisoryProofAfterActiveEventClearsOnNextEvaluation()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        evaluator.RecordControllerCommand(
            a,
            SingleCommand(new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(12, 5, "W", b.AircraftType, 5000))),
            scenarioElapsedSeconds: 11,
            [a, b]
        );
        evaluator.RecordControllerCommand(
            b,
            SingleCommand(new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(12, 5, "E", a.AircraftType, 5000))),
            scenarioElapsedSeconds: 11,
            [a, b]
        );
        evaluator.Evaluate([a, b], scenarioElapsedSeconds: 12, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 12, new ApproachReportData([], [], 12, "N/A"), AircraftDebriefContext.Empty);

        Assert.DoesNotContain(report.ActiveEvents, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(2, report.Timeline.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
    }

    [Fact]
    public void Evaluate_WrongRtisTargetDoesNotSuppressAdvisory()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(
            a,
            SingleCommand(new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(3, 5, "W", "B737", 5000))),
            scenarioElapsedSeconds: 5,
            [a, b]
        );
        evaluator.RecordControllerCommand(
            b,
            SingleCommand(new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(12, 5, "E", a.AircraftType, 5000))),
            scenarioElapsedSeconds: 6,
            [a, b]
        );

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(a.Callsign, advisory.Callsigns[0]);
        Assert.Equal(b.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Evaluate_MissingTrafficAdvisory_DedupesAcrossRepeatedTicks()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();

        var first = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);
        var second = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 11, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 11, new ApproachReportData([], [], 11, "N/A"), AircraftDebriefContext.Empty);

        Assert.Equal(2, first.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
        Assert.Empty(second);
        Assert.Equal(2, report.Timeline.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
    }

    [Fact]
    public void Reset_ClearsTrafficAdvisoryProofAndEvents()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(
            a,
            SingleCommand(new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(12, 5, "W", b.AircraftType, 5000))),
            scenarioElapsedSeconds: 5,
            [a, b]
        );
        evaluator.RecordControllerCommand(
            b,
            SingleCommand(new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(12, 5, "E", a.AircraftType, 5000))),
            scenarioElapsedSeconds: 6,
            [a, b]
        );
        Assert.DoesNotContain(
            evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default),
            e => e.Category == SoloTrainingEventCategory.AdvisoryVisual
        );

        evaluator.Reset();
        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 11, AirspaceDatabase.Default);

        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
    }

    [Fact]
    public void SendCommand_StructuredRtisRecordsTrafficAdvisoryProofIntoEvaluator()
    {
        var (a, b) = CreateClosingIfrPair();
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 5) };
        engine.World.AddAircraft(a);
        engine.World.AddAircraft(b);

        var result = engine.SendCommand(a.Callsign, $"RTIS 12 5 W {b.AircraftType} 050");
        var notices = engine.SoloTrainingEvaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.True(result.Success, result.Message);
        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(b.Callsign, advisory.Callsigns[0]);
        Assert.Equal(a.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Replay_StructuredRtisRecordsTrafficAdvisoryProofUsingFullSnapshot()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        var recording = new SessionRecording
        {
            ScenarioJson = BuildReplayProofScenarioJson(),
            RngSeed = 1,
            TotalElapsedSeconds = 2,
            Actions =
            [
                new RecordedSettingChange(0, "SoloTrainingMode", "True"),
                new RecordedCommand(1, "AAL1", "RTIS 12 2 W A320 050", "TS", "test-conn"),
            ],
        };

        engine.Replay(recording, 2);
        var a = engine.FindAircraft("AAL1");
        var b = engine.FindAircraft("UAL2");
        Assert.NotNull(a);
        Assert.NotNull(b);
        a.HasMadeInitialContact = true;
        b.HasMadeInitialContact = true;

        var notices = engine.SoloTrainingEvaluator.Evaluate([a, b], scenarioElapsedSeconds: 2, AirspaceDatabase.Default);

        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Equal(b.Callsign, advisory.Callsigns[0]);
        Assert.Equal(a.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void SendCommand_StructuredRfisRecordsProofAndStartsFieldAcquisition()
    {
        var aircraft = CreateAircraft("AAL1", "B738", flightRules: "IFR", new LatLon(38.10, -122.70), altitude: 5000, isOnGround: false);
        aircraft.FlightPlan.Destination = "KOAK";
        var other = CreateAircraft("UAL2", "A320", flightRules: "IFR", new LatLon(39.00, -123.00), altitude: 7000, isOnGround: false);
        var runway = CreateRunway();
        aircraft.Phases = new PhaseList
        {
            AssignedRunway = runway,
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "VIS28R",
                AirportCode = "KOAK",
                RunwayId = "28R",
                FinalApproachCourse = runway.TrueHeading,
            },
        };
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 5) };
        engine.World.AddAircraft(aircraft);
        engine.World.AddAircraft(other);

        var result = engine.SendCommand(aircraft.Callsign, "RFIS 12 12");
        var notices = engine.SoloTrainingEvaluator.Evaluate([aircraft, other], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.True(result.Success, result.Message);
        Assert.True(
            aircraft.Approach.HasReportedFieldInSight || aircraft.PendingObservations.Any(o => o is FieldAcquisitionObservation),
            "Structured RFIS should either resolve field acquisition immediately or arm the soft-fail observation."
        );
        Assert.DoesNotContain(notices, e => e.Title == "Visual approach field proof missing");
    }

    [Fact]
    public void Evaluate_CvaWithoutFieldProofRecordsVisualWarningUntilRfisProof()
    {
        var (aircraft, other) = CreateClosingIfrPair();
        other.Position = GeoMath.ProjectPoint(aircraft.Position, new TrueHeading(90), 10.0);
        var runway = CreateRunway();
        aircraft.Phases = new PhaseList
        {
            AssignedRunway = runway,
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "VIS28R",
                AirportCode = "KOAK",
                RunwayId = "28R",
                FinalApproachCourse = runway.TrueHeading,
            },
        };
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([aircraft, other], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);
        var visual = Assert.Single(notices, e => e.Title == "Visual approach field proof missing");
        Assert.Contains("7110.65 §7-4-3", visual.RuleReference);

        evaluator.RecordControllerCommand(
            aircraft,
            SingleCommand(new ReportFieldAdvisoryCommand(new FieldAdvisoryDetails(12, 10))),
            scenarioElapsedSeconds: 11,
            [aircraft, other]
        );
        evaluator.Evaluate([aircraft, other], scenarioElapsedSeconds: 12, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 12, new ApproachReportData([], [], 12, "N/A"), AircraftDebriefContext.Empty);

        Assert.DoesNotContain(report.ActiveEvents, e => e.Title == "Visual approach field proof missing");
    }

    [Fact]
    public void Evaluate_CvaWithoutFieldProofScoresOnceWithoutTrafficPair()
    {
        var aircraft = CreateAircraft("AAL1", "B738", flightRules: "IFR", new LatLon(38.10, -122.70), altitude: 5000, isOnGround: false);
        var runway = CreateRunway();
        aircraft.Phases = new PhaseList
        {
            AssignedRunway = runway,
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "VIS28R",
                AirportCode = "KOAK",
                RunwayId = "28R",
                FinalApproachCourse = runway.TrueHeading,
            },
        };
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([aircraft], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.Single(notices, e => e.Title == "Visual approach field proof missing");
    }

    [Fact]
    public void ResolveRequirement_ClassCOuterAreaIfrVfrPair_AppliesTargetResolution()
    {
        var ifr = CreateAircraft("AAL1", "C172", flightRules: "IFR", new LatLon(37.95, -122.22), altitude: 2500, isOnGround: false);
        var vfr = CreateAircraft(
            "N123AB",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(ifr.Position, new TrueHeading(90), 0.2),
            altitude: 2500,
            isOnGround: false
        );

        var requirement = SoloTrainingEvaluator.ResolveRequirement(ifr, vfr, AirspaceDatabase.Default);

        Assert.NotNull(requirement);
        Assert.Contains("outer-area", requirement.Name);
        Assert.Contains("AIM §3-2-4", requirement.RuleReference);
        Assert.Equal(SoloTrainingEventCategory.Separation, requirement.Category);
    }

    [Fact]
    public void Evaluate_NoMinimaProximityCreatesAdvisoryOnlyEvents()
    {
        var first = CreateAircraft("N123AB", "C172", flightRules: "VFR", new LatLon(37.0000, -121.0000), altitude: 2500, isOnGround: false);
        var second = CreateAircraft(
            "N456CD",
            "C172",
            flightRules: "VFR",
            GeoMath.ProjectPoint(first.Position, new TrueHeading(90), 2.0),
            altitude: 2500,
            isOnGround: false
        );
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([first, second], scenarioElapsedSeconds: 20, AirspaceDatabase.Default);

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.Separation);
        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual));
        Assert.All(notices, e => Assert.Contains("7110.65 §2-1-21", e.RuleReference));
    }

    [Fact]
    public void Evaluate_TransferredAwayRecipientDoesNotReceiveMissingAdvisoryScoring()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(a, SingleCommand(new FrequencyChangeApprovedCommand()), scenarioElapsedSeconds: 5, [a, b]);

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Traffic advisory needed");
        Assert.Equal(b.Callsign, advisory.Callsigns[0]);
        Assert.Equal(a.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Evaluate_DifferentOwnerRecipientDoesNotReceiveMissingAdvisoryScoringWhenStudentPositionKnown()
    {
        var (a, b) = CreateClosingIfrPair();
        var student = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");
        a.Track.Owner = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
        b.Track.Owner = student;
        var evaluator = new SoloTrainingEvaluator();

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default, student);

        var advisory = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Traffic advisory needed");
        Assert.Equal(b.Callsign, advisory.Callsigns[0]);
        Assert.Equal(a.Callsign, advisory.Callsigns[1]);
    }

    [Fact]
    public void Evaluate_TowerStudentScoresRecipientWhenOriginatingControllerHasInitiatedHandoff()
    {
        var (a, b) = CreateClosingIfrPair();
        var student = TrackOwner.CreateStars("SFO_TWR", "SFO", 3, "T");
        a.Track.Owner = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
        a.Track.HandoffPeer = student;
        b.Track.Owner = student;
        var evaluator = new SoloTrainingEvaluator();
        var serviceContext = new SoloTrainingServiceContext(
            new InitialContactEligibilityContext(student, "TWR", "ZOA", "KSFO", InitialContactTransferCatalog.Empty),
            WakeDirectiveCatalog.Empty
        );

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default, serviceContext);

        Assert.Equal(2, notices.Count(e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Traffic advisory needed"));
    }

    [Fact]
    public void Evaluate_SafetyAlertProofSuppressesOnlyCurrentSafetyAlertScoring()
    {
        var (a, b) = CreateConflictingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(
            a,
            SingleCommand(new SafetyAlertCommand(new SafetyAlertDetails(12, 3, null, null))),
            scenarioElapsedSeconds: 5,
            [a, b]
        );
        evaluator.RecordControllerCommand(
            b,
            SingleCommand(new SafetyAlertCommand(new SafetyAlertDetails(6, 3, null, null))),
            scenarioElapsedSeconds: 6,
            [a, b]
        );

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual);
        Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.Separation);
    }

    [Fact]
    public void Evaluate_SafetyAlertOutsideCurrentSafetyStateRecordsOveruse()
    {
        var (a, b) = CreateClosingIfrPair();
        var evaluator = new SoloTrainingEvaluator();
        evaluator.RecordControllerCommand(
            a,
            SingleCommand(new SafetyAlertCommand(new SafetyAlertDetails(12, 5, null, null))),
            scenarioElapsedSeconds: 5,
            [a, b]
        );

        var notices = evaluator.Evaluate([a, b], scenarioElapsedSeconds: 10, AirspaceDatabase.Default);

        Assert.Contains(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Safety alert overused");
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
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
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
        var report = evaluator.BuildReport(true, 11, new ApproachReportData([], [], 11, "N/A"), AircraftDebriefContext.Empty);
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
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
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
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
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
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
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
        var report = evaluator.BuildReport(true, 51, new ApproachReportData([], [], 51, "N/A"), AircraftDebriefContext.Empty);
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
        var report = evaluator.BuildReport(true, 62, new ApproachReportData([], [], 62, "N/A"), AircraftDebriefContext.Empty);

        Assert.Single(first, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Empty(second);
        Assert.Single(report.Timeline, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Single(report.ActiveEvents, e => e.Category == SoloTrainingEventCategory.RunwayWake);
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
        var report = evaluator.BuildReport(true, 72, new ApproachReportData([], [], 72, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_ReciprocalDepartureBeforePrecedingDepartureCrossesRunwayEnd_RecordsRunwayWakeSafetyEvent()
    {
        var runway28R = CreateRunway();
        var runway10L = runway28R.ForApproach("10L");
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway28R, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway28R, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway10L, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway10L, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 73, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway28R, 2500);
        lead.IsOnGround = false;
        SetPhase(lead, runway28R, new InitialClimbPhase());
        SetPhase(follower, runway10L, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 74, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Equal(SoloTrainingEventSeverity.Safety, notice.Severity);
        Assert.Equal("28R/10L", notice.RunwayId);
        Assert.Contains("7110.65 §3-9-6(a)", notice.RuleReference);
        Assert.Contains("opposite-direction", notice.Title);
        Assert.Contains("crossed DER/runway end", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ReciprocalDepartureAfterPrecedingDepartureCrossesRunwayEnd_DoesNotRecordViolation()
    {
        var runway28R = CreateRunway();
        var runway10L = runway28R.ForApproach("10L");
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway28R, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway28R, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(runway10L, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway10L, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 75, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway28R, runway28R.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway28R, new InitialClimbPhase());
        SetPhase(follower, runway10L, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 76, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 76, new ApproachReportData([], [], 76, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_IntersectingDepartureBehindDepartureBeforeIntersection_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var crossing = CreateCrossingRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 2000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(crossing, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, crossing, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 77, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 3000);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, crossing, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 78, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §3-9-8", notice.RuleReference);
        Assert.Contains("intersecting-runway", notice.Title);
        Assert.Contains("passed the intersection", notice.RequiredText);
        Assert.Contains("before runway-intersection separation existed", notice.Description);
    }

    [Fact]
    public void Evaluate_IntersectingDepartureBehindDepartureAfterIntersection_DoesNotRecordViolation()
    {
        var runway = CreateRunway();
        var crossing = CreateCrossingRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 2000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(crossing, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, crossing, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 79, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 6000);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, crossing, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 80, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 80, new ApproachReportData([], [], 80, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_IntersectingDepartureBehindLandingBeforeIntersection_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var crossing = CreateCrossingRunway();
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", PositionOnRunway(runway, 3000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new LandingPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(crossing, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, crossing, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 81, AirspaceDatabase.Default);

        SetPhase(follower, crossing, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 82, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §3-9-8", notice.RuleReference);
        Assert.Contains("Departure behind landing intersecting-runway", notice.Title);
        Assert.Contains("clear of runway or passed the intersection", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_IntersectingArrivalBehindDepartureBeforeIntersection_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var crossing = CreateCrossingRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 3000), altitude: 500, isOnGround: false);
        SetPhase(lead, runway, new InitialClimbPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(crossing, -500), altitude: 100, isOnGround: false);
        SetPhase(follower, crossing, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 83, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(crossing, 100);
        SetPhase(follower, crossing, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 84, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §3-10-4", notice.RuleReference);
        Assert.Contains("Arrival behind departure intersecting-runway", notice.Title);
        Assert.Contains("passed the intersection/flight path", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_IntersectingArrivalBehindLandingBeforeIntersection_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var crossing = CreateCrossingRunway();
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", PositionOnRunway(runway, 3000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new LandingPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(crossing, -500), altitude: 100, isOnGround: false);
        SetPhase(follower, crossing, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 81, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(crossing, 100);
        SetPhase(follower, crossing, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 82, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §3-10-4", notice.RuleReference);
        Assert.Contains("intersecting-runway", notice.Title);
        Assert.Contains("clear of runway or passed the intersection", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ConvergingDepartureBehindDepartureBeforeProjectedIntersection_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var converging = CreateProjectedConvergingRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 3000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(converging, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, converging, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 85, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 5000);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, converging, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 86, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Equal("28R/16", notice.RunwayId);
        Assert.Contains("7110.65 §3-9-9", notice.RuleReference);
        Assert.Contains("converging-runway", notice.Title);
        Assert.Contains("projected intersection", notice.RequiredText);
        Assert.Contains("before converging-runway separation existed", notice.Description);
    }

    [Fact]
    public void Evaluate_ConvergingDepartureBehindDepartureAfterProjectedIntersection_DoesNotRecordViolation()
    {
        var runway = CreateRunway();
        var converging = CreateProjectedConvergingRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 3000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(converging, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, converging, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 87, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 9500);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, converging, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 88, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 88, new ApproachReportData([], [], 88, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_ConvergingDepartureBehindLandingBeforeProjectedIntersection_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var converging = CreateProjectedConvergingRunway();
        var lead = CreateAircraft("N111AA", "C172", flightRules: "VFR", PositionOnRunway(runway, 5000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new LandingPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(converging, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, converging, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 89, AirspaceDatabase.Default);

        SetPhase(follower, converging, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 90, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §3-9-9", notice.RuleReference);
        Assert.Contains("Departure behind landing converging-runway", notice.Title);
        Assert.Contains("clear of runway or passed the projected intersection", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ConvergingArrivalBehindDepartureBeforeProjectedIntersection_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var converging = CreateProjectedConvergingRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 5000), altitude: 500, isOnGround: false);
        SetPhase(lead, runway, new InitialClimbPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(converging, -500), altitude: 100, isOnGround: false);
        SetPhase(follower, converging, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 91, AirspaceDatabase.Default);

        follower.Position = PositionOnRunway(converging, 100);
        SetPhase(follower, converging, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 92, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §3-10-4", notice.RuleReference);
        Assert.Contains("Arrival behind departure converging-runway", notice.Title);
        Assert.Contains("passed the projected intersection/flight path", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ConvergingRunwayViolation_DedupesAcrossRepeatedTicks()
    {
        var runway = CreateRunway();
        var converging = CreateProjectedConvergingRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 3000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(converging, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, converging, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 93, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 5000);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, converging, new TakeoffPhase());

        var first = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 94, AirspaceDatabase.Default);
        var second = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 95, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 95, new ApproachReportData([], [], 95, "N/A"), AircraftDebriefContext.Empty);

        Assert.Single(first, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Empty(second);
        Assert.Single(report.Timeline, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Single(report.ActiveEvents, e => e.Category == SoloTrainingEventCategory.RunwayWake);
    }

    [Fact]
    public void Evaluate_NonIntersectingConvergingRunways_DoNotRecordRunwayConflict()
    {
        var runway = CreateRunway();
        var converging = CreateNonIntersectingConvergingRunway();
        var lead = CreateAircraft("AAL1", "B738", flightRules: "IFR", PositionOnRunway(runway, 2000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("UAL2", "B738", flightRules: "IFR", PositionOnRunway(converging, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, converging, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 83, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 2500);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, converging, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 84, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 84, new ApproachReportData([], [], 84, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_DepartureWakeIntervalBeforeRequiredTime_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        var notice = Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §3-9-6(f)", notice.RuleReference);
        Assert.Contains("2 minutes", notice.RequiredText);
        Assert.Contains("60 seconds", notice.ActualText);
    }

    [Fact]
    public void Evaluate_DepartureWakeIntervalWithoutCwtProof_RecordsAdvisoryEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);

        Assert.Contains(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake && e.Title == "Departure wake interval");
        var advisory = Assert.Single(
            notices,
            e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing"
        );
        Assert.Equal(follower.Callsign, advisory.Callsigns[0]);
        Assert.Equal(lead.Callsign, advisory.Callsigns[1]);
        Assert.Contains("7110.65 §2-1-20", advisory.RuleReference);
    }

    [Fact]
    public void Evaluate_CwtProofSuppressesSingleWakeAdvisoryContext()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);
        evaluator.RecordControllerCommand(
            follower,
            SingleCommand(new ClearedForTakeoffCommand(new DefaultDeparture()) { CautionWakeTurbulence = true }),
            159,
            [lead, follower]
        );

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);

        Assert.Contains(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake && e.Title == "Departure wake interval");
        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing");
    }

    [Fact]
    public void Evaluate_BareCwtProofSuppressesCurrentSingleWakeAdvisoryContext()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var first = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        Assert.Contains(first, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing");

        evaluator.RecordControllerCommand(follower, SingleCommand(new WakeAdvisoryCommand()), scenarioElapsedSeconds: 161, [lead, follower]);
        var second = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 162, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 162, new ApproachReportData([], [], 162, "N/A"), AircraftDebriefContext.Empty);

        Assert.DoesNotContain(second, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing");
        Assert.DoesNotContain(
            report.ActiveEvents,
            e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing"
        );
    }

    [Fact]
    public void Evaluate_EarlyBareCwtProofDoesNotSuppressLaterWakeAdvisoryContext()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);
        evaluator.RecordControllerCommand(follower, SingleCommand(new WakeAdvisoryCommand()), scenarioElapsedSeconds: 120, [lead, follower]);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);

        Assert.Contains(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing");
    }

    [Fact]
    public void Evaluate_QueuedCwtProofDoesNotCountBeforeDispatch()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);
        var queuedCommand = new CompoundCommand([new ParsedBlock(null, [new WaitCommand(10)]), new ParsedBlock(null, [new WakeAdvisoryCommand()])]);
        evaluator.RecordControllerCommand(follower, queuedCommand, scenarioElapsedSeconds: 159, [lead, follower]);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);

        Assert.Contains(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing");
    }

    [Fact]
    public void Evaluate_WakeDirectiveSuppressesMatchingWakeIntervalButKeepsAdvisoryRequirement()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 100,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(SuppressWakeIntervalRule())
        );

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 160,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(SuppressWakeIntervalRule())
        );

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake && e.Title == "Departure wake interval");
        var advisory = Assert.Single(
            notices,
            e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing"
        );
        Assert.Equal(follower.Callsign, advisory.Callsigns[0]);
        Assert.Equal(lead.Callsign, advisory.Callsigns[1]);
        Assert.Contains("facility directive", advisory.RuleReference);
    }

    [Fact]
    public void Evaluate_WakeDirectiveDoesNotSuppressNonMatchingCwtCategory()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 100,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(NonMatchingCwtRule())
        );

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 160,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(NonMatchingCwtRule())
        );

        Assert.Contains(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake && e.Title == "Departure wake interval");
    }

    [Fact]
    public void Evaluate_WakeDirectiveRequiresAdvisoryWhenWakeIntervalIsSatisfied()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 100,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(RequireWakeAdvisoryRule())
        );

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 230,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(RequireWakeAdvisoryRule())
        );

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Single(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing");
    }

    [Fact]
    public void Evaluate_WakeDirectiveAdvisoryIsSuppressedByCwtProof()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 100,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(RequireWakeAdvisoryRule())
        );
        evaluator.RecordControllerCommand(
            follower,
            SingleCommand(new ClearedForTakeoffCommand(new DefaultDeparture()) { CautionWakeTurbulence = true }),
            scenarioElapsedSeconds: 229,
            [lead, follower]
        );

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 230,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(RequireWakeAdvisoryRule())
        );

        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing");
    }

    [Fact]
    public void Evaluate_WakeDirectiveSuppressesAdvisoryOnly()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 100,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(SuppressWakeAdvisoryRule())
        );

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate(
            [lead, follower],
            scenarioElapsedSeconds: 160,
            AirspaceDatabase.Default,
            WakeDirectiveServiceContext(SuppressWakeAdvisoryRule())
        );

        Assert.Contains(notices, e => e.Category == SoloTrainingEventCategory.RunwayWake && e.Title == "Departure wake interval");
        Assert.DoesNotContain(notices, e => e.Category == SoloTrainingEventCategory.AdvisoryVisual && e.Title == "Wake turbulence advisory missing");
    }

    [Fact]
    public void Evaluate_DepartureWakeIntervalAfterRequiredTime_DoesNotRecordViolation()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 221, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 221, new ApproachReportData([], [], 221, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_DepartureWakeIntervalBeforeRequiredTime_DoesNotRecordWhenCwtDistanceExists()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 6.0 * GeoMath.FeetPerNm);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 160, new ApproachReportData([], [], 160, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_ParallelRunwayUnderWakeThreshold_AppliesDepartureWakeInterval()
    {
        var leadRunway = CreateRunway();
        var followerRunway = CreateParallelRunway("28L", offsetFt: 1000);
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(leadRunway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, leadRunway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(followerRunway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, followerRunway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(leadRunway, leadRunway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, leadRunway, new InitialClimbPhase());
        SetPhase(follower, followerRunway, new TakeoffPhase());

        var notice = Assert.Single(
            evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default),
            e => e.Category == SoloTrainingEventCategory.RunwayWake
        );
        Assert.Contains("7110.65 §3-9-6(f)", notice.RuleReference);
        Assert.Contains("parallel runways less than 2,500 ft", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_ParallelRunwayOutsideWakeThreshold_DoesNotApplyDepartureWakeInterval()
    {
        var leadRunway = CreateRunway();
        var followerRunway = CreateParallelRunway("28L", offsetFt: 3000);
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(leadRunway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, leadRunway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(followerRunway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, followerRunway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(leadRunway, leadRunway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, leadRunway, new InitialClimbPhase());
        SetPhase(follower, followerRunway, new TakeoffPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 160, new ApproachReportData([], [], 160, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_IntersectionDepartureWakeInterval_UsesSection397()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("UAL1", "B752", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notice = Assert.Single(
            evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 220, AirspaceDatabase.Default),
            e => e.Category == SoloTrainingEventCategory.RunwayWake
        );
        Assert.Contains("7110.65 §3-9-7(a)", notice.RuleReference);
        Assert.Contains("3 minutes", notice.RequiredText);
        Assert.Contains("120 seconds", notice.ActualText);
    }

    [Fact]
    public void Evaluate_IntersectionDepartureWakeIntervalWithoutMileAlternative_ReportsTimeOnly()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("FFT1", "A320", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("N222BB", "C172", flightRules: "VFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var notice = Assert.Single(
            evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default),
            e => e.Category == SoloTrainingEventCategory.RunwayWake
        );
        Assert.Contains("7110.65 §3-9-7(a)", notice.RuleReference);
        Assert.Contains("3 minutes", notice.RequiredText);
        Assert.DoesNotContain("CWT spacing", notice.RequiredText);
    }

    [Fact]
    public void Evaluate_TerminalApproachCwtSpacingInsideRequiredDistance_RecordsRunwayWakeSafetyEvent()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("UAE1", "A388", flightRules: "IFR", PositionOnRunway(runway, -100), altitude: 100, isOnGround: false);
        SetPhase(lead, runway, new FinalApproachPhase());
        var follower = CreateAircraft(
            "SWA2",
            "B738",
            flightRules: "IFR",
            PositionOnRunway(runway, -6.0 * GeoMath.FeetPerNm),
            altitude: 700,
            isOnGround: false
        );
        SetPhase(follower, runway, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 300, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 0);
        SetPhase(lead, runway, new LandingPhase());

        var notice = Assert.Single(
            evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 301, AirspaceDatabase.Default),
            e => e.Category == SoloTrainingEventCategory.RunwayWake
        );
        Assert.Equal(SoloTrainingEventCategory.RunwayWake, notice.Category);
        Assert.Contains("7110.65 §5-5-4(h)", notice.RuleReference);
        Assert.Contains("7 NM", notice.RequiredText);
        Assert.Contains("6.0 NM", notice.ActualText);
    }

    [Fact]
    public void Evaluate_TerminalApproachCwtSpacingAtRequiredDistance_DoesNotRecordViolation()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("UAE1", "A388", flightRules: "IFR", PositionOnRunway(runway, -100), altitude: 100, isOnGround: false);
        SetPhase(lead, runway, new FinalApproachPhase());
        var follower = CreateAircraft(
            "SWA2",
            "B738",
            flightRules: "IFR",
            PositionOnRunway(runway, -8.0 * GeoMath.FeetPerNm),
            altitude: 700,
            isOnGround: false
        );
        SetPhase(follower, runway, new FinalApproachPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 300, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, 0);
        SetPhase(lead, runway, new LandingPhase());

        var notices = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 301, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 301, new ApproachReportData([], [], 301, "N/A"), AircraftDebriefContext.Empty);
        Assert.Empty(notices);
        Assert.Empty(report.Timeline);
    }

    [Fact]
    public void Evaluate_WakeIntervalViolation_DedupesAcrossRepeatedTicks()
    {
        var runway = CreateRunway();
        var lead = CreateAircraft("BAW1", "B744", flightRules: "IFR", PositionOnRunway(runway, 1000), altitude: 10, isOnGround: true);
        SetPhase(lead, runway, new TakeoffPhase());
        var follower = CreateAircraft("SWA2", "B738", flightRules: "IFR", PositionOnRunway(runway, 0), altitude: 10, isOnGround: true);
        SetPhase(follower, runway, new LinedUpAndWaitingPhase());
        var evaluator = new SoloTrainingEvaluator();
        evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 100, AirspaceDatabase.Default);

        lead.Position = PositionOnRunway(runway, runway.LengthFt + 100);
        lead.IsOnGround = false;
        SetPhase(lead, runway, new InitialClimbPhase());
        SetPhase(follower, runway, new TakeoffPhase());

        var first = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 160, AirspaceDatabase.Default);
        var second = evaluator.Evaluate([lead, follower], scenarioElapsedSeconds: 161, AirspaceDatabase.Default);
        var report = evaluator.BuildReport(true, 161, new ApproachReportData([], [], 161, "N/A"), AircraftDebriefContext.Empty);

        Assert.Single(first, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Empty(second);
        Assert.Single(report.Timeline, e => e.Category == SoloTrainingEventCategory.RunwayWake);
        Assert.Single(report.ActiveEvents, e => e.Category == SoloTrainingEventCategory.RunwayWake);
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
            HasMadeInitialContact = true,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                AircraftType = type,
                FlightRules = flightRules,
            },
        };

    private static (AircraftState A, AircraftState B) CreateConflictingIfrPair()
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
        return (a, b);
    }

    private static (AircraftState A, AircraftState B) CreateClosingIfrPair()
    {
        var a = CreateAircraft("AAL1", "B738", flightRules: "IFR", new LatLon(37.6213, -122.3790), altitude: 5000, isOnGround: false);
        var b = CreateAircraft(
            "UAL2",
            "A320",
            flightRules: "IFR",
            GeoMath.ProjectPoint(a.Position, new TrueHeading(90), 5.0),
            altitude: 5000,
            isOnGround: false
        );
        a.TrueHeading = new TrueHeading(90);
        a.TrueTrack = new TrueHeading(90);
        b.TrueHeading = new TrueHeading(270);
        b.TrueTrack = new TrueHeading(270);
        return (a, b);
    }

    private static SoloTrainingServiceContext WakeDirectiveServiceContext(params WakeDirectiveRule[] rules) =>
        new(new InitialContactEligibilityContext(null, null, "ZOA", "KOAK", InitialContactTransferCatalog.Empty), new WakeDirectiveCatalog(rules));

    private static WakeDirectiveRule SuppressWakeIntervalRule() =>
        WakeDirectiveRule("test-suppress-wake-interval", WakeDirectiveEffect.SuppressWakeInterval, WakeDirectiveEffect.RequireWakeAdvisory);

    private static WakeDirectiveRule RequireWakeAdvisoryRule() =>
        WakeDirectiveRule("test-require-wake-advisory", WakeDirectiveEffect.RequireWakeAdvisory);

    private static WakeDirectiveRule SuppressWakeAdvisoryRule() =>
        WakeDirectiveRule("test-suppress-wake-advisory", WakeDirectiveEffect.SuppressWakeAdvisory);

    private static WakeDirectiveRule NonMatchingCwtRule() =>
        new()
        {
            Id = "test-nonmatching-cwt",
            ArtccId = "ZOA",
            AirportId = "KOAK",
            Runways = ["28R"],
            Operation = WakeDirectiveOperation.DepartureBehindDeparture,
            Relation = WakeDirectiveRelation.SameRunway,
            PrecedingCwt = ['B'],
            SucceedingCwt = ['I'],
            SourceRuleReferences = ["7110.65 §3-9-6(f)"],
            Effects = [WakeDirectiveEffect.SuppressWakeInterval],
            RuleReference = "7110.65 §2-1-20; facility directive",
            Notes = "Unit test directive",
        };

    private static WakeDirectiveRule WakeDirectiveRule(string id, params WakeDirectiveEffect[] effects) =>
        new()
        {
            Id = id,
            ArtccId = "ZOA",
            AirportId = "KOAK",
            Runways = ["28R"],
            Operation = WakeDirectiveOperation.DepartureBehindDeparture,
            Relation = WakeDirectiveRelation.SameRunway,
            PrecedingCwt = ['B'],
            SucceedingCwt = ['F'],
            SourceRuleReferences = ["7110.65 §3-9-6(f)"],
            Effects = [.. effects],
            RuleReference = "7110.65 §2-1-20; facility directive",
            Notes = "Unit test directive",
        };

    private static CompoundCommand SingleCommand(ParsedCommand command) => new([new ParsedBlock(null, [command])]);

    private static SimScenarioState NewScenario(bool soloTrainingMode, double elapsedSeconds) =>
        new()
        {
            ScenarioId = "solo-training-evaluator-test",
            ScenarioName = "Solo Training Evaluator Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsedSeconds,
            SoloTrainingMode = soloTrainingMode,
        };

    private static string BuildReplayProofScenarioJson() =>
        """
            {
              "name": "Replay Proof Scenario",
              "primaryAirportId": "OAK",
              "aircraft": [
                {
                  "id": "test-ac-1",
                  "aircraftId": "AAL1",
                  "aircraftType": "B738",
                  "transponderMode": "C",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": { "lat": 37.6213, "lon": -122.3790 },
                    "altitude": 5000,
                    "heading": 90,
                    "speed": 180
                  },
                  "flightplan": {
                    "rules": "VFR",
                    "departure": "KSFO",
                    "destination": "KOAK",
                    "cruiseAltitude": 5000,
                    "cruiseSpeed": 250,
                    "route": "",
                    "remarks": "",
                    "aircraftType": "B738"
                  },
                  "presetCommands": [],
                  "spawnDelay": 0,
                  "airportId": "OAK",
                  "difficulty": "Easy"
                },
                {
                  "id": "test-ac-2",
                  "aircraftId": "UAL2",
                  "aircraftType": "A320",
                  "transponderMode": "C",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": { "lat": 37.6213, "lon": -122.3350 },
                    "altitude": 5000,
                    "heading": 270,
                    "speed": 180
                  },
                  "flightplan": {
                    "rules": "VFR",
                    "departure": "KSJC",
                    "destination": "KOAK",
                    "cruiseAltitude": 5000,
                    "cruiseSpeed": 220,
                    "route": "",
                    "remarks": "",
                    "aircraftType": "A320"
                  },
                  "presetCommands": [],
                  "spawnDelay": 0,
                  "airportId": "OAK",
                  "difficulty": "Easy"
                }
              ]
            }
            """;

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

    private static RunwayInfo CreateParallelRunway(string designator, double offsetFt)
    {
        var baseRunway = CreateRunway();
        var start = GeoMath.ProjectPoint(new LatLon(baseRunway.Lat1, baseRunway.Lon1), new TrueHeading(0), offsetFt / GeoMath.FeetPerNm);
        var end = GeoMath.ProjectPoint(new LatLon(baseRunway.Lat2, baseRunway.Lon2), new TrueHeading(0), offsetFt / GeoMath.FeetPerNm);
        return new RunwayInfo
        {
            AirportId = baseRunway.AirportId,
            Id = new RunwayIdentifier(designator, "10R"),
            Designator = designator,
            Lat1 = start.Lat,
            Lon1 = start.Lon,
            Elevation1Ft = baseRunway.Elevation1Ft,
            TrueHeading1 = baseRunway.TrueHeading1,
            Lat2 = end.Lat,
            Lon2 = end.Lon,
            Elevation2Ft = baseRunway.Elevation2Ft,
            TrueHeading2 = baseRunway.TrueHeading2,
            LengthFt = baseRunway.LengthFt,
            WidthFt = baseRunway.WidthFt,
        };
    }

    private static RunwayInfo CreateCrossingRunway() =>
        new()
        {
            AirportId = "KOAK",
            Id = new RunwayIdentifier("18", "36"),
            Designator = "18",
            Lat1 = 37.735000,
            Lon1 = -122.205000,
            Elevation1Ft = 10,
            TrueHeading1 = new TrueHeading(180),
            Lat2 = 37.707600,
            Lon2 = -122.205000,
            Elevation2Ft = 10,
            TrueHeading2 = new TrueHeading(0),
            LengthFt = 10000,
            WidthFt = 150,
        };

    private static RunwayInfo CreateProjectedConvergingRunway() =>
        new()
        {
            AirportId = "KOAK",
            Id = new RunwayIdentifier("16", "34"),
            Designator = "16",
            Lat1 = 37.735000,
            Lon1 = -122.205000,
            Elevation1Ft = 10,
            TrueHeading1 = new TrueHeading(160),
            Lat2 = 37.725000,
            Lon2 = -122.195000,
            Elevation2Ft = 10,
            TrueHeading2 = new TrueHeading(340),
            LengthFt = 10000,
            WidthFt = 150,
        };

    private static RunwayInfo CreateNonIntersectingConvergingRunway() =>
        new()
        {
            AirportId = "KOAK",
            Id = new RunwayIdentifier("16", "34"),
            Designator = "16",
            Lat1 = 37.735000,
            Lon1 = -122.245000,
            Elevation1Ft = 10,
            TrueHeading1 = new TrueHeading(160),
            Lat2 = 37.707600,
            Lon2 = -122.215000,
            Elevation2Ft = 10,
            TrueHeading2 = new TrueHeading(340),
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
