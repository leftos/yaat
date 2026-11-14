using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub #1380 (YAAT-2285): in solo training an arrival the student has cleared for the approach (a deferred CAPP held
/// until the STAR delivers it) or told what approach to expect (EAPP) still called "request approach assignment". A held
/// clearance counts as having an approach, and a controller's EAPP marks the arrival as told what to expect; the
/// scenario's seeded expected approach does not. Replays the reporter's S3 FAT 3 session at KFAT.
/// </summary>
public class Issue1380ApproachAssignmentRequestTests
{
    private const string RecordingPath = "TestData/issue1380-capp-eapp-approach-request-recording.yaat-bug-report-bundle.zip";

    /// <summary>The recording's last second.</summary>
    private const int RecordingEndSeconds = 467;

    private const string ApproachRequestTerminal = "request approach assignment.";

    private readonly ITestOutputHelper _output;

    public Issue1380ApproachAssignmentRequestTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void N160TM_DeferredCappI29ry_NeverRequestsApproachAssignment()
    {
        // EAPP I29RY at 25 s, AT ALTTA CAPP I29RY at 42 s: the clearance is held until the STAR delivers the aircraft.
        List<double> calls = ReplayApproachRequestTimes("N160TM");
        Assert.True(calls.Count == 0, $"N160TM requested an approach assignment at t={string.Join(", ", calls)}");
    }

    [Fact]
    public void N172YY_EappI29ry_NeverRequestsApproachAssignment()
    {
        // EAPP I29RY at 62 s, then vectors: the pilot has been told what approach to expect.
        List<double> calls = ReplayApproachRequestTimes("N172YY");
        Assert.True(calls.Count == 0, $"N172YY requested an approach assignment at t={string.Join(", ", calls)}");
    }

    [Fact]
    public void ExpectApproach_AfterTheRequest_SatisfiesIt_NeverReasked()
    {
        AircraftState aircraft = ArrivalNearKoak();
        SimScenarioState scenario = SoloApproachScenario();
        TickApproachRequestAt(aircraft, scenario, 100);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Approach, request.Kind);
        Assert.Single(aircraft.PendingPilotTransmissions);

        // In SimulationEngine.AnswerAcceptedCommand's order: the stamp, then the answer.
        CompoundCommand eapp = Compound(new ExpectApproachCommand("I30", null));
        PilotRequestTracker.StampExpectedApproach(aircraft, eapp, 110);
        PilotRequestTracker.ApplyControllerResponse(aircraft, eapp, 110);

        Assert.Equal(PilotPendingRequestResponseState.Satisfied, request.ResponseState);
        Assert.Equal(110, aircraft.ExpectedApproachAdvisedAtSeconds);
        TickApproachRequestAt(aircraft, scenario, 400);
        Assert.Same(request, aircraft.PendingPilotRequest);
        Assert.Single(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void SeededExpectedApproach_NoControllerEapp_StillRequests()
    {
        // The scenario's primaryApproach seeds Approach.Expected (ScenarioLoader) on an arrival nobody has spoken to.
        AircraftState aircraft = ArrivalNearKoak();
        aircraft.Approach.Expected = "I30";

        TickApproachRequestAt(aircraft, SoloApproachScenario(), 400);

        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Approach, request.Kind);
        Assert.Null(aircraft.ExpectedApproachAdvisedAtSeconds);
    }

