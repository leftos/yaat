using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Situation;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins the owner-reviewed show/hide rules of the default quick lists: each rule shows its entry in the case it allows and
/// hides it in the case it forbids. Widened entries are the phases <c>QuickCommandSimAcceptanceTests</c> proves the sim
/// accepts; narrowed entries are the cases the sim refuses.
/// </summary>
public class QuickCommandVisibilityRulesTests
{
    private static AircraftModel Aircraft(AircraftSituation situation, string phase, bool onGround, string rules) =>
        new()
        {
            Callsign = "TST123",
            Situation = situation,
            CurrentPhase = phase,
            IsOnGround = onGround,
            FlightRules = rules,
        };

    private static MenuContext Context(VfrCommandsForIfr mode) =>
        new(new MenuClick("TST123", null, null, []), new MenuSession("XX", false, mode, QuickCommandDefaults.For));

    private static bool Shows(AircraftModel aircraft, string id, VfrCommandsForIfr mode)
    {
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(aircraft, Context(mode), _ => true);
        return resolution.Strip.Any(item => item.Entry.Id == id) || resolution.Text.Any(entry => entry.Id == id);
    }

    private static bool Lists(AircraftSituation situation, string id) =>
        QuickCommandDefaults.For(situation).Any(entry => entry is CatalogQuickCommandEntry catalog && catalog.CatalogId == id);

    // --- Dropped entries ---

    [Fact]
    public void PushingBack_NoLongerListsFollow() => Assert.False(Lists(AircraftSituation.PushingBack, MenuIds.GroundFollow));

    [Theory]
    [InlineData(MenuIds.TowerExitLeft)]
    [InlineData(MenuIds.TowerExitRight)]
    public void LinedUp_NoLongerListsTheExits(string id) => Assert.False(Lists(AircraftSituation.LinedUp, id));

    [Fact]
    public void RolloutExit_NoLongerListsHoldShort() => Assert.False(Lists(AircraftSituation.RolloutExit, MenuIds.GroundHoldShort));

    // --- Widened entries ---

    [Theory]
    [InlineData("Pushback", true)]
    [InlineData("Holding After Pushback", true)]
    [InlineData("Taxiing", false)]
    public void PushRoute_ShowsDuringThePush(string phase, bool shown) =>
        Assert.Equal(
            shown,
            Shows(Aircraft(AircraftSituation.PushingBack, phase, onGround: true, "VFR"), MenuIds.GroundPushRoute, VfrCommandsForIfr.None)
        );

    [Theory]
    [InlineData(AircraftSituation.LinedUp, "LiningUp", true)]
    [InlineData(AircraftSituation.LinedUp, "LinedUpAndWaiting", true)]
    [InlineData(AircraftSituation.LinedUp, "Takeoff", false)]
    [InlineData(AircraftSituation.RolloutExit, "Landing", true)]
    [InlineData(AircraftSituation.RolloutExit, "Runway Exit", true)]
    [InlineData(AircraftSituation.RolloutExit, "Clearing Runway", false)]
    public void DrawTaxiRoute_ShowsOnTheRunwayWhereTheSimTakesATaxi(AircraftSituation situation, string phase, bool shown) =>
        Assert.Equal(shown, Shows(Aircraft(situation, phase, onGround: true, "VFR"), MenuIds.GroundDrawTaxiRoute, VfrCommandsForIfr.None));

    [Theory]
    [InlineData("GoAround", "VFR", true)]
    [InlineData("LowApproach", "VFR", true)]
    [InlineData("GoAround", "IFR", false)]
    public void PatternEntry_ShowsAtTheStartOfTheClimbOut(string phase, string rules, bool shown) =>
        Assert.Equal(
            shown,
            Shows(Aircraft(AircraftSituation.GoAround, phase, onGround: false, rules), MenuIds.PatternEnterLeftDownwind, VfrCommandsForIfr.None)
        );

