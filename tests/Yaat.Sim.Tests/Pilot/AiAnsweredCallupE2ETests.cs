using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// An AI-staffed position is a student stand-in: a parked aircraft calls it ready to taxi (addressed by its radio
/// name), the request opens for the AI to answer, the transmission reaches the terminal, and follow-ups re-fire —
/// while the human student's contact flag stays untouched.
/// </summary>
public class AiAnsweredCallupE2ETests
{
    private const string ParkedAtOak = """
        {
          "id": "ai-callup",
          "name": "AI answered callup",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    private const string OnFinalAtOak = """
        {
          "id": "ai-on-final",
          "name": "AI tower answers on final",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "OnFinal", "runway": "28R", "distanceFromRunway": 4 },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AiAnsweredCallupE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void AiGround_GetsTheReadyToTaxiCall_AndTheStudentFlagStaysFalse()
    {
        if (_zoa is null)
        {
            return;
        }

        var (engine, said) = LoadWithAiGround(solo: false, student: null);

        Tick(engine, 7);

        var ac = engine.World.GetSnapshot()[0];
        Assert.NotNull(ac.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestKind.Taxi, ac.PendingPilotRequest.Kind);
        Assert.True(ac.PendingPilotRequest.IsOpen);
        Assert.Contains("Oakland Ground", ac.PendingPilotRequest.LastPilotLine, StringComparison.OrdinalIgnoreCase);
        Assert.False(ac.HasMadeInitialContact);
        Assert.Equal([TestAiPositions.OakGround(_zoa).PositionId], ac.AiInitialContactPositionIds);
        Assert.Contains(said, line => line.Contains("Oakland Ground", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AiTower_AnswersTheOnFinalCall_AndTheAircraftDoesNotAlsoCheckInWithTheStudentApproach()
    {
        if (_zoa is null)
        {
            return;
        }

        // Solo student on NorCal Approach, AI Oakland Local: an arrival spawned on final calls the tower and only the
        // tower — an aircraft on a four-mile final never makes an initial call to approach (AIM 5-4-3.a).
        var engine = new SimulationEngine(new TestAirportGroundData());
        var warnings = engine.LoadScenario(OnFinalAtOak, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        var scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = true;
        scenario.StudentPosition = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("NCT_APP")!.Id)!;
        scenario.StudentPositionType = "APP";
        var aiTower = TestAiPositions.OakTower(_zoa);
        scenario.SetAiStaffedPositions([aiTower]);
        var said = new List<string>();
        engine.TerminalEntryEmitted += entry => said.Add(entry.Message);

        Tick(engine, 5);

        var ac = engine.World.GetSnapshot()[0];
        Assert.Contains(said, line => line.Contains("Oakland Tower", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(said, line => line.Contains("approach", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([aiTower.PositionId], ac.AiInitialContactPositionIds);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void UnansweredRequest_FollowsUp_ForTheAiPosition()
    {
        if (_zoa is null)
        {
            return;
        }

        var (engine, said) = LoadWithAiGround(solo: false, student: null);

        Tick(engine, 7);
        int afterCallup = said.Count;
        Tick(engine, (int)PilotRequestTracker.NormalFollowUpDelaySeconds + 5);

        Assert.True(said.Count > afterCallup, "the unanswered taxi request should re-voice after the follow-up horizon");
    }

    [Fact]
    public void NobodyAnswering_NoCall_NoRequest()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(ParkedAtOak, 42, MagneticDeclination.EvaluationDateUtc);
        engine.Scenario!.ArtccConfig = _zoa;
        var said = new List<string>();
        engine.TerminalEntryEmitted += entry => said.Add(entry.Message);

        Tick(engine, 10);

        var ac = engine.World.GetSnapshot()[0];
        Assert.Null(ac.PendingPilotRequest);
        Assert.Empty(ac.AiInitialContactPositionIds);
        Assert.DoesNotContain(said, line => line.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AiCommand_ClosesTheRequest_WithoutStudentContact_AndReplaysTheSame()
    {
        if (_zoa is null)
        {
            return;
        }

        var (engine, _) = LoadWithAiGround(solo: false, student: null);
        Tick(engine, 7);
        var ac = engine.World.GetSnapshot()[0];
        var compound = CommandParser.ParseCompound("TAXIAUTO 28R", ac.FlightPlan.Route).Value!;

        var aiCtx = new DispatchContext(
            ac.Ground.Layout ?? engine.ResolveGroundLayout(ac),
            engine.World.Rng,
            engine.World.Weather,
            engine.FindAircraft,
            () => engine.World.GetSnapshot(),
            engine.Scenario!.ValidateDctFixes,
            engine.Scenario.AutoCrossRunway,
            engine.Scenario.SoloTrainingMode,
            engine.Scenario.RpoShowPilotSpeech,
            engine.EmitTerminalEntry,
            engine.Scenario.ArtccConfig,
            engine.Scenario.ElapsedSeconds,
            PreserveConditionals: false,
            IsScenarioScripted: true
        );
        var result = CommandDispatcher.DispatchCompound(compound, ac, aiCtx);
        engine.ApplyPostDispatch(ac, compound, result, DispatchOrigin.ControllerAi);

        Assert.True(result.Success, result.Message);
        Assert.False(ac.PendingPilotRequest!.IsOpen);
        Assert.False(ac.HasMadeInitialContact);
        Assert.False(ac.HasControllerAcknowledgedInitialContact);
        Assert.NotEmpty(ac.PendingPilotTransmissions);

        // Replay of the same AI command from its recorded connection id lands in the same state.
        var (replayEngine, _) = LoadWithAiGround(solo: false, student: null);
        Tick(replayEngine, 7);
        replayEngine.Actions.Apply(new RecordedCommand(7, "N152SP", "TAXIAUTO 28R", "AI", AiConnectionId.Format("pos")));
        var replayed = replayEngine.World.GetSnapshot()[0];
        Assert.False(replayed.PendingPilotRequest!.IsOpen);
        Assert.False(replayed.HasMadeInitialContact);
    }

    [Fact]
    public void HumanCommand_StillRegistersStudentContact()
    {
        if (_zoa is null)
        {
            return;
        }

        var (engine, _) = LoadWithAiGround(solo: false, student: null);
        Tick(engine, 7);

        var result = engine.SendCommand("N152SP", "TAXIAUTO 28R");

        var ac = engine.World.GetSnapshot()[0];
        Assert.True(result.Success, result.Message);
        Assert.True(ac.HasMadeInitialContact);
        Assert.True(ac.HasControllerAcknowledgedInitialContact);
    }

    private (SimulationEngine Engine, List<string> Said) LoadWithAiGround(bool solo, TrackOwner? student)
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        var warnings = engine.LoadScenario(ParkedAtOak, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        var scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = solo;
        scenario.StudentPosition = student;
        scenario.StudentPositionType = student is null ? null : AtcPositionTypeClassifier.Classify(student.Callsign);
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa!)]);
        var said = new List<string>();
        engine.TerminalEntryEmitted += entry => said.Add(entry.Message);
        return (engine, said);
    }

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }
}
