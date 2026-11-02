using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// What the pilot reports on taking a taxiway hold short, judged against where it actually came to rest (YAAT-1418): stopped
/// short of the hold-short point (or on it) it says "holding short of {X} at {T}"; stopped beyond it, it never says it is holding
/// short. Beyond it with no unable call made it says "unable to hold short of {X}, holding position on {T}"; beyond it after the
/// unable call it says only "holding position on {T}". {T} is the movement-area taxiway it stands on. The window cases are the
/// owner's YAAT-1409 ruling: a stop the firm rate makes short of the crossed taxiway's wingtip envelope moves the hold-short
/// point to where the aircraft rests, so the hold is made and reported as held short.
/// </summary>
public class HoldShortArrivalReportTests(ITestOutputHelper output)
{
    private const string SfoCallsign = "SKW9001";
    private const string Widebody = "B744";
    private const string Narrowbody = "B738";

    /// <summary>The least speed (kt) the SFO rig issues <c>HS T</c> at, rolling at taxi speed.</summary>
    private const double TaxiIssueKts = 24.0;

    /// <summary>The least speed (kt) the slow SFO rig issues <c>HS T</c> at, rolling at the <c>SPD 10</c> it was given.</summary>
    private const double SlowIssueKts = 9.0;

    /// <summary>The wingtip floor (ft) less a foot of pose noise: the nose of a hold made short of T rests at least this far from T.</summary>
    private const double NoseClearOfTFt = 155.0;

    private const string OakCallsign = "N738SP";

    /// <summary>The taxi from spot I8R down C onto A.</summary>
    private const string TaxiCToA = "TAXI C A B RWY 28R";

    /// <summary>The re-route, given with the nose just past the C/A junction, whose HS A binds at the first A node past it.</summary>
    private const string TaxiCToAHoldShortA = "TAXI A B RWY 28R HS A";

    /// <summary>A B738 at KOAK spot I8R on C (the <c>HoldInsideStoppingDistanceOfBarTests</c> OAK rig's scenario).</summary>
    private const string OakScenario = """
        {
          "id": "01JH02G8F0L1W5T7R9N2K4X6QD",
          "name": "Hold short of A taken beyond its point",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "studentPositionId": "01GEAMCGAZ418Q26GEPYNXWZ4A",
          "autoDeleteMode": "None",
          "initializationTriggers": [],
          "aircraftGenerators": [],
          "aircraft": [
            {
              "id": "01JH02N738SPG8F0L1W5T7R9ND",
              "aircraftId": "N738SP",
              "aircraftType": "B738",
              "transponderMode": "Standby",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.725692, "lon": -122.204250 }, "heading": 112 },
              "onAltitudeProfile": false,
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KLVK",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 0,
                "route": "",
                "remarks": "/V/",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ],
          "atc": [
            {
              "id": "01G9CQW3XEE3RW84EBYKEBCPQD",
              "artccId": "ZOA",
              "facilityId": "OAK",
              "positionId": "01GEAMB98RKCPP9HCNPW5AVDA5",
              "autoConnect": true,
              "autoTrackAirportIds": []
            }
          ],
          "flightStripConfigurations": []
        }
        """;

    /// <summary>One run to the hold and what it measured and heard.</summary>
    private sealed record ArrivalRun(AircraftState Aircraft, AirportGroundLayout Layout, CommandResult Issued, List<PilotSpeechText> Reports)
    {
        /// <summary>How far (ft) the centre was from the T bar's node when <c>HS T</c> was issued; NaN for a run that issued none.</summary>
        public double IssueToBarNodeFt { get; init; } = double.NaN;
    }

