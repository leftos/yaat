using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
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
        var roster = PilotContactRoster.Build(false, null, null, null, [], _zoa);

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

        TrackOwner tower = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("OAK_TWR", facilityHint: null)!.Id)!;
        var roster = PilotContactRoster.Build(true, tower, "TWR", null, [], _zoa);

        PilotAnsweringPosition? answering = roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(tower, "TWR"), true);
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

        TrackOwner tower = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("OAK_TWR", facilityHint: null)!.Id)!;
        var roster = PilotContactRoster.Build(true, tower, "TWR", null, [TestAiPositions.OakGround(_zoa)], _zoa);

        PilotAnsweringPosition? ground = roster.ResolveFor(OakAircraft(), "GND", "OAK", Eligibility(tower, "TWR"), true);
        PilotAnsweringPosition? local = roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(tower, "TWR"), false);
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

        TrackOwner ground = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("OAK_GND", facilityHint: null)!.Id)!;
        var roster = PilotContactRoster.Build(true, ground, "GND", null, [TestAiPositions.OakTower(_zoa)], _zoa);

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

        var roster = PilotContactRoster.Build(false, null, null, null, [TestAiPositions.SfoGround(_zoa)], _zoa);

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
        AircraftState toSfo = OakAircraft();
        toSfo.FlightPlan.Destination = "KSFO";
        var both = PilotContactRoster.Build(false, null, null, null, [TestAiPositions.SfoGround(_zoa), TestAiPositions.OakGround(_zoa)], _zoa);
        PilotAnsweringPosition? answering = both.ResolveFor(toSfo, "GND", "OAK", Eligibility(null, null), true);
        Assert.Equal("OAK_GND", answering!.Owner!.Callsign);

        var sfoOnly = PilotContactRoster.Build(false, null, null, null, [TestAiPositions.SfoGround(_zoa)], _zoa);
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

        AiPositionConfig aiTower = TestAiPositions.OakTower(_zoa);
        TrackOwner approach = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("NCT_APP", facilityHint: null)!.Id)!;
        var roster = PilotContactRoster.Build(false, null, null, null, [aiTower], _zoa);
        InitialContactEligibilityContext eligibility = Eligibility(null, null);
        AircraftState owned = OakAircraft();
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

        var roster = PilotContactRoster.Build(false, null, null, null, [TestAiPositions.OakTower(_zoa)], _zoa);

        PilotAnsweringPosition? ground = roster.ResolveFor(OakAircraft(), "GND", "OAK", Eligibility(null, null), true);
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

        var roster = PilotContactRoster.Build(false, null, null, null, [TestAiPositions.OakGround(_zoa)], _zoa);

        Assert.Null(roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(null, null), false));
    }

    [Fact]
    public void AiEntryThatIsTheStudentPosition_IsDroppedWhileSolo()
    {
        if (_zoa is null)
        {
            return;
        }

        AiPositionConfig ai = TestAiPositions.OakGround(_zoa);
        var roster = PilotContactRoster.Build(true, ai.Identity, "GND", null, [ai], _zoa);

        PilotAnsweringPosition answering = Assert.Single(roster.Positions);
        Assert.Equal(PilotAnsweringAgent.Student, answering.Agent);
    }

    [Fact]
    public void Student_StaysSubjectToTheSopEligibilityRules()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner tower = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("OAK_TWR", facilityHint: null)!.Id)!;
        TrackOwner approach = _zoa.ResolvePosition(_zoa.FindPositionByCallsign("NCT_APP", facilityHint: null)!.Id)!;
        var roster = PilotContactRoster.Build(true, tower, "TWR", null, [], _zoa);
        AircraftState owned = OakAircraft();
        owned.Track.Owner = approach;

        // Owned by approach with no handoff: the parking / final call-ups check the SOP and stay silent,
        // exactly as CanInitiateWithStudent decided before the roster.
        InitialContactEligibilityContext eligibility = Eligibility(tower, "TWR");
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

        AiPositionConfig oakGround = TestAiPositions.OakGround(_zoa);
        var roster = PilotContactRoster.Build(false, null, null, null, [oakGround, TestAiPositions.SfoGround(_zoa)], _zoa);
        AircraftState aircraft = OakAircraft();
        PilotAnsweringPosition oak = roster.ResolveFor(aircraft, "GND", "OAK", Eligibility(null, null), true)!;
        PilotAnsweringPosition sfo = roster.ResolveFor(aircraft, "GND", "SFO", Eligibility(null, null, "SFO"), true)!;

        oak.MarkInitialContact(aircraft);

        Assert.True(oak.HasInitialContact(aircraft));
        Assert.False(sfo.HasInitialContact(aircraft));
        Assert.False(aircraft.HasMadeInitialContact);
        Assert.Equal([oakGround.PositionId], aircraft.AiInitialContactPositionIds);
    }

    [Fact]
    public void UnsetRange_GroundStudent_StillAnswersTheTowerCall()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner ground = Resolve("OAK_GND");
        var roster = PilotContactRoster.Build(true, ground, "GND", null, [], _zoa);

        Assert.Equal(PilotAnsweringAgent.Student, roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(ground, "GND"), false)!.Agent);
    }

    [Fact]
    public void GroundOnly_TowerCall_GoesNowhere()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner ground = Resolve("OAK_GND");
        var roster = PilotContactRoster.Build(true, ground, "GND", Range(ground, AtcPositionLevel.Ground, []), [], _zoa);

        Assert.Null(roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(ground, "GND"), false));
    }

    [Fact]
    public void GroundOnly_TowerCall_AiTowerStillAnswers()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner ground = Resolve("OAK_GND");
        var roster = PilotContactRoster.Build(
            true,
            ground,
            "GND",
            Range(ground, AtcPositionLevel.Ground, []),
            [TestAiPositions.OakTower(_zoa)],
            _zoa
        );

        PilotAnsweringPosition? answering = roster.ResolveFor(OakAircraft(), "TWR", "OAK", Eligibility(ground, "GND"), false);
        Assert.Equal(PilotAnsweringAgent.ControllerAi, answering!.Agent);
        Assert.Equal("OAK_TWR", answering.Owner!.Callsign);
    }

    [Fact]
    public void GroundOnly_GroundCall_StudentAnswers()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner ground = Resolve("OAK_GND");
        var roster = PilotContactRoster.Build(true, ground, "GND", Range(ground, AtcPositionLevel.Ground, []), [], _zoa);

        Assert.Equal(PilotAnsweringAgent.Student, roster.ResolveFor(OakAircraft(), "GND", "OAK", Eligibility(ground, "GND"), false)!.Agent);
    }

    [Fact]
    public void ApproachOnly_GroundAndTowerCalls_GoNowhere()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner approach = Resolve("NCT_APP");
        var roster = PilotContactRoster.Build(true, approach, "APP", Range(approach, AtcPositionLevel.Approach, []), [], _zoa);
        InitialContactEligibilityContext eligibility = Eligibility(approach, "APP");

        Assert.Null(roster.ResolveFor(OakAircraft(), "GND", "OAK", eligibility, false));
        Assert.Null(roster.ResolveFor(OakAircraft(), "TWR", "OAK", eligibility, false));
        Assert.Equal(PilotAnsweringAgent.Student, roster.ResolveFor(OakAircraft(), "APP", "OAK", eligibility, false)!.Agent);
    }

    [Fact]
    public void ApproachDownToDelivery_AnswersEveryType()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner approach = Resolve("NCT_APP");
        var roster = PilotContactRoster.Build(true, approach, "APP", Range(approach, AtcPositionLevel.Delivery, []), [], _zoa);
        InitialContactEligibilityContext eligibility = Eligibility(approach, "APP");

        foreach (string type in new[] { "GND", "TWR", "APP" })
        {
            Assert.Equal(PilotAnsweringAgent.Student, roster.ResolveFor(OakAircraft(), type, "OAK", eligibility, false)!.Agent);
        }

        Assert.True(roster.StudentWorksDeliveryAt("OAK"));
        // Center is above the student's own position.
        Assert.Null(roster.ResolveFor(OakAircraft(), "CTR", "OAK", eligibility, false));
    }

    [Fact]
    public void OnlineOakTower_TakesTowerOutOfRange_AtOakOnly()
    {
        if (_zoa is null)
        {
            return;
        }

        // ZOA's OAK_TWR student would be the online OAK_TWR itself, so an approach student works down to delivery.
        TrackOwner approach = Resolve("NCT_APP");
        var onlineTower = new ResolvedAtcPosition
        {
            Source = new ScenarioAtc { FacilityId = "OAK", AutoConnect = true },
            Owner = Resolve("OAK_TWR"),
        };
        var roster = PilotContactRoster.Build(true, approach, "APP", Range(approach, AtcPositionLevel.Delivery, [onlineTower]), [], _zoa);
        InitialContactEligibilityContext eligibility = Eligibility(approach, "APP");

        Assert.Null(roster.ResolveFor(OakAircraft(), "TWR", "OAK", eligibility, false));
        Assert.Equal(PilotAnsweringAgent.Student, roster.ResolveFor(OakAircraft(), "TWR", "SFO", eligibility, false)!.Agent);
        Assert.Equal(PilotAnsweringAgent.Student, roster.ResolveFor(OakAircraft(), "GND", "OAK", eligibility, false)!.Agent);
    }

    [Fact]
    public void StudentWorksDeliveryAt_GroundStudent_FollowsTheLowest()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner ground = Resolve("OAK_GND");

        Assert.False(
            PilotContactRoster.Build(true, ground, "GND", Range(ground, AtcPositionLevel.Ground, []), [], _zoa).StudentWorksDeliveryAt("OAK")
        );
        Assert.True(
            PilotContactRoster.Build(true, ground, "GND", Range(ground, AtcPositionLevel.Delivery, []), [], _zoa).StudentWorksDeliveryAt("OAK")
        );
        Assert.False(PilotContactRoster.Build(true, ground, "GND", null, [], _zoa).StudentWorksDeliveryAt("OAK"));
    }

    [Fact]
    public void TowerIsSimulatedAt_GroundOnly_NoAiTower_IsTrue()
    {
        if (_zoa is null)
        {
            return;
        }

        TrackOwner ground = Resolve("OAK_GND");
        TrackOwner tower = Resolve("OAK_TWR");
        StudentWorkedRange? groundOnly = Range(ground, AtcPositionLevel.Ground, []);

        Assert.True(PilotContactRoster.Build(true, ground, "GND", groundOnly, [], _zoa).TowerIsSimulatedAt("OAK"));
        Assert.False(PilotContactRoster.Build(true, ground, "GND", groundOnly, [TestAiPositions.OakTower(_zoa)], _zoa).TowerIsSimulatedAt("OAK"));
        Assert.False(PilotContactRoster.Build(true, tower, "TWR", Range(tower, AtcPositionLevel.Tower, []), [], _zoa).TowerIsSimulatedAt("OAK"));
        Assert.False(PilotContactRoster.Build(true, tower, "TWR", Range(tower, AtcPositionLevel.Delivery, []), [], _zoa).TowerIsSimulatedAt("OAK"));
        Assert.False(PilotContactRoster.Build(true, ground, "GND", null, [], _zoa).TowerIsSimulatedAt("OAK"));
    }

    private TrackOwner Resolve(string callsign) => _zoa!.ResolvePosition(_zoa!.FindPositionByCallsign(callsign, facilityHint: null)!.Id)!;

    private static StudentWorkedRange? Range(TrackOwner student, AtcPositionLevel lowest, IReadOnlyList<ResolvedAtcPosition> atcPositions) =>
        StudentWorkedRange.For(student, lowest, atcPositions);

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
            _zoa is null ? Yaat.Sim.Data.InitialContactTransferCatalog.Empty : Yaat.Sim.Data.NavigationDatabase.Instance.InitialContactTransfers
        );
}
