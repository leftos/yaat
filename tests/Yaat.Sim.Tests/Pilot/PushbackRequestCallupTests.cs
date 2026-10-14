using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A jet or turboprop parked on a push-back stand makes its stand call as a pushback request, not a taxi request: the
/// call reads "request pushback", records a <see cref="PilotPendingRequestKind.Pushback"/> request and re-arms the
/// after-push call, so the push answers the request and the crew then reports ready to taxi from the alley. A piston,
/// and any aircraft on an either or taxi-out stand, still calls ready to taxi. A taxi straight off the stand answers
/// the request too and leaves no after-push call behind.
/// </summary>
public class PushbackRequestCallupTests
{
    /// <summary>The stand call comes 5 s after spawn; the budget leaves room for the pacing slot.</summary>
    private const int StandCallBudgetSeconds = 15;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public PushbackRequestCallupTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Jet_OnPushBackStand_RequestsPushback()
    {
        if (Load("S7", "B738", "430") is not { } engine)
        {
            return;
        }

        AircraftState aircraft = StandCall(engine);
        Assert.Equal(StandDeparture.PushBack, ParkedStandDeparture.Of(aircraft, aircraft.Ground.Layout));
        PilotPendingRequest request = aircraft.PendingPilotRequest!;
        Assert.Equal(PilotPendingRequestKind.Pushback, request.Kind);
        Assert.Equal("Oakland Ground, at gate S7, with information Alpha, request pushback.", request.LastPilotLine);
        Assert.Equal(InitialCallupPlan.AfterPush, aircraft.Ground.InitialCallup);
        Assert.False(aircraft.Ground.InitialCallupDecisionProcessed);
    }

    /// <summary>A turboprop on a push-back stand asks for pushback too, as a jet does.</summary>
    [Fact]
    public void Turboprop_OnPushBackStand_RequestsPushback()
    {
        if (Load("S7", "DH8D", "300") is not { } engine)
        {
            return;
        }

        AircraftState aircraft = StandCall(engine);
        Assert.Equal(AircraftCategory.Turboprop, AircraftCategorization.Categorize("DH8D"));
        Assert.Equal(StandDeparture.PushBack, ParkedStandDeparture.Of(aircraft, aircraft.Ground.Layout));
        Assert.Equal(PilotPendingRequestKind.Pushback, aircraft.PendingPilotRequest!.Kind);
        Assert.Equal("Oakland Ground, at gate S7, with information Alpha, request pushback.", aircraft.PendingPilotRequest.LastPilotLine);
    }

    /// <summary>The spoken form of the request names the aircraft and spells the stand, as the ready-to-taxi call does.</summary>
    [Fact]
    public void PushbackRequest_SpokenForm()
    {
        if (Load("S7", "B738", "430") is not { } engine)
        {
            return;
        }

        AircraftState aircraft = StandCall(engine);
        PilotSpeechText line = PilotResponder.BuildPushbackRequest(aircraft, "Oakland Ground", "A", ReadyToTaxiLocation.Stand("S7"));

        Assert.Equal(aircraft.PendingPilotRequest!.LastPilotLine, line.Terminal);
        Assert.Equal("Oakland Ground, november one five two sierra papa at gate sierra seven, with information Alpha, request pushback.", line.Tts);
    }

    [Theory]
    [InlineData("S7", "C172", "100")] // a piston on a push-back stand
    [InlineData("MTN1", "B738", "430")] // a jet on an either stand
    [InlineData("SIG4", "B738", "430")] // a jet on a taxi-out stand
    public void OtherStandOrCategory_StillReadyToTaxi(string stand, string type, string cruiseSpeed)
    {
        if (Load(stand, type, cruiseSpeed) is not { } engine)
        {
            return;
        }

        AircraftState aircraft = StandCall(engine);
        PilotPendingRequest request = aircraft.PendingPilotRequest!;
        Assert.Equal(PilotPendingRequestKind.Taxi, request.Kind);
        Assert.EndsWith("ready to taxi.", request.LastPilotLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_SatisfiesTheRequest_AndTheAfterPushCallFollows()
    {
        if (Load("S7", "B738", "430") is not { } engine)
        {
            return;
        }

        AircraftState aircraft = StandCall(engine);
        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "PUSH").Success);
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, aircraft.PendingPilotRequest!.ResponseState);
        double pushedAt = engine.Scenario!.ElapsedSeconds;