    /// <summary>P1: a B744 whose firm stop lies past the setback but short of T's envelope holds on the moved point and reports it.</summary>
    [Fact]
    public void P1_WidebodyWindowStop_ReportsHoldingShortOfT()
    {
        if (RunSfo(SfoScript.Plain(Widebody, 450.0, TaxiIssueKts, false)) is not { } run)
        {
            return;
        }

        Assert.Equal("Hold short of T", run.Issued.Message);
        HoldShortPoint bar = BarOfT(run);
        Assert.False(bar.Unable, "the window hold was flagged unable");
        Assert.True(bar.StopMoved, "the window stop was not moved to where the firm rate stops the aircraft");
        AssertRestsOnThePoint(run, bar);
        double noseFt = SfoHoldShortLiveSegmentTests.NoseToTFt(run.Layout, run.Aircraft.Position, run.Aircraft.TrueHeading, Widebody, 0.0);
        Assert.True(noseFt >= NoseClearOfTFt, $"held with the nose {noseFt:F1} ft from T");
        AssertReport(run, "holding short of T at B.", $", holding short of T at B.");
    }

    /// <summary>P2: the B744 one second later cannot stop short of T's envelope: unable, then at rest only "holding position on B".</summary>
    [Fact]
    public void P2_WidebodyInsideTheEnvelope_ReportsHoldingPositionOnB()
    {
        if (RunSfo(SfoScript.Plain(Widebody, 400.0, TaxiIssueKts, false)) is not { } run)
        {
            return;
        }

        Assert.Contains("Unable to hold short of T", run.Issued.Message, StringComparison.Ordinal);
        AssertReport(run, "holding position on B.", ", holding position on bravo.");
    }

