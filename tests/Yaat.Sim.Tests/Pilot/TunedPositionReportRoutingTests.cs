using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// <see cref="PilotResponder.RouteByTunedPosition"/>: a sim-initiated pilot report goes to whoever the pilot is tuned to. In solo
/// training it is spoken when the pilot is on the student's side or tuned to an AI-staffed position, and shown as a warning when
/// tuned to a position nobody in the room answers; an RPO room keeps its pilot-speech or warning routing.
/// </summary>
public class TunedPositionReportRoutingTests
{
    private static readonly PilotSpeechText Text = new(
        "holding position, not clear of runway 28R",
        "holding position not clear of runway two eight right"
    )
    {
        RpoTerminal = "UAL1 holding position, not clear of runway 28R",
    };

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TunedPositionReportRoutingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Solo_PilotOnTheStudentsSide_IsSpoken()
    {
        if (StudentTower() is not { } tower)
        {
            return;
        }

        AircraftState aircraft = NewAircraft(tunedPosition: null);

        PilotResponder.RouteByTunedPosition(aircraft, SoloRouting(tower, []), Text);

        AssertSpoken(aircraft);
    }

    [Fact]
    public void Solo_PilotTunedToTheStudent_IsSpoken()
    {
        if (StudentTower() is not { } tower)
        {
            return;
        }

        AircraftState aircraft = NewAircraft(tunedPosition: TrackOwner.CreateNonNas(tower.Callsign.ToLowerInvariant()));

        PilotResponder.RouteByTunedPosition(aircraft, SoloRouting(tower, []), Text);

        AssertSpoken(aircraft);
    }

    [Fact]
    public void Solo_PilotTunedToAnAiStaffedPosition_IsSpoken()
    {
        if ((_zoa is null) || (StudentTower() is not { } tower))
        {
            return;
        }

        PilotReportRouting routing = SoloRouting(tower, [TestAiPositions.OakGround(_zoa)]);
        TrackOwner? aiGround = Assert.Single(routing.Contacts.Positions, p => p.Agent == PilotAnsweringAgent.ControllerAi).Owner;
        Assert.NotNull(aiGround);
        AircraftState aircraft = NewAircraft(tunedPosition: aiGround);

        PilotResponder.RouteByTunedPosition(aircraft, routing, Text);

        AssertSpoken(aircraft);
    }

    [Fact]
    public void Solo_PilotTunedToAnotherHumanPosition_IsAWarning()
    {
        if (StudentTower() is not { } tower)
        {
            return;
        }

        AircraftState aircraft = NewAircraft(tunedPosition: TrackOwner.CreateNonNas("SFO_TWR"));

        PilotResponder.RouteByTunedPosition(aircraft, SoloRouting(tower, []), Text);

        AssertWarning(aircraft, Text.Terminal);
    }

    [Fact]
    public void Solo_PilotTunedElsewhere_IsAWarning()
    {
        if (StudentTower() is not { } tower)
        {
            return;
        }

        AircraftState aircraft = NewAircraft(tunedPosition: TrackOwner.Elsewhere);

        PilotResponder.RouteByTunedPosition(aircraft, SoloRouting(tower, []), Text);

        AssertWarning(aircraft, Text.Terminal);
    }

    [Fact]
    public void Solo_NoStudentPosition_PilotOnTheStudentsSide_IsSpoken()
    {
        AircraftState aircraft = NewAircraft(tunedPosition: null);
        var routing = new PilotReportRouting(true, false, null, PilotContactRoster.ForStudent(true, null, null, null));

        PilotResponder.RouteByTunedPosition(aircraft, routing, Text);

        AssertSpoken(aircraft);
    }

    [Fact]
    public void Solo_NoStudentPosition_PilotTunedToAPosition_IsAWarning()
    {
        AircraftState aircraft = NewAircraft(tunedPosition: TrackOwner.CreateNonNas("SFO_TWR"));
        var routing = new PilotReportRouting(true, false, null, PilotContactRoster.ForStudent(true, null, null, null));

        PilotResponder.RouteByTunedPosition(aircraft, routing, Text);

        AssertWarning(aircraft, Text.Terminal);
    }

    [Fact]
    public void Rpo_ShowPilotSpeech_QueuesPilotSpeech()
    {
        AircraftState aircraft = NewAircraft(tunedPosition: null);
        var routing = new PilotReportRouting(false, true, null, PilotContactRoster.Empty);

        PilotResponder.RouteByTunedPosition(aircraft, routing, Text);

        Assert.Same(Text, Assert.Single(aircraft.PendingPilotSpeech));
        Assert.Empty(aircraft.PendingPilotTransmissions);
        Assert.Empty(aircraft.PendingWarnings);
    }

    [Fact]
    public void Rpo_NoPilotSpeech_IsAWarningInTheRpoForm()
    {
        AircraftState aircraft = NewAircraft(tunedPosition: null);
        var routing = new PilotReportRouting(false, false, null, PilotContactRoster.Empty);

        PilotResponder.RouteByTunedPosition(aircraft, routing, Text);

        AssertWarning(aircraft, Text.TerminalForRpo);
    }

    private TrackOwner? StudentTower()
    {
        if (_zoa is null)
        {
            return null;
        }

        PositionConfig? position = _zoa.FindPositionByCallsign("OAK_TWR", facilityHint: null);
        Assert.NotNull(position);
        return _zoa.ResolvePosition(position.Id);
    }

    private PilotReportRouting SoloRouting(TrackOwner student, IReadOnlyList<AiPositionConfig> aiStaffed) =>
        new(true, false, student, PilotContactRoster.Build(true, student, "TWR", null, aiStaffed, _zoa));

    private static AircraftState NewAircraft(TrackOwner? tunedPosition) =>
        new()
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            IsOnGround = true,
            TunedPosition = tunedPosition,
        };

    private static void AssertSpoken(AircraftState aircraft)
    {
        PilotTransmission spoken = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal(Text.Terminal, spoken.Text);
        Assert.Equal(Text.Tts, spoken.SpeechText);
        Assert.Equal(PilotTransmissionKind.Proactive, spoken.Kind);
        Assert.Empty(aircraft.PendingWarnings);
        Assert.Empty(aircraft.PendingPilotSpeech);
    }

    private static void AssertWarning(AircraftState aircraft, string expected)
    {
        Assert.Equal(expected, Assert.Single(aircraft.PendingWarnings));
        Assert.Empty(aircraft.PendingPilotTransmissions);
        Assert.Empty(aircraft.PendingPilotSpeech);
    }
}