    [Theory]
    [InlineData("LevelBelowBravo", MenuIds.PatternEnterFinal)]
    [InlineData("HoldOutsideCharlie", MenuIds.PatternEnterRightBase)]
    [InlineData("AR anchor AR601", MenuIds.PatternEnterLeftDownwind)]
    public void PatternEntry_ShowsInTheBoundaryAndAnchorHolds(string phase, string id) =>
        Assert.True(Shows(Aircraft(AircraftSituation.Holding, phase, onGround: false, "VFR"), id, VfrCommandsForIfr.None));

    [Fact]
    public void PatternEntry_HiddenInAnUnprovenHold() =>
        Assert.False(
            Shows(Aircraft(AircraftSituation.Holding, "VfrHold", onGround: false, "VFR"), MenuIds.PatternEnterLeftDownwind, VfrCommandsForIfr.None)
        );

    [Fact]
    public void Follow_ShowsWhileFollowing() =>
        Assert.True(
            Shows(Aircraft(AircraftSituation.Taxiing, "Following LEAD1", onGround: true, "VFR"), MenuIds.GroundFollow, VfrCommandsForIfr.None)
        );

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GiveWay_WhileFollowing_ShowsOnlyWithATaxiRoute(bool hasRoute)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.Taxiing, "Following LEAD1", onGround: true, "VFR");
        aircraft.HasActiveTaxiRoute = hasRoute;
        Assert.Equal(hasRoute, Shows(aircraft, MenuIds.GroundGiveWay, VfrCommandsForIfr.None));
    }

    /// <summary>
    /// Exit left / right are on the Final list and show there, and in All Commands, only while the server lists the exits ahead
    /// (5 NM down to 1 NM), on either side whatever the list holds: on a side with no exit forecast the flyout offers the
    /// "Exit at" box over the runway's exits there and no pilot's choice, and drops out only when the runway has none on that
    /// side (judged when the flyout is built). Inside 1 NM the list is gone and they hide in the quick list until the roll
    /// decelerates.
    /// </summary>
    [Theory]
    [InlineData("both sides", true, true)]
    [InlineData("left only", true, true)]
    [InlineData("right only", true, true)]
    [InlineData("empty", true, true)]
    [InlineData("none", false, false)]
    public void ExitEntries_OnFinal_ShowOnBothSidesWhileAListIsStored(string list, bool left, bool right)
    {
        Assert.True(Lists(AircraftSituation.Final, MenuIds.TowerExitLeft));
        Assert.True(Lists(AircraftSituation.Final, MenuIds.TowerExitRight));
        AircraftModel aircraft = Aircraft(AircraftSituation.Final, "FinalApproach", onGround: false, "IFR");
        var leftRow = new ExitAheadDto("W1", ExitSide.Left, 4300, false, 90);
        var rightRow = new ExitAheadDto("W2", ExitSide.Right, 5200, false, 90);
        aircraft.ExitsAhead = list switch
        {
            "both sides" => [leftRow, rightRow],
            "left only" => [leftRow],
            "right only" => [rightRow],
            "empty" => [],
            _ => null,
        };

        Assert.Equal(left, Shows(aircraft, MenuIds.TowerExitLeft, VfrCommandsForIfr.None));
        Assert.Equal(right, Shows(aircraft, MenuIds.TowerExitRight, VfrCommandsForIfr.None));
        Assert.Equal(list != "none", AircraftCommandApplicability.CanExitRunway(aircraft));
    }

    /// <summary>On the rollout the entries show only once the roll decelerates, whether or not a list is stored.</summary>
    [Theory]
    [InlineData(SituationFlags.RolloutDecelerating, true, true)]
    [InlineData(SituationFlags.RolloutDecelerating, false, true)]
    [InlineData(SituationFlags.None, true, false)]
    [InlineData(SituationFlags.None, false, false)]
    public void ExitEntries_Rollout_ShowOnlyOnceDecelerating(SituationFlags flags, bool listed, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.RolloutExit, "Landing", onGround: true, "IFR");
        aircraft.SituationFlags = flags;
        aircraft.ExitsAhead = listed
            ? [new ExitAheadDto("W1", ExitSide.Left, 900, false, 90), new ExitAheadDto("W2", ExitSide.Right, 1800, true, 90)]
            : null;

        Assert.Equal(shown, Shows(aircraft, MenuIds.TowerExitLeft, VfrCommandsForIfr.None));
        Assert.Equal(shown, Shows(aircraft, MenuIds.TowerExitRight, VfrCommandsForIfr.None));
    }

    /// <summary>Cross shows while taxiing, holding on the ground or rolling out only with a runway to cross next.</summary>
    [Theory]
    [InlineData(AircraftSituation.Taxiing, "Taxiing", "28R", true)]
    [InlineData(AircraftSituation.Taxiing, "Taxiing", null, false)]
    [InlineData(AircraftSituation.HoldingOnGround, "Holding In Position", "28R", true)]
    [InlineData(AircraftSituation.HoldingOnGround, "Holding In Position", null, false)]
    [InlineData(AircraftSituation.RolloutExit, "Runway Exit", "10L", true)]
    [InlineData(AircraftSituation.RolloutExit, "Runway Exit", null, false)]
    [InlineData(AircraftSituation.HoldingOnGround, "Holding After Exit", "28R", true)]
    [InlineData(AircraftSituation.HoldingOnGround, "Holding After Pushback", "28R", true)]
    [InlineData(AircraftSituation.RolloutExit, "Clearing Runway", "10L", true)]
    [InlineData(AircraftSituation.RolloutExit, "Rejected Takeoff", "10L", true)]
    public void CrossRunway_OffAHoldShort_ShowsOnlyWithARunwayToCrossNext(AircraftSituation situation, string phase, string? nextCrossing, bool shown)
    {
        AircraftModel aircraft = Aircraft(situation, phase, onGround: true, "VFR");
        aircraft.AssignedRunway = "28L";
        aircraft.NextCrossingRunway = nextCrossing;
        Assert.Equal(shown, Shows(aircraft, MenuIds.GroundCrossRunway, VfrCommandsForIfr.None));
    }

    /// <summary>Cancel takeoff clearance shows while taxiing only with a takeoff clearance stored for the runway.</summary>
    [Theory]
    [InlineData(SituationFlags.HasTakeoffClearance, true)]
    [InlineData(SituationFlags.None, false)]
    public void CancelTakeoff_WhileTaxiing_ShowsOnlyWithAStoredTakeoffClearance(SituationFlags flags, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.Taxiing, "Taxiing", onGround: true, "VFR");
        aircraft.SituationFlags = flags;
        Assert.Equal(shown, Shows(aircraft, MenuIds.TowerCancelTakeoff, VfrCommandsForIfr.None));
    }

    // --- Narrowed entries ---

    [Theory]
    [InlineData("Takeoff", SituationFlags.HasTakeoffClearance, true)]
    [InlineData("Takeoff", SituationFlags.HasTakeoffClearance | SituationFlags.PastV1, false)]
    [InlineData("LinedUpAndWaiting", SituationFlags.None, false)]
    public void CancelTakeoff_ShowsOnlyClearedForTakeoffAndBelowV1(string phase, SituationFlags flags, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.LinedUp, phase, onGround: true, "VFR");
        aircraft.SituationFlags = flags;
        Assert.Equal(shown, Shows(aircraft, MenuIds.TowerCancelTakeoff, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData(SituationFlags.None, true)]
    [InlineData(SituationFlags.ApproachClearedForDescent, false)]
    public void ClearedApproach_HiddenOnceClearedForTheApproach(SituationFlags flags, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.Approach, "InterceptCourse", onGround: false, rules: "IFR");
        aircraft.SituationFlags = flags;
        Assert.Equal(shown, Shows(aircraft, MenuIds.ApproachCleared, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData(MenuIds.TowerClearedForTakeoff, false, true)]
    [InlineData(MenuIds.TowerClearedForTakeoff, true, false)]
    [InlineData(MenuIds.TowerLineUpAndWait, false, true)]
    [InlineData(MenuIds.TowerLineUpAndWait, true, false)]
    public void DepartureClearance_HiddenWhileHeldForRelease(string id, bool held, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.HoldingShort, "Holding Short 28L", onGround: true, "VFR");
        aircraft.AssignedRunway = "28L";
        aircraft.SituationFlags = SituationFlags.HoldShortIsDepartureRunway;
        aircraft.IsHeldForRelease = held;
        Assert.Equal(shown, Shows(aircraft, id, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GiveWay_WhileTaxiing_ShowsOnlyWithATaxiRoute(bool hasRoute)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.Taxiing, "Taxiing", onGround: true, "VFR");
        aircraft.HasActiveTaxiRoute = hasRoute;
        Assert.Equal(hasRoute, Shows(aircraft, MenuIds.GroundGiveWay, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData("NIMI5", true)]
    [InlineData("", false)]
    public void ClimbViaSid_ShowsOnlyWithASid(string sid, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.Departing, "InitialClimb", onGround: false, rules: "IFR");
        aircraft.ActiveSidId = sid;
        Assert.Equal(shown, Shows(aircraft, MenuIds.ProceduresClimbViaSid, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData("SERFR4", true)]
    [InlineData("", false)]
    public void DescendViaStar_ShowsOnlyWithAStar(string star, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.IfrArrival, "", onGround: false, rules: "IFR");
        aircraft.ActiveStarId = star;
        Assert.Equal(shown, Shows(aircraft, MenuIds.ProceduresDescendViaStar, VfrCommandsForIfr.None));
    }

    // --- Other rulings ---

    [Theory]
    [InlineData("", true)]
    [InlineData("ClearedToLand", false)]
    public void ClearedToLand_OnFinal_HiddenOnceCleared(string clearance, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.Final, "FinalApproach", onGround: false, rules: "IFR");
        aircraft.LandingClearance = clearance;
        Assert.Equal(shown, Shows(aircraft, MenuIds.TowerClearedToLand, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData(3000.0, true)]
    [InlineData(null, false)]
    public void ClearedApproach_AfterAGoAround_ShowsOnlyWithAnAssignedAltitude(double? altitude, bool shown)
    {
        AircraftModel aircraft = Aircraft(AircraftSituation.GoAround, "GoAround", onGround: false, rules: "IFR");
        aircraft.AssignedAltitude = altitude;
        Assert.Equal(shown, Shows(aircraft, MenuIds.ApproachCleared, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData(AircraftSituation.Holding, "HoldingPattern")]
    [InlineData(AircraftSituation.GoAround, "GoAround")]
    public void ClearedApproach_IsIfrOnly_InTheHoldingAndGoAroundLists(AircraftSituation situation, string phase)
    {
        AircraftModel ifr = Aircraft(situation, phase, onGround: false, rules: "IFR");
        ifr.AssignedAltitude = 3000;
        AircraftModel vfr = Aircraft(situation, phase, onGround: false, rules: "VFR");
        vfr.AssignedAltitude = 3000;

        Assert.True(Shows(ifr, MenuIds.ApproachCleared, VfrCommandsForIfr.None));
        Assert.False(Shows(vfr, MenuIds.ApproachCleared, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData(VfrCommandsForIfr.All, true)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly, false)]
    [InlineData(VfrCommandsForIfr.None, false)]
    public void VfrOnlyEntry_ForIfr_FollowsTheVfrCommandsForIfrSetting(VfrCommandsForIfr mode, bool shown) =>
        Assert.Equal(
            shown,
            Shows(Aircraft(AircraftSituation.Holding, "HoldingPattern", onGround: false, rules: "IFR"), MenuIds.PatternEnterLeftDownwind, mode)
        );

    [Theory]
    [InlineData(VfrCommandsForIfr.All, true)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly, true)]
    [InlineData(VfrCommandsForIfr.None, false)]
    public void VfrOnlyEnterFinal_ForIfr_ShowsUnderEnterFinalOnly(VfrCommandsForIfr mode, bool shown) =>
        Assert.Equal(
            shown,
            Shows(Aircraft(AircraftSituation.Holding, "HoldingPattern", onGround: false, rules: "IFR"), MenuIds.PatternEnterFinal, mode)
        );

    // --- Helicopters: Air taxi and Land, and no push rows ---

    private static AircraftModel Typed(string type, AircraftSituation situation, string phase, bool onGround)
    {
        TestVnasData.EnsureInitialized();
        AircraftModel aircraft = Aircraft(situation, phase, onGround, rules: "VFR");
        aircraft.AircraftType = type;
        return aircraft;
    }

    [Theory]
    [InlineData("EC35", false, true)]
    [InlineData("B738", true, false)]
    public void AtParking_PushRowsHiddenForAHelicopter_AirTaxiShownInstead(string type, bool pushShown, bool airTaxiShown)
    {
        AircraftModel parked = Typed(type, AircraftSituation.AtParking, "At Parking", onGround: true);

        Assert.Equal(pushShown, Shows(parked, MenuIds.GroundPushback, VfrCommandsForIfr.None));
        Assert.Equal(pushShown, Shows(parked, MenuIds.GroundPushbackTo, VfrCommandsForIfr.None));
        Assert.Equal(airTaxiShown, Shows(parked, MenuIds.GroundAirTaxi, VfrCommandsForIfr.None));
    }

    /// <summary>The push rows are a quick-list rule only: All Commands, which reads applicability alone, keeps them for a helicopter.</summary>
    [Fact]
    public void AtParking_Helicopter_AllCommandsKeepsThePushRows()
    {
        AircraftModel parked = Typed("EC35", AircraftSituation.AtParking, "At Parking", onGround: true);
        MenuContext context = Context(VfrCommandsForIfr.None);

        Assert.True(MenuCatalog.Get(MenuIds.GroundPushback).IsApplicable(parked, context));
        Assert.True(MenuCatalog.Get(MenuIds.GroundPushbackTo).IsApplicable(parked, context));
    }

    [Theory]
    [InlineData(AircraftSituation.HoldingOnGround, "Holding In Position")]
    [InlineData(AircraftSituation.HoldingShort, "Holding Short 28R")]
    [InlineData(AircraftSituation.RolloutExit, "Clearing Runway")]
    public void GroundSituations_ShowAirTaxi_ForAHelicopterOnly(AircraftSituation situation, string phase)
    {
        Assert.True(Shows(Typed("EC35", situation, phase, onGround: true), MenuIds.GroundAirTaxi, VfrCommandsForIfr.None));
        Assert.False(Shows(Typed("C172", situation, phase, onGround: true), MenuIds.GroundAirTaxi, VfrCommandsForIfr.None));
    }

    /// <summary>
    /// On the landing rollout a helicopter is offered Air taxi only once it decelerates, as Cross runway waits: a ground
    /// movement instruction is not issued on the landing roll right after touchdown (7110.65 §3-10-9.a NOTE).
    /// </summary>
    [Theory]
    [InlineData(SituationFlags.None, false)]
    [InlineData(SituationFlags.RolloutDecelerating, true)]
    public void LandingRollout_ShowsAirTaxi_OnlyOnceDecelerating(SituationFlags flags, bool shown)
    {
        AircraftModel heli = Typed("EC35", AircraftSituation.RolloutExit, "Landing", onGround: true);
        heli.SituationFlags = flags;

        Assert.Equal(shown, Shows(heli, MenuIds.GroundAirTaxi, VfrCommandsForIfr.None));
    }

    [Theory]
    [InlineData(AircraftSituation.VfrFlightFollowing, "")]
    [InlineData(AircraftSituation.Taxiing, "AirTaxi")]
    [InlineData(AircraftSituation.Holding, "VFR Hold")]
    [InlineData(AircraftSituation.Final, "HelicopterApproach")]
    public void AirborneSituations_ShowLand_ForAHelicopterOnly(AircraftSituation situation, string phase)
    {
        Assert.True(Shows(Typed("EC35", situation, phase, onGround: false), MenuIds.GroundLandHelicopter, VfrCommandsForIfr.None));
        Assert.False(Shows(Typed("EC35", situation, phase, onGround: false), MenuIds.GroundAirTaxi, VfrCommandsForIfr.None));
        Assert.False(Shows(Typed("C172", situation, phase, onGround: false), MenuIds.GroundLandHelicopter, VfrCommandsForIfr.None));
    }
}