    /// <summary>P3: a B738 told <c>HS T</c> far out makes the bar and reports holding short of it.</summary>
    [Fact]
    public void P3_NarrowbodyFarOut_ReportsHoldingShortOfT()
    {
        if (RunSfo(SfoScript.Plain(Narrowbody, 600.0, TaxiIssueKts, false)) is not { } run)
        {
            return;
        }

        Assert.DoesNotContain("Unable", run.Issued.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(BarOfT(run).StopMoved, "a bar made from far out had its stop moved");
        AssertReport(run, "holding short of T at B.", ", holding short of T at B.");
    }

    /// <summary>P4: a B738 told <c>HS T</c> at the bar, its nose already inside T's floor: unable, then "holding position on B".</summary>
    [Fact]
    public void P4_NarrowbodyAtTheBar_ReportsHoldingPositionOnB()
    {
        if (RunSfo(SfoScript.Plain(Narrowbody, 220.0, TaxiIssueKts, false)) is not { } run)
        {
            return;
        }

        Assert.Contains("Unable", run.Issued.Message, StringComparison.OrdinalIgnoreCase);
        AssertReport(run, "holding position on B.", ", holding position on bravo.");
    }

    /// <summary>
    /// P5: OAK, re-routed <c>TAXI A B RWY 28R HS A</c> with the nose just past C/A: HS A has no envelope (A runs straight on with no
    /// other branch), the stop is where the aircraft stands, and it holds beyond it with no unable call made.
    /// </summary>
    [Fact]
    public void P5_OakBeyondThePointWithNoUnableCall_ReportsUnableAndHoldingPositionOnC()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        TestVnasData.EnsureInitialized();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(OakScenario, 1, MagneticDeclination.EvaluationDateUtc);
        Assert.IsType<SimScenarioState>(engine.Scenario).RpoShowPilotSpeech = true;
        List<PilotSpeechText> reports = CaptureReports(engine, OakCallsign);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(OakCallsign));
        Assert.True(engine.SendCommand(OakCallsign, TaxiCToA).Success, $"{TaxiCToA} was rejected");
        engine.TickOneSecond();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);
        GroundNode junction = GroundNodeLookup.Junction(layout, "C", "A");
        GroundNode spotI8R = layout.FindSpotNodeByName("I8R") ?? throw new InvalidOperationException("OAK: no spot I8R");
        double approachDeg = GeoMath.BearingTo(junction.Position, spotI8R.Position);

        CommandResult? issued = null;
        double? envelopeAtIssueFt = double.NaN;
        for (int second = 0; (second < 400) && (issued is null); second++)
        {
            if (NosePastFt(aircraft, junction, approachDeg) >= 0.0)
            {
                issued = engine.SendCommand(OakCallsign, TaxiCToAHoldShortA);
                output.WriteLine($"{TaxiCToAHoldShortA} at {aircraft.GroundSpeed:F1} kt: {issued.Success} — {issued.Message}");
                TaxiRoute issuedRoute = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
                HoldShortPoint issuedBar = Assert.Single(issuedRoute.HoldShortPoints, h => h.TargetName == "A");
                envelopeAtIssueFt = HoldShortAnnotator.EnvelopeEntryAheadFt(
                    layout,
                    issuedRoute,
                    issuedBar,
                    AircraftLength.ResolveFt(aircraft.AircraftType),
                    aircraft.Position
                );
            }

            engine.TickOneSecond();
        }

        Assert.True(issued is { Success: true }, $"{TaxiCToAHoldShortA} never issued or refused: {issued?.Message}");
        Assert.Null(envelopeAtIssueFt);
        CommandResult reRouted = issued!;
        TickUntilHolding(engine, aircraft);
        HoldShortPoint bar = Assert.Single(Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute).HoldShortPoints, h => h.TargetName == "A");
        Assert.False(bar.Unable, "HS A was flagged unable");
        AssertReport(
            new ArrivalRun(aircraft, layout, reRouted, reports),
            "unable to hold short of A, holding position on C.",
            ", unable to hold short of alpha, holding position on charlie."
        );
    }

    /// <summary>
    /// The P5 rig with an AI ground answering at OAK and a preset taxi ending at the A hold short: the aircraft comes to rest beyond
    /// the bar, so the beyond report is its only arrival report and the delayed call-up follows from where it stopped — "on taxiway C",
    /// never "holding short of A at C" (YAAT-1418).
    /// </summary>
    [Fact]
    public void BeyondAPresetStop_CallsUpFromTheTaxiwayItStoppedOn()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            output.WriteLine("SKIP: ZOA ARTCC config unavailable");
            return;
        }

        TestVnasData.EnsureInitialized();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(OakScenario, 1, MagneticDeclination.EvaluationDateUtc);
        SimScenarioState scenario = Assert.IsType<SimScenarioState>(engine.Scenario);
        scenario.RpoShowPilotSpeech = true;
        scenario.ArtccConfig = zoa;
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(zoa)]);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(OakCallsign));
        aircraft.Ground.InitialCallup = InitialCallupPlan.AfterTaxiArrival;
        aircraft.Ground.PresetTaxiStop = new PresetTaxiStop(PresetTaxiStopKind.TaxiwayHoldShort, "A", null);
        List<PilotSpeechText> speech = CaptureSpeech(engine, OakCallsign);
        List<TerminalEntry> said = CaptureTerminalLines(engine, OakCallsign);
        Assert.True(engine.SendCommand(OakCallsign, TaxiCToA).Success, $"{TaxiCToA} was rejected");
        engine.TickOneSecond();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);
        GroundNode junction = GroundNodeLookup.Junction(layout, "C", "A");
        GroundNode spotI8R = layout.FindSpotNodeByName("I8R") ?? throw new InvalidOperationException("OAK: no spot I8R");
        double approachDeg = GeoMath.BearingTo(junction.Position, spotI8R.Position);

        CommandResult? issued = null;
        for (int second = 0; (second < 400) && (issued is null); second++)
        {
            if (NosePastFt(aircraft, junction, approachDeg) >= 0.0)
            {
                issued = engine.SendCommand(OakCallsign, TaxiCToAHoldShortA);
                output.WriteLine($"{TaxiCToAHoldShortA} at {aircraft.GroundSpeed:F1} kt: {issued.Success} — {issued.Message}");
            }

            engine.TickOneSecond();
        }

        Assert.True(issued is { Success: true }, $"{TaxiCToAHoldShortA} never issued or refused: {issued?.Message}");
        TickUntilHolding(engine, aircraft);
        for (int second = 0; second < 25; second++)
        {
            engine.TickOneSecond();
        }

        foreach (PilotSpeechText line in speech)
        {
            output.WriteLine($"said: '{line.TerminalForRpo}' / '{line.Tts}'");
        }

        foreach (TerminalEntry line in said)
        {
            output.WriteLine($"terminal {line.Kind}: '{line.Message}'");
        }

        List<TerminalEntry> callups = [.. said.Where(e => e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase))];
        Assert.True(
            callups.Count == 1,
            $"the delayed call-up should come once from the bar; heard {callups.Count}: {string.Join(" | ", said.Select(e => e.Message))}"
        );
        Assert.Contains("on taxiway C", callups[0].Message, StringComparison.Ordinal);
        Assert.Single(speech, s => s.Terminal.Contains("holding position on C", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(speech, s => s.Terminal.Contains("holding short", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(said, e => e.Message.Contains("holding short", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>P6: the B744 at 10 kt, told <c>HS T</c> already past the setback point: a window stop, held on the moved point.</summary>
    [Fact]
    public void P6_WidebodyAtTenKnotsPastTheSetback_ReportsHoldingShortOfT()
    {
        if (RunSfo(SfoScript.Plain(Widebody, 350.0, SlowIssueKts, true)) is not { } run)
        {
            return;
        }

        double lengthSetbackFt = AircraftLength.ResolveFt(Widebody) + HoldShortAnnotator.TaxiwayHoldShortBufferFt;
        Assert.True(
            run.IssueToBarNodeFt < lengthSetbackFt,
            $"staging moved: HS T was issued {run.IssueToBarNodeFt:F1} ft from the bar's node, not past its {lengthSetbackFt:F1} ft setback"
        );
        Assert.Equal("Hold short of T", run.Issued.Message);
        HoldShortPoint bar = BarOfT(run);
        Assert.False(bar.Unable, "the window hold was flagged unable");
        Assert.True(bar.StopMoved, "the window stop was not moved to where the aircraft stops");
        AssertRestsOnThePoint(run, bar);
        AssertReport(run, "holding short of T at B.", ", holding short of T at B.");
    }

    /// <summary>
    /// P7: P2's hold at rest, snapshotted and restored, carries its verdict; a snapshot with no verdict (one written before the field
    /// existed, or of a hold taken short of its point) restores as held short.
    /// </summary>
    [Fact]
    public void P7_Snapshot_CarriesTheVerdict_AndAbsentRestoresAsHeldShort()
    {
        if (RunSfo(SfoScript.Plain(Widebody, 400.0, TaxiIssueKts, false)) is not { } run)
        {
            return;
        }

        HoldingShortPhase held = Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases?.CurrentPhase);
        Assert.Equal(HoldShortArrival.BeyondAfterUnableCall, held.Arrival);
        HoldingShortPhaseDto dto = Assert.IsType<HoldingShortPhaseDto>(held.ToSnapshot());
        Assert.Equal(HoldShortArrival.BeyondAfterUnableCall, HoldingShortPhase.FromSnapshot(dto).Arrival);

        var legacy = new HoldingShortPhaseDto
        {
            Status = dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            Requirements = dto.Requirements,
            HoldShortNodeId = dto.HoldShortNodeId,
            RunwayId = dto.RunwayId,
            Reason = dto.Reason,
        };
        Assert.Null(legacy.Arrival);
        Assert.Equal(HoldShortArrival.HeldShort, HoldingShortPhase.FromSnapshot(legacy).Arrival);
    }

    /// <summary>
    /// A bar the taxi phase judged against the wingtip envelope window keeps that record across a snapshot of its route, so a
    /// restored taxi never judges it again near the bar.
    /// </summary>
    [Fact]
    public void WindowJudgedBar_SurvivesARouteSnapshotRoundTrip()
    {
        if (RunSfo(SfoScript.Plain(Narrowbody, 600.0, TaxiIssueKts, false)) is not { } run)
        {
            return;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Aircraft.Ground.AssignedTaxiRoute);
        Assert.True(BarOfT(run).WindowJudged, "the T bar was never judged against the envelope window");
        TaxiRouteDto dto = route.ToSnapshot();
        Assert.True(Assert.Single(dto.HoldShortPoints!, h => h.RunwayId == "T").WindowJudged);
        TaxiRoute restored = Assert.IsType<TaxiRoute>(TaxiRoute.FromSnapshot(dto, run.Layout));
        Assert.True(Assert.Single(restored.HoldShortPoints, h => h.TargetName == "T").WindowJudged, "WindowJudged was lost on restore");
    }

    /// <summary>
    /// A later <c>HS</c> issued while the B744 brakes onto its window stop re-places the route's bars; the moved window stop is kept,
    /// so the aircraft still holds on it and reports holding short of T.
    /// </summary>
    [Fact]
    public void WindowStop_KeptWhenALaterHoldShortReplacesTheBars()
    {
        if (RunSfo(new SfoScript(Widebody, 450.0, TaxiIssueKts, Slow: false, ArmAndCrossWithinFt: null, AfterIssue: "HS K")) is not { } run)
        {
            return;
        }

        Assert.Equal("Hold short of T", run.Issued.Message);
        HoldShortPoint bar = BarOfT(run);
        Assert.True(bar.StopMoved, "the window stop was not moved");
        AssertRestsOnThePoint(run, bar);
        AssertReport(run, "holding short of T at B.", ", holding short of T at B.");
    }

    /// <summary>
    /// <c>HS T</c> far out (judged makeable), <c>CROSS T</c>, then <c>HS T</c> again inside the B744's envelope window: the re-armed
    /// bar is judged afresh, so its stop moves to the firm stop and the hold is made and reported held short.
    /// </summary>
    [Fact]
    public void ReArmedBar_IsJudgedAgainForTheWindow()
    {
        if (RunSfo(new SfoScript(Widebody, 450.0, TaxiIssueKts, Slow: false, ArmAndCrossWithinFt: 1200.0, AfterIssue: null)) is not { } run)
        {
            return;
        }

        Assert.Equal("Hold short of T", run.Issued.Message);
        HoldShortPoint bar = BarOfT(run);
        Assert.False(bar.Unable, "the re-armed window hold was flagged unable");
        Assert.True(bar.StopMoved, "the re-armed window stop was not moved");
        AssertReport(run, "holding short of T at B.", ", holding short of T at B.");
    }

    /// <summary>
    /// A B738 rolling on KOAK taxiway C told <c>HS 33</c> inside its firm stopping distance: unable, and at rest past the runway's
    /// holding position marking it names the runway it is not clear of (AIM 2-3-5.a.1), by one end.
    /// </summary>
    [Fact]
    public void RunwayBar_BeyondAfterTheUnableCall_ReportsNotClearOfTheRunway()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        TestVnasData.EnsureInitialized();
        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        SimScenarioState scenario = Assert.IsType<SimScenarioState>(engine.Scenario);
        scenario.RpoShowPilotSpeech = true;
        scenario.SoloTrainingMode = false;
        const string Callsign = "N401YA";
        List<PilotSpeechText> reports = CaptureReports(engine, Callsign);
        GroundNode bar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "33", "C") ?? throw new InvalidOperationException("KOAK: no 33 bar on C");
        List<GroundEdge> barEdges = [.. bar.Edges.OfType<GroundEdge>().Where(e => e.MatchesTaxiway("C")).OrderByDescending(e => e.DistanceNm)];
        GroundNode behind = barEdges[0].OtherNode(bar);
        GroundNode pastTheBar = barEdges[^1].OtherNode(bar);
        double bearingDeg = GeoMath.BearingTo(behind.Position, bar.Position);
        AircraftState aircraft = SpawnRollingOnC(engine, layout, Callsign, (behind.Position, new TrueHeading(bearingDeg)), 24.0);
        Assert.True(engine.SendCommand(Callsign, $"TAXI #{behind.Id} #{pastTheBar.Id}").Success);

        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        double firmStopFt = (24.0 * 24.0) / (2.0 * firmRate) * GeoMath.FeetPerNm / 3600.0;
        double approachFt = (firmStopFt * 0.85) + (AircraftLength.ResolveFt("B738") / 2.0);
        aircraft.Position = GeoMath.ProjectPoint(bar.Position, new TrueHeading((bearingDeg + 180.0) % 360.0), approachFt / GeoMath.FeetPerNm);
        CommandResult issued = engine.SendCommand(Callsign, "HS 33");
        output.WriteLine($"HS 33 {approachFt:F1} ft before the bar: {issued.Success} — {issued.Message}");
        Assert.Contains("Unable", issued.Message, StringComparison.OrdinalIgnoreCase);

        TickUntilHolding(engine, aircraft);
        AssertReportMatches(
            reports,
            @"^holding position on C, not clear of runway 33\.$",
            @", holding position on charlie, not clear of runway three three\.$"
        );
    }

    /// <summary>
    /// A B738 rolling on KOAK taxiway C given a bare <c>TAXI B</c> inside the setback of its no-destination end hold: unable at
    /// dispatch, and at rest the report still asks for the rest of the way (AIM 4-4-1.b).
    /// </summary>
    [Fact]
    public void RouteIncompleteBar_BeyondAfterTheUnableCall_RequestsFurtherTaxi()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        TestVnasData.EnsureInitialized();
        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        SimScenarioState scenario = Assert.IsType<SimScenarioState>(engine.Scenario);
        scenario.RpoShowPilotSpeech = true;
        scenario.SoloTrainingMode = false;
        const string Callsign = "SWA1314";
        List<PilotSpeechText> reports = CaptureReports(engine, Callsign);
        GroundNode junction = Assert.IsType<GroundNode>(layout.FindIntersectionNode("B", "C"));
        GroundNode caJunction = Assert.IsType<GroundNode>(layout.FindIntersectionNode("A", "C"));
        GroundNode nextOnC = caJunction.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C")).Select(e => e.OtherNode(caJunction)).Single();
        var heading = new TrueHeading(GeoMath.BearingTo(caJunction.Position, nextOnC.Position));
        AircraftState aircraft = SpawnRollingOnC(engine, layout, Callsign, (caJunction.Position, heading), 20.0);
        Assert.True(engine.SendCommand(Callsign, "TAXI C D").Success);
        Assert.True(engine.SendCommand(Callsign, "SPD 20").Success);

        CommandResult? taxiB = null;
        for (int second = 1; (second <= 60) && (taxiB is null); second++)
        {
            engine.TickOneSecond();
            double toJunctionFt = GeoMath.DistanceNm(aircraft.Position, junction.Position) * GeoMath.FeetPerNm;
            if ((toJunctionFt <= 150.0) && (Math.Abs(aircraft.GroundSpeed - 20.0) <= 0.6))
            {
                taxiB = engine.SendCommand(Callsign, "TAXI B");
                output.WriteLine($"TAXI B {toJunctionFt:F1} ft from C/B: {taxiB.Success} — {taxiB.Message}");
            }
        }

        Assert.Contains("Unable to hold short of B", taxiB?.Message ?? "", StringComparison.Ordinal);
        TickUntilHolding(engine, aircraft);
        AssertReportMatches(reports, @"^holding position on C, request further taxi\.$", @", holding position on charlie, request further taxi\.$");
    }

    /// <summary>A B738 placed rolling at <paramref name="speedKts"/> on KOAK taxiway C, as the KOAK firm-rate and bare-taxi rigs place one.</summary>
    private static AircraftState SpawnRollingOnC(
        SimulationEngine engine,
        AirportGroundLayout layout,
        string callsign,
        (LatLon Position, TrueHeading Heading) pose,
        double speedKts
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = pose.Position,
            TrueHeading = pose.Heading,
            TrueTrack = pose.Heading,
            Altitude = 6,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.IndicatedAirspeed = speedKts;
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "C";
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// Exactly one hold report: its terminal form matches <paramref name="terminalPattern"/>, its spoken form ends matching
    /// <paramref name="spokenEndPattern"/>.
    /// </summary>
    private void AssertReportMatches(List<PilotSpeechText> reports, string terminalPattern, string spokenEndPattern)
    {
        foreach (PilotSpeechText report in reports)
        {
            output.WriteLine($"report: '{report.TerminalForRpo}' / '{report.Tts}'");
        }

        PilotSpeechText said = Assert.Single(reports);
        Assert.Matches(terminalPattern, said.TerminalForRpo);
        Assert.Matches(spokenEndPattern, said.Tts);
    }

    /// <summary>
    /// One scripted SFO run: the type, where and how fast <c>HS T</c> is issued, whether it is slowed by <c>SPD 10</c>, whether an
    /// earlier <c>HS T</c> then <c>CROSS T</c> is given first (within <see cref="ArmAndCrossWithinFt"/> of the junction), and a
    /// command sent one second after the issue.
    /// </summary>
    private sealed record SfoScript(string Type, double IssueWithinFt, double IssueKts, bool Slow, double? ArmAndCrossWithinFt, string? AfterIssue)
    {
        public static SfoScript Plain(string type, double issueWithinFt, double issueKts, bool slow) =>
            new(type, issueWithinFt, issueKts, slow, ArmAndCrossWithinFt: null, AfterIssue: null);
    }

    /// <summary>
    /// The SFO rig: a jet spawned on B about 1,800 ft east of the B/T junction, cleared west past T, told <c>HS T</c> the first
    /// second it is within <see cref="SfoScript.IssueWithinFt"/> of the junction at <see cref="SfoScript.IssueKts"/> or more, then
    /// run to the hold and one second on, so the report has been drained.
    /// </summary>
    private ArrivalRun? RunSfo(SfoScript script)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return null;
        }

        Assert.IsType<SimScenarioState>(ground.Engine.Scenario).RpoShowPilotSpeech = true;
        List<PilotSpeechText> reports = CaptureReports(ground.Engine, SfoCallsign);
        GroundNode junction = Assert.IsType<GroundNode>(ground.Layout.FindIntersectionNode("B", "T"));
        GroundNode spawn = SfoHoldShortLiveSegmentTests.WalkEastAlongB(ground.Layout, junction, wantFt: 1800.0);
        var heading = new TrueHeading(GeoMath.BearingTo(spawn.Position, junction.Position));
        AircraftState aircraft = SfoGroundHarness.SpawnAt(ground, SfoCallsign, script.Type, (spawn, heading), new HoldingInPositionPhase());
        Send(ground.Engine, SfoHoldShortLiveSegmentTests.TaxiClearance);
        if (script.Slow)
        {
            Send(ground.Engine, "SPD 10");
        }

        bool armed = script.ArmAndCrossWithinFt is null;
        CommandResult? issued = null;
        double issueToBarNodeFt = double.NaN;
        for (int second = 1; (second <= 240) && (issued is null); second++)
        {
            ground.Engine.TickOneSecond();
            double distFt = GeoMath.DistanceNm(aircraft.Position, junction.Position) * GeoMath.FeetPerNm;
            if (!armed && (distFt <= script.ArmAndCrossWithinFt))
            {
                Send(ground.Engine, "HS T");
                ground.Engine.TickOneSecond();
                Send(ground.Engine, "CROSS T");
                armed = true;
                continue;
            }

            if (armed && (distFt <= script.IssueWithinFt) && (aircraft.GroundSpeed >= script.IssueKts))
            {
                issued = ground.Engine.SendCommand(SfoCallsign, "HS T");
                output.WriteLine($"HS T at {distFt:F1} ft / {aircraft.GroundSpeed:F1} kt: {issued.Success} — {issued.Message}");
                GroundNode barNode = ground.Layout.Nodes[BarOfT(aircraft).NodeId];
                issueToBarNodeFt = GeoMath.DistanceNm(aircraft.Position, barNode.Position) * GeoMath.FeetPerNm;
            }
        }

        Assert.True(issued is { Success: true }, $"HS T never issued or refused: {issued?.Message}");
        if (script.AfterIssue is { } follow)
        {
            ground.Engine.TickOneSecond();
            Send(ground.Engine, follow);
        }

        TickUntilHolding(ground.Engine, aircraft);
        return new ArrivalRun(aircraft, ground.Layout, issued!, reports) { IssueToBarNodeFt = issueToBarNodeFt };
    }

    /// <summary>Sends <paramref name="command"/> to the SFO aircraft and requires it accepted.</summary>
    private void Send(SimulationEngine engine, string command)
    {
        CommandResult result = engine.SendCommand(SfoCallsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"{command} refused: {result.Message}");
    }

    /// <summary>Ticks until the aircraft holds short (60 s at most), then one more second so the hold's report has been drained.</summary>
    private void TickUntilHolding(SimulationEngine engine, AircraftState aircraft)
    {
        for (int second = 0; (second < 60) && (aircraft.Phases?.CurrentPhase is not HoldingShortPhase); second++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        engine.TickOneSecond();
        output.WriteLine($"at rest: phase {aircraft.Phases?.CurrentPhase?.Name}, {aircraft.GroundSpeed:F1} kt");
    }

    /// <summary>Every terminal line the room emitted for the aircraft: pilot speech and the SAY echoes of its transmissions alike.</summary>
    private static List<TerminalEntry> CaptureTerminalLines(SimulationEngine engine, string callsign)
    {
        var said = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry =>
        {
            if (string.Equals(entry.Callsign, callsign, StringComparison.OrdinalIgnoreCase))
            {
                said.Add(entry);
            }
        };
        return said;
    }

    /// <summary>Every pilot line the aircraft says.</summary>
    private static List<PilotSpeechText> CaptureSpeech(SimulationEngine engine, string callsign)
    {
        var said = new List<PilotSpeechText>();
        engine.PilotSpeechEmitted += (who, speech) =>
        {
            if (who == callsign)
            {
                said.Add(speech);
            }
        };
        return said;
    }

    /// <summary>Every pilot speech the aircraft says that reports where it holds (its terminal form names "holding").</summary>
    private static List<PilotSpeechText> CaptureReports(SimulationEngine engine, string callsign)
    {
        var reports = new List<PilotSpeechText>();
        engine.PilotSpeechEmitted += (said, speech) =>
        {
            if ((said == callsign) && speech.Terminal.Contains("holding", StringComparison.OrdinalIgnoreCase))
            {
                reports.Add(speech);
            }
        };
        return reports;
    }

    /// <summary>
    /// The run made exactly one hold report: its terminal form is <paramref name="terminal"/>, its spoken form the callsign then
    /// <paramref name="spokenAfterCallsign"/>.
    /// </summary>
    private void AssertReport(ArrivalRun run, string terminal, string spokenAfterCallsign)
    {
        foreach (PilotSpeechText report in run.Reports)
        {
            output.WriteLine($"report: '{report.TerminalForRpo}' / '{report.Tts}'");
        }

        PilotSpeechText said = Assert.Single(run.Reports);
        Assert.Equal(terminal, said.TerminalForRpo);
        Assert.StartsWith(CallsignParser.IcaoToSpoken(run.Aircraft.Callsign), said.Tts, StringComparison.Ordinal);
        Assert.EndsWith(spokenAfterCallsign, said.Tts, StringComparison.Ordinal);
    }

    private static HoldShortPoint BarOfT(ArrivalRun run) => BarOfT(run.Aircraft);

    private static HoldShortPoint BarOfT(AircraftState aircraft) =>
        Assert.Single(Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute).HoldShortPoints, h => h.TargetName == "T");

    /// <summary>
    /// The aircraft rests within <see cref="TaxiingPhase.SetBackStopTakeFt"/> of the bar's (moved) point along B, which is straight
    /// there.
    /// </summary>
    private void AssertRestsOnThePoint(ArrivalRun run, HoldShortPoint bar)
    {
        var point = new LatLon(Assert.NotNull(bar.Latitude), Assert.NotNull(bar.Longitude));
        double toPointFt = SfoHoldShortLiveSegmentTests.AlongHeadingFt(run.Aircraft.Position, run.Aircraft.TrueHeading, point);
        output.WriteLine($"rest {toPointFt:F1} ft short of the hold-short point (negative: past it)");
        Assert.True(Math.Abs(toPointFt) <= TaxiingPhase.SetBackStopTakeFt, $"rested {toPointFt:F1} ft from the hold-short point");
    }

    /// <summary>
    /// How far (ft) the nose is past <paramref name="junction"/> along the approach from <paramref name="approachDeg"/>; negative
    /// while short.
    /// </summary>
    private static double NosePastFt(AircraftState aircraft, GroundNode junction, double approachDeg)
    {
        LatLon nose = GeoMath.ProjectPoint(
            aircraft.Position,
            aircraft.TrueHeading,
            AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm
        );
        return -GeoMath.AlongTrackDistanceNm(nose, junction.Position, new TrueHeading(approachDeg)) * GeoMath.FeetPerNm;
    }
}