    [Fact]
    public void HeldClearance_DroppedByALaterRunwayAssignment_RequestsAgain()
    {
        AircraftState aircraft = ArrivalNearKoak();
        aircraft.Approach.PendingClearance = HeldI30();
        SimScenarioState scenario = SoloApproachScenario();

        Assert.True(PilotProactive.HasApproachClearance(aircraft));
        TickApproachRequestAt(aircraft, scenario, 100);
        Assert.Null(aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);

        DispatchOk(new AssignRunwayCommand("28R"), aircraft);
        Assert.Null(aircraft.Approach.PendingClearance);
        Assert.False(PilotProactive.HasApproachClearance(aircraft));

        TickApproachRequestAt(aircraft, scenario, 110);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Approach, request.Kind);
    }

    [Fact]
    public void ExpectApproach_ThenRunwayChangeToAnother_RequestsAgain()
    {
        AircraftState aircraft = ExpectI30AtKoak();

        DispatchOk(new AssignRunwayCommand("28R"), aircraft);

        Assert.Null(aircraft.ExpectedApproachAdvisedAtSeconds);
        Assert.Equal("I30", aircraft.Approach.Expected);
        TickApproachRequestAt(aircraft, SoloApproachScenario(), 110);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Approach, request.Kind);
    }

    [Fact]
    public void ExpectApproach_ThenTheSameRunway_NoRequest()
    {
        AircraftState aircraft = ExpectI30AtKoak();

        DispatchOk(new AssignRunwayCommand("30"), aircraft);

        Assert.Equal(100, aircraft.ExpectedApproachAdvisedAtSeconds);
        TickApproachRequestAt(aircraft, SoloApproachScenario(), 110);
        Assert.Null(aircraft.PendingPilotRequest);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void ExpectApproach_FollowedByADestinationChange_InOneCompound_DoesNotStamp()
    {
        AircraftState aircraft = ArrivalNearKoak();

        PilotRequestTracker.StampExpectedApproach(
            aircraft,
            Compound(new ExpectApproachCommand("I30", null), new ChangeDestinationCommand("KSJC")),
            100
        );

        Assert.Null(aircraft.ExpectedApproachAdvisedAtSeconds);
    }

    [Fact]
    public void DestinationChange_FollowedByAnExpectApproach_InOneCompound_Stamps()
    {
        AircraftState aircraft = ArrivalNearKoak();

        PilotRequestTracker.StampExpectedApproach(
            aircraft,
            Compound(new ChangeDestinationCommand("KSJC"), new ExpectApproachCommand("I30", null)),
            100
        );

        Assert.Equal(100, aircraft.ExpectedApproachAdvisedAtSeconds);
    }

    [Fact]
    public void OpenApproachRequest_ThenAHeldClearance_FollowUpIsSuperseded()
    {
        AircraftState aircraft = ArrivalNearKoak();
        TickApproachRequestAt(aircraft, SoloApproachScenario(), 100);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        aircraft.PendingPilotTransmissions.Clear();
        aircraft.Approach.PendingClearance = HeldI30();

        Assert.False(PilotRequestTracker.TryQueueFollowUp(aircraft, 100 + PilotRequestTracker.NormalFollowUpDelaySeconds, null, Airport));

        Assert.Equal(PilotPendingRequestResponseState.Superseded, request.ResponseState);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void OpenApproachRequest_ThenAnExpectApproachStamp_FollowUpIsSuperseded()
    {
        AircraftState aircraft = ArrivalNearKoak();
        TickApproachRequestAt(aircraft, SoloApproachScenario(), 100);
        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        aircraft.PendingPilotTransmissions.Clear();
        aircraft.ExpectedApproachAdvisedAtSeconds = 110;

        Assert.False(PilotRequestTracker.TryQueueFollowUp(aircraft, 100 + PilotRequestTracker.NormalFollowUpDelaySeconds, null, Airport));

        Assert.Equal(PilotPendingRequestResponseState.Superseded, request.ResponseState);
    }

    [Fact]
    public void ExpectApproachBeforeAVisual_UnableVisual_RequestStaysOpenAndIsReasked()
    {
        // EAPP before the CVA, then the pilot loses the field: its "unable visual, request vectors" request was opened
        // after the advice, so the advice does not close it.
        AircraftState aircraft = ArrivalNearKoak();
        aircraft.ExpectedApproachAdvisedAtSeconds = 50;
        aircraft.Phases = VisualApproachPhases();
        PhaseContext ctx = new()
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = 1,
            ActiveRunways = ActiveRunways.Empty,
            Logger = NullLogger.Instance,
            ScenarioElapsedSeconds = 100,
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

        VisualApproachHelper.EndVisualLostReference(ctx, true, false, null);

        PilotPendingRequest request = Assert.IsType<PilotPendingRequest>(aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Approach, request.Kind);
        aircraft.PendingPilotTransmissions.Clear();
        Assert.True(PilotRequestTracker.TryQueueFollowUp(aircraft, 100 + PilotRequestTracker.NormalFollowUpDelaySeconds, null, Airport));
        Assert.True(request.IsOpen);
        Assert.Equal(1, request.FollowUpsVoiced);
        Assert.Single(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void VoidedVisualApproach_HasNoApproachClearance()
    {
        // "Unable visual" records an approach request and voids the clearance; the voided arrival has no approach.
        AircraftState aircraft = ArrivalNearKoak();
        aircraft.Phases = VisualApproachPhases();
        Assert.True(PilotProactive.HasApproachClearance(aircraft));

        VisualApproachHelper.VoidVisualApproach(aircraft);

        Assert.False(PilotProactive.HasApproachClearance(aircraft));
    }

    [Fact]
    public void ExpectedApproachStamp_SurvivesASnapshotRoundTrip()
    {
        AircraftState aircraft = ArrivalNearKoak();
        aircraft.ExpectedApproachAdvisedAtSeconds = 321.0;

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), null);

        Assert.Equal(321.0, restored.ExpectedApproachAdvisedAtSeconds);
    }

    [Fact]
    public void ExpectedApproachStamp_AbsentFromAnOlderSnapshot_RestoresNone()
    {
        AircraftState aircraft = ArrivalNearKoak();
        string json = JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(AircraftSnapshotDto.ExpectedApproachAdvisedAtSeconds), json);

        AircraftSnapshotDto fromJson = Assert.IsType<AircraftSnapshotDto>(
            JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)
        );
        Assert.Null(AircraftState.FromSnapshot(fromJson, null).ExpectedApproachAdvisedAtSeconds);
    }

    /// <summary>
    /// Replays the recording in solo training from 0 s to its end and returns the times <paramref name="callsign"/> called
    /// "request approach assignment".
    /// </summary>
    private List<double> ReplayApproachRequestTimes(string callsign)
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        Assert.NotNull(archive);
        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            var groundData = RecordingGroundData.Evergreen("approach-assignment requests over the S3 FAT 3 session");
            SimLogBuilder.CreateForTest(_output).InitializeSimLog();
            SimulationEngine engine = new(groundData, PushPlanStore.CreateShipped());
            engine.Replay(recording, 0);
            SimScenarioState scenario = Assert.IsType<SimScenarioState>(engine.Scenario);
            scenario.SoloTrainingMode = true;
            Assert.True(scenario.PilotContacts.AnyAnswering, "Nobody answers pilots in the recording");
            Assert.Equal("APP", scenario.StudentPositionType);

            double? lastAt = engine.World.GetSnapshot().FirstOrDefault(a => a.Callsign == callsign)?.LastPilotTransmission?.AtSeconds;
            List<double> calls = [];
            bool seen = false;
            for (int t = 1; t <= RecordingEndSeconds; t++)
            {
                engine.FastForwardTo(t, recording.Actions);
                if (engine.World.GetSnapshot().FirstOrDefault(a => a.Callsign == callsign) is not { } aircraft)
                {
                    continue;
                }

                seen = true;
                if ((aircraft.LastPilotTransmission is { } line) && (line.AtSeconds != lastAt))
                {
                    lastAt = line.AtSeconds;
                    if (line.Text.EndsWith(ApproachRequestTerminal, StringComparison.Ordinal))
                    {
                        _output.WriteLine($"t={t}: {line.Text}");
                        calls.Add(t);
                    }
                }
            }

            Assert.True(seen, $"{callsign} is not in the recording");
            return calls;
        }
    }

    private static CompoundCommand Compound(params ParsedCommand[] commands) => new([new ParsedBlock(null, [.. commands])]);

    private static void DispatchOk(ParsedCommand command, AircraftState aircraft)
    {
        CommandResult result = CommandDispatcher.Dispatch(command, aircraft, TestDispatch.Context(new Random(1)));
        Assert.True(result.Success, result.Message);
    }

    /// <summary>The arrival near KOAK told to expect the ILS 30 by an accepted EAPP at 100 s.</summary>
    private static AircraftState ExpectI30AtKoak()
    {
        AircraftState aircraft = ArrivalNearKoak();
        ExpectApproachCommand eapp = new("I30", null);
        DispatchOk(eapp, aircraft);
        PilotRequestTracker.StampExpectedApproach(aircraft, Compound(eapp), 100);
        Assert.Equal(100, aircraft.ExpectedApproachAdvisedAtSeconds);
        return aircraft;
    }

    private static RunwayInfo KoakRunway30() => Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("KOAK", "30"));

    private static ApproachClearance ClearanceTo30(string approachId) =>
        new()
        {
            TransitionName = null,
            ApproachId = approachId,
            AirportCode = "KOAK",
            RunwayId = "30",
            FinalApproachCourse = KoakRunway30().TrueHeading,
        };

    /// <summary>The ILS 30 at KOAK held as a deferred clearance.</summary>
    private static PendingApproachInfo HeldI30() =>
        new()
        {
            Clearance = ClearanceTo30("I30"),
            AssignedRunway = KoakRunway30(),
            ConnectingFix = null,
        };

    /// <summary>A visual approach to KOAK runway 30, cleared and active, with no phase running.</summary>
    private static PhaseList VisualApproachPhases() => new() { AssignedRunway = KoakRunway30(), ActiveApproach = ClearanceTo30("VIS30") };

    private static void TickApproachRequestAt(AircraftState aircraft, SimScenarioState scenario, double nowSeconds)
    {
        scenario.ElapsedSeconds = nowSeconds;
        PilotProactive.TickArrivalApproachRequest(aircraft, scenario, Airport);
    }

    /// <summary>An IFR B738 8 NM east of KOAK at 5,000 ft, on the student's frequency, with no approach.</summary>
    private static AircraftState ArrivalNearKoak() =>
        new()
        {
            Callsign = "UAL325",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(Assert.IsType<LatLon>(Airport("KOAK")), new TrueHeading(90), 8.0),
            TrueHeading = new TrueHeading(270),
            Altitude = 5000,
            IndicatedAirspeed = 200,
            IsOnGround = false,
            HasMadeInitialContact = true,
            HasControllerAcknowledgedInitialContact = true,
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR", Destination = "KOAK" },
        };

    private static SimScenarioState SoloApproachScenario() =>
        new()
        {
            ScenarioId = "issue1380-approach-request",
            ScenarioName = "Approach-assignment request",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

    private static LatLon? Airport(string id) => NavigationDatabase.Instance.GetAirportPosition(id) is { } pos ? new LatLon(pos.Lat, pos.Lon) : null;
}
