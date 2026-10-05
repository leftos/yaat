using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// Truth-table guard for <see cref="AircraftCommandApplicability"/>. Each predicate is
/// checked against representative phases and both flight-rule values, matching the
/// aviation-validated phase→command table in the context-menu cleanup plan.
/// </summary>
public class AircraftCommandApplicabilityTests
{
    private static AircraftModel Ac(
        string phase,
        bool onGround,
        string rules = "IFR",
        string assignedRunway = "",
        string landingClearance = "",
        string phaseSequence = ""
    )
    {
        return new AircraftModel
        {
            Callsign = "TST123",
            CurrentPhase = phase,
            IsOnGround = onGround,
            FlightRules = rules,
            AssignedRunway = assignedRunway,
            LandingClearance = landingClearance,
            PhaseSequence = phaseSequence,
        };
    }

    // --- IsVfr ---

    [Theory]
    [InlineData("VFR", true)]
    [InlineData("vfr", true)]
    [InlineData("IFR", false)]
    [InlineData("", false)]
    public void IsVfr_MatchesFlightRules(string rules, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.IsVfr(Ac("", false, rules)));

    [Fact]
    public void NullAircraft_AllPredicatesFalse()
    {
        Assert.False(AircraftCommandApplicability.IsVfr(null));
        Assert.False(AircraftCommandApplicability.CanLineUpAndWait(null));
        Assert.False(AircraftCommandApplicability.CanClearForTakeoff(null));
        Assert.False(AircraftCommandApplicability.ShowVfrTakeoffModifiers(null, VfrCommandsForIfr.All));
        Assert.False(AircraftCommandApplicability.CanCancelTakeoff(null));
        Assert.False(AircraftCommandApplicability.CanClearToLand(null));
        Assert.False(AircraftCommandApplicability.CanIssueVfrOption(null, VfrCommandsForIfr.All));
        Assert.False(AircraftCommandApplicability.CanGoAround(null));
        Assert.False(AircraftCommandApplicability.CanCancelLandingClearance(null));
        Assert.False(AircraftCommandApplicability.CanExitRunway(null));
        Assert.False(AircraftCommandApplicability.CanEnterPattern(null, VfrCommandsForIfr.All));
        Assert.False(AircraftCommandApplicability.CanEnterFinal(null, VfrCommandsForIfr.All));
        Assert.False(AircraftCommandApplicability.CanIssuePatternManeuvers(null, VfrCommandsForIfr.All));
        Assert.False(AircraftCommandApplicability.CanDrawTaxiRoute(null));
        Assert.False(AircraftCommandApplicability.CanAskPilot(null));
        Assert.False(AircraftCommandApplicability.CanEditFlightPlan(null));
    }

    // --- Departures: Line up and wait ---

    [Theory]
    [InlineData("Holding Short 28L/10R", true, true)]
    [InlineData("Holding Short", true, false)] // no runway to name: neither held nor assigned
    [InlineData("LinedUpAndWaiting", true, false)] // already on the runway
    [InlineData("At Parking", true, false)]
    [InlineData("FinalApproach", false, false)] // airborne arrival
    public void CanLineUpAndWait_ByPhase(string phase, bool onGround, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanLineUpAndWait(Ac(phase, onGround)));

    [Theory]
    [InlineData("28R", true)]
    [InlineData("", false)] // taxiing with no runway assigned yet
    public void CanLineUpAndWait_Taxiing_RequiresAssignedRunway(string runway, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanLineUpAndWait(Ac("Taxiing", true, assignedRunway: runway)));

    // --- Departures: Cleared for takeoff ---

    [Theory]
    [InlineData("LinedUpAndWaiting", true, true)]
    [InlineData("LiningUp", true, true)]
    [InlineData("Holding Short 28L", true, true)]
    [InlineData("Holding Short", true, false)] // no runway to name: neither held nor assigned
    [InlineData("Takeoff", true, false)] // never once rolling
    [InlineData("Takeoff", false, false)] // airborne — already departing
    [InlineData("At Parking", true, false)]
    [InlineData("FinalApproach", false, false)]
    public void CanClearForTakeoff_ByPhase(string phase, bool onGround, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanClearForTakeoff(Ac(phase, onGround)));

    [Theory]
    [InlineData("28R", true)]
    [InlineData("", false)]
    public void CanClearForTakeoff_Taxiing_RequiresAssignedRunway(string runway, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanClearForTakeoff(Ac("Taxiing", true, assignedRunway: runway)));

    [Theory]
    [InlineData("VFR", VfrCommandsForIfr.None, true)]
    [InlineData("VFR", VfrCommandsForIfr.EnterFinalOnly, true)]
    [InlineData("VFR", VfrCommandsForIfr.All, true)]
    [InlineData("IFR", VfrCommandsForIfr.None, false)]
    [InlineData("IFR", VfrCommandsForIfr.EnterFinalOnly, false)]
    [InlineData("IFR", VfrCommandsForIfr.All, true)]
    public void ShowVfrTakeoffModifiers_VfrOrOptedIn(string rules, VfrCommandsForIfr mode, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowVfrTakeoffModifiers(Ac("LinedUpAndWaiting", true, rules), mode));

    // --- Departures: Cancel takeoff ---