        int budget = 400 + (int)InitialCallupCall.PostPushSetupDelaySeconds(AircraftCategory.Jet);
        AircraftState pushed = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            budget
        );
        Assert.Contains("pushed back from gate S7", pushed.PendingPilotRequest!.LastPilotLine, StringComparison.Ordinal);
        Assert.EndsWith("ready to taxi.", pushed.PendingPilotRequest.LastPilotLine, StringComparison.Ordinal);
        Assert.True(
            engine.Scenario.ElapsedSeconds - pushedAt >= InitialCallupCall.PostPushSetupDelaySeconds(AircraftCategory.Jet),
            $"the after-push call came {engine.Scenario.ElapsedSeconds - pushedAt:F0}s after the push, inside the jet's setup time"
        );
    }

    [Fact]
    public void TaxiOffTheStandWithoutAPush_SatisfiesTheRequest_AndNoAfterPushCallFires()
    {
        if (Load("S7", "B738", "430") is not { } engine)
        {
            return;
        }

        AircraftState aircraft = StandCall(engine);
        CommandResult taxi = engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30");
        Assert.True(taxi.Success, taxi.Message);
        Assert.Equal(PilotPendingRequestResponseState.Satisfied, aircraft.PendingPilotRequest!.ResponseState);

        for (int second = 0; second < 300; second++)
        {
            AiTestFixture.Tick(engine, 1);
            AircraftState now = engine.FindAircraft(AiTestFixture.Callsign)!;
            Assert.False(
                now.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi or PilotPendingRequestKind.Pushback },
                $"a stray call fired {second + 1}s after the taxi: {now.PendingPilotRequest?.LastPilotLine}"
            );
        }

        Assert.Equal(InitialCallupPlan.None, engine.FindAircraft(AiTestFixture.Callsign)!.Ground.InitialCallup);
    }

    [Fact]
    public void OpenPushbackRequest_RoundTripsThroughASnapshot()
    {
        if (Load("S7", "B738", "430") is not { } engine)
        {
            return;
        }

        AircraftState aircraft = StandCall(engine);
        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), aircraft.Ground.Layout);

        Assert.Equal(PilotPendingRequestKind.Pushback, restored.PendingPilotRequest!.Kind);
        Assert.True(restored.PendingPilotRequest.IsOpen);
        Assert.Equal(aircraft.PendingPilotRequest!.LastPilotLine, restored.PendingPilotRequest.LastPilotLine);
        Assert.Equal(InitialCallupPlan.AfterPush, restored.Ground.InitialCallup);
        Assert.False(restored.Ground.InitialCallupDecisionProcessed);
    }

    /// <summary>
    /// <see cref="AiTestFixture.ParkedAtOak"/>'s aircraft as <paramref name="type"/> on <paramref name="stand"/>, with an
    /// AI ground staffed (observing only) so someone answers the stand call; null when the ZOA config is missing.
    /// </summary>
    private SimulationEngine? Load(string stand, string type, string cruiseSpeed)
    {
        if (_zoa is null)
        {
            return null;
        }

        string scenario = AiTestFixture
            .ParkedAtOak.Replace("\"C172\"", $"\"{type}\"", StringComparison.Ordinal)
            .Replace("\"cruiseSpeed\": 100", $"\"cruiseSpeed\": {cruiseSpeed}", StringComparison.Ordinal)
            .Replace("\"parking\": \"SIG1\"", $"\"parking\": \"{stand}\"", StringComparison.Ordinal);
        return AiTestFixture.Load(scenario, _zoa, 7, [TestAiPositions.OakGround(_zoa)]);
    }

    private static AircraftState StandCall(SimulationEngine engine) =>
        AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.PendingPilotRequest is { IsOpen: true }, StandCallBudgetSeconds);
}
