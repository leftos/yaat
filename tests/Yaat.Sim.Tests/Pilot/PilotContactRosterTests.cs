using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// <see cref="PilotContactRoster"/> — who a pilot calls: an AI position of the wanted type covering the airport the
/// call is made at first, else the solo student under the unchanged SOP eligibility rules, else (ground calls only) an
/// AI tower working the cab alone, else nobody.
/// </summary>
public class PilotContactRosterTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public PilotContactRosterTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void NoStudent_NoAi_NobodyAnswers()
    {
        var roster = PilotContactRoster.Build(false, null, null, [], _zoa);

        Assert.False(roster.AnyAnswering);
        Assert.Null(roster.ResolveFor(OakAircraft(), "GND", "OAK", Eligibility(null, null), true));
    }

    [Fact]
    public void SoloStudent_AnswersWithItsRadioName()
    {
        if (_zoa is null)
        {
            return;
        }

        var tower = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("OAK_TWR")!.Id)!;
        var roster = PilotContactRoster.Build(true, tower, "TWR", [], _zoa);

        var answering = roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(tower, "TWR"), true);
        Assert.NotNull(answering);
        Assert.Equal(PilotAnsweringAgent.Student, answering.Agent);
        Assert.Equal("Oakland Tower", PilotResponder.ResolveAnsweringCallName(answering, "TWR", "tower"));
        // A tower-only student still answers the ground call, addressed generically.
        Assert.Equal("ground", PilotResponder.ResolveAnsweringCallName(answering, "GND", "ground"));
    }

    [Fact]
    public void AiGround_AnswersTheGroundCall_AndTheStudentTowerAnswersTheTowerCall()
    {
        if (_zoa is null)
        {
            return;
        }

        var tower = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("OAK_TWR")!.Id)!;
        var roster = PilotContactRoster.Build(true, tower, "TWR", [TestAiPositions.OakGround(_zoa)], _zoa);

        var ground = roster.ResolveFor(OakAircraft(), "GND", "OAK", Eligibility(tower, "TWR"), true);
        var local = roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(tower, "TWR"), false);
        Assert.Equal(PilotAnsweringAgent.ControllerAi, ground!.Agent);
        Assert.Equal("Oakland Ground", PilotResponder.ResolveAnsweringCallName(ground, "GND", "ground"));
        Assert.Equal(PilotAnsweringAgent.Student, local!.Agent);
        Assert.Equal("Oakland Tower", PilotResponder.ResolveAnsweringCallName(local, "TWR", "tower"));
    }

    [Fact]
    public void AiTower_WithStudentGround_MirrorsTheSplit()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("OAK_GND")!.Id)!;
        var roster = PilotContactRoster.Build(true, ground, "GND", [TestAiPositions.OakTower(_zoa)], _zoa);

        // The student ground takes the ground call ahead of the AI tower's combined-cab fallback.
        Assert.Equal(PilotAnsweringAgent.Student, roster.ResolveFor(OakAircraft(), "GND", "OAK", Eligibility(ground, "GND"), true)!.Agent);
        Assert.Equal(PilotAnsweringAgent.ControllerAi, roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(ground, "GND"), false)!.Agent);
    }

    [Fact]
    public void AiOnly_AnswersItsOwnAirport_NotAnother()
    {
        if (_zoa is null)
        {
            return;
        }

        var roster = PilotContactRoster.Build(false, null, null, [TestAiPositions.SfoGround(_zoa)], _zoa);

        Assert.True(roster.AnyAnswering);
        Assert.Null(roster.Student);
        Assert.Null(roster.ResolveFor(OakAircraft(), "GND", "OAK", Eligibility(null, null), true));
        Assert.NotNull(roster.ResolveFor(OakAircraft(), "GND", "SFO", Eligibility(null, null, "SFO"), true));
    }

    [Fact]
    public void AiTowerCab_IsMatchedOnThePhysicalAirport_NeverTheFiledDestination()
    {
        if (_zoa is null)
        {
            return;
        }

        // An OAK departure filed to SFO calls Oakland Ground — not San Francisco Ground, whatever the position-id order.
        var toSfo = OakAircraft();
        toSfo.FlightPlan.Destination = "KSFO";
        var both = PilotContactRoster.Build(false, null, null, [TestAiPositions.SfoGround(_zoa), TestAiPositions.OakGround(_zoa)], _zoa);
        var answering = both.ResolveFor(toSfo, "GND", "OAK", Eligibility(null, null), true);
        Assert.Equal("OAK_GND", answering!.Owner!.Callsign);

        var sfoOnly = PilotContactRoster.Build(false, null, null, [TestAiPositions.SfoGround(_zoa)], _zoa);
        Assert.Null(sfoOnly.ResolveFor(toSfo, "GND", "OAK", Eligibility(null, null), true));
        // No known surface airport: a tower-cab position is never assumed.
        Assert.Null(sfoOnly.ResolveFor(toSfo, "GND", null, Eligibility(null, null, "SFO"), true));
    }

    [Fact]
    public void AiTower_IsSubjectToTheSopRules_ForAnArrivalOwnedByApproach()
    {
        if (_zoa is null)
        {
            return;
        }

        var aiTower = TestAiPositions.OakTower(_zoa);
        var approach = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("NCT_APP")!.Id)!;
        var roster = PilotContactRoster.Build(false, null, null, [aiTower], _zoa);
        var eligibility = Eligibility(null, null);
        var owned = OakAircraft();
        owned.Track.Owner = approach;

        // ZOA SOP: approach → tower contact only once a handoff is initiated. Owned by approach with no handoff, the
        // on-final call stays silent whoever staffs the tower; the hold-short call never checked (and still does not).
        Assert.Null(roster.ResolveFor(owned, "TWR", "OAK", eligibility, true));
        Assert.NotNull(roster.ResolveFor(owned, "TWR", "OAK", eligibility, false));

        owned.Track.HandoffPeer = aiTower.Identity;
        Assert.NotNull(roster.ResolveFor(owned, "TWR", "OAK", eligibility, true));
    }

    [Fact]
    public void AiTowerAlone_WorksGroundToo_AddressedGenerically()
    {
        if (_zoa is null)
        {
            return;
        }

        var roster = PilotContactRoster.Build(false, null, null, [TestAiPositions.OakTower(_zoa)], _zoa);

        var ground = roster.ResolveFor(OakAircraft(), "GND", "OAK", Eligibility(null, null), true);
        Assert.NotNull(ground);
        Assert.Equal("OAK_TWR", ground.Owner!.Callsign);
        Assert.Equal("ground", PilotResponder.ResolveAnsweringCallName(ground, "GND", "ground"));
        Assert.Equal(
            "Oakland Tower",
            PilotResponder.ResolveAnsweringCallName(roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(null, null), false)!, "TWR", "tower")
        );
    }

    [Fact]
    public void AiGroundAlone_DoesNotAnswerTheTowerCall()
    {
        if (_zoa is null)
        {
            return;
        }

        var roster = PilotContactRoster.Build(false, null, null, [TestAiPositions.OakGround(_zoa)], _zoa);

        Assert.Null(roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(null, null), false));
    }

    [Fact]
    public void AiEntryThatIsTheStudentPosition_IsDroppedWhileSolo()
    {
        if (_zoa is null)
        {
            return;
        }

        var ai = TestAiPositions.OakGround(_zoa);
        var roster = PilotContactRoster.Build(true, ai.Identity, "GND", [ai], _zoa);

        var answering = Assert.Single(roster.Positions);
        Assert.Equal(PilotAnsweringAgent.Student, answering.Agent);
    }

    [Fact]
    public void Student_StaysSubjectToTheSopEligibilityRules()
    {
        if (_zoa is null)
        {
            return;
        }

        var tower = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("OAK_TWR")!.Id)!;
        var approach = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("NCT_APP")!.Id)!;
        var roster = PilotContactRoster.Build(true, tower, "TWR", [], _zoa);
        var owned = OakAircraft();
        owned.Track.Owner = approach;

        // Owned by approach with no handoff: the parking / final call-ups check the SOP and stay silent,
        // exactly as CanInitiateWithStudent decided before the roster.
        var eligibility = Eligibility(tower, "TWR");
        Assert.Equal(
            PilotInitialContactEligibility.CanInitiateWithStudent(owned, eligibility),
            roster.ResolveFor(owned, "TWR", "OAK", eligibility, true) is not null
        );
        // The hold-short call never checked it.
        Assert.NotNull(roster.ResolveFor(owned, "TWR", "OAK", eligibility, false));
    }

    [Fact]
    public void InitialContactLatch_IsPerAiPosition_AndLeavesTheStudentFlagAlone()
    {
        if (_zoa is null)
        {
            return;
        }

        var oakGround = TestAiPositions.OakGround(_zoa);
        var roster = PilotContactRoster.Build(false, null, null, [oakGround, TestAiPositions.SfoGround(_zoa)], _zoa);
        var aircraft = OakAircraft();
        var oak = roster.ResolveFor(aircraft, "GND", "OAK", Eligibility(null, null), true)!;
        var sfo = roster.ResolveFor(aircraft, "GND", "SFO", Eligibility(null, null, "SFO"), true)!;

        oak.MarkInitialContact(aircraft);

        Assert.True(oak.HasInitialContact(aircraft));
        Assert.False(sfo.HasInitialContact(aircraft));
        Assert.False(aircraft.HasMadeInitialContact);
        Assert.Equal([oakGround.PositionId], aircraft.AiInitialContactPositionIds);
    }

    private static AircraftState OakAircraft() =>
        new()
        {
            Callsign = "N1",
            AircraftType = "C172",
            Position = new LatLon(37.7213, -122.2208),
            AirportId = "OAK",
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KOAK" },
        };

    private InitialContactEligibilityContext Eligibility(TrackOwner? student, string? studentType, string primaryAirportId = "OAK") =>
        new(
            student,
            studentType,
            "ZOA",
            primaryAirportId,
            _zoa is null ? Data.InitialContactTransferCatalog.Empty : Data.NavigationDatabase.Instance.InitialContactTransfers
        );
}
