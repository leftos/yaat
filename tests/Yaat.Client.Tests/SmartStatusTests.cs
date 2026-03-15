using Xunit;
using Yaat.Client.Models;

namespace Yaat.Client.Tests;

public class SmartStatusTests
{
    private static AircraftModel CreateModel()
    {
        return new AircraftModel { Callsign = "AAL100", AircraftType = "B738" };
    }

    [Fact]
    public void FinalApproach_NoLandingClearance_Critical()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "FinalApproach";
        ac.LandingClearance = "";
        ac.ComputeSmartStatus();

        Assert.Equal("No landing clnc", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Critical, ac.SmartStatusSeverity);
    }

    [Fact]
    public void FinalApproach_WithLandingClearance_Normal()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "FinalApproach";
        ac.LandingClearance = "ClearedToLand";
        ac.ActiveApproachId = "ILS28R";
        ac.ComputeSmartStatus();

        Assert.Equal("ILS28R final", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Normal, ac.SmartStatusSeverity);
    }

    [Fact]
    public void Landing_NoLandingClearance_Critical()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Landing";
        ac.LandingClearance = "";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Landing — no clnc!", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Critical, ac.SmartStatusSeverity);
    }

    [Fact]
    public void LandingH_NoLandingClearance_Critical()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Landing-H";
        ac.LandingClearance = "";
        ac.ComputeSmartStatus();

        Assert.Equal("Landing — no clnc!", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Critical, ac.SmartStatusSeverity);
    }

    [Fact]
    public void HandoffPeer_Warning()
    {
        var ac = CreateModel();
        ac.HandoffPeer = "conn123";
        ac.HandoffPeerSectorCode = "NR";
        ac.ComputeSmartStatus();

        Assert.Equal("HO → NR", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Warning, ac.SmartStatusSeverity);
    }

    [Fact]
    public void HandoffPeer_NoSectorCode_UsesConnectionId()
    {
        var ac = CreateModel();
        ac.HandoffPeer = "conn123";
        ac.HandoffPeerSectorCode = null;
        ac.ComputeSmartStatus();

        Assert.Equal("HO → conn123", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Warning, ac.SmartStatusSeverity);
    }

    [Fact]
    public void Airborne_NoPhase_NoSid_NoAlt_NoRoute_NotDelayed_Warning()
    {
        var ac = CreateModel();
        ac.IsOnGround = false;
        ac.CurrentPhase = "";
        ac.ActiveSidId = "";
        ac.ActiveStarId = "";
        ac.AssignedAltitude = null;
        ac.NavigationRoute = "";
        ac.Status = "Active";
        ac.VerticalSpeed = 0;
        ac.ComputeSmartStatus();

        Assert.Equal("No altitude asgn", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Warning, ac.SmartStatusSeverity);
    }

    [Fact]
    public void Airborne_NoPhase_WithSid_NoWarning()
    {
        var ac = CreateModel();
        ac.IsOnGround = false;
        ac.CurrentPhase = "";
        ac.ActiveSidId = "OAK5";
        ac.AssignedAltitude = null;
        ac.NavigationRoute = "";
        ac.VerticalSpeed = 500;
        ac.ComputeSmartStatus();

        Assert.NotEqual("No altitude asgn", ac.SmartStatus);
        Assert.NotEqual(SmartStatusSeverity.Warning, ac.SmartStatusSeverity);
    }

    [Fact]
    public void Delayed_NoAltWarning_Suppressed()
    {
        var ac = CreateModel();
        ac.IsOnGround = false;
        ac.CurrentPhase = "";
        ac.ActiveSidId = "";
        ac.ActiveStarId = "";
        ac.AssignedAltitude = null;
        ac.NavigationRoute = "";
        ac.Status = "Delayed (120s)";
        ac.VerticalSpeed = 0;
        ac.ComputeSmartStatus();

        Assert.NotEqual("No altitude asgn", ac.SmartStatus);
    }

    [Fact]
    public void AtParking_WithSpot()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "At Parking";
        ac.ParkingSpot = "A12";
        ac.IsOnGround = true;
        ac.ComputeSmartStatus();

        Assert.Equal("At parking A12", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Normal, ac.SmartStatusSeverity);
    }

    [Fact]
    public void AtParking_NoSpot()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "At Parking";
        ac.ParkingSpot = "";
        ac.IsOnGround = true;
        ac.ComputeSmartStatus();

        Assert.Equal("At parking", ac.SmartStatus);
    }

    [Fact]
    public void Pushback()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Pushback";
        ac.IsOnGround = true;
        ac.ComputeSmartStatus();

        Assert.Equal("Pushing back", ac.SmartStatus);
    }

    [Fact]
    public void HoldingShort_WithTarget()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Holding Short 28R";
        ac.IsOnGround = true;
        ac.ComputeSmartStatus();

        Assert.Equal("Hold short 28R", ac.SmartStatus);
    }

    [Fact]
    public void Taxiing_WithRunway_WithRoute()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Taxiing";
        ac.AssignedRunway = "28R";
        ac.TaxiRoute = "A B C D E";
        ac.IsOnGround = true;
        ac.ComputeSmartStatus();

        Assert.Equal("Taxi to RWY 28R via A B C", ac.SmartStatus);
    }

    [Fact]
    public void Taxiing_NoRunway_NoRoute()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Taxiing";
        ac.AssignedRunway = "";
        ac.TaxiRoute = "";
        ac.IsOnGround = true;
        ac.ComputeSmartStatus();

        Assert.Equal("Taxiing", ac.SmartStatus);
    }

    [Fact]
    public void LinedUpAndWaiting()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "LinedUpAndWaiting";
        ac.AssignedRunway = "28R";
        ac.IsOnGround = true;
        ac.ComputeSmartStatus();

        Assert.Equal("LUAW 28R", ac.SmartStatus);
    }

    [Fact]
    public void Takeoff()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Takeoff";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Takeoff 28R", ac.SmartStatus);
    }

    [Fact]
    public void InitialClimb_WithSid()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "InitialClimb";
        ac.DepartureRunway = "28R";
        ac.ActiveSidId = "OAK5";
        ac.ComputeSmartStatus();

        Assert.Equal("Departing 28R, OAK5", ac.SmartStatus);
    }

    [Fact]
    public void InitialClimb_WithHeading()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "InitialClimb";
        ac.DepartureRunway = "28R";
        ac.ActiveSidId = "";
        ac.AssignedHeading = 280;
        ac.NavigatingTo = "";
        ac.ComputeSmartStatus();

        Assert.Equal("Departing 28R, hdg 280", ac.SmartStatus);
    }

    [Fact]
    public void ApproachNav_WithRoute()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "ApproachNav";
        ac.ActiveApproachId = "ILS28R";
        ac.NavigationRoute = "CEPIN > DUMBA > AXMUL";
        ac.ComputeSmartStatus();

        Assert.Equal("ILS28R → CEPIN DUMBA AXMUL", ac.SmartStatus);
    }

    [Fact]
    public void HoldingPattern_WithFix()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "HoldingPattern";
        ac.NavigatingTo = "CEPIN";
        ac.ComputeSmartStatus();

        Assert.Equal("Holding at CEPIN", ac.SmartStatus);
    }

    [Fact]
    public void Landing_WithClearance()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Landing";
        ac.LandingClearance = "ClearedToLand";
        ac.ClearedRunway = "28R";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Landing 28R", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Normal, ac.SmartStatusSeverity);
    }

    [Fact]
    public void GoAround()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "GoAround";
        ac.ClearedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Go-around 28R", ac.SmartStatus);
    }

    [Fact]
    public void PatternDownwind()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Downwind";
        ac.PatternDirection = "Left";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Left downwind 28R", ac.SmartStatus);
    }

    [Fact]
    public void TouchAndGo()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "TouchAndGo";
        ac.ClearedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Touch-and-go 28R", ac.SmartStatus);
    }

    [Fact]
    public void NoPhase_OnGround_Stationary()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "";
        ac.IsOnGround = true;
        ac.GroundSpeed = 3;
        ac.ComputeSmartStatus();

        Assert.Equal("On ground", ac.SmartStatus);
    }

    [Fact]
    public void NoPhase_Climbing_WithAssignedAlt()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "";
        ac.IsOnGround = false;
        ac.VerticalSpeed = 1500;
        ac.AssignedAltitude = 10000;
        ac.NavigationRoute = "";
        ac.ActiveSidId = "OAK5";
        ac.ComputeSmartStatus();

        Assert.Equal("\u2191 10,000", ac.SmartStatus);
    }

    [Fact]
    public void NoPhase_Climbing_NoAssignedAlt()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "";
        ac.IsOnGround = false;
        ac.VerticalSpeed = 1500;
        ac.AssignedAltitude = null;
        ac.NavigationRoute = "";
        ac.ActiveSidId = "OAK5";
        ac.ComputeSmartStatus();

        Assert.Equal("Climbing", ac.SmartStatus);
    }

    [Fact]
    public void NoPhase_Climbing_WithNavRoute()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "";
        ac.IsOnGround = false;
        ac.VerticalSpeed = 1500;
        ac.AssignedAltitude = 24000;
        ac.NavigationRoute = "OAK > SFO";
        ac.ActiveSidId = "OAK5";
        ac.ComputeSmartStatus();

        Assert.Equal("\u2191 FL240 \u2192 OAK SFO", ac.SmartStatus);
    }

    [Fact]
    public void NoPhase_Descending_WithAssignedAlt()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "";
        ac.IsOnGround = false;
        ac.VerticalSpeed = -1500;
        ac.AssignedAltitude = 5000;
        ac.NavigationRoute = "";
        ac.ActiveStarId = "STAR1";
        ac.ComputeSmartStatus();

        Assert.Equal("\u2193 5,000", ac.SmartStatus);
    }

    [Fact]
    public void NoPhase_Level_WithNavRoute()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "";
        ac.IsOnGround = false;
        ac.VerticalSpeed = 0;
        ac.NavigationRoute = "OAK > SFO > LAX";
        ac.ActiveSidId = "OAK5";
        ac.ComputeSmartStatus();

        Assert.Equal("\u2192 OAK SFO LAX", ac.SmartStatus);
    }

    [Fact]
    public void NoPhase_Level_NavigatingToFix()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "";
        ac.IsOnGround = false;
        ac.VerticalSpeed = 0;
        ac.NavigationRoute = "";
        ac.NavigatingTo = "CEPIN";
        ac.ActiveSidId = "OAK5";
        ac.ComputeSmartStatus();

        Assert.Equal("\u2192 CEPIN", ac.SmartStatus);
    }

    [Fact]
    public void NoPhase_Level_NothingSet()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "";
        ac.IsOnGround = false;
        ac.VerticalSpeed = 0;
        ac.NavigationRoute = "";
        ac.NavigatingTo = "";
        ac.Altitude = 35000;
        ac.ActiveSidId = "OAK5";
        ac.ComputeSmartStatus();

        Assert.Equal("FL350, on course", ac.SmartStatus);
    }

    [Fact]
    public void FormatAltitudeCompact_Below18000()
    {
        Assert.Equal("10,000", AircraftModel.FormatAltitudeCompact(10000));
    }

    [Fact]
    public void FormatAltitudeCompact_AtOrAbove18000()
    {
        Assert.Equal("FL350", AircraftModel.FormatAltitudeCompact(35000));
    }

    [Fact]
    public void FormatAltitudeCompact_FL180()
    {
        Assert.Equal("FL180", AircraftModel.FormatAltitudeCompact(18000));
    }

    [Fact]
    public void HoldPresentPosition()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "HPP-L";
        ac.ComputeSmartStatus();

        Assert.Equal("Hold present position", ac.SmartStatus);
    }

    [Fact]
    public void STurns()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "S-Turns";
        ac.ComputeSmartStatus();

        Assert.Equal("S-turns", ac.SmartStatus);
    }

    [Fact]
    public void Following()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Following UAL456";
        ac.ComputeSmartStatus();

        Assert.Equal("Following UAL456", ac.SmartStatus);
    }

    [Fact]
    public void TurnPhase()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "TurnL90";
        ac.ComputeSmartStatus();

        Assert.Equal("Turning", ac.SmartStatus);
    }

    [Fact]
    public void RunwayExit()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Runway Exit";
        ac.ComputeSmartStatus();

        Assert.Equal("Exiting runway", ac.SmartStatus);
    }

    [Fact]
    public void PatternEntry()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Pattern Entry";
        ac.PatternDirection = "Right";
        ac.ComputeSmartStatus();

        Assert.Equal("Right pattern entry", ac.SmartStatus);
    }

    [Fact]
    public void MidfieldCrossing()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "MidfieldCrossing";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Midfield crossing 28R", ac.SmartStatus);
    }

    [Fact]
    public void InterceptCourse_WithApproach()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "InterceptCourse";
        ac.ActiveApproachId = "ILS28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Intercepting ILS28R", ac.SmartStatus);
    }

    [Fact]
    public void InterceptCourse_NoApproach()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "InterceptCourse";
        ac.ActiveApproachId = null;
        ac.ComputeSmartStatus();

        Assert.Equal("Intercepting course", ac.SmartStatus);
    }

    [Fact]
    public void ProceedToFix()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "ProceedToFix";
        ac.NavigatingTo = "OAKEY";
        ac.ComputeSmartStatus();

        Assert.Equal("Proceeding to OAKEY", ac.SmartStatus);
    }

    [Fact]
    public void StopAndGo()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "StopAndGo";
        ac.ClearedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Stop-and-go 28R", ac.SmartStatus);
    }

    [Fact]
    public void LowApproach()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "LowApproach";
        ac.ClearedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Low approach 28R", ac.SmartStatus);
    }

    [Fact]
    public void AirTaxi()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "AirTaxi";
        ac.ComputeSmartStatus();

        Assert.Equal("Air taxi", ac.SmartStatus);
    }

    [Fact]
    public void CrossingRunway()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Crossing Runway";
        ac.ComputeSmartStatus();

        Assert.Equal("Crossing runway", ac.SmartStatus);
    }

    [Fact]
    public void HoldingAfterExit()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Holding After Exit";
        ac.ComputeSmartStatus();

        Assert.Equal("Clear of runway", ac.SmartStatus);
    }

    [Fact]
    public void AlertPriority_HandoffOverridesPhase()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Taxiing";
        ac.HandoffPeer = "conn1";
        ac.HandoffPeerSectorCode = "NR";
        ac.ComputeSmartStatus();

        Assert.Equal("HO → NR", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Warning, ac.SmartStatusSeverity);
    }

    [Fact]
    public void AlertPriority_FinalNoClearanceOverridesHandoff()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "FinalApproach";
        ac.LandingClearance = "";
        ac.HandoffPeer = "conn1";
        ac.ComputeSmartStatus();

        Assert.Equal("No landing clnc", ac.SmartStatus);
        Assert.Equal(SmartStatusSeverity.Critical, ac.SmartStatusSeverity);
    }
}
