using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using static Yaat.Sim.AircraftStatusDescriber;

namespace Yaat.Client.Tests;

/// <summary>
/// Exercises the shared <see cref="AircraftStatusDescriber"/> projection that produces the Aircraft
/// List "Info" column text. The status is computed server-side from <see cref="AircraftStatusView"/>
/// (an AircraftState projection) and shipped in the DTO; these assertions pin the exact strings.
/// </summary>
public class SmartStatusTests
{
    private static string Text(AircraftStatusView v) => Describe(v).Text;

    private static AircraftStatusSeverity Severity(AircraftStatusView v) => Describe(v).Severity;

    [Fact]
    public void FinalApproach_NoLandingClearance_Critical()
    {
        var v = new AircraftStatusView { CurrentPhase = "FinalApproach", LandingClearance = "" };
        Assert.Equal("No landing clnc", Text(v));
        Assert.Equal(AircraftStatusSeverity.Critical, Severity(v));
    }

    [Fact]
    public void FinalApproach_NoLandingClearance_AutoCtl_Normal()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "FinalApproach",
            LandingClearance = "",
            IsAutoClearedToLand = true,
            ActiveApproachId = "ILS28R",
        };
        Assert.Equal("ILS28R final", Text(v));
        Assert.Equal(AircraftStatusSeverity.Normal, Severity(v));
    }

    [Fact]
    public void FinalApproach_WithLandingClearance_Normal()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "FinalApproach",
            LandingClearance = "ClearedToLand",
            ActiveApproachId = "ILS28R",
        };
        Assert.Equal("ILS28R final", Text(v));
        Assert.Equal(AircraftStatusSeverity.Normal, Severity(v));
    }

    [Fact]
    public void Landing_NoLandingClearance_Critical()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Landing",
            LandingClearance = "",
            AssignedRunway = "28R",
        };
        Assert.Equal("Landing — no clnc!", Text(v));
        Assert.Equal(AircraftStatusSeverity.Critical, Severity(v));
    }

    [Fact]
    public void LandingH_NoLandingClearance_Critical()
    {
        var v = new AircraftStatusView { CurrentPhase = "Landing-H", LandingClearance = "" };
        Assert.Equal("Landing — no clnc!", Text(v));
        Assert.Equal(AircraftStatusSeverity.Critical, Severity(v));
    }

    [Fact]
    public void Landing_NoLandingClearance_AutoCtl_Normal()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Landing",
            LandingClearance = "",
            AssignedRunway = "28R",
            IsAutoClearedToLand = true,
        };
        Assert.Equal("Landing 28R", Text(v));
        Assert.Equal(AircraftStatusSeverity.Normal, Severity(v));
    }

    [Fact]
    public void HandoffPeer_Warning()
    {
        var v = new AircraftStatusView { HandoffPeer = "conn123", HandoffPeerSectorCode = "NR" };
        Assert.Equal("HO → NR", Text(v));
        Assert.Equal(AircraftStatusSeverity.Warning, Severity(v));
    }

    [Fact]
    public void HandoffPeer_NoSectorCode_UsesConnectionId()
    {
        var v = new AircraftStatusView { HandoffPeer = "conn123", HandoffPeerSectorCode = null };
        Assert.Equal("HO → conn123", Text(v));
        Assert.Equal(AircraftStatusSeverity.Warning, Severity(v));
    }

    [Fact]
    public void Airborne_NoPhase_NoSid_NoAlt_NoRoute_NotDelayed_Warning()
    {
        var v = new AircraftStatusView
        {
            IsOnGround = false,
            CurrentPhase = "",
            ActiveSidId = "",
            ActiveStarId = "",
            AssignedAltitude = null,
            VerticalSpeed = 0,
        };
        Assert.Equal("No altitude asgn", Text(v));
        Assert.Equal(AircraftStatusSeverity.Warning, Severity(v));
    }

    [Fact]
    public void Airborne_NoPhase_WithSid_NoWarning()
    {
        var v = new AircraftStatusView
        {
            IsOnGround = false,
            CurrentPhase = "",
            ActiveSidId = "OAK5",
            AssignedAltitude = null,
            VerticalSpeed = 500,
        };
        Assert.NotEqual("No altitude asgn", Text(v));
        Assert.NotEqual(AircraftStatusSeverity.Warning, Severity(v));
    }

    [Fact]
    public void Delayed_NoAltWarning_Suppressed()
    {
        var v = new AircraftStatusView
        {
            IsOnGround = false,
            CurrentPhase = "",
            ActiveSidId = "",
            ActiveStarId = "",
            AssignedAltitude = null,
            IsDelayed = true,
            VerticalSpeed = 0,
        };
        Assert.NotEqual("No altitude asgn", Text(v));
    }

    [Fact]
    public void AtParking_WithSpot()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "At Parking",
            ParkingSpot = "A12",
            IsOnGround = true,
        };
        Assert.Equal("At parking A12", Text(v));
        Assert.Equal(AircraftStatusSeverity.Normal, Severity(v));
    }

    [Fact]
    public void AtParking_NoSpot()
    {
        Assert.Equal(
            "At parking",
            Text(
                new AircraftStatusView
                {
                    CurrentPhase = "At Parking",
                    ParkingSpot = "",
                    IsOnGround = true,
                }
            )
        );
    }

    [Fact]
    public void Pushback()
    {
        Assert.Equal("Pushing back", Text(new AircraftStatusView { CurrentPhase = "Pushback", IsOnGround = true }));
    }

    [Fact]
    public void HoldingShort_WithTarget()
    {
        Assert.Equal("Holding short 28R", Text(new AircraftStatusView { CurrentPhase = "Holding Short 28R", IsOnGround = true }));
    }

    [Fact]
    public void HoldingShort_RunwayWithCurrentTaxiway()
    {
        Assert.Equal(
            "Holding short 28R @ E",
            Text(
                new AircraftStatusView
                {
                    CurrentPhase = "Holding Short 28R",
                    CurrentTaxiway = "E",
                    IsOnGround = true,
                }
            )
        );
    }

    [Fact]
    public void HoldingShort_TaxiwayWithCurrentTaxiway()
    {
        Assert.Equal(
            "Holding short of E on C",
            Text(
                new AircraftStatusView
                {
                    CurrentPhase = "Holding Short E",
                    CurrentTaxiway = "C",
                    IsOnGround = true,
                }
            )
        );
    }

    [Fact]
    public void HoldingShort_TaxiwayNoCurrentTaxiway()
    {
        Assert.Equal("Holding short of E", Text(new AircraftStatusView { CurrentPhase = "Holding Short E", IsOnGround = true }));
    }

    [Fact]
    public void Taxiing_WithRunway_WithRoute()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Taxiing",
            AssignedRunway = "28R",
            TaxiRoute = "A B C D E",
            IsOnGround = true,
        };
        Assert.Equal("Taxi to RWY 28R via A B C D E", Text(v));
    }

    [Fact]
    public void Taxiing_NoRunway_NoRoute()
    {
        Assert.Equal(
            "Taxiing",
            Text(
                new AircraftStatusView
                {
                    CurrentPhase = "Taxiing",
                    AssignedRunway = "",
                    TaxiRoute = "",
                    IsOnGround = true,
                }
            )
        );
    }

    [Fact]
    public void LinedUpAndWaiting()
    {
        Assert.Equal(
            "LUAW 28R",
            Text(
                new AircraftStatusView
                {
                    CurrentPhase = "LinedUpAndWaiting",
                    AssignedRunway = "28R",
                    IsOnGround = true,
                }
            )
        );
    }

    [Fact]
    public void Takeoff()
    {
        Assert.Equal("Takeoff 28R", Text(new AircraftStatusView { CurrentPhase = "Takeoff", AssignedRunway = "28R" }));
    }

    [Fact]
    public void InitialClimb_WithSid()
    {
        Assert.Equal(
            "Departing 28R, OAK5",
            Text(
                new AircraftStatusView
                {
                    CurrentPhase = "InitialClimb",
                    DepartureRunway = "28R",
                    ActiveSidId = "OAK5",
                }
            )
        );
    }

    [Fact]
    public void InitialClimb_Sid_TakesPrecedence_WithTargetAltitude()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            ActiveSidId = "OAK5",
            Departure = new OnCourseDeparture(),
            TargetAltitude = 5000,
        };
        Assert.Equal("Departing 28R, OAK5, ↑ 5,000", Text(v));
    }

    [Fact]
    public void InitialClimb_FlyHeading_NoTurn()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new FlyHeadingDeparture(new MagneticHeading(270), null),
            TargetAltitude = 3000,
        };
        Assert.Equal("Departing 28R, hdg 270, ↑ 3,000", Text(v));
    }

    [Fact]
    public void InitialClimb_FlyHeading_WithTurnDirection()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new FlyHeadingDeparture(new MagneticHeading(270), TurnDirection.Right),
            TargetAltitude = 3000,
        };
        Assert.Equal("Departing 28R, right turn hdg 270, ↑ 3,000", Text(v));
    }

    [Fact]
    public void InitialClimb_RelativeTurn()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new RelativeTurnDeparture(90, TurnDirection.Right),
            TargetAltitude = 2000,
        };
        Assert.Equal("Departing 28R, right turn 90°, ↑ 2,000", Text(v));
    }

    [Fact]
    public void InitialClimb_OnCourse()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new OnCourseDeparture(),
            TargetAltitude = 5000,
        };
        Assert.Equal("Departing 28R, on course, ↑ 5,000", Text(v));
    }

    [Fact]
    public void InitialClimb_DirectFix()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new DirectFixDeparture("VPMID", 37.5, -122.2, null),
            TargetAltitude = 3000,
        };
        Assert.Equal("Departing 28R, → VPMID, ↑ 3,000", Text(v));
    }

    [Fact]
    public void InitialClimb_RunwayHeading()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new RunwayHeadingDeparture(),
            TargetAltitude = 3000,
        };
        Assert.Equal("Departing 28R, runway heading, ↑ 3,000", Text(v));
    }

    [Fact]
    public void InitialClimb_DefaultDeparture_Ifr_ShowsRouteFix()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new DefaultDeparture(),
            NavigatingTo = "SUTRO",
            TargetAltitude = 5000,
        };
        Assert.Equal("Departing 28R, → SUTRO, ↑ 5,000", Text(v));
    }

    [Fact]
    public void InitialClimb_DefaultDeparture_Vfr_RunwayHeading()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new DefaultDeparture(),
            NavigatingTo = "",
            TargetAltitude = 1400,
        };
        Assert.Equal("Departing 28R, runway heading, ↑ 1,400", Text(v));
    }

    [Fact]
    public void InitialClimb_ClosedTraffic_ViaPatternDirection()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            PatternDirection = "Right",
            TargetAltitude = 1400,
        };
        Assert.Equal("Departing 28R, right traffic, ↑ 1,400", Text(v));
    }

    [Fact]
    public void InitialClimb_ClosedTraffic_ViaInstruction()
    {
        // No PatternDirection (e.g. after a snapshot restore where it survives but the
        // instruction degraded) — the live ClosedTrafficDeparture still renders the leg.
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            TargetAltitude = 1400,
        };
        Assert.Equal("Departing 28R, left traffic, ↑ 1,400", Text(v));
    }

    [Fact]
    public void InitialClimb_TargetAltitude_FlightLevel()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = new OnCourseDeparture(),
            TargetAltitude = 23000,
        };
        Assert.Equal("Departing 28R, on course, ↑ FL230", Text(v));
    }

    [Fact]
    public void InitialClimb_NoDeparture_FallsBackToAssignedHeading()
    {
        // No retained departure instruction (or one not yet set): fall back to the
        // assigned magnetic heading. No target altitude → no vertical suffix.
        var v = new AircraftStatusView
        {
            CurrentPhase = "InitialClimb",
            DepartureRunway = "28R",
            Departure = null,
            AssignedHeading = new MagneticHeading(280),
            NavigatingTo = "",
        };
        Assert.Equal("Departing 28R, hdg 280", Text(v));
    }

    [Fact]
    public void ApproachNav_WithRoute()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "ApproachNav",
            ActiveApproachId = "ILS28R",
            NavigationRouteCount = 3,
            NavigationRouteDisplay = "CEPIN DUMBA AXMUL",
        };
        Assert.Equal("ILS28R → CEPIN DUMBA AXMUL", Text(v));
    }

    [Fact]
    public void HoldingPattern_WithFix()
    {
        Assert.Equal("Holding at CEPIN", Text(new AircraftStatusView { CurrentPhase = "HoldingPattern", NavigatingTo = "CEPIN" }));
    }

    [Fact]
    public void Landing_WithClearance()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Landing",
            LandingClearance = "ClearedToLand",
            ClearedRunway = "28R",
            AssignedRunway = "28R",
        };
        Assert.Equal("Landing 28R", Text(v));
        Assert.Equal(AircraftStatusSeverity.Normal, Severity(v));
    }

    [Fact]
    public void GoAround()
    {
        Assert.Equal("Go-around 28R", Text(new AircraftStatusView { CurrentPhase = "GoAround", ClearedRunway = "28R" }));
    }

    [Fact]
    public void PatternDownwind()
    {
        Assert.Equal(
            "Left downwind 28R",
            Text(
                new AircraftStatusView
                {
                    CurrentPhase = "Downwind",
                    PatternDirection = "Left",
                    AssignedRunway = "28R",
                }
            )
        );
    }

    [Fact]
    public void TouchAndGo()
    {
        Assert.Equal("Touch-and-go 28R", Text(new AircraftStatusView { CurrentPhase = "TouchAndGo", ClearedRunway = "28R" }));
    }

    [Fact]
    public void NoPhase_OnGround_Stationary()
    {
        Assert.Equal(
            "On ground",
            Text(
                new AircraftStatusView
                {
                    CurrentPhase = "",
                    IsOnGround = true,
                    GroundSpeed = 3,
                }
            )
        );
    }

    [Fact]
    public void NoPhase_Climbing_WithAssignedAlt()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "",
            IsOnGround = false,
            VerticalSpeed = 1500,
            AssignedAltitude = 10000,
            ActiveSidId = "OAK5",
        };
        Assert.Equal("↑ 10,000", Text(v));
    }

    [Fact]
    public void NoPhase_Climbing_NoAssignedAlt()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "",
            IsOnGround = false,
            VerticalSpeed = 1500,
            AssignedAltitude = null,
            ActiveSidId = "OAK5",
        };
        Assert.Equal("Climbing", Text(v));
    }

    [Fact]
    public void NoPhase_Climbing_WithNavRoute()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "",
            IsOnGround = false,
            VerticalSpeed = 1500,
            AssignedAltitude = 24000,
            NavigationRouteCount = 2,
            NavigationRouteDisplay = "OAK SFO",
            ActiveSidId = "OAK5",
        };
        Assert.Equal("↑ FL240 → OAK SFO", Text(v));
    }

    [Fact]
    public void NoPhase_Descending_WithAssignedAlt()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "",
            IsOnGround = false,
            VerticalSpeed = -1500,
            AssignedAltitude = 5000,
            ActiveStarId = "STAR1",
        };
        Assert.Equal("↓ 5,000", Text(v));
    }

    [Fact]
    public void NoPhase_Level_WithNavRoute()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "",
            IsOnGround = false,
            VerticalSpeed = 0,
            NavigationRouteCount = 3,
            NavigationRouteDisplay = "OAK SFO LAX",
            ActiveSidId = "OAK5",
        };
        Assert.Equal("→ OAK SFO LAX", Text(v));
    }

    [Fact]
    public void NoPhase_Level_NavigatingToFix()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "",
            IsOnGround = false,
            VerticalSpeed = 0,
            NavigatingTo = "CEPIN",
            ActiveSidId = "OAK5",
        };
        Assert.Equal("→ CEPIN", Text(v));
    }

    [Fact]
    public void NoPhase_Level_NothingSet()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "",
            IsOnGround = false,
            VerticalSpeed = 0,
            NavigatingTo = "",
            Altitude = 35000,
            ActiveSidId = "OAK5",
        };
        Assert.Equal("FL350, on course", Text(v));
    }

    [Fact]
    public void NoPhase_Level_AssignedHeading()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "",
            IsOnGround = false,
            VerticalSpeed = 0,
            NavigatingTo = "",
            Altitude = 35000,
            AssignedHeading = new MagneticHeading(270),
        };
        Assert.Equal("FL350, on course, hdg 270", Text(v));
    }

    [Fact]
    public void Phase_AssignedHeading_SuppressedOnPatternLeg()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Downwind",
            PatternDirection = "Left",
            AssignedRunway = "28R",
            AssignedHeading = new MagneticHeading(90),
        };
        Assert.Equal("Left downwind 28R", Text(v));
    }

    [Fact]
    public void Phase_AssignedHeading_KeptOnProceedToFix()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "ProceedToFix",
            NavigatingTo = "OAKEY",
            AssignedHeading = new MagneticHeading(270),
        };
        Assert.Equal("Proceeding to OAKEY, hdg 270", Text(v));
    }

    [Fact]
    public void FormatAltitudeCompact_Below18000()
    {
        Assert.Equal("10,000", FormatAltitudeCompact(10000));
    }

    [Fact]
    public void FormatAltitudeCompact_AtOrAbove18000()
    {
        Assert.Equal("FL350", FormatAltitudeCompact(35000));
    }

    [Fact]
    public void FormatAltitudeCompact_FL180()
    {
        Assert.Equal("FL180", FormatAltitudeCompact(18000));
    }

    [Fact]
    public void HoldPresentPosition()
    {
        Assert.Equal("Hold present position", Text(new AircraftStatusView { CurrentPhase = "HPP-L" }));
    }

    [Fact]
    public void STurns()
    {
        Assert.Equal("S-turns", Text(new AircraftStatusView { CurrentPhase = "S-Turns" }));
    }

    [Fact]
    public void Following()
    {
        Assert.Equal("Following UAL456", Text(new AircraftStatusView { CurrentPhase = "Following UAL456" }));
    }

    [Fact]
    public void TurnPhase()
    {
        Assert.Equal("Turning", Text(new AircraftStatusView { CurrentPhase = "TurnL90" }));
    }

    [Fact]
    public void RunwayExit()
    {
        Assert.Equal("Exiting runway", Text(new AircraftStatusView { CurrentPhase = "Runway Exit" }));
    }

    [Fact]
    public void PatternEntry_FallbackWithoutKind()
    {
        Assert.Equal("Right pattern entry", Text(new AircraftStatusView { CurrentPhase = "Pattern Entry", PatternDirection = "Right" }));
    }

    [Fact]
    public void MidfieldCrossing()
    {
        Assert.Equal("Midfield crossing 28R", Text(new AircraftStatusView { CurrentPhase = "MidfieldCrossing", AssignedRunway = "28R" }));
    }

    [Fact]
    public void InterceptCourse_WithApproach()
    {
        Assert.Equal("Intercepting ILS28R", Text(new AircraftStatusView { CurrentPhase = "InterceptCourse", ActiveApproachId = "ILS28R" }));
    }

    [Fact]
    public void InterceptCourse_NoApproach()
    {
        Assert.Equal("Intercepting course", Text(new AircraftStatusView { CurrentPhase = "InterceptCourse", ActiveApproachId = null }));
    }

    [Fact]
    public void ProceedToFix()
    {
        Assert.Equal("Proceeding to OAKEY", Text(new AircraftStatusView { CurrentPhase = "ProceedToFix", NavigatingTo = "OAKEY" }));
    }

    [Fact]
    public void StopAndGo()
    {
        Assert.Equal("Stop-and-go 28R", Text(new AircraftStatusView { CurrentPhase = "StopAndGo", ClearedRunway = "28R" }));
    }

    [Fact]
    public void LowApproach()
    {
        Assert.Equal("Low approach 28R", Text(new AircraftStatusView { CurrentPhase = "LowApproach", ClearedRunway = "28R" }));
    }

    [Fact]
    public void AirTaxi()
    {
        Assert.Equal("Air taxi", Text(new AircraftStatusView { CurrentPhase = "AirTaxi" }));
    }

    [Fact]
    public void CrossingRunway()
    {
        Assert.Equal("Crossing runway", Text(new AircraftStatusView { CurrentPhase = "Crossing Runway" }));
    }

    [Fact]
    public void HoldingAfterExit()
    {
        Assert.Equal("Clear of runway", Text(new AircraftStatusView { CurrentPhase = "Holding After Exit" }));
    }

    [Fact]
    public void AlertPriority_HandoffOverridesPhase()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Taxiing",
            HandoffPeer = "conn1",
            HandoffPeerSectorCode = "NR",
        };
        Assert.Equal("HO → NR", Text(v));
        Assert.Equal(AircraftStatusSeverity.Warning, Severity(v));
    }

    [Fact]
    public void AlertPriority_FinalNoClearanceOverridesHandoff()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "FinalApproach",
            LandingClearance = "",
            HandoffPeer = "conn1",
        };
        Assert.Equal("No landing clnc", Text(v));
        Assert.Equal(AircraftStatusSeverity.Critical, Severity(v));
    }

    [Fact]
    public void VfrFollow_Phase_ShowsFollowingCallsign()
    {
        var v = new AircraftStatusView { CurrentPhase = "VFR Follow", FollowingCallsign = "N436MS" };
        Assert.Equal("Following N436MS", Text(v));
        Assert.Equal(AircraftStatusSeverity.Normal, Severity(v));
    }

    [Fact]
    public void Downwind_WithFollowingCallsign_PrefixesFollowState()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Downwind",
            PatternDirection = "right",
            AssignedRunway = "28R",
            FollowingCallsign = "N436MS",
        };
        Assert.Equal("Following N436MS → right downwind 28R", Text(v));
    }

    [Fact]
    public void FinalApproach_WithFollowingCallsign_PrefixesFollowState()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "FinalApproach",
            LandingClearance = "ClearedToLand",
            ActiveApproachId = "ILS28R",
            FollowingCallsign = "N436MS",
        };
        Assert.Equal("Following N436MS → ILS28R final", Text(v));
    }

    [Fact]
    public void Landing_WithFollowingCallsign_PrefixesFollowState()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Landing",
            LandingClearance = "ClearedToLand",
            ClearedRunway = "28R",
            FollowingCallsign = "N436MS",
        };
        Assert.Equal("Following N436MS → landing 28R", Text(v));
    }

    [Fact]
    public void VfrFollow_NullFollowingCallsign_FallsBackToGenericLabel()
    {
        Assert.Equal("VFR follow", Text(new AircraftStatusView { CurrentPhase = "VFR Follow", FollowingCallsign = null }));
    }
}