    [Theory]
    [InlineData("LinedUpAndWaiting", true, true)]
    [InlineData("LiningUp", true, true)]
    [InlineData("Takeoff", true, true)]
    [InlineData("Takeoff", false, false)]
    [InlineData("Holding Short 28L", true, false)]
    public void CanCancelTakeoff_ByPhase(string phase, bool onGround, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanCancelTakeoff(Ac(phase, onGround)));

    // --- Arrivals: Cleared to land (both rules, throughout the pattern + approach) ---

    [Theory]
    [InlineData("FinalApproach", true)]
    [InlineData("ApproachNav", true)]
    [InlineData("InterceptCourse", true)]
    [InlineData("Pattern Entry", true)]
    [InlineData("Upwind", true)]
    [InlineData("Crosswind", true)]
    [InlineData("Downwind", true)]
    [InlineData("Base", true)]
    [InlineData("MidfieldCrossing", true)]
    [InlineData("GoAround", true)] // re-clear after go-around
    [InlineData("Landing", false)] // already on the runway
    [InlineData("InitialClimb", false)]
    [InlineData("Takeoff", false)]
    [InlineData("", false)]
    public void CanClearToLand_ByPhase_RuleIndependent(string phase, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanClearToLand(Ac(phase, false, "IFR")));
        Assert.Equal(expected, AircraftCommandApplicability.CanClearToLand(Ac(phase, false, "VFR")));
    }

    // --- Arrivals: VFR options (touch-and-go / stop-and-go / low approach / option) ---

    [Theory]
    [InlineData("FinalApproach", "VFR", VfrCommandsForIfr.None, true)]
    [InlineData("Downwind", "VFR", VfrCommandsForIfr.None, true)]
    [InlineData("FinalApproach", "IFR", VfrCommandsForIfr.None, false)]
    [InlineData("FinalApproach", "IFR", VfrCommandsForIfr.EnterFinalOnly, false)] // EF only opens EF, not the option set
    [InlineData("FinalApproach", "IFR", VfrCommandsForIfr.All, true)]
    [InlineData("Downwind", "IFR", VfrCommandsForIfr.All, true)]
    [InlineData("Landing", "VFR", VfrCommandsForIfr.All, false)] // no pending landing phase
    public void CanIssueVfrOption_RequiresPendingLandingAndRules(string phase, string rules, VfrCommandsForIfr mode, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanIssueVfrOption(Ac(phase, false, rules), mode));

    // --- Arrivals: Go around ---

    [Theory]
    [InlineData("FinalApproach", true)]
    [InlineData("Downwind", true)]
    [InlineData("Base", true)]
    [InlineData("TouchAndGo", true)]
    [InlineData("StopAndGo", true)]
    [InlineData("LowApproach", true)]
    [InlineData("Landing", false)] // committed rollout — not offered
    [InlineData("GoAround", false)] // already going around
    [InlineData("InitialClimb", false)]
    public void CanGoAround_ByPhase(string phase, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanGoAround(Ac(phase, false)));

    // --- Arrivals during transient maneuvers (360/270/S-turn/procedure-turn) ---
    // CurrentPhase drops the leg/approach name; a landing in the sequence keeps the items.

    [Theory]
    [InlineData("TurnL360", "Downwind > Base > FinalApproach > Landing", true)]
    [InlineData("TurnR270", "FinalApproach > Landing", true)]
    [InlineData("S-Turns", "ApproachNav > FinalApproach > Landing", true)]
    [InlineData("ProcedureTurn", "ProcedureTurn > FinalApproach > Landing", true)]
    [InlineData("TurnL360", "", false)] // enroute 360 for spacing — no landing pending
    [InlineData("TurnL360", "TurnL360", false)]
    [InlineData("TeardropReentry", "TeardropReentry > HoldingPattern", false)] // pure holding entry
    public void TransientManeuver_ClearToLandFollowsPendingLanding(string phase, string sequence, bool expected)
    {
        AircraftModel ac = Ac(phase, false, "IFR", phaseSequence: sequence);
        Assert.Equal(expected, AircraftCommandApplicability.CanClearToLand(ac));
        Assert.Equal(expected, AircraftCommandApplicability.CanGoAround(ac));
    }

    [Fact]
    public void TransientManeuver_VfrInPattern_KeepsOptionClearances()
    {
        AircraftModel ac = Ac("TurnL360", false, "VFR", phaseSequence: "TurnL360 > Downwind > Base > FinalApproach > Landing");
        Assert.True(AircraftCommandApplicability.CanIssueVfrOption(ac, VfrCommandsForIfr.None));
    }

    /// <summary>
    /// An aircraft still free-flying toward its pattern entry ("DCT VPCOL; ERD 28R", entry queued) has no
    /// arrival phase, but the clearance verbs are pre-issued against the queued entry — so the menu must
    /// still offer them.
    /// </summary>
    [Fact]
    public void QueuedPatternEntry_OffersClearancesWithNoArrivalPhase()
    {
        AircraftModel ac = Ac("", false, "VFR");
        Assert.False(AircraftCommandApplicability.CanClearToLand(ac));

        ac.HasQueuedPatternEntry = true;
        Assert.True(AircraftCommandApplicability.CanClearToLand(ac));
        Assert.True(AircraftCommandApplicability.CanIssueVfrOption(ac, VfrCommandsForIfr.None));
    }

    // --- Arrivals: Cancel landing clearance (only when a clearance is set) ---

    [Theory]
    [InlineData("ClearedToLand", true)]
    [InlineData("ClearedForOption", true)]
    [InlineData("", false)]
    public void CanCancelLandingClearance_RequiresActiveClearance(string clearance, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanCancelLandingClearance(Ac("FinalApproach", false, landingClearance: clearance)));

    /// <summary>A clearance pre-issued against a queued entry is cancellable too, with no phase yet.</summary>
    [Fact]
    public void CanCancelLandingClearance_CoversPreIssuedClearance()
    {
        AircraftModel ac = Ac("", false, "VFR");
        ac.HasQueuedPatternEntry = true;
        Assert.False(AircraftCommandApplicability.CanCancelLandingClearance(ac));

        ac.PendingLandingClearance = "ClearedToLand";
        Assert.True(AircraftCommandApplicability.CanCancelLandingClearance(ac));
    }

    // --- Runway exit (fixed-wing only) ---

    [Theory]
    [InlineData("Landing", true)]
    [InlineData("Runway Exit", true)]
    [InlineData("Landing-H", false)] // helicopters land to a spot, not a runway exit
    [InlineData("FinalApproach", false)]
    public void CanExitRunway_ByPhase(string phase, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanExitRunway(Ac(phase, false)));

    // --- Pattern entry (airborne VFR; inbound / holding / pending-landing) ---

    [Theory]
    [InlineData("", "VFR", false, true)] // free-flight inbound
    [InlineData("HoldingPattern", "VFR", false, true)]
    [InlineData("Downwind", "VFR", false, true)]
    [InlineData("FinalApproach", "VFR", false, true)]
    [InlineData("", "IFR", false, false)] // circuit legs stay closed for IFR under the default mode
    [InlineData("Downwind", "IFR", false, false)]
    [InlineData("Taxiing", "VFR", true, false)] // on ground
    [InlineData("InitialClimb", "VFR", false, false)] // departing
    public void CanEnterPattern_ByPhaseAndRules(string phase, string rules, bool onGround, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanEnterPattern(Ac(phase, onGround, rules), VfrCommandsForIfr.EnterFinalOnly));

    /// <summary>
    /// The circuit-leg entries (ELD/ERD/ELB/ERB) only open up for an IFR aircraft under the full
    /// setting; straight-in final opens under the default one. That split is the point of #317.
    /// </summary>
    [Theory]
    [InlineData(VfrCommandsForIfr.None, false, false)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly, false, true)]
    [InlineData(VfrCommandsForIfr.All, true, true)]
    public void CanEnterPatternVsFinal_IfrDependsOnMode(VfrCommandsForIfr mode, bool circuitLegs, bool straightIn)
    {
        AircraftModel ac = Ac("", false, "IFR");

        Assert.Equal(circuitLegs, AircraftCommandApplicability.CanEnterPattern(ac, mode));
        Assert.Equal(straightIn, AircraftCommandApplicability.CanEnterFinal(ac, mode));
    }

    /// <summary>A VFR aircraft gets both regardless of the setting, and the state gate still applies.</summary>
    [Theory]
    [InlineData(VfrCommandsForIfr.None)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly)]
    [InlineData(VfrCommandsForIfr.All)]
    public void CanEnterPatternVsFinal_VfrAlwaysBoth(VfrCommandsForIfr mode)
    {
        Assert.True(AircraftCommandApplicability.CanEnterPattern(Ac("Downwind", false, "VFR"), mode));
        Assert.True(AircraftCommandApplicability.CanEnterFinal(Ac("Downwind", false, "VFR"), mode));

        // Departing: not a pattern-entry state for either predicate.
        Assert.False(AircraftCommandApplicability.CanEnterPattern(Ac("InitialClimb", false, "VFR"), mode));
        Assert.False(AircraftCommandApplicability.CanEnterFinal(Ac("InitialClimb", false, "VFR"), mode));
    }

    [Theory]
    [InlineData("VFR", VfrCommandsForIfr.None, true)]
    [InlineData("IFR", VfrCommandsForIfr.None, false)]
    [InlineData("IFR", VfrCommandsForIfr.EnterFinalOnly, false)]
    [InlineData("IFR", VfrCommandsForIfr.All, true)]
    public void CanIssuePatternManeuvers_VfrOrOptedIn(string rules, VfrCommandsForIfr mode, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanIssuePatternManeuvers(Ac("Downwind", false, rules), mode));

    // --- Ground routing: draw / preset taxi route ---

    [Theory]
    [InlineData("At Parking", true, true)]
    [InlineData("Pushback", true, true)]
    [InlineData("Taxiing", true, true)]
    [InlineData("Holding After Exit", true, true)]
    [InlineData("Holding After Pushback", true, true)]
    [InlineData("Holding In Position", true, true)] // held mid-taxi — was resume-only
    [InlineData("Holding Short 28L/10R", true, true)]
    [InlineData("Following UAL123", true, true)] // ground taxi-follow — was hold-only
    [InlineData("Crossing Runway", true, false)] // transient runway phase
    [InlineData("Runway Exit", true, false)]
    [InlineData("LiningUp", true, false)]
    [InlineData("LinedUpAndWaiting", true, false)]
    [InlineData("AirTaxi", true, false)]
    [InlineData("Following UAL123", false, false)] // not on ground (guards airborne name reuse)
    [InlineData("VFR Follow", false, false)] // airborne pattern follow — distinct phase name
    [InlineData("FinalApproach", false, false)] // airborne arrival
    public void CanDrawTaxiRoute_ByPhase(string phase, bool onGround, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanDrawTaxiRoute(Ac(phase, onGround)));

    // --- Live traffic shadows ---

    private static AircraftModel Shadow(bool onGround)
    {
        AircraftModel ac = Ac("", onGround, rules: "VFR", assignedRunway: "28R", landingClearance: "CL");
        ac.IsLiveTraffic = true;
        return ac;
    }

    [Fact]
    public void IsControllable_AirborneShadow_IsTrue() => Assert.True(AircraftCommandApplicability.IsControllable(Shadow(onGround: false)));

    [Fact]
    public void IsControllable_SurfaceShadow_IsFalse() => Assert.False(AircraftCommandApplicability.IsControllable(Shadow(onGround: true)));

    // Ask-pilot and the flight-plan editor are not command-path groups: the server refuses a read-only query on a
    // shadow, and refuses a plan edit ahead of the auto-assume gate, so both stay out of every shadow's menu.
    [Theory]
    [InlineData(false, false, true)] // simulated aircraft
    [InlineData(true, false, false)] // airborne shadow
    [InlineData(true, true, false)] // surface shadow
    public void CanAskPilot_SimulatedAircraftOnly(bool liveTraffic, bool onGround, bool expected)
    {
        AircraftModel ac = liveTraffic ? Shadow(onGround) : Ac("FinalApproach", onGround);
        Assert.Equal(expected, AircraftCommandApplicability.CanAskPilot(ac));
    }

    [Theory]
    [InlineData(false, false, true)] // simulated aircraft
    [InlineData(true, false, false)] // airborne shadow
    [InlineData(true, true, false)] // surface shadow
    public void CanEditFlightPlan_SimulatedAircraftOnly(bool liveTraffic, bool onGround, bool expected)
    {
        AircraftModel ac = liveTraffic ? Shadow(onGround) : Ac("FinalApproach", onGround);
        Assert.Equal(expected, AircraftCommandApplicability.CanEditFlightPlan(ac));
    }

    /// <summary>
    /// The airborne arrival / pattern group applies to an assumable shadow as it does to a simulated
    /// aircraft: a command sent to an airborne shadow auto-assumes it server-side before it is applied.
    /// </summary>
    [Fact]
    public void CanClearToLand_AirborneShadow_IsTrue()
    {
        AircraftModel ac = Shadow(onGround: false);
        ac.CurrentPhase = "FinalApproach";
        ac.PhaseSequence = "FinalApproach > Landing";
        Assert.True(AircraftCommandApplicability.CanClearToLand(ac));
        Assert.True(AircraftCommandApplicability.CanGoAround(ac));
    }

    /// <summary>
    /// An airborne shadow is controllable now that a command would auto-assume it, so the airborne command
    /// groups apply; only the state-gated arrival items stay out while it has no arrival phase.
    /// </summary>
    [Fact]
    public void AirborneShadow_IsControllableWithNoArrivalPhaseYet()
    {
        AircraftModel ac = Shadow(onGround: false);
        Assert.True(AircraftCommandApplicability.CanAssume(ac));
        Assert.True(AircraftCommandApplicability.IsControllable(ac));
        Assert.False(AircraftCommandApplicability.CanClearToLand(ac));
        Assert.False(AircraftCommandApplicability.CanGoAround(ac));
        Assert.False(AircraftCommandApplicability.CanIssueVfrOption(ac, VfrCommandsForIfr.All));
        Assert.True(AircraftCommandApplicability.CanEnterPattern(ac, VfrCommandsForIfr.All));
        Assert.True(AircraftCommandApplicability.CanEnterFinal(ac, VfrCommandsForIfr.All));
    }

    [Fact]
    public void SurfaceShadow_NothingApplies_NotEvenAssume()
    {
        AircraftModel ac = Shadow(onGround: true);
        ac.CurrentPhase = "Taxiing";
        Assert.False(AircraftCommandApplicability.CanAssume(ac));
        Assert.False(AircraftCommandApplicability.CanLineUpAndWait(ac));
        Assert.False(AircraftCommandApplicability.CanClearForTakeoff(ac));
        Assert.False(AircraftCommandApplicability.CanCancelTakeoff(ac));
        Assert.False(AircraftCommandApplicability.CanExitRunway(ac));
        Assert.False(AircraftCommandApplicability.CanDrawTaxiRoute(ac));
        Assert.False(AircraftCommandApplicability.ShowVfrTakeoffModifiers(ac, VfrCommandsForIfr.All));
    }

    [Fact]
    public void CanUnassume_OnlyForAnAssumedSimulatedAircraft()
    {
        AircraftModel assumed = Shadow(onGround: false);
        assumed.IsLiveTraffic = false;
        assumed.AssumedFromLiveTraffic = true;
        Assert.True(AircraftCommandApplicability.CanUnassume(assumed));

        // A scenario aircraft never came from the feed, so there is nothing to release it back to.
        AircraftModel scenario = Ac("FinalApproach", onGround: false);
        Assert.False(AircraftCommandApplicability.CanUnassume(scenario));

        // A shadow is live traffic already; the marker alone must not offer the verb.
        AircraftModel shadow = Shadow(onGround: false);
        shadow.AssumedFromLiveTraffic = true;
        Assert.False(AircraftCommandApplicability.CanUnassume(shadow));

        Assert.False(AircraftCommandApplicability.CanUnassume(null));
    }

    [Fact]
    public void AssumedAircraft_IsControllableAgain()
    {
        AircraftModel ac = Shadow(onGround: false);
        ac.IsLiveTraffic = false;
        ac.CurrentPhase = "FinalApproach";
        Assert.False(AircraftCommandApplicability.CanAssume(ac));
        Assert.True(AircraftCommandApplicability.IsControllable(ac));
        Assert.True(AircraftCommandApplicability.CanClearToLand(ac));
        Assert.True(AircraftCommandApplicability.CanEnterFinal(ac, VfrCommandsForIfr.None));
    }

    // --- Ground view gates ---

    [Fact]
    public void CanResumeFromHoldShort_HoldingShortWithAnActiveTaxiRoute()
    {
        AircraftModel withRoute = Ac("Holding Short 28R/10L", onGround: true);
        withRoute.HasActiveTaxiRoute = true;
        Assert.True(AircraftCommandApplicability.CanResumeFromHoldShort(withRoute));

        // Without a route there is nothing to resume onto.
        Assert.False(AircraftCommandApplicability.CanResumeFromHoldShort(Ac("Holding Short 28R/10L", onGround: true)));

        // The stationary holds resume through CanResumeTaxi, not here.
        AircraftModel inPosition = Ac("Holding In Position", onGround: true);
        inPosition.HasActiveTaxiRoute = true;
        Assert.False(AircraftCommandApplicability.CanResumeFromHoldShort(inPosition));

        // A surface shadow is never controllable.
        AircraftModel shadow = Shadow(onGround: true);
        shadow.CurrentPhase = "Holding Short 28R/10L";
        shadow.HasActiveTaxiRoute = true;
        Assert.False(AircraftCommandApplicability.CanResumeFromHoldShort(shadow));

        Assert.False(AircraftCommandApplicability.CanResumeFromHoldShort(null));
    }

    [Theory]
    [InlineData("Holding Short 15/33", "28R", false, true)]
    [InlineData("Holding Short", "10R", false, true)]
    [InlineData("Holding Short", "", false, false)]
    [InlineData("Taxiing", "28R", false, false)]
    [InlineData("LinedUpAndWaiting", "28R", false, false)]
    [InlineData("Holding Short 15/33", "28R", true, false)]
    public void CanCrossRunway_HoldingShortOfARunwayNamedByThePhaseOrTheAssignment(
        string phase,
        string assignedRunway,
        bool surfaceShadow,
        bool expected
    )
    {
        AircraftModel ac = Ac(phase, onGround: true, assignedRunway: assignedRunway);
        ac.IsLiveTraffic = surfaceShadow;
        Assert.Equal(expected, AircraftCommandApplicability.CanCrossRunway(ac));
    }

    [Fact]
    public void CanCheckReleaseWindow_OnTheGroundWithAReleaseWindow()
    {
        AircraftModel ac = Ac("At Parking", onGround: true);
        Assert.False(AircraftCommandApplicability.CanCheckReleaseWindow(ac));

        ac.CfrWindowStartUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(AircraftCommandApplicability.CanCheckReleaseWindow(ac));

        // A surface shadow is never controllable.
        AircraftModel shadow = Shadow(onGround: true);
        shadow.CfrWindowStartUtc = ac.CfrWindowStartUtc;
        Assert.False(AircraftCommandApplicability.CanCheckReleaseWindow(shadow));

        ac.IsOnGround = false;
        Assert.False(AircraftCommandApplicability.CanCheckReleaseWindow(ac));

        Assert.False(AircraftCommandApplicability.CanCheckReleaseWindow(null));
    }

    [Fact]
    public void CanReleaseHeld_HeldAndControllable()
    {
        AircraftModel ac = Ac("At Parking", onGround: true);
        Assert.False(AircraftCommandApplicability.CanReleaseHeld(ac));

        ac.IsHeldForRelease = true;
        Assert.True(AircraftCommandApplicability.CanReleaseHeld(ac));

        // No on-ground term: a departure can be held for release off the ground.
        ac.IsOnGround = false;
        Assert.True(AircraftCommandApplicability.CanReleaseHeld(ac));

        // A surface shadow is never controllable.
        AircraftModel shadow = Shadow(onGround: true);
        shadow.IsHeldForRelease = true;
        Assert.False(AircraftCommandApplicability.CanReleaseHeld(shadow));

        // An airborne shadow is controllable: the sim auto-assumes it and applies the command in the same call.
        AircraftModel airborneShadow = Shadow(onGround: false);
        airborneShadow.IsHeldForRelease = true;
        Assert.True(AircraftCommandApplicability.CanReleaseHeld(airborneShadow));

        Assert.False(AircraftCommandApplicability.CanReleaseHeld(null));
    }

    [Theory]
    [InlineData("Taxiing", false, true)]
    [InlineData("Holding Short 28R", false, false)]
    [InlineData("Following UAL1", false, false)]
    [InlineData("Pushback", false, false)]
    [InlineData("Holding In Position", false, false)]
    [InlineData("Taxiing", true, false)]
    public void CanBreakConflict_TaxiingOnly(string phase, bool surfaceShadow, bool expected)
    {
        AircraftModel ac = Ac(phase, onGround: true);
        ac.IsLiveTraffic = surfaceShadow;
        Assert.Equal(expected, AircraftCommandApplicability.CanBreakConflict(ac));
    }

    // --- Hold short, follow and give way ---

    /// <summary>A menu context for the <see cref="Ac"/> aircraft, with <paramref name="previousSelection"/> selected before it.</summary>
    private static MenuContext Context(AircraftModel? previousSelection) =>
        new(new MenuClick("TST123", previousSelection, null, []), new MenuSession("AB", false, VfrCommandsForIfr.None, QuickCommandDefaults.For));

    /// <summary>Another on-ground aircraft, selected before the right-click, which makes the ground menu relative.</summary>
    private static AircraftModel OtherGroundAircraft()
    {
        AircraftModel other = Ac("Taxiing", onGround: true);
        other.Callsign = "SWA200";
        return other;
    }

    [Theory]
    [InlineData("Taxiing", false, true)]
    [InlineData("At Parking", false, false)]
    [InlineData("Holding Short 28R", false, false)]
    [InlineData("Following UAL1", false, false)]
    [InlineData("Holding In Position", false, false)]
    [InlineData("Taxiing", true, false)]
    public void CanHoldShort_TaxiingOnly(string phase, bool surfaceShadow, bool expected)
    {
        AircraftModel ac = Ac(phase, onGround: true);
        ac.IsLiveTraffic = surfaceShadow;
        Assert.Equal(expected, AircraftCommandApplicability.CanHoldShort(ac));
    }

    [Fact]
    public void CanHoldShort_NoAircraft_IsFalse() => Assert.False(AircraftCommandApplicability.CanHoldShort(null));

    [Theory]
    [InlineData("At Parking", true)]
    [InlineData("Taxiing", true)]
    [InlineData("Holding In Position", true)]
    [InlineData("Holding After Exit", true)]
    [InlineData("Holding After Pushback", true)]
    [InlineData("Pushback", false)]
    [InlineData("Holding Short 28R", false)]
    [InlineData("Following UAL1", false)]
    public void CanFollowBehind_GroundPhases(string phase, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.CanFollowBehind(Ac(phase, onGround: true), Context(null)));

    [Fact]
    public void CanFollowBehind_RelativeSelectionShadowOrNoAircraft_IsFalse()
    {
        AircraftModel ac = Ac("Taxiing", onGround: true);
        AircraftModel selected = OtherGroundAircraft();
        Assert.False(AircraftCommandApplicability.CanFollowBehind(ac, Context(selected)));

        AircraftModel shadow = Ac("Taxiing", onGround: true);
        shadow.IsLiveTraffic = true;
        Assert.False(AircraftCommandApplicability.CanFollowBehind(shadow, Context(null)));

        Assert.False(AircraftCommandApplicability.CanFollowBehind(null, Context(null)));
    }

    /// <summary>An airborne selection, or the right-clicked aircraft itself, offers no relative items, so follow stays.</summary>
    [Fact]
    public void CanFollowBehind_AirborneOrSelfSelection_IsTrue()
    {
        AircraftModel ac = Ac("Taxiing", onGround: true);
        AircraftModel airborne = OtherGroundAircraft();
        airborne.IsOnGround = false;

        Assert.True(AircraftCommandApplicability.CanFollowBehind(ac, Context(airborne)));
        Assert.True(AircraftCommandApplicability.CanFollowBehind(ac, Context(Ac("Taxiing", onGround: true))));
    }

    [Theory]
    [InlineData("Taxiing", false, true)]
    [InlineData("Holding In Position", false, true)]
    [InlineData("Holding After Exit", false, false)]
    [InlineData("Holding After Exit", true, true)]
    [InlineData("Holding After Pushback", false, false)]
    [InlineData("Holding After Pushback", true, true)]
    [InlineData("At Parking", false, false)]
    [InlineData("At Parking", true, false)]
    [InlineData("Pushback", true, false)]
    [InlineData("Holding Short 28R", true, false)]
    public void CanGiveWayTo_GroundPhasesAndTaxiRoute(string phase, bool hasActiveTaxiRoute, bool expected)
    {
        AircraftModel ac = Ac(phase, onGround: true);
        ac.HasActiveTaxiRoute = hasActiveTaxiRoute;
        Assert.Equal(expected, AircraftCommandApplicability.CanGiveWayTo(ac, Context(null)));
    }

    [Fact]
    public void CanGiveWayTo_RelativeSelectionShadowOrNoAircraft_IsFalse()
    {
        AircraftModel ac = Ac("Taxiing", onGround: true);
        AircraftModel selected = OtherGroundAircraft();
        Assert.False(AircraftCommandApplicability.CanGiveWayTo(ac, Context(selected)));

        AircraftModel shadow = Ac("Taxiing", onGround: true);
        shadow.IsLiveTraffic = true;
        Assert.False(AircraftCommandApplicability.CanGiveWayTo(shadow, Context(null)));

        Assert.False(AircraftCommandApplicability.CanGiveWayTo(null, Context(null)));
    }

    /// <summary>An airborne selection, or the right-clicked aircraft itself, offers no relative items, so give way stays.</summary>
    [Fact]
    public void CanGiveWayTo_AirborneOrSelfSelection_IsTrue()
    {
        AircraftModel ac = Ac("Taxiing", onGround: true);
        AircraftModel airborne = OtherGroundAircraft();
        airborne.IsOnGround = false;

        Assert.True(AircraftCommandApplicability.CanGiveWayTo(ac, Context(airborne)));
        Assert.True(AircraftCommandApplicability.CanGiveWayTo(ac, Context(Ac("Taxiing", onGround: true))));
    }

    // --- Quick-list visibility (situation flags) ---

    private static AircraftModel Situated(AircraftSituation situation, SituationFlags flags, string rules)
    {
        AircraftModel ac = Ac("", onGround: false, rules);
        ac.Situation = situation;
        ac.SituationFlags = flags;
        return ac;
    }

    [Theory]
    [InlineData(AircraftSituation.Taxiing, SituationFlags.NearingDepartureHoldLine, true)]
    [InlineData(AircraftSituation.Taxiing, SituationFlags.None, false)]
    [InlineData(AircraftSituation.LinedUp, SituationFlags.None, true)] // the rule hides takeoff only while taxiing
    public void ShowsTakeoffWhileTaxiing_OnlyNearingTheDepartureHoldLine(AircraftSituation situation, SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsTakeoffWhileTaxiing(Situated(situation, flags, "IFR")));

    [Theory]
    [InlineData(AircraftSituation.HoldingShort, SituationFlags.HoldShortIsDepartureRunway, true)]
    [InlineData(AircraftSituation.HoldingShort, SituationFlags.None, false)]
    [InlineData(AircraftSituation.Taxiing, SituationFlags.None, true)] // the rule hides takeoff and LUAW only at a hold-short
    public void ShowsDepartureClearanceAtHoldShort_OnlyAtTheDepartureRunway(AircraftSituation situation, SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsDepartureClearanceAtHoldShort(Situated(situation, flags, "IFR")));

    [Theory]
    [InlineData(AircraftSituation.HoldingShort, SituationFlags.None, true)]
    [InlineData(AircraftSituation.HoldingShort, SituationFlags.HoldShortIsDepartureRunway, false)]
    [InlineData(AircraftSituation.RolloutExit, SituationFlags.HoldShortIsDepartureRunway, true)]
    public void ShowsCrossAtHoldShort_NeverAtTheDepartureRunway(AircraftSituation situation, SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsCrossAtHoldShort(Situated(situation, flags, "IFR")));

    [Theory]
    [InlineData(AircraftSituation.HoldingShort, SituationFlags.None, true)]
    [InlineData(AircraftSituation.HoldingShort, SituationFlags.HoldShortIsDepartureRunway, false)]
    [InlineData(AircraftSituation.HoldingOnGround, SituationFlags.HoldShortIsDepartureRunway, true)]
    public void ShowsResumeTaxiAtHoldShort_NeverAtTheDepartureRunway(AircraftSituation situation, SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsResumeTaxiAtHoldShort(Situated(situation, flags, "IFR")));

    [Theory]
    [InlineData("IFR", SituationFlags.HasReportedFieldInSight, true)]
    [InlineData("IFR", SituationFlags.HasReportedTrafficInSight, true)]
    [InlineData("IFR", SituationFlags.None, false)]
    [InlineData("VFR", SituationFlags.HasReportedFieldInSight, false)]
    public void ShowsClearedVisual_IfrAfterFieldOrTrafficInSight(string rules, SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsClearedVisual(Situated(AircraftSituation.Approach, flags, rules)));

    [Theory]
    [InlineData(SituationFlags.None, true)]
    [InlineData(SituationFlags.InsideFinalApproachFix, false)]
    public void ShowsSpeedAdjustment_HiddenInsideTheFinalApproachFix(SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsSpeedAdjustment(Situated(AircraftSituation.Final, flags, "IFR")));

    [Theory]
    [InlineData(AircraftSituation.RolloutExit, SituationFlags.RolloutDecelerating, true)]
    [InlineData(AircraftSituation.RolloutExit, SituationFlags.None, false)]
    [InlineData(AircraftSituation.LinedUp, SituationFlags.None, true)] // the rule hides exits only on the rollout
    public void ShowsRunwayExit_OnRolloutOnlyOnceDecelerating(AircraftSituation situation, SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsRunwayExit(Situated(situation, flags, "IFR")));

    [Theory]
    [InlineData(AircraftSituation.Taxiing, "28R", true)]
    [InlineData(AircraftSituation.HoldingOnGround, "28R", true)]
    [InlineData(AircraftSituation.RolloutExit, "10L", true)]
    [InlineData(AircraftSituation.Taxiing, null, false)]
    [InlineData(AircraftSituation.RolloutExit, null, false)]
    [InlineData(AircraftSituation.LinedUp, "28R", false)] // the widening admits Cross only in the three ground-movement situations
    public void WidensCrossRunway_OnlyWithARunwayToCrossNext(AircraftSituation situation, string? nextCrossing, bool expected)
    {
        AircraftModel ac = Situated(situation, SituationFlags.None, "IFR");
        ac.IsOnGround = true;
        ac.NextCrossingRunway = nextCrossing;
        Assert.Equal(expected, AircraftCommandApplicability.WidensCrossRunway(ac));
    }

    [Fact]
    public void WidensCrossRunway_Airborne_IsFalse()
    {
        AircraftModel ac = Situated(AircraftSituation.RolloutExit, SituationFlags.None, "IFR");
        ac.NextCrossingRunway = "10L";

        Assert.False(AircraftCommandApplicability.WidensCrossRunway(ac));
    }

    /// <summary>
    /// On the landing rollout Cross waits for the deceleration, as the runway exits do: no taxi instruction immediately
    /// after touchdown (7110.65 §3-10-9 note). Clearing the runway does not wait on it.
    /// </summary>
    [Theory]
    [InlineData("Landing", SituationFlags.None, false)]
    [InlineData("Landing", SituationFlags.RolloutDecelerating, true)]
    [InlineData("Clearing Runway", SituationFlags.None, true)]
    public void WidensCrossRunway_OnTheLandingRollout_OnlyOnceDecelerating(string phase, SituationFlags flags, bool expected)
    {
        AircraftModel ac = Situated(AircraftSituation.RolloutExit, flags, "IFR");
        ac.CurrentPhase = phase;
        ac.IsOnGround = true;
        ac.NextCrossingRunway = "28R";

        Assert.Equal(expected, AircraftCommandApplicability.WidensCrossRunway(ac));
    }

    /// <summary>A rejected takeoff shows Cross only once the aircraft has slowed to taxi speed, not while it brakes from up to V1.</summary>
    [Theory]
    [InlineData(100.0, false)]
    [InlineData(31.0, false)]
    [InlineData(30.0, true)]
    [InlineData(0.0, true)]
    public void WidensCrossRunway_InARejectedTakeoff_OnlyAtTaxiSpeed(double groundSpeedKts, bool expected)
    {
        AircraftModel ac = Situated(AircraftSituation.RolloutExit, SituationFlags.None, "IFR");
        ac.CurrentPhase = "Rejected Takeoff";
        ac.IsOnGround = true;
        ac.GroundSpeed = groundSpeedKts;
        ac.NextCrossingRunway = "28R";

        Assert.Equal(expected, AircraftCommandApplicability.WidensCrossRunway(ac));
    }

    [Theory]
    [InlineData("Taxiing", true, SituationFlags.HasTakeoffClearance, true)]
    [InlineData("Taxiing", true, SituationFlags.None, false)]
    [InlineData("Holding In Position", true, SituationFlags.HasTakeoffClearance, false)]
    [InlineData("Taxiing", false, SituationFlags.HasTakeoffClearance, false)]
    public void WidensCancelTakeoff_OnlyTaxiingWithAStoredTakeoffClearance(string phase, bool onGround, SituationFlags flags, bool expected)
    {
        AircraftModel ac = Situated(AircraftSituation.Taxiing, flags, "IFR");
        ac.CurrentPhase = phase;
        ac.IsOnGround = onGround;

        Assert.Equal(expected, AircraftCommandApplicability.WidensCancelTakeoff(ac));
    }

    [Theory]
    [InlineData(SituationFlags.HasTakeoffClearance, true)]
    [InlineData(SituationFlags.None, false)]
    [InlineData(SituationFlags.HasTakeoffClearance | SituationFlags.PastV1, false)]
    public void ShowsCancelTakeoff_OnlyClearedForTakeoffAndBelowV1(SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsCancelTakeoff(Situated(AircraftSituation.LinedUp, flags, "IFR")));

    [Theory]
    [InlineData(SituationFlags.None, true)]
    [InlineData(SituationFlags.ApproachClearedForDescent, false)]
    public void ShowsApproachClearanceUntilCleared_HiddenOnceClearedForTheApproach(SituationFlags flags, bool expected) =>
        Assert.Equal(expected, AircraftCommandApplicability.ShowsApproachClearanceUntilCleared(Situated(AircraftSituation.Approach, flags, "IFR")));

    [Fact]
    public void QuickVisibilityPredicates_NullAircraft_AreFalse()
    {
        Assert.False(AircraftCommandApplicability.WidensCrossRunway(null));
        Assert.False(AircraftCommandApplicability.WidensCancelTakeoff(null));
        Assert.False(AircraftCommandApplicability.ShowsCancelTakeoff(null));
        Assert.False(AircraftCommandApplicability.ShowsApproachClearanceUntilCleared(null));
        Assert.False(AircraftCommandApplicability.ShowsTakeoffWhileTaxiing(null));
        Assert.False(AircraftCommandApplicability.ShowsDepartureClearanceAtHoldShort(null));
        Assert.False(AircraftCommandApplicability.ShowsCrossAtHoldShort(null));
        Assert.False(AircraftCommandApplicability.ShowsResumeTaxiAtHoldShort(null));
        Assert.False(AircraftCommandApplicability.ShowsClearedVisual(null));
        Assert.False(AircraftCommandApplicability.ShowsSpeedAdjustment(null));
        Assert.False(AircraftCommandApplicability.ShowsRunwayExit(null));
    }
}
