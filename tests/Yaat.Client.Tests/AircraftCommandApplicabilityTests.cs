using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

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
    public void IsVfr_MatchesFlightRules(string rules, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.IsVfr(Ac("", false, rules)));
    }

    [Fact]
    public void NullAircraft_AllPredicatesFalse()
    {
        Assert.False(AircraftCommandApplicability.IsVfr(null));
        Assert.False(AircraftCommandApplicability.CanLineUpAndWait(null));
        Assert.False(AircraftCommandApplicability.CanClearForTakeoff(null));
        Assert.False(AircraftCommandApplicability.ShowVfrTakeoffModifiers(null));
        Assert.False(AircraftCommandApplicability.CanCancelTakeoff(null));
        Assert.False(AircraftCommandApplicability.CanClearToLand(null));
        Assert.False(AircraftCommandApplicability.CanIssueVfrOption(null));
        Assert.False(AircraftCommandApplicability.CanGoAround(null));
        Assert.False(AircraftCommandApplicability.CanCancelLandingClearance(null));
        Assert.False(AircraftCommandApplicability.CanExitRunway(null));
        Assert.False(AircraftCommandApplicability.CanEnterPattern(null));
        Assert.False(AircraftCommandApplicability.CanDrawTaxiRoute(null));
    }

    // --- Departures: Line up and wait ---

    [Theory]
    [InlineData("Holding Short 28L/10R", true, true)]
    [InlineData("Holding Short", true, true)]
    [InlineData("LinedUpAndWaiting", true, false)] // already on the runway
    [InlineData("At Parking", true, false)]
    [InlineData("FinalApproach", false, false)] // airborne arrival
    public void CanLineUpAndWait_ByPhase(string phase, bool onGround, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanLineUpAndWait(Ac(phase, onGround)));
    }

    [Theory]
    [InlineData("28R", true)]
    [InlineData("", false)] // taxiing with no runway assigned yet
    public void CanLineUpAndWait_Taxiing_RequiresAssignedRunway(string runway, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanLineUpAndWait(Ac("Taxiing", true, assignedRunway: runway)));
    }

    // --- Departures: Cleared for takeoff ---

    [Theory]
    [InlineData("LinedUpAndWaiting", true, true)]
    [InlineData("LiningUp", true, true)]
    [InlineData("Holding Short 28L", true, true)]
    [InlineData("Takeoff", true, true)] // on-ground rolling re-clear
    [InlineData("Takeoff", false, false)] // airborne — already departing
    [InlineData("At Parking", true, false)]
    [InlineData("FinalApproach", false, false)]
    public void CanClearForTakeoff_ByPhase(string phase, bool onGround, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanClearForTakeoff(Ac(phase, onGround)));
    }

    [Theory]
    [InlineData("28R", true)]
    [InlineData("", false)]
    public void CanClearForTakeoff_Taxiing_RequiresAssignedRunway(string runway, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanClearForTakeoff(Ac("Taxiing", true, assignedRunway: runway)));
    }

    [Theory]
    [InlineData("VFR", true)]
    [InlineData("IFR", false)]
    public void ShowVfrTakeoffModifiers_VfrOnly(string rules, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.ShowVfrTakeoffModifiers(Ac("LinedUpAndWaiting", true, rules)));
    }

    // --- Departures: Cancel takeoff ---

    [Theory]
    [InlineData("LinedUpAndWaiting", true, true)]
    [InlineData("LiningUp", true, true)]
    [InlineData("Takeoff", true, true)]
    [InlineData("Takeoff", false, false)]
    [InlineData("Holding Short 28L", true, false)]
    public void CanCancelTakeoff_ByPhase(string phase, bool onGround, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanCancelTakeoff(Ac(phase, onGround)));
    }

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
    [InlineData("FinalApproach", "VFR", true)]
    [InlineData("Downwind", "VFR", true)]
    [InlineData("FinalApproach", "IFR", false)] // VFR-only
    [InlineData("Downwind", "IFR", false)]
    [InlineData("Landing", "VFR", false)] // no pending landing phase
    public void CanIssueVfrOption_RequiresPendingLandingAndVfr(string phase, string rules, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanIssueVfrOption(Ac(phase, false, rules)));
    }

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
    public void CanGoAround_ByPhase(string phase, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanGoAround(Ac(phase, false)));
    }

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
        var ac = Ac(phase, false, "IFR", phaseSequence: sequence);
        Assert.Equal(expected, AircraftCommandApplicability.CanClearToLand(ac));
        Assert.Equal(expected, AircraftCommandApplicability.CanGoAround(ac));
    }

    [Fact]
    public void TransientManeuver_VfrInPattern_KeepsOptionClearances()
    {
        var ac = Ac("TurnL360", false, "VFR", phaseSequence: "TurnL360 > Downwind > Base > FinalApproach > Landing");
        Assert.True(AircraftCommandApplicability.CanIssueVfrOption(ac));
    }

    // --- Arrivals: Cancel landing clearance (only when a clearance is set) ---

    [Theory]
    [InlineData("ClearedToLand", true)]
    [InlineData("ClearedForOption", true)]
    [InlineData("", false)]
    public void CanCancelLandingClearance_RequiresActiveClearance(string clearance, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanCancelLandingClearance(Ac("FinalApproach", false, landingClearance: clearance)));
    }

    // --- Runway exit (fixed-wing only) ---

    [Theory]
    [InlineData("Landing", true)]
    [InlineData("Runway Exit", true)]
    [InlineData("Landing-H", false)] // helicopters land to a spot, not a runway exit
    [InlineData("FinalApproach", false)]
    public void CanExitRunway_ByPhase(string phase, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanExitRunway(Ac(phase, false)));
    }

    // --- Pattern entry (airborne VFR; inbound / holding / pending-landing) ---

    [Theory]
    [InlineData("", "VFR", false, true)] // free-flight inbound
    [InlineData("HoldingPattern", "VFR", false, true)]
    [InlineData("Downwind", "VFR", false, true)]
    [InlineData("FinalApproach", "VFR", false, true)]
    [InlineData("", "IFR", false, false)] // IFR cannot enter VFR pattern
    [InlineData("Downwind", "IFR", false, false)]
    [InlineData("Taxiing", "VFR", true, false)] // on ground
    [InlineData("InitialClimb", "VFR", false, false)] // departing
    public void CanEnterPattern_ByPhaseAndRules(string phase, string rules, bool onGround, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanEnterPattern(Ac(phase, onGround, rules)));
    }

    // --- Ground routing: draw / preset taxi route ---

    [Theory]
    [InlineData("At Parking", true, true)]
    [InlineData("Pushback", true, true)]
    [InlineData("Pushback to Spot", true, true)]
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
    public void CanDrawTaxiRoute_ByPhase(string phase, bool onGround, bool expected)
    {
        Assert.Equal(expected, AircraftCommandApplicability.CanDrawTaxiRoute(Ac(phase, onGround)));
    }
}
